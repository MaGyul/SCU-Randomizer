using System.Text.RegularExpressions;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace Scu.Core;

/// <summary>
/// Experimental: maps built on the 2-player multi pak's maps (--base-maps). The multi pak syncs the layout by making the
/// tile roots replicate their movement and running TileX/T/L only on the server. The rooms the build adds have their own
/// roots, so they get the same four flags, in the order the multi pak writes them.
/// </summary>
public static class MultiReplication
{
    static readonly Regex RootName = new(@"ArireRoot_[XTL]\d+(?:_\d+)?$", RegexOptions.Compiled);
    static readonly string[] Flags = { "bStaticMeshReplicateMovement", "bReplicates", "bReplicateMovement", "bAlwaysRelevant" };

    /// <returns>(roots in the level, roots that got at least one flag)</returns>
    public static (int Roots, int Marked) MarkRoots(Asset a)
    {
        var level = (LevelExport)a.Export(HostComposer.LevelIds(a).Level);
        int roots = 0, marked = 0;
        foreach (var p in level.Actors)
        {
            if (p.Index <= 0) continue;
            if (a.Export(p.Index) is not NormalExport e || !RootName.IsMatch(e.ObjectName.ToString())) continue;
            if (!e.Data.Any(d => d.Name.ToString() == "RootComponent")) continue;
            roots++;
            bool changed = false;
            foreach (var flag in Flags)
            {
                var existing = e.Data.FirstOrDefault(d => d.Name.ToString() == flag);
                if (existing is BoolPropertyData { Value: true }) continue;
                if (existing != null) e.Data.Remove(existing);
                e.Data.Add(new BoolPropertyData(Names.Make(a, flag)) { Value = true });
                changed = true;
            }
            if (changed) marked++;
        }
        return (roots, marked);
    }
}
