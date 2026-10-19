using System.Text.Json;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record LtEvent(int Owner, string Event, string Wrapper, string District);

public sealed record SanctuaryLtPaths(string RandomizerRoot, string CacheDirectory)
{
    public string Stage6 => Path.Combine(RandomizerRoot, "extracted/Kageroh/Content/Levels/RundomMapTiles/Stage6x5.umap");
    public string All => Path.Combine(RandomizerRoot, "app/updates/sanctuary-LT/all");
}

/// <summary>
/// Port of sanctuary_lt_bundles.inject with updates/sanctuary-LT/all_build (callbacks, bridges, shallow water) and bundle_core.
/// SCU-PATCH sanctuary_lt_bundles#1, updates/sanctuary-LT/bundle_core#1-#4 (RETAIN) are applied through <c>retain</c>.
/// The prepared donor and the shallow-water template ship as JSON from the randomizer's own preparation; they are
/// converted to binary packages once (cache directory).
/// </summary>
public static class SanctuaryLt
{
    const int ScriptIndex = 11413;   // LevelScriptActor of the pristine Stage6x5

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static Asset LoadCached(string jsonPath, string cachePath)
    {
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(jsonPath)) return Asset.Load(cachePath);
        var asset = Asset.LoadJson(jsonPath);
        Names.Finalize(asset);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        asset.Raw.Write(cachePath);
        return Asset.Load(cachePath);
    }

    public static (List<LtRow> Rows, LtIndexMap Map) Inject(Asset data, SanctuaryLtPaths paths, IList<string> selected, IReadOnlyDictionary<string, string> slots, bool retain)
    {
        Require(selected.Count > 0 && selected.Distinct().Count() == selected.Count && selected.All(LtBundles.AllDistricts.Contains), "unsupported Sanctuary L/T selection");
        var preparedDonor = Prepare.Resolve(paths.RandomizerRoot, "sanctuary-lt/prepared-donor.umap", null);
        var donor = preparedDonor != null ? Asset.Load(preparedDonor)
            : LoadCached(Path.Combine(paths.All, "prepared-donor.json"), Path.Combine(paths.CacheDirectory, "sanctuary-lt-prepared-donor.umap"));
        using var preflight = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.All, "donor-preflight.json")));
        var rows = preflight.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => selected.Contains(r.GetProperty("district").GetString()!))
            .Select(r => (District: r.GetProperty("district").GetString()!, Root: r.GetProperty("root").GetInt32())).ToList();
        using var eventDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.All, "event-plan.json")));
        var events = eventDoc.RootElement.EnumerateArray()
            .Select(e => new LtEvent(e.GetProperty("owner").GetInt32(), e.GetProperty("event").GetString()!, e.GetProperty("wrapper").GetString()!, e.GetProperty("district").GetString()!))
            .Where(e => selected.Contains(e.District)).ToList();
        Require(rows.Select(r => r.District).ToHashSet().SetEquals(selected), "donor preflight rows are incomplete");

        var (bundleRows, map) = LtBundles.InjectBundles(data, selected, donor, slots, "SCMix_Stage6_LT_", retain);
        var source = AssetCache.Take(paths.Stage6);   // pristine Stage6x5 (all_build "source")
        Callbacks(data, paths, source, map.Donor, events);
        foreach (var n in new[] { "L2", "T9" })
            if (selected.Contains(n)) Bridge(data, paths, source, map.Donor, events, n);
        Waters(data, paths, source, map.Donor, rows);
        foreach (var e in data.Exports)
        {
            var full = e.ObjectName.ToString();
            DonorCallbacks.AddNames(data, full, Regex.Replace(full, @"_\d+$", ""));
        }
        Compose.UpdateGenerations(data);
        LevelChecks.Validate(data);
        return (bundleRows, map);
    }

    // ---- all_build.plan / callbacks ----

    static (CallbackPlan Plan, int Wid) Plan(Asset proof, Asset source, string wrapper, string label, int owner, string eventName, ISet<int>? extraStates = null)
    {
        const string Uber = "ExecuteUbergraph_Stage6x5";
        var indices = HandlerPlanner.Reachable(source, Uber, wrapper, out _);
        var code = ((FunctionExport)source.Exports.First(e => e.ObjectName.ToString() == Uber)).ScriptBytecode;
        var pointers = indices.SelectMany(i => KismetOps.Find<KismetPropertyPointer>(code[i])).Select(p => p.Old.Index).Where(i => i > 0).ToHashSet();
        var states = pointers.Where(i =>
        {
            var n = proof.Export(i).ObjectName.ToString();
            return n.StartsWith("Temp_bool_IsClosed", StringComparison.Ordinal) || n.StartsWith("Temp_bool_Has_Been_Initd", StringComparison.Ordinal);
        }).ToHashSet();
        if (extraStates != null) states.UnionWith(extraStates);
        int wid = Enumerable.Range(1, proof.Exports.Count).First(i => proof.Exports[i - 1].ObjectName.ToString() == wrapper);
        var plan = CallbackBuilder.AddCallback(proof, 0, indices.Min(), indices.Max() + 1, states, "Stage6_LT_" + label, indices, wid, Uber, classOwnedStates: true);
        return (plan, wid);
    }

    static void Callbacks(Asset data, SanctuaryLtPaths paths, Asset source, IReadOnlyDictionary<int, int> dm, List<LtEvent> events)
    {
        var proof = AssetCache.Take(paths.Stage6);
        var defs = Props.Of(source.Export(3));
        CallbackPlan? shared = null;
        foreach (var ev in events)
        {
            if (ev.District is "L2" or "T9" && ev.Event == "PullRever") continue;
            if (ev.District == "T3" && ev.Event == "PullRever" && shared != null)
            {
                DonorCallbacks.Bind(data, dm[ev.Owner], ev.Event, shared.FunctionName);
                continue;
            }
            var (plan, _) = Plan(proof, source, ev.Wrapper, $"{ev.District}_{ev.Owner}_{ev.Event}", ev.Owner, ev.Event,
                ev.District == "T3" && ev.Event == "PullRever" ? new HashSet<int> { 2454 } : null);
            var refs = plan.SourceActorPropertiesRetained.Select(i => ((ObjectPropertyData)defs[proof.ObjectName(i)]).Value.Index).ToHashSet();
            Require(refs.IsSubsetOf(dm.Keys), $"callback external {ev.District}");
            CallbackBuilder.Integrate(data, proof, plan, refs.ToDictionary(i => i, i => dm[i]), DonorCallbacks.ImportMapper(proof, data));
            DonorCallbacks.Bind(data, dm[ev.Owner], ev.Event, plan.FunctionName);
            if (ev.District == "T3" && ev.Event == "PullRever")
            {
                Require(source.Export(ev.Owner) is NormalExport se && Props.Of(se).TryGetValue("DoOnce", out var doOnce) && doOnce is BoolPropertyData { Value: true }, "T3 lever is not DoOnce");
                shared = plan;
            }
        }
    }

    // ---- bridges (timeline driven) for L2 and T9: integrate_sanctuary_x9_bundle with the numbers substituted ----

    static void Bridge(Asset data, SanctuaryLtPaths paths, Asset source, IReadOnlyDictionary<int, int> dm, List<LtEvent> events, string n)
    {
        var proof = AssetCache.Take(paths.Stage6);
        int idx = n == "L2" ? 0 : 2;
        int component = 39276 + idx, template = 39285 + idx, curve = 6584 + idx, field = 8682 + idx, height = 7733 + idx;
        var ev = events.First(e => e.District == n && e.Event == "PullRever");
        var plans = new List<CallbackPlan>();
        foreach (var (wrapper, label) in new[] { (ev.Wrapper, "Start"), ($"タイムライン_{idx}__UpdateFunc", "Update"), ($"タイムライン_{idx}__FinishedFunc", "Finished") })
            plans.Add(Plan(proof, source, wrapper, n + "_" + label, label == "Start" ? ev.Owner : 0, label == "Start" ? "PullRever" : "").Plan);
        var defs = Props.Of(source.Export(3));
        var fields = plans.SelectMany(p => p.SourceActorPropertiesRetained).ToHashSet();
        var actors = fields.Where(i => i != field && i != height).Select(i => ((ObjectPropertyData)defs[proof.ObjectName(i)]).Value.Index).ToHashSet();
        Require(actors.IsSubsetOf(dm.Keys), $"bridge external {n}");
        var info = IntegrateBridge(data, proof, plans, actors.ToDictionary(i => i, i => dm[i]), DonorCallbacks.ImportMapper(proof, data), n, idx, template, curve, field, height);

        var imp = DonorCallbacks.ImportMapper(proof, data);
        int levelScript = LevelChecks.Validate(data).Script;
        int Ref(int v) => v < 0 ? imp(v) : v == ScriptIndex ? levelScript : v == curve ? info.Curve : v != 0 ? throw new InvalidOperationException("bridge timeline ref " + v) : 0;
        var e0 = DeepClone.Of(source.Export(component), data.Raw);
        Refs.RewriteNormal(e0, Ref);
        e0.ObjectName = Names.Make(data, $"SCMix_LT_{n}_BridgeTimeline");
        var names = info.Renamed.ToDictionary(kv => source.ObjectName(kv.Key), kv => kv.Value);
        names[$"タイムライン_{idx}__UpdateFunc"] = plans[1].FunctionName;
        names[$"タイムライン_{idx}__FinishedFunc"] = plans[2].FunctionName;
        RenameTimelineStrings((NormalExport)e0, names, data);
        int newIndex = data.Exports.Count + 1;
        e0.Asset = data.Raw;
        data.Exports.Add(e0);
        data.Raw.DependsMap.Add((source.Raw.DependsMap[component - 1] ?? Array.Empty<int>()).Select(Ref).ToArray());
        data.InvalidateIndex();
        var owner = (NormalExport)data.Export(levelScript);
        var sourceScript = (NormalExport)source.Export(ScriptIndex);
        var variable = (ObjectPropertyData)DeepClone.Of(sourceScript.Data.First(p => p.Name.ToString() == $"タイムライン_{idx}"), data.Raw);
        variable.Name = e0.ObjectName;
        variable.Value = new FPackageIndex(newIndex);
        owner.Data.Add(variable);
        var created = owner.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "BlueprintCreatedComponents");
        var sourceCreated = (ArrayPropertyData)sourceScript.Data.First(p => p.Name.ToString() == "BlueprintCreatedComponents");
        if (created == null)
        {
            created = DeepClone.Of(sourceCreated, data.Raw);
            created.Value = Array.Empty<PropertyData>();
            owner.Data.Add(created);
        }
        var item = (ObjectPropertyData)DeepClone.Of(sourceCreated.Value[0], data.Raw);
        item.Name = FName.DefineDummy(data.Raw, created.Value.Length.ToString(), 0);
        item.Value = new FPackageIndex(newIndex);
        created.Value = created.Value.Append(item).ToArray();
        created.DummyStruct = null;
        owner.CreateBeforeSerializationDependencies.Add(new FPackageIndex(newIndex));
        DonorCallbacks.Bind(data, dm[ev.Owner], "PullRever", plans[0].FunctionName);
    }

    /// <summary>Strings inside the timeline template that name renamed members ('Value' and 'Delegate' fields).</summary>
    static void RenameTimelineStrings(NormalExport e, Dictionary<string, string> names, Asset data)
    {
        ObjectWalker.WalkWithNames(e.Data, node =>
        {
            switch (node)
            {
                case NamePropertyData np when names.TryGetValue(np.Value.ToString(), out var renamed):
                    np.Value = Names.Make(data, renamed); break;
                case StrPropertyData sp when sp.Value != null && names.TryGetValue(sp.Value.ToString(), out var renamed2):
                    sp.Value = new FString(renamed2); break;
                case FDelegate d when names.TryGetValue(d.Delegate.ToString(), out var renamed3):
                    d.Delegate = Names.Make(data, renamed3); break;
            }
            return true;
        });
    }

    sealed record BridgeInfo(int Curve, Dictionary<int, string> Renamed);

    static BridgeInfo IntegrateBridge(Asset host, Asset proof, List<CallbackPlan> plans, IReadOnlyDictionary<int, int> actors, Func<int, int> imp,
        string n, int idx, int template, int curve, int field, int height)
    {
        var ex = host.Exports;
        var src = proof.Exports;
        var ids = new SortedSet<int> { template, curve, field, height };
        var templateExport = (NormalExport)src[template - 1];
        var timeline = ((NamePropertyData)templateExport.Data.First(p => p.Name.ToString() == "DirectionPropertyName")).Value.ToString();
        int direction = Enumerable.Range(1, src.Count).First(i => src[i - 1].OuterIndex.Index == 2 && src[i - 1].ObjectName.ToString() == timeline);
        ids.Add(direction);
        var funcs = plans.Select(p => p.Function).ToList();
        foreach (var p in plans)
        {
            ids.Add(p.Function);
            foreach (int s in p.StateFields) ids.Add(s);
            foreach (int s in p.SourceActorPropertiesRetained) ids.Add(s);
        }
        var todo = funcs.SelectMany(i => ((StructExport)src[i - 1]).Children.Select(c => c.Index)).ToList();
        while (todo.Count > 0)
        {
            int i = todo[^1]; todo.RemoveAt(todo.Count - 1);
            if (ids.Contains(i)) continue;
            ids.Add(i);
            if (src[i - 1] is PropertyExport { Property: UArrayProperty { Inner.Index: not 0 } array }) todo.Add(array.Inner.Index);
        }
        var m = new Dictionary<int, int>();
        int next = ex.Count;
        foreach (int i in ids) m[i] = ++next;
        var rename = ids.Where(i => src[i - 1].OuterIndex.Index == 2 && !funcs.Contains(i)).ToDictionary(i => i, i => $"SCMix_LT_{n}_" + src[i - 1].ObjectName);
        rename[field] = $"SCMix_LT_{n}_BridgeTimeline";
        rename[template] = $"SCMix_LT_{n}_BridgeTimeline_Template";
        rename[height] = $"SCMix_LT_{n}_BridgeHeight";
        rename[direction] = $"SCMix_LT_{n}_BridgeDirection";
        var names = rename.ToDictionary(kv => src[kv.Key - 1].ObjectName.ToString(), kv => kv.Value);
        names[$"タイムライン_{idx}__UpdateFunc"] = src[funcs[1] - 1].ObjectName.ToString();
        names[$"タイムライン_{idx}__FinishedFunc"] = src[funcs[2] - 1].ObjectName.ToString();

        foreach (var nm in proof.Raw.GetNameMapIndexList()) host.Raw.AddNameReference(nm);
        int Ref(int v)
        {
            if (v == 0) return 0;
            if (v < 0) return imp(v);
            if (v == 2) return 2;
            if (m.TryGetValue(v, out int mapped)) return mapped;
            if (!actors.TryGetValue(v, out int actor)) throw new InvalidOperationException($"bridge external reference {v} {src[v - 1].ObjectName}");
            return actor;
        }
        foreach (int i in ids)
        {
            var e = DeepClone.Of(src[i - 1], host.Raw);
            Refs.RewriteNormal(e, Ref, Refs.AllowedStreamingWorld);
            if (rename.TryGetValue(i, out var newName)) e.ObjectName = Names.Make(host, newName);
            if (e is StructExport st)
            {
                st.SuperStruct = new FPackageIndex(Ref(st.SuperStruct.Index));
                if (st.Children != null) st.Children = st.Children.Select(c => new FPackageIndex(Ref(c.Index))).ToArray();
            }
            if (e is PropertyExport { Property: { } prop })
                foreach (var f in new[] { "Inner", "PropertyClass", "SignatureFunction", "MetaClass", "Struct", "InterfaceClass", "Enum", "UnderlyingProp" })
                {
                    var member = prop.GetType().GetField(f);
                    if (member?.FieldType == typeof(FPackageIndex) && member.GetValue(prop) is FPackageIndex idxv)
                        member.SetValue(prop, new FPackageIndex(Ref(idxv.Index)));
                }
            if (e is StructExport { ScriptBytecode: { } code })
                foreach (var node in KismetOps.Find<object>(code))
                    switch (node)
                    {
                        case KismetPropertyPointer ptr: ptr.Old = new FPackageIndex(Ref(ptr.Old.Index)); break;
                        case EX_ObjectConst oc: oc.Value = new FPackageIndex(Ref(oc.Value.Index)); break;
                        case EX_CallMath cm: cm.StackNode = new FPackageIndex(Ref(cm.StackNode.Index)); break;
                        case EX_LocalFinalFunction lf: lf.StackNode = new FPackageIndex(Ref(lf.StackNode.Index)); break;
                        case EX_DynamicCast dc: dc.ClassPtr = new FPackageIndex(Ref(dc.ClassPtr.Index)); break;
                        default:
                            if (node.GetType() == typeof(EX_FinalFunction)) { var ff = (EX_FinalFunction)node; ff.StackNode = new FPackageIndex(Ref(ff.StackNode.Index)); }
                            break;
                    }
            if (i == template) RenameTimelineStrings((NormalExport)e, names, host);
            Require(m[i] == ex.Count + 1, "bridge export numbering drifted");
            e.Asset = host.Raw;
            ex.Add(e);
            host.Raw.DependsMap.Add((proof.Raw.DependsMap[i - 1] ?? Array.Empty<int>()).Select(Ref).ToArray());
        }
        host.InvalidateIndex();

        var cls = (ClassExport)ex[1];
        var classFields = ids.Where(i => src[i - 1].OuterIndex.Index == 2 && src[i - 1] is PropertyExport).Select(i => m[i]).ToList();
        cls.Children = funcs.Select(i => m[i]).Concat(classFields).Select(x => new FPackageIndex(x)).Concat(cls.Children).ToArray();
        cls.SerializationBeforeSerializationDependencies.AddRange(funcs.Select(i => m[i]).Concat(classFields).Append(m[template]).Select(x => new FPackageIndex(x)));
        foreach (int i in funcs) cls.FuncMap.Add(Names.Make(host, src[i - 1].ObjectName.ToString()), new FPackageIndex(m[i]));
        var cdo = (NormalExport)ex[2];
        var sourceCdo = (NormalExport)src[2];
        foreach (int i in ids)
        {
            if (!rename.ContainsKey(i) || src[i - 1] is not PropertyExport) continue;
            var original = sourceCdo.Data.FirstOrDefault(p => p.Name.ToString() == src[i - 1].ObjectName.ToString());
            if (original == null) continue;
            var p = DeepClone.Of(original, host.Raw);
            p.Name = Names.Make(host, rename[i]);
            Refs.RewriteData(new[] { p }, Ref, Refs.AllowedStreamingWorld);
            cdo.Data.Add(p);
            if (p is ObjectPropertyData { Value.Index: > 0 } op) cdo.CreateBeforeSerializationDependencies.Add(new FPackageIndex(op.Value.Index));
        }
        var classData = (NormalExport)cls;
        var timelines = classData.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Timelines");
        var sourceTimelines = (ArrayPropertyData)((NormalExport)src[1]).Data.First(p => p.Name.ToString() == "Timelines");
        if (timelines == null)
        {
            timelines = DeepClone.Of(sourceTimelines, host.Raw);
            timelines.Value = Array.Empty<PropertyData>();
            classData.Data.Add(timelines);
        }
        var entry = (ObjectPropertyData)DeepClone.Of(sourceTimelines.Value[0], host.Raw);
        entry.Name = FName.DefineDummy(host.Raw, timelines.Value.Length.ToString(), 0);
        entry.Value = new FPackageIndex(m[template]);
        timelines.Value = timelines.Value.Append(entry).ToArray();
        foreach (var nm in names.Values) DonorCallbacks.AddNames(host, nm, Regex.Replace(nm, @"_\d+$", ""));
        foreach (var e in ex)
        {
            var full = e.ObjectName.ToString();
            DonorCallbacks.AddNames(host, full, Regex.Replace(full, @"_\d+$", ""));
        }
        Compose.UpdateGenerations(host);
        return new BridgeInfo(m[curve], rename);
    }

    // ---- shallow water (sanctuary_lt_bundles.waters) ----

    static void Waters(Asset data, SanctuaryLtPaths paths, Asset source, IReadOnlyDictionary<int, int> dm, List<(string District, int Root)> rows)
    {
        var preparedAll = Prepare.Resolve(paths.RandomizerRoot, "sanctuary-lt/all.umap", null);
        var src = preparedAll != null ? Asset.Load(preparedAll) : LoadCached(Path.Combine(paths.All, "parsed.json"), Path.Combine(paths.CacheDirectory, "sanctuary-lt-all.umap"));
        int lv = LevelChecks.Validate(data).Level;
        int old = Enumerable.Range(1, src.Exports.Count).First(i => src.Exports[i - 1].ObjectName.ToString() == "SCMix_Shallow_Stage6_LT_L1");
        var group = src.Closure(new[] { old });
        Require(group.Count == 3, "shallow water template changed");
        int parent = Props.Ref(src.Export(Props.Ref(src.Export(old), "RootComponent")), "AttachParent");
        int sl = LevelChecks.Validate(src).Level;
        foreach (var nm in src.Raw.GetNameMapIndexList()) data.Raw.AddNameReference(nm);
        var imp = DonorCallbacks.ImportMapper(src, data);
        var level = (LevelExport)data.Export(lv);
        var ordered = group.OrderBy(x => x).ToList();
        foreach (var row in rows)
        {
            var mapping = new Dictionary<int, int>();
            for (int j = 0; j < ordered.Count; j++) mapping[ordered[j]] = data.Exports.Count + j + 1;
            mapping[parent] = dm[Props.Ref(source.Export(row.Root), "RootComponent")];
            mapping[sl] = lv;
            int Ref(int v) => v < 0 ? imp(v) : v != 0 ? mapping[v] : 0;
            foreach (int i in ordered)
            {
                var e = DeepClone.Of(src.Export(i), data.Raw);
                Require(e.Extras == null || e.Extras.All(x => x == 0), "shallow water has native data");
                Refs.RewriteNormal(e, Ref);
                if (i == old) e.ObjectName = Names.Make(data, "SCMix_Shallow_Stage6_LT_" + row.District);
                e.Asset = data.Raw;
                data.Exports.Add(e);
                data.Raw.DependsMap.Add((src.Raw.DependsMap[i - 1] ?? Array.Empty<int>()).Select(Ref).ToArray());
            }
            data.InvalidateIndex();
            int actor = mapping[old];
            level.Actors.Add(new FPackageIndex(actor));
            level.CreateBeforeSerializationDependencies.Add(new FPackageIndex(actor));
        }
    }
}
