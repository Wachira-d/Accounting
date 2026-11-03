using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Models.DTOs.Cms;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก D · §1 ข้อ 9) — ตาราง <b>"มัดจำ & การยกเลิก ของทุกงาน"</b> ของเว็บ (อ่านอย่างเดียว · ที่เดียวที่เห็นครบ):
/// บริการจองแต่ละตัว (ผลที่มีจริงจาก <see cref="BookingPaymentPolicy"/> + ยกเลิกฟรีก่อน/ค่าธรรมเนียม) · คำสั่งซื้อสั่งทำ (มัดจำ/ริบเมื่อยกเลิก) · ที่พัก (มัดจำ/นโยบายยกเลิก) —
/// แต่ละแถวบอก "แก้ที่ไหน" · ไม่มีช่องแก้ซ้ำ (ห้ามสองฟอร์มแก้ค่าเดียวกัน)
/// </summary>
public partial class CmsBookingService
{
    public async Task<List<DepositCancelOverviewRow>> GetDepositCancelOverviewAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var rows = new List<DepositCancelOverviewRow>();
        var siteRow = await _db.SiteBookingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && s.IsActive && !s.IsDeleted)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        foreach (var s in services)
        {
            var eff = PaymentOf(s, siteRow);
            rows.Add(new DepositCancelOverviewRow
            {
                Work = "การจอง", Name = s.Name,
                Deposit = BookingPaymentPolicy.Describe(eff),
                Cancellation = s.FreeCancellationHours is int h
                    ? $"ยกเลิกฟรีก่อน {h} ชม. · หลังจากนั้นค่าธรรมเนียม {(s.CancellationFeePercent ?? 0m):0.##}% (ไม่เกินมัดจำ)"
                    : "ไม่ได้ตั้งเวลายกเลิกฟรี — ยกเลิกเมื่อไรก็ไม่มีค่าธรรมเนียม",
                Where = "หน้าเว็บ › บริการจอง › แก้บริการ (ลูกค้าต้องจ่ายก่อนไหม · ยกเลิกฟรีก่อน)",
                Warning = eff.Problem ?? BookingPayFirst.DepositConflict(s.BookingType, s.DepositAmount, s.DepositPercent),
            });
        }
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        if (cfg != null && await _db.SiteProducts.AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted))
        {
            var mode = PaymentTermMode.Normalize(cfg.MtoPaymentTermMode);
            rows.Add(new DepositCancelOverviewRow
            {
                Work = "คำสั่งซื้อ (สินค้าตามสั่ง)", Name = "ทุกสินค้าของเว็บ",
                Deposit = PaymentTermMode.Label(mode)
                    + (cfg.MtoDepositPercent is decimal p && p > 0m ? $" · {p:0.##}%" : "") + (cfg.MtoDepositFixedAmount is decimal f && f > 0m ? $" · ฿{f:N2}" : ""),
                Cancellation = cfg.DepositForfeitOnCancelPercent is decimal fp ? $"ยกเลิกแล้วริบมัดจำ {fp:0.##}%" : "ไม่ได้ตั้งการริบมัดจำ",
                Where = "ตั้งค่าเว็บ › สินค้าตามสั่ง / พรีออเดอร์",   // รอบ 230: ย้ายจาก modal หน้าคำสั่งซื้อ
            });
        }
        var props = await _db.LodgingProperties.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted)
            .Select(p => new { p.Name, p.DepositPercent, Policy = p.DefaultCancellationPolicy == null ? null : p.DefaultCancellationPolicy.Name })
            .ToListAsync();
        foreach (var p in props)
            rows.Add(new DepositCancelOverviewRow
            {
                Work = "ที่พัก", Name = p.Name, Deposit = $"มัดจำ {p.DepositPercent:0.##}% ของค่าห้อง",
                Cancellation = p.Policy != null ? $"นโยบายยกเลิก: {p.Policy}" : "ยังไม่ได้ตั้งนโยบายยกเลิกตั้งต้น",
                Where = "ตั้งค่าที่พัก › มัดจำ & การยืนยัน · นโยบายยกเลิก",
            });
        return rows;
    }

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก E · §4.2): แผง "สิ่งที่ยังไม่ได้ตั้งค่า" ของเว็บ — รวบรวมข้อเท็จจริงจากตัวตรวจเดิมแล้วให้
    /// <see cref="CmsSiteSetupChecklist.Evaluate"/> ตัดสิน (ไม่มีเงื่อนไขซ้ำที่นี่)</summary>
    public async Task<CmsSiteSetupStatusDto> GetSetupStatusAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var site = await _db.Sites.AsNoTracking().Where(x => x.Id == siteId && x.CompanyId == companyId)
            .Select(x => new { x.Status, x.SiteType, x.IndustryType, x.ContactPhone, x.ContactEmail, x.LineId, x.PrivacyPolicyUrl, x.PrivacyPolicyMarkdown })
            .FirstAsync();
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted).Select(s => new { s.IsActive, s.BookingType }).ToListAsync();
        var usesBookings = services.Count > 0 || site.SiteType == SiteType.Booking && site.IndustryType != IndustryType.Hotel;
        var productCount = await _db.SiteProducts.CountAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted && p.IsVisible);
        var usesOrders = productCount > 0 || site.SiteType == SiteType.Ecommerce;
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);

        var prop = await _db.LodgingProperties.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted)
            .Select(p => new { p.Id, p.IsActive, p.OnlineBookingEnabled, p.GuestConfirmMode, p.ConfirmWithoutDeposit }).FirstOrDefaultAsync();
        string? lodgingProblem = null, lodgingTransfer = null;
        var lodgingGuestPays = false;
        if (prop != null)
        {
            var units = await _db.LodgingUnits.CountAsync(u => u.CompanyId == companyId && u.IsActive && u.RoomType.PropertyId == prop.Id);
            var r = LodgingPublicReadiness.Evaluate(siteId, true, null, prop.IsActive, prop.OnlineBookingEnabled, units);
            lodgingProblem = r.IsLive ? null : r.Message;
            lodgingGuestPays = LodgingGuestConfirmPolicy.Resolve(prop.GuestConfirmMode, prop.ConfirmWithoutDeposit) != LodgingGuestConfirmMode.Instant;
            lodgingTransfer = SiteTransferAccount.LodgingWarning(true, lodgingGuestPays,
                (cfg?.EnableBankTransfer ?? true) && SiteTransferAccount.Payable(ctx.Transfer, ctx.PromptPayValid), ctx.CompanyGatewayActive);
        }
        var bookingCollects = services.Any(s => s.IsActive && s.BookingType == BookingType.PrePayment) || ctx.SiteRule.Mode is BookingPaymentMode.DepositRequired or BookingPaymentMode.DepositOptional or BookingPaymentMode.FullRequired;
        var facts = new CmsSiteSetupFacts(
            Published: site.Status == SiteStatus.Published,
            HasContact: !string.IsNullOrWhiteSpace(site.ContactPhone) || !string.IsNullOrWhiteSpace(site.ContactEmail) || !string.IsNullOrWhiteSpace(site.LineId),
            HasPrivacyPolicy: !string.IsNullOrWhiteSpace(site.PrivacyPolicyUrl) || !string.IsNullOrWhiteSpace(site.PrivacyPolicyMarkdown),
            UsesBookings: usesBookings, ActiveServiceCount: services.Count(s => s.IsActive),
            BookingPaymentProblem: usesBookings ? BookingPaymentPolicy.SiteSaveProblem(ctx.SiteRule, ctx.GatewayReady, ctx.TransferReady) : null,
            BookingChannelWarnings: usesBookings ? ctx.ChannelWarnings : new List<string>(),
            UsesOrders: usesOrders, ActiveProductCount: productCount,
            UsesLodging: prop != null, LodgingReadinessProblem: lodgingProblem, LodgingTransferWarning: null,
            NeedsTransferAccount: usesOrders && (cfg?.EnableBankTransfer ?? true) || usesBookings && bookingCollects || lodgingGuestPays,
            TransferPayable: SiteTransferAccount.Payable(ctx.Transfer, ctx.PromptPayValid), OnlineReady: ctx.CompanyGatewayActive,
            FreeDeliveryWarning: (await FreeDeliveryStateAsync(companyId, siteId)).Warning);
        _ = lodgingTransfer;   // ข้อ "ช่องทางรับเงิน" ครอบกรณีที่พักแล้ว (ไม่แสดงซ้ำสองข้อ)
        var items = CmsSiteSetupChecklist.Evaluate(facts);
        var (done, total) = CmsSiteSetupChecklist.Score(items);
        return new CmsSiteSetupStatusDto { Items = items, Done = done, Total = total, BeforePublishNote = CmsSiteSetupChecklist.BeforePublishNote(items) };
    }
}

