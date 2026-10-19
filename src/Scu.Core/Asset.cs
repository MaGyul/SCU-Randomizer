using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>
/// 1-based export / negative-import view over a UAsset, matching the index convention
/// used throughout the Python composer (a.obj(i), a.path(i), a.e, a.im).
/// </summary>
public sealed class Asset
{
    public UAsset Raw { get; }
    Dictionary<int, List<int>>? _children;

    public Asset(UAsset raw) => Raw = raw;

    public static Asset Load(string umapPath, EngineVersion version = EngineVersion.VER_UE4_22)
        => new(new UAsset(umapPath, version));

    /// <summary>Load a package stored as UAssetAPI JSON (the randomizer's prepared donor files). Slow for big files; cache as umap.</summary>
    public static Asset LoadJson(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        return new Asset(UAsset.DeserializeJson(stream));
    }

    public static Asset LoadJson(Stream json) => new(UAsset.DeserializeJson(json));

    public List<Export> Exports => Raw.Exports;
    public List<Import> Imports => Raw.Imports;

    /// <summary>Call after adding, removing or re-parenting exports so Closure sees the change.</summary>
    public void InvalidateIndex() => _children = null;

    /// <summary>Export for i&gt;0, Import for i&lt;0, null for 0.</summary>
    public object? Obj(int i)
    {
        if (i > 0) return Raw.Exports[i - 1];
        if (i < 0) return Raw.Imports[-i - 1];
        return null;
    }

    public Export Export(int i) => i > 0 ? Raw.Exports[i - 1] : throw new ArgumentOutOfRangeException(nameof(i), i, "not an export index");

    public string ObjectName(int i) => Obj(i) switch
    {
        Export e => e.ObjectName.ToString(),
        Import im => im.ObjectName.ToString(),
        _ => "None",
    };

    public int OuterOf(int i) => Obj(i) switch
    {
        Export e => e.OuterIndex.Index,
        Import im => im.OuterIndex.Index,
        _ => 0,
    };

    /// <summary>Dotted outer chain, e.g. "Package.Outer.Name" (analyze_exports.Asset.path).</summary>
    public string Path(int i)
    {
        if (i == 0) return "None";
        var parts = new List<string>();
        for (int cur = i; cur != 0; cur = OuterOf(cur)) parts.Add(ObjectName(cur));
        parts.Reverse();
        return string.Join('.', parts);
    }

    Dictionary<int, List<int>> Children()
    {
        if (_children != null) return _children;
        var children = new Dictionary<int, List<int>>();
        for (int i = 1; i <= Raw.Exports.Count; i++)
        {
            int outer = Raw.Exports[i - 1].OuterIndex.Index;
            if (outer == 0) continue;
            if (!children.TryGetValue(outer, out var list)) children[outer] = list = new List<int>();
            list.Add(i);
        }
        return _children = children;
    }

    /// <summary>
    /// Every export whose OuterIndex chain reaches one of <paramref name="ids"/> (composer.closure).
    /// Uses a cached outer-&gt;children index instead of the Python fixpoint scan.
    /// </summary>
    public HashSet<int> Closure(IEnumerable<int> ids)
    {
        var children = Children();
        var result = new HashSet<int>(ids);
        var queue = new Queue<int>(result);
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var kids)) continue;
            foreach (var k in kids) if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }
}
