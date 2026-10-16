using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Warudo.Core;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;
using Warudo.Plugins.Core.Assets.Cinematography;

namespace StudioRaming.WarudoAgentBridge
{
    // render.pass: the clay view's CPU rasterizer, with one pass per kind of render information an agent may want.
    // Everything that touches the engine (the live skinned meshes, the materials, the textures a renderer really uses
    // right now, the light) is read on the main thread first; the drawing then runs on the thread pool from plain
    // arrays (see RenderRaster), so WARUDO's view, materials and pipeline are never changed.
    public sealed partial class AgentBridgePlugin
    {
        static readonly string[] PassNames = { "parts", "albedo", "texture", "alpha", "normals", "depth", "uv", "lighting", "overlap" };
        const int MaxPassPanels = 12;
        const int PassTextureSide = 1024; // textures are read at most this big (the GPU filters the downscale)

        sealed class PassSlotInfo
        {
            public string Path, RendererType, Material, Shader, Mode;
            public int Slot;
            public float Cutoff = -1;
            public string TextureProperty, TextureSource, TextureName, ChosenReason;
            public int TextureWidth, TextureHeight;
            public bool TextureIsRenderTexture;
            public float[] Tint = { 1, 1, 1, 1 };
            public readonly List<string> Notes = new List<string>();
        }

        struct PassTexture
        {
            public Texture Texture;
            public string Source, Reason;
            public float Sx, Sy, Ox, Oy;
        }

        // Textures are copied to CPU memory once per call: through a temporary RenderTexture, so compressed and
        // unreadable textures work, and so a texture that is itself a RenderTexture (a plugin's recolor) is read as it is now.
        sealed class PassTextureReader
        {
            readonly Dictionary<Texture, RasterTexture> cache = new Dictionary<Texture, RasterTexture>();
            readonly Dictionary<Texture, string> failures = new Dictionary<Texture, string>();

            public RasterTexture Read(Texture texture, out string problem)
            {
                problem = null;
                if (texture == null) return null;
                RasterTexture found;
                if (cache.TryGetValue(texture, out found)) return found;
                if (failures.TryGetValue(texture, out problem)) return null;
                try
                {
                    if (texture.dimension != TextureDimension.Tex2D) throw new InvalidOperationException("not a 2D texture (" + texture.dimension + ")");
                    int width = texture.width, height = texture.height;
                    if (width < 1 || height < 1) throw new InvalidOperationException("texture has no size");
                    int longest = Math.Max(width, height);
                    if (longest > PassTextureSide) { width = Math.Max(1, width * PassTextureSide / longest); height = Math.Max(1, height * PassTextureSide / longest); }
                    bool srgb = texture.isDataSRGB;
                    RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                    RenderTexture previous = RenderTexture.active;
                    Texture2D readback = null;
                    try
                    {
                        Graphics.Blit(texture, target);
                        RenderTexture.active = target;
                        readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !srgb);
                        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        found = new RasterTexture { Width = width, Height = height, Pixels = readback.GetPixels32(), Clamp = texture.wrapMode == TextureWrapMode.Clamp };
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(target);
                        if (readback != null) UnityEngine.Object.Destroy(readback);
                    }
                    cache[texture] = found;
                    return found;
                }
                catch (Exception e)
                {
                    problem = texture.name + ": " + e.Message;
                    failures[texture] = problem;
                    return null;
                }
            }
        }

        sealed class PassScene
        {
            public RasterMesh Mesh;
            public RasterSlot[] Slots;
            public List<PassSlotInfo> Infos = new List<PassSlotInfo>();
            public List<string> Used = new List<string>();
            public List<string> Skipped = new List<string>();
            public AnalysisScene Frame;
        }

        sealed class PassPanel
        {
            public string Pass, View;
            public Color32[] Pixels;
        }

        sealed class PassOutput
        {
            public List<PassPanel> Panels = new List<PassPanel>();
            public Dictionary<string, SlotStats> CameraStats = new Dictionary<string, SlotStats>();
            public AnalysisView CameraView;
            public Dictionary<string, List<object>> Notes = new Dictionary<string, List<object>>();
        }

