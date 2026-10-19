using System.Diagnostics;
using System.Security.Cryptography;
using Scu.Core;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

// scu roundtrip <input.umap> <output.umap>            umap -> UAsset -> umap, byte-for-byte compare
// scu dump-json <input.umap> <out.json> [shift]        UAssetAPI JSON of the asset, optionally after a deterministic index shift
// scu validate-level <input.umap>                      composer.validate_level
if (args.Length < 1 || (args.Length < 2 && args[0] != "build")) return Usage();

switch (args[0])
{
    case "roundtrip" when args.Length >= 3: return Roundtrip(args[1], args[2]);
    case "dump-json" when args.Length >= 3: return DumpJson(args[1], args[2], args.Length > 3 && args[3] == "shift");
    case "validate-level": return ValidateLevel(args[1]);
    case "inspect" when args.Length >= 3:
    {
        var a = Asset.Load(args[1]);
        var e = a.Exports[int.Parse(args[2]) - 1];
        Console.WriteLine($"{e.GetType().Name} {e.ObjectName} class={a.ObjectName(e.ClassIndex.Index)} serialOffset={e.SerialOffset} serialSize={e.SerialSize} extras={e.Extras?.Length} props={(e as UAssetAPI.ExportTypes.NormalExport)?.Data.Count}");
        return 0;
    }
    case "diagnose-write" when args.Length >= 6:
    {
        // transplant, write once, and report why exports degrade to RawExport on re-read
        var merged = Asset.Load(args[2]);
        var host = Asset.Load(args[3]);
        var (res, _, _) = RetainHost.Transplant(merged, args[1], host, args[4]);
        var path = Path.Combine(args[5], "diag.umap");
        Directory.CreateDirectory(args[5]);
        res.Raw.Write(path);
        var back = Asset.Load(path);
        var bad = back.Exports.Select((e, i) => (e, i)).Where(x => x.e is UAssetAPI.ExportTypes.RawExport && res.Exports[x.i] is not UAssetAPI.ExportTypes.RawExport).ToList();
        Console.WriteLine($"degraded exports: {bad.Count}");
        foreach (var (e, i) in bad.Take(3))
        {
            var before = res.Exports[i];
            Console.WriteLine($"#{i + 1} {before.GetType().Name} {before.ObjectName} serialSize before={before.SerialSize} after={e.SerialSize}");
            Console.WriteLine(res.Raw.SerializeJsonObject(before, Newtonsoft.Json.Formatting.Indented));
        }
        return 0;
    }
    case "build-base" when args.Length >= 6:
    {
        // scu build-base <base> <merged.umap> <host.umap> <randomizerRoot> <outDir>: transplant + save + pak
        string baseStage = args[1], root = args[4], outDir = args[5];
        var total = Stopwatch.StartNew();
        var (result, native, _) = RetainHost.Transplant(Asset.Load(args[2]), baseStage, Asset.Load(args[3]), root);
        var package = HostComposer.Assets[baseStage];
        var saved = HostSerialization.Serialize(result, native, Path.Combine(outDir, "map"), package);
        var pak = Path.Combine(outDir, "Kageroh-SCUMapShuffle_0_P.pak");
        var files = new Dictionary<string, string>
        {
            [PakTools.MapDirectory + package + ".umap"] = saved.Umap,
            [PakTools.MapDirectory + package + ".uexp"] = saved.Uexp,
        };
        var sha = PakTools.PackMaps(root, pak, files, Path.Combine(outDir, "stage"));
        Console.WriteLine($"pak {pak}  size={new FileInfo(pak).Length} sha256={sha}  total {total.Elapsed.TotalSeconds:F1}s");
        return 0;
    }
    case "compose" when args.Length >= 4:
    {
        // scu compose <base.umap (prepared all-Sanctuary Random5x5)> <host.umap (pristine Random5x5)> <out.json>
        var sw = Stopwatch.StartNew();
        var (data, native, plan) = Compose.Run(Asset.Load(args[1]), Asset.Load(args[2]), Array.Empty<int>(), Enumerable.Range(1, 11));
        Console.WriteLine($"compose {sw.Elapsed.TotalSeconds:F1}s exports={data.Exports.Count} native={native.Count} removed={plan.RemovedExports} added={plan.AddedExports} cleared={plan.ClearedCallbackFields.Count}");
        File.WriteAllText(args[3], data.Raw.SerializeJson(Newtonsoft.Json.Formatting.None));
        return 0;
    }
    case "stages" when args.Length >= 3:
    {
        // scu stages <randomizerRoot> <outDir> [upTo=abyss]: run the merge stages and dump each stage as JSON for comparison
        string root = args[1], outDir = args[2], upTo = args.Length > 3 ? args[3] : "abyss";
        Directory.CreateDirectory(outDir);
        string rtm = Path.Combine(root, "extracted/Kageroh/Content/Levels/RundomMapTiles");
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var (data, native, plan) = Compose.Run(
            Asset.Load(Path.Combine(root, "reports/tile-reference-swap/expansion-candidates/sanctuary-all-X-trapstatefix/Random5x5.umap")),
            Asset.Load(Path.Combine(rtm, "Random5x5.umap")), Array.Empty<int>(), Enumerable.Range(1, 11));
        Console.WriteLine($"compose  {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count}");
        void Dump(string name) { Names.Finalize(data, addMissing: true); File.WriteAllText(Path.Combine(outDir, name + ".json"), data.Raw.SerializeJson(Newtonsoft.Json.Formatting.None)); }
        if (upTo == "compose") { Dump("1-compose"); return 0; }
        sw.Restart();
        var abyss = Enumerable.Range(1, 10).ToHashSet();
        var (rows, callbacks) = AbyssBundles.Inject(data, Path.Combine(rtm, "Extra5x5.umap"), abyss, abyss.ToDictionary(n => n, n => n), retain: true);
        Console.WriteLine($"abyss    {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count} callbacks={callbacks.Count}");
        Dump("2-abyss");
        if (upTo == "abyss") { Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB"); return 0; }
        sw.Restart();
        var soul = Enumerable.Range(1, 10).ToHashSet();
        var soulRows = SoulBundles.Inject(data, new SoulPaths(root), soul, BundleInjector.SoulSlots, retain: true);
        Console.WriteLine($"soul     {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count} rows={soulRows.Count}");
        Dump("3-soul");
        if (upTo == "soul") { Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB"); return 0; }
        var lt = LtBundles.AllDistricts;
        var identity = lt.ToDictionary(n => n, n => n);
        sw.Restart();
        var (soulLtRows, soulLtMap, soulLtCallbacks) = LtStages.InjectSoul(data, new SoulPaths(root), lt, identity, retain: true);
        Console.WriteLine($"soul-lt  {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count} callbacks={soulLtCallbacks.Count}");
        Dump("4-soul-lt");
        if (upTo == "soul-lt") { Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s"); return 0; }
        sw.Restart();
        var (abyssLtRows, abyssLtMap, abyssLtCallbacks) = LtStages.InjectAbyss(data, Path.Combine(rtm, "Extra5x5.umap"), lt, identity, retain: true);
        Console.WriteLine($"abyss-lt {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count} callbacks={abyssLtCallbacks.Count}");
        Dump("5-abyss-lt");
        if (upTo == "abyss-lt") { Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB"); return 0; }
        sw.Restart();
        var cache = Path.Combine(Path.GetFullPath(outDir), "cache");
        var (sancRows, sancMap) = SanctuaryLt.Inject(data, new SanctuaryLtPaths(root, cache), lt, identity, retain: true);
        Console.WriteLine($"sanctuary-lt {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count}");
        Dump("6-sanctuary-lt");
        if (upTo == "sanctuary-lt") { Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB"); return 0; }
        sw.Restart();
        var (looked, summary) = DistrictLook.Apply(data, "cicada");
        Console.WriteLine($"look     {sw.Elapsed.TotalSeconds,6:F1}s exports={data.Exports.Count} summary={(summary == null ? "none" : $"actors={summary.Actors} leaves={summary.ChangedDoorLeaves} walls={summary.ChangedDoorWalls} furniture={summary.ChangedFurniture} targets={summary.RuntimeTargets}")}");
        Dump("7-cicada-look");
        Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
        return 0;
    }
    case "json2umap" when args.Length >= 3:
    {
        // scu json2umap <in.json> <out.umap>: convert a UAssetAPI JSON package to a binary package (and report timing)
        var sw = Stopwatch.StartNew();
        var asset = Asset.LoadJson(args[1]);
        Console.WriteLine($"json load {sw.Elapsed.TotalSeconds:F1}s exports={asset.Exports.Count} imports={asset.Imports.Count}");
        sw.Restart();
        Names.Finalize(asset);
        asset.Raw.Write(args[2]);
        Console.WriteLine($"write {sw.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
        return 0;
    }
    case "build":
    {
        // scu build --randomizer <dir> --game <dir> --out <dir> [--cache dir] [--stages a,b] [--bases cicada,..] [--profiles original] [--parallel n] [--work dir] [--keep-work]
        string Opt(string name, string? fallback = null)
        {
            int i = Array.IndexOf(args, name);
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            return fallback ?? throw new ArgumentException("missing " + name);
        }
        string[] List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var bases = new[] { "cicada" }.Concat(List(Opt("--bases", "cicada")).Where(b => b != "cicada")).ToArray();
        return Builder.Run(new BuildOptions(Opt("--randomizer"), Opt("--game"), Opt("--out"),
            List(Opt("--stages", string.Join(',', Builder.AllStages))), bases, List(Opt("--profiles", "original")),
            args.Contains("--work") ? Opt("--work") : null, args.Contains("--keep-work"), int.Parse(Opt("--parallel", "1")), !args.Contains("--no-validate"), args.Contains("--strict-verify"),
            args.Contains("--cache") ? Opt("--cache") : null));
    }
    case "prepare" when args.Length >= 3:
    {
        // scu prepare <randomizerRoot> <gameRoot>: extract the original maps and build the prepared donor packages
        var sw = Stopwatch.StartNew();
        Prepare.Run(args[1], args[2], n => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] {n}"));
        Console.WriteLine($"prepared in {sw.Elapsed.TotalSeconds:F1}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
        return 0;
    }
    case "serialize" when args.Length >= 6: return SerializeCmd(args[1], args[2], args[3], args[4], args[5]);
    case "transplant" when args.Length >= 6: return Transplant(args[1], args[2], args[3], args[4], args[5]);
    default: return Usage();
}

// scu transplant <base> <merged.umap> <host.umap> <randomizerRoot> <out.json>
static int Transplant(string baseStage, string mergedPath, string hostPath, string root, string outJson)
{
    var total = Stopwatch.StartNew();
    var sw = Stopwatch.StartNew();
    var merged = Asset.Load(mergedPath);
    var host = Asset.Load(hostPath);
    Console.WriteLine($"load       {sw.Elapsed.TotalSeconds,7:F2}s  merged={merged.Exports.Count} host={host.Exports.Count}");
    sw.Restart();
    var (result, native, proof) = RetainHost.Transplant(merged, baseStage, host, root);
    Console.WriteLine($"transplant {sw.Elapsed.TotalSeconds,7:F2}s  exports={result.Exports.Count} native={native.Count} renamed={proof.RenamedHostActors} imported={proof.ImportedActors}/{proof.AddedExports}");
    sw.Restart();
    File.WriteAllText(outJson, result.Raw.SerializeJson(Newtonsoft.Json.Formatting.None));
    Console.WriteLine($"json       {sw.Elapsed.TotalSeconds,7:F2}s  total={total.Elapsed.TotalSeconds:F2}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
    return 0;
}

// scu serialize <base> <merged.umap> <host.umap> <randomizerRoot> <outDir>: transplant then two-pass save + verification
static int SerializeCmd(string baseStage, string mergedPath, string hostPath, string root, string outDir)
{
    var total = Stopwatch.StartNew();
    var merged = Asset.Load(mergedPath);
    var host = Asset.Load(hostPath);
    var sw = Stopwatch.StartNew();
    var (result, native, proof) = RetainHost.Transplant(merged, baseStage, host, root);
    Console.WriteLine($"transplant {sw.Elapsed.TotalSeconds,7:F2}s");
    sw.Restart();
    var saved = HostSerialization.Serialize(result, native, outDir, HostComposer.Assets[baseStage]);
    Console.WriteLine($"serialize  {sw.Elapsed.TotalSeconds,7:F2}s");
    foreach (var (name, sha) in saved.Members) Console.WriteLine($"  {name} {sha}");
    Console.WriteLine($"total      {total.Elapsed.TotalSeconds,7:F2}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: scu roundtrip <in.umap> <out.umap> | dump-json <in.umap> <out.json> [shift] | validate-level <in.umap>");
    return 2;
}

static int Roundtrip(string input, string output)
{
    var total = Stopwatch.StartNew();
    var sw = Stopwatch.StartNew();
    var asset = new UAsset(input, EngineVersion.VER_UE4_22);
    Console.WriteLine($"load      {sw.Elapsed.TotalSeconds,7:F2}s  exports={asset.Exports.Count} imports={asset.Imports.Count}");
    sw.Restart();
    asset.Write(output);
    Console.WriteLine($"write     {sw.Elapsed.TotalSeconds,7:F2}s");
    bool umapSame = Sha(input) == Sha(output);
    bool uexpSame = Sha(Path.ChangeExtension(input, ".uexp")) == Sha(Path.ChangeExtension(output, ".uexp"));
    Console.WriteLine($"compare   umap={(umapSame ? "SAME" : "DIFF")} uexp={(uexpSame ? "SAME" : "DIFF")}");
    Console.WriteLine($"total     {total.Elapsed.TotalSeconds,7:F2}s  peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB");
    return umapSame && uexpSame ? 0 : 1;
}

static int DumpJson(string input, string output, bool shift)
{
    var a = Asset.Load(input);
    if (shift)
    {
        // deterministic remap used to compare against the Python rewrite_normal/rewrite_extra
        static int Fn(int v) => v > 0 ? v + 7 : v < 0 ? v - 3 : 0;
        var sw = Stopwatch.StartNew();
        foreach (var e in a.Exports) { Refs.RewriteNormal(e, Fn, Refs.AllowedStreamingWorld); Refs.RewriteExtra(e, Fn); }
        Console.WriteLine($"rewrite   {sw.Elapsed.TotalSeconds,7:F2}s  exports={a.Exports.Count}");
    }
    File.WriteAllText(output, a.Raw.SerializeJson(Newtonsoft.Json.Formatting.None));
    return 0;
}

static int ValidateLevel(string input)
{
    var check = LevelChecks.Validate(Asset.Load(input));
    Console.WriteLine($"level={check.Level} script={check.Script} model={check.Model} passed={check.Passed}");
    return 0;
}

static string Sha(string path)
{
    using var f = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(f));
}
