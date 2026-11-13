using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Services.Implementations;

public sealed record PartnerPaymentPlanDto(Guid Id, string Kind, string KindLabel, decimal Amount, string MethodLabel, string AccountLabel, string? ChosenBy,
    DateTime CreatedAt, string StateLabel, string? SlipCopyNote, Guid? BuyerPaymentId);

/// <param name="Problem">เหตุที่ใช้ทั้งสองปุ่มไม่ได้ (null = ใช้ได้) — ข้อความไทยจากเซิร์ฟเวอร์</param>
/// <param name="GatewayProblem">เหตุที่ปุ่ม "จ่ายจากระบบของเรา" ใช้ไม่ได้ (ความยินยอม · ผู้ขายไม่เปิดช่องทางออนไลน์) — ปุ่มแจ้งโอนยังใช้ได้</param>
public sealed record PartnerOutboundOptionsDto(bool Eligible, string? Problem, string? SellerName, string? SourceDocumentNumber, decimal BalanceDue,
    List<PartnerPaymentOptionDto> Accounts, bool GatewayOffered, string? GatewayProblem, decimal? GatewayAmount, List<PartnerPaymentOptionDto> GatewayMethods,
    string GatewayCaveat, List<PartnerPaymentOptionDto> TransferMethods, decimal? TransferAmount, decimal? TransferWht, string? TransferProblem,
    List<PartnerPaymentPlanDto> Plans);

public sealed record PartnerGatewayStartResult(Guid PlanId, Guid IntentId, string Status, decimal Amount, string? QrPayload, DateTime? QrExpiresAt, string? AuthorizeUrl, string Message);

