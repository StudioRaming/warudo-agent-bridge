using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Warudo.Core;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;
using Warudo.Plugins.Core.Assets.Cinematography;

namespace StudioRaming.WarudoAgentBridge
{
    // Analysis renders: the subject reduced to what a check needs, for an agent to read at a glance.
    // clay: flat unlit mannequin colored by body part. skeleton: bones and joints by part over a faint
    // silhouette. wireframe: visible triangle edges by part. All are drawn on the CPU from the live
    // skinned meshes and bones, so WARUDO's view, materials and pipeline are never touched, there is
    // no background, and several viewpoints fit in one image.
    public sealed partial class AgentBridgePlugin
    {
        struct Segment
        {
            public Vector3 A, B;
            public Color32 Color;
            public int Width;
            public byte Limb;
        }

        sealed class AnalysisScene
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<byte> Limbs = new List<byte>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<Segment> Bones = new List<Segment>();
            public readonly List<Vector3> Joints = new List<Vector3>();
            public Vector3 Root, Forward, Right;
            public Vector3? Face;
            public Vector3 HeadPosition;
        }

        sealed class AnalysisView
        {
            public string Name;
            public bool Perspective;
            public Vector3 Right, Up, Forward;
            public Matrix4x4 ViewMatrix, ViewProjection;
            public float Aspect, Near;
            public Vector3 CameraPosition;
            public int X0, Y0, Size;
            public int ClipX0, ClipY0, ClipX1, ClipY1; // drawable pixels; the camera view is its letterboxed frame
        }

        static readonly Color32 Background = new Color32(34, 35, 40, 255);
        static readonly Color32 Torso = new Color32(200, 200, 204, 255);

        // Left side warm, right side cool; upper limb darker than lower limb, hands and feet lightest.
        static Color32 PartColor(HumanBodyBones bone)
        {
            string name = bone.ToString();
            bool left = name.StartsWith("Left", StringComparison.Ordinal);
            bool right = name.StartsWith("Right", StringComparison.Ordinal);
            bool finger = name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little");
            if (name.EndsWith("Eye", StringComparison.Ordinal) || bone == HumanBodyBones.Head || bone == HumanBodyBones.Jaw) return new Color32(240, 205, 70, 255);
            if (bone == HumanBodyBones.Neck) return new Color32(225, 215, 150, 255);
            if (left || right)
            {
                if (name.EndsWith("Shoulder", StringComparison.Ordinal) || name.EndsWith("UpperArm", StringComparison.Ordinal))
                    return left ? new Color32(200, 60, 50, 255) : new Color32(45, 105, 215, 255);
                if (name.EndsWith("LowerArm", StringComparison.Ordinal))
                    return left ? new Color32(250, 115, 95, 255) : new Color32(95, 160, 255, 255);
                if (name.EndsWith("Hand", StringComparison.Ordinal) || finger)
                    return left ? new Color32(255, 180, 170, 255) : new Color32(170, 205, 255, 255);
                if (name.EndsWith("UpperLeg", StringComparison.Ordinal))
                    return left ? new Color32(215, 125, 35, 255) : new Color32(30, 150, 135, 255);
                if (name.EndsWith("LowerLeg", StringComparison.Ordinal))
                    return left ? new Color32(245, 170, 60, 255) : new Color32(55, 200, 180, 255);
                return left ? new Color32(255, 210, 140, 255) : new Color32(160, 240, 225, 255); // Foot, Toes
            }
            return Torso;
        }

        static readonly Dictionary<string, string> Legend = new Dictionary<string, string>
        {
            { "left arm", "red: shoulder/upper arm dark, forearm light, hand pale" },
            { "right arm", "blue: upper arm dark, forearm light, hand pale" },
            { "left leg", "orange: thigh dark, shin light, foot pale" },
            { "right leg", "teal: thigh dark, shin light, foot pale" },
            { "head", "yellow (magenta line = where the face points)" },
            { "torso", "light gray" },
            { "floor", "grid at the character's feet; green arrow = character facing" },
        };

