using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record AbyssRecipe(int Number, int Owner, string Event, string? Wrapper);

/// <summary>
/// Port of abyss_bundles.inject + abyss_callbacks.install: add the ten Abyss "X" districts as bundles.
/// With <c>retain</c> (SCU-PATCH soul_bundles#1-#3, stage tag "Extra") the host rooms stay in the pool.
/// </summary>
public static class AbyssBundles
{
    static readonly Regex Uber = new("^ExecuteUbergraph_Extra5x5$");

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>abyss_callbacks.RECIPES: the two overlap triggers plus the three lever handlers (owners come from the CDO).</summary>
    public static List<AbyssRecipe> Recipes(Asset extra)
    {
        var recipes = new List<AbyssRecipe>
        {
            new(2, 32089, "OnActorBeginOverlap", null),
            new(9, 32090, "OnActorBeginOverlap", null),
        };
        var cdo = Props.Of(extra.Export(3));
        foreach (var (n, field, wrapper) in new[] { (6, 8708, "カスタムイベント_0"), (9, 8709, "PullRaver1"), (9, 8707, "PullRaver2") })
            recipes.Add(new AbyssRecipe(n, ((ObjectPropertyData)cdo[extra.ObjectName(field)]).Value.Index, "PullRever", wrapper));
        return recipes;
    }

    public static (List<BundleRow> Rows, List<CallbackPlan> Callbacks) Inject(Asset data, string extraUmap, ISet<int> selected,
        IReadOnlyDictionary<int, int> slots, bool retain)
    {
        Require(selected.IsSubsetOf(Enumerable.Range(1, 10)), "unsupported Abyss district");
        var d = AssetCache.Take(extraUmap);
        int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        var recipes = Recipes(d);
        var tiles = Ownership.TileOfActors(d);
        foreach (int n in selected.OrderBy(x => x))
        {
            int root = Enumerable.Range(1, d.Exports.Count).First(i => Regex.IsMatch(d.Exports[i - 1].ObjectName.ToString(), $@"^ArireRoot_X{n}(?:_447)?$"));
            var (actors, group) = Ownership.Owned(d, root, tiles);
            var omit = d.Closure(actors.Where(i => d.ObjectName(d.Export(i).ClassIndex.Index) is "BP_Kokeshi_C" or "BP_ArchiveItem_C"));
            actors.ExceptWith(omit); group.ExceptWith(omit);
            foreach (var r in recipes.Where(r => r.Number == n))
            {
                var obj = (NormalExport)d.Export(r.Owner);
                var binding = obj.Data.Where(p => p.Name.ToString() == r.Event).ToList();
                if (binding.Count == 0) continue;
                Require(r.Event == "OnActorBeginOverlap" && binding.Count == 1 && ((MulticastDelegatePropertyData)binding[0]).Value.Length == 1
                        && ((MulticastDelegatePropertyData)binding[0]).Value[0].Object.Index == 8400, "unexpected overlap binding");
                obj.Data.Remove(binding[0]);
                foreach (var list in new[] { obj.SerializationBeforeSerializationDependencies, obj.CreateBeforeSerializationDependencies, obj.SerializationBeforeCreateDependencies, obj.CreateBeforeCreateDependencies })
                    list.RemoveAll(v => v.Index == 8400);
                d.Raw.DependsMap[r.Owner - 1] = (d.Raw.DependsMap[r.Owner - 1] ?? Array.Empty<int>()).Where(v => v != 8400).ToArray();
            }
            foreach (int i in group)
                foreach (int v in Refs.CollectNormal(d.Export(i)))
                    Require(!(v > 0 && !group.Contains(v) && v != dl), $"unresolved Abyss reference {n} {i} {v}");
            // Names are registered in the donor name map in ascending export order (the Python iterates a set).
            foreach (int i in actors.OrderBy(x => x))
            {
                var obj = d.Export(i);
                var renamed = $"SCMix_Extra_X{n}_{i}_" + obj.ObjectName;
                obj.ObjectName = Names.Make(d, renamed);
                BundleInjector.RegisterName(d, renamed);
            }
        }
        var rows = BundleInjector.Inject(data, selected, d, slots, "Extra", retain);
        var callbacks = InstallCallbacks(data, extraUmap, selected, recipes);
        return (rows, callbacks);
    }

