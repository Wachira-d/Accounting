namespace Accounting.Helpers;

/// <summary>
/// <b>ข้อความ "ไฟล์แนบยื่นแบบของรอบเงินเดือนไม่ถูกสร้าง" — ตัวเดียว</b> (ฝ่ายค้าน W1c P2-5 · ทีม W1d)
///
/// <para>ตอนจ่ายเงินเดือน ระบบสร้าง ภ.ง.ด.1 + สปส.1-10 แนบรอบให้อัตโนมัติ (best-effort หลังตอบ — การจ่ายไม่ล้มเพราะไฟล์แนบ ·
/// คำตัดสินข้อ 150) · ไฟล์ ภ.ง.ด.1 ถูกกั้นได้ด้วยด่านของไฟล์นำเข้า (<see cref="Pnd1FileFormat.ImportFileBlocker"/>: เลขผู้เสียภาษีบริษัทผิด ·
/// สาขาผู้หัก ≥ 10000) · เดิมมีแค่ LogWarning ⇒ รอบที่จ่ายแล้ว "ไม่มีไฟล์ ภ.ง.ด.1" โดยไม่มีใครรู้เหตุ (F2 #7)</para>
///
/// <para>ข้อความนี้ไปสามที่ (ล้มดัง 3 ที่): <c>PayrollRun.FilingAttachmentWarning</c> (แบนเนอร์หน้ารอบ) · การแจ้งเตือน PayrollPaid ·
/// <c>Notice</c> ของคำตอบปุ่ม "จ่าย" (<see cref="PredictAtPayment"/> — ด่านเดียวกัน ตัดสินก่อนตอบ เพราะงานสร้างไฟล์รันหลังตอบ)</para>
/// </summary>
public static class PayrollFilingAttachmentNotice
{
    /// <summary>ทางไปต่อร่วม — ไฟล์ยื่นจริงสร้างได้เสมอจากเมนู Export ยื่นภาษี (ไฟล์แนบของรอบเป็นสำเนาอำนวยความสะดวก)</summary>
    public const string WayForward =
        "หลังแก้แล้ว ดาวน์โหลดไฟล์ของงวดนี้ได้ที่เมนู \"Export ยื่นภาษี\" (ไฟล์แนบของรอบจะสร้างใหม่ให้เมื่อกลับรายการจ่ายแล้วจ่ายใหม่เท่านั้น)";

    /// <summary>ภ.ง.ด.1 สร้างไม่ได้ — ข้อความธุรกิจ (มีทางแก้ในตัว) แสดงตรง · ข้อผิดพลาดอื่นไม่ echo ข้อความภายใน (กฎเหล็ก #4 E)</summary>
    public static string Pnd1Missing(Exception ex) => Missing("ภ.ง.ด.1", ex);

    /// <summary>สปส.1-10 สร้างไม่ได้ — รูปเดียวกับ <see cref="Pnd1Missing"/></summary>
    public static string Sso110Missing(Exception ex) => Missing("สปส.1-10", ex);

    private static string Missing(string form, Exception ex)
        => $"ไม่มีไฟล์แนบ {form} ของรอบนี้ — "
            + (ex is BusinessRuleException br ? br.Message : "ระบบสร้างไฟล์ไม่สำเร็จ (บันทึกรายละเอียดไว้ใน log ของระบบ — แจ้งผู้ดูแลระบบ)")
            + " · " + WayForward;

    /// <summary>รวมหลายไฟล์เป็นข้อความเดียว · ไม่มีปัญหา = null (ล้างแบนเนอร์)</summary>
    public static string? Combine(IReadOnlyCollection<string> problems)
        => problems.Count == 0 ? null : string.Join("\n", problems);

    /// <summary>คำเตือนสำหรับคำตอบปุ่ม "จ่าย" — รอบมีภาษีหัก และด่านไฟล์นำเข้าของผู้หักจะกั้น ⇒ บอกทันทีว่าจะไม่มีไฟล์แนบ ภ.ง.ด.1 ·
    /// ไม่มีภาษีหัก (ไม่สร้างไฟล์อยู่แล้ว) หรือผ่านด่าน = null</summary>
    public static string? PredictAtPayment(decimal totalWithholdingTax, string? companyTaxId, string? companyBranchCode)
    {
        if (totalWithholdingTax <= 0) return null;
        var blocker = Pnd1FileFormat.ImportFileBlocker(companyTaxId, companyBranchCode, "ภ.ง.ด.1");
        return blocker == null ? null : "⚠️ " + Pnd1Missing(blocker);
    }
}
