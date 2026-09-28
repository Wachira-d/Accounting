namespace Accounting.Helpers;

/// <summary>ช่องของบรรทัดรายงานภาษีซื้อ (§87) ที่มาจาก JE ภาษีซื้อซึ่งไม่มีเอกสารต้นทาง</summary>
public readonly record struct JournalInputTaxInvoiceFields(
    DateTime TransactionDate, string DocumentNo, string? SupplierName, string? SupplierTaxId, string? SupplierBranch,
    bool FromStructuredFields);

/// <summary>
/// **ใบกำกับที่ JE ภาษีซื้อ (ไม่มีเอกสารต้นทาง) อ้าง — ตัวตัดสินตัวเดียวของรายงานภาษีซื้อเส้น JE_INPUT** (review198-E2 E2-5)
///
/// <para>═══ ที่มา ═══ รายงานภาษีซื้อเส้นใบสำคัญแกะเลขที่ใบกำกับด้วย regex <c>[A-Z]{2,6}[-/]?\d…</c> จาก Reference/คำอธิบาย และใช้วันที่ใบสำคัญ ⇒
/// ใบสำคัญ "รับใบกำกับค่าธรรมเนียม" ของผู้ให้บริการรับชำระเงิน: วันที่ในรายงาน = วันที่เคลม (ไม่ใช่วันที่บนใบกำกับ — §87 ต้องเป็นวันที่ใบกำกับ) ·
/// เลขที่ล้วนตัวเลข/ตัวพิมพ์เล็ก ตกไปเป็นเลขใบสำคัญ · เลขที่ยาว (<c>OMTH-INV-…</c>) ถูกตัด · ช่องสาขาว่าง</para>
///
/// <para>═══ กติกา ═══ JE ที่บันทึกใบกำกับเป็นข้อมูลโครงสร้าง (<c>TaxInvoiceNo</c> มีค่า) ⇒ ใช้ช่องโครงสร้างทั้งชุด (วันที่ใบกำกับ ·
/// เลขที่ตามจริง · ชื่อ/เลขผู้เสียภาษี/สาขาผู้ออก) · ไม่มี ⇒ ค่าเดิมที่ผู้เรียกแกะมา (พฤติกรรมเดิมของ JE อื่นทุกใบ — ไม่แตะ)</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class JournalInputTaxInvoice
{
    public static JournalInputTaxInvoiceFields Resolve(
        DateTime entryDate, string? taxInvoiceNo, DateTime? taxInvoiceDate, string? supplierName, string? supplierTaxId,
        string? supplierBranch, string fallbackDocumentNo, string? fallbackName, string? fallbackTaxId)
    {
        if (string.IsNullOrWhiteSpace(taxInvoiceNo))
            return new JournalInputTaxInvoiceFields(entryDate, fallbackDocumentNo, fallbackName, fallbackTaxId, null, false);

        var taxId = ThaiTaxId.Normalize(supplierTaxId);
        return new JournalInputTaxInvoiceFields(
            taxInvoiceDate ?? entryDate,
            taxInvoiceNo.Trim(),
            string.IsNullOrWhiteSpace(supplierName) ? fallbackName : supplierName.Trim(),
            taxId.Length == 13 ? taxId : fallbackTaxId,
            string.IsNullOrWhiteSpace(supplierBranch) ? null : supplierBranch.Trim(),
            true);
    }
}
