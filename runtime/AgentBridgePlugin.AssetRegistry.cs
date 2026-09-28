using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Warudo.Core;
using Warudo.Core.Scenes;
using Warudo.Plugins.Core.Assets;

namespace StudioRaming.WarudoAgentBridge
{
    public sealed partial class AgentBridgePlugin
    {
        object AssetTypes()
        {
            return Context.AssetTypeRegistry.GetRegisteredTypes().Values
                .Where(meta => meta != null && meta.AssetType != null && meta.Type != null)
                .OrderBy(meta => meta.AssetType.category)
                .ThenBy(meta => meta.AssetType.categoryOrder)
                .ThenBy(meta => meta.AssetType.title)
                .Select(meta => new
                {
                    typeId = meta.Id,
                    typeName = meta.Type.FullName,
                    title = meta.AssetType.title,
                    category = meta.AssetType.category,
                    categoryId = meta.AssetType.categoryId,
                    userModifiable = meta.AssetType.userModifiable,
                    singleton = meta.AssetType.singleton,
                    acceptsSource = ResourceTypeFor(meta.Type)
                }).ToArray();
        }

        async UniTask<object> AssetAdd(JObject p)
        {
            string typeId = Required(p, "typeId");
            var meta = Context.AssetTypeRegistry.GetTypeMeta(typeId);
            if (meta == null || meta.AssetType == null) throw new ArgumentException("Unknown asset typeId. Refresh warudo_asset_types.");
            if (!meta.AssetType.userModifiable) throw new InvalidOperationException("Asset type is not user-creatable.");
            string group = (string)p["group"];
            if (group != null && group.Length > 256) throw new ArgumentException("group is too long.");
            string requestedName = (string)p["name"];
            if (requestedName != null && (string.IsNullOrWhiteSpace(requestedName) || requestedName.Length > 256))
                throw new ArgumentException("name must be 1..256 characters.");
            // Validate everything before the asset exists, so a bad source cannot leave an empty asset behind.
            string source = (string)p["source"];
            if (source != null) ValidateSource(source, meta.Type);

            string recoverySnapshot = SnapshotScene("before-asset-add");
            Asset asset = Context.OpenedScene.AddAssetToGroup(typeId, group);
            if (requestedName != null) asset.Name = requestedName;
            if (source != null) asset.SetDataInput("Source", source, true);
            await UniTask.Yield();
            return new { created = true, dispatched = source != null, loaded = false, recoverySnapshot, asset = DescribeAsset(asset) };
        }

        object AssetDelete(JObject p)
        {
            string assetId = Required(p, "assetId");
            if (Required(p, "observedAssetId") != assetId) throw new ArgumentException("observedAssetId must match assetId exactly.");
            JObject expected = p["before"] as JObject;
            if (expected == null || (string)expected["id"] != assetId)
                throw new ArgumentException("before must be the warudo_asset_get readback for this exact assetId.");
            Asset asset = AssetById(p);
            if ((string)expected["name"] != asset.Name || (string)expected["type"] != asset.GetType().FullName)
                throw new InvalidOperationException("ASSET_CHANGED: the asset changed since the before readback; read it again.");
            object before = DescribeAsset(asset);
            string recoverySnapshot = SnapshotScene("before-asset-delete");
            Context.OpenedScene.RemoveAsset(asset.Id);
            return new { deleted = true, assetId, before, recoverySnapshot };
        }
    }
}
