using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Scenes;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;
using Warudo.Plugins.Core.Assets.Environment;
using Warudo.Plugins.Core.Assets.Prop;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        Asset AssetById(JObject p)
        {
            Guid id;
            if (!Guid.TryParse(Required(p, "assetId"), out id)) throw new ArgumentException("Invalid assetId.");
            Asset asset = Context.OpenedScene == null ? null : Context.OpenedScene.GetAsset(id);
            if (asset == null) throw new ArgumentException("ASSET_NOT_FOUND: asset no longer exists. Refresh warudo_asset_list.");
            return asset;
        }

        object DescribeAsset(Asset asset)
        {
            var game = asset as GameObjectAsset;
            var fromSource = asset as FromSourceGameObjectAsset;
            return new
            {
                id = asset.Id.ToString(), name = asset.Name, type = asset.GetType().FullName, active = asset.Active,
                enabled = game == null ? (bool?)null : game.Enabled,
                source = fromSource == null ? null : fromSource.Source,
                gameObjectId = game == null || game.GameObject == null ? (int?)null : game.GameObject.GetInstanceID(),
                localPosition = game == null || game.Transform == null ? null : Wire(game.Transform.Position),
                localRotation = game == null || game.Transform == null ? null : Wire(game.Transform.Rotation),
                localScale = game == null || game.Transform == null ? null : Wire(game.Transform.Scale),
                worldPosition = game == null || game.GameObject == null ? null : Wire(game.GameObject.transform.position)
            };
        }

        async UniTask<object> AssetOperation(string op, JObject p)
        {
            if (Context.OpenedScene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            if (op == "asset.types") return AssetTypes();
            if (op == "asset.add") return await AssetAdd(p);
            if (op == "asset.delete") return AssetDelete(p);
            if (op == "asset.list")
            {
                string query = (string)p["query"] ?? "";
                return Context.OpenedScene.GetAssets().Values.Where(a => a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    a.GetType().FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Select(DescribeAsset).ToArray();
            }
            Asset asset = AssetById(p);
            if (op == "asset.get") return DescribeAsset(asset);
            if (op == "asset.fields") return EntityFields(asset, p, DescribeAsset(asset));
            if (op == "asset.patch") return EntityPatch(asset, p["fields"] as JObject, DescribeAsset(asset));
            if (op == "asset.select") { Context.Service.SetSelectedAsset(asset.Id); return DescribeAsset(asset); }
            var game = asset as GameObjectAsset;
            if (game == null) throw new ArgumentException("Asset does not have a GameObject.");
            object before = DescribeAsset(asset);
            if (op == "asset.transform")
            {
                if (game.GameObject == null) throw new InvalidOperationException("Asset has no loaded GameObject.");
                Transform transform = game.GameObject.transform;
                string space = (string)p["space"] ?? "world";
                if (space != "world" && space != "local") throw new ArgumentException("space must be world or local.");
                bool world = space == "world";
                Vector3? requestedPosition = p["position"] == null ? (Vector3?)null : Vector(p["position"]);
                Vector3? requestedRotation = p["rotation"] == null ? (Vector3?)null : Vector(p["rotation"]);
                Vector3? requestedScale = p["scale"] == null ? (Vector3?)null : Vector(p["scale"]);
                if (requestedScale.HasValue && (requestedScale.Value.x <= 0 || requestedScale.Value.y <= 0 || requestedScale.Value.z <= 0))
                    throw new ArgumentException("Scale must be positive.");

                // Resolve every value before touching the asset so a bad later value cannot leave an earlier write behind.
                Vector3? nextPosition = requestedPosition.HasValue
                    ? (Vector3?)(world && transform.parent != null ? transform.parent.InverseTransformPoint(requestedPosition.Value) : requestedPosition.Value)
                    : null;
                Quaternion? nextRotation = requestedRotation.HasValue
                    ? (Quaternion?)(world && transform.parent != null ? Quaternion.Inverse(transform.parent.rotation) * Quaternion.Euler(requestedRotation.Value) : Quaternion.Euler(requestedRotation.Value))
                    : null;
                Vector3 oldPosition = game.Transform.Position;
                Quaternion oldRotation = game.Transform.RotationQuaternion;
                Vector3 oldScale = game.Transform.Scale;
                try
                {
                    if (nextPosition.HasValue) game.Transform.Position = nextPosition.Value;
                    if (nextRotation.HasValue) game.Transform.RotationQuaternion = nextRotation.Value;
                    if (requestedScale.HasValue) game.Transform.Scale = requestedScale.Value;
                    game.Transform.ApplyAsLocalTransform(transform);
                }
                catch
                {
                    try
                    {
                        game.Transform.Position = oldPosition;
                        game.Transform.RotationQuaternion = oldRotation;
                        game.Transform.Scale = oldScale;
                        game.Transform.ApplyAsLocalTransform(transform);
                    }
                    catch (Exception) { }
                    throw;
                }
                game.BroadcastDataInput("Transform");
            }
            else if (op == "asset.enabled")
            {
                bool enabled = (bool?)p["enabled"] ?? throw new ArgumentException("enabled is required.");
                game.SetDataInput("Enabled", enabled, true);
            }
            else if (op == "asset.source")
            {
                // Loading is asynchronous in WARUDO: this is a dispatch receipt, not a load-success claim.
                string source = ValidateSource(Required(p, "source"), asset.GetType());
                game.SetDataInput("Source", source, true);
                return new { before, after = DescribeAsset(asset), dispatched = true, loaded = false };
            }
            else throw new ArgumentException("Unsupported asset operation: " + op);
            await UniTask.Yield();
            return new { before, after = DescribeAsset(asset), persisted = false };
        }

        static string ResourceTypeFor(Type assetType)
        {
            if (typeof(CharacterAsset).IsAssignableFrom(assetType)) return "Character";
            if (typeof(EnvironmentAsset).IsAssignableFrom(assetType)) return "Environment";
            if (typeof(PropAsset).IsAssignableFrom(assetType)) return "Prop";
            return null;
        }

        // A source must be a URI WARUDO itself lists for that asset type, or a file in WARUDO's own data folder.
        string ValidateSource(string source, Type assetType)
        {
            string resourceType = ResourceTypeFor(assetType);
            if (resourceType == null) throw new ArgumentException("source is supported only for character, environment and prop assets.");
            string decoded = Uri.UnescapeDataString(source ?? string.Empty);
            if (decoded.Contains("..") || decoded.Contains("\\") || decoded.Contains("\0") || decoded.Contains("?") || decoded.Contains("#"))
                throw new ArgumentException("source contains a forbidden sequence.");
            if (ResourceUris(resourceType).Contains(source)) return source;
            string scheme = resourceType == "Character" ? "character" : resourceType == "Environment" ? "environment" : "prop";
            string folder = resourceType == "Character" ? "Characters" : resourceType == "Environment" ? "Environments" : "Props";
            if (source.StartsWith(scheme + "://data/" + folder + "/", StringComparison.Ordinal))
            {
                // A file WARUDO has not listed yet: it must resolve to an existing file inside that data
                // folder. Loading a .warudo loads a mod, so a drive letter, a stream name (":"), a UNC
                // path or anything else that could point outside the folder is refused.
                string relative = decoded.Substring((scheme + "://data/").Length);
                if (relative.Contains(":") || relative.Contains("//") || Path.IsPathRooted(relative))
                    throw new ArgumentException("source contains a forbidden sequence.");
                string root = Path.GetFullPath(Context.PersistentDataManager.GetFullPath(folder)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Context.PersistentDataManager.GetFullPath(relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("source must stay inside WARUDO's " + folder + " folder.");
                if (!File.Exists(full)) throw new ArgumentException("No such file in WARUDO's " + folder + " folder: " + relative.Substring(folder.Length + 1));
                return source;
            }
            throw new ArgumentException("source must be one of the URIs from warudo_resource_list type=" + resourceType + ".");
        }
    }
}
