namespace Accounting.Helpers;

/// <summary>
/// **ผังตั้งต้นตามชนิดกระดาษที่สแกน** (ตัวเดียว) — ผู้อ่าน: <c>OcrService.ParseThaiDocument</c> (เติม Dr/Cr ตั้งต้นของเส้น Tesseract/PDF text-layer)
/// และด่านวางฝั่งของ <c>OcrService.ScanAsync</c> (ตัดเดบิตที่เป็นหนี้สิน/ทุนแล้วต้องมีเดบิตแทน)
///
/// <para>═══ ที่มา (ฝ่ายค้าน f5b10674 · P2-A · 2026-10-16) ═══ ด่านวางฝั่งตัดเดบิตที่ตัวเรียนสอนไว้ (21230 เจ้าหนี้กรรมการ) แล้ว seed ใหม่จาก
/// ผังของหมวดที่ตัวจัดหมวดอ่านได้ · ใบที่ไม่มีกฎหมวดไหนตรง เดิมได้ seed จากชื่อหมวดตั้งต้น ("ค่าบริการ") ซึ่งถูกถอดแล้ว ⇒ seed = (null, null) ⇒
/// <b>เดบิตว่าง</b> (กฎเหล็ก #3 ห้าม) · ตอนนี้ตกมาที่ผังตั้งต้นของชนิดกระดาษ (ตารางเดียวกับที่ ParseThaiDocument ใช้) แล้วผังค่าใช้จ่ายทั่วไป</para>
///
/// <para>═══ ฝ่ายค้าน EC2 P2-1 (2026-10-16) ═══ รหัสเดบิตเดิม (5100 · 5300 · 1200) <b>ไม่มีในผังมาตรฐาน</b> (<c>ChartOfAccountTemplates</c> ใช้ 5 หลัก) ⇒
/// ตัวหาผังตกไปค้นตามชื่อ แล้วได้ <b>บัญชีคุม</b> "51 ต้นทุนขาย" / "54 ค่าใช้จ่ายในการบริหาร" (มีบัญชีย่อย ลงรายการไม่ได้) ⇒ ตอนสร้างเอกสาร
/// ด่าน IsPostableGl ตัดทิ้ง ⇒ เดบิตกลับมาว่างที่ฟอร์ม · และใบกำกับภาษีของบริษัทที่ไม่ถือสต็อกได้ "ต้นทุนขาย" · ตอนนี้: รหัสเดบิตทุกตัวเป็น
/// <b>บัญชีย่อยระดับ 4 ที่มีอยู่จริงในผังมาตรฐาน</b> (เทสต์ล็อกกับแม่แบบจริง) · ใบซื้อสินค้าได้ผังต้นทุนสินค้า/สินค้าคงเหลือ<b>เฉพาะบริษัทที่ถือสต็อก</b>
/// (<see cref="InventoryIndustry.KeepsInventory"/>) · ไม่รู้ประเภทกิจการ (ParseThaiDocument ยังไม่รู้จักบริษัท) ⇒ ค่าใช้จ่ายบริหารอื่น ๆ + ไฮไลต์เหลือง</para>
///
/// <para>ฝั่งเครดิต (เจ้าหนี้/เงินสด) และเดบิตที่เป็นหนี้สิน (ใบลดหนี้ · หนังสือรับรองหัก ณ ที่จ่าย) <b>คงรหัสเดิม</b> — ไม่อยู่ในขอบเขตรอบนี้
/// (เครดิตที่ตรงผังจริงจะเปิดเส้นแหล่งเงินตอนสร้างเอกสาร ต้องตรวจแยก) · ตัวเลือกเดบิตแทนตัดหนี้สินทิ้งเองอยู่แล้ว</para>
///
/// <para>═══ ฝ่ายค้าน EC3 P2-7 (ทีม EC4 · 2026-10-16) ═══ "บริษัทถือสต็อก" ไม่ได้แปลว่า "ของบนใบนี้เป็นสินค้าคงเหลือ" — ใบกำกับภาษีค่าซ่อมแอร์/ค่าเช่า
/// ของบริษัทซื้อขายเคยได้ 51110 ต้นทุนขาย (เส้นด่านวางฝั่ง) ⇒ กำไรขั้นต้นเพี้ยน · ตอนนี้ผังสต็อก (51110 · 11500) ต้องผ่าน <see cref="PurchaseIsStock"/>:
/// บริษัทถือสต็อก <b>และทุกบรรทัดจับคู่กับสินค้า master ได้</b> (<c>OcrExtractedData.ProductMasterMatchedLines</c> — หลักฐานเดียวที่มีตอนนั้นว่าของบนใบคือสินค้าของเรา) ·
/// ไม่รู้ ⇒ 54910 ค่าใช้จ่ายทั่วไป + ไฮไลต์เหลือง (ห้ามเดาเป็นต้นทุนขาย)</para>
///
/// <para>ไม่ใช่หมวดค่าใช้จ่าย — ไม่มีชื่อหมวดในตารางนี้ (คำตัดสินเจ้าของ 2026-10-16 "ถอดออกเลย")</para>
/// </summary>
public static class OcrDocTypeDefaultAccounts
{
    public readonly record struct Defaults(string DebitCode, string DebitName, string CreditCode, string CreditName);

    /// <summary>ผังเดบิตค่าใช้จ่ายทั่วไป (บัญชีย่อยใต้ 549 ในผังมาตรฐาน) — ชั้นสุดท้ายเมื่อชนิดกระดาษไม่มีผังตั้งต้นที่เป็นค่าใช้จ่าย/สินทรัพย์</summary>
    public static readonly (string Code, string Name) GenericExpenseDebit = ("54910", "ค่าใช้จ่ายบริหารอื่นๆ");