        async UniTask<object> RenderPassCapture(JObject p)
        {
            float started = Time.realtimeSinceStartup;
            string[] passes = (p["passes"] as JArray)?.Select(v => ((string)v ?? "").ToLowerInvariant()).ToArray() ?? throw new ArgumentException("passes is required: one or more of " + string.Join(", ", PassNames) + ".");
            if (passes.Length == 0 || passes.Length > PassNames.Length || passes.Distinct().Count() != passes.Length || passes.Any(x => !PassNames.Contains(x)))
                throw new ArgumentException("passes takes 1.." + PassNames.Length + " different names of " + string.Join(", ", PassNames) + ".");
            string[] viewNames = (p["views"] as JArray)?.Select(v => ((string)v ?? "").ToLowerInvariant()).ToArray() ?? new[] { "camera" };
            if (viewNames.Length == 0 || viewNames.Length > 6) throw new ArgumentException("views takes 1..6 of camera, front, back, left, right, top, front-left, front-right, back-left, back-right.");
            int size = (int?)p["size"] ?? 640;
            if (size < 256 || size > 1024) throw new ArgumentException("size must be 256..1024 pixels per view.");
            int maxSize = (int?)p["maxSize"] ?? 1600;
            if (maxSize < 256 || maxSize > 4096) throw new ArgumentException("maxSize must be 256..4096.");
            string textureProperty = (string)p["textureProperty"];
            if (textureProperty != null && (textureProperty.Length == 0 || textureProperty.Length > 128 || textureProperty.Any(char.IsWhiteSpace))) throw new ArgumentException("textureProperty must be a shader property name such as _BaseMap.");
            if (passes.Contains("texture") && string.IsNullOrEmpty(textureProperty)) throw new ArgumentException("textureProperty is required for the texture pass (for example _BaseMap, _MainTex, _ShadeTex or _EmissionMap).");
            if (passes.Length * viewNames.Length > MaxPassPanels) throw new ArgumentException("passes x views is " + passes.Length * viewNames.Length + " panels; the limit is " + MaxPassPanels + ".");
            string[] exclude = (p["exclude"] as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? new string[0];
            string[] include = (p["include"] as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? new string[0];
            bool labels = (bool?)p["labels"] ?? true;

            GameObjectAsset target;
            if (!string.IsNullOrEmpty((string)p["assetId"])) target = AssetById(p) as GameObjectAsset ?? throw new ArgumentException("assetId is not an asset with a GameObject.");
            else target = CharacterById(p);
            if (target.GameObject == null) throw new InvalidOperationException("ASSET_NOT_LOADED: " + target.Name + " has no GameObject yet.");
            CharacterAsset character = target as CharacterAsset;
            Animator animator = character != null && character.Animator != null && character.Animator.isHuman ? character.Animator : null;

            CameraAsset cameraAsset = null;
            try { cameraAsset = Context.PluginManager.GetPlugin<CorePlugin>().MainCameraAsset as CameraAsset; } catch (Exception) { }

            PassScene scene = GatherPassScene(target, animator, include, exclude, passes, textureProperty);
            if (scene.Mesh.TriangleCount == 0) throw new InvalidOperationException("NOTHING_TO_DRAW: no visible mesh on " + target.Name + ". Skipped: " + string.Join("; ", scene.Skipped.Take(10)));
            float afterGather = Time.realtimeSinceStartup;

            var views = viewNames.Select(name => MakeAnalysisView(name, scene.Frame, cameraAsset)).ToList();
            foreach (AnalysisView view in views) { view.X0 = 0; view.Y0 = 0; view.Size = size; view.ClipX0 = 0; view.ClipY0 = 0; view.ClipX1 = size; view.ClipY1 = size; }
            Light light = passes.Contains("lighting") ? MainLight() : null;
            Vector3? lightToward = light == null ? (Vector3?)null : -light.transform.forward;

            PassOutput output = await UniTask.RunOnThreadPool(() => DrawPasses(scene, passes, views, size, labels, textureProperty, lightToward));
            float afterDraw = Time.realtimeSinceStartup;

            int count = output.Panels.Count;
            int columns = count <= 2 ? count : count <= 4 ? 2 : 3, rows = (count + columns - 1) / columns;
            int width = columns * size, height = rows * size;
            double scale = Math.Min(1.0, maxSize / (double)Math.Max(width, height));
            int outWidth = Math.Max(1, (int)Math.Round(width * scale)), outHeight = Math.Max(1, (int)Math.Round(height * scale));
            string path = Path.Combine(root, "evidence", activeRequest + ".png");
            byte[] png = await UniTask.RunOnThreadPool(() =>
            {
                var sheet = new Color32[width * height];
                for (int i = 0; i < count; i++) RenderRaster.Copy(sheet, width, output.Panels[i].Pixels, size, (i % columns) * size, (i / columns) * size);
                if (outWidth != width || outHeight != height) sheet = RenderRaster.Downscale(sheet, width, height, outWidth, outHeight);
                var flipped = new Color32[sheet.Length]; // PNG rows run bottom-up; the sheet was drawn top-down
                for (int y = 0; y < outHeight; y++) Array.Copy(sheet, y * outWidth, flipped, (outHeight - 1 - y) * outWidth, outWidth);
                return ImageConversion.EncodeArrayToPNG(flipped, GraphicsFormat.R8G8B8A8_UNorm, (uint)outWidth, (uint)outHeight);
            });
            File.WriteAllBytes(path, png);

            SlotStats camera = output.CameraStats.Count == 0 ? null : output.CameraStats.Values.First();
            AnalysisView cameraView = output.CameraView;
            var legend = new List<object>();
            for (int i = 0; i < scene.Infos.Count; i++)
            {
                PassSlotInfo info = scene.Infos[i];
                object bounds = null;
                int visible = 0;
                if (camera != null)
                {
                    visible = camera.Count[i];
                    if (visible > 0)
                    {
                        float fx = cameraView.ClipX1 - cameraView.ClipX0, fy = cameraView.ClipY1 - cameraView.ClipY0;
                        bounds = new
                        {
                            panelPixels = new[] { camera.MinX[i], camera.MinY[i], camera.MaxX[i] + 1, camera.MaxY[i] + 1 },
                            frame = new[] { Round((camera.MinX[i] - cameraView.ClipX0) / fx, 4), Round((camera.MinY[i] - cameraView.ClipY0) / fy, 4), Round((camera.MaxX[i] + 1 - cameraView.ClipX0) / fx, 4), Round((camera.MaxY[i] + 1 - cameraView.ClipY0) / fy, 4) },
                        };
                    }
                }
                Color32 part = scene.Slots[i].PartColor;
                legend.Add(new
                {
                    number = scene.Slots[i].Number, path = info.Path, renderer = info.RendererType, slot = info.Slot, material = info.Material, shader = info.Shader, mode = info.Mode,
                    cutoff = info.Cutoff < 0 ? (float?)null : info.Cutoff, partColor = new[] { (int)part.r, part.g, part.b },
                    texture = info.TextureProperty == null ? null : new
                    {
                        property = info.TextureProperty, source = info.TextureSource, name = info.TextureName, width = info.TextureWidth, height = info.TextureHeight,
                        renderTexture = info.TextureIsRenderTexture, tiling = new[] { scene.Slots[i].Auto.Sx, scene.Slots[i].Auto.Sy }, offset = new[] { scene.Slots[i].Auto.Ox, scene.Slots[i].Auto.Oy },
                    },
                    textureProperty = textureProperty == null ? null : info.ChosenReason ?? "found",
                    color = info.Tint, cameraBounds = bounds, visiblePixels = camera == null ? (int?)null : visible, notes = info.Notes,
                });
            }

            var panels = new List<object>();
            for (int i = 0; i < count; i++)
                panels.Add(new { pass = output.Panels[i].Pass, view = output.Panels[i].View, x = (int)Math.Round((i % columns) * size * scale), y = (int)Math.Round((i / columns) * size * scale), width = (int)Math.Round(size * scale), height = (int)Math.Round(size * scale) });
            var notes = new Dictionary<string, object>();
            foreach (var pair in output.Notes) notes[pair.Key] = pair.Value;
            if (light != null) notes["lighting"] = new { light = light.name, intensity = light.intensity, color = new[] { light.color.r, light.color.g, light.color.b }, towardLight = Wire(-light.transform.forward), ambient = 0.25 };
            else if (passes.Contains("lighting")) notes["lighting"] = "no directional light is active in the scene; the panels are lit from the viewer";

            return new
            {
                path, width = outWidth, height = outHeight, scale = Round((float)scale, 3), size, passes, views = views.Select(v => v.Name).ToArray(),
                layout = columns + " x " + rows + " grid, left to right then top to bottom, one panel per pass x view (passes first)",
                panels, slots = legend, passNotes = notes, renderers = scene.Used, skippedRenderers = scene.Skipped,
                triangles = scene.Mesh.TriangleCount, cameraFrame = cameraView == null ? null : "camera view: legend frame values are 0..1 of the camera frame, origin top-left",
                msMainThread = Mathf.RoundToInt((afterGather - started) * 1000f), msDrawing = Mathf.RoundToInt((afterDraw - afterGather) * 1000f),
                ms = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f),
                hint = "Numbers on the parts panel match slots[].number. Views other than camera are orthographic. Nothing in WARUDO changed."
            };
        }

        static Light MainLight()
        {
            Light sun = RenderSettings.sun;
            if (sun != null && sun.isActiveAndEnabled) return sun;
            return UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional && l.isActiveAndEnabled).OrderByDescending(l => l.intensity).FirstOrDefault();
        }

