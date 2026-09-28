using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Warudo.Core;

namespace StudioRaming.WarudoAgentBridge
{
    // Keeps the bridge's own files bounded. Only files the bridge created are ever deleted.
    public sealed partial class AgentBridgePlugin
    {
        const string PosePreviewPrefix = "_AgentPose_";
        static readonly System.Text.RegularExpressions.Regex PosePreviewReference = new System.Text.RegularExpressions.Regex(@"_AgentPose_[0-9a-f]{8}_[0-9a-f]{8}\.anim");

        sealed class CleanupPlan
        {
            public string Root, AnimationsDir, ScenesDir;
            public RetentionPolicy Policy;
            public HashSet<string> Protected;
            public bool DryRun;
        }

        void ScheduleMaintenance()
        {
            RefreshConfig();
            CleanupPlan plan = NewCleanupPlan(false);
            Task.Run(() =>
            {
                try { Clean(plan); }
                catch (Exception e) { Debug.LogWarning("[WARUDO Agent Bridge] Maintenance failed: " + e.Message); }
            });
        }

        object RunMaintenance(bool dryRun)
        {
            RefreshConfig();
            return Clean(NewCleanupPlan(dryRun));
        }

        CleanupPlan NewCleanupPlan(bool dryRun)
        {
            string animations = null, scenes = null;
            try
            {
                animations = Context.PersistentDataManager.GetFullPath("CharacterAnimations");
                scenes = Context.PersistentDataManager.GetFullPath("Scenes");
            }
            catch (Exception) { }
            var protectedFiles = new HashSet<string>(ActivePoseFiles().Select(SamePath), StringComparer.OrdinalIgnoreCase);
            return new CleanupPlan { Root = root, AnimationsDir = animations, ScenesDir = scenes, Policy = retention, Protected = protectedFiles, DryRun = dryRun };
        }

        // WARUDO's data paths use '/', FileInfo.FullName uses '\'. Compare paths only in this form.
        static string SamePath(string path)
        {
            return Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        static object Clean(CleanupPlan plan)
        {
            DateTime now = DateTime.UtcNow;
            var report = new Dictionary<string, object>();
            var p = plan.Policy;

            report["responses"] = Delete(plan, Files(Path.Combine(plan.Root, "responses"), "*.json").Where(f => now - f.LastWriteTimeUtc > TimeSpan.FromHours(p.ResponsesHours)));

            var evidence = Files(Path.Combine(plan.Root, "evidence"), "*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            report["evidence"] = Delete(plan, evidence.Where((f, index) => index >= p.EvidenceMaxFiles || now - f.LastWriteTimeUtc > TimeSpan.FromDays(p.EvidenceDays)));

            // Snapshot files share a 32-hex id prefix: <id>.json, <id>.meta.json, <id>.saved-scene.json.
            var snapshotGroups = Files(Path.Combine(plan.Root, "snapshots"), "*.json")
                .GroupBy(f => f.Name.Length >= 32 ? f.Name.Substring(0, 32) : f.Name)
                .OrderByDescending(g => g.Max(f => f.LastWriteTimeUtc)).ToList();
            report["snapshots"] = Delete(plan, snapshotGroups.Skip(p.SnapshotsKeep)
                .Where(g => now - g.Max(f => f.LastWriteTimeUtc) > TimeSpan.FromDays(p.SnapshotsDays)).SelectMany(g => g));

            if (!string.IsNullOrEmpty(plan.AnimationsDir))
            {
                // A saved scene may still use a preview pose as its idle animation; never delete those.
                foreach (FileInfo scene in Files(plan.ScenesDir, "*.json"))
                {
                    try
                    {
                        foreach (System.Text.RegularExpressions.Match match in PosePreviewReference.Matches(File.ReadAllText(scene.FullName)))
                            plan.Protected.Add(SamePath(Path.Combine(plan.AnimationsDir, match.Value)));
                    }
                    catch (IOException) { }
                }
                report["posePreviews"] = Delete(plan, Files(plan.AnimationsDir, PosePreviewPrefix + "*")
                    .Where(f => !plan.Protected.Contains(SamePath(StripBundle(f.FullName))) && now - f.LastWriteTimeUtc > TimeSpan.FromHours(p.PosePreviewHours)));
            }

            report["temporary"] = Delete(plan, new[] { "requests", "responses", "evidence", "snapshots" }
                .SelectMany(sub => Files(Path.Combine(plan.Root, sub), "*.tmp").Concat(Files(Path.Combine(plan.Root, sub), "*.withdrawn")))
                .Where(f => now - f.LastWriteTimeUtc > TimeSpan.FromHours(1)));

            string log = Path.Combine(plan.Root, "commands.jsonl");
            bool rotated = false;
            if (File.Exists(log) && new FileInfo(log).Length > p.CommandLogMaxMB * 1024 * 1024)
            {
                rotated = true;
                if (!plan.DryRun)
                {
                    try
                    {
                        File.Copy(log, Path.Combine(plan.Root, "commands.1.jsonl"), true);
                        File.WriteAllText(log, "");
                    }
                    catch (IOException) { rotated = false; }
                }
            }
            report["commandLogRotated"] = rotated;
            report["dryRun"] = plan.DryRun;
            return report;
        }

        static string StripBundle(string path)
        {
            return path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - ".bundle".Length) : path;
        }

        static IEnumerable<FileInfo> Files(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return Enumerable.Empty<FileInfo>();
            try { return new DirectoryInfo(dir).GetFiles(pattern); }
            catch (IOException) { return Enumerable.Empty<FileInfo>(); }
        }

        // A dry run also names what it would delete (first 50), so an agent can check before cleaning.
        static object Delete(CleanupPlan plan, IEnumerable<FileInfo> files)
        {
            int count = 0;
            long bytes = 0;
            var names = new List<string>();
            foreach (FileInfo file in files.ToList())
            {
                try
                {
                    long length = file.Length;
                    if (!plan.DryRun) file.Delete();
                    count++;
                    bytes += length;
                    if (plan.DryRun && names.Count < 50) names.Add(file.Name);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (plan.DryRun) return new { files = count, bytes, names };
            return new { files = count, bytes };
        }
    }
}
