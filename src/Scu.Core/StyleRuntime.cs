using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

/// <summary>
/// Port of district_style_runtime.apply: two functions on the level class (SCMixScheduleMapStyle / SCMixApplyMapStyle)
/// that, 0.25 s after BeginPlay, call SetMaterial on the listed door and shelf components, plus a call to the scheduler
/// inserted before the end of ReceiveBeginPlay.
/// SCU-PATCH app/district_style_runtime.py #1-#2: the end-of-event structure check and the insertion point follow the patched version.
/// </summary>
public static class StyleRuntime
{
    const string ApplyName = "SCMixApplyMapStyle";
    const string ScheduleName = "SCMixScheduleMapStyle";

    static readonly Dictionary<string, HashSet<string>> ComponentProperties = new()
    {
        ["/Game/Blueprints/Items/BP_Rack.BP_Rack_C"] = new() { "Mesh_Body" },
        ["/Game/Blueprints/BP_DoorActor_Slide1.BP_DoorActor_Slide1_C"] = new() { "Mesh_Wall", "WoodDoor", "WoodDoor1", "WoodDoor_DM", "WoodDoor_DM1", "WoodDoor_DM_Vine", "WoodDoor_DM_Vine1" },
        ["/Game/Blueprints/BP_DoorActor_Slide2.BP_DoorActor_Slide2_C"] = new() { "Mesh_Wall", "Syouzi", "Syouzi1", "Syouzi_DM_1", "Syouzi_DM_2", "Syouzi_DM_Vine", "Syouzi_DM_Vine1" },
        ["/Game/Blueprints/BP_DoorActor_Slide3.BP_DoorActor_Slide3_C"] = new() { "Mesh_Wall", "Husuma", "Husuma1", "Husuma_DM", "Husuma_DM1", "Husuma_DM_Vine", "Husuma_DM_Vine1" },
        ["/Game/Blueprints/BP_DoorActor_Push.BP_DoorActor_Push_C"] = new() { "Mesh_Wall", "Mesh_Door", "WoodDoor4_DM" },
    };

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    sealed record Label(string Name);

    public static bool Install(Asset a, SortedDictionary<int, SortedDictionary<int, string>> targets)
    {
        if (targets.Count == 0) return false;
        int oldCount = a.Exports.Count;
        var cache = new Dictionary<int, string?>();
        var lookup = new Dictionary<(string, string, string), int>();
        string? Path(int i)
        {
            if (i == 0) return null;
            if (cache.TryGetValue(i, out var hit)) return hit;
            return cache[i] = a.Path(i);
        }
        for (int i = 1; i <= a.Imports.Count; i++)
            lookup.TryAdd((Path(-i)!, a.Imports[i - 1].ClassName.ToString(), a.Imports[i - 1].ClassPackage.ToString()), -i);
        void AddName(string s) => DonorCallbacks.AddNames(a, s);

        int Imp(string fq, string kind, string cp)
        {
            var key = (fq, kind, cp);
            if (lookup.TryGetValue(key, out int hit)) return hit;
            int outer; string shortName;
            if (kind == "Package") { outer = 0; shortName = fq; }
            else
            {
                int dot = fq.LastIndexOf('.');
                string parent = fq[..dot]; shortName = fq[(dot + 1)..];
                outer = parent.StartsWith("/Script/", StringComparison.Ordinal) && !parent.Contains('.') ? Imp(parent, "Package", "/Script/CoreUObject")
                    : parent.StartsWith("/Script/", StringComparison.Ordinal) ? Imp(parent, "Class", "/Script/CoreUObject")
                    : Imp(parent, "Package", "/Script/CoreUObject");
            }
            a.Imports.Add(NewImport(a, shortName, outer, cp, kind));
            int ix = -a.Imports.Count;
            lookup[key] = ix; cache[ix] = fq;
            foreach (var n in new[] { shortName, kind, cp }) AddName(n);
            return ix;
        }

        // ---- class, begin event, templates ----
        var classes = Enumerable.Range(1, oldCount).Where(i => a.Exports[i - 1] is ClassExport && a.Exports[i - 1].ObjectName.ToString() is "Random5x5_C" or "Extra5x5_C" or "Stage5x5_C" or "Stage6x5_C").ToList();
        Require(classes.Count == 1, "level class not found");
        int clsId = classes[0];
        var cls = (ClassExport)a.Export(clsId);
        var begins = Enumerable.Range(1, oldCount).Where(i => a.Exports[i - 1].ObjectName.ToString() == "ReceiveBeginPlay" && a.Exports[i - 1].OuterIndex.Index == clsId).ToList();
        Require(begins.Count == 1, "ReceiveBeginPlay not found");
        int beginId = begins[0];
        var begin = (FunctionExport)a.Export(beginId);
        var beginCode = begin.ScriptBytecode;
        Require(beginCode.Length >= 3 && beginCode[^3] is EX_LocalFinalFunction && beginCode[^2] is EX_Return, "unexpected ReceiveBeginPlay structure");
        Require(!a.Exports.Any(e => e.ObjectName.ToString() is ApplyName or ScheduleName), "style functions already present");
        var template = (FunctionExport)a.Exports.First(e => e is FunctionExport f && f.OuterIndex.Index == clsId && f.Children.Length == 0 && f.SuperStruct.Index == 0 && !f.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Delegate));
        var propertyTemplate = (PropertyExport)a.Exports.First(e => e is PropertyExport);