        // ---- main thread: what the renderers really use now -------------------------------------------------

        static bool BlockHas(MaterialPropertyBlock block, string property) { return block != null && !block.isEmpty && block.HasProperty(property); }

        static float MaterialFloat(Material material, MaterialPropertyBlock block, string property, float fallback)
        {
            if (BlockHas(block, property)) return block.GetFloat(property);
            return material.HasProperty(property) ? material.GetFloat(property) : fallback;
        }

        static PassTexture ResolveTexture(Material material, MaterialPropertyBlock block, string property)
        {
            var result = new PassTexture { Sx = 1, Sy = 1 };
            if (!material.HasProperty(property)) { result.Reason = "the shader has no property " + property; return result; }
            Texture texture = null;
            if (BlockHas(block, property))
            {
                texture = block.GetTexture(property);
                if (texture != null) result.Source = "property block";
            }
            if (texture == null)
            {
                texture = material.GetTexture(property);
                if (texture != null) result.Source = "material";
            }
            if (texture == null) { result.Reason = property + " has no texture"; return result; }
            result.Texture = texture;
            if (BlockHas(block, property + "_ST"))
            {
                Vector4 st = block.GetVector(property + "_ST");
                result.Sx = st.x; result.Sy = st.y; result.Ox = st.z; result.Oy = st.w;
            }
            else
            {
                Vector2 scale = material.GetTextureScale(property), offset = material.GetTextureOffset(property);
                result.Sx = scale.x; result.Sy = scale.y; result.Ox = offset.x; result.Oy = offset.y;
            }
            return result;
        }

