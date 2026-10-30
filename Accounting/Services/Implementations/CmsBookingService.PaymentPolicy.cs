using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177 · <c>erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md</c>) — <b>นโยบายรับชำระล่วงหน้า</b>ของการจองนัดหมาย:
/// ค่าตั้งเว็บ (+ช่องทางโอนในแถว <c>SitePaymentGateway</c>) · ตัวอย่างก่อนกดจอง · ข้อมูลการชำระบนคำตอบ (ยอด/สถานะ/ช่องทาง/สลิป) ·
/// ตัวตัดสินทั้งหมดอยู่ใน <c>Helpers/BookingPaymentPolicy</c> + <c>Helpers/BookingPaymentChannels</c> — ไฟล์นี้ต่อสาย (ไฟล์แยกเพื่อลดการชนกับทีมอื่น)
/// · สลิป (แนบ/ยืนยัน/ปฏิเสธ) อยู่ที่ <c>BookingSlipService</c> (ต้องใช้ <c>IPaymentIntentService</c> — อยู่ที่นี่จะเป็นวงกลม DI ผ่าน handler)
/// </summary>
public partial class CmsBookingService
{
    /// <summary>นโยบายที่มีผลของบริการ (ค่าเว็บจากแถว <paramref name="siteRow"/>) — ตัวประกอบเดียวในไฟล์บริการ</summary>
    private static BookingPaymentEffective PaymentOf(SiteBookingService s, SiteBookingSettings? siteRow)
        => BookingPaymentPolicy.Resolve(s.BookingType, BookingPaymentContextLoader.SiteRule(siteRow), BookingPaymentContextLoader.ServiceRule(s));

    private Task<BookingPaymentSiteContext> LoadPaymentContextAsync(Guid companyId, Guid siteId)
        => BookingPaymentContextLoader.LoadAsync(_db, companyId, siteId, _promptPay.ValidatePromptPayId);

    /// <summary>ฝ่ายค้านรอบ 218: บริการตั้งโหมดเก็บเงินเอง ⇒ ต้องมีช่องทาง + ยอด (ไม่บันทึกค่าที่ไม่มีผล) — เรียกจากสร้าง/แก้บริการ</summary>
    private async Task EnsureServicePaymentAsync(Guid companyId, Guid siteId, SiteBookingService svc)
    {
        if (svc.PaymentMode is not BookingPaymentMode m || m == BookingPaymentMode.Off) return;
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var eff = BookingPaymentPolicy.Resolve(svc.BookingType, ctx.SiteRule, BookingPaymentContextLoader.ServiceRule(svc));
        if (BookingPaymentPolicy.ServiceChannelProblem(eff, ctx.GatewayReady, ctx.TransferReady) is string problem)
            throw new BusinessRuleException(problem, BookingPaymentChannels.RuleCode);
    }

    // ═══════════════ ค่าตั้งเว็บ ═══════════════

