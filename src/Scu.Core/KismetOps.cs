using System.Reflection;
using UAssetAPI.Kismet.Bytecode;

namespace Scu.Core;

/// <summary>Helpers for editing kismet bytecode trees (the Python code edits the JSON nodes in place).</summary>
public static class KismetOps
{
    /// <summary>Every node of type <typeparamref name="T"/> anywhere below <paramref name="root"/>.</summary>
    public static List<T> Find<T>(object root) where T : class
    {
        var found = new List<T>();
        ObjectWalker.Walk(root, node =>
        {
            if (node is T t) found.Add(t);
            return true;
        });
        return found;
    }

    /// <summary>Byte offset of each top-level statement (trace_cache_bytecode.size cumulative).</summary>
    public static List<long> Offsets(Asset a, IEnumerable<KismetExpression> code, out long total)
    {
        var offsets = new List<long>();
        long offset = 0;
        foreach (var x in code)
        {
            offsets.Add(offset);
            offset += x.GetSize(a.Raw);
        }
        total = offset;
        return offsets;
    }

    public static long Size(Asset a, IEnumerable<KismetExpression> code) => code.Sum(x => (long)x.GetSize(a.Raw));

    /// <summary>
    /// Replace expression nodes in place (including nested ones). The callback returns the replacement or null to keep the node.
    /// Needed for the in-place "$type" edits of the Python (e.g. EX_LocalVariable becoming EX_InstanceVariable).
    /// </summary>
    public static void Replace(KismetExpression[] code, Func<KismetExpression, KismetExpression?> replacer)
    {
        for (int i = 0; i < code.Length; i++)
        {
            var node = code[i];
            var replacement = replacer(node);
            if (replacement != null) { code[i] = replacement; node = replacement; }
            ReplaceChildren(node, replacer);
        }
    }

    static void ReplaceChildren(object node, Func<KismetExpression, KismetExpression?> replacer)
    {
        var type = node.GetType();
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Handle(f.FieldType, () => f.GetValue(node), v => f.SetValue(node, v), replacer);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                Handle(p.PropertyType, () => p.GetValue(node), v => p.SetValue(node, v), replacer);
    }

    static void Handle(Type memberType, Func<object?> get, Action<object?> set, Func<KismetExpression, KismetExpression?> replacer)
    {
        if (typeof(KismetExpression).IsAssignableFrom(memberType))
        {
            if (get() is not KismetExpression child) return;
            var replacement = replacer(child);
            if (replacement != null) { set(replacement); child = replacement; }
            ReplaceChildren(child, replacer);
        }
        else if (memberType.IsArray && typeof(KismetExpression).IsAssignableFrom(memberType.GetElementType()))
        {
            if (get() is not Array array) return;
            for (int i = 0; i < array.Length; i++)
            {
                if (array.GetValue(i) is not KismetExpression child) continue;
                var replacement = replacer(child);
                if (replacement != null) { array.SetValue(replacement, i); child = replacement; }
                ReplaceChildren(child, replacer);
            }
        }
        else if (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>) &&
                 typeof(KismetExpression).IsAssignableFrom(memberType.GetGenericArguments()[0]))
        {
            if (get() is not System.Collections.IList list) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not KismetExpression child) continue;
                var replacement = replacer(child);
                if (replacement != null) { list[i] = replacement; child = replacement; }
                ReplaceChildren(child, replacer);
            }
        }
        else if (memberType.Namespace?.StartsWith("UAssetAPI.Kismet", StringComparison.Ordinal) == true &&
                 !memberType.IsEnum && !memberType.IsPrimitive && get() is { } nested)
        {
            ReplaceChildren(nested, replacer);   // e.g. switch cases that hold expressions
        }
    }
}
