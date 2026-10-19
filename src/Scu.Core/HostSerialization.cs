using System.Buffers.Binary;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record SerializationResult(string Umap, string Uexp, Dictionary<string, string> Members, LevelCheck LevelCheck);

/// <summary>
/// SCU-PATCH(c#-only): in-process two-pass write instead of two UAssetGUI runs (PATCHES.md section 5).
/// Port of host_serialization.serialize_asset: write the package twice (the first pass reveals the final
/// serial offsets that bulk-data pointers inside opaque payloads must point at), then re-read and verify it.
/// No intermediate JSON and no external converter process.
/// </summary>
public static class HostSerialization
{
    static readonly string[] IgnoredKeys = { "SerialOffset", "SerialSize", "ScriptSerializationStartOffset", "ScriptSerializationEndOffset" };

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static LevelCheck ValidateHostLevel(Asset a, string package)
    {
        Require(HostComposer.Assets.Values.Contains(package), "unknown host package " + package);
        var levels = a.Exports.Select((e, i) => (e, i: i + 1)).Where(x => x.e is LevelExport).ToList();
        Require(levels.Count == 1, "exactly one persistent level is required");
        var (e, i) = levels[0];
        var level = (LevelExport)e;
        int script = level.LevelScriptActor.Index, model = level.Model.Index;
        var scriptExport = a.Export(script);
        var modelExport = a.Export(model);
        Require(a.ObjectName(scriptExport.ClassIndex.Index) == package + "_C", "base map script class mismatch");
        Require(scriptExport.OuterIndex.Index == i && level.Actors.Any(p => p.Index == script), "script actor not owned by level");
        Require(a.ObjectName(modelExport.ClassIndex.Index) == "Model" && modelExport.OuterIndex.Index == i, "level model mismatch");
        Require(level.Actors.All(p => p.Index >= 0 && p.Index <= a.Exports.Count), "actor index out of range");
        var live = level.Actors.Where(p => p.Index != 0).Select(p => p.Index).ToList();
        Require(live.Count == live.Distinct().Count(), "duplicate live actor reference");
        return new LevelCheck(i, script, model, true);
    }

