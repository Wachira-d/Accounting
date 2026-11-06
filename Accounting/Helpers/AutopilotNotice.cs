using System.Globalization;
using System.Text;
using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>ส่งแจ้ง "ทีมเรา" ทันที หรือรวมในสรุปประจำวัน</summary>
public enum AutopilotDelivery
{
    /// <summary>ล้ม/ติด/ต้องให้คนทำ — ส่งทันทีผ่านเครื่องแจ้งเตือนกลาง</summary>
    Immediate,
    /// <summary>สำเร็จ/ข้อมูล — นับในสรุปประจำวัน (อ่านจากแถวสถานะจริงตอนประกอบ · ไม่มีตารางสถานะซ้ำ)</summary>
    Digest,
    /// <summary>ไม่แจ้งทีม (ค่าตั้งต้นของเหตุการณ์ที่ถี่และไม่ต้องทำอะไร)</summary>
    Off,
}

/// <summary>
/// **แค็ตตาล็อกอีเมล/แจ้งเตือนสถานะ Autopilot ถึงทีมเรา (E-01…E-15) + ข้อความทุกฉบับ — ตัวตั้งตัวเดียว** (รอบ 252 WP-12A · AUTOPILOT_BILLING_DESIGN §10 ·
/// คำตัดสิน §2 #36 Q7: <b>ล้ม/ติด = ทันที · สำเร็จ = สรุปประจำวัน 08:40</b>) · pure
/// <para>เซิร์ฟเวอร์ประกอบข้อความ (กฎ F2 ข้อ 5) · ผู้ส่งเรียก <c>INotificationEngine.DispatchAsync</c> ด้วยคีย์ของรายการนั้น · ข้อความถึงทีมภายใน (ไม่ใช่ลูกค้า) —
/// ไม่อ้างมาตรากฎหมาย (คำตัดสิน N10) · ยอดเงินพิมพ์ <c>N2</c></para>
/// <para>เหตุการณ์ที่เป็นของส่วน B (คู่ค้า NextAcc E-09/E-10 · 50 ทวิ E-07/E-08) อยู่ในแค็ตตาล็อกพร้อมป้าย "ส่วน B" — ยังไม่มีผู้ส่ง (ไม่แต่งว่าส่งแล้ว)</para>
/// </summary>
public static class AutopilotNotice
{
    /// <summary>หนึ่งเหตุการณ์ — <paramref name="EventKey"/> = คีย์ของเครื่องแจ้งเตือนกลาง (null = นับในสรุปเท่านั้น/ยังไม่มีผู้ส่ง) · <paramref name="Wired"/> = มีผู้ส่ง/ผู้นับในรอบนี้แล้ว</summary>
    public sealed record Event(string Code, string Label, AutopilotDelivery Delivery, string? EventKey, bool Wired);

    public static readonly IReadOnlyList<Event> Catalogue = new[]
    {
        new Event("E-01", "ลูกค้ายอมรับ/เซ็นใบเสนอราคาออนไลน์", AutopilotDelivery.Immediate, NotificationEvents.DocumentCustomerSigned, true),
        new Event("E-01b", "ระบบแปลงใบเสนอราคาที่ลูกค้ายอมรับเป็นร่างใบแจ้งหนี้/ใบกำกับ", AutopilotDelivery.Digest, null, true),
        new Event("E-01c", "ลูกค้ายอมรับแล้วแต่ระบบแปลงอัตโนมัติไม่ได้", AutopilotDelivery.Immediate, NotificationEvents.AutopilotQuotationBlocked, true),
        new Event("E-02", "รอบรายการประจำสร้าง/อนุมัติเอกสารไม่ได้", AutopilotDelivery.Immediate, NotificationEvents.RecurringRunFailed, true),
        new Event("E-02b", "รายการประจำอนุมัติพร้อมข้อสังเกต", AutopilotDelivery.Immediate, NotificationEvents.RecurringApprovedWithNotes, true),
        new Event("E-03", "ส่งเอกสารถึงลูกค้าแล้ว", AutopilotDelivery.Digest, null, true),
        new Event("E-04", "ลูกค้าเปิดลิงก์/เริ่มชำระ", AutopilotDelivery.Off, null, false),
        new Event("E-05", "รับเงินแล้ว + ออก/ส่งใบเสร็จ", AutopilotDelivery.Digest, null, true),
        new Event("E-05b", "งานหลังรับเงินหยุด/ล้ม (ต้องให้คนทำ)", AutopilotDelivery.Immediate, NotificationEvents.PostPaymentAutomationBlocked, true),
        new Event("E-06", "ลูกค้าแจ้งโอน/แนบสลิป", AutopilotDelivery.Immediate, NotificationEvents.AutopilotSlipSubmitted, true),
        new Event("E-07", "ได้รับหนังสือรับรอง 50 ทวิ (ส่วน B — WP-09)", AutopilotDelivery.Digest, null, false),
        new Event("E-09", "คู่ค้าปฏิเสธใบซื้อ / ส่งเข้าระบบคู่ค้าล้ม (ส่วน B — รอบ 252–253)", AutopilotDelivery.Immediate, null, false),
        new Event("E-10", "คู่ค้ายอมรับ/เพิกถอนการเชื่อม (ส่วน B)", AutopilotDelivery.Immediate, null, false),
        new Event("E-13", "จบกระบวนการ (ชำระครบ + ส่งใบเสร็จแล้ว)", AutopilotDelivery.Digest, null, true),
        new Event("E-14", "เงินโอนเข้าที่ระบบปิดหนี้ให้/รอยืนยัน", AutopilotDelivery.Digest, null, true),
        new Event("E-14b", "เงินโอนตรงใบค้างแต่บันทึกรับชำระอัตโนมัติไม่สำเร็จ", AutopilotDelivery.Immediate, NotificationEvents.AutopilotBankSettleFailed, true),
        new Event("E-15", "สรุป Autopilot ประจำวัน", AutopilotDelivery.Digest, NotificationEvents.AutopilotDailyDigest, true),
    };

