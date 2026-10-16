namespace Accounting.Helpers;

/// <summary>
/// **คีย์คำอธิบายตัวเดียวของตัวเรียนผังบัญชีหัวใบ** (<c>ExpenseCategoryLearner</c>) — ฝั่งเขียน (ผู้ใช้แก้ผลสแกน ·
/// <c>OcrService.SubmitCorrectionAsync</c>) และฝั่งอ่าน (<c>OcrService.ScanAsync</c> ทำนายผังเดบิต + NaiveBayes) ต้องได้ข้อความเดียวกัน
///
/// <para>═══ ที่มา (2026-10-16 · คำตัดสินเจ้าของ "ถอดช่องหมวดค่าใช้จ่ายออกเลย") ═══<br/>
/// เดิมฝั่งเขียนบันทึก <c>DescriptionKeyword</c> = <b>ชื่อหมวด</b> ("ค่าบริการ" · "ค่าประกัน") ขณะที่ฝั่งอ่านทำนายด้วย
/// <b>คำอธิบายบรรทัดแรก</b> ⇒ ชั้น "ผู้ขาย + คำอธิบายตรงกัน" (Tier 1 ความมั่นใจสูงสุด) <b>ไม่เคยแมตช์</b> — ผู้ใช้สอนกี่ครั้งก็ได้แค่ชั้น
/// "ผู้ขายอย่างเดียว" (×0.65) · และชื่อหมวดเองเป็นค่าที่ระบบ/LLM เดา (ค่าตั้งต้นตามชนิดกระดาษ "ค่าบริการ") ไม่ใช่ข้อความบนกระดาษ</para>
///
/// <para>กติกา: คำอธิบาย<b>บรรทัดแรกที่มีข้อความ</b> ของรายการที่สแกนได้ · <b>ห้าม fallback เป็นชื่อหมวด</b> — นั่นคือบั๊กเดิม</para>
///
/// <para>═══ ใบที่ไม่มีบรรทัด (ฝ่ายค้าน f5b10674 · P2-C) ═══<br/>
/// เดิม (ก่อน f5b10674) ใบไม่มีบรรทัดเขียน/อ่านด้วยหมวดตั้งต้นตามชนิดกระดาษทั้งสองฝั่ง ⇒ Tier 1 ตรงกันโดยบังเอิญ (สอนครั้งเดียว 0.667) ·
/// หลัง f5b10674 คีย์ว่าง ⇒ ตกชั้นผู้ขายอย่างเดียว 0.433 &lt; เกณฑ์ทับ 0.55 = ผู้ใช้สอนครั้งแรกแล้วใบถัดไปไม่ได้ผังที่สอน ·
/// ตอนนี้ <see cref="For(IEnumerable{string?}?, string?)"/> ให้คีย์ <c>#ไม่มีรายการ#&lt;ชนิดกระดาษ&gt;</c> — หลักฐานจากกระดาษ (ชนิดที่ไปป์ไลน์อ่านได้)
/// ไม่ใช่ชื่อหมวด · ฝั่งเขียนใช้ชนิดกระดาษ<b>ก่อนผู้ใช้แก้</b> (ใบถัดไปของผู้ขายเดียวกันจะถูกอ่านเป็นชนิดเดิม) · แถวเก่าที่คีย์เป็นหมวดตั้งต้น
/// อ่านได้ครั้งเดียวผ่าน <see cref="PredictAsync"/> (อ่านอย่างเดียว · ไม่เขียนคีย์เก่าเพิ่ม)</para>
/// </summary>
public static class OcrLearnerDescriptionKey
{
    /// <summary>คำนำหน้าคีย์ของใบที่ไม่มีบรรทัด — ไม่มีอักขระที่ตัวแยกคำของตัวเรียนตัด (ช่องว่าง , . - ( ) / : ;) ⇒ ชั้นคำใกล้เคียงไม่ชนคำอธิบายจริง</summary>
    public const string LinelessPrefix = "#ไม่มีรายการ#";

    /// <summary>คีย์คำอธิบายของใบ — ส่งคำอธิบายบรรทัดตามลำดับบนกระดาษ · ไม่มีบรรทัด ⇒ <c>""</c> (NaiveBayes ใช้รูปนี้: ฝึกจากบรรทัดเอกสารจริง)</summary>
    public static string For(IEnumerable<string?>? lineDescriptions)
    {
        if (lineDescriptions == null) return "";
        foreach (var d in lineDescriptions)
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        return "";
    }

