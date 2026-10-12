using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Warudo.Core;
using Warudo.Core.Plugins;

namespace StudioRaming.WarudoAgentBridge
{
    // WARUDO's Settings tab is the data inputs of plugin entities (PluginManager.GetPlugins()), the same
    // kind of entity as assets and nodes, so the shared field read/patch code applies unchanged.
    public sealed partial class AgentBridgePlugin
    {
        // Plugins exist without an open scene, so none of these operations needs one.
        const string PluginRestartNote = "WARUDO exposes no restart signal for plugin data inputs (PluginLoadChangeResult.requiresRestart " +
            "only covers enabling or disabling a plugin mod), so restartReported is always false. Read the state back, or check the " +
            "renderingPipeline before/after, to see whether the change took effect live.";

        Plugin PluginByRef(JObject p)
        {
            string key = Required(p, "pluginId").Trim();
            var plugins = Context.PluginManager.GetPlugins();
            Guid entityId;
            Plugin plugin = Context.PluginManager.GetPlugin(key) ??
                (Guid.TryParse(key, out entityId) ? plugins.FirstOrDefault(it => it.Id == entityId) : null) ??
                plugins.FirstOrDefault(it => string.Equals(it.PluginId, key, StringComparison.OrdinalIgnoreCase));
            if (plugin == null) throw new ArgumentException("PLUGIN_NOT_FOUND: no loaded plugin has type id or entity id '" + key + "'. List them with warudo_plugin_list.");
            return plugin;
        }

        object DescribePlugin(Plugin plugin)
        {
            PluginType type = plugin.Type.PluginType;
            return new
            {
                pluginId = type.id, entityId = plugin.Id.ToString(), name = type.name, version = type.version, author = type.author,
                description = type.description,
                fieldCount = plugin.DataInputPortCollection.GetPorts().Values.Count(port => !port.Properties.alwaysHidden),
                triggerCount = plugin.TriggerPortCollection.GetPorts().Values.Count(t => !t.Properties.alwaysHidden)
            };
        }

        async UniTask<object> PluginOperation(string op, JObject p)
        {
            if (op == "plugin.list")
            {
                string query = ((string)p["query"] ?? "").Trim();
                return Context.PluginManager.GetPlugins().Where(it =>
                {
                    PluginType type = it.Type.PluginType;
                    return query.Length == 0 || new[] { type.id, type.name, type.author, it.Id.ToString() }
                        .Any(value => (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                }).Select(DescribePlugin).ToArray();
            }
            Plugin plugin = PluginByRef(p);
            object header = DescribePlugin(plugin);
            if (op == "plugin.fields") return EntityFields(plugin, p, header);
            if (op != "plugin.patch") throw new ArgumentException("Unsupported plugin operation: " + op);

            string pipelineBefore = Context.RenderingPipelineManager.CurrentPipeline.ToString();
            // Persist exactly as WARUDO's Settings tab does (PluginManager.SavePluginData, which writes
            // Plugins/Data/<id>.json), so the value survives a restart.
            JObject result = JObject.FromObject(EntityPatch(plugin, p["fields"] as JObject, header, () => Context.PluginManager.SavePluginData(plugin)));
            // Watchers such as NiloToon's Enabled switch the render pipeline; give them a frame before reading it back.
            await UniTask.Yield();
            result["renderingPipeline"] = JObject.FromObject(new { before = pipelineBefore, after = Context.RenderingPipelineManager.CurrentPipeline.ToString() });
            if ((bool?)result["persisted"] == true) result["persistedTo"] = "Plugins/Data/" + plugin.PluginId + ".json";
            result["restartReported"] = false;
            result["restartNote"] = PluginRestartNote;
            return result;
        }
    }
}
