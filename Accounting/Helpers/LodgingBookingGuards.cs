using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านของการจองที่พักที่ไม่ใช่ราคา — ตัวตัดสินตัวเดียว** (รอบ 202 ทีม LO)
///
/// <list type="bullet">
///   <item><b>O-P1-3</b> แผนราคาที่แขกส่ง id มาเอง ต้องผ่านเงื่อนไขของแผน (ช่วงวัน · คืนขั้นต่ำ/สูงสุด · จองล่วงหน้า · วันในสัปดาห์) —
///     เดิมตรวจแค่ "แผนนี้ใช้กับประเภทห้องนี้ได้" ⇒ ส่ง id แผนโปรโมชันนอกช่วงมาเองได้ราคาโปร · พนักงานเลือกแผนนอกเงื่อนไขได้ (ตั้งใจให้ส่วนลดพิเศษ)</item>
///   <item><b>O-P1-6 / คำตัดสินข้อ 122</b> การจองสาธารณะที่ยังรอชำระ ≤ 3 ใบต่อเบอร์/อีเมลต่อที่พัก + เพดานต่อ IP (นับใน DB ผ่าน
///     <c>IChatRateLimiter</c> — ข้ามเครื่องได้ ไม่มี static dict) — เดิมสคริปต์เดียวกันห้องทั้งที่พักได้ด้วย POST เปล่า ๆ</item>
///   <item><b>P2 PromoCode</b> — ไม่มีเครื่องคิดโค้ดส่วนลด ⇒ ปฏิเสธพร้อมข้อความ (เดิมรับแล้วคิด 0 เงียบ = silent no-op)</item>
///   <item><b>P2 เลื่อนวัน</b> — จับคู่บรรทัดเดิมกับบรรทัดที่คิดราคาใหม่ด้วย "กุญแจ" ไม่ใช่ลำดับ (ตัวคิดราคาจัดกลุ่มตามประเภทห้อง ⇒ ลำดับเปลี่ยน ·
///     บริการเสริมที่ถูกลบจากผังหายจากรายการคิดราคา ⇒ index เลื่อน ⇒ ยอดผิดบรรทัด)</item>
/// </list>
/// </summary>
public static class LodgingBookingGuards
{
    /// <summary>จำนวนการจองที่ยังรอชำระสูงสุดต่อเบอร์/อีเมลต่อที่พัก (คำตัดสินข้อ 122)</summary>
    public const int MaxPendingPerGuest = 3;

    /// <summary>เพดาน POST จองสาธารณะต่อ IP ต่อเว็บ — ต่อนาที / ต่อวัน (ผู้จองจริงไม่กดเกินนี้ · สำนักงาน/NAT ยังพอ)</summary>
    public const int PublicCreatePerMinute = 5;
    public const int PublicCreatePerDay = 30;

    /// <summary>เงื่อนไขของแผนราคา ณ วันเช็คอิน/จำนวนคืน/วันนี้ (ปฏิทินไทย) — ตัวเดียวของการเลือกแผนอัตโนมัติและการตรวจแผนที่แขกส่งมา</summary>
    public static bool RatePlanApplies(DateTime? validFrom, DateTime? validTo, int? minNights, int? maxNights,
        int? minAdvanceDays, int? maxAdvanceDays, int applicableDaysMask, DateTime checkIn, int nights, DateTime todayThai)
    {
        var d = checkIn.Date;
        if (validFrom is DateTime vf && d < vf.Date) return false;
        if (validTo is DateTime vt && d > vt.Date) return false;
        if (minNights is int mn && nights < mn) return false;
        if (maxNights is int mx && nights > mx) return false;
        var lead = (int)(d - todayThai.Date).TotalDays;
        if (minAdvanceDays is int mad && lead < mad) return false;
        if (maxAdvanceDays is int xad && lead > xad) return false;
        if (applicableDaysMask != 0 && (applicableDaysMask & LodgingPricingEngine.DayMask(d.DayOfWeek)) == 0) return false;
        return true;
    }

    /// <summary>แผนที่ผู้เรียกระบุ id มาเอง — แขกต้องผ่านเงื่อนไข · พนักงานข้ามได้ (null = ใช้ได้)</summary>
    public static string? ExplicitRatePlanProblem(bool isStaff, bool applies, string planName)
        => isStaff || applies ? null
            : $"แผนราคา “{planName}” ใช้กับวันที่/จำนวนคืนที่เลือกไม่ได้ (นอกช่วงโปรโมชัน หรือไม่ตรงเงื่อนไขจำนวนคืน/การจองล่วงหน้า) — กรุณาเลือกแผนอื่น";

