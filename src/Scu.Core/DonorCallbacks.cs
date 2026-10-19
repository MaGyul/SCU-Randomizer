using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>Shared pieces of the callback installers (abyss_callbacks, soul_x4/x8_callback, soul_special.callback3/6/10, latent).</summary>
public static class DonorCallbacks
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>The "imp" closure every installer defines: import a proof-package import into the target, reusing existing ones.</summary>
    public static Func<int, int> ImportMapper(Asset proof, Asset target)
    {
        var lookup = new Dictionary<(string, string, string), int>();
        for (int i = -1; i >= -target.Imports.Count; i--)
            lookup[(target.Path(i), target.Imports[-i - 1].ClassPackage.ToString(), target.Imports[-i - 1].ClassName.ToString())] = i;
        var cache = new Dictionary<int, int>();
        int Imp(int i)
        {
            if (cache.TryGetValue(i, out int hit)) return hit;
            var e = proof.Imports[-i - 1];
            int parent = e.OuterIndex.Index != 0 ? Imp(e.OuterIndex.Index) : 0;
            var key = (proof.Path(i), e.ClassPackage.ToString(), e.ClassName.ToString());
            if (!lookup.ContainsKey(key))
            {
                var created = DeepClone.Of(e, target.Raw);
                created.OuterIndex = new FPackageIndex(parent);
                target.Imports.Add(created);
                lookup[key] = -target.Imports.Count;
            }
            return cache[i] = lookup[key];
        }
        return Imp;
    }

    /// <summary>"actor(old)": the single export in the composed package whose name starts with the donor-prefixed id.</summary>
    public static int FindActor(Asset data, string prefix, int old)
    {
        var start = $"{prefix}{old}_";
        var found = Enumerable.Range(1, data.Exports.Count).Where(i => data.Exports[i - 1].ObjectName.ToString().StartsWith(start, StringComparison.Ordinal)).ToList();
        Require(found.Count == 1, $"expected exactly one export for {start}: {found.Count}");
        return found[0];
    }

    public static MulticastDelegatePropertyData NewBinding(Asset data, string eventName, int scriptActor, string functionName)
        => new(Names.Make(data, eventName))
        {
            Value = new[] { new FDelegate(new FPackageIndex(scriptActor), Names.Make(data, functionName)) },
        };

    /// <summary>Append the event binding to <paramref name="owner"/> and register the names the converter would add.</summary>
    public static void Bind(Asset data, int owner, string eventName, string functionName, bool addDepends = true)
    {
        int script = LevelChecks.Validate(data).Script;
        var e = (NormalExport)data.Export(owner);
        Require(!e.Data.Any(p => p.Name.ToString() == eventName), "event already bound");
        e.Data.Add(NewBinding(data, eventName, script, functionName));
        e.CreateBeforeSerializationDependencies.Add(new FPackageIndex(script));
        if (addDepends) data.Raw.DependsMap[owner - 1] = (data.Raw.DependsMap[owner - 1] ?? Array.Empty<int>()).Append(script).ToArray();
        AddNames(data, eventName, "MulticastDelegateProperty", functionName);
        Compose.UpdateGenerations(data);
    }

    public static void AddNames(Asset a, params string[] names)
    {
        foreach (var n in names)
            if (!a.Raw.ContainsNameReference(new FString(n))) a.Raw.AddNameReference(new FString(n));
    }

    /// <summary>
    /// soul_special.callback3/6/10: lift one handler out of the pristine Stage5x5, integrate it into the composed package and bind it.
    /// </summary>
    public static CallbackPlan InstallSoulHandler(Asset data, string stage5Umap, string actorPrefix, string labelPrefix, string label,
        int owner, int wrapper, IReadOnlyList<int> indices, ISet<int> states, string eventName)
    {
        var proof = AssetCache.Take(stage5Umap);
        var plan = CallbackBuilder.AddCallback(proof, 0, indices[0], indices[^1] + 1, states, labelPrefix + label, indices, wrapper);
        var proofCdo = Props.Of(proof.Export(3));
        var refs = plan.SourceActorPropertiesRetained.Select(i => ((ObjectPropertyData)proofCdo[proof.ObjectName(i)]).Value.Index).ToHashSet();
        CallbackBuilder.Integrate(data, proof, plan, refs.ToDictionary(i => i, i => FindActor(data, actorPrefix, i)), ImportMapper(proof, data));
        Bind(data, FindActor(data, actorPrefix, owner), eventName, plan.FunctionName);
        return plan;
    }
}
