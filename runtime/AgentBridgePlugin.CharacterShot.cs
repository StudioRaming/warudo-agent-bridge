using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Warudo.Core;
using Warudo.Core.Scenes;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;
using Warudo.Plugins.Core.Assets.Cinematography;

namespace StudioRaming.WarudoAgentBridge
{
    // character.shot: a photo of a Character asset from an orbit around the character, never from the user's camera.
    // A temporary camera (OffscreenCamera) copies the main camera's rendering and is placed on the orbit. Every view
    // proves the character is in it: its silhouette bounds, how much of the frame it covers, and how much of it is hidden
    // behind something else (three low-resolution renders compared: character only, whole scene, scene without it).
    public sealed partial class AgentBridgePlugin
    {
        static readonly string[] ShotFramings = { "full", "upper", "bust", "face" };
        static readonly string[] ShotBackgrounds = { "scene", "flat", "transparent" };
        static readonly string[] ShotViewNames = { "front", "back", "left", "right", "front-left", "front-right", "three-quarter", "top" };
        static readonly string[] ShotRotation = { "front", "front-right", "right", "back", "left", "front-left" };
        static readonly Color ShotMaskBackground = new Color(1f, 0f, 1f, 1f);
        const int ShotProofSize = 192;

        sealed class ShotPlan { public string Name; public float Yaw, Pitch; }

        sealed class ShotSubject
        {
            public CharacterAsset Asset;
            public Animator Animator;
            public readonly List<Renderer> Visible = new List<Renderer>(); // everything that draws, for the isolation
            public readonly List<string> Used = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            public Vector3 Min, Max;
            public V3 Forward, Right;
            public V3 Center;
            public V3[] Corners;
            public string BoxSource, BoneReport;
            public float BodyHeight;
        }

        sealed class ShotAttempt
        {
            public ShotPlan Plan;
            public float Distance;
            public Vector3 Position;
            public Quaternion Rotation;
            public MaskResult Mask;
            public float Coverage, Occlusion;
            public bool OcclusionKnown;
            public string[] Touched = new string[0]; // frame edges the full-body silhouette touches (full framing only)
            public bool TooSmall;
            public List<string> Occluders = new List<string>();
            public float Score { get { return Coverage * (1f - Occlusion); } }
        }

        // Edge contact only matters for the whole body; closer framings cut the body on purpose.
        static void ShotJudge(ShotAttempt a, string framing, float fit)
        {
            MaskResult m = a.Mask;
            if (m == null || m.Character == 0) return;
            if (framing == "full")
            {
                var edges = new List<string>();
                const float edge = 0.006f; // about one pixel of the proof render
                if (m.X0 <= edge) edges.Add("left");
                if (m.X1 >= 1f - edge) edges.Add("right");
                if (m.Y0 <= edge) edges.Add("top");
                if (m.Y1 >= 1f - edge) edges.Add("bottom");
                a.Touched = edges.ToArray();
            }
            a.TooSmall = Math.Max(m.X1 - m.X0, m.Y1 - m.Y0) < 0.4f * fit;
        }

        static Vector3 Unity(V3 v) { return new Vector3(v.X, v.Y, v.Z); }
        static V3 Math3(Vector3 v) { return new V3(v.x, v.y, v.z); }
        static double Round(float v, int digits = 3) { return Math.Round(v, digits); }

        object CharacterShot(JObject p)
        {
            float started = Time.realtimeSinceStartup;
            string framing = ((string)p["framing"] ?? "full").ToLowerInvariant();
            if (!ShotFramings.Contains(framing)) throw new ArgumentException("framing must be " + string.Join(", ", ShotFramings) + ".");
            string background = ((string)p["background"] ?? "scene").ToLowerInvariant();
            if (!ShotBackgrounds.Contains(background)) throw new ArgumentException("background must be " + string.Join(", ", ShotBackgrounds) + ".");
            int size = (int?)p["size"] ?? 1280;
            if (size < 256 || size > 2048) throw new ArgumentException("size must be 256..2048.");
            float pitch = (float?)p["pitch"] ?? 5f;
            if (float.IsNaN(pitch) || pitch < -80f || pitch > 89f) throw new ArgumentException("pitch must be -80..89 degrees.");
            float fit = (float?)p["fit"] ?? 0.85f;
            if (float.IsNaN(fit) || fit < 0.3f || fit > 1f) throw new ArgumentException("fit must be 0.3..1.");
            float? fovParameter = (float?)p["fov"];
            if (fovParameter.HasValue && (float.IsNaN(fovParameter.Value) || fovParameter.Value < 10f || fovParameter.Value > 120f)) throw new ArgumentException("fov must be 10..120 degrees.");
            List<ShotPlan> plans = ShotPlans(p, pitch);

