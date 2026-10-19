using System.Reflection;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>
/// Name handling that reproduces what the Python pipeline gets from the JSON converter.
/// In the Python flow names are plain strings; the name map only grows through explicit appends, and any name the
/// converter finds missing is appended implicitly when the package is written. Here names are created as "dummy"
/// FNames (no name-map side effect) and <see cref="Finalize"/> performs the implicit additions right before a write.
/// </summary>
public static class Names
{
    static readonly Regex TrailingNumber = new(@"_\d+$", RegexOptions.Compiled);

    /// <summary>"Foo_10" becomes (Foo, 11), exactly like the JSON converter; the name map is not touched.</summary>
    public static FName Make(Asset a, string s)
    {
        var m = TrailingNumber.Match(s);
        if (m.Success && m.Index > 0)
        {
            var digits = s[(m.Index + 1)..];
            if (digits.Length <= 9 && (digits == "0" || digits[0] != '0')) return FName.DefineDummy(a.Raw, s[..m.Index], int.Parse(digits) + 1);
        }
        return FName.DefineDummy(a.Raw, s, 0);
    }

    /// <summary>
    /// Re-bind every FName in the package to it and append the names the name map lacks, in the order the JSON converter
    /// meets them (exports first, then imports). Run before every write.
    /// </summary>
    public static void Finalize(Asset a, bool addMissing = true)
    {
        var raw = a.Raw;
        void Visit(object root)
        {
            // names that are never written: the Name of an array's elements and property ancestry paths
            var notSerialized = new HashSet<object>(ReferenceEqualityComparer.Instance);
            ObjectWalker.WalkWithNames(root, node =>
            {
                if (node is UAssetAPI.PropertyTypes.Objects.ArrayPropertyData array)
                    foreach (var element in array.Value) notSerialized.Add(element.Name);
                if (node is UAssetAPI.PropertyTypes.Objects.AncestryInfo) return false;
                if (node is not FName name || notSerialized.Contains(name)) return true;
                bool dummy = name.IsDummy;
                if (!dummy && ReferenceEquals(name.Asset, raw)) return true;
                var text = name.Value;
                bool existed = raw.ContainsNameReference(text);
                if (!existed && !addMissing) return true;   // diagnostic dumps: do not extend the name map
                int index = existed ? raw.SearchNameReference(text) : raw.AddNameReference(text);
                if (!existed && Environment.GetEnvironmentVariable("SCU_DEBUG_NAMES") == "1")
                    Console.Error.WriteLine($"[names] added '{text}' dummy={dummy} assetIsRaw={ReferenceEquals(name.Asset, raw)}");
                if (dummy) DummyValueField.SetValue(name, null);
                name.Asset = raw;
                NameIndexField.SetValue(name, index);
                return true;
            });
        }
        foreach (var e in a.Exports) Visit(e);
        foreach (var i in a.Imports) Visit(i);
    }

    static readonly FieldInfo NameIndexField = typeof(FName).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly FieldInfo DummyValueField = typeof(FName).GetField("DummyValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
}