    /// <summary>native_x6.rebase: point bulk-data fields at their final absolute file positions.</summary>
    public static byte[] Rebase(byte[] raw, NativeInfo parsed, long baseOffset)
    {
        var copy = (byte[])raw.Clone();
        foreach (var f in parsed.Formats)
            BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(f.OffsetField, 8), baseOffset + f.PayloadLocalOffset);
        foreach (var r in parsed.Relocations)
            BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(r.OffsetField, 8), baseOffset + r.TargetLocalOffset);
        return copy;
    }

    static string Sha(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    public static SerializationResult Serialize(Asset data, List<NativeEntry>? native, string directory, string package, Action<string>? notify = null, bool strict = false)
    {
        Directory.CreateDirectory(directory);
        ValidateHostLevel(data, package);
        native ??= RetainHost.Native(data);
        string umap = Path.Combine(directory, package + ".umap"), uexp = Path.ChangeExtension(umap, ".uexp");
        notify?.Invoke("베이스 맵 저장·재파싱 검사 중…");
        var timing = Environment.GetEnvironmentVariable("SCU_TIMING") == "1";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what) { if (timing) { Console.Error.WriteLine($"[serialize {package}] {what} {clock.Elapsed.TotalSeconds:F1}s"); clock.Restart(); } }

        // Pass 0 only reveals where the writer puts every export so the bulk-data pointers inside opaque payloads can be set;
        // the writer records those positions in the exports themselves, so no re-read is needed between the passes.
        Names.Finalize(data);
        Lap("finalize names");
        data.Raw.Write(umap);
        Lap("write pass 0");
        foreach (var entry in native)
        {
            if (entry.Class is not ("BodySetup" or "Texture2D")) continue;
            var e = data.Exports[entry.Index - 1];
            long extrasStart = e.SerialOffset + e.SerialSize - e.Extras.Length;
            e.Extras = Rebase(e.Extras, entry.Info, extrasStart);
        }
        data.Raw.Write(umap);
        Lap("write pass 1");
        Asset z = Asset.Load(umap);
        Lap("reload");
        var downgraded = z.Exports.Select((e, i) => (e, i)).Where(x => x.e is RawExport && data.Exports[x.i] is not RawExport).Take(5).ToList();
        if (downgraded.Count > 0)
            throw new InvalidOperationException("exports unreadable after write: " + string.Join("; ", downgraded.Select(x =>
                $"{x.i + 1} {data.Exports[x.i].GetType().Name} {data.Exports[x.i].ObjectName} class={data.ObjectName(data.Exports[x.i].ClassIndex.Index)}")));

        var levelCheck = ValidateHostLevel(z, package);
        Require(data.Exports.Count == z.Exports.Count, "export count changed on roundtrip");
        Lap("level check");
        if (strict) VerifyExportsEqual(data, z);
        else VerifyRewriteIsStable(z, umap, uexp);
        Lap("roundtrip check");

        var zNames = z.Raw.GetNameMapIndexList().Select(n => n.ToString()).ToList();
        foreach (var entry in native)
        {
            var e = z.Exports[entry.Index - 1];
            var current = NativePayload.Parse(entry.Class, e.Extras, zNames);
            long start = e.SerialOffset + e.SerialSize - e.Extras.Length;
            Require(current.Refs.SequenceEqual(entry.Info.Refs), $"host native references changed ({entry.Index})");
            foreach (var r in current.Relocations) Require(r.SerializedOffset == start + r.TargetLocalOffset, "relocation target moved");
            Require(current.Payloads.SequenceEqual(entry.Info.Payloads), $"host native payload changed ({entry.Index})");
            foreach (var f in current.Formats)
            {
                Require(f.SerializedOffset == start + f.PayloadLocalOffset, $"format payload offset moved: export {entry.Index} {entry.Class} serialized={f.SerializedOffset} expected={start + f.PayloadLocalOffset} (start={start})");
                Require(entry.Info.Formats.First(o => o.Name == f.Name).PayloadSha256 == f.PayloadSha256, "format payload changed");
            }
        }

        const string Dir = "Kageroh/Content/Levels/RundomMapTiles/";
        var members = new Dictionary<string, string>
        {
            [Dir + package + ".umap"] = Sha(umap),
            [Dir + package + ".uexp"] = Sha(uexp),
        };
        z = null!;   // the re-read copy is only needed for the checks above
        return new SerializationResult(umap, uexp, members, levelCheck);
    }

    /// <summary>
    /// Fast roundtrip check: writing the re-read package again must reproduce the saved files byte for byte. That shows the
    /// reader and writer agree on every export (nothing dropped, reordered or misread) without building a JSON tree per export.
    /// </summary>
    static void VerifyRewriteIsStable(Asset reread, string umap, string uexp)
    {
        var temp = Path.Combine(Path.GetDirectoryName(umap)!, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var again = Path.Combine(temp, Path.GetFileName(umap));
            reread.Raw.Write(again);
            Require(FilesEqual(umap, again), "저장한 맵을 다시 읽어 쓴 결과가 다릅니다 (.umap)");
            Require(FilesEqual(uexp, Path.ChangeExtension(again, ".uexp")), "저장한 맵을 다시 읽어 쓴 결과가 다릅니다 (.uexp)");
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
    }

    static bool FilesEqual(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var x = File.OpenRead(a); using var y = File.OpenRead(b);
        var bx = new byte[1 << 20]; var by = new byte[1 << 20];
        while (true)
        {
            int nx = x.Read(bx, 0, bx.Length);
            if (nx == 0) return true;
            int read = 0;
            while (read < nx) { int ny = y.Read(by, read, nx - read); if (ny == 0) return false; read += ny; }
            if (!bx.AsSpan(0, nx).SequenceEqual(by.AsSpan(0, nx))) return false;
        }
    }

    /// <summary>The re-read package must equal the written one, ignoring the offsets the writer recomputes (slow, per-export JSON).</summary>
    static void VerifyExportsEqual(Asset written, Asset reread)
    {
        var settings = new JsonSerializerSettings();
        var bad = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, written.Exports.Count, i =>
        {
            var x = JObject.Parse(written.Raw.SerializeJsonObject(written.Exports[i], Formatting.None));
            var y = JObject.Parse(reread.Raw.SerializeJsonObject(reread.Exports[i], Formatting.None));
            foreach (var k in IgnoredKeys) { x.Remove(k); y.Remove(k); }
            if (!JToken.DeepEquals(x, y)) bad.Add(i + 1);
        });
        if (!bad.IsEmpty) throw new InvalidOperationException("host roundtrip changed export " + bad.Min());
    }
}
