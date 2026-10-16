using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Plugins;

namespace StudioRaming.WarudoAgentBridge
{
    // WARUDO Agent Bridge runtime. Runs inside WARUDO as a Playground plugin and serves
    // requests that the MCP server / CLI drop into <home>/runtime/requests.
    // https://github.com/StudioRaming/warudo-agent-bridge (MIT)
    [PluginType(Id = "b99388a5-0957-4e7e-bcce-949f657fdb56", Name = "WARUDO Agent Bridge",
        Author = "StudioRaming", Version = "0.2.0",
        Description = "Local MCP/CLI bridge that lets AI agents inspect and control WARUDO.")]
    public sealed partial class AgentBridgePlugin : Plugin
    {
        const string Version = "0.2.0";
        const int Protocol = 2;
        static readonly string[] Features = { "claim", "stopped-marker", "entity-paths", "resources", "character", "pose", "ik", "blueprint", "maintenance", "analysis-render", "plugin-settings", "pointer", "capture-tools" };

        string home, root, session, leaseOwner, activeRequest;
        DateTime leaseUntil;
        float nextHeartbeat, nextRequest, nextMaintenance;
        bool stopped, busy;
        long logSequence;
        readonly Queue<JObject> logs = new Queue<JObject>();
        readonly object logLock = new object();

        // Operations that never change WARUDO scene state. Everything else needs the write lease.
        static readonly HashSet<string> LeaseFreeOperations = new HashSet<string>
        {
            "status", "lease.status", "runtime.logs",
            "scene.list", "scene.get", "scene.snapshot", "scene.snapshots",
            "asset.types", "asset.fields", "asset.list", "asset.get", "resource.list", "plugin.list", "plugin.fields",
            "camera.get", "camera.capture", "screen.capture", "analysis.capture",
            "hierarchy.list", "object.get", "component.get", "render.globals",
            "character.expressions", "character.blendshapes", "character.pose.get", "character.pose.save", "character.ik.get",
            "graph.list", "graph.get", "node.fields",
            "maintenance.cleanup", "pointer.state", "ui.list", "ui.at", "ui.capture",
        };

