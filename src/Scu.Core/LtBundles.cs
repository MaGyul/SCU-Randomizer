using System.Text.Json;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record LtRow(string District, string Slot, int Actors, int Exports);

/// <summary>The index map the Python writes to index-map.json: where host and donor exports ended up.</summary>
public sealed class LtIndexMap
{
    public Dictionary<int, int> Host = new();
    public Dictionary<int, int> Donor = new();
    public List<int> Retired = new();
}

public sealed record LtRecipe(string District, int Owner, string Event, string? Wrapper);

/// <summary>
/// Port of lt_bundles / soul_lt_bundles / updates/sanctuary-LT/bundle_core: add the saved 2-3 branch ("L" and "T") districts
/// of one donor stage next to the host's own. SCU-PATCH lt_bundles#1-#4, soul_lt_bundles#1-#4 and bundle_core#1-#4
/// (RETAIN, <c>SCU_RETAIN_LT=1</c>) are embedded: host room actors get an SCApp_Host_ prefix and stay in the package.
/// </summary>
public static class LtBundles
{
    public static readonly string[] AllDistricts = Enumerable.Range(1, 4).Select(i => "L" + i).Concat(Enumerable.Range(1, 10).Select(i => "T" + i)).ToArray();

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>lt_bundles.topology: the actors attached (through any chain) to a root's root component, and their closure.</summary>
    public static (HashSet<int> Actors, HashSet<int> Closure) Topology(Asset a, int root)
    {
        int lv = a.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        int rc = Props.Ref(a.Export(root), "RootComponent");
        var actors = new HashSet<int>();
        foreach (var p in ((LevelExport)a.Export(lv)).Actors)
        {
            if (p.Index <= 0) continue;
            int c = Props.Ref(a.Export(p.Index), "RootComponent");
            var seen = new HashSet<int>();
            while (c != 0)
            {
                Require(seen.Add(c), "attachment cycle");
                if (c == rc) { actors.Add(p.Index); break; }
                c = Props.Ref(a.Export(c), "AttachParent");
            }
        }
        return (actors, a.Closure(actors));
    }

