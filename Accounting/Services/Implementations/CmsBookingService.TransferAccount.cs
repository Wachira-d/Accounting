using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก A) — <b>การ์ด "บัญชีรับโอน / พร้อมเพย์" ของทุกเว็บ</b> (ร้านค้า · การจอง · ที่พัก ใช้แถวเดียวกัน):
/// ตัวตรวจ <see cref="NormalizeTransferInput"/> + ตัวเขียน <see cref="WriteTransferRowAsync"/> ตัวเดียว ใช้ร่วมกับค่าตั้งการชำระของการจอง (<c>UpdatePaymentSettingsAsync</c>) ·
/// ล้างค่าที่มีอยู่ต้องยืนยัน · ถ้าการจองของเว็บเก็บเงินได้ทางโอนอย่างเดียว ⇒ ล้าง/ปิดแล้วการจองเก็บไม่ได้ ⇒ ปฏิเสธพร้อมเหตุ (ด่านเดียวกับหน้าการจอง)
/// </summary>
public partial class CmsBookingService
{
    /// <summary>ตรวจ + ทำให้เป็นรูปมาตรฐาน — ตัวจริงที่ <see cref="SiteTransferAccount.Normalize"/> (ใช้ร่วมทางเข้าเดิม payment-gateways · ฝ่ายค้าน S-4)</summary>
    private TransferAccountInput NormalizeTransferInput(string? bankName, string? accountNumber, string? accountName, string? branch,
        string? promptPayId, string? qrUrl, List<string> errs)
        => SiteTransferAccount.Normalize(bankName, accountNumber, accountName, branch, promptPayId, qrUrl, _promptPay.ValidatePromptPayId, errs);

    private static BookingTransferInfo? TransferInfoOf(SitePaymentGateway? gw) => SiteTransferAccount.InfoOf(gw);

    /// <summary>เขียนแถวบัญชีรับโอน — ตัวจริงที่ <see cref="SiteTransferAccount.WriteRowAsync"/> (ไม่บันทึก · ผู้เรียก SaveChanges)</summary>
    private Task<SitePaymentGateway?> WriteTransferRowAsync(Guid companyId, Guid siteId, TransferAccountInput t, bool confirmClear, string userId)
        => SiteTransferAccount.WriteRowAsync(_db, companyId, siteId, t, confirmClear, userId);

