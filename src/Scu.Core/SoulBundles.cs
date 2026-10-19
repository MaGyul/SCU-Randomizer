using System.Text.Json;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record SoulPaths(string RandomizerRoot)
{
    public string MapTiles => Path.Combine(RandomizerRoot, "extracted/Kageroh/Content/Levels/RundomMapTiles");
    public string Stage5 => Path.Combine(MapTiles, "Stage5x5.umap");
    public string SoulX1 => Prepare.Resolve(RandomizerRoot, "soul-X1/Random5x5.umap", Prepare.SoulX1Rel) ?? throw new BuildException("not_prepared", "soul-X1 준비 자료가 없습니다.");
    public string Updates => Path.Combine(RandomizerRoot, "app/updates");
}

/// <summary>
/// Port of soul_bundles.inject and soul_special.inject_optional: add the ten Soul Gathering "X" districts.
/// SCU-PATCH app/soul_bundles.py #1-#3 (RETAIN, stage tag "Stage5") are applied through <c>retain</c>.
/// </summary>
public static class SoulBundles
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    static readonly string[] BindingEvents = { "OnActorBeginOverlap", "OnActorEndOverlap" };

    public static List<BundleRow> Inject(Asset data, SoulPaths paths, ISet<int> selected, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        Require(selected.IsSubsetOf(BundleInjector.SoulSlots.Keys), "unsupported Soul district");
        var info = new List<BundleRow>();
        var firstGroup = selected.Where(n => n is 1 or 2 or 7).ToHashSet();
        if (firstGroup.Count > 0) info.AddRange(InjectFromSoulX1(data, paths, firstGroup, slots, retain));

        foreach (int n in selected.Where(n => n is 4 or 5 or 8 or 9).OrderBy(n => n))
        {
            var d = AssetCache.Take(paths.Stage5);
            int root = n switch { 4 => 11902, 5 => 11903, 8 => 11906, _ => 11907 };
            var (actors, cl) = Ownership.Owned(d, root);
            var removeClasses = n == 4 ? new[] { "BP_Kokeshi_C", "BP_ArchiveItem_C" } : new[] { "BP_Kokeshi_C" };
            var excluded = d.Closure(actors.Where(i => removeClasses.Contains(d.ObjectName(d.Export(i).ClassIndex.Index))));
            actors.ExceptWith(excluded); cl.ExceptWith(excluded);
            int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
            if (n == 8) StripBinding(d, 46266, new[] { "OnActorBeginOverlap" }, expectedObject: 11883, requireSingle: true);
            foreach (int i in cl)
                foreach (int v in Refs.CollectNormal(d.Export(i)))
                    Require(!(v > 0 && !cl.Contains(v) && v != dl), $"unresolved raw Soul donor reference {n} {i} {v}");
            RenameActors(d, actors, $"SCMix_Stage5_X{n}_");
            info.AddRange(BundleInjector.Inject(data, new HashSet<int> { n }, d, slots, "Stage5", retain));
        }
        if (selected.Contains(4)) InstallX4(data, paths);
        if (selected.Contains(8)) InstallX8(data, paths);
        if (selected.Any(n => n is 3 or 6 or 10)) info.AddRange(InjectOptional(data, paths, selected, slots, retain));
        return info.OrderBy(r => r.Number).ToList();
    }

    /// <summary>soul_bundles._inject with d=None: districts 1, 2 and 7 come from the prepared soul-X1 package.</summary>
    static List<BundleRow> InjectFromSoulX1(Asset data, SoulPaths paths, ISet<int> selected, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        var d = Asset.Load(paths.SoulX1);
        if (selected.Contains(2))
        {
            // The early X2 transplant reused this actor under its host name; give it donor ownership so the guard travels with X2.
            var marker = d.Exports.First(e => e.ObjectName.ToString() == "BP_EnemySpawner5");
            Require(Props.Of(marker)["SpawnType"] is BytePropertyData, "unexpected X2 marker");
            var renamed = "SCMix_Stage5_X2_5765_BP_EnemySpawner_212";
            marker.ObjectName = Names.Make(d, renamed);
            BundleInjector.RegisterName(d, renamed);
        }
        return BundleInjector.Inject(data, selected, d, slots, "Stage5", retain);
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

    /// <summary>Remove event bindings (and the dependency on the donor script actor 11883) from a donor trigger.</summary>
    static void StripBinding(Asset d, int owner, string[] events, int expectedObject = 0, bool requireSingle = false)
    {
        var e = (NormalExport)d.Export(owner);
        if (expectedObject != 0 && requireSingle)
        {
            var binding = e.Data.OfType<MulticastDelegatePropertyData>().First(q => q.Name.ToString() == events[0]);
            Require(binding.Value.Length == 1 && binding.Value[0].Object.Index == expectedObject, "unexpected trigger binding");
        }
        e.Data = e.Data.Where(q => !events.Contains(q.Name.ToString())).ToList();
        foreach (var list in new[] { e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies })
            list.RemoveAll(v => v.Index == 11883);
        d.Raw.DependsMap[owner - 1] = (d.Raw.DependsMap[owner - 1] ?? Array.Empty<int>()).Where(v => v != 11883).ToArray();
    }

    // ---- X4 / X8 callbacks (soul_x4_callback / soul_x8_callback) ----

    static void InstallX4(Asset data, SoulPaths paths)
    {
        var proof = AssetCache.Take(paths.Stage5);
        int wrapper = Enumerable.Range(1, proof.Exports.Count).First(i => proof.Exports[i - 1].ObjectName.ToString() == "カスタムイベント_0");
        var other = (StructExport)proof.Exports.First(e => e.ObjectName.ToString() == "カスタムイベント_1");
        Require(((StructExport)proof.Export(wrapper)).Children.Length == 0 && other.Children.Length == 0, "unexpected X4 wrappers");
        var plan = CallbackBuilder.AddCallback(proof, 0, 423, 450, new HashSet<int> { 2564, 2580, 2593, 2594 }, "Stage5_X4",
            new[] { 447, 448, 449 }.Concat(Enumerable.Range(423, 24)).ToList(), wrapper);
        Require(plan.SourceActorPropertiesRetained.SequenceEqual(new[] { 8499 }), "unexpected X4 actor properties");
        CallbackBuilder.Integrate(data, proof, plan, new Dictionary<int, int> { [5479] = DonorCallbacks.FindActor(data, "SCMix_Stage5_X4_", 5479) },
            DonorCallbacks.ImportMapper(proof, data));
        foreach (int old in new[] { 5790, 5791 })
            DonorCallbacks.Bind(data, DonorCallbacks.FindActor(data, "SCMix_Stage5_X4_", old), "PullRever", plan.FunctionName, addDepends: false);
    }

    static void InstallX8(Asset data, SoulPaths paths)
    {
        var proof = AssetCache.Take(paths.Stage5);
        var plan = CallbackBuilder.AddCallback(proof, 46266, 676, 682, new HashSet<int> { 2570, 2585 }, "Stage5_X8",
            Enumerable.Range(676, 6).Concat(Enumerable.Range(450, 12)).Concat(Enumerable.Range(420, 3)).ToList());
        const string name = "SCMix_Stage5_X8_TrapOverlap";
        proof.Export(plan.Function).ObjectName = Names.Make(proof, name);
        var cls = (ClassExport)proof.Export(2);
        var fnMap = cls.FuncMap;
        foreach (var key in fnMap.Keys.ToList())
            if (fnMap[key].Index == plan.Function) { fnMap.Remove(key); }
        fnMap.Add(Names.Make(proof, name), new FPackageIndex(plan.Function));
        plan.FunctionName = name;
        proof.Raw.AddNameReference(new FString(name));
        Require(plan.SourceActorPropertiesRetained.SequenceEqual(new[] { 8503, 8513 }), "unexpected X8 actor properties");

        var result = CallbackBuilder.Integrate(data, proof, plan,
            new Dictionary<int, int> { [5510] = DonorCallbacks.FindActor(data, "SCMix_Stage5_X8_", 5510), [5756] = DonorCallbacks.FindActor(data, "SCMix_Stage5_X8_", 5756) },
            DonorCallbacks.ImportMapper(proof, data));
        int triggerIndex = DonorCallbacks.FindActor(data, "SCMix_Stage5_X8_", 46266);
        var trigger = (NormalExport)data.Export(triggerIndex);
        Require(!trigger.Data.Any(q => q.Name.ToString() == "OnActorBeginOverlap"), "trigger already bound");
        var pristine = AssetCache.Take(paths.Stage5);
        var binding = DeepClone.Of((MulticastDelegatePropertyData)((NormalExport)pristine.Export(46266)).Data.First(q => q.Name.ToString() == "OnActorBeginOverlap"), data.Raw);
        int script = LevelChecks.Validate(data).Script;
        binding.Value[0].Object = new FPackageIndex(script);
        binding.Value[0].Delegate = Names.Make(data, plan.FunctionName);
        trigger.Data.Add(binding);
        trigger.CreateBeforeSerializationDependencies.Add(new FPackageIndex(script));
        data.Raw.DependsMap[triggerIndex - 1] = (data.Raw.DependsMap[triggerIndex - 1] ?? Array.Empty<int>()).Append(script).ToArray();
        DonorCallbacks.AddNames(data, "OnActorBeginOverlap", plan.FunctionName);
        Compose.UpdateGenerations(data);
    }

    // ---- X3 / X10 / X6 special districts (soul_special) ----

    static readonly (string Label, int Owner, int Wrapper, int[] Indices, int[] States, string Event)[] Recipes3 =
    {
        ("RoomEntry", 46257, 2635, Enumerable.Range(999, 6).Concat(new[] { 418, 419, 416, 417, 413, 414 }).Concat(Enumerable.Range(397, 7)).ToArray(), new[] { 2569, 2578 }, "OnActorBeginOverlap"),
        ("RoomEnemiesIn", 46263, 2620, Enumerable.Range(1037, 6).ToArray(), Array.Empty<int>(), "OnActorBeginOverlap"),
        ("RoomEnemiesOut", 46263, 2618, Enumerable.Range(1043, 5).ToArray(), Array.Empty<int>(), "OnActorEndOverlap"),
        ("RoomLever", 5794, 2633, new[] { 1007, 1005, 1006 }, Array.Empty<int>(), "PullRever"),
    };
    static readonly (string Label, int Owner, int Wrapper, int[] Indices, int[] States, string Event)[] Recipes10 =
    {
        ("TrapEnter", 46251, 2678, Enumerable.Range(685, 6).Concat(new[] { 672, 673, 387, 388, 383, 384 }).Concat(Enumerable.Range(406, 7)).ToArray(), new[] { 2562, 2584 }, "OnActorBeginOverlap"),
        ("SpawnerExit", 46269, 2614, Enumerable.Range(1052, 4).ToArray(), Array.Empty<int>(), "OnActorEndOverlap"),
    };
    static readonly (string Label, int Owner, int Wrapper, int[] Indices, int[] States, string Event)[] Recipes6 =
    {
        ("LeavesEnter", 46267, 2610, Enumerable.Range(1065, 4).ToArray(), Array.Empty<int>(), "OnActorBeginOverlap"),
        ("LeavesExit", 46267, 2608, Enumerable.Range(1069, 4).ToArray(), Array.Empty<int>(), "OnActorEndOverlap"),
        ("AsphaltOff", 46248, 2606, Enumerable.Range(1073, 4).ToArray(), Array.Empty<int>(), "OnActorBeginOverlap"),
        ("AsphaltOn", 46247, 2604, Enumerable.Range(1077, 4).ToArray(), Array.Empty<int>(), "OnActorBeginOverlap"),
        ("ParticlesEnter", 46262, 2624, Enumerable.Range(1021, 8).ToArray(), Array.Empty<int>(), "OnActorBeginOverlap"),
        ("ParticlesExit", 46262, 2622, Enumerable.Range(1029, 8).ToArray(), Array.Empty<int>(), "OnActorEndOverlap"),
        ("GateQuake", 6330, 2637, new[] { 992, 993 }, Array.Empty<int>(), "StartOpenAnim"),
    };

    static List<BundleRow> InjectOptional(Asset data, SoulPaths paths, ISet<int> selected, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        var info = new List<BundleRow>();
        if (selected.Contains(3)) info.AddRange(Inject3(data, paths, slots, retain));
        if (selected.Contains(10)) info.AddRange(Inject10(data, paths, slots, retain));
        if (selected.Contains(6)) { info.AddRange(Inject6(data, paths, slots, retain)); AddFlowers(data, paths); }
        return info;
    }

    static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    /// <summary>Move an unattached actor root by (dx,0,0): float32(x + dx), exactly like the Python struct round-trip.</summary>
    static void TranslateX(NormalExport component, double dx)
    {
        var loc = component.Data.OfType<StructPropertyData>().First(q => q.Name.ToString() == "RelativeLocation");
        var vector = (VectorPropertyData)loc.Value[0];
        var v = vector.Value;
        float x = (float)((double)v.XFloat + dx);
        vector.Value = new FVector((double)x, v.Y, v.Z);
    }

    static void TranslateRoots(Asset d, IEnumerable<int> actors, double dx)
    {
        foreach (int i in actors.OrderBy(x => x))
        {
            int rc = Props.Ref(d.Export(i), "RootComponent");
            var component = (NormalExport)d.Export(rc);
            Require(Props.Ref(component, "AttachParent") == 0, "root component is attached");
            TranslateX(component, dx);
        }
    }

    static List<BundleRow> Inject3(Asset data, SoulPaths paths, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        var d = AssetCache.Take(paths.Stage5);
        using var survey = ReadJson(Path.Combine(paths.Updates, "soul-X3-analysis/destination-spatial-survey.json"));
        var ids = survey.RootElement.GetProperty("actors").EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ToHashSet();
        Require(ids.Count == 142 && new[] { 43871, 5753, 5755, 5757, 5794, 8347, 46257, 46263 }.All(ids.Contains), "X3 survey changed");
        var excluded = new[] { 5829, 43849 };
        Require(excluded.All(ids.Contains), "X3 exclusions");
        ids.ExceptWith(excluded);
        var (tile, _) = Ownership.Owned(d, 11901);
        var combined = new HashSet<int>(tile); combined.UnionWith(ids);
        var cl = d.Closure(combined);
        TranslateRoots(d, ids, 40000);
        foreach (int old in new[] { 46257, 46263 }) StripBinding(d, old, BindingEvents);
        foreach (int old in cl)
            foreach (int v in Refs.CollectNormal(d.Export(old)))
                Require(!(v > 0 && !cl.Contains(v) && v != 8312), $"external room ref {old} {v}");
        RenameActors(d, combined, "SCMix_Stage5_X3_");
        var rows = BundleInjector.Inject(data, new HashSet<int> { 3 }, d, slots, "Stage5", retain);
        foreach (var r in Recipes3)
            DonorCallbacks.InstallSoulHandler(data, paths.Stage5, "SCMix_Stage5_X3_", "Stage5_X3_", r.Label, r.Owner, r.Wrapper, r.Indices, r.States.ToHashSet(), r.Event);
        return rows;
    }

    static List<BundleRow> Inject10(Asset data, SoulPaths paths, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        var d = AssetCache.Take(paths.Stage5);
        var (actors, cl) = Ownership.Owned(d, 11898);
        var removed = actors.Where(i => d.ObjectName(d.Export(i).ClassIndex.Index) is "BP_Kokeshi_C" or "BP_ArchiveItem_C").ToHashSet();
        var excluded = d.Closure(removed);
        actors.ExceptWith(excluded); cl.ExceptWith(excluded);
        foreach (int i in new[] { 46251, 46269 }) StripBinding(d, i, BindingEvents);
        foreach (int i in cl)
            foreach (int v in Refs.CollectNormal(d.Export(i)))
                Require(!(v > 0 && !cl.Contains(v) && v != 8312), $"external {i} {v}");
        RenameActors(d, actors, "SCMix_Stage5_X10_");
        var rows = BundleInjector.Inject(data, new HashSet<int> { 10 }, d, slots, "Stage5", retain);
        foreach (var r in Recipes10)
            DonorCallbacks.InstallSoulHandler(data, paths.Stage5, "SCMix_Stage5_X10_", "Stage5_X10_", r.Label, r.Owner, r.Wrapper, r.Indices, r.States.ToHashSet(), r.Event);
        return rows;
    }

    static List<BundleRow> Inject6(Asset data, SoulPaths paths, IReadOnlyDictionary<int, int> slots, bool retain)
    {
        var d = AssetCache.Take(paths.Stage5);
        using var survey = ReadJson(Path.Combine(paths.Updates, "soul-X6-analysis/special-space-survey.json"));
        var room = survey.RootElement.GetProperty("actors").EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ToHashSet();
        Require(room.Count == 560, "X6 survey changed");
        var (tile, _) = Ownership.Owned(d, 11904);
        var allActors = new HashSet<int>(room); allActors.UnionWith(tile);
        var excluded = allActors.Where(i => d.ObjectName(d.Export(i).ClassIndex.Index) is "BP_Kokeshi_C" or "BP_ArchiveItem_C" or "PlayerStart").ToHashSet();
        room.ExceptWith(excluded); allActors.ExceptWith(excluded);

        // Preserve approval: the terminal mirror returns to the tile's attached marker.
        using var patch = ReadJson(Path.Combine(paths.Updates, "soul-X6-analysis/terminal-mirror-patch.json"));
        var after = d.Raw.DeserializeJsonObject<Export>(patch.RootElement.GetProperty("after").GetRawText());
        after.Asset = d.Raw;
        d.Exports[5808] = after;
        d.InvalidateIndex();
        var dep = d.Raw.DependsMap[5808] ?? Array.Empty<int>();
        if (!dep.Contains(43850)) d.Raw.DependsMap[5808] = dep.Append(43850).ToArray();

        foreach (int i in new[] { 46247, 46248, 46262, 46267, 6330, 5797 })
            StripBinding(d, i, new[] { "OnActorBeginOverlap", "OnActorEndOverlap", "StartOpenAnim", "PullRever" });

        // Keep space intact, translate every unattached actor root by the same vector.
        TranslateRoots(d, room, 50000);
        var cl = BundleInjector.DonorClosure(d, allActors);
        Require(!d.Closure(excluded).Overlaps(cl), "excluded actors inside the bundle closure");
        foreach (int i in cl.OrderBy(x => x))
        {
            var e = d.Export(i);
            if (allActors.Contains(i) || e.OuterIndex.Index == 0)
            {
                var renamed = $"SCMix_Stage5_X6_{i}_" + e.ObjectName;
                e.ObjectName = Names.Make(d, renamed);
                BundleInjector.RegisterName(d, renamed);
            }
        }
        var rows = BundleInjector.Inject(data, new HashSet<int> { 6 }, d, slots, "Stage5", retain, landscape: true);
        foreach (var r in Recipes6)
            DonorCallbacks.InstallSoulHandler(data, paths.Stage5, "SCMix_Stage5_X6_", "Stage5_X6_", r.Label, r.Owner, r.Wrapper, r.Indices, r.States.ToHashSet(), r.Event);
        InstallLatent(data, paths);
        Compose.UpdateGenerations(data);
        return rows;
    }

    // ---- latent floor handler for X6 (latent_x6) ----

    static void InstallLatent(Asset data, SoulPaths paths)
        => LatentBuilder.Install(data, paths.Stage5, new LatentSpec(
            ResumeLabel: "Stage5_X6_FloorResume", StartLabel: "Stage5_X6_FloorStart", StateFieldName: "SCMix_Stage5_X6_FloorStarted",
            ActorPrefix: "SCMix_Stage5_X6_", BindOwner: 5797,
            Indices: new[] { 1, 674, 675 }.Concat(Enumerable.Range(4, 31)).ToArray(),
            WrapperIndex: _ => 2682,
            UuidFor: id => id switch { 172020150 => 160600001, -1688438197 => 160600002, _ => throw new InvalidOperationException("unexpected latent id " + id) },
            StartEntryKey: 19844, ExpectedActors: null));

    // ---- flowers (instanced foliage) for X6 ----

    static void AddFlowers(Asset data, SoulPaths paths)
    {
        var d = AssetCache.Take(paths.Stage5);
        var ids = d.Closure(new[] { 8046 });
        Require(ids.SetEquals(new[] { 7987, 7988, 7989, 7990, 7991, 7992, 8046, 11236 }), "flower bundle changed");
        int level = LevelChecks.Validate(data).Level;
        var sorted = ids.OrderBy(x => x).ToList();
        var mapping = new Dictionary<int, int>();
        for (int j = 0; j < sorted.Count; j++) mapping[sorted[j]] = data.Exports.Count + j + 1;
        mapping[8312] = level;
        foreach (var n in d.Raw.GetNameMapIndexList()) data.Raw.AddNameReference(n);
        var imp = DonorCallbacks.ImportMapper(d, data);
        int Remap(int i) => i < 0 ? imp(i) : i != 0 ? mapping[i] : 0;
        var dNames = d.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();
        foreach (int old in sorted)
        {
            var e = DeepClone.Of(d.Export(old), data.Raw);
            if (e.Extras != null && e.Extras.Any(x => x != 0))
            {
                var raw = (byte[])e.Extras.Clone();
                var parsed = NativePayload.Parse(d.ObjectName(e.ClassIndex.Index), raw, dNames);
                foreach (var r in parsed.Refs) RetainHost.WriteInt(raw, r.Offset, Remap(r.Value));
                e.Extras = raw;
            }
            Refs.RewriteNormal(e, Remap);
            var renamed = $"SCMix_Stage5_X6_{old}_" + e.ObjectName;
            e.ObjectName = Names.Make(data, renamed);
            e.Asset = data.Raw;
            data.Exports.Add(e);
            data.Raw.DependsMap.Add((d.Raw.DependsMap[old - 1] ?? Array.Empty<int>()).Select(Remap).ToArray());
            DonorCallbacks.AddNames(data, renamed, Regex.Replace(renamed, @"_\d+$", ""));
        }
        data.InvalidateIndex();
        // Instance matrices and cluster bounds are component-local; translating their shared root moves the whole bundle.
        var loc = DeepClone.Of(((NormalExport)d.Export(11258)).Data.First(p => p.Name.ToString() == "RelativeLocation"), data.Raw);
        var vector = (VectorPropertyData)((StructPropertyData)loc).Value[0];
        vector.Value = new FVector(50000.0, 0.0, 0.0);
        ((NormalExport)data.Export(mapping[11236])).Data.Add(loc);
        var lv = (LevelExport)data.Export(level);
        lv.Actors.Add(new FPackageIndex(mapping[8046]));
        lv.CreateBeforeSerializationDependencies.Add(new FPackageIndex(mapping[8046]));
        Compose.UpdateGenerations(data);
    }
}