            Scene scene = Context.OpenedScene;
            if (scene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            ShotSubject subject = ShotSubjectFor(scene, p, out List<ShotCandidate> candidates, out SubjectChoice choice);

            CameraAsset cameraAsset = Context.PluginManager.GetPlugin<CorePlugin>().MainCameraAsset as CameraAsset;
            Camera main = cameraAsset == null ? null : cameraAsset.Camera;
            if (main == null) throw new InvalidOperationException("CAMERA_UNAVAILABLE: the main Camera asset has no live Camera, so there is nothing to copy the rendering from.");
            float fov = fovParameter ?? main.fieldOfView;
            float? aspectParameter = (float?)p["aspect"];
            if (aspectParameter.HasValue && (float.IsNaN(aspectParameter.Value) || aspectParameter.Value < 0.3f || aspectParameter.Value > 4f)) throw new ArgumentException("aspect must be 0.3..4 (panel width / height).");
            float mainAspect = main.aspect > 0.4f && main.aspect < 4f ? main.aspect : 16f / 9f;
            bool portraitDefault = framing == "full" || framing == "upper"; // a standing character in 16:9 wastes most of the pixels
            float aspect = aspectParameter ?? (portraitDefault ? 0.75f : mainAspect);
            string aspectSource = aspectParameter.HasValue ? "aspect parameter" : portraitDefault ? "default portrait 3:4 for framing " + framing : "main camera";

            ShotFrame(subject, framing);
            int columns = plans.Count <= 3 ? plans.Count : plans.Count <= 4 ? 2 : 3, rows = (plans.Count + columns - 1) / columns;
            int panelWidth = Math.Max(128, (int)(size / Math.Max(columns, rows / aspect)));
            int panelHeight = Math.Max(72, (int)Math.Round(panelWidth / aspect));
            int proofHeight = Math.Max(16, (int)Math.Round(ShotProofSize / aspect));
            int width = columns * panelWidth, height = rows * panelHeight;
            var sheet = new Color32[width * height]; // top-down while it is built
            var views = new List<object>();
            var warnings = new List<string>();

            using (var camera = new OffscreenCamera(main))
            {
                warnings.AddRange(camera.Notes);
                for (int i = 0; i < plans.Count; i++)
                {
                    ShotPlan plan = plans[i];
                    ShotAttempt first = ShotEvaluate(camera, subject, plan, 1f, fov, aspect, fit, ShotProofSize, proofHeight);
                    ShotJudge(first, framing, fit);
                    ShotAttempt chosen = first;
                    object retry = null;
                    // Framing is judged by the silhouette bounds, never by how many pixels the character covers (a whole body in a
                    // frame is a small share by nature). Retry only when the whole body is cut by the frame (step back), or when more
                    // than half of it is hidden behind something (next view).
                    bool hidden = first.OcclusionKnown && first.Occlusion > 0.5f;
                    bool cut = first.Touched.Length > 0;
                    if (cut || hidden)
                    {
                        ShotPlan alternative = hidden ? ShotNextPlan(plan, pitch) : plan;
                        ShotAttempt second = ShotEvaluate(camera, subject, alternative, hidden ? 1f : 1.3f, fov, aspect, fit, ShotProofSize, proofHeight);
                        ShotJudge(second, framing, fit);
                        bool better = hidden ? (second.Touched.Length <= first.Touched.Length && second.Score > first.Score) : second.Touched.Length <= first.Touched.Length;
                        chosen = better ? second : first;
                        retry = new
                        {
                            reason = hidden ? "more than 50 % of the character was hidden behind something else" : "the character was cut by the frame edge (" + string.Join(", ", first.Touched) + ")",
                            tried = hidden ? "next view " + alternative.Name : "farther, distance x 1.3",
                            before = new { coverage = Round(first.Coverage), occlusion = first.OcclusionKnown ? (double?)Round(first.Occlusion) : null, touchedEdges = first.Touched },
                            after = new { coverage = Round(second.Coverage), occlusion = second.OcclusionKnown ? (double?)Round(second.Occlusion) : null, touchedEdges = second.Touched },
                            kept = chosen == second ? "the retry" : "the first attempt (the retry was not better)"
                        };
                    }
                    if (chosen.Touched.Length > 0) warnings.Add(chosen.Plan.Name + ": the character still touches the frame edge (" + string.Join(", ", chosen.Touched) + "); pass a larger distance with a smaller fit.");
                    if (chosen.TooSmall) warnings.Add(chosen.Plan.Name + ": the silhouette is far smaller than the fit target; the framing bounds are probably wrong (see framingSource).");
                    Color32[] pixels = ShotFinalRender(camera, subject, chosen, fov, panelWidth, panelHeight, background);
                    // ReadPixels is bottom-up; the sheet is built top-down so labels read normally.
                    int cx = (i % columns) * panelWidth, cy = (i / columns) * panelHeight;
                    for (int y = 0; y < panelHeight; y++)
                        Array.Copy(pixels, (panelHeight - 1 - y) * panelWidth, sheet, (cy + y) * width + cx, panelWidth);
                    if (plans.Count > 1) RenderRaster.DrawText(sheet, width, cx + 10, cy + 10, chosen.Plan.Name.ToUpperInvariant(), 3, new Color32(235, 235, 240, 255));
                    MaskResult m = chosen.Mask;
                    views.Add(new
                    {
                        view = chosen.Plan.Name, yaw = Round(chosen.Plan.Yaw, 1), pitch = Round(chosen.Plan.Pitch, 1), distance = Round(chosen.Distance),
                        cameraPosition = Wire(chosen.Position), cameraRotation = Wire(chosen.Rotation.eulerAngles), lookAt = Wire(Unity(subject.Center)),
                        panel = new { x = cx, y = cy, width = panelWidth, height = panelHeight },
                        characterBounds = m == null || m.Character == 0 ? null : new { x0 = Round(m.X0), y0 = Round(m.Y0), x1 = Round(m.X1), y1 = Round(m.Y1), frame = "0..1 of the view, origin top-left" },
                        coverage = Round(chosen.Coverage), occlusion = chosen.OcclusionKnown ? (double?)Round(chosen.Occlusion) : null,
                        touchedEdges = framing == "full" ? chosen.Touched : null, occluders = chosen.Occluders, visible = chosen.Coverage >= 0.02f && (!chosen.OcclusionKnown || chosen.Occlusion < 0.9f), retry
                    });
                }
            }

            var flipped = new Color32[sheet.Length];
            for (int y = 0; y < height; y++) Array.Copy(sheet, y * width, flipped, (height - 1 - y) * width, width);
            string path = Path.Combine(root, "evidence", activeRequest + ".png");
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(flipped, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height));
            return new
            {
                path, width, height, layout = columns + " x " + rows + " grid, left to right then top to bottom",
                subject = new { assetId = subject.Asset.Id.ToString(), name = subject.Asset.Name },
                otherCharacters = choice.Others.Select(c => new { assetId = c.Id, name = c.Name, enabled = c.Enabled, loaded = c.Loaded }).ToArray(),
                aspect = Round(aspect), aspectSource, framing, framingSource = subject.BoxSource, boneReport = subject.BoneReport, pivot = Wire(Unity(subject.Center)), bodyHeight = Round(subject.BodyHeight),
                fieldOfView = Round(fov, 1), fit, background, views,
                renderers = subject.Used, skippedRenderers = subject.Skipped, warnings,
                mainCameraMoved = false, sceneChanged = false,
                look = "A temporary camera copies the main camera (projection, clear flags, culling mask, URP or Built-in post-processing) and is placed on the orbit. WARUDO's own Beautify and HBAO effects are not copied, so colors can differ slightly from the screen.",
                ms = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f)
            };
        }

