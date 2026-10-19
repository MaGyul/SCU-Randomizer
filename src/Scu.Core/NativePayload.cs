using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Scu.Core;

public sealed record NativeRef(int Offset, string Field, int Value);
public sealed record NativeFormat(string Name, int Instance, int Flags, int Bytes, int OffsetField, long SerializedOffset, int PayloadLocalOffset, string PayloadSha256);
public sealed record NativeName(int Offset, string Name);
public sealed record NativeRelocation(int OffsetField, int TargetLocalOffset, long SerializedOffset);
public sealed record NativePayloadInfo(string Name, int Bytes, string Sha256);

/// <summary>Result of the strict class-specific reader for an export's opaque trailing bytes (Extras).</summary>
public sealed class NativeInfo
{
    public List<NativeRef> Refs { get; } = new();
    public List<NativeFormat> Formats { get; } = new();
    public List<NativeName> Names { get; } = new();
    public List<NativeRelocation> Relocations { get; } = new();
    public List<NativePayloadInfo> Payloads { get; } = new();
    public int ConsumedBytes { get; set; }
}

/// <summary>
/// Port of classify_native.parse + native_x6.parse + soul_special.parse (foliage).
/// Exact observed UE4.22 cooked layouts only; anything else throws.
/// </summary>
public static class NativePayload
{
    sealed class Reader
    {
        readonly byte[] _b;
        public int P;
        public readonly List<NativeRef> Refs = new();
        public Reader(byte[] b) => _b = b;

        public ReadOnlySpan<byte> Read(int n)
        {
            if (n < 0 || P + n > _b.Length) throw new InvalidDataException("out of bounds at " + P);
            var span = new ReadOnlySpan<byte>(_b, P, n);
            P += n;
            return span;
        }
        public int I() => BinaryPrimitives.ReadInt32LittleEndian(Read(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Read(8));
        public int Count()
        {
            int n = I();
            if (n < 0 || n > 100000) throw new InvalidDataException("invalid count");
            return n;
        }
        public bool Boolean()
        {
            int v = I();
            if (v != 0 && v != 1) throw new InvalidDataException("invalid bool");
            return v == 1;
        }
        public int Ref(string name)
        {
            int p = P, v = I();
            Refs.Add(new NativeRef(p, name, v));
            return v;
        }
        public void Bulk(int size)
        {
            int s = I(), n = Count();
            if (s != size) throw new InvalidDataException("unexpected element size " + s);
            Read(checked(s * n));
        }
        public void End()
        {
            if (P != _b.Length) throw new InvalidDataException("unconsumed bytes " + (_b.Length - P));
        }
    }

    static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    public static NativeInfo Parse(string cls, byte[] b, IReadOnlyList<string> names)
    {
        switch (cls)
        {
            case "InstancedFoliageActor":
            case "FoliageInstancedStaticMeshComponent": return ParseFoliage(cls, b);
            case "Texture2D":
            case "LandscapeHeightfieldCollisionComponent":
            case "LandscapeComponent":
            case "LandscapeMaterialInstanceConstant": return ParseLandscape(cls, b, names);
            default: return ParseClassic(cls, b, names);
        }
    }

