using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scu.Core;

public sealed record BuildOptions(string Randomizer, string Game, string Out, string[] Stages, string[] Bases, string[] Profiles,
    string? Work = null, bool KeepWork = false, int Parallel = 1, bool Validate = true, bool StrictVerify = false, string? Cache = null);

public sealed class BuildException : Exception
{
    public string Code { get; }
    public BuildException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// The whole map build (scu_map_patch.main / run_build / build_maps in one process): merge the donor districts into
/// the Cicada Corridor package, add them to each requested base map, apply the district look, save, pack and verify.
/// Progress goes to stdout as one JSON object per line, like the Python builder, so the SCU app can keep using it.
/// </summary>
public static class Builder
{
    public static readonly string[] AllStages = { "sanctuary", "soul", "abyss", "soul-lt", "abyss-lt" };
    static readonly string[] SupportedBases = { "cicada", "soul", "abyss", "sanctuary" };
    public const string PakName = "Kageroh-SCUMapShuffle_0_P.pak";
    public const string ReleaseSupported = "0.30.21";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    static readonly object ConsoleLock = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Emit(string kind, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["event"] = kind, ["t"] = Math.Round(Clock.Elapsed.TotalSeconds, 1) };
        foreach (var (k, v) in fields) o[k] = JsonSerializer.SerializeToNode(v, JsonOptions);
        lock (ConsoleLock)
        {
            Console.Out.WriteLine(o.ToJsonString());
            Console.Out.Flush();
        }
    }

    sealed record OpResult((string Base, string Profile) Op, string Package, string Umap, string Uexp, LookSummary? Look);

    /// <summary>Packages are big; give memory back between stages instead of waiting for the runtime to get around to it.</summary>
    static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        AssetCache.Clear();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    static void Progress(string stage, string message, int? percent, string? @base = null)
    {
        var fields = new List<(string, object?)> { ("stage", stage), ("message", message), ("percent", percent) };
        if (@base != null) fields.Add(("base", @base));
        Emit("progress", fields.ToArray());
    }

    public static int Run(BuildOptions o)
    {
        var started = Stopwatch.StartNew();
        string pakOut = o.Out;
        try
        {
            Emit("start", ("randomizer", o.Randomizer), ("out", o.Out));
            Directory.CreateDirectory(o.Out);
            // with a cache folder, everything made from the user's game (extracted maps, prepared maps, rules pak) lives there, so replacing the randomizer data does not touch it
            var shipped = Path.GetFullPath(o.Randomizer);
            if (o.Cache != null)
            {
                var cacheRoot = Path.GetFullPath(o.Cache);
                Directory.CreateDirectory(cacheRoot);
                Prepare.SyncShipped(shipped, cacheRoot);
                o = o with { Randomizer = cacheRoot };
            }
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(o.Out))!).AvailableFreeSpace;
            if (free < 3L * 1024 * 1024 * 1024) throw new BuildException("disk_space", "빈 공간이 3GB 미만입니다.");
            Validate(o);

            // clear the work folder first: a leftover from an earlier builder must not make the build fail minutes later
            var work = Path.GetFullPath(o.Work ?? Path.Combine(o.Out, "work"));
            try { FsUtil.DeleteTreeWithRetry(work); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                work = work + "-" + Environment.ProcessId;   // cannot clear the old one: use a fresh folder next to it
                FsUtil.DeleteTreeWithRetry(work);
            }
            Directory.CreateDirectory(work);
            var inputs = CheckInputs(o, shipped);
            var cache = Path.Combine(work, "cache");

            // ---- merge (composer.build) ----
            Progress("merge", "구역 묶음과 참조 검사", 15);
            var rtm = inputs.MapTiles;
            var (data, composePlan) = Merge(o, inputs, cache);
            double validateSeconds = 0;
            var timing = new object();
            void Check(Asset package, string label, string baseStage)
            {
                if (!o.Validate) return;
                var sw = Stopwatch.StartNew();
                PackageValidator.Validate(package, label, PackageValidator.ExpectedRoots(o.Stages, baseStage));
                lock (timing) validateSeconds += sw.Elapsed.TotalSeconds;
            }
            Progress("validate", "병합본 검증", 60);
            Check(data, "병합본", "cicada");

