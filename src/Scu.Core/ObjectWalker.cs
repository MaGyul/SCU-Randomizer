using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>
/// Generic depth-first walk over a UAssetAPI object graph (property trees, kismet bytecode).
/// The visitor sees every reference-type node once and returns false to skip its children.
/// Replaces the Python "walk every dict" helpers without materializing a JSON tree.
/// </summary>
public static class ObjectWalker
{
    static readonly ConcurrentDictionary<Type, Func<object, object?>[]> Members = new();
    static readonly ConcurrentDictionary<Type, Func<object, object?>[]> MembersWithNames = new();

    static bool Opaque(Type t, bool names) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) ||
        (!names && t == typeof(FName)) || t == typeof(FString) || t == typeof(byte[]) ||
        typeof(UAsset).IsAssignableFrom(t) || typeof(INameMap).IsAssignableFrom(t) ||
        typeof(Type).IsAssignableFrom(t) || t.IsPointer;

    static bool Descendable(Type t, bool names) =>
        !Opaque(t, names) && t != typeof(FName) && (t.Namespace?.StartsWith("UAssetAPI", StringComparison.Ordinal) == true ||
                       (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)));

    static Func<object, object?>[] MembersOf(Type t, bool names) => (names ? MembersWithNames : Members).GetOrAdd(t, type =>
    {
        var getters = new List<Func<object, object?>>();
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!Opaque(f.FieldType, names)) { var fi = f; getters.Add(o => fi.GetValue(o)); }
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.GetIndexParameters().Length == 0 && !Opaque(p.PropertyType, names))
            { var pi = p; getters.Add(o => { try { return pi.GetValue(o); } catch (TargetInvocationException) { return null; } }); }
        return getters.ToArray();
    });

    public static void Walk(object? root, Func<object, bool> visitor)
        => Visit(root, visitor, new HashSet<object>(ReferenceEqualityComparer.Instance), false);

    /// <summary>Like <see cref="Walk"/> but FName instances are visited too (they are never descended into).</summary>
    public static void WalkWithNames(object? root, Func<object, bool> visitor)
        => Visit(root, visitor, new HashSet<object>(ReferenceEqualityComparer.Instance), true);

    static void Visit(object? o, Func<object, bool> visitor, HashSet<object> seen, bool names)
    {
        if (o is null) return;
        var t = o.GetType();
        if (Opaque(t, names)) return;
        if (!t.IsValueType && !seen.Add(o)) return;
        if (!visitor(o)) return;

        if (o is IDictionary dict)
        {
            foreach (DictionaryEntry de in dict) { Visit(de.Key, visitor, seen, names); Visit(de.Value, visitor, seen, names); }
            return;
        }
        if (o is IEnumerable seq)
        {
            foreach (var item in seq) Visit(item, visitor, seen, names);
            return;
        }
        if (!Descendable(t, names)) return;
        foreach (var get in MembersOf(t, names)) Visit(get(o), visitor, seen, names);
    }
}
