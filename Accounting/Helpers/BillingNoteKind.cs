using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ใบวางบิล 2 ชนิด (ทีมตรวจงานค้าง 2026-10-08 · ข้อ C-02) — ตัวตัดสินเดียว
/// <list type="bullet">
/// <item><b>ใบวางบิลรวมใบแจ้งหนี้</b> (<c>CreateBillingNoteFromInvoicesAsync</c> — ทุกบรรทัดอ้าง <c>SourceDocumentId</c> = ใบแจ้งหนี้/ใบกำกับที่ตั้งลูกหนี้แล้ว):
/// เป็นแค่ "หนังสือทวงยอด" — รายได้/ลูกหนี้อยู่ที่ใบแจ้งหนี้แต่ละใบแล้ว ⇒ <b>ห้ามแปลง</b>เป็นใบเสร็จ/ใบแจ้งหนี้/ใบกำกับ
/// (ใบเสร็จจากใบวางบิลลงแบบขายสด Dr เงินสด/Cr รายได้ ⇒ รายได้ซ้ำ + ลูกหนี้ใบแจ้งหนี้ไม่ถูกตัด) · รับเงินที่ใบแจ้งหนี้แต่ละใบ</item>
/// <item><b>ใบวางบิลจากใบเสนอราคา</b> (ไม่มี <c>SourceDocumentId</c>): ยังไม่มีใบตั้งหนี้ ⇒ แปลงเป็นใบแจ้งหนี้/ใบกำกับ/ใบเสร็จได้ตามเดิม</item>
/// </list>
/// </summary>
public static class BillingNoteKind
{
    public const string RuleCode = "CONVERT-BN-ROLLUP";

    public static bool IsRollup(DocumentType type, bool anyLineHasSourceDocument)
        => type == DocumentType.BillingNote && anyLineHasSourceDocument;

    public static string ConvertBlockedMessage(string billingNoteNumber)
        => $"ใบวางบิล {billingNoteNumber} รวมใบแจ้งหนี้/ใบกำกับที่ตั้งลูกหนี้ไว้แล้ว — แปลงเป็นใบเสร็จ/ใบแจ้งหนี้ใหม่ไม่ได้ (รายได้จะซ้ำและลูกหนี้ใบเดิมไม่ถูกตัด) · "
           + "รับเงินที่ปุ่ม “💰 รับชำระตามใบวางบิล” (บันทึกชำระที่ใบแจ้งหนี้แต่ละใบ ออกใบเสร็จได้ตามปกติ)";

    public static string PaymentBlockedMessage(string billingNoteNumber)
        => $"ใบวางบิล {billingNoteNumber} เป็นหนังสือทวงยอดของใบแจ้งหนี้ — บันทึกชำระที่ใบแจ้งหนี้แต่ละใบ (ปุ่ม “💰 รับชำระตามใบวางบิล” ในหน้าใบวางบิล) ไม่ใช่ที่ใบวางบิล";
}
