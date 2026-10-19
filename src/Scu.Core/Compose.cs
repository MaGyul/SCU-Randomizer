using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed class ComposePlan
{
    public List<int> Sanctuary = new();
    public Dictionary<string, int> SanctuarySlots = new();
    public List<int> HostSlots = new();
    public int RemovedExports;
    public int AddedExports;
    public List<string> ClearedCallbackFields = new();
    public List<int> PoolRoots = new();
    public int Level;
    public bool RetainedPostGoalAnchor;
}

/// <summary>
/// Port of composer.compose / sanctuary_relocation.prepare: start from the prepared "all Sanctuary rooms" Random5x5
/// package, put the Cicada host rooms back, and (SCU) keep the Sanctuary donor rooms in the pool instead of retiring them.
/// SCU-PATCH app/composer.py #1-#8 (RETAIN donors) are embedded here, see randomizer/PATCHES.md section 1.
/// </summary>
public static class Compose
{
    /// <summary>MAP: pool slot -> Sanctuary donor number whose room is natively at that slot.</summary>
    public static readonly IReadOnlyDictionary<int, int> Map = new Dictionary<int, int>
    {
        [1] = 1, [2] = 3, [3] = 2, [4] = 7, [5] = 11, [6] = 4, [7] = 10, [8] = 8, [9] = 6, [10] = 5, [11] = 9,
    };
    static readonly Dictionary<int, int> Home = Map.ToDictionary(kv => kv.Value, kv => kv.Key);

