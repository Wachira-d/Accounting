using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ลายเซ็น AWS Signature Version 4 สำหรับ S3-compatible object storage — ตัวตั้งตัวเดียว** (ทีม FS · 2026-11-02)
///
/// <para>ใช้กับ AWS S3 · Cloudflare R2 · MinIO · DigitalOcean Spaces (ทุกเจ้ารับ SigV4 แบบเดียวกัน) · เขียนเองแทน AWSSDK.S3
/// เพราะใช้แค่ 4 คำสั่ง (PUT/GET/HEAD/DELETE) — ไม่ลาก dependency ทั้งชุด (แนวเดียวกับ MiniExcel "zero transitive deps") และ
/// ล็อกสูตรได้ด้วยเวกเตอร์ทดสอบทางการของ AWS (<c>S3SigV4Tests</c>) · hash/signature มีฟังก์ชันเดียว (กฎเหล็ก #4 C)</para>
/// </summary>
public static class S3SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    /// <summary>SHA-256 ของ payload ว่าง (GET/HEAD/DELETE)</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>เข้ารหัส object key ตาม RFC 3986 ทีละช่วง (คง <c>/</c>) — S3 ไม่ double-encode canonical URI</summary>
    public static string EncodeKeyPath(string key)
        => string.Join('/', key.Split('/').Select(UriEncode));

    /// <summary>RFC 3986 unreserved (A-Z a-z 0-9 - _ . ~) ผ่านตรง · ที่เหลือ %XX ตัวพิมพ์ใหญ่ของไบต์ UTF-8</summary>
    public static string UriEncode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>รูปวันที่ของ header <c>x-amz-date</c></summary>
    public static string AmzDate(DateTime utc) => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// คำนวณลายเซ็นของคำขอ — <paramref name="headers"/> = header ที่จะเซ็น (ชื่อตัวเล็ก → ค่า) ต้องมี <c>host</c> ·
    /// <c>x-amz-date</c> · <c>x-amz-content-sha256</c> ครบ (ผู้เรียกใส่ header ชุดเดียวกันลงคำขอจริง)
    /// </summary>
    public static string Signature(string method, string canonicalUri, string canonicalQuery,
        IReadOnlyDictionary<string, string> headers, string payloadHash,
        string amzDate, string region, string secretKey, string service = "s3")
    {
        var names = headers.Keys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var lookup = headers.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
        var canonicalHeaders = string.Concat(names.Select(n => n + ":" + CollapseSpaces(lookup[n]) + "\n"));
        var signedHeaders = string.Join(';', names);
        var canonicalRequest = string.Join('\n', method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        var date = amzDate[..8];
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        return Convert.ToHexString(Hmac(kSigning, stringToSign)).ToLowerInvariant();
    }

    /// <summary>ค่า header <c>Authorization</c> ทั้งบรรทัด</summary>
    public static string Authorization(string accessKey, string amzDate, string region, IEnumerable<string> headerNames, string signature, string service = "s3")
    {
        var signed = string.Join(';', headerNames.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));
        return $"{Algorithm} Credential={accessKey}/{amzDate[..8]}/{region}/{service}/aws4_request, SignedHeaders={signed}, Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string CollapseSpaces(string v)
    {
        var t = v.Trim();
        while (t.Contains("  ", StringComparison.Ordinal)) t = t.Replace("  ", " ", StringComparison.Ordinal);
        return t;
    }
}
