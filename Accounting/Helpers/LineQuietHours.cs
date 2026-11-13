using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-4 (team-A L8 · คำตัดสิน 260-4-1/260-4-2) — <b>ชั่วโมงเงียบของ LINE ถึงลูกค้า: ตัวตัดสินเดียว</b> (pure · server computes, page displays)
/// <list type="bullet">
/// <item>ค่าตั้งบริษัท <c>CompanySettings.LineQuietHoursEnabled/LineQuietStartHour/LineQuietEndHour</c> (เวลาไทย Asia/Bangkok +07:00 ไม่มีเวลาออมแสง) ·
/// ค่าตั้งต้น <b>ปิด</b> (ช่วงตั้งต้นที่เสนอ 21:00–08:00) — เวลาส่งของกฎตารางส่งเป็นค่าที่เจ้าของเลือกไว้แล้ว ⇒ ไม่เปลี่ยนเวลาส่งของบริษัทเดิมเงียบ ๆ
/// (F2 ข้อ 8) · ไม่มีแถวค่าตั้ง = ปิด</item>
/// <item>ผู้อ่านตัวเดียว: <c>ContactLineQueueService.ProcessPendingAsync</c> — แถวที่จองได้ในช่วงเงียบ ⇒ <b>เลื่อน</b> (กลับเป็นรอส่ง · <c>ScheduledFor</c> = เวลาสิ้นช่วงเงียบ ·
/// ไม่นับเป็นความพยายาม · หมายเหตุบนแถว) ไม่ใช่ทิ้ง · เครื่องหลายตัวปลอดภัยเพราะแถวถูกจองด้วย SKIP LOCKED และการเลื่อนบันทึกลงฐาน</item>
/// <item>เลื่อน = การ์ดตามกฎตารางส่ง (อนุมัติ · ใกล้/เกินกำหนด · รายการประจำ) + ทวง 50 ทวิ · <b>ยกเว้น</b> (ส่งทันที) = ใบเสร็จหลังลูกค้าชำระ
/// <b>เองผ่านลิงก์/ผู้ให้บริการรับชำระ</b> (<c>LineQueue.CustomerInitiated</c> — ฝ่ายค้าน 260-4 L4 · คำตัดสิน 260-4-13: ใบเสร็จจากการบันทึก/จับคู่ธนาคาร/นำเข้า
/// ตอนกลางคืนถูกเลื่อน) + พนักงานกดส่งเองจากหน้าเอกสาร (คนตัดสินใจเวลาเอง) · คำตอบในแชต (reply ของ webhook) ไม่ผ่านคิวนี้ = ไม่ถูกเลื่อน</item>
/// </list>
/// </summary>
public static class LineQuietHours
{
    public const string RuleCode = "LINE-QUIET-HOURS";
    public const bool DefaultEnabled = false;
    public const int DefaultStartHour = 21;
    public const int DefaultEndHour = 8;

    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    /// <summary>ส่งทันทีแม้อยู่ในช่วงเงียบไหม — ส่งเองจากหน้าเอกสาร ⇒ ใช่ · ใบเสร็จหลังรับชำระ ⇒ เฉพาะลูกค้าชำระเอง (<paramref name="customerInitiated"/>) · ที่เหลือเลื่อน</summary>
    public static bool IsExempt(string? trigger, bool customerInitiated = false)
        => trigger == ContactLineChannel.ManualTrigger || trigger == ContactLineChannel.SignedCopyTrigger   // รอบ 267 P3: ลูกค้าเพิ่งลงนามเอง
           || (trigger == ContactLineChannel.ReceiptTrigger && customerInitiated);

    /// <summary>ด่านตอนบันทึก — null = ผ่าน (ชั่วโมง 0–23 · เริ่ม ≠ สิ้นสุด)</summary>
    public static string? Problem(int startHour, int endHour)
    {
        if (startHour is < 0 or > 23 || endHour is < 0 or > 23) return "ชั่วโมงเงียบต้องเป็นชั่วโมง 0–23";
        if (startHour == endHour) return "ชั่วโมงเริ่มและสิ้นสุดของช่วงเงียบต้องไม่ใช่ชั่วโมงเดียวกัน";
        return null;
    }

    /// <summary>ชั่วโมงไทย <paramref name="hour"/> อยู่ในช่วงเงียบไหม (ช่วงข้ามเที่ยงคืนได้ เช่น 21 → 8)</summary>
    public static bool InWindow(int hour, int startHour, int endHour)
        => startHour < endHour ? hour >= startHour && hour < endHour : hour >= startHour || hour < endHour;