    static readonly Regex DonorRoot = new(@"^SCMix_Stage6_X(\d+)_\d+_ArireRoot_X\d+(?:_447)?$", RegexOptions.Compiled);
    static readonly Regex PoolField = new(@"^ArireRoot_X(\d+)(?:_447)?_EdGraph_1_RefProperty$", RegexOptions.Compiled);
    static readonly Regex TrailingNumber = new(@"_\d+$", RegexOptions.Compiled);
    static readonly string[] RootKeys = { "Tags" };
    static readonly string[] ComponentKeys = { "RelativeLocation", "RelativeRotation", "RelativeScale3D" };

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Final slot -> donor assignment, in the Python dict's insertion order (the order drives export numbering).
    /// </summary>
    public static List<(int Slot, int Donor)> Prepare(Asset a, Asset h, IEnumerable<int> selectedDonors, IDictionary<int, int>? overrides = null)
    {
        var selected = selectedDonors.ToHashSet();
        var dest = selected.ToDictionary(n => n, n => Home[n]);
        if (overrides != null)
        {
            Require(overrides.Keys.All(selected.Contains) && overrides.Values.All(Map.ContainsKey), "Unsupported Sanctuary relocation");
            foreach (var kv in overrides) dest[kv.Key] = kv.Value;
        }
        Require(dest.Values.Distinct().Count() == dest.Count, "Sanctuary slot collision");
        var effective = new List<(int Slot, int Donor)>(dest.Select(kv => (kv.Value, kv.Key)));
        bool Has(int slot) => effective.Any(x => x.Slot == slot);
        var pending = new List<int>();
        foreach (int n in Home.Keys.Where(k => !selected.Contains(k)).OrderBy(k => k))
        {
            if (!Has(Home[n])) effective.Add((Home[n], n)); else pending.Add(n);
        }
        var free = Map.Keys.Where(s => !Has(s)).OrderBy(s => s).ToList();
        for (int i = 0; i < pending.Count; i++) effective.Add((free[i], pending[i]));
        Require(effective.Select(x => x.Slot).ToHashSet().SetEquals(Map.Keys) && effective.Select(x => x.Donor).ToHashSet().SetEquals(Home.Keys), "bad slot assignment");

        var rr = new Dictionary<int, int>();
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            var m = DonorRoot.Match(a.Exports[i - 1].ObjectName.ToString());
            if (m.Success) rr[int.Parse(m.Groups[1].Value)] = i;
        }
        Require(rr.Keys.ToHashSet().SetEquals(Home.Keys), "Sanctuary donor roots missing");
        var slotDonor = effective.ToDictionary(x => x.Slot, x => x.Donor);
        var cdo = (NormalExport)a.Exports.First(e => e.ObjectName.ToString() == "Default__Random5x5_C");
        foreach (var p in cdo.Data)
        {
            var m = PoolField.Match(p.Name.ToString());
            if (m.Success) ((ObjectPropertyData)p).Value = new FPackageIndex(rr[slotDonor[int.Parse(m.Groups[1].Value)]]);
        }
        foreach (var (slot, n) in effective)
        {
            var root = (NormalExport)a.Export(rr[n]);
            var old = (NormalExport)h.Exports.First(e => Regex.IsMatch(e.ObjectName.ToString(), $@"^ArireRoot_X{slot}(?:_447)?$"));
            var pairs = new (NormalExport Target, NormalExport Source, string[] Keys)[]
            {
                (root, old, RootKeys),
                ((NormalExport)a.Export(Props.Ref(root, "RootComponent")), (NormalExport)h.Export(Props.Ref(old, "RootComponent")), ComponentKeys),
            };
            foreach (var (target, source, keys) in pairs)
            {
                var kept = target.Data.Where(p => !keys.Contains(p.Name.ToString()));
                var copied = source.Data.Where(p => keys.Contains(p.Name.ToString())).Select(p => DeepClone.Of(p, a.Raw));
                target.Data = kept.Concat(copied).ToList();
            }
        }
        return effective;
    }

    /// <param name="a">Prepared "all Sanctuary rooms" Random5x5 (reports/.../sanctuary-all-X-trapstatefix/Random5x5.umap); modified in place.</param>
    /// <param name="h">Pristine Random5x5.</param>
    public static (Asset Data, List<NativeEntry> Native, ComposePlan Plan) Run(
        Asset a, Asset h, IEnumerable<int> keepDonors, IEnumerable<int> retainDonors, IDictionary<int, int>? sanctuarySlots = null)
    {
        var keep = keepDonors.ToHashSet();
        var retain = retainDonors.ToHashSet();   // SCU-PATCH composer#1
        Require(keep.IsSubsetOf(Map.Values), "unknown Sanctuary donor");
        var ret = new HashSet<int>();

        // Names are copied between packages with their indices re-bound, so the host names must already be present.
        // The order matches the original: nothing else extends the name map before this point.
        foreach (var n in h.Raw.GetNameMapIndexList()) a.Raw.AddNameReference(n);

        var effective = Prepare(a, h, keep, sanctuarySlots);
        const int LevelIndex = 7094, ScriptIndex = 7720, TimelineIndex = 32348;
        Require(HostComposer.LevelIds(a).Level == LevelIndex && HostComposer.LevelIds(h).Level == LevelIndex, "unexpected level index");
        var aLevel = (LevelExport)a.Export(LevelIndex);
        var hLevel = (LevelExport)h.Export(LevelIndex);

        // The historical Sanctuary baseline contains a reused host guard attached to X8, and an archive item on X4.
        // SCU-PATCH composer#2/#3: also removed when those donors are retained.
        if (keep.Contains(8) || retain.Contains(8))
        {
            var stale = Enumerable.Range(1, a.Exports.Count).Where(i => a.Exports[i - 1].ObjectName.ToString() == "BP_EnemySpawner5").ToList();
            Require(stale.Count == 1, "stale X8 guard");
            ret.UnionWith(a.Closure(stale));
        }
        if (keep.Contains(4) || retain.Contains(4))
        {
            var stale = Enumerable.Range(1, a.Exports.Count).Where(i => a.Exports[i - 1].ObjectName.ToString() == "BP_ArchiveItem5_2700").ToList();
            Require(stale.Count == 1, "stale X4 archive");
            ret.UnionWith(a.Closure(stale));
        }

        bool Below(int comp, int root)
        {
            var seen = new HashSet<int>();
            while (comp != 0 && seen.Add(comp))
            {
                if (comp == root) return true;
                comp = Props.Ref(a.Export(comp), "AttachParent");
            }
            return false;
        }

        var hostTiles = Ownership.TileOfActors(h);
        var restores = new List<(int Slot, int Hr, int Dr, int Rc, HashSet<int> Ha, HashSet<int> Hc)>();
        var retainedDr = new HashSet<int>();   // SCU-PATCH composer#4
        foreach (var (slot, donor) in effective)
        {
            if (keep.Contains(donor)) continue;
            bool isRetain = retain.Contains(donor);
            int hr = Enumerable.Range(1, h.Exports.Count).First(i => Regex.IsMatch(h.Exports[i - 1].ObjectName.ToString(), $@"^ArireRoot_X{slot}(?:_447)?$"));
            int dr = Enumerable.Range(1, a.Exports.Count).First(i =>
            {
                var name = a.Exports[i - 1].ObjectName.ToString();
                return name.StartsWith($"SCMix_Stage6_X{donor}_", StringComparison.Ordinal) && Regex.IsMatch(name, @"_ArireRoot_X\d+(?:_447)?$");
            });
            int rc = Props.Ref(a.Export(dr), "RootComponent");
            var actors = aLevel.Actors.Select(p => p.Index).Where(i => i > 0 && Below(Props.Ref(a.Export(i), "RootComponent"), rc)).ToHashSet();
            Require(actors.Contains(dr), "donor root not among its actors");
            // SCU-PATCH composer#5: a retained donor room keeps its exports.
            if (isRetain) retainedDr.Add(dr); else ret.UnionWith(a.Closure(actors));
            var (ha, hc) = Ownership.Owned(h, hr, hostTiles);
            var omit = h.Closure(ha.Where(i => h.ObjectName(h.Export(i).ClassIndex.Index) == "BP_Kokeshi_C"));
            ha.ExceptWith(omit); hc.ExceptWith(omit);
            restores.Add((slot, hr, dr, rc, ha, hc));
        }

        // Timeline is not needed when its district is absent; keep harmless class template/functions.
        // SCU-PATCH composer#7: also kept when X9 is retained.
        if (!keep.Contains(9) && !retain.Contains(9))
        {
            Require(a.ObjectName(TimelineIndex) == "SCMix_X9_BridgeTimeline", "X9 timeline moved");
            ret.Add(TimelineIndex);
            var owner = (NormalExport)a.Export(ScriptIndex);
            owner.Data = owner.Data.Where(p => p.Name.ToString() != "SCMix_X9_BridgeTimeline").ToList();
            foreach (var p in owner.Data.OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "BlueprintCreatedComponents"))
            {
                p.Value = p.Value.Where(v => ((ObjectPropertyData)v).Value.Index != TimelineIndex).ToArray();
                if (p.Value.Length == 0) p.DummyStruct = null;
            }
        }

        var newmap = new Dictionary<int, int>();
        var replacement = new Dictionary<int, int>();
        var addedActors = new List<int>();
        var nativeSource = new Dictionary<int, (string Class, byte[] Raw, IReadOnlyList<string> Names)>();
        var importLookup = new Dictionary<(string, string, string), int>();
        for (int i = -1; i >= -a.Imports.Count; i--)
            importLookup[(a.Path(i), a.Imports[-i - 1].ClassPackage.ToString(), a.Imports[-i - 1].ClassName.ToString())] = i;
        var imports = new Dictionary<int, int>();
        int Imp(int i)
        {
            if (imports.TryGetValue(i, out int cached)) return cached;
            var e = h.Imports[-i - 1];
            int parent = e.OuterIndex.Index != 0 ? Imp(e.OuterIndex.Index) : 0;
            var key = (h.Path(i), e.ClassPackage.ToString(), e.ClassName.ToString());
            if (!importLookup.ContainsKey(key))
            {
                var created = DeepClone.Of(e, a.Raw);
                created.OuterIndex = new FPackageIndex(parent);
                a.Imports.Add(created);
                importLookup[key] = -a.Imports.Count;
            }
            return imports[i] = importLookup[key];
        }

        var allHc = restores.SelectMany(r => r.Hc).ToHashSet();
        foreach (int old in allHc.OrderBy(x => x)) newmap[old] = a.Exports.Count + newmap.Count + 1;
        newmap[LevelIndex] = LevelIndex;
        int Donor(int v) => v < 0 ? Imp(v) : v != 0 ? newmap[v] : 0;

        var cdoExport = (NormalExport)a.Exports.First(e => e.ObjectName.ToString() == "Default__Random5x5_C");
        foreach (var (slot, hr, dr, rc, ha, hc) in restores)
        {
            if (retainedDr.Contains(dr))
            {
                // SCU-PATCH composer#6: the retained donor stays; only the pool field of its host slot points at the restored host room.
                foreach (var p in cdoExport.Data.OfType<ObjectPropertyData>())
                    if (Regex.IsMatch(p.Name.ToString(), $@"^ArireRoot_X{slot}(?:_447)?_EdGraph_1_RefProperty$")) p.Value = new FPackageIndex(newmap[hr]);
            }
            else
            {
                replacement[dr] = newmap[hr];
                replacement[rc] = newmap[Props.Ref(h.Export(hr), "RootComponent")];
            }
            addedActors.AddRange(hLevel.Actors.Select(p => p.Index).Where(ha.Contains).Select(i => newmap[i]));
        }

        // Retained host markers may be referenced by global systems; restore their identity mapping.
        var originalPaths = new Dictionary<string, int>();
        foreach (int j in allHc) originalPaths[h.Path(j)] = j;
        foreach (int j in ret)
            if (originalPaths.TryGetValue(a.Path(j), out int sourceId)) replacement[j] = newmap[sourceId];

        // This TargetPoint belongs to the global post-goal spawner even though its scene component is attached to a room.
        var goalSpawners = Enumerable.Range(1, a.Exports.Count).Where(i => a.Exports[i - 1].ObjectName.ToString() == "BP_MotherShadowSpawner_353").ToList();
        Require(goalSpawners.Count == 1, "goal spawner");
        int goalSpawner = goalSpawners[0];
        int goalAnchor = Props.Ref(a.Export(goalSpawner), "EnemySpawnerPositionAfterGoal");
        Require(a.ObjectName(goalAnchor) == "EnemySpawnerLocationAfterGoal" && a.ObjectName(a.Export(goalAnchor).ClassIndex.Index) == "TargetPoint", "goal anchor");
        bool retainedGoalAnchor = ret.Contains(goalAnchor) && !replacement.ContainsKey(goalAnchor);
        if (retainedGoalAnchor)
        {
            Require(!ret.Contains(goalSpawner) && aLevel.Actors.Any(p => p.Index == goalAnchor), "goal anchor ownership");
            ret.ExceptWith(a.Closure(new[] { goalAnchor }));
        }

        var renameSlots = new Dictionary<int, int>();
        foreach (var r in restores) foreach (int i in r.Ha) renameSlots[i] = r.Slot;
        var renames = new Dictionary<Export, string>(ReferenceEqualityComparer.Instance);
        foreach (int old in allHc.OrderBy(x => x))
        {
            var e = DeepClone.Of(h.Export(old), a.Raw);
            Require(e.GetType() == typeof(NormalExport), "host export type");
            if (e.Extras != null && e.Extras.Any(x => x != 0))
                nativeSource[newmap[old]] = (h.ObjectName(e.ClassIndex.Index), (byte[])e.Extras.Clone(), h.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList());
            Refs.RewriteNormal(e, Donor);
            // Distinct names prevent accidentally retaining retired host equipment identities.
            if (renameSlots.TryGetValue(old, out int slot)) renames[e] = $"SCApp_Host_X{slot}_{old}_" + e.ObjectName;
            Require(newmap[old] == a.Exports.Count + 1, "export numbering drifted");
            e.Asset = a.Raw;
            a.Exports.Add(e);
            a.Raw.DependsMap.Add((h.Raw.DependsMap[old - 1] ?? Array.Empty<int>()).Select(Donor).ToArray());
        }
        a.InvalidateIndex();

        // Clear only known private callback Actor fields of omitted donor districts.
        var cleared = new List<string>();
        foreach (var p in ((NormalExport)a.Export(3)).Data.OfType<ObjectPropertyData>())
        {
            if (p.GetType() != typeof(ObjectPropertyData)) continue;
            int v = p.Value.Index;
            if (ret.Contains(v) && !replacement.ContainsKey(v))
            {
                Require(p.Name.ToString().StartsWith("SCMix_", StringComparison.Ordinal), "unknown retained reference");
                cleared.Add(p.Name.ToString());
                p.Value = new FPackageIndex(0);
            }
        }
        aLevel.Actors = aLevel.Actors.Where(v => !ret.Contains(v.Index)).Concat(addedActors.Select(x => new FPackageIndex(x))).ToList();
        aLevel.CreateBeforeSerializationDependencies.AddRange(addedActors.Select(x => new FPackageIndex(x)));

        // Complete compact index map. Every remaining reference must resolve.
        var survivors = Enumerable.Range(1, a.Exports.Count).Where(i => !ret.Contains(i)).ToList();
        var move = new Dictionary<int, int>();
        for (int j = 0; j < survivors.Count; j++) move[survivors[j]] = j + 1;
        int Remap(int v)
        {
            if (v <= 0) return v;
            int target = replacement.GetValueOrDefault(v, v);
            Require(move.ContainsKey(target), $"unresolved reference {v} {a.ObjectName(v)}");
            return move[target];
        }

        foreach (var e in a.Exports)
        {
            var full = renames.TryGetValue(e, out var renamedTo) ? renamedTo : e.ObjectName.ToString();
            foreach (var name in new[] { full, TrailingNumber.Replace(full, "") })
                a.Raw.AddNameReference(new FString(name));
        }
        foreach (var (e, name) in renames) e.ObjectName = RetainHost.MakeName(a, name);

        var aNames = a.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();
        var native = new List<NativeEntry>();
        var newExports = new List<Export>();
        var newDepends = new List<int[]>();
        foreach (int i in survivors)
        {
            var e = a.Export(i);
            foreach (var list in new[] { e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies })
                list.RemoveAll(v => ret.Contains(v.Index) && !replacement.ContainsKey(v.Index));
            var deps = (a.Raw.DependsMap[i - 1] ?? Array.Empty<int>()).Where(v => !ret.Contains(v) || replacement.ContainsKey(v)).ToArray();
            if (e.GetType() == typeof(NormalExport) && e.Extras != null && e.Extras.Any(x => x != 0))
            {
                var raw = (byte[])e.Extras.Clone();
                bool moved = nativeSource.TryGetValue(i, out var src);
                var cls = moved ? src.Class : a.ObjectName(e.ClassIndex.Index);
                var parsed = NativePayload.Parse(cls, moved ? src.Raw : raw, moved ? src.Names : aNames);
                foreach (var r in parsed.Refs)
                    RetainHost.WriteInt(raw, r.Offset, Remap(moved ? Donor(r.Value) : r.Value));
                foreach (var f in parsed.Formats)
                    RetainHost.WriteInt(raw, f.OffsetField - 20, aNames.IndexOf(f.Name));
                e.Extras = raw;
                native.Add(new NativeEntry(move[i], cls, NativePayload.Parse(cls, raw, aNames)));
            }
            Refs.RewriteNormal(e, Remap, Refs.AllowedStreamingWorld);
            Refs.RewriteExtra(e, Remap);
            if (e is LevelExport lv) foreach (var p in lv.Actors) p.Index = Remap(p.Index);
            newExports.Add(e);
            newDepends.Add(deps.Select(v => Remap(v)).ToArray());
        }
        a.Raw.Exports.Clear(); a.Raw.Exports.AddRange(newExports);
        a.Raw.DependsMap.Clear(); a.Raw.DependsMap.AddRange(newDepends);
        a.InvalidateIndex();

        int finalAnchor = move[replacement.GetValueOrDefault(goalAnchor, goalAnchor)];
        Require(Props.Ref(a.Export(move[goalSpawner]), "EnemySpawnerPositionAfterGoal") == finalAnchor, "goal anchor changed");
        var finalLevel = (LevelExport)a.Export(move[LevelIndex]);
        Require(finalLevel.Actors.Any(p => p.Index == finalAnchor), "goal anchor not in level");
        Require(a.ObjectName(a.Export(finalAnchor).ClassIndex.Index) == "TargetPoint", "goal anchor class");
        UpdateGenerations(a);
        foreach (var e in a.Exports)
            foreach (int v in HostComposer.CollectRefs(e))
                Require(-a.Imports.Count <= v && v <= a.Exports.Count, "invalid package index");
        LevelChecks.Validate(a);
        ComposePolicy.Check(a);

        var roots = ((NormalExport)a.Export(move[3])).Data.OfType<ObjectPropertyData>()
            .Where(p => Regex.IsMatch(p.Name.ToString(), @"^ArireRoot_X\d+(?:_447)?_EdGraph_1_RefProperty$")).Select(p => p.Value.Index).ToList();
        Require(roots.Count == 11 && roots.Distinct().Count() == 11, "pool roots");
        foreach (int root in roots) Require(finalLevel.Actors.Any(p => p.Index == root), "pool root not in level");
        foreach (int donorId in Map.Values)
        {
            bool exists = a.Exports.Any(e =>
            {
                var n = e.ObjectName.ToString();
                return n.StartsWith($"SCMix_Stage6_X{donorId}_", StringComparison.Ordinal) && n.Contains("ArireRoot");
            });
            Require(exists == (keep.Contains(donorId) || retain.Contains(donorId)), "donor presence mismatch");   // SCU-PATCH composer#8
        }

        var plan = new ComposePlan
        {
            Sanctuary = keep.OrderBy(x => x).ToList(),
            SanctuarySlots = effective.Where(x => keep.Contains(x.Donor)).ToDictionary(x => x.Donor.ToString(), x => x.Slot),
            HostSlots = restores.Select(r => r.Slot).ToList(),
            RemovedExports = ret.Count,
            AddedExports = allHc.Count,
            ClearedCallbackFields = cleared,
            PoolRoots = roots,
            Level = move[LevelIndex],
            RetainedPostGoalAnchor = retainedGoalAnchor,
        };
        return (a, native, plan);
    }

    internal static void UpdateGenerations(Asset a)
    {
        var generations = a.Raw.Generations;
        if (generations.Count == 0) return;
        var last = generations[^1];
        last.ExportCount = a.Exports.Count;
        last.NameCount = a.Raw.GetNameMapIndexList().Count;
        generations[^1] = last;
    }
}

/// <summary>enforce_randomizer_policy.check: collector and retired host equipment policies.</summary>
public static class ComposePolicy
{
    static readonly HashSet<string> ForbiddenHostGear = new() { "BP_EquipItem3_137", "BP_EquipItem4_464", "BP_EquipItem5_740", "BP_EquipItem6_251" };

    public static void Check(Asset a)
    {
        var collectors = new List<int>();
        var residual = new List<int>();
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            var e = a.Exports[i - 1];
            int ci = e.ClassIndex.Index;
            var cls = ci != 0 ? a.ObjectName(ci) : "";
            if (cls == "BP_Kokeshi_C") collectors.Add(i);
            if (cls == "BP_EquipItem_C" && ForbiddenHostGear.Contains(e.ObjectName.ToString())) residual.Add(i);
        }
        if (collectors.Count > 0) throw new InvalidOperationException("Kokeshi actors remain: " + string.Join(",", collectors.Take(5)));
        if (residual.Count > 0) throw new InvalidOperationException("retired host equipment remains: " + string.Join(",", residual.Take(5)));
    }
}