    /// <summary>โค้ดส่วนลด — ระบบที่พักยังไม่มีเครื่องคิดโค้ด ⇒ ไม่ว่าง = ปฏิเสธพร้อมทางไปต่อ (null = ไม่มีปัญหา)</summary>
    public static string? PromoCodeProblem(string? promoCode)
        => string.IsNullOrWhiteSpace(promoCode) ? null
            : "ระบบจองของที่พักนี้ยังไม่รองรับโค้ดส่วนลด — กรุณาลบโค้ดออกแล้วจองตามราคาปกติ (ถ้าได้รับโค้ดจากที่พัก กรุณาติดต่อที่พักโดยตรง)";

    /// <summary>คำตัดสินเจ้าของข้อ 126: ค่าปรับยกเลิก/no-show ไม่เกินมัดจำที่รับไว้ (ไม่ติดลบ) — ไม่มีส่วนต่างที่ต้องเรียกเก็บเพิ่ม</summary>
    public static decimal CappedCancellationFee(decimal policyFee, decimal depositReceived)
        => Math.Max(0m, Math.Min(policyFee, Math.Max(0m, depositReceived)));

    /// <summary>เบอร์โทรเทียบกันได้ — ตัวเลขล้วน · +66/66 นำหน้า ⇒ 0 (null = ไม่มีเบอร์)</summary>
    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("66", StringComparison.Ordinal)) digits = "0" + digits[2..];
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>อีเมลเทียบกันได้ — ตัดช่องว่าง + ตัวพิมพ์เล็ก (null = ไม่มีอีเมล)</summary>
    private static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(e) ? null : e;
    }

    /// <summary>นับการจองที่ยังรอชำระของผู้จองคนนี้ (เบอร์ <i>หรือ</i> อีเมลตรง — เปลี่ยนรูปแบบการพิมพ์เบอร์ไม่ช่วยหลบ)</summary>
    public static int CountPendingForGuest(IEnumerable<(string? Phone, string? Email)> pending, string? phone, string? email)
    {
        var p = NormalizePhone(phone); var e = NormalizeEmail(email);
        if (p == null && e == null) return 0;
        return pending.Count(x => (p != null && NormalizePhone(x.Phone) == p) || (e != null && NormalizeEmail(x.Email) == e));
    }

    /// <summary>ข้อความปฏิเสธเมื่อถึงเพดานการจองที่ยังรอชำระ (null = จองต่อได้)</summary>
    public static string? PendingCapProblem(int existingPending)
        => existingPending < MaxPendingPerGuest ? null
            : $"มีการจองที่ยังรอชำระมัดจำจากเบอร์โทร/อีเมลนี้ครบ {MaxPendingPerGuest} รายการแล้ว — กรุณาชำระหรือยกเลิกรายการเดิมก่อน "
              + "(เปิดลิงก์การจองในอีเมล) หรือติดต่อที่พักโดยตรง";

    /// <summary>คีย์เพดานต่อ IP ของการจองสาธารณะ — แฮช IP (ไม่เก็บ IP ดิบในตารางนับ · PDPA) · ต่อเว็บ</summary>
    public static string PublicCreateRateKey(Guid siteId, string? remoteIp)
    {
        var raw = Encoding.UTF8.GetBytes((remoteIp ?? "unknown").Trim());
        var hash = Convert.ToHexString(SHA256.HashData(raw))[..24].ToLowerInvariant();
        return $"lodging-res:{siteId:N}:{hash}";
    }

    /// <summary>จับคู่บรรทัดเดิมกับบรรทัดที่คิดราคาใหม่ด้วยกุญแจ (ซ้ำได้ — จับตามลำดับภายในกุญแจเดียวกัน) · คืน index ของบรรทัดใหม่ต่อบรรทัดเดิม
    /// (null = ไม่มีคู่ — ผู้เรียกต้องจัดการ ห้ามเดาเอาบรรทัดถัดไป)</summary>
    public static IReadOnlyList<int?> PairByKey<TKey>(IReadOnlyList<TKey> existingKeys, IReadOnlyList<TKey> quotedKeys) where TKey : notnull
    {
        var queues = new Dictionary<TKey, Queue<int>>();
        for (var i = 0; i < quotedKeys.Count; i++)
        {
            if (!queues.TryGetValue(quotedKeys[i], out var q)) queues[quotedKeys[i]] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var result = new List<int?>(existingKeys.Count);
        foreach (var k in existingKeys)
            result.Add(queues.TryGetValue(k, out var q) && q.Count > 0 ? q.Dequeue() : null);
        return result;
    }
}