    /// <param name="donorPrefix">"SCMix_Stage5_", "SCMix_Extra_" or "SCMix_Stage6_LT_".</param>
    public static (List<LtRow> Rows, LtIndexMap Map) InjectBundles(Asset a, IList<string> selected, Asset d, IReadOnlyDictionary<string, string> slots, string donorPrefix, bool retain)
    {
        int hostCount = a.Exports.Count;
        int levelId = LevelChecks.Validate(a).Level;
        int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        var aLevel = (LevelExport)a.Export(levelId);
        var dLevel = (LevelExport)d.Export(dl);
        var ret = new HashSet<int>();
        var bundles = new List<(string N, string Slot, int Root, int Dr, HashSet<int> Da, HashSet<int> Dc)>();
        foreach (var n in selected)
        {
            var slot = slots.TryGetValue(n, out var s) ? s : n;
            var pattern = new Regex($@"^(?:SCApp_Host_{slot}_\d+_)?ArireRoot_{slot}(?:_447)?$");
            int root = aLevel.Actors.Select(p => p.Index).First(i => i > 0 && pattern.IsMatch(a.Export(i).ObjectName.ToString()));
            var (actors, hc) = Topology(a, root);
            if (retain)
            {
                // SCU-PATCH: keep the host room; prefix its actors with the slot so the runtime can find the room's members.
                foreach (int ai in actors.OrderBy(x => x))
                {
                    var ae = a.Export(ai);
                    if (!ae.ObjectName.ToString().StartsWith("SCApp_Host_", StringComparison.Ordinal))
                        ae.ObjectName = Names.Make(a, $"SCApp_Host_{slot}_{ai}_" + ae.ObjectName);
                }
            }
            else ret.UnionWith(hc);
            int dr = dLevel.Actors.Select(p => p.Index).First(i => i > 0 && d.Export(i).ObjectName.ToString().Contains("_ArireRoot_" + n, StringComparison.Ordinal)
                && d.Export(i).ObjectName.ToString().StartsWith($"{donorPrefix}{n}_", StringComparison.Ordinal));
            var da = dLevel.Actors.Select(p => p.Index).Where(i => i > 0 && d.Export(i).ObjectName.ToString().StartsWith($"{donorPrefix}{n}_", StringComparison.Ordinal)).ToHashSet();
            bundles.Add((n, slot, root, dr, da, d.Closure(da)));
        }

        foreach (var nm in d.Raw.GetNameMapIndexList()) a.Raw.AddNameReference(nm);
        var imp = DonorCallbacks.ImportMapper(d, a);
        var dcAll = bundles.SelectMany(b => b.Dc).ToHashSet();
        var mapping = new Dictionary<int, int>();
        int next = a.Exports.Count;
        foreach (int old in dcAll.OrderBy(x => x)) mapping[old] = ++next;
        mapping[dl] = levelId;
        int Donor(int v) => v < 0 ? imp(v) : v != 0 ? mapping[v] : 0;

        var replacement = new Dictionary<int, int>();
        var nativeSources = new Dictionary<int, (string Class, byte[] Raw, IReadOnlyList<string> Names)>();
        var newActors = new List<int>();
        var dNames = d.Raw.GetNameMapIndexList().Select(x => x.ToString()).ToList();
        foreach (var (n, slot, root, dr, da, dc) in bundles)
        {
            if (!retain)
            {
                replacement[root] = mapping[dr];
                replacement[Props.Ref(a.Export(root), "RootComponent")] = mapping[Props.Ref(d.Export(dr), "RootComponent")];
            }
            newActors.AddRange(dLevel.Actors.Select(p => p.Index).Where(da.Contains).Select(i => mapping[i]));
        }
        foreach (int old in dcAll.OrderBy(x => x))
        {
            var e = DeepClone.Of(d.Export(old), a.Raw);
            Require(e.GetType() == typeof(NormalExport), "donor export type");
            foreach (int v in Refs.CollectNormal(e)) Donor(v);
            if (e.Extras != null && e.Extras.Any(x => x != 0))
                nativeSources[mapping[old]] = (d.ObjectName(e.ClassIndex.Index), (byte[])e.Extras.Clone(), dNames);
            Refs.RewriteNormal(e, Donor);
            Require(mapping[old] == a.Exports.Count + 1, "export numbering drifted");
            e.Asset = a.Raw;
            a.Exports.Add(e);
            a.Raw.DependsMap.Add((d.Raw.DependsMap[old - 1] ?? Array.Empty<int>()).Select(Donor).ToArray());
        }
        a.InvalidateIndex();

        foreach (var (n, slot, root, dr, da, dc) in bundles)
        {
            var target = (NormalExport)a.Export(mapping[dr]);
            var old = (NormalExport)a.Export(root);
            target.Data = target.Data.Where(p => p.Name.ToString() != "Tags")
                .Concat(old.Data.Where(p => p.Name.ToString() == "Tags").Select(p => DeepClone.Of(p, a.Raw))).ToList();
            var dst = (NormalExport)a.Export(mapping[Props.Ref(d.Export(dr), "RootComponent")]);
            var src = (NormalExport)a.Export(Props.Ref(old, "RootComponent"));
            var keys = new[] { "RelativeLocation", "RelativeRotation", "RelativeScale3D" };
            dst.Data = dst.Data.Where(p => !keys.Contains(p.Name.ToString()))
                .Concat(src.Data.Where(p => keys.Contains(p.Name.ToString())).Select(p => DeepClone.Of(p, a.Raw))).ToList();
        }
        aLevel.Actors = aLevel.Actors.Where(v => !ret.Contains(v.Index)).Concat(newActors.Select(x => new FPackageIndex(x))).ToList();
        aLevel.CreateBeforeSerializationDependencies.AddRange(newActors.Select(x => new FPackageIndex(x)));

        var ids = Enumerable.Range(1, a.Exports.Count).Where(i => !ret.Contains(i)).ToList();
        var move = new Dictionary<int, int>();
        for (int j = 0; j < ids.Count; j++) move[ids[j]] = j + 1;
        int Remap(int v)
        {
            if (v <= 0) return v;
            int target = replacement.GetValueOrDefault(v, v);
            Require(move.ContainsKey(target), $"unresolved retained reference {v}");
            return move[target];
        }
        var aNames = a.Raw.GetNameMapIndexList().Select(x => x.ToString()).ToList();
        var exports = new List<Export>();
        var deps = new List<int[]>();
        foreach (int i in ids)
        {
            var e = a.Export(i);
            foreach (var list in new[] { e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies })
                list.RemoveAll(v => ret.Contains(v.Index) && !replacement.ContainsKey(v.Index));
            var dep = (a.Raw.DependsMap[i - 1] ?? Array.Empty<int>()).Where(v => !ret.Contains(v) || replacement.ContainsKey(v)).ToArray();
            if (e.GetType() == typeof(NormalExport) && e.Extras != null && e.Extras.Any(x => x != 0))
            {
                var raw = (byte[])e.Extras.Clone();
                bool moved = nativeSources.TryGetValue(i, out var srcInfo);
                var cls = moved ? srcInfo.Class : a.ObjectName(e.ClassIndex.Index);
                var parsed = NativePayload.Parse(cls, moved ? srcInfo.Raw : raw, moved ? srcInfo.Names : aNames);
                foreach (var r in parsed.Refs) RetainHost.WriteInt(raw, r.Offset, Remap(moved ? Donor(r.Value) : r.Value));
                foreach (var f in parsed.Formats) RetainHost.WriteInt(raw, f.OffsetField - 20, aNames.IndexOf(f.Name));
                e.Extras = raw;
            }
            Refs.RewriteNormal(e, Remap, Refs.AllowedStreamingWorld);
            Refs.RewriteExtra(e, Remap);
            if (e is LevelExport lv) foreach (var p in lv.Actors) p.Index = Remap(p.Index);
            exports.Add(e);
            deps.Add(dep.Select(Remap).ToArray());
        }
        a.Raw.Exports.Clear(); a.Raw.Exports.AddRange(exports);
        a.Raw.DependsMap.Clear(); a.Raw.DependsMap.AddRange(deps);
        a.InvalidateIndex();
        Compose.UpdateGenerations(a);
        LevelChecks.Validate(a);
        ComposePolicy.Check(a);

        var map = new LtIndexMap { Retired = ret.OrderBy(x => x).ToList() };
        foreach (int i in ids) if (i <= hostCount) map.Host[i] = move[i];
        foreach (var kv in mapping) map.Donor[kv.Key] = move[kv.Value];
        return (bundles.Select(b => new LtRow(b.N, b.Slot, b.Da.Count, b.Dc.Count)).ToList(), map);
    }

