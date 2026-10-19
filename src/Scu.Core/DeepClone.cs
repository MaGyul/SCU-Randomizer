using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>
/// Reflection deep copy for UAssetAPI exports and property trees (copy.deepcopy equivalent).
/// Shares the owning UAsset / name-map references instead of copying the whole package.
/// </summary>
public static class DeepClone
{
    static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();

    // FName caches its name-map index in a private field; there is no public setter.
    static readonly FieldInfo NameIndexField = typeof(FName).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!;

    static bool Shared(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) ||
        t.IsPointer || typeof(Type).IsAssignableFrom(t) ||
        typeof(UAsset).IsAssignableFrom(t) || typeof(INameMap).IsAssignableFrom(t);

    static FieldInfo[] FieldsOf(Type t) => Fields.GetOrAdd(t, static type =>
    {
        var list = new List<FieldInfo>();
        for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
            list.AddRange(cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && f.FieldType != typeof(string)));
        return list.ToArray();
    });

    /// <summary>
    /// SCU-PATCH(c#-only): see randomizer/PATCHES.md section 5.
    /// Deep copy. When <paramref name="rebindNames"/> is given, every copied FName is re-bound to that package:
    /// an FName resolves its text through its own Asset and a cached name-map index, so a name still bound to the
    /// source package would be written with the source package's index. The target must already contain every name.
    /// </summary>
    public static T Of<T>(T value, UAsset? rebindNames = null) where T : class
        => (T)Copy(value, new Dictionary<object, object>(ReferenceEqualityComparer.Instance), rebindNames)!;

    static object? Copy(object? o, Dictionary<object, object> map, UAsset? names)
    {
        if (o is null) return null;
        var t = o.GetType();
        if (Shared(t)) return o;
        if (!t.IsValueType && map.TryGetValue(o, out var done)) return done;

        if (t.IsArray)
        {
            var src = (Array)o;
            var dst = Array.CreateInstance(t.GetElementType()!, src.Length);
            map[o] = dst;
            if (Shared(t.GetElementType()!)) Array.Copy(src, dst, src.Length);
            else for (int i = 0; i < src.Length; i++) dst.SetValue(Copy(src.GetValue(i), map, names), i);
            return dst;
        }

        object clone = t.IsValueType ? o : MemberwiseCloneMethod.Invoke(o, null)!;
        if (!t.IsValueType) map[o] = clone;
        foreach (var f in FieldsOf(t))
        {
            var v = f.GetValue(o);
            if (v is null) continue;
            f.SetValue(clone, Copy(v, map, names));
        }
        if (names != null && clone is FName name)
        {
            var text = ((FName)o).Value;
            // names that are not serialized (e.g. property ancestry paths) are not in the name map and stay as they are
            if (names.ContainsNameReference(text))
            {
                name.Asset = names;
                NameIndexField.SetValue(name, names.SearchNameReference(text));
            }
        }
        return clone;
    }
}
