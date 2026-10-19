using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetAPI;

namespace Scu.Core;

/// <summary>
/// Port of the randomizer's first-run preparation (launcher.prepare + local_map_materials) for what the SCU build needs:
/// the original maps extracted from the user's game, and the prepared donor packages that the randomizer ships only as
/// edit instructions (app/local-map-recipes/recipe.json). The recipe is evaluated directly into packages; no JSON files are written.
/// </summary>
public static class Prepare
{
    /// <summary>Recipe targets the SCU build reads, and the package file they become (relative to the prepared directory).</summary>
    static readonly (string Target, string Output)[] Wanted =
    {
        ("reports/tile-reference-swap/expansion-candidates/sanctuary-all-X-trapstatefix/Random5x5.json", "sanctuary-all-X-trapstatefix/Random5x5.umap"),
        ("reports/tile-reference-swap/expansion-candidates/soul-X1/Random5x5.json", "soul-X1/Random5x5.umap"),
        ("app/updates/sanctuary-LT/all/prepared-donor.json", "sanctuary-lt/prepared-donor.umap"),
        ("app/updates/sanctuary-LT/all/parsed.json", "sanctuary-lt/all.umap"),
    };

    public static readonly JsonDocumentOptions DeepJson = new() { MaxDepth = 4096 };

    public static string PreparedDirectory(string randomizerRoot) => Path.Combine(randomizerRoot, "scu-prepared");

    public static string PreparedPath(string randomizerRoot, string relative) => Path.Combine(PreparedDirectory(randomizerRoot), relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Plain JSON documents the build reads from the randomizer folder. They are not in the shipped package (they hold data
    /// from the user's game), so they are written from the recipe, byte for byte as the randomizer writes them.
    /// </summary>
    static readonly string[] Documents =
    {
        "app/soul-lt-room.json",
        "app/updates/sanctuary-LT/all/room-plan.json",
        "app/updates/soul-X3-analysis/destination-spatial-survey.json",
        "app/updates/soul-X6-analysis/special-space-survey.json",
        "app/updates/soul-X6-analysis/terminal-mirror-patch.json",
    };

    /// <summary>The transcendent rules pak: a shipped template whose holes are filled from the user's game and from locally rebuilt maps.</summary>
    const string RulesPakTarget = "app/difficulty-assets/20261011-v100/rules.pak";

    static string DocumentPath(string root, string target) => Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar));

    public static bool IsPrepared(string randomizerRoot)
        => Documents.All(d => File.Exists(DocumentPath(randomizerRoot, d))) && File.Exists(DocumentPath(randomizerRoot, RulesPakTarget)) && Wanted.All(w => File.Exists(PreparedPath(randomizerRoot, w.Output)) && File.Exists(Path.ChangeExtension(PreparedPath(randomizerRoot, w.Output), ".uexp")))
           && File.Exists(Path.Combine(randomizerRoot, "extracted/Kageroh/Content/Levels/RundomMapTiles/Stage6x5.umap"));

    /// <summary>Prepared package: ours if present, otherwise the one the Python randomizer's preparation left in the folder.</summary>
    public static string? Resolve(string root, string preparedRelative, string? legacyRelative)
    {
        var ours = PreparedPath(root, preparedRelative);
        if (File.Exists(ours)) return ours;
        if (legacyRelative != null)
        {
            var legacy = Path.Combine(root, legacyRelative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(legacy)) return legacy;
        }
        return null;
    }

    public const string SanctuaryAllRel = "reports/tile-reference-swap/expansion-candidates/sanctuary-all-X-trapstatefix/Random5x5.umap";
    public const string SoulX1Rel = "reports/tile-reference-swap/expansion-candidates/soul-X1/Random5x5.umap";

    static void Require(bool ok, string message)
    {
        if (!ok) throw new BuildException("prepare_failed", message);
    }