        static PassTexture ResolveAuto(Material material, MaterialPropertyBlock block, out string property)
        {
            foreach (string name in new[] { "_BaseMap", "_MainTex" })
            {
                PassTexture found = ResolveTexture(material, block, name);
                if (found.Texture != null) { property = name; return found; }
            }
            foreach (string name in material.GetTexturePropertyNames())
            {
                if (name == "_BaseMap" || name == "_MainTex") continue;
                PassTexture found = ResolveTexture(material, block, name);
                if (found.Texture != null) { property = name; return found; }
            }
            property = null;
            return new PassTexture { Sx = 1, Sy = 1, Reason = "the material has no 2D texture" };
        }

        // cutout when the material clips alpha (URP/NiloToon _AlphaClip, lilToon cutout mode, the AlphaTest queue or keyword).
        static string MaterialMode(Material material, MaterialPropertyBlock block, out float cutoff)
        {
            bool clip = (material.HasProperty("_AlphaClip") && MaterialFloat(material, block, "_AlphaClip", 0) > 0.5f) || material.IsKeywordEnabled("_ALPHATEST_ON") ||
                        (material.HasProperty("_TransparentMode") && Math.Abs(MaterialFloat(material, block, "_TransparentMode", 0) - 1f) < 0.5f) ||
                        (material.renderQueue >= 2450 && material.renderQueue < 2500);
            cutoff = clip ? MaterialFloat(material, block, "_Cutoff", 0.5f) : -1f;
            if (clip) return "cutout";
            bool transparent = material.renderQueue >= 2500 || MaterialFloat(material, block, "_Surface", 0) > 0.5f || MaterialFloat(material, block, "_TransparentMode", 0) >= 1.5f;
            return transparent ? "transparent" : "opaque";
        }

        static Color MaterialTint(Material material, MaterialPropertyBlock block, string textureProperty)
        {
            foreach (string name in textureProperty == "_BaseMap" ? new[] { "_BaseColor", "_Color" } : new[] { "_Color", "_BaseColor" })
            {
                if (!material.HasProperty(name)) continue;
                return BlockHas(block, name) ? block.GetColor(name) : material.GetColor(name);
            }
            return Color.white;
        }