    /// <summary>The recipes' district without the trailing b/e marker used to tell begin/end overlap apart (n.rstrip('be')).</summary>
    public static string DistrictOf(string tag) => tag.TrimEnd('b', 'e');

    /// <summary>soul_lt_bundles.install / lt_bundles.install: lift each recipe's handler out of the donor and bind it.</summary>
    public static List<CallbackPlan> InstallCallbacks(Asset data, string donorUmap, string uberName, Func<LtRecipe, string> label,
        string actorPrefix, IEnumerable<string> selected, IEnumerable<LtRecipe> recipes)
    {
        var sel = selected.ToHashSet();
        var plans = new List<CallbackPlan>();
        foreach (var r in recipes)
        {
            var district = DistrictOf(r.District);
            if (!sel.Contains(district)) continue;
            var (proof, plan) = HandlerPlanner.PlanFor(donorUmap, uberName, label(r), r.Owner, r.Event, r.Wrapper);
            var imp = DonorCallbacks.ImportMapper(proof, data);
            int levelId = LevelChecks.Validate(data).Level;
            var pattern = new Regex($@"^{Regex.Escape(actorPrefix)}{district}_(\d+)_");
            var mapping = new Dictionary<int, int>();
            for (int i = 1; i <= data.Exports.Count; i++)
            {
                var e = data.Exports[i - 1];
                if (e.OuterIndex.Index != levelId) continue;
                var m = pattern.Match(e.ObjectName.ToString());
                if (m.Success) mapping[int.Parse(m.Groups[1].Value)] = i;
            }
            var proofCdo = Props.Of(proof.Export(3));
            var actorRefs = plan.SourceActorPropertiesRetained.Select(i => ((ObjectPropertyData)proofCdo[proof.ObjectName(i)]).Value.Index).ToHashSet();
            Require(actorRefs.IsSubsetOf(mapping.Keys), $"callback crosses district {r.District}");
            CallbackBuilder.Integrate(data, proof, plan, actorRefs.ToDictionary(o => o, o => mapping[o]), imp);
            DonorCallbacks.Bind(data, mapping[r.Owner], r.Event, plan.FunctionName);
            plans.Add(plan);
        }
        return plans;
    }
}
