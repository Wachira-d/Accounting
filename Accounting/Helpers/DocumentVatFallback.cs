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
/// <para><b>ขอบเขตที่จงใจแคบ</b>: มีเฉพาะ "ฐานก่อน VAT" ซึ่งเป็นกติกาที่
/// <c>TaxService.GeneratePp36Report</c> ใช้อยู่จริง. เคยมีเมธอด <c>ClaimableVat</c>
/// คู่กัน แล้วต้องถอดออก — สาขา header-only ของมัน<b>ข้ามธง <c>IsVatClaimable</c>
/// §82/5 ทั้งหมด</b> และแยกไม่ออกระหว่าง "ไม่มีบรรทัดจริง" กับ "ผู้เรียกลืม
/// <c>.Include(Lines)</c>" ⇒ ผู้เรียกคนถัดไปที่ลืม Include จะได้ VAT ต้องห้าม
/// นับเป็นเคลมได้โดยไม่มี error. <b>ห้ามเพิ่มกลับ</b>เว้นแต่พิสูจน์ได้ว่ามีแถวจริง
/// ที่ไม่มีบรรทัด และมีด่าน §82/5 รองรับที่ฝั่งผู้เรียก</para>
/// </summary>
public static class DocumentVatFallback
{
    /// <summary>true = เอกสารนี้เก็บยอดไว้ที่หัว ไม่มีบรรทัดย่อยให้รวม</summary>
    public static bool IsHeaderOnly(ICollection<DocumentLine>? lines)
        => lines is not { Count: > 0 };

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
