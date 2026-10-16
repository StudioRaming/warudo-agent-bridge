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
using Warudo.Plugins.Core.Assets.Cinematography;

namespace StudioRaming.WarudoAgentBridge
{
    // render.isolate: the real look (real shaders, lights, post-processing) of chosen renderers. A temporary camera
    // copies the main camera and sits at its pose; the chosen renderers are moved to one unused layer for a few
    // synchronous renders and put back in the same call (LayerIsolation restores in Dispose, and there is no await
    // while they are moved). The main camera and the scene are not changed.
    public sealed partial class AgentBridgePlugin
    {
        const int IsolateMaxParts = 16;
        const int IsolateStatsSide = 480;
        static readonly Color32 IsolateMagenta = new Color32(255, 0, 255, 255);

        sealed class IsolatePart
        {
            public Renderer Renderer;
            public int Pixels, Visible, StatsWidth, StatsHeight;
            public float X0, Y0, X1, Y1;
            public double R, G, B;
        }

        sealed class IsolateRaw
        {
            public int Width, Height;
            public Color32[] Full, Mask, Without, Only; // bottom-up, as ReadPixels returns them
            public List<IsolatePart> Parts = new List<IsolatePart>();
            public List<string> Notes = new List<string>();
        }

        static int IsolateDiff(Color32 a, Color32 b)
        {
            return Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        }