        int Append(Export e)
        {
            a.Exports.Add(e);
            a.Raw.DependsMap.Add(e.OuterIndex.Index != 0 ? new[] { e.OuterIndex.Index } : Array.Empty<int>());
            AddName(e.ObjectName.ToString());
            a.InvalidateIndex();
            return a.Exports.Count;
        }
        void Clear(Export e)
        {
            e.SerializationBeforeSerializationDependencies = new List<FPackageIndex>();
            e.CreateBeforeSerializationDependencies = new List<FPackageIndex>();
            e.SerializationBeforeCreateDependencies = new List<FPackageIndex>();
            e.CreateBeforeCreateDependencies = new List<FPackageIndex>();
        }
        (int Id, FunctionExport Fn) NewFunction(string fname)
        {
            var e = DeepClone.Of(template, a.Raw);
            e.ObjectName = Names.Make(a, fname);
            e.OuterIndex = new FPackageIndex(clsId);
            e.SuperStruct = new FPackageIndex(0);
            e.SuperIndex = new FPackageIndex(0);
            e.Children = Array.Empty<FPackageIndex>();
            e.LoadedProperties = Array.Empty<FProperty>();
            e.FunctionFlags = EFunctionFlags.FUNC_Public | EFunctionFlags.FUNC_BlueprintCallable | EFunctionFlags.FUNC_BlueprintEvent;
            Clear(e);
            e.CreateBeforeCreateDependencies = new List<FPackageIndex> { new(clsId) };
            return (Append(e), e);
        }
        var (applyId, applyFn) = NewFunction(ApplyName);
        var (scheduleId, scheduleFn) = NewFunction(ScheduleName);