    static NativeInfo ParseClassic(string cls, byte[] b, IReadOnlyList<string> names)
    {
        var r = new Reader(b);
        var info = new NativeInfo();
        switch (cls)
        {
            case "StaticMeshComponent":
            {
                int n = r.Count();
                for (int j = 0; j < n; j++)
                {
                    var flags = r.Read(2);
                    if (!(flags[0] == 1 && flags[1] == 1)) throw new InvalidDataException("unhandled LOD strip flags");
                    r.Read(16);
                }
                break;
            }
            case "World":
            {
                r.Ref("PersistentLevel");
                foreach (var name in new[] { "ExtraReferencedObjects", "StreamingLevels" })
                {
                    int n = r.Count();
                    for (int j = 0; j < n; j++) r.Ref(name + ":" + j);
                }
                break;
            }
            case "Model":
            {
                var flags = r.Read(2).ToArray();
                if (!(flags[0] == 1 && (flags[1] == 1 || flags[1] == 0))) throw new InvalidDataException("unhandled model flags");
                r.Read(28);
                r.Bulk(12); r.Bulk(12); r.Bulk(64);
                int surfs = r.Count();
                for (int j = 0; j < surfs; j++)
                {
                    r.Ref($"Surfs:{j}:Material"); r.Read(24);
                    r.Ref($"Surfs:{j}:Actor"); r.Read(24);
                }
                r.Bulk(24);
                r.I(); r.Boolean(); r.Boolean(); r.I();
                if (flags[1] == 0)
                {
                    int n = r.Count();
                    var payload = r.Read(n * 56);
                    for (int o = 0; o < payload.Length; o += 4)
                        if (!float.IsFinite(BitConverter.ToSingle(payload.Slice(o, 4)))) throw new InvalidDataException("nonfinite model vertex data");
                }
                r.Read(16);
                int lightmass = r.Count();
                for (int j = 0; j < lightmass; j++)
                {
                    r.Boolean(); r.Boolean(); r.Read(4); r.Boolean(); r.Boolean(); r.Read(16);
                }
                break;
            }
            case "ModelComponent":
            {
                r.Ref("Model");
                List<int> NodeArray()
                {
                    int n = r.Count();
                    var payload = r.Read(n * 2);
                    var list = new List<int>(n);
                    for (int o = 0; o < payload.Length; o += 2) list.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, 2)));
                    return list;
                }
                int elements = r.Count();
                var elementNodes = new List<int>();
                for (int j = 0; j < elements; j++)
                {
                    r.Read(16);
                    r.Ref($"Elements:{j}:Component");
                    r.Ref($"Elements:{j}:Material");
                    elementNodes.AddRange(NodeArray());
                }
                int componentIndex = r.I();
                if (componentIndex < 0 || componentIndex > 65535) throw new InvalidDataException("invalid model component index");
                var nodes = NodeArray();
                if (elementNodes.Distinct().Count() != elementNodes.Count || nodes.Distinct().Count() != nodes.Count)
                    throw new InvalidDataException("duplicate model component node");
                if (!elementNodes.ToHashSet().SetEquals(nodes)) throw new InvalidDataException("model component node coverage mismatch");
                break;
            }
            case "BodySetup":
            {
                r.Read(16);
                if (!r.Boolean() || !r.Boolean()) throw new InvalidDataException("unhandled uncooked/no-data body");
                int formats = r.Count();
                for (int j = 0; j < formats; j++)
                {
                    int nameIdx = r.I(), instance = r.I();
                    if (nameIdx < 0 || nameIdx >= names.Count) throw new InvalidDataException("invalid FName");
                    int flags = r.I(), count = r.Count(), size = r.Count(), offsetPos = r.P;
                    long offset = r.I64();
                    if (flags != 0x48 || count != size) throw new InvalidDataException("unhandled bulk flags/size");
                    int payloadPos = r.P;
                    var payload = r.Read(size);
                    info.Formats.Add(new NativeFormat(names[nameIdx], instance, flags, size, offsetPos, offset, payloadPos, Sha(payload)));
                }
                break;
            }
            default:
                throw new InvalidDataException("unhandled class " + cls);
        }
        r.End();
        info.ConsumedBytes = r.P;
        info.Refs.AddRange(r.Refs);
        return info;
    }

    static NativeInfo ParseLandscape(string cls, byte[] b, IReadOnlyList<string> names)
    {
        var r = new Reader(b);
        var info = new NativeInfo();
        switch (cls)
        {
            case "LandscapeComponent":
                foreach (int expected in new[] { 2, 0, 0, 1, 0 }) Require(r.I() == expected, "unexpected landscape component layout");
                break;
            case "LandscapeMaterialInstanceConstant":
            {
                int resources = r.Count();
                Require(resources == 2, "expected two material resources");
                for (int k = r.Count(); k > 0; k--)
                {
                    int n = r.Count();
                    var s = r.Read(n);
                    Require(n > 0 && s[^1] == 0, "bad local name");
                    r.Read(4);
                }
                int locCount = r.Count();
                var locs = new List<(int, byte, byte)>();
                for (int k = 0; k < locCount; k++) { int a = r.I(); var two = r.Read(2); locs.Add((a, two[0], two[1])); }
                Require(locs.Count == resources && locs[0] == (0, 3, 1) && locs[1].Item2 == 2 && locs[1].Item3 == 1, "unexpected resource locations");
                int n2 = r.I();
                var payload = r.Read(n2).ToArray();
                Require(0 < locs[1].Item1 && locs[1].Item1 < n2, "resource location out of range");
                foreach (var (start, _, _) in locs)
                    Require(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(start, 4)) == 1 && BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(start + 4, 4)) == 1, "unexpected shader archive header");
                info.Payloads.Add(new NativePayloadInfo("compiled-shader-archive", b.Length, Sha(b)));
                break;
            }
            case "LandscapeHeightfieldCollisionComponent":
            {
                Require(r.Boolean(), "uncooked landscape");
                Require(r.I() == 1, "collision bulk element size");
                int count = r.Count();
                var payload = r.Read(count);
                Require(count >= 8 && Encoding.ASCII.GetString(payload[..8]) == "NXS\x01HFHF", "unexpected PhysX heightfield");
                info.Payloads.Add(new NativePayloadInfo("heightfield", count, Sha(payload)));
                break;
            }
            default: // Texture2D
            {
                var flags = r.Read(4);
                Require(flags[0] == 1 && flags[1] == 0 && flags[2] == 1 && flags[3] == 0, "unhandled texture strip flags");
                Require(r.Boolean(), "uncooked texture");
                string FName()
                {
                    int pos = r.P, idx = r.I(), num = r.I();
                    Require(idx >= 0 && idx < names.Count && num == 0, "bad FName");
                    info.Names.Add(new NativeName(pos, names[idx]));
                    return names[idx];
                }
                var fmt = FName();
                Require(fmt == "PF_B8G8R8A8", "unsupported texture format " + fmt);
                int skipPos = r.P;
                long skip = r.I64();
                int sx = r.I(), sy = r.I(), packed = r.I();
                Require(sx > 0 && sx <= 8192 && sy > 0 && sy <= 8192 && packed == 1, "bad texture size");
                int strlen = r.Count();
                Require(Encoding.ASCII.GetString(r.Read(strlen)) == "PF_B8G8R8A8\0", "bad pixel format string");
                Require(r.I() == 0, "nonzero first mip");
                int mips = r.Count();
                Require(mips >= 1 && mips <= 14, "bad mip count");
                for (int j = 0; j < mips; j++)
                {
                    Require(r.Boolean(), "mip not present");
                    int mflags = r.I(), mcount = r.I(), mbytes = r.I(), offsetPos = r.P;
                    long offset = r.I64();
                    Require(mflags == 0x48 && mcount == mbytes && mbytes > 0 && (long)mbytes <= 4L * 8192 * 8192, "unsupported bulk data");
                    int payloadPos = r.P;
                    var payload = r.Read(mbytes);
                    int x = r.I(), y = r.I(), z = r.I();
                    Require((x, y, z) == (Math.Max(1, sx >> j), Math.Max(1, sy >> j), 1) && mbytes == x * y * 4, "mip dimensions");
                    info.Relocations.Add(new NativeRelocation(offsetPos, payloadPos, offset));
                    info.Payloads.Add(new NativePayloadInfo($"mip{j}", mbytes, Sha(payload)));
                }
                info.Relocations.Add(new NativeRelocation(skipPos, r.P, skip));
                Require(FName() == "None", "expected trailing None");
                break;
            }
        }
        r.End();
        info.ConsumedBytes = r.P;
        return info;
    }

    static float ClusterFloat(ReadOnlySpan<byte> node, int k) => BitConverter.ToSingle(node.Slice(k * 4, 4));
    static int ClusterInt(ReadOnlySpan<byte> node, int k) => BinaryPrimitives.ReadInt32LittleEndian(node.Slice(k * 4, 4));

    static NativeInfo ParseFoliage(string cls, byte[] b)
    {
        var r = new Reader(b);
        var info = new NativeInfo();
        if (cls == "InstancedFoliageActor")
        {
            int n = r.Count();
            Require(n == 1 || n == 3, "unsupported foliage type count");
            for (int j = 0; j < n; j++) { r.Ref("FoliageType" + j); r.Ref("Component" + j); }
        }
        else
        {
            int lod = r.Count();
            for (int k = 0; k < lod; k++)
            {
                var f = r.Read(2);
                Require(f[0] == 1 && f[1] == 1, "bad foliage lod flags");
                r.Read(16);
            }
            Require(r.Boolean(), "foliage flag");
            Require(r.I() == 64, "foliage matrix size");
            int n = r.Count();
            var matrices = r.Read(64 * n);
            for (int j = 0; j < n; j++)
            {
                var m = new float[16];
                for (int k = 0; k < 16; k++) m[k] = BitConverter.ToSingle(matrices.Slice(j * 64 + k * 4, 4));
                Require(m.All(float.IsFinite) && m[3] == 0 && m[7] == 0 && m[11] == 0 && m[15] == 1, "bad foliage matrix");
            }
            ulong renderBytes = BinaryPrimitives.ReadUInt64LittleEndian(r.Read(8));
            int end = checked((int)(r.P + (long)renderBytes));
            if (renderBytes != 0)
            {
                Require(r.Boolean() && r.I() == n, "foliage render header");
                foreach (int stride in new[] { 16, 8, 24 })
                {
                    Require(r.I() == stride && r.I() == n, "foliage render stride");
                    r.Read(stride * n);
                }
            }
            else Require(n == 0, "foliage render bytes missing");
            Require(r.P == end, "foliage render size mismatch");
            Require(r.I() == 64, "foliage cluster node size");
            int clusters = r.Count();
            var nodes = r.Read(64 * clusters);
            for (int j = 0; j < clusters; j++)
            {
                var o = nodes.Slice(j * 64, 64);
                // layout <3fi3fiii6f>: f0-2, i3, f4-6, i7, i8, i9, f10-15
                int child = ClusterInt(o, 3), lastChild = ClusterInt(o, 7), first = ClusterInt(o, 8), last = ClusterInt(o, 9);
                Require(0 <= first && first <= last && last < n, "foliage cluster range");
                float[] lo = { ClusterFloat(o, 0), ClusterFloat(o, 1), ClusterFloat(o, 2) }, hi = { ClusterFloat(o, 4), ClusterFloat(o, 5), ClusterFloat(o, 6) };
                Require(lo[0] <= hi[0] && lo[1] <= hi[1] && lo[2] <= hi[2], "foliage cluster bounds");
                Require((child == -1 && lastChild == -1) || (0 <= child && child <= lastChild && lastChild < clusters), "foliage cluster children");
            }
            info.Payloads.Add(new NativePayloadInfo("foliage-instance-render-cluster", b.Length, Sha(b)));
        }
        r.End();
        info.ConsumedBytes = r.P;
        info.Refs.AddRange(r.Refs);
        return info;
    }
}
