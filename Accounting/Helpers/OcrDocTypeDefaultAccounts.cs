namespace Accounting.Helpers;

/// <summary>
/// **ผังตั้งต้นตามชนิดกระดาษที่สแกน** (ตัวเดียว) — ผู้อ่าน: <c>OcrService.ParseThaiDocument</c> (เติม Dr/Cr ตั้งต้นของเส้น Tesseract/PDF text-layer)
/// และด่านวางฝั่งของ <c>OcrService.ScanAsync</c> (ตัดเดบิตที่เป็นหนี้สิน/ทุนแล้วต้องมีเดบิตแทน)
///
/// <para>═══ ที่มา (ฝ่ายค้าน f5b10674 · P2-A · 2026-10-16) ═══ ด่านวางฝั่งตัดเดบิตที่ตัวเรียนสอนไว้ (21230 เจ้าหนี้กรรมการ) แล้ว seed ใหม่จาก
/// ผังของหมวดที่ตัวจัดหมวดอ่านได้ · ใบที่ไม่มีกฎหมวดไหนตรง เดิมได้ seed จากชื่อหมวดตั้งต้น ("ค่าบริการ") ซึ่งถูกถอดแล้ว ⇒ seed = (null, null) ⇒
/// <b>เดบิตว่าง</b> (กฎเหล็ก #3 ห้าม) · ตอนนี้ตกมาที่ผังตั้งต้นของชนิดกระดาษ (ตารางเดียวกับที่ ParseThaiDocument ใช้) แล้วผังค่าใช้จ่ายทั่วไป</para>
///
/// <para>ไม่ใช่หมวดค่าใช้จ่าย — ไม่มีชื่อหมวดในตารางนี้ (คำตัดสินเจ้าของ 2026-10-16 "ถอดออกเลย")</para>
/// </summary>
public static class OcrDocTypeDefaultAccounts
{
    public readonly record struct Defaults(string DebitCode, string DebitName, string CreditCode, string CreditName);

    private static readonly Dictionary<string, Defaults> ByType = new(StringComparer.Ordinal)
    {
        ["TaxInvoice"] = new("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
        ["Invoice"] = new("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
        ["Receipt"] = new("5300", "ค่าใช้จ่ายบริหาร", "1110", "เงินสด"),
        ["PurchaseOrder"] = new("1200", "สินค้าคงเหลือ", "2100", "เจ้าหนี้การค้า"),
        ["WHT"] = new("2170", "ภาษีหัก ณ ที่จ่าย", "1110", "เงินสด"),
        ["CreditNote"] = new("2100", "เจ้าหนี้การค้า", "5100", "ต้นทุนขาย"),
        ["DebitNote"] = new("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
    };

    /// <summary>ผังเดบิตค่าใช้จ่ายทั่วไป — ชั้นสุดท้ายเมื่อชนิดกระดาษไม่มีผังตั้งต้นที่เป็นค่าใช้จ่าย/สินทรัพย์</summary>
    public static readonly (string Code, string Name) GenericExpenseDebit = ("5300", "ค่าใช้จ่ายบริหาร");

    /// <summary>ผัง Dr/Cr ตั้งต้นของชนิดกระดาษ · null = ไม่รู้จักชนิดนี้</summary>
    public static Defaults? For(string? scannedDocumentType)
        => scannedDocumentType != null && ByType.TryGetValue(scannedDocumentType, out var d) ? d : null;

    /// <summary>seed ของเดบิตแทนเมื่อด่านวางฝั่งตัดเดบิตทิ้งและหมวดไม่มีผัง — ตามลำดับ: ผังตั้งต้นของชนิดกระดาษ → ผังค่าใช้จ่ายทั่วไป
    /// (ผู้เรียกกรองประเภทบัญชีเอง — <see cref="GlDebitAccountPicker"/> รับเฉพาะค่าใช้จ่าย/สินทรัพย์ ⇒ ผังตั้งต้นที่เป็นหนี้สินตกไปชั้นถัดไป)</summary>
    public static IReadOnlyList<(string Code, string Name)> DebitFallbackSeeds(string? scannedDocumentType)
    {
        var seeds = new List<(string Code, string Name)>();
        if (For(scannedDocumentType) is { } d) seeds.Add((d.DebitCode, d.DebitName));
        if (!seeds.Contains(GenericExpenseDebit)) seeds.Add(GenericExpenseDebit);
        return seeds;
    }
}