        (int Id, PropertyExport Prop) NewProperty(string pname, string kind, Func<UProperty> make)
        {
            var e = DeepClone.Of(propertyTemplate, a.Raw);
            e.ObjectName = Names.Make(a, pname);
            e.OuterIndex = new FPackageIndex(scheduleId);
            e.ClassIndex = new FPackageIndex(Imp("/Script/CoreUObject." + kind + "Property", "Class", "/Script/CoreUObject"));
            e.TemplateIndex = new FPackageIndex(Imp("/Script/CoreUObject.Default__" + kind + "Property", kind + "Property", "/Script/CoreUObject"));
            e.SuperIndex = new FPackageIndex(0);
            e.Data = new List<PropertyData>();
            e.Extras = Array.Empty<byte>();
            var prop = make();
            prop.ArrayDim = EArrayDim.TArray;
            prop.ElementSize = 0;
            prop.PropertyFlags = EPropertyFlags.CPF_None;
            prop.RepNotifyFunc = Names.Make(a, "None");
            prop.BlueprintReplicationCondition = ELifetimeCondition.COND_None;
            prop.Next = null!;   // the converter leaves the chain end unset
            e.Property = prop;
            Clear(e);
            e.CreateBeforeCreateDependencies = new List<FPackageIndex> { new(scheduleId) };
            return (Append(e), e);
        }
        int signature = Imp("/Script/Engine.TimerDynamicDelegate__DelegateSignature", "DelegateFunction", "/Script/CoreUObject");
        var (delegateId, delegateProp) = NewProperty("SCMapStyleTimerDelegate", "Delegate", () => new UDelegateProperty { SignatureFunction = new FPackageIndex(signature) });
        delegateProp.CreateBeforeSerializationDependencies = new List<FPackageIndex> { new(signature) };
        int timerStruct = Imp("/Script/Engine.TimerHandle", "ScriptStruct", "/Script/CoreUObject");
        var (handleId, handleProp) = NewProperty("SCMapStyleTimerHandle", "Struct", () => new UStructProperty { Struct = new FPackageIndex(timerStruct) });
        handleProp.SerializationBeforeSerializationDependencies = new List<FPackageIndex> { new(timerStruct) };
        int system = Imp("/Script/Engine.Default__KismetSystemLibrary", "KismetSystemLibrary", "/Script/Engine");
        int timer = Imp("/Script/Engine.KismetSystemLibrary.K2_SetTimerDelegate", "Function", "/Script/CoreUObject");
        int valid = Imp("/Script/Engine.KismetSystemLibrary.IsValid", "Function", "/Script/CoreUObject");
        int material = Imp("/Script/Engine.PrimitiveComponent.SetMaterial", "Function", "/Script/CoreUObject");

        // ---- expression builders ----
        KismetPropertyPointer Ptr(int i) => new() { Old = new FPackageIndex(i) };
        KismetExpression Local(int i) => new EX_LocalVariable { Variable = Ptr(i) };
        KismetExpression Call(int i, params KismetExpression[] args) => new EX_FinalFunction { StackNode = new FPackageIndex(i), Parameters = args };
        KismetExpression Ctx(KismetExpression obj, KismetExpression expression, int result = 0, bool quiet = false)
        {
            var c = quiet ? new EX_Context_FailSilent() : new EX_Context();
            c.ObjectExpression = obj;
            c.Offset = expression.GetSize(a.Raw);
            c.PropertyType = 0;
            c.RValuePointer = Ptr(result);
            c.ContextExpression = expression;
            return c;
        }
        KismetExpression[] End() => new KismetExpression[] { new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript() };
        void SetCode(FunctionExport fn, KismetExpression[] items)
        {
            fn.ScriptBytecode = items;
            fn.ScriptBytecodeRaw = null!;
            fn.ScriptBytecodeSize = (int)KismetOps.Size(a, items);
        }

        scheduleFn.Children = new[] { new FPackageIndex(delegateId), new FPackageIndex(handleId) };
        var scheduleCode = new List<KismetExpression>
        {
            new EX_BindDelegate { FunctionName = Names.Make(a, ApplyName), Delegate = Local(delegateId), ObjectTerm = new EX_Self() },
            new EX_Let
            {
                Value = Ptr(handleId), Variable = Local(handleId),
                Expression = Ctx(new EX_ObjectConst { Value = new FPackageIndex(system) },
                    Call(timer, Local(delegateId), new EX_FloatConst { Value = 0.25f }, new EX_False()), handleId),
            },
        };
        scheduleCode.AddRange(End());
        SetCode(scheduleFn, scheduleCode.ToArray());

