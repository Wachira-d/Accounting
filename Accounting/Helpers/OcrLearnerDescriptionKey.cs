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
/// <para>กติกา: คำอธิบาย<b>บรรทัดแรกที่มีข้อความ</b> ของรายการที่สแกนได้ · ไม่มีบรรทัด/ไม่มีข้อความ ⇒ <c>""</c>
/// (ตัวเรียนตกไปชั้นผู้ขายอย่างเดียว) · <b>ห้าม fallback เป็นชื่อหมวด</b> — นั่นคือบั๊กเดิม</para>
/// </summary>
public static class OcrLearnerDescriptionKey
{
    /// <summary>คีย์คำอธิบายของใบ — ส่งคำอธิบายบรรทัดตามลำดับบนกระดาษ</summary>
    public static string For(IEnumerable<string?>? lineDescriptions)
    {
        if (lineDescriptions == null) return "";
        foreach (var d in lineDescriptions)
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        return "";
    }
}
