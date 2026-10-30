using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>นโยบายการเรียกเก็บค่าบริการ NextAcc ที่มีผลจริง (ผลของ <see cref="PlatformBillingSettingsRules.FromRow"/>) — ผู้อ่านทุกตัวรับค่านี้
/// ไม่อ่านแถว <see cref="PlatformBillingSettings"/> เอง (ตัวแปลงเดียว · ค่าเพี้ยนในฐานถูกบีบที่นี่ที่เดียว)</summary>
/// <param name="ReminderDaysBeforeDue">เรียงมากไปน้อย (7,3,1)</param>
/// <param name="ReminderDaysAfterDue">เรียงน้อยไปมาก (1,3,7)</param>
public sealed record PlatformBillingPolicy(
    int RenewalInvoiceLeadDays,
    IReadOnlyList<int> ReminderDaysBeforeDue,
    IReadOnlyList<int> ReminderDaysAfterDue,
    int SuspendGraceDays,
    bool AutoSuspendEnabled,
    int PaymentLinkExpiryDays,
    bool ReminderEmailEnabled,
    bool ReminderLineEnabled);

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินข้อ 180 · เจ้าของ: "อยากได้ระบบที่ flexible ตั้งค่าได้") — <b>ตัวตั้งค่าตั้งต้น + ตัวตรวจ + ตัวแปลง</b> ของค่าตั้งการเรียกเก็บ
/// ค่าบริการ NextAcc ที่เดียว (pure · เทสต์ <c>PlatformBillingPhase2Tests</c>) · หน้าแอดมินแสดงช่วงที่รับได้จากค่าคงที่ที่นี่ผ่าน API (ไม่พิมพ์ซ้ำใน JS)
/// </summary>
public static class PlatformBillingSettingsRules
{
    public const int DefaultRenewalInvoiceLeadDays = 15;
    public const string DefaultReminderDaysBeforeDue = "7,3,1";
    public const string DefaultReminderDaysAfterDue = "1,3,7";
    public const int DefaultSuspendGraceDays = 14;
    public const int DefaultPaymentLinkExpiryDays = 14;

    public const int MinRenewalInvoiceLeadDays = 1, MaxRenewalInvoiceLeadDays = 60;
    public const int MinSuspendGraceDays = 0, MaxSuspendGraceDays = 90;
    public const int MinPaymentLinkExpiryDays = 1, MaxPaymentLinkExpiryDays = 90;
    public const int MaxReminderDay = 60, MaxReminderCount = 6;

    public static PlatformBillingPolicy Defaults { get; } = new(
        DefaultRenewalInvoiceLeadDays,
        ParseDays(DefaultReminderDaysBeforeDue, descending: true),
        ParseDays(DefaultReminderDaysAfterDue, descending: false),
        DefaultSuspendGraceDays, true, DefaultPaymentLinkExpiryDays, true, true);

    /// <summary>แถว → นโยบาย · ไม่มีแถว = ค่าตั้งต้น · ค่าที่หลุดช่วง (แก้ในฐานตรง ๆ) ถูกบีบเข้าช่วง — ห้ามให้ค่าเพี้ยนทำงานแปลก
    /// (เช่น ผ่อนผัน −5 วัน = ระงับก่อนครบกำหนด)</summary>
    public static PlatformBillingPolicy FromRow(PlatformBillingSettings? row)
    {
        if (row == null) return Defaults;
        return new PlatformBillingPolicy(
            Math.Clamp(row.RenewalInvoiceLeadDays, MinRenewalInvoiceLeadDays, MaxRenewalInvoiceLeadDays),
            ParseDays(row.ReminderDaysBeforeDue, descending: true),
            ParseDays(row.ReminderDaysAfterDue, descending: false),
            Math.Clamp(row.SuspendGraceDays, MinSuspendGraceDays, MaxSuspendGraceDays),
            row.AutoSuspendEnabled,
            Math.Clamp(row.PaymentLinkExpiryDays, MinPaymentLinkExpiryDays, MaxPaymentLinkExpiryDays),
            row.ReminderEmailEnabled,
            row.ReminderLineEnabled);
    }

