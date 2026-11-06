namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 ข้อ 1 (LINE_OA_TERMS_CHECK C2/C5 · AUDIT_249 WP-11 #1) — ข้อความ<b>ที่ส่งผ่าน LINE OA กลางของ NextAcc ในนามบริษัท</b> ต้องมี
/// (ก) ป้ายผู้ส่งชัด "ในนาม {บริษัท} · ผ่าน NextAcc" (คำตอบเจ้าของ Q1(a): แสดงข้อมูลผู้ส่งให้ชัดเจน) และ (ข) ช่องทางยกเลิก ·
/// ตัวประกอบเดียว (pure) ของข้อความ text — การ์ด Flex ใช้ <see cref="LineDocumentCard"/> (ป้ายเดียวกันผ่าน DocumentLabels)
/// </summary>
public static class LineOnBehalfText
{
    public const string OptOutLine = "ไม่ต้องการรับทาง LINE: กด \"ยกเลิกการเชื่อมต่อ LINE\" ที่หน้าเว็บของร้าน หรือบล็อกบัญชี LINE นี้";

    private static string SenderLine(string? companyName)
        => $"ในนาม {(string.IsNullOrWhiteSpace(companyName) ? "ร้าน" : companyName!.Trim())} · ผ่าน NextAcc";

    /// <summary>ห่อข้อความเดิมด้วยป้ายผู้ส่ง (บรรทัดแรก) + ช่องทางยกเลิก (บรรทัดท้าย) · ความยาวรวมถูกตัดที่ตัวส่ง (<see cref="LineMessageLimits"/>)</summary>
    public static string ForNextAcc(string? companyName, string text)
    {
        // ฝ่ายค้าน L6: ตัดเนื้อก่อนต่อบรรทัดยกเลิก — ตัวส่งตัดที่ 5,000 ตัว ⇒ ถ้าตัดทีหลัง บรรทัดยกเลิก (C5) หายเป็นส่วนแรก
        var head = SenderLine(companyName) + "\n";
        var tail = "\n—\n" + OptOutLine;
        var room = Math.Max(0, LineMessageLimits.MaxTextChars - head.Length - tail.Length);
        return head + LineMessageLimits.Truncate((text ?? "").Trim(), room) + tail;
    }
}
