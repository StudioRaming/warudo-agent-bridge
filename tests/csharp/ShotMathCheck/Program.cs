using System;
using StudioRaming.WarudoAgentBridge;

// Prints one line per check: "PASS name" or "FAIL name: detail". Exit code = number of failures.
static class Program
{
    static int failures;
    static void Check(bool ok, string name, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length == 0 ? "" : (ok ? " (" : ": ") + detail + (ok ? ")" : ""))); if (!ok) failures++; }

    // Space above and below the box in the picture, as a share of the frame height.
    static void Margins(V3 center, V3[] corners, float fovDeg, float aspect, float fit, out float above, out float below)
    {
        V3 dir = ShotMath.OrbitDirection(new V3(0, 0, 1), new V3(1, 0, 0), 0, 0);
        float distance = ShotMath.FitDistance(center, corners, dir, fovDeg, aspect, fit, 0.3f);
        V3 position = center + dir * distance, forward, up;
        ShotMath.LookAt(position, center, out forward, out up);
        float half = (float)Math.Tan(fovDeg * Math.PI / 360.0);
        float top = float.MinValue, bottom = float.MaxValue;
        foreach (V3 corner in corners)
        {
            V3 rel = corner - position;
            float y = V3.Dot(rel, up) / (V3.Dot(rel, forward) * half); // -1..1 over the frame height
            top = Math.Max(top, y); bottom = Math.Min(bottom, y);
        }
        above = (1f - top) / 2f; below = (1f + bottom) / 2f;
    }

    static int Main()
    {
        // MAYO from WARUDO: body 1.32 high, the upper box runs from 0.58 to 1.00 of it, shoulders 0.26 apart, portrait 3:4, fov 20, fit 0.85.
        float height = 1.32f, yMin = 0.58f * height, yMax = height, shoulders = 0.26f, aspect = 0.75f, fov = 20f, fit = 0.85f;
        V3 right = new V3(1, 0, 0), forward = new V3(0, 0, 1);
        float halfWidth = ShotMath.TorsoHalfWidth(shoulders, height, (yMax - yMin) / 2f, aspect, 0.03f, 0.12f);
        V3 center; V3[] corners;
        ShotMath.FramedBox(new V3(0.2f, 0, -0.1f), yMin, yMax, right, forward, halfWidth, 0.15f * height, out center, out corners);
        Check(Math.Abs(center.Y - (yMin + yMax) / 2f) < 1e-5f, "the look-at point is the vertical center of the framed box");
        Check(Math.Abs(center.X - 0.2f) < 1e-5f && Math.Abs(center.Z + 0.1f) < 1e-5f, "the horizontal anchor is kept");
        float above, below;
        Margins(center, corners, fov, aspect, fit, out above, out below);
        Check(Math.Abs(above - below) <= 0.02f, "upper: space above and below the box is equal within 2 % of the frame height", "above " + above.ToString("F3") + " below " + below.ToString("F3"));
        Check(above <= 0.10f, "upper: the box is limited by its height, so the margin is about (1 - fit) / 2, not a width-limited gap", "margin " + above.ToString("F3"));

        // The old shape (at least 0.20 of the height wide, plus 0.07) was limited by width and left a large gap.
        float oldHalfWidth = Math.Max(0.20f * height, shoulders / 2f + 0.07f * height);
        V3 oldCenter; V3[] oldCorners;
        ShotMath.FramedBox(new V3(0, 0, 0), yMin, yMax, right, forward, oldHalfWidth, 0.15f * height, out oldCenter, out oldCorners);
        Margins(oldCenter, oldCorners, fov, aspect, fit, out above, out below);
        Check(above > 0.15f, "control: the old width-limited box leaves more than 15 % empty above the head", "margin " + above.ToString("F3"));

        // An anchor at (hips + head) / 2 instead of the box center would show as unequal margins.
        V3 shifted = new V3(0, (0.58f * height + 0.82f * height) / 2f, 0);
        Margins(shifted, corners, fov, aspect, fit, out above, out below);
        Check(Math.Abs(above - below) > 0.02f, "control: aiming at (hips + head) / 2 gives unequal margins", "above " + above.ToString("F3") + " below " + below.ToString("F3"));

        // Bust (shoulders to top) on a 16:9 camera: still centered, never width-limited.
        float bustMin = 0.77f * height - 0.07f * height;
        float bustWidth = ShotMath.TorsoHalfWidth(shoulders, height, (yMax - bustMin) / 2f, 16f / 9f, 0.02f, 0.10f);
        ShotMath.FramedBox(new V3(0, 0, 0), bustMin, yMax, right, forward, bustWidth, 0.12f * height, out center, out corners);
        Margins(center, corners, fov, 16f / 9f, fit, out above, out below);
        Check(Math.Abs(above - below) <= 0.02f && above <= 0.10f, "bust: centered and height-limited", "above " + above.ToString("F3") + " below " + below.ToString("F3"));

        // The shoulders are never cropped, even when the frame shape asks for less.
        float narrow = ShotMath.TorsoHalfWidth(0.6f, height, 0.1f, 0.75f, 0.03f, 0.12f);
        Check(narrow >= 0.3f, "the shoulders always fit", "half width " + narrow.ToString("F3"));
        return failures;
    }
}