    /// <summary>อ่านรายการวันแบบผ่อนปรน (ค่าที่เก็บไว้แล้ว) — ตัดตัวที่อ่านไม่ได้/หลุดช่วงทิ้ง · ไม่ซ้ำ · ไม่เกิน <see cref="MaxReminderCount"/> ตัว</summary>
    public static IReadOnlyList<int> ParseDays(string? raw, bool descending)
    {
        var days = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, out var d) ? d : -1)
            .Where(d => d >= 1 && d <= MaxReminderDay)
            .Distinct();
        var sorted = descending ? days.OrderByDescending(d => d) : days.OrderBy(d => d);
        return sorted.Take(MaxReminderCount).ToList();
    }

    /// <summary>ตรวจรายการวันแบบเข้ม (ค่าที่แอดมินกรอก) — คืนข้อความไทยเมื่อผิด · ว่าง = ไม่แจ้งเตือนช่วงนั้น (ตั้งใจปิดได้)</summary>
    private static string? ValidateDays(string? raw, string label, bool descending, out string normalized)
    {
        normalized = "";
        var tokens = (raw ?? "").Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<int>();
        foreach (var t in tokens)
        {
            if (!int.TryParse(t, out var d) || d < 1 || d > MaxReminderDay)
                return $"{label}: \"{t}\" ไม่ถูกต้อง — ใส่ตัวเลข 1–{MaxReminderDay} คั่นด้วยจุลภาค";
            if (!list.Contains(d)) list.Add(d);
        }
        if (list.Count > MaxReminderCount) return $"{label}: ได้ไม่เกิน {MaxReminderCount} วัน";
        normalized = string.Join(",", descending ? list.OrderByDescending(d => d) : list.OrderBy(d => d));
        return null;
    }

    /// <summary>ตรวจค่าตั้งทั้งชุดก่อนบันทึก — คืนข้อความไทยตัวแรกที่ผิด (null = ผ่าน) + รายการวันที่จัดรูปแล้ว</summary>
    public static string? Validate(int renewalLeadDays, string? before, string? after, int graceDays, int linkExpiryDays,
        out string normalizedBefore, out string normalizedAfter)
    {
        normalizedBefore = normalizedAfter = "";
        if (renewalLeadDays < MinRenewalInvoiceLeadDays || renewalLeadDays > MaxRenewalInvoiceLeadDays)
            return $"ออกใบแจ้งหนี้ต่ออายุล่วงหน้าต้องอยู่ระหว่าง {MinRenewalInvoiceLeadDays}–{MaxRenewalInvoiceLeadDays} วัน";
        if (graceDays < MinSuspendGraceDays || graceDays > MaxSuspendGraceDays)
            return $"ระยะผ่อนผันก่อนระงับต้องอยู่ระหว่าง {MinSuspendGraceDays}–{MaxSuspendGraceDays} วัน";
        if (linkExpiryDays < MinPaymentLinkExpiryDays || linkExpiryDays > MaxPaymentLinkExpiryDays)
            return $"อายุลิงก์ชำระต้องอยู่ระหว่าง {MinPaymentLinkExpiryDays}–{MaxPaymentLinkExpiryDays} วัน";
        return ValidateDays(before, "วันแจ้งเตือนก่อนครบกำหนด", true, out normalizedBefore)
            ?? ValidateDays(after, "วันแจ้งเตือนหลังเกินกำหนด", false, out normalizedAfter);
    }

    /// <summary>สรุปนโยบายเป็นภาษาไทยสำหรับแสดง (ศูนย์บิลของผู้เช่า · หน้าแอดมิน) — เซิร์ฟเวอร์ประกอบ หน้าเว็บแสดงอย่างเดียว</summary>
    public static string DescribeSuspension(PlatformBillingPolicy p, bool enforced) =>
        !p.AutoSuspendEnabled
            ? "ไม่มีการระงับอัตโนมัติ — ผู้ดูแลระบบติดตามยอดค้างเอง"
            : enforced
                ? $"ค้างชำระเกินกำหนด {p.SuspendGraceDays} วัน ระบบจะระงับการใช้งานจนกว่าจะชำระ (ชำระแล้วเปิดคืนอัตโนมัติ)"
                : $"ค้างชำระเกินกำหนด {p.SuspendGraceDays} วัน อาจถูกระงับการใช้งาน (ขณะนี้ระบบยังบันทึกอย่างเดียว ไม่ระงับจริง)";
}