    static string Sha(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>The files from the randomizer package that the build reads, copied into a separate cache folder (the rest of the package is not needed there).</summary>
    static readonly string[] SmallShipped =
    {
        "portable-spec.json",
        "tools/repak-0.2.3/repak.exe",
        "app/difficulty-assets/catalog.json",
        "app/difficulty-assets/20261011-v100/manifest.json",
        "app/updates/sanctuary-LT/all/event-plan.json",
        "app/updates/sanctuary-LT/all/donor-preflight.json",
    };

    /// <summary>Make <paramref name="cacheRoot"/> usable as the randomizer root: copy the small shipped files (the big ones are read from the package when needed).</summary>
    public static void SyncShipped(string shippedRoot, string cacheRoot)
    {
        static string Normal(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Normal(shippedRoot), Normal(cacheRoot), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var rel in SmallShipped)
        {
            var from = Path.Combine(shippedRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var to = Path.Combine(cacheRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(from)) throw new BuildException("randomizer_incomplete", $"필요한 파일을 찾을 수 없습니다: {from}");
            if (File.Exists(to) && Sha(to) == Sha(from)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
    }

    public static void Run(string root, string game, Action<string> note, string? shippedRoot = null)
    {
        shippedRoot ??= root;
        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "portable-spec.json"), Encoding.UTF8));
        var s = spec.RootElement;
        note("게임 버전 확인 중…");
        foreach (var f in s.GetProperty("game_files").EnumerateObject())
        {
            var path = Path.Combine(game, f.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || Sha(path) != f.Value.GetString())
                throw new BuildException("prepare_failed", "지원하는 게임 버전이 아닙니다. 원본 파일을 확인해 주세요.\n" + f.Name);
        }
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        if (free < 6L * 1024 * 1024 * 1024) throw new BuildException("disk_space", "작업 폴더의 여유 공간이 부족합니다 (6GB 이상 필요).");

        // ---- original maps out of the game pak ----
        var pak = Path.Combine(game, "Kageroh/Content/Paks/Kageroh-WindowsNoEditor.pak");
        var inputs = new Dictionary<string, string>();   // json target -> extracted umap
        foreach (var row in s.GetProperty("original_json").EnumerateArray())
        {
            var target = row.GetProperty("target").GetString()!;
            string? umap = null;
            foreach (var member in row.GetProperty("members").EnumerateArray())
            {
                var rel = member.GetString()!;
                var dest = Path.Combine(root, "extracted", rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dest))
                {
                    note("맵 자료 준비 중: " + Path.GetFileNameWithoutExtension(rel));
                    Require(PakTools.Extract(root, pak, rel, dest), "게임에 필요한 맵 자료가 없습니다: " + rel);
                }
                if (rel.EndsWith(".umap", StringComparison.Ordinal)) umap = dest;
            }
            inputs[target] = umap!;
        }

        // ---- prepared packages from the recipe ----
        var pending = Wanted.Where(w => !File.Exists(PreparedPath(root, w.Output))).ToList();
        if (pending.Count == 0 && Documents.All(d => File.Exists(DocumentPath(root, d))) && File.Exists(DocumentPath(root, RulesPakTarget))) return;
        note("사용자 원본으로 맵 재료 준비 중…");
        var recipePath = Path.Combine(shippedRoot, "app/local-map-recipes/recipe.json");
        using var recipeDoc = JsonDocument.Parse(File.ReadAllBytes(recipePath), DeepJson);
        var recipe = recipeDoc.RootElement;
        var jobs = recipe.GetProperty("json").EnumerateArray().ToDictionary(r => r.GetProperty("target").GetString()!, r => r);
        var aliases = recipe.GetProperty("inputs").EnumerateArray().ToDictionary(r => r.GetProperty("alias").GetString()!, r => r.GetProperty("target").GetString()!);
        var sources = new RecipeSources(aliases, inputs);
        foreach (var (target, output) in pending)
        {
            Require(jobs.TryGetValue(target, out var job), "recipe target missing: " + target);
            note("사용자 원본으로 맵 재료 준비 중: " + Path.GetFileName(Path.GetDirectoryName(output)));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 4096 }))
                RecipeEvaluator.Write(null, job.GetProperty("expression"), sources, writer);
            stream.Position = 0;
            var asset = Asset.LoadJson(stream);
            Names.Finalize(asset);
            var dest = PreparedPath(root, output);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            asset.Raw.Write(dest);
            sources.Trim();
        }

        // ---- plain JSON documents, written the way the randomizer writes them (their hashes are pinned) ----
        foreach (var target in Documents)
        {
            Require(jobs.TryGetValue(target, out var job), "recipe target missing: " + target);
            var wanted = job.GetProperty("sha256").GetString()!;
            var dest = DocumentPath(root, target);
            if (File.Exists(dest) && Sha(dest) == wanted) continue;
            note("사용자 원본으로 맵 재료 준비 중: " + Path.GetFileName(target));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 4096 }))
                RecipeEvaluator.Write(null, job.GetProperty("expression"), sources, writer);
            var bytes = PythonJson.Format(stream.ToArray(), job);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Require(actual == wanted, "생성한 맵 자료가 검증된 결과와 다릅니다: " + Path.GetFileName(target));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            sources.Trim();
        }

        EnsureRulesPak(root, shippedRoot, game, s, recipe, jobs, sources, note);
    }

    /// <summary>local_original_assets.ensure_local_assets for the rules pak (mode binary_holes).</summary>
    static void EnsureRulesPak(string root, string shippedRoot, string game, JsonElement spec, JsonElement recipe, Dictionary<string, JsonElement> jobs, RecipeSources sources, Action<string> note)
    {
        var row = spec.GetProperty("local_assets").GetProperty("paks").EnumerateArray().First(p => p.GetProperty("target").GetString() == RulesPakTarget);
        var wanted = row.GetProperty("sha256").GetString()!;
        var dest = DocumentPath(root, RulesPakTarget);
        if (File.Exists(dest) && Sha(dest) == wanted) return;
        note("사용자 게임 자료로 수정 Pak 준비 중: rules.pak");
        if (row.GetProperty("mode").GetString() != "binary_holes") Fail("지원하지 않는 수정 자료의 준비 방식입니다.");
        var template = DocumentPath(shippedRoot, row.GetProperty("template").GetString()!);
        Require(File.Exists(template) && Sha(template) == row.GetProperty("template_sha256").GetString(), "배포한 수정 자료가 손상되었습니다.");
        var data = File.ReadAllBytes(template);
        var holes = row.GetProperty("original_members").EnumerateArray().ToList();
        var ranges = new List<(int Start, int End)>();
        foreach (var hole in holes)
        {
            int start = hole.GetProperty("offset").GetInt32(), size = hole.GetProperty("length").GetInt32(), end = start + size;
            Require(start >= 0 && size > 0 && end <= data.Length, "원본 자료를 채울 위치가 올바르지 않습니다.");
            Require(!ranges.Any(r => start < r.End && r.Start < end), "원본 자료를 채울 위치가 겹칩니다.");
            Require(data.AsSpan(start, size).IndexOfAnyExcept((byte)0) < 0, "수정 템플릿의 원본 자료 자리가 비어 있지 않습니다.");
            ranges.Add((start, end));
        }

        // locally rebuilt maps: output path -> the conversion that produces it
        var conversions = new Dictionary<string, JsonElement>();
        foreach (var c in recipe.GetProperty("conversions").EnumerateArray())
            foreach (var output in c.GetProperty("outputs").EnumerateObject()) conversions[output.Name] = c;

        var gamePak = Path.Combine(game, "Kageroh/Content/Paks/Kageroh-WindowsNoEditor.pak");
        var temp = Path.Combine(Path.GetTempPath(), "scu-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var hole in holes)
            {
                var expected = hole.GetProperty("sha256").GetString()!;
                byte[] bytes;
                if (hole.TryGetProperty("source_member", out var member))
                {
                    var file = Path.Combine(temp, Guid.NewGuid().ToString("N"));
                    Require(PakTools.Extract(root, gamePak, member.GetString()!, file), "게임에 필요한 원본 자료가 없습니다: " + member.GetString());
                    bytes = File.ReadAllBytes(file);
                    File.Delete(file);
                }
                else
                {
                    var target = hole.GetProperty("source_target").GetString()!;
                    var path = DocumentPath(root, target);
                    if (!File.Exists(path) || Sha(path) != expected)
                    {
                        Require(conversions.TryGetValue(target, out var conversion), "recipe conversion missing: " + target);
                        BuildMapMaterial(root, conversion, jobs, sources, note);
                    }
                    bytes = File.ReadAllBytes(path);
                }
                Require(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == expected, "지원하는 원본 자료와 다릅니다: " + hole.GetProperty("member").GetString());
                Require(bytes.Length == hole.GetProperty("length").GetInt32(), "원본 자료의 크기가 일치하지 않습니다.");
                Array.Copy(bytes, 0, data, hole.GetProperty("offset").GetInt32(), bytes.Length);
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Require(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() == wanted, "재구성한 수정 Pak이 검증된 결과와 다릅니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, data);
    }

    /// <summary>One recipe conversion: evaluate the recipe into a package and write its .umap/.uexp where the randomizer keeps them.</summary>
    static void BuildMapMaterial(string root, JsonElement conversion, Dictionary<string, JsonElement> jobs, RecipeSources sources, Action<string> note)
    {
        var source = conversion.GetProperty("source").GetString()!;
        Require(jobs.TryGetValue(source, out var job), "recipe target missing: " + source);
        note("사용자 원본으로 맵 재료 준비 중: " + Path.GetFileNameWithoutExtension(source));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 4096 }))
            RecipeEvaluator.Write(null, job.GetProperty("expression"), sources, writer);
        stream.Position = 0;
        var asset = Asset.LoadJson(stream);
        Names.Finalize(asset);
        var umap = conversion.GetProperty("outputs").EnumerateObject().First(o => o.Value.GetString() == ".umap").Name;
        var dest = DocumentPath(root, umap);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Raw.Write(dest);
        sources.Trim();
    }

    static void Fail(string message) => throw new BuildException("prepare_failed", message);
}

