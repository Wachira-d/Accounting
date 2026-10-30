using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175) — <b>ตัวตั้งเดียว</b>ของกติกา <c>Idempotency-Key</c> ทุกเส้น <c>/api/*</c> (POST/PUT/PATCH) ·
/// ผู้ใช้เดียวคือ <see cref="Accounting.Middleware.IdempotencyMiddleware"/> (ที่เก็บ = ตาราง <c>IdempotencyRecords</c> · ข้ามเครื่องได้ — กฎ §D) ·
/// หน้าเว็บส่งคีย์ผ่าน <c>wwwroot/js/submit-once.js</c> ตัวเดียว
/// <list type="bullet">
///   <item>คีย์เดิม + เนื้อคำขอเดิม (ลายนิ้วมือตรง) ⇒ คืนผลเดิม (replay · ไม่ทำงานซ้ำ)</item>
///   <item>คีย์เดิม + เนื้อคำขอต่าง (คำขอเดิมจบแล้ว) ⇒ 409 <see cref="StatusMismatch"/></item>
///   <item>คีย์เดิมกำลังทำงาน ⇒ 409 <see cref="StatusInFlight"/> + <c>Retry-After</c> — <b>ตัดสินก่อนเทียบเนื้อ</b> (ฝ่ายค้าน S2: retry ระหว่างรอต้องไม่กลายเป็น mismatch)</item>
///   <item>InFlight ค้างเกิน <see cref="StaleInFlightAfter"/> (เครื่องล้มกลางคัน) ⇒ ยึดคืนได้ (ฝ่ายค้าน S5 — เดิมค้าง 24 ชม.)</item>
/// </list>
/// <b>ลายนิ้วมือ = HMAC-SHA256 ด้วยกุญแจของเซิร์ฟเวอร์</b> (ฝ่ายค้าน S6: แฮชเปล่าของเนื้อที่มีรหัสผ่าน = เดาย้อนได้) ·
/// multipart ไม่รวม boundary (สุ่มทุกครั้ง — ฝ่ายค้าน S2) · เส้นที่ถือรหัสผ่าน/โทเคน (<see cref="IsCredentialPath"/>) ไม่เข้าระบบคีย์เลย ·
/// เก็บเฉพาะผลสำเร็จ (<see cref="ShouldStore"/>) · อายุคำนวณใน SQL (<c>now() - "CreatedAt"</c> — ไม่พึ่ง DateTimeKind ของ legacy timestamp)
/// </summary>
public static class IdempotencyKeyPolicy
{
    public const string Header = "Idempotency-Key";
    /// <summary>header ตอบกลับที่บอกชนิดของ 409 — หน้าเว็บแยก "รอแล้วลองใหม่" ออกจาก "คีย์ชนคำขออื่น" และ 409 ทางธุรกิจ</summary>
    public const string StatusHeader = "Idempotency-Status";
    public const string StatusInFlight = "in-flight";
    public const string StatusMismatch = "mismatch";
    public const string StatusReplayed = "replayed";
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 128;
    public const int MaxCacheKeyLength = 400;
    /// <summary>วินาทีที่บอกให้รอ (Retry-After) ตอนคำขอเดิมยังทำงานอยู่</summary>
    public const int InFlightRetryAfterSeconds = 2;
    /// <summary>ป้ายของกุญแจลูกที่แยกจากกุญแจเซ็น JWT (HMAC(label) — ไม่ใช้กุญแจ JWT ตรง ๆ)</summary>
    public const string KeyDerivationLabel = "nextacc:idempotency-request-hash:v1";

    /// <summary>อายุของแถว — สัญญาเดิมกับ partner (24 ชม.) · แถวเก่ากว่านี้ยึดคีย์ทำใหม่ได้ และถูกล้างโดย ChatRetentionPurgeJob</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    /// <summary>InFlight ที่เก่ากว่านี้ = คำขอที่เครื่องล้มกลางคัน (คำขอปกติจบในไม่กี่วินาที) ⇒ ยึดคืนได้</summary>
    public static readonly TimeSpan StaleInFlightAfter = TimeSpan.FromMinutes(2);

    public const string BadKeyMessage = "Idempotency-Key must be 8-128 chars (UUID recommended).";
    public const string InFlightMessage = "คำขอนี้กำลังดำเนินการอยู่ — โปรดรอสักครู่ ระบบจะแสดงผลเดิมให้ (ไม่บันทึกซ้ำ)";
    public const string MismatchMessage = "คำขอนี้ถูกส่งไปแล้วด้วยข้อมูลชุดอื่น — โปรดตรวจรายการของคุณก่อน (เช่น \"การจองของฉัน\") แล้วกดส่งใหม่อีกครั้ง";
    public const string MissingSecretMessage = "ไม่มีกุญแจของเซิร์ฟเวอร์สำหรับลายนิ้วมือคำขอ (Jwt:Secret/JWT_SECRET) — ตั้งค่าก่อนเปิดระบบ";

    /// <summary>คีย์ที่ใช้ได้ (ตัดช่องว่าง) — null + ข้อความ = ไม่ผ่าน · null + null = ไม่ได้ส่งมา (ปล่อยผ่านตามเดิม)</summary>
    public static (string? Key, string? Problem) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var k = raw.Trim();
        return k.Length < MinKeyLength || k.Length > MaxKeyLength ? (null, BadKeyMessage) : (k, null);
    }

    /// <summary>ฝ่ายค้าน S6: เส้นที่เนื้อคำขอ/คำตอบถือรหัสผ่านหรือโทเคน (ล็อกอิน · สมัคร · รีเซ็ต/เปลี่ยนรหัส · refresh) — ไม่เก็บอะไรเลย
    /// (ไม่มีลายนิ้วมือ · ไม่เก็บโทเคนในคำตอบไว้ replay) · กันซ้ำด้วยเงื่อนไขธุรกิจของเส้นนั้นเอง (อีเมลซ้ำ ฯลฯ)</summary>
    public static bool IsCredentialPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.ToLowerInvariant();
        foreach (var seg in CredentialSegments)
            if (p.Contains(seg, StringComparison.Ordinal)) return true;
        return false;
    }
    private static readonly string[] CredentialSegments =
        { "/login", "/register", "/password", "/refresh", "/token", "/signup", "/sso", "/otp", "/2fa", "/mfa" };

    /// <summary>namespace ของผู้เรียก — กันคนอื่นที่บังเอิญ/ตั้งใจใช้คีย์เดียวกันเห็นผลของเรา:
    /// บริษัทจาก claim (API key) → ชื่อผู้ใช้ → <b>แฮชของ credential header</b> (Authorization / X-Storefront-Token — มิดเดิลแวร์อยู่ก่อน
    /// UseAuthentication ⇒ claim ของ JWT ยังไม่มี) → IP (นิรนาม)</summary>
    public static string Subject(string? companyClaim, string? userName, string? credentialHeader, string? remoteIp)
    {
        if (!string.IsNullOrWhiteSpace(companyClaim)) return companyClaim;
        if (!string.IsNullOrWhiteSpace(userName)) return userName;
        if (!string.IsNullOrWhiteSpace(credentialHeader))
            return "cred:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialHeader.Trim())))[..32].ToLowerInvariant();
        return $"anon:{remoteIp}";
    }

    /// <summary>กุญแจแถวในตาราง (unique) — ผู้เรียก + method + path + คีย์</summary>
    public static string CacheKey(string subject, string method, string path, string key)
    {
        var ck = $"idem:{subject}:{method}:{path}:{key}";
        return ck.Length > MaxCacheKeyLength ? ck[..MaxCacheKeyLength] : ck;
    }

    /// <summary>กุญแจลูกสำหรับลายนิ้วมือ จากความลับของเซิร์ฟเวอร์ (JWT secret — fail-fast ใน production อยู่แล้วที่ Program.cs) ·
    /// ว่าง ⇒ โยน (§4C: ห้ามถอยไปกุญแจ dev เงียบ ๆ)</summary>
    public static byte[] DeriveHashKey(string? serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret)) throw new InvalidOperationException(MissingSecretMessage);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), Encoding.UTF8.GetBytes(KeyDerivationLabel));
    }

    /// <summary>เริ่มลายนิ้วมือ (HMAC-SHA256) — method + path + query</summary>
    public static IncrementalHash BeginRequestHash(byte[] hashKey, string method, string pathAndQuery)
    {
        var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hashKey);
        h.AppendData(Encoding.UTF8.GetBytes(method.ToUpperInvariant() + "\n" + pathAndQuery + "\n"));
        return h;
    }

    /// <summary>ลายนิ้วมือของเนื้อธรรมดา (JSON ฯลฯ) ในประโยคเดียว — ผลเท่ากับ <see cref="BeginRequestHash"/> + AppendData(body) ทุกไบต์</summary>
    public static string RequestHash(byte[] hashKey, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        using var h = BeginRequestHash(hashKey, method, pathAndQuery);
        h.AppendData(body);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public sealed record MultipartField(string Name, string Value);
    public sealed record MultipartFile(string Name, string? FileName, string? ContentType, byte[] ContentSha256);

    /// <summary>ฝ่ายค้าน S2: ลายนิ้วมือของ multipart <b>ไม่รวม boundary</b> (เบราว์เซอร์สุ่มใหม่ทุกครั้ง ⇒ เดิมแฮชต่างทุกครั้ง = mismatch ปลอม) ·
    /// ใช้ชื่อ+ค่าของช่อง และชื่อ/ชื่อไฟล์/ชนิด/SHA-256 ของเนื้อไฟล์ เรียงตามชื่อ (ลำดับช่องไม่มีผล)</summary>
    public static string MultipartHash(byte[] hashKey, string method, string pathAndQuery,
        IEnumerable<MultipartField> fields, IEnumerable<MultipartFile> files)
    {
        using var h = BeginRequestHash(hashKey, method, pathAndQuery);
        h.AppendData(Encoding.UTF8.GetBytes("multipart\n"));
        foreach (var f in fields.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Value, StringComparer.Ordinal))
            h.AppendData(Encoding.UTF8.GetBytes($"f:{f.Name.Length}:{f.Name}={f.Value.Length}:{f.Value}\n"));
        foreach (var f in files.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.FileName, StringComparer.Ordinal))
            h.AppendData(Encoding.UTF8.GetBytes($"x:{f.Name}|{f.FileName}|{f.ContentType}|{Convert.ToHexString(f.ContentSha256)}\n"));
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsMultipart(string? contentType)
        => contentType != null && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>ฝ่ายค้านข้อสังเกต 1: เก็บไว้ replay เฉพาะ<b>ผลสำเร็จ</b> — 2xx และไม่ใช่ JSON ที่บอก <c>"success": false</c>
    /// (บางเส้นตอบ 200 + success:false = ล้มทางธุรกิจ — ต้องให้แก้แล้วส่งคีย์เดิมได้ ไม่ replay ความล้มเหลว)</summary>
    public static bool ShouldStore(int statusCode, string? contentType, ReadOnlySpan<byte> body)
    {
        if (statusCode is < 200 or >= 300) return false;
        if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || body.Length == 0) return true;
        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "success", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.False)
                        return false;
            return true;
        }
        catch (JsonException) { return true; }
    }

    public enum Decision
    {
        /// <summary>แถวหมดอายุ (24 ชม.) หรือ InFlight ค้าง (เครื่องล้ม) ⇒ ยึดคีย์ทำใหม่ (ผู้เรียกยึดแบบมีเงื่อนไข — ได้ 0 แถว = มีคนยึดไปก่อน ⇒ InFlight)</summary>
        Reclaim,
        /// <summary>เสร็จแล้ว + เนื้อตรง ⇒ คืนผลเดิม</summary>
        Replay,
        /// <summary>ยังทำงานอยู่ ⇒ 409 รอแล้วลองใหม่ (ไม่เทียบเนื้อ)</summary>
        InFlight,
        /// <summary>เสร็จแล้วแต่เนื้อคำขอต่าง ⇒ 409 ไม่ replay</summary>
        Mismatch,
    }

    /// <summary>ตัดสินเมื่อจองคีย์ไม่ได้ (มีแถวอยู่แล้ว) — <paramref name="ageSeconds"/> คำนวณใน SQL (<c>now() - "CreatedAt"</c>) ·
    /// <paramref name="storedHash"/> null = แถวรุ่นก่อนรอบ 217 (ไม่มีลายนิ้วมือ ⇒ ถือว่าตรง)</summary>
    public static Decision Decide(string? storedStatus, string? storedHash, bool hasBody, double ageSeconds, string requestHash)
    {
        if (ageSeconds > Retention.TotalSeconds) return Decision.Reclaim;
        if (storedStatus != "Done" || !hasBody)
            return ageSeconds > StaleInFlightAfter.TotalSeconds ? Decision.Reclaim : Decision.InFlight;
        if (storedHash != null && !string.Equals(storedHash, requestHash, StringComparison.Ordinal)) return Decision.Mismatch;
        return Decision.Replay;
    }
}