        protected override void OnCreate()
        {
            base.OnCreate();
            string configured = Environment.GetEnvironmentVariable("WARUDO_AGENT_BRIDGE_HOME");
            home = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".warudo-agent-bridge")
                : Path.GetFullPath(configured);
            root = Path.Combine(home, "runtime");
            foreach (string sub in new[] { "requests", "responses", "evidence", "snapshots" }) Directory.CreateDirectory(Path.Combine(root, sub));
            session = Guid.NewGuid().ToString("N");
            LoadConfig();
            LoadPoseState();
            RestoreLease();
            Application.logMessageReceivedThreaded += ReceiveLog;
            RecoverInterruptedRequests();
            // A client reading state.json at this instant makes File.Replace fail; the first
            // OnUpdate writes the heartbeat again, so the bridge must not fail to start over it.
            try { WriteHeartbeat(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            nextMaintenance = Time.realtimeSinceStartup + 15f;
            Debug.Log("[WARUDO Agent Bridge] Ready version=" + Version + " session=" + session + " home=" + home);
        }

        protected override void OnDestroy()
        {
            stopped = true;
            Application.logMessageReceivedThreaded -= ReceiveLog;
            ReleaseCharacterOverrides();
            DestroyPointer();
            PersistLease();
            try { AtomicWrite(Path.Combine(root, "state.json"), Status(true).ToString(Formatting.None)); } catch (Exception) { }
            base.OnDestroy();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (stopped) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextHeartbeat)
            {
                nextHeartbeat = now + .5f;
                try { WriteHeartbeat(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (now >= nextMaintenance)
            {
                nextMaintenance = now + 1800f;
                ScheduleMaintenance();
            }
            PointerTick();
            if (busy || now < nextRequest) return;
            nextRequest = now + .05f;
            string path;
            try
            {
                path = Directory.GetFiles(Path.Combine(root, "requests"), "*.json").OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
            }
            catch (IOException) { return; }
            if (path != null) ProcessRequest(path).Forget();
        }

        JObject Status(bool stopping = false)
        {
            return JObject.FromObject(new
            {
                protocol = Protocol, version = Version, features = Features, sessionId = session,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id, stopped = stopping,
                utc = DateTime.UtcNow.ToString("o"), heartbeatEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                applicationFocused = Application.isFocused, runInBackground = Application.runInBackground, timeScale = Time.timeScale, pointerActive = PointerActive,
                frame = Time.frameCount, deltaTime = Time.unscaledDeltaTime,
                scene = Context.OpenedScene == null ? null : Context.OpenedScene.Name,
                busy, activeRequest,
                leaseOwner = DateTime.UtcNow < leaseUntil ? leaseOwner : null,
                leaseUntilUtc = DateTime.UtcNow < leaseUntil ? leaseUntil.ToString("o") : null,
                renderingPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null ? "Built-in" : "Scriptable",
                home, mailboxRoot = root, configError
            });
        }

        void WriteHeartbeat() { AtomicWrite(Path.Combine(root, "state.json"), Status().ToString(Formatting.None)); }

        // A Playground reload recreates the plugin. Carry an unexpired lease across it so the
        // agent that held it keeps writing without a gap another agent could slip into.
        string LeaseFile { get { return Path.Combine(root, "lease.json"); } }

        void PersistLease()
        {
            try
            {
                if (DateTime.UtcNow < leaseUntil && leaseOwner != null)
                    AtomicWrite(LeaseFile, JsonConvert.SerializeObject(new { owner = leaseOwner, untilUtc = leaseUntil.ToString("o") }));
                else if (File.Exists(LeaseFile)) File.Delete(LeaseFile);
            }
            catch (Exception) { }
        }

        void RestoreLease()
        {
            try
            {
                if (!File.Exists(LeaseFile)) return;
                JObject saved = ParseObject(File.ReadAllText(LeaseFile));
                File.Delete(LeaseFile);
                DateTime until;
                string owner = (string)saved["owner"];
                if (!string.IsNullOrWhiteSpace(owner) && owner.Length <= 128 &&
                    DateTime.TryParse((string)saved["untilUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until) &&
                    until > DateTime.UtcNow && until < DateTime.UtcNow.AddSeconds(601))
                {
                    leaseOwner = owner;
                    leaseUntil = until;
                }
            }
            catch (Exception) { }
        }

        // A request left claimed by a previous session was interrupted mid-run. Answer it so the
        // waiting client learns the outcome is unknown instead of timing out.
        void RecoverInterruptedRequests()
        {
            foreach (string claimed in Directory.GetFiles(Path.Combine(root, "requests"), "*.claimed"))
            {
                try
                {
                    string id = Path.GetFileNameWithoutExtension(claimed);
                    Guid parsed;
                    string output = Path.Combine(root, "responses", id + ".json");
                    if (Guid.TryParseExact(id, "N", out parsed) && !File.Exists(output))
                    {
                        AtomicWrite(output, JsonConvert.SerializeObject(new { requestId = id, sessionId = session, ok = false, finishedUtc = DateTime.UtcNow.ToString("o"),
                            error = "INTERRUPTED: WARUDO stopped or reloaded the bridge while this request was running. Its effect is unknown; read the state back." }));
                    }
                    File.Delete(claimed);
                }
                catch (IOException) { }
            }
        }

        async UniTask ProcessRequest(string path)
        {
            busy = true;
            string id = Path.GetFileNameWithoutExtension(path);
            string claimed = Path.Combine(Path.GetDirectoryName(path), id + ".claimed");
            // Protocol 2: claim by rename. If the client already withdrew the request, the move fails and nothing runs.
            try { File.Move(path, claimed); }
            catch (IOException) { busy = false; return; }
            catch (UnauthorizedAccessException) { busy = false; return; }

            string operation = "unknown";
            string owner = null;
            JObject response = null;
            try
            {
                Guid parsed;
                if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidOperationException("MALFORMED_REQUEST: request filename must be a GUID.");
                if (File.Exists(Path.Combine(root, "responses", id + ".json"))) return; // A completed request never runs twice.
                if (new FileInfo(claimed).Length > 65536) throw new InvalidOperationException("REQUEST_TOO_LARGE: request exceeds 64 KiB.");
                JObject request = ParseObject(File.ReadAllText(claimed));
                int protocol = (int?)request["protocol"] ?? 0;
                if ((protocol != 1 && protocol != 2) || (string)request["requestId"] != id) throw new InvalidOperationException("MALFORMED_REQUEST: protocol/requestId mismatch.");
                if ((string)request["sessionId"] != session) throw new InvalidOperationException("STALE_SESSION: the bridge restarted; read status and send a new request.");
                DateTime created;
                if (!DateTime.TryParse((string)request["createdUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out created))
                    throw new InvalidOperationException("MALFORMED_REQUEST: createdUtc must be an ISO-8601 timestamp.");
                if (Math.Abs((DateTime.UtcNow - created).TotalSeconds) > 60) throw new InvalidOperationException("REQUEST_EXPIRED: request age exceeds 60 seconds.");
                owner = (string)request["owner"];
                if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128) throw new InvalidOperationException("MALFORMED_REQUEST: owner must be 1..128 characters.");
                operation = (string)request["operation"];
                if (string.IsNullOrWhiteSpace(operation) || operation.Length > 128) throw new InvalidOperationException("MALFORMED_REQUEST: operation is required.");
                JToken rawParams = request["params"];
                if (rawParams != null && rawParams.Type != JTokenType.Object) throw new InvalidOperationException("MALFORMED_REQUEST: params must be an object.");
                JObject args = rawParams as JObject ?? new JObject();
                activeRequest = id;
                if (RequiresLease(operation, args) && (DateTime.UtcNow >= leaseUntil || leaseOwner != owner))
                    throw new InvalidOperationException("LEASE_REQUIRED: acquire the write lease with the same owner first." +
                        (DateTime.UtcNow < leaseUntil ? " Held by " + leaseOwner + " until " + leaseUntil.ToString("o") + "." : ""));
                object data = await Dispatch(operation, args, owner);
                response = JObject.FromObject(new { requestId = id, sessionId = session, operation, owner, ok = true, finishedUtc = DateTime.UtcNow.ToString("o"), data });
            }
            catch (Exception e)
            {
                response = JObject.FromObject(new { requestId = id, sessionId = session, operation, owner, ok = false, finishedUtc = DateTime.UtcNow.ToString("o"), error = e.GetBaseException().Message });
            }
            finally
            {
                try
                {
                    Guid safe;
                    if (response != null && Guid.TryParseExact(id, "N", out safe))
                    {
                        AtomicWrite(Path.Combine(root, "responses", id + ".json"), response.ToString(Formatting.None));
                        File.AppendAllText(Path.Combine(root, "commands.jsonl"), JsonConvert.SerializeObject(new
                        { requestId = id, sessionId = session, operation, owner, ok = (bool)response["ok"], utc = DateTime.UtcNow.ToString("o") }) + "\n");
                    }
                    File.Delete(claimed);
                }
                catch (IOException e) { Debug.LogWarning("[WARUDO Agent Bridge] Result persistence failed: " + e.Message); }
                activeRequest = null;
                busy = false;
            }
        }

        async UniTask<object> Dispatch(string op, JObject p, string owner)
        {
            switch (op)
            {
                case "status":
                case "lease.status":
                    return Status();
                case "lease.acquire":
                {
                    if (DateTime.UtcNow < leaseUntil && leaseOwner != owner) throw new InvalidOperationException("LEASE_BUSY: held by " + leaseOwner + " until " + leaseUntil.ToString("o"));
                    float seconds = (float?)p["seconds"] ?? 120;
                    if (float.IsNaN(seconds) || float.IsInfinity(seconds)) throw new ArgumentException("Lease seconds must be finite.");
                    leaseOwner = owner;
                    leaseUntil = DateTime.UtcNow.AddSeconds(Mathf.Clamp(seconds, 5, 600));
                    return Status();
                }
                case "lease.release":
                    if (DateTime.UtcNow < leaseUntil && leaseOwner != owner) throw new InvalidOperationException("LEASE_BUSY: the lease belongs to " + leaseOwner);
                    leaseUntil = DateTime.MinValue;
                    leaseOwner = null;
                    if (PointerActive) ReleasePointer("lease released").Forget();
                    return Status();
                case "runtime.logs":
                {
                    long after = (long?)p["after"] ?? 0;
                    int limit = Mathf.Clamp((int?)p["limit"] ?? 50, 1, 500);
                    lock (logLock) return new { cursor = logSequence, entries = logs.Where(l => (long)l["sequence"] > after).Take(limit).ToArray() };
                }
                case "scene.list":
                case "scene.get":
                case "scene.snapshot":
                case "scene.snapshots":
                case "scene.open":
                case "scene.restore":
                case "scene.save_as":
                    return await SceneOperation(op, p);
                case "asset.list":
                case "asset.types":
                case "asset.get":
                case "asset.fields":
                case "asset.patch":
                case "asset.add":
                case "asset.delete":
                case "asset.select":
                case "asset.transform":
                case "asset.enabled":
                case "asset.source":
                    return await AssetOperation(op, p);
                case "plugin.list":
                case "plugin.fields":
                case "plugin.patch":
                    return await PluginOperation(op, p);
                case "entity.trigger":
                    return EntityTrigger(p);
                case "resource.list":
                    return ResourceList(p);
                case "character.expressions":
                case "character.expression":
                case "character.blendshapes":
                case "character.blendshape":
                case "character.animation":
                case "character.ik.get":
                case "character.ik.set":
                    return await CharacterOperation(op, p, owner);
                case "character.pose.get":
                case "character.pose.set":
                case "character.pose.clear":
                case "character.pose.save":
                    return await PoseOperation(op, p);
                case "graph.list":
                case "graph.get":
                case "graph.enabled":
                case "node.fields":
                case "node.patch":
                case "node.flow":
                    return GraphOperation(op, p);
                case "camera.get":
                case "camera.capture":
                case "camera.pose":
                case "camera.orbit":
                case "camera.pan":
                case "camera.zoom":
                case "camera.restore":
                case "viewport.pick":
                    return await CameraOperation(op, p);
                case "screen.capture":
                    return await ScreenOperation(op, p);
                case "analysis.capture":
                    return await AnalysisCapture(p);
                case "hierarchy.list":
                case "object.get":
                case "object.transform":
                case "object.active":
                case "component.get":
                case "component.patch":
                case "component.call":
                    return ObjectOperation(op, p);
                case "render.globals":
                    return RenderGlobals(p);
                case "input.key":
                    return SendKey(p);
                case "pointer.play":
                case "pointer.release":
                case "pointer.state":
                    return await PointerOperation(op, p, owner);
                case "ui.list":
                case "ui.at":
                case "ui.set_text":
                    return UiOperation(op, p);
                case "ui.capture":
                    return await UiCapture(p);
                case "maintenance.cleanup":
                    return RunMaintenance((bool?)p["dryRun"] ?? true);
                default:
                    throw new InvalidOperationException("UNKNOWN_OPERATION: " + op + ". Update the MCP server or the Playground runtime so both are the same version.");
            }
        }

        bool RequiresLease(string op, JObject p)
        {
            // Replacing an existing file is a change like any other: hold the lease for it.
            if (op == "character.pose.save" && (bool?)p["overwrite"] == true) return true;
            if (LeaseFreeOperations.Contains(op) || op.StartsWith("lease.", StringComparison.Ordinal)) return false;
            if (op == "viewport.pick" && (bool?)p["select"] == false) return false;
            return true;
        }

        static readonly Regex SecretValue = new Regex(@"(?i)\b(token|password|passwd|secret|authorization|cookie|credential|api[_-]?key)(\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled);
        static readonly Regex BearerValue = new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled);

        static string Redact(string message)
        {
            return BearerValue.Replace(SecretValue.Replace(message ?? "", "$1$2REDACTED"), "Bearer REDACTED");
        }

        void ReceiveLog(string message, string trace, LogType kind)
        {
            message = Redact(message);
            if (message.Length > 4096) message = message.Substring(0, 4096);
            lock (logLock)
            {
                logs.Enqueue(JObject.FromObject(new { sequence = ++logSequence, utc = DateTime.UtcNow.ToString("o"), level = kind.ToString(), message }));
                while (logs.Count > 2000) logs.Dequeue();
            }
        }

        // Field and property names that look like credentials are never read or written.
        static bool Sensitive(string value)
        {
            string lower = (value ?? "").ToLowerInvariant();
            return new[] { "token", "password", "secret", "authorization", "cookie", "credential", "api_key", "apikey" }.Any(lower.Contains);
        }

        // JObject.Parse turns ISO-looking strings into dates and back into "MM/dd/yyyy" text.
        // Every JSON file and request is read with that off, so strings stay exactly as sent.
        static JToken ParseJson(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                return JToken.ReadFrom(reader);
        }

        static JObject ParseObject(string text)
        {
            return ParseJson(text) as JObject ?? throw new FormatException("Expected a JSON object.");
        }

        static void AtomicWrite(string path, string value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, value);
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        static string Required(JObject p, string key)
        {
            string value = (string)p[key];
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException(key + " is required.");
            return value;
        }

        static Vector3 Vector(JToken token)
        {
            if (!(token is JArray) || token.Count() != 3) throw new ArgumentException("Expected a 3-number vector.");
            float[] v = token.ToObject<float[]>();
            if (v.Any(x => float.IsNaN(x) || float.IsInfinity(x) || Mathf.Abs(x) > 100000)) throw new ArgumentException("Invalid vector.");
            return new Vector3(v[0], v[1], v[2]);
        }

        static float[] Wire(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    }
}
