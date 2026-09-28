using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace StudioRaming.WarudoAgentBridge
{
    // Read-only component inspection. Nothing in this file assigns to Unity state.
    // Renderer.material/materials, MeshFilter.mesh and Collider.material are never read:
    // those getters instantiate per-renderer copies and would silently change the scene.
    public sealed partial class AgentBridgePlugin
    {
        const int MaxListItems = 128;
        const int MaxKeywords = 64;
        const int MaxMaterialProperties = 32;
        const int MaxValueDepth = 4;

        sealed class ReadContext
        {
            public string[] MaterialProperties = new string[0];
            public readonly JObject Notes = new JObject();
            public void Note(string field, string message) { Notes[field] = message; }
        }

        static string[] MaterialPropertyNames(JObject p)
        {
            JToken token = p["materialProperties"];
            if (token == null || token.Type == JTokenType.Null) return new string[0];
            if (!(token is JArray)) throw new ArgumentException("materialProperties must be an array of property names.");
            string[] names = token.ToObject<string[]>();
            if (names.Length > MaxMaterialProperties) throw new ArgumentException("At most " + MaxMaterialProperties + " materialProperties per request.");
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128)) throw new ArgumentException("materialProperties entries must be 1..128 characters.");
            return names.Distinct().ToArray();
        }

        static void NoteTruncation(ReadContext context, string field, object raw)
        {
            var list = raw as ICollection;
            if (list != null && !(raw is string) && list.Count > MaxListItems && context.Notes[field] == null)
                context.Note(field, "Truncated to " + MaxListItems + " of " + list.Count + " items.");
        }

        Func<object> ComponentReadFor(Component component, string name, ReadContext context)
        {
            if (component == null || string.IsNullOrWhiteSpace(name) || Sensitive(name)) return null;
            var renderer = component as Renderer;
            if (renderer != null)
            {
                switch (name)
                {
                    case "enabled": return () => renderer.enabled;
                    case "isVisible": return () => renderer.isVisible;
                    case "bounds": return () => renderer.bounds;
                    case "localBounds": return () => renderer.localBounds;
                    case "sharedMaterial": return () => renderer.sharedMaterial;
                    case "sharedMaterials": return () => renderer.sharedMaterials;
                    case "material":
                        context.Note(name, "Mapped to sharedMaterial. Renderer.material is never read because it instantiates a material copy.");
                        return () => renderer.sharedMaterial;
                    case "materials":
                        context.Note(name, "Mapped to sharedMaterials. Renderer.materials is never read because it instantiates material copies.");
                        return () => renderer.sharedMaterials;
                    case "lightmapIndex": return () => renderer.lightmapIndex;
                    case "sortingLayerName": return () => renderer.sortingLayerName;
                    case "sortingLayerID": return () => renderer.sortingLayerID;
                    case "rendererPriority": return () => renderer.rendererPriority;
                    case "renderingLayerMask": return () => renderer.renderingLayerMask;
                    case "allowOcclusionWhenDynamic": return () => renderer.allowOcclusionWhenDynamic;
                    case "forceRenderingOff": return () => renderer.forceRenderingOff;
                    case "isPartOfStaticBatch": return () => renderer.isPartOfStaticBatch;
                    case "motionVectorGenerationMode": return () => renderer.motionVectorGenerationMode;
                    case "probeAnchor": return () => renderer.probeAnchor;
                }
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    switch (name)
                    {
                        case "sharedMesh": return () => skinned.sharedMesh;
                        case "mesh":
                            context.Note(name, "Mapped to sharedMesh.");
                            return () => skinned.sharedMesh;
                        case "rootBone": return () => skinned.rootBone;
                        case "bones": return () => skinned.bones;
                        case "updateWhenOffscreen": return () => skinned.updateWhenOffscreen;
                        case "quality": return () => skinned.quality;
                    }
                }
            }
            var filter = component as MeshFilter;
            if (filter != null)
            {
                if (name == "sharedMesh") return () => filter.sharedMesh;
                if (name == "mesh")
                {
                    context.Note(name, "Mapped to sharedMesh. MeshFilter.mesh is never read because it instantiates a mesh copy.");
                    return () => filter.sharedMesh;
                }
            }
            var collider = component as Collider;
            if (collider != null)
            {
                switch (name)
                {
                    case "enabled": return () => collider.enabled;
                    case "isTrigger": return () => collider.isTrigger;
                    case "bounds": return () => collider.bounds;
                    case "contactOffset": return () => collider.contactOffset;
                    case "sharedMaterial": return () => collider.sharedMaterial;
                    case "material":
                        context.Note(name, "Mapped to sharedMaterial. Collider.material is never read because it instantiates a physic material copy.");
                        return () => collider.sharedMaterial;
                }
            }
            var light = component as Light;
            if (light != null)
            {
                switch (name)
                {
                    case "type": return () => light.type;
                    case "cookie": return () => light.cookie;
                    case "cullingMask": return () => light.cullingMask;
                    case "renderMode": return () => light.renderMode;
                }
            }
            var camera = component as Camera;
            if (camera != null)
            {
                if (name == "targetTexture") return () => camera.targetTexture;
                if (name == "clearFlags") return () => camera.clearFlags;
            }
            var behaviour = component as Behaviour;
            if (behaviour != null && name == "isActiveAndEnabled") return () => behaviour.isActiveAndEnabled;
            ComponentWrite write = ComponentWriteFor(component, name);
            if (write != null) return write.Get;
            // Public instance fields of any type are readable; non-public fields are never exposed.
            FieldInfo field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && !field.IsStatic) return () => field.GetValue(component);
            return null;
        }

        // FieldValue(object) stays in Objects.cs so this file can be deployed before Objects.cs without a duplicate member.
        JToken FieldValue(object value, ReadContext context) { return FieldValue(value, context, 0); }

        JToken FieldValue(object value, ReadContext context, int depth)
        {
            if (value == null) return JValue.CreateNull();
            var asset = value as Warudo.Core.Scenes.Asset;
            if (asset != null) return JObject.FromObject(new { id = asset.Id.ToString(), name = asset.Name, type = asset.GetType().FullName });
            if (value is UnityEngine.Object) return UnityObjectValue((UnityEngine.Object)value, context);
            if (value is Vector2) { var v = (Vector2)value; return JArray.FromObject(new[] { v.x, v.y }); }
            if (value is Vector3) return JArray.FromObject(Wire((Vector3)value));
            if (value is Vector4) { var v = (Vector4)value; return JArray.FromObject(new[] { v.x, v.y, v.z, v.w }); }
            if (value is Quaternion) { var q = (Quaternion)value; return JArray.FromObject(new[] { q.x, q.y, q.z, q.w }); }
            if (value is Color) { var c = (Color)value; return JArray.FromObject(new[] { c.r, c.g, c.b, c.a }); }
            if (value is Color32) { var c = (Color32)value; return JArray.FromObject(new[] { (int)c.r, c.g, c.b, c.a }); }
            if (value is Bounds)
            {
                var b = (Bounds)value;
                return JObject.FromObject(new { center = Wire(b.center), size = Wire(b.size), min = Wire(b.min), max = Wire(b.max) });
            }
            if (value is Rect) { var r = (Rect)value; return JObject.FromObject(new { x = r.x, y = r.y, width = r.width, height = r.height }); }
            if (value is LayerMask) return new JValue(((LayerMask)value).value);
            if (value is string) { string s = (string)value; return Sensitive(s) ? new JValue("REDACTED") : new JValue(s.Length > 1024 ? s.Substring(0, 1024) : s); }
            if (value.GetType().IsPrimitive || value is decimal) return JToken.FromObject(value);
            if (value.GetType().IsEnum) return new JValue(value.ToString());
            var list = value as IList;
            if (list != null)
            {
                if (depth >= MaxValueDepth) return new JValue("<" + value.GetType().Name + ">");
                var array = new JArray();
                int count = Math.Min(list.Count, MaxListItems);
                for (int index = 0; index < count; index++) array.Add(FieldValue(list[index], context, depth + 1));
                return array;
            }
            return new JValue("<" + value.GetType().Name + ">");
        }

        JToken UnityObjectValue(UnityEngine.Object unity, ReadContext context)
        {
            int instanceId = unity.GetInstanceID();
            var summary = new JObject { ["type"] = unity.GetType().FullName, ["instanceId"] = instanceId, ["id"] = instanceId };
            // Unity overloads == null for destroyed objects; report them instead of touching their native side.
            if (unity == null) { summary["missing"] = true; return summary; }
            summary["name"] = unity.name;
            var material = unity as Material;
            if (material != null) { MaterialSummary(summary, material, context); return summary; }
            var texture = unity as Texture;
            if (texture != null)
            {
                summary["width"] = texture.width;
                summary["height"] = texture.height;
                summary["dimension"] = texture.dimension.ToString();
                return summary;
            }
            var mesh = unity as Mesh;
            if (mesh != null)
            {
                summary["vertexCount"] = mesh.vertexCount;
                summary["subMeshCount"] = mesh.subMeshCount;
                return summary;
            }
            var shader = unity as Shader;
            if (shader != null) { summary["isSupported"] = shader.isSupported; summary["passCount"] = shader.passCount; return summary; }
            var component = unity as Component;
            if (component != null)
            {
                summary["objectId"] = component.gameObject.GetInstanceID();
                summary["path"] = ObjectPath(component.transform);
                summary["inScene"] = component.gameObject.scene.IsValid();
                return summary;
            }
            var gameObject = unity as GameObject;
            if (gameObject != null)
            {
                summary["objectId"] = instanceId;
                summary["path"] = ObjectPath(gameObject.transform);
                summary["inScene"] = gameObject.scene.IsValid();
            }
            return summary;
        }

        void MaterialSummary(JObject summary, Material material, ReadContext context)
        {
            Shader shader = material.shader;
            summary["shader"] = shader == null ? null : shader.name;
            summary["shaderSupported"] = shader == null ? (bool?)null : shader.isSupported;
            summary["renderQueue"] = material.renderQueue;
            string[] keywords = material.shaderKeywords ?? new string[0];
            summary["keywords"] = new JArray(keywords.Take(MaxKeywords).Cast<object>().ToArray());
            if (keywords.Length > MaxKeywords) summary["keywordCount"] = keywords.Length;
            summary["passCount"] = material.passCount;
            if (context == null || context.MaterialProperties.Length == 0) return;
            var properties = new JObject();
            foreach (string name in context.MaterialProperties)
            {
                if (Sensitive(name)) { properties[name] = "REDACTED"; continue; }
                int index = shader == null || !material.HasProperty(name) ? -1 : shader.FindPropertyIndex(name);
                if (index < 0) { properties[name] = new JObject { ["missing"] = true }; continue; }
                ShaderPropertyType kind = shader.GetPropertyType(index);
                var entry = new JObject { ["type"] = kind.ToString() };
                switch (kind)
                {
                    case ShaderPropertyType.Color: entry["value"] = FieldValue(material.GetColor(name)); break;
                    case ShaderPropertyType.Vector: entry["value"] = FieldValue(material.GetVector(name)); break;
                    case ShaderPropertyType.Int: entry["value"] = material.GetInteger(name); break;
                    case ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name);
                        entry["value"] = texture == null ? JValue.CreateNull() : UnityObjectValue(texture, null);
                        entry["textureDimension"] = shader.GetPropertyTextureDimension(index).ToString();
                        entry["scale"] = FieldValue(material.GetTextureScale(name));
                        entry["offset"] = FieldValue(material.GetTextureOffset(name));
                        break;
                    default: entry["value"] = material.GetFloat(name); break;
                }
                properties[name] = entry;
            }
            summary["properties"] = properties;
        }
    }
}
