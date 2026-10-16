using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;

namespace StudioRaming.WarudoAgentBridge
{
    // Where an image sits in the window: the crop (window pixels), the size of the PNG, and the rectangle of the PNG that shows the crop.
    // The MCP server keeps this next to an imageId, so a point read off the PNG can be clicked without any scale math.
    sealed class CaptureMapping
    {
        public int WindowWidth, WindowHeight;
        public int CropX, CropY, CropW, CropH;
        public int ImageW, ImageH;
        public int ViewX, ViewY, ViewW, ViewH;
        public bool Clickable = true;

        public double ImageX(double windowX) { return ViewX + (windowX - CropX) * ViewW / CropW; }
        public double ImageY(double windowY) { return ViewY + (windowY - CropY) * ViewH / CropH; }

        // The same mapping for a panel that is later pasted at (dx, dy) into a bigger image.
        public CaptureMapping Placed(int dx, int dy, int imageW, int imageH)
        {
            var copy = (CaptureMapping)MemberwiseClone();
            copy.ViewX += dx;
            copy.ViewY += dy;
            copy.ImageW = imageW;
            copy.ImageH = imageH;
            return copy;
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["window"] = new JArray(WindowWidth, WindowHeight),
                ["crop"] = new JArray(CropX, CropY, CropW, CropH),
                ["image"] = new JArray(ImageW, ImageH),
                ["viewport"] = new JArray(ViewX, ViewY, ViewW, ViewH),
                ["scale"] = Math.Round((double)ViewW / CropW, 5),
                ["clickable"] = Clickable,
            };
        }
    }

    // The options every image tool shares. Parse validates them; the plugin resolves region/around against the live window.
    sealed class CaptureOptions
    {
        public int MaxSize = 1600, GridStep = 100, Padding = 16;
        public bool Grid, MarkPointer = true, Before;
        public int[] Region;
        public JObject Around;

        public static CaptureOptions Parse(JObject p)
        {
            var o = new CaptureOptions();
            o.MaxSize = OptInt(p, "maxSize", 1600, 256, 4096);
            o.GridStep = OptInt(p, "gridStep", 100, 10, 1000);
            o.Padding = OptInt(p, "padding", 16, 0, 400);
            o.Grid = OptBool(p, "grid", false);
            o.MarkPointer = OptBool(p, "markPointer", true);
            o.Before = OptBool(p, "captureBefore", false);
            JToken region = p["region"], around = p["around"];
            bool hasRegion = region != null && region.Type != JTokenType.Null, hasAround = around != null && around.Type != JTokenType.Null;
            if (hasRegion && hasAround) throw new ArgumentException("INVALID_ARGUMENT: pass region or around, not both.");
            if (hasRegion) o.Region = ParseRegion(region, "region");
            if (hasAround)
            {
                o.Around = around as JObject ?? throw new ArgumentException("INVALID_ARGUMENT: around must be an object with objectId or path.");
                bool hasId = o.Around["objectId"] != null && o.Around["objectId"].Type != JTokenType.Null;
                bool hasPath = o.Around["path"] != null && o.Around["path"].Type != JTokenType.Null;
                if (hasId == hasPath) throw new ArgumentException("INVALID_ARGUMENT: around needs exactly one of objectId or path.");
                if (hasId && o.Around["objectId"].Type != JTokenType.Integer) throw new ArgumentException("INVALID_ARGUMENT: around.objectId must be an integer.");
                if (hasPath && (o.Around["path"].Type != JTokenType.String || ((string)o.Around["path"]).Length == 0)) throw new ArgumentException("INVALID_ARGUMENT: around.path must be a non-empty string.");
            }
            return o;
        }

        // [x, y, w, h] in window pixels, widened to whole pixels.
        public static int[] ParseRegion(JToken token, string name)
        {
            var array = token as JArray;
            if (array == null || array.Count != 4) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must be [x, y, width, height].");
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (array[i].Type != JTokenType.Integer && array[i].Type != JTokenType.Float) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must hold numbers.");
                v[i] = (double)array[i];
                if (double.IsNaN(v[i]) || double.IsInfinity(v[i]) || Math.Abs(v[i]) > 100000) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must hold finite numbers.");
            }
            if (v[2] <= 0 || v[3] <= 0) throw new ArgumentException("INVALID_ARGUMENT: " + name + " width and height must be positive.");
            int x0 = (int)Math.Floor(v[0]), y0 = (int)Math.Floor(v[1]);
            int x1 = (int)Math.Ceiling(v[0] + v[2]), y1 = (int)Math.Ceiling(v[1] + v[3]);
            return new[] { x0, y0, x1 - x0, y1 - y0 };
        }

        public static int OptInt(JObject p, string name, int fallback, int min, int max)
        {
            JToken token = p[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must be an integer " + min + ".." + max + ".");
            double value = (double)token;
            if (value != Math.Floor(value) || value < min || value > max) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must be an integer " + min + ".." + max + ".");
            return (int)value;
        }

        public static bool OptBool(JObject p, string name, bool fallback)
        {
            JToken token = p[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Boolean) throw new ArgumentException("INVALID_ARGUMENT: " + name + " must be true or false.");
            return (bool)token;
        }
    }

    // A frame read from the backbuffer, top-left origin.
    struct GrabbedFrame
    {
        public Pixmap Image;
        public int Frame;
        public double WaitMs, ReadMs;
    }

    // The part of the window an image shows, and what it was asked to be around.
    sealed class CropPlan
    {
        public int X, Y, W, H;
        public JObject Around;
        public JArray ToJson() { return new JArray(X, Y, W, H); }
    }

    // A full-resolution crop kept in memory so screen_diff can compare against a capture from an earlier call.
    sealed class RawFrame
    {
        public string EvidencePath;
        public Pixmap Raw;
        public int WindowW, WindowH, CropX, CropY;
        public float At;
        public long Bytes { get { return (long)Raw.Pixels.Length * 4; } }
    }

    public sealed partial class AgentBridgePlugin
    {
        const int RawFrameLimit = 8;
        const long RawFrameBudget = 96L * 1024 * 1024;
        const float RawFrameSeconds = 600f;
        const string CaptureApiName = "UnityEngine.Texture2D.ReadPixels(backbuffer) + EncodeToPNG";

        readonly List<RawFrame> rawFrames = new List<RawFrame>();

        static long Stamp() { return System.Diagnostics.Stopwatch.GetTimestamp(); }
        static double Ms(long since) { return Math.Round((Stamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1); }

        // screen.capture: the final frame (UI included), optionally cropped, fitted to maxSize and overlaid with a coordinate grid.
        async UniTask<object> CaptureScreen(JObject p)
        {
            OptionalSuperSize(p);
            int timeoutMs = OptionalTimeout(p);
            CaptureOptions options = CaptureOptions.Parse(p);
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");

            long started = Stamp();
            GrabbedFrame grabbed = await GrabFrame(timeoutMs);
            double grabMs = Ms(started);
            long processStart = Stamp();
            int width = grabbed.Image.Width, height = grabbed.Image.Height;
            CropPlan crop = ResolveCrop(options, width, height);
            Pixmap raw = grabbed.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
            CaptureMapping mapping = PanelMapping(width, height, crop, options.MaxSize, options.MaxSize);
            Pixmap image = raw.Downscale(mapping.ImageW, mapping.ImageH);
            if (options.Grid) DrawGrid(image, mapping, options.GridStep);
            double processMs = Ms(processStart);

            JObject result = SaveCapture(path, image, mapping, raw, grabbed, grabMs, processMs);
            result["crop"] = crop.ToJson();
            if (crop.Around != null) result["around"] = crop.Around;
            return result;
        }

        // Reads the final frame the way screen.capture always did: at the end of a frame, from a null active RenderTexture.
        async UniTask<GrabbedFrame> GrabFrame(int timeoutMs)
        {
            float started = Time.realtimeSinceStartup;
            using (var cancellation = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    Context runner = Context.Instance;
                    if (runner == null) throw new InvalidOperationException("SCREEN_CAPTURE_UNAVAILABLE: WARUDO frame runner is not available.");
                    await UniTask.WaitForEndOfFrame(runner, cancellation.Token);
                    float waited = Time.realtimeSinceStartup;
                    int width = Screen.width, height = Screen.height;
                    ValidateDimensions(width, height, 1);
                    RenderTexture previous = RenderTexture.active;
                    Texture2D texture = null;
                    try
                    {
                        RenderTexture.active = null;
                        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        Pixmap image = Pixmap.FromTexture(texture);
                        return new GrabbedFrame
                        {
                            Image = image,
                            Frame = Time.frameCount,
                            WaitMs = Math.Round((waited - started) * 1000f, 3),
                            ReadMs = Math.Round((Time.realtimeSinceStartup - waited) * 1000f, 3),
                        };
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        if (texture != null) UnityEngine.Object.Destroy(texture);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("SCREEN_CAPTURE_TIMEOUT: EndOfFrame or backbuffer readback exceeded the requested timeout.");
                }
            }
        }

        // The window area an image shows. region must lie inside the window; around is widened by padding and clipped to it.
        CropPlan ResolveCrop(CaptureOptions options, int width, int height)
        {
            if (options.Region != null)
            {
                int[] r = options.Region;
                if (r[0] < 0 || r[1] < 0 || r[0] + r[2] > width || r[1] + r[3] > height)
                    throw new ArgumentException("REGION_OUTSIDE_SCREEN: region [" + r[0] + ", " + r[1] + ", " + r[2] + ", " + r[3] + "] is not inside the " + width + "x" + height + " window.");
                return new CropPlan { X = r[0], Y = r[1], W = r[2], H = r[3] };
            }
            if (options.Around != null)
            {
                Rect rect;
                JObject info = ResolveAround(options.Around, width, height, out rect);
                int x0 = Math.Max(0, (int)Math.Floor(rect.xMin - options.Padding)), y0 = Math.Max(0, (int)Math.Floor(rect.yMin - options.Padding));
                int x1 = Math.Min(width, (int)Math.Ceiling(rect.xMax + options.Padding)), y1 = Math.Min(height, (int)Math.Ceiling(rect.yMax + options.Padding));
                if (x1 - x0 < 1 || y1 - y0 < 1) throw new InvalidOperationException("AROUND_OFFSCREEN: " + info["path"] + " is outside the window.");
                return new CropPlan { X = x0, Y = y0, W = x1 - x0, H = y1 - y0, Around = info };
            }
            return new CropPlan { X = 0, Y = 0, W = width, H = height };
        }

        // A UI object (its RectTransform on the screen) or a renderer / GameObject (its bounds projected by the main camera).
        JObject ResolveAround(JObject around, int width, int height, out Rect rect)
        {
            GameObject target;
            if (around["objectId"] != null && around["objectId"].Type != JTokenType.Null)
            {
                int id = (int)around["objectId"];
                target = FindUiObject(id) ?? SceneObjects().FirstOrDefault(go => go != null && go.GetInstanceID() == id);
                if (target == null) throw new ArgumentException("AROUND_NOT_FOUND: no object " + id + "; read it again with warudo_ui_list or warudo_hierarchy_list.");
            }
            else
            {
                string path = (string)around["path"];
                target = FindUiObjectByPath(path) ?? SceneObjects().FirstOrDefault(go => go != null && ObjectPath(go.transform) == path);
                if (target == null) throw new ArgumentException("AROUND_NOT_FOUND: no object at path \"" + path + "\".");
            }
            string kind;
            var rectTransform = target.transform as RectTransform;
            Canvas canvas = rectTransform == null ? null : target.GetComponentInParent<Canvas>();
            if (rectTransform != null && canvas != null)
            {
                kind = "ui";
                if (!TryUiScreenRect(rectTransform, UiEventCamera(canvas.rootCanvas), height, out rect))
                    throw new InvalidOperationException("AROUND_OFFSCREEN: " + ObjectPath(target.transform) + " is behind its camera.");
            }
            else
            {
                kind = "renderer";
                rect = ProjectedBounds(target, height);
            }
            return new JObject
            {
                ["objectId"] = target.GetInstanceID(),
                ["path"] = ObjectPath(target.transform),
                ["kind"] = kind,
                ["rect"] = new JArray(R(rect.x), R(rect.y), R(rect.width), R(rect.height)),
            };
        }

        GameObject FindUiObjectByPath(string path)
        {
            foreach (Canvas canvas in ActiveRootCanvases())
            {
                foreach (Transform child in canvas.GetComponentsInChildren<Transform>(false))
                    if (ObjectPath(child) == path) return child.gameObject;
            }
            return null;
        }

        static Camera CaptureCamera()
        {
            Camera main = Camera.main;
            if (main != null && main.enabled) return main;
            return Camera.allCameras.FirstOrDefault(camera => camera.enabled && camera.targetTexture == null);
        }

        // The screen rectangle (top-left window pixels) of every active renderer under the object, from the corners of their bounds.
        static Rect ProjectedBounds(GameObject target, int screenHeight)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0) throw new InvalidOperationException("AROUND_NO_BOUNDS: " + target.name + " has no active renderer or UI rectangle to frame.");
            Camera camera = CaptureCamera() ?? throw new InvalidOperationException("AROUND_NO_CAMERA: there is no enabled camera to project renderer bounds with.");
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            Vector3 c = bounds.center, e = bounds.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                var world = new Vector3(c.x + ((corner & 1) == 0 ? -e.x : e.x), c.y + ((corner & 2) == 0 ? -e.y : e.y), c.z + ((corner & 4) == 0 ? -e.z : e.z));
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f) continue;
                float y = screenHeight - screen.y;
                minX = Mathf.Min(minX, screen.x); maxX = Mathf.Max(maxX, screen.x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            if (minX > maxX) throw new InvalidOperationException("AROUND_OFFSCREEN: " + target.name + " is behind the camera.");
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // Fits the crop into bound x bound (never enlarges) and returns where it lands. The PNG is exactly the fitted crop.
        static CaptureMapping PanelMapping(int windowW, int windowH, CropPlan crop, int boundW, int boundH)
        {
            double scale = Math.Min(1.0, Math.Min((double)boundW / crop.W, (double)boundH / crop.H));
            int w = Math.Max(1, (int)Math.Round(crop.W * scale)), h = Math.Max(1, (int)Math.Round(crop.H * scale));
            return new CaptureMapping
            {
                WindowWidth = windowW, WindowHeight = windowH,
                CropX = crop.X, CropY = crop.Y, CropW = crop.W, CropH = crop.H,
                ImageW = w, ImageH = h, ViewX = 0, ViewY = 0, ViewW = w, ViewH = h,
            };
        }

        static int CaptureTextScale(int width, int height) { return Math.Max(width, height) >= 1100 ? 2 : 1; }

        // Thin two-tone lines every step window pixels at about 25 % opacity, labeled with WINDOW pixels at the top and left edges
        // however the image was cropped or scaled. Labels that would touch the previous one are skipped.
        static void DrawGrid(Pixmap image, CaptureMapping m, int step)
        {
            int textScale = CaptureTextScale(image.Width, image.Height);
            int textHeight = Pixmap.TextHeight(textScale);
            var dark = new Color32(0, 0, 0, 255);
            var light = new Color32(255, 255, 255, 255);
            var chip = new Color32(24, 24, 24, 255);
            var ink = new Color32(255, 235, 90, 255);
            int fromX = (m.CropX + step - 1) / step * step;
            int nextFreeX = -1;
            for (int wx = fromX; wx < m.CropX + m.CropW; wx += step)
            {
                int ix = (int)Math.Round(m.ImageX(wx));
                image.FillRect(ix, m.ViewY, 1, m.ViewH, light, 64);
                image.FillRect(ix + 1, m.ViewY, 1, m.ViewH, dark, 64);
                string label = wx.ToString();
                if (ix >= nextFreeX)
                {
                    image.DrawText(ix + 2 + textScale * 2, m.ViewY + 2 + textScale * 2, label, textScale, ink, chip);
                    nextFreeX = ix + Pixmap.TextWidth(label, textScale) + textScale * 6 + 6;
                }
            }
            int fromY = (m.CropY + step - 1) / step * step;
            int nextFreeY = m.ViewY + textHeight + textScale * 6;
            for (int wy = fromY; wy < m.CropY + m.CropH; wy += step)
            {
                int iy = (int)Math.Round(m.ImageY(wy));
                image.FillRect(m.ViewX, iy, m.ViewW, 1, light, 64);
                image.FillRect(m.ViewX, iy + 1, m.ViewW, 1, dark, 64);
                string label = wy.ToString();
                if (iy >= nextFreeY)
                {
                    image.DrawText(m.ViewX + 2 + textScale * 2, iy + 2 + textScale * 2, label, textScale, ink, chip);
                    nextFreeY = iy + textHeight + textScale * 6 + 6;
                }
            }
        }

        // Writes the PNG into the evidence folder (one per request), remembers the raw crop for screen_diff, and builds the reply.
        JObject SaveCapture(string path, Pixmap image, CaptureMapping mapping, Pixmap raw, GrabbedFrame grabbed, double grabMs, double processMs)
        {
            long encodeStart = Stamp();
            byte[] png = image.EncodePng();
            double encodeMs = Ms(encodeStart);
            if (png == null || png.Length == 0 || png.Length > ScreenCaptureMaxPngBytes)
                throw new InvalidOperationException("SCREEN_CAPTURE_TOO_LARGE: encoded PNG is empty or exceeds the 64 MiB guard.");
            WriteEvidencePng(path, png);
            if (!HasCompletePng(path))
            {
                TryDelete(path);
                throw new InvalidOperationException("SCREEN_CAPTURE_FAILED: PNG signature or IEND chunk is incomplete.");
            }
            if (raw != null) RememberRaw(path, raw, mapping.WindowWidth, mapping.WindowHeight, mapping.CropX, mapping.CropY);
            return new JObject
            {
                ["path"] = path,
                ["width"] = mapping.WindowWidth,
                ["height"] = mapping.WindowHeight,
                ["outputWidth"] = image.Width,
                ["outputHeight"] = image.Height,
                ["superSize"] = 1,
                ["bytes"] = png.Length,
                ["frame"] = grabbed.Frame,
                ["frameWaited"] = true,
                ["frameWaitMs"] = grabbed.WaitMs,
                ["captureApi"] = CaptureApiName,
                ["includesScreenSpaceOverlay"] = true,
                ["cameraChanged"] = false,
                ["sceneChanged"] = false,
                ["managedTextureAllocated"] = true,
                ["managedTextureDestroyed"] = true,
                ["mapping"] = mapping.ToJson(),
                ["timingMs"] = new JObject { ["grab"] = grabMs, ["process"] = processMs, ["encode"] = encodeMs, ["total"] = Math.Round(grabMs + processMs + encodeMs, 1) },
            };
        }

        void RememberRaw(string evidencePath, Pixmap raw, int windowW, int windowH, int cropX, int cropY)
        {
            float now = Time.realtimeSinceStartup;
            rawFrames.RemoveAll(frame => now - frame.At > RawFrameSeconds || string.Equals(frame.EvidencePath, evidencePath, StringComparison.OrdinalIgnoreCase));
            rawFrames.Add(new RawFrame { EvidencePath = evidencePath, Raw = raw, WindowW = windowW, WindowH = windowH, CropX = cropX, CropY = cropY, At = now });
            while (rawFrames.Count > 1 && (rawFrames.Count > RawFrameLimit || rawFrames.Sum(frame => frame.Bytes) > RawFrameBudget)) rawFrames.RemoveAt(0);
        }

        RawFrame FindRaw(string evidencePath)
        {
            float now = Time.realtimeSinceStartup;
            rawFrames.RemoveAll(frame => now - frame.At > RawFrameSeconds);
            return rawFrames.FirstOrDefault(frame => string.Equals(frame.EvidencePath, evidencePath, StringComparison.OrdinalIgnoreCase));
        }
    }
}
