using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Scenes;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;

namespace StudioRaming.WarudoAgentBridge
{
    // Character expressions, blendshape overrides, animations and IK.
    public sealed partial class AgentBridgePlugin
    {
        const string AnimationUriPrefix = "character-animation://data/CharacterAnimations/";
        static readonly string[] IkChains = { "Spine", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };

        // Blendshape weights held on top of tracking and expressions, through WARUDO's own override hook.
        struct HeldWeight
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public float Weight;
        }

        sealed class BlendShapeOverride : IBlendShapeEntry
        {
            public string Mesh, Shape;
            public float Value;
            // Rendered weights from before the bridge held this shape. WARUDO leaves a shape that
            // nothing drives at its last weight, so these are put back when the hold ends.
            public List<HeldWeight> Before;
            public string Key { get { return (Mesh ?? "*") + "/" + Shape; } }
            public bool GetUseVRMBlendShapeProxy() { return false; }
            public bool GetApplyToAllSkinnedMeshes() { return Mesh == null; }
            public string GetTargetSkinnedMesh() { return Mesh ?? ""; }
            public string GetBlendShape() { return Shape; }
            public float GetValue() { return Value; }
        }

        sealed class BlendShapeOverrides : IBlendShapeEntryProvider
        {
            public readonly Dictionary<string, BlendShapeOverride> Entries = new Dictionary<string, BlendShapeOverride>(StringComparer.Ordinal);
            public IEnumerable<IBlendShapeEntry> ProvideBlendShapeEntries() { return Entries.Values.Cast<IBlendShapeEntry>().ToList(); }
        }

        readonly Dictionary<Guid, BlendShapeOverrides> blendShapeOverrides = new Dictionary<Guid, BlendShapeOverrides>();

        CharacterAsset CharacterById(JObject p)
        {
            if (Context.OpenedScene == null) throw new InvalidOperationException("NO_SCENE: no WARUDO scene is open.");
            if (!string.IsNullOrEmpty((string)p["assetId"])) return AssetById(p) as CharacterAsset ?? throw new ArgumentException("assetId is not a character asset.");
            var selected = Context.OpenedScene.GetSelectedAsset() as CharacterAsset;
            if (selected != null) return selected;
            var characters = Context.OpenedScene.GetAssets().Values.OfType<CharacterAsset>().ToArray();
            if (characters.Length == 1) return characters[0];
            throw new ArgumentException(characters.Length == 0 ? "NO_CHARACTER: the scene has no character asset." :
                "AMBIGUOUS_CHARACTER: pass assetId. Characters: " + string.Join(", ", characters.Select(c => c.Name + " (" + c.Id + ")")));
        }

        static CharacterAsset RequireLoaded(CharacterAsset character)
        {
            if (!character.Active || character.Animator == null) throw new InvalidOperationException("CHARACTER_NOT_LOADED: " + character.Name + " is not loaded (set its source and wait with warudo_asset_wait_loaded).");
            return character;
        }

        void ReleaseCharacterOverrides()
        {
            foreach (var pair in blendShapeOverrides)
            {
                var character = Context.OpenedScene == null ? null : Context.OpenedScene.GetAsset(pair.Key) as CharacterAsset;
                if (character != null) character.RemoveOverrideBlendShapeEntryProvider(pair.Value);
                PutBack(pair.Value.Entries.Values);
            }
            blendShapeOverrides.Clear();
        }

        static void PutBack(IEnumerable<BlendShapeOverride> entries)
        {
            foreach (BlendShapeOverride entry in entries)
                foreach (HeldWeight held in entry.Before ?? new List<HeldWeight>())
                    if (held.Renderer != null) held.Renderer.SetBlendShapeWeight(held.Index, held.Weight);
        }

        // The weights a new hold must restore later. A renderer another hold already covers keeps
        // that hold's original weight, not the overridden one it shows now.
        static List<HeldWeight> WeightsBefore(CharacterAsset c, string mesh, string shape, IEnumerable<BlendShapeOverride> existing)
        {
            var weights = new List<HeldWeight>();
            foreach (var pair in c.BlendShapes ?? new Dictionary<string, List<string>>())
            {
                if ((mesh != null && pair.Key != mesh) || !pair.Value.Contains(shape)) continue;
                SkinnedMeshRenderer renderer = MeshRendererOf(c, pair.Key);
                int index = renderer == null ? -1 : renderer.sharedMesh.GetBlendShapeIndex(shape);
                if (index < 0) continue;
                float weight = renderer.GetBlendShapeWeight(index);
                foreach (BlendShapeOverride other in existing)
                    foreach (HeldWeight held in other.Before ?? new List<HeldWeight>())
                        if (held.Renderer == renderer && held.Index == index) weight = held.Weight;
                weights.Add(new HeldWeight { Renderer = renderer, Index = index, Weight = weight });
            }
            return weights;
        }

