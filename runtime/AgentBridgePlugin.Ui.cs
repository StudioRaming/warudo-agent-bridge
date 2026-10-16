using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudioRaming.WarudoAgentBridge
{
    // Reads of WARUDO's main-window UGUI (what can be clicked, what is under a point) and text entry for input fields.
    // Pairs with the pointer operations: ui.list gives ids and rectangles, pointer.play clicks them.
    public sealed partial class AgentBridgePlugin
    {
        const int UiLabelDepth = 2;
        const int UiLabelChildren = 24;
        const int UiLabelLength = 80;

        static readonly Vector3[] UiCorners = new Vector3[4];
        static readonly List<CanvasGroup> UiGroups = new List<CanvasGroup>();
        static readonly Dictionary<Type, PropertyInfo> UiTextProperties = new Dictionary<Type, PropertyInfo>();

        sealed class UiElement
        {
            public GameObject Target;
            public Canvas Root;
            public Selectable Selectable;
            public readonly List<string> Handlers = new List<string>();
            public Rect Rect;
            public bool OnScreen, Interactable;
            public string Path, Label;
            public bool LabelDone;
            public string Kind;
        }

        object UiOperation(string op, JObject p)
        {
            switch (op)
            {
                case "ui.list": return UiList(p);
                case "ui.at": return UiAt(p);
                case "ui.set_text": return UiSetText(p);
                default: throw new ArgumentException("Unsupported ui operation: " + op);
            }
        }

        object UiList(JObject p)
        {
            string query = ((string)p["query"] ?? "").Trim();
            bool interactableOnly = (bool?)p["interactableOnly"] ?? true;
            int? onlyId = (int?)p["id"];
            int offset = Math.Max(0, (int?)p["offset"] ?? 0), limit = Mathf.Clamp((int?)p["limit"] ?? 100, 1, 500);
            int width = Screen.width, height = Screen.height;
            int[] region = p["region"] == null || p["region"].Type == JTokenType.Null ? null : CaptureOptions.ParseRegion(p["region"], "region");

            UiElement[] matches = FilterUiElements(width, height, query, interactableOnly, onlyId, region).ToArray();
            return new
            {
                screen = new { width, height },
                total = matches.Length,
                offset,
                limit,
                truncated = matches.Length > offset + limit,
                items = matches.Skip(offset).Take(limit).Select(DescribeUiElement).ToArray(),
            };
        }

        // The elements ui.list and ui.capture agree on: the same filters, in the same (frontmost canvas first) order.
        // region [x, y, w, h] keeps the elements whose rectangle overlaps it.
        IEnumerable<UiElement> FilterUiElements(int width, int height, string query, bool interactableOnly, int? onlyId, int[] region)
        {
            IEnumerable<UiElement> elements = CollectUiElements(width, height);
            if (onlyId.HasValue) elements = elements.Where(element => element.Target.GetInstanceID() == onlyId.Value);
            if (interactableOnly) elements = elements.Where(element => element.Interactable);
            if (region != null)
            {
                var area = new Rect(region[0], region[1], region[2], region[3]);
                elements = elements.Where(element => element.Rect.Overlaps(area));
            }
            if (query.Length > 0)
                elements = elements.Where(element =>
                    (UiLabelOf(element) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    UiPathOf(element).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            return elements;
        }

        // button, toggle, slider, input, scroll or other: from the components first (InputField > Toggle > Slider/Scrollbar > ScrollRect > Button),
        // then from the handlers (a drag-only control is a slider, a wheel handler a scroll area, a click handler a button).
        static string UiKindOf(UiElement element)
        {
            if (element.Kind != null) return element.Kind;
            bool input = false, toggle = false, slider = false, scroll = false, button = false, scrollHandler = false, drag = false, click = false;
            foreach (Component component in element.Target.GetComponents<Component>())
            {
                if (component == null) continue;
                for (Type type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                {
                    string name = type.Name;
                    if (name == "InputField" || name == "TMP_InputField") input = true;
                    else if (name == "Toggle") toggle = true;
                    else if (name == "Slider" || name == "Scrollbar") slider = true;
                    else if (name == "ScrollRect") scroll = true;
                    else if (name == "Button") button = true;
                }
                if (component is IScrollHandler) scrollHandler = true;
                if (component is IDragHandler) drag = true;
                if (component is IPointerClickHandler) click = true;
            }
            element.Kind = input ? "input" : toggle ? "toggle" : slider ? "slider" : scroll ? "scroll" : button ? "button"
                : scrollHandler ? "scroll" : drag && !click ? "slider" : click ? "button" : "other";
            return element.Kind;
        }

        object DescribeUiElement(UiElement element)
        {
            Rect rect = element.Rect;
            return new
            {
                id = element.Target.GetInstanceID(),
                path = UiPathOf(element),
                kind = UiKindOf(element),
                handlers = element.Handlers.Distinct().ToArray(),
                label = UiLabelOf(element),
                rect = new[] { R(rect.x), R(rect.y), R(rect.width), R(rect.height) },
                center = new[] { R(rect.center.x), R(rect.center.y) },
                interactable = element.Interactable,
                onScreen = element.OnScreen,
                canvas = element.Root.name,
                sortingOrder = element.Root.sortingOrder,
                renderMode = element.Root.renderMode.ToString(),
                hidden = element.Root.gameObject.hideFlags != HideFlags.None,
            };
        }

        static float R(float value) { return (float)Math.Round(value, 1); }

        // Every active root canvas, including the ones plugins build with HideFlags.HideAndDontSave: FindObjectsOfType skips
        // those. FindObjectsOfTypeAll also returns prefab assets and other non-scene objects; they are never active in a hierarchy.
        static IEnumerable<Canvas> ActiveRootCanvases()
        {
            return UnityEngine.Resources.FindObjectsOfTypeAll<Canvas>().Where(canvas => canvas != null && canvas.isRootCanvas && canvas.isActiveAndEnabled && canvas.gameObject.activeInHierarchy);
        }

        // Active, visible elements that take a click, press, drag or wheel turn, frontmost canvas first.
        static IEnumerable<UiElement> CollectUiElements(int width, int height)
        {
            Canvas[] roots = ActiveRootCanvases()
                .OrderByDescending(canvas => canvas.sortingOrder).ThenBy(canvas => canvas.name).ToArray();
            var behaviours = new List<MonoBehaviour>();
            foreach (Canvas root in roots)
            {
                Camera camera = UiEventCamera(root);
                root.GetComponentsInChildren(false, behaviours);
                var byObject = new Dictionary<GameObject, UiElement>();
                var ordered = new List<UiElement>();
                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (behaviour == null || !behaviour.isActiveAndEnabled || !IsPointerHandler(behaviour)) continue;
                    UiElement element;
                    if (!byObject.TryGetValue(behaviour.gameObject, out element))
                    {
                        element = new UiElement { Target = behaviour.gameObject, Root = root };
                        byObject[behaviour.gameObject] = element;
                        ordered.Add(element);
                    }
                    element.Handlers.Add(behaviour.GetType().Name);
                    if (element.Selectable == null) element.Selectable = behaviour as Selectable;
                }
                foreach (UiElement element in ordered)
                {
                    RectTransform rectTransform = element.Target.transform as RectTransform;
                    Rect rect;
                    if (rectTransform == null || !TryUiScreenRect(rectTransform, camera, height, out rect) || rect.width < 1f || rect.height < 1f) continue;
                    element.Target.GetComponentsInParent(false, UiGroups);
                    float alpha = 1f;
                    bool reacts = true;
                    bool alphaDone = false, reactsDone = false;
                    foreach (CanvasGroup group in UiGroups)
                    {
                        if (!alphaDone) alpha *= group.alpha;
                        if (!reactsDone && (!group.interactable || !group.blocksRaycasts)) reacts = false;
                        if (group.ignoreParentGroups) { alphaDone = true; reactsDone = true; }
                    }
                    if (alpha <= 0.01f) continue;
                    element.Rect = rect;
                    element.OnScreen = rect.xMax > 0f && rect.yMax > 0f && rect.xMin < width && rect.yMin < height;
                    element.Interactable = reacts && (element.Selectable == null || element.Selectable.IsInteractable());
                    yield return element;
                }
            }
        }

        // Same camera choice as GraphicRaycaster: none for overlay canvases, the main camera as a fallback for world-space ones.
        static Camera UiEventCamera(Canvas root)
        {
            if (root.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            if (root.worldCamera != null) return root.worldCamera;
            return root.renderMode == RenderMode.WorldSpace ? Camera.main : null;
        }

        // Screen rectangle of a UI element in top-left window pixels. False when it is behind the camera.
        static bool TryUiScreenRect(RectTransform rectTransform, Camera camera, int screenHeight, out Rect rect)
        {
            rectTransform.GetWorldCorners(UiCorners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector3 corner in UiCorners)
            {
                Vector2 point;
                if (camera == null) point = new Vector2(corner.x, corner.y);
                else
                {
                    Vector3 screenPoint = camera.WorldToScreenPoint(corner);
                    if (screenPoint.z <= 0f) { rect = default(Rect); return false; }
                    point = new Vector2(screenPoint.x, screenPoint.y);
                }
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
            }
            rect = new Rect(minX, screenHeight - maxY, maxX - minX, maxY - minY);
            return true;
        }

        string UiPathOf(UiElement element)
        {
            return element.Path ?? (element.Path = ObjectPath(element.Target.transform));
        }

        string UiLabelOf(UiElement element)
        {
            if (!element.LabelDone)
            {
                element.Label = FirstUiText(element.Target, UiLabelDepth);
                element.LabelDone = true;
            }
            return element.Label;
        }

        // The first non-empty text in the element or just below it: UnityEngine.UI.Text or any component with a string "text"
        // property (TextMeshPro). Input fields are skipped because their text is what a user typed, possibly a password.
        static string FirstUiText(GameObject target, int depth)
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component == null) continue;
                Type type = component.GetType();
                if (type.Name.IndexOf("InputField", StringComparison.Ordinal) >= 0) continue;
                PropertyInfo property;
                if (!UiTextProperties.TryGetValue(type, out property))
                {
                    property = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && (property.PropertyType != typeof(string) || !property.CanRead || property.GetIndexParameters().Length != 0)) property = null;
                    UiTextProperties[type] = property;
                }
                if (property == null) continue;
                string value;
                try { value = (string)property.GetValue(component, null); }
                catch (Exception) { continue; }
                if (!string.IsNullOrWhiteSpace(value)) return TidyUiText(value);
            }
            if (depth <= 0) return null;
            Transform transform = target.transform;
            int count = Math.Min(transform.childCount, UiLabelChildren);
            for (int index = 0; index < count; index++)
            {
                Transform child = transform.GetChild(index);
                if (!child.gameObject.activeSelf) continue;
                string found = FirstUiText(child.gameObject, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        static string TidyUiText(string value)
        {
            string text = string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return text.Length > UiLabelLength ? text.Substring(0, UiLabelLength) : text;
        }

        // x/y in pixels (top-left origin) or, with space "normalized", as 0..1 of the window.
        static Vector2 UiPoint(JObject p)
        {
            float x = (float?)p["x"] ?? throw new ArgumentException("x is required.");
            float y = (float?)p["y"] ?? throw new ArgumentException("y is required.");
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) throw new ArgumentException("x and y must be finite.");
            string space = (string)p["space"] ?? "pixels";
            if (space == "normalized") return new Vector2(x * Screen.width, y * Screen.height);
            if (space != "pixels") throw new ArgumentException("space must be pixels or normalized.");
            return new Vector2(x, y);
        }

        object UiAt(JObject p)
        {
            if (EventSystem.current == null) throw new InvalidOperationException("NO_EVENT_SYSTEM: there is no current EventSystem, so no UI is listening.");
            Vector2 point = UiPoint(p);
            List<RaycastResult> hits = UiRaycast(point);
            return new
            {
                x = point.x,
                y = point.y,
                screen = new { width = Screen.width, height = Screen.height },
                overUi = hits.Count > 0,
                hitCount = hits.Count,
                hits = hits.Take(30).Select(DescribeRaycast).ToArray(),
                handlers = hits.Count == 0 ? null : DescribeHandlers(hits[0].gameObject),
            };
        }

        static bool IsUiInputField(Type type)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName == "UnityEngine.UI.InputField" || type.FullName == "TMPro.TMP_InputField") return true;
            return false;
        }

        static void InvokeUiTextEvent(Component field, string eventName, string value, List<string> fired)
        {
            PropertyInfo property = field.GetType().GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public);
            UnityEngine.Events.UnityEvent<string> unityEvent = property == null ? null : property.GetValue(field, null) as UnityEngine.Events.UnityEvent<string>;
            if (unityEvent == null) return;
            unityEvent.Invoke(value);
            fired.Add(eventName);
        }

        // Types into an InputField / TMP_InputField the way a user would: the text setter (which raises onValueChanged),
        // then onEndEdit, and onSubmit when submit is true. Typing itself cannot be sent, because UGUI reads text from OnGUI.
        object UiSetText(JObject p)
        {
            RequirePointerEnabled();
            int id = (int?)p["id"] ?? throw new ArgumentException("id is required.");
            string text = (string)p["text"] ?? throw new ArgumentException("text is required.");
            if (text.Length > 4000) throw new ArgumentException("text is limited to 4000 characters.");
            bool submit = (bool?)p["submit"] ?? false;
            GameObject target = FindUiObject(id) ?? throw new ArgumentException("UI_NOT_FOUND: no active UI object " + id + "; refresh ui.list.");
            Component field = target.GetComponents<Component>().FirstOrDefault(component => component != null && IsUiInputField(component.GetType()));
            if (field == null) throw new ArgumentException("NOT_A_TEXT_FIELD: " + ObjectPath(target.transform) + " has no InputField or TMP_InputField.");
            Type type = field.GetType();
            PropertyInfo textProperty = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo readOnlyProperty = type.GetProperty("readOnly", BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo contentProperty = type.GetProperty("contentType", BindingFlags.Instance | BindingFlags.Public);
            if (textProperty == null) throw new InvalidOperationException("NOT_A_TEXT_FIELD: " + type.FullName + " has no text property.");
            if (readOnlyProperty != null && (bool)readOnlyProperty.GetValue(field, null)) throw new InvalidOperationException("READ_ONLY: " + ObjectPath(target.transform) + " is read only.");
            var selectable = field as Selectable;
            if (selectable != null && !selectable.IsInteractable()) throw new InvalidOperationException("NOT_INTERACTABLE: " + ObjectPath(target.transform) + " is not interactable.");
            bool password = contentProperty != null && contentProperty.GetValue(field, null).ToString() == "Password";

            string before = (string)textProperty.GetValue(field, null);
            textProperty.SetValue(field, text, null);
            string after = (string)textProperty.GetValue(field, null);
            var fired = new List<string>();
            if (after != before) fired.Add("onValueChanged");
            InvokeUiTextEvent(field, "onEndEdit", after, fired);
            if (submit) InvokeUiTextEvent(field, "onSubmit", after, fired);
            return new
            {
                id,
                path = ObjectPath(target.transform),
                type = type.Name,
                before = password ? "(password field)" : before,
                after = password ? "(password field)" : after,
                changed = after != before,
                eventsFired = fired.ToArray(),
            };
        }
    }
}
