using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scu.Core;

/// <summary>
/// Map Shuffle version of the 2-player multi pack (Four9's pak, used with permission): take the official pack folder,
/// add the extra rooms to the multi pak's own maps (<see cref="BuildOptions.BaseMaps"/>), put the four maps back into
/// the multi pak and write the result as a pack folder of its own (tools/version.json with scuMapShuffle).
/// Every player builds it on their own PC; the same official pack and map data must give the same pak bytes,
/// because the relay compares the pack version (tag + sha) of host and joiner.
/// </summary>
public static class MultiPack
{
    /// <summary>Bump when the output for the same inputs changes: the tag becomes "&lt;official tag&gt;-scu&lt;revision&gt;".</summary>
    public const int Revision = 1;

    public sealed record Options(string Pack, string Randomizer, string Game, string Out, string? Cache = null, string? Work = null, int Parallel = 1);

    static string Sha(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    static void Progress(string stage, int percent) => Builder.Emit("progress", ("stage", stage), ("message", stage), ("percent", percent));

    public static int Run(Options o)
    {
        string work = Path.GetFullPath(o.Work ?? o.Out.TrimEnd('\\', '/') + ".work");
        try
        {
            // ---- the official pack ----
            var pack = Path.GetFullPath(o.Pack);
            var versionFile = Path.Combine(pack, "tools", "version.json");
            if (!File.Exists(versionFile)) throw new BuildException("multi_pack_invalid", "tools/version.json 이 없습니다.");
            var version = JsonNode.Parse(File.ReadAllText(versionFile).TrimStart('﻿'))!.AsObject();
            if (version["scuMapShuffle"]?.GetValue<bool>() == true) throw new BuildException("multi_pack_invalid", "이미 맵 셔플용으로 만든 꾸러미입니다.");
            var tag = version["tag"]?.GetValue<string>() ?? "";
            var sha = (version["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant();
            long bytes = version["bytes"]?.GetValue<long>() ?? -1;
            var pakPath = Directory.GetFiles(pack, "Kageroh-*_P.pak").FirstOrDefault(p => new FileInfo(p).Length == bytes)
                ?? throw new BuildException("multi_pack_invalid", "version.json 크기와 맞는 Pak이 없습니다.");
            Progress("multi-check", 1);
            if (Sha(pakPath) != sha) throw new BuildException("multi_pack_invalid", "Pak의 sha256이 version.json과 다릅니다.");

            FsUtil.DeleteTreeWithRetry(work);
            Directory.CreateDirectory(work);
            var tree = Path.Combine(work, "multi");
            var rtm = Path.Combine(tree, PakTools.MapDirectory.Replace('/', Path.DirectorySeparatorChar));

            // repak is only in the randomizer data (or the cache made from it)
            var tools = o.Cache != null && File.Exists(PakTools.RepakPath(o.Cache)) ? o.Cache : o.Randomizer;
            Progress("multi-unpack", 2);
            PakTools.Unpack(tools, pakPath, tree);
            var official = PakTools.HashList(tools, pakPath);
            foreach (var package in HostComposer.Assets.Values)
                if (!File.Exists(Path.Combine(rtm, package + ".umap")) || !File.Exists(Path.Combine(rtm, package + ".uexp")))
                    throw new BuildException("multi_pack_invalid", $"멀티 Pak에 {package} 맵이 없습니다.");

            // ---- extra rooms on the multi maps ----
            var buildOut = Path.Combine(work, "build");
            int code = Builder.Run(new BuildOptions(o.Randomizer, o.Game, buildOut, Builder.AllStages, new[] { "cicada", "soul", "abyss", "sanctuary" },
                new[] { "original" }, Path.Combine(work, "build-work"), false, o.Parallel, true, false, o.Cache, rtm));
            if (code != 0) return code;   // the builder has already reported the error

            // ---- put the maps back into the multi pak ----
            Progress("multi-pack", 96);
            var built = Path.Combine(buildOut, Builder.PakName);
            var maps = Path.Combine(work, "maps");
            PakTools.Unpack(tools, built, maps);
            var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var package in HostComposer.Assets.Values)
                foreach (var ext in new[] { ".umap", ".uexp" })
                {
                    var rel = PakTools.MapDirectory + package + ext;
                    var source = Path.Combine(maps, rel.Replace('/', Path.DirectorySeparatorChar));
                    File.Copy(source, Path.Combine(tree, rel.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);
                    replaced[rel] = Sha(source);
                }

            var stage = Path.GetFullPath(o.Out.TrimEnd('\\', '/') + ".tmp");
            FsUtil.DeleteTreeWithRetry(stage);
            Directory.CreateDirectory(stage);
            // everything but the pak and version.json comes from the official pack as it is (instructions, scripts)
            foreach (var file in Directory.GetFiles(pack, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pack, file);
                if (string.Equals(file, pakPath, StringComparison.OrdinalIgnoreCase) || rel.Equals(Path.Combine("tools", "version.json"), StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(stage, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            var outPak = Path.Combine(stage, Path.GetFileName(pakPath));
            PakTools.Pack(tools, tree, outPak);

            // the new pak: the same entries, only the four maps differ
            var mine = PakTools.HashList(tools, outPak);
            if (!mine.Keys.ToHashSet().SetEquals(official.Keys)) throw new BuildException("unexpected_members", "만든 Pak의 파일 구성이 멀티 Pak과 다릅니다.");
            foreach (var (entry, hash) in mine)
            {
                var expected = replaced.TryGetValue(entry, out var r) ? r : official[entry];
                if (hash != expected) throw new BuildException("pak_verify_failed", "만든 Pak의 내용이 예상과 다릅니다: " + entry);
            }

            var newSha = Sha(outPak);
            var newTag = $"{tag}-scu{Revision}";
            version["tag"] = newTag;
            version["sha256"] = newSha;
            version["bytes"] = new FileInfo(outPak).Length;
            version["scuMapShuffle"] = true;
            version["scuBase"] = new JsonObject { ["tag"] = tag, ["sha256"] = sha };
            Directory.CreateDirectory(Path.Combine(stage, "tools"));
            File.WriteAllText(Path.Combine(stage, "tools", "version.json"), version.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var dest = Path.GetFullPath(o.Out);
            FsUtil.DeleteTreeWithRetry(dest);
            Directory.Move(stage, dest);
            Builder.Emit("multi_done", ("dir", dest), ("tag", newTag), ("sha256", newSha), ("bytes", new FileInfo(Path.Combine(dest, Path.GetFileName(pakPath))).Length),
                ("baseTag", tag), ("baseSha256", sha));
            return 0;
        }
        catch (BuildException e)
        {
            Builder.Emit("error", ("code", e.Code), ("message", e.Message));
            return 1;
        }
        catch (Exception e)
        {
            Builder.Emit("error", ("code", "multi_pack_failed"), ("message", e.GetType().Name + ": " + e.Message), ("trace", e.ToString()));
            Console.Error.WriteLine(e);
            return 1;
        }
        finally
        {
            try { FsUtil.DeleteTreeWithRetry(work); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