        // ---- apply function: validity checks and SetMaterial per component ----
        var items = new List<object>();   // KismetExpression or Label
        var rows = new List<(int Component, int Slot, string Material)>();
        foreach (var (component, slots) in targets)
        {
            var binding = Member(a, component, Path, lookup, AddName);
            if (binding.ActorClass == "/Game/Blueprints/Items/BP_Rack.BP_Rack_C" && !(slots.Count == 1 && slots.Keys.All(s => s == 0)))
                throw new InvalidOperationException("shelves may only change their wood surface");
            KismetExpression Receiver() => Ctx(new EX_ObjectConst { Value = new FPackageIndex(binding.Owner) },
                new EX_InstanceVariable { Variable = Ptr(binding.PropertyRef) }, binding.PropertyRef);
            var label = new Label("skip_" + component);
            items.Add(new EX_JumpIfNot
            {
                CodeOffset = 0,
                BooleanExpression = Ctx(new EX_ObjectConst { Value = new FPackageIndex(system) }, Call(valid, new EX_ObjectConst { Value = new FPackageIndex(binding.Owner) })),
            });
            items.Add(("jump", label, 0));
            items.Add(new EX_JumpIfNot
            {
                CodeOffset = 0,
                BooleanExpression = Ctx(new EX_ObjectConst { Value = new FPackageIndex(system) }, Call(valid, Receiver())),
            });
            items.Add(("jump", label, 0));
            foreach (var (slot, matPath) in slots)
            {
                int r = Imp(matPath, "Material", "/Script/Engine");
                items.Add(Ctx(Receiver(), Call(material, new EX_IntConst { Value = slot }, new EX_ObjectConst { Value = new FPackageIndex(r) }), quiet: true));
                rows.Add((component, slot, matPath));
            }
            items.Add(label);
        }
        foreach (var x in End()) items.Add(x);
        SetCode(applyFn, FinishCode(a, items));

        void Dependencies(FunctionExport fn)
        {
            var refs = new SortedSet<int>();
            ObjectWalker.Walk(fn.ScriptBytecode, node =>
            {
                switch (node)
                {
                    case KismetPropertyPointer p: refs.Add(p.Old.Index); break;
                    case EX_ObjectConst oc: refs.Add(oc.Value.Index); break;
                    case EX_FinalFunction ff: refs.Add(ff.StackNode.Index); break;
                    case EX_StructConst sc: refs.Add(sc.Struct.Index); break;
                    case EX_CastBase cb: refs.Add(cb.ClassPtr.Index); break;
                }
                return true;
            });
            fn.SerializationBeforeSerializationDependencies = fn.Children.Select(c => new FPackageIndex(c.Index)).ToList();
            var children = fn.Children.Select(c => c.Index).ToHashSet();
            fn.CreateBeforeSerializationDependencies = refs.Where(r => !children.Contains(r) && r != 0).Select(r => new FPackageIndex(r)).ToList();
        }
        Dependencies(scheduleFn);
        Dependencies(applyFn);

