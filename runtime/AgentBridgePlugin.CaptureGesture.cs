using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // Where the virtual pointer pressed, released and moved during a gesture, in window pixels. Drawn on the capture so the
    // reader sees what was clicked without guessing.
    sealed class GestureMarks
    {
        public Vector2? Press, Release, Hover;
        public readonly List<Vector2> Path = new List<Vector2>();
        public string HoverLabel = "HOVER";

        public static GestureMarks From(PointerFrame[] rows, int pressRow, int releaseRow, string gesture)
        {
            var marks = new GestureMarks();
            if (pressRow >= 0 && rows[pressRow].HasPosition) marks.Press = new Vector2(rows[pressRow].X, rows[pressRow].Y);
            if (releaseRow >= 0 && rows[releaseRow].HasPosition) marks.Release = new Vector2(rows[releaseRow].X, rows[releaseRow].Y);
            if (pressRow >= 0)
            {
                int end = releaseRow >= pressRow ? releaseRow : rows.Length - 1;
                for (int i = pressRow; i <= end; i++)
                {
                    if (!rows[i].HasPosition) continue;
                    var point = new Vector2(rows[i].X, rows[i].Y);
                    if (marks.Path.Count == 0 || (marks.Path[marks.Path.Count - 1] - point).sqrMagnitude > 0.25f) marks.Path.Add(point);
                }
            }
            else
            {
                for (int i = rows.Length - 1; i >= 0; i--)
                {
                    if (!rows[i].HasPosition) continue;
                    marks.Hover = new Vector2(rows[i].X, rows[i].Y);
                    break;
                }
                if (gesture == "wheel") marks.HoverLabel = "WHEEL";
            }
            return marks;
        }

        public JObject ToJson()
        {
            var json = new JObject();
            if (Press.HasValue) json["press"] = new JArray(Math.Round(Press.Value.x, 1), Math.Round(Press.Value.y, 1));
            if (Release.HasValue) json["release"] = new JArray(Math.Round(Release.Value.x, 1), Math.Round(Release.Value.y, 1));
            if (Hover.HasValue) json["hover"] = new JArray(Math.Round(Hover.Value.x, 1), Math.Round(Hover.Value.y, 1));
            if (Path.Count > 2) json["pathPoints"] = Path.Count;
            return json;
        }

        // Drag path in yellow, press crosshair in red, release crosshair in green, a hover crosshair in cyan. A click (press and
        // release on the same pixel) is one red crosshair labeled CLICK.
        public void Draw(Pixmap image, CaptureMapping m)
        {
            int ts = CaptureTextScaleFor(image);
            int radius = 8 * ts + 4;
            var black = new Color32(0, 0, 0, 255);
            var yellow = new Color32(255, 230, 0, 255);
            var red = new Color32(255, 40, 40, 255);
            var green = new Color32(60, 255, 90, 255);
            var cyan = new Color32(0, 230, 255, 255);
            Func<Vector2, Vector2> toImage = v => new Vector2((float)m.ImageX(v.x), (float)m.ImageY(v.y));
            if (Path.Count >= 2)
            {
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 1; i < Path.Count; i++)
                    {
                        Vector2 a = toImage(Path[i - 1]), b = toImage(Path[i]);
                        image.Line((int)Math.Round(a.x), (int)Math.Round(a.y), (int)Math.Round(b.x), (int)Math.Round(b.y), pass == 0 ? 3 * ts + 2 : 2 * ts, pass == 0 ? black : yellow);
                    }
            }
            bool sameSpot = Press.HasValue && Release.HasValue && (toImage(Press.Value) - toImage(Release.Value)).magnitude < radius;
            if (Release.HasValue && !sameSpot) Mark(image, toImage(Release.Value), radius, green, "UP", ts);
            if (Press.HasValue) Mark(image, toImage(Press.Value), radius, red, sameSpot ? "CLICK" : "DOWN", ts);
            if (Hover.HasValue) Mark(image, toImage(Hover.Value), radius, cyan, HoverLabel, ts);
        }

        static void Mark(Pixmap image, Vector2 at, int radius, Color32 color, string label, int ts)
        {
            int x = (int)Math.Round(at.x), y = (int)Math.Round(at.y);
            image.Cross(x, y, radius, color);
            int labelX = x + radius + 4 * ts, labelY = y - radius;
            if (labelX + Pixmap.TextWidth(label, ts) + 4 * ts > image.Width) labelX = x - radius - 4 * ts - Pixmap.TextWidth(label, ts);
            image.DrawText(labelX, Math.Max(2 * ts, labelY), label, ts, new Color32(0, 0, 0, 255), color);
        }

        static int CaptureTextScaleFor(Pixmap image) { return Math.Max(image.Width, image.Height) >= 1100 ? 2 : 1; }
    }

    public sealed partial class AgentBridgePlugin
    {
        const int BeforeAfterGap = 8;

        // The capture of a gesture: the final frame with the pointer marked, or, with captureBefore, the frame from before the
        // gesture next to it (BEFORE / AFTER, one PNG) and what changed between them.
        async UniTask<object> CaptureGesture(JObject p, CaptureOptions options, GestureMarks marks, GrabbedFrame? before)
        {
            int timeoutMs = OptionalTimeout(p);
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");
            long started = Stamp();
            GrabbedFrame after = await GrabFrame(timeoutMs);
            double grabMs = Ms(started);
            long processStart = Stamp();
            int width = after.Image.Width, height = after.Image.Height;
            CropPlan crop = ResolveCrop(options, width, height);
            Pixmap raw = after.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
            bool compare = before.HasValue && before.Value.Image.Width == width && before.Value.Image.Height == height;

            Pixmap image;
            CaptureMapping mapping;
            DiffResult diff = null;
            if (!compare)
            {
                mapping = PanelMapping(width, height, crop, options.MaxSize, options.MaxSize);
                image = raw.Downscale(mapping.ImageW, mapping.ImageH);
                if (options.Grid) DrawGrid(image, mapping, options.GridStep);
                if (options.MarkPointer) marks.Draw(image, mapping);
            }
            else
            {
                Pixmap beforeRaw = before.Value.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
                diff = PixelDiff.Compute(beforeRaw, raw, 6, 8, DiffMaxBoxes);
                // Two panels side by side under a header strip; maxSize bounds the longest side of the whole sheet.
                int panelBoundW = Math.Max(16, (options.MaxSize - BeforeAfterGap) / 2);
                CaptureMapping panel = PanelMapping(width, height, crop, panelBoundW, options.MaxSize);
                int ts = CaptureTextScale(panel.ImageW * 2, panel.ImageH);
                int header = Pixmap.TextHeight(ts) + ts * 8;
                if (panel.ImageH + header > options.MaxSize) panel = PanelMapping(width, height, crop, panelBoundW, Math.Max(16, options.MaxSize - header));
                Pixmap beforePanel = beforeRaw.Downscale(panel.ImageW, panel.ImageH);
                Pixmap afterPanel = raw.Downscale(panel.ImageW, panel.ImageH);
                if (options.Grid) { DrawGrid(beforePanel, panel, options.GridStep); DrawGrid(afterPanel, panel, options.GridStep); }
                DrawDiffBoxes(afterPanel, panel, diff);
                if (options.MarkPointer) marks.Draw(afterPanel, panel);
                int sheetW = panel.ImageW * 2 + BeforeAfterGap, sheetH = panel.ImageH + header;
                image = new Pixmap(sheetW, sheetH);
                image.Fill(new Color32(24, 24, 24, 255));
                image.Blit(beforePanel, 0, header);
                image.Blit(afterPanel, panel.ImageW + BeforeAfterGap, header);
                var ink = new Color32(255, 255, 255, 255);
                var chip = new Color32(24, 24, 24, 255);
                image.DrawText(ts * 3, ts * 3, "BEFORE", ts, ink, chip);
                string summary = "AFTER   " + Math.Round(diff.ChangedFraction * 100.0, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "% CHANGED   " + diff.BoxesTotal + (diff.BoxesTotal == 1 ? " BOX" : " BOXES");
                image.DrawText(panel.ImageW + BeforeAfterGap + ts * 3, ts * 3, summary, ts, ink, chip);
                mapping = panel.Placed(panel.ImageW + BeforeAfterGap, header, sheetW, sheetH);
            }
            double processMs = Ms(processStart);

            JObject result = SaveCapture(path, image, mapping, raw, after, grabMs, processMs);
            result["crop"] = crop.ToJson();
            if (crop.Around != null) result["around"] = crop.Around;
            result["markedPointer"] = options.MarkPointer;
            result["pointerMarks"] = marks.ToJson();
            if (options.Before)
            {
                result["before"] = compare;
                if (!compare) result["beforeSkipped"] = "the window changed size during the gesture";
            }
            if (diff != null) result["diff"] = DiffJson(diff, mapping, 6, 8);
            return result;
        }
    }
}
