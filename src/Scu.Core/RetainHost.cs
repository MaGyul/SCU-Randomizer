using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record NativeEntry(int Index, string Class, NativeInfo Info);

public sealed class RetainProof
{
    public string BaseStage = "";
    public List<string> SkippedStages = new();
    public int RenamedHostActors;
    public SortedDictionary<string, int> ImportedRooms = new(StringComparer.Ordinal);
    public int ImportedActors;
    public int AddedExports;
    public List<string> PrivateFunctions = new();
}

/// <summary>
/// SCU-PATCH app/patcher/scu_retain_host.py (embedded, see randomizer/PATCHES.md section 0).
/// Port of scu_retain_host.transplant_retain: keep the host stage's own rooms and add the merged
/// staging package's rooms to the pool. The merged asset <c>s</c> is only read; the host <c>h</c> is modified.
/// Changed relative to the original Python: names are re-bound to the host package (DeepClone), the name map is
/// extended before copying, and renames are applied afterwards (PATCHES.md section 5).
/// </summary>
public static class RetainHost
{
    static readonly (string Prefix, string Stage)[] StagePrefix =
    {
        ("SCApp_Host_", "cicada"), ("SCMix_Stage6_", "sanctuary"), ("SCMix_Stage5_", "soul"), ("SCMix_Extra_", "abyss"),
    };
    static readonly Regex RootName = new(@"ArireRoot_[XTL]\d+(?:_\d+)?$", RegexOptions.Compiled);
    static readonly Regex RoomPrefix = new(@"^((?:SCMix_[^_]+_(?:LT_)?|SCApp_Host_)[LTX]\d+_)\d+_", RegexOptions.Compiled);
    static readonly Regex TrailingNumber = new(@"_\d+$", RegexOptions.Compiled);
    static readonly string[] FunctionNameMembers = { "Delegate", "VirtualFunctionName", "FunctionName", "Value" };

    public static string? StageOfName(string name)
    {
        foreach (var (prefix, stage) in StagePrefix)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return stage;
        return null;
    }

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    static bool IsPlainNormal(Export e) => e.GetType() == typeof(NormalExport);
    static bool AnyNonZero(byte[]? raw) => raw != null && raw.Any(x => x != 0);

    /// <summary>Kept for the callers in this file; see <see cref="Names.Make"/>.</summary>
    internal static FName MakeName(Asset a, string s) => Names.Make(a, s);


    /// <summary>"object name" -> int for a property whose JSON "Value" is an integer (object refs and integral values).</summary>
    static int? IntegerValue(PropertyData p) => p switch
    {
        ObjectPropertyData o => o.Value.Index,
        _ => p.RawValue switch
        {
            int i => i, uint u => (int)u, long l => (int)l, short s => s, ushort us => us, byte b => b, sbyte sb => sb,
            _ => null,
        },
    };

    static IEnumerable<string> NamedStrings(Export e)
    {
        var found = new List<string>();
        ObjectWalker.Walk(e, node =>
        {
            var t = node.GetType();
            foreach (var m in FunctionNameMembers)
            {
                object? v = t.GetProperty(m)?.GetValue(node) ?? t.GetField(m)?.GetValue(node);
                switch (v)
                {
                    case FName n: found.Add(n.ToString()); break;
                    case FString s: found.Add(s.ToString()); break;
                    case string s: found.Add(s); break;
                }
            }
            return true;
        });
        return found;
    }

