using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Plugins.Core.Assets.Character;

namespace StudioRaming.WarudoAgentBridge
{
    // Humanoid posing. A pose is 95 Unity muscle values plus body height/rotation. The bridge bakes it
    // into a two-key .anim with WARUDO's own AnimBuilder and plays it as the idle animation, so WARUDO's
    // animation, IK and tracking layers keep working on top of it. The same clip is what gets saved.
    public sealed partial class AgentBridgePlugin
    {
        sealed class PoseState
        {
            public string PreviousIdle;
            public string AppliedUri;
            public string AppliedFile;
            public float[] Muscles;
            public Vector3 BodyPosition;
            public Quaternion BodyRotation;
        }

        readonly Dictionary<Guid, PoseState> poses = new Dictionary<Guid, PoseState>();

        // The file behind a bridge preview URI, or null for any other animation.
        string PreviewFileOf(string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(AnimationUriPrefix, StringComparison.Ordinal)) return null;
            string name = uri.Substring(AnimationUriPrefix.Length);
            if (!name.StartsWith(PosePreviewPrefix, StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return Context.PersistentDataManager.GetFullPath("CharacterAnimations/" + name);
        }

        // Preview clips still in use: each applied pose, the idle pose_clear would restore, and the
        // idle any character in the open scene plays right now. Called on the main thread.
        HashSet<string> ActivePoseFiles()
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in poses)
            {
                if (pair.Value.AppliedFile != null) files.Add(pair.Value.AppliedFile);
                foreach (string slot in PoseSlotFiles(pair.Key)) files.Add(slot);
                string previous = PreviewFileOf(pair.Value.PreviousIdle);
                if (previous != null) files.Add(previous);
            }
            try
            {
                if (Context.OpenedScene != null)
                    foreach (CharacterAsset c in Context.OpenedScene.GetAssets().Values.OfType<CharacterAsset>())
                    {
                        string live = PreviewFileOf(c.DefaultIdleAnimation);
                        if (live == null) continue;
                        files.Add(live);
                        foreach (string slot in PoseSlotFiles(c.Id)) files.Add(slot);
                    }
            }
            catch (Exception e) { Debug.LogWarning("[WARUDO Agent Bridge] Could not list scene idle animations: " + e.Message); }
            return files;
        }

        // Pose state survives Playground reloads and WARUDO restarts, so pose_clear can still
        // restore the idle animation the character had before the bridge posed it.
        string PoseStateFile { get { return Path.Combine(root, "pose-state.json"); } }

        void SavePoseState()
        {
            var entries = new JArray(poses.Select(pair => JObject.FromObject(new
            {
                characterId = pair.Key.ToString(), previousIdle = pair.Value.PreviousIdle, appliedUri = pair.Value.AppliedUri, appliedFile = pair.Value.AppliedFile,
                muscles = pair.Value.Muscles, bodyPosition = Wire(pair.Value.BodyPosition),
                bodyRotation = new[] { pair.Value.BodyRotation.x, pair.Value.BodyRotation.y, pair.Value.BodyRotation.z, pair.Value.BodyRotation.w }
            })));
            try { AtomicWrite(PoseStateFile, entries.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (IOException e) { Debug.LogWarning("[WARUDO Agent Bridge] Could not save pose state: " + e.Message); }
        }

        void LoadPoseState()
        {
            poses.Clear();
            if (!File.Exists(PoseStateFile)) return;
            try
            {
                foreach (JObject entry in ((ParseJson(File.ReadAllText(PoseStateFile)) as JArray) ?? new JArray()).OfType<JObject>())
                {
                    Guid id;
                    float[] muscles = entry["muscles"] == null ? null : entry["muscles"].ToObject<float[]>();
                    float[] position = entry["bodyPosition"] == null ? null : entry["bodyPosition"].ToObject<float[]>();
                    float[] rotation = entry["bodyRotation"] == null ? null : entry["bodyRotation"].ToObject<float[]>();
                    if (!Guid.TryParse((string)entry["characterId"], out id) || muscles == null || muscles.Length != HumanTrait.MuscleCount || position == null || position.Length != 3 || rotation == null || rotation.Length != 4) continue;
                    poses[id] = new PoseState
                    {
                        PreviousIdle = (string)entry["previousIdle"], AppliedUri = (string)entry["appliedUri"], AppliedFile = (string)entry["appliedFile"],
                        Muscles = muscles, BodyPosition = new Vector3(position[0], position[1], position[2]),
                        BodyRotation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3])
                    };
                }
            }
            catch (Exception e) { Debug.LogWarning("[WARUDO Agent Bridge] Ignoring unreadable pose state: " + e.Message); }
        }

