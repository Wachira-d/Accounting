using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **กุญแจกันซ้ำของบรรทัดจาก feed ที่ไม่มีเลขอ้างอิง** (รอบ 252 WP-12A ฝ่ายค้านรอบสอง M-C) · pure
/// <para>เดิมกันซ้ำด้วย <c>Reference</c> อย่างเดียว ⇒ ธนาคารที่ไม่ส่งเลขอ้างอิง ดึงซ้ำทุกรอบ = เงินเข้าเดียวกลายเป็นหลายบรรทัด (ผู้สมัครปิดหนี้ซ้ำ) ·
/// ไม่มีเลขอ้างอิง ⇒ กุญแจ = วันที่ + ยอด (2 ตำแหน่ง) + ทิศ + คำอธิบายที่ปรับรูป (ตัวอักษร/ตัวเลข ตัวพิมพ์ใหญ่) · ข้อจำกัดที่ยอมรับ: สองรายการจริงที่
/// ทุกอย่างเหมือนกันในวันเดียวกันจาก feed ที่ไม่มีเลขอ้างอิงจะเหลือหนึ่ง (ผู้ใช้เพิ่มเองได้ · ดีกว่าเงินเข้าถูกนับซ้ำ)</para>
/// </summary>
public static class BankFeedDedupe
{
    public static string NoReferenceKey(DateTime date, decimal amount, bool isDeposit, string? description)
    {
        var desc = new string((description ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.Join("|", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture),
            isDeposit ? "D" : "W", desc);
    }
}
