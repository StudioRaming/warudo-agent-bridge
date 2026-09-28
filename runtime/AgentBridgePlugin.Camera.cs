using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Cinemachine;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Cinematography;
using Warudo.Plugins.Core.Events;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        CameraAsset CameraById(JObject p)
        {
            if (!string.IsNullOrEmpty((string)p["assetId"])) return AssetById(p) as CameraAsset ?? throw new ArgumentException("assetId is not a Camera asset.");
            return Context.PluginManager.GetPlugin<CorePlugin>().MainCameraAsset as CameraAsset ?? throw new InvalidOperationException("No main Camera asset.");
        }
        object DescribeCamera(CameraAsset c)
        {
            Camera camera = c.Camera;
            Camera tagged = Camera.main;
            return new { asset = DescribeAsset(c), mode = c.ControlMode.ToString(), fieldOfView = c.FieldOfView,
                focusCharacterId = c.FocusCharacter == null ? null : c.FocusCharacter.Id.ToString(), followCharacter = c.FollowCharacter,
                orbitRotation = new[] { c.OrbitRotation.x, c.OrbitRotation.y }, orbitOffset = Wire(c.OrbitOffset),
                cameraId = camera == null ? (int?)null : camera.GetInstanceID(), cameraName = camera == null ? null : camera.name,
                worldPosition = camera == null ? null : Wire(camera.transform.position), worldRotation = camera == null ? null : Wire(camera.transform.eulerAngles),
                nearClip = camera == null ? 0 : camera.nearClipPlane, farClip = camera == null ? 0 : camera.farClipPlane,
                aspect = camera == null ? 0 : camera.aspect, outputWidth = camera == null ? 0 : camera.pixelWidth, outputHeight = camera == null ? 0 : camera.pixelHeight,
                taggedMainCameraId = tagged == null ? (int?)null : tagged.GetInstanceID(), taggedMainCameraName = tagged == null ? null : tagged.name,
                taggedMainPosition = tagged == null ? null : Wire(tagged.transform.position) };
        }
        async UniTask<object> CameraOperation(string op, JObject p)
        {
            CameraAsset c = CameraById(p);
            if (op == "camera.get") return DescribeCamera(c);
            if (op == "camera.capture")
            {
                Texture2D image = await c.CreateTexture();
                if (image == null) throw new InvalidOperationException("Camera capture returned no texture.");
                string path = Path.Combine(root, "evidence", activeRequest + ".png");
                int width = image.width, height = image.height;
                try { File.WriteAllBytes(path, image.EncodeToPNG()); }
                finally { UnityEngine.Object.Destroy(image); }
                return new { path, width, height, frame = Time.frameCount, camera = DescribeCamera(c) };
            }
            if (op == "viewport.pick") return Pick(c, p);
            object before = DescribeCamera(c);
            CameraAsset.CameraControlMode? restoreMode = null;
            float[] restoreOrbit = null;
            Vector3? restoreOffset = null;
            if (op == "camera.pose")
            {
                Vector3 position = Vector(p["position"]);
                Quaternion rotation;
                if (p["lookAt"] != null)
                {
                    Vector3 lookAt = Vector(p["lookAt"]);
                    if ((lookAt - position).sqrMagnitude < 0.000001f) throw new ArgumentException("lookAt must differ from position.");
                    rotation = Quaternion.LookRotation(lookAt - position, Vector3.up);
                }
                else if (p["rotation"] != null) rotation = Quaternion.Euler(Vector(p["rotation"]));
                else throw new ArgumentException("camera.pose needs lookAt or rotation.");
                c.SetDataInput("ControlMode", CameraAsset.CameraControlMode.FreeLook, true);
                c.TeleportTo(position, rotation, true);
            }
            else if (op == "camera.orbit")
            {
                if (c.FocusCharacter == null) throw new InvalidOperationException("Orbit requires a focused character.");
                float yaw = (float?)p["yaw"] ?? c.OrbitRotation.x;
                float vertical = (float?)p["vertical"] ?? c.OrbitRotation.y;
                Vector3? offset = p["offset"] == null ? (Vector3?)null : Vector(p["offset"]);
                if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(vertical) || float.IsInfinity(vertical) || vertical < 0 || vertical > 1)
                    throw new ArgumentException("Invalid orbit value.");
                c.SetDataInput("ControlMode", CameraAsset.CameraControlMode.Orbit, true);
                c.SetDataInput("OrbitRotation", new Vector2(yaw, vertical), true);
                if (offset.HasValue) c.SetDataInput("OrbitOffset", offset.Value, true);
            }
            else if (op == "camera.pan")
            {
                Vector3 delta = Vector(p["delta"]);
                if (c.ControlMode == CameraAsset.CameraControlMode.Orbit) c.SetDataInput("OrbitOffset", c.OrbitOffset + delta, true);
                else
                {
                    if (c.Camera == null) throw new InvalidOperationException("Camera asset has no live Camera component.");
                    c.TeleportTo(c.Camera.transform.position + c.Camera.transform.TransformDirection(delta), c.Camera.transform.rotation, true);
                }
            }
            else if (op == "camera.zoom")
            {
                float fov = (float?)p["fieldOfView"] ?? throw new ArgumentException("fieldOfView is required.");
                if (float.IsNaN(fov) || float.IsInfinity(fov) || fov < 10 || fov > 120) throw new ArgumentException("fieldOfView must be10..120.");
                c.SetDataInput("FieldOfView", fov, true);
            }
            else if (op == "camera.restore")
            {
                CameraAsset.CameraControlMode mode;
                if (!Enum.TryParse(Required(p, "mode"), true, out mode) || !Enum.IsDefined(typeof(CameraAsset.CameraControlMode), mode))
                    throw new ArgumentException("Invalid camera mode.");
                Vector3 position = Vector(p["position"]), rotation = Vector(p["rotation"]);
                float fov = (float?)p["fieldOfView"] ?? c.FieldOfView;
                float[] orbitRotation = null;
                Vector3? orbitOffset = null;
                if (p["orbitRotation"] != null)
                {
                    var a = p["orbitRotation"].ToObject<float[]>();
                    if (a == null || a.Length != 2 || a.Any(x => float.IsNaN(x) || float.IsInfinity(x)) || a[1] < 0 || a[1] > 1)
                        throw new ArgumentException("Invalid orbitRotation.");
                    orbitRotation = a;
                }
                if (p["orbitOffset"] != null) orbitOffset = Vector(p["orbitOffset"]);
                if (float.IsNaN(fov) || float.IsInfinity(fov) || fov < 10 || fov > 120) throw new ArgumentException("Invalid fieldOfView.");

                // Validate mode, pose, FOV and all optional orbit values before changing any input.
                c.SetDataInput("ControlMode", mode, true);
                c.SetDataInput("FieldOfView", fov, true);
                if (mode != CameraAsset.CameraControlMode.Orbit)
                    c.TeleportTo(position, Quaternion.Euler(rotation), true);

                // Orbit mode derives the live pose from these two inputs. TeleportTo in Orbit
                // reinitializes Cinemachine axes and overwrites them, so restore the orbit inputs
                // last and leave the frame position to the active follow target.
                if (orbitRotation != null) c.SetDataInput("OrbitRotation", new Vector2(orbitRotation[0], orbitRotation[1]), true);
                if (orbitOffset.HasValue) c.SetDataInput("OrbitOffset", orbitOffset.Value, true);
                restoreMode = mode;
                restoreOrbit = orbitRotation;
                restoreOffset = orbitOffset;
            }
            else throw new ArgumentException("Unsupported camera operation: " + op);
            await UniTask.Yield();
            if (op == "camera.restore" && restoreMode == CameraAsset.CameraControlMode.Orbit && restoreOrbit != null)
            {
                ApplyOrbitRuntimeState(c, restoreOrbit, restoreOffset);
                c.SetDataInput("OrbitRotation", new Vector2(restoreOrbit[0], restoreOrbit[1]), true);
                if (restoreOffset.HasValue) c.SetDataInput("OrbitOffset", restoreOffset.Value, true);
            }
            bool settled = await WaitCameraSettled(c, 3f);
            return new { before, after = DescribeCamera(c), settled, persisted = false };
        }

        // A mode switch makes Cinemachine blend between virtual cameras, and orbit moves are
        // damped, so the rendered view arrives a moment later. Wait for it so a capture made right
        // after a camera move shows the requested view. false: still moving at the timeout
        // (e.g. the camera follows a moving character).
        static async UniTask<bool> WaitCameraSettled(CameraAsset c, float timeoutSeconds)
        {
            Camera camera = c.Camera;
            if (camera == null) return false;
            CinemachineBrain brain = camera.GetComponent<CinemachineBrain>();
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            Vector3 lastPosition = camera.transform.position;
            Quaternion lastRotation = camera.transform.rotation;
            int still = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                await UniTask.Yield();
                if (camera == null) return false;
                Vector3 position = camera.transform.position;
                Quaternion rotation = camera.transform.rotation;
                bool moving = (position - lastPosition).sqrMagnitude > 1e-6f || Quaternion.Angle(rotation, lastRotation) > 0.05f;
                lastPosition = position;
                lastRotation = rotation;
                still = moving || (brain != null && brain.IsBlending) ? 0 : still + 1;
                if (still >= 5) return true;
            }
            return false;
        }
        void ApplyOrbitRuntimeState(CameraAsset camera, float[] orbit, Vector3? offset)
        {
            if (camera.CinemachineFreeLook != null)
            {
                var xAxis = camera.CinemachineFreeLook.m_XAxis;
                xAxis.Value = orbit[0];
                xAxis.m_Recentering.CancelRecentering();
                camera.CinemachineFreeLook.m_XAxis = xAxis;
                var yAxis = camera.CinemachineFreeLook.m_YAxis;
                yAxis.Value = orbit[1];
                yAxis.m_Recentering.CancelRecentering();
                camera.CinemachineFreeLook.m_YAxis = yAxis;
            }
            if (offset.HasValue && camera.CinemachineCameraOffset != null)
                camera.CinemachineCameraOffset.m_Offset = offset.Value;
        }
        object Pick(CameraAsset c, JObject p)
        {
            if (c.Camera == null) throw new InvalidOperationException("Camera asset has no live Camera component.");
            float x = (float?)p["x"] ?? -1, y = (float?)p["y"] ?? -1;
            if (x < 0 || x > 1 || y < 0 || y > 1 || float.IsNaN(x) || float.IsNaN(y)) throw new ArgumentException("x/y must be normalized top-left viewport coordinates0..1.");
            Ray ray = c.Camera.ViewportPointToRay(new Vector3(x, 1-y, 0));
            Renderer selected = null; float nearest = float.MaxValue;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.gameObject.layer == 5 ||
                    (c.Camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                float distance;
                if (renderer.bounds.IntersectRay(ray, out distance) && distance < nearest) { selected = renderer; nearest = distance; }
            }
            if (selected == null) return new { hit = false };
            var asset = Context.OpenedScene.GetAssets().Values.OfType<GameObjectAsset>().Where(a => a.GameObject != null &&
                (selected.transform == a.GameObject.transform || selected.transform.IsChildOf(a.GameObject.transform))).FirstOrDefault();
            if ((bool?)p["select"] != false && asset != null) Context.Service.SetSelectedAsset(asset.Id);
            return new { hit = true, method = "renderer-bounds ray pick", objectId = selected.gameObject.GetInstanceID(),
                name = selected.name, distance = nearest, assetId = asset == null ? null : asset.Id.ToString(), selectedInUi = asset != null && (bool?)p["select"] != false };
        }
        object SendKey(JObject p)
        {
            if ((bool?)p["hostFocused"] != true || !Application.isFocused)
                throw new InvalidOperationException("HOST_FOCUS_REQUIRED: input.key is synthetic EventBus dispatch and is allowed only with caller proof and Application.isFocused=true.");
            Keystroke key;
            if (!Enum.TryParse(Required(p, "key"), true, out key) || !Enum.IsDefined(typeof(Keystroke), key)) throw new ArgumentException("Unknown WARUDO Keystroke.");
            Context.EventBus.Broadcast(new KeystrokePressedEvent(key, (bool?)p["ctrl"] ?? false, (bool?)p["shift"] ?? false,
                (bool?)p["alt"] ?? false, (bool?)p["meta"] ?? false));
            return new { key = key.ToString(), dispatched = true, inputPath = "WARUDO EventBus", physicalKeyboard = false };
        }
    }
}
