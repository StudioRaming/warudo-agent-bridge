using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Data;
using Warudo.Core.Graphs;
using Warudo.Core.Plugins;
using Warudo.Core.Scenes;

namespace StudioRaming.WarudoAgentBridge
{
    // Inspector fields ("data inputs") and trigger buttons shared by assets and blueprint nodes.
    // Nested structured data uses WARUDO's own path syntax: "LeftHandIK.Enabled", "Expressions.0.Name".
    public sealed partial class AgentBridgePlugin
    {
        static bool SafeFieldType(Type type)
        {
            return type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(float) ||
                type == typeof(double) || type == typeof(string) || type.IsEnum || type == typeof(Vector2) ||
                type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color) ||
                typeof(Asset).IsAssignableFrom(type);
        }

        static string FieldKind(Type type)
        {
            if (type.IsEnum) return "Enum";
            if (typeof(Asset).IsAssignableFrom(type)) return "Asset";
            if (typeof(StructuredData).IsAssignableFrom(type)) return "Structured";
            if (type.IsArray) return typeof(StructuredData).IsAssignableFrom(type.GetElementType()) ? "StructuredArray" : "Array";
            return "Value";
        }

        // Accepts "A[0].B" or WARUDO's "A.0.B".
        static string NativePath(string path)
        {
            string value = Regex.Replace((path ?? "").Trim(), @"\[(\d+)\]", ".$1").Trim('.');
            if (value.Length == 0 || value.Length > 256 || value.Contains("..")) throw new ArgumentException("Invalid field path: " + path);
            return value;
        }

        // Walks structured data and returns the entity that owns the last path segment.
        static Entity OwnerOf(Entity rootEntity, string nativePath, out string key)
        {
            string[] parts = nativePath.Split('.');
            Entity current = rootEntity;
            int i = 0;
            while (i < parts.Length - 1)
            {
                if (current.GetDataInputPort(parts[i]) == null) throw new ArgumentException("Unknown field '" + parts[i] + "' in path " + nativePath);
                object value = current.GetDataInput(parts[i]);
                i++;
                var array = value as Array;
                if (array != null)
                {
                    int index;
                    if (i >= parts.Length - 1 || !int.TryParse(parts[i], out index) || index < 0 || index >= array.Length)
                        throw new ArgumentException("Array path needs an index in range 0.." + (array.Length - 1) + ": " + nativePath);
                    value = array.GetValue(index);
                    i++;
                }
                current = value as Entity;
                if (current == null) throw new ArgumentException("Path segment is not structured data: " + nativePath);
            }
            key = parts[parts.Length - 1];
            if (current.GetDataInputPort(key) == null) throw new ArgumentException("Unknown field: " + nativePath);
            return current;
        }

        // Resolves a path that ends at structured data, e.g. "LeftHandIK" or "Expressions.0".
        static Entity StructuredAt(Entity rootEntity, string nativePath)
        {
            string[] parts = nativePath.Split('.');
            Entity current = rootEntity;
            int i = 0;
            while (i < parts.Length)
            {
                if (current.GetDataInputPort(parts[i]) == null) throw new ArgumentException("Unknown field '" + parts[i] + "' in path " + nativePath);
                object value = current.GetDataInput(parts[i]);
                i++;
                var array = value as Array;
                if (array != null)
                {
                    int index;
                    if (i >= parts.Length) throw new ArgumentException("Add an element index to browse an array, e.g. " + nativePath + ".0 (" + array.Length + " elements).");
                    if (!int.TryParse(parts[i], out index) || index < 0 || index >= array.Length) throw new ArgumentException("Array index out of range 0.." + (array.Length - 1) + ": " + nativePath);
                    value = array.GetValue(index);
                    i++;
                }
                current = value as Entity ?? throw new ArgumentException("Not structured data (only structured fields can be browsed): " + nativePath);
            }
            return current;
        }

        object FieldMetadata(Entity owner, string path, string key, bool includeValue)
        {
            DataInputPort port = owner.GetDataInputPort(key);
            Type type = port.Type;
            string kind = FieldKind(type);
            var props = port.Properties;
            bool safe = SafeFieldType(type) && !props.transient && !props.alwaysHidden && !Sensitive(key);
            object value = null;
            if (includeValue)
            {
                object raw = owner.GetDataInput(key);
                if (safe) value = FieldValue(raw);
                else if (kind == "Structured") value = raw == null ? null : "<structured data: browse with path=\"" + path + "\">";
                else if (raw is Array) value = new { count = ((Array)raw).Length };
            }
            return new
            {
                name = path,
                type = type.FullName,
                kind,
                label = props.label,
                description = props.description,
                hidden = props.hidden,
                disabled = props.disabled || props.alwaysDisabled,
                writable = safe && !props.alwaysDisabled,
                enumValues = type.IsEnum ? Enum.GetNames(type) : null,
                value
            };
        }