/// <summary>The original maps as UAssetAPI JSON documents (what the recipe's paths index into), loaded on demand.</summary>
public sealed class RecipeSources
{
    readonly Dictionary<string, string> _aliasTargets;
    readonly Dictionary<string, string> _umapByTarget;
    readonly Dictionary<string, JsonDocument> _docs = new();

    public RecipeSources(Dictionary<string, string> aliasTargets, Dictionary<string, string> umapByTarget)
    { _aliasTargets = aliasTargets; _umapByTarget = umapByTarget; }

    JsonDocument Document(string alias)
    {
        if (_docs.TryGetValue(alias, out var doc)) return doc;
        if (!_aliasTargets.TryGetValue(alias, out var target) || !_umapByTarget.TryGetValue(target, out var umap))
            throw new BuildException("prepare_failed", "알 수 없는 원본 맵 참조입니다: " + alias);
        var json = Asset.Load(umap).Raw.SerializeJson(Newtonsoft.Json.Formatting.None);
        return _docs[alias] = JsonDocument.Parse(Encoding.UTF8.GetBytes(json), Prepare.DeepJson);
    }

    public JsonElement Get(JsonElement reference)
    {
        var alias = reference[0].GetString()!;
        var value = Document(alias).RootElement;
        foreach (var part in reference[1].EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Number)
            {
                int i = part.GetInt32();
                if (value.ValueKind != JsonValueKind.Array || i < 0 || i >= value.GetArrayLength()) throw new BuildException("prepare_failed", "원본 맵 참조 위치가 올바르지 않습니다.");
                value = value[i];
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part.GetString()!, out var next)) throw new BuildException("prepare_failed", "원본 맵 참조 위치가 올바르지 않습니다.");
                value = next;
            }
        }
        return value;
    }

    /// <summary>Drop the big documents between targets to keep the peak low.</summary>
    public void Trim()
    {
        foreach (var d in _docs.Values) d.Dispose();
        _docs.Clear();
        GC.Collect();
    }
}