        // ---- hook the scheduler into ReceiveBeginPlay, register the functions on the class ----
        var scheduleCall = new EX_LocalFinalFunction { StackNode = new FPackageIndex(scheduleId), Parameters = Array.Empty<KismetExpression>() };
        begin.ScriptBytecode = beginCode[..^2].Concat(new KismetExpression[] { scheduleCall }).Concat(beginCode[^2..]).ToArray();
        begin.ScriptBytecodeRaw = null!;
        begin.ScriptBytecodeSize += (int)scheduleCall.GetSize(a.Raw);
        begin.CreateBeforeSerializationDependencies.Add(new FPackageIndex(scheduleId));
        cls.Children = cls.Children.Concat(new[] { new FPackageIndex(applyId), new FPackageIndex(scheduleId) }).ToArray();
        cls.SerializationBeforeSerializationDependencies.AddRange(new[] { new FPackageIndex(applyId), new FPackageIndex(scheduleId) });
        cls.FuncMap.Add(Names.Make(a, ApplyName), new FPackageIndex(applyId));
        cls.FuncMap.Add(Names.Make(a, ScheduleName), new FPackageIndex(scheduleId));
        foreach (var row in a.Imports)
            foreach (var n in new[] { row.ObjectName.ToString(), row.ClassName.ToString(), row.ClassPackage.ToString() }) AddName(n);
        foreach (var n in new[] { ApplyName, ScheduleName, "None" }) AddName(n);
        Compose.UpdateGenerations(a);
        return true;
    }

    static Import NewImport(Asset a, string objectName, int outer, string classPackage, string className)
        => new(Names.Make(a, classPackage), Names.Make(a, className), new FPackageIndex(outer), Names.Make(a, objectName), false);

    sealed record Binding(int Component, int Owner, string ActorClass, string ComponentName, string PropertyPath, int PropertyRef);

    static Binding Member(Asset a, int component, Func<int, string?> path, Dictionary<(string, string, string), int> lookup, Action<string> addName)
    {
        Require(component >= 1 && component <= a.Exports.Count, "door component index out of range");
        var child = a.Export(component);
        int owner = child.OuterIndex.Index;
        Require(owner >= 1 && owner <= a.Exports.Count, "door owner index out of range");
        var actor = (NormalExport)a.Export(owner);
        int aclass = actor.ClassIndex.Index;
        var apath = path(aclass)!;
        var pname = child.ObjectName.ToString();
        Require(aclass < 0 && a.Imports[-aclass - 1].ClassName.ToString() == "BlueprintGeneratedClass" && a.Imports[-aclass - 1].ClassPackage.ToString() == "/Script/Engine"
                && ComponentProperties.TryGetValue(apath, out var allowed) && allowed.Contains(pname), "unverified door component");
        var expected = pname.Contains("_DM") ? "/Script/ApexDestruction.DestructibleComponent" : "/Script/Engine.StaticMeshComponent";
        Require(path(child.ClassIndex.Index) == expected && path(child.TemplateIndex.Index) == apath + "." + pname + "_GEN_VARIABLE", "door component type or template differs");
        if (apath == "/Game/Blueprints/Items/BP_Rack.BP_Rack_C")
        {
            var meshes = ((NormalExport)child).Data.Where(p => p.Name.ToString() == "StaticMesh").ToList();
            Require(meshes.Count <= 1 && (meshes.Count == 0 || (meshes[0] is ObjectPropertyData m && path(m.Value.Index) == "/Game/Props/lack.lack")), "shelf mesh differs");
        }
        var pointer = actor.Data.Where(p => p.Name.ToString() == pname).ToList();
        Require(pointer.Count <= 1 && (pointer.Count == 0 || (pointer[0] is ObjectPropertyData po && po.Value.Index == component)), "door owner pointer differs");
        var fq = apath + "." + pname;
        var key = (fq, "ObjectProperty", "/Script/CoreUObject");
        if (!lookup.ContainsKey(key))
        {
            a.Imports.Add(NewImport(a, pname, aclass, "/Script/CoreUObject", "ObjectProperty"));
            lookup[key] = -a.Imports.Count;
            foreach (var n in new[] { pname, "ObjectProperty", "/Script/CoreUObject" }) addName(n);
        }
        Require(a.Imports[-lookup[key] - 1].OuterIndex.Index == aclass, "door component property class differs");
        return new Binding(component, owner, apath, pname, fq, lookup[key]);
    }

    /// <summary>finish_code: resolve label placeholders to byte offsets and return the plain expression list.</summary>
    static KismetExpression[] FinishCode(Asset a, List<object> items)
    {
        var labels = new Dictionary<string, long>();
        long offset = 0;
        var output = new List<KismetExpression>();
        var jumps = new List<(EX_JumpIfNot Jump, Label Target)>();
        foreach (var item in items)
        {
            switch (item)
            {
                case Label l: labels[l.Name] = offset; break;
                case ValueTuple<string, Label, int> j: jumps.Add(((EX_JumpIfNot)output[^1], j.Item2)); break;
                case KismetExpression x: output.Add(x); offset += x.GetSize(a.Raw); break;
            }
        }
        foreach (var (jump, target) in jumps) jump.CodeOffset = (uint)labels[target.Name];
        return output.ToArray();
    }
}
