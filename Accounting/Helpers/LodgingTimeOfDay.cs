using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// ตัวอ่าน "เวลาเช็คอิน/เช็คเอาต์" ของที่พัก — ตัวตัดสินตัวเดียว (รอบ 202 ทีม LS · S-P1-2 · คำตัดสินข้อ 116)
///
/// <para><b>ที่มา (บั๊กจริง)</b>: เดิม <c>LodgingService.ParseTime</c> ใช้ <c>TryParseExact("HH:mm")</c> แล้ว
/// <b>แทนด้วย 14:00/12:00 เงียบ ๆ</b> เมื่ออ่านไม่ได้ ⇒ ผู้ใช้พิมพ์ "9:00" หรือ "15.00" กดบันทึกได้ "สำเร็จ"
/// แต่ที่พักกลับเป็น 14:00 โดยไม่มีใครรู้ (silent no-op · กฎเหล็ก #4 A) · ขณะที่ DTO เป็น <c>string</c>
/// ไม่ nullable ⇒ ASP.NET ใส่ [Required] เอง ⇒ เว้นว่างกลับถูกตีกลับเป็นอังกฤษ — สองชั้นขัดกัน</para>
///
/// <para><b>กติกา (คำตัดสินข้อ 116)</b>: ไม่บังคับ — <b>ว่าง = ค่าเริ่มต้น</b> (14:00 / 12:00) ·
/// <b>ไม่ว่างแต่อ่านไม่ได้ = ปฏิเสธพร้อมข้อความไทยที่บอกชื่อช่อง</b> (ไม่แทนเงียบ) ·
/// รับ "H:mm" และ "HH:mm" (+ "HH:mm:ss" ที่ <c>&lt;input type="time"&gt;</c> บางเบราว์เซอร์ส่งมา)</para>
/// </summary>
public static class LodgingTimeOfDay
{
    /// <summary>ค่าเริ่มต้นเมื่อเว้นว่าง — ตัวเดียวกับค่าตั้งต้นของ entity/seed (<see cref="LodgingSeedDefaults"/>)</summary>
    public static readonly TimeOnly DefaultCheckIn = new(LodgingSeedDefaults.CheckInHour, 0);
    public static readonly TimeOnly DefaultCheckOut = new(LodgingSeedDefaults.CheckOutHour, 0);

    public const string CheckInLabel = "เวลาเช็คอิน";
    public const string CheckOutLabel = "เวลาเช็คเอาต์";

    private static readonly string[] Formats = { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" };

    /// <summary>อ่านเวลาได้ไหม (ไม่โยน) — ว่าง/ช่องว่างล้วน = false</summary>
    private static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TimeOnly.TryParseExact(text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>ว่าง ⇒ <paramref name="fallback"/> · อ่านได้ ⇒ ค่านั้น · ไม่ว่างแต่อ่านไม่ได้ ⇒ <see cref="BusinessRuleException"/>
    /// ที่บอกชื่อช่อง (<paramref name="fieldLabel"/>) + รูปแบบที่รับ</summary>
    public static TimeOnly Parse(string? text, TimeOnly fallback, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (TryParse(text, out var t)) return t;
        throw new BusinessRuleException(
            $"«{fieldLabel}» อ่านเวลา “{text.Trim()}” ไม่ได้ — ใช้รูปแบบ ชั่วโมง:นาที เช่น 14:00 หรือ 9:30 "
            + $"(เว้นว่าง = {fallback.ToString("HH:mm", CultureInfo.InvariantCulture)})",
            "LODGING-TIME");
    }

    /// <summary>รูปแบบที่แสดง/echo กลับ (HH:mm · invariant culture)</summary>
    public static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