        // Called before a scene change: the characters these refer to are about to be destroyed.
        // Pose state is keyed by character id and kept, so returning to a scene can still clear its pose.
        void ForgetCharacterState()
        {
            ReleaseCharacterOverrides();
        }

        AnimationClip ResolveAnimation(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) throw new ArgumentException("animation URI is empty.");
            if (!uri.StartsWith(AnimationUriPrefix, StringComparison.Ordinal) && !ResourceUris("CharacterAnimation").Contains(uri))
                throw new ArgumentException("Animation must be a URI from warudo_resource_list type=CharacterAnimation.");
            AnimationClip clip = Context.ResourceManager.ResolveResourceUri<AnimationClip>(uri);
            if (clip == null) throw new InvalidOperationException("ANIMATION_NOT_LOADED: WARUDO could not load " + uri);
            return clip;
        }

        // Whether an idle URI that WARUDO itself stored can still be played. A data clip is checked
        // on disk (no load); a built-in resource asks WARUDO's resolver.
        bool AnimationExists(string uri)
        {
            try
            {
                if (uri.StartsWith(AnimationUriPrefix, StringComparison.Ordinal))
                {
                    string relative = uri.Substring(AnimationUriPrefix.Length);
                    return relative.Length > 0 && !relative.Contains("..") && File.Exists(Context.PersistentDataManager.GetFullPath("CharacterAnimations/" + relative));
                }
                return Context.ResourceManager.ResolveResourceUri<AnimationClip>(uri) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        async UniTask<object> CharacterOperation(string op, JObject p, string owner)
        {
            CharacterAsset c = CharacterById(p);
            switch (op)
            {
                case "character.expressions":
                    return new
                    {
                        character = DescribeAsset(c),
                        expressions = (c.Expressions ?? new CharacterAsset.ExpressionData[0]).Select(e => new { name = e.Name, layer = e.Layer, active = c.IsExpressionActive(e.Name) }).ToArray(),
                        active = c.ActiveExpressions == null ? new string[0] : c.ActiveExpressions.ToArray()
                    };
                case "character.expression":
                {
                    string action = Required(p, "action");
                    string[] before = c.ActiveExpressions == null ? new string[0] : c.ActiveExpressions.ToArray();
                    string name = action == "exitAll" ? null : Required(p, "name");
                    bool transient = (bool?)p["transient"] ?? false;
                    if (action == "exitAll") c.ExitAllExpressions(true);
                    else
                    {
                        if (!c.HasExpression(name))
                            throw new ArgumentException("Unknown expression '" + name + "'. Expressions: " + string.Join(", ", c.Expressions.Select(e => e.Name)));
                        if (action == "enter") c.EnterExpression(name, transient);
                        else if (action == "exit") c.ExitExpression(name);
                        else throw new ArgumentException("action must be enter, exit or exitAll.");
                    }
                    // Exits fade out and WARUDO updates the active list when the fade ends; wait
                    // (up to 2 s) so "after" is what the character shows.
                    float deadline = Time.realtimeSinceStartup + 2f;
                    Func<bool> done = action == "exitAll" ? (Func<bool>)(() => c.ActiveExpressions == null || c.ActiveExpressions.Count() == 0)
                        : action == "exit" ? () => !c.IsExpressionActive(name)
                        : () => transient || c.IsExpressionActive(name);
                    do await UniTask.Yield(); while (!done() && Time.realtimeSinceStartup < deadline);
                    return new { character = c.Name, action, before, after = c.ActiveExpressions == null ? new string[0] : c.ActiveExpressions.ToArray(), settled = done() };
                }
                case "character.blendshapes":
                    return BlendShapeList(RequireLoaded(c), p);
                case "character.blendshape":
                    return SetBlendShapes(RequireLoaded(c), p);
                case "character.animation":
                {
                    RequireLoaded(c);
                    string idle = (string)p["idle"], oneShot = (string)p["oneShot"];
                    if (string.IsNullOrEmpty(idle) == string.IsNullOrEmpty(oneShot)) throw new ArgumentException("Pass exactly one of idle or oneShot.");
                    if (!string.IsNullOrEmpty(idle))
                    {
                        ResolveAnimation(idle); // Fail here, not inside WARUDO, which would reset the idle to its default.
                        string before = c.DefaultIdleAnimation;
                        await WaitIdleListed(idle); // a clip saved a moment ago may not be in WARUDO's cached list yet
                        c.SetDataInput("DefaultIdleAnimation", idle, true);
                        return new { character = c.Name, idle, before, persisted = false };
                    }
                    AnimationClip clip = ResolveAnimation(oneShot);
                    float weight = Mathf.Clamp01((float?)p["weight"] ?? 1f);
                    float speed = Mathf.Clamp((float?)p["speed"] ?? 1f, 0.05f, 5f);
                    var allParts = (CharacterAsset.AnimationMaskedBodyPart[])Enum.GetValues(typeof(CharacterAsset.AnimationMaskedBodyPart));
                    c.PlayOneShotAnimation(clip, allParts, weight, speed, false, null);
                    return new { character = c.Name, oneShot, lengthSeconds = clip.length / speed, weight, speed };
                }
                case "character.ik.get":
                    return new { character = DescribeAsset(c), chains = IkChains.Select(chain => DescribeIk(c, chain)).ToArray() };
                case "character.ik.set":
                    return await SetIk(RequireLoaded(c), p);
            }
            throw new ArgumentException("Unsupported character operation: " + op);
        }

        object BlendShapeList(CharacterAsset c, JObject p)
        {
            string mesh = (string)p["mesh"];
            string query = ((string)p["query"] ?? "").Trim();
            int offset = Math.Max(0, (int?)p["offset"] ?? 0);
            int limit = Mathf.Clamp((int?)p["limit"] ?? 200, 1, 500);
            BlendShapeOverrides held;
            blendShapeOverrides.TryGetValue(c.Id, out held);
            var renderers = new Dictionary<string, SkinnedMeshRenderer>();
            var rows = (c.BlendShapes ?? new Dictionary<string, List<string>>())
                .Where(m => string.IsNullOrEmpty(mesh) || m.Key == mesh)
                .SelectMany(m => m.Value.Select(shape => new { mesh = m.Key, shape }))
                .Where(r => query.Length == 0 || r.shape.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(r =>
                {
                    SkinnedMeshRenderer renderer;
                    if (!renderers.TryGetValue(r.mesh, out renderer)) renderers[r.mesh] = renderer = MeshRendererOf(c, r.mesh);
                    BlendShapeOverride o = null;
                    if (held != null && !held.Entries.TryGetValue(r.mesh + "/" + r.shape, out o)) held.Entries.TryGetValue("*/" + r.shape, out o);
                    return new { r.mesh, name = r.shape, weight = RenderedWeight(c, renderer, r.mesh, r.shape), overridden = o == null ? (float?)null : o.Value };
                }).ToArray();
            var page = rows.Skip(offset).Take(limit).ToArray();
            int next = offset + page.Length;
            return new { character = c.Name, meshes = (c.BlendShapes ?? new Dictionary<string, List<string>>()).Keys.ToArray(), total = rows.Length, offset, nextOffset = next < rows.Length ? (int?)next : null, blendShapes = page };
        }

        // WARUDO keys skinned meshes by their path below the model root.
        static SkinnedMeshRenderer MeshRendererOf(CharacterAsset c, string key)
        {
            foreach (Transform root in new[] { c.Animator == null ? null : c.Animator.transform, c.GameObject == null ? null : c.GameObject.transform })
            {
                Transform found = root == null ? null : root.Find(key);
                SkinnedMeshRenderer renderer = found == null ? null : found.GetComponent<SkinnedMeshRenderer>();
                if (renderer != null && renderer.sharedMesh != null) return renderer;
            }
            return null;
        }

        // The weight (0..1) the mesh rendered last frame. WARUDO's own record only covers shapes that
        // tracking or expressions drove, so it is the fallback.
        static float? RenderedWeight(CharacterAsset c, SkinnedMeshRenderer renderer, string mesh, string shape)
        {
            int index = renderer == null ? -1 : renderer.sharedMesh.GetBlendShapeIndex(shape);
            if (index >= 0) return (float)Math.Round(renderer.GetBlendShapeWeight(index) / 100f, 4);
            Dictionary<string, float> last;
            float value;
            if (c.LastBlendShapes != null && c.LastBlendShapes.TryGetValue(mesh, out last) && last.TryGetValue(shape, out value)) return value;
            return null;
        }

        object SetBlendShapes(CharacterAsset c, JObject p)
        {
            string mesh = string.IsNullOrEmpty((string)p["mesh"]) ? null : (string)p["mesh"];
            JObject values = p["values"] as JObject;
            bool clear = (bool?)p["clear"] ?? false;
            if (!clear && (values == null || values.Count == 0)) throw new ArgumentException("Pass values and/or clear=true.");
            var shapes = c.BlendShapes ?? new Dictionary<string, List<string>>();
            if (mesh != null && !shapes.ContainsKey(mesh)) throw new ArgumentException("Unknown skinned mesh '" + mesh + "'. Meshes: " + string.Join(", ", shapes.Keys));
            var prepared = new List<BlendShapeOverride>();
            foreach (var property in (values ?? new JObject()).Properties())
            {
                bool exists = mesh != null ? shapes[mesh].Contains(property.Name) : shapes.Values.Any(list => list.Contains(property.Name));
                if (!exists) throw new ArgumentException("Unknown blendshape '" + property.Name + "'" + (mesh == null ? "" : " on " + mesh) + ". List them with warudo_character_blendshapes.");
                if (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer) throw new ArgumentException(property.Name + " must be a number 0..1.");
                float value = property.Value.Value<float>();
                if (float.IsNaN(value) || value < 0 || value > 1) throw new ArgumentException(property.Name + " must be within 0..1.");
                prepared.Add(new BlendShapeOverride { Mesh = mesh, Shape = property.Name, Value = value });
            }
            BlendShapeOverrides held;
            if (!blendShapeOverrides.TryGetValue(c.Id, out held))
            {
                held = new BlendShapeOverrides();
                blendShapeOverrides[c.Id] = held;
                c.AddOverrideBlendShapeEntryProvider(held);
            }
            var removed = clear ? held.Entries.Values.ToList() : new List<BlendShapeOverride>();
            if (clear) held.Entries.Clear();
            foreach (var entry in prepared)
            {
                entry.Before = WeightsBefore(c, entry.Mesh, entry.Shape, removed.Concat(held.Entries.Values));
                held.Entries[entry.Key] = entry;
            }
            if (held.Entries.Count == 0)
            {
                c.RemoveOverrideBlendShapeEntryProvider(held);
                blendShapeOverrides.Remove(c.Id);
            }
            PutBack(removed.Where(entry => !held.Entries.ContainsKey(entry.Key)));
            return new { character = c.Name, overrides = held.Entries.Values.Select(e => new { mesh = e.Mesh ?? "*", name = e.Shape, value = e.Value }).ToArray(), persisted = false };
        }

        static CharacterAsset.SpineIKData IkData(CharacterAsset c, string chain)
        {
            switch (chain)
            {
                case "Spine": return c.SpineIK;
                case "LeftHand": return c.LeftHandIK;
                case "RightHand": return c.RightHandIK;
                case "LeftFoot": return c.LeftFootIK;
                case "RightFoot": return c.RightFootIK;
            }
            throw new ArgumentException("chain must be one of " + string.Join(", ", IkChains));
        }

        object DescribeIk(CharacterAsset c, string chain)
        {
            CharacterAsset.SpineIKData data = IkData(c, chain);
            var limb = data as CharacterAsset.LimbIKData;
            Vector3? bone = null;
            Transform transform;
            if (c.Active && c.HumanBodyBoneToBodyTransforms != null && c.HumanBodyBoneToBodyTransforms.TryGetValue(data.GetTargetBone(), out transform) && transform != null)
                bone = transform.position;
            return new
            {
                chain,
                enabled = data.Enabled,
                targetAssetId = data.IkTarget == null ? null : data.IkTarget.Id.ToString(),
                targetName = data.IkTarget == null ? null : data.IkTarget.Name,
                positionWeight = data.PositionWeight,
                rotationWeight = data.RotationWeight,
                bendGoalAssetId = limb == null || limb.BendGoalTarget == null ? null : limb.BendGoalTarget.Id.ToString(),
                bendGoalWeight = limb == null ? (float?)null : limb.BendGoalWeight,
                bone = data.GetTargetBone().ToString(),
                boneWorldPosition = bone.HasValue ? Wire(bone.Value) : null
            };
        }

        async UniTask<object> SetIk(CharacterAsset c, JObject p)
        {
            string chain = Required(p, "chain");
            CharacterAsset.SpineIKData data = IkData(c, chain);
            bool limb = data is CharacterAsset.LimbIKData;
            string prefix = chain + "IK.";
            var fields = new JObject();
            object before = DescribeIk(c, chain);
            string createdAnchor = null;

            if (p["targetPosition"] != null)
            {
                if (p["targetAssetId"] != null) throw new ArgumentException("Pass targetPosition or targetAssetId, not both.");
                GameObjectAsset anchor = await IkAnchor(c, chain);
                Vector3 position = Vector(p["targetPosition"]);
                Quaternion rotation = p["targetRotation"] != null ? Quaternion.Euler(Vector(p["targetRotation"])) :
                    (anchor.GameObject != null ? anchor.GameObject.transform.rotation : Quaternion.identity);
                anchor.Transform.Position = position;
                anchor.Transform.RotationQuaternion = rotation;
                if (anchor.GameObject != null) anchor.Transform.ApplyAsLocalTransform(anchor.GameObject.transform);
                anchor.BroadcastDataInput("Transform");
                fields[prefix + "IkTarget"] = anchor.Id.ToString();
                createdAnchor = anchor.Id.ToString();
            }
            else if (p["targetAssetId"] != null) fields[prefix + "IkTarget"] = p["targetAssetId"];
            if (p["enabled"] != null) fields[prefix + "Enabled"] = p["enabled"];
            else if (fields.Count > 0) fields[prefix + "Enabled"] = true;
            if (p["positionWeight"] != null) fields[prefix + "PositionWeight"] = p["positionWeight"];
            if (p["rotationWeight"] != null) fields[prefix + "RotationWeight"] = p["rotationWeight"];
            if (limb && p["bendGoalAssetId"] != null) fields[prefix + "BendGoalTarget"] = p["bendGoalAssetId"];
            if (limb && p["bendGoalWeight"] != null) fields[prefix + "BendGoalWeight"] = p["bendGoalWeight"];
            if (!limb && (p["bendGoalAssetId"] != null || p["bendGoalWeight"] != null)) throw new ArgumentException("Spine IK has no bend goal.");
            if (fields.Count == 0) throw new ArgumentException("Nothing to change: pass enabled, a target, or weights.");
            foreach (var weight in new[] { "PositionWeight", "RotationWeight", "BendGoalWeight" })
            {
                JToken value = fields[prefix + weight];
                if (value != null && (value.Type != JTokenType.Float && value.Type != JTokenType.Integer || value.Value<float>() < 0 || value.Value<float>() > 1))
                    throw new ArgumentException(weight + " must be a number 0..1.");
            }
            EntityPatch(c, fields, null);
            await UniTask.Yield();
            return new { character = c.Name, before, after = DescribeIk(c, chain), anchorAssetId = createdAnchor, persisted = false };
        }

        // One reusable anchor per character and chain, so repeated calls move it instead of piling up assets.
        async UniTask<GameObjectAsset> IkAnchor(CharacterAsset c, string chain)
        {
            string name = "Agent IK " + chain + " (" + c.Name + ")";
            var existing = Context.OpenedScene.GetAssets().Values.OfType<GameObjectAsset>().FirstOrDefault(a => a.Name == name && a.GetType().Name == "AnchorAsset");
            if (existing != null) return existing;
            var anchorType = Context.AssetTypeRegistry.GetRegisteredTypes().Values.FirstOrDefault(meta => meta != null && meta.Type != null && meta.Type.Name == "AnchorAsset");
            if (anchorType == null) throw new InvalidOperationException("WARUDO has no Anchor asset type to use as an IK target.");
            SnapshotScene("before-ik-anchor");
            var anchor = Context.OpenedScene.AddAssetToGroup(anchorType.Id, "Agent IK") as GameObjectAsset;
            if (anchor == null) throw new InvalidOperationException("Could not create an anchor asset.");
            anchor.Name = name;
            for (int frame = 0; frame < 10 && anchor.GameObject == null; frame++) await UniTask.Yield();
            return anchor;
        }
    }
}
