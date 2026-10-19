using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record LatentSpec(
    string ResumeLabel, string StartLabel, string StateFieldName, string ActorPrefix, int BindOwner,
    int[] Indices, Func<Asset, int> WrapperIndex, Func<int, int> UuidFor, int StartEntryKey, ISet<int>? ExpectedActors);

/// <summary>
/// Port of latent_x6.make_proof / soul_lt_special.make_proof + latent(): a private "resume" UFunction for the floor
/// animation (an int EntryPoint parameter and a latent-action struct constant) plus a one-shot "start" function.
/// </summary>
public static class LatentBuilder
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static void Install(Asset data, string stage5Umap, LatentSpec spec)
    {
        var a = AssetCache.Take(stage5Umap);
        var src = (FunctionExport)a.Export(48);
        int wid = spec.WrapperIndex(a);
        // A private resume UFunction requires the original int EntryPoint parameter.
        var wrapper = (FunctionExport)a.Export(wid);
        var saved = DeepClone.Of(wrapper, a.Raw);
        wrapper.Children = new[] { new FPackageIndex(47) };
        wrapper.ScriptBytecode = Array.Empty<KismetExpression>();
        var p = CallbackBuilder.AddCallback(a, 0, 0, 0, new HashSet<int>(), spec.ResumeLabel, spec.Indices, wid);
        a.Raw.Exports[wid - 1] = saved;
        a.InvalidateIndex();
        var fn = (FunctionExport)a.Export(p.Function);
        fn.FunctionFlags = src.FunctionFlags;
        var old = KismetOps.Offsets(a, src.ScriptBytecode, out _);
        var now = KismetOps.Offsets(a, fn.ScriptBytecode, out _);
        var remap = new Dictionary<long, long>();
        for (int j = 0; j < spec.Indices.Length; j++) remap[old[spec.Indices[j]]] = now[j + 1];
        int constants = 0;
        foreach (var q in KismetOps.Find<EX_StructConst>(fn.ScriptBytecode))
        {
            Require(q.Struct.Index == -959, "unexpected struct const");
            var vals = q.Value;
            Require(((EX_NameConst)vals[2]).Value.ToString() == "ExecuteUbergraph_Stage5x5", "unexpected latent target");
            var skip = (EX_SkipOffsetConst)vals[0];
            skip.Value = (uint)remap[skip.Value];
            var id = (EX_IntConst)vals[1];
            id.Value = spec.UuidFor(id.Value);
            ((EX_NameConst)vals[2]).Value = Names.Make(a, p.FunctionName);
            constants++;
        }
        Require(constants > 0, "latent constant missing");

        // No-argument lever delegate, guarded per level instance, before scheduling.
        var w = CallbackBuilder.AddCallback(a, 0, 0, 0, new HashSet<int>(), spec.StartLabel, new List<int>(), wid);
        int sid = a.Exports.Count + 1;
        var state = (PropertyExport)DeepClone.Of(a.Export(2562), a.Raw);
        state.ObjectName = Names.Make(a, spec.StateFieldName);
        state.OuterIndex = new FPackageIndex(2);
        state.Property.PropertyFlags = EPropertyFlags.CPF_Transient;
        state.SerializationBeforeSerializationDependencies = new List<FPackageIndex>();
        state.CreateBeforeSerializationDependencies = new List<FPackageIndex>();
        state.SerializationBeforeCreateDependencies = new List<FPackageIndex>();
        state.CreateBeforeCreateDependencies = new List<FPackageIndex> { new(2) };
        a.Exports.Add(state); a.Raw.DependsMap.Add(new[] { 2 }); a.InvalidateIndex();
        w.StateFields = new List<int> { sid };
        var cls = (ClassExport)a.Export(2);
        cls.Children = new[] { new FPackageIndex(sid) }.Concat(cls.Children).ToArray();
        cls.SerializationBeforeSerializationDependencies.Add(new FPackageIndex(sid));
        KismetExpression Var() => new EX_InstanceVariable { Variable = new KismetPropertyPointer { Old = new FPackageIndex(sid) } };
        KismetExpression Ret() => new EX_Return { ReturnExpression = new EX_Nothing() };
        var code = new KismetExpression[]
        {
            new EX_JumpIfNot { CodeOffset = 0, BooleanExpression = Var() },
            Ret(),
            new EX_LetBool { VariableExpression = Var(), AssignmentExpression = new EX_True() },
            new EX_LocalFinalFunction { StackNode = new FPackageIndex(p.Function), Parameters = new KismetExpression[] { new EX_IntConst { Value = (int)remap[spec.StartEntryKey] } } },
            Ret(),
            new EX_EndOfScript(),
        };
        ((EX_JumpIfNot)code[0]).CodeOffset = (uint)(code[0].GetSize(a.Raw) + code[1].GetSize(a.Raw));
        var wf = (FunctionExport)a.Export(w.Function);
        wf.ScriptBytecode = code;
        wf.ScriptBytecodeSize = (int)KismetOps.Size(a, code);
        wf.CreateBeforeSerializationDependencies = new List<FPackageIndex> { new(sid), new(p.Function) };
        DonorCallbacks.AddNames(a, state.ObjectName.ToString(), p.FunctionName, w.FunctionName);

        var proofCdo = Props.Of(a.Export(3));
        var refs = p.SourceActorPropertiesRetained.Select(i => ((ObjectPropertyData)proofCdo[a.ObjectName(i)]).Value.Index).ToHashSet();
        if (spec.ExpectedActors != null) Require(refs.SetEquals(spec.ExpectedActors), "unexpected latent actor references");
        var imp = DonorCallbacks.ImportMapper(a, data);
        // The ordinary callback integrator predates latent struct constants; EX_StructConst.Struct is remapped too.
        var rp = CallbackBuilder.Integrate(data, a, p, refs.ToDictionary(i => i, i => DonorCallbacks.FindActor(data, spec.ActorPrefix, i)), imp, remapStructConst: true);
        CallbackBuilder.Integrate(data, a, w, new Dictionary<int, int> { [p.Function] = rp.Function }, imp, remapStructConst: true);
        DonorCallbacks.Bind(data, DonorCallbacks.FindActor(data, spec.ActorPrefix, spec.BindOwner), "PullRever", w.FunctionName);
        Compose.UpdateGenerations(data);
    }
}
