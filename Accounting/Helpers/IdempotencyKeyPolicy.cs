using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175) — <b>ตัวตั้งเดียว</b>ของกติกา <c>Idempotency-Key</c> ทุกเส้น <c>/api/*</c> (POST/PUT/PATCH) ·
/// ผู้ใช้เดียวคือ <see cref="Accounting.Middleware.IdempotencyMiddleware"/> (ที่เก็บ = ตาราง <c>IdempotencyRecords</c> · ข้ามเครื่องได้ — กฎ §D) ·
/// หน้าเว็บส่งคีย์ผ่าน <c>wwwroot/js/submit-once.js</c> ตัวเดียว
/// <list type="bullet">
///   <item>คีย์เดิม + เนื้อคำขอเดิม (แฮชตรง) ⇒ คืนผลเดิม (replay · ไม่ทำงานซ้ำ)</item>
///   <item>คีย์เดิม + เนื้อคำขอต่าง ⇒ 409 <see cref="StatusMismatch"/> (คีย์ถูกใช้กับคำขออื่นแล้ว — ไม่ replay ผลของคำขออื่นให้)</item>
///   <item>คีย์เดิมกำลังทำงาน ⇒ 409 <see cref="StatusInFlight"/> + <c>Retry-After</c> (หน้าเว็บรอแล้วยิงคีย์เดิมซ้ำ ⇒ ได้ผลเดิม)</item>
/// </list>
/// pure — ไม่มี I/O (ทดสอบได้ใน <c>IdempotencyKeyPolicyTests</c>)
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

    /// <summary>อายุของแถว — สัญญาเดิมกับ partner (24 ชม.) · แถวเก่ากว่านี้ยึดคีย์ทำใหม่ได้ และถูกล้างโดย ChatRetentionPurgeJob</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public const string BadKeyMessage = "Idempotency-Key must be 8-128 chars (UUID recommended).";
    public const string InFlightMessage = "คำขอนี้กำลังดำเนินการอยู่ — โปรดรอสักครู่ ระบบจะแสดงผลเดิมให้ (ไม่บันทึกซ้ำ)";
    public const string MismatchMessage = "คำขอนี้ถูกส่งไปแล้วด้วยข้อมูลชุดอื่น — โปรดตรวจรายการของคุณก่อน (เช่น \"การจองของฉัน\") แล้วกดส่งใหม่อีกครั้ง";

    /// <summary>คีย์ที่ใช้ได้ (ตัดช่องว่าง) — null + ข้อความ = ไม่ผ่าน · null + null = ไม่ได้ส่งมา (ปล่อยผ่านตามเดิม)</summary>
    public static (string? Key, string? Problem) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var k = raw.Trim();
        return k.Length < MinKeyLength || k.Length > MaxKeyLength ? (null, BadKeyMessage) : (k, null);
    }

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

    /// <summary>แฮชของคำขอ (SHA-256 hex) — method + path + query + เนื้อ · ใช้ตัดสิน "คีย์เดิมแต่เนื้อต่าง"</summary>
    public static IncrementalHash BeginRequestHash(string method, string pathAndQuery)
    {
        var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes(method.ToUpperInvariant() + "\n" + pathAndQuery + "\n"));
        return h;
    }

    /// <summary>แฮชในประโยคเดียว (เทสต์/เนื้อสั้น) — ผลเท่ากับ <see cref="BeginRequestHash"/> + AppendData(body) ทุกไบต์</summary>
    public static string RequestHash(string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        using var h = BeginRequestHash(method, pathAndQuery);
        h.AppendData(body);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public enum Decision
    {
        /// <summary>แถวหมดอายุ ⇒ ยึดคีย์ทำใหม่</summary>
        Reclaim,
        /// <summary>เสร็จแล้ว + เนื้อตรง ⇒ คืนผลเดิม</summary>
        Replay,
        /// <summary>ยังทำงานอยู่ (หรือรอบก่อนล้มกลางคันยังไม่ถูกปล่อย) ⇒ 409 รอแล้วลองใหม่</summary>
        InFlight,
        /// <summary>คีย์เดิมแต่เนื้อคำขอต่าง ⇒ 409 ไม่ replay</summary>
        Mismatch,
    }

    /// <summary>ตัดสินเมื่อจองคีย์ไม่ได้ (มีแถวอยู่แล้ว) — <paramref name="storedHash"/> null = แถวรุ่นก่อนรอบ 217 (ไม่มีแฮช ⇒ ถือว่าตรง)</summary>
    public static Decision Decide(string? storedStatus, string? storedHash, bool hasBody, DateTime storedCreatedAtUtc, string requestHash, DateTime nowUtc)
    {
        if (storedCreatedAtUtc < nowUtc - Retention) return Decision.Reclaim;
        if (storedHash != null && !string.Equals(storedHash, requestHash, StringComparison.Ordinal)) return Decision.Mismatch;
        return storedStatus == "Done" && hasBody ? Decision.Replay : Decision.InFlight;
    }
}