    public static (Asset Host, List<NativeEntry> Native, RetainProof Proof) Transplant(
        Asset s, string baseStage, Asset h, string randomizerRoot, IEnumerable<string>? skipStages = null)
    {
        Require(baseStage is "cicada" or "abyss" or "soul" or "sanctuary", baseStage);
        var hIds = HostComposer.LevelIds(h);
        var sIds = HostComposer.LevelIds(s);
        (int hl, int hs, int hc, int hd) = (hIds.Level, hIds.Script, hIds.Class, hIds.Cdo);
        (int sl, int ss, int sc, int sd) = (sIds.Level, sIds.Script, sIds.Class, sIds.Cdo);
        Require(h.ObjectName(hc) == HostComposer.Assets[baseStage] + "_C", "unexpected host class");
        var (hr, hg) = HostComposer.Groups(h);
        var skip = new HashSet<string>(skipStages ?? Array.Empty<string>()) { baseStage };

        // ---- prefix the host rooms' actor names (the runtime finds a room's actors by prefix) ----
        var remote = baseStage switch
        {
            "soul" => HostComposer.SoulDetachedRooms(h, hr.Keys.ToList(), randomizerRoot),
            "sanctuary" => HostComposer.SanctuaryDetachedRooms(h, hr.Keys.ToList(), randomizerRoot),
            _ => new Dictionary<string, HashSet<int>>(),
        };
        int renamed = 0;
        // SCU-PATCH(c#-only): renames are applied after the name map has been extended in the original order
        var renames = new Dictionary<Export, string>(ReferenceEqualityComparer.Instance);
        var afterNames = new List<Action>();   // anything that would add names to the map as a side effect
        foreach (var slot in hr.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var actors = new HashSet<int>(hg[slot]);
            if (remote.TryGetValue(slot, out var extra)) actors.UnionWith(extra);
            foreach (int ai in actors.OrderBy(x => x))
            {
                var e = h.Export(ai);
                var name = e.ObjectName.ToString();
                if (name.StartsWith("SCApp_Host_", StringComparison.Ordinal) || name.StartsWith("SCMix_", StringComparison.Ordinal)) continue;
                renames[e] = $"SCApp_Host_{slot}_{ai}_{name}";
                renamed++;
            }
        }

        // ---- rooms to import from the merged package ----
        var levelActors = ((LevelExport)s.Export(sl)).Actors.Select(p => p.Index).Where(i => i > 0).ToList();
        var chosen = new List<(int Root, string Name)>();
        foreach (int i in levelActors)
        {
            var e = s.Export(i);
            var name = e.ObjectName.ToString();
            if (!RootName.IsMatch(name) || !IsPlainNormal(e)) continue;
            var stage = StageOfName(name);
            if (stage != null && !skip.Contains(stage)) chosen.Add((i, name));
        }
        var components = chosen.ToDictionary(c => Props.Ref(s.Export(c.Root), "RootComponent"), c => c.Root);
        var attached = chosen.ToDictionary(c => c.Root, _ => new HashSet<int>());
        foreach (int a in levelActors)
        {
            int owner = HostComposer.AttachedTo(s, a, components);
            if (owner != 0) attached[owner].Add(a);
        }
        var prefixMembers = new Dictionary<string, HashSet<int>>();
        foreach (int a in levelActors)
        {
            var m = RoomPrefix.Match(s.Export(a).ObjectName.ToString());
            if (!m.Success) continue;
            if (!prefixMembers.TryGetValue(m.Groups[1].Value, out var set)) prefixMembers[m.Groups[1].Value] = set = new HashSet<int>();
            set.Add(a);
        }
        var incomingActors = new HashSet<int>();
        var roomOf = new Dictionary<int, string>();
        foreach (var (root, name) in chosen)
        {
            var actors = new HashSet<int>(attached[root]);
            var m = RoomPrefix.Match(name);
            if (m.Success && prefixMembers.TryGetValue(m.Groups[1].Value, out var members)) actors.UnionWith(members);
            foreach (int a in actors)
            {
                Require(roomOf.TryAdd(a, name), "actor shared by two rooms " + s.Export(a).ObjectName);
            }
            incomingActors.UnionWith(actors);
        }
        Require(incomingActors.Count > 0, "no rooms to import");
        var incoming = s.Closure(incomingActors);

        var softPaths = new HashSet<string>();
        foreach (var e in h.Exports)
            if (e is NormalExport ne)
                ObjectWalker.Walk(ne.Data, node =>
                {
                    if (node is SoftObjectPropertyData soft) { softPaths.Add(soft.Value.AssetPath.AssetName.ToString()); return false; }
                    return true;
                });

        // ---- imports ----
        var importLookup = new Dictionary<(string, string, string), int>();
        for (int i = -1; i >= -h.Imports.Count; i--)
            importLookup[(h.Path(i), h.Imports[-i - 1].ClassPackage.ToString(), h.Imports[-i - 1].ClassName.ToString())] = i;
        var imports = new Dictionary<int, int>();
        int Imp(int i)
        {
            if (imports.TryGetValue(i, out int cached)) return cached;
            var e = s.Imports[-i - 1];
            int parent = e.OuterIndex.Index != 0 ? Imp(e.OuterIndex.Index) : 0;
            var key = (s.Path(i), e.ClassPackage.ToString(), e.ClassName.ToString());
            if (!importLookup.ContainsKey(key))
            {
                var created = DeepClone.Of(e, h.Raw);
                created.OuterIndex = new FPackageIndex(parent);
                h.Imports.Add(created);
                importLookup[key] = -h.Imports.Count;
            }
            return imports[i] = importLookup[key];
        }

        // ---- transitive support closure ----
        var anchors = new Dictionary<int, int> { [sl] = hl, [ss] = hs, [sc] = hc, [sd] = hd };
        var sClass = (ClassExport)s.Export(sc);
        var sourceFunctions = sClass.FuncMap.Keys.ToDictionary(k => k.ToString(), k => sClass.FuncMap[k].Index);
        var sourceCdo = LastByName(((NormalExport)s.Export(sd)).Data);
        var sourceInstance = LastByName(((NormalExport)s.Export(ss)).Data);
        var srcActorIds = ((LevelExport)s.Export(sl)).Actors.Select(p => p.Index).ToHashSet();
        int originalCount = h.Exports.Count;
        var pending = new List<int>(incoming);
        var cdoFields = new HashSet<int>();
        var instanceFields = new HashSet<int>();
        while (pending.Count > 0)
        {
            int i = pending[^1]; pending.RemoveAt(pending.Count - 1);
            var e = s.Export(i);
            var refs = HostComposer.CollectRefs(e);
            refs.AddRange(s.Raw.DependsMap[i - 1] ?? Array.Empty<int>());
            if (IsPlainNormal(e) && AnyNonZero(e.Extras))
                refs.AddRange(NativePayload.Parse(s.ObjectName(e.ClassIndex.Index), e.Extras, NameList(s)).Refs.Select(r => r.Value));
            foreach (var name in NamedStrings(e))
                if (name.StartsWith("SCMix_", StringComparison.Ordinal) && sourceFunctions.TryGetValue(name, out int fn)) refs.Add(fn);
            for (int k = 0; k < refs.Count; k++)
            {
                int v = refs[k];
                if (v <= 0 || anchors.ContainsKey(v) || incoming.Contains(v)) continue;
                var target = s.Export(v);
                var targetName = target.ObjectName.ToString();
                if (srcActorIds.Contains(v))
                    Require(targetName.StartsWith("SCMix_", StringComparison.Ordinal) || targetName.StartsWith("SCApp_Host_", StringComparison.Ordinal),
                        "foreign staging global actor dependency " + targetName);
                if (target.OuterIndex.Index == sc)
                {
                    Require(targetName.StartsWith("SCMix_", StringComparison.Ordinal), "foreign staging generator field/function dependency " + targetName);
                    if (sourceCdo.TryGetValue(targetName, out var cdoProp))
                    {
                        cdoFields.Add(v);
                        if (IntegerValue(cdoProp) is int val && val > 0) refs.Add(val);
                    }
                    if (sourceInstance.TryGetValue(targetName, out var instProp))
                    {
                        instanceFields.Add(v);
                        ObjectWalker.Walk(instProp, node =>
                        {
                            if (node.GetType() == typeof(ObjectPropertyData)) refs.Add(((ObjectPropertyData)node).Value.Index);
                            return true;
                        });
                    }
                }
                incoming.Add(v);
                pending.Add(v);
                foreach (int child in s.Closure(new[] { v }))
                    if (child != v && incoming.Add(child)) pending.Add(child);
            }
        }

        var sorted = incoming.OrderBy(x => x).ToList();
        var mapping = new Dictionary<int, int>();
        for (int j = 0; j < sorted.Count; j++) mapping[sorted[j]] = originalCount + j + 1;
        foreach (var kv in anchors) mapping[kv.Key] = kv.Value;

        int Donor(int v)
        {
            if (v < 0) return Imp(v);
            if (v == 0) return 0;
            Require(mapping.ContainsKey(v), $"unmapped staging reference {v} {s.Export(v).ObjectName}");
            return mapping[v];
        }

        // SCU-PATCH(c#-only): every staging name joins the host name map (in staging order) before any export is copied, because
        // copied names are re-bound to the host package and need their final index.
        foreach (var n in s.Raw.GetNameMapIndexList()) h.Raw.AddNameReference(n);

        // ---- move the exports ----
        var nativeSources = new Dictionary<int, (string Class, byte[] Raw, IReadOnlyList<string> Names)>();
        var sNames = NameList(s);
        foreach (int old in sorted)
        {
            var e = DeepClone.Of(s.Export(old), h.Raw);
            if (srcActorIds.Contains(old) && e.ObjectName.ToString().StartsWith("SCApp_Host_", StringComparison.Ordinal))
                renames[e] = "SCMix_Cicada_" + e.ObjectName.ToString()["SCApp_Host_".Length..];
            if (IsPlainNormal(e) && AnyNonZero(e.Extras))
                nativeSources[mapping[old]] = (s.ObjectName(e.ClassIndex.Index), (byte[])e.Extras.Clone(), sNames);
            HostComposer.Rewrite(e, Donor, softPaths);
            Refs.RewriteExtra(e, Donor);
            Require(mapping[old] == h.Exports.Count + 1, "export numbering drifted");
            e.Asset = h.Raw;
            h.Exports.Add(e);
            h.Raw.DependsMap.Add((s.Raw.DependsMap[old - 1] ?? Array.Empty<int>()).Select(Donor).ToArray());
        }
        h.InvalidateIndex();

        // ---- generator class: private callback fields and functions ----
        var importedClassFields = sorted.Where(i => s.Export(i).OuterIndex.Index == sc).ToList();
        var hClass = (ClassExport)h.Export(hc);
        var oldNames = hClass.Children.Select(c => h.ObjectName(c.Index)).ToHashSet();
        Require(!oldNames.Overlaps(importedClassFields.Select(i => s.ObjectName(i))), "private callback name collision");
        hClass.Children = importedClassFields.Select(i => new FPackageIndex(mapping[i])).Concat(hClass.Children).ToArray();
        hClass.SerializationBeforeSerializationDependencies.AddRange(importedClassFields.Select(i => new FPackageIndex(mapping[i])));
        var importedFunctions = importedClassFields.Where(i => s.Export(i).GetType() == typeof(FunctionExport)).ToList();
        foreach (int i in importedFunctions)
        {
            int function = i;
            afterNames.Add(() => hClass.FuncMap.Add(MakeName(h, s.ObjectName(function)), new FPackageIndex(mapping[function])));
        }

        void RemapProperty(PropertyData p) => ObjectWalker.Walk(p, node =>
        {
            if (node is ObjectPropertyData o && node.GetType() != typeof(InterfacePropertyData)) o.Value.Index = Donor(o.Value.Index);
            return true;
        });

        foreach (int i in cdoFields.OrderBy(x => x))
        {
            var p = DeepClone.Of(sourceCdo[s.ObjectName(i)], h.Raw);
            RemapProperty(p);
            ((NormalExport)h.Export(hd)).Data.Add(p);
        }
        var hInstance = (NormalExport)h.Export(hs);
        foreach (int i in instanceFields.OrderBy(x => x))
        {
            var p = DeepClone.Of(sourceInstance[s.ObjectName(i)], h.Raw);
            RemapProperty(p);
            Require(!hInstance.Data.Any(q => q.Name.ToString() == p.Name.ToString()), "duplicate instance field " + p.Name);
            hInstance.Data.Add(p);
        }
        if (sourceInstance.TryGetValue("BlueprintCreatedComponents", out var createdProp) && createdProp is ArrayPropertyData created)
        {
            var copied = created.Value.Where(p => p is ObjectPropertyData o && incoming.Contains(o.Value.Index)).Select(p => DeepClone.Of(p, h.Raw)).ToList();
            foreach (var p in copied) RemapProperty(p);
            if (copied.Count > 0)
            {
                var target = hInstance.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "BlueprintCreatedComponents");
                if (target == null)
                {
                    target = DeepClone.Of(created, h.Raw);
                    target.Value = Array.Empty<PropertyData>();
                    hInstance.Data.Add(target);
                }
                target.Value = target.Value.Concat(copied).ToArray();
                var elements = target.Value;
                // SCU-PATCH(c#-only): array element names are never serialized, so they must not enter the name map (a dummy FName does not add them).
                afterNames.Add(() => { for (int n = 0; n < elements.Length; n++) elements[n].Name = FName.DefineDummy(h.Raw, n.ToString(), 0); });
                hInstance.CreateBeforeSerializationDependencies.AddRange(copied.Select(p => new FPackageIndex(((ObjectPropertyData)p).Value.Index)));
            }
        }

