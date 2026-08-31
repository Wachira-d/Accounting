using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "เอกสารชนิดนี้อยู่ฝั่งซื้อหรือฝั่งขาย" — **ตัวตัดสินกลางตัวเดียวของระบบ**
///
/// ═══ ทำไมต้องมี ═══
/// ผลตรวจพบว่าคำถามเดียวกันนี้ถูกตอบด้วยโค้ดคนละชุด <b>3 ที่</b> และ
/// ทั้งสามให้คำตอบไม่ตรงกัน:
/// <list type="bullet">
/// <item><c>OcrScanComplianceEvaluator.IsPurchaseSide</c> — นับ GRN/ใบขอซื้อด้วย</item>
/// <item><c>OcrService.CreateDocumentFromScanAsync.isSalesSide</c> — CN/DN เป็นฝั่งขาย
///   เฉพาะเมื่อ <c>OurRole == "Seller"</c></item>
/// <item><c>document-scan.html salesTypes</c> — รวม DeliveryNote และตัด CN/DN
///   ออกจากฝั่งขายเสมอ</item>
/// </list>
/// สามคำตอบที่ต่างกันบนเอกสารใบเดียว = คำเตือน/บัญชีคู่/รายงานภาษีไปคนละทาง
/// (defect class เดียวกับ "รายการที่คัดลอกมาด้วยมือ" ใน CLAUDE.md)
///
/// ═══ กติกา ═══
/// <b>ใบลดหนี้/ใบเพิ่มหนี้เป็นชนิดที่อยู่ได้สองฝั่ง</b> — enum ตัวเดียวใช้ทั้ง
/// CN ขาย (ลดหนี้ให้ลูกค้า) และ CN ซื้อ (ผู้ขายลดหนี้ให้เรา) ⇒ ตัวชนิดอย่างเดียว
/// ตัดสินไม่ได้ ต้องดู <c>OurRole</c> ประกอบ. ผู้เรียกที่ไม่มี OurRole ให้ใช้
/// <see cref="IsAmbiguous"/> เช็คก่อน แล้วจัดการเคสกำกวมอย่างตั้งใจ
/// ห้ามเดาเงียบ ๆ
/// </summary>
public static class DocumentSide
{
    /// <summary>ชนิดที่เป็นฝั่งขายเสมอ ไม่ว่าบทบาทจะเป็นอะไร</summary>
    private static readonly HashSet<DocumentType> AlwaysSales = new()
    {
        DocumentType.Quotation,
        DocumentType.Invoice,
        DocumentType.Receipt,
        DocumentType.TaxInvoice,
        DocumentType.BillingNote,
        DocumentType.ReceiptVoucher,
    };

    /// <summary>ชนิดที่เป็นฝั่งซื้อเสมอ</summary>
    private static readonly HashSet<DocumentType> AlwaysPurchase = new()
    {
        DocumentType.PurchaseRequisition,
        DocumentType.PurchaseOrder,
        DocumentType.GoodsReceiptNote,
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.PaymentVoucher,
        DocumentType.CertificateInLieu,
    };

    /// <summary>ชนิดที่อยู่ได้ทั้งสองฝั่ง — ต้องใช้ <c>OurRole</c> ตัดสิน
    ///
    /// <para>DeliveryNote อยู่ตรงนี้เพราะ "ใบส่งของ" ที่เราออกให้ลูกค้า = ฝั่งขาย
    /// แต่ใบส่งของที่ผู้ขายแนบมากับของ = ฝั่งซื้อ (กลายเป็น GRN)</para></summary>
    private static readonly HashSet<DocumentType> BothSides = new()
    {
        DocumentType.CreditNote,
        DocumentType.DebitNote,
        DocumentType.DeliveryNote,
    };

    /// <summary>ชนิดนี้ต้องรู้บทบาทก่อนถึงจะบอกฝั่งได้</summary>
    public static bool IsAmbiguous(DocumentType type) => BothSides.Contains(type);

    /// <summary>
    /// ฝั่งขายหรือไม่ — <paramref name="ourRole"/> ใช้เฉพาะกับชนิดกำกวม
    /// (CN/DN/ใบส่งของ); ชนิดอื่นตอบจากตัวชนิดเอง
    ///
    /// <para>เมื่อชนิดกำกวมและไม่รู้บทบาท → คืน <c>false</c> (ถือเป็นฝั่งซื้อ)
    /// ซึ่งเป็น default ที่ปลอดภัยกว่า: เอกสารที่สแกนเข้ามาส่วนใหญ่เป็นฝั่งซื้อ
    /// และการตีเป็นฝั่งขายผิดจะไปสร้างรายการในรายงานภาษี<b>ขาย</b>ซึ่งกระทบ
    /// ภ.พ.30 ที่ยื่นออกไปแล้ว</para>
    /// </summary>
    public static bool IsSales(DocumentType type, string? ourRole = null)
    {
        if (AlwaysSales.Contains(type)) return true;
        if (AlwaysPurchase.Contains(type)) return false;
        return string.Equals(ourRole, "Seller", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ฝั่งซื้อหรือไม่ — ตรงข้ามกับ <see cref="IsSales"/></summary>
    public static bool IsPurchase(DocumentType type, string? ourRole = null)
        => !IsSales(type, ourRole);

    /// <summary>ชนิดเอกสารกับบทบาทที่อนุมานได้ อยู่ฝั่งเดียวกันไหม —
    /// ใช้เป็น guard ก่อนยอมให้ "ความรู้จากประวัติ/ระบบกลาง" มาทับเป้าหมาย
    /// (ประวัติรู้แค่ว่า vendor รายนี้มักลงเป็นอะไร ไม่รู้ว่าใบนี้เราอยู่ฝั่งไหน)</summary>
    public static bool MatchesRole(DocumentType type, string? ourRole)
    {
        if (string.IsNullOrEmpty(ourRole)) return true;      // ไม่รู้บทบาท = ไม่ขวาง
        if (IsAmbiguous(type)) return true;                   // ชนิดสองฝั่ง = ไม่ขวาง
        var wantSales = string.Equals(ourRole, "Seller", StringComparison.OrdinalIgnoreCase);
        return IsSales(type) == wantSales;
    }

    /// <summary>แผนที่ "ชื่อชนิดเอกสาร → ฝั่ง" สำหรับส่งให้หน้าเว็บใช้แทนการ
    /// ถือลิสต์ของตัวเอง. ค่า: <c>"Sales"</c> · <c>"Purchase"</c> · <c>"Both"</c>
    ///
    /// <para>สร้างจาก 3 เซ็ตข้างบนโดยตรง ⇒ เพิ่ม/ย้ายชนิดที่นี่ที่เดียว
    /// หน้าเว็บตามทันทีโดยโครงสร้าง (drift เป็นศูนย์ — กลไกเดียวกับที่ใช้กับ
    /// <c>MENU_SECTIONS</c> และ <c>complianceIssues</c>)</para>
    ///
    /// <para>ชนิดที่ไม่อยู่ในเซ็ตไหนเลย (เช่น JournalVoucher) ไม่ถูกใส่ในแผนที่ —
    /// ผู้เรียกต้องตีความว่า "ไม่ใช่เอกสารซื้อ/ขาย" ไม่ใช่เดาเป็นฝั่งใดฝั่งหนึ่ง</para></summary>
    public static IReadOnlyDictionary<string, string> BuildSideMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in AlwaysSales) map[t.ToString()] = "Sales";
        foreach (var t in AlwaysPurchase) map[t.ToString()] = "Purchase";
        foreach (var t in BothSides) map[t.ToString()] = "Both";
        return map;
    }
}