    /// <summary>ใบซื้อสินค้าคงเหลือ (<see cref="PurchaseIsStock"/>) — ผังเดียวกับกฎ "ซื้อสินค้า / วัตถุดิบ" ของตัวจัดหมวด</summary>
    private static readonly (string Code, string Name) GoodsCostDebit = ("51110", "ต้นทุนสินค้า");

    /// <summary>ใบสั่งซื้อสินค้าคงเหลือ (<see cref="PurchaseIsStock"/>)</summary>
    private static readonly (string Code, string Name) InventoryDebit = ("11500", "สินค้าคงเหลือ");

    /// <summary>ชนิดกระดาษที่เป็น "ซื้อสินค้า" โดยปกติ — เดบิตขึ้นกับว่าบริษัทถือสต็อกไหม</summary>
    private static readonly HashSet<string> GoodsPurchaseTypes = new(StringComparer.Ordinal) { "TaxInvoice", "Invoice", "DebitNote" };

    private static readonly Dictionary<string, (string CreditCode, string CreditName)> Credits = new(StringComparer.Ordinal)
    {
        ["TaxInvoice"] = ("2100", "เจ้าหนี้การค้า"),
        ["Invoice"] = ("2100", "เจ้าหนี้การค้า"),
        ["Receipt"] = ("1110", "เงินสด"),
        ["PurchaseOrder"] = ("2100", "เจ้าหนี้การค้า"),
        ["WHT"] = ("1110", "เงินสด"),
        ["CreditNote"] = ("5100", "ต้นทุนขาย"),
        ["DebitNote"] = ("2100", "เจ้าหนี้การค้า"),
    };

    /// <summary>เดบิตที่เป็นหนี้สิน (ไม่ใช่ค่าใช้จ่าย) — คงรหัสเดิม · ตัวเลือกเดบิตแทน (<see cref="GlDebitAccountPicker"/>) ตัดทิ้งเอง</summary>
    private static readonly Dictionary<string, (string Code, string Name)> LiabilityDebits = new(StringComparer.Ordinal)
    {
        ["WHT"] = ("2170", "ภาษีหัก ณ ที่จ่าย"),
        ["CreditNote"] = ("2100", "เจ้าหนี้การค้า"),
    };

    /// <summary>
    /// ของบนใบนี้เป็น<b>สินค้าคงเหลือ</b>ของบริษัทไหม — ตัวตัดสินเดียวของผังสต็อก (51110 / 11500)
    /// · บริษัทถือสต็อก (<see cref="InventoryIndustry.KeepsInventory"/>) <b>และ</b>ทุกบรรทัดจับคู่กับสินค้า master ได้ · ไม่มีบรรทัด/จับคู่ไม่ครบ ⇒ false (ไม่เดา)
    /// </summary>
    public static bool PurchaseIsStock(bool keepsInventory, int lineCount, int linesMatchedToProductMaster)
        => keepsInventory && lineCount > 0 && linesMatchedToProductMaster >= lineCount;

    /// <summary>ผัง Dr/Cr ตั้งต้นของชนิดกระดาษ · null = ไม่รู้จักชนิดนี้</summary>
    /// <param name="purchaseIsStock">ของบนใบเป็นสินค้าคงเหลือไหม (<see cref="PurchaseIsStock"/>) — false/ไม่รู้ ⇒ ใบซื้อได้ค่าใช้จ่ายทั่วไป ไม่ใช่ต้นทุนขาย</param>
    public static Defaults? For(string? scannedDocumentType, bool purchaseIsStock = false)
    {
        if (scannedDocumentType == null || !Credits.TryGetValue(scannedDocumentType, out var cr)) return null;
        var dr = DebitFor(scannedDocumentType, purchaseIsStock);
        return new Defaults(dr.Code, dr.Name, cr.CreditCode, cr.CreditName);
    }

    private static (string Code, string Name) DebitFor(string type, bool purchaseIsStock)
    {
        if (LiabilityDebits.TryGetValue(type, out var liab)) return liab;
        if (type == "PurchaseOrder") return purchaseIsStock ? InventoryDebit : GenericExpenseDebit;
        if (GoodsPurchaseTypes.Contains(type)) return purchaseIsStock ? GoodsCostDebit : GenericExpenseDebit;
        return GenericExpenseDebit;   // Receipt และชนิดอื่นที่รู้จัก
    }

    /// <summary>seed ของเดบิตแทนเมื่อด่านวางฝั่งตัดเดบิตทิ้งและหมวดไม่มีผัง — ตามลำดับ: ผังตั้งต้นของชนิดกระดาษ → ผังค่าใช้จ่ายทั่วไป
    /// (ผู้เรียกกรองประเภทบัญชี + บัญชีคุมเอง — <see cref="GlDebitAccountPicker"/> รับเฉพาะค่าใช้จ่าย/สินทรัพย์ ⇒ ผังตั้งต้นที่เป็นหนี้สินตกไปชั้นถัดไป)</summary>
    public static IReadOnlyList<(string Code, string Name)> DebitFallbackSeeds(string? scannedDocumentType, bool purchaseIsStock = false)
    {
        var seeds = new List<(string Code, string Name)>();
        if (For(scannedDocumentType, purchaseIsStock) is { } d) seeds.Add((d.DebitCode, d.DebitName));
        if (!seeds.Contains(GenericExpenseDebit)) seeds.Add(GenericExpenseDebit);
        return seeds;
    }
}
