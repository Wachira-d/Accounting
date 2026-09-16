using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ยอด VAT / ฐานภาษีของ "เอกสารที่ไม่มีบรรทัดย่อย" — **กติกาเดียวของทั้งระบบ**
///
/// <para><b>ที่มา</b>: <c>Expense</c> · <c>PaymentVoucher</c> · <c>CertificateInLieu</c>
/// เก็บยอดไว้ที่ <b>หัวเอกสาร</b> ได้โดยไม่มี <c>DocumentLine</c> เลย (เป็นทรงหลัก
/// ของใบบริการต่างประเทศและค่าใช้จ่ายที่คีย์เร็ว ๆ) ⇒ ทุกที่ที่เขียน
/// <c>doc.Lines.Sum(...)</c> ตรง ๆ จะได้ <b>0</b> ทั้งที่ GL มียอดเต็ม —
/// เงียบสนิทเพราะ 0 ก็เป็นตัวเลขที่ "ดูสมเหตุสมผล"</para>
///
/// <para>เจอมาแล้ว 2 ที่คนละรอบ: <c>TaxService.GeneratePp36Report</c> (แก้ไปแล้ว
/// ด้วย fallback ของตัวเอง) และหน้า <b>"ภาษีซื้อยังไม่ถึงกำหนด"</b>
/// (<c>DocumentService</c>) ที่ยังเหลืออยู่ ⇒ ยกเป็นตัวเดียวตามกฎ CLAUDE.md
/// "แก้ตัวเดียว เหลือที่เหลือ"</para>
/// </summary>
public static class DocumentVatFallback
{
    /// <summary>true = เอกสารนี้เก็บยอดไว้ที่หัว ไม่มีบรรทัดย่อยให้รวม</summary>
    public static bool IsHeaderOnly(ICollection<DocumentLine>? lines)
        => lines is not { Count: > 0 };

    /// <summary>
    /// ภาษีซื้อที่ <b>เคลมได้</b> ของเอกสาร — มีบรรทัด: รวมเฉพาะบรรทัดที่
    /// <c>IsVatClaimable</c> · ไม่มีบรรทัด: ใช้ VAT ระดับหัวเอกสาร
    ///
    /// <para>เอกสาร header-only ไม่มีธง claimable รายบรรทัดให้ดู — ธง §82/5 ของมัน
    /// ถูกบันทึกไว้คนละที่ (<c>[VAT-CLAIM]</c> ใน ProcessingNotes / ผังบัญชีที่เลือก)
    /// ⇒ ที่นี่คืนยอดเต็มแล้วให้ด่านของเส้นนั้นเป็นตัวตัด — <b>คืน 0 คือการโกหก</b>
    /// เพราะแปลว่า "ไม่มีภาษีซื้อ" ซึ่งไม่จริง</para>
    /// </summary>
    public static decimal ClaimableVat(ICollection<DocumentLine>? lines, decimal headerVatAmount)
        => IsHeaderOnly(lines)
            ? headerVatAmount
            : lines!.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount);

    /// <summary>
    /// ฐานก่อน VAT ของเอกสาร — มีบรรทัด: รวมบรรทัดที่ไม่ใช่ "ยกเว้น VAT"
    /// (<c>VatRate == -1</c>) · ไม่มีบรรทัด: <c>SubTotal</c> ถ้ามี ไม่งั้นถอยจาก
    /// ยอดรวมหัก VAT
    /// </summary>
    public static decimal TaxBase(ICollection<DocumentLine>? lines,
        decimal subTotal, decimal totalAmount, decimal vatAmount)
        => IsHeaderOnly(lines)
            ? (subTotal > 0 ? subTotal : totalAmount - vatAmount)
            : lines!.Where(l => l.VatRate != -1).Sum(l => l.Amount);
}
