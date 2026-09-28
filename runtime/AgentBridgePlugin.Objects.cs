using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        IEnumerable<GameObject> SceneObjects(string sceneName = null)
        {
            for (int index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(index);
                if (!scene.isLoaded || (!string.IsNullOrEmpty(sceneName) && scene.name != sceneName)) continue;
                foreach (GameObject rootObject in scene.GetRootGameObjects())
                foreach (Transform child in rootObject.GetComponentsInChildren<Transform>(true)) yield return child.gameObject;
            }
        }
        GameObject ObjectById(JObject p)
        {
            int id = (int?)p["objectId"] ?? throw new ArgumentException("objectId is required.");
            return SceneObjects().FirstOrDefault(g => g.GetInstanceID() == id) ?? throw new ArgumentException("Object no longer exists; refresh hierarchy.list.");
        }
        string ObjectPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }
        object DescribeObject(GameObject g, bool components)
        {
            return new { objectId = g.GetInstanceID(), name = g.name, path = ObjectPath(g.transform), scene = g.scene.name,
                activeSelf = g.activeSelf, activeInHierarchy = g.activeInHierarchy, layer = g.layer,
                parentId = g.transform.parent == null ? (int?)null : g.transform.parent.gameObject.GetInstanceID(),
                position = Wire(g.transform.position), rotation = Wire(g.transform.eulerAngles), localScale = Wire(g.transform.localScale),
                components = !components ? null : g.GetComponents<Component>().Select(c => c == null ? null : new
                { id = c.GetInstanceID(), type = c.GetType().FullName, enabled = c is Behaviour ? (bool?)((Behaviour)c).enabled : null }).ToArray() };
        }
        object ObjectOperation(string op, JObject p)
        {
            if (op == "hierarchy.list")
            {
                string query = (string)p["query"] ?? "";
                string scene = (string)p["scene"];
                int offset = Math.Max(0, (int?)p["offset"] ?? 0), limit = Mathf.Clamp((int?)p["limit"] ?? 100, 1, 500);
                var matches = SceneObjects(scene).Where(g => g.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                return new { total = matches.Length, offset, items = matches.Skip(offset).Take(limit).Select(g => DescribeObject(g, false)).ToArray() };
            }
            GameObject go = ObjectById(p);
            if (op == "object.get") return DescribeObject(go, true);
            if (op == "object.transform")
            {
                Vector3? position = p["position"] == null ? (Vector3?)null : Vector(p["position"]);
                Vector3? rotation = p["rotation"] == null ? (Vector3?)null : Vector(p["rotation"]);
                Vector3? scale = p["scale"] == null ? (Vector3?)null : Vector(p["scale"]);
                if (scale.HasValue && (scale.Value.x <= 0 || scale.Value.y <= 0 || scale.Value.z <= 0)) throw new ArgumentException("Scale must be positive.");
                object before = DescribeObject(go, false);
                if (position.HasValue) go.transform.position = position.Value;
                if (rotation.HasValue) go.transform.rotation = Quaternion.Euler(rotation.Value);
                if (scale.HasValue) go.transform.localScale = scale.Value;
                return new { before, after = DescribeObject(go, false), persisted = false };
            }
            if (op == "object.active")
            {
                bool enabled = (bool?)p["active"] ?? throw new ArgumentException("active is required.");
                bool before = go.activeSelf; go.SetActive(enabled); return new { before, after = go.activeSelf, persisted = false };
            }
            int componentId = (int?)p["componentId"] ?? 0;
            string type = (string)p["type"];
            Component[] matchesByType = go.GetComponents<Component>().Where(c => c != null &&
                (componentId != 0 ? c.GetInstanceID() == componentId : c.GetType().FullName == type)).ToArray();
            if (matchesByType.Length != 1) throw new ArgumentException("Specify one observed componentId or exact type on the object.");
            Component component = matchesByType[0];
            if (op == "component.get")
            {
                var fields = new JObject();
                string[] names = p["fields"] == null ? new string[0] : p["fields"].ToObject<string[]>();
                if (names.Length > 40) throw new ArgumentException("At most40fields per request.");
                var context = new ReadContext { MaterialProperties = MaterialPropertyNames(p) };
                var unreadable = new JArray();
                foreach (string name in names)
                {
                    if (Sensitive(name)) { fields[name] = "REDACTED"; continue; }
                    // Read-only resolution: allowlisted Unity properties, then the existing safe writable surface, then public instance fields.
                    Func<object> read = ComponentReadFor(component, name, context);
                    if (read == null) { fields[name] = JValue.CreateNull(); unreadable.Add(name); continue; }
                    object raw = read();
                    NoteTruncation(context, name, raw);
                    fields[name] = FieldValue(raw, context);
                }
                return new { componentId = component.GetInstanceID(), type = component.GetType().FullName, fields, unreadable, notes = context.Notes };
            }
            if (op == "component.patch")
            {
                JObject fields = p["fields"] as JObject ?? throw new ArgumentException("fields is required.");
                if (fields.Count == 0 || fields.Count > 40) throw new ArgumentException("fields must contain 1..40 entries.");
                var prepared = new List<KeyValuePair<ComponentWrite, object>>();
                foreach (var value in fields.Properties())
                {
                    ComponentWrite write = ComponentWriteFor(component, value.Name);
                    if (write == null || Sensitive(value.Name)) throw new ArgumentException("Property or field is not safely writable: " + value.Name);
                    prepared.Add(new KeyValuePair<ComponentWrite, object>(write, ConvertValue(value.Value, write.ValueType)));
                }
                var beforeValues = new Dictionary<ComponentWrite, object>();
                var before = new JObject();
                foreach (var value in prepared)
                {
                    beforeValues[value.Key] = value.Key.Get();
                    before[value.Key.Name] = FieldValue(beforeValues[value.Key]);
                }
                try
                {
                    foreach (var value in prepared) value.Key.Set(value.Value);
                }
                catch
                {
                    foreach (var value in prepared)
                    {
                        try { value.Key.Set(beforeValues[value.Key]); }
                        catch { }
                    }
                    throw;
                }
                var after = new JObject();
                foreach (var value in prepared) after[value.Key.Name] = FieldValue(value.Key.Get());
                return new { componentId = component.GetInstanceID(), before, after, persisted = false };
            }
            if (op == "component.call")
            {
                // Method calls are off unless the user allows a type prefix and method names in config.json.
                RefreshConfig();
                string fullType = component.GetType().FullName;
                string method = Required(p, "method");
                bool allowed = callRules.Any(rule => fullType.StartsWith(rule.TypePrefix, StringComparison.Ordinal) && rule.Methods.Contains(method));
                if (!allowed) throw new InvalidOperationException("CALL_NOT_ALLOWED: " + fullType + "." + method + " is not in config.json componentCall.allow (" + ConfigFile + ").");
                var args = p["arguments"] as JArray ?? new JArray();
                var methods = component.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == method && m.GetParameters().Length == args.Count).ToArray();
                if (methods.Length != 1) throw new ArgumentException("Method overload is ambiguous or missing.");
                var parameters = methods[0].GetParameters();
                object[] values = args.Select((v, index) => ConvertValue(v, parameters[index].ParameterType)).ToArray();
                object returned = methods[0].Invoke(component, values);
                return new { componentId = component.GetInstanceID(), method, result = FieldValue(returned), frame = Time.frameCount };
            }
            throw new ArgumentException("Unsupported object operation: " + op);
        }
        sealed class ComponentWrite
        {
            public string Name;
            public Type ValueType;
            public Func<object> Get;
            public Action<object> Set;
        }
        ComponentWrite ComponentWriteFor(Component component, string name)
        {
            if (component == null || string.IsNullOrWhiteSpace(name) || Sensitive(name)) return null;
            var transform = component as Transform;
            if (transform != null)
            {
                if (name == "position") return Property(name, typeof(Vector3), () => transform.position, value => transform.position = (Vector3)value);
                if (name == "localPosition") return Property(name, typeof(Vector3), () => transform.localPosition, value => transform.localPosition = (Vector3)value);
                if (name == "eulerAngles") return Property(name, typeof(Vector3), () => transform.eulerAngles, value => transform.eulerAngles = (Vector3)value);
                if (name == "localEulerAngles") return Property(name, typeof(Vector3), () => transform.localEulerAngles, value => transform.localEulerAngles = (Vector3)value);
                if (name == "localScale") return Property(name, typeof(Vector3), () => transform.localScale, value => transform.localScale = (Vector3)value);
            }
            var light = component as Light;
            if (light != null)
            {
                if (name == "color") return Property(name, typeof(Color), () => light.color, value => light.color = (Color)value);
                if (name == "intensity") return Property(name, typeof(float), () => light.intensity, value => light.intensity = (float)value);
                if (name == "range") return Property(name, typeof(float), () => light.range, value => light.range = (float)value);
                if (name == "spotAngle") return Property(name, typeof(float), () => light.spotAngle, value => light.spotAngle = (float)value);
                if (name == "bounceIntensity") return Property(name, typeof(float), () => light.bounceIntensity, value => light.bounceIntensity = (float)value);
                if (name == "shadowStrength") return Property(name, typeof(float), () => light.shadowStrength, value => light.shadowStrength = (float)value);
                if (name == "shadows") return Property(name, typeof(LightShadows), () => light.shadows, value => light.shadows = (LightShadows)value);
            }
            var camera = component as Camera;
            if (camera != null)
            {
                if (name == "fieldOfView") return Property(name, typeof(float), () => camera.fieldOfView, value => camera.fieldOfView = (float)value);
                if (name == "nearClipPlane") return Property(name, typeof(float), () => camera.nearClipPlane, value => camera.nearClipPlane = (float)value);
                if (name == "farClipPlane") return Property(name, typeof(float), () => camera.farClipPlane, value => camera.farClipPlane = (float)value);
                if (name == "orthographic") return Property(name, typeof(bool), () => camera.orthographic, value => camera.orthographic = (bool)value);
                if (name == "orthographicSize") return Property(name, typeof(float), () => camera.orthographicSize, value => camera.orthographicSize = (float)value);
                if (name == "depth") return Property(name, typeof(float), () => camera.depth, value => camera.depth = (float)value);
                if (name == "cullingMask") return Property(name, typeof(int), () => camera.cullingMask, value => camera.cullingMask = (int)value);
                if (name == "backgroundColor") return Property(name, typeof(Color), () => camera.backgroundColor, value => camera.backgroundColor = (Color)value);
                if (name == "allowHDR") return Property(name, typeof(bool), () => camera.allowHDR, value => camera.allowHDR = (bool)value);
                if (name == "allowMSAA") return Property(name, typeof(bool), () => camera.allowMSAA, value => camera.allowMSAA = (bool)value);
            }
            var renderer = component as Renderer;
            if (renderer != null)
            {
                if (name == "shadowCastingMode") return Property(name, typeof(ShadowCastingMode), () => renderer.shadowCastingMode, value => renderer.shadowCastingMode = (ShadowCastingMode)value);
                if (name == "receiveShadows") return Property(name, typeof(bool), () => renderer.receiveShadows, value => renderer.receiveShadows = (bool)value);
                if (name == "lightProbeUsage") return Property(name, typeof(LightProbeUsage), () => renderer.lightProbeUsage, value => renderer.lightProbeUsage = (LightProbeUsage)value);
                if (name == "reflectionProbeUsage") return Property(name, typeof(ReflectionProbeUsage), () => renderer.reflectionProbeUsage, value => renderer.reflectionProbeUsage = (ReflectionProbeUsage)value);
                if (name == "sortingOrder") return Property(name, typeof(int), () => renderer.sortingOrder, value => renderer.sortingOrder = (int)value);
            }
            var behaviour = component as Behaviour;
            if (behaviour != null && name == "enabled") return Property(name, typeof(bool), () => behaviour.enabled, value => behaviour.enabled = (bool)value);
            FieldInfo field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && !field.IsInitOnly && !field.IsStatic && SafeValueType(field.FieldType))
                return Property(name, field.FieldType, () => field.GetValue(component), value => field.SetValue(component, value));
            return null;
        }
        static ComponentWrite Property(string name, Type type, Func<object> get, Action<object> set)
        {
            return new ComponentWrite { Name = name, ValueType = type, Get = get, Set = set };
        }
        JToken FieldValue(object value) { return FieldValue(value, null, 0); }
        static bool SafeValueType(Type type)
        {
            return type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(float) ||
                type == typeof(double) || type == typeof(string) || type.IsEnum || type == typeof(Vector2) ||
                type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color);
        }
        object ConvertValue(JToken token, Type type)
        {
            if (type == typeof(bool)) return token.Type == JTokenType.Boolean ? token.Value<bool>() : throw new ArgumentException("Expected boolean.");
            if (type == typeof(int)) return token.Type == JTokenType.Integer ? token.Value<int>() : throw new ArgumentException("Expected integer.");
            if (type == typeof(long)) return token.Type == JTokenType.Integer ? token.Value<long>() : throw new ArgumentException("Expected integer.");
            if (type == typeof(float))
            {
                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new ArgumentException("Expected a number.");
                float value = token.Value<float>();
                if (float.IsNaN(value) || float.IsInfinity(value) || Mathf.Abs(value) > 100000) throw new ArgumentException("Invalid float.");
                return value;
            }
            if (type == typeof(double))
            {
                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new ArgumentException("Expected a number.");
                double value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 100000) throw new ArgumentException("Invalid double.");
                return value;
            }
            if (type == typeof(string))
            {
                string value = token.Type == JTokenType.String ? token.Value<string>() : null;
                if (value == null || value.Length > 2048) throw new ArgumentException("Expected a bounded string.");
                return value;
            }
            if (type.IsEnum)
            {
                if (token.Type != JTokenType.String) throw new ArgumentException("Expected an enum name.");
                object value;
                try { value = Enum.Parse(type, token.Value<string>(), true); }
                catch { throw new ArgumentException("Invalid enum value."); }
                if (!Enum.IsDefined(type, value)) throw new ArgumentException("Invalid enum value.");
                return value;
            }
            if (type == typeof(Vector2))
            {
                if (!(token is JArray) || token.Count() != 2) throw new ArgumentException("Expected a 2-number vector.");
                float[] a = token.ToObject<float[]>();
                if (a.Any(x => float.IsNaN(x) || float.IsInfinity(x) || Mathf.Abs(x) > 100000)) throw new ArgumentException("Invalid vector.");
                return new Vector2(a[0], a[1]);
            }
            if (type == typeof(Vector4))
            {
                if (!(token is JArray) || token.Count() != 4) throw new ArgumentException("Expected a 4-number vector.");
                float[] a = token.ToObject<float[]>();
                if (a.Any(x => float.IsNaN(x) || float.IsInfinity(x) || Mathf.Abs(x) > 100000)) throw new ArgumentException("Invalid vector.");
                return new Vector4(a[0], a[1], a[2], a[3]);
            }
            if (type == typeof(Quaternion))
            {
                if (!(token is JArray) || token.Count() != 4) throw new ArgumentException("Expected a 4-number quaternion.");
                float[] a = token.ToObject<float[]>();
                if (a.Any(x => float.IsNaN(x) || float.IsInfinity(x) || Mathf.Abs(x) > 1.1f)) throw new ArgumentException("Invalid quaternion.");
                return new Quaternion(a[0], a[1], a[2], a[3]);
            }
            if (type == typeof(Vector3)) return Vector(token);
            if (type == typeof(Color))
            {
                if (!(token is JArray)) throw new ArgumentException("Expected a color array [r,g,b] or [r,g,b,a].");
                float[] a = token.ToObject<float[]>();
                if ((a.Length != 3 && a.Length != 4) || a.Any(v => float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 100)) throw new ArgumentException("Expected 3 or 4 finite color values.");
                return new Color(a[0], a[1], a[2], a.Length == 4 ? a[3] : 1f);
            }
            throw new InvalidOperationException("Unsupported diagnostic value type: " + type.Name);
        }
    }
}
