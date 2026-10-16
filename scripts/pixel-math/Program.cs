using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using StudioRaming.WarudoAgentBridge;
using UnityEngine;

// Checks the pixel math the capture tools run in the WARUDO runtime (Pixmap.cs, PixelDiff.cs) under plain .NET, with a stub for the few
// UnityEngine types they touch. Run it with: npm run check:pixels
static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static Pixmap Solid(int w, int h, byte r, byte g, byte b)
    {
        var p = new Pixmap(w, h);
        p.Fill(new Color32(r, g, b, 255));
        return p;
    }

    static int Main()
    {
        // Downscale: a 4x4 checker averaged to 2x2 and to 1x1.
        var checker = new Pixmap(4, 4);
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) checker.Pixels[y * 4 + x] = ((x + y) % 2 == 0) ? new Color32(200, 100, 0, 255) : new Color32(0, 0, 0, 255);
        Pixmap half = checker.Downscale(2, 2);
        Check(half.Pixels.All(c => c.r == 100 && c.g == 50 && c.b == 0), "downscale 4x4 checker to 2x2 averages to (100,50,0)");
        Pixmap one = checker.Downscale(1, 1);
        Check(one.Pixels[0].r == 100, "downscale to 1x1");
        // Non-integer ratio keeps a flat color flat.
        Pixmap flat = Solid(7, 5, 10, 20, 30).Downscale(3, 2);
        Check(flat.Pixels.All(c => c.r == 10 && c.g == 20 && c.b == 30), "non-integer downscale of a flat image stays flat");
        // Crop.
        var gradient = new Pixmap(10, 6);
        for (int y = 0; y < 6; y++) for (int x = 0; x < 10; x++) gradient.Pixels[y * 10 + x] = new Color32((byte)x, (byte)y, 0, 255);
        Pixmap crop = gradient.Crop(3, 2, 4, 3);
        Check(crop.Width == 4 && crop.Height == 3 && crop.Pixels[0].r == 3 && crop.Pixels[0].g == 2 && crop.Pixels[11].r == 6 && crop.Pixels[11].g == 4, "crop copies the right window");
        Check(ReferenceEquals(gradient.Crop(0, 0, 10, 6), gradient), "a full crop returns the same pixmap");

        // Diff: two 200x100 frames, two separate changed blocks and one close pair that must merge with gap 8.
        Pixmap a = Solid(200, 100, 50, 50, 50);
        Pixmap b = a.Clone();
        Paint(b, 10, 10, 20, 10, new Color32(255, 0, 0, 255));       // box 1: x 10..29, y 10..19
        Paint(b, 100, 50, 10, 10, new Color32(0, 255, 0, 255));      // box 2 part A: x 100..109
        Paint(b, 115, 50, 10, 10, new Color32(0, 255, 0, 255));      // part B: x 115..124 (gap 5 px from A) -> merged at gap 8, split at gap 2
        Paint(b, 150, 80, 3, 3, new Color32(52, 50, 50, 255));       // change of 2 per channel: under the default threshold 6
        DiffResult d = PixelDiff.Compute(a, b, 6, 8, 32);
        Check(d.ChangedPixels == 20 * 10 + 10 * 10 + 10 * 10, "changed pixel count ignores the sub-threshold change (" + d.ChangedPixels + ")");
        Check(d.Boxes.Count == 2 && d.BoxesTotal == 2, "gap 8 merges the close pair into 2 boxes (" + d.Boxes.Count + ")");
        DiffBox first = d.Boxes[0];
        Check(first.Changed == 200 && first.X == 10 && first.Y == 10 && first.W == 20 && first.H == 10, "largest-first: the 200 px red box is box 1");
        Check(first.Before.r == 50 && first.After.r == 255 && first.After.g == 0, "mean colors before/after of box 1");
        DiffBox second = d.Boxes[1];
        Check(second.X == 100 && second.W == 25 && second.Changed == 200 && second.After.g == 255, "merged box spans both parts: x 100, w 25");
        DiffResult split = PixelDiff.Compute(a, b, 6, 2, 32);
        Check(split.Boxes.Count == 3, "gap 2 keeps the pair apart (" + split.Boxes.Count + " boxes)");
        DiffResult loud = PixelDiff.Compute(a, b, 1, 8, 32);
        Check(loud.ChangedPixels == d.ChangedPixels + 9, "threshold 1 sees the 2-per-channel change too");
        Check(PixelDiff.Compute(a, a, 6, 8, 32).ChangedPixels == 0, "identical frames: nothing changed");
        DiffResult capped = PixelDiff.Compute(a, b, 6, 0, 1);
        Check(capped.Boxes.Count == 1 && capped.BoxesTotal == 3 && capped.OmittedChanged == 200, "max boxes caps the list and counts the rest (" + capped.Boxes.Count + "/" + capped.BoxesTotal + ", omitted " + capped.OmittedChanged + ")");
        // A diagonal line is one component even though its 8x8 blocks only touch at corners.
        Pixmap diag = a.Clone();
        for (int i = 0; i < 64; i++) Paint(diag, i, i, 1, 1, new Color32(255, 255, 255, 255));
        Check(PixelDiff.Compute(a, diag, 6, 0, 32).BoxesTotal == 1, "a diagonal stroke is one box");
        // Render: unchanged grey+dark, changed untouched.
        Pixmap rendered = PixelDiff.Render(b, d);
        Color32 unchanged = rendered.Pixels[0], hot = rendered.Pixels[10 * 200 + 10];
        Check(unchanged.r == unchanged.g && unchanged.g == unchanged.b && unchanged.r < 30 && unchanged.r > 10, "unchanged pixels are dark grey (" + unchanged.r + ")");
        Check(hot.r == 255 && hot.g == 0, "changed pixels keep their color");

        // Probe stats.
        var sample = new Pixmap(4, 1);
        sample.Pixels[0] = new Color32(0, 0, 0, 0);       // alpha 0 must not matter
        sample.Pixels[1] = new Color32(10, 20, 30, 255);
        sample.Pixels[2] = new Color32(10, 20, 30, 255);
        sample.Pixels[3] = new Color32(250, 20, 30, 255);
        JObject s = PixelStats.Describe(sample, 0, 0, 4, 1);
        Check((long)s["samples"] == 4, "probe sample count");
        Check((double)s["mean"][0] == 67.5 && (int)s["median"][0] == 10 && (int)s["min"][0] == 0 && (int)s["max"][0] == 250, "probe mean/median/min/max, alpha ignored: " + s["mean"] + " " + s["median"]);
        JObject red = PixelStats.Describe(Solid(3, 3, 255, 0, 0), 1, 1, 2, 2);
        Check((double)red["hsv"][0] == 0 && (double)red["hsv"][1] == 1 && (double)red["hsv"][2] == 1 && (double)red["luminance"] == 54.2 && (string)red["hex"] == "#ff0000", "pure red HSV/luminance/hex " + red);
        JObject green = PixelStats.Describe(Solid(2, 2, 0, 128, 0), 0, 0, 2, 2);
        Check((double)green["hsv"][0] == 120 && (string)green["hex"] == "#008000", "green hue 120, hex " + green["hex"]);
        JObject blue = PixelStats.Describe(Solid(2, 2, 0, 0, 255), 0, 0, 2, 2);
        Check((double)blue["hsv"][0] == 240, "blue hue 240");

        // Drawing: text, outline, alpha fill, line stay inside the image.
        var canvas = Solid(40, 20, 0, 0, 0);
        int width = canvas.DrawText(1, 1, "AB 12", 1, new Color32(255, 255, 255, 255));
        Check(width == Pixmap.TextWidth("AB 12", 1) && canvas.Pixels.Any(c => c.r == 255), "text draws");
        canvas.Outline(-5, -5, 100, 100, 2, new Color32(9, 9, 9, 255));
        canvas.Line(-10, -10, 100, 50, 3, new Color32(1, 2, 3, 255));
        canvas.FillRect(0, 0, 10, 10, new Color32(200, 200, 200, 255), 64);
        canvas.Cross(20, 10, 6, new Color32(255, 0, 0, 255));
        Check(true, "outline/line/fill/cross outside the bounds do not throw");
        var tint = Solid(4, 4, 100, 100, 100);
        tint.FillRect(0, 0, 4, 4, new Color32(200, 200, 200, 255), 64);
        Check(tint.Pixels[0].r == (100 * 191 + 200 * 64) / 255, "alpha fill blends 25 %");
        // Every glyph in the labels we draw exists (a missing glyph is skipped silently, so check widths only).
        Check(Pixmap.TextWidth("BEFORE", 2) == 6 * 12 - 2, "text width formula");

        // Speed on a 2560x1440 pair (numbers for the report; no assertion).
        var big = new Pixmap(2560, 1440);
        var big2 = new Pixmap(2560, 1440);
        var rng = new Random(1);
        for (int i = 0; i < big.Pixels.Length; i++) { byte v = (byte)rng.Next(256); big.Pixels[i] = new Color32(v, v, v, 255); big2.Pixels[i] = big.Pixels[i]; }
        Paint(big2, 500, 400, 300, 200, new Color32(255, 0, 255, 255));
        Paint(big2, 1500, 900, 400, 100, new Color32(0, 255, 255, 255));
        for (int round = 0; round < 3; round++)
        {
            var sw = Stopwatch.StartNew();
            DiffResult bigDiff = PixelDiff.Compute(big, big2, 6, 8, 32);
            long diffMs = sw.ElapsedMilliseconds; sw.Restart();
            Pixmap img = PixelDiff.Render(big2, bigDiff);
            long renderMs = sw.ElapsedMilliseconds; sw.Restart();
            Pixmap small = img.Downscale(1600, 900);
            long scaleMs = sw.ElapsedMilliseconds; sw.Restart();
            JObject stats = PixelStats.Describe(big, 0, 0, 2560, 1440);
            long probeMs = sw.ElapsedMilliseconds; sw.Restart();
            Pixmap cropBig = big.Crop(100, 100, 1200, 800);
            long cropMs = sw.ElapsedMilliseconds; sw.Restart();
            Pixmap fitted = cropBig.Downscale(1200, 800); // same size = clone
            long cloneMs = sw.ElapsedMilliseconds;
            Console.WriteLine("2560x1440 round " + round + ": diff " + diffMs + " ms (" + bigDiff.Boxes.Count + " boxes), render " + renderMs + " ms, downscale->1600 " + scaleMs + " ms, probe full " + probeMs + " ms, crop " + cropMs + " ms, clone " + cloneMs + " ms");
        }
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void Paint(Pixmap p, int x, int y, int w, int h, Color32 c) { p.FillRect(x, y, w, h, c); }
}