public sealed record PartnerTransferResult(Guid PlanId, Guid PaymentId, string AccountLabel, string Origin, bool SlipCopySent, string SlipCopyNote, string? Warning, string Message);

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11 · 264-5-12) — <b>ผู้ซื้อ (B) เป็นผู้เริ่มจ่าย</b> ใบซื้อจากคู่ค้า NextAcc จากในระบบของ B (ล็อกอิน · สิทธิ์บันทึกจ่ายใบซื้อ —
/// ด่านที่ controller) · ไม่มีตัวเลือกบัญชีของ B บนหน้าชำระสาธารณะของ A
/// <list type="bullet">
/// <item><b>จ่ายจากระบบของเรา</b> (<see cref="StartGatewayAsync"/>): บัญชีของเรา + วิธี ⇒ แถว <see cref="PartnerPaymentPlan"/> ⇒ เปิดรายการชำระของ<b>เอกสารของผู้ขาย</b> ผ่าน
/// <see cref="Accounting.Services.Payments.IPaymentIntentService.StartAsync"/> ตัวเดิม บนเป้าหมายจากตัวตัดสินหน้าชำระตัวเดียว (<see cref="IDocumentPayLinkService.PartnerPayTargetAsync"/>) ·
/// <b>ไม่ประทับ "ชำระแล้ว"</b> — ผู้ขายรับเงินเมื่อผู้ให้บริการยืนยัน (webhook) ⇒ การแจ้งตามมา ⇒ ชั้น PayerDeclared ของตัวตัดสินแหล่งเงิน ·
/// ความยินยอม <c>AutoRecordPayment</c> ตรวจเพื่อเขียนในธุรกรรมล็อกที่ถือไว้จนเปิดรายการเสร็จ</item>
/// <item><b>แจ้งโอนให้ผู้ขาย</b> (<see cref="NotifyTransferAsync"/>): คนแนบสลิปของตัวเอง + เลือกบัญชี (สลิปตรวจไขว้) ⇒ PV ของเราทันทีจากหลักฐานของเรา (การกระทำของคน) ⇒
/// สำเนาสลิปเข้าคิวรอยืนยันของผู้ขาย (<see cref="SlipEvidenceService.SubmitAsync"/> ช่อง <see cref="SlipEvidenceService.ChannelPartnerNextAcc"/> — ยังไม่ใช่เงินเข้า) ภายใต้ความยินยอม
/// <c>ReceiveSlipCopy</c> ของ<b>ผู้ขาย</b> (CheckForWriteAsync ในธุรกรรมล็อก) · ผู้ขายยืนยันทีหลัง ⇒ การแจ้งพบ PV ของแผนเดียวกัน ⇒ ปิด ไม่สร้าง PV ที่สอง</item>
/// <item>ไม่มีจุดเรียก AI (บัญชี/ยอดเชิงกำหนด · อ่านสลิปในเครื่อง) ⇒ ไม่มี AiFeatureKey</item>
/// </list>
/// </summary>
public class PartnerOutboundPaymentService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PartnerOutboundPaymentService> _logger;
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly Accounting.Services.Payments.IPaymentIntentService? _intents;
    private readonly SlipEvidenceService? _slips;
    private readonly ISlipImageReader? _reader;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    /// <summary>ตัวตรวจความยินยอมของคำขอนี้ — <b>อ่านอย่างเดียว</b> สำหรับ <see cref="OptionsAsync"/> (เส้นเขียนใช้ตัวของ scope ล็อก + CheckForWriteAsync)</summary>
    private readonly PartnerConsentService? _readConsent;

    /// <summary>วิธีชำระของปุ่ม "จ่ายจากระบบของเรา" — บัตรต้องโทเคนจากหน้าผู้ให้บริการ (ไม่มีในหน้านี้) · สลิปใช้ปุ่มแจ้งโอน</summary>
    public static readonly IReadOnlyList<PaymentMethodKind> GatewayKinds = new[] { PaymentMethodKind.PromptPay, PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking, PaymentMethodKind.TrueMoney };

    public PartnerOutboundPaymentService(AccountingDbContext db, IDocumentService docs, IServiceScopeFactory scopes, ILogger<PartnerOutboundPaymentService> logger,
        IDocumentPayLinkService? payLinks = null, Accounting.Services.Payments.IPaymentIntentService? intents = null, SlipEvidenceService? slips = null,
        ISlipImageReader? reader = null, Accounting.Services.Storage.IFileStorage? storage = null, PartnerConsentService? readConsent = null)
    {
        _db = db; _docs = docs; _scopes = scopes; _logger = logger; _payLinks = payLinks; _intents = intents; _slips = slips; _reader = reader; _storage = storage;
        _readConsent = readConsent;
    }

    private static readonly DocumentStatus[] PayableStatuses = { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };

    private sealed record Ctx(Document Purchase, Guid LinkId, Guid PartnershipId, Guid SourceCompanyId, Guid SourceDocumentId, string? SourceNumber, string? SellerName,
        bool Accepted, string? Problem, PartnerConsent.CapAmount? SourceCap = null);

    /// <summary>ใบซื้อของบริษัทผู้เรียก + ลิงก์ InvoiceFlow ที่ใบปลายทางคือใบนี้ + คู่ Accepted (ทุก query ผูก CompanyId) · ไม่พบใบ ⇒ KeyNotFound</summary>
    private async Task<Ctx> LoadAsync(Guid companyId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == purchaseId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบใบซื้อ");
        if (!DocumentPermissionHelper.IsPurchase(purchase.DocumentType)) throw new KeyNotFoundException("ไม่พบใบซื้อ");
        var link = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetCompanyId == companyId && l.TargetDocumentId == purchaseId && l.LinkType == CrossTenantLinkType.InvoiceFlow && !l.IsDeleted)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.Id, l.TradingPartnershipId, l.SourceCompanyId, l.SourceDocumentId }).FirstOrDefaultAsync(ct);
        if (link == null) return new Ctx(purchase, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, null, null, false, PartnerPaymentPlanRule.NoLinkReason);
        var accepted = await _db.TradingPartnerships.AsNoTracking()
            .AnyAsync(p => p.Id == link.TradingPartnershipId && p.Status == TradingPartnershipStatus.Accepted
                && (p.CompanyAId == companyId || p.CompanyBId == companyId) && (p.CompanyAId == link.SourceCompanyId || p.CompanyBId == link.SourceCompanyId), ct);
        var src = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == link.SourceDocumentId && d.CompanyId == link.SourceCompanyId).Select(d => new { d.DocumentNumber, d.TotalAmount, d.Currency }).FirstOrDefaultAsync(ct);
        var seller = await _db.Companies.AsNoTracking().Where(c => c.Id == link.SourceCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var problem = PartnerPaymentPlanRule.StartProblem(true, accepted, PayableStatuses.Contains(purchase.Status), purchase.BalanceDue);
        // เพดานความยินยอมของผู้ขาย (ถ้าตั้ง) เทียบยอดรวมเอกสารของผู้ขายตามสกุลของใบ (แบบ CapOf ของท่อ)
        var cap = src == null ? null : new PartnerConsent.CapAmount(src.TotalAmount, string.IsNullOrWhiteSpace(src.Currency) ? "THB" : src.Currency.Trim());
        return new Ctx(purchase, link.Id, link.TradingPartnershipId, link.SourceCompanyId, link.SourceDocumentId, src?.DocumentNumber, seller, accepted, problem, cap);
    }

    /// <summary>ตัวเลือกของสองปุ่มบนใบซื้อ (อ่านอย่างเดียว · server computes ป้าย/ยอด/เหตุ) — ความยินยอมอ่านด้วยตัวตรวจอ่านอย่างเดียว (เขียนจริงตรวจใหม่ในธุรกรรม)</summary>
    public async Task<PartnerOutboundOptionsDto> OptionsAsync(Guid companyId, Guid purchaseId, CancellationToken ct = default)
    {
        var c = await LoadAsync(companyId, purchaseId, ct);
        var usable = await PartnerPayablePipeline.UsablePayAccountsAsync(_db, companyId, ct);
        var accounts = usable.Select(u => new PartnerPaymentOptionDto(u.Key.ToString(), u.Value)).OrderBy(o => o.Label).ToList();
        var transferMethods = PartnerPaymentNoticeRule.Methods.Where(m => m.Method != PaymentMethod.Cash)
            .Select(m => new PartnerPaymentOptionDto(m.Method.ToString(), m.Label)).ToList();
        var plans = await PlansAsync(companyId, purchaseId, usable, ct);
        if (c.Problem != null)
            return new PartnerOutboundOptionsDto(false, c.Problem, c.SellerName, c.SourceNumber, c.Purchase.BalanceDue, accounts, false, c.Problem, null,
                new List<PartnerPaymentOptionDto>(), PartnerPaymentPlanRule.GatewayCaveat, transferMethods, null, null, c.Problem, plans);

        string? gatewayProblem = null;
        decimal? gatewayAmount = null;
        var gatewayMethods = new List<PartnerPaymentOptionDto>();
        var consent = _readConsent == null ? null
            : await _readConsent.CheckAsync(companyId, c.PartnershipId, Scope.AutoRecordPayment, PartnerPayablePipeline.CapOf(c.Purchase), ct);
        if (consent is not { Ok: true }) gatewayProblem = PartnerPaymentPlanRule.ConsentNeededPrefix + (consent?.Reason ?? "ยังไม่ได้ให้ความยินยอม") + PartnerPaymentPlanRule.GatewayFallback;
        else
        {
            var target = _payLinks == null ? null : await _payLinks.PartnerPayTargetAsync(c.SourceCompanyId, c.SourceDocumentId, ct);
            if (target == null) gatewayProblem = "ผู้ขายยังไม่ได้เปิดลิงก์ชำระออนไลน์ของเอกสารนี้" + PartnerPaymentPlanRule.GatewayFallback;
            else if (!target.CanPayNow) gatewayProblem = (target.BlockedNote ?? "ไม่มียอดให้ชำระผ่านช่องทางของผู้ขายแล้ว") + PartnerPaymentPlanRule.GatewayFallback;
            else
            {
                gatewayAmount = target.AmountDue;
                foreach (var k in GatewayKinds.Where(k => target.AllowedMethods == null || target.AllowedMethods.Contains(k.ToString(), StringComparer.OrdinalIgnoreCase)))
                    gatewayMethods.Add(new PartnerPaymentOptionDto(k.ToString(), PlatformPayMethods.Label(k)));
                if (gatewayMethods.Count == 0) gatewayProblem = "ช่องทางออนไลน์ที่ผู้ขายเปิดไว้ต้องจ่ายที่หน้าชำระของผู้ขาย (เช่น บัตร)" + PartnerPaymentPlanRule.GatewayFallback;
            }
        }
        // ยอดเงินที่โอน = ยอดสุทธิหลังภาษีหักของใบ (ตัวคิดเดียวกับปุ่มเดียว/ท่อ) — แสดงเป็นค่าแนะนำ ผู้แจ้งแก้ได้ (ด่านเดิมตรวจซ้ำตอนบันทึก)
        var cap = await DocumentService.PaymentWhtFiguresAsync(_db, companyId, c.Purchase, c.Purchase.BalanceDue, ct);
        var (netPlan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, cap.NetCap, DateTime.UtcNow), ct);
        return new PartnerOutboundOptionsDto(true, null, c.SellerName, c.SourceNumber, c.Purchase.BalanceDue, accounts, gatewayProblem == null, gatewayProblem, gatewayAmount,
            gatewayMethods, PartnerPaymentPlanRule.GatewayCaveat, transferMethods, netPlan.Amount, netPlan.Wht, netPlan.Problem, plans);
    }

    /// <summary>การแจ้งชั่วคราว (ไม่บันทึก) ให้ตัวคิดยอด/ภาษีหักตัวเดียว (<see cref="PartnerPaymentNoticeService.PlanForAsync"/>)</summary>
    private static PartnerPaymentNotice TransientNotice(Ctx c, decimal cash, DateTime paidAt) => new()
    {
        CompanyId = c.Purchase.CompanyId, TargetDocumentId = c.Purchase.Id, CashAmount = Math.Round(cash, 2, MidpointRounding.AwayFromZero), PaidAt = paidAt,
        SourceCompanyId = c.SourceCompanyId, SourceDocumentId = c.SourceDocumentId, TradingPartnershipId = c.PartnershipId, LinkId = c.LinkId,
    };

    private async Task<List<PartnerPaymentPlanDto>> PlansAsync(Guid companyId, Guid purchaseId, IReadOnlyDictionary<Guid, string> usable, CancellationToken ct)
    {
        var rows = await _db.PartnerPaymentPlans.AsNoTracking().Where(x => x.CompanyId == companyId && x.PurchaseDocumentId == purchaseId)
            .OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct);
        var users = rows.Select(r => r.ChosenByUserId).Distinct().ToList();
        var names = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == companyId && users.Contains(cu.UserId))
            .Select(cu => new { cu.UserId, cu.User.FullName }).ToListAsync(ct);
        var notices = await _db.PartnerPaymentNotices.AsNoTracking().Where(n => n.CompanyId == companyId && n.TargetDocumentId == purchaseId && n.PaymentIntentId != null)
            .Select(n => new { n.PaymentIntentId, n.Status }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var notice = notices.FirstOrDefault(n => n.PaymentIntentId == r.SellerIntentId);
            // สถานะจากข้อเท็จจริงเท่านั้น: มีการแจ้งของผู้ขาย = ผู้ขายยืนยันรับเงินแล้ว · ไม่มี = ยังรอ (ไม่ประทับ "ชำระแล้ว" เอง)
            var state = notice != null ? "ผู้ขายยืนยันรับเงินแล้ว (" + PartnerPaymentNoticeRule.StatusLabel(notice.Status) + ")"
                : r.Kind == PartnerPaymentPlanRule.KindGateway ? "รอผู้ให้บริการ/ผู้ขายยืนยันรับเงิน"
                : r.SellerIntentId != null ? "บันทึกจ่ายของเราแล้ว · รอผู้ขายยืนยันรับเงิน" : "บันทึกจ่ายของเราแล้ว · ผู้ขายยังไม่ได้รับสำเนาสลิป";
            return new PartnerPaymentPlanDto(r.Id, r.Kind, r.Kind == PartnerPaymentPlanRule.KindGateway ? "จ่ายจากระบบของเรา" : "แจ้งโอนให้ผู้ขาย", r.Amount,
                Enum.TryParse<PaymentMethodKind>(r.Method, out var k) ? PlatformPayMethods.Label(k)
                    : PartnerPaymentNoticeRule.Methods.FirstOrDefault(m => m.Method.ToString() == r.Method).Label ?? r.Method,
                usable.TryGetValue(r.BankAccountId, out var l) ? l : "บัญชี (ใช้ไม่ได้แล้ว)", names.FirstOrDefault(n => n.UserId == r.ChosenByUserId)?.FullName,
                r.CreatedAt, state, r.SlipCopyNote, r.BuyerPaymentId);
        }).ToList();
    }

    // ═══════════════ A2 — จ่ายจากระบบของเรา ═══════════════

    public async Task<PartnerGatewayStartResult> StartGatewayAsync(Guid companyId, Guid userId, Guid purchaseId, Guid? bankAccountId, string? method, string? returnUrl,
        CancellationToken ct = default)
    {
        var c = await LoadAsync(companyId, purchaseId, ct);
        if (c.Problem != null) throw new BusinessRuleException(c.Problem, PartnerPaymentPlanRule.RuleCode, 409);
        if (!Enum.TryParse<PaymentMethodKind>(method, false, out var kind) || !GatewayKinds.Contains(kind))
            throw new BusinessRuleException("วิธีชำระไม่ถูกต้อง — เลือกจากรายการบนปุ่ม", PartnerPaymentPlanRule.RuleCode, 400);
        var usable = await PartnerPayablePipeline.UsablePayAccountsAsync(_db, companyId, ct);
        if (bankAccountId is not Guid bank) throw new BusinessRuleException(PartnerPaymentPlanRule.NoAccountReason, PartnerPaymentPlanRule.RuleCode, 400);
        if (!usable.ContainsKey(bank)) throw new BusinessRuleException(PartnerPaymentPlanRule.AccountUnusableReason, PartnerPaymentPlanRule.RuleCode, 400);
        if (_payLinks == null || _intents == null)
            throw new BusinessRuleException("ระบบชำระออนไลน์ยังไม่พร้อม — ใช้ “แจ้งโอนให้ผู้ขาย”", PartnerPaymentPlanRule.RuleCode, 503);
        var since = DateTime.UtcNow.AddHours(-1);
        if (await _db.PartnerPaymentPlans.AsNoTracking().CountAsync(x => x.CompanyId == companyId && x.PurchaseDocumentId == purchaseId && x.CreatedAt >= since, ct)
            >= PartnerPaymentPlanRule.MaxStartsPerHour)
            throw new BusinessRuleException(PartnerPaymentPlanRule.TooManyStartsReason, PartnerPaymentPlanRule.RuleCode, 429);

        // ── ธุรกรรมล็อกความยินยอม (connection แยก — แบบท่อ) ถือไว้จนเปิดรายการชำระ + บันทึกแผนเสร็จ ──
        using var lockScope = _scopes.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
        await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
        await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
        var consent = await consentSvc.CheckForWriteAsync(companyId, c.PartnershipId, Scope.AutoRecordPayment, PartnerPayablePipeline.CapOf(c.Purchase), ct);
        if (!consent.Ok)
        {
            await lockTx.RollbackAsync(ct);
            throw new BusinessRuleException(PartnerPaymentPlanRule.ConsentNeededPrefix + consent.Reason + PartnerPaymentPlanRule.GatewayFallback, PartnerConsent.RuleCode, 409);
        }
        var target = await _payLinks.PartnerPayTargetAsync(c.SourceCompanyId, c.SourceDocumentId, ct);
        if (target == null || !target.CanPayNow)
        {
            await lockTx.RollbackAsync(ct);
            throw new BusinessRuleException((target?.BlockedNote ?? "ผู้ขายยังไม่ได้เปิดลิงก์ชำระออนไลน์ของเอกสารนี้") + PartnerPaymentPlanRule.GatewayFallback,
                PartnerPaymentPlanRule.RuleCode, 409);
        }
        if (target.AllowedMethods != null && !target.AllowedMethods.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            await lockTx.RollbackAsync(ct);
            throw new BusinessRuleException("ผู้ขายไม่ได้เปิดช่องทางนี้สำหรับเอกสารนี้ — เลือกช่องทางอื่น", PartnerPaymentPlanRule.RuleCode, 400);
        }
        PaymentIntent intent;
        try
        {
            intent = await _intents.StartAsync(c.SourceCompanyId, new Accounting.Services.Payments.StartPaymentRequest(
                target.SourceKind, target.SourceId, target.AmountDue, kind, Description: target.Description,
                CustomerEmail: target.CustomerEmail, CustomerPhone: target.CustomerPhone, ReturnUrl: SafeReturnUrl(returnUrl), ContactId: target.ContactId), ct: ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or BusinessRuleException)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            throw new BusinessRuleException("เปิดรายการชำระของผู้ขายไม่สำเร็จ: " + ex.Message, PartnerPaymentPlanRule.RuleCode, 409);
        }
        var plan = new PartnerPaymentPlan
        {
            CompanyId = companyId, TradingPartnershipId = c.PartnershipId, LinkId = c.LinkId, PurchaseDocumentId = purchaseId,
            SourceCompanyId = c.SourceCompanyId, SourceDocumentId = c.SourceDocumentId, Kind = PartnerPaymentPlanRule.KindGateway,
            BankAccountId = bank, Method = kind.ToString(), Amount = intent.Amount, ChosenByUserId = userId, SellerIntentId = intent.Id,
            AccountOrigin = nameof(PartnerPaySourceAccount.Origin.PayerDeclared), CreatedBy = userId.ToString(),
        };
        _db.PartnerPaymentPlans.Add(plan);
        AuditPlan(_db, companyId, userId, "PartnerPaymentPlanStarted", plan, new { consentId = consent.ConsentId, intentStatus = intent.Status.ToString() });
        await _db.SaveChangesAsync(ct);
        await lockTx.CommitAsync(ct);
        return new PartnerGatewayStartResult(plan.Id, intent.Id, intent.Status.ToString(), intent.Amount, intent.QrPayload, intent.QrExpiresAt, intent.AuthorizeUrl,
            "เปิดรายการชำระของผู้ขายแล้ว — ชำระให้เสร็จที่ช่องทางนี้ · สถานะยังไม่ใช่ “ชำระแล้ว” จนกว่าผู้ให้บริการ/ผู้ขายยืนยัน (การแจ้งจะมาที่หน้าคู่ค้า)");
    }

    /// <summary>ลิงก์กลับต้องเป็นหน้าในระบบเรา (path สัมพัทธ์) — อื่น ๆ ทิ้ง (กัน open redirect)</summary>
    internal static string? SafeReturnUrl(string? url)
        => url != null && url.StartsWith("/pages/", StringComparison.Ordinal) && !url.Contains("//", StringComparison.Ordinal) && url.Length <= 300 ? url : null;

    // ═══════════════ A7 — แจ้งโอนให้ผู้ขายจากระบบของเรา ═══════════════

    public async Task<PartnerTransferResult> NotifyTransferAsync(Guid companyId, Guid userId, Guid purchaseId, SlipFile file, Guid? bankAccountId, string? method,
        decimal amount, DateTime transferredAtUtc, string? bankRef, CancellationToken ct = default)
    {
        var c = await LoadAsync(companyId, purchaseId, ct);
        if (c.Problem != null) throw new BusinessRuleException(c.Problem, PartnerPaymentPlanRule.RuleCode, 409);
        var payMethod = PaymentMethod.BankTransfer;
        if (!string.IsNullOrWhiteSpace(method))
        {
            if (!Enum.TryParse<PaymentMethod>(method, false, out var pm) || pm == PaymentMethod.Cash || !PartnerPaymentNoticeRule.Methods.Any(m => m.Method == pm))
                throw new BusinessRuleException("วิธีจ่ายไม่ถูกต้อง — เลือกจากรายการ", PartnerPaymentPlanRule.RuleCode, 400);
            payMethod = pm;
        }
        if (amount <= 0m) throw new BusinessRuleException("กรอกยอดเงินที่โอนจริง", PartnerPaymentPlanRule.RuleCode, 400);
        var sniffed = SlipEvidenceRule.DetectPublicType(file.Bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed) ?? throw new BusinessRuleException("รองรับเฉพาะรูปภาพ (JPEG/PNG/WEBP) หรือ PDF ของสลิป", PartnerPaymentPlanRule.RuleCode, 400);

        // บัญชี: คนเลือกเสมอ · สลิปของเราเอง (ผู้โอน = เรา) ตรวจไขว้ผ่านตัวตัดสินแหล่งเงินตัวเดียว (ขัดกัน ⇒ ปฏิเสธพร้อมเหตุ)
        var usable = await PartnerPayablePipeline.UsablePayAccountsAsync(_db, companyId, ct);
        var reading = _reader == null || sniffed == "application/pdf" ? OcrSlipText.Reading.Empty : await _reader.ReadAsync(file.Bytes, sniffed!, ct);
        PartnerPaySourceAccount.Slip? slip = null;
        if (reading.Payer is { Kind: OcrSlipText.PartyKind.BankAccount, BankCode: not null, AccountMask: not null } payer)
        {
            var keys = await _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == companyId && !b.IsDeleted)
                .Select(b => new SlipAccountMatch.AccountKey(b.Id, b.BankName, b.AccountNumber)).ToListAsync(ct);
            slip = PartnerPayablePipeline.SlipKindOf(payer.BankCode, payer.AccountMask, keys);
        }
        var chooser = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == companyId && cu.UserId == userId).Select(cu => cu.User.FullName).FirstOrDefaultAsync(ct);
        var (bank, origin, accountProblem) = PartnerPaymentPlanRule.TransferAccount(usable, bankAccountId, slip, chooser);
        if (bank is not Guid bankId) throw new BusinessRuleException(accountProblem ?? PartnerPaymentPlanRule.NoAccountReason, PartnerPaySourceAccount.RuleCode, 409);
        string? warning = reading.Amount is decimal read && read != Math.Round(amount, 2, MidpointRounding.AwayFromZero)
            ? $"ยอดที่อ่านได้จากสลิป {read:N2} บาท ไม่ตรงยอดที่แจ้ง {amount:N2} บาท — ระบบบันทึกตามยอดที่ท่านยืนยัน (ตรวจอีกครั้ง)" : null;

        var planId = Guid.NewGuid();
        var (plan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, amount, transferredAtUtc), ct);
        if (plan.Amount is not decimal cash || plan.Problem != null)
            throw new BusinessRuleException(plan.Problem ?? "บันทึกจ่ายจากยอดนี้ไม่ได้ — บันทึกเองที่หน้าใบซื้อ", PartnerPaymentPlanRule.RuleCode, 409);

        // (1) PV ของเราจากหลักฐานของเรา (การกระทำของคน · เส้นจ่ายเดิม · เลขอ้างอิงของแผน = กุญแจกันซ้ำ)
        Models.DTOs.Document.PaymentResponse pay;
        try
        {
            pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                DocumentId: purchaseId, PaymentDate: transferredAtUtc, Amount: cash, PaymentMethod: payMethod,
                Reference: PartnerPaymentPlanRule.BuyerReference(planId), BankAccount: null,
                Notes: $"แจ้งโอนให้ผู้ขายคู่ค้าจากระบบของเรา — {c.SourceNumber} ({usable[bankId]})",
                OverrideBankAccountId: bankId, WithholdingTaxAmount: plan.Wht), userId.ToString(), PaymentEntrySource.Manual);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or DbUpdateException)
        {
            if (ex is DbUpdateException) _logger.LogError(ex, "บันทึกจ่ายจากการแจ้งโอนของใบซื้อ {Purchase} บริษัท {Company} ล้มที่ฐานข้อมูล", purchaseId, companyId);
            throw new BusinessRuleException("บันทึกจ่ายไม่สำเร็จ: " + PartnerPayablePipeline.UserMessageOf(ex) + " — แก้แล้วแนบใหม่ หรือบันทึกเองที่หน้าใบซื้อ",
                PartnerPaymentPlanRule.RuleCode, 409);
        }
        await AttachOwnSlipAsync(companyId, purchaseId, userId, file, sniffed!, ext, ct);

        // (2) สำเนาสลิปเข้าคิวรอยืนยันของผู้ขาย — ความยินยอม ReceiveSlipCopy ของผู้ขาย ตรวจเพื่อเขียนในธุรกรรมล็อกที่ถือไว้จนส่งเสร็จ ·
        // ไม่ได้ส่ง = PV ของเรายังอยู่ (เงินออกแล้วจริง) + เหตุบนแผน/คำตอบ (ไม่เงียบ)
        Guid? sellerIntent = null;
        string copyNote;
        using (var lockScope = _scopes.CreateScope())
        {
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
            await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
            await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
            var sellerConsent = await consentSvc.CheckForWriteAsync(c.SourceCompanyId, c.PartnershipId, Scope.ReceiveSlipCopy, c.SourceCap, ct);
            if (!sellerConsent.Ok || _slips == null)
            {
                await lockTx.RollbackAsync(ct);
                copyNote = _slips == null ? PartnerPaymentPlanRule.SlipCopyFailedPrefix + "ระบบรับสลิปของผู้ขายยังไม่พร้อม"
                    : PartnerPaymentPlanRule.SlipCopyNotConsentedPrefix + sellerConsent.Reason + ") — แจ้งผู้ขายเองพร้อมสลิป";
            }
            else
            {
                try
                {
                    var r = await _slips.SubmitAsync(c.SourceCompanyId, c.SourceDocumentId, null, file, cash, bankRef ?? reading.Ref, SlipEvidenceService.ChannelPartnerNextAcc, ct);
                    sellerIntent = r.Intent.Id;
                    copyNote = PartnerPaymentPlanRule.SlipCopySent;
                    await lockTx.CommitAsync(ct);
                }
                catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException or ArgumentException)
                {
                    await lockTx.RollbackAsync(CancellationToken.None);
                    copyNote = PartnerPaymentPlanRule.SlipCopyFailedPrefix + ex.Message;
                }
            }
        }

        var row = new PartnerPaymentPlan
        {
            Id = planId, CompanyId = companyId, TradingPartnershipId = c.PartnershipId, LinkId = c.LinkId, PurchaseDocumentId = purchaseId,
            SourceCompanyId = c.SourceCompanyId, SourceDocumentId = c.SourceDocumentId, Kind = PartnerPaymentPlanRule.KindTransfer, BankAccountId = bankId,
            Method = payMethod.ToString(), Amount = cash, ChosenByUserId = userId, SellerIntentId = sellerIntent, BuyerPaymentId = pay.Id,
            TransferredAt = transferredAtUtc, SlipCopyNote = copyNote.Length > 500 ? copyNote[..500] : copyNote, AccountOrigin = origin.ToString(), CreatedBy = userId.ToString(),
        };
        _db.PartnerPaymentPlans.Add(row);
        AuditPlan(_db, companyId, userId, "PartnerTransferNotified", row, new { paymentId = pay.Id, wht = plan.Wht, slipKind = slip?.Kind.ToString(), slipCopySent = sellerIntent != null });
        await _db.SaveChangesAsync(ct);
        return new PartnerTransferResult(row.Id, pay.Id, usable[bankId], origin.ToString(), sellerIntent != null, copyNote, warning,
            "บันทึกจ่ายใบซื้อของเราแล้ว" + (sellerIntent != null ? " · ส่งสำเนาสลิปให้ผู้ขายตรวจแล้ว (ผู้ขายยังต้องยืนยันรับเงิน)" : " · " + copyNote));
    }

    /// <summary>สลิปของเราเองแนบกับใบซื้อ (หลักฐานประกอบการจ่าย — ม.14) · ที่เก็บกลาง · โฟลเดอร์ไม่เสิร์ฟตรง</summary>
    private async Task AttachOwnSlipAsync(Guid companyId, Guid purchaseId, Guid userId, SlipFile file, string contentType, string ext, CancellationToken ct)
    {
        var storage = _storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;
        var relDir = $"uploads/document-slips/{DateTime.UtcNow:yyyy-MM}";
        var name = $"{Guid.NewGuid():N}{ext}";
        var abs = System.IO.Path.Combine(storage.Locator.RelativeBase, "wwwroot", relDir, name);
        await storage.WriteAsync(abs, file.Bytes, contentType, ct);
        _db.Set<FileAttachment>().Add(new FileAttachment
        {
            CompanyId = companyId, FileName = name, OriginalFileName = "slip" + ext, ContentType = contentType, FileSize = file.Bytes.LongLength, StoragePath = abs,
            EntityType = "Document", EntityId = purchaseId, UploadedByUserId = userId, CreatedBy = "partner-transfer-own",
        });
        await _db.SaveChangesAsync(ct);
    }

    private static void AuditPlan(AccountingDbContext db, Guid companyId, Guid userId, string evt, PartnerPaymentPlan p, object extra)
        => db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Create, EntityType = nameof(PartnerPaymentPlan), EntityId = p.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = evt, kind = p.Kind, purchaseDocumentId = p.PurchaseDocumentId, sellerCompanyId = p.SourceCompanyId, sourceDocumentId = p.SourceDocumentId,
                bankAccountId = p.BankAccountId, accountOrigin = p.AccountOrigin, method = p.Method, amount = p.Amount, sellerIntentId = p.SellerIntentId,
                buyerPaymentId = p.BuyerPaymentId, slipCopyNote = p.SlipCopyNote, ruleCode = PartnerPaymentPlanRule.RuleCode, legalReference = PartnerPaymentPlanRule.LegalReference,
                usedAi = false, detail = extra,
            }),
            Timestamp = DateTime.UtcNow,
        });
}