            // ---- per base map ----
            DifficultyRules? rules = null;
            if (o.Profiles.Contains("transcendent")) { rules = DifficultyRules.Load(o.Randomizer); rules.CheckIntegrity(); }
            var ops = MapOperations(o.Bases, o.Profiles, rules?.ManagesCicada ?? false);
            var cicadaOwn = ops.Where(op => op.Base == "cicada").Select(op => op.Profile).ToHashSet();
            var packFiles = new Dictionary<string, Dictionary<string, string>>();
            var looks = new JsonObject();
            int done = 0;
            int degree = Math.Clamp(o.Parallel, 1, 4);

            OpResult RunOp((string Base, string Profile) op)
            {
                var (baseStage, profile) = op;
                var package = HostComposer.Assets[baseStage];
                var dir = Path.Combine(work, "maps", profile, baseStage);
                int percent = 62 + (int)(32.0 * Volatile.Read(ref done) / ops.Count);
                Progress("compose", $"{baseStage} compose", percent, baseStage);
                LookSummary? look;
                Asset final;
                List<NativeEntry>? native = null;
                if (baseStage == "cicada" && profile == "original")
                {
                    Progress("look", $"{baseStage} look", percent + 5, baseStage);
                    (final, look) = DistrictLook.Apply(data, "cicada");
                }
                else
                {
                    var baseline = Asset.Load(profile == "original" ? Path.Combine(rtm, package + ".umap") : rules!.BaselineUmap(package, cache));
                    var (transplanted, nat, _) = RetainHost.Transplant(data, baseStage, baseline, o.Randomizer);
                    Progress("look", $"{baseStage} look", percent + 5, baseStage);
                    (final, look) = DistrictLook.Apply(transplanted, baseStage);
                    native = RetainHost.Native(final);
                }
                Check(final, baseStage + (profile == "original" ? "" : ":" + profile), baseStage);
                Progress("save", $"{baseStage} save", percent + 8, baseStage);
                var saved = HostSerialization.Serialize(final, native, dir, package, strict: o.StrictVerify);
                Interlocked.Increment(ref done);
                final = null!; native = null;
                if (degree == 1) ReleaseMemory();
                return new OpResult(op, package, saved.Umap, saved.Uexp, look);
            }

            // Operations that only read the merged package can run side by side; the Cicada map edits it in place and runs last.
            var independent = ops.Where(op => !(op.Base == "cicada" && op.Profile == "original")).ToList();
            var results = new Dictionary<(string, string), OpResult>();
            var gate = new object();
            Parallel.ForEach(independent, new ParallelOptions { MaxDegreeOfParallelism = degree }, op =>
            {
                var r = RunOp(op);
                lock (gate) results[op] = r;
            });
            foreach (var op in ops.Where(op => op.Base == "cicada" && op.Profile == "original")) results[op] = RunOp(op);
            ReleaseMemory();

            foreach (var op in ops)   // assemble in the fixed operation order
            {
                var r = results[op];
                var (baseStage, profile) = op;
                // The plain Cicada map does not depend on the difficulty profile unless the rules manage it themselves.
                var targets = baseStage == "cicada" && profile == "original" ? o.Profiles.Where(p => p == "original" || !cicadaOwn.Contains(p)).ToArray() : new[] { profile };
                foreach (var target in targets)
                {
                    if (!packFiles.TryGetValue(target, out var files)) packFiles[target] = files = new();
                    files[PakTools.MapDirectory + r.Package + ".umap"] = r.Umap;
                    files[PakTools.MapDirectory + r.Package + ".uexp"] = r.Uexp;
                }
                var look = r.Look;
                looks[profile == "original" ? baseStage : baseStage + ":" + profile] = look == null ? null : JsonSerializer.SerializeToNode(new
                {
                    actors = look.Actors, byStyle = look.ByStyle, changedDoorLeaves = look.ChangedDoorLeaves,
                    changedDoorWalls = look.ChangedDoorWalls, changedFurniture = look.ChangedFurniture,
                    runtimeTargets = look.RuntimeTargets, delay = look.Delay,
                });
            }