    /// <summary>เวลา UTC ที่ควรเลื่อนไปส่ง — null = ส่งได้เลย (ปิดอยู่ · trigger ยกเว้น · นอกช่วงเงียบ · ค่าตั้งเสีย)</summary>
    public static DateTime? DeferUntilUtc(DateTime nowUtc, bool enabled, int startHour, int endHour, string? trigger, bool customerInitiated = false)
    {
        if (!enabled || IsExempt(trigger, customerInitiated) || Problem(startHour, endHour) != null) return null;
        var local = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) + Bangkok;
        if (!InWindow(local.Hour, startHour, endHour)) return null;
        var end = new DateTime(local.Year, local.Month, local.Day, endHour, 0, 0, DateTimeKind.Utc);
        if (end <= local) end = end.AddDays(1);
        return DateTime.SpecifyKind(end - Bangkok, DateTimeKind.Utc);
    }

    /// <summary>หมายเหตุบนแถวคิวที่ถูกเลื่อน (คงไว้จนส่ง — ส่งแล้วล้าง)</summary>
    public static string DeferNote(DateTime untilUtc, int startHour, int endHour)
        => $"รอชั่วโมงเงียบ ({Hh(startHour)}–{Hh(endHour)} น.) — จะส่ง {ThaiDate.ToThaiDisplayString(untilUtc)} {Hh(endHour)} น. (เลื่อน ไม่ได้ทิ้ง)";

    /// <summary>ประโยคสถานะในแท็บ LINE ของหน้าตั้งค่า</summary>
    public static string Sentence(bool enabled, int startHour, int endHour) => enabled
        ? $"เปิดอยู่: {Hh(startHour)}–{Hh(endHour)} น. (เวลาไทย) การ์ดถึงลูกค้าทาง LINE ตามตารางส่ง (ใบแจ้งหนี้ · ทวงใกล้/เกินกำหนด · รายการประจำ · ทวง 50 ทวิ) "
          + "จะรอส่งหลังช่วงเงียบ · ส่งทันทีเสมอ: ใบเสร็จเมื่อลูกค้าชำระเองผ่านลิงก์/ช่องทางออนไลน์ และการกดส่งเองจากหน้าเอกสาร · ปิดชั่วโมงเงียบแล้วการ์ดที่รออยู่ส่งรอบถัดไป"
        : "ปิดอยู่: การ์ดถึงลูกค้าทาง LINE ส่งตามเวลาของกฎทุกชั่วโมง (รวมกลางคืน)";

    /// <summary>ฝ่ายค้าน 260-4 L3 (คำตัดสิน 260-4-14): คำเตือนบนฟอร์มกฎตารางส่ง ต่อชั่วโมงส่ง (0–23) — ช่วงเงียบเปิดและกฎติ๊ก LINE แล้วชั่วโมงส่งตกในช่วง ⇒
    /// LINE เลื่อนไปสิ้นช่วง (อีเมลส่งตามชั่วโมงเดิม ⇒ บอกว่าสองช่องจะถึงคนละเวลา) · null = ไม่มีคำเตือน · หน้าเว็บแสดงตามช่องที่ติ๊ก</summary>
    public sealed record RuleWarnings(IReadOnlyList<string?> LineOnly, IReadOnlyList<string?> LineAndEmail);

    public static RuleWarnings RuleWarningsByHour(bool enabled, int startHour, int endHour)
    {
        var lineOnly = new string?[24];
        var both = new string?[24];
        if (enabled && Problem(startHour, endHour) == null)
            for (var h = 0; h < 24; h++)
            {
                if (!InWindow(h, startHour, endHour)) continue;
                lineOnly[h] = $"🌙 {Hh(h)} น. อยู่ในชั่วโมงเงียบของ LINE ({Hh(startHour)}–{Hh(endHour)} น.) — การ์ด LINE ของกฎนี้จะรอส่ง {Hh(endHour)} น.";
                both[h] = lineOnly[h] + " · อีเมลยังส่งตาม " + Hh(h) + " น. (ลูกค้าได้อีเมลกับ LINE คนละเวลา)";
            }
        return new RuleWarnings(lineOnly, both);
    }

    private static string Hh(int h) => h.ToString("00", CultureInfo.InvariantCulture) + ":00";
}
