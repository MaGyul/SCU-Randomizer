using System.Diagnostics;
using System.Text;

namespace Scu.Core;

/// <summary>Thin wrapper over repak.exe (already native Rust): pack a staged tree and verify the resulting entries.</summary>
public static class PakTools
{
    public const string MapDirectory = "Kageroh/Content/Levels/RundomMapTiles/";

    static (int ExitCode, string Output, string Error) Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("repak timed out"); }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string RepakPath(string randomizerRoot) => Path.Combine(randomizerRoot, "tools", "repak-0.2.3", "repak.exe");

    public static void Pack(string randomizerRoot, string stageDirectory, string pakPath, int timeoutMs = 300_000)
    {
        var (code, _, err) = Run(RepakPath(randomizerRoot),
            new[] { "pack", stageDirectory, pakPath, "--version", "V8A", "--mount-point", "../../../" }, timeoutMs);
        if (code != 0) throw new InvalidOperationException("pak creation failed: " + err.Trim());
    }

    /// <summary>Entry path -> SHA-256 of its content, as computed by repak from the finished pak.</summary>
    public static Dictionary<string, string> HashList(string randomizerRoot, string pakPath, int timeoutMs = 300_000)
    {
        var (code, output, err) = Run(RepakPath(randomizerRoot), new[] { "hash-list", pakPath }, timeoutMs);
        if (code != 0) throw new InvalidOperationException("pak listing failed: " + err.Trim());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            int space = text.IndexOf(' ');
            if (space > 0) result[text[(space + 1)..]] = text[..space];
        }
        return result;
    }

    /// <summary>SHA-256 of one file inside a pak, or null if it is not there (repak get streams the content).</summary>
    public static string? ContentSha(string randomizerRoot, string pakPath, string entry, int timeoutMs = 600_000)
    {
        var info = new ProcessStartInfo(RepakPath(randomizerRoot)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "get", pakPath, entry }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var stderr = p.StandardError.ReadToEndAsync();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(p.StandardOutput.BaseStream);
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("repak timed out"); }
        return p.ExitCode == 0 ? Convert.ToHexString(hash).ToLowerInvariant() : null;
    }

    /// <summary>Write one file of a pak to <paramref name="destination"/> (repak get streams the content).</summary>
    public static bool Extract(string randomizerRoot, string pakPath, string entry, string destination, int timeoutMs = 600_000)
    {
        var info = new ProcessStartInfo(RepakPath(randomizerRoot)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "get", pakPath, entry }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var stderr = p.StandardError.ReadToEndAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using (var file = File.Create(destination)) p.StandardOutput.BaseStream.CopyTo(file);
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("repak timed out"); }
        if (p.ExitCode != 0) { File.Delete(destination); return false; }
        return true;
    }

    public static void Unpack(string randomizerRoot, string pakPath, string outputDirectory, int timeoutMs = 600_000)
    {
        var (code, _, err) = Run(RepakPath(randomizerRoot), new[] { "unpack", "-q", "-f", "-o", outputDirectory, pakPath }, timeoutMs);
        if (code != 0) throw new InvalidOperationException("pak unpack failed: " + err.Trim());
    }

    /// <summary>Stage the map files under their game-relative paths, pack them and check the pak holds exactly those files.</summary>
    public static string PackMaps(string randomizerRoot, string pakPath, IReadOnlyDictionary<string, string> sourceFiles, string stageDirectory)
    {
        FsUtil.DeleteTreeWithRetry(stageDirectory);
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, source) in sourceFiles)
        {
            var target = Path.Combine(stageDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            members[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
        }
        Pack(randomizerRoot, stageDirectory, pakPath);
        var inPak = HashList(randomizerRoot, pakPath);
        if (!inPak.Keys.ToHashSet().SetEquals(members.Keys)) throw new InvalidOperationException("unexpected pak members");
        foreach (var (relative, sha) in members)
            if (inPak[relative] != sha) throw new InvalidOperationException("pak content check failed: " + relative);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pakPath))).ToLowerInvariant();
    }
}
