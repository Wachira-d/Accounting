using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาของ "ใบเสร็จรับเงินที่ออกคู่กับการรับชำระ" (settlement receipt) — ตัวเดียว
/// ของระบบ ใช้ทั้งตอน<b>บันทึกรับชำระ</b> (<c>DocumentService.CreatePaymentAsync</c>)
/// และตอน<b>ออกใบย้อนหลัง</b> (<c>IssueReceiptForPaymentAsync</c>)
///
/// <para>═══ ทำไมต้องเป็นตัวกลาง ═══ เกณฑ์ "ใบเสร็จนี้ถือ VAT (= ใบกำกับภาษี ณ วัน
/// รับเงิน §78/1) หรือเป็นใบรับเปล่า" ถูกเขียนไว้ที่เส้นบันทึกรับชำระก่อน พอเปิดเส้น
/// ที่สองให้ออกย้อนหลัง ถ้าคัดลอกเงื่อนไขไปวางอีกชุด = สำเนามือที่รอ drift
/// (กฎเหล็ก #4 A) — และ drift ตรงนี้แปลว่า<b>ใบเดียวกันถือ VAT หรือไม่ถือ ขึ้นกับว่า
/// ผู้ใช้กดปุ่มไหน</b> ⇒ ภ.พ.30 กับกระดาษเล่าคนละเรื่อง</para>
/// </summary>
public static class SettlementReceiptPolicy
{
    /// <summary>ชนิดเอกสารต้นทางที่ "รับเงินจากลูกค้า" แล้วออกใบเสร็จให้ได้</summary>
    public static bool IsReceivableSource(DocumentType type)
        => type is DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.DebitNote;

    /// <summary>ใบเสร็จใบนี้ทำหน้าที่ <b>ใบกำกับภาษี ณ วันรับเงิน (§78/1)</b> ไหม
    ///
    /// <para>เฉพาะ<b>ใบแจ้งหนี้</b>ที่มี VAT (พักไว้ที่ 21913) และปิดยอดใน<b>งวดเดียว</b>
    /// — ใบกำกับภาษี (TaxInvoice) ออก VAT ไปแล้วตั้งแต่ต้นทาง ใบเสร็จจึงต้อง VAT = 0
    /// ไม่งั้นภาษีขายถูกนับสองรอบ · ผ่อนหลายงวดก็ไม่เข้า เพราะใบกำกับ ณ วันรับเงิน
    /// ต้องครอบ "การรับเงินครั้งนั้น" ทั้งก้อน ไม่ใช่เศษของหลายงวด</para></summary>
    public static bool CarriesTaxInvoiceRole(
        DocumentType sourceType, decimal sourceVatAmount, bool singleShotFull)
        => sourceType == DocumentType.Invoice && sourceVatAmount > 0m && singleShotFull;

    /// <summary>เหตุผล (ภาษาไทย เอาไปโชว์ได้) ที่ออกใบเสร็จย้อนหลังให้การรับชำระนี้
    /// ไม่ได้ — <c>null</c> = ออกได้
    ///
    /// <para><b>ห้ามคืน bool เปล่า ๆ</b> แล้วให้แต่ละหน้าจอแต่งคำเอง (= สำเนาชุดที่สาม)
    /// และทุกการปฏิเสธต้องบอก<b>ทางไปต่อ</b> ไม่ใช่ตันเฉย ๆ (กฎเหล็ก #4)</para></summary>
    /// <param name="sourceServesAsReceipt">ใบต้นทาง<b>ทำหน้าที่ใบเสร็จของการรับเงิน
    /// ก้อนนี้อยู่แล้ว</b> (รับครบในวันเดียวกับวันที่บนใบ ⇒ หัวกระดาษพิมพ์
    /// "ใบกำกับภาษี/ใบเสร็จรับเงิน" — ดู <c>DocumentService.ComputeServedAsReceipt</c>).
    /// ไม่มีค่าเริ่มต้นโดยตั้งใจ: ผู้เรียกทุกรายต้อง<b>ตัดสินใจ</b>ว่าคำนวณมาแล้ว
    /// ไม่ใช่ปล่อยผ่านเพราะลืมส่ง (กฎเหล็ก #4 — พารามิเตอร์ที่ตัดสินผลทางกฎหมาย
    /// ห้ามมี default)</param>
    public static string? WhyCannotIssue(
        bool paymentVoided, DocumentType sourceType, DocumentStatus sourceStatus,
        int allocationCount, bool sourceServesAsReceipt)
    {
        if (paymentVoided)
            return "การชำระเงินนี้ถูกยกเลิกไปแล้ว — ออกใบเสร็จไม่ได้";
        if (allocationCount > 0)
            return "การชำระนี้ถูกกระจายไปหลายเอกสาร — ระบบออกใบเสร็จอัตโนมัติให้ไม่ได้ "
                + "เพราะยอดต่อใบเป็นการจัดสรร ไม่ใช่ยอดที่รับจริงต่อใบ "
                + "ให้ใช้ \"แปลงเอกสาร → ใบเสร็จรับเงิน\" ที่เอกสารแต่ละใบแทน";
        if (!IsReceivableSource(sourceType))
            return "ใบเสร็จรับเงินออกได้เฉพาะการรับเงินจากลูกค้า "
                + $"(ใบแจ้งหนี้/ใบกำกับภาษี/ใบเพิ่มหนี้) — เอกสารต้นทางนี้เป็น {sourceType}";
        if (sourceStatus is DocumentStatus.Voided or DocumentStatus.Rejected or DocumentStatus.Draft)
            return $"เอกสารต้นทางอยู่ในสถานะ {sourceStatus} — ออกใบเสร็จไม่ได้ "
                + "(ถ้าเอกสารถูกยกเลิกไปแล้ว ต้องยกเลิกการชำระนี้ด้วย)";
        // ⬅ ที่มา (ผู้ใช้รายงาน 2026-09-11): ปุ่ม "ออกใบเสร็จ" โผล่บนใบ TIV ที่หัว
        // กระดาษเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" อยู่แล้ว (รับเงินวันเดียวกับวันที่ใบ)
        // ⇒ กดแล้วได้กระดาษใบที่สองที่อ้างการรับเงินก้อนเดียวกัน = ใบรับซ้ำ ซึ่งเป็น
        // สิ่งที่ ReceiptIssuePolicy ทั้งไฟล์เขียนขึ้นมาเพื่อกัน
        if (sourceServesAsReceipt)
            return "ใบต้นทางรับเงินครบในวันเดียวกับวันที่บนใบ ระบบจึงพิมพ์หัวเป็น "
                + "\"ใบกำกับภาษี/ใบเสร็จรับเงิน\" — ตัวใบเองเป็นใบเสร็จของการรับเงินนี้แล้ว "
                + "ออกอีกใบจะมีกระดาษ 2 ใบที่อ้างการรับเงินก้อนเดียวกัน. "
                + "ถ้าต้องการแยกใบเสร็จออกมาทุกครั้ง ให้ตั้งค่าบริษัทเป็น "
                + "\"แยกใบกำกับภาษี–ใบเสร็จรับเงิน\" ที่หน้าตั้งค่า → เอกสาร";
        return null;
    }
}
