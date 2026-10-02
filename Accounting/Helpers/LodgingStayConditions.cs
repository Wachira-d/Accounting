using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// ข้อความ "เงื่อนไขเข้า-ออกก่อน/หลังเวลา" ที่แขกเห็น — ตัวประกอบข้อความตัวเดียว (รอบ 202 ทีม LS · คำตัดสินข้อ 121)
///
/// <para><b>ที่มา</b>: <c>EarlyCheckInHours</c>/<c>LateCheckOutHours</c> มีช่องบนหน้าตั้งค่า + เก็บ + echo ครบ แต่<b>ไม่มีผู้อ่าน</b>
/// ("มีช่อง ≠ มีผล" · F2 ข้อ 2) · คำตัดสินข้อ 121: แสดงเป็น<b>เงื่อนไข</b>บนหลักฐานการจอง + /lodging/info (ผู้อ่านจริง)
/// — <b>ไม่บังคับเวลา</b>ในระบบ (ค่าธรรมเนียมยังคิดเมื่อพนักงานติ๊กตอนเช็คอิน/เช็คเอาต์เหมือนเดิม)</para>
///
/// <para>ชั่วโมง 0 = ไม่มีบริการนั้น ⇒ ไม่มีบรรทัด (ห้ามพิมพ์ "ได้ 0 ชม.") · ข้อความไม่ผ่าน HTML encode ที่นี่ —
/// ผู้เรียกที่ต่อเข้า HTML ต้อง encode เอง (กฎเหล็ก #4 C)</para>
/// </summary>
public static class LodgingStayConditions
{
    public static List<string> Lines(string checkInTime, string checkOutTime,
        int earlyCheckInHours, decimal earlyCheckInFee, int lateCheckOutHours, decimal lateCheckOutFee)
    {
        var lines = new List<string>();
        if (earlyCheckInHours > 0)
            lines.Add($"เช็คอินก่อนเวลา ({checkInTime} น.) ได้สูงสุด {earlyCheckInHours} ชม. — {FeeText(earlyCheckInFee)} · ขึ้นกับห้องว่าง กรุณาแจ้งที่พักล่วงหน้า");
        if (lateCheckOutHours > 0)
            lines.Add($"เช็คเอาต์หลังเวลา ({checkOutTime} น.) ได้สูงสุด {lateCheckOutHours} ชม. — {FeeText(lateCheckOutFee)} · ขึ้นกับห้องว่าง กรุณาแจ้งที่พักล่วงหน้า");
        return lines;
    }

    private static string FeeText(decimal fee)
        => fee > 0 ? $"ค่าธรรมเนียม {fee.ToString("N2", CultureInfo.InvariantCulture)} บาท" : "ไม่มีค่าธรรมเนียม";
}
