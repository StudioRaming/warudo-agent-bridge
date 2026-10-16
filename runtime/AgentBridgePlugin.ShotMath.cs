using System;
using System.Collections.Generic;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // character.shot: the decisions and math that need no engine, so they can be compiled and checked outside Unity.

    struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator *(V3 a, float k) { return new V3(a.X * k, a.Y * k, a.Z * k); }
        public static float Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public V3 Normalized { get { float l = Length; return l < 1e-9f ? new V3(0, 0, 0) : this * (1f / l); } }
    }

    sealed class ShotCandidate
    {
        public string Id, Name;
        public bool Enabled, Loaded, Selected;
    }

    sealed class SubjectChoice
    {
        public string Error;                    // a code: NO_CHARACTER_ASSET, NOT_A_CHARACTER
        public string Message;
        public ShotCandidate Chosen;
        public List<ShotCandidate> Others = new List<ShotCandidate>(); // the other characters of the scene, shot or not
    }

    sealed class MaskResult
    {
        public int Total, Character, Hidden;
        public float Coverage;       // character pixels / frame pixels
        public float Occlusion;      // hidden character pixels / character pixels
        public float X0, Y0, X1, Y1; // character bounds in the frame, 0..1, origin top-left
        public List<float[]> HiddenSamples = new List<float[]>(); // viewport u, v (origin bottom-left) of some hidden pixels
    }

    static class ShotMath
    {
        // Which character a shot is about. A Character asset in the list always yields a subject; whether it can
        // be shot is answered later (disabled, not loaded, nothing renders), never as "no character".
        public static SubjectChoice PickSubject(string requestedId, List<ShotCandidate> characters, List<string> assetTypes)
        {
            var result = new SubjectChoice();
            if (!string.IsNullOrEmpty(requestedId))
            {
                result.Chosen = characters.Find(c => string.Equals(c.Id, requestedId, StringComparison.OrdinalIgnoreCase));
                if (result.Chosen == null)
                {
                    result.Error = "NOT_A_CHARACTER";
                    result.Message = "assetId " + requestedId + " is not a Character asset. Characters: " + Describe(characters) + ".";
                    return result;
                }
            }
            else if (characters.Count == 0)
            {
                result.Error = "NO_CHARACTER_ASSET";
                result.Message = "The open scene has no Character asset. Asset types present: " + (assetTypes.Count == 0 ? "none" : string.Join(", ", assetTypes)) + ". Add one with warudo_asset_add, or pass assetId of another asset to warudo_analysis_capture.";
                return result;
            }
            else
            {
                // Selected, then the only one, then the first enabled and loaded one, then any enabled one, then the first.
                result.Chosen = characters.Find(c => c.Selected) ?? (characters.Count == 1 ? characters[0] : null) ??
                    characters.Find(c => c.Enabled && c.Loaded) ?? characters.Find(c => c.Enabled) ?? characters[0];
            }
            foreach (ShotCandidate c in characters) if (c != result.Chosen) result.Others.Add(c);
            return result;
        }

        static string Describe(List<ShotCandidate> characters)
        {
            if (characters.Count == 0) return "none";
            return string.Join(", ", characters.ConvertAll(c => c.Name + " (" + c.Id + ")"));
        }

        // Named views as yaw degrees relative to the character's facing (0 = in front, looking at its face,
        // positive = toward the character's right). top has its own pitch.
        public static bool ViewAngles(string name, out float yaw, out float? pitch)
        {
            pitch = null;
            switch (name)
            {
                case "front": yaw = 0; return true;
                case "back": yaw = 180; return true;
                case "left": yaw = -90; return true;
                case "right": yaw = 90; return true;
                case "front-left": yaw = -45; return true;
                case "front-right": yaw = 45; return true;
                case "three-quarter": yaw = 35; return true;
                case "top": yaw = 0; pitch = 85f; return true;
                default: yaw = 0; return false;
            }
        }

        // From the pivot towards the camera. forward is the character's horizontal facing, right the character's right.
        public static V3 OrbitDirection(V3 forward, V3 right, float yawDeg, float pitchDeg)
        {
            double yaw = yawDeg * Math.PI / 180.0, pitch = pitchDeg * Math.PI / 180.0;
            V3 flat = forward * (float)Math.Cos(yaw) + right * (float)Math.Sin(yaw);
            return (flat * (float)Math.Cos(pitch) + new V3(0, 1, 0) * (float)Math.Sin(pitch)).Normalized;
        }

        public static V3[] BoxCorners(V3 center, V3 right, V3 up, V3 forward, float hx, float hy, float hz)
        {
            var corners = new V3[8];
            int i = 0;
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sy in new[] { -1f, 1f })
                    foreach (float sz in new[] { -1f, 1f })
                        corners[i++] = center + right * (sx * hx) + up * (sy * hy) + forward * (sz * hz);
            return corners;
        }

        // Half the width of the body box of a framing that cuts the body (upper, bust): the shoulders plus a small margin, never
        // wider than the frame shape allows (halfHeight * aspect), so the frame is limited by the height of the box and the head
        // does not sit low with empty space above it. The shoulders themselves are always kept; the arms may be cropped.
        public static float TorsoHalfWidth(float shoulderSpan, float bodyHeight, float halfHeight, float aspect, float marginShare, float minShare)
        {
            float wanted = Math.Max(minShare * bodyHeight, shoulderSpan / 2f + marginShare * bodyHeight);
            return Math.Max(shoulderSpan / 2f + 0.01f * bodyHeight, Math.Min(wanted, halfHeight * aspect));
        }

        // The box a cut framing aims at and fits: the look-at point is the center of the box (vertically the middle of yMin..yMax),
        // so the distance fit leaves the same margin above and below.
        public static void FramedBox(V3 anchor, float yMin, float yMax, V3 right, V3 forward, float halfWidth, float halfDepth, out V3 center, out V3[] corners)
        {
            center = new V3(anchor.X, (yMin + yMax) / 2f, anchor.Z);
            corners = BoxCorners(center, right, new V3(0, 1, 0), forward, halfWidth, (yMax - yMin) / 2f, halfDepth);
        }

        // Smallest distance from the pivot, along dirToCamera, at which every corner stays inside fit (0..1) of the frame
        // height and of the frame width (aspect = width / height) for a vertical field of view of fovDeg.
        public static float FitDistance(V3 pivot, V3[] corners, V3 dirToCamera, float fovDeg, float aspect, float fit, float minimum)
        {
            V3 forward = dirToCamera * -1f;
            V3 hint = Math.Abs(forward.Y) > 0.98f ? new V3(0, 0, 1) : new V3(0, 1, 0);
            V3 right = V3.Cross(hint, forward).Normalized;
            V3 up = V3.Cross(forward, right);
            float t = (float)Math.Tan(fovDeg * Math.PI / 360.0) * fit;
            float distance = minimum;
            foreach (V3 corner in corners)
            {
                V3 rel = corner - pivot;
                float x = Math.Abs(V3.Dot(rel, right)), y = Math.Abs(V3.Dot(rel, up)), z = V3.Dot(rel, forward);
                distance = Math.Max(distance, y / t - z);
                distance = Math.Max(distance, x / (t * aspect) - z);
            }
            return distance;
        }

        // The camera at pivot + dir * distance, looking at the pivot. Returns the unit forward and up vectors.
        public static void LookAt(V3 position, V3 target, out V3 forward, out V3 up)
        {
            forward = (target - position).Normalized;
            V3 hint = Math.Abs(forward.Y) > 0.98f ? new V3(0, 0, 1) : new V3(0, 1, 0);
            V3 right = V3.Cross(hint, forward).Normalized;
            up = V3.Cross(forward, right);
        }

        // Compares three renders from the same pose (bottom-up rows, as ReadPixels returns them):
        //   mask:  only the character, on a clear color maskBackground
        //   full:  the whole scene
        //   empty: the scene without the character
        // A pixel is the character where the mask differs from its background. It is hidden where the full render
        // equals the render without the character: something else is in front of it.
        public static MaskResult Compare(Color32[] mask, Color32 maskBackground, Color32[] full, Color32[] empty, int width, int height)
        {
            var result = new MaskResult { Total = width * height };
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            var hidden = new List<int>();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (Difference(mask[i], maskBackground) <= 24) continue;
                    result.Character++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (Difference(full[i], empty[i]) <= 18) { result.Hidden++; hidden.Add(i); }
                }
            result.Coverage = result.Character / (float)result.Total;
            result.Occlusion = result.Character == 0 ? 0f : result.Hidden / (float)result.Character;
            if (result.Character > 0)
            {
                result.X0 = minX / (float)width; result.X1 = (maxX + 1) / (float)width;
                result.Y0 = 1f - (maxY + 1) / (float)height; result.Y1 = 1f - minY / (float)height;
            }
            int step = Math.Max(1, hidden.Count / 16);
            for (int k = 0; k < hidden.Count && result.HiddenSamples.Count < 16; k += step)
                result.HiddenSamples.Add(new[] { (hidden[k] % width + 0.5f) / width, (hidden[k] / width + 0.5f) / height });
            return result;
        }

        static int Difference(Color32 a, Color32 b) { return Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b); }
    }
}