        var lv = (LevelExport)h.Export(hl);
        var addedActors = ((LevelExport)s.Export(sl)).Actors.Where(p => incoming.Contains(p.Index)).Select(p => mapping[p.Index]).ToList();
        lv.Actors.AddRange(addedActors.Select(x => new FPackageIndex(x)));
        lv.CreateBeforeSerializationDependencies.AddRange(addedActors.Select(x => new FPackageIndex(x)));

        // ---- names ----
        foreach (var e in h.Exports)
        {
            var full = renames.TryGetValue(e, out var renamedTo) ? renamedTo : e.ObjectName.ToString();
            h.Raw.AddNameReference(new FString(full));
            h.Raw.AddNameReference(new FString(TrailingNumber.Replace(full, "")));
        }
        foreach (var (e, name) in renames) e.ObjectName = MakeName(h, name);
        foreach (var action in afterNames) action();

        // ---- opaque trailing payloads: remap embedded package indices and name indices ----
        var hNames = NameList(h);
        for (int i = 1; i <= h.Exports.Count; i++)
        {
            var e = h.Exports[i - 1];
            if (!IsPlainNormal(e) || !AnyNonZero(e.Extras)) continue;
            var raw = (byte[])e.Extras.Clone();
            bool moved = nativeSources.TryGetValue(i, out var src);
            var parsed = NativePayload.Parse(moved ? src.Class : h.ObjectName(e.ClassIndex.Index), moved ? src.Raw : raw, moved ? src.Names : hNames);
            foreach (var r in parsed.Refs) WriteInt(raw, r.Offset, moved ? Donor(r.Value) : r.Value);
            foreach (var f in parsed.Formats) WriteInt(raw, f.OffsetField - 20, NameIndex(h, f.Name));
            foreach (var n in parsed.Names) WriteInt(raw, n.Offset, NameIndex(h, n.Name));
            e.Extras = raw;
        }