    static List<CallbackPlan> InstallCallbacks(Asset data, string extraUmap, ISet<int> selected, List<AbyssRecipe> recipes)
    {
        var plans = new List<CallbackPlan>();
        foreach (var recipe in recipes)
        {
            if (!selected.Contains(recipe.Number)) continue;
            var (proof, plan) = PlanFor(extraUmap, recipe);
            var lookup = new Dictionary<(string, string, string), int>();
            for (int i = -1; i >= -data.Imports.Count; i--)
                lookup[(data.Path(i), data.Imports[-i - 1].ClassPackage.ToString(), data.Imports[-i - 1].ClassName.ToString())] = i;
            var imports = new Dictionary<int, int>();
            int Imp(int i)
            {
                if (imports.TryGetValue(i, out int cached)) return cached;
                var e = proof.Imports[-i - 1];
                int parent = e.OuterIndex.Index != 0 ? Imp(e.OuterIndex.Index) : 0;
                var key = (proof.Path(i), e.ClassPackage.ToString(), e.ClassName.ToString());
                if (!lookup.ContainsKey(key))
                {
                    var created = DeepClone.Of(e, data.Raw);
                    created.OuterIndex = new FPackageIndex(parent);
                    data.Imports.Add(created);
                    lookup[key] = -data.Imports.Count;
                }
                return imports[i] = lookup[key];
            }

            int levelId = LevelChecks.Validate(data).Level;
            var pattern = new Regex($@"^SCMix_Extra_X{recipe.Number}_(\d+)_");
            var mapping = new Dictionary<int, int>();
            for (int i = 1; i <= data.Exports.Count; i++)
            {
                var e = data.Exports[i - 1];
                if (e.OuterIndex.Index != levelId) continue;
                var m = pattern.Match(e.ObjectName.ToString());
                if (m.Success) mapping[int.Parse(m.Groups[1].Value)] = i;
            }
            var proofCdo = Props.Of(proof.Export(3));
            var actorRefs = plan.SourceActorPropertiesRetained
                .Select(i => ((ObjectPropertyData)proofCdo[proof.ObjectName(i)]).Value.Index).ToHashSet();
            Require(actorRefs.IsSubsetOf(mapping.Keys), $"callback crosses district {recipe.Number}");
            CallbackBuilder.Integrate(data, proof, plan, actorRefs.ToDictionary(old => old, old => mapping[old]), Imp);

            var obj = (NormalExport)data.Export(mapping[recipe.Owner]);
            Require(!obj.Data.Any(q => q.Name.ToString() == recipe.Event), "event already bound");
            int script = LevelChecks.Validate(data).Script;
            var binding = new MulticastDelegatePropertyData(Names.Make(data, recipe.Event))
            {
                Value = new[] { new FDelegate(new FPackageIndex(script), Names.Make(data, plan.FunctionName)) },
            };
            obj.Data.Add(binding);
            obj.CreateBeforeSerializationDependencies.Add(new FPackageIndex(script));
            data.Raw.DependsMap[mapping[recipe.Owner] - 1] = (data.Raw.DependsMap[mapping[recipe.Owner] - 1] ?? Array.Empty<int>()).Append(script).ToArray();
            foreach (var n in new[] { recipe.Event, "MulticastDelegateProperty", plan.FunctionName })
                if (!data.Raw.ContainsNameReference(new FString(n))) data.Raw.AddNameReference(new FString(n));
            plans.Add(plan);
        }
        Compose.UpdateGenerations(data);
        return plans;
    }

    /// <summary>abyss_callbacks.plan_for.</summary>
    static (Asset Proof, CallbackPlan Plan) PlanFor(string extraUmap, AbyssRecipe recipe)
        => HandlerPlanner.PlanFor(extraUmap, "ExecuteUbergraph_Extra5x5", $"Extra_X{recipe.Number}_{recipe.Owner}", recipe.Owner, recipe.Event, recipe.Wrapper);
}
