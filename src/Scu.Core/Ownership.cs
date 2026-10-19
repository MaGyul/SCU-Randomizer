using System.Text.RegularExpressions;
using UAssetAPI.ExportTypes;

namespace Scu.Core;

/// <summary>
/// Which actors belong to which tile root (analyze_exports.Asset.analyze "tile" column) and the closure of
/// everything those actors own (preflight_offline_swap.owned). Computed from the package instead of the
/// pre-generated reports/*/actors.json.
/// </summary>
public static class Ownership
{
    static readonly Regex RootActor = new(@"^ArireRoot_[LTX]", RegexOptions.Compiled);

    /// <summary>Actor export index -> tile root export index, for every positioned actor in the level.</summary>
    public static Dictionary<int, int> TileOfActors(Asset a)
    {
        var levels = a.Exports.Select((e, i) => (e, i: i + 1)).Where(x => x.e is LevelExport).Select(x => x.i).ToHashSet();
        var actors = new Dictionary<int, Export>();
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            var e = a.Exports[i - 1];
            if (levels.Contains(e.OuterIndex.Index) && e is NormalExport && Props.Ref(e, "RootComponent") != 0) actors[i] = e;
        }
        var attachParent = new Dictionary<int, int>();
        for (int i = 1; i <= a.Exports.Count; i++)
            if (actors.ContainsKey(a.Exports[i - 1].OuterIndex.Index)) attachParent[i] = Props.Ref(a.Exports[i - 1], "AttachParent");
        var rootComponents = new Dictionary<int, int>();
        foreach (var (i, e) in actors)
            if (RootActor.IsMatch(e.ObjectName.ToString())) rootComponents[Props.Ref(e, "RootComponent")] = i;

        var result = new Dictionary<int, int>();
        foreach (var (i, e) in actors)
        {
            int c = Props.Ref(e, "RootComponent");
            var seen = new HashSet<int>();
            while (c != 0 && seen.Add(c))
            {
                if (rootComponents.TryGetValue(c, out int root)) { result[i] = root; break; }
                c = attachParent.GetValueOrDefault(c);
            }
        }
        return result;
    }

    /// <summary>preflight_offline_swap.owned: (actors of a tile, their full export closure).</summary>
    public static (HashSet<int> Actors, HashSet<int> Closure) Owned(Asset a, int root, Dictionary<int, int>? tiles = null)
    {
        tiles ??= TileOfActors(a);
        var actors = tiles.Where(kv => kv.Value == root).Select(kv => kv.Key).ToHashSet();
        return (actors, a.Closure(actors));
    }
}
