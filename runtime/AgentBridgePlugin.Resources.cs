using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Resource;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        // The resource type names WARUDO's own inspector uses for auto-complete lists.
        static readonly HashSet<string> ResourceTypes = new HashSet<string>
        { "Character", "CharacterAnimation", "CharacterHandPose", "Environment", "Prop", "Image", "Video", "Music", "Sound", "Particle", "LUT", "LensDirt" };

        IEnumerable<KeyValuePair<string, Resource>> Resources(string type)
        {
            if (!ResourceTypes.Contains(type)) throw new ArgumentException("type must be one of: " + string.Join(", ", ResourceTypes));
            var results = Context.ResourceManager.ProvideResources(type) ?? new List<ResourceProviderResult>();
            return results.Where(r => r != null && r.resources != null)
                .SelectMany(r => r.resources.Where(res => res != null && res.uri != null).Select(res => new KeyValuePair<string, Resource>(r.providerName, res)));
        }

        HashSet<string> ResourceUris(string type)
        {
            return new HashSet<string>(Resources(type).Select(pair => pair.Value.uri.ToString()), StringComparer.Ordinal);
        }

        object ResourceList(JObject p)
        {
            string type = Required(p, "type");
            string query = ((string)p["query"] ?? "").Trim();
            int offset = Math.Max(0, (int?)p["offset"] ?? 0);
            int limit = Mathf.Clamp((int?)p["limit"] ?? 200, 1, 500);
            var all = Resources(type)
                .Select(pair => new { provider = pair.Key, category = pair.Value.category, label = pair.Value.label, uri = pair.Value.uri.ToString() })
                .Where(r => query.Length == 0 || (r.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || r.uri.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            var page = all.Skip(offset).Take(limit).ToArray();
            int next = offset + page.Length;
            return new { type, total = all.Length, offset, nextOffset = next < all.Length ? (int?)next : null, resources = page };
        }

        object RenderGlobals(JObject p)
        {
            string[] names = (p["names"] as JArray ?? throw new ArgumentException("names is required.")).Select(n => (string)n).ToArray();
            if (names.Length == 0 || names.Length > 32 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128)) throw new ArgumentException("names must hold 1..32 property names.");
            var values = new JObject();
            foreach (string name in names.Distinct())
            {
                Texture texture = Shader.GetGlobalTexture(name);
                Vector4 vector = Shader.GetGlobalVector(name);
                values[name] = JObject.FromObject(new
                {
                    @float = Shader.GetGlobalFloat(name),
                    vector = new[] { vector.x, vector.y, vector.z, vector.w },
                    texture = texture == null ? null : new { name = texture.name, width = texture.width, height = texture.height, dimension = texture.dimension.ToString() }
                });
            }
            return new { values, note = "Unset globals read as 0 / null." };
        }
    }
}