    public async Task<BookingPaymentSettingsDto> GetPaymentSettingsAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var r = ctx.SiteRule;
        var dto = new BookingPaymentSettingsDto
        {
            Mode = r.Mode?.ToString() ?? "",
            DepositFixedAmount = r.FixedAmount, DepositPercent = r.Percent, DepositMinAmount = r.MinAmount, DepositMaxAmount = r.MaxAmount,
            DeadlineMinutes = r.DeadlineMinutes,
            OnlineGatewayEnabled = ctx.OnlineEnabled, BankTransferEnabled = ctx.TransferEnabled, PaymentNote = ctx.PaymentNote,
            BankName = ctx.Transfer?.BankName, BankAccountNumber = ctx.Transfer?.AccountNumber, BankAccountName = ctx.Transfer?.AccountName,
            BankBranch = ctx.Transfer?.Branch, PromptPayId = ctx.Transfer?.PromptPayId, PromptPayQrUrl = ctx.Transfer?.QrImageUrl,
            ModeOptions = BookingPaymentPolicy.ModeOptions.ToList(),
            CompanyGatewayConfigured = ctx.CompanyGatewayActive, CompanyGatewayName = ctx.CompanyGatewayName,
            GatewayReady = ctx.GatewayReady, TransferReady = ctx.TransferReady, PromptPayIdValid = ctx.PromptPayValid,
            Problem = BookingPaymentPolicy.SiteSaveProblem(r, ctx.GatewayReady, ctx.TransferReady),
        };
        var siteRow = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        dto.Services = services.Select(s =>
        {
            var eff = PaymentOf(s, siteRow);
            return new BookingPaymentServiceEffectDto
            {
                Id = s.Id, Name = s.Name, BookingType = s.BookingType.ToString(), Summary = BookingPaymentPolicy.Describe(eff),
                Problem = eff.Problem, Collects = eff.Collects, UsesSiteSetting = s.PaymentMode == null,
            };
        }).ToList();
        return dto;
    }

    /// <summary>บันทึกค่าตั้งเว็บ — ตรวจด้วยตัวตัดสินเดียว · โหมดเก็บเงินที่ไม่มีช่องทาง ⇒ ปฏิเสธ (ไม่ silent) · ช่องทางโอนเขียนแถว <c>SitePaymentGateway</c>
    /// แถวแรกที่ใช้งาน (ไม่มี ⇒ สร้าง) เฉพาะเมื่อ <c>UpdateTransfer</c></summary>
    public async Task<BookingPaymentSettingsDto> UpdatePaymentSettingsAsync(Guid companyId, Guid siteId, BookingPaymentSettingsDto request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var (mode, modeErr) = BookingPaymentPolicy.ParseMode(request.Mode);
        var errs = new List<string>();
        if (modeErr != null) errs.Add(modeErr);
        var rule = new BookingPaymentRuleInput(mode, request.DepositFixedAmount, request.DepositPercent, request.DepositMinAmount,
            request.DepositMaxAmount, request.DeadlineMinutes);
        errs.AddRange(BookingPaymentPolicy.ValidateRule(rule));
        if (request.PaymentNote?.Length > 1000) errs.Add("ข้อความถึงลูกค้ายาวเกิน 1,000 ตัวอักษร");

        string? acct = null, ppId = null, qr = null;
        if (request.UpdateTransfer)
        {
            errs.AddRange(BookingPaymentChannels.ValidateBankAccount(request.BankName, request.BankAccountNumber, request.BankAccountName, request.BankBranch));
            acct = BookingPaymentChannels.NormalizeAccountNumber(request.BankAccountNumber);
            ppId = string.IsNullOrWhiteSpace(request.PromptPayId) ? null : new string(request.PromptPayId.Where(char.IsDigit).ToArray());
            if (ppId != null && !_promptPay.ValidatePromptPayId(ppId))
                errs.Add("พร้อมเพย์ต้องเป็นเบอร์มือถือ 10 หลัก (ขึ้นต้น 0) หรือเลขประจำตัวผู้เสียภาษี/บัตรประชาชน 13 หลักที่ถูกต้อง");
            qr = string.IsNullOrWhiteSpace(request.PromptPayQrUrl) ? null : request.PromptPayQrUrl.Trim();
            if (!BookingPaymentChannels.QrImageUrlAcceptable(qr)) errs.Add("รูป QR ต้องอัปโหลดผ่านคลังสื่อของเว็บ หรือเป็นลิงก์ https");
        }
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), BookingPaymentPolicy.RuleCode);

        var row = await _db.SiteBookingSettings.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        if (row == null)
        {
            row = new SiteBookingSettings { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
            _db.SiteBookingSettings.Add(row);
        }
        SitePaymentGateway? gw = null;
        if (request.UpdateTransfer)
        {
            // ฝ่ายค้านรอบ 218 (B1): อ่าน/เขียนเฉพาะแถวชนิดโอน/พร้อมเพย์ (แถว Omise/2C2P/Stripe — webhook ของคำสั่งซื้อหาด้วยชนิด — ไม่ถูกแตะ) ·
            // ไม่เคยเปลี่ยนชนิด/ชื่อของแถวเดิม · ไม่มีแถวชนิดโอน ⇒ สร้างใหม่ · ช่องที่มีค่าแล้วส่งมาว่าง ⇒ ต้องยืนยันการล้าง (ใช้ร่วมกับคำสั่งซื้อ)
            gw = await _db.SitePaymentGateways
                .Where(g => g.CompanyId == companyId && g.SiteId == siteId && g.IsActive && !g.IsDeleted
                    && (g.GatewayType == PaymentGatewayType.BankTransfer || g.GatewayType == PaymentGatewayType.PromptPay))
                .OrderBy(g => g.SortOrder).FirstOrDefaultAsync();
            var beforeInfo = gw == null ? null
                : new BookingTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.BankBranch, gw.PromptPayId, gw.PromptPayQrUrl);
            var cleared = BookingPaymentChannels.ClearedFields(beforeInfo, acct == null ? null : request.BankName, acct,
                acct == null ? null : request.BankAccountName, acct == null ? null : request.BankBranch, ppId, qr);
            if (cleared.Count > 0 && !request.ConfirmClearTransfer)
                throw new BusinessRuleException(BookingPaymentChannels.ClearConfirmMessage(cleared), BookingPaymentChannels.ClearConfirmRuleCode, 409);
            if (gw == null && (acct != null || ppId != null || qr != null))
            {
                gw = new SitePaymentGateway
                {
                    CompanyId = companyId, SiteId = siteId, CreatedBy = userId, IsTestMode = false,
                    GatewayType = ppId != null ? PaymentGatewayType.PromptPay : PaymentGatewayType.BankTransfer,
                    Name = ppId != null ? "PromptPay" : (string.IsNullOrWhiteSpace(request.BankName) ? "โอนเงิน" : request.BankName.Trim()),
                };
                _db.SitePaymentGateways.Add(gw);
            }
            if (gw != null)
            {
                gw.BankName = acct == null ? null : request.BankName?.Trim();
                gw.BankAccountNumber = acct;
                gw.BankAccountName = acct == null ? null : request.BankAccountName?.Trim();
                gw.BankBranch = acct == null || string.IsNullOrWhiteSpace(request.BankBranch) ? null : request.BankBranch.Trim();
                gw.PromptPayId = ppId;
                gw.PromptPayQrUrl = qr;
                gw.UpdatedBy = userId;
            }
        }

        // ด่านช่องทางคิดจากค่าที่ "จะเป็น" หลังบันทึก (ไม่ใช่ค่าเดิม) — gateway ของบริษัทอ่านสด
        var before = await LoadPaymentContextAsync(companyId, siteId);
        var transferAfter = request.UpdateTransfer
            ? (gw == null ? null : new BookingTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.BankBranch, gw.PromptPayId, gw.PromptPayQrUrl))
            : before.Transfer;
        var ppValidAfter = request.UpdateTransfer ? ppId != null : before.PromptPayValid;
        var gatewayReady = BookingPaymentChannels.GatewayReady(request.OnlineGatewayEnabled, before.CompanyGatewayActive);
        var transferReady = BookingPaymentChannels.TransferReady(request.BankTransferEnabled, transferAfter, ppValidAfter);
        if (BookingPaymentPolicy.SiteSaveProblem(rule, gatewayReady, transferReady) is string problem)
            throw new BusinessRuleException(problem, BookingPaymentChannels.RuleCode);
        if (request.OnlineGatewayEnabled && !before.CompanyGatewayActive)
            throw new BusinessRuleException("บริษัทยังไม่ได้ตั้งค่าระบบรับชำระเงินออนไลน์ — ตั้งที่ “ตั้งค่า → รับชำระเงินออนไลน์” ก่อน หรือปิดตัวเลือกชำระออนไลน์",
                BookingPaymentChannels.RuleCode);

        row.PaymentMode = mode;
        row.PaymentDepositFixedAmount = request.DepositFixedAmount; row.PaymentDepositPercent = request.DepositPercent;
        row.PaymentDepositMinAmount = request.DepositMinAmount; row.PaymentDepositMaxAmount = request.DepositMaxAmount;
        row.PaymentDeadlineMinutes = request.DeadlineMinutes is > 0 ? request.DeadlineMinutes : null;
        row.OnlineDepositEnabled = request.OnlineGatewayEnabled;
        row.BankTransferEnabled = request.BankTransferEnabled;
        row.PaymentNote = string.IsNullOrWhiteSpace(request.PaymentNote) ? null : request.PaymentNote.Trim();
        row.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return await GetPaymentSettingsAsync(companyId, siteId);
    }

    // ═══════════════ ก่อนกดจอง ═══════════════

    public async Task<BookingPaymentPreviewResponse?> PreviewPaymentAsync(Guid companyId, Guid siteId, BookingPaymentPreviewRequest request)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted);
        if (svc == null) return null;
        var site = await SiteContextAsync(companyId, siteId);
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var rules = BookingSlotEngine.RulesOf(svc, site.Defaults);
        var total = BookingSlotEngine.TotalPrice(rules, svc.Price, Math.Clamp(request.Units, 1, Math.Max(1, svc.MaxUnitsPerBooking)));
        var eff = PaymentOf(svc, site.Row);
        var amount = BookingPaymentPolicy.AmountDue(eff, total, customerOptsIn: true);
        var hasHold = eff.Required && !eff.IsLegacy && eff.DeadlineMinutes > 0;
        return new BookingPaymentPreviewResponse
        {
            Mode = eff.Mode.ToString(), ModeLabel = BookingPaymentPolicy.ModeLabel(eff.Mode),
            Collects = eff.Collects && amount > 0m, Required = eff.Required && amount > 0m,
            Optional = eff.Mode == BookingPaymentMode.DepositOptional && amount > 0m,
            TotalAmount = total, AmountDue = amount, Currency = svc.Currency,
            DeadlineMinutes = hasHold ? eff.DeadlineMinutes : 0,
            Summary = BookingPaymentPolicy.CustomerSummary(eff, amount, hasHold),
            GatewayAvailable = ctx.GatewayReady, TransferAvailable = ctx.TransferReady,
            // นโยบายยกเลิก/คืนเงิน — ข้อความเดียวกับการจองจริง (CreditBookingLink.CancellationPolicy · ริบ ≤ มัดจำ ข้อ 126)
            CancellationPolicy = CreditBookingLink.CancellationPolicy(null, null, svc.FreeCancellationHours, svc.CancellationFeePercent).Text,
            PaymentNote = ctx.PaymentNote,
        };
    }

    // ═══════════════ ข้อมูลการชำระบนคำตอบ ═══════════════

    /// <summary>เติม <see cref="BookingResponse.Payment"/> — เรียกจาก <c>FillCustomerFacingAsync</c> (ทุกคำตอบของการจอง) · คืนสลิปรอตรวจไหม
    /// (ผู้เรียกใช้ปิดช่องจ่ายออนไลน์ชั่วคราว)</summary>
    private async Task<bool> FillPaymentInfoAsync(Guid companyId, Guid siteId, BookingResponse resp, decimal due)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == resp.Id && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.PaymentHoldUntil, x.SlipAttachmentId, x.SlipUploadedAt, x.SlipIntentId, x.SlipRejectedReason, Service = x.BookingService })
            .FirstOrDefaultAsync();
        if (b == null) return false;
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var siteRow = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var eff = PaymentOf(b.Service, siteRow);
        var slipPending = b.SlipUploadedAt != null && b.SlipAttachmentId != null;
        var open = resp.Status is BookingStatus.Pending or BookingStatus.Confirmed;
        var hold = open ? b.PaymentHoldUntil : null;
        resp.Payment = new BookingPaymentInfoDto
        {
            Mode = eff.IsLegacy ? null : eff.Mode.ToString(),
            ModeLabel = BookingPaymentPolicy.ModeLabel(eff.Mode),
            AmountDue = due,
            StatusLabel = BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(resp.Status, resp.DepositAmount, resp.PaidAmount, slipPending, hold, b.SlipRejectedReason)),
            HoldUntil = hold,
            SlipPending = slipPending, SlipUploadedAt = b.SlipUploadedAt, SlipRejectedReason = b.SlipRejectedReason,
            CanUploadSlip = open && due > 0.005m && ctx.TransferReady && !slipPending && resp.PublicToken != null,
            GatewayAvailable = open && due > 0.005m && ctx.GatewayReady && !slipPending,
            TransferAvailable = open && due > 0.005m && ctx.TransferReady,
            PromptPayQrAvailable = open && due > 0.005m && ctx.TransferReady && ctx.PromptPayValid,
            Transfer = open && due > 0.005m && ctx.TransferReady && ctx.Transfer is BookingTransferInfo t
                ? new BookingTransferInfoDto
                {
                    BankName = t.AccountNumber == null ? null : t.BankName, AccountNumber = t.AccountNumber,
                    AccountName = t.AccountNumber == null ? null : t.AccountName, Branch = t.AccountNumber == null ? null : t.Branch,
                    PromptPayId = ctx.PromptPayValid ? t.PromptPayId : null, QrImageUrl = t.QrImageUrl,
                }
                : null,
            Summary = due > 0.005m ? BookingPaymentPolicy.CustomerSummary(eff, due, hold != null) : null,
            PaymentNote = ctx.PaymentNote,
            SlipIntentId = slipPending ? b.SlipIntentId : null,
            SlipFileUrl = slipPending && b.SlipAttachmentId is Guid aid ? CmsMediaUrl.SlipDownloadUrl(companyId, aid) : null,
        };
        return slipPending;
    }

    /// <summary>ป้ายการชำระของรายการการจอง (หน้า cms-bookings) — ตัวตัดสินเดียวกับคำตอบเต็ม</summary>
    private async Task FillListPaymentLabelsAsync(Guid companyId, IReadOnlyList<BookingListResponse> items)
    {
        if (items.Count == 0) return;
        var ids = items.Select(i => i.Id).ToList();
        var rows = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && ids.Contains(b.Id))
            .Select(b => new { b.Id, b.Status, b.DepositAmount, b.PaidAmount, b.PaymentHoldUntil, b.SlipUploadedAt, b.SlipAttachmentId, b.SlipRejectedReason, b.BookingService.BookingType,
                b.BookingDate, b.StartTime })
            .ToListAsync();
        foreach (var it in items)
        {
            var r = rows.FirstOrDefault(x => x.Id == it.Id);
            if (r == null || r.BookingType != BookingType.PrePayment) continue;
            it.PaymentStatusLabel = BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(r.Status, r.DepositAmount, r.PaidAmount,
                r.SlipUploadedAt != null && r.SlipAttachmentId != null, r.PaymentHoldUntil, r.SlipRejectedReason,
                // ฝ่ายค้านรอบ 218: สลิปค้างตรวจจนเลยเวลานัด ⇒ ป้าย "ตรวจด่วน" (ไม่ยกเลิกเอง — ลูกค้าโอนแล้ว)
                PastStart: CreditBookingLink.StartUtc(r.BookingDate, r.StartTime) <= DateTime.UtcNow));
        }
    }
}