    /// <summary>เวลาส่งสรุปตั้งต้น (เวลาไทย) — คำตัดสิน Q7</summary>
    public static readonly TimeSpan DefaultDigestTime = new(8, 40, 0);

    /// <summary>"HH:mm" ที่บริษัทตั้ง → เวลา · ว่าง/อ่านไม่ออก/นอกช่วง ⇒ <see cref="DefaultDigestTime"/></summary>
    public static TimeSpan DigestTime(string? configured)
        => TimeSpan.TryParseExact((configured ?? "").Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t : DefaultDigestTime;

    /// <summary>ค่าที่ผู้ใช้ส่งมาตั้งเวลาสรุป — null = ใช้ค่าตั้งต้น · ปัญหา = ข้อความไทย (ไม่ clamp เงียบ)</summary>
    public static (string? Value, string? Problem) NormalizeDigestTime(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return (null, null);
        return TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? (t.ToString(@"hh\:mm", CultureInfo.InvariantCulture), null)
            : (null, "เวลาส่งสรุปต้องเป็นรูป ชั่วโมง:นาที เช่น 08:40");
    }

    /// <summary>วันไทย (UTC+7) ของเวลานี้ — ค่าที่เก็บใน <c>AutopilotDigestLastSentOn</c> (เที่ยงคืน · Kind Unspecified)</summary>
    public static DateTime BangkokDate(DateTime nowUtc) => DateTime.SpecifyKind(nowUtc.AddHours(7).Date, DateTimeKind.Unspecified);

    /// <summary>ถึงเวลาส่งสรุปของวันนี้หรือยัง — เลยเวลาที่ตั้ง (เวลาไทย) และวันนี้ยังไม่ส่ง · เครื่องที่ตื่นสายส่งได้ทั้งวัน (ไม่พลาดวัน) แต่ไม่ส่งซ้ำ</summary>
    public static bool DigestDue(DateTime nowUtc, string? configuredTime, DateTime? lastSentOn)
    {
        var local = nowUtc.AddHours(7);
        if (local.TimeOfDay < DigestTime(configuredTime)) return false;
        return lastSentOn == null || lastSentOn.Value.Date < local.Date;
    }

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    /// <summary>วันที่ในข้อความถึงทีม (ค.ศ. · ไม่ขึ้นกับ culture ของเครื่อง — th-TH จะพิมพ์ พ.ศ. ปนกัน)</summary>
    private static string Day(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>E-01c — หัวเรื่อง/ข้อความ</summary>
    public static (string Title, string Message) QuotationBlocked(string quotationNumber, string? customerName, string reason)
        => ($"ใบเสนอราคา {quotationNumber}: ลูกค้ายอมรับแล้วแต่ระบบแปลงอัตโนมัติไม่ได้",
            $"ลูกค้า{(string.IsNullOrWhiteSpace(customerName) ? "" : " " + customerName!.Trim())} ยอมรับใบเสนอราคา {quotationNumber} ออนไลน์แล้ว — {reason.Trim()} · " +
            "เปิดหน้า Autopilot → \"เรื่องที่ระบบทำต่อไม่ได้\" เพื่อแปลงเองหรือกดลองอีกครั้ง");

    /// <summary>E-06 — ลูกค้าแจ้งโอน (สลิปเป็นข้อบ่งชี้ ยังไม่ใช่รับชำระ)</summary>
    public static (string Title, string Message) SlipSubmitted(string documentNumber, decimal amount, string? bankRef)
        => ($"ลูกค้าแจ้งโอน {documentNumber} ยอด {Money(amount)} บาท (รอตรวจ)",
            $"ลูกค้าแนบสลิปของ {documentNumber} ยอด {Money(amount)} บาท" + (string.IsNullOrWhiteSpace(bankRef) ? "" : $" อ้างอิงโอน {bankRef!.Trim()}") +
            " — ยังไม่ใช่การรับชำระ: ระบบจะจับคู่เมื่อรายการเดินบัญชีจริงเข้ามา หรือยืนยันสลิปที่หน้ารายการรับชำระ");

    /// <summary>E-14b — เงินโอนตรงใบแต่บันทึกรับชำระไม่สำเร็จ</summary>
    public static (string Title, string Message) BankSettleFailed(DateTime lineDate, decimal amount, string documentNumber, string error)
        => ($"เงินโอน {Money(amount)} บาท ตรงกับ {documentNumber} แต่บันทึกรับชำระอัตโนมัติไม่สำเร็จ",
            $"รายการเดินบัญชีวันที่ {Day(lineDate)} ยอด {Money(amount)} บาท ตรงกับ {documentNumber} — บันทึกรับชำระไม่สำเร็จ: {error.Trim()} · " +
            "ใบยังค้าง (ไม่ได้ประทับว่าชำระ) · แก้สาเหตุแล้วกด \"ยืนยัน\" ในหน้า Autopilot → เงินเข้าที่รอยืนยัน");

    /// <summary>ข้อเท็จจริงของสรุปประจำวัน — นับจากแถวสถานะจริงในช่วง [ส่งครั้งก่อน, ตอนนี้)</summary>
    public sealed record DigestFacts(
        int DocumentsEmailed,
        int PaymentsRecorded,
        decimal PaymentsTotal,
        int ReceiptsSent,
        int QuotationsConverted,
        int BankAutoSettled,
        int BankAwaitingConfirm,
        int OpenIssues,
        IReadOnlyList<string> TopIssues);

    /// <summary>E-15 — null = ไม่มีอะไรจะรายงาน (ไม่ส่งอีเมลว่าง) · มีเรื่องค้างอย่างเดียวก็ส่ง (เรื่องค้างต้องดัง)</summary>
    public static (string Title, string Message)? ComposeDigest(DateTime bangkokDate, DigestFacts f)
    {
        var activity = f.DocumentsEmailed + f.PaymentsRecorded + f.ReceiptsSent + f.QuotationsConverted + f.BankAutoSettled;
        if (activity == 0 && f.BankAwaitingConfirm == 0 && f.OpenIssues == 0) return null;
        var sb = new StringBuilder();
        if (f.DocumentsEmailed > 0) sb.Append($"• ส่งเอกสารถึงลูกค้า {f.DocumentsEmailed} ฉบับ\n");
        if (f.PaymentsRecorded > 0) sb.Append($"• รับชำระ {f.PaymentsRecorded} รายการ รวม {Money(f.PaymentsTotal)} บาท\n");
        if (f.ReceiptsSent > 0) sb.Append($"• ออก/ส่งใบเสร็จอัตโนมัติ {f.ReceiptsSent} ฉบับ\n");
        if (f.QuotationsConverted > 0) sb.Append($"• แปลงใบเสนอราคาที่ลูกค้ายอมรับเป็นร่างใบแจ้งหนี้/ใบกำกับ {f.QuotationsConverted} ใบ (รออนุมัติ)\n");
        if (f.BankAutoSettled > 0) sb.Append($"• เงินโอนเข้าที่ระบบปิดหนี้ให้ {f.BankAutoSettled} รายการ\n");
        if (f.BankAwaitingConfirm > 0) sb.Append($"• เงินโอนเข้าที่รอคนยืนยัน {f.BankAwaitingConfirm} รายการ\n");
        if (f.OpenIssues > 0)
        {
            sb.Append($"• ❗ เรื่องที่ระบบทำต่อไม่ได้ {f.OpenIssues} เรื่อง");
            var top = (f.TopIssues ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Take(5).ToList();
            if (top.Count > 0) sb.Append(":\n").Append(string.Join("\n", top.Select(t => "   – " + t.Trim())));
            sb.Append('\n');
        }
        sb.Append("รายละเอียดและปุ่มแก้: หน้า Autopilot");
        var title = f.OpenIssues > 0
            ? $"สรุป Autopilot {Day(bangkokDate)} — ❗ ติด {f.OpenIssues} เรื่อง"
            : $"สรุป Autopilot {Day(bangkokDate)}";
        return (title, sb.ToString().TrimEnd());
    }
}
