using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace Scu.Core;

/// <summary>Port of the shared helpers in host_composer.py (level_ids, roots, groups, _refs, _rewrite, detached rooms).</summary>
public static class HostComposer
{
    public static readonly IReadOnlyDictionary<string, string> Assets = new Dictionary<string, string>
    {
        ["cicada"] = "Random5x5", ["abyss"] = "Extra5x5", ["soul"] = "Stage5x5", ["sanctuary"] = "Stage6x5",
    };

    static readonly Regex RootField = new(@"^ArireRoot_([LTX]\d+)(?:_447)?_EdGraph_\d+_RefProperty$", RegexOptions.Compiled);

    public readonly record struct LevelIdSet(int Level, int Script, int Class, int Cdo);

    public static LevelIdSet LevelIds(Asset a)
    {
        var levels = a.Exports.Select((e, i) => (e, i: i + 1)).Where(x => x.e is LevelExport).ToList();
        Require(levels.Count == 1, "exactly one persistent level is required");
        var (e, level) = levels[0];
        int script = ((LevelExport)e).LevelScriptActor.Index;
        int cls = a.Export(script).ClassIndex.Index;
        int cdo = ((ClassExport)a.Export(cls)).ClassDefaultObject.Index;
        Require(cls == 2 && cdo == 3, "unexpected host class/CDO layout");
        return new LevelIdSet(level, script, cls, cdo);
    }

    /// <summary>Generator root fields on the class default object: slot ("X3") -> root actor export.</summary>
    public static Dictionary<string, int> Roots(Asset a)
    {
        var cdo = (NormalExport)a.Export(LevelIds(a).Cdo);
        var result = new Dictionary<string, int>();
        foreach (var p in cdo.Data)
        {
            var m = RootField.Match(p.Name.ToString());
            if (!m.Success) continue;
            int value = ((ObjectPropertyData)p).Value.Index;
            Require(!result.ContainsKey(m.Groups[1].Value) && value > 0, "duplicate or invalid generator root");
            result[m.Groups[1].Value] = value;
        }
        return result;
    }

    /// <summary>Walk attach parents of <paramref name="actor"/>'s root component up to one of <paramref name="components"/>.</summary>
    public static T? AttachedTo<T>(Asset a, int actor, IReadOnlyDictionary<int, T> components)
    {
        int c = Props.Ref(a.Export(actor), "RootComponent");
        var seen = new HashSet<int>();
        while (c != 0)
        {
            Require(seen.Add(c), "attachment cycle");
            if (components.TryGetValue(c, out var owner)) return owner;
            c = Props.Ref(a.Export(c), "AttachParent");
        }
        return default;
    }

    public static (Dictionary<string, int> Roots, Dictionary<string, HashSet<int>> Actors) Groups(Asset a)
    {
        int level = LevelIds(a).Level;
        var roots = Roots(a);
        var components = roots.ToDictionary(kv => Props.Ref(a.Export(kv.Value), "RootComponent"), kv => kv.Key);
        var actors = roots.Keys.ToDictionary(k => k, _ => new HashSet<int>());
        foreach (var p in ((LevelExport)a.Export(level)).Actors)
        {
            if (p.Index <= 0) continue;
            var slot = AttachedTo(a, p.Index, components);
            if (slot != null) actors[slot].Add(p.Index);
        }
        return (roots, actors);
    }

    /// <summary>host_composer._rewrite: header indices, dependencies and object references in property data.</summary>
    public static void Rewrite(Export e, Func<int, int> fn, ISet<string>? softPaths = null)
        => Refs.RewriteNormal(e, fn, soft =>
        {
            if (soft.Name.ToString() != "WorldAsset" || !string.IsNullOrEmpty(soft.Value.SubPathString?.ToString()))
                throw new InvalidOperationException("unexpected soft object reference " + soft.Name);
            if (softPaths != null && !softPaths.Contains(soft.Value.AssetPath.AssetName.ToString()))
                throw new InvalidOperationException("foreign streaming level");
        });

    /// <summary>host_composer._refs: every typed package index the serializer understands, plus level actors.</summary>
    public static List<int> CollectRefs(Export e)
    {
        var values = new List<int>();
        int Collect(int v) { values.Add(v); return v; }
        Rewrite(e, Collect);
        Refs.RewriteExtra(e, Collect);
        if (e is LevelExport level) values.AddRange(level.Actors.Select(p => p.Index));
        return values;
    }