        static int MuscleIndex(string name)
        {
            int index;
            if (int.TryParse(name, out index) && index >= 0 && index < HumanTrait.MuscleCount) return index;
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                if (string.Equals(HumanTrait.MuscleName[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new ArgumentException("Unknown muscle '" + name + "'. Read the names with warudo_character_pose_get.");
        }

        static HumanPoseHandler PoseHandler(CharacterAsset c)
        {
            Animator animator = c.Animator;
            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
                throw new InvalidOperationException("NOT_HUMANOID: " + c.Name + " has no valid humanoid avatar.");
            return new HumanPoseHandler(animator.avatar, animator.transform);
        }

        async UniTask<object> PoseOperation(string op, JObject p)
        {
            CharacterAsset c = RequireLoaded(CharacterById(p));
            PoseState state;
            poses.TryGetValue(c.Id, out state);
            switch (op)
            {
                case "character.pose.get": return PoseGet(c, p, state);
                case "character.pose.set": return await PoseSet(c, p, state);
                case "character.pose.clear":
                {
                    if (state == null) return new { character = c.Name, cleared = false, note = "No bridge pose is active on this character." };
                    string restore = state.PreviousIdle;
                    string note = null;
                    if (c.DefaultIdleAnimation != state.AppliedUri)
                    {
                        restore = null;
                        note = "The idle animation was changed after the pose was set; it was left as it is.";
                    }
                    else if (string.IsNullOrEmpty(restore) || !AnimationExists(restore))
                    {
                        restore = null;
                        note = "The idle animation from before the pose is unknown or gone, so the pose keeps playing until you pick one with warudo_character_animation.";
                    }
                    if (restore != null)
                    {
                        await WaitIdleListed(restore);
                        c.SetDataInput("DefaultIdleAnimation", restore, true);
                    }
                    poses.Remove(c.Id);
                    SavePoseState();
                    if (restore != null)
                    {
                        await UniTask.Yield();
                        DeletePoseFile(state.AppliedFile);
                        foreach (string slot in PoseSlotFiles(c.Id)) DeletePoseFile(slot);
                    }
                    return new { character = c.Name, cleared = true, restoredIdle = restore, note };
                }
                case "character.pose.save": return PoseSave(c, p, state);
            }
            throw new ArgumentException("Unsupported pose operation: " + op);
        }

        object PoseGet(CharacterAsset c, JObject p, PoseState state)
        {
            var handler = PoseHandler(c);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            string query = ((string)p["query"] ?? "").Trim();
            var muscles = Enumerable.Range(0, HumanTrait.MuscleCount)
                .Where(i => query.Length == 0 || HumanTrait.MuscleName[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(i => new
                {
                    index = i, name = HumanTrait.MuscleName[i], value = Math.Round(pose.muscles[i], 4),
                    applied = state == null ? (double?)null : Math.Round(state.Muscles[i], 4),
                    minDegrees = HumanTrait.GetMuscleDefaultMin(i), maxDegrees = HumanTrait.GetMuscleDefaultMax(i)
                }).ToArray();
            object bones = null;
            if ((bool?)p["includeBones"] ?? true)
            {
                var map = new JObject();
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    Transform t = c.Animator.GetBoneTransform(bone);
                    if (t == null) continue;
                    map[bone.ToString()] = JObject.FromObject(new { localEuler = Wire(t.localEulerAngles), worldPosition = Wire(t.position) });
                }
                bones = map;
            }
            return new
            {
                character = c.Name,
                bodyHeight = pose.bodyPosition.y,
                bodyRotation = Wire(pose.bodyRotation.eulerAngles),
                muscles,
                bones,
                applied = state == null ? null : new { uri = state.AppliedUri, previousIdle = state.PreviousIdle },
                hint = "Muscles are about -1..1. Change a few, capture, compare with the reference, repeat."
            };
        }

        async UniTask<object> PoseSet(CharacterAsset c, JObject p, PoseState state)
        {
            var handler = PoseHandler(c);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            string baseMode = (string)p["base"] ?? "applied";
            if (baseMode == "applied" && state != null)
            {
                pose.muscles = (float[])state.Muscles.Clone();
                pose.bodyPosition = state.BodyPosition;
                pose.bodyRotation = state.BodyRotation;
            }
            else if (baseMode == "applied" || baseMode == "standing")
            {
                pose = StandingPose(c, handler);
                baseMode = "standing";
            }
            else if (baseMode == "neutral")
            {
                pose.muscles = new float[HumanTrait.MuscleCount];
                pose.bodyRotation = Quaternion.identity;
            }
            else if (baseMode != "current") throw new ArgumentException("base must be applied, standing, current or neutral.");
            pose.bodyPosition = new Vector3(0, pose.bodyPosition.y, 0);

            // Muscles and body values first, aims last: an aim is a final direction in character
            // space, so a spine or shoulder muscle set in the same call must not swing it afterwards.
            // The hips are the root of the hierarchy: remember where the base pose has them, so spine
            // muscles bend the body above the hips instead of Unity counter-turning the pelvis.
            Dictionary<string, Quaternion> rest = RestRotations(c.Animator);
            Quaternion? hipsBefore = rest == null ? (Quaternion?)null : HipsOrientation(c.Animator, handler, pose, rest);

            JObject muscles = p["muscles"] as JObject;
            var changed = new JObject();
            foreach (var property in (muscles ?? new JObject()).Properties())
            {
                int index = MuscleIndex(property.Name);
                if (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer) throw new ArgumentException(property.Name + " must be a number.");
                float value = property.Value.Value<float>();
                if (float.IsNaN(value) || value < -2 || value > 2) throw new ArgumentException(property.Name + " must be within -2..2 (usually -1..1).");
                pose.muscles[index] = value;
                changed[HumanTrait.MuscleName[index]] = value;
            }
            if (rest != null)
            {
                // bodyRotation is the hips' orientation relative to the character; unset keeps the base's.
                Quaternion target = p["bodyRotation"] != null ? Quaternion.Euler(Vector(p["bodyRotation"])) : hipsBefore.Value;
                Quaternion hipsNow = HipsOrientation(c.Animator, handler, pose, rest);
                pose.bodyRotation = target * Quaternion.Inverse(hipsNow) * pose.bodyRotation;
            }
            else if (p["bodyRotation"] != null) pose.bodyRotation = Quaternion.Euler(Vector(p["bodyRotation"]));
            if (p["bodyHeight"] != null)
            {
                float height = (float?)p["bodyHeight"] ?? 0;
                if (float.IsNaN(height) || height < 0 || height > 3) throw new ArgumentException("bodyHeight must be 0..3.");
                pose.bodyPosition = new Vector3(0, height, 0);
            }
            // Where the hips go. Unity stores the body's mass centre rather than the hips, so aiming the
            // limbs, or a joint held at its limit below, would otherwise slide the whole body (folding
            // the legs back for a kneel moved it about 5 cm). The hips stay where the base put them.
            Vector3 hipsTarget = HipsLocal(c, handler, pose);
            if (p["hipsHeight"] != null)
            {
                float hipsHeight = (float?)p["hipsHeight"] ?? 0;
                if (float.IsNaN(hipsHeight) || hipsHeight < 0 || hipsHeight > 3) throw new ArgumentException("hipsHeight must be 0..3 meters.");
                hipsTarget.y = hipsHeight;
            }
            JObject bones = p["bones"] as JObject;
            string space = ((string)p["space"] ?? "body").ToLowerInvariant();
            if (space != "body" && space != "character") throw new ArgumentException("space must be body or character.");
            if (bones != null && bones.Count > 0) pose = PoseWithBones(c, handler, pose, bones, space == "body");

            // Muscle -1..1 is the avatar's humanoid joint range, so a body can only take poses a
            // person could. Anything an aim or a value pushed past it is held at the limit and reported.
            var limited = new List<object>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                float requested = pose.muscles[i];
                if (requested >= -1f && requested <= 1f) continue;
                float applied = Mathf.Clamp(requested, -1f, 1f);
                pose.muscles[i] = applied;
                if (Mathf.Abs(requested - applied) < 0.02f) continue;
                limited.Add(new
                {
                    muscle = HumanTrait.MuscleName[i], requested = Math.Round(requested, 2), applied,
                    requestedDegrees = Math.Round(MuscleDegrees(i, requested)), limitDegrees = Math.Round(MuscleDegrees(i, applied))
                });
            }
            pose = PlaceHips(c, handler, pose, hipsTarget);

            // Write the slot that is not playing, and make sure its sibling exists too, so both stay
            // in WARUDO's animation list from the first pose on (see PoseSlotFiles).
            string[] slots = PoseSlotFiles(c.Id);
            string playing = PreviewFileOf(c.DefaultIdleAnimation) ?? (state == null ? null : state.AppliedFile);
            string file = string.Equals(playing, slots[0], StringComparison.OrdinalIgnoreCase) ? slots[1] : slots[0];
            string sibling = file == slots[0] ? slots[1] : slots[0];
            string fileName = Path.GetFileName(file);
            WritePoseFile(file, pose);
            if (!File.Exists(sibling)) WritePoseFile(sibling, pose);
            string uri = AnimationUriPrefix + fileName;
            ResolveAnimation(uri);

            if (state == null)
            {
                // Only remember an idle that can actually be played again (a preview clip left over
                // from a lost pose state may already be deleted).
                string idle = c.DefaultIdleAnimation;
                state = new PoseState { PreviousIdle = !string.IsNullOrEmpty(idle) && AnimationExists(idle) ? idle : null };
                poses[c.Id] = state;
            }
            string previousFile = state.AppliedFile;
            state.AppliedUri = uri;
            state.AppliedFile = file;
            state.Muscles = (float[])pose.muscles.Clone();
            state.BodyPosition = pose.bodyPosition;
            state.BodyRotation = pose.bodyRotation;
            SavePoseState();
            await WaitIdleListed(uri);
            await WaitIdleListed(AnimationUriPrefix + Path.GetFileName(sibling));
            c.SetDataInput("DefaultIdleAnimation", uri, true);
            await UniTask.Yield();
            // A preview from before the two slots (0.2.0 and earlier) is no longer needed.
            if (previousFile != null && !slots.Contains(previousFile, StringComparer.OrdinalIgnoreCase)) DeletePoseFile(previousFile);
            PoseShown shown = await WaitPoseShown(c, uri, pose.muscles, 3f);
            string problem = shown.ReplacedBy != null
                ? "The idle animation was switched to " + shown.ReplacedBy + " while the pose was loading, so the character is not showing it; set the pose again. "
                : shown.Matched ? "" : "The character does not fully show the pose yet (see shown): another animation, tracking or IK layer is moving " + shown.FurthestMuscle + ". ";
            return new
            {
                character = c.Name, uri, baseMode, changedMuscles = changed, bonesApplied = bones == null ? 0 : bones.Count,
                bodyHeight = pose.bodyPosition.y, hipsHeight = Math.Round(hipsTarget.y, 3), bodyRotation = Wire(pose.bodyRotation.eulerAngles),
                limited, previousIdle = state.PreviousIdle, persisted = false,
                shown = new { matched = shown.Matched, maxDifference = shown.MaxDifference, furthestMuscle = shown.FurthestMuscle, waitedMs = shown.WaitedMs, replacedBy = shown.ReplacedBy },
                note = (limited.Count > 0 ? "Some joints were held at their limit (see limited): the requested direction is not reachable, so ease that aim. " : "") + problem +
                    "Playing as the idle animation. warudo_character_pose_clear restores the previous idle; warudo_character_pose_save keeps it."
            };
        }

        // Previews play from two fixed files per character, used in turn. WARUDO's editor treats the
        // idle field as pick-from-list: when the value changes it checks it against the animation list
        // it last fetched and resets a value it cannot find to the default idle. A new file for every
        // pose was often missing from that list (the ResourceManager and the editor both cache it), so
        // the pose silently fell back to the default idle. Two files that stay put are always listed,
        // and the one being rewritten is never the one playing.
        string[] PoseSlotFiles(Guid characterId)
        {
            string stem = "CharacterAnimations/" + PosePreviewPrefix + characterId.ToString("N").Substring(0, 8) + "_";
            return new[] { Context.PersistentDataManager.GetFullPath(stem + "a.anim"), Context.PersistentDataManager.GetFullPath(stem + "b.anim") };
        }

        // Rewrite a preview clip. WARUDO plays a .anim through a .bundle it builds on first use and
        // reuses afterwards, so the old bundle has to go or WARUDO would keep playing the old pose.
        static void WritePoseFile(string file, HumanPose pose)
        {
            WritePoseClip(file, Path.GetFileNameWithoutExtension(file), pose);
            string bundle = file + ".bundle";
            try { if (File.Exists(bundle)) File.Delete(bundle); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { } // locked or read-only: reported just below
            if (File.Exists(bundle)) throw new IOException("POSE_FILE_BUSY: could not replace " + bundle + "; close whatever holds it and set the pose again.");
        }

        // The editor checks a new idle against WARUDO's animation list, which the ResourceManager caches
        // for a second: switch only once that list includes the clip.
        async UniTask WaitIdleListed(string uri)
        {
            float deadline = Time.realtimeSinceStartup + 2f;
            while (!ResourceUris("CharacterAnimation").Contains(uri))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Debug.LogWarning("[WARUDO Agent Bridge] " + uri + " is not in WARUDO's animation list yet; the editor may reset the idle.");
                    return;
                }
                await UniTask.Yield();
            }
        }

        sealed class PoseShown
        {
            public bool Matched;
            public double MaxDifference;
            public string FurthestMuscle;
            public int WaitedMs;
            public string ReplacedBy;
        }

        // WARUDO crossfades into a new idle over about half a second. Wait until the character shows
        // the clip, so a capture or measurement made right after the reply sees the new pose. Tracking
        // or IK layers on top can hold a few muscles away from the clip for good; then it stops once
        // the pose has closed most of the gap and stopped getting closer, and reports how far off it is.
        static async UniTask<PoseShown> WaitPoseShown(CharacterAsset c, string uri, float[] target, float timeoutSeconds)
        {
            var handler = PoseHandler(c);
            var live = new HumanPose();
            float start = Time.realtimeSinceStartup, deadline = start + timeoutSeconds;
            float initialGap = -1, markGap = 0, markTime = start, worst = 0;
            int worstMuscle = -1, matchedFrames = 0;
            string replacedBy = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                await UniTask.Yield();
                if (c.Animator == null) break;
                if (c.DefaultIdleAnimation != uri) { replacedBy = c.DefaultIdleAnimation ?? "none"; break; }
                handler.GetHumanPose(ref live);
                float now = Time.realtimeSinceStartup, gap = 0;
                worst = 0;
                worstMuscle = -1;
                for (int i = 0; i < target.Length; i++)
                {
                    float d = Mathf.Abs(live.muscles[i] - target[i]);
                    gap += d;
                    if (d > worst) { worst = d; worstMuscle = i; }
                }
                if (initialGap < 0) { initialGap = markGap = gap; markTime = now; }
                matchedFrames = worst < 0.03f ? matchedFrames + 1 : 0;
                if (matchedFrames >= 2) break;
                // Progress is judged over time, not per frame: at a high frame rate each frame of the
                // crossfade moves only a little.
                if (gap < markGap - 0.02f * initialGap) { markGap = gap; markTime = now; }
                if (gap <= 0.3f * initialGap && now - markTime >= 0.4f) break;
            }
            return new PoseShown
            {
                Matched = replacedBy == null && worst < 0.03f, MaxDifference = Math.Round(worst, 3),
                FurthestMuscle = worstMuscle < 0 ? null : HumanTrait.MuscleName[worstMuscle],
                WaitedMs = (int)((Time.realtimeSinceStartup - start) * 1000), ReplacedBy = replacedBy
            };
        }

        static List<KeyValuePair<Transform, TransformSnapshot>> SnapshotHumanoid(Animator animator)
        {
            var saved = new List<KeyValuePair<Transform, TransformSnapshot>> { new KeyValuePair<Transform, TransformSnapshot>(animator.transform, new TransformSnapshot(animator.transform)) };
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t != null) saved.Add(new KeyValuePair<Transform, TransformSnapshot>(t, new TransformSnapshot(t)));
            }
            return saved;
        }

        // The hips' position in the character root's frame for a pose.
        static Vector3 HipsLocal(CharacterAsset c, HumanPoseHandler handler, HumanPose pose)
        {
            Animator animator = c.Animator;
            var saved = SnapshotHumanoid(animator);
            try
            {
                handler.SetHumanPose(ref pose);
                return animator.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Hips).position);
            }
            finally
            {
                foreach (var item in saved) item.Value.Restore(item.Key);
            }
        }

        // Move the pose's body position until its hips land on target (character-root frame).
        // bodyPosition is in the avatar's normalised units, so the scale is read off a test move.
        static HumanPose PlaceHips(CharacterAsset c, HumanPoseHandler handler, HumanPose pose, Vector3 target)
        {
            Animator animator = c.Animator;
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips), root = animator.transform;
            var saved = SnapshotHumanoid(animator);
            try
            {
                handler.SetHumanPose(ref pose);
                Vector3 at = root.InverseTransformPoint(hips.position);
                HumanPose probe = pose;
                probe.bodyPosition += Vector3.up * 0.1f;
                handler.SetHumanPose(ref probe);
                float scale = (root.InverseTransformPoint(hips.position).y - at.y) / 0.1f;
                if (Mathf.Abs(scale) < 1e-4f) return pose;
                for (int i = 0; i < 3 && (target - at).sqrMagnitude > 1e-8f; i++)
                {
                    pose.bodyPosition += (target - at) / scale;
                    handler.SetHumanPose(ref pose);
                    at = root.InverseTransformPoint(hips.position);
                }
                return pose;
            }
            finally
            {
                foreach (var item in saved) item.Value.Restore(item.Key);
            }
        }

        // Approximate joint angle of a muscle value, from Unity's default humanoid ranges.
        static float MuscleDegrees(int muscle, float value)
        {
            return value >= 0 ? value * HumanTrait.GetMuscleDefaultMax(muscle) : value * -HumanTrait.GetMuscleDefaultMin(muscle);
        }

        // The bone an aim points at: the direction from a bone to this child is what "aim" sets.
        static readonly Dictionary<HumanBodyBones, HumanBodyBones> AimChild = new Dictionary<HumanBodyBones, HumanBodyBones>
        {
            { HumanBodyBones.Hips, HumanBodyBones.Spine }, { HumanBodyBones.Spine, HumanBodyBones.Chest }, { HumanBodyBones.Chest, HumanBodyBones.Neck },
            { HumanBodyBones.UpperChest, HumanBodyBones.Neck }, { HumanBodyBones.Neck, HumanBodyBones.Head },
            { HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm }, { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm }, { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand },
            { HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal }, { HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm },
            { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm }, { HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand }, { HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal },
            { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg }, { HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot }, { HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes },
            { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg }, { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot }, { HumanBodyBones.RightFoot, HumanBodyBones.RightToes },
            { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate }, { HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal },
            { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate }, { HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
            { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate }, { HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
            { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate }, { HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
            { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate }, { HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
            { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate }, { HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal },
            { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate }, { HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate }, { HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate }, { HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate }, { HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
        };

        // A clip sets all 95 muscles, so whatever the base leaves in the legs is what the legs do.
        // Muscle 0 is not standing (Unity's zero pose bends hips and knees), so the standing base
        // is built from this avatar's own rest skeleton (its import T-pose: legs and spine straight),
        // with the arms lowered to the sides and the fingers relaxed.
        static readonly JObject ArmsDown = JObject.Parse(
            "{\"LeftUpperArm\":{\"aim\":[-0.15,-1,0.03]},\"LeftLowerArm\":{\"aim\":[-0.08,-1,0.1]}," +
            "\"RightUpperArm\":{\"aim\":[0.15,-1,0.03]},\"RightLowerArm\":{\"aim\":[0.08,-1,0.1]}}");

        static HumanPose StandingPose(CharacterAsset c, HumanPoseHandler handler)
        {
            Animator animator = c.Animator;
            SkeletonBone[] skeleton = animator.avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
                throw new InvalidOperationException("NO_REST_POSE: " + c.Name + "'s avatar has no rest skeleton; use base=current and aim the legs.");

            // Only the transforms on the chains down to the humanoid bones decide the muscles.
            var chain = new HashSet<Transform>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                for (Transform t = animator.GetBoneTransform(bone); t != null && t != animator.transform; t = t.parent)
                    if (!chain.Add(t)) break;
            }
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in chain)
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;

            var saved = chain.Select(t => new KeyValuePair<Transform, TransformSnapshot>(t, new TransformSnapshot(t))).ToList();
            var rest = new HumanPose();
            try
            {
                // Positions too: the playing animation may have lowered the hips (kneeling, sitting),
                // and the rest pose must not inherit that height.
                foreach (SkeletonBone bone in skeleton)
                {
                    Transform t;
                    if (bone.name == null || !byName.TryGetValue(bone.name, out t)) continue;
                    t.localPosition = bone.position;
                    t.localRotation = bone.rotation;
                }
                handler.GetHumanPose(ref rest);
            }
            finally
            {
                foreach (var item in saved) item.Value.Restore(item.Key);
            }
            rest.bodyPosition = new Vector3(0, rest.bodyPosition.y, 0);
            rest.bodyRotation = Quaternion.identity;

            HumanPose standing = PoseWithBones(c, handler, rest, ArmsDown);
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                string name = HumanTrait.MuscleName[i];
                if (name.EndsWith(" Stretched", StringComparison.Ordinal) && (name.StartsWith("Left ", StringComparison.Ordinal) || name.StartsWith("Right ", StringComparison.Ordinal)) &&
                    (name.Contains(" Thumb ") || name.Contains(" Index ") || name.Contains(" Middle ") || name.Contains(" Ring ") || name.Contains(" Little ")))
                    standing.muscles[i] *= 0.6f; // relaxed: part way from the straight rest fingers toward Unity's curled zero
            }
            return standing;
        }

        sealed class BoneEdit
        {
            public HumanBodyBones Bone;
            public Transform Transform;
            public Transform AimAt;
            public Vector3? Aim;
            public Quaternion? Rotate;
            public int Depth;
        }

        // The torso part a bone hangs from. Body-space aims are read in this part's frame, so turning
        // the hips carries the spine and legs, and turning the chest carries the arms, neck and head,
        // the way the humanoid hierarchy does.
        static HumanBodyBones? AimAnchor(Animator animator, HumanBodyBones bone)
        {
            Func<HumanBodyBones[], HumanBodyBones?> first = list =>
            {
                foreach (HumanBodyBones b in list) if (animator.GetBoneTransform(b) != null) return b;
                return null;
            };
            string name = bone.ToString();
            if (bone == HumanBodyBones.Hips) return null;
            if (bone == HumanBodyBones.Spine) return HumanBodyBones.Hips;
            if (bone == HumanBodyBones.Chest) return first(new[] { HumanBodyBones.Spine, HumanBodyBones.Hips });
            if (bone == HumanBodyBones.UpperChest) return first(new[] { HumanBodyBones.Chest, HumanBodyBones.Spine });
            if (bone == HumanBodyBones.Head) return first(new[] { HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest });
            if (name.EndsWith("Eye", StringComparison.Ordinal) || bone == HumanBodyBones.Jaw) return HumanBodyBones.Head;
            if (name.Contains("UpperLeg") || name.Contains("LowerLeg") || name.EndsWith("Foot", StringComparison.Ordinal) || name.EndsWith("Toes", StringComparison.Ordinal)) return HumanBodyBones.Hips;
            return first(new[] { HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine }); // neck, shoulders, arms, hands, fingers
        }

        static Dictionary<string, Quaternion> RestRotations(Animator animator)
        {
            SkeletonBone[] skeleton = animator.avatar == null ? null : animator.avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0) return null;
            var rest = new Dictionary<string, Quaternion>();
            foreach (SkeletonBone b in skeleton)
                if (b.name != null && !rest.ContainsKey(b.name)) rest[b.name] = b.rotation;
            return rest;
        }

        // Where a pose puts the hips, relative to the character (identity = level, facing forward).
        // Unity's bodyRotation is the whole body's centre, not the pelvis, so this is read off the skeleton.
        static Quaternion HipsOrientation(Animator animator, HumanPoseHandler handler, HumanPose pose, Dictionary<string, Quaternion> rest)
        {
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var saved = new List<KeyValuePair<Transform, TransformSnapshot>>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t != null) saved.Add(new KeyValuePair<Transform, TransformSnapshot>(t, new TransformSnapshot(t)));
            }
            try
            {
                handler.SetHumanPose(ref pose);
                return Quaternion.Inverse(animator.transform.rotation) * hips.rotation * Quaternion.Inverse(RestRelative(animator, hips, rest));
            }
            finally
            {
                foreach (var item in saved) item.Value.Restore(item.Key);
            }
        }

        // A bone's rotation relative to the character root in the rest (import) pose, where the body
        // stands straight facing the root's forward.
        static Quaternion RestRelative(Animator animator, Transform bone, Dictionary<string, Quaternion> rest)
        {
            var chain = new List<Transform>();
            for (Transform t = bone; t != null && t != animator.transform; t = t.parent) chain.Add(t);
            chain.Reverse();
            Quaternion relative = Quaternion.identity;
            foreach (Transform t in chain)
            {
                Quaternion local;
                relative *= rest.TryGetValue(t.name, out local) ? local : t.localRotation;
            }
            return relative;
        }

        // Unity's retargeting converts bone rotations to muscles: put the base pose on the skeleton,
        // turn the requested bones parent first, read the muscles back, then restore the transforms.
        // aim: point the bone at its child along a direction (x = right, y = up, z = forward). In body
        // space that frame is the torso part the bone hangs from (see AimAnchor) as it is posed right
        // now; in character space it is the character's root.
        // rotate: extra local euler degrees on top of the base pose.
        static HumanPose PoseWithBones(CharacterAsset c, HumanPoseHandler handler, HumanPose pose, JObject bones, bool bodySpace = true)
        {
            Animator animator = c.Animator;
            Dictionary<string, Quaternion> rest = RestRotations(animator);
            var edits = new List<BoneEdit>();
            foreach (var property in bones.Properties())
            {
                HumanBodyBones bone;
                if (!Enum.TryParse(property.Name, true, out bone) || bone == HumanBodyBones.LastBone) throw new ArgumentException("Unknown bone '" + property.Name + "' (use HumanBodyBones names such as LeftUpperArm).");
                Transform t = animator.GetBoneTransform(bone);
                if (t == null) throw new ArgumentException(c.Name + " has no " + bone + " bone.");
                JObject spec = property.Value as JObject;
                if (spec == null || (spec["aim"] == null && spec["rotate"] == null)) throw new ArgumentException(bone + ": use {\"aim\":[x,y,z]} or {\"rotate\":[x,y,z]}.");
                var edit = new BoneEdit { Bone = bone, Transform = t };
                if (spec["aim"] != null)
                {
                    HumanBodyBones child;
                    Vector3 aim = Vector(spec["aim"]);
                    if (aim.sqrMagnitude < 1e-6f) throw new ArgumentException(bone + ": aim must be a non-zero direction.");
                    if (!AimChild.TryGetValue(bone, out child) || animator.GetBoneTransform(child) == null)
                        throw new ArgumentException(bone + " cannot be aimed on this character; use rotate.");
                    edit.Aim = aim.normalized;
                    edit.AimAt = animator.GetBoneTransform(child);
                }
                if (spec["rotate"] != null) edit.Rotate = Quaternion.Euler(Vector(spec["rotate"]));
                for (Transform walk = t; walk != null; walk = walk.parent) edit.Depth++;
                edits.Add(edit);
            }
            var saved = new List<KeyValuePair<Transform, TransformSnapshot>>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t != null) saved.Add(new KeyValuePair<Transform, TransformSnapshot>(t, new TransformSnapshot(t)));
            }
            saved.Add(new KeyValuePair<Transform, TransformSnapshot>(animator.transform, new TransformSnapshot(animator.transform)));
            try
            {
                handler.SetHumanPose(ref pose);
                Quaternion characterSpace = animator.transform.rotation;
                foreach (var edit in edits.OrderBy(e => e.Depth))
                {
                    if (edit.Aim.HasValue)
                    {
                        // The frame is read now, after every parent edit, so parents carry their children.
                        Quaternion frame = characterSpace;
                        HumanBodyBones? anchor = rest == null || !bodySpace ? null : AimAnchor(animator, edit.Bone);
                        Transform anchorTransform = anchor.HasValue ? animator.GetBoneTransform(anchor.Value) : null;
                        if (anchorTransform != null) frame = anchorTransform.rotation * Quaternion.Inverse(RestRelative(animator, anchorTransform, rest));
                        Vector3 target = frame * edit.Aim.Value;
                        Vector3 current = edit.AimAt.position - edit.Transform.position;
                        if (current.sqrMagnitude < 1e-8f) { }
                        else if (rest != null)
                        {
                            // Swing from the rest pose, not from the base pose: the bone keeps its rest
                            // twist, so reaching a direction never piles up twist the joint cannot take.
                            Quaternion restWorld = frame * RestRelative(animator, edit.Transform, rest);
                            Vector3 localChild = Quaternion.Inverse(edit.Transform.rotation) * current;
                            edit.Transform.rotation = Quaternion.FromToRotation(restWorld * localChild, target) * restWorld;
                        }
                        else edit.Transform.rotation = Quaternion.FromToRotation(current, target) * edit.Transform.rotation;
                    }
                    if (edit.Rotate.HasValue) edit.Transform.localRotation = edit.Transform.localRotation * edit.Rotate.Value;
                }
                var derived = new HumanPose();
                handler.GetHumanPose(ref derived);
                derived.bodyPosition = new Vector3(0, derived.bodyPosition.y, 0);
                return derived;
            }
            finally
            {
                foreach (var item in saved) item.Value.Restore(item.Key);
            }
        }

        struct TransformSnapshot
        {
            readonly Vector3 position;
            readonly Quaternion rotation;
            public TransformSnapshot(Transform t) { position = t.localPosition; rotation = t.localRotation; }
            public void Restore(Transform t) { t.localPosition = position; t.localRotation = rotation; }
        }

        static void WritePoseClip(string file, string clipName, HumanPose pose)
        {
            var builder = new RuntimeAnimationClip.AnimBuilder { name = clipName, sampleRate = 30, loopTime = true };
            float[] times = { 0f, 1f / 30f };
            builder.SetHumanPositionCurve(times, new[] { pose.bodyPosition, pose.bodyPosition });
            builder.SetHumanRotationCurve(times, new[] { pose.bodyRotation, pose.bodyRotation });
            for (int i = 0; i < HumanTrait.MuscleCount; i++) builder.AddMuscleCurve(times, i, new[] { pose.muscles[i], pose.muscles[i] });
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            builder.ExportAsset(file);
        }

        static void DeletePoseFile(string file)
        {
            if (string.IsNullOrEmpty(file) || !Path.GetFileName(file).StartsWith(PosePreviewPrefix, StringComparison.Ordinal)) return;
            foreach (string path in new[] { file, file + ".bundle" })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { } // best effort: maintenance_cleanup retries later
            }
        }

        object PoseSave(CharacterAsset c, JObject p, PoseState state)
        {
            string name = Regex.Replace(Required(p, "name").Trim(), @"[^\w\- ]", "_");
            if (name.Length == 0 || name.Length > 80 || name.StartsWith(PosePreviewPrefix, StringComparison.Ordinal)) throw new ArgumentException("name must be 1..80 characters of letters, digits, space, - or _.");
            bool overwrite = (bool?)p["overwrite"] ?? false;
            string from = (string)p["from"] ?? "applied";
            var pose = new HumanPose();
            if (from == "applied")
            {
                if (state == null) throw new InvalidOperationException("NO_APPLIED_POSE: set a pose first, or pass from=live to capture what the character shows now.");
                pose.muscles = (float[])state.Muscles.Clone();
                pose.bodyPosition = state.BodyPosition;
                pose.bodyRotation = state.BodyRotation;
            }
            else if (from == "live")
            {
                PoseHandler(c).GetHumanPose(ref pose);
                pose.bodyPosition = new Vector3(0, pose.bodyPosition.y, 0);
            }
            else throw new ArgumentException("from must be applied or live.");

            string destination = (string)p["destination"] ?? "warudo";
            string file;
            if (destination == "warudo") file = Context.PersistentDataManager.GetFullPath("CharacterAnimations/" + name + ".anim");
            else if (destination == "path")
            {
                file = Required(p, "path");
                if (!Path.IsPathRooted(file) || !string.Equals(Path.GetExtension(file), ".anim", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("path must be an absolute path ending in .anim.");
                file = Path.GetFullPath(file);
                if (!Directory.Exists(Path.GetDirectoryName(file))) throw new ArgumentException("The folder does not exist: " + Path.GetDirectoryName(file));
            }
            else throw new ArgumentException("destination must be warudo or path.");
            if (File.Exists(file) && !overwrite) throw new InvalidOperationException("FILE_EXISTS: " + file + " (pass overwrite=true to replace it).");
            WritePoseClip(file, name, pose);
            if (destination == "warudo" && File.Exists(file + ".bundle")) File.Delete(file + ".bundle"); // WARUDO rebuilds its cache from the new clip.
            return new
            {
                character = c.Name, file, from,
                uri = destination == "warudo" ? AnimationUriPrefix + name + ".anim" : null,
                note = destination == "warudo" ? "Now selectable as an idle or one-shot animation in WARUDO." : "Humanoid .anim clip; import it into a Unity project."
            };
        }
    }
}
