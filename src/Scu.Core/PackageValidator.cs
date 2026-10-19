using System.Text.RegularExpressions;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Scu.Core;

/// <summary>What a finished package must contain (derived from the requested stages).</summary>
public sealed record PackageExpectation(
    IReadOnlyCollection<string> ImportedRootPrefixes,   // e.g. "SCMix_Stage5_X", "SCMix_Extra_T": every listed number must have exactly one root
    IReadOnlyCollection<string> ImportedRootNames);     // full root suffixes, e.g. "SCMix_Stage5_X3_*_ArireRoot_X3"

/// <summary>
/// Structural checks every package has to pass before it is written. These are the checks of the Python builder that
/// still matter in retain mode (reference ranges, unique identities, class/function tables, bytecode jump targets,
/// room presence); the replace-mode checks that the SCU patches switched off are not repeated.
/// </summary>
public static class PackageValidator
{
    sealed class Problems
    {
        public readonly List<string> Items = new();
        public void Add(string text) { if (Items.Count < 25) Items.Add(text); else Overflow++; }
        public int Overflow;
    }

    public static void Validate(Asset a, string label, IEnumerable<string> expectedRoots)
    {
        var problems = new Problems();
        CheckReferences(a, problems);
        CheckIdentities(a, problems);
        CheckLevel(a, problems);
        CheckClassTables(a, problems);
        CheckBytecode(a, problems);
        CheckRooms(a, expectedRoots, problems);
        if (problems.Items.Count > 0)
            throw new BuildException("validation_failed",
                $"{label}: 패키지 검증에 실패했습니다 ({problems.Items.Count + problems.Overflow}건): " + string.Join(" | ", problems.Items.Take(6)));
    }

    // ---- 1. every package index and the dependency table are in range ----

    static void CheckReferences(Asset a, Problems problems)
    {
        int exports = a.Exports.Count, imports = a.Imports.Count;
        if (a.Raw.DependsMap.Count != exports) problems.Add($"DependsMap 길이 {a.Raw.DependsMap.Count} != export 수 {exports}");
        for (int i = 1; i <= exports; i++)
        {
            foreach (int v in HostComposer.CollectRefs(a.Exports[i - 1]))
                if (v < -imports || v > exports) problems.Add($"export {i} {a.Exports[i - 1].ObjectName}: 참조 범위 밖 {v}");
            var depends = a.Raw.DependsMap[i - 1];
            if (depends != null)
                foreach (int v in depends)
                    if (v < -imports || v > exports) problems.Add($"export {i}: DependsMap 범위 밖 {v}");
        }
        for (int i = 1; i <= imports; i++)
        {
            int outer = a.Imports[i - 1].OuterIndex.Index;
            if (outer < -imports || outer > 0) problems.Add($"import {-i}: 바깥 참조 범위 밖 {outer}");
        }
    }

    // ---- 2. (outer, name) is unique ----