/// <summary>local_map_materials._apply, streaming straight into a JSON writer instead of building the result.</summary>
public static class RecipeEvaluator
{
    static void Fail(string message) => throw new BuildException("prepare_failed", message);

    public static void Write(JsonElement? @base, JsonElement patch, RecipeSources sources, Utf8JsonWriter w)
    {
        if (patch.ValueKind == JsonValueKind.Null) { if (@base is { } b) b.WriteTo(w); else w.WriteNullValue(); return; }
        if (patch.ValueKind != JsonValueKind.Object) { Fail("맵 변경 명령이 올바르지 않습니다."); return; }
        var keys = patch.EnumerateObject().Select(p => p.Name).ToHashSet();
        if (keys.SetEquals(new[] { "v" }))
        {
            var value = patch.GetProperty("v");
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 256))
                Fail("원본 자료를 포함하는 변경 명령은 지원하지 않습니다.");
            value.WriteTo(w); return;
        }
        if (keys.SetEquals(new[] { "r" })) { sources.Get(patch.GetProperty("r")).WriteTo(w); return; }
        if (keys.SetEquals(new[] { "c", "p" })) { Write(sources.Get(patch.GetProperty("c")), patch.GetProperty("p"), sources, w); return; }
        if (keys.Contains("d") && keys.IsSubsetOf(new[] { "d", "x", "o" })) { WriteObject(@base, patch, sources, w); return; }
        if (keys.SetEquals(new[] { "l" })) { WriteList(@base, patch.GetProperty("l"), sources, w); return; }
        if (keys.SetEquals(new[] { "a" }))
        {
            w.WriteStartArray();
            foreach (var item in patch.GetProperty("a").EnumerateArray()) Write(null, item, sources, w);
            w.WriteEndArray(); return;
        }
        if (keys.SetEquals(new[] { "n" }))
        {
            w.WriteStartObject();
            foreach (var pair in patch.GetProperty("n").EnumerateArray())
            {
                w.WritePropertyName(pair[0].GetString()!);
                Write(null, pair[1], sources, w);
            }
            w.WriteEndObject(); return;
        }
        if (keys.SetEquals(new[] { "b" })) { WriteBinary(patch.GetProperty("b"), sources, w); return; }
        Fail("지원하지 않는 맵 변경 명령입니다.");
    }

    static void WriteObject(JsonElement? @base, JsonElement patch, RecipeSources sources, Utf8JsonWriter w)
    {
        if (@base is not { ValueKind: JsonValueKind.Object } baseObject) { Fail("맵 객체 변경 대상이 올바르지 않습니다."); return; }
        var order = new List<string>();
        var original = new Dictionary<string, JsonElement>();
        foreach (var p in baseObject.EnumerateObject()) { order.Add(p.Name); original[p.Name] = p.Value; }
        var removed = new HashSet<string>();
        if (patch.TryGetProperty("x", out var x))
            foreach (var key in x.EnumerateArray())
            {
                var k = key.GetString()!;
                if (!original.ContainsKey(k) || removed.Contains(k)) Fail("맵 객체 삭제 대상이 없습니다.");
                removed.Add(k);
            }
        var keep = order.Where(k => !removed.Contains(k)).ToList();
        var changes = new Dictionary<string, JsonElement>();
        var added = new List<string>();
        foreach (var pair in patch.GetProperty("d").EnumerateArray())
        {
            var key = pair[0].GetString()!;
            if (changes.ContainsKey(key)) Fail("맵 객체 변경 키가 중복되었습니다.");
            changes[key] = pair[1];
            if (!original.ContainsKey(key) || removed.Contains(key)) { if (!keep.Contains(key)) { keep.Add(key); added.Add(key); } }
        }
        if (patch.TryGetProperty("o", out var o))
        {
            var wanted = o.EnumerateArray().Select(e => e.GetString()!).ToList();
            if (wanted.Count != keep.Count || !wanted.ToHashSet().SetEquals(keep)) Fail("맵 객체 필드 순서가 올바르지 않습니다.");
            keep = wanted;
        }
        w.WriteStartObject();
        foreach (var key in keep)
        {
            w.WritePropertyName(key);
            bool hasBase = original.TryGetValue(key, out var existing) && !removed.Contains(key);
            if (changes.TryGetValue(key, out var child)) Write(hasBase ? existing : null, child, sources, w);
            else existing.WriteTo(w);
        }
        w.WriteEndObject();
    }

    static void WriteList(JsonElement? @base, JsonElement edits, RecipeSources sources, Utf8JsonWriter w)
    {
        if (@base is not { ValueKind: JsonValueKind.Array } list) { Fail("맵 목록 변경 대상이 올바르지 않습니다."); return; }
        var changes = new Dictionary<int, JsonElement>();
        foreach (var pair in edits.EnumerateArray())
        {
            int index = pair[0].GetInt32();
            if (index < 0 || index >= list.GetArrayLength()) Fail("맵 목록 변경 위치가 올바르지 않습니다.");
            changes[index] = pair[1];   // a later edit of the same index wins on its original value, like the sequential Python loop
        }
        w.WriteStartArray();
        int n = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (changes.TryGetValue(n, out var child)) Write(item, child, sources, w); else item.WriteTo(w);
            n++;
        }
        w.WriteEndArray();
    }

    static void WriteBinary(JsonElement rule, RecipeSources sources, Utf8JsonWriter w)
    {
        var raw = Convert.FromBase64String(sources.Get(rule.GetProperty("r")).GetString()!);
        if (raw.Length != rule.GetProperty("length").GetInt32()) Fail("원본 맵 바이너리의 크기가 다릅니다.");
        int end = 0, changed = 0;
        foreach (var edit in rule.GetProperty("edits").EnumerateArray())
        {
            int start = edit[0].GetInt32();
            var bytes = Convert.FromHexString(edit[1].GetString()!);
            if (start < end || bytes.Length == 0 || bytes.Length > 256 || start + bytes.Length > raw.Length) Fail("맵 바이너리 변경 범위가 올바르지 않습니다.");
            Array.Copy(bytes, 0, raw, start, bytes.Length);
            changed += bytes.Length;
            end = start + bytes.Length;
        }
        if (changed > Math.Max(32, raw.Length / 4)) Fail("맵 바이너리 변경량이 원본 참조 범위를 벗어났습니다.");
        w.WriteStringValue(Convert.ToBase64String(raw));
    }
}
