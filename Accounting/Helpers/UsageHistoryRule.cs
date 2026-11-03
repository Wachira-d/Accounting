using System.Globalization;
using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §D · คำตัดสินข้อ 231-6) — <b>กติกาของ snapshot/ประวัติการใช้งานรายเดือน (pure)</b>: งวด · ป้ายไทย · เพดานที่เก็บ ·
/// การรวมค่า (Capacity = สูงสุดของเดือน · Consumable = ค่าล่าสุด) · เก็บ <see cref="RetentionMonths"/> เดือน
/// </summary>
public static class UsageHistoryRule
{
    public const int RetentionMonths = 24;
    public const int DefaultHistoryMonths = 12;

    /// <summary>งวดของเวลา UTC (เดือนไทย · ตัวเดียวกับบิล/มิเตอร์ — <see cref="AddOnBilling.PeriodOf"/>)</summary>
    public static string PeriodOf(DateTime utc) => AddOnBilling.PeriodOf(utc);

    /// <summary>งวดที่ตัวนับบน <c>Subscription</c> "กำลังจะปิด" ณ ตอนขึ้นเดือนใหม่ = เดือนก่อนวันรีเซ็ต (ตัวนับเหล่านี้เดินตามเดือน UTC — <see cref="SubscriptionUsageRollover"/>) ·
    /// ค้างหลายเดือน (ไม่มีใครแตะ) ก็ยังเป็นเดือนที่ตัวนับเริ่มเดิน ไม่ใช่เดือนปัจจุบัน</summary>
    public static string ClosingPeriod(DateTime usageResetDateUtc)
        => usageResetDateUtc.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>รายการงวดย้อนหลัง (ใหม่ → เก่า) รวมเดือนปัจจุบัน · ไม่เกิน <see cref="RetentionMonths"/></summary>
    public static IReadOnlyList<string> Periods(DateTime nowUtc, int months)
    {
        months = Math.Clamp(months, 1, RetentionMonths);
        var th = nowUtc.AddHours(7);
        var first = new DateTime(th.Year, th.Month, 1);
        var list = new List<string>(months);
        for (var i = 0; i < months; i++) list.Add(first.AddMonths(-i).ToString("yyyy-MM", CultureInfo.InvariantCulture));
        return list;
    }

    public static bool TryParsePeriod(string? period, out int year, out int month)
    {
        year = month = 0;
        if (period == null || period.Length != 7 || period[4] != '-') return false;
        return int.TryParse(period[..4], NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && int.TryParse(period[5..], NumberStyles.None, CultureInfo.InvariantCulture, out month) && month is >= 1 and <= 12;
    }

    /// <summary>ช่วง UTC ของงวดไทย [เริ่ม, สิ้นสุด)</summary>
    public static (DateTime StartUtc, DateTime EndUtc) PeriodRangeUtc(string period)
    {
        if (!TryParsePeriod(period, out var y, out var m)) throw new ArgumentException("งวดต้องเป็น yyyy-MM", nameof(period));
        var start = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-7);
        return (start, start.AddMonths(1));
    }

    private static readonly string[] ThaiMonths = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    /// <summary>"2026-11" ⇒ "พ.ย. 2569"</summary>
    public static string Label(string period)
        => TryParsePeriod(period, out var y, out var m) ? $"{ThaiMonths[m - 1]} {y + 543}" : period;

    /// <summary>เพดานที่เก็บใน snapshot: ไม่จำกัด (ค่าลบ/ค่าแทน/ไม่ตั้ง) = null — เกณฑ์เดียวกับ <see cref="UsageBarRule"/></summary>
    public static long? StoredLimit(long? limit, bool unlimited) => unlimited || limit is null or <= 0 ? null : limit;

    /// <summary>Capacity (ลดได้) เก็บค่าสูงสุดของเดือน · Consumable (เพิ่มทางเดียว) เก็บค่าล่าสุด — ผู้ใช้: <c>UsageSnapshotService.UpsertAsync</c> (GREATEST ใน SQL)</summary>
    public static bool IsCapacity(string resourceKey)
        => QuotaResources.Find(resourceKey)?.Kind == QuotaResources.ResourceKind.Capacity;

    /// <summary>ทรัพยากรที่แจ้งเตือนจากงานรายวัน — เอกสาร/ข้อความช่องกลางแจ้งแบบทันที (ตัวนับเพิ่มทางเดียว · เส้นเดิม) จึงไม่ซ้ำที่นี่</summary>
    public static bool DailyWarnApplies(string resourceKey)
        => resourceKey != OverageResources.Documents && resourceKey != OverageResources.NotifyMessages
           && !(QuotaResources.Find(resourceKey)?.PerSite ?? false);

    /// <summary>ทรัพยากรทั้งหมดที่หน้าประวัติแสดง (ลำดับคงที่): เอกสาร · OCR · ข้อความ · ที่นั่ง · พื้นที่ · เว็บ · ที่พัก (+ AI เป็นข้อมูล)</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Unit)> DisplayResources = new (string, string, string)[]
    {
        (OverageResources.Documents, "เอกสาร / เดือน", "ฉบับ"),
        (QuotaResources.OcrPagesMonth, "หน้าสแกน OCR / เดือน", "หน้า"),
        (OverageResources.NotifyMessages, "ข้อความแจ้งเตือนผ่านช่องกลาง / เดือน", "ข้อความ"),
        (QuotaResources.Users, "ผู้ใช้ (ที่นั่ง)", "คน"),
        (QuotaResources.StorageGb, "พื้นที่จัดเก็บ", "GB"),
        (QuotaResources.CmsSites, "เว็บไซต์ (CMS)", "เว็บ"),
        (QuotaResources.LodgingProperties, "ที่พัก (แห่ง)", "แห่ง"),
    };

    public const string AiKey = "ai.calls";
}
