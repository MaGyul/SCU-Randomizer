using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;

namespace Scu.Core;

/// <summary>
/// abyss_callbacks.plan_for / soul_lt_callbacks.plan_for: in a fresh copy of the donor package, find the ubergraph statements
/// reachable from a handler wrapper's entry point and lift them into a function (<see cref="CallbackBuilder.AddCallback"/>).
/// </summary>
public static class HandlerPlanner
{
    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// flow_audit.cfg: ubergraph statements reachable from a handler wrapper's entry, in execution-start order
    /// (those at or after the entry first, then those before it). <paramref name="entry"/> is the entry statement.
    /// </summary>
    public static List<int> Reachable(Asset a, string uberName, string wrapperName, out int entry)
    {
        var uber = (FunctionExport)a.Exports.First(e => e.ObjectName.ToString() == uberName);
        var code = uber.ScriptBytecode;
        var offsets = KismetOps.Offsets(a, code, out _);
        var by = new Dictionary<long, int>();
        for (int i = 0; i < offsets.Count; i++) by[offsets[i]] = i;
        int wid = Enumerable.Range(1, a.Exports.Count).First(i => a.Exports[i - 1].ObjectName.ToString() == wrapperName);
        var calls = KismetOps.Find<EX_FinalFunction>(((FunctionExport)a.Export(wid)).ScriptBytecode)
            .Where(q => a.ObjectName(q.StackNode.Index) == uberName).ToList();
        Require(calls.Count == 1, "expected exactly one ubergraph call");
        entry = by[((EX_IntConst)calls[0].Parameters[0]).Value];
        var pending = new List<int> { entry };
        var seen = new HashSet<int>();
        while (pending.Count > 0)
        {
            int i = pending[^1]; pending.RemoveAt(pending.Count - 1);
            if (!seen.Add(i)) continue;
            var q = code[i];
            if (q is EX_Return or EX_EndOfScript or EX_PopExecutionFlow) continue;
            if (q is EX_Jump jump) { pending.Add(by[jump.CodeOffset]); continue; }
            if (q is EX_JumpIfNot jin) pending.Add(by[jin.CodeOffset]);
            if (q is EX_PushExecutionFlow push) pending.Add(by[push.PushingAddress]);
            Require(q is not EX_ComputedJump, "computed jump");
            pending.Add(i + 1);
        }
        int e0 = entry;
        return seen.Where(i => i >= e0).OrderBy(i => i).Concat(seen.Where(i => i < e0).OrderBy(i => i)).ToList();
    }

    public static (Asset Proof, CallbackPlan Plan) PlanFor(string umap, string uberName, string label, int owner, string eventName, string? wrapper)
    {
        var a = AssetCache.Take(umap);
        var uber = (FunctionExport)a.Exports.First(e => e.ObjectName.ToString() == uberName);
        var code = uber.ScriptBytecode;
        string wrapperName = wrapper ?? ((MulticastDelegatePropertyData)((NormalExport)a.Export(owner)).Data
            .First(p => p.Name.ToString() == eventName)).Value[0].Delegate.ToString();
        int wid = Enumerable.Range(1, a.Exports.Count).First(i => a.Exports[i - 1].ObjectName.ToString() == wrapperName);
        var indices = Reachable(a, uberName, wrapperName, out _);
        var pointers = indices.SelectMany(i => KismetOps.Find<KismetPropertyPointer>(code[i])).Select(p => p.Old.Index).Where(i => i > 0).ToHashSet();
        var states = pointers.Where(i =>
        {
            var n = a.Export(i).ObjectName.ToString();
            return n.StartsWith("Temp_bool_IsClosed", StringComparison.Ordinal) || n.StartsWith("Temp_bool_Has_Been_Initd", StringComparison.Ordinal);
        }).ToHashSet();
        var plan = CallbackBuilder.AddCallback(a, eventName == "OnActorBeginOverlap" ? owner : 0, indices.Min(), indices.Max() + 1,
            states, label, indices, wid, uberName);
        return (a, plan);
    }
}
