using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาเดียวของระบบว่า "การขายหนึ่งครั้งออกกระดาษกี่ใบ" ตาม
/// <see cref="ReceiptIssueMode"/> ของบริษัท
///
/// ═══ ทำไมต้องเป็นตัวกลาง ═══
/// เงื่อนไขนี้ถูกอ่านจาก <b>5 จุดที่อยู่คนละไฟล์</b>: ตอนตัดสินหัวกระดาษ
/// (<c>PdfGenerationService.ResolveServedAsReceiptAsync</c>), ตอนตอบ API/หน้าจอ
/// (<c>DocumentService.ComputeServedAsReceipt</c>), ตอนบันทึกรับชำระ
/// (<c>DocumentService.CreatePaymentAsync</c>), ตอนสร้างเอกสาร (ฟอร์ม
/// documents.html) และตอนกันภาษีขายซ้ำ. ถ้าปล่อยให้แต่ละที่เขียน
/// <c>mode == ReceiptIssueMode.X</c> เอง = สำเนามือ 5 ชุดที่รอ drift
/// (กฎเหล็ก #4 A — defect class ที่เรพนี้เจอบ่อยที่สุด)
///
/// **ทุกฟังก์ชันที่ตอบว่า "ไม่ได้/ไม่ออก" ต้องมีคู่ที่อธิบายเหตุผลเป็นข้อความ
/// ไทยที่เอาไปโชว์ได้** — ห้ามให้หน้าจอซ่อนปุ่มเงียบ ๆ แล้วผู้ใช้เดาเอง
/// </summary>
public static class ReceiptIssuePolicy
{
    /// <summary>ใบกำกับที่รับเงินครบแล้ว "ยกหัวเป็นใบเสร็จในตัว" ได้ไหม
    /// (<c>ServedAsReceipt</c>). โหมดแยกใบ = ไม่ได้ เพราะใบเสร็จตัวจริงคือ REC
    /// อีกใบ — ถ้ายกหัวด้วยจะมีกระดาษ 2 ใบที่ต่างพูดว่า "ใบเสร็จรับเงิน"
    /// จากการรับเงินก้อนเดียว</summary>
    public static bool AllowsCombinedReceiptHeader(ReceiptIssueMode mode)
        => mode != ReceiptIssueMode.SeparateReceipt;

    /// <summary>ค่าเริ่มต้นของ "ออกใบเสร็จให้ลูกค้าด้วยไหม" ตอนบันทึกรับชำระ
    /// (<c>CreatePaymentRequest.IssueReceiptDocument</c> ที่ไม่ได้ระบุมา)
    ///
    /// Combined คืน true = <b>พฤติกรรมเดิมเป๊ะ</b> (server เดิมใช้ <c>?? true</c>)
    /// — โหมดนี้เป็น default ของทุก tenant จึงต้องไม่เปลี่ยนอะไรเลย</summary>
    public static bool DefaultIssueSeparateReceipt(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt => true,
        // ค้าปลีก: รับเงินพร้อมออกใบอยู่แล้ว ออกใบเสร็จตามหลังอีกใบ = กระดาษเกิน
        ReceiptIssueMode.RetailReceipt => false,
        _ => true,
    };

    /// <summary>บังคับออกใบเสร็จแยกเสมอไหม — true = หน้าจอต้อง<b>ล็อกช่อง</b>
    /// (ติ๊กไว้ กดออกไม่ได้) พร้อมบอกเหตุผล ไม่ใช่ปล่อยให้ติ๊กออกแล้วเงียบ</summary>
    public static bool ForcesSeparateReceipt(ReceiptIssueMode mode)
        => mode == ReceiptIssueMode.SeparateReceipt;

    /// <summary>เปิดให้เลือกกระดาษแบบ "ใบกำกับ+ใบเสร็จในใบเดียว" ตอนสร้างเอกสาร
    /// ไหม (โหมดกระดาษ <c>tax_receipt</c> / <c>invoice_tax_paid</c> /
    /// <c>tax_paid</c> ในฟอร์ม). โหมดแยกใบ = ไม่เปิด — ไม่งั้นผู้ใช้เลือกทางที่
    /// ขัดกับนโยบายของบริษัทตัวเองได้จากหน้าเดียวกัน</summary>
    public static bool AllowsCashReceiptPapers(ReceiptIssueMode mode)
        => mode != ReceiptIssueMode.SeparateReceipt;

    /// <summary>คำอธิบายสั้น ๆ ของโหมด — ใช้ทั้งหน้าตั้งค่าและข้อความบอกเหตุผล
    /// เวลาช่องถูกล็อก (ข้อความชุดเดียว ห้ามให้แต่ละหน้าจอแต่งคำเอง)</summary>
    public static string Describe(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt =>
            "บริษัทตั้งค่าเป็น \"แยกใบกำกับภาษี–ใบเสร็จรับเงิน\" — ใบกำกับ (TIV) ออก ณ วันส่งมอบ/วางบิล "
            + "และใบเสร็จ (REC) ออก ณ วันรับเงินเสมอ",
        ReceiptIssueMode.RetailReceipt =>
            "บริษัทตั้งค่าเป็น \"ค้าปลีก (ใบเดียวที่จุดขาย)\" — ออกใบเสร็จรับเงิน/ใบกำกับภาษีใบเดียวจบ "
            + "ไม่ออกใบเสร็จตามหลังการรับชำระ",
        _ =>
            "บริษัทตั้งค่าเป็น \"ใบเดียวจบ\" — ใบกำกับที่รับเงินครบแล้วทำหน้าที่ใบเสร็จในตัว",
    };

    /// <summary>ชื่อโหมดสั้น ๆ สำหรับ dropdown/ป้าย</summary>
    public static string Label(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt => "แยกใบกำกับภาษี–ใบเสร็จรับเงิน",
        ReceiptIssueMode.RetailReceipt => "ค้าปลีก (ใบเดียวที่จุดขาย)",
        _ => "ใบเดียวจบ (ค่าเริ่มต้น)",
    };
}
