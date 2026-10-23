namespace Accounting.Helpers;

/// <summary>
/// ช่อง "จำนวน" บนเอกสารพิมพ์ — ตัวตัดสินเดียวของ renderer ทั้งสองตัว (HTML <c>PdfGenerationService</c> + QuestPDF <c>DocumentRenderer</c>).
/// <para>บรรทัดบรรยาย/รายการย่อย (ราคา 0 · ยอด 0 · VAT 0) ใต้บรรทัดแม่ที่ถือราคา: เดิมซ่อนจำนวนเมื่อ = 1 ทุกกรณี (กันบรรทัดข้อความล้วน
/// ที่ฟอร์มเติม 1 ให้เองโชว์ "1.00") ⇒ ผู้ใช้รายงาน 2026-10-23 "รายการข้างล่างใส่เลข 1 ไม่ขึ้น" (ใบแจ้งหนี้โซลาร์เซลล์: Smart Logger 1 เครื่อง ·
/// Zero Export 1 ชุด · ออกแบบ 1 งาน — 29 แผง / 3 เครื่อง ขึ้นปกติ)</para>
/// <para>กติกาใหม่: บรรทัดบรรยายที่ <b>มีหน่วย</b> = ผู้ใช้ตั้งใจระบุปริมาณ ⇒ แสดงจำนวนเสมอ · ไม่มีหน่วยและจำนวน 1 (ค่าที่ฟอร์มเติมให้) ⇒ เว้นว่าง ·
/// จำนวนอื่นที่ไม่ใช่ 1 ⇒ แสดงเสมอ · บรรทัดปกติ ⇒ แสดงเสมอ</para>
/// </summary>
public static class PrintedLineQuantity
{
    public static string Format(decimal quantity, string? unit, bool isDescriptiveLine)
        => isDescriptiveLine && quantity == 1m && string.IsNullOrWhiteSpace(unit)
            ? ""
            : quantity.ToString("N2");
}