    public async Task<SiteTransferAccountDto> GetTransferAccountAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var t = ctx.Transfer;
        var raw = ctx.RawQrUrl;   // ค่าที่เก็บจริง (ตัวอ่านกรองลิงก์ที่ใช้ไม่ได้ออก — หน้าตั้งค่าต้องเห็นค่าจริง)
        var usedBy = new List<string>();
        if (await _db.SiteBookingServices.AnyAsync(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted)) usedBy.Add("การจอง");
        if (await _db.SiteProducts.AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted)) usedBy.Add("คำสั่งซื้อ");
        if (await _db.LodgingProperties.AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted)) usedBy.Add("ที่พัก");
        return new SiteTransferAccountDto
        {
            BankName = t?.BankName, BankAccountNumber = t?.AccountNumber, BankAccountName = t?.AccountName, BankBranch = t?.Branch,
            PromptPayId = t?.PromptPayId, PromptPayQrUrl = raw ?? t?.QrImageUrl,
            BankNameOptions = SiteTransferAccount.BankNameOptions(t?.BankName),
            Payable = SiteTransferAccount.Payable(t, ctx.PromptPayValid),
            UsedBy = usedBy,
        };
    }

    public async Task<SiteTransferAccountDto> UpdateTransferAccountAsync(Guid companyId, Guid siteId, SiteTransferAccountDto request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var errs = new List<string>();
        var t = NormalizeTransferInput(request.BankName, request.BankAccountNumber, request.BankAccountName, request.BankBranch,
            request.PromptPayId, request.PromptPayQrUrl, errs);
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), SiteTransferAccount.RuleCode);
        var before = await LoadPaymentContextAsync(companyId, siteId);
        // รีวิวรอบ 228: ธุรกรรมเดียว (ล็อกต่อเว็บใน WriteRowAsync ถือจนบันทึกเสร็จ)
        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        var gw = await WriteTransferRowAsync(companyId, siteId, t, request.ConfirmClear, userId);
        // การจองของเว็บที่เก็บเงินล่วงหน้า — ล้างบัญชีแล้วเก็บไม่ได้ ⇒ ปฏิเสธ (ด่านเดียวกับหน้าการชำระของการจอง)
        var transferReady = BookingPaymentChannels.TransferReady(before.TransferEnabled, TransferInfoOf(gw), t.PromptPayId != null);
        if (BookingPaymentPolicy.SiteSaveProblem(before.SiteRule, before.GatewayReady, transferReady) is string problem)
            throw new BusinessRuleException(problem, BookingPaymentChannels.RuleCode, 409);
        var ownCollecting = await _db.SiteBookingServices.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId && x.IsActive && !x.IsDeleted && x.PaymentMode != null && x.PaymentMode != BookingPaymentMode.Off)
            .ToListAsync();
        var broken = ownCollecting.Where(x => BookingPaymentPolicy.ServiceChannelProblem(
                BookingPaymentPolicy.Resolve(x.BookingType, before.SiteRule, BookingPaymentContextLoader.ServiceRule(x)), before.GatewayReady, transferReady) != null)
            .Select(x => x.Name).ToList();
        if (BookingPaymentPolicy.SiteChannelsBreakServicesProblem(broken) is string svcProblem)
            throw new BusinessRuleException(svcProblem, BookingPaymentChannels.RuleCode, 409);
        // ฝ่ายค้านรอบ 228 S-7: งานอื่นของเว็บ (คำสั่งซื้อที่รับโอน · ที่พักที่แขกต้องโอน) ก็ใช้บัญชีนี้ — ล้างจนรับเงินไม่ได้ ⇒ 409 พร้อมเหตุ
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        var ordersTakeTransfer = (cfg?.EnableBankTransfer ?? true) && await _db.SiteProducts.AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted);
        var lodging = await _db.LodgingProperties.AsNoTracking().Where(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted && p.IsActive)
            .Select(p => new { p.GuestConfirmMode, p.ConfirmWithoutDeposit }).ToListAsync();
        var lodgingGuestPays = lodging.Any(p => LodgingGuestConfirmPolicy.Resolve(p.GuestConfirmMode, p.ConfirmWithoutDeposit) != LodgingGuestConfirmMode.Instant);
        var payableAfter = SiteTransferAccount.Payable(TransferInfoOf(gw), t.PromptPayId != null);
        var hadPayable = SiteTransferAccount.Payable(before.Transfer, before.PromptPayValid);
        if (hadPayable && SiteTransferAccount.ClearBreaksOtherWorkProblem(payableAfter, before.CompanyGatewayActive, ordersTakeTransfer, lodgingGuestPays) is string otherProblem)
            throw new BusinessRuleException(otherProblem, SiteTransferAccount.RuleCode, 409);
        // ฝ่ายค้านรอบ 228 S-3: ร่องรอยการเปลี่ยนบัญชีรับเงิน (ปิดเลขบัญชี/พร้อมเพย์ — PII ไม่ลงเต็ม) · ลงในธุรกรรมเดียวกับการบันทึก
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = Guid.TryParse(userId, out var uid) ? uid : null,
            EntityType = "SiteTransferAccount", EntityId = siteId.ToString(), Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(SiteTransferAccount.MaskForAudit(before.Transfer)),
            NewValues = System.Text.Json.JsonSerializer.Serialize(SiteTransferAccount.MaskForAudit(TransferInfoOf(gw))),
        });
        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();
        _logger.LogInformation("บันทึกบัญชีรับโอนของเว็บ {SiteId} โดย {User}", siteId, userId);
        return await GetTransferAccountAsync(companyId, siteId);
    }
}
