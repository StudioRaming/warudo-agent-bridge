using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Serializations;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        async UniTask<object> SceneOperation(string op, JObject p)
        {
            if (op == "scene.list") return Context.SceneManager.GetScenes();
            if (op == "scene.snapshots") return SnapshotList(Mathf.Clamp((int?)p["limit"] ?? 30, 1, 200));
            var scene = Context.OpenedScene;
            if (scene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            if (op == "scene.get")
            {
                var selected = scene.GetSelectedAsset();
                return new { name = scene.Name, selectedAsset = selected == null ? null : selected.Id.ToString(),
                    assets = scene.GetAssets().Values.Select(DescribeAsset).ToArray(), blueprints = scene.GetGraphs().Count };
            }
            if (op == "scene.snapshot") return new { snapshotId = SnapshotScene("manual") };
            if (op == "scene.open")
            {
                string name = Required(p, "name");
                if (!Context.SceneManager.HasScene(name)) throw new ArgumentException("Saved scene was not found: " + name);
                string snapshot = SnapshotScene("before-open");
                ForgetCharacterState();
                await Context.SceneManager.OpenScene(name);
                return new { name = Context.OpenedScene.Name, recoverySnapshot = snapshot, persisted = false };
            }
            if (op == "scene.restore")
            {
                string id = Required(p, "snapshotId");
                Guid valid;
                if (!Guid.TryParseExact(id, "N", out valid)) throw new ArgumentException("Invalid snapshotId.");
                string file = Path.Combine(root, "snapshots", id + ".json");
                if (!File.Exists(file)) throw new ArgumentException("Snapshot does not exist.");
                // Keep date-like strings in the scene (text fields, names) exactly as saved.
                var snapshot = JsonConvert.DeserializeObject<SerializedScene>(File.ReadAllText(file), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (snapshot == null) throw new InvalidOperationException("Snapshot is empty or invalid.");
                string before = SnapshotScene("before-restore");
                ForgetCharacterState();
                await Context.Instance.OpenScene(snapshot);
                return new { name = Context.OpenedScene.Name, recoverySnapshot = before, persisted = false };
            }
            if (op == "scene.save_as")
            {
                string name = Required(p, "name");
                bool exists = Context.SceneManager.HasScene(name);
                if (exists && (bool?)p["allowOverwrite"] != true) throw new InvalidOperationException("SCENE_EXISTS: pass allowOverwrite=true to replace it.");
                string snapshot = SnapshotScene("before-save-as");
                string previousSavedCopy = null;
                if (exists)
                {
                    string source = Context.PersistentDataManager.GetFullPath("Scenes/" + Context.SceneManager.GetSafeSceneName(name) + ".json");
                    previousSavedCopy = Path.Combine(root, "snapshots", Guid.NewGuid().ToString("N") + ".saved-scene.json");
                    File.Copy(source, previousSavedCopy, false);
                }
                await Context.SceneManager.SaveScene(name, scene.Serialize(false), !exists);
                return new { name, recoverySnapshot = snapshot, previousSavedCopy, persisted = true };
            }
            throw new ArgumentException("Unsupported scene operation: " + op);
        }

        string SnapshotScene(string reason)
        {
            if (Context.OpenedScene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene to snapshot.");
            string id = Guid.NewGuid().ToString("N");
            AtomicWrite(Path.Combine(root, "snapshots", id + ".json"), JsonConvert.SerializeObject(Context.OpenedScene.Serialize(false)));
            AtomicWrite(Path.Combine(root, "snapshots", id + ".meta.json"), JsonConvert.SerializeObject(new
            { id, reason, sessionId = session, scene = Context.OpenedScene.Name, utc = DateTime.UtcNow.ToString("o") }));
            return id;
        }

        object SnapshotList(int limit)
        {
            var dir = new DirectoryInfo(Path.Combine(root, "snapshots"));
            var metas = dir.Exists ? dir.GetFiles("*.meta.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).ToList() : new System.Collections.Generic.List<FileInfo>();
            return metas.Select(meta =>
            {
                JObject info;
                try { info = ParseObject(File.ReadAllText(meta.FullName)); }
                catch (Exception) { info = new JObject(); }
                string id = meta.Name.Substring(0, meta.Name.Length - ".meta.json".Length);
                var body = new FileInfo(Path.Combine(dir.FullName, id + ".json"));
                return new { snapshotId = id, reason = (string)info["reason"], scene = (string)info["scene"], utc = (string)info["utc"], bytes = body.Exists ? body.Length : 0 };
            }).ToArray();
        }
    }
}
