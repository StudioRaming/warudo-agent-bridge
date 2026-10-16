using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StudioRaming.WarudoAgentBridge
{
    // Virtual pointer: plays a frame-stamped mouse/key timeline into WARUDO's UGUI through BaseInputModule.inputOverride,
    // so the main window can be clicked and dragged while it is not focused. No OS input is involved.
    // The MCP server builds the timeline (src/pointer-timeline.mjs); this side only plays it and reports what was under the pointer.
    public sealed partial class AgentBridgePlugin
    {
        const float PointerPlayTimeoutSeconds = 50f;

        VirtualPointerInput pointerDriver;
        string pointerOwner;
        float pointerLastCall;
        bool pointerPlaying, pointerReleasing;

        bool PointerActive { get { return pointerDriver != null && pointerDriver.Active; } }

        async UniTask<object> PointerOperation(string op, JObject p, string owner)
        {
            switch (op)
            {
                case "pointer.play": return await PointerPlay(p, owner);
                case "pointer.release": return await PointerReleaseOperation();
                case "pointer.state": return PointerStateInfo();
                default: throw new ArgumentException("Unsupported pointer operation: " + op);
            }
        }

        void RequirePointerEnabled()
        {
            RefreshConfig();
            if (!pointerPolicy.Enabled) throw new InvalidOperationException("DISABLED: pointer.enabled is false in config.json.");
        }

        VirtualPointerInput EnsurePointerDriver()
        {
            if (pointerDriver != null) return pointerDriver;
            var holder = new GameObject("WARUDO Agent Bridge Virtual Pointer") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(holder);
            pointerDriver = holder.AddComponent<VirtualPointerInput>();
            return pointerDriver;
        }

        // Runs every frame from OnUpdate. Keeps a parked pointer fed and ends it when nobody needs it any more.
        void PointerTick()
        {
            if (!PointerActive) return;
            // A play or release feeds the driver itself; this only keeps it alive across the awaits in between (capture).
            if (pointerPlaying || pointerReleasing) { pointerDriver.LastFeed = Time.realtimeSinceStartup; return; }
            if (!pointerDriver.Feed()) { ReleasePointer(pointerDriver.UserAbort ? "user input" : "driver stopped").Forget(); return; }
            if (DateTime.UtcNow >= leaseUntil || leaseOwner != pointerOwner) ReleasePointer("lease ended").Forget();
            else if ((Time.realtimeSinceStartup - pointerLastCall) * 1000.0 > pointerPolicy.IdleReleaseMs) ReleasePointer("idle timeout").Forget();
        }

        // Gives UGUI its real input back. Held buttons and keys are let go first (one Up frame), or UGUI would keep a drag open.
        async UniTask<bool> ReleasePointer(string reason)
        {
            VirtualPointerInput driver = pointerDriver;
            if (driver == null || !driver.Active || pointerReleasing) return false;
            pointerReleasing = true;
            try
            {
                PointerState held = driver.Snapshot();
                if (held.Buttons != 0 || held.Keys.Length != 0)
                {
                    var step = new JObject { ["f"] = 0, ["clear"] = 7, ["keysDel"] = new JArray(held.Keys.Select(key => key.ToString())) };
                    var plan = new JObject { ["frames"] = 2, ["steps"] = new JArray(step) };
                    PointerState after;
                    PointerFrame[] rows = PointerTimeline.Build(plan, held, Screen.width, Screen.height, out after);
                    int startFrame = Time.frameCount + 1;
                    driver.Arm(rows, held, after, startFrame);
                    while (Time.frameCount < startFrame + 2) await UniTask.Yield();
                }
                driver.Deactivate();
            }
            finally
            {
                pointerReleasing = false;
                pointerOwner = null;
            }
            Debug.Log("[WARUDO Agent Bridge] Virtual pointer released: " + reason);
            return true;
        }

        void DestroyPointer()
        {
            if (pointerDriver == null) return;
            try
            {
                pointerDriver.Deactivate();
                UnityEngine.Object.Destroy(pointerDriver.gameObject);
            }
            catch (Exception) { }
            pointerDriver = null;
        }

        async UniTask<object> PointerReleaseOperation()
        {
            while (pointerReleasing) await UniTask.Yield();
            bool was = PointerActive;
            PointerState held = was ? pointerDriver.Snapshot() : null;
            if (was) await ReleasePointer("pointer.release");
            pointerLastCall = Time.realtimeSinceStartup;
            return new { released = was, heldButtons = held == null ? new string[0] : ButtonNames(held.Buttons), heldKeys = held == null ? new string[0] : held.Keys.Select(key => key.ToString()).ToArray(), applicationFocused = Application.isFocused };
        }

        async UniTask<object> PointerPlay(JObject p, string owner)
        {
            RequirePointerEnabled();
            while (pointerReleasing) await UniTask.Yield();
            VirtualPointerInput driver = EnsurePointerDriver();
            PointerState initial = driver.Active ? driver.Snapshot() : new PointerState();
            int screenWidth = Screen.width, screenHeight = Screen.height;
            PointerState post;
            PointerFrame[] rows = PointerTimeline.Build(p, initial, screenWidth, screenHeight, out post);
            bool keep = ((bool?)p["keep"] ?? false) || post.Buttons != 0 || post.Keys.Length != 0;
            int settle = Mathf.Clamp((int?)p["settleFrames"] ?? 2, 0, 60);
            bool capture = ((bool?)p["capture"] ?? false) || ((bool?)p["captureBefore"] ?? false);
            // A bad capture option or an unframeable target must fail before the gesture runs, not after it clicked.
            CaptureOptions captureOptions = capture ? CaptureOptions.Parse(p) : null;
            if (capture) ResolveCrop(captureOptions, screenWidth, screenHeight);
            int? expectId = (int?)p["expectObject"];
            int pressRow = Array.FindIndex(rows, row => row.Down != 0);
            int releaseRow = Array.FindLastIndex(rows, row => row.Up != 0);

            GameObject expected = null;
            if (expectId.HasValue)
            {
                if (pressRow < 0) throw new ArgumentException("INVALID_ARGUMENT: expectObject needs a gesture that presses a button.");
                expected = FindUiObject(expectId.Value) ?? throw new ArgumentException("UI_NOT_FOUND: expectObject " + expectId.Value + " is not an active UI object; refresh ui.list.");
                RequireNotCovered(expected, new Vector2(rows[pressRow].X, rows[pressRow].Y));
            }

            pointerPlaying = true;
            pointerOwner = owner;
            bool completed = false;
            float started = Time.realtimeSinceStartup;
            try
            {
                // captureBefore: the frame as it is before anything moves, for the BEFORE / AFTER sheet.
                GrabbedFrame? beforeFrame = null;
                if (captureOptions != null && captureOptions.Before) beforeFrame = await GrabFrame(OptionalTimeout(p));
                driver.Activate();
                int startFrame = Time.frameCount + 1;
                driver.Arm(rows, initial, post, startFrame);
                int until = startFrame + rows.Length + settle;
                // The hit under the pointer is read on the frame before the press and the release, so a covered target is caught before the press.
                int pressCheck = pressRow < 0 ? int.MaxValue : startFrame + pressRow - 1;
                int releaseCheck = releaseRow < 0 ? int.MaxValue : startFrame + releaseRow - 1;
                object pressHit = null, releaseHit = null;
                bool pressChecked = false, releaseChecked = false;
                while (true)
                {
                    int frame = Time.frameCount;
                    if (!driver.Feed())
                        throw new InvalidOperationException(driver.UserAbort
                            ? "USER_INPUT: the user clicked in WARUDO, so the virtual pointer was released. Run the gesture again when they are done."
                            : "RELEASED: the virtual pointer was released while the gesture ran (lease ended, pointer.release, or the bridge reloaded).");
                    if (!pressChecked && frame >= pressCheck)
                    {
                        var at = new Vector2(rows[pressRow].X, rows[pressRow].Y);
                        if (expected != null) RequireNotCovered(expected, at);
                        pressHit = DescribeHitAt(at);
                        pressChecked = true;
                    }
                    if (!releaseChecked && frame >= releaseCheck)
                    {
                        releaseHit = DescribeHitAt(new Vector2(rows[releaseRow].X, rows[releaseRow].Y));
                        releaseChecked = true;
                    }
                    if (frame >= until) break;
                    if (Time.realtimeSinceStartup - started > PointerPlayTimeoutSeconds)
                        throw new TimeoutException("PLAY_TIMEOUT: the gesture needed more than " + PointerPlayTimeoutSeconds + " s (WARUDO runs at a very low frame rate).");
                    await UniTask.Yield();
                }

                // The capture is taken while the virtual pointer is still parked, so hover and pressed looks are in it.
                object captured = capture ? await CaptureGesture(p, captureOptions, GestureMarks.From(rows, pressRow, releaseRow, (string)p["gesture"]), beforeFrame) : null;
                completed = true;
                EventSystem current = EventSystem.current;
                GameObject selected = current == null ? null : current.currentSelectedGameObject;
                return new
                {
                    gesture = (string)p["gesture"],
                    screen = new { width = screenWidth, height = screenHeight },
                    frames = rows.Length,
                    startFrame,
                    endFrame = Time.frameCount,
                    module = driver.CurrentModule == null ? null : driver.CurrentModule.GetType().FullName,
                    press = pressRow < 0 ? null : new { frame = startFrame + pressRow, position = new[] { rows[pressRow].X, rows[pressRow].Y }, hit = pressHit },
                    release = releaseRow < 0 ? null : new { frame = startFrame + releaseRow, position = new[] { rows[releaseRow].X, rows[releaseRow].Y }, hit = releaseHit },
                    selected = selected == null ? null : ObjectPath(selected.transform),
                    aborted = false,
                    kept = keep,
                    capture = captured,
                };
            }
            finally
            {
                pointerPlaying = false;
                pointerLastCall = Time.realtimeSinceStartup;
                if (completed && keep && driver.Active) driver.Hold(post);
                else await ReleasePointer("gesture finished");
                if (!PointerActive) pointerOwner = null;
            }
        }

        object PointerStateInfo()
        {
            RefreshConfig();
            VirtualPointerInput driver = pointerDriver;
            bool active = PointerActive;
            EventSystem current = EventSystem.current;
            BaseInputModule module = current == null ? null : current.currentInputModule;
            PointerState held = active ? driver.Snapshot() : null;
            bool hasPosition = held != null && !float.IsNaN(held.X) && !float.IsNaN(held.Y);
            GameObject selected = current == null ? null : current.currentSelectedGameObject;
            return new
            {
                enabled = pointerPolicy.Enabled,
                focusBridgeAvailable = VirtualPointerInput.CanBridgeFocus,
                active,
                playing = pointerPlaying,
                owner = active ? pointerOwner : null,
                heldButtons = held == null ? new string[0] : ButtonNames(held.Buttons),
                heldKeys = held == null ? new string[0] : held.Keys.Select(key => key.ToString()).ToArray(),
                position = hasPosition ? new[] { held.X, held.Y } : null,
                focusForced = active,
                applicationFocused = Application.isFocused,
                screen = new { width = Screen.width, height = Screen.height },
                eventSystem = current == null ? null : current.name,
                module = module == null ? null : module.GetType().FullName,
                overrideInstalled = module != null && module.inputOverride != null,
                hovered = hasPosition ? DescribeHitAt(new Vector2(held.X, held.Y)) : null,
                selected = selected == null ? null : ObjectPath(selected.transform),
                idleReleaseMs = pointerPolicy.IdleReleaseMs,
                idleForMs = active ? Math.Round((Time.realtimeSinceStartup - pointerLastCall) * 1000.0) : (double?)null,
            };
        }

        static string[] ButtonNames(int mask)
        {
            var names = new List<string>();
            if ((mask & 1) != 0) names.Add("left");
            if ((mask & 2) != 0) names.Add("right");
            if ((mask & 4) != 0) names.Add("middle");
            return names.ToArray();
        }

        // Everything under a point, front first, as the EventSystem would raycast it. topLeft is in top-left window pixels.
        static List<RaycastResult> UiRaycast(Vector2 topLeft)
        {
            var results = new List<RaycastResult>();
            EventSystem current = EventSystem.current;
            if (current == null) return results;
            var data = new PointerEventData(current) { position = new Vector2(topLeft.x, Screen.height - topLeft.y) };
            current.RaycastAll(data, results);
            results.RemoveAll(hit => hit.gameObject == null);
            return results;
        }

        object DescribeRaycast(RaycastResult hit)
        {
            Canvas canvas = hit.gameObject.GetComponentInParent<Canvas>();
            Canvas root = canvas == null ? null : canvas.rootCanvas;
            return new
            {
                objectId = hit.gameObject.GetInstanceID(),
                path = ObjectPath(hit.gameObject.transform),
                module = hit.module == null ? null : hit.module.GetType().Name,
                depth = hit.depth,
                sortingLayer = hit.sortingLayer,
                sortingOrder = hit.sortingOrder,
                distance = hit.distance,
                canvas = root == null ? null : root.name,
                canvasSortingOrder = root == null ? (int?)null : root.sortingOrder,
            };
        }

        static bool IsPointerHandler(Component component)
        {
            return component is IPointerClickHandler || component is IPointerDownHandler || component is IDragHandler || component is IScrollHandler;
        }

        static string[] HandlerTypeNames(GameObject target)
        {
            return target.GetComponents<Component>().Where(component => component != null && IsPointerHandler(component)).Select(component => component.GetType().Name).Distinct().ToArray();
        }

        object DescribeHandler(GameObject handler)
        {
            return handler == null ? null : new { objectId = handler.GetInstanceID(), path = ObjectPath(handler.transform), types = HandlerTypeNames(handler) };
        }

        // The objects that would get a click, a drag or a wheel turn at the hit, walking up from it like UGUI does.
        object DescribeHandlers(GameObject hit)
        {
            return new
            {
                click = DescribeHandler(ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit)),
                drag = DescribeHandler(ExecuteEvents.GetEventHandler<IDragHandler>(hit)),
                scroll = DescribeHandler(ExecuteEvents.GetEventHandler<IScrollHandler>(hit)),
            };
        }

        object DescribeHitAt(Vector2 topLeft)
        {
            List<RaycastResult> hits = UiRaycast(topLeft);
            return new
            {
                x = topLeft.x,
                y = topLeft.y,
                hitCount = hits.Count,
                top = hits.Count == 0 ? null : DescribeRaycast(hits[0]),
                handlers = hits.Count == 0 ? null : DescribeHandlers(hits[0].gameObject),
            };
        }

        void RequireNotCovered(GameObject expected, Vector2 topLeft)
        {
            List<RaycastResult> hits = UiRaycast(topLeft);
            if (hits.Count > 0 && (hits[0].gameObject == expected || hits[0].gameObject.transform.IsChildOf(expected.transform))) return;
            string top = hits.Count == 0
                ? "nothing"
                : ObjectPath(hits[0].gameObject.transform) + " (" + (hits[0].module == null ? "?" : hits[0].module.GetType().Name) + ", sorting order " + hits[0].sortingOrder + ")";
            throw new InvalidOperationException("COVERED: " + top + " is on top at (" + Math.Round(topLeft.x, 1) + ", " + Math.Round(topLeft.y, 1) + "), not " + ObjectPath(expected.transform) + ".");
        }

        // An active UI object by instance id, across every active canvas (ObjectById only sees loaded scenes).
        static GameObject FindUiObject(int id)
        {
            foreach (Canvas canvas in ActiveRootCanvases())
            {
                foreach (Transform child in canvas.GetComponentsInChildren<Transform>(false))
                    if (child.gameObject.GetInstanceID() == id) return child.gameObject;
            }
            return null;
        }
    }
}
