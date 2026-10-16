using System;
using System.Collections.Generic;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // A top-left-origin RGBA image on a plain Color32[]. Capture work (crop, scale, grid, boxes, text, compare) runs on these
    // arrays; Texture2D is only used to read the frame in and to encode the PNG out.
    sealed class Pixmap
    {
        public readonly int Width, Height;
        public readonly Color32[] Pixels;

        public Pixmap(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new Color32[width * height];
        }

        public Pixmap(int width, int height, Color32[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        // Texture rows start at the bottom; a Pixmap starts at the top.
        public static Pixmap FromTexture(Texture2D texture)
        {
            int w = texture.width, h = texture.height;
            Color32[] source = texture.GetPixels32();
            var result = new Pixmap(w, h);
            for (int y = 0; y < h; y++) Array.Copy(source, (h - 1 - y) * w, result.Pixels, y * w, w);
            return result;
        }

        public byte[] EncodePng()
        {
            var flipped = new Color32[Pixels.Length];
            for (int y = 0; y < Height; y++)
            {
                Array.Copy(Pixels, (Height - 1 - y) * Width, flipped, y * Width, Width);
            }
            for (int i = 0; i < flipped.Length; i++) flipped[i].a = 255;
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(flipped);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        public Pixmap Clone() { return new Pixmap(Width, Height, (Color32[])Pixels.Clone()); }

        public Pixmap Crop(int x, int y, int w, int h)
        {
            if (x == 0 && y == 0 && w == Width && h == Height) return this;
            var result = new Pixmap(w, h);
            for (int row = 0; row < h; row++) Array.Copy(Pixels, (y + row) * Width + x, result.Pixels, row * w, w);
            return result;
        }

        // Area average (a box filter), so small text stays legible when a big frame is shrunk. One pass over the source:
        // rows are summed into an accumulator, then each destination pixel averages its span of that accumulator.
        public Pixmap Downscale(int dw, int dh)
        {
            if (dw == Width && dh == Height) return Clone();
            double sx = (double)Width / dw, sy = (double)Height / dh;
            var result = new Pixmap(dw, dh);
            var accumulator = new float[Width * 3];
            for (int dy = 0; dy < dh; dy++)
            {
                double y0 = dy * sy, y1 = Math.Min(Height, (dy + 1) * sy);
                Array.Clear(accumulator, 0, accumulator.Length);
                for (int row = (int)y0; row < Height && row < y1; row++)
                {
                    float weight = (float)(Math.Min(y1, row + 1) - Math.Max(y0, row));
                    if (weight <= 0f) continue;
                    int baseIndex = row * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        Color32 c = Pixels[baseIndex + x];
                        int a = x * 3;
                        accumulator[a] += c.r * weight;
                        accumulator[a + 1] += c.g * weight;
                        accumulator[a + 2] += c.b * weight;
                    }
                }
                double rowWeight = y1 - y0;
                for (int dx = 0; dx < dw; dx++)
                {
                    double x0 = dx * sx, x1 = Math.Min(Width, (dx + 1) * sx);
                    double r = 0, g = 0, b = 0;
                    for (int x = (int)x0; x < Width && x < x1; x++)
                    {
                        double weight = Math.Min(x1, x + 1) - Math.Max(x0, x);
                        if (weight <= 0) continue;
                        int a = x * 3;
                        r += accumulator[a] * weight;
                        g += accumulator[a + 1] * weight;
                        b += accumulator[a + 2] * weight;
                    }
                    double area = rowWeight * (x1 - x0);
                    result.Pixels[dy * dw + dx] = new Color32((byte)Math.Round(r / area), (byte)Math.Round(g / area), (byte)Math.Round(b / area), 255);
                }
            }
            return result;
        }

        public void Blit(Pixmap source, int dx, int dy)
        {
            for (int row = 0; row < source.Height; row++)
            {
                int y = dy + row;
                if (y < 0 || y >= Height) continue;
                int from = 0, to = source.Width;
                if (dx < 0) from = -dx;
                if (dx + to > Width) to = Width - dx;
                if (to > from) Array.Copy(source.Pixels, row * source.Width + from, Pixels, y * Width + dx + from, to - from);
            }
        }

        public void Fill(Color32 color)
        {
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = color;
        }

        // alpha is 0..255 (255 = opaque). Rectangles are clipped to the image.
        public void FillRect(int x, int y, int w, int h, Color32 color, int alpha = 255)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
            if (alpha >= 255)
            {
                for (int row = y0; row < y1; row++)
                    for (int col = x0; col < x1; col++) Pixels[row * Width + col] = new Color32(color.r, color.g, color.b, 255);
                return;
            }
            int inverse = 255 - alpha;
            for (int row = y0; row < y1; row++)
            {
                for (int col = x0; col < x1; col++)
                {
                    Color32 d = Pixels[row * Width + col];
                    Pixels[row * Width + col] = new Color32((byte)((d.r * inverse + color.r * alpha) / 255), (byte)((d.g * inverse + color.g * alpha) / 255), (byte)((d.b * inverse + color.b * alpha) / 255), 255);
                }
            }
        }

        public void Outline(int x, int y, int w, int h, int thickness, Color32 color)
        {
            if (w < 1 || h < 1) return;
            int t = Math.Max(1, Math.Min(thickness, Math.Min(w, h)));
            FillRect(x, y, w, t, color);
            FillRect(x, y + h - t, w, t, color);
            FillRect(x, y, t, h, color);
            FillRect(x + w - t, y, t, h, color);
        }

        // Bresenham with a square brush.
        public void Line(int x0, int y0, int x1, int y1, int thickness, Color32 color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            int half = thickness / 2;
            for (int guard = 0; guard < 20000; guard++)
            {
                FillRect(x0 - half, y0 - half, thickness, thickness, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // A crosshair with a dark underlay, readable on light and dark pixels.
        public void Cross(int x, int y, int radius, Color32 color)
        {
            Line(x - radius, y, x + radius, y, 5, new Color32(0, 0, 0, 255));
            Line(x, y - radius, x, y + radius, 5, new Color32(0, 0, 0, 255));
            Line(x - radius, y, x + radius, y, 3, color);
            Line(x, y - radius, x, y + radius, 3, color);
        }

        public static int TextWidth(string text, int scale) { return text.Length * 6 * scale - scale; }
        public static int TextHeight(int scale) { return 7 * scale; }

        // Upper-case letters, digits and a few signs (see Glyphs). Returns the drawn width.
        public int DrawText(int x, int y, string text, int scale, Color32 color, Color32? background = null)
        {
            int width = TextWidth(text, scale);
            if (background.HasValue) FillRect(x - scale * 2, y - scale * 2, width + scale * 4, TextHeight(scale) + scale * 4, background.Value, 255);
            int cursor = x;
            foreach (char raw in text)
            {
                byte[] rows;
                if (Glyphs.TryGetValue(char.ToUpperInvariant(raw), out rows))
                {
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                            if ((rows[row] & (1 << (4 - col))) != 0) FillRect(cursor + col * scale, y + row * scale, scale, scale, color);
                }
                cursor += 6 * scale;
            }
            return width;
        }

        // 5x7 bitmap font, one hex byte per row (5 bits used).
        static readonly Dictionary<char, byte[]> Glyphs = BuildGlyphs();

        static Dictionary<char, byte[]> BuildGlyphs()
        {
            string[] table =
            {
                "0:0E 11 13 15 19 11 0E", "1:04 0C 04 04 04 04 0E", "2:0E 11 01 02 04 08 1F", "3:1F 02 04 02 01 11 0E", "4:02 06 0A 12 1F 02 02",
                "5:1F 10 1E 01 01 11 0E", "6:06 08 10 1E 11 11 0E", "7:1F 01 02 04 08 08 08", "8:0E 11 11 0E 11 11 0E", "9:0E 11 11 0F 01 02 0C",
                "A:0E 11 11 1F 11 11 11", "B:1E 11 11 1E 11 11 1E", "C:0E 11 10 10 10 11 0E", "D:1E 11 11 11 11 11 1E", "E:1F 10 10 1E 10 10 1F",
                "F:1F 10 10 1E 10 10 10", "G:0E 11 10 17 11 11 0F", "H:11 11 11 1F 11 11 11", "I:0E 04 04 04 04 04 0E", "J:07 02 02 02 02 12 0C",
                "K:11 12 14 18 14 12 11", "L:10 10 10 10 10 10 1F", "M:11 1B 15 15 11 11 11", "N:11 11 19 15 13 11 11", "O:0E 11 11 11 11 11 0E",
                "P:1E 11 11 1E 10 10 10", "Q:0E 11 11 11 15 12 0D", "R:1E 11 11 1E 14 12 11", "S:0F 10 10 0E 01 01 1E", "T:1F 04 04 04 04 04 04",
                "U:11 11 11 11 11 11 0E", "V:11 11 11 11 11 0A 04", "W:11 11 11 15 15 15 0A", "X:11 11 0A 04 0A 11 11", "Y:11 11 0A 04 04 04 04",
                "Z:1F 01 02 04 08 10 1F", "-:00 00 00 1F 00 00 00", ".:00 00 00 00 00 0C 0C", "::00 0C 0C 00 0C 0C 00", "/:01 01 02 04 08 10 10",
                "%:18 19 02 04 08 13 03", "#:0A 0A 1F 0A 1F 0A 0A", "=:00 00 1F 00 1F 00 00", "+:00 04 04 1F 04 04 00", " :00 00 00 00 00 00 00",
            };
            var glyphs = new Dictionary<char, byte[]>();
            foreach (string entry in table)
            {
                string[] rows = entry.Substring(2).Split(' ');
                var bytes = new byte[7];
                for (int i = 0; i < 7; i++) bytes[i] = Convert.ToByte(rows[i], 16);
                glyphs[entry[0]] = bytes;
            }
            return glyphs;
        }
    }
}
