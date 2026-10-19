using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record BundleRow(int Number, int Slot, int Actors, int Exports);

/// <summary>
/// Port of soul_bundles._inject: transplant whole donor districts (Soul "Stage5" or Abyss "Extra") into the composed
/// package as serialized bundles.
/// SCU-PATCH app/soul_bundles.py #1-#3 (RETAIN) embedded here: when <c>retain</c> is set the host room is neither retired
/// nor replaced; the donor room is added next to it. See randomizer/PATCHES.md section 2.
/// </summary>
public static class BundleInjector
{
    public static readonly IReadOnlyDictionary<int, int> SoulSlots = new Dictionary<int, int>
    {
        [1] = 11, [2] = 8, [7] = 2, [9] = 6, [4] = 4, [5] = 7, [8] = 9, [3] = 3, [10] = 1, [6] = 10,
    };

    static readonly Regex TrailingNumber = new(@"_\d+$", RegexOptions.Compiled);
    static readonly int[] SharedMarkerSources = { 14, 15, 27652 };

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>Add the donor-name spelling (and its number-less base) to a donor package's name map, like the Python does.</summary>
    public static void RegisterName(Asset d, string name)
    {
        d.Raw.AddNameReference(new FString(name));
        d.Raw.AddNameReference(new FString(TrailingNumber.Replace(name, "")));
    }

    /// <summary>native_x6.donor_closure: the closure plus the material/texture assets the landscape payloads reference.</summary>
    public static HashSet<int> DonorClosure(Asset d, IEnumerable<int> ids)
    {
        var result = d.Closure(ids);
        while (true)
        {
            var extra = new HashSet<int>();
            foreach (int i in result)
                foreach (int v in Refs.CollectNormal(d.Export(i)))
                    if (v > 0 && !result.Contains(v) && v != 8312)
                    {
                        var cls = d.ObjectName(d.Export(v).ClassIndex.Index);
                        Require(cls is "Texture2D" or "LandscapeMaterialInstanceConstant" or "MaterialInstanceConstant", $"unresolved external {i} {v} {cls}");
                        extra.Add(v);
                    }
            if (extra.Count == 0) return result;
            result.UnionWith(d.Closure(extra));
        }
    }

