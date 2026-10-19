using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace Scu.Core;

public static class Props
{
    /// <summary>props(e): top-level property name -> property (last one wins like the Python dict).</summary>
    public static Dictionary<string, PropertyData> Of(Export e)
    {
        var map = new Dictionary<string, PropertyData>();
        if (e is NormalExport n)
            foreach (var p in n.Data) map[p.Name.ToString()] = p;
        return map;
    }

    /// <summary>Object-reference value of a property (ObjectPropertyData.Value.Index), or 0 if absent.</summary>
    public static int Ref(Export e, string name)
        => Of(e).TryGetValue(name, out var p) && p is ObjectPropertyData o ? o.Value.Index : 0;
}
