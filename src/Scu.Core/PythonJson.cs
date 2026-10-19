using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Scu.Core;

/// <summary>
/// Writes a JSON document byte-for-byte the way the randomizer's local_map_materials does (Python json.JSONEncoder with
/// ensure_ascii=False): indent 2 or compact separators, Python float repr, optional CRLF / BOM / final newline.
/// The documents it writes are hash-pinned by the randomizer, so the bytes have to match exactly.
/// </summary>
public static class PythonJson
{
    public static byte[] Format(ReadOnlyMemory<byte> json, JsonElement row)
    {
        bool compact = row.TryGetProperty("format", out var f) && f.GetString() == "compact";
        bool crlf = row.TryGetProperty("newline", out var n) && n.GetString() == "crlf";
        bool bom = row.TryGetProperty("bom", out var b) && b.ValueKind == JsonValueKind.True;
        bool finalNewline = row.TryGetProperty("final_newline", out var fn) && fn.ValueKind == JsonValueKind.True;
        bool upper = row.TryGetProperty("uppercase_exponents", out var u) && u.ValueKind == JsonValueKind.True;

        using var doc = JsonDocument.Parse(json, Prepare.DeepJson);
        var text = new StringBuilder(json.Length * 2);
        Write(text, doc.RootElement, compact, 0, upper);
        var result = text.ToString();
        if (crlf) result = result.Replace("\n", "\r\n");
        if (finalNewline) result += crlf ? "\r\n" : "\n";
        var body = new UTF8Encoding(false).GetBytes(result);
        if (!bom) return body;
        return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
    }

    static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);

    static void Write(StringBuilder sb, JsonElement e, bool compact, int level, bool upper)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var props = e.EnumerateObject().ToList();
                if (props.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (!compact) { sb.Append('\n'); Indent(sb, level + 1); }
                    String(sb, props[i].Name);
                    sb.Append(compact ? ":" : ": ");
                    Write(sb, props[i].Value, compact, level + 1, upper);
                }
                if (!compact) { sb.Append('\n'); Indent(sb, level); }
                sb.Append('}');
                return;
            }
            case JsonValueKind.Array:
            {
                int count = e.GetArrayLength();
                if (count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                int i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (i++ > 0) sb.Append(',');
                    if (!compact) { sb.Append('\n'); Indent(sb, level + 1); }
                    Write(sb, item, compact, level + 1, upper);
                }
                if (!compact) { sb.Append('\n'); Indent(sb, level); }
                sb.Append(']');
                return;
            }
            case JsonValueKind.String: String(sb, e.GetString()!); return;
            case JsonValueKind.Number: Number(sb, e.GetRawText(), upper); return;
            case JsonValueKind.True: sb.Append("true"); return;
            case JsonValueKind.False: sb.Append("false"); return;
            default: sb.Append("null"); return;
        }
    }

    /// <summary>json.encoder.encode_basestring (non-ASCII stays as is).</summary>
    static void String(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    static void Number(StringBuilder sb, string raw, bool upper)
    {
        // integers keep their digits; anything with a fraction or exponent was a Python float
        if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) { sb.Append(raw); return; }
        sb.Append(Repr(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture), upper));
    }

    /// <summary>Python's float.__repr__ (shortest round-trip digits; scientific form outside 1e-4 .. 1e16).</summary>
    public static string Repr(double d, bool upperExponent = false)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";

        string s = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        int exp = 0;
        int e = s.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exp = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..e];
        }
        int dot = s.IndexOf('.');
        string whole = dot < 0 ? s : s[..dot];
        string frac = dot < 0 ? "" : s[(dot + 1)..];
        string digits = whole + frac;
        int point = whole.Length + exp;   // the decimal point sits after this many digits
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..];
        point -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0.0";

        int sci = point - 1;   // exponent of the first digit
        var sb = new StringBuilder();
        if (d < 0) sb.Append('-');
        if (sci >= -4 && sci < 16)
        {
            if (point <= 0) sb.Append("0.").Append('0', -point).Append(digits);
            else if (point >= digits.Length) sb.Append(digits).Append('0', point - digits.Length).Append(".0");
            else sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        }
        else
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append(upperExponent ? 'E' : 'e').Append(sci < 0 ? '-' : '+').Append(Math.Abs(sci).ToString("00", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
