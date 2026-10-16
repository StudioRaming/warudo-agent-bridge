using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // One changed region of a diff: a box in crop pixels and the mean color of its changed pixels before and after.
    sealed class DiffBox
    {
        public int X, Y, W, H;
        public long Changed;
        public Color32 Before, After;
    }

    sealed class DiffResult
    {
        public int Width, Height;
        public long ChangedPixels;
        public byte[] Mask;
        public int BoxesTotal;
        public long OmittedChanged;
        public readonly List<DiffBox> Boxes = new List<DiffBox>();

        public double ChangedFraction { get { return Width * (double)Height == 0 ? 0 : ChangedPixels / (Width * (double)Height); } }
    }

    // Compares two same-size pixel maps. A pixel changed when any RGB channel differs by more than threshold (alpha is ignored:
    // the backbuffer's alpha is not meaningful). Changed pixels are grouped by 8x8 blocks, blocks whose changed pixels lie within
    // mergeGap of each other are joined, and each group becomes one box. Everything works on Color32[] arrays, no GetPixel.
    static class PixelDiff
    {
        const int BlockShift = 3;
        const int Block = 1 << BlockShift;

        public static DiffResult Compute(Pixmap before, Pixmap after, int threshold, int mergeGap, int maxBoxes)
        {
            if (before.Width != after.Width || before.Height != after.Height) throw new ArgumentException("SIZE_MISMATCH: the two frames differ in size.");
            int w = before.Width, h = before.Height;
            var result = new DiffResult { Width = w, Height = h, Mask = new byte[w * h] };
            int bw = (w + Block - 1) >> BlockShift, bh = (h + Block - 1) >> BlockShift;
            var minX = new int[bw * bh];
            var minY = new int[bw * bh];
            var maxX = new int[bw * bh];
            var maxY = new int[bw * bh];
            var count = new int[bw * bh];
            for (int i = 0; i < minX.Length; i++) { minX[i] = int.MaxValue; minY[i] = int.MaxValue; maxX[i] = -1; maxY[i] = -1; }
            Color32[] pa = before.Pixels, pb = after.Pixels;
            long changed = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, blockRow = (y >> BlockShift) * bw;
                for (int x = 0; x < w; x++)
                {
                    Color32 p = pa[row + x], q = pb[row + x];
                    int d = p.r - q.r; if (d < 0) d = -d;
                    int e = p.g - q.g; if (e < 0) e = -e;
                    if (e > d) d = e;
                    e = p.b - q.b; if (e < 0) e = -e;
                    if (e > d) d = e;
                    if (d <= threshold) continue;
                    result.Mask[row + x] = 1;
                    changed++;
                    int block = blockRow + (x >> BlockShift);
                    if (x < minX[block]) minX[block] = x;
                    if (x > maxX[block]) maxX[block] = x;
                    if (y < minY[block]) minY[block] = y;
                    if (y > maxY[block]) maxY[block] = y;
                    count[block]++;
                }
            }
            result.ChangedPixels = changed;
            if (changed == 0) return result;

            // Union-find over the changed blocks. Two blocks join when their changed pixels are within mergeGap of each other.
            var parent = new int[bw * bh];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int reach = mergeGap / Block + 1;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int a = by * bw + bx;
                    if (count[a] == 0) continue;
                    for (int ny = by; ny <= Math.Min(bh - 1, by + reach); ny++)
                    {
                        for (int nx = Math.Max(0, bx - reach); nx <= Math.Min(bw - 1, bx + reach); nx++)
                        {
                            if (ny == by && nx <= bx) continue;
                            int b = ny * bw + nx;
                            if (count[b] == 0) continue;
                            int gx = Math.Max(0, Math.Max(minX[a], minX[b]) - Math.Min(maxX[a], maxX[b]) - 1);
                            int gy = Math.Max(0, Math.Max(minY[a], minY[b]) - Math.Min(maxY[a], maxY[b]) - 1);
                            if (gx <= mergeGap && gy <= mergeGap) Union(parent, a, b);
                        }
                    }
                }
            }
            var boxes = new Dictionary<int, DiffBox>();
            var right = new Dictionary<int, int>();
            var bottom = new Dictionary<int, int>();
            for (int i = 0; i < count.Length; i++)
            {
                if (count[i] == 0) continue;
                int root = Find(parent, i);
                DiffBox box;
                if (!boxes.TryGetValue(root, out box))
                {
                    box = new DiffBox { X = minX[i], Y = minY[i] };
                    boxes[root] = box;
                    right[root] = maxX[i];
                    bottom[root] = maxY[i];
                }
                else
                {
                    if (minX[i] < box.X) box.X = minX[i];
                    if (minY[i] < box.Y) box.Y = minY[i];
                    if (maxX[i] > right[root]) right[root] = maxX[i];
                    if (maxY[i] > bottom[root]) bottom[root] = maxY[i];
                }
                box.Changed += count[i];
            }
            foreach (KeyValuePair<int, DiffBox> pair in boxes)
            {
                pair.Value.W = right[pair.Key] - pair.Value.X + 1;
                pair.Value.H = bottom[pair.Key] - pair.Value.Y + 1;
            }
            List<DiffBox> ordered = boxes.Values.OrderByDescending(box => box.Changed).ThenBy(box => box.Y).ThenBy(box => box.X).ToList();
            result.BoxesTotal = ordered.Count;
            foreach (DiffBox box in ordered.Take(maxBoxes))
            {
                MeanOfChanged(result.Mask, before, after, box);
                result.Boxes.Add(box);
            }
            result.OmittedChanged = changed - result.Boxes.Sum(box => box.Changed);
            return result;
        }

        static void MeanOfChanged(byte[] mask, Pixmap before, Pixmap after, DiffBox box)
        {
            long br = 0, bg = 0, bb = 0, ar = 0, ag = 0, ab = 0, n = 0;
            for (int y = box.Y; y < box.Y + box.H; y++)
            {
                int row = y * before.Width;
                for (int x = box.X; x < box.X + box.W; x++)
                {
                    if (mask[row + x] == 0) continue;
                    Color32 p = before.Pixels[row + x], q = after.Pixels[row + x];
                    br += p.r; bg += p.g; bb += p.b; ar += q.r; ag += q.g; ab += q.b; n++;
                }
            }
            if (n == 0) return;
            box.Before = new Color32((byte)((br + n / 2) / n), (byte)((bg + n / 2) / n), (byte)((bb + n / 2) / n), 255);
            box.After = new Color32((byte)((ar + n / 2) / n), (byte)((ag + n / 2) / n), (byte)((ab + n / 2) / n), 255);
        }

        // Only how many pixels changed and where (for the per-frame flicker numbers of a sequence).
        public static long CountChanged(Pixmap before, Pixmap after, int threshold, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
            int w = before.Width, h = before.Height;
            long changed = 0;
            Color32[] pa = before.Pixels, pb = after.Pixels;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Color32 p = pa[row + x], q = pb[row + x];
                    int d = p.r - q.r; if (d < 0) d = -d;
                    int e = p.g - q.g; if (e < 0) e = -e;
                    if (e > d) d = e;
                    e = p.b - q.b; if (e < 0) e = -e;
                    if (e > d) d = e;
                    if (d <= threshold) continue;
                    changed++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return changed;
        }

        // Unchanged pixels become dark grey (about 40 % of their luminance); changed pixels keep their color from `after`.
        public static Pixmap Render(Pixmap after, DiffResult diff)
        {
            var image = new Pixmap(after.Width, after.Height);
            Color32[] source = after.Pixels, target = image.Pixels;
            byte[] mask = diff.Mask;
            for (int i = 0; i < source.Length; i++)
            {
                Color32 c = source[i];
                if (mask[i] != 0) { target[i] = new Color32(c.r, c.g, c.b, 255); continue; }
                byte grey = (byte)(((c.r * 54 + c.g * 183 + c.b * 19) >> 8) * 40 / 100);
                target[i] = new Color32(grey, grey, grey, 255);
            }
            return image;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        public static JArray Rgb(Color32 c) { return new JArray((int)c.r, (int)c.g, (int)c.b); }
    }

    // Color numbers of a rectangle of a frame: mean, median, HSV of the mean, luminance, min, max. Alpha is ignored.
    static class PixelStats
    {
        public static JObject Describe(Pixmap frame, int x, int y, int w, int h)
        {
            var histogram = new int[3][];
            for (int c = 0; c < 3; c++) histogram[c] = new int[256];
            var sum = new long[3];
            int[] min = { 255, 255, 255 }, max = { 0, 0, 0 };
            for (int row = y; row < y + h; row++)
            {
                int offset = row * frame.Width;
                for (int col = x; col < x + w; col++)
                {
                    Color32 pixel = frame.Pixels[offset + col];
                    int r = pixel.r, g = pixel.g, b = pixel.b;
                    histogram[0][r]++; histogram[1][g]++; histogram[2][b]++;
                    sum[0] += r; sum[1] += g; sum[2] += b;
                    if (r < min[0]) min[0] = r;
                    if (r > max[0]) max[0] = r;
                    if (g < min[1]) min[1] = g;
                    if (g > max[1]) max[1] = g;
                    if (b < min[2]) min[2] = b;
                    if (b > max[2]) max[2] = b;
                }
            }
            long samples = (long)w * h;
            var mean = new double[3];
            var median = new int[3];
            for (int c = 0; c < 3; c++)
            {
                mean[c] = sum[c] / (double)samples;
                long target = (samples + 1) / 2, seen = 0;
                for (int v = 0; v < 256; v++)
                {
                    seen += histogram[c][v];
                    if (seen >= target) { median[c] = v; break; }
                }
            }
            double hue, saturation, value;
            Hsv(mean[0] / 255.0, mean[1] / 255.0, mean[2] / 255.0, out hue, out saturation, out value);
            double luminance = 0.2126 * mean[0] + 0.7152 * mean[1] + 0.0722 * mean[2];
            string hex = "#" + ((int)Math.Round(mean[0])).ToString("x2") + ((int)Math.Round(mean[1])).ToString("x2") + ((int)Math.Round(mean[2])).ToString("x2");
            return new JObject
            {
                ["rect"] = new JArray(x, y, w, h),
                ["samples"] = samples,
                ["mean"] = new JArray(Math.Round(mean[0], 1), Math.Round(mean[1], 1), Math.Round(mean[2], 1)),
                ["hex"] = hex,
                ["median"] = new JArray(median[0], median[1], median[2]),
                ["hsv"] = new JArray(Math.Round(hue, 1), Math.Round(saturation, 3), Math.Round(value, 3)),
                ["luminance"] = Math.Round(luminance, 1),
                ["min"] = new JArray(min[0], min[1], min[2]),
                ["max"] = new JArray(max[0], max[1], max[2]),
            };
        }

        // r, g, b in 0..1; hue in degrees 0..360, saturation and value in 0..1.
        public static void Hsv(double r, double g, double b, out double hue, out double saturation, out double value)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
            value = max;
            saturation = max <= 0 ? 0 : delta / max;
            if (delta <= 0) hue = 0;
            else if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }
    }
}
