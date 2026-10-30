using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องทางติดต่อลูกค้าของการจอง (บัญชีลูกค้า → อีเมล/LINE · แขก → อีเมล) · ไม่มีทั้งคู่ = ส่งไม่ได้ (ต้องบอก ไม่ใช่ประทับว่าส่งแล้ว)</summary>
public sealed record BookingContact(string? Email, string? LineUserId)
{
    /// <summary>รอบ 211: เป้าหมายสำหรับหา LINE ที่ลูกค้าผูกเอง (การจอง/ออเดอร์/สมาชิก/ผู้ติดต่อ) — <c>BookingNotifier</c> หาให้ผ่าน
    /// <c>ICustomerLineLinkService.ResolveLineUserIdAsync</c> · null = เดิม (อีเมล/LINE ตามที่ผู้เรียกให้มา)</summary>
    public CustomerLineTarget? LineTarget { get; init; }

    public bool HasChannel => !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(LineUserId);
}

/// <summary>ผลการส่งข้อความถึงลูกค้า — <see cref="Sent"/> จริงเฉพาะเมื่อช่องทางใดช่องทางหนึ่งตอบว่าส่งสำเร็จ · <see cref="Error"/> = ร่องรอยต่อแถวเมื่อไม่สำเร็จ</summary>
public sealed record BookingSendResult(bool Sent, string? Channel, string? Error)
{
    public static BookingSendResult Ok(string channel) => new(true, channel, null);
    public static BookingSendResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของงานเบื้องหลังเครื่องจอง (รอบ 205 แพ็ก W3 · R-12/R-13 · <c>Services/Background/BookingReminderJob</c>):
/// <list type="bullet">
///   <item>เตือนก่อนนัด (reminder) กี่ชั่วโมงก่อนเริ่ม (ค่าตั้งต่อไซต์ <c>SiteBookingSettings.ReminderHoursBefore</c> เช่น "24,2") — ตัดสินจาก
///     <c>ReminderSentAt</c> คอลัมน์เดียว: ขั้นที่ "ถึงเวลาแล้ว" และยังไม่เคยส่งหลังเวลานั้น ⇒ ส่ง · ส่งทีละขั้น (job ดับไปนาน ⇒ ส่งขั้นล่าสุดขั้นเดียว ไม่ยิงรัว) ·
///     ขั้นที่เวลาถึงก่อนลูกค้าจองเสียอีก (จองล่วงหน้า 3 ชม. — ขั้น 24 ชม. ผ่านไปแล้ว) ⇒ ไม่ส่ง (ลูกค้าเพิ่งจองเอง)</item>
///   <item>ประทับ <c>ReminderSentAt</c> <b>หลังส่งถึงจริง</b>เท่านั้น (F2 #3) — ส่งล้ม ⇒ ยัง null + <c>NotifyLastError</c> + ลองใหม่หลัง <see cref="RetryAfter"/></item>
///   <item>คิวสำรองได้คิว (R-13): ข้อความแจ้ง + ถือรอบ <c>WaitlistHoldMinutes</c> นาที (0 = ไม่ถือ) — หมดเวลาแล้วยังรอยืนยัน ⇒ job ปล่อยรอบ (ยกเลิกผ่านเส้นสถานะปกติ)</item>
/// </list>
/// pure · เวลาทุกตัวเป็นเวลาไทย (<c>BookingSlotEngine.BangkokNow</c>) · เทสต์ <c>BookingReminderPlanTests</c>
/// </summary>
public static class BookingReminderPlan
{
    public const string DefaultHoursCsv = "24,2";
    public const int MaxHoursBefore = 24 * 30;
    /// <summary>ส่งล้มแล้วรอกี่นาทีก่อนลองใหม่ (ไม่ยิง SMTP/LINE ทุกรอบ job)</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    public const string DefaultTemplate =
        "เรียน {ชื่อ}\nขอเตือนนัดหมาย {บริการ} ที่ {เว็บ}\nวันที่ {วันที่} เวลา {เวลา}{ผู้ให้บริการ}\nเลขที่จอง {เลขที่}\nหากต้องการเลื่อน/ยกเลิก กรุณาติดต่อร้านล่วงหน้า";

    /// <summary>ตัวแทนที่ใช้ได้ในเทมเพลต (server ประกาศ · หน้าเว็บแสดงเป็นคำใบ้ — ไม่มีสำเนาใน JS)</summary>
    public static readonly IReadOnlyList<string> Placeholders = new[]
        { "{ชื่อ}", "{บริการ}", "{เว็บ}", "{วันที่}", "{เวลา}", "{ผู้ให้บริการ}", "{เลขที่}" };