        static List<ShotPlan> ShotPlans(JObject p, float pitch)
        {
            var plans = new List<ShotPlan>();
            var names = (p["views"] as JArray)?.Select(v => ((string)v ?? "").ToLowerInvariant()).ToList();
            if (names == null)
            {
                float? yaw = (float?)p["yaw"];
                if (yaw.HasValue)
                {
                    if (float.IsNaN(yaw.Value) || float.IsInfinity(yaw.Value)) throw new ArgumentException("yaw must be a finite number of degrees.");
                    plans.Add(new ShotPlan { Name = "yaw " + Math.Round(yaw.Value, 1), Yaw = yaw.Value, Pitch = pitch });
                    return plans;
                }
                names = new List<string> { "front" };
            }
            if (names.Count == 0 || names.Count > 6) throw new ArgumentException("views takes 1..6 of " + string.Join(", ", ShotViewNames) + ".");
            foreach (string name in names)
            {
                float yaw; float? fixedPitch;
                if (!ShotMath.ViewAngles(name, out yaw, out fixedPitch)) throw new ArgumentException("Unknown view '" + name + "'. Use " + string.Join(", ", ShotViewNames) + ".");
                plans.Add(new ShotPlan { Name = name, Yaw = yaw, Pitch = fixedPitch ?? pitch });
            }
            return plans;
        }

