using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StudioRaming.WarudoAgentBridge
{
    // One frame of virtual input. X/Y are top-left window pixels; NaN means "follow the real mouse".
    struct PointerFrame
    {
        public float X, Y;
        public int Buttons, Down, Up;
        public float Wheel;
        public KeyCode[] Keys, KeysDown, KeysUp;
        public bool HasPosition { get { return !float.IsNaN(X) && !float.IsNaN(Y); } }
    }

    // What stays held between two calls: where the pointer is, which buttons and keys are down.
    sealed class PointerState
    {
        public float X = float.NaN, Y = float.NaN;
        public int Buttons;
        public KeyCode[] Keys = new KeyCode[0];
    }

    // Expands the step list the MCP server sends into one PointerFrame per frame.
    // Keep this in step with expandTimeline() in src/pointer-timeline.mjs, which the tests cover.
    //   step: { f, x?, y?, set?, clear?, keysAdd?, keysDel?, wheel? }, applied in order on top of the previous frame.
    //   plan: { frames, steps, holdKeys?, space? }; holdKeys are down for frames 0..frames-1 only.
    static class PointerTimeline
    {
        public const int MaxFrames = 600;
        static readonly KeyCode[] None = new KeyCode[0];

        public static PointerFrame[] Build(JObject plan, PointerState initial, int screenWidth, int screenHeight, out PointerState post)
        {
            int frames = (int?)plan["frames"] ?? 0;
            if (frames < 1) throw new ArgumentException("INVALID_ARGUMENT: frames must be at least 1.");
            if (frames > MaxFrames) throw new ArgumentException("GESTURE_TOO_LONG: " + frames + " frames; the limit is " + MaxFrames + ".");
            JArray steps = plan["steps"] as JArray ?? throw new ArgumentException("INVALID_ARGUMENT: steps must be an array.");
            if (steps.Count > 2000) throw new ArgumentException("INVALID_ARGUMENT: too many steps.");
            bool normalized = (string)plan["space"] == "normalized";
            KeyCode[] hold = ParseKeys(plan["holdKeys"]);
            var held = new List<KeyCode>(initial.Keys);
            float x = initial.X, y = initial.Y;
            int buttons = initial.Buttons;
            int previousButtons = buttons;
            KeyCode[] previousKeys = initial.Keys;
            var rows = new PointerFrame[frames];
            int next = 0;
            for (int f = 0; f < frames; f++)
            {
                float wheel = 0;
                while (next < steps.Count)
                {
                    JObject step = steps[next] as JObject ?? throw new ArgumentException("INVALID_ARGUMENT: every step must be an object.");
                    int at = (int?)step["f"] ?? throw new ArgumentException("INVALID_ARGUMENT: step.f is required.");
                    if (at < f) throw new ArgumentException("INVALID_ARGUMENT: steps must be sorted by f.");
                    if (at > f) break;
                    if (step["x"] != null || step["y"] != null)
                    {
                        float sx = (float?)step["x"] ?? throw new ArgumentException("INVALID_ARGUMENT: step needs x and y together.");
                        float sy = (float?)step["y"] ?? throw new ArgumentException("INVALID_ARGUMENT: step needs x and y together.");
                        if (float.IsNaN(sx) || float.IsInfinity(sx) || float.IsNaN(sy) || float.IsInfinity(sy)) throw new ArgumentException("INVALID_ARGUMENT: x and y must be finite.");
                        x = normalized ? sx * screenWidth : sx;
                        y = normalized ? sy * screenHeight : sy;
                    }
                    int set = (int?)step["set"] ?? 0, clear = (int?)step["clear"] ?? 0;
                    if ((set | clear) < 0 || (set | clear) > 7) throw new ArgumentException("INVALID_ARGUMENT: button masks are 1 (left), 2 (right), 4 (middle).");
                    buttons = (buttons | set) & ~clear;
                    foreach (KeyCode key in ParseKeys(step["keysAdd"])) if (!held.Contains(key)) held.Add(key);
                    foreach (KeyCode key in ParseKeys(step["keysDel"])) held.Remove(key);
                    wheel += (float?)step["wheel"] ?? 0f;
                    next++;
                }
                KeyCode[] effective = hold.Length == 0 ? held.ToArray() : held.Union(hold).ToArray();
                rows[f] = new PointerFrame
                {
                    X = x, Y = y, Buttons = buttons, Down = buttons & ~previousButtons, Up = previousButtons & ~buttons, Wheel = wheel,
                    Keys = effective, KeysDown = effective.Except(previousKeys).ToArray(), KeysUp = previousKeys.Except(effective).ToArray(),
                };
                previousButtons = buttons;
                previousKeys = effective;
            }
            if (next < steps.Count) throw new ArgumentException("INVALID_ARGUMENT: a step lies beyond the last frame.");

            // Frames before the first known position start where the pointer first lands, so it never jumps there.
            int first = Array.FindIndex(rows, row => row.HasPosition);
            for (int f = 0; f < first; f++) { rows[f].X = rows[first].X; rows[f].Y = rows[first].Y; }
            foreach (PointerFrame row in rows)
                if (row.Down != 0 && !row.HasPosition)
                    throw new InvalidOperationException("NO_POINTER_POSITION: no pointer position is known; pass x and y.");

            post = new PointerState { X = x, Y = y, Buttons = buttons, Keys = held.ToArray() };
            return rows;
        }

        public static KeyCode[] ParseKeys(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return None;
            JArray list = token as JArray ?? throw new ArgumentException("INVALID_ARGUMENT: key lists must be arrays of KeyCode names.");
            if (list.Count > 16) throw new ArgumentException("INVALID_ARGUMENT: at most 16 keys.");
            return list.Select(item =>
            {
                string name = (string)item;
                if (string.IsNullOrWhiteSpace(name) || !Enum.IsDefined(typeof(KeyCode), name))
                    throw new ArgumentException("INVALID_ARGUMENT: unknown KeyCode \"" + name + "\".");
                return (KeyCode)Enum.Parse(typeof(KeyCode), name);
            }).Distinct().ToArray();
        }
    }

    // The input source UGUI reads while the bridge plays a gesture. It lives on a hidden object, replaces the
    // input of the current input module (BaseInputModule.inputOverride) and forces EventSystem focus, because
    // StandaloneInputModule ignores everything while its window is not focused.
    //
    // Plugin contract: read EventSystem.current?.currentInputModule?.input; when it has a public
    // bool GetKey/GetKeyDown/GetKeyUp(KeyCode), use those for keys and modifiers, and treat inputOverride != null as focused.
    [DefaultExecutionOrder(-32000)]
    public sealed class VirtualPointerInput : BaseInput
    {
        static readonly FieldInfo FocusField = typeof(EventSystem).GetField("m_HasFocus", BindingFlags.Instance | BindingFlags.NonPublic);
        const float FeedTimeout = 1.5f;

        PointerFrame[] table = new PointerFrame[0];
        PointerFrame pre = Idle(), post = Idle();
        int start;
        EventSystem eventSystem;
        BaseInputModule module;
        internal bool Active;
        internal bool UserAbort;
        internal float LastFeed;

        internal static bool CanBridgeFocus { get { return FocusField != null; } }
        internal EventSystem CurrentEventSystem { get { return eventSystem; } }
        internal BaseInputModule CurrentModule { get { return module; } }

        static PointerFrame Idle()
        {
            return new PointerFrame { X = float.NaN, Y = float.NaN, Keys = new KeyCode[0], KeysDown = new KeyCode[0], KeysUp = new KeyCode[0] };
        }

        PointerFrame Row()
        {
            int index = Time.frameCount - start;
            if (index < 0) return pre;
            if (index >= table.Length) return post;
            return table[index];
        }

        public override bool mousePresent { get { return true; } }

        public override Vector2 mousePosition
        {
            get
            {
                PointerFrame row = Row();
                return row.HasPosition ? new Vector2(row.X, Screen.height - row.Y) : (Vector2)UnityEngine.Input.mousePosition;
            }
        }

        public override Vector2 mouseScrollDelta { get { return new Vector2(0f, Row().Wheel); } }
        public override bool GetMouseButton(int button) { return button >= 0 && button < 3 && (Row().Buttons & (1 << button)) != 0; }
        public override bool GetMouseButtonDown(int button) { return button >= 0 && button < 3 && (Row().Down & (1 << button)) != 0; }
        public override bool GetMouseButtonUp(int button) { return button >= 0 && button < 3 && (Row().Up & (1 << button)) != 0; }
        public override bool touchSupported { get { return false; } }
        public override int touchCount { get { return 0; } }
        public override Touch GetTouch(int index) { return default(Touch); }
        public override float GetAxisRaw(string axisName) { return 0f; }
        public override bool GetButtonDown(string buttonName) { return false; }

        // Virtual keys, duck-typed by plugins (see the contract above). WARUDO hotkeys do not see them.
        public bool GetKey(KeyCode key) { return Array.IndexOf(Row().Keys, key) >= 0; }
        public bool GetKeyDown(KeyCode key) { return Array.IndexOf(Row().KeysDown, key) >= 0; }
        public bool GetKeyUp(KeyCode key) { return Array.IndexOf(Row().KeysUp, key) >= 0; }

        internal PointerState Snapshot()
        {
            return new PointerState { X = post.X, Y = post.Y, Buttons = post.Buttons, Keys = post.Keys };
        }

        internal Vector2 CurrentPosition { get { PointerFrame row = Row(); return new Vector2(row.X, row.Y); } }

        // Plays rows starting on frame startFrame. Before it the input is the initial state, after it the final one.
        internal void Arm(PointerFrame[] rows, PointerState initial, PointerState after, int startFrame)
        {
            float px = float.IsNaN(initial.X) && rows.Length > 0 ? rows[0].X : initial.X;
            float py = float.IsNaN(initial.Y) && rows.Length > 0 ? rows[0].Y : initial.Y;
            pre = new PointerFrame { X = px, Y = py, Buttons = initial.Buttons, Keys = initial.Keys, KeysDown = new KeyCode[0], KeysUp = new KeyCode[0] };
            post = new PointerFrame { X = after.X, Y = after.Y, Buttons = after.Buttons, Keys = after.Keys, KeysDown = new KeyCode[0], KeysUp = new KeyCode[0] };
            table = rows;
            start = startFrame;
        }

        // Keeps answering with one fixed state (pointer parked, buttons and keys as they were).
        internal void Hold(PointerState state)
        {
            Arm(new PointerFrame[0], state, state, Time.frameCount);
        }

        // Installs this object as the input of the current module and forces EventSystem focus.
        internal void Activate()
        {
            if (FocusField == null) throw new InvalidOperationException("POINTER_UNSUPPORTED: EventSystem.m_HasFocus was not found in this UGUI version, so the window focus cannot be bridged.");
            EventSystem current = EventSystem.current;
            if (current == null) throw new InvalidOperationException("NO_EVENT_SYSTEM: there is no current EventSystem, so no UI is listening.");
            BaseInputModule active = current.currentInputModule;
            if (active == null) throw new InvalidOperationException("NO_INPUT_MODULE: the current EventSystem has no active input module.");
            if (!(active is StandaloneInputModule))
                throw new InvalidOperationException("UNSUPPORTED_INPUT_MODULE: " + active.GetType().FullName + " does not read BaseInputModule.inputOverride; only StandaloneInputModule is supported.");
            Bind(current, active);
            Active = true;
            UserAbort = false;
            LastFeed = Time.realtimeSinceStartup;
        }

        void Bind(EventSystem current, BaseInputModule active)
        {
            if (module != null && module != active && module.inputOverride == this) module.inputOverride = null;
            if (eventSystem != null && eventSystem != current) FocusField.SetValue(eventSystem, Application.isFocused);
            active.inputOverride = this;
            eventSystem = current;
            module = active;
            FocusField.SetValue(current, true);
        }

        // Called every frame by the bridge (and by Update while the bridge keeps feeding): follows the current
        // EventSystem/module and keeps the forced focus. Returns false when the real user clicked.
        internal bool Feed()
        {
            LastFeed = Time.realtimeSinceStartup;
            if (!Active || UserAbort) return false;
            if (UserClicked())
            {
                UserAbort = true;
                return false;
            }
            EventSystem current = EventSystem.current;
            BaseInputModule active = current == null ? null : current.currentInputModule;
            if (active != null && active is StandaloneInputModule && (current != eventSystem || active != module || active.inputOverride != this)) Bind(current, active);
            else if (eventSystem != null) FocusField.SetValue(eventSystem, true);
            return true;
        }

        static bool UserClicked()
        {
            try
            {
                return Application.isFocused && (UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonDown(1) || UnityEngine.Input.GetMouseButtonDown(2));
            }
            catch (Exception) { return false; }
        }

        // Gives the module its real input back and EventSystem its real focus.
        internal void Deactivate()
        {
            if (module != null && module.inputOverride == this) module.inputOverride = null;
            if (eventSystem != null && FocusField != null) FocusField.SetValue(eventSystem, Application.isFocused);
            module = null;
            eventSystem = null;
            Active = false;
        }

        // Self-heal: if the bridge stopped feeding this (plugin destroyed, hot reload, crash), UGUI must not stay on a dead override.
        void Update()
        {
            if (!Active) return;
            if (Time.realtimeSinceStartup - LastFeed > FeedTimeout) { Deactivate(); return; }
            if (UserClicked()) UserAbort = true;
            if (eventSystem != null && FocusField != null) FocusField.SetValue(eventSystem, true);
        }

        protected override void OnDestroy()
        {
            Deactivate();
            base.OnDestroy();
        }
    }
}
