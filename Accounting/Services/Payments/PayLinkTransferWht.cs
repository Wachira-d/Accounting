using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>
/// **ยอดที่ลูกค้าต้องโอน + ภาษีหัก ณ ที่จ่ายที่บันทึกคู่กับเงินโอน — สูตรเดียวกับลิงก์ชำระ/QR/<c>DocumentPaymentHandler</c>** (รอบ 252 WP-12A · AUTOPILOT §5.3 G-T3)
/// <para>ประกอบจากชิ้นเดิมทุกชิ้น ห้ามสูตรใหม่: <c>DocumentService.PaymentWhtFiguresAsync</c> (ใบลดหนี้ที่มีผล + ตัวนับหักไปแล้ว — NetCap) →
/// <c>DocumentPayLinkPolicy.BuyerWithholds</c> → <c>ExpectedWht</c> → <c>AmountsFor(...).Net</c> (ยอดโอน = ยอดค้างสุทธิ) · WHT ของงวด =
/// <c>WhtForPayment</c> · ผู้ซื้อไม่มีหน้าที่หัก ⇒ 0 ชัด ๆ (ไม่ใช่ null = ยอดแนะนำ — ฝ่ายค้าน S1 รอบ 244) · ผู้เรียก: <c>BankReceiptSettleService</c>
/// (ประเมินผู้สมัคร + บันทึกรับชำระ) — <c>DocumentPaymentHandler</c> ยังประกอบชิ้นเดียวกันเอง (ล็อกด้วย required_call_site_check ทั้งสองที่ · ย้ายมาใช้ตัวนี้ = งานต่อ)</para>
/// </summary>
public static class PayLinkTransferWht
{
    /// <param name="ExpectedNet">ยอดที่ลูกค้าต้องโอน (= ยอดค้างของเอกสาร)</param>
    /// <param name="ExpectedGross">ยอดก่อนหัก ณ ที่จ่าย</param>
    /// <param name="WhtRemaining">WHT ที่ยังไม่ถูกหัก (ตามที่คาดว่าผู้ซื้อจะหัก)</param>
    public sealed record TransferFigures(decimal ExpectedNet, decimal ExpectedGross, decimal WhtRemaining, bool? BuyerWithholds);

    public static async Task<TransferFigures> ForAsync(AccountingDbContext db, Guid companyId, Document doc, decimal amount, CancellationToken ct = default)
    {
        var whtFigures = await Accounting.Services.Implementations.DocumentService.PaymentWhtFiguresAsync(db, companyId, doc, amount, ct);
        var buyer = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == doc.ContactId && c.CompanyId == companyId)
            .Select(c => new { c.ContactType, c.TaxId }).FirstOrDefaultAsync(ct);
        var buyerWithholds = DocumentPayLinkPolicy.BuyerWithholds(buyer?.ContactType, buyer?.TaxId);
        var expectedWht = DocumentPayLinkPolicy.ExpectedWht(whtFigures.NetCap, buyerWithholds);
        var amounts = DocumentPayLinkPolicy.AmountsFor(doc.BalanceDue, expectedWht, 0m);
        return new TransferFigures(amounts.Net, amounts.Gross, amounts.Wht, buyerWithholds);
    }

    /// <summary>WHT ที่ส่งเข้า <c>CreatePaymentAsync</c> — ผู้ซื้อไม่มีหน้าที่หัก ⇒ 0 · โอนยอดค้างพอดี ⇒ WHT ที่เหลือทั้งก้อน · อื่น ๆ ⇒ null (ตัวบันทึกรับชำระแนะนำเอง)</summary>
    public static decimal? PaymentWht(TransferFigures f, decimal paidAmount, decimal balanceDue)
    {
        return f.BuyerWithholds == false ? 0m : DocumentPayLinkPolicy.WhtForPayment(paidAmount, balanceDue, f.WhtRemaining);
    }
}
