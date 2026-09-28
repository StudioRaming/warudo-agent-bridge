using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        const int ScreenCaptureMinDimension = 16;
        const int ScreenCaptureMaxWidth = 7680;
        const int ScreenCaptureMaxHeight = 4320;
        const long ScreenCaptureMaxPixels = 16777216L;
        const int ScreenCaptureMaxPngBytes = 64 * 1024 * 1024;

        async UniTask<object> ScreenOperation(string op, JObject p)
        {
            if (op != "screen.capture") throw new ArgumentException("Unsupported screen operation: " + op);
            int superSize = OptionalSuperSize(p);
            int timeoutMs = OptionalTimeout(p);
            float started = Time.realtimeSinceStartup;
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");

            int width;
            int height;
            byte[] png;
            using (var cancellation = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    // Wait until cameras and Screen Space Overlay UI have rendered.
                    Context runner = Context.Instance;
                    if (runner == null) throw new InvalidOperationException("SCREEN_CAPTURE_UNAVAILABLE: WARUDO frame runner is not available.");
                    await UniTask.WaitForEndOfFrame(runner, cancellation.Token);
                    width = Screen.width;
                    height = Screen.height;
                    ValidateDimensions(width, height, superSize);

                    RenderTexture previous = RenderTexture.active;
                    Texture2D image = null;
                    try
                    {
                        // A null active RenderTexture makes ReadPixels read the final GameView backbuffer.
                        RenderTexture.active = null;
                        image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        image.Apply(false, false);
                        png = image.EncodeToPNG();
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        if (image != null) UnityEngine.Object.Destroy(image);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("SCREEN_CAPTURE_TIMEOUT: EndOfFrame or backbuffer readback exceeded the requested timeout.");
                }
            }

            if (png == null || png.Length == 0 || png.Length > ScreenCaptureMaxPngBytes)
                throw new InvalidOperationException("SCREEN_CAPTURE_TOO_LARGE: encoded PNG is empty or exceeds the64 MiB guard.");
            WriteEvidencePng(path, png);
            if (!HasCompletePng(path))
            {
                TryDelete(path);
                throw new InvalidOperationException("SCREEN_CAPTURE_FAILED: PNG signature or IEND chunk is incomplete.");
            }

            return new
            {
                path,
                width,
                height,
                outputWidth = width,
                outputHeight = height,
                superSize,
                bytes = png.Length,
                frame = Time.frameCount,
                frameWaited = true,
                frameWaitMs = Math.Round((Time.realtimeSinceStartup - started) * 1000f, 3),
                captureApi = "UnityEngine.Texture2D.ReadPixels(backbuffer) + EncodeToPNG",
                includesScreenSpaceOverlay = true,
                cameraChanged = false,
                sceneChanged = false,
                managedTextureAllocated = true,
                managedTextureDestroyed = true
            };
        }

        static int OptionalSuperSize(JObject p)
        {
            if (p["superSize"] == null) return 1;
            int value;
            try { value = (int)p["superSize"]; }
            catch { throw new ArgumentException("superSize must be an integer."); }
            if (value != 1) throw new ArgumentException("superSize currently supports1 only for exact GameView readback.");
            return value;
        }

        static int OptionalTimeout(JObject p)
        {
            if (p["timeoutMs"] == null) return 2000;
            int value;
            try { value = (int)p["timeoutMs"]; }
            catch { throw new ArgumentException("timeoutMs must be an integer."); }
            if (value < 100 || value > 10000) throw new ArgumentException("timeoutMs must be100..10000.");
            return value;
        }

        static void ValidateDimensions(int width, int height, int superSize)
        {
            if (width < ScreenCaptureMinDimension || height < ScreenCaptureMinDimension)
                throw new ArgumentException("GameView dimensions must be at least16x16.");
            long outputWidth = (long)width * superSize;
            long outputHeight = (long)height * superSize;
            if (outputWidth > ScreenCaptureMaxWidth || outputHeight > ScreenCaptureMaxHeight)
                throw new ArgumentException("Requested screen capture exceeds7680x4320.");
            if (outputWidth * outputHeight > ScreenCaptureMaxPixels)
                throw new ArgumentException("Requested screen capture exceeds the16 megapixel guard.");
        }

        string ScreenEvidencePath()
        {
            string evidence = Path.GetFullPath(Path.Combine(root, "evidence"));
            Directory.CreateDirectory(evidence);
            string path = Path.GetFullPath(Path.Combine(evidence, activeRequest + ".png"));
            string prefix = evidence.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).ToLowerInvariant() != ".png")
                throw new InvalidOperationException("EVIDENCE_PATH_REJECTED: capture output must remain under runtime/evidence and use PNG.");
            return path;
        }

        static void WriteEvidencePng(string path, byte[] data)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        static bool HasCompletePng(string path)
        {
            byte[] header = new byte[8];
            byte[] tail = new byte[12];
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length < 20 || stream.Read(header, 0, header.Length) != header.Length) return false;
                stream.Seek(-12, SeekOrigin.End);
                if (stream.Read(tail, 0, tail.Length) != tail.Length) return false;
            }
            return header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71 &&
                header[4] == 13 && header[5] == 10 && header[6] == 26 && header[7] == 10 &&
                tail[0] == 0 && tail[1] == 0 && tail[2] == 0 && tail[3] == 0 &&
                tail[4] == 73 && tail[5] == 69 && tail[6] == 78 && tail[7] == 68;
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }
}