    static void CheckIdentities(Asset a, Problems problems)
    {
        var seen = new Dictionary<(int, string), int>();
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            var key = (a.Exports[i - 1].OuterIndex.Index, a.Exports[i - 1].ObjectName.ToString());
            if (!seen.TryAdd(key, i)) problems.Add($"중복 객체 이름 {key.Item2} (export {seen[key]}, {i})");
        }
    }

    // ---- 3. the persistent level ----

    static void CheckLevel(Asset a, Problems problems)
    {
        LevelCheck level;
        try { level = LevelChecks.Validate(a, anyHostClass: true); }
        catch (Exception e) { problems.Add("레벨 검사 실패: " + e.Message); return; }
        var lv = (LevelExport)a.Export(level.Level);
        var live = lv.Actors.Where(p => p.Index != 0).Select(p => p.Index).ToList();
        if (live.Count != live.Distinct().Count()) problems.Add("레벨 액터 목록에 같은 액터가 중복되어 있습니다");
        foreach (int actor in live)
        {
            if (actor < 0 || actor > a.Exports.Count) { problems.Add($"레벨 액터 범위 밖 {actor}"); continue; }
            if (a.Export(actor).OuterIndex.Index != level.Level) problems.Add($"레벨 액터 {actor}({a.Export(actor).ObjectName})의 소속 레벨이 다릅니다");
        }
    }

    // ---- 4. class children and the function map ----

    static void CheckClassTables(Asset a, Problems problems)
    {
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            if (a.Exports[i - 1] is not ClassExport cls) continue;
            foreach (var child in cls.Children)
            {
                int c = child.Index;
                if (c <= 0 || c > a.Exports.Count) { problems.Add($"클래스 {cls.ObjectName}의 자식 참조가 올바르지 않습니다 ({c})"); continue; }
                if (a.Export(c).OuterIndex.Index != i) problems.Add($"클래스 {cls.ObjectName}의 자식 {a.Export(c).ObjectName}의 소속이 다릅니다");
            }
            var names = new HashSet<string>();
            foreach (var key in cls.FuncMap.Keys)
            {
                var fn = cls.FuncMap[key].Index;
                var name = key.ToString();
                if (!names.Add(name)) problems.Add($"클래스 {cls.ObjectName}의 함수 표에 {name} 이(가) 중복되어 있습니다");
                if (fn <= 0 || fn > a.Exports.Count || a.Export(fn) is not FunctionExport) { problems.Add($"함수 표 {name} 이(가) 함수를 가리키지 않습니다"); continue; }
                if (a.Export(fn).ObjectName.ToString() != name) problems.Add($"함수 표 {name} 이(가) 이름이 다른 함수 {a.Export(fn).ObjectName} 를 가리킵니다");
            }
        }
    }

    // ---- 5. bytecode: recorded size and jump targets ----

    static void CheckBytecode(Asset a, Problems problems)
    {
        for (int i = 1; i <= a.Exports.Count; i++)
        {
            if (a.Exports[i - 1] is not FunctionExport fn || fn.ScriptBytecode == null || fn.ScriptBytecode.Length == 0) continue;
            var offsets = KismetOps.Offsets(a, fn.ScriptBytecode, out long total);
            if (total != fn.ScriptBytecodeSize) problems.Add($"함수 {fn.ObjectName}: 바이트코드 크기 기록 {fn.ScriptBytecodeSize} != 계산 {total}");
            var boundaries = new HashSet<long>(offsets) { total };
            foreach (var statement in fn.ScriptBytecode)
            {
                long? target = statement switch
                {
                    EX_Jump j => j.CodeOffset,
                    EX_JumpIfNot j => j.CodeOffset,
                    EX_PushExecutionFlow p => p.PushingAddress,
                    _ => null,
                };
                if (target is long t && !boundaries.Contains(t)) problems.Add($"함수 {fn.ObjectName}: 점프 목적지 {t} 가 명령 경계가 아닙니다");
            }
        }
    }

    // ---- 6. the requested rooms exist exactly once ----

    static readonly Regex RootName = new(@"^(?<prefix>SCMix_(?:Stage5|Stage6|Extra|Cicada)_(?:LT_)?)(?<district>[LTX]\d+)_\d+_ArireRoot_[LTX]\d+(?:_\d+)?$", RegexOptions.Compiled);

    static void CheckRooms(Asset a, IEnumerable<string> expectedRoots, Problems problems)
    {
        var found = new Dictionary<string, int>();
        foreach (var e in a.Exports)
        {
            var m = RootName.Match(e.ObjectName.ToString());
            if (!m.Success) continue;
            var key = m.Groups["prefix"].Value + m.Groups["district"].Value;
            found[key] = found.GetValueOrDefault(key) + 1;
        }
        foreach (var expected in expectedRoots)
        {
            int n = found.GetValueOrDefault(expected);
            if (n != 1) problems.Add($"방 {expected}: 루트 {n}개 (정확히 1개여야 합니다)");
        }
    }

    static readonly (string Prefix, string Stage)[] PrefixStages =
        { ("SCMix_Stage6_", "sanctuary"), ("SCMix_Stage5_", "soul"), ("SCMix_Extra_", "abyss") };

    /// <summary>
    /// Room roots a map built from these stages must contain, as "prefix+district" keys. A base map keeps its own
    /// stage's rooms natively (SCApp_Host_), so those are not imported again and are not expected under the donor prefix.
    /// </summary>
    public static List<string> ExpectedRoots(IEnumerable<string> stages, string baseStage)
    {
        var stageSet = stages.ToHashSet();
        var roots = new List<string>();
        var lt = LtBundles.AllDistricts;
        if (stageSet.Contains("sanctuary"))
        {
            roots.AddRange(Enumerable.Range(1, 11).Select(n => "SCMix_Stage6_X" + n));
            roots.AddRange(lt.Select(n => "SCMix_Stage6_LT_" + n));
        }
        if (stageSet.Contains("soul")) roots.AddRange(Enumerable.Range(1, 10).Select(n => "SCMix_Stage5_X" + n));
        if (stageSet.Contains("abyss")) roots.AddRange(Enumerable.Range(1, 10).Select(n => "SCMix_Extra_X" + n));
        if (stageSet.Contains("soul-lt")) roots.AddRange(lt.Select(n => "SCMix_Stage5_" + n));
        if (stageSet.Contains("abyss-lt")) roots.AddRange(lt.Select(n => "SCMix_Extra_" + n));
        foreach (var (prefix, stage) in PrefixStages)
            if (stage == baseStage) roots.RemoveAll(r => r.StartsWith(prefix, StringComparison.Ordinal));
        return roots;
    }
}