        List<Renderer> IsolateChoose(JObject p, List<string> notes, out string basis)
        {
            var chosen = new List<Renderer>();
            var basisParts = new List<string>();
            JArray ids = p["objectIds"] as JArray;
            string[] include = (p["include"] as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrEmpty(v)).ToArray() ?? new string[0];
            if ((ids == null || ids.Count == 0) && include.Length == 0) throw new ArgumentException("Choose parts with objectIds (from warudo_hierarchy_list) or include (path substrings under the asset).");
            if (ids != null && ids.Count > 0)
            {
                basisParts.Add("objectIds");
                var objects = SceneObjects().ToDictionary(g => g.GetInstanceID(), g => g);
                foreach (JToken token in ids)
                {
                    GameObject go;
                    if (!objects.TryGetValue((int)token, out go)) { notes.Add("objectId " + (int)token + " no longer exists"); continue; }
                    int before = chosen.Count;
                    foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(false))
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy && !chosen.Contains(renderer)) chosen.Add(renderer);
                    if (chosen.Count == before) notes.Add("objectId " + (int)token + " (" + ObjectPath(go.transform) + ") has no active, enabled renderer");
                }
            }
            if (include.Length > 0)
            {
                basisParts.Add("include");
                object[] others;
                GameObjectAsset target = PickTarget(p, out others);
                if (others.Length > 0) notes.Add("include was resolved on " + target.Name + "; other Characters exist (pass assetId to choose): " + string.Join(", ", others.Select(o => o.GetType().GetProperty("name").GetValue(o))));
                if (target.GameObject == null) throw new InvalidOperationException("ASSET_NOT_LOADED: " + target.Name + " has no GameObject yet.");
                Transform root = target.GameObject.transform;
                foreach (Renderer renderer in target.GameObject.GetComponentsInChildren<Renderer>(false))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || chosen.Contains(renderer)) continue;
                    string path = RelativePath(root, renderer.transform);
                    if (include.Any(x => path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) chosen.Add(renderer);
                }
                foreach (string x in include)
                    if (!chosen.Any(r => RelativePath(root, r.transform).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) notes.Add("include '" + x + "' matched no active renderer of " + target.Name);
            }
            basis = string.Join(" + ", basisParts);
            return chosen;
        }

        async UniTask<object> RenderIsolate(JObject p)
        {
            float started = Time.realtimeSinceStartup;
            string mode = ((string)p["mode"] ?? "highlight").ToLowerInvariant();
            if (mode != "only" && mode != "highlight") throw new ArgumentException("mode must be only or highlight.");
            string background = ((string)p["background"] ?? "neutral").ToLowerInvariant();
            if (background != "neutral" && background != "checker" && background != "transparent") throw new ArgumentException("background must be neutral, checker or transparent.");
            int size = (int?)p["size"] ?? 1280;
            if (size < 256 || size > 2048) throw new ArgumentException("size must be 256..2048 pixels (longest side).");
            int maxSize = (int?)p["maxSize"] ?? 1600;
            if (maxSize < 256 || maxSize > 4096) throw new ArgumentException("maxSize must be 256..4096.");

            var notes = new List<string>();
            string basis;
            List<Renderer> chosen = IsolateChoose(p, notes, out basis);
            if (chosen.Count == 0) throw new InvalidOperationException("NOTHING_CHOSEN: no active renderer matches. " + string.Join("; ", notes));
            if (chosen.Count > IsolateMaxParts) throw new ArgumentException("The selection has " + chosen.Count + " renderers; the limit is " + IsolateMaxParts + ". Narrow it with include or objectIds.");

            CameraAsset cameraAsset = Context.PluginManager.GetPlugin<CorePlugin>().MainCameraAsset as CameraAsset;
            Camera main = cameraAsset == null ? null : cameraAsset.Camera;
            if (main == null) throw new InvalidOperationException("CAMERA_UNAVAILABLE: the main Camera asset has no live Camera, so there is nothing to copy the rendering from.");
            float aspect = main.aspect > 0.4f && main.aspect < 4f ? main.aspect : 16f / 9f;
            int width = aspect >= 1f ? size : Math.Max(64, (int)Math.Round(size * aspect));
            int height = aspect >= 1f ? Math.Max(64, (int)Math.Round(size / aspect)) : size;

            // Renders: synchronous, no await until every layer is back.
            IsolateRaw raw = IsolateRender(main, chosen, mode, background, width, height, aspect, notes);
            float afterRender = Time.realtimeSinceStartup;

            int outWidth = width, outHeight = height;
            if (Math.Max(width, height) > maxSize) { double k = maxSize / (double)Math.Max(width, height); outWidth = Math.Max(1, (int)Math.Round(width * k)); outHeight = Math.Max(1, (int)Math.Round(height * k)); }
            string path = Path.Combine(root, "evidence", activeRequest + ".png");
            int visibleUnion = 0;
            byte[] png = await UniTask.RunOnThreadPool(() =>
            {
                Color32[] image = mode == "highlight" ? IsolateHighlight(raw, out visibleUnion) : IsolateOnly(raw, background);
                if (outWidth != width || outHeight != height) image = RenderRaster.Downscale(image, width, height, outWidth, outHeight);
                return ImageConversion.EncodeArrayToPNG(image, GraphicsFormat.R8G8B8A8_UNorm, (uint)outWidth, (uint)outHeight);
            });
            File.WriteAllBytes(path, png);

            var parts = raw.Parts.Select(part =>
            {
                string hex = null; int[] color = null;
                if (part.Visible > 0)
                {
                    color = new[] { (int)Math.Round(part.R / part.Visible), (int)Math.Round(part.G / part.Visible), (int)Math.Round(part.B / part.Visible) };
                    hex = "#" + color[0].ToString("x2") + color[1].ToString("x2") + color[2].ToString("x2");
                }
                return new
                {
                    path = ObjectPath(part.Renderer.transform), renderer = part.Renderer.GetType().Name,
                    materials = part.Renderer.sharedMaterials.Select(m => m == null ? null : m.name).ToArray(),
                    seenFromCamera = part.Visible > 0,
                    visibleShareOfPart = part.Pixels == 0 ? (double?)null : Math.Round(part.Visible / (double)part.Pixels, 3),
                    visibleShareOfFrame = Math.Round(part.Visible / (double)(part.StatsWidth * part.StatsHeight), 5),
                    screenBounds = part.Visible == 0 ? null : new { x0 = Math.Round(part.X0, 4), y0 = Math.Round(part.Y0, 4), x1 = Math.Round(part.X1, 4), y1 = Math.Round(part.Y1, 4) },
                    meanVisibleColorSrgb = color, hex,
                };
            }).ToArray();

            Transform ct = main.transform;
            return new
            {
                path, width = outWidth, height = outHeight, mode, background = mode == "only" ? background : null, chosenBy = basis, parts,
                camera = new { note = "the main camera's pose and rendering, copied to a temporary camera", position = Wire(ct.position), rotation = Wire(ct.eulerAngles), fieldOfView = Math.Round(main.fieldOfView, 2), orthographic = main.orthographic, aspect = Math.Round(aspect, 3) },
                highlightedFrameShare = mode == "highlight" ? Math.Round(visibleUnion / (double)(width * height), 5) : (double?)null,
                notes = raw.Notes,
                legend = "screenBounds are 0..1 of the frame, origin top-left; meanVisibleColorSrgb is the average final color (lit, post-processed, 0..255) of the pixels where the part is not hidden. Stats are measured at " + IsolateStatsSide + " px on the long side.",
                ms = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f), msRender = Mathf.RoundToInt((afterRender - started) * 1000f),
                hint = "Nothing in WARUDO changed: the chosen renderers' layers were restored in the same call."
            };
        }

        IsolateRaw IsolateRender(Camera main, List<Renderer> chosen, string mode, string background, int width, int height, float aspect, List<string> notes)
        {
            var raw = new IsolateRaw { Width = width, Height = height };
            raw.Notes.AddRange(notes);
            int statsWidth = aspect >= 1f ? IsolateStatsSide : Math.Max(32, (int)Math.Round(IsolateStatsSide * aspect));
            int statsHeight = aspect >= 1f ? Math.Max(32, (int)Math.Round(IsolateStatsSide / aspect)) : IsolateStatsSide;
            using (var camera = new OffscreenCamera(main))
            using (LayerIsolation isolation = LayerIsolation.Create(new Renderer[0]))
            {
                raw.Notes.AddRange(camera.Notes);
                if (isolation == null) throw new InvalidOperationException("NO_FREE_LAYER: isolating parts needs one unused Unity layer and every unnamed layer is taken.");
                camera.Place(main.transform.position, main.transform.rotation, main.fieldOfView);
                int sceneMask = camera.CullingMask, layerMask = 1 << isolation.Layer;
                Color32[] statsFull = camera.Render(statsWidth, statsHeight, sceneMask, camera.ClearFlags, camera.Background, true, false);
                if (mode == "highlight") raw.Full = camera.Render(width, height, sceneMask, camera.ClearFlags, camera.Background, true, false);
                try
                {
                    foreach (Renderer renderer in chosen) isolation.Move(renderer);
                    if (mode == "highlight")
                    {
                        raw.Mask = camera.Render(width, height, layerMask, CameraClearFlags.SolidColor, IsolateMagenta, false, false);
                        raw.Without = camera.Render(width, height, sceneMask & ~layerMask, camera.ClearFlags, camera.Background, true, false);
                    }
                    else if (background == "neutral") raw.Only = camera.Render(width, height, layerMask, CameraClearFlags.SolidColor, new Color(0.78f, 0.79f, 0.82f, 1f), true, false);
                    else raw.Only = camera.Render(width, height, layerMask, CameraClearFlags.SolidColor, new Color(0, 0, 0, 0), false, true);
                    foreach (Renderer renderer in chosen) isolation.Return(renderer);

                    // One part at a time: where is it, and how much of it is not hidden by anything.
                    foreach (Renderer renderer in chosen)
                    {
                        var part = new IsolatePart { Renderer = renderer, StatsWidth = statsWidth, StatsHeight = statsHeight, X0 = 1, Y0 = 1, X1 = 0, Y1 = 0 };
                        raw.Parts.Add(part);
                        isolation.Move(renderer);
                        Color32[] alone = camera.Render(statsWidth, statsHeight, layerMask, CameraClearFlags.SolidColor, IsolateMagenta, false, false);
                        Color32[] without = camera.Render(statsWidth, statsHeight, sceneMask & ~layerMask, camera.ClearFlags, camera.Background, true, false);
                        isolation.Return(renderer);
                        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                        for (int y = 0; y < statsHeight; y++)
                            for (int x = 0; x < statsWidth; x++)
                            {
                                int i = y * statsWidth + x;
                                if (IsolateDiff(alone[i], IsolateMagenta) <= 24) continue;
                                part.Pixels++;
                                if (IsolateDiff(statsFull[i], without[i]) <= 12) continue;
                                part.Visible++;
                                part.R += statsFull[i].r; part.G += statsFull[i].g; part.B += statsFull[i].b;
                                if (x < minX) minX = x; if (x > maxX) maxX = x;
                                if (y < minY) minY = y; if (y > maxY) maxY = y;
                            }
                        if (part.Visible > 0)
                        {
                            part.X0 = minX / (float)statsWidth; part.X1 = (maxX + 1) / (float)statsWidth;
                            part.Y0 = (statsHeight - 1 - maxY) / (float)statsHeight; part.Y1 = (statsHeight - minY) / (float)statsHeight; // rows are bottom-up, bounds are top-down
                        }
                    }
                }
                finally
                {
                    foreach (Renderer renderer in chosen) isolation.Return(renderer);
                }
            }
            if (chosen.Select(r => r.gameObject).Distinct().Count() < chosen.Count) raw.Notes.Add("Several renderers share one GameObject: they are shown and measured together.");
            return raw;
        }

        // Everything but the chosen parts desaturated and dark; the parts as the camera sees them, with an outline.
        static Color32[] IsolateHighlight(IsolateRaw raw, out int visibleCount)
        {
            int w = raw.Width, h = raw.Height;
            var visible = new bool[w * h];
            visibleCount = 0;
            for (int i = 0; i < visible.Length; i++)
                if (IsolateDiff(raw.Mask[i], IsolateMagenta) > 24 && IsolateDiff(raw.Full[i], raw.Without[i]) > 12) { visible[i] = true; visibleCount++; }
            var image = new Color32[w * h];
            for (int i = 0; i < image.Length; i++)
            {
                Color32 c = raw.Full[i];
                if (visible[i]) { image[i] = new Color32(c.r, c.g, c.b, 255); continue; }
                float grey = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                float r = (c.r + (grey - c.r) * 0.85f) * 0.35f, g = (c.g + (grey - c.g) * 0.85f) * 0.35f, b = (c.b + (grey - c.b) * 0.85f) * 0.35f;
                image[i] = new Color32((byte)r, (byte)g, (byte)b, 255);
            }
            int ring = Math.Max(2, Math.Max(w, h) / 640);
            var outline = new Color32(255, 214, 0, 255);
            var ringMask = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (visible[y * w + x]) continue;
                    bool near = false;
                    for (int dy = -ring; dy <= ring && !near; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            if (visible[yy * w + xx]) { near = true; break; }
                        }
                    }
                    ringMask[y * w + x] = near;
                }
            for (int i = 0; i < image.Length; i++) if (ringMask[i]) image[i] = outline;
            return image;
        }

        static Color32[] IsolateOnly(IsolateRaw raw, string background)
        {
            if (background == "neutral") return raw.Only;
            int w = raw.Width, h = raw.Height;
            if (background == "transparent") return raw.Only;
            var image = new Color32[w * h];
            int cell = Math.Max(8, Math.Max(w, h) / 80);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float shade = (((x / cell) + (y / cell)) & 1) == 0 ? 200f : 150f;
                    Color32 c = raw.Only[i];
                    float a = c.a / 255f;
                    image[i] = new Color32((byte)(c.r * a + shade * (1 - a)), (byte)(c.g * a + shade * (1 - a)), (byte)(c.b * a + shade * (1 - a)), 255);
                }
            return image;
        }
    }
}