    /// <param name="landscape">soul_special.inject_native: extended closure and Texture2D name fix-ups (Soul X6 only).</param>
    public static List<BundleRow> Inject(Asset a, ISet<int> selected, Asset d, IReadOnlyDictionary<int, int> slots, string stageTag, bool retain, bool landscape = false)
    {
        Require(stageTag is "Stage5" or "Extra", stageTag);
        Require(selected.IsSubsetOf(SoulSlots.Keys), "unsupported district");
        if (selected.Count == 0) return new();

        int levelId = a.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        int dl = d.Exports.Select((e, i) => (e, i: i + 1)).First(x => x.e is LevelExport).i;
        var aLevel = (LevelExport)a.Export(levelId);
        var dLevel = (LevelExport)d.Export(dl);

        var ret = new HashSet<int>();
        var bundles = new List<(int N, int Slot, int Root, int Dr, HashSet<int> Da, HashSet<int> Dc)>();
        foreach (int n in selected.OrderBy(x => x))
        {
            int slot = slots.TryGetValue(n, out int s) ? s : SoulSlots[n];
            string prefix = $"SCApp_Host_X{slot}_";
            var roots = Enumerable.Range(1, a.Exports.Count).Where(i =>
            {
                var name = a.Exports[i - 1].ObjectName.ToString();
                return name.StartsWith(prefix, StringComparison.Ordinal) && name.Contains("_ArireRoot_", StringComparison.Ordinal);
            }).ToList();
            Require(roots.Count == 1, $"slot must first be restored to host ({slot})");
            int root = roots[0];
            var actors = aLevel.Actors.Select(p => p.Index).Where(i => i > 0 && a.Export(i).ObjectName.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToHashSet();
            // Preserve global markers only; guards and archives belong to their rooms.
            var shared = actors.Where(i => SharedMarkerSources.Any(old => a.Export(i).ObjectName.ToString().StartsWith(prefix + old + "_", StringComparison.Ordinal))).ToHashSet();
            if (!retain) ret.UnionWith(a.Closure(actors.Except(shared)));   // SCU-PATCH soul_bundles#2
            var da = dLevel.Actors.Select(p => p.Index).Where(i => i > 0 && d.Export(i).ObjectName.ToString().StartsWith($"SCMix_{stageTag}_X{n}_", StringComparison.Ordinal)).ToHashSet();
            var dc = landscape ? DonorClosure(d, da) : d.Closure(da);
            int dr = dLevel.Actors.Select(p => p.Index).First(i => da.Contains(i) && d.Export(i).ObjectName.ToString().Contains("_ArireRoot_", StringComparison.Ordinal));
            bundles.Add((n, slot, root, dr, da, dc));
        }

        var importLookup = new Dictionary<(string, string, string), int>();
        for (int i = -1; i >= -a.Imports.Count; i--)
            importLookup[(a.Path(i), a.Imports[-i - 1].ClassPackage.ToString(), a.Imports[-i - 1].ClassName.ToString())] = i;
        var imports = new Dictionary<int, int>();

        // Every donor name joins the host name map (donor order) before exports are copied; copied names are re-bound to the host.
        foreach (var n in d.Raw.GetNameMapIndexList()) a.Raw.AddNameReference(n);

        int Imp(int i)
        {
            if (imports.TryGetValue(i, out int cached)) return cached;
            var e = d.Imports[-i - 1];
            int parent = e.OuterIndex.Index != 0 ? Imp(e.OuterIndex.Index) : 0;
            var key = (d.Path(i), e.ClassPackage.ToString(), e.ClassName.ToString());
            if (!importLookup.ContainsKey(key))
            {
                var created = DeepClone.Of(e, a.Raw);
                created.OuterIndex = new FPackageIndex(parent);
                a.Imports.Add(created);
                importLookup[key] = -a.Imports.Count;
            }
            return imports[i] = importLookup[key];
        }

        var dcAll = bundles.SelectMany(b => b.Dc).ToHashSet();
        var mapping = new Dictionary<int, int>();
        int next = a.Exports.Count;
        foreach (int old in dcAll.OrderBy(x => x)) mapping[old] = ++next;
        mapping[dl] = levelId;
        int Donor(int v) => v < 0 ? Imp(v) : v != 0 ? mapping[v] : 0;

        var replacement = new Dictionary<int, int>();
        var nativeSources = new Dictionary<int, (string Class, byte[] Raw, IReadOnlyList<string> Names)>();
        var newActors = new List<int>();
        var dNames = d.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();
        foreach (var (n, slot, root, dr, da, dc) in bundles)
        {
            if (!retain)   // SCU-PATCH soul_bundles#3
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
            foreach (int v in Refs.CollectNormal(e)) Donor(v);   // creates the needed imports in the original order
            if (e.Extras != null && e.Extras.Any(x => x != 0))
                nativeSources[mapping[old]] = (d.ObjectName(e.ClassIndex.Index), (byte[])e.Extras.Clone(), dNames);
            Refs.RewriteNormal(e, Donor);
            Require(mapping[old] == a.Exports.Count + 1, "export numbering drifted");
            e.Asset = a.Raw;
            a.Exports.Add(e);
            a.Raw.DependsMap.Add((d.Raw.DependsMap[old - 1] ?? Array.Empty<int>()).Select(Donor).ToArray());
        }
        a.InvalidateIndex();

        // Align tile root transforms and tags to the host slot; internal transforms are preserved.
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

        var aNames = a.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();
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
                bool moved = nativeSources.TryGetValue(i, out var src);
                var cls = moved ? src.Class : a.ObjectName(e.ClassIndex.Index);
                var parsed = NativePayload.Parse(cls, moved ? src.Raw : raw, moved ? src.Names : aNames);
                foreach (var r in parsed.Refs) RetainHost.WriteInt(raw, r.Offset, Remap(moved ? Donor(r.Value) : r.Value));
                foreach (var f in parsed.Formats) RetainHost.WriteInt(raw, f.OffsetField - 20, aNames.IndexOf(f.Name));
                if (landscape) foreach (var nm in parsed.Names) RetainHost.WriteInt(raw, nm.Offset, aNames.IndexOf(nm.Name));
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
        return bundles.Select(b => new BundleRow(b.N, b.Slot, b.Da.Count, b.Dc.Count)).ToList();
    }
}
