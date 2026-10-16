using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // screen.diff, screen.probe and screen.sequence: the capture tools that answer "what changed", "what color is it" and
    // "what happens over time" with numbers next to an image.
    public sealed partial class AgentBridgePlugin
    {
        const int DiffMaxBoxes = 32;
        const int SequenceMaxSpan = 1200;
        const double SequenceMaxSeconds = 45.0;
        const int SequenceGap = 4;

        // A baseline must be a PNG inside the bridge's own evidence folder, nothing else.
        string ConfineEvidencePath(string candidate)
        {
            string evidence = Path.GetFullPath(Path.Combine(root, "evidence"));
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception) { throw new ArgumentException("EVIDENCE_PATH_REJECTED: baseline is not a valid path."); }
            string prefix = evidence.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(full).ToLowerInvariant() != ".png")
                throw new ArgumentException("EVIDENCE_PATH_REJECTED: baseline must be a PNG returned by a capture tool, inside runtime/evidence.");
            return full;
        }

        // Boxes of a diff on a finished image: yellow outline and a number tag on top of each, in window coordinates.
        static void DrawDiffBoxes(Pixmap image, CaptureMapping m, DiffResult diff)
        {
            int textScale = CaptureTextScale(image.Width, image.Height);
            var yellow = new Color32(255, 230, 0, 255);
            var ink = new Color32(0, 0, 0, 255);
            for (int i = 0; i < diff.Boxes.Count; i++)
            {
                DiffBox box = diff.Boxes[i];
                int x0 = (int)Math.Floor(m.ImageX(m.CropX + box.X)) - 2, y0 = (int)Math.Floor(m.ImageY(m.CropY + box.Y)) - 2;
                int x1 = (int)Math.Ceiling(m.ImageX(m.CropX + box.X + box.W)) + 2, y1 = (int)Math.Ceiling(m.ImageY(m.CropY + box.Y + box.H)) + 2;
                x0 = Math.Max(m.ViewX, x0); y0 = Math.Max(m.ViewY, y0);
                x1 = Math.Min(m.ViewX + m.ViewW, x1); y1 = Math.Min(m.ViewY + m.ViewH, y1);
                image.Outline(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0), Math.Max(2, textScale), yellow);
                string label = (i + 1).ToString();
                int tagWidth = Pixmap.TextWidth(label, textScale) + textScale * 4, tagHeight = Pixmap.TextHeight(textScale) + textScale * 4;
                int tagY = y0 - tagHeight < m.ViewY ? y0 : y0 - tagHeight;
                image.DrawText(x0 + textScale * 2, tagY + textScale * 2, label, textScale, ink, yellow);
            }
        }

        static JObject DiffJson(DiffResult diff, CaptureMapping m, int threshold, int mergeGap)
        {
            var boxes = new JArray();
            for (int i = 0; i < diff.Boxes.Count; i++)
            {
                DiffBox box = diff.Boxes[i];
                boxes.Add(new JObject
                {
                    ["n"] = i + 1,
                    ["rect"] = new JArray(m.CropX + box.X, m.CropY + box.Y, box.W, box.H),
                    ["changedPixels"] = box.Changed,
                    ["meanBefore"] = PixelDiff.Rgb(box.Before),
                    ["meanAfter"] = PixelDiff.Rgb(box.After),
                });
            }
            return new JObject
            {
                ["changedPixels"] = diff.ChangedPixels,
                ["changedFraction"] = Math.Round(diff.ChangedFraction, 5),
                ["threshold"] = threshold,
                ["mergeGap"] = mergeGap,
                ["boxesTotal"] = diff.BoxesTotal,
                ["boxes"] = boxes,
                ["omitted"] = new JObject { ["boxes"] = diff.BoxesTotal - diff.Boxes.Count, ["changedPixels"] = diff.OmittedChanged },
            };
        }

        // screen.diff: now against an earlier capture of this runtime session. The baseline is the raw crop kept in memory for the
        // evidence path any capture tool returned; the area compared is that capture's crop unless region/around narrows it.
        async UniTask<object> ScreenDiff(JObject p)
        {
            int timeoutMs = OptionalTimeout(p);
            CaptureOptions options = CaptureOptions.Parse(p);
            string baselineArgument = (string)p["baseline"];
            if (string.IsNullOrWhiteSpace(baselineArgument)) throw new ArgumentException("INVALID_ARGUMENT: baseline must be the path of a PNG returned by a capture tool.");
            int threshold = CaptureOptions.OptInt(p, "threshold", 6, 0, 255);
            int mergeGap = CaptureOptions.OptInt(p, "mergeGap", 8, 0, 64);
            string baselinePath = ConfineEvidencePath(baselineArgument);
            RawFrame baseline = FindRaw(baselinePath) ?? throw new InvalidOperationException(
                "BASELINE_NOT_AVAILABLE: no raw frame is kept for " + Path.GetFileName(baselinePath) + ". The runtime keeps the last " + RawFrameLimit + " captures for " + (int)(RawFrameSeconds / 60) + " minutes and forgets them when the bridge reloads; take a new baseline capture.");
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");

            long started = Stamp();
            GrabbedFrame grabbed = await GrabFrame(timeoutMs);
            double grabMs = Ms(started);
            long processStart = Stamp();
            int width = grabbed.Image.Width, height = grabbed.Image.Height;
            if (width != baseline.WindowW || height != baseline.WindowH)
                throw new InvalidOperationException("BASELINE_SIZE_MISMATCH: the baseline was taken at " + baseline.WindowW + "x" + baseline.WindowH + " and the window is now " + width + "x" + height + ".");
            CropPlan crop = options.Region == null && options.Around == null
                ? new CropPlan { X = baseline.CropX, Y = baseline.CropY, W = baseline.Raw.Width, H = baseline.Raw.Height }
                : ResolveCrop(options, width, height);
            if (crop.X < baseline.CropX || crop.Y < baseline.CropY || crop.X + crop.W > baseline.CropX + baseline.Raw.Width || crop.Y + crop.H > baseline.CropY + baseline.Raw.Height)
                throw new ArgumentException("BASELINE_REGION_MISMATCH: the baseline covers [" + baseline.CropX + ", " + baseline.CropY + ", " + baseline.Raw.Width + ", " + baseline.Raw.Height + "] only; the compared area must lie inside it.");

            Pixmap now = grabbed.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
            Pixmap then = baseline.Raw.Crop(crop.X - baseline.CropX, crop.Y - baseline.CropY, crop.W, crop.H);
            DiffResult diff = PixelDiff.Compute(then, now, threshold, mergeGap, DiffMaxBoxes);
            CaptureMapping mapping = PanelMapping(width, height, crop, options.MaxSize, options.MaxSize);
            Pixmap image = PixelDiff.Render(now, diff).Downscale(mapping.ImageW, mapping.ImageH);
            if (options.Grid) DrawGrid(image, mapping, options.GridStep);
            DrawDiffBoxes(image, mapping, diff);
            double processMs = Ms(processStart);

            JObject result = SaveCapture(path, image, mapping, now, grabbed, grabMs, processMs);
            result["crop"] = crop.ToJson();
            if (crop.Around != null) result["around"] = crop.Around;
            result["baseline"] = baselinePath;
            result["diff"] = DiffJson(diff, mapping, threshold, mergeGap);
            return result;
        }

        // screen.probe: colors at points (a small square around each) and regions of the final frame, as numbers only.
        async UniTask<object> ScreenProbe(JObject p)
        {
            int timeoutMs = OptionalTimeout(p);
            int radius = CaptureOptions.OptInt(p, "radius", 2, 0, 32);
            JArray points = p["points"] == null || p["points"].Type == JTokenType.Null ? new JArray() : p["points"] as JArray ?? throw new ArgumentException("INVALID_ARGUMENT: points must be an array of [x, y].");
            JArray regions = p["regions"] == null || p["regions"].Type == JTokenType.Null ? new JArray() : p["regions"] as JArray ?? throw new ArgumentException("INVALID_ARGUMENT: regions must be an array of [x, y, width, height].");
            if (points.Count == 0 && regions.Count == 0) throw new ArgumentException("INVALID_ARGUMENT: pass points, regions or both.");
            if (points.Count > 64) throw new ArgumentException("INVALID_ARGUMENT: at most 64 points.");
            if (regions.Count > 32) throw new ArgumentException("INVALID_ARGUMENT: at most 32 regions.");

            long started = Stamp();
            GrabbedFrame grabbed = await GrabFrame(timeoutMs);
            double grabMs = Ms(started);
            long processStart = Stamp();
            Pixmap frame = grabbed.Image;
            var items = new JArray();
            for (int i = 0; i < points.Count; i++)
            {
                var pair = points[i] as JArray;
                if (pair == null || pair.Count != 2 || !pair.All(value => value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
                    throw new ArgumentException("INVALID_ARGUMENT: points[" + i + "] must be [x, y].");
                int x = (int)Math.Floor((double)pair[0]), y = (int)Math.Floor((double)pair[1]);
                if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
                    throw new ArgumentException("REGION_OUTSIDE_SCREEN: points[" + i + "] [" + pair[0] + ", " + pair[1] + "] is not inside the " + frame.Width + "x" + frame.Height + " window.");
                int x0 = Math.Max(0, x - radius), y0 = Math.Max(0, y - radius), x1 = Math.Min(frame.Width, x + radius + 1), y1 = Math.Min(frame.Height, y + radius + 1);
                JObject item = PixelStats.Describe(frame, x0, y0, x1 - x0, y1 - y0);
                item["kind"] = "point";
                item["at"] = new JArray(x, y);
                items.Add(item);
            }
            for (int i = 0; i < regions.Count; i++)
            {
                int[] r = CaptureOptions.ParseRegion(regions[i], "regions[" + i + "]");
                if (r[0] < 0 || r[1] < 0 || r[0] + r[2] > frame.Width || r[1] + r[3] > frame.Height)
                    throw new ArgumentException("REGION_OUTSIDE_SCREEN: regions[" + i + "] [" + r[0] + ", " + r[1] + ", " + r[2] + ", " + r[3] + "] is not inside the " + frame.Width + "x" + frame.Height + " window.");
                JObject item = PixelStats.Describe(frame, r[0], r[1], r[2], r[3]);
                item["kind"] = "region";
                items.Add(item);
            }
            return new JObject
            {
                ["screen"] = new JObject { ["width"] = frame.Width, ["height"] = frame.Height },
                ["frame"] = grabbed.Frame,
                ["radius"] = radius,
                ["alphaIgnored"] = true,
                ["note"] = "RGB 0..255 as shown on screen (sRGB). Alpha is ignored, the backbuffer's alpha is not meaningful. median is per channel. luminance is Rec.709 on the mean.",
                ["items"] = items,
                ["timingMs"] = new JObject { ["grab"] = grabMs, ["process"] = Ms(processStart) },
            };
        }

        // screen.sequence: frames taken every K rendered frames, laid out as one contact sheet, with how much each changed from the
        // one before (flicker check). Only the previous full-resolution crop and the small cells are kept in memory.
        async UniTask<object> ScreenSequence(JObject p)
        {
            int timeoutMs = OptionalTimeout(p);
            CaptureOptions options = CaptureOptions.Parse(p);
            int count = CaptureOptions.OptInt(p, "frames", 6, 2, 16);
            int every = CaptureOptions.OptInt(p, "everyFrames", 10, 1, 120);
            if ((count - 1) * every > SequenceMaxSpan)
                throw new ArgumentException("SEQUENCE_TOO_LONG: " + count + " frames every " + every + " frames span " + (count - 1) * every + " rendered frames; the limit is " + SequenceMaxSpan + ".");
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");

            long started = Stamp();
            long processTotal = 0;
            GrabbedFrame grabbed = await GrabFrame(timeoutMs);
            long grabTotal = Stamp() - started;
            int width = grabbed.Image.Width, height = grabbed.Image.Height;
            CropPlan crop = ResolveCrop(options, width, height);

            int cols = count == 4 ? 2 : count == 9 ? 3 : count <= 3 ? count : count <= 6 ? 3 : 4;
            int rows = (count + cols - 1) / cols;
            double fit = Math.Min(1.0, Math.Min((double)(options.MaxSize - (cols + 1) * SequenceGap) / (cols * crop.W), (double)(options.MaxSize - (rows + 1) * SequenceGap) / (rows * crop.H)));
            int cellW = Math.Max(16, (int)Math.Floor(crop.W * fit)), cellH = Math.Max(16, (int)Math.Floor(crop.H * fit));
            var sheet = new Pixmap(cols * cellW + (cols + 1) * SequenceGap, rows * cellH + (rows + 1) * SequenceGap);
            sheet.Fill(new Color32(24, 24, 24, 255));
            int labelScale = cellW >= 700 ? 2 : 1;
            CaptureMapping cellMapping = PanelMapping(width, height, crop, cellW, cellH);

            var entries = new JArray();
            Pixmap previous = null, last = null;
            int firstFrame = grabbed.Frame;
            bool truncated = false;
            double maxFraction = 0;
            int taken = 0;
            for (int index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    if ((Stamp() - started) / (double)System.Diagnostics.Stopwatch.Frequency > SequenceMaxSeconds)
                    {
                        truncated = true;
                        break;
                    }
                    int target = firstFrame + index * every;
                    while (Time.frameCount < target) await UniTask.Yield();
                    long grabStart = Stamp();
                    grabbed = await GrabFrame(timeoutMs);
                    grabTotal += Stamp() - grabStart;
                }
                long processStart = Stamp();
                Pixmap raw = grabbed.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
                Pixmap cell = raw.Downscale(cellMapping.ImageW, cellMapping.ImageH);
                if (options.Grid) DrawGrid(cell, cellMapping, options.GridStep);
                string label = "#" + (index + 1) + " +" + (grabbed.Frame - firstFrame);
                cell.DrawText(2 + labelScale * 2, 2 + labelScale * 2, label, labelScale, new Color32(255, 235, 90, 255), new Color32(24, 24, 24, 255));
                int column = index % cols, row = index / cols;
                sheet.Blit(cell, SequenceGap + column * (cellW + SequenceGap), SequenceGap + row * (cellH + SequenceGap));

                var entry = new JObject { ["index"] = index + 1, ["frame"] = grabbed.Frame, ["frameOffset"] = grabbed.Frame - firstFrame };
                if (previous != null)
                {
                    int minX, minY, maxX, maxY;
                    long changed = PixelDiff.CountChanged(previous, raw, 6, out minX, out minY, out maxX, out maxY);
                    double fraction = changed / ((double)raw.Width * raw.Height);
                    maxFraction = Math.Max(maxFraction, fraction);
                    entry["changedFraction"] = Math.Round(fraction, 5);
                    entry["changedPixels"] = changed;
                    entry["changedBounds"] = changed == 0 ? null : new JArray(crop.X + minX, crop.Y + minY, maxX - minX + 1, maxY - minY + 1);
                }
                entries.Add(entry);
                previous = raw;
                last = raw;
                taken++;
                processTotal += Stamp() - processStart;
            }
            if (taken < 2) throw new TimeoutException("SEQUENCE_TOO_SLOW: only " + taken + " frame(s) were captured in " + (int)SequenceMaxSeconds + " s (WARUDO runs at a very low frame rate).");

            // The contact sheet is a composite, not a view of the window: space="image" is refused for it.
            var mapping = new CaptureMapping
            {
                WindowWidth = width, WindowHeight = height,
                CropX = crop.X, CropY = crop.Y, CropW = crop.W, CropH = crop.H,
                ImageW = sheet.Width, ImageH = sheet.Height,
                ViewX = SequenceGap, ViewY = SequenceGap, ViewW = cellW, ViewH = cellH,
                Clickable = false,
            };
            JObject result = SaveCapture(path, sheet, mapping, last, grabbed, grabTotal * 1000.0 / System.Diagnostics.Stopwatch.Frequency, processTotal * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            result["crop"] = crop.ToJson();
            if (crop.Around != null) result["around"] = crop.Around;
            result["everyFrames"] = every;
            result["requestedFrames"] = count;
            result["truncated"] = truncated;
            result["layout"] = new JObject { ["columns"] = cols, ["rows"] = rows, ["cell"] = new JArray(cellW, cellH), ["gap"] = SequenceGap };
            result["maxChangedFraction"] = Math.Round(maxFraction, 5);
            result["frames"] = entries;
            result["elapsedMs"] = Ms(started);
            return result;
        }
    }
}
