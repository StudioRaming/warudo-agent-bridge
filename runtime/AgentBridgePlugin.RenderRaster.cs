using System;
using System.Collections.Generic;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // Render passes: the CPU math behind render.pass. Everything here works on plain arrays and Color32
    // (no engine calls), so it is safe on the thread pool and can be compiled and measured outside Unity.
    // One rasterization per view fills a G-buffer (nearest triangle and perspective-correct weights per
    // pixel); every pass is then a small shader over that buffer.

    // A texture read once per call, as the bytes an artist sees (sRGB textures stay sRGB-encoded).
    sealed class RasterTexture
    {
        public int Width, Height;
        public Color32[] Pixels; // row 0 is v = 0 (bottom), the Unity convention
        public bool Clamp;
    }

    // A texture and its _ST (tiling and offset) as the renderer really uses it.
    struct TexBinding
    {
        public RasterTexture Texture;
        public float Sx, Sy, Ox, Oy;
        public bool Has { get { return Texture != null; } }
    }

    // One renderer material slot that owns triangles.
    sealed class RasterSlot
    {
        public int Number;                       // legend number, 1-based
        public Color32 PartColor;
        public TexBinding Auto;                  // the material's own main texture: its alpha decides the cutout
        public TexBinding Display;               // what albedo and alpha show: textureProperty when given, else Auto
        public TexBinding Chosen;                // textureProperty only: the texture pass
        public float TintR = 1, TintG = 1, TintB = 1, TintA = 1;
        public float Cutoff = -1;                // >= 0: cutout, pixels with alpha below it are not drawn
    }

    sealed class RasterMesh
    {
        public float[] Pos;      // world x,y,z per vertex
        public float[] Nrm;      // world normal per vertex, unit length or zero
        public float[] Uv;       // uv0 per vertex
        public int[] Group;      // renderer index per vertex (a seam never joins two renderers)
        public int[] Tris;       // vertex indices, three per triangle
        public int[] TriSlot;    // slot index per triangle
        public int TriangleCount { get { return Tris.Length / 3; } }
    }

    // A view projected into one panel (panel-local pixels, origin top-left).
    sealed class RasterProjection
    {
        public int Size;
        public int ClipX0, ClipY0, ClipX1, ClipY1; // drawable pixels, ClipX1/ClipY1 exclusive
        public bool Perspective;
        public float[] Sx, Sy, Depth, InvW;        // per vertex; Depth is distance along the view direction
        public bool[] Ok;
        public float CamX, CamY, CamZ;             // eye position (perspective)
        public float FwdX, FwdY, FwdZ;             // view direction (orthographic)
    }

    sealed class GBuffer
    {
        public readonly int Size;
        public readonly float[] Depth;
        public readonly int[] Tri;       // -1 where nothing is drawn
        public readonly float[] B0, B1;  // perspective-correct weights of the first two vertices; the third is the rest
        public readonly ushort[] Cover;  // front-facing surfaces covering the pixel (after the alpha test)
        public GBuffer(int size)
        {
            Size = size;
            Depth = new float[size * size];
            Tri = new int[size * size];
            B0 = new float[size * size];
            B1 = new float[size * size];
            Cover = new ushort[size * size];
            for (int i = 0; i < Depth.Length; i++) { Depth[i] = float.MaxValue; Tri[i] = -1; }
        }
    }

    struct Frag
    {
        public int Tri, Slot, A, B, C;
        public float P0, P1, P2;
    }

    // Visible pixels per slot: count, bounding box and centroid.
    sealed class SlotStats
    {
        public int[] Count, MinX, MinY, MaxX, MaxY;
        public double[] SumX, SumY;
    }

    static class RenderRaster
    {
        // ---- sampling ----------------------------------------------------------------------------

        static int Wrap(int i, int n, bool clamp)
        {
            if (clamp) return i < 0 ? 0 : i >= n ? n - 1 : i;
            i %= n;
            return i < 0 ? i + n : i;
        }

        // Bilinear sample, channels 0..255. u/v are mesh UVs; the binding's tiling and offset are applied here.
        public static void Sample(ref TexBinding b, float u, float v, out float r, out float g, out float bl, out float a)
        {
            RasterTexture t = b.Texture;
            float x = (u * b.Sx + b.Ox) * t.Width - 0.5f, y = (v * b.Sy + b.Oy) * t.Height - 0.5f;
            if (!(Math.Abs(x) < 1e7f)) x = 0;
            if (!(Math.Abs(y) < 1e7f)) y = 0;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            int xa = Wrap(x0, t.Width, t.Clamp), xb = Wrap(x0 + 1, t.Width, t.Clamp);
            int ya = Wrap(y0, t.Height, t.Clamp), yb = Wrap(y0 + 1, t.Height, t.Clamp);
            Color32 c00 = t.Pixels[ya * t.Width + xa], c10 = t.Pixels[ya * t.Width + xb];
            Color32 c01 = t.Pixels[yb * t.Width + xa], c11 = t.Pixels[yb * t.Width + xb];
            float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
            r = c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11;
            g = c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11;
            bl = c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11;
            a = c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11;
        }

        // Alpha as a 0..1 value the way the shader sees it: texture alpha times the material color alpha.
        static float SlotAlpha(RasterSlot slot, ref TexBinding binding, float u, float v)
        {
            if (!binding.Has) return slot.TintA;
            float r, g, b, a;
            Sample(ref binding, u, v, out r, out g, out b, out a);
            return a / 255f * slot.TintA;
        }

        // ---- rasterization -----------------------------------------------------------------------

        // Sorted endpoints make the edge function exactly antisymmetric, so two triangles that share an edge
        // agree on every pixel center that lies on it.
        static float EdgeSym(float ax, float ay, float bx, float by, float px, float py)
        {
            if (ax < bx || (ax == bx && ay <= by)) return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
            return -((ax - bx) * (py - by) - (ay - by) * (px - bx));
        }

        static bool Own(float gx, float gy) { return gx > 0 || (gx == 0 && gy > 0); }

        // Per triangle: true when it faces the viewer (Unity front faces are the ones whose
        // cross(b - a, c - a) points toward the viewer).
        public static bool[] Facing(RasterMesh m, RasterProjection pr)
        {
            int count = m.TriangleCount;
            var front = new bool[count];
            float[] p = m.Pos;
            for (int t = 0; t < count; t++)
            {
                int a = m.Tris[3 * t] * 3, b = m.Tris[3 * t + 1] * 3, c = m.Tris[3 * t + 2] * 3;
                float ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
                float vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float tx, ty, tz;
                if (pr.Perspective)
                {
                    tx = pr.CamX - (p[a] + p[b] + p[c]) / 3f; ty = pr.CamY - (p[a + 1] + p[b + 1] + p[c + 1]) / 3f; tz = pr.CamZ - (p[a + 2] + p[b + 2] + p[c + 2]) / 3f;
                }
                else { tx = -pr.FwdX; ty = -pr.FwdY; tz = -pr.FwdZ; }
                front[t] = nx * tx + ny * ty + nz * tz > 0;
            }
            return front;
        }

        // alphaCut: cutout slots drop the pixels whose alpha is below their cutoff (a real cutout hole shows what is behind it).
        public static GBuffer Rasterize(RasterMesh m, RasterSlot[] slots, RasterProjection pr, bool[] front, bool alphaCut)
        {
            int size = pr.Size;
            var g = new GBuffer(size);
            int count = m.TriangleCount;
            for (int t = 0; t < count; t++)
            {
                int ia = m.Tris[3 * t], ib = m.Tris[3 * t + 1], ic = m.Tris[3 * t + 2];
                if (!pr.Ok[ia] || !pr.Ok[ib] || !pr.Ok[ic]) continue;
                float x0 = pr.Sx[ia], y0 = pr.Sy[ia], x1 = pr.Sx[ib], y1 = pr.Sy[ib], x2 = pr.Sx[ic], y2 = pr.Sy[ic];
                float area = EdgeSym(x0, y0, x1, y1, x2, y2);
                if (Math.Abs(area) < 1e-6f) continue;
                float sign = area > 0 ? 1f : -1f;
                bool own0 = Own(-sign * (y2 - y1), sign * (x2 - x1));
                bool own1 = Own(-sign * (y0 - y2), sign * (x0 - x2));
                bool own2 = Own(-sign * (y1 - y0), sign * (x1 - x0));
                int minX = Math.Max(pr.ClipX0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
                int maxX = Math.Min(pr.ClipX1 - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                int minY = Math.Max(pr.ClipY0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
                int maxY = Math.Min(pr.ClipY1 - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
                if (minX > maxX || minY > maxY) continue;
                RasterSlot slot = slots[m.TriSlot[t]];
                bool cut = alphaCut && slot.Cutoff >= 0;
                bool isFront = front[t];
                float iw0 = pr.InvW[ia], iw1 = pr.InvW[ib], iw2 = pr.InvW[ic];
                float z0 = pr.Depth[ia], z1 = pr.Depth[ib], z2 = pr.Depth[ic];
                float u0 = m.Uv[2 * ia], v0 = m.Uv[2 * ia + 1], u1 = m.Uv[2 * ib], v1 = m.Uv[2 * ib + 1], u2 = m.Uv[2 * ic], v2 = m.Uv[2 * ic + 1];
                for (int y = minY; y <= maxY; y++)
                {
                    float py = y + 0.5f;
                    for (int x = minX; x <= maxX; x++)
                    {
                        float px = x + 0.5f;
                        float e0 = EdgeSym(x1, y1, x2, y2, px, py) * sign;
                        if (e0 < 0 || (e0 == 0 && !own0)) continue;
                        float e1 = EdgeSym(x2, y2, x0, y0, px, py) * sign;
                        if (e1 < 0 || (e1 == 0 && !own1)) continue;
                        float e2 = EdgeSym(x0, y0, x1, y1, px, py) * sign;
                        if (e2 < 0 || (e2 == 0 && !own2)) continue;
                        float sum = e0 + e1 + e2;
                        if (sum <= 0) continue;
                        float l0 = e0 / sum, l1 = e1 / sum, l2 = e2 / sum;
                        float p0, p1, z;
                        if (pr.Perspective)
                        {
                            float q0 = l0 * iw0, q1 = l1 * iw1, q2 = l2 * iw2;
                            float den = q0 + q1 + q2;
                            if (den <= 0) continue;
                            p0 = q0 / den; p1 = q1 / den; z = 1f / den;
                        }
                        else { p0 = l0; p1 = l1; z = l0 * z0 + l1 * z1 + l2 * z2; }
                        if (cut)
                        {
                            float p2 = 1f - p0 - p1;
                            float alpha = SlotAlpha(slot, ref slot.Auto, p0 * u0 + p1 * u1 + p2 * u2, p0 * v0 + p1 * v1 + p2 * v2);
                            if (alpha < slot.Cutoff) continue;
                        }
                        int d = y * size + x;
                        if (isFront && g.Cover[d] < ushort.MaxValue) g.Cover[d]++;
                        if (z >= g.Depth[d]) continue;
                        g.Depth[d] = z; g.Tri[d] = t; g.B0[d] = p0; g.B1[d] = p1;
                    }
                }
            }
            return g;
        }

        static bool Fetch(RasterMesh m, GBuffer g, int idx, out Frag f)
        {
            f = new Frag();
            int t = g.Tri[idx];
            if (t < 0) return false;
            f.Tri = t; f.Slot = m.TriSlot[t];
            f.A = m.Tris[3 * t]; f.B = m.Tris[3 * t + 1]; f.C = m.Tris[3 * t + 2];
            f.P0 = g.B0[idx]; f.P1 = g.B1[idx]; f.P2 = 1f - f.P0 - f.P1;
            return true;
        }

        static void Uv(RasterMesh m, ref Frag f, out float u, out float v)
        {
            u = f.P0 * m.Uv[2 * f.A] + f.P1 * m.Uv[2 * f.B] + f.P2 * m.Uv[2 * f.C];
            v = f.P0 * m.Uv[2 * f.A + 1] + f.P1 * m.Uv[2 * f.B + 1] + f.P2 * m.Uv[2 * f.C + 1];
        }

        // Interpolated normal, unit length, flipped on back faces the way a shader's VFACE flips it.
        static void Normal(RasterMesh m, ref Frag f, bool front, out float nx, out float ny, out float nz)
        {
            float[] n = m.Nrm;
            nx = f.P0 * n[3 * f.A] + f.P1 * n[3 * f.B] + f.P2 * n[3 * f.C];
            ny = f.P0 * n[3 * f.A + 1] + f.P1 * n[3 * f.B + 1] + f.P2 * n[3 * f.C + 1];
            nz = f.P0 * n[3 * f.A + 2] + f.P1 * n[3 * f.B + 2] + f.P2 * n[3 * f.C + 2];
            float length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-6f) { nx = ny = nz = 0; return; }
            float k = (front ? 1f : -1f) / length;
            nx *= k; ny *= k; nz *= k;
        }

        static byte B(float v) { return (byte)(v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f)); }
        static Color32 Rgb(float r, float g, float b) { return new Color32(B(r), B(g), B(b), 255); }

        // ---- passes ------------------------------------------------------------------------------

        public static SlotStats Stats(RasterMesh m, GBuffer g, int slotCount)
        {
            var s = new SlotStats
            {
                Count = new int[slotCount], MinX = new int[slotCount], MinY = new int[slotCount], MaxX = new int[slotCount], MaxY = new int[slotCount],
                SumX = new double[slotCount], SumY = new double[slotCount],
            };
            for (int i = 0; i < slotCount; i++) { s.MinX[i] = s.MinY[i] = int.MaxValue; s.MaxX[i] = s.MaxY[i] = int.MinValue; }
            int size = g.Size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int t = g.Tri[y * size + x];
                    if (t < 0) continue;
                    int slot = m.TriSlot[t];
                    s.Count[slot]++; s.SumX[slot] += x; s.SumY[slot] += y;
                    if (x < s.MinX[slot]) s.MinX[slot] = x; if (x > s.MaxX[slot]) s.MaxX[slot] = x;
                    if (y < s.MinY[slot]) s.MinY[slot] = y; if (y > s.MaxY[slot]) s.MaxY[slot] = y;
                }
            return s;
        }

        // Every slot in its own flat color, a darker line where two slots meet, the legend number on each.
        public static void ShadeParts(RasterMesh m, RasterSlot[] slots, GBuffer g, SlotStats stats, Color32[] img, bool labels)
        {
            int size = g.Size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x, t = g.Tri[idx];
                    if (t < 0) continue;
                    int slot = m.TriSlot[t];
                    Color32 c = slots[slot].PartColor;
                    bool edge = (x + 1 < size && SlotAt(m, g, idx + 1) != slot) || (y + 1 < size && SlotAt(m, g, idx + size) != slot);
                    img[idx] = edge ? new Color32((byte)(c.r * 45 / 100), (byte)(c.g * 45 / 100), (byte)(c.b * 45 / 100), 255) : c;
                }
            if (!labels) return;
            int scale = size >= 700 ? 3 : 2;
            var bestDistance = new double[slots.Length];
            var bestIndex = new int[slots.Length];
            for (int i = 0; i < slots.Length; i++) { bestDistance[i] = double.MaxValue; bestIndex[i] = -1; }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int t = g.Tri[y * size + x];
                    if (t < 0) continue;
                    int slot = m.TriSlot[t];
                    double dx = x - stats.SumX[slot] / stats.Count[slot], dy = y - stats.SumY[slot] / stats.Count[slot];
                    double d = dx * dx + dy * dy;
                    if (d < bestDistance[slot]) { bestDistance[slot] = d; bestIndex[slot] = y * size + x; }
                }
            for (int i = 0; i < slots.Length; i++)
            {
                if (bestIndex[i] < 0) continue;
                string text = slots[i].Number.ToString();
                int width = text.Length * 6 * scale - scale;
                int px = bestIndex[i] % size - width / 2, py = bestIndex[i] / size - 7 * scale / 2;
                px = Math.Max(2, Math.Min(size - width - 2, px)); py = Math.Max(2, Math.Min(size - 7 * scale - 2, py));
                Color32 c = slots[i].PartColor;
                bool light = c.r * 299 + c.g * 587 + c.b * 114 > 150 * 1000;
                DrawText(img, size, px, py, text, scale, light ? new Color32(10, 10, 12, 255) : new Color32(255, 255, 255, 255));
            }
        }

        static int SlotAt(RasterMesh m, GBuffer g, int idx) { int t = g.Tri[idx]; return t < 0 ? -1 : m.TriSlot[t]; }

        // The slot's texture times its color, unlit.
        public static void ShadeAlbedo(RasterMesh m, RasterSlot[] slots, GBuffer g, Color32[] img)
        {
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                Frag f;
                if (!Fetch(m, g, idx, out f)) continue;
                RasterSlot slot = slots[f.Slot];
                if (!slot.Display.Has) { img[idx] = Rgb(slot.TintR * 255f, slot.TintG * 255f, slot.TintB * 255f); continue; }
                float u, v, r, gr, b, a;
                Uv(m, ref f, out u, out v);
                Sample(ref slot.Display, u, v, out r, out gr, out b, out a);
                img[idx] = Rgb(r * slot.TintR, gr * slot.TintG, b * slot.TintB);
            }
        }

        // A named texture as it is (no color multiply); slots without it stay grey.
        public static readonly Color32 MissingGrey = new Color32(110, 110, 110, 255);

        public static void ShadeTexture(RasterMesh m, RasterSlot[] slots, GBuffer g, Color32[] img)
        {
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                Frag f;
                if (!Fetch(m, g, idx, out f)) continue;
                RasterSlot slot = slots[f.Slot];
                if (!slot.Chosen.Has) { img[idx] = MissingGrey; continue; }
                float u, v, r, gr, b, a;
                Uv(m, ref f, out u, out v);
                Sample(ref slot.Chosen, u, v, out r, out gr, out b, out a);
                img[idx] = Rgb(r, gr, b);
            }
        }

        // Alpha as grey. Pixels a cutout material would drop (alpha under its cutoff) are tinted red. g must come
        // from a rasterization without the alpha test, or the holes would show what is behind them instead.
        public static void ShadeAlpha(RasterMesh m, RasterSlot[] slots, GBuffer g, Color32[] img)
        {
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                Frag f;
                if (!Fetch(m, g, idx, out f)) continue;
                RasterSlot slot = slots[f.Slot];
                float u, v;
                Uv(m, ref f, out u, out v);
                float alpha = Math.Max(0f, Math.Min(1f, SlotAlpha(slot, ref slot.Display, u, v)));
                float grey = alpha * 255f;
                img[idx] = slot.Cutoff >= 0 && alpha < slot.Cutoff ? Rgb(grey * 0.4f + 150f, grey * 0.2f, grey * 0.2f) : Rgb(grey, grey, grey);
            }
        }

        public static void ShadeNormals(RasterMesh m, GBuffer g, bool[] front, Color32[] img)
        {
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                Frag f;
                if (!Fetch(m, g, idx, out f)) continue;
                float nx, ny, nz;
                Normal(m, ref f, front[f.Tri], out nx, out ny, out nz);
                img[idx] = nx == 0 && ny == 0 && nz == 0 ? MissingGrey : Rgb((nx * 0.5f + 0.5f) * 255f, (ny * 0.5f + 0.5f) * 255f, (nz * 0.5f + 0.5f) * 255f);
            }
        }

        // White material, Lambert from one direction (towards the light) plus a flat ambient.
        public static void ShadeLighting(RasterMesh m, GBuffer g, bool[] front, Color32[] img, float lx, float ly, float lz, float ambient)
        {
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                Frag f;
                if (!Fetch(m, g, idx, out f)) continue;
                float nx, ny, nz;
                Normal(m, ref f, front[f.Tri], out nx, out ny, out nz);
                float lit = Math.Max(0f, nx * lx + ny * ly + nz * lz);
                float value = (ambient + (1f - ambient) * lit) * 255f;
                img[idx] = Rgb(value, value, value);
            }
        }

        static readonly int[][] DepthStops =
        {
            new[] { 255, 245, 120 }, new[] { 255, 150, 40 }, new[] { 220, 50, 90 }, new[] { 110, 40, 160 }, new[] { 30, 40, 120 },
        };

        static Color32 Ramp(int[][] stops, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t)) * (stops.Length - 1);
            int i = Math.Min((int)t, stops.Length - 2);
            float k = t - i;
            return Rgb(stops[i][0] + (stops[i + 1][0] - stops[i][0]) * k, stops[i][1] + (stops[i + 1][1] - stops[i][1]) * k, stops[i][2] + (stops[i + 1][2] - stops[i][2]) * k);
        }

        public static bool DepthRange(GBuffer g, out float near, out float far)
        {
            near = float.MaxValue; far = float.MinValue;
            for (int i = 0; i < g.Depth.Length; i++)
            {
                if (g.Tri[i] < 0) continue;
                if (g.Depth[i] < near) near = g.Depth[i];
                if (g.Depth[i] > far) far = g.Depth[i];
            }
            return near <= far;
        }

        // Near is warm, far is cool; a darker line every 5 % of the range makes the depth readable.
        public static void ShadeDepth(GBuffer g, Color32[] img, float near, float far)
        {
            int size = g.Size;
            float span = Math.Max(1e-4f, far - near);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x;
                    if (g.Tri[idx] < 0) continue;
                    float t = (g.Depth[idx] - near) / span;
                    Color32 c = Ramp(DepthStops, t);
                    int band = (int)(t * 20f);
                    bool line = (x + 1 < size && g.Tri[idx + 1] >= 0 && (int)((g.Depth[idx + 1] - near) / span * 20f) != band) ||
                                (y + 1 < size && g.Tri[idx + size] >= 0 && (int)((g.Depth[idx + size] - near) / span * 20f) != band);
                    img[idx] = line ? new Color32((byte)(c.r * 78 / 100), (byte)(c.g * 78 / 100), (byte)(c.b * 78 / 100), 255) : c;
                }
        }

        public static readonly int[][] OverlapStops =
        {
            new[] { 40, 90, 120 }, new[] { 60, 170, 100 }, new[] { 235, 205, 60 }, new[] { 240, 120, 40 }, new[] { 240, 45, 45 },
        };

        public static Color32 OverlapColor(int count)
        {
            return Ramp(OverlapStops, (Math.Min(Math.Max(count, 1), 5) - 1) / 4f);
        }

        public static void ShadeOverlap(GBuffer g, Color32[] img, out int maxCover)
        {
            maxCover = 0;
            int size = g.Size;
            for (int idx = 0; idx < size * size; idx++)
            {
                if (g.Tri[idx] < 0) continue;
                int count = g.Cover[idx];
                if (count > maxCover) maxCover = count;
                img[idx] = OverlapColor(count);
            }
        }

        // UV as color (u red, v green) with a line every 0.1, hatching where a UV leaves 0..1.
        public static void ShadeUv(RasterMesh m, RasterProjection pr, GBuffer g, Color32[] img)
        {
            int size = g.Size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x;
                    Frag f;
                    if (!Fetch(m, g, idx, out f)) continue;
                    float u, v;
                    Uv(m, ref f, out u, out v);
                    bool outside = u < 0 || u > 1 || v < 0 || v > 1;
                    float fu = u - (float)Math.Floor(u), fv = v - (float)Math.Floor(v);
                    float r = fu * 255f, gr = fv * 255f, b = 60f;
                    // Texels per pixel from the triangle's UV area over its screen area, so lines are about one pixel wide.
                    float uvArea = Math.Abs((m.Uv[2 * f.B] - m.Uv[2 * f.A]) * (m.Uv[2 * f.C + 1] - m.Uv[2 * f.A + 1]) - (m.Uv[2 * f.B + 1] - m.Uv[2 * f.A + 1]) * (m.Uv[2 * f.C] - m.Uv[2 * f.A]));
                    float screenArea = Math.Abs((pr.Sx[f.B] - pr.Sx[f.A]) * (pr.Sy[f.C] - pr.Sy[f.A]) - (pr.Sy[f.B] - pr.Sy[f.A]) * (pr.Sx[f.C] - pr.Sx[f.A]));
                    float footprint = screenArea < 1e-6f ? 0f : (float)Math.Sqrt(uvArea / screenArea);
                    float half = Math.Max(1e-5f, Math.Min(0.02f, footprint * 0.75f));
                    float cu = u * 10f - (float)Math.Floor(u * 10f), cv = v * 10f - (float)Math.Floor(v * 10f);
                    bool grid = Math.Min(cu, 1f - cu) / 10f < half || Math.Min(cv, 1f - cv) / 10f < half;
                    if (grid) { r *= 0.35f; gr *= 0.35f; b *= 0.35f; }
                    if (outside)
                    {
                        bool stripe = ((x + y) / 4) % 2 == 0;
                        float k = stripe ? 235f : 20f;
                        r = r * 0.35f + k * 0.65f; gr = gr * 0.35f + k * 0.65f; b = b * 0.35f + k * 0.65f;
                    }
                    img[idx] = Rgb(r, gr, b);
                }
        }

        struct PositionKey : IEquatable<PositionKey>
        {
            public int X, Y, Z, Group;
            public bool Equals(PositionKey o) { return X == o.X && Y == o.Y && Z == o.Z && Group == o.Group; }
            public override bool Equals(object o) { return o is PositionKey && Equals((PositionKey)o); }
            public override int GetHashCode() { return ((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791)) * 31 + Group; }
        }

        // Edges that are one place in 3D but two places in UV space (the cuts of the unwrap), as vertex index pairs.
        // Vertices at the same position (to 0.05 mm) in the same renderer count as one position.
        public static List<int> FindSeams(RasterMesh m)
        {
            int vertices = m.Pos.Length / 3;
            var positionId = new int[vertices];
            var ids = new Dictionary<PositionKey, int>(vertices);
            for (int v = 0; v < vertices; v++)
            {
                var key = new PositionKey
                {
                    X = (int)Math.Round(m.Pos[3 * v] * 20000.0), Y = (int)Math.Round(m.Pos[3 * v + 1] * 20000.0),
                    Z = (int)Math.Round(m.Pos[3 * v + 2] * 20000.0), Group = m.Group[v],
                };
                int id;
                if (!ids.TryGetValue(key, out id)) { id = ids.Count; ids[key] = id; }
                positionId[v] = id;
            }
            // Edge key = the two position ids; the value is the vertex pair of the first usage (low position id first).
            var first = new Dictionary<long, long>(m.TriangleCount * 2);
            var done = new HashSet<long>();
            var seams = new List<int>();
            for (int t = 0; t < m.TriangleCount; t++)
                for (int corner = 0; corner < 3; corner++)
                {
                    int a = m.Tris[3 * t + corner], b = m.Tris[3 * t + (corner + 1) % 3];
                    int pa = positionId[a], pb = positionId[b];
                    if (pa == pb) continue;
                    int lo = pa < pb ? a : b, hi = pa < pb ? b : a;
                    long key = ((long)Math.Min(pa, pb) << 32) | (uint)Math.Max(pa, pb);
                    long other;
                    if (!first.TryGetValue(key, out other)) { first[key] = ((long)lo << 32) | (uint)hi; continue; }
                    if (done.Contains(key)) continue;
                    int oLo = (int)(other >> 32), oHi = (int)(other & 0xFFFFFFFFL);
                    bool same = Math.Abs(m.Uv[2 * lo] - m.Uv[2 * oLo]) < 1e-4f && Math.Abs(m.Uv[2 * lo + 1] - m.Uv[2 * oLo + 1]) < 1e-4f &&
                                Math.Abs(m.Uv[2 * hi] - m.Uv[2 * oHi]) < 1e-4f && Math.Abs(m.Uv[2 * hi + 1] - m.Uv[2 * oHi + 1]) < 1e-4f;
                    if (same) continue;
                    done.Add(key);
                    seams.Add(lo); seams.Add(hi);
                }
            return seams;
        }

        // Seam lines in white, hidden behind nearer surface like the wireframe of analysis.capture.
        public static void DrawSeams(List<int> seams, GBuffer g, RasterProjection pr, Color32[] img)
        {
            int size = g.Size;
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i + 1 < seams.Count; i += 2)
            {
                int a = seams[i], b = seams[i + 1];
                if (!pr.Ok[a] || !pr.Ok[b]) continue;
                float x0 = pr.Sx[a], y0 = pr.Sy[a], z0 = pr.Depth[a], x1 = pr.Sx[b], y1 = pr.Sy[b], z1 = pr.Depth[b];
                int steps = Math.Max(1, Math.Min(4096, (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)))));
                for (int s = 0; s <= steps; s++)
                {
                    float k = (float)s / steps;
                    int px = (int)Math.Round(x0 + (x1 - x0) * k), py = (int)Math.Round(y0 + (y1 - y0) * k);
                    if (px < pr.ClipX0 || py < pr.ClipY0 || px >= pr.ClipX1 || py >= pr.ClipY1) continue;
                    if (z0 + (z1 - z0) * k - 0.004f > g.Depth[py * size + px]) continue;
                    img[py * size + px] = white;
                }
            }
        }

        // ---- colors ------------------------------------------------------------------------------

        static Color32 Hsv(float h, float s, float v)
        {
            float c = v * s, hp = (h - (float)Math.Floor(h)) * 6f, x = c * (1f - Math.Abs(hp % 2f - 1f)), m = v - c;
            float r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
            return Rgb((r + m) * 255f, (g + m) * 255f, (b + m) * 255f);
        }

        public static double ColorDistance(Color32 a, Color32 b)
        {
            double dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);
        }

        // n colors that are far apart: each one is the candidate farthest from every color chosen before it.
        public static Color32[] Palette(int n)
        {
            var candidates = new List<Color32>();
            foreach (float v in new[] { 1.0f, 0.8f, 0.55f })
                foreach (float s in new[] { 1.0f, 0.75f, 0.45f })
                    for (int h = 0; h < 24; h++) candidates.Add(Hsv(h / 24f, s, v));
            var chosen = new List<Color32> { candidates[0] };
            var result = new Color32[n];
            if (n > 0) result[0] = candidates[0];
            var nearest = new double[candidates.Count];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = ColorDistance(candidates[i], candidates[0]);
            for (int k = 1; k < n; k++)
            {
                if (k >= candidates.Count) { result[k] = result[k % candidates.Count]; continue; }
                int best = 0;
                for (int i = 1; i < nearest.Length; i++) if (nearest[i] > nearest[best]) best = i;
                result[k] = candidates[best];
                for (int i = 0; i < nearest.Length; i++) nearest[i] = Math.Min(nearest[i], ColorDistance(candidates[i], candidates[best]));
            }
            return result;
        }

        // ---- images ------------------------------------------------------------------------------

        public static void Copy(Color32[] dst, int dstWidth, Color32[] src, int srcSize, int x0, int y0)
        {
            for (int y = 0; y < srcSize; y++) Array.Copy(src, y * srcSize, dst, (y0 + y) * dstWidth + x0, srcSize);
        }

        // Box filter, for maxSize.
        public static Color32[] Downscale(Color32[] src, int width, int height, int newWidth, int newHeight)
        {
            var result = new Color32[newWidth * newHeight];
            for (int y = 0; y < newHeight; y++)
            {
                int ya = y * height / newHeight, yb = Math.Max(ya + 1, (y + 1) * height / newHeight);
                for (int x = 0; x < newWidth; x++)
                {
                    int xa = x * width / newWidth, xb = Math.Max(xa + 1, (x + 1) * width / newWidth);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = ya; sy < yb; sy++)
                        for (int sx = xa; sx < xb; sx++)
                        {
                            Color32 c = src[sy * width + sx];
                            r += c.r; g += c.g; b += c.b; n++;
                        }
                    result[y * newWidth + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return result;
        }

        // 5x7 bitmap font for labels (upper case, digits, a few symbols).
        static readonly Dictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
        {
            { 'A', new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 } }, { 'B', new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E } },
            { 'C', new byte[] { 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E } }, { 'D', new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E } },
            { 'E', new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F } }, { 'F', new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 } },
            { 'G', new byte[] { 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F } }, { 'H', new byte[] { 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 } },
            { 'I', new byte[] { 0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E } }, { 'J', new byte[] { 0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C } },
            { 'K', new byte[] { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 } }, { 'L', new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F } },
            { 'M', new byte[] { 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11 } }, { 'N', new byte[] { 0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11 } },
            { 'O', new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E } }, { 'P', new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 } },
            { 'Q', new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D } }, { 'R', new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 } },
            { 'S', new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E } }, { 'T', new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 } },
            { 'U', new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E } }, { 'V', new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04 } },
            { 'W', new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A } }, { 'X', new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11 } },
            { 'Y', new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 } }, { 'Z', new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F } },
            { '0', new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E } }, { '1', new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E } },
            { '2', new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F } }, { '3', new byte[] { 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E } },
            { '4', new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 } }, { '5', new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E } },
            { '6', new byte[] { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E } }, { '7', new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 } },
            { '8', new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E } }, { '9', new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C } },
            { '-', new byte[] { 0, 0, 0, 0x1F, 0, 0, 0 } }, { '.', new byte[] { 0, 0, 0, 0, 0, 0x0C, 0x0C } },
            { ':', new byte[] { 0, 0x0C, 0x0C, 0, 0x0C, 0x0C, 0 } }, { '/', new byte[] { 0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10 } },
            { '+', new byte[] { 0, 0x04, 0x04, 0x1F, 0x04, 0x04, 0 } }, { '_', new byte[] { 0, 0, 0, 0, 0, 0, 0x1F } },
            { '=', new byte[] { 0, 0, 0x1F, 0, 0x1F, 0, 0 } }, { '%', new byte[] { 0x19, 0x19, 0x02, 0x04, 0x08, 0x13, 0x13 } },
            { ',', new byte[] { 0, 0, 0, 0, 0x0C, 0x04, 0x08 } }, { '>', new byte[] { 0x10, 0x08, 0x04, 0x02, 0x04, 0x08, 0x10 } },
            { '(', new byte[] { 0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02 } }, { ')', new byte[] { 0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08 } },
        };

        public static int TextWidth(string text, int scale) { return text.Length * 6 * scale - scale; }

        public static void DrawText(Color32[] image, int width, int x, int y, string text, int scale, Color32 color)
        {
            var shadow = new Color32(0, 0, 0, 255);
            int height = image.Length / width;
            foreach (int pass in new[] { 1, 0 })
            {
                int cursor = x + pass;
                foreach (char ch in text)
                {
                    byte[] rows;
                    if (Glyphs.TryGetValue(char.ToUpperInvariant(ch), out rows))
                        for (int row = 0; row < 7; row++)
                            for (int col = 0; col < 5; col++)
                            {
                                if ((rows[row] & (0x10 >> col)) == 0) continue;
                                for (int sy = 0; sy < scale; sy++)
                                    for (int sx = 0; sx < scale; sx++)
                                    {
                                        int px = cursor + col * scale + sx, py = y + pass + row * scale + sy;
                                        if (px >= 0 && py >= 0 && px < width && py < height) image[py * width + px] = pass == 1 ? shadow : color;
                                    }
                            }
                    cursor += 6 * scale;
                }
            }
        }
    }
}
