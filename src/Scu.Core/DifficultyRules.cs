using System.Security.Cryptography;
using System.Text.Json;

namespace Scu.Core;

/// <summary>
/// The transcendent difficulty rules shipped with the randomizer (app/difficulty-assets): one pak that carries the rule
/// changes and native-difficulty versions of the base maps. Port of difficulty_rules.bundle, host_level_rules.baseline_path
/// and scu_map_patch.build_difficulty_pak / rules_manage_cicada.
/// </summary>
public sealed class DifficultyRules
{
    public const string Revision = "20261017-v102";
    public string Root { get; }
    public string RulesPak { get; }
    public HashSet<string> ManagedMembers { get; }
    public string ManifestRevision { get; }
    public string ExpectedSha256 { get; }

    DifficultyRules(string root, string rulesPak, HashSet<string> managed, string revision, string sha)
    { Root = root; RulesPak = rulesPak; ManagedMembers = managed; ManifestRevision = revision; ExpectedSha256 = sha; }

    public static DifficultyRules Load(string randomizerRoot)
    {
        var assets = Path.Combine(randomizerRoot, "app/difficulty-assets");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "catalog.json")));
        var manifestPath = Path.GetFullPath(Path.Combine(assets, catalog.RootElement.GetProperty("manifest").GetString()!));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var m = manifest.RootElement;
        var rules = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, m.GetProperty("pak").GetString()!));
        var managed = m.GetProperty("managed_members").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        return new DifficultyRules(randomizerRoot, rules, managed, m.GetProperty("revision").GetString()!, m.GetProperty("sha256").GetString()!);
    }

    string MapMember(string package, string ext) => PakTools.MapDirectory + package + ext;

    /// <summary>rules_manage_cicada: since 0.30.21 the rules pak also changes the Cicada Corridor map.</summary>
    public bool ManagesCicada => ManagedMembers.Contains(MapMember("Random5x5", ".umap"));

    public void CheckIntegrity()
    {
        if (ManifestRevision != Revision) throw new BuildException("unsupported_release", "기본 스테이지의 난이도 자료 버전을 확인해 주세요.");
        using var f = File.OpenRead(RulesPak);
        if (Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant() != ExpectedSha256)
            throw new BuildException("build_failed", "초월자 규칙 pak의 해시가 다릅니다. 데이터를 다시 설치하세요.");
    }

    /// <summary>The base map as the transcendent rules define it, extracted next to <paramref name="cacheDirectory"/>.</summary>
    public string BaselineUmap(string package, string cacheDirectory)
    {
        var umap = Path.Combine(cacheDirectory, "transcendent", package + ".umap");
        foreach (var ext in new[] { ".umap", ".uexp" })
        {
            var dest = Path.Combine(cacheDirectory, "transcendent", package + ext);
            if (File.Exists(dest)) continue;
            if (!PakTools.Extract(Root, RulesPak, MapMember(package, ext), dest))
                throw new BuildException("build_failed", $"초월자 규칙 pak에서 {package}{ext} 를 찾지 못했습니다.");
        }
        return umap;
    }

    public const string DifficultyPakName = "Kageroh-SCUDifficulty_0_P.pak";
    public const string DifficultyMapsPakName = "Kageroh-SCUDifficultyMaps_0_P.pak";

    /// <summary>build_difficulty_pak: the rules without the base maps, and a pak with just the rules' own base maps.</summary>
    public (PakInfo Rules, PakInfo Maps) BuildPaks(string outDirectory, string workDirectory)
    {
        var tree = Path.Combine(workDirectory, "difficulty-tree");
        var mapsTree = Path.Combine(workDirectory, "difficulty-maps-tree");
        foreach (var d in new[] { tree, mapsTree }) { FsUtil.DeleteTreeWithRetry(d); Directory.CreateDirectory(d); }
        PakTools.Unpack(Root, RulesPak, tree);
        var required = new[] { "Extra5x5", "Stage5x5", "Stage6x5" }.SelectMany(p => new[] { MapMember(p, ".umap"), MapMember(p, ".uexp") }).ToHashSet();
        var optional = new[] { MapMember("Random5x5", ".umap"), MapMember("Random5x5", ".uexp") }.ToHashSet();
        var moved = new HashSet<string>();
        foreach (var rel in required.Concat(optional).OrderBy(x => x, StringComparer.Ordinal))
        {
            var source = Path.Combine(tree, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            var target = Path.Combine(mapsTree, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            moved.Add(rel);
        }
        if (!required.IsSubsetOf(moved) || !(moved.Except(required).Count() == 0 || moved.Except(required).ToHashSet().SetEquals(optional)))
            throw new BuildException("build_failed", "초월자 규칙 pak의 기본 맵 구성이 예상과 다릅니다.");
        var infos = new List<PakInfo>();
        foreach (var (name, folder) in new[] { (DifficultyPakName, tree), (DifficultyMapsPakName, mapsTree) })
        {
            var pak = Path.Combine(outDirectory, name);
            if (File.Exists(pak)) File.Delete(pak);
            PakTools.Pack(Root, folder, pak);
            using var f = File.OpenRead(pak);
            infos.Add(new PakInfo(pak, Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(), new FileInfo(pak).Length,
                PakTools.HashList(Root, pak).Count, ManifestRevision));
        }
        FsUtil.DeleteTreeWithRetry(tree); FsUtil.DeleteTreeWithRetry(mapsTree);
        return (infos[0], infos[1]);
    }
}

public sealed record PakInfo(string Pak, string Sha256, long Size, int Members, string RulesRevision);
