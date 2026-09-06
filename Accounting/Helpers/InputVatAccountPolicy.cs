namespace Accounting.Helpers;

/// <summary>
/// **ผังบัญชีเป็นคนตัดสินว่า "บรรทัดนี้เคลมภาษีซื้อได้ไหม" — ตัวเดียวของระบบ**
///
/// ═══ ที่มา (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T1-05) ═══
/// <para><c>DocumentService.CreateDocumentAsync</c> โหลดธง
/// <c>ChartOfAccount.InputVatClaimable</c> ของทุกบัญชีที่บรรทัดใช้ แล้วบังคับ
/// <c>IsVatClaimable=false</c> เมื่อบัญชีถูกตั้งเป็นภาษีซื้อต้องห้าม (เช่น
/// "ค่ารับรอง" · "ค่าใช้จ่ายรถยนต์นั่ง") — แต่ <b>สาย OCR สร้าง Document เอง
/// ด้วย <c>_db.Documents.Add</c></b> จึงไม่เคยผ่านด่านนี้เลย ⇒ ใบเดียวกัน:
/// คีย์มือ = ไม่เคลม (ถูก) · สแกน = เคลม (ผิด §82/5) — <b>กติกาสองมาตรฐาน
/// ในระบบเดียว</b></para>
///
/// <para>แยกเป็นฟังก์ชันบริสุทธิ์เพื่อให้ทั้งสองเส้นเรียกตัวเดียวกันและทดสอบได้
/// (กฎ "Resolver กลาง ห้ามคำนวณเอง")</para>
/// </summary>
public static class InputVatAccountPolicy
{
    /// <summary>เหตุผลมาตรฐานเมื่อผังบัญชีเป็นตัวปิดการเคลม — ข้อความเดียวทั้งระบบ</summary>
    public const string ChartFlagReason = "บัญชีนี้ตั้งเป็นภาษีซื้อต้องห้ามในผังบัญชี";

    /// <summary>ผลของการบังคับใช้ธงผังบัญชีกับบรรทัดหนึ่ง</summary>
    /// <param name="Claimable">ค่าที่ต้องเขียนลง <c>DocumentLine.IsVatClaimable</c></param>
    /// <param name="Reason">เหตุผล (null = ไม่ต้องเปลี่ยน)</param>
    /// <param name="Changed">ธงถูกบังคับเปลี่ยนจากค่าที่ผู้เรียกส่งมาหรือไม่</param>
    public readonly record struct LineOutcome(bool Claimable, string? Reason, bool Changed);

    /// <summary>บังคับธงของบรรทัดหนึ่งตามผังบัญชี
    ///
    /// <para><paramref name="accountClaimable"/> = ค่าของ
    /// <c>ChartOfAccount.InputVatClaimable</c> ของบัญชีที่บรรทัดใช้
    /// (<c>null</c> = ไม่รู้จักบัญชี/ไม่มีบัญชี → ไม่บังคับอะไร)</para></summary>
    public static LineOutcome Apply(bool requestedClaimable, string? requestedReason, bool? accountClaimable)
    {
        if (accountClaimable == false)
            return new LineOutcome(false, requestedReason ?? ChartFlagReason, requestedClaimable);
        return new LineOutcome(requestedClaimable, requestedReason, false);
    }
}