            // ---- pack and verify ----
            Progress("pack", "pak 만드는 중", 95);
            var made = new JsonObject();
            var members = new List<string>();
            foreach (var (profile, files) in packFiles)
            {
                var pak = Path.Combine(work, "maps", profile, "Kageroh-MixedSelector_0_P.pak");
                PakTools.PackMaps(o.Randomizer, pak, files, Path.Combine(work, "maps", profile, "s"));
                Progress("verify", "결과 pak 검사", 97);
                VerifyOnlyModified(o, pak, files.Keys);
                var finalPak = Path.Combine(o.Out, profile == "original" ? PakName : "Kageroh-SCUMapShuffleT_0_P.pak");
                File.Copy(pak, finalPak, overwrite: true);
                made[profile] = new JsonObject { ["pak"] = finalPak, ["sha256"] = Sha(finalPak), ["size"] = new FileInfo(finalPak).Length };
                members = files.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            }

            JsonNode? difficultyPak = null, difficultyMapsPak = null;
            if (rules != null)
            {
                Progress("difficulty", "초월자 난이도 에셋 만드는 중", 98);
                var (dp, dm) = rules.BuildPaks(o.Out, work);
                difficultyPak = JsonSerializer.SerializeToNode(new { pak = dp.Pak, sha256 = dp.Sha256, size = dp.Size, members = dp.Members, rulesRevision = dp.RulesRevision });
                difficultyMapsPak = JsonSerializer.SerializeToNode(new { pak = dm.Pak, sha256 = dm.Sha256, size = dm.Size, members = dm.Members, rulesRevision = dm.RulesRevision });
            }
            var original = (JsonObject)made["original"]!;
            var rooms = new JsonObject
            {
                ["stages"] = JsonSerializer.SerializeToNode(o.Stages),
                ["retainedSanctuaryX"] = JsonSerializer.SerializeToNode(o.Stages.Contains("sanctuary") ? Enumerable.Range(1, 11) : Array.Empty<int>()),
                ["retainedSanctuaryLT"] = JsonSerializer.SerializeToNode(o.Stages.Contains("sanctuary") ? LtBundles.AllDistricts : Array.Empty<string>()),
                ["retainedSoulX"] = JsonSerializer.SerializeToNode(o.Stages.Contains("soul") ? Enumerable.Range(1, 10) : Array.Empty<int>()),
                ["retainedAbyssX"] = JsonSerializer.SerializeToNode(o.Stages.Contains("abyss") ? Enumerable.Range(1, 10) : Array.Empty<int>()),
                ["retainedSoulLT"] = JsonSerializer.SerializeToNode(o.Stages.Contains("soul-lt") ? LtBundles.AllDistricts : Array.Empty<string>()),
                ["retainedAbyssLT"] = JsonSerializer.SerializeToNode(o.Stages.Contains("abyss-lt") ? LtBundles.AllDistricts : Array.Empty<string>()),
                ["hostSlots"] = JsonSerializer.SerializeToNode(composePlan.HostSlots),
            };
            var result = new JsonObject
            {
                ["ok"] = true, ["pak"] = original["pak"]!.DeepClone(), ["sha256"] = original["sha256"]!.DeepClone(), ["size"] = original["size"]!.DeepClone(),
                ["paks"] = made, ["difficultyPak"] = difficultyPak, ["difficultyMapsPak"] = difficultyMapsPak,
                ["members"] = JsonSerializer.SerializeToNode(members), ["randomizerRelease"] = ReleaseSupported, ["rooms"] = rooms,
                ["bases"] = JsonSerializer.SerializeToNode(o.Bases), ["workerStats"] = new JsonArray(), ["districtLook"] = looks,
                ["seconds"] = Math.Round(started.Elapsed.TotalSeconds, 1), ["validated"] = o.Validate, ["validateSeconds"] = Math.Round(validateSeconds, 1),
            };
            File.WriteAllText(Path.Combine(o.Out, "result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.Out.WriteLine(new JsonObject(result.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))) { ["event"] = "done" }.ToJsonString());
            Console.Out.Flush();
            if (!o.KeepWork) try { FsUtil.DeleteTreeWithRetry(work); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return 0;
        }
        catch (BuildException e)
        {
            Fail(o, e.Code, e.Message);
            return 1;
        }
        catch (Exception e)
        {
            Fail(o, "build_failed", e.GetType().Name + ": " + e.Message, e.ToString());
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    static void Fail(BuildOptions o, string code, string message, string? trace = null)
    {
        var result = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        if (trace != null) result["trace"] = trace.Length > 4000 ? trace[..4000] : trace;
        try { File.WriteAllText(Path.Combine(o.Out, "result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { }
        result["event"] = "error";
        Console.Out.WriteLine(result.ToJsonString());
        Console.Out.Flush();
    }

    static void Validate(BuildOptions o)
    {
        if (o.Stages.Length == 0 || o.Stages.Any(s => !AllStages.Contains(s))) throw new BuildException("invalid_stages", $"지원하지 않는 스테이지: {string.Join(',', o.Stages)}");
        if (o.Bases.Any(b => !SupportedBases.Contains(b))) throw new BuildException("invalid_stages", $"지원하지 않는 기본 맵: {string.Join(',', o.Bases)}");
        if (o.Profiles.Length == 0 || !o.Profiles.Contains("original") || o.Profiles.Any(p => p is not ("original" or "transcendent")))
            throw new BuildException("invalid_stages", $"지원하지 않는 난이도 기준: {string.Join(',', o.Profiles)}");
    }

    // ---- prepared inputs ----

    sealed record Inputs(string Root, string MapTiles, string Game, string BasePak);

    static Inputs CheckInputs(BuildOptions o, string shipped)
    {
        var root = Path.GetFullPath(o.Randomizer);
        var rtm = Path.Combine(root, "extracted/Kageroh/Content/Levels/RundomMapTiles");
        var basePak = Path.Combine(o.Game, "Kageroh/Content/Paks/Kageroh-WindowsNoEditor.pak");
        foreach (var required in new[] { Path.Combine(root, "tools/repak-0.2.3/repak.exe"), basePak, Path.Combine(root, "portable-spec.json") })
            if (!File.Exists(required)) throw new BuildException("randomizer_incomplete", $"필요한 파일을 찾을 수 없습니다: {required}");
        using (var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "portable-spec.json"))))
        {
            var release = spec.RootElement.TryGetProperty("release", out var r) ? r.GetString() : null;
            if (release != ReleaseSupported) throw new BuildException("unsupported_release", $"지원하지 않는 랜더마이저 버전입니다: {release}");
        }
        bool legacyReady = HostComposer.Assets.Values.All(p => File.Exists(Path.Combine(rtm, p + ".umap")) && File.Exists(Path.Combine(rtm, p + ".uexp")))
            && File.Exists(Path.Combine(root, Prepare.SanctuaryAllRel)) && File.Exists(Path.Combine(root, Prepare.SoulX1Rel))
            && File.Exists(Path.Combine(root, "app/updates/sanctuary-LT/all/prepared-donor.json")) && File.Exists(Path.Combine(root, "app/updates/sanctuary-LT/all/parsed.json"));
        if (!Prepare.IsPrepared(root) && !legacyReady)
        {
            int n = 0;
            Prepare.Run(root, o.Game, note => Progress("prepare", note, Math.Min(40, 4 + ++n)), shipped);
        }
        var required2 = new List<string>
        {
            Path.Combine(root, "app/updates/sanctuary-LT/all/event-plan.json"), Path.Combine(root, "app/updates/sanctuary-LT/all/donor-preflight.json"),
            Path.Combine(root, "app/soul-lt-room.json"),
        };
        required2.AddRange(HostComposer.Assets.Values.SelectMany(p => new[] { Path.Combine(rtm, p + ".umap"), Path.Combine(rtm, p + ".uexp") }));
        var missing = required2.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0) throw new BuildException("not_prepared", "준비 자료가 없습니다: " + string.Join(", ", missing.Take(3).Select(Path.GetFileName)));
        return new Inputs(root, rtm, o.Game, basePak);
    }

    // ---- merge ----

    static (Asset Data, ComposePlan Plan) Merge(BuildOptions o, Inputs inputs, string cache)
    {
        var root = inputs.Root;
        var retainDonors = o.Stages.Contains("sanctuary") ? Enumerable.Range(1, 11) : Enumerable.Empty<int>();
        var (data, native, plan) = Compose.Run(
            Asset.Load(Prepare.Resolve(root, "sanctuary-all-X-trapstatefix/Random5x5.umap", Prepare.SanctuaryAllRel) ?? throw new BuildException("not_prepared", "성역 준비 맵이 없습니다.")),
            Asset.Load(Path.Combine(inputs.MapTiles, "Random5x5.umap")), Array.Empty<int>(), retainDonors);
        var lt = LtBundles.AllDistricts;
        var identity = lt.ToDictionary(n => n, n => n);
        var tenth = Enumerable.Range(1, 10).ToHashSet();
        if (o.Stages.Contains("abyss"))
        {
            Progress("merge", "심연 구역 추가", 25);
            AbyssBundles.Inject(data, Path.Combine(inputs.MapTiles, "Extra5x5.umap"), tenth, tenth.ToDictionary(n => n, n => n), retain: true);
        }
        if (o.Stages.Contains("soul"))
        {
            ReleaseMemory();
            Progress("merge", "영혼 집합소 구역 추가", 35);
            SoulBundles.Inject(data, new SoulPaths(root), tenth, BundleInjector.SoulSlots, retain: true);
        }
        if (o.Stages.Contains("soul-lt"))
        {
            ReleaseMemory();
            Progress("merge", "영혼 2·3갈래 추가", 45);
            LtStages.InjectSoul(data, new SoulPaths(root), lt, identity, retain: true);
        }
        if (o.Stages.Contains("abyss-lt"))
        {
            ReleaseMemory();
            Progress("merge", "심연 2·3갈래 추가", 52);
            LtStages.InjectAbyss(data, Path.Combine(inputs.MapTiles, "Extra5x5.umap"), lt, identity, retain: true);
        }
        if (o.Stages.Contains("sanctuary"))
        {
            ReleaseMemory();
            Progress("merge", "성역 구역·기믹 연결 검사", 58);
            SanctuaryLt.Inject(data, new SanctuaryLtPaths(root, cache), lt, identity, retain: true);
        }
        // the transplants read this package from several threads: build every lazily created lookup now
        data.Closure(Array.Empty<int>());
        data.Raw.GetNameMapIndexList();
        data.Raw.ContainsNameReference(new UAssetAPI.UnrealTypes.FString("None"));
        ReleaseMemory();
        return (data, plan);
    }

    // ---- operations ----

    static List<(string Base, string Profile)> MapOperations(string[] bases, string[] profiles, bool cicadaPerProfile)
    {
        var list = new List<(string, string)>();
        var all = new[] { "cicada" }.Concat(bases.Where(b => b != "cicada")).ToList();
        foreach (var b in all)
        {
            if (b == "cicada")
            {
                list.Add(("cicada", "original"));
                if (cicadaPerProfile && profiles.Contains("transcendent")) list.Add(("cicada", "transcendent"));
            }
            else foreach (var p in profiles) list.Add((b, p));
        }
        return list;
    }

    // ---- verification against the installed game ----

    static string Sha(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    static void VerifyOnlyModified(BuildOptions o, string builtPak, IEnumerable<string> expected)
    {
        var builtList = PakTools.HashList(o.Randomizer, builtPak);
        if (!builtList.Keys.ToHashSet().SetEquals(expected))
            throw new BuildException("unexpected_members", "결과 pak의 파일 구성이 예상과 다릅니다: " + string.Join(", ", builtList.Keys));
        var basePak = Path.Combine(o.Game, "Kageroh/Content/Paks/Kageroh-WindowsNoEditor.pak");
        foreach (var (path, sha) in builtList)
        {
            var original = PakTools.ContentSha(o.Randomizer, basePak, path);
            if (original != null && original == sha)
                throw new BuildException("unmodified_asset", $"원본과 같은 에셋이 포함되어 있습니다(배포 금지): {path}");
        }
    }
}
