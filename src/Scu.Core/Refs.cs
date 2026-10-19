using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>Package-index rewriting for exports (build_structural_draft.rewrite_normal + composer.rewrite_extra).</summary>
public static class Refs
{
    static readonly string[] FieldRefNames =
        { "Next", "Inner", "PropertyClass", "MetaClass", "SignatureFunction", "Struct", "Enum", "UnderlyingProp", "KeyProp", "ValueProp", "ElementProp" };

    static readonly string[] AllowedStreamingWorlds =
        new[] { "/Game/Levels/RundomMapTiles/Goal.Goal" }
        .Concat(Enumerable.Range(1, 4).Select(n => $"/Game/Levels/RundomMapTiles/StartPoint/StartPoint{n}.StartPoint{n}"))
        .ToArray();

    /// <summary>One rewrite pass; a shared FPackageIndex instance is remapped only once.</summary>
    sealed class Pass
    {
        readonly Func<int, int> _fn;
        readonly HashSet<object> _done = new(ReferenceEqualityComparer.Instance);
        public Pass(Func<int, int> fn) => _fn = fn;
        public void Fix(FPackageIndex? p)
        {
            if (p is null || !_done.Add(p)) return;
            p.Index = _fn(p.Index);
        }
        public void FixAll(IEnumerable<FPackageIndex>? ps) { if (ps is null) return; foreach (var p in ps) Fix(p); }
    }

    static readonly Func<Export, List<FPackageIndex>>[] DependencyLists =
    {
        e => e.SerializationBeforeSerializationDependencies,
        e => e.CreateBeforeSerializationDependencies,
        e => e.SerializationBeforeCreateDependencies,
        e => e.CreateBeforeCreateDependencies,
    };

    /// <summary>Header indices, dependency lists and object references inside property data.</summary>
    public static void RewriteNormal(Export e, Func<int, int> fn, Action<SoftObjectPropertyData>? onSoft = null)
    {
        var pass = new Pass(fn);
        pass.Fix(e.OuterIndex); pass.Fix(e.ClassIndex); pass.Fix(e.SuperIndex); pass.Fix(e.TemplateIndex);
        foreach (var list in DependencyLists) pass.FixAll(list(e));
        if (e is not NormalExport ne) return;
        RewriteData(ne.Data, pass, onSoft);
    }

    /// <summary>Object references inside property data only (rewrite_normal on a {'Data': [...]} stub).</summary>
    public static void RewriteData(IEnumerable<PropertyData> data, Func<int, int> fn, Action<SoftObjectPropertyData>? onSoft = null)
        => RewriteData(data, new Pass(fn), onSoft);

    static void RewriteData(IEnumerable<PropertyData> data, Pass pass, Action<SoftObjectPropertyData>? onSoft)
    {
        ObjectWalker.Walk(data, node =>
        {
            switch (node)
            {
                case SoftObjectPropertyData soft:
                    if (onSoft is null) throw new InvalidOperationException("unhandled reference type " + node.GetType().Name);
                    onSoft(soft);
                    return false;
                case ObjectPropertyData when node.GetType().Name.Contains("WeakObjectProperty"):
                    throw new InvalidOperationException("unhandled reference type " + node.GetType().Name);
                case ObjectPropertyData obj:   // includes InterfacePropertyData
                    pass.Fix(obj.Value); return false;
                case FDelegate del:
                    pass.Fix(del.Object); return false;
                default:
                    var n = node.GetType().Name;
                    if (n.Contains("SoftObjectProperty") || n.Contains("LazyObjectProperty") || n.Contains("WeakObjectProperty"))
                        throw new InvalidOperationException("unhandled reference type " + n);
                    return true;
            }
        });
    }

    /// <summary>preflight_offline_swap.ref_fields: header indices, dependency lists and object references in property data.</summary>
    public static List<int> CollectNormal(Export e)
    {
        var values = new List<int>();
        RewriteNormal(e, v => { values.Add(v); return v; }, _ => { });
        return values;
    }

    public static void AllowedStreamingWorld(SoftObjectPropertyData soft)
    {
        if (soft.Name.ToString() != "WorldAsset" || !string.IsNullOrEmpty(soft.Value.SubPathString?.ToString()))
            throw new InvalidOperationException("unexpected soft object reference " + soft.Name);
        var path = soft.Value.AssetPath.AssetName.ToString();
        if (Array.IndexOf(AllowedStreamingWorlds, path) < 0)
            throw new InvalidOperationException("streaming world not allowed: " + path);
    }

    /// <summary>Level/class/struct metadata and kismet bytecode references.</summary>
    public static void RewriteExtra(Export e, Func<int, int> fn)
    {
        var pass = new Pass(fn);
        if (e is LevelExport level)
        {
            pass.Fix(level.Model); pass.Fix(level.LevelScriptActor); pass.Fix(level.NavListStart); pass.Fix(level.NavListEnd);
            pass.Fix(level.Owner);
            pass.FixAll(level.ModelComponents);
        }
        if (e is StructExport st)
        {
            pass.Fix(st.SuperStruct);
            if (st.Children is not null) pass.FixAll(st.Children);
            if (st.LoadedProperties is { Length: > 0 }) throw new InvalidOperationException("unsupported metadata");
        }
        if (e is ClassExport cls)
        {
            pass.Fix(cls.ClassWithin); pass.Fix(cls.ClassDefaultObject); pass.Fix(cls.ClassGeneratedBy);
            foreach (var key in cls.FuncMap.Keys.ToList()) pass.Fix(cls.FuncMap[key]);
            if (cls.Interfaces is { Length: > 0 }) throw new InvalidOperationException("unsupported metadata");
        }
        if (e is FieldExport fe && fe.Field is not null) FixNamed(fe.Field, pass);
        if (e is PropertyExport pe && pe.Property is not null) FixNamed(pe.Property, pass);

        if (e is StructExport { ScriptBytecode: { } code })
        {
            ObjectWalker.Walk(code, node =>
            {
                switch (node)
                {
                    case KismetPropertyPointer ptr: pass.Fix(ptr.Old); return false;
                    case EX_ObjectConst oc: pass.Fix(oc.Value); return true;
                    case EX_FinalFunction ff: pass.Fix(ff.StackNode); return true;   // Local/CallMath/CallMulticast derive from it
                    case EX_StructConst sc: pass.Fix(sc.Struct); return true;
                    case EX_DynamicCast or EX_MetaCast: pass.Fix(((EX_CastBase)node).ClassPtr); return true;
                    default: return true;
                }
            });
        }
    }

    static void FixNamed(object obj, Pass pass)
    {
        var t = obj.GetType();
        foreach (var name in FieldRefNames)
        {
            var f = t.GetField(name);
            if (f?.FieldType == typeof(FPackageIndex)) { pass.Fix((FPackageIndex?)f.GetValue(obj)); continue; }
            var p = t.GetProperty(name);
            if (p?.PropertyType == typeof(FPackageIndex) && p.GetIndexParameters().Length == 0) pass.Fix((FPackageIndex?)p.GetValue(obj));
        }
    }
}
