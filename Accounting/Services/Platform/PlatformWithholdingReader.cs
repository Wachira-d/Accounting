using Accounting.Data;
using Accounting.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Platform;

/// <summary>
/// รอบ 234 (คำตัดสิน 234-1…234-5) — ตัวอ่านข้อเท็จจริงจากฐานให้ตัวตัดสิน pure สองตัว (<see cref="PlatformPayerProfileGate"/> · <see cref="PlatformWhtSuggestion"/>) —
/// ทุกเส้นที่ผู้เช่าจ่ายเงินให้ NextAcc เรียกที่นี่ (ไม่อ่านบริษัท/ธง VAT แล้วคิดเอง): <c>SubscriptionService</c> (ซื้อ/อัปเกรด/ต่ออายุ/ชำระใบ/ใบเสนอ) ·
/// <c>PlatformBillingCenterService</c> (ยอดเสนอของใบ + การ์ดข้อมูลบริษัท) · <c>PlatformAutoChargeService</c> · <c>PublicCheckoutService</c>
/// </summary>
public static class PlatformWithholdingReader
{
    /// <summary>ข้อมูลผู้ซื้อ (บริษัทผู้เช่า) — ไม่พบบริษัท = ข้อมูลว่าง (ไม่ครบ)</summary>
    public static async Task<PlatformPayerProfileGate.Result> PayerAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var c = await db.Companies.AsNoTracking().Where(x => x.Id == companyId)
            .Select(x => new { x.Name, x.TaxId, x.BranchCode, x.Address, x.District, x.Province })
            .FirstOrDefaultAsync(ct);
        return PlatformPayerProfileGate.Evaluate(new PlatformPayerProfileGate.Facts(c?.Name, c?.TaxId, c?.BranchCode, c?.Address, c?.District, c?.Province));
    }

    /// <summary>ผู้ให้บริการออกใบกำกับภาษี (ราคารวม VAT) ไหม — ตัวตัดสินเดียวกับตัวออกใบ/preview (<see cref="PlanPurchasePreview.IssuesTaxInvoice"/>)</summary>
    public static async Task<bool> PlatformIssuesTaxInvoiceAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        var vs = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => new { s.PlatformIsVatRegistered, s.PlatformSellerTaxId }).FirstOrDefaultAsync(ct);
        return vs != null && PlanPurchasePreview.IssuesTaxInvoice(vs.PlatformIsVatRegistered, vs.PlatformSellerTaxId);
    }

    /// <summary>ยอดเต็มที่ต้องจ่าย (รวม VAT เมื่อออกใบกำกับ) ของรายการค่าแพ็กเกจ — ฐาน = ถอด VAT ด้วยตัวแยกเดียวกับใบที่ออก ·
    /// <paramref name="contractAnnualGross"/> = มูลค่าสัญญาต่อปี (รวม VAT · <see cref="PlatformWhtSuggestion.AnnualContractGross"/>) ⇒ ถอด VAT แล้วใช้ตัดสินเกณฑ์ ฿1,000 (234-2)</summary>
    public static async Task<PlatformWhtSuggestion.Result> ForReceivedAsync(AccountingDbContext db, Guid companyId, decimal grossDue, bool withhold,
        decimal? contractAnnualGross = null, CancellationToken ct = default)
    {
        var payer = await PayerAsync(db, companyId, ct);
        var vat = await PlatformIssuesTaxInvoiceAsync(db, ct);
        return PlatformWhtSuggestion.Decide(payer, grossDue, PlatformWhtSuggestion.BaseOfReceived(grossDue, vat), withhold,
            contractAnnualGross is decimal c ? PlatformWhtSuggestion.BaseOfReceived(c, vat) : null);
    }

    /// <summary>มูลค่าสัญญาค่าแพ็กเกจต่อปี<b>ก่อน VAT</b> ของ subscription ปัจจุบัน (ราคาต่อรอบที่ตกลง × รอบต่อปี) — ใช้กับใบแจ้งหนี้ต่ออายุ (สัญญาต่อเนื่อง) · null = ไม่รู้</summary>
    public static async Task<decimal?> SubscriptionContractBaseAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var s = await db.Subscriptions.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.PricePerCycle, x.BillingCycle }).FirstOrDefaultAsync(ct);
        if (s == null || PlatformWhtSuggestion.AnnualContractGross(s.PricePerCycle, PlatformPlanPricing.MonthsOf(s.BillingCycle)) is not decimal g) return null;
        return PlatformWhtSuggestion.BaseOfReceived(g, await PlatformIssuesTaxInvoiceAsync(db, ct));
    }

    /// <summary>ยอดค้างของใบแจ้งหนี้ — ฐาน = ยอดก่อน VAT ตามสัดส่วน (ตัวเดียวกับตัวตรวจ <c>PlatformInvoiceRules.WithholdingProblem</c>) ·
    /// <paramref name="contractAnnualBase"/> = มูลค่าสัญญาต่อปีก่อน VAT (เฉพาะใบต่ออายุ · null = ไม่รู้)</summary>
    public static PlatformWhtSuggestion.Result ForInvoice(PlatformPayerProfileGate.Result payer, decimal balanceDue, decimal totalAmount, decimal subTotal,
        bool withhold, decimal? contractAnnualBase = null)
    {
        return PlatformWhtSuggestion.Decide(payer, balanceDue, PlatformWhtSuggestion.BaseOfInvoice(balanceDue, totalAmount, subTotal), withhold, contractAnnualBase);
    }

    /// <summary>ผู้เช่าขอหักแต่ข้อมูลบริษัทไม่ครบ ⇒ ปฏิเสธพร้อมสิ่งที่ขาด (ไม่เงียบ · ทางไปต่อ = กรอกการ์ดข้อมูลบริษัท หรือไม่ติ๊กหัก)</summary>
    public static void ThrowIfProfileBlocks(PlatformWhtSuggestion.Result r, bool withhold)
    {
        if (withhold && r.ProfileProblem is string p)
            throw new BusinessRuleException(p, PlatformPayerProfileGate.RuleCode, 400);
    }
}