    /// <summary>คีย์ของตัวเรียนผัง — มีบรรทัด ⇒ คำอธิบายบรรทัดแรก · ไม่มีบรรทัด ⇒ <c>#ไม่มีรายการ#&lt;ชนิดกระดาษ&gt;</c> (ตัวเดียวทั้งเขียนและอ่าน)</summary>
    /// <param name="scannedDocumentType">ชนิดกระดาษที่ไปป์ไลน์อ่านได้ (<c>OcrScanResult.DocumentType</c> ก่อนผู้ใช้แก้)</param>
    public static string For(IEnumerable<string?>? lineDescriptions, string? scannedDocumentType)
    {
        var key = For(lineDescriptions);
        return key.Length > 0 ? key : Lineless(scannedDocumentType);
    }

    /// <summary>คีย์ของใบไม่มีบรรทัดตามชนิดกระดาษ</summary>
    private static string Lineless(string? scannedDocumentType)
        => LinelessPrefix + (string.IsNullOrWhiteSpace(scannedDocumentType) ? "Unknown" : scannedDocumentType.Trim());

    private static bool IsLineless(string? key) => key != null && key.StartsWith(LinelessPrefix, StringComparison.Ordinal);

    /// <summary><b>อ่านอย่างเดียว</b> — คีย์ของแถวรุ่นก่อน 2026-10-16 ที่ใบไม่มีบรรทัดถูกเขียน/อ่านด้วยหมวดตั้งต้นตามชนิดกระดาษ (ค่าที่
    /// <c>ParseThaiDocument</c> และกติกาสำรองของ ocr-service ตั้งให้) · null = ชนิดที่ไม่มีหมวดตั้งต้น ·
    /// ⚠️ ห้ามใช้เป็นคีย์เขียน (นั่นคือบั๊กเดิม — ชื่อหมวดไม่ใช่ข้อความบนกระดาษ)</summary>
    private static string? LegacyLinelessKey(string? scannedDocumentType) => scannedDocumentType switch
    {
        "Receipt" => "ค่าบริการ",
        "TaxInvoice" or "Invoice" or "PurchaseOrder" or "CreditNote" or "DebitNote" => "ค่าสินค้า",
        "WHT" => "อื่นๆ",
        _ => null,
    };

    /// <summary>
    /// ทำนายผังของตัวเรียนด้วยคีย์ตัวเดียว + อ่านแถวรุ่นเก่าของใบไม่มีบรรทัด<b>ครั้งเดียว</b> เมื่อคีย์ใหม่ยังไม่ถึงเกณฑ์
    /// (แถวที่ผู้ใช้สอนก่อน 2026-10-16 ยังให้ผลเท่าเดิม แทนที่จะตกชั้นผู้ขายอย่างเดียว)
    /// </summary>
    /// <param name="predict">ตัวทำนายของตัวเรียน (<c>ExpenseCategoryLearner.PredictAsync</c> ของผู้ขายนี้) รับคีย์คำอธิบาย</param>
    /// <param name="minConfidence">เกณฑ์ที่ผู้เรียกใช้ทับ (ScanAsync = 0.55)</param>
    public static async Task<(string? Code, string? Name, decimal Confidence, string Key, bool FromLegacyRow)> PredictAsync(
        Func<string, Task<(string? Code, string? Name, decimal Confidence)>> predict,
        IEnumerable<string?>? lineDescriptions, string? scannedDocumentType, decimal minConfidence)
    {
        var key = For(lineDescriptions, scannedDocumentType);
        var (code, name, conf) = await predict(key);
        if (!IsLineless(key) || (code != null && conf >= minConfidence)) return (code, name, conf, key, false);
        if (LegacyLinelessKey(scannedDocumentType) is not { } legacyKey) return (code, name, conf, key, false);
        var (lCode, lName, lConf) = await predict(legacyKey);
        return lCode != null && lConf > conf
            ? (lCode, lName, lConf, key, true)
            : (code, name, conf, key, false);
    }
}