    /// <summary>"24,2" → [24, 2] (เรียงมากไปน้อย · ตัดซ้ำ/ค่าผิด) · ว่าง/ผิดรูปทั้งหมด ⇒ ค่าเริ่มต้น</summary>
    public static IReadOnlyList<int> ParseHours(string? csv)
    {
        var list = (csv ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : -1)
            .Where(h => h >= 1 && h <= MaxHoursBefore)
            .Distinct().OrderByDescending(h => h).ToList();
        return list.Count > 0 ? list : ParseHours(DefaultHoursCsv);
    }

    /// <summary>ข้อผิดพลาดของค่าตั้ง (ภาษาไทย) — ว่าง = ใช้ได้ · ผู้บันทึกค่าตั้งไซต์เรียกก่อนเขียน</summary>
    public static IReadOnlyList<string> Validate(string? hoursCsv, int holdMinutes, string? template)
    {
        var errs = new List<string>();
        if (!string.IsNullOrWhiteSpace(hoursCsv))
        {
            var bad = hoursCsv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) || h < 1 || h > MaxHoursBefore)
                .ToList();
            if (bad.Count > 0) errs.Add($"ชั่วโมงเตือนก่อนนัด \"{string.Join(",", bad)}\" ต้องเป็นจำนวนเต็ม 1–{MaxHoursBefore} คั่นด้วยจุลภาค (เช่น 24,2)");
        }
        if (holdMinutes < 0 || holdMinutes > 7 * 24 * 60) errs.Add("ถือรอบคิวสำรองต้องเป็น 0–10080 นาที (0 = ไม่ถือ)");
        if (template != null && template.Length > 2000) errs.Add("เทมเพลตข้อความเตือนยาวเกิน 2000 ตัวอักษร");
        return errs;
    }

    /// <summary>สถานะที่ยังต้องเตือน — รอยืนยัน/ยืนยันแล้วเท่านั้น (คิวสำรองยังไม่มีคิว · กำลังให้บริการ/เสร็จ/ยกเลิก/ไม่มา ไม่เตือน)</summary>
    public static bool NeedsReminder(BookingStatus status) => status is BookingStatus.Pending or BookingStatus.Confirmed;

    /// <summary>
    /// ขั้นเตือน (ชั่วโมงก่อนเริ่ม) ที่ถึงเวลาส่ง ณ <paramref name="nowLocal"/> · null = ยังไม่ถึง/ส่งครบแล้ว/นัดผ่านไปแล้ว ·
    /// กติกา: เลือกขั้นที่ใกล้นัดที่สุดที่ (ก) เวลาส่ง = เริ่ม − h ≤ ตอนนี้ (ข) ยังไม่เคยส่งหลังเวลาส่งนั้น (<paramref name="lastSentLocal"/> &lt; เวลาส่ง)
    /// (ค) เวลาส่งไม่ก่อนเวลาที่ลูกค้าจอง (<paramref name="createdLocal"/>) — ลูกค้าเพิ่งจองเองไม่ต้องเตือนทันที
    /// </summary>
    public static int? DueStage(IReadOnlyList<int> hoursBefore, DateTime startLocal, DateTime nowLocal, DateTime createdLocal, DateTime? lastSentLocal)
    {
        if (startLocal <= nowLocal) return null;
        foreach (var h in hoursBefore.OrderBy(h => h))   // ใกล้นัดที่สุดก่อน — job ดับนาน ⇒ ส่งขั้นเดียว
        {
            var at = startLocal.AddHours(-h);
            if (at > nowLocal) continue;
            if (lastSentLocal is DateTime sent && sent >= at) return null;   // ส่งหลังเวลานี้ไปแล้ว ⇒ ขั้นที่ไกลกว่าก็ส่งแล้วเช่นกัน
            if (at < createdLocal) return null;   // ขั้นนี้ (และขั้นที่ไกลกว่า) ผ่านไปก่อนจอง
            return h;
        }
        return null;
    }

    /// <summary>ส่งล้มครั้งก่อน ⇒ ลองใหม่ได้เมื่อพ้น <see cref="RetryAfter"/> (ไม่เคยลอง = ลองได้)</summary>
    public static bool MayAttempt(DateTime? lastAttemptUtc, DateTime nowUtc)
        => lastAttemptUtc == null || nowUtc - lastAttemptUtc.Value >= RetryAfter;

    /// <summary>เติมตัวแทนในเทมเพลต (ว่าง = เทมเพลตเริ่มต้น) · ตัวแทนที่ไม่รู้จักคงไว้ตามเดิม</summary>
    public static string Render(string? template, string name, string service, string site, DateOnly date, TimeOnly start, string? resource, string bookingNumber)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        return t.Replace("{ชื่อ}", string.IsNullOrWhiteSpace(name) ? "ลูกค้า" : name.Trim())
            .Replace("{บริการ}", service)
            .Replace("{เว็บ}", site)
            .Replace("{วันที่}", ThaiDate(date))
            .Replace("{เวลา}", start.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Replace("{ผู้ให้บริการ}", string.IsNullOrWhiteSpace(resource) ? "" : $" กับ {resource}")
            .Replace("{เลขที่}", bookingNumber);
    }

    /// <summary>หัวเรื่องอีเมลเตือน — ขั้น ≥ 24 ชม. บอกเป็นวัน · น้อยกว่าบอกเป็นชั่วโมง</summary>
    public static string ReminderSubject(int hoursBefore, string service, string bookingNumber)
        => hoursBefore >= 24
            ? $"เตือนนัดหมาย {service} ในอีก {hoursBefore / 24} วัน (#{bookingNumber})"
            : $"เตือนนัดหมาย {service} ในอีก {hoursBefore} ชั่วโมง (#{bookingNumber})";

    /// <summary>ถือรอบคิวสำรองถึงเมื่อไร (เวลา UTC) · 0 นาที = ไม่ถือ (null) · ใช้เฉพาะใบที่เลื่อนขึ้นเป็น "รอยืนยัน" — ยืนยันอัตโนมัติไม่ต้องถือ</summary>
    public static DateTime? HoldUntil(DateTime promotedAtUtc, int holdMinutes, BookingStatus statusAfterPromote)
        => holdMinutes > 0 && statusAfterPromote == BookingStatus.Pending ? promotedAtUtc.AddMinutes(holdMinutes) : null;

    /// <summary>ปล่อยรอบที่ถือไว้ได้ไหม — ยังรอยืนยัน + พ้นเวลาถือ (ยืนยันแล้ว/ยกเลิกแล้ว = ไม่แตะ)</summary>
    public static bool HoldExpired(BookingStatus status, DateTime? holdUntilUtc, DateTime nowUtc)
        => status == BookingStatus.Pending && holdUntilUtc is DateTime u && u <= nowUtc;

    public const string HoldExpiredReason = "ไม่ยืนยันภายในเวลาที่ถือรอบจากคิวสำรอง — ระบบปล่อยรอบให้คิวถัดไป";

    /// <summary>ข้อความ "ได้คิวแล้ว" ถึงลูกค้าคิวสำรอง · มีถือรอบ ⇒ บอกเวลาสิ้นสุด (เวลาไทย)</summary>
    public static string WaitlistPromotedMessage(string name, string service, string site, DateOnly date, TimeOnly start, string? resource,
        string bookingNumber, DateTime? holdUntilUtc, bool autoConfirmed)
    {
        var who = string.IsNullOrWhiteSpace(name) ? "ลูกค้า" : name.Trim();
        var res = string.IsNullOrWhiteSpace(resource) ? "" : $" กับ {resource}";
        var head = $"เรียน {who}\nคิวสำรองของคุณได้ที่ว่างแล้ว: {service} ที่ {site}\nวันที่ {ThaiDate(date)} เวลา {start.ToString("HH:mm", CultureInfo.InvariantCulture)}{res}\nเลขที่จอง {bookingNumber}";
        if (autoConfirmed) return head + "\nการจองยืนยันแล้ว — พบกันตามนัด";
        if (holdUntilUtc is DateTime u)
            return head + $"\nกรุณายืนยัน/ชำระเงินภายใน {BookingSlotEngine.BangkokNow(u):dd/MM/yyyy HH:mm} มิฉะนั้นระบบจะปล่อยรอบให้คิวถัดไป";
        return head + "\nกรุณาติดต่อร้านเพื่อยืนยันการจอง";
    }

    public static string WaitlistPromotedSubject(string service, string bookingNumber) => $"คิวสำรองได้ที่ว่างแล้ว — {service} (#{bookingNumber})";

    /// <summary>วันที่แบบไทย (พ.ศ.) สำหรับข้อความถึงลูกค้า เช่น "ศุกร์ 6 พ.ย. 2569"</summary>
    public static string ThaiDate(DateOnly d)
    {
        string[] days = { "อาทิตย์", "จันทร์", "อังคาร", "พุธ", "พฤหัสบดี", "ศุกร์", "เสาร์" };
        string[] months = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };
        return $"{days[(int)d.DayOfWeek]} {d.Day} {months[d.Month - 1]} {d.Year + 543}";
    }

    /// <summary>ข้อความอีเมล (HTML) จากข้อความธรรมดา — ทุกบรรทัด HtmlEncode (ชื่อลูกค้า/บริการ/เทมเพลตมาจากผู้ใช้ — กฎ #4 C)</summary>
    public static string ToHtml(string text)
        => "<div style=\"font-family:sans-serif;white-space:pre-wrap;line-height:1.6\">" + System.Net.WebUtility.HtmlEncode(text) + "</div>";
}