        static ShotPlan ShotNextPlan(ShotPlan plan, float pitch)
        {
            int index = Array.IndexOf(ShotRotation, plan.Name);
            if (index >= 0)
            {
                string next = ShotRotation[(index + 1) % ShotRotation.Length];
                float yaw; float? fixedPitch;
                ShotMath.ViewAngles(next, out yaw, out fixedPitch);
                return new ShotPlan { Name = next, Yaw = yaw, Pitch = fixedPitch ?? pitch };
            }
            return new ShotPlan { Name = plan.Name + " +45", Yaw = plan.Yaw + 45f, Pitch = plan.Pitch };
        }

        // The one subject chooser of the render tools: assetId, else the selected Character, else the only one, else the
        // first enabled and loaded one; the others are listed in choice.Others. A Character asset is never "no character".
        CharacterAsset PickCharacter(JObject p, out List<ShotCandidate> candidates, out SubjectChoice choice)
        {
            Scene scene = Context.OpenedScene;
            if (scene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            Asset selected = scene.GetSelectedAsset();
            var characters = scene.GetAssets().Values.OfType<CharacterAsset>().ToList();
            candidates = characters.Select(c => new ShotCandidate
            {
                Id = c.Id.ToString(), Name = c.Name, Enabled = c.Enabled,
                Loaded = c.Active && c.GameObject != null && c.Animator != null, Selected = selected != null && selected.Id == c.Id
            }).ToList();
            var types = scene.GetAssets().Values.Select(x => x.GetType().Name).Distinct().ToList();
            SubjectChoice picked = ShotMath.PickSubject((string)p["assetId"], candidates, types);
            choice = picked;
            if (picked.Error != null) throw new InvalidOperationException(picked.Error + ": " + picked.Message);
            return characters.First(c => c.Id.ToString() == picked.Chosen.Id);
        }

        // For render.pass and render.isolate: an explicit assetId may be any asset with a GameObject; without one the Character chooser decides.
        GameObjectAsset PickTarget(JObject p, out object[] otherCharacters)
        {
            otherCharacters = new object[0];
            if (!string.IsNullOrEmpty((string)p["assetId"])) return AssetById(p) as GameObjectAsset ?? throw new ArgumentException("assetId is not an asset with a GameObject.");
            List<ShotCandidate> candidates; SubjectChoice choice;
            CharacterAsset character = PickCharacter(p, out candidates, out choice);
            otherCharacters = choice.Others.Select(c => (object)new { assetId = c.Id, name = c.Name, enabled = c.Enabled, loaded = c.Loaded }).ToArray();
            return character;
        }

        // Finds the Character, or says exactly why it cannot be photographed.
        ShotSubject ShotSubjectFor(Scene scene, JObject p, out List<ShotCandidate> candidates, out SubjectChoice choice)
        {
            CharacterAsset character = PickCharacter(p, out candidates, out choice);
            SubjectChoice picked = choice;
            string others = picked.Others.Count == 0 ? "" : " Other characters: " + string.Join(", ", picked.Others.Select(c => c.Name + " (" + c.Id + ")")) + ".";
            if (!character.Active || character.GameObject == null || character.Animator == null)
                throw new InvalidOperationException("CHARACTER_NOT_LOADED: " + character.Name + " (" + character.Id + ") is a Character asset but has no model yet" +
                    ((character as FromSourceGameObjectAsset)?.Source is string source && !string.IsNullOrEmpty(source) ? " (source " + source + " is still loading or failed; check warudo_runtime_logs)" : " (no source set)") +
                    ". Wait for it with warudo_asset_wait_loaded." + others);
            if (!character.Enabled || !character.GameObject.activeInHierarchy)
                throw new InvalidOperationException("CHARACTER_DISABLED: " + character.Name + " (" + character.Id + ") exists and is loaded, but the asset is disabled. Enable it with warudo_asset_enabled (assetId, enabled=true), shoot, and restore." + others);

            Animator animator = character.Animator;
            if (animator == null || !animator.isHuman) animator = character.GameObject.GetComponentsInChildren<Animator>(true).FirstOrDefault(x => x.isHuman) ?? animator;
            var subject = new ShotSubject { Asset = character, Animator = animator };
            Transform root = character.GameObject.transform;
            bool any = false;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var baked = new Mesh();
            try
            {
                foreach (Renderer renderer in character.GameObject.GetComponentsInChildren<Renderer>(true))
                {
                    string name = RelativePath(root, renderer.transform);
                    if (!renderer.gameObject.activeInHierarchy) { subject.Skipped.Add(name + ": inactive in the hierarchy"); continue; }
                    if (!renderer.enabled) { subject.Skipped.Add(name + ": renderer component disabled"); continue; }
                    subject.Visible.Add(renderer);
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null)
                    {
                        if (skinned.sharedMesh == null) { subject.Skipped.Add(name + ": SkinnedMeshRenderer without a mesh"); continue; }
                        skinned.BakeMesh(baked, true);
                        Matrix4x4 toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                        foreach (Vector3 v in baked.vertices) Extend(ref min, ref max, toWorld.MultiplyPoint3x4(v));
                        any = true; subject.Used.Add(name);
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) { subject.Skipped.Add(name + ": MeshRenderer without a mesh"); continue; }
                        Bounds b = filter.sharedMesh.bounds;
                        Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
                        for (int corner = 0; corner < 8; corner++)
                            Extend(ref min, ref max, toWorld.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))));
                        any = true; subject.Used.Add(name);
                    }
                    else subject.Skipped.Add(name + ": " + renderer.GetType().Name + " (not a mesh; drawn but not used for framing)");
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }
            if (!any)
                throw new InvalidOperationException("CHARACTER_NO_RENDERERS: " + character.Name + " (" + character.Id + ") is loaded and enabled but nothing in it renders a mesh. Renderers: " +
                    (subject.Skipped.Count == 0 ? "none found under the model" : string.Join("; ", subject.Skipped.Take(20))) + "." + others);
            subject.Min = min; subject.Max = max;
            return subject;
        }

        static void Extend(ref Vector3 min, ref Vector3 max, Vector3 v)
        {
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }

        // A humanoid bone of the subject: the Animator's avatar first, then WARUDO's own bone lookup.
        static Transform ShotBone(ShotSubject s, HumanBodyBones id)
        {
            Transform t = null;
            if (s.Animator != null && s.Animator.isHuman)
            {
                try { t = s.Animator.GetBoneTransform(id); } catch (Exception) { }
            }
            if (t == null && s.Asset != null)
            {
                try { t = s.Asset.GetBoneTransform(id, CharacterBoneLayer.Raw); } catch (Exception) { }
            }
            return t;
        }

        // The box that must fit the frame, from the humanoid bones whenever the avatar has them (Hips, Chest, shoulders, Head, feet),
        // the renderer vertex bounds only for the height and when there are no bones. boneReport says what was found, as shares of the body height.
        static void ShotFrame(ShotSubject s, string framing)
        {
            Vector3 forward = s.Animator != null ? s.Animator.transform.forward : s.Asset.GameObject.transform.forward;
            forward.y = 0;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            s.Forward = Math3(forward);
            s.Right = V3.Cross(new V3(0, 1, 0), s.Forward).Normalized;
            V3 up = new V3(0, 1, 0);
            V3 min = Math3(s.Min), max = Math3(s.Max);
            V3 center = (min + max) * 0.5f;
            Transform hips = ShotBone(s, HumanBodyBones.Hips), head = ShotBone(s, HumanBodyBones.Head);
            Transform chest = ShotBone(s, HumanBodyBones.UpperChest) ?? ShotBone(s, HumanBodyBones.Chest) ?? ShotBone(s, HumanBodyBones.Spine);
            Transform leftArm = ShotBone(s, HumanBodyBones.LeftUpperArm), rightArm = ShotBone(s, HumanBodyBones.RightUpperArm), neck = ShotBone(s, HumanBodyBones.Neck);
            float feetY = min.Y;
            foreach (HumanBodyBones foot in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
            {
                Transform t = ShotBone(s, foot);
                if (t != null) feetY = Math.Min(feetY, t.position.y);
            }
            float top = max.Y;
            float height = Math.Max(0.3f, top - feetY);
            s.BodyHeight = height;
            bool bones = hips != null && head != null;
            string why = bones ? null : s.Animator == null ? "the character has no Animator" : !s.Animator.isHuman ? "its Animator has no humanoid avatar" : "the humanoid avatar has no Hips or Head bone";
            if (bones)
            {
                Func<Transform, string> share = t => t == null ? "none" : ((t.position.y - feetY) / height).ToString("F2");
                s.BoneReport = "shares of the body height above the feet: hips " + share(hips) + ", chest " + share(chest) + ", shoulders " + share(leftArm) + ", neck " + share(neck) + ", head " + share(head) + ", top of mesh 1.00";
            }

            if (framing == "full")
            {
                float fullY = (min.Y + max.Y) / 2f, halfHeight = (max.Y - min.Y) / 2f;
                float vertexHalf = Math.Max(max.X - min.X, max.Z - min.Z) / 2f; // rotation-agnostic upper bound from the vertex bounds
                if (bones)
                {
                    // Height from the vertex bounds (hair, ears and soles included); horizontal extent from where the bones are,
                    // so a stray accessory or far physics bone does not widen the frame. The silhouette check then confirms it.
                    var points = new List<V3> { Math3(hips.position), Math3(head.position) };
                    foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                    {
                        Transform t = ShotBone(s, bone);
                        if (t != null) points.Add(Math3(t.position));
                    }
                    float r0 = float.MaxValue, r1 = float.MinValue, f0 = float.MaxValue, f1 = float.MinValue;
                    foreach (V3 point in points)
                    {
                        float r = V3.Dot(point, s.Right), fw = V3.Dot(point, s.Forward);
                        r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); f0 = Math.Min(f0, fw); f1 = Math.Max(f1, fw);
                    }
                    float halfWidthFull = Math.Min(vertexHalf, Math.Max(0.2f * height, (r1 - r0) / 2f + 0.08f * height));
                    float halfDepthFull = Math.Min(vertexHalf, Math.Max(0.12f * height, (f1 - f0) / 2f + 0.10f * height));
                    V3 horizontal = s.Right * ((r0 + r1) / 2f) + s.Forward * ((f0 + f1) / 2f);
                    s.Center = new V3(horizontal.X, fullY, horizontal.Z);
                    s.Corners = ShotMath.BoxCorners(s.Center, s.Right, up, s.Forward, halfWidthFull, halfHeight, halfDepthFull);
                    s.BoxSource = "humanoid bones (width and depth) and renderer vertex bounds (height)";
                    return;
                }
                s.Center = center;
                s.Corners = ShotMath.BoxCorners(center, new V3(1, 0, 0), up, new V3(0, 0, 1), (max.X - min.X) / 2f, halfHeight, (max.Z - min.Z) / 2f);
                s.BoxSource = "renderer vertex bounds (" + why + ")";
                return;
            }

            float yMin, yMax = top, halfWidth, halfDepth;
            V3 anchor; // horizontal position of the box
            if (bones)
            {
                s.BoxSource = "humanoid bones";
                V3 hipsPosition = Math3(hips.position), headPosition = Math3(head.position);
                // Standing: the head is well above the hips. Then the box can never reach below the proportions of a body (waist 0.50,
                // shoulders 0.68 of the height), so an odd bone placement cannot frame the legs.
                bool standing = headPosition.Y - hipsPosition.Y > 0.15f * height;
                float shoulderY = leftArm != null && rightArm != null ? (leftArm.position.y + rightArm.position.y) / 2f : neck != null ? neck.position.y : chest != null ? chest.position.y : feetY + 0.8f * height;
                float shoulderSpan = leftArm != null && rightArm != null ? Math.Abs(V3.Dot(Math3(leftArm.position) - Math3(rightArm.position), s.Right)) : 0.25f * height;
                float waistY = chest != null ? (hipsPosition.Y + chest.position.y) / 2f : hipsPosition.Y + 0.1f * height;
                if (framing == "upper")
                {
                    yMin = waistY; if (standing) yMin = Math.Max(yMin, feetY + 0.50f * height);
                    halfWidth = Math.Max(0.20f * height, shoulderSpan / 2f + 0.07f * height); halfDepth = 0.15f * height;
                    anchor = (hipsPosition + headPosition) * 0.5f;
                }
                else if (framing == "bust")
                {
                    yMin = shoulderY - 0.07f * height; if (standing) yMin = Math.Max(yMin, feetY + 0.68f * height);
                    halfWidth = Math.Max(0.14f * height, shoulderSpan / 2f + 0.04f * height); halfDepth = 0.12f * height; anchor = headPosition;
                }
                else
                {
                    Transform left = ShotBone(s, HumanBodyBones.LeftEye), right = ShotBone(s, HumanBodyBones.RightEye);
                    float eyeY = left != null && right != null ? (left.position.y + right.position.y) / 2f : head.position.y + 0.05f * height;
                    yMin = eyeY - 0.075f * height; yMax = eyeY + 0.085f * height; halfWidth = 0.08f * height; halfDepth = 0.08f * height; anchor = headPosition;
                }
            }
            else
            {
                float share = framing == "upper" ? 0.45f : framing == "bust" ? 0.25f : 0.13f;
                s.BoxSource = "renderer vertex bounds (" + why + "): top " + (int)Math.Round(share * 100) + " % of the height";
                yMin = max.Y - share * (max.Y - min.Y);
                anchor = center;
                halfWidth = Math.Min((max.X - min.X) / 2f, (framing == "upper" ? 0.30f : framing == "bust" ? 0.20f : 0.09f) * height); halfDepth = Math.Min((max.Z - min.Z) / 2f, 0.15f * height);
            }
            s.Center = new V3(anchor.X, (yMin + yMax) / 2f, anchor.Z);
            s.Corners = ShotMath.BoxCorners(s.Center, s.Right, up, s.Forward, halfWidth, (yMax - yMin) / 2f, halfDepth);
        }


        // One camera pose on the orbit and its visibility proof (low-resolution renders, nothing is kept).
        ShotAttempt ShotEvaluate(OffscreenCamera camera, ShotSubject s, ShotPlan plan, float distanceScale, float fov, float aspect, float fit, int proofWidth, int proofHeight)
        {
            V3 direction = ShotMath.OrbitDirection(s.Forward, s.Right, plan.Yaw, plan.Pitch);
            float distance = ShotMath.FitDistance(s.Center, s.Corners, direction, fov, aspect, fit, 0.3f) * distanceScale;
            V3 position = s.Center + direction * distance, forward, up;
            ShotMath.LookAt(position, s.Center, out forward, out up);
            var attempt = new ShotAttempt { Plan = plan, Distance = distance, Position = Unity(position), Rotation = Quaternion.LookRotation(Unity(forward), Unity(up)) };
            camera.Place(attempt.Position, attempt.Rotation, fov);
            int mask = camera.CullingMask;
            Color32[] full = camera.Render(proofWidth, proofHeight, mask, camera.ClearFlags, camera.Background, false, false);
            using (LayerIsolation isolation = LayerIsolation.Create(s.Visible))
            {
                if (isolation != null)
                {
                    Color32[] only = camera.Render(proofWidth, proofHeight, 1 << isolation.Layer, CameraClearFlags.SolidColor, ShotMaskBackground, false, false);
                    Color32[] without = camera.Render(proofWidth, proofHeight, mask & ~(1 << isolation.Layer), camera.ClearFlags, camera.Background, false, false);
                    attempt.Mask = ShotMath.Compare(only, ShotMaskBackground, full, without, proofWidth, proofHeight);
                    attempt.Coverage = attempt.Mask.Coverage; attempt.Occlusion = attempt.Mask.Occlusion; attempt.OcclusionKnown = true;
                }
            }
            if (attempt.Mask == null)
            {
                // No unused layer to isolate the character on: estimate the coverage from the projected box and say occlusion is unknown.
                float x0 = 1, y0 = 1, x1 = 0, y1 = 0;
                foreach (V3 corner in s.Corners)
                {
                    Vector3 v = camera.Camera.WorldToViewportPoint(Unity(corner));
                    if (v.z <= 0) continue;
                    x0 = Math.Min(x0, v.x); x1 = Math.Max(x1, v.x); y0 = Math.Min(y0, v.y); y1 = Math.Max(y1, v.y);
                }
                attempt.Coverage = Math.Max(0f, Math.Min(1f, x1 - x0)) * Math.Max(0f, Math.Min(1f, y1 - y0));
            }
            else if (attempt.Occlusion > 0.2f) attempt.Occluders = ShotOccluders(camera.Camera, s, attempt.Mask);
            return attempt;
        }

        // Names what is in front of the hidden part: renderer bounds hit by rays through hidden pixels before the character's own bounds.
        List<string> ShotOccluders(Camera camera, ShotSubject s, MaskResult mask)
        {
            var own = new HashSet<Renderer>(s.Visible);
            var counts = new Dictionary<string, int>();
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var characterBounds = new Bounds(Unity((Math3(s.Min) + Math3(s.Max)) * 0.5f), Unity(Math3(s.Max) - Math3(s.Min)));
            foreach (float[] sample in mask.HiddenSamples)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(sample[0], sample[1], 0));
                float entry;
                if (!characterBounds.IntersectRay(ray, out entry)) continue;
                Renderer nearest = null; float nearestDistance = entry;
                foreach (Renderer renderer in all)
                {
                    if (own.Contains(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if ((camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                    float distance;
                    if (renderer.bounds.IntersectRay(ray, out distance) && distance > camera.nearClipPlane && distance < nearestDistance) { nearest = renderer; nearestDistance = distance; }
                }
                if (nearest == null) continue;
                string name = ObjectPath(nearest.transform);
                int count;
                counts[name] = counts.TryGetValue(name, out count) ? count + 1 : 1;
            }
            return counts.OrderByDescending(c => c.Value).Take(5).Select(c => c.Key + " (" + c.Value + " of " + mask.HiddenSamples.Count + " samples, renderer bounds test)").ToList();
        }

        // The accepted pose at full size, on the chosen background.
        Color32[] ShotFinalRender(OffscreenCamera camera, ShotSubject s, ShotAttempt attempt, float fov, int width, int height, string background)
        {
            camera.Place(attempt.Position, attempt.Rotation, fov);
            if (background == "scene") return camera.Render(width, height, camera.CullingMask, camera.ClearFlags, camera.Background, true, false);
            using (LayerIsolation isolation = LayerIsolation.Create(s.Visible))
            {
                if (isolation == null) throw new InvalidOperationException("NO_FREE_LAYER: background=" + background + " needs one unused Unity layer to show the character alone; use background=scene.");
                bool transparent = background == "transparent";
                return camera.Render(width, height, 1 << isolation.Layer, CameraClearFlags.SolidColor, transparent ? new Color(0, 0, 0, 0) : new Color(0.78f, 0.79f, 0.82f, 1f), true, transparent);
            }
        }
    }
}