        object EntityFields(Entity entity, JObject p, object header)
        {
            string query = ((string)p["query"] ?? "").Trim();
            int offset = Math.Max(0, (int?)p["offset"] ?? 0);
            int limit = Mathf.Clamp((int?)p["limit"] ?? 40, 1, 80);
            string browse = string.IsNullOrWhiteSpace((string)p["path"]) ? null : NativePath((string)p["path"]);
            if (p["fields"] != null)
            {
                string[] names = p["fields"].ToObject<string[]>();
                if (names.Length > 80) throw new ArgumentException("At most 80 fields per request.");
                var read = names.Select(name =>
                {
                    string path = NativePath(browse == null ? name : browse + "." + name);
                    string key;
                    Entity owner = OwnerOf(entity, path, out key);
                    return FieldMetadata(owner, path, key, true);
                }).ToArray();
                return new { entity = header, fields = read };
            }
            Entity target = browse == null ? entity : StructuredAt(entity, browse);
            var ports = target.DataInputPortCollection.GetPorts().Values
                .Where(port => query.Length == 0 || port.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (port.Properties.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(port => port.Properties.order).ThenBy(port => port.Key, StringComparer.Ordinal).ToArray();
            var page = ports.Skip(offset).Take(limit).Select(port => FieldMetadata(target, browse == null ? port.Key : browse + "." + port.Key, port.Key, false)).ToArray();
            var triggers = target.TriggerPortCollection.GetPorts().Values
                .Where(t => !t.Properties.alwaysHidden)
                .Select(t => new { key = t.Key, label = t.Properties.label, description = t.Properties.description, hidden = t.Properties.hidden }).ToArray();
            int next = offset + page.Length;
            return new { entity = header, path = browse, total = ports.Length, offset, limit, nextOffset = next < ports.Length ? (int?)next : null, fields = page, triggers };
        }

        object ConvertFieldValue(JToken token, Type type)
        {
            if (typeof(Asset).IsAssignableFrom(type))
            {
                if (token == null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && (string)token == "")) return null;
                Guid id;
                if (!Guid.TryParse((string)token, out id)) throw new ArgumentException("Asset reference must be an asset UUID (or empty to clear).");
                Asset reference = Context.OpenedScene.GetAsset(id);
                if (reference == null || !type.IsInstanceOfType(reference)) throw new ArgumentException("Asset reference does not exist or has the wrong type (" + type.Name + ").");
                return reference;
            }
            return ConvertValue(token, type);
        }

        // persist, when given, runs after a successful patch (plugin settings are saved the way WARUDO's Settings tab saves them).
        // A save failure is reported, not thrown: the live values are already applied and the caller needs the before/after.
        object EntityPatch(Entity entity, JObject fields, object header, Action persist = null)
        {
            if (fields == null || fields.Count == 0 || fields.Count > 40) throw new ArgumentException("fields must contain 1..40 entries.");
            var prepared = new List<KeyValuePair<string, object>>();
            var before = new JObject();
            var beforeValues = new Dictionary<string, object>();
            foreach (var property in fields.Properties())
            {
                string path = NativePath(property.Name);
                if (path == "Source" && entity is Asset) throw new ArgumentException("Use warudo_asset_source to change Source.");
                string key;
                Entity owner = OwnerOf(entity, path, out key);
                DataInputPort port = owner.GetDataInputPort(key);
                if (port.Properties.transient || port.Properties.alwaysHidden || port.Properties.alwaysDisabled || Sensitive(key) || !SafeFieldType(port.Type))
                    throw new ArgumentException("Field is not writable through the bridge: " + path + " (" + FieldKind(port.Type) + " " + port.Type.Name + ")");
                prepared.Add(new KeyValuePair<string, object>(path, ConvertFieldValue(property.Value, port.Type)));
                beforeValues[path] = owner.GetDataInput(key);
                before[path] = FieldValue(beforeValues[path]);
            }
            var applied = new List<string>();
            try
            {
                foreach (var item in prepared)
                {
                    Apply(entity, item.Key, item.Value);
                    applied.Add(item.Key);
                }
            }
            catch (Exception applyError)
            {
                var rollbackFailures = new List<string>();
                foreach (string path in applied)
                {
                    try { Apply(entity, path, beforeValues[path]); }
                    catch (Exception) { rollbackFailures.Add(path); }
                }
                throw new InvalidOperationException((rollbackFailures.Count == 0 ? "PATCH_FAILED: " : "PARTIAL_ROLLBACK: ") + applyError.GetBaseException().Message +
                    " before=" + before.ToString(Formatting.None) + (rollbackFailures.Count == 0 ? "" : " rollbackFailures=" + string.Join(",", rollbackFailures)), applyError);
            }
            var after = new JObject();
            foreach (var item in prepared)
            {
                string key;
                Entity owner = OwnerOf(entity, item.Key, out key);
                after[item.Key] = FieldValue(owner.GetDataInput(key));
            }
            if (persist == null) return new { entity = header, before, after, persisted = false };
            string persistError = null;
            try { persist(); }
            catch (Exception saveError) { persistError = saveError.GetBaseException().Message; }
            return new { entity = header, before, after, persisted = persistError == null, persistError };
        }

        static void Apply(Entity entity, string path, object value)
        {
            if (path.IndexOf('.') < 0)
            {
                entity.SetDataInput(path, value, true);
                entity.BroadcastDataInput(path);
                return;
            }
            // WARUDO's own nested setter notifies the parent entity's watchers.
            if (!entity.DataInputPortCollection.SetValueAtPath(path, value, true)) throw new InvalidOperationException("WARUDO rejected the value at " + path);
        }

        object EntityTrigger(JObject p)
        {
            string key = Required(p, "trigger");
            Entity entity;
            object header;
            if (p["pluginId"] != null)
            {
                Plugin plugin = PluginByRef(p);
                entity = plugin;
                header = DescribePlugin(plugin);
            }
            else if (p["assetId"] != null)
            {
                Asset asset = AssetById(p);
                entity = asset;
                header = DescribeAsset(asset);
            }
            else
            {
                var node = NodeById(p);
                entity = node;
                header = DescribeNode(node);
            }
            TriggerPort port = entity.GetTriggerPort(key);
            if (port == null) throw new ArgumentException("Unknown trigger '" + key + "'. List triggers with warudo_asset_fields, warudo_plugin_fields or warudo_node_fields.");
            entity.InvokeTrigger(key);
            return new { entity = header, trigger = key, invoked = true };
        }
    }
}