        PassScene GatherPassScene(GameObjectAsset target, Animator animator, string[] include, string[] exclude, string[] passes, string textureProperty)
        {
            var scene = new PassScene { Frame = new AnalysisScene() };
            FrameScene(scene.Frame, target, animator);
            Transform root = target.GameObject.transform;
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var group = new List<int>();
            var tris = new List<int>(); var triSlot = new List<int>();
            var slots = new List<RasterSlot>();
            var reader = new PassTextureReader();
            var block = new MaterialPropertyBlock();
            var baked = new Mesh();
            int rendererIndex = 0;
            try
            {
                foreach (Renderer renderer in target.GameObject.GetComponentsInChildren<Renderer>(true))
                {
                    string name = RelativePath(root, renderer.transform);
                    if (!renderer.gameObject.activeInHierarchy) { scene.Skipped.Add(name + ": inactive in the hierarchy"); continue; }
                    if (!renderer.enabled) { scene.Skipped.Add(name + ": renderer component disabled"); continue; }
                    bool wanted = include.Length == 0 || include.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!wanted || exclude.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) { scene.Skipped.Add(name + ": not selected by include/exclude"); continue; }
                    Mesh mesh; Matrix4x4 toWorld; Vector2[] uvs;
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null && skinned.sharedMesh != null)
                    {
                        skinned.BakeMesh(baked, true);
                        mesh = baked;
                        toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                        uvs = baked.uv;
                        if (uvs.Length != baked.vertexCount && skinned.sharedMesh.isReadable && skinned.sharedMesh.uv.Length == baked.vertexCount) uvs = skinned.sharedMesh.uv;
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) { scene.Skipped.Add(name + ": MeshRenderer without a mesh"); continue; }
                        if (!filter.sharedMesh.isReadable) { scene.Skipped.Add(name + ": mesh not readable (Read/Write is off in its import settings)"); continue; }
                        mesh = filter.sharedMesh;
                        toWorld = renderer.transform.localToWorldMatrix;
                        uvs = mesh.uv;
                    }
                    else { scene.Skipped.Add(name + ": " + renderer.GetType().Name + " is not a mesh renderer"); continue; }

                    Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                    int vertexBase = pos.Count / 3;
                    bool hasUv = uvs.Length == vertices.Length;
                    Matrix4x4 normalMatrix = toWorld.inverse.transpose;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 w = toWorld.MultiplyPoint3x4(vertices[i]);
                        pos.Add(w.x); pos.Add(w.y); pos.Add(w.z);
                        Vector3 n = normals.Length == vertices.Length ? normalMatrix.MultiplyVector(normals[i]).normalized : Vector3.zero;
                        nrm.Add(n.x); nrm.Add(n.y); nrm.Add(n.z);
                        uv.Add(hasUv ? uvs[i].x : 0f); uv.Add(hasUv ? uvs[i].y : 0f);
                        group.Add(rendererIndex);
                    }
                    Material[] materials = renderer.sharedMaterials;
                    bool drew = false;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        int[] indices = mesh.GetTriangles(sub);
                        if (indices.Length == 0) continue;
                        if (sub >= materials.Length) { scene.Skipped.Add(name + ": submesh " + sub + " has no material slot (" + materials.Length + " materials)"); continue; }
                        Material material = materials[sub];
                        if (material == null) { scene.Skipped.Add(name + ": slot " + sub + " has no material"); continue; }
                        renderer.GetPropertyBlock(block, sub);
                        RasterSlot slot = BuildSlot(slots.Count + 1, name, renderer, sub, material, block, reader, textureProperty, hasUv, scene.Infos);
                        slots.Add(slot);
                        foreach (int index in indices) tris.Add(vertexBase + index);
                        for (int t = 0; t < indices.Length / 3; t++) triSlot.Add(slots.Count - 1);
                        drew = true;
                    }
                    if (drew) { scene.Used.Add(name); rendererIndex++; }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }
            Color32[] palette = RenderRaster.Palette(slots.Count);
            for (int i = 0; i < slots.Count; i++) slots[i].PartColor = palette[i];
            scene.Slots = slots.ToArray();
            scene.Mesh = new RasterMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv = uv.ToArray(), Group = group.ToArray(), Tris = tris.ToArray(), TriSlot = triSlot.ToArray() };
            return scene;
        }

        static RasterSlot BuildSlot(int number, string path, Renderer renderer, int slotIndex, Material material, MaterialPropertyBlock block, PassTextureReader reader, string textureProperty, bool hasUv, List<PassSlotInfo> infos)
        {
            var info = new PassSlotInfo { Path = path, RendererType = renderer.GetType().Name, Slot = slotIndex, Material = material.name, Shader = material.shader == null ? null : material.shader.name };
            info.Mode = MaterialMode(material, block, out info.Cutoff);
            var slot = new RasterSlot { Number = number, Cutoff = info.Cutoff };
            if (!hasUv) info.Notes.Add("the mesh has no UV0: textures cannot be sampled");

            string autoProperty;
            PassTexture auto = ResolveAuto(material, block, out autoProperty);
            slot.Auto = BindTexture(auto, reader, info, "main texture");
            if (slot.Auto.Has)
            {
                info.TextureProperty = autoProperty; info.TextureSource = auto.Source; info.TextureName = auto.Texture.name;
                info.TextureWidth = auto.Texture.width; info.TextureHeight = auto.Texture.height; info.TextureIsRenderTexture = auto.Texture is RenderTexture;
            }
            else if (info.Mode == "cutout") info.Notes.Add("cutout without a readable texture: nothing is cut");
            if (info.Mode == "cutout" && !slot.Auto.Has) slot.Cutoff = -1;
            Color tint = MaterialTint(material, block, autoProperty);
            slot.TintR = tint.r; slot.TintG = tint.g; slot.TintB = tint.b; slot.TintA = tint.a;
            info.Tint = new[] { (float)Math.Round(tint.r, 3), (float)Math.Round(tint.g, 3), (float)Math.Round(tint.b, 3), (float)Math.Round(tint.a, 3) };

            slot.Display = slot.Auto;
            if (textureProperty != null)
            {
                PassTexture chosen = ResolveTexture(material, block, textureProperty);
                slot.Chosen = BindTexture(chosen, reader, info, textureProperty);
                if (slot.Chosen.Has)
                {
                    slot.Display = slot.Chosen;
                    info.ChosenReason = "found (" + chosen.Source + ", " + chosen.Texture.name + ")";
                }
                else info.ChosenReason = chosen.Reason ?? "could not be read";
            }
            return AddInfo(slot, info, infos);
        }

        static RasterSlot AddInfo(RasterSlot slot, PassSlotInfo info, List<PassSlotInfo> infos) { infos.Add(info); return slot; }

        static TexBinding BindTexture(PassTexture found, PassTextureReader reader, PassSlotInfo info, string what)
        {
            var binding = new TexBinding { Sx = found.Sx, Sy = found.Sy, Ox = found.Ox, Oy = found.Oy };
            if (found.Texture == null) return binding;
            string problem;
            binding.Texture = reader.Read(found.Texture, out problem);
            if (binding.Texture == null) info.Notes.Add(what + " unreadable: " + problem);
            return binding;
        }

        // ---- thread pool: rasterize every view once, shade every pass ---------------------------------------

        static PassOutput DrawPasses(PassScene scene, string[] passes, List<AnalysisView> views, int size, bool labels, string textureProperty, Vector3? lightToward)
        {
            RasterMesh mesh = scene.Mesh;
            RasterSlot[] slots = scene.Slots;
            int vertexCount = mesh.Pos.Length / 3;
            var output = new PassOutput();
            var fit = new List<Vector3>(vertexCount + 1);
            for (int i = 0; i < vertexCount; i++) fit.Add(new Vector3(mesh.Pos[3 * i], mesh.Pos[3 * i + 1], mesh.Pos[3 * i + 2]));
            fit.Add(scene.Frame.Root);
            bool needUncut = passes.Contains("alpha") && slots.Any(s => s.Cutoff >= 0);

            foreach (string passName in passes) output.Notes[passName] = new List<object>();
            foreach (AnalysisView view in views)
            {
                var template = new Color32[size * size];
                for (int i = 0; i < template.Length; i++) template[i] = Background;
                Func<Vector3, Vector3?> project = ProjectView(view, size, template, fit);
                var pr = new RasterProjection
                {
                    Size = size, ClipX0 = view.ClipX0, ClipY0 = view.ClipY0, ClipX1 = view.ClipX1, ClipY1 = view.ClipY1, Perspective = view.Perspective,
                    Sx = new float[vertexCount], Sy = new float[vertexCount], Depth = new float[vertexCount], InvW = new float[vertexCount], Ok = new bool[vertexCount],
                    CamX = view.CameraPosition.x, CamY = view.CameraPosition.y, CamZ = view.CameraPosition.z, FwdX = view.Forward.x, FwdY = view.Forward.y, FwdZ = view.Forward.z,
                };
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3? s = project(fit[i]);
                    if (s == null) continue;
                    pr.Sx[i] = s.Value.x; pr.Sy[i] = s.Value.y; pr.Depth[i] = s.Value.z; pr.InvW[i] = view.Perspective ? 1f / s.Value.z : 1f; pr.Ok[i] = true;
                }
                bool[] front = RenderRaster.Facing(mesh, pr);
                GBuffer cut = RenderRaster.Rasterize(mesh, slots, pr, front, true);
                GBuffer uncut = needUncut ? RenderRaster.Rasterize(mesh, slots, pr, front, false) : cut;
                SlotStats stats = RenderRaster.Stats(mesh, cut, slots.Length);
                if (view.Name == "camera") { output.CameraStats[view.Name] = stats; output.CameraView = view; }
                int scale = size >= 480 ? 2 : 1;

                foreach (string passName in passes)
                {
                    Color32[] img = (Color32[])template.Clone();
                    string caption = null;
                    switch (passName)
                    {
                        case "parts":
                            RenderRaster.ShadeParts(mesh, slots, cut, stats, img, labels);
                            caption = "NUMBERS = SLOTS IN THE LEGEND";
                            break;
                        case "albedo":
                            RenderRaster.ShadeAlbedo(mesh, slots, cut, img);
                            caption = "TEXTURE X COLOR, UNLIT";
                            break;
                        case "texture":
                            RenderRaster.ShadeTexture(mesh, slots, cut, img);
                            caption = (textureProperty + "  GREY = SLOT HAS NONE").ToUpperInvariant();
                            break;
                        case "alpha":
                            RenderRaster.ShadeAlpha(mesh, slots, uncut, img);
                            caption = "GREY = ALPHA  RED = CUT";
                            break;
                        case "normals":
                            RenderRaster.ShadeNormals(mesh, cut, front, img);
                            caption = "WORLD NORMAL XYZ = RGB";
                            int sw = 14 * scale;
                            for (int axis = 0; axis < 3; axis++)
                            {
                                Color32 swatch = axis == 0 ? new Color32(255, 128, 128, 255) : axis == 1 ? new Color32(128, 255, 128, 255) : new Color32(128, 128, 255, 255);
                                RenderRaster.FillRect(img, size, 10 + axis * 64 * scale / 2, size - 60 * scale / 2 - sw, sw, sw, swatch);
                                RenderRaster.DrawText(img, size, 10 + axis * 64 * scale / 2 + sw + 4, size - 60 * scale / 2 - sw + 2, "+" + "XYZ"[axis], scale, new Color32(235, 235, 240, 255));
                            }
                            break;
                        case "depth":
                        {
                            float near, far;
                            if (RenderRaster.DepthRange(cut, out near, out far))
                            {
                                RenderRaster.ShadeDepth(cut, img, near, far);
                                caption = view.Perspective ? "NEAR " + near.ToString("F2") + "M  FAR " + far.ToString("F2") + "M" : "NEAR 0M  FAR " + (far - near).ToString("F2") + "M (ORTHO)";
                                int barX = 10, barY = size - 56 * scale / 2 - 10 * scale, barW = 120 * scale;
                                for (int i = 0; i < barW; i++) RenderRaster.FillRect(img, size, barX + i, barY, 1, 8 * scale, RenderRaster.DepthColor(i / (float)(barW - 1)));
                                output.Notes["depth"].Add(new { view = view.Name, near = Math.Round(near, 3), far = Math.Round(far, 3), unit = view.Perspective ? "meters from the camera" : "meters along the view direction, near = 0 here" });
                            }
                            else output.Notes["depth"].Add(new { view = view.Name, note = "nothing drawn" });
                            break;
                        }
                        case "uv":
                            RenderRaster.ShadeUv(mesh, pr, cut, img);
                            RenderRaster.DrawSeams(RenderRaster.FindSeams(mesh), cut, pr, img);
                            caption = "R=U G=V LINES 0.1 WHITE=SEAM STRIPES=OUTSIDE 0-1";
                            break;
                        case "lighting":
                        {
                            Vector3 toward = lightToward ?? -view.Forward; // no light: a headlight, pointing back at the viewer
                            toward.Normalize();
                            RenderRaster.ShadeLighting(mesh, cut, front, img, toward.x, toward.y, toward.z, 0.25f);
                            caption = "WHITE MATERIAL N.L + AMBIENT 0.25";
                            break;
                        }
                        case "overlap":
                        {
                            int maxCover;
                            RenderRaster.ShadeOverlap(cut, img, out maxCover);
                            caption = "FRONT SURFACES PER PIXEL";
                            for (int n = 1; n <= 5; n++)
                            {
                                RenderRaster.FillRect(img, size, 10 + (n - 1) * 34 * scale / 2, size - 56 * scale / 2 - 10 * scale, 14 * scale, 14 * scale, RenderRaster.OverlapColor(n));
                                RenderRaster.DrawText(img, size, 10 + (n - 1) * 34 * scale / 2 + 14 * scale + 2, size - 56 * scale / 2 - 10 * scale + 2, n == 5 ? "5+" : n.ToString(), scale, new Color32(235, 235, 240, 255));
                            }
                            output.Notes["overlap"].Add(new { view = view.Name, mostSurfacesOnOnePixel = maxCover });
                            break;
                        }
                    }
                    if (caption != null) RenderRaster.DrawText(img, size, 10, size - 24 * scale / 2 - 4, caption, scale, new Color32(235, 235, 240, 255));
                    RenderRaster.DrawText(img, size, 10, 10, passName.ToUpperInvariant() + "  " + view.Name.ToUpperInvariant() + (view.Perspective ? "" : "  ORTHO"), 3, new Color32(235, 235, 240, 255));
                    var border = new Color32(70, 72, 80, 255);
                    for (int i = 0; i < size; i++) { img[i] = border; img[i * size] = border; }
                    output.Panels.Add(new PassPanel { Pass = passName, View = view.Name, Pixels = img });
                }
            }

            // Per-pass lists of what could not be shown, and the UV range of each slot.
            for (int i = 0; i < slots.Length; i++)
            {
                PassSlotInfo info = scene.Infos[i];
                string who = slots[i].Number + " " + info.Path + " [" + info.Slot + "] " + info.Material;
                if (output.Notes.ContainsKey("albedo") && !slots[i].Display.Has) output.Notes["albedo"].Add(new { slot = who, reason = "no readable texture: drawn as the material color" + (info.Notes.Count > 0 ? " (" + info.Notes[0] + ")" : "") });
                if (output.Notes.ContainsKey("alpha") && !slots[i].Display.Has) output.Notes["alpha"].Add(new { slot = who, reason = "no readable texture: alpha is the material color alpha " + slots[i].TintA });
                if (output.Notes.ContainsKey("texture") && !slots[i].Chosen.Has) output.Notes["texture"].Add(new { slot = who, reason = info.ChosenReason ?? "not asked" });
            }
            if (output.Notes.ContainsKey("uv"))
            {
                var outside = new int[slots.Length]; var total = new int[slots.Length];
                for (int t = 0; t < mesh.TriangleCount; t++)
                    for (int c = 0; c < 3; c++)
                    {
                        int v = mesh.Tris[3 * t + c];
                        total[mesh.TriSlot[t]]++;
                        float u = mesh.Uv[2 * v], w = mesh.Uv[2 * v + 1];
                        if (u < 0 || u > 1 || w < 0 || w > 1) outside[mesh.TriSlot[t]]++;
                    }
                for (int i = 0; i < slots.Length; i++)
                    if (outside[i] > 0) output.Notes["uv"].Add(new { slot = slots[i].Number + " " + scene.Infos[i].Path + " [" + scene.Infos[i].Slot + "]", outside01 = Math.Round(outside[i] / (double)total[i], 3), note = "share of triangle corners with a UV outside 0..1 (tiling or UDIM-like layout)" });
            }
            foreach (var name in output.Notes.Where(n => n.Value.Count == 0).Select(n => n.Key).ToList()) output.Notes.Remove(name);
            return output;
        }
    }
}
