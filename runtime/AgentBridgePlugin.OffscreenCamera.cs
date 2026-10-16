using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StudioRaming.WarudoAgentBridge
{
    // A throwaway camera that renders the way another camera does, into a RenderTexture, synchronously.
    // camera.capture does not do this: it blits the frame WARUDO already rendered (CameraAsset.CreateTexture reads
    // ProcessedOutputTexture), so it can only show where the user's camera is. This one is placed anywhere and
    // the user's camera never moves.
    //
    // Copied from the source camera: Camera.CopyFrom (projection, clear flags, background, culling mask, clip planes,
    // HDR and MSAA flags), the URP additional camera data (post-processing, volume mask, antialiasing, depth and color
    // texture requests, shadows). Not copied: the Built-in PostProcessLayer and components WARUDO adds for its own
    // look (Beautify, HBAO), so a frame can differ slightly from the screen.
    sealed class OffscreenCamera : IDisposable
    {
        readonly GameObject host;
        readonly Camera source;
        readonly UniversalAdditionalCameraData urp;
        readonly bool sourcePostProcessing;
        public readonly Camera Camera;
        public readonly List<string> Notes = new List<string>();

        public OffscreenCamera(Camera source)
        {
            this.source = source;
            host = new GameObject("WARUDO Agent Bridge offscreen camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera = host.AddComponent<Camera>();
            Camera.CopyFrom(source);
            Camera.targetTexture = null;
            Camera.enabled = false; // rendered only by Render(), never by the pipeline loop
            Camera.tag = "Untagged";
            Camera.rect = new Rect(0, 0, 1, 1);
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                UniversalAdditionalCameraData from = source.GetUniversalAdditionalCameraData();
                urp = Camera.GetUniversalAdditionalCameraData();
                urp.renderType = CameraRenderType.Base;
                urp.renderPostProcessing = from.renderPostProcessing;
                urp.volumeLayerMask = from.volumeLayerMask;
                urp.volumeTrigger = from.volumeTrigger == null || from.volumeTrigger == source.transform ? host.transform : from.volumeTrigger;
                urp.antialiasing = from.antialiasing;
                urp.antialiasingQuality = from.antialiasingQuality;
                urp.requiresDepthTexture = from.requiresDepthTexture;
                urp.requiresColorTexture = from.requiresColorTexture;
                urp.renderShadows = from.renderShadows;
                urp.dithering = from.dithering;
                urp.stopNaN = from.stopNaN;
                sourcePostProcessing = from.renderPostProcessing;
            }
            else
            {
                // WARUDO's Playground compiler does not reference Unity.Postprocessing.Runtime, so the Built-in
                // PostProcessLayer cannot be copied here; Built-in frames come out without post-processing.
                Notes.Add("post-processing not copied (Built-in pipeline)");
            }
        }

        public void Place(Vector3 position, Quaternion rotation, float fieldOfView)
        {
            Camera.transform.SetPositionAndRotation(position, rotation);
            Camera.fieldOfView = fieldOfView;
        }

        // Post-processing as the source camera has it, or off (cheaper, and it keeps alpha for a transparent background).
        void UsePostProcessing(bool on)
        {
            on = on && sourcePostProcessing;
            if (urp != null) urp.renderPostProcessing = on;
        }

        public Color Background { get { return source.backgroundColor; } }
        public CameraClearFlags ClearFlags { get { return source.clearFlags; } }
        public int CullingMask { get { return source.cullingMask; } }

        // One synchronous render. Returns the pixels bottom-up, as ReadPixels does.
        public Color32[] Render(int width, int height, int cullingMask, CameraClearFlags clearFlags, Color background, bool postProcessing, bool keepAlpha)
        {
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                Camera.cullingMask = cullingMask;
                Camera.clearFlags = clearFlags;
                Camera.backgroundColor = background;
                Camera.allowHDR = !keepAlpha && source.allowHDR;
                UsePostProcessing(postProcessing && !keepAlpha);
                Camera.targetTexture = target;
                Camera.aspect = width / (float)height;
                Camera.Render();
                RenderTexture.active = target;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return readback.GetPixels32();
            }
            finally
            {
                Camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        public void Dispose()
        {
            if (host != null) UnityEngine.Object.Destroy(host);
        }
    }

    // Puts some renderers on a free layer for the duration of a few synchronous renders, so a camera can see only
    // them (or everything but them). Lights that would skip the layer are widened for the same time. Disposing
    // restores every layer and light; there must be no await between creating and disposing.
    sealed class LayerIsolation : IDisposable
    {
        public readonly int Layer;
        readonly List<GameObject> objects = new List<GameObject>();
        readonly List<int> layers = new List<int>();
        readonly List<Light> lights = new List<Light>();
        readonly List<int> masks = new List<int>();

        LayerIsolation(int layer) { Layer = layer; }

        // An unnamed layer that no object in the scene uses. WARUDO's own cameras and objects use named layers.
        public static int FindFreeLayer()
        {
            var used = new HashSet<int>();
            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>()) used.Add(t.gameObject.layer);
            for (int layer = 31; layer >= 8; layer--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)) && !used.Contains(layer)) return layer;
            return -1;
        }

        public static LayerIsolation Create(IEnumerable<Renderer> renderers)
        {
            int layer = FindFreeLayer();
            if (layer < 0) return null;
            var isolation = new LayerIsolation(layer);
            try
            {
                foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    isolation.lights.Add(light);
                    isolation.masks.Add(light.cullingMask);
                    light.cullingMask |= 1 << layer;
                }
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || isolation.objects.Contains(renderer.gameObject)) continue;
                    isolation.objects.Add(renderer.gameObject);
                    isolation.layers.Add(renderer.gameObject.layer);
                    renderer.gameObject.layer = layer;
                }
            }
            catch (Exception)
            {
                isolation.Dispose();
                throw;
            }
            return isolation;
        }

        // Moves one more renderer's object onto the layer (and Return puts it back), for one-part-at-a-time renders.
        public void Move(Renderer renderer)
        {
            if (renderer == null || objects.Contains(renderer.gameObject)) return;
            objects.Add(renderer.gameObject);
            layers.Add(renderer.gameObject.layer);
            renderer.gameObject.layer = Layer;
        }

        public void Return(Renderer renderer)
        {
            if (renderer == null) return;
            int index = objects.IndexOf(renderer.gameObject);
            if (index < 0) return;
            if (objects[index] != null) objects[index].layer = layers[index];
            objects.RemoveAt(index); layers.RemoveAt(index);
        }

        public void Dispose()
        {
            for (int i = 0; i < objects.Count; i++) if (objects[i] != null) objects[i].layer = layers[i];
            for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].cullingMask = masks[i];
            objects.Clear(); lights.Clear();
        }
    }
}