        async UniTask<object> AnalysisCapture(JObject p)
        {
            float started = Time.realtimeSinceStartup;
            string mode = ((string)p["mode"] ?? "clay").ToLowerInvariant();
            if (mode != "clay" && mode != "skeleton" && mode != "wireframe") throw new ArgumentException("mode must be clay, skeleton or wireframe.");
            int size = Mathf.Clamp((int?)p["size"] ?? 560, 256, 1024);
            string[] viewNames = (p["views"] as JArray)?.Select(v => ((string)v ?? "").ToLowerInvariant()).ToArray() ?? new[] { "camera", "front", "right", "top" };
            if (viewNames.Length == 0 || viewNames.Length > 6) throw new ArgumentException("views takes 1..6 of camera, front, back, left, right, top, front-left, front-right, back-left, back-right.");
            string[] exclude = (p["exclude"] as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? new string[0];
            string[] include = (p["include"] as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? new string[0];
            bool bones = (bool?)p["bones"] ?? mode != "wireframe";
            bool floor = (bool?)p["floor"] ?? true;

            GameObjectAsset target;
            if (!string.IsNullOrEmpty((string)p["assetId"])) target = AssetById(p) as GameObjectAsset ?? throw new ArgumentException("assetId is not an asset with a GameObject.");
            else target = CharacterById(p);
            if (target.GameObject == null) throw new InvalidOperationException("ASSET_NOT_LOADED: " + target.Name + " has no GameObject yet.");
            CharacterAsset character = target as CharacterAsset;
            Animator animator = character != null && character.Animator != null && character.Animator.isHuman ? character.Animator : null;

            var used = new List<string>();
            var skipped = new List<string>();
            AnalysisScene scene = GatherAnalysisScene(target, animator, include, exclude, used, skipped);
            if (scene.Triangles.Count == 0 && scene.Bones.Count == 0) throw new InvalidOperationException("NOTHING_TO_DRAW: no visible mesh or humanoid bones on " + target.Name + ".");

            CameraAsset cameraAsset = null;
            try { cameraAsset = Context.PluginManager.GetPlugin<CorePlugin>().MainCameraAsset as CameraAsset; } catch (Exception) { }
            var views = viewNames.Select(name => MakeAnalysisView(name, scene, cameraAsset)).ToList();
            object joints = viewNames.Contains("camera") ? ScreenJoints(animator, cameraAsset) : null;
            int columns = views.Count <= 2 ? views.Count : views.Count <= 4 ? 2 : 3;
            int rows = (views.Count + columns - 1) / columns;
            for (int i = 0; i < views.Count; i++)
            {
                views[i].X0 = (i % columns) * size;
                views[i].Y0 = (i / columns) * size;
                views[i].Size = size;
                views[i].ClipX0 = views[i].X0; views[i].ClipY0 = views[i].Y0; views[i].ClipX1 = views[i].X0 + size; views[i].ClipY1 = views[i].Y0 + size;
            }
            int width = columns * size, height = rows * size;
            string path = Path.Combine(root, "evidence", activeRequest + ".png");

            // Everything Unity-facing was read above; the drawing needs no engine calls.
            byte[] png = await UniTask.RunOnThreadPool(() =>
            {
                var image = new Color32[width * height];
                for (int i = 0; i < image.Length; i++) image[i] = Background;
                foreach (AnalysisView view in views) DrawAnalysisView(image, width, height, view, scene, mode, bones, floor);
                // PNG rows run bottom-up; the canvas was drawn top-down.
                var flipped = new Color32[image.Length];
                for (int y = 0; y < height; y++) Array.Copy(image, y * width, flipped, (height - 1 - y) * width, width);
                return ImageConversion.EncodeArrayToPNG(flipped, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
            });
            File.WriteAllBytes(path, png);
            return new
            {
                path, width, height, mode, views = views.Select(v => v.Name).ToArray(),
                layout = columns + " x " + rows + " grid, left to right then top to bottom",
                contacts = Contacts(scene, animator), floorClearance = FloorClearance(scene, animator), nearFloor = NearFloor(scene, animator), joints,
                legend = Legend, renderers = used, excluded = skipped, triangles = scene.Triangles.Count / 3,
                ms = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f),
                hint = "Views other than camera are orthographic. Hide hair or skirts that cover the body with exclude (names from renderers)."
            };
        }

        // The asset's root position and horizontal facing, which the named views are relative to.
        static void FrameScene(AnalysisScene scene, GameObjectAsset target, Animator animator)
        {
            Transform rootTransform = target.GameObject.transform;
            scene.Root = animator != null ? animator.transform.position : rootTransform.position;
            Vector3 facing = animator != null ? animator.transform.forward : rootTransform.forward;
            facing.y = 0;
            scene.Forward = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
            scene.Right = Vector3.Cross(Vector3.up, scene.Forward);
        }

        // Reads the live skinned meshes and humanoid bones on the main thread.
        static AnalysisScene GatherAnalysisScene(GameObjectAsset target, Animator animator, string[] include, string[] exclude, List<string> used, List<string> skipped)
        {
            var scene = new AnalysisScene();
            Transform rootTransform = target.GameObject.transform;
            FrameScene(scene, target, animator);

            // Body part of every transform: its nearest humanoid ancestor (hair follows the head,
            // a skirt the hips). Non-humanoid assets are drawn in one torso color.
            var partOf = new Dictionary<Transform, HumanBodyBones>();
            if (animator != null)
            {
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    Transform t = animator.GetBoneTransform(bone);
                    if (t != null) partOf[t] = bone;
                }
            }
            Func<Transform, HumanBodyBones?> boneOf = t =>
            {
                for (Transform walk = t; walk != null; walk = walk.parent)
                {
                    HumanBodyBones found;
                    if (partOf.TryGetValue(walk, out found)) return found;
                }
                return null;
            };
            Func<Transform, Color32> colorOf = t => { HumanBodyBones? b = boneOf(t); return b.HasValue ? PartColor(b.Value) : Torso; };
            Func<Transform, byte> limbOf = t => LimbOf(boneOf(t));

            var baked = new Mesh();
            try
            {
                foreach (Renderer renderer in target.GameObject.GetComponentsInChildren<Renderer>(false))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    string name = RelativePath(rootTransform, renderer.transform);
                    bool wanted = include.Length == 0 || include.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!wanted || exclude.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) { skipped.Add(name); continue; }
                    Vector3[] vertices, normals;
                    int[] triangles;
                    Color32[] colors;
                    byte[] limbs;
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null && skinned.sharedMesh != null)
                    {
                        skinned.BakeMesh(baked, true);
                        Matrix4x4 toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                        vertices = baked.vertices.Select(v => toWorld.MultiplyPoint3x4(v)).ToArray();
                        normals = baked.normals.Select(n => toWorld.MultiplyVector(n)).ToArray();
                        triangles = baked.triangles;
                        colors = new Color32[vertices.Length];
                        limbs = new byte[vertices.Length];
                        Transform[] boneList = skinned.bones ?? new Transform[0];
                        BoneWeight[] weights = skinned.sharedMesh.isReadable ? skinned.sharedMesh.boneWeights : null;
                        var boneColors = boneList.Select(b => colorOf(b ?? skinned.transform)).ToArray();
                        var boneLimbs = boneList.Select(b => limbOf(b ?? skinned.transform)).ToArray();
                        Color32 own = colorOf(skinned.transform);
                        byte ownLimb = limbOf(skinned.transform);
                        for (int i = 0; i < colors.Length; i++)
                        {
                            int index = weights != null && i < weights.Length ? weights[i].boneIndex0 : -1;
                            bool known = index >= 0 && index < boneColors.Length;
                            colors[i] = known ? boneColors[index] : own;
                            limbs[i] = known ? boneLimbs[index] : ownLimb;
                        }
                        if (weights == null && animator != null) ColorByNearestBone(vertices, colors, limbs, animator);
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) { skipped.Add(name + " (mesh not readable)"); continue; }
                        Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
                        vertices = filter.sharedMesh.vertices.Select(v => toWorld.MultiplyPoint3x4(v)).ToArray();
                        normals = filter.sharedMesh.normals.Select(n => toWorld.MultiplyVector(n)).ToArray();
                        triangles = filter.sharedMesh.triangles;
                        colors = Enumerable.Repeat(colorOf(renderer.transform), vertices.Length).ToArray();
                        limbs = Enumerable.Repeat(limbOf(renderer.transform), vertices.Length).ToArray();
                    }
                    else continue;
                    if (normals.Length != vertices.Length) normals = new Vector3[vertices.Length];
                    int offset = scene.Vertices.Count;
                    scene.Vertices.AddRange(vertices);
                    scene.Normals.AddRange(normals.Select(n => n.normalized));
                    scene.Limbs.AddRange(limbs);
                    scene.Colors.AddRange(colors);
                    foreach (int index in triangles) scene.Triangles.Add(offset + index);
                    used.Add(name);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }

            if (animator != null)
            {
                var human = new Dictionary<Transform, HumanBodyBones>();
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    Transform t = animator.GetBoneTransform(bone);
                    if (t != null) human[t] = bone;
                }
                foreach (var pair in human)
                {
                    scene.Joints.Add(pair.Key.position);
                    Transform parent = pair.Key.parent;
                    while (parent != null && !human.ContainsKey(parent)) parent = parent.parent;
                    if (parent == null) continue;
                    string name = pair.Value.ToString();
                    bool finger = name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little");
                    bool eye = name.EndsWith("Eye", StringComparison.Ordinal) || pair.Value == HumanBodyBones.Jaw;
                    if (eye) continue;
                    scene.Bones.Add(new Segment { A = parent.position, B = pair.Key.position, Color = PartColor(pair.Value), Width = finger ? 2 : 5 });
                }
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null)
                {
                    scene.HeadPosition = head.position;
                    scene.Face = FaceDirection(animator, head);
                }
            }
            return scene;
        }

        // Where the face points: the head's rotation relative to its rest (import) pose applied to the
        // rest facing, which for a humanoid is the root's forward. Eye positions would bias this
        // upward because the eyes sit above the head joint.
        static Vector3? FaceDirection(Animator animator, Transform head)
        {
            SkeletonBone[] skeleton = animator.avatar == null ? null : animator.avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0) return null;
            var rest = new Dictionary<string, Quaternion>();
            foreach (SkeletonBone bone in skeleton)
                if (bone.name != null && !rest.ContainsKey(bone.name)) rest[bone.name] = bone.rotation;
            var chain = new List<Transform>();
            for (Transform t = head; t != null && t != animator.transform; t = t.parent) chain.Add(t);
            chain.Reverse();
            Quaternion restRelative = Quaternion.identity;
            foreach (Transform t in chain)
            {
                Quaternion local;
                restRelative *= rest.TryGetValue(t.name, out local) ? local : t.localRotation;
            }
            Quaternion currentRelative = Quaternion.Inverse(animator.transform.rotation) * head.rotation;
            return (animator.transform.rotation * (currentRelative * Quaternion.Inverse(restRelative) * Vector3.forward)).normalized;
        }

        const byte LimbOther = 0, LimbHead = 1, LimbLeftArm = 2, LimbRightArm = 3, LimbLeftLeg = 4, LimbRightLeg = 5;
        static readonly string[] LimbNames = { "torso", "head", "left arm", "right arm", "left leg", "right leg" };

        static byte LimbOf(HumanBodyBones? bone)
        {
            if (!bone.HasValue) return LimbOther;
            string name = bone.Value.ToString();
            if (bone == HumanBodyBones.Head || bone == HumanBodyBones.Neck || bone == HumanBodyBones.Jaw || name.EndsWith("Eye", StringComparison.Ordinal)) return LimbHead;
            bool left = name.StartsWith("Left", StringComparison.Ordinal), right = name.StartsWith("Right", StringComparison.Ordinal);
            if (!left && !right) return LimbOther;
            bool leg = name.EndsWith("UpperLeg", StringComparison.Ordinal) || name.EndsWith("LowerLeg", StringComparison.Ordinal) || name.EndsWith("Foot", StringComparison.Ordinal) || name.EndsWith("Toes", StringComparison.Ordinal);
            return leg ? (left ? LimbLeftLeg : LimbRightLeg) : (left ? LimbLeftArm : LimbRightArm);
        }

        // How close each hand and foot is to the rest of the body, and whether it sank in. Probes are
        // joint centres (wrist, finger tips, ankle, toes), so about 1-3 cm reads as touching; a
        // negative distance means the joint is inside another body part.
        static object Contacts(AnalysisScene scene, Animator animator)
        {
            if (animator == null || scene.Vertices.Count == 0) return null;
            var probes = new[]
            {
                new { name = "left hand", limb = LimbLeftArm, bones = new[] { HumanBodyBones.LeftHand, HumanBodyBones.LeftThumbDistal, HumanBodyBones.LeftIndexDistal, HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftRingDistal, HumanBodyBones.LeftLittleDistal } },
                new { name = "right hand", limb = LimbRightArm, bones = new[] { HumanBodyBones.RightHand, HumanBodyBones.RightThumbDistal, HumanBodyBones.RightIndexDistal, HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightRingDistal, HumanBodyBones.RightLittleDistal } },
                new { name = "left foot", limb = LimbLeftLeg, bones = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes } },
                new { name = "right foot", limb = LimbRightLeg, bones = new[] { HumanBodyBones.RightFoot, HumanBodyBones.RightToes } },
            };
            var result = new Dictionary<string, object>();
            foreach (var probe in probes)
            {
                string closestJoint = null, closestPart = null;
                float closest = float.MaxValue;
                foreach (HumanBodyBones bone in probe.bones)
                {
                    Transform t = animator.GetBoneTransform(bone);
                    if (t == null) continue;
                    Vector3 point = t.position;
                    int nearest = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < scene.Vertices.Count; i++)
                    {
                        if (scene.Limbs[i] == probe.limb) continue;
                        float d = (scene.Vertices[i] - point).sqrMagnitude;
                        if (d < best) { best = d; nearest = i; }
                    }
                    if (nearest < 0) continue;
                    float signed = Mathf.Sqrt(best) * (Vector3.Dot(point - scene.Vertices[nearest], scene.Normals[nearest]) < 0 ? -1f : 1f);
                    if (signed < closest) { closest = signed; closestJoint = bone.ToString(); closestPart = LimbNames[scene.Limbs[nearest]]; }
                }
                if (closestJoint == null) continue;
                float floor = probe.bones.Select(b => animator.GetBoneTransform(b)).Where(t => t != null).Min(t => t.position.y) - scene.Root.y;
                result[probe.name] = new
                {
                    nearestPart = closestPart, joint = closestJoint, distanceCm = Math.Round(closest * 100f, 1),
                    state = closest < 0 ? "inside the body" : closest < 0.03f ? "touching" : "clear",
                    lowestAboveFloorCm = Math.Round(floor * 100f, 1)
                };
            }
            return result;
        }

        // Lowest mesh point of each body part above the character's floor, lowest first. Kneeling,
        // sitting and lying poses rest on these, so "on the floor" and "below the floor" matter there.
        // "near" names the joint closest to that lowest point (a knee, the toes, the heel side of a foot).
        static object FloorClearance(AnalysisScene scene, Animator animator)
        {
            if (scene.Vertices.Count == 0) return null;
            var lowest = new Dictionary<byte, int>();
            for (int i = 0; i < scene.Vertices.Count; i++)
            {
                int current;
                if (!lowest.TryGetValue(scene.Limbs[i], out current) || scene.Vertices[i].y < scene.Vertices[current].y) lowest[scene.Limbs[i]] = i;
            }
            var result = new Dictionary<string, object>();
            foreach (var pair in lowest.OrderBy(p => scene.Vertices[p.Value].y))
            {
                Vector3 point = scene.Vertices[pair.Value];
                float y = point.y - scene.Root.y;
                string near = null;
                if (animator != null)
                {
                    float best = float.MaxValue;
                    foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                    {
                        if (bone == HumanBodyBones.LastBone || LimbOf(bone) != pair.Key) continue;
                        Transform t = animator.GetBoneTransform(bone);
                        if (t == null) continue;
                        float d = (t.position - point).sqrMagnitude;
                        if (d < best) { best = d; near = bone.ToString(); }
                    }
                }
                result[LimbNames[pair.Key]] = new
                {
                    lowestCm = Math.Round(y * 100f, 1), near,
                    state = y < -0.005f ? "below the floor" : y < 0.015f ? "on the floor" : "above the floor"
                };
            }
            return result;
        }

        // Mesh within 12 cm of the floor, grouped by the joint each point is closest to: the lowest
        // point near each knee, heel, toe or hand, so both knees and both toe tips can be set down.
        static object NearFloor(AnalysisScene scene, Animator animator)
        {
            if (animator == null || scene.Vertices.Count == 0) return null;
            // Part and position of each joint, read once: this runs on the main thread, over every
            // vertex near the floor (most of the mesh for a lying pose).
            var bones = new List<HumanBodyBones>();
            var limbs = new List<byte>();
            var positions = new List<Vector3>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t == null) continue;
                bones.Add(bone);
                limbs.Add(LimbOf(bone));
                positions.Add(t.position);
            }
            var lowest = new Dictionary<HumanBodyBones, float>();
            for (int i = 0; i < scene.Vertices.Count; i++)
            {
                float y = scene.Vertices[i].y - scene.Root.y;
                if (y > 0.12f) continue;
                HumanBodyBones nearest = HumanBodyBones.LastBone;
                float best = float.MaxValue;
                for (int j = 0; j < bones.Count; j++)
                {
                    if (limbs[j] != scene.Limbs[i]) continue;
                    float d = (positions[j] - scene.Vertices[i]).sqrMagnitude;
                    if (d < best) { best = d; nearest = bones[j]; }
                }
                if (nearest == HumanBodyBones.LastBone) continue;
                float current;
                if (!lowest.TryGetValue(nearest, out current) || y < current) lowest[nearest] = y;
            }
            var result = new Dictionary<string, double>();
            foreach (var pair in lowest.OrderBy(p => p.Value)) result[pair.Key.ToString()] = Math.Round(pair.Value * 100f, 1);
            return result;
        }

        // Where each humanoid joint lands in the camera frame, to line a pose up with a reference image.
        static object ScreenJoints(Animator animator, CameraAsset cameraAsset)
        {
            Camera camera = cameraAsset == null ? null : cameraAsset.Camera;
            if (animator == null || camera == null) return null;
            var positions = new Dictionary<string, float[]>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                string name = bone.ToString();
                if (name.EndsWith("Proximal", StringComparison.Ordinal) || name.EndsWith("Intermediate", StringComparison.Ordinal)) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t == null) continue;
                Vector3 v = camera.WorldToViewportPoint(t.position);
                if (v.z <= 0) continue;
                positions[name] = new[] { (float)Math.Round(v.x, 4), (float)Math.Round(1f - v.y, 4), (float)Math.Round(v.z, 3) };
            }
            return new { frame = "camera viewport: x 0..1 left to right, y 0..1 top to bottom, then distance in metres", aspect = camera.aspect, positions };
        }

        static void ColorByNearestBone(Vector3[] vertices, Color32[] colors, byte[] limbs, Animator animator)
        {
            var segments = new List<Segment>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                Transform t = animator.GetBoneTransform(bone);
                if (t == null) continue;
                Transform child = t.childCount > 0 ? t.GetChild(0) : null;
                segments.Add(new Segment { A = t.position, B = child != null ? child.position : t.position, Color = PartColor(bone), Limb = LimbOf(bone) });
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                float best = float.MaxValue;
                foreach (Segment s in segments)
                {
                    Vector3 ab = s.B - s.A;
                    float t = ab.sqrMagnitude < 1e-8f ? 0 : Mathf.Clamp01(Vector3.Dot(vertices[i] - s.A, ab) / ab.sqrMagnitude);
                    float d = (s.A + ab * t - vertices[i]).sqrMagnitude;
                    if (d < best) { best = d; colors[i] = s.Color; limbs[i] = s.Limb; }
                }
            }
        }

        static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (Transform walk = t; walk != null && walk != root; walk = walk.parent) parts.Add(walk.name);
            parts.Reverse();
            return parts.Count == 0 ? t.name : string.Join("/", parts);
        }

        static AnalysisView MakeAnalysisView(string name, AnalysisScene scene, CameraAsset cameraAsset)
        {
            Vector3 f = scene.Forward, r = scene.Right, up = Vector3.up;
            if (name == "camera")
            {
                Camera camera = cameraAsset == null ? null : cameraAsset.Camera;
                if (camera == null) throw new InvalidOperationException("The camera view needs the main Camera asset.");
                return new AnalysisView
                {
                    Name = "camera", Perspective = true, ViewMatrix = camera.worldToCameraMatrix,
                    ViewProjection = camera.projectionMatrix * camera.worldToCameraMatrix, Aspect = camera.aspect, Near = camera.nearClipPlane,
                    CameraPosition = camera.transform.position,
                };
            }
            Vector3 forward, upHint = up;
            switch (name)
            {
                case "front": forward = -f; break;
                case "back": forward = f; break;
                case "right": forward = -r; break;
                case "left": forward = r; break;
                case "top": forward = -up; upHint = f; break;
                case "front-right": forward = -(f + r).normalized; break;
                case "front-left": forward = -(f - r).normalized; break;
                case "back-right": forward = (f - r).normalized; break;
                case "back-left": forward = (f + r).normalized; break;
                default: throw new ArgumentException("Unknown view '" + name + "'. Use camera, front, back, left, right, top, front-left, front-right, back-left, back-right.");
            }
            Vector3 right = Vector3.Cross(upHint, forward).normalized;
            return new AnalysisView { Name = name, Forward = forward, Right = right, Up = Vector3.Cross(forward, right) };
        }

        // ---- CPU drawing (thread-pool safe: plain math on copied data) --------------------------

        // Where a view puts a world point in its panel: x, y in image pixels and the depth along the view direction.
        // Perspective views are letterboxed to the camera aspect (the bars are painted into image when it is given, and the
        // drawable rectangle is stored in view.Clip*); orthographic views fit the given points with a margin.
        static Func<Vector3, Vector3?> ProjectView(AnalysisView view, int width, Color32[] image, IEnumerable<Vector3> fit)
        {
            int size = view.Size;
            if (view.Perspective)
            {
                float viewWidth = size, viewHeight = size / Mathf.Max(0.1f, view.Aspect);
                if (viewHeight > size) { viewHeight = size; viewWidth = size * view.Aspect; }
                float offX = view.X0 + (size - viewWidth) / 2f, offY = view.Y0 + (size - viewHeight) / 2f;
                view.ClipX0 = Mathf.RoundToInt(offX);
                view.ClipY0 = Mathf.RoundToInt(offY);
                view.ClipX1 = Mathf.RoundToInt(offX + viewWidth);
                view.ClipY1 = Mathf.RoundToInt(offY + viewHeight);
                // Bars outside the camera frame are darker, so the frame edge reads as the frame.
                if (image != null)
                {
                    var bar = new Color32(22, 22, 26, 255);
                    for (int y = view.Y0; y < view.Y0 + size; y++)
                        for (int x = view.X0; x < view.X0 + size; x++)
                            if (x < view.ClipX0 || y < view.ClipY0 || x >= view.ClipX1 || y >= view.ClipY1) image[y * width + x] = bar;
                }
                Matrix4x4 vp = view.ViewProjection, v = view.ViewMatrix;
                float near = view.Near;
                return point =>
                {
                    float depth = -v.MultiplyPoint3x4(point).z;
                    if (depth <= near) return null;
                    Vector4 clip = vp * new Vector4(point.x, point.y, point.z, 1f);
                    if (Mathf.Abs(clip.w) < 1e-6f) return null;
                    return new Vector3(offX + (clip.x / clip.w * 0.5f + 0.5f) * viewWidth, offY + (1f - (clip.y / clip.w * 0.5f + 0.5f)) * viewHeight, depth);
                };
            }
            else
            {
                // Fit everything (mesh, joints, floor under the feet) with a margin.
                var points = fit.ToList();
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (Vector3 point in points)
                {
                    float x = Vector3.Dot(point, view.Right), y = Vector3.Dot(point, view.Up);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                float spanX = Mathf.Max(0.05f, maxX - minX), spanY = Mathf.Max(0.05f, maxY - minY);
                float scale = Mathf.Min(size * 0.86f / spanX, size * 0.86f / spanY);
                float midX = (minX + maxX) / 2f, midY = (minY + maxY) / 2f;
                Vector3 right = view.Right, up = view.Up, forward = view.Forward;
                float cx = view.X0 + size / 2f, cy = view.Y0 + size / 2f;
                return point => new Vector3(cx + (Vector3.Dot(point, right) - midX) * scale, cy - (Vector3.Dot(point, up) - midY) * scale, Vector3.Dot(point, forward));
            }
        }

        static void DrawAnalysisView(Color32[] image, int width, int height, AnalysisView view, AnalysisScene scene, string mode, bool drawBones, bool drawFloor)
        {
            int size = view.Size;
            int count = scene.Vertices.Count;
            var sx = new float[count];
            var sy = new float[count];
            var sz = new float[count];
            var ok = new bool[count];

            Func<Vector3, Vector3?> project = ProjectView(view, width, image, scene.Vertices.Concat(scene.Joints).Concat(new[] { scene.Root }));
            for (int i = 0; i < count; i++)
            {
                Vector3? s = project(scene.Vertices[i]);
                if (s == null) continue;
                sx[i] = s.Value.x; sy[i] = s.Value.y; sz[i] = s.Value.z; ok[i] = true;
            }

            var depth = new float[size * size];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.MaxValue;
            Vector3 viewDirection = view.Perspective ? Vector3.zero : view.Forward;
            Vector3 cameraPosition = view.CameraPosition;
            bool fill = mode != "wireframe";
            float silhouette = mode == "skeleton" ? 0.28f : 1f;

            for (int t = 0; t + 2 < scene.Triangles.Count; t += 3)
            {
                int a = scene.Triangles[t], b = scene.Triangles[t + 1], c = scene.Triangles[t + 2];
                if (!ok[a] || !ok[b] || !ok[c]) continue;
                Vector3 wa = scene.Vertices[a], wb = scene.Vertices[b], wc = scene.Vertices[c];
                Vector3 normal = Vector3.Cross(wb - wa, wc - wa);
                if (normal.sqrMagnitude < 1e-14f) continue;
                normal.Normalize();
                Vector3 toward = view.Perspective ? ((wa + wb + wc) / 3f - cameraPosition).normalized : viewDirection;
                float light = 0.35f + 0.65f * Mathf.Abs(Vector3.Dot(normal, toward));
                Color32 part = scene.Colors[a];
                var shaded = new Color32((byte)(Background.r + (part.r * light - Background.r) * silhouette), (byte)(Background.g + (part.g * light - Background.g) * silhouette), (byte)(Background.b + (part.b * light - Background.b) * silhouette), 255);
                FillTriangle(image, width, depth, view, sx[a], sy[a], sz[a], sx[b], sy[b], sz[b], sx[c], sy[c], sz[c], shaded, fill);
            }

            if (mode == "wireframe")
            {
                const float epsilon = 0.004f; // metres of depth an edge may sit behind its own surface
                for (int t = 0; t + 2 < scene.Triangles.Count; t += 3)
                {
                    int a = scene.Triangles[t], b = scene.Triangles[t + 1], c = scene.Triangles[t + 2];
                    if (!ok[a] || !ok[b] || !ok[c]) continue;
                    Color32 part = scene.Colors[a];
                    DrawLine(image, width, depth, view, sx[a], sy[a], sz[a], sx[b], sy[b], sz[b], part, 1, epsilon);
                    DrawLine(image, width, depth, view, sx[b], sy[b], sz[b], sx[c], sy[c], sz[c], part, 1, epsilon);
                    DrawLine(image, width, depth, view, sx[c], sy[c], sz[c], sx[a], sy[a], sz[a], part, 1, epsilon);
                }
            }

            if (drawFloor)
            {
                var grid = new Color32(90, 92, 102, 255);
                for (int i = -4; i <= 4; i++)
                {
                    DrawWorldLine(image, width, depth, view, project, scene.Root + scene.Right * (i * 0.25f) - scene.Forward, scene.Root + scene.Right * (i * 0.25f) + scene.Forward, grid, 1, 0.01f);
                    DrawWorldLine(image, width, depth, view, project, scene.Root + scene.Forward * (i * 0.25f) - scene.Right, scene.Root + scene.Forward * (i * 0.25f) + scene.Right, grid, 1, 0.01f);
                }
                var green = new Color32(80, 220, 110, 255);
                Vector3 tip = scene.Root + scene.Forward * 0.45f;
                DrawWorldLine(image, width, null, view, project, scene.Root, tip, green, 3, 0);
                DrawWorldLine(image, width, null, view, project, tip, tip - scene.Forward * 0.1f + scene.Right * 0.07f, green, 3, 0);
                DrawWorldLine(image, width, null, view, project, tip, tip - scene.Forward * 0.1f - scene.Right * 0.07f, green, 3, 0);
            }

            if (drawBones && scene.Bones.Count > 0)
            {
                // Far bones first so nearer ones stay on top; far ones are dimmer.
                var ordered = scene.Bones.Select(s => new { s, a = project(s.A), b = project(s.B) }).Where(x => x.a != null && x.b != null)
                    .OrderByDescending(x => (x.a.Value.z + x.b.Value.z) / 2f).ToList();
                float nearest = ordered.Count == 0 ? 0 : ordered.Min(x => (x.a.Value.z + x.b.Value.z) / 2f);
                float farthest = ordered.Count == 0 ? 1 : ordered.Max(x => (x.a.Value.z + x.b.Value.z) / 2f);
                var outline = new Color32(15, 15, 18, 255);
                foreach (var x in ordered)
                {
                    float along = farthest - nearest < 1e-4f ? 0 : ((x.a.Value.z + x.b.Value.z) / 2f - nearest) / (farthest - nearest);
                    float dim = 1f - 0.45f * along;
                    var color = new Color32((byte)(x.s.Color.r * dim), (byte)(x.s.Color.g * dim), (byte)(x.s.Color.b * dim), 255);
                    DrawLine(image, width, null, view, x.a.Value.x, x.a.Value.y, 0, x.b.Value.x, x.b.Value.y, 0, outline, x.s.Width + 2, 0);
                    DrawLine(image, width, null, view, x.a.Value.x, x.a.Value.y, 0, x.b.Value.x, x.b.Value.y, 0, color, x.s.Width, 0);
                }
                var joint = new Color32(245, 245, 245, 255);
                foreach (var x in ordered)
                    if (x.s.Width > 2) Stamp(image, width, view, x.b.Value.x, x.b.Value.y, 3, joint, null, 0);
                if (scene.Face.HasValue)
                    DrawWorldLine(image, width, null, view, project, scene.HeadPosition, scene.HeadPosition + scene.Face.Value * 0.22f, new Color32(255, 60, 220, 255), 4, 0);
            }

            RenderRaster.DrawText(image, width, view.X0 + 10, view.Y0 + 10, view.Name.ToUpperInvariant() + (view.Perspective ? "" : "  ORTHO"), 3, new Color32(235, 235, 240, 255));
            var border = new Color32(70, 72, 80, 255);
            for (int i = 0; i < size; i++)
            {
                Put(image, width, view, view.X0 + i, view.Y0, border);
                Put(image, width, view, view.X0, view.Y0 + i, border);
            }
        }

        static void Put(Color32[] image, int width, AnalysisView view, int x, int y, Color32 color)
        {
            if (x < view.X0 || y < view.Y0 || x >= view.X0 + view.Size || y >= view.Y0 + view.Size) return;
            image[y * width + x] = color;
        }

        static float Edge(float ax, float ay, float bx, float by, float px, float py) { return (bx - ax) * (py - ay) - (by - ay) * (px - ax); }

        static void FillTriangle(Color32[] image, int width, float[] depth, AnalysisView view, float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2, Color32 color, bool write)
        {
            float area = Edge(x0, y0, x1, y1, x2, y2);
            if (Mathf.Abs(area) < 1e-6f) return;
            int minX = Mathf.Max(view.ClipX0, Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2))));
            int maxX = Mathf.Min(view.ClipX1 - 1, Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2))));
            int minY = Mathf.Max(view.ClipY0, Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2))));
            int maxY = Mathf.Min(view.ClipY1 - 1, Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2))));
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float w0 = Edge(x1, y1, x2, y2, px, py) / area, w1 = Edge(x2, y2, x0, y0, px, py) / area, w2 = 1f - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * z0 + w1 * z1 + w2 * z2;
                    int d = (y - view.Y0) * view.Size + (x - view.X0);
                    if (z >= depth[d]) continue;
                    depth[d] = z;
                    if (write) image[y * width + x] = color;
                }
            }
        }

        static void Stamp(Color32[] image, int width, AnalysisView view, float cx, float cy, int radius, Color32 color, float[] depth, float z)
        {
            int r = Mathf.Max(0, radius);
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r + r) continue;
                    int x = Mathf.RoundToInt(cx) + dx, y = Mathf.RoundToInt(cy) + dy;
                    if (x < view.ClipX0 || y < view.ClipY0 || x >= view.ClipX1 || y >= view.ClipY1) continue;
                    if (depth != null && z > depth[(y - view.Y0) * view.Size + (x - view.X0)]) continue;
                    image[y * width + x] = color;
                }
        }

        // depth == null draws on top of everything; otherwise the line is hidden behind the mesh.
        static void DrawLine(Color32[] image, int width, float[] depth, AnalysisView view, float x0, float y0, float z0, float x1, float y1, float z1, Color32 color, int thickness, float epsilon)
        {
            float length = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
            int steps = Mathf.Clamp(Mathf.CeilToInt(length), 1, 8192);
            int radius = thickness / 2;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t, z = z0 + (z1 - z0) * t;
                if (radius == 0)
                {
                    int px = Mathf.RoundToInt(x), py = Mathf.RoundToInt(y);
                    if (px < view.ClipX0 || py < view.ClipY0 || px >= view.ClipX1 || py >= view.ClipY1) continue;
                    if (depth != null && z - epsilon > depth[(py - view.Y0) * view.Size + (px - view.X0)]) continue;
                    image[py * width + px] = color;
                }
                else Stamp(image, width, view, x, y, radius, color, depth, z - epsilon);
            }
        }

        static void DrawWorldLine(Color32[] image, int width, float[] depth, AnalysisView view, Func<Vector3, Vector3?> project, Vector3 a, Vector3 b, Color32 color, int thickness, float epsilon)
        {
            Vector3? pa = project(a), pb = project(b);
            if (pa == null || pb == null) return;
            DrawLine(image, width, depth, view, pa.Value.x, pa.Value.y, pa.Value.z, pb.Value.x, pb.Value.y, pb.Value.z, color, thickness, epsilon);
        }
    }
}
