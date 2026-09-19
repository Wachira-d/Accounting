namespace Accounting.Helpers;

/// <summary>
/// **"ผู้ใช้รับคำตอบของ AI หรือไม่" — นิยามเดียวของทุกทางเข้า** (pure, ไม่มี I/O)
///
/// ═══ ที่มา (ผลตรวจ 2026-09-18 · D3-4) ═══
/// <para>เส้น "กดสร้างเอกสารจากการ์ด" (1-click) บันทึก
/// <c>acceptedAi = (scan.TargetDocumentType == docType)</c> — เทียบกับ <b>ค่าสุดท้าย</b>
/// ของช่อง ไม่ใช่คำตอบของ AI ⇒ AI ตอบ X แล้วชั้นหลัง (ประวัติผู้ขาย · คลังกลาง ·
/// เครดิตเทอม · ใบรับรองแทนใบเสร็จ · ใบมัดจำ) ทับเป็น Y ผู้ใช้กดสร้าง = คลังบันทึกว่า
/// "AI ถูก" ทั้งที่คำตอบของ AI ถูกทิ้งไปแล้ว ⇒ นักเรียนเรียนจาก label ปลอม แล้วดัน
/// Wilson ทะลุเกณฑ์ short-circuit จนไม่มีครูมาขัดอีก · ขณะที่เส้น
/// <c>SubmitCorrectionAsync</c> เทียบกับคำตอบ AI มาตลอด = <b>สองนิยามสองทางเข้า</b></para>
///
/// <para>กติกา: <b>ไม่มีคำตอบของ AI = ไม่มีทางเป็น "AI ถูก"</b> — เงื่อนไขที่เป็นเท็จเพราะ
/// ไม่มีข้อมูล ห้ามตกเป็น "ผ่าน" (DECISION_DOCTRINE §1)</para>
/// </summary>
public static class OcrAiLabelScope
{
    /// <param name="aiSuggestedAnswer">คำตอบที่ครู/นักเรียนตอบมาจริง ๆ
    /// (<c>OcrScanResult.TargetDocTypeAiSuggested</c> · <c>OurRoleAiSuggested</c> ฯลฯ) —
    /// <c>null</c>/ว่าง = ไม่มีใครตอบ หรือคำตอบไม่ผ่านด่านกันมั่ว</param>
    /// <param name="finalChoice">ค่าที่ผู้ใช้ยืนยัน/ระบบสร้างจริง</param>
    public static bool AcceptedAi(string? aiSuggestedAnswer, string? finalChoice)
    {
        if (string.IsNullOrWhiteSpace(aiSuggestedAnswer)) return false;
        if (string.IsNullOrWhiteSpace(finalChoice)) return false;
        // ชื่อ enum เดียวกันคนละตัวพิมพ์ = คำตอบเดียวกัน (ทางเข้าหนึ่งเคยเทียบแบบ
        // Ordinal อีกทางเทียบแบบ IgnoreCase ⇒ ใบเดียวกันได้ label คนละอย่าง)
        return string.Equals(aiSuggestedAnswer.Trim(), finalChoice.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
