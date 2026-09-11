using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **เดาชื่อผู้ออกบิลจาก "กรอบบน" ของแบบฟอร์มพิมพ์สำเร็จ — ค่าเสนอความมั่นใจต่ำ**
///
/// <para>═══ ที่มา (บิลเงินสดเขียนมือ 2026-09-11) ═══ หลังตัวตัดสินย้ายชื่อเราไปฝั่งผู้ซื้อ
/// ช่องผู้ขายถูกปล่อยว่าง ทั้งที่กระดาษมีชื่อร้าน "อ๊อฟ พิการ" เขียนอยู่บรรทัดแรกในกรอบ
/// บนซึ่งเล่มบิลทุกเล่มสงวนไว้ให้<b>ผู้ออกบิล</b>. กฎเหล็ก #3 บังคับให้ "เติมไว้แล้ว
/// ไฮไลต์ให้ตรวจ" ไม่ใช่ปล่อยว่าง — และใบรับรองแทนใบเสร็จ (เป้าหมายของบิลแบบนี้) ต้อง
/// มีชื่อผู้รับเงินตามเกณฑ์สรรพากร</para>
///
/// <para>═══ กติกาความปลอดภัย ═══ ใช้เฉพาะเมื่อ (ก) ช่องผู้ขายว่าง (ข) รู้แล้วว่าเราเป็น
/// ผู้ซื้อ (ค) กระดาษเป็นแบบฟอร์มพิมพ์สำเร็จ (มีหัว บิลเงินสด/ใบเสร็จ/CASHSALE …) ·
/// ผู้สมัครต้องอยู่<b>เหนือ</b>บรรทัดหัวแบบฟอร์ม · ไม่ใช่คำบนแบบฟอร์ม/ที่อยู่/ตัวเลข/
/// ชื่อเราเอง · คืนความมั่นใจ <see cref="Confidence"/> = 0.35 เสมอ (ต่ำกว่าเกณฑ์ไฮไลต์
/// และต่ำกว่าเกณฑ์สร้าง Contact อัตโนมัติ) — เป็น "ข้อเสนอให้ตรวจ" ไม่ใช่การอ่าน</para>
/// </summary>
public static class OcrIssuerHeaderGuess
{
    public const double Confidence = 0.35;

    private static readonly Regex FormTitle = new(
        @"บิลเงินสด|ใบเสร็จ|ใบส่งของ|ใบกำกับ|ใบแจ้งหนี้|ใบวางบิล|CASH\s*SALE|RECEIPT|INVOICE|DELIVERY",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddressToken = new(
        @"(^|\s)(ม\.|หมู่|ต\.|ตำบล|อ\.|อำเภอ|จ\.|จังหวัด|ถ\.|ถนน|ซ\.|ซอย|แขวง|เขต|เลขที่)\s*\S",
        RegexOptions.Compiled);
    private static readonly string[] Boilerplate =
    {
        "เล่มที่", "เลขที่", "วันที่", "นาม", "ที่อยู่", "NAME", "DATE", "ADDRESS", "TEL", "โทร",
        "จำนวน", "รายการ", "หน่วยละ", "จำนวนเงิน", "รวมเงิน", "บาท", "ต้นฉบับ", "สำเนา", "ORIGINAL", "COPY",
    };

    /// <summary>ชื่อผู้ออกบิลที่เดาได้จากกรอบบน หรือ null เมื่อไม่เข้าเกณฑ์</summary>
    public static string? FromTopBox(string? rawText, OcrOurIdentity us)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var lines = rawText.Split('\n').Select(l => l.Trim()).ToList();
        var titleIdx = lines.FindIndex(l => FormTitle.IsMatch(l));
        if (titleIdx <= 0) return null;                      // ไม่ใช่แบบฟอร์มพิมพ์สำเร็จ / กรอบบนไม่มีอะไร
        foreach (var line in lines.Take(Math.Min(titleIdx, 6)))
        {
            if (line.Length < 3 || line.Length > 60) continue;
            if (Boilerplate.Any(b => line.Equals(b, StringComparison.OrdinalIgnoreCase))) continue;
            if (line.Count(char.IsDigit) >= 2) continue;     // เลขที่/วันที่/เบอร์โทร
            if (AddressToken.IsMatch(line)) continue;
            if (OcrSelfPartyGuard.IsSelf(line, us.Name) || OcrSelfPartyGuard.IsSelf(line, us.NameEn)) continue;
            if (!line.Any(char.IsLetter)) continue;
            return line;
        }
        return null;
    }
}
