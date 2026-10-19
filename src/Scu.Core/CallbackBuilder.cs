using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed class CallbackPlan
{
    public string Label = "";
    public int Trigger;
    public int Function;
    public string FunctionName = "";
    public List<int> SourceIndices = new();
    public Dictionary<int, int> PropertyMap = new();
    public List<int> StateFields = new();
    public List<int> SourceActorPropertiesRetained = new();
    public long BytecodeSize;
}

public sealed record IntegratedCallback(int Function, Dictionary<int, int> Fields, Dictionary<int, string> ActorFields);

/// <summary>
/// Port of soul_x4_callback.add_callback and integrate_sanctuary_callback.integrate_callback: lift the lever/overlap
/// handler (a slice of the donor's ExecuteUbergraph) into its own function inside the donor package, then copy that
/// function with its private fields into the composed package.
/// </summary>
public static class CallbackBuilder
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    static T Pass<T>(T value, Action<T> action) { action(value); return value; }

    public static CallbackPlan AddCallback(Asset a, int trigger, int start, int end, ISet<int> states, string label,
        IReadOnlyList<int>? indicesOverride = null, int wrapperId = 0, string sourceFunction = "ExecuteUbergraph_Stage5x5", bool classOwnedStates = false)
    {
        var ex = a.Exports;
        var source = (FunctionExport)ex.First(e => e.ObjectName.ToString() == sourceFunction);
        int sourceIndex = ex.IndexOf(source) + 1;
        MulticastDelegatePropertyData? delegateProperty = trigger != 0
            ? ((NormalExport)a.Export(trigger)).Data.OfType<MulticastDelegatePropertyData>().First(p => p.Name.ToString() == "OnActorBeginOverlap")
            : null;
        var wrapper = (FunctionExport)(wrapperId != 0
            ? a.Export(wrapperId)
            : ex.First(e => e.ObjectName.ToString() == delegateProperty!.Value[0].Delegate.ToString()));

        var oldOffsets = KismetOps.Offsets(a, source.ScriptBytecode, out long total);
        Require(total == source.ScriptBytecodeSize, "source bytecode size mismatch");

        int fnId = ex.Count + 1;
        var fn = DeepClone.Of(wrapper, a.Raw);
        string name = "SCMix_" + label + "_LeverPulled";
        fn.ObjectName = Names.Make(a, name);
        var fnChildren = new List<FPackageIndex>();
        fn.ScriptBytecodeRaw = null!;
        fn.OuterIndex = new FPackageIndex(2);
        ClearDependencies(fn);
        fn.CreateBeforeCreateDependencies = new List<FPackageIndex> { new(2) };
        ex.Add(fn); a.Raw.DependsMap.Add(new[] { 2 });
        a.InvalidateIndex();

        var mapping = new Dictionary<int, int>();
        var classProps = new List<int>();
        int ClonePropExport(int src, int outer)
        {
            if (mapping.TryGetValue(src, out int done)) return done;
            var old = a.Export(src);
            Require(old.GetType() == typeof(PropertyExport), "expected a property export");
            var e = (PropertyExport)DeepClone.Of(old, a.Raw);
            int i = ex.Count + 1;
            mapping[src] = i;
            e.OuterIndex = new FPackageIndex(outer);
            ClearDependencies(e);
            e.CreateBeforeCreateDependencies = new List<FPackageIndex> { new(outer) };
            ex.Add(e); a.Raw.DependsMap.Add(new[] { outer });
            a.InvalidateIndex();
            var pr = e.Property;
            if (pr is UArrayProperty array && array.Inner.Index != 0)
            {
                array.Inner = new FPackageIndex(ClonePropExport(array.Inner.Index, i));
                e.SerializationBeforeSerializationDependencies = new List<FPackageIndex> { new(array.Inner.Index) };
            }
            if (states.Contains(src))
            {
                e.ObjectName = Names.Make(a, "SCMix_" + label + "_" + old.ObjectName);
                pr.PropertyFlags = EPropertyFlags.CPF_Transient;
                classProps.Add(i);
            }
            else if (outer == fnId) fnChildren.Add(new FPackageIndex(i));
            return i;
        }

        foreach (var child in wrapper.Children) ClonePropExport(child.Index, fnId);
        foreach (var x in wrapper.ScriptBytecode)
            if (x is EX_LetValueOnPersistentFrame let)
                mapping[let.DestinationProperty.Old.Index] = mapping[((EX_VariableBase)let.AssignmentExpression).Variable.Old.Index];

        var indices = indicesOverride != null ? indicesOverride.ToList()
            : Enumerable.Range(start, end - start).Concat(label == "Stage6_X8" ? new[] { 789, 790, 791 } : Array.Empty<int>()).ToList();
        var code = indices.Select(i => DeepClone.Of(source.ScriptBytecode[i], a.Raw)).ToArray();
        var pointers = new SortedSet<int>(KismetOps.Find<KismetPropertyPointer>(code).Select(p => p.Old.Index).Where(i => i > 0));
        foreach (int old in pointers)
        {
            if (mapping.ContainsKey(old)) continue;
            var e = a.Export(old);
            // source-map proof retains these existing actor reference properties
            // SCU-PATCH(sanctuary-LT all_build): a class-owned boolean that is declared a state is cloned like a temporary DoOnce field.
            if (e.OuterIndex.Index == 2 && !(classOwnedStates && states.Contains(old))) continue;
            Require(e.OuterIndex.Index == sourceIndex || (classOwnedStates && e.OuterIndex.Index == 2), "unknown pointer owner " + old);
            ClonePropExport(old, states.Contains(old) ? 2 : fnId);
        }
        foreach (var p in KismetOps.Find<KismetPropertyPointer>(code))
            p.Old = new FPackageIndex(mapping.TryGetValue(p.Old.Index, out int mapped) ? mapped : p.Old.Index);
        // Separate pass: the walk visits an expression before its nested property pointer.
        KismetOps.Replace(code, x => x.GetType() == typeof(EX_LocalVariable) && classProps.Contains(((EX_LocalVariable)x).Variable.Old.Index)
            ? new EX_InstanceVariable { Variable = ((EX_LocalVariable)x).Variable }
            : null);

        var offsets = new Dictionary<long, long>();
        long cursor = 5;
        for (int k = 0; k < indices.Count; k++)
        {
            offsets[oldOffsets[indices[k]]] = cursor;
            cursor += code[k].GetSize(a.Raw);
        }
        foreach (var node in KismetOps.Find<KismetExpression>(code))
        {
            switch (node)
            {
                case EX_Jump j: j.CodeOffset = (uint)Lookup(offsets, j.CodeOffset); break;
                case EX_JumpIfNot j: j.CodeOffset = (uint)Lookup(offsets, j.CodeOffset); break;
                case EX_PushExecutionFlow p: p.PushingAddress = (uint)Lookup(offsets, p.PushingAddress); break;
            }
        }

        fn.ScriptBytecode = new KismetExpression[] { new EX_PushExecutionFlow { PushingAddress = (uint)cursor } }
            .Concat(code)
            .Concat(new KismetExpression[] { new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript() })
            .ToArray();
        fn.ScriptBytecodeSize = (int)KismetOps.Size(a, fn.ScriptBytecode);
        fn.Children = fnChildren.ToArray();
        fn.SerializationBeforeSerializationDependencies = fnChildren.Select(c => new FPackageIndex(c.Index)).ToList();
        var childIndices = fnChildren.Select(c => c.Index).ToHashSet();
        fn.CreateBeforeSerializationDependencies = mapping.Values.ToHashSet().Except(childIndices).OrderBy(x => x).Select(x => new FPackageIndex(x)).ToList();

        var cls = (ClassExport)a.Export(2);
        cls.FuncMap.Add(Names.Make(a, name), new FPackageIndex(fnId));
        cls.Children = new[] { new FPackageIndex(fnId) }.Concat(classProps.Select(x => new FPackageIndex(x))).Concat(cls.Children).ToArray();
        cls.SerializationBeforeSerializationDependencies.AddRange(new[] { fnId }.Concat(classProps).Select(x => new FPackageIndex(x)));
        // UFunction registration and field defaults only. Do not run the donor's full LevelScript.
        if (delegateProperty != null) delegateProperty.Value[0].Delegate = Names.Make(a, name);
        foreach (var n in new[] { name }.Concat(mapping.Values.Select(i => ex[i - 1].ObjectName.ToString())))
            if (!a.Raw.ContainsNameReference(new FString(n))) a.Raw.AddNameReference(new FString(n));

        return new CallbackPlan
        {
            Label = label, Trigger = trigger, Function = fnId, FunctionName = name, SourceIndices = indices,
            PropertyMap = mapping, StateFields = classProps,
            SourceActorPropertiesRetained = pointers.Where(p => !mapping.ContainsKey(p)).ToList(),
            BytecodeSize = fn.ScriptBytecodeSize,
        };
    }

    static long Lookup(Dictionary<long, long> offsets, long key)
    {
        if (!offsets.TryGetValue(key, out long value)) throw new InvalidOperationException("external jump " + key);
        return value;
    }

    static void ClearDependencies(Export e)
    {
        e.SerializationBeforeSerializationDependencies = new List<FPackageIndex>();
        e.CreateBeforeSerializationDependencies = new List<FPackageIndex>();
        e.SerializationBeforeCreateDependencies = new List<FPackageIndex>();
        e.CreateBeforeCreateDependencies = new List<FPackageIndex>();
    }

    /// <summary>integrate_sanctuary_callback.integrate_callback.</summary>
    public static IntegratedCallback Integrate(Asset host, Asset proof, CallbackPlan plan, IReadOnlyDictionary<int, int> actorMap, Func<int, int> imp, bool remapStructConst = false)
    {
        var ex = host.Exports;
        var source = proof.Exports;
        int fn = plan.Function;
        var states = plan.StateFields;
        var refs = plan.SourceActorPropertiesRetained;
        var ids = new SortedSet<int> { fn };
        foreach (int i in states) ids.Add(i);
        foreach (int i in refs) ids.Add(i);
        var todo = new List<int>(source[fn - 1] is StructExport sfn ? sfn.Children.Select(c => c.Index) : Array.Empty<int>());
        while (todo.Count > 0)
        {
            int i = todo[^1]; todo.RemoveAt(todo.Count - 1);
            if (ids.Contains(i)) continue;
            ids.Add(i);
            if (source[i - 1] is PropertyExport { Property: UArrayProperty { Inner.Index: not 0 } array }) todo.Add(array.Inner.Index);
        }
        var m = new Dictionary<int, int>();
        int next = ex.Count;
        foreach (int i in ids) m[i] = ++next;
        var actorFields = refs.ToDictionary(i => i, i => "SCMix_" + plan.Label + "_" + source[i - 1].ObjectName);

        // Names first: copied names are re-bound to the host package, so the proof's names must already be there.
        foreach (var n in proof.Raw.GetNameMapIndexList()) host.Raw.AddNameReference(n);

        int Ref(int v)
        {
            if (v == 0) return 0;
            if (v < 0) return imp(v);
            if (m.TryGetValue(v, out int mapped)) return mapped;
            if (v == 2) return 2;
            if (!actorMap.TryGetValue(v, out int actor)) throw new InvalidOperationException("unmapped callback ref " + v);
            return actor;
        }

        foreach (int i in ids)
        {
            var e = DeepClone.Of(source[i - 1], host.Raw);
            Refs.RewriteNormal(e, Ref, Refs.AllowedStreamingWorld);
            if (actorFields.TryGetValue(i, out var fieldName)) e.ObjectName = Names.Make(host, fieldName);
            if (e is StructExport st)
            {
                st.SuperStruct = new FPackageIndex(Ref(st.SuperStruct.Index));
                if (st.Children != null) st.Children = st.Children.Select(c => new FPackageIndex(Ref(c.Index))).ToArray();
            }
            if (e is PropertyExport { Property: { } prop })
                foreach (var field in new[] { "Inner", "PropertyClass", "SignatureFunction", "MetaClass", "Struct", "InterfaceClass" })
                {
                    var member = prop.GetType().GetField(field);
                    if (member?.FieldType == typeof(FPackageIndex) && member.GetValue(prop) is FPackageIndex idx)
                        member.SetValue(prop, new FPackageIndex(Ref(idx.Index)));
                }
            if (e is StructExport { ScriptBytecode: { } code })
                foreach (var node in KismetOps.Find<object>(code))
                {
                    switch (node)
                    {
                        case KismetPropertyPointer ptr: ptr.Old = new FPackageIndex(Ref(ptr.Old.Index)); break;
                        case EX_ObjectConst oc: oc.Value = new FPackageIndex(Ref(oc.Value.Index)); break;
                        case EX_CallMath cm: cm.StackNode = new FPackageIndex(Ref(cm.StackNode.Index)); break;
                        case EX_LocalFinalFunction lf: lf.StackNode = new FPackageIndex(Ref(lf.StackNode.Index)); break;
                        case EX_DynamicCast dc: dc.ClassPtr = new FPackageIndex(Ref(dc.ClassPtr.Index)); break;
                        case EX_StructConst sc when remapStructConst: sc.Struct = new FPackageIndex(Ref(sc.Struct.Index)); break;
                        default:
                            if (node.GetType() == typeof(EX_FinalFunction)) { var ff = (EX_FinalFunction)node; ff.StackNode = new FPackageIndex(Ref(ff.StackNode.Index)); }
                            break;
                    }
                }
            Require(m[i] == ex.Count + 1, "callback export numbering drifted");
            e.Asset = host.Raw;
            ex.Add(e);
            host.Raw.DependsMap.Add((proof.Raw.DependsMap[i - 1] ?? Array.Empty<int>()).Select(Ref).ToArray());
        }
        host.InvalidateIndex();

        var cls = (ClassExport)ex[1];
        var newClass = states.Concat(refs).Select(v => m[v]).ToList();
        cls.Children = new[] { m[fn] }.Concat(newClass).Select(x => new FPackageIndex(x)).Concat(cls.Children).ToArray();
        cls.FuncMap.Add(Names.Make(host, source[fn - 1].ObjectName.ToString()), new FPackageIndex(m[fn]));
        cls.SerializationBeforeSerializationDependencies.AddRange(new[] { m[fn] }.Concat(newClass).Select(x => new FPackageIndex(x)));
        var cdo = (NormalExport)ex[2];
        var sourceCdo = (NormalExport)source[2];
        foreach (var (i, name) in actorFields)
        {
            var p = DeepClone.Of(sourceCdo.Data.First(q => q.Name.ToString() == source[i - 1].ObjectName.ToString()), host.Raw);
            p.Name = Names.Make(host, name);
            var obj = (ObjectPropertyData)p;
            obj.Value = new FPackageIndex(Ref(obj.Value.Index));
            cdo.Data.Add(p);
            cdo.CreateBeforeSerializationDependencies.Add(new FPackageIndex(obj.Value.Index));
        }
        foreach (var e in ex)
        {
            var full = e.ObjectName.ToString();
            if (!host.Raw.ContainsNameReference(new FString(full))) host.Raw.AddNameReference(new FString(full));
        }
        Compose.UpdateGenerations(host);
        return new IntegratedCallback(m[fn], m, actorFields);
    }
}
