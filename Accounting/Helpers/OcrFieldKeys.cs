namespace Accounting.Helpers;

/// <summary>
/// ชื่อ "ช่อง" ของผลสแกน OCR — **คำศัพท์กลางชุดเดียวของระบบ**
///
/// ═══ ทำไมต้องมี (ผลตรวจของทีมวิเคราะห์ข้อมูล) ═══
/// <c>FieldConfidence</c> ถูกเขียนจาก 4 แหล่ง ด้วยชื่อ key <b>3 ชุดที่ไม่ตรงกัน</b>:
/// <list type="bullet">
/// <item>Azure DI ใช้ชื่อของ Azure — <c>VendorName · VendorTaxId · InvoiceId ·
///   InvoiceDate · TotalTax · InvoiceTotal</c></item>
/// <item><c>SmartFieldExtractor</c> ใช้ชื่อของตัวเอง — <c>SellerName ·
///   SellerTaxId · DocumentNumber · DocumentDate · VatAmount · TotalAmount</c></item>
/// <item>หน้าเว็บถามอีกชุด — <c>SupplierName · SupplierTaxId · PaymentChannel</c>
///   ซึ่ง<b>ไม่มีฝั่งไหนผลิตเลยสักตัว</b></item>
/// </list>
///
/// ผลที่ตามมา 3 อย่าง ทั้งหมดเป็นการตายเงียบ:
/// <list type="number">
/// <item>ด่านลด confidence ใน <c>OcrConfidenceGateway</c> มองหาชื่อชุด Azure
///   ⇒ <b>ไม่เคยทำงานบน 3 ใน 4 เส้นทาง</b> — และเส้นทางที่มันไม่ทำงานคือเส้นทาง
///   ที่ความมั่นใจต่ำที่สุด (Tesseract/Python)</item>
/// <item>ไฮไลต์เหลือง "🤖 ตรวจสอบอีกครั้ง" บนฟอร์มเอกสาร (กฎเหล็ก #3 ข้อ 3)
///   แทบไม่เคยขึ้น เพราะ map ไปที่ชื่อที่ไม่มีใครผลิต</item>
/// <item>ป้าย % ข้างช่องในหน้า review ตกกลับไปใช้ confidence ของทั้งใบเมื่อหา
///   key ไม่เจอ ⇒ โชว์เลขเดียวกันข้างทุกช่อง ดูเหมือนเป็นข้อมูลรายช่องจริง</item>
/// </list>
///
/// กติกา: ทุกฝั่งที่ <b>เขียน</b> confidence ต้องเขียนด้วยชื่อในคลาสนี้
/// (ผู้ผลิตที่ใช้ชื่อของตัวเอง เช่น Azure ให้ผ่าน <see cref="Canonical"/> ก่อน)
/// และทุกฝั่งที่ <b>อ่าน</b> ต้องอ่านด้วยชื่อเดียวกัน
/// </summary>
public static class OcrFieldKeys
{
    public const string SellerName = "SellerName";
    public const string SellerTaxId = "SellerTaxId";
    public const string SellerBranchCode = "SellerBranchCode";
    public const string SellerAddress = "SellerAddress";
    public const string BuyerName = "BuyerName";
    public const string BuyerTaxId = "BuyerTaxId";
    public const string BuyerBranchCode = "BuyerBranchCode";
    public const string BuyerAddress = "BuyerAddress";
    public const string DocumentNumber = "DocumentNumber";
    public const string DocumentDate = "DocumentDate";
    public const string SubTotal = "SubTotal";
    public const string VatAmount = "VatAmount";
    public const string TotalAmount = "TotalAmount";
    public const string WhtRate = "WhtRate";
    public const string ExpenseCategory = "ExpenseCategory";
    public const string DebitAccount = "DebitAccount";
    public const string PaymentTerms = "PaymentTerms";

    /// <summary>ช่องที่ "ต้องมีค่า" ตาม §86/4 — ใช้เป็นชุดอ้างอิงของด่านลด
    /// confidence และของกฎ "OCR ต้องกรอกให้ครบ" (กฎเหล็ก #3 ข้อ 1)</summary>
    public static readonly string[] CriticalForTaxInvoice =
    {
        SellerName, SellerTaxId, BuyerTaxId, DocumentNumber, DocumentDate, TotalAmount,
    };

    /// <summary>ชื่อของผู้ผลิตแต่ละเจ้า → ชื่อกลาง. คืนชื่อเดิมเมื่อไม่รู้จัก
    /// (ไม่ทิ้งข้อมูล — แค่ไม่ได้มาตรฐาน)</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── ชื่อชุดของ Azure Document Intelligence ──
        ["VendorName"] = SellerName,
        ["MerchantName"] = SellerName,
        ["VendorTaxId"] = SellerTaxId,
        ["VendorAddress"] = SellerAddress,
        ["CustomerName"] = BuyerName,
        ["CustomerTaxId"] = BuyerTaxId,
        ["CustomerAddress"] = BuyerAddress,
        ["InvoiceId"] = DocumentNumber,
        ["InvoiceDate"] = DocumentDate,
        ["TotalTax"] = VatAmount,
        ["InvoiceTotal"] = TotalAmount,
        // ── ชื่อที่หน้าเว็บเคยถาม (ไม่มีใครผลิต แต่ map ไว้กันของเก่าพัง) ──
        ["SupplierName"] = SellerName,
        ["SupplierTaxId"] = SellerTaxId,
        // ── ชื่อชุด python microservice ──
        ["vendor_name"] = SellerName,
        ["vendor_tax_id"] = SellerTaxId,
        ["buyer_name"] = BuyerName,
        ["buyer_tax_id"] = BuyerTaxId,
        ["document_number"] = DocumentNumber,
        ["document_date"] = DocumentDate,
        ["sub_total"] = SubTotal,
        ["vat_amount"] = VatAmount,
        ["total_amount"] = TotalAmount,
    };

    /// <summary>แปลงชื่อของผู้ผลิตเป็นชื่อกลาง</summary>
    public static string Canonical(string key)
        => Aliases.TryGetValue(key, out var c) ? c : key;

    /// <summary>ยุบทั้ง dictionary เป็นชื่อกลาง — ค่าที่ชนกันเอาตัวที่มั่นใจกว่า
    /// (ผู้ผลิตสองเจ้าอาจให้ค่าช่องเดียวกันคนละชื่อ)</summary>
    public static Dictionary<string, double> Canonicalize(IEnumerable<KeyValuePair<string, double>>? source)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return result;
        foreach (var (k, v) in source)
        {
            var ck = Canonical(k);
            if (!result.TryGetValue(ck, out var existing) || v > existing) result[ck] = v;
        }
        return result;
    }
}