        var generations = h.Raw.Generations;
        if (generations.Count > 0)
        {
            var last = generations[^1];
            last.ExportCount = h.Exports.Count;
            last.NameCount = h.Raw.GetNameMapIndexList().Count;
            generations[^1] = last;
        }

        var identities = h.Exports.Select(e => (e.OuterIndex.Index, e.ObjectName.ToString())).ToList();
        Require(identities.Distinct().Count() == identities.Count, "duplicate object identity in host package");
        foreach (var e in h.Exports)
            foreach (int v in HostComposer.CollectRefs(e))
                Require(-h.Imports.Count <= v && v <= h.Exports.Count, "invalid final package index");

        var proof = new RetainProof { BaseStage = baseStage, SkippedStages = skip.OrderBy(x => x, StringComparer.Ordinal).ToList(), RenamedHostActors = renamed };
        foreach (var (_, name) in chosen)
        {
            var st = StageOfName(name)!;
            proof.ImportedRooms[st] = proof.ImportedRooms.GetValueOrDefault(st) + 1;
        }
        proof.ImportedActors = incomingActors.Count;
        proof.AddedExports = incoming.Count;
        proof.PrivateFunctions = importedFunctions.Select(i => s.ObjectName(i)).ToList();
        return (h, Native(h), proof);
    }

    /// <summary>host_composer._native: strict parse of every opaque payload in the package.</summary>
    public static List<NativeEntry> Native(Asset a)
    {
        var names = NameList(a);
        var rows = new List<NativeEntry>();
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            var e = a.Exports[i - 1];
            if (!IsPlainNormal(e) || !AnyNonZero(e.Extras)) continue;
            var cls = a.ObjectName(e.ClassIndex.Index);
            rows.Add(new NativeEntry(i, cls, NativePayload.Parse(cls, e.Extras, names)));
        }
        return rows;
    }

    static Dictionary<string, PropertyData> LastByName(IEnumerable<PropertyData> data)
    {
        var map = new Dictionary<string, PropertyData>();
        foreach (var p in data) map[p.Name.ToString()] = p;
        return map;
    }

    static IReadOnlyList<string> NameList(Asset a) => a.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();

    static int NameIndex(Asset a, string name)
    {
        int idx = a.Raw.SearchNameReference(new FString(name));
        Require(idx >= 0, "name missing from name map: " + name);
        return idx;
    }

    internal static void WriteInt(byte[] raw, int offset, int value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset, 4), value);
}