    // ---- detached remote rooms (hash-pinned surveys shipped with the randomizer) ----

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static Dictionary<string, HashSet<int>> SoulDetachedRooms(Asset a, IEnumerable<string> slots, string randomizerRoot)
    {
        var surveys = new Dictionary<string, (string Path, string Sha, string? Field, string Key, int Count, int Anchor, string Name)>
        {
            ["X3"] = ("app/updates/soul-X3-analysis/destination-spatial-survey.json",
                      "64b0c315d0d33e4fecf547877eac99492f7325e8ecc8c967b6be49d13d0cb99f", "actors", "index", 142, 43871, "WrapPoint_X3"),
            ["T3"] = ("app/soul-lt-room.json",
                      "f5775d341fb430f43a41d45ed411b5da3e16833338b9e13e71422c82269d78a9", null, "id", 211, 43872, "WrapPoint_X4"),
        };
        var (_, groups) = Groups(a);
        var attached = groups.Values.SelectMany(s => s).ToHashSet();
        var level = (LevelExport)a.Export(LevelIds(a).Level);
        var levelActors = level.Actors.Select(p => p.Index).ToHashSet();
        var result = new Dictionary<string, HashSet<int>>();
        foreach (var slot in slots.Where(surveys.ContainsKey).Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            var (path, expected, field, key, count, anchor, name) = surveys[slot];
            if (a.Exports.Count < anchor) continue;   // small fixtures have no remote room
            Require(a.ObjectName(anchor) == name, "Soul remote room identity changed");
            var file = System.IO.Path.Combine(randomizerRoot, path);
            Require(Sha(file) == expected, "Soul remote room survey changed");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var rows = field != null ? doc.RootElement.GetProperty(field) : doc.RootElement;
            var ids = rows.EnumerateArray().Select(r => r.GetProperty(key).GetInt32()).ToHashSet();
            Require(ids.Count == count && ids.Contains(anchor) && ids.IsSubsetOf(levelActors), "Soul remote room membership changed");
            Require(!ids.Overlaps(attached), "Soul remote room gained an unexpected attachment");
            Require(ids.All(i => Props.Ref(a.Export(Props.Ref(a.Export(i), "RootComponent")), "AttachParent") == 0), "Soul remote room transform changed");
            result[slot] = ids;
        }
        return result;
    }

    public static Dictionary<string, HashSet<int>> SanctuaryDetachedRooms(Asset a, IEnumerable<string> slots, string randomizerRoot)
    {
        if (!slots.Contains("T3") || a.Exports.Count < 37049) return new();
        Require(a.ObjectName(37049) == "TargetPoint_2", "Sanctuary remote room identity changed");
        var file = System.IO.Path.Combine(randomizerRoot, "app/updates/sanctuary-LT/all/room-plan.json");
        Require(Sha(file) == "2beffdcbfc64af44ba8c5e0c0697c7eee05e7b6d72152f6e691c6f002e4a6a3b", "Sanctuary remote room survey changed");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var survey = doc.RootElement;
        var ids = survey.GetProperty("source_rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToHashSet();
        var actors = survey.GetProperty("actors").EnumerateArray().Select(x => x.GetInt32());
        var excluded = survey.GetProperty("excluded").EnumerateArray().Select(x => x.GetInt32()).ToList();
        Require(ids.Count == 438 && ids.SetEquals(actors.Concat(excluded)), "Sanctuary remote room membership changed");
        Require(ids.Contains(37049) && excluded.SequenceEqual(new[] { 5839 }), "Sanctuary remote room exclusion changed");
        var level = (LevelExport)a.Export(LevelIds(a).Level);
        var (_, groups) = Groups(a);
        Require(ids.IsSubsetOf(level.Actors.Select(p => p.Index).ToHashSet()) && !ids.Overlaps(groups.Values.SelectMany(s => s)),
            "Sanctuary remote room attachment changed");
        Require(ids.All(i => Props.Ref(a.Export(Props.Ref(a.Export(i), "RootComponent")), "AttachParent") == 0), "Sanctuary remote room transform changed");
        return new() { ["T3"] = ids };
    }
}
