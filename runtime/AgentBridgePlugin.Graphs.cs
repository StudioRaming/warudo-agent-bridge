using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Warudo.Core;
using Warudo.Core.Graphs;

namespace StudioRaming.WarudoAgentBridge
{
    // Blueprints (graphs) and their nodes.
    public sealed partial class AgentBridgePlugin
    {
        Graph GraphById(JObject p)
        {
            if (Context.OpenedScene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            Guid id;
            if (!Guid.TryParse(Required(p, "graphId"), out id)) throw new ArgumentException("Invalid graphId.");
            return Context.OpenedScene.GetGraph(id) ?? throw new ArgumentException("GRAPH_NOT_FOUND: refresh warudo_blueprint_list.");
        }

        Node NodeById(JObject p)
        {
            Graph graph = GraphById(p);
            Guid id;
            if (!Guid.TryParse(Required(p, "nodeId"), out id)) throw new ArgumentException("Invalid nodeId.");
            return graph.GetNode(id) ?? throw new ArgumentException("NODE_NOT_FOUND: refresh warudo_blueprint_get.");
        }

        static object DescribeGraph(Graph graph)
        {
            return new { id = graph.Id.ToString(), name = graph.Name, enabled = graph.Enabled, order = graph.Order, nodeCount = graph.GetNodes().Count };
        }

        static object DescribeNode(Node node)
        {
            var meta = node.GetTypeMeta();
            return new
            {
                id = node.Id.ToString(),
                graphId = node.Graph == null ? null : node.Graph.Id.ToString(),
                name = node.Name,
                typeId = meta == null ? null : meta.Id,
                title = meta == null || meta.NodeType == null ? null : meta.NodeType.title,
                category = meta == null || meta.NodeType == null ? null : meta.NodeType.category,
                typeName = node.GetType().FullName,
                dataInputs = node.DataInputPortCollection.GetPorts().Keys.ToArray(),
                dataOutputs = node.DataOutputPortCollection.GetPorts().Keys.ToArray(),
                flowInputs = node.FlowInputPortCollection.GetPorts().Keys.ToArray(),
                flowOutputs = node.FlowOutputPortCollection.GetPorts().Keys.ToArray(),
                triggers = node.TriggerPortCollection.GetPorts().Keys.ToArray()
            };
        }

        object GraphOperation(string op, JObject p)
        {
            if (op == "graph.list")
            {
                if (Context.OpenedScene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
                string query = (string)p["query"] ?? "";
                return Context.OpenedScene.GetGraphs().Values
                    .Where(g => query.Length == 0 || (g.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(g => g.Order).Select(DescribeGraph).ToArray();
            }
            if (op == "graph.get")
            {
                Graph graph = GraphById(p);
                bool connections = (bool?)p["includeConnections"] ?? true;
                var serialized = connections ? graph.Serialize() : null;
                return new
                {
                    graph = DescribeGraph(graph),
                    nodes = graph.GetNodes().Values.Select(DescribeNode).ToArray(),
                    dataConnections = serialized == null || serialized.dataConnections == null ? null : serialized.dataConnections
                        .Select(c => new { from = c.outputNode.ToString(), fromPort = c.outputPort, to = c.inputNode.ToString(), toPort = c.inputPort }).ToArray(),
                    flowConnections = serialized == null || serialized.flowConnections == null ? null : serialized.flowConnections
                        .Select(c => new { from = c.outputNode.ToString(), fromPort = c.outputPort, to = c.inputNode.ToString(), toPort = c.inputPort }).ToArray()
                };
            }
            if (op == "graph.enabled")
            {
                Graph graph = GraphById(p);
                bool enabled = (bool?)p["enabled"] ?? throw new ArgumentException("enabled is required.");
                bool before = graph.Enabled;
                if (Context.Service != null) Context.Service.SetGraphEnabled(graph.Id, enabled);
                else graph.Enabled = enabled;
                return new { graph = DescribeGraph(graph), before, after = graph.Enabled, persisted = false };
            }
            Node node = NodeById(p);
            if (op == "node.fields") return EntityFields(node, p, DescribeNode(node));
            if (op == "node.patch") return EntityPatch(node, p["fields"] as JObject, DescribeNode(node));
            if (op == "node.flow")
            {
                string input = (string)p["input"] ?? "Enter";
                if (!node.FlowInputPortCollection.ContainsPort(input))
                    throw new ArgumentException("Node has no flow input '" + input + "'. Flow inputs: " + string.Join(", ", node.FlowInputPortCollection.GetPorts().Keys));
                if (!node.Graph.Enabled) throw new InvalidOperationException("GRAPH_DISABLED: enable the blueprint first (warudo_blueprint_enabled).");
                node.Graph.InvokeFlowAtInput(node, input, false);
                return new { node = DescribeNode(node), input, invoked = true };
            }
            throw new ArgumentException("Unsupported blueprint operation: " + op);
        }
    }
}
