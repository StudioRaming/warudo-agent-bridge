using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace StudioRaming.WarudoAgentBridge
{
    // <home>/config.json. Written by the installer from config.example.json and edited by the user.
    public sealed partial class AgentBridgePlugin
    {
        sealed class CallRule
        {
            public string TypePrefix;
            public HashSet<string> Methods;
        }

        sealed class RetentionPolicy
        {
            public double ResponsesHours = 1;
            public double EvidenceDays = 3;
            public int EvidenceMaxFiles = 200;
            public double SnapshotsDays = 14;
            public int SnapshotsKeep = 30;
            public double PosePreviewHours = 24;
            public double CommandLogMaxMB = 5;
        }

        // The virtual pointer (pointer.* operations). IdleReleaseMs ends a parked pointer nobody has used for that long.
        sealed class PointerPolicy
        {
            public bool Enabled = true;
            public double IdleReleaseMs = 15000;
        }

        List<CallRule> callRules = new List<CallRule>();
        RetentionPolicy retention = new RetentionPolicy();
        PointerPolicy pointerPolicy = new PointerPolicy();
        string configError;
        DateTime configStamp = DateTime.MinValue;

        string ConfigFile { get { return Path.Combine(home, "config.json"); } }

        void LoadConfig()
        {
            var rules = new List<CallRule>();
            var policy = new RetentionPolicy();
            var pointer = new PointerPolicy();
            configError = null;
            try
            {
                if (File.Exists(ConfigFile))
                {
                    configStamp = File.GetLastWriteTimeUtc(ConfigFile);
                    JObject config = ParseObject(File.ReadAllText(ConfigFile));
                    foreach (JObject rule in (config.SelectToken("componentCall.allow") as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string prefix = (string)rule["typePrefix"];
                        string[] methods = (rule["methods"] as JArray ?? new JArray()).Select(m => (string)m).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
                        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3 || methods.Length == 0)
                            throw new FormatException("componentCall.allow entries need typePrefix (3+ chars) and a non-empty methods list.");
                        rules.Add(new CallRule { TypePrefix = prefix, Methods = new HashSet<string>(methods, StringComparer.Ordinal) });
                    }
                    JObject r = config["retention"] as JObject;
                    if (r != null)
                    {
                        policy.ResponsesHours = Positive(r, "responsesHours", policy.ResponsesHours);
                        policy.EvidenceDays = Positive(r, "evidenceDays", policy.EvidenceDays);
                        policy.EvidenceMaxFiles = (int)Positive(r, "evidenceMaxFiles", policy.EvidenceMaxFiles);
                        policy.SnapshotsDays = Positive(r, "snapshotsDays", policy.SnapshotsDays);
                        policy.SnapshotsKeep = (int)Positive(r, "snapshotsKeep", policy.SnapshotsKeep);
                        policy.PosePreviewHours = Positive(r, "posePreviewHours", policy.PosePreviewHours);
                        policy.CommandLogMaxMB = Positive(r, "commandLogMaxMB", policy.CommandLogMaxMB);
                    }
                    JObject pointerConfig = config["pointer"] as JObject;
                    if (pointerConfig != null)
                    {
                        JToken enabled = pointerConfig["enabled"];
                        if (enabled != null && enabled.Type != JTokenType.Boolean) throw new FormatException("pointer.enabled must be true or false.");
                        if (enabled != null) pointer.Enabled = (bool)enabled;
                        double? idle = (double?)pointerConfig["idleReleaseMs"];
                        if (idle != null)
                        {
                            if (double.IsNaN(idle.Value) || idle.Value < 1000 || idle.Value > 600000) throw new FormatException("pointer.idleReleaseMs must be 1000..600000.");
                            pointer.IdleReleaseMs = idle.Value;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // A broken config keeps the safe defaults: no component calls allowed.
                configError = "config.json ignored: " + e.Message;
                rules.Clear();
                policy = new RetentionPolicy();
                pointer = new PointerPolicy();
            }
            callRules = rules;
            retention = policy;
            pointerPolicy = pointer;
        }

        void RefreshConfig()
        {
            DateTime stamp = File.Exists(ConfigFile) ? File.GetLastWriteTimeUtc(ConfigFile) : DateTime.MinValue;
            if (stamp != configStamp) LoadConfig();
        }

        static double Positive(JObject o, string key, double fallback)
        {
            double? value = (double?)o[key];
            if (value == null) return fallback;
            if (double.IsNaN(value.Value) || value.Value <= 0) throw new FormatException("retention." + key + " must be a positive number.");
            return value.Value;
        }
    }
}
