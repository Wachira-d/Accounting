using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>ข้อเท็จจริงเรื่องการชำระล่วงหน้าของเว็บหนึ่ง (อ่านครั้งเดียวต่อคำขอ) — ค่าตั้งเว็บ + ช่องทาง</summary>
public sealed record BookingPaymentSiteContext(
    BookingPaymentRuleInput SiteRule,
    bool OnlineEnabled,
    bool TransferEnabled,
    string? PaymentNote,
    BookingTransferInfo? Transfer,
    bool PromptPayValid,
    bool CompanyGatewayActive,
    string? CompanyGatewayName,
    Guid? TransferGatewayRowId)
{
    public bool GatewayReady => BookingPaymentChannels.GatewayReady(OnlineEnabled, CompanyGatewayActive);
    public bool TransferReady => BookingPaymentChannels.TransferReady(TransferEnabled, Transfer, PromptPayValid);
}

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177) — <b>ตัวอ่านเดียว</b>ของค่าตั้งการชำระล่วงหน้า (เว็บ <c>SiteBookingSettings</c> · บริการ <c>SiteBookingService</c> ·
/// ช่องโอน <c>SitePaymentGateway</c> แถวแรกที่ใช้งาน — ตัวเดียวกับ <c>GetStorefrontPaymentOptionsAsync</c>/<c>PromptPayQrPngAsync</c> ·
/// gateway ของบริษัท <c>PaymentProviderConfig</c>) — ผู้เรียก: <c>CmsBookingService</c> (สร้างการจอง/คำตอบ) · <c>BookingPaymentService</c> ·
/// <c>PublicPaymentResolver</c> · ทุก query กรอง <c>CompanyId</c>
/// </summary>
public static class BookingPaymentContextLoader
{
    public static BookingPaymentRuleInput SiteRule(SiteBookingSettings? row)
        => row == null ? BookingPaymentRuleInput.None
            : new BookingPaymentRuleInput(row.PaymentMode, row.PaymentDepositFixedAmount, row.PaymentDepositPercent,
                row.PaymentDepositMinAmount, row.PaymentDepositMaxAmount, row.PaymentDeadlineMinutes);

    public static BookingPaymentRuleInput ServiceRule(SiteBookingService s)
        => new(s.PaymentMode, s.DepositAmount, s.DepositPercent, s.DepositMinAmount, s.DepositMaxAmount, s.PaymentDeadlineMinutes);

    public static async Task<BookingPaymentSiteContext> LoadAsync(AccountingDbContext db, Guid companyId, Guid siteId,
        Func<string, bool> promptPayValid, CancellationToken ct = default)
    {
        var row = await db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted, ct);
        var gw = await db.SitePaymentGateways.AsNoTracking()
            // ฝ่ายค้านรอบ 218 (B1): เฉพาะแถวชนิดโอน/พร้อมเพย์ — แถว gateway อื่น (Omise ฯลฯ) ไม่ใช่ช่องโอน
            .Where(g => g.CompanyId == companyId && g.SiteId == siteId && g.IsActive && !g.IsDeleted
                && (g.GatewayType == PaymentGatewayType.BankTransfer || g.GatewayType == PaymentGatewayType.PromptPay))
            .OrderBy(g => g.SortOrder)
            .Select(g => new { g.Id, g.BankName, g.BankAccountNumber, g.BankAccountName, g.BankBranch, g.PromptPayId, g.PromptPayQrUrl })
            .FirstOrDefaultAsync(ct);
        var cfg = await db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .Select(c => new { c.DisplayName, c.ProviderCode })
            .FirstOrDefaultAsync(ct);
        var transfer = gw == null ? null
            : new BookingTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.BankBranch, gw.PromptPayId,
                BookingPaymentChannels.QrImageUrlAcceptable(gw.PromptPayQrUrl) ? gw.PromptPayQrUrl : null);
        var ppValid = !string.IsNullOrWhiteSpace(gw?.PromptPayId) && promptPayValid(gw!.PromptPayId!);
        // manual-slip ไม่ใช่ gateway ออนไลน์ (หน้า public-pay/methods ตัดออกเช่นกัน)
        var gatewayActive = cfg != null && cfg.ProviderCode != Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code;
        return new BookingPaymentSiteContext(SiteRule(row), row?.OnlineDepositEnabled ?? false, row?.BankTransferEnabled ?? true,
            row?.PaymentNote, transfer, ppValid, gatewayActive, gatewayActive ? cfg!.DisplayName : null, gw?.Id);
    }
}
