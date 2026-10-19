using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>soul_lt_bundles.inject (+ soul_lt_special for T3) and lt_bundles.inject (Abyss 2-3 branch districts).</summary>
public static class LtStages
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static readonly LtRecipe[] SoulRecipes =
    {
        new("T1", 46252, "OnActorBeginOverlap", null), new("T6", 46254, "OnActorBeginOverlap", null), new("T10", 46256, "OnActorBeginOverlap", null),
        new("T3", 46258, "OnActorBeginOverlap", null), new("T3", 46246, "OnActorEndOverlap", null),
        new("L3", 5772, "PullRever", "PullRever_L3"), new("T2", 5775, "PullRever", "PullRever_T2"), new("T5", 5780, "PullRever", "PullRever_T5"),
        new("T6", 5769, "PullRever", "PullRever_T6"), new("T8", 5785, "PullRever", "PullRever_T8"), new("T8", 5788, "PullRever", "PullRever_T8_2"),
    };

    public static readonly LtRecipe[] AbyssRecipes =
    {
        new("L3", 32091, "OnActorBeginOverlap", null), new("L4", 32092, "OnActorBeginOverlap", null),
        new("T4", 32093, "OnActorBeginOverlap", null), new("T7", 32096, "OnActorBeginOverlap", null),
        new("T9b", 32095, "OnActorBeginOverlap", "BndEvt__TriggerVolume9_K2Node_ActorBoundEvent_0_ActorBeginOverlapSignature__DelegateSignature"),
        new("T9e", 32095, "OnActorEndOverlap", "BndEvt__TriggerVolume9_K2Node_ActorBoundEvent_1_ActorEndOverlapSignature__DelegateSignature"),
    };

    static void CheckDonorGroup(Asset d, ISet<int> group, int levelIndex, string district)
    {
        foreach (int i in group)
        {
            var refs = Refs.CollectNormal(d.Export(i));
            refs.AddRange(d.Raw.DependsMap[i - 1] ?? Array.Empty<int>());
            var e = d.Export(i);
            if (e.Extras != null && e.Extras.Any(x => x != 0))
                refs.AddRange(NativePayload.Parse(d.ObjectName(e.ClassIndex.Index), e.Extras, d.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList()).Refs.Select(r => r.Value));
            foreach (int v in refs)
                Require(!(v > 0 && !group.Contains(v) && v != levelIndex), $"unresolved donor reference {district} {i} {v}");
        }
    }

    static void RenameActors(Asset d, IEnumerable<int> actors, string prefix)
    {
        foreach (int i in actors.OrderBy(x => x))
        {
            var e = d.Export(i);
            var renamed = prefix + i + "_" + e.ObjectName;
            e.ObjectName = Names.Make(d, renamed);
            BundleInjector.RegisterName(d, renamed);
        }
    }

    /// <summary>One district of a donor package: actors, closure, minus collectibles; removes the donor's event bindings.</summary>
    static (HashSet<int> Actors, HashSet<int> Group) PrepareDistrict(Asset d, string n, int dl, int script, IEnumerable<LtRecipe> recipes, Func<Asset, HashSet<int>>? room, bool soul)
    {
        int dr = Enumerable.Range(1, d.Exports.Count).First(i =>
            d.Export(i) is NormalExport && System.Text.RegularExpressions.Regex.IsMatch(d.Exports[i - 1].ObjectName.ToString(), $@"^ArireRoot_{n}(?:_447)?$")
            && ((LevelExport)d.Export(dl)).Actors.Any(p => p.Index == i));
        var (actors, group) = LtBundles.Topology(d, dr);
        if (room != null)
        {
            actors.UnionWith(room(d));
            group = d.Closure(actors);
        }
        var omitActors = actors.Where(i => d.ObjectName(d.Export(i).ClassIndex.Index) is "BP_Kokeshi_C" or "BP_ArchiveItem_C").ToHashSet();
        var omit = d.Closure(omitActors);
        actors.ExceptWith(omit); group.ExceptWith(omit);
        foreach (var r in recipes.Where(r => LtBundles.DistrictOf(r.District) == n))
        {
            var obj = (NormalExport)d.Export(r.Owner);
            if (soul && r.Event == "PullRever")
            {
                Require(!obj.Data.Any(p => p.Name.ToString() == r.Event), "lever already bound");
                continue;
            }
            var q = obj.Data.OfType<MulticastDelegatePropertyData>().First(p => p.Name.ToString() == r.Event);
            Require(q.Value.Length == 1 && q.Value[0].Object.Index == script, "unexpected donor binding");
            obj.Data.Remove(q);
            foreach (var list in new[] { obj.SerializationBeforeSerializationDependencies, obj.CreateBeforeSerializationDependencies, obj.SerializationBeforeCreateDependencies, obj.CreateBeforeCreateDependencies })
                list.RemoveAll(v => v.Index == script);
            d.Raw.DependsMap[r.Owner - 1] = (d.Raw.DependsMap[r.Owner - 1] ?? Array.Empty<int>()).Where(v => v != script).ToArray();
        }
        CheckDonorGroup(d, group, dl, n);
        return (actors, group);
    }

    // ---- Soul 2-3 branch ----

    const double SoulT3Translation = 45000;

    static HashSet<int> PrepareSoulT3Room(Asset d, SoulPaths paths)
    {
        using var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.RandomizerRoot, "app/soul-lt-room.json")));
        var ids = rows.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToHashSet();
        Require(ids.Count == 211, "T3 room survey changed");
        var omit = ids.Where(i => d.ObjectName(d.Export(i).ClassIndex.Index) is "BP_Kokeshi_C" or "BP_ArchiveItem_C" or "PlayerStart").ToHashSet();
        Require(omit.SetEquals(new[] { 5828 }), "unexpected T3 room exclusions");
        ids.ExceptWith(omit);
        foreach (int i in ids)
        {
            var component = (NormalExport)d.Export(Props.Ref(d.Export(i), "RootComponent"));
            Require(Props.Ref(component, "AttachParent") == 0, "T3 room actor is attached");
            var loc = component.Data.OfType<StructPropertyData>().First(q => q.Name.ToString() == "RelativeLocation");
            var vector = (VectorPropertyData)loc.Value[0];
            var v = vector.Value;
            vector.Value = new FVector((double)(float)((double)v.XFloat + SoulT3Translation), v.Y, v.Z);
        }
        Require(new[] { 43872, 5778, 46258, 46246, 5576, 5760, 43852, 12030, 12031, 12032, 12033, 8355, 6152, 6154 }.All(ids.Contains), "T3 room allowlist changed");
        return ids;
    }

    public static (List<LtRow> Rows, LtIndexMap Map, List<CallbackPlan> Callbacks) InjectSoul(
        Asset data, SoulPaths paths, IList<string> selected, IReadOnlyDictionary<string, string> slots, bool retain)
    {
        var d = AssetCache.Take(paths.Stage5);
        int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        int script = ((LevelExport)d.Export(dl)).LevelScriptActor.Index;
        foreach (var n in selected)
        {
            var (actors, _) = PrepareDistrict(d, n, dl, script, SoulRecipes, n == "T3" ? (x => PrepareSoulT3Room(x, paths)) : null, soul: true);
            RenameActors(d, actors, $"SCMix_Stage5_{n}_");
        }
        var (rows, map) = LtBundles.InjectBundles(data, selected, d, slots, "SCMix_Stage5_", retain);
        var callbacks = LtBundles.InstallCallbacks(data, paths.Stage5, "ExecuteUbergraph_Stage5x5", r => $"Stage5_LT{r.District}_{r.Owner}", "SCMix_Stage5_", selected, SoulRecipes);
        if (selected.Contains("T3"))
            LatentBuilder.Install(data, paths.Stage5, new LatentSpec(
                ResumeLabel: "Stage5_T3_FloorResume", StartLabel: "Stage5_T3_FloorStart", StateFieldName: "SCMix_Stage5_T3_FloorStarted",
                ActorPrefix: "SCMix_Stage5_T3_", BindOwner: 5778,
                Indices: new[] { 1, 717, 718 }.Concat(Enumerable.Range(35, 10)).ToArray(),
                WrapperIndex: a => Enumerable.Range(1, a.Exports.Count).First(i => a.Exports[i - 1].ObjectName.ToString() == "PullRever_T3_Warp"),
                UuidFor: _ => 160300001, StartEntryKey: 20695, ExpectedActors: new HashSet<int> { 12030, 12031, 12032, 12033 }));
        return (rows, map, callbacks);
    }

    // ---- Abyss 2-3 branch ----

    public static (List<LtRow> Rows, LtIndexMap Map, List<CallbackPlan> Callbacks) InjectAbyss(
        Asset data, string extraUmap, IList<string> selected, IReadOnlyDictionary<string, string> slots, bool retain)
    {
        var d = AssetCache.Take(extraUmap);
        int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        int script = ((LevelExport)d.Export(dl)).LevelScriptActor.Index;
        foreach (var n in selected)
        {
            var (actors, _) = PrepareDistrict(d, n, dl, script, AbyssRecipes, null, soul: false);
            RenameActors(d, actors, $"SCMix_Extra_{n}_");
        }
        var (rows, map) = LtBundles.InjectBundles(data, selected, d, slots, "SCMix_Extra_", retain);
        var callbacks = LtBundles.InstallCallbacks(data, extraUmap, "ExecuteUbergraph_Extra5x5", r => $"Extra_X{r.District}_{r.Owner}", "SCMix_Extra_", selected, AbyssRecipes);
        return (rows, map, callbacks);
    }
}
