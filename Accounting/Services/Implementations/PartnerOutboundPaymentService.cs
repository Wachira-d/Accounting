using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Services.Implementations;

public sealed record PartnerPaymentPlanDto(Guid Id, string Kind, string KindLabel, decimal Amount, string MethodLabel, string AccountLabel, string? ChosenBy,
    DateTime CreatedAt, string StateLabel, string? SlipCopyNote, Guid? BuyerPaymentId);

/// <param name="Problem">เหตุที่ใช้ทั้งสองปุ่มไม่ได้ (null = ใช้ได้) — ข้อความไทยจากเซิร์ฟเวอร์</param>
/// <param name="GatewayProblem">เหตุที่ปุ่ม "จ่ายจากระบบของเรา" ใช้ไม่ได้ (ผู้ขายไม่เปิดช่องทาง · ยอดไม่หักภาษีตามกติกาของเรา) — ปุ่มแจ้งโอนยังใช้ได้</param>
/// <param name="GatewayAfterNote">ฝ่ายค้าน 264-P4 M6: หลังผู้ขายยืนยันรับเงินจะเกิดอะไร (บันทึกเองตามความยินยอม หรือปุ่มเดียว) — ข้อมูล ไม่ใช่ด่าน</param>
/// <param name="TransferButtonLabel">ฝ่ายค้าน L8: ป้ายปุ่มแจ้งโอนตามสถานะความยินยอมรับสลิปของผู้ขาย (ไม่สัญญาว่าจะส่งเมื่อส่งไม่ได้)</param>
public sealed record PartnerOutboundOptionsDto(bool Eligible, string? Problem, string? SellerName, string? SourceDocumentNumber, decimal BalanceDue,
    List<PartnerPaymentOptionDto> Accounts, bool GatewayOffered, string? GatewayProblem, decimal? GatewayAmount, List<PartnerPaymentOptionDto> GatewayMethods,
    string GatewayCaveat, List<PartnerPaymentOptionDto> TransferMethods, decimal? TransferAmount, decimal? TransferWht, string? TransferProblem,
    List<PartnerPaymentPlanDto> Plans, string? GatewayAfterNote = null, string TransferButtonLabel = "บันทึกจ่าย");

public sealed record PartnerGatewayStartResult(Guid PlanId, Guid IntentId, string Status, decimal Amount, string? QrPayload, DateTime? QrExpiresAt, string? AuthorizeUrl, string Message);

public sealed record PartnerTransferResult(Guid PlanId, Guid PaymentId, string AccountLabel, string Origin, bool SlipCopySent, string SlipCopyNote, string? Warning, string Message);

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11 · 264-5-12 · ฝ่ายค้าน review-264-P4 ⇒ 264-5-14…18) — <b>ผู้ซื้อ (B) เป็นผู้เริ่มจ่าย</b> ใบซื้อจากคู่ค้า NextAcc
/// จากในระบบของ B (ล็อกอิน · สิทธิ์อนุมัติใบซื้อ — ด่านที่ controller) · ไม่มีตัวเลือกบัญชีของ B บนหน้าชำระสาธารณะของ A
/// <list type="bullet">
/// <item><b>จ่ายจากระบบของเรา</b> (<see cref="StartGatewayAsync"/>): เปิดได้ด้วยสิทธิ์ของผู้กด + คู่ค้า Accepted + ผู้ขายเปิดช่องทาง (<c>CanPayNow</c>) — <b>ไม่ใช้ความยินยอม</b>
/// (ฝ่ายค้าน M6 · คำตัดสิน 264-5-11 ถูกกลับ: <c>AutoRecordPayment</c> ตัดสินแค่ว่าการแจ้งที่ตามมาบันทึกเองหรือไม่) · ยอดของผู้ขายต้องตรงแผนภาษีหักของเรา (M5) ·
/// <b>ไม่ประทับ "ชำระแล้ว"</b> — ผู้ขายรับเงินเมื่อผู้ให้บริการยืนยัน ⇒ การแจ้งตามมา ⇒ ชั้น PayerDeclared (บันทึกเองเฉพาะช่องทางที่ผูกบัญชีต้นทาง — M4)</item>
/// <item><b>แจ้งโอนให้ผู้ขาย</b> (<see cref="NotifyTransferAsync"/>): กุญแจกันซ้ำคงที่ (H1) + ล็อกต่อใบซื้อ ⇒ <b>จองแถวแผนก่อน PV</b> (M2 — PV ของแผนหาเจอเสมอด้วยเลขอ้างอิงของแผน) ⇒
/// PV ของเรา (การกระทำของคน) ⇒ outbox สำเนาสลิป (<see cref="DeliverSlipCopyAsync"/>: ความยินยอม <c>AcceptPayerSlipCopy</c> ของ<b>ผู้ขาย</b> — H2) ·
/// ผู้ขายยืนยันทีหลัง ⇒ การแจ้งปิดด้วย PV ของแผน (ผูกรายการชำระของผู้ขาย หรือยอด/วันตรงตัวเดียว — M3) ⇒ ไม่มี PV ที่สอง</item>
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
    /// <summary>ตัวตรวจความยินยอมของคำขอนี้ — <b>อ่านอย่างเดียว</b> สำหรับป้ายบน <see cref="OptionsAsync"/> (เส้นเขียนใช้ตัวของ scope ล็อก + CheckForWriteAsync)</summary>
    private readonly PartnerConsentService? _readConsent;
    private readonly INotificationEngine? _notify;

    /// <summary>วิธีชำระของปุ่ม "จ่ายจากระบบของเรา" — บัตรต้องโทเคนจากหน้าผู้ให้บริการ (ไม่มีในหน้านี้) · สลิปใช้ปุ่มแจ้งโอน</summary>
    public static readonly IReadOnlyList<PaymentMethodKind> GatewayKinds = new[] { PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking, PaymentMethodKind.PromptPay, PaymentMethodKind.TrueMoney };

    public const int SlipCopyBatch = 50;

    public PartnerOutboundPaymentService(AccountingDbContext db, IDocumentService docs, IServiceScopeFactory scopes, ILogger<PartnerOutboundPaymentService> logger,
        IDocumentPayLinkService? payLinks = null, Accounting.Services.Payments.IPaymentIntentService? intents = null, SlipEvidenceService? slips = null,
        ISlipImageReader? reader = null, Accounting.Services.Storage.IFileStorage? storage = null, PartnerConsentService? readConsent = null,
        INotificationEngine? notify = null)
    {
        _db = db; _docs = docs; _scopes = scopes; _logger = logger; _payLinks = payLinks; _intents = intents; _slips = slips; _reader = reader; _storage = storage;
        _readConsent = readConsent; _notify = notify;
    }

    private Accounting.Services.Storage.IFileStorage Storage => _storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;

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

    /// <summary>ตัวเลือกของสองปุ่มบนใบซื้อ (อ่านอย่างเดียว · server computes ป้าย/ยอด/เหตุ) — ความยินยอมอ่านด้วยตัวตรวจอ่านอย่างเดียว (ป้ายเท่านั้น — ไม่ใช่ด่าน)</summary>
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
        var target = _payLinks == null ? null : await _payLinks.PartnerPayTargetAsync(c.SourceCompanyId, c.SourceDocumentId, ct);
        if (target == null) gatewayProblem = "ผู้ขายยังไม่ได้เปิดลิงก์ชำระออนไลน์ของเอกสารนี้" + PartnerPaymentPlanRule.GatewayFallback;
        else if (!target.CanPayNow) gatewayProblem = (target.BlockedNote ?? "ไม่มียอดให้ชำระผ่านช่องทางของผู้ขายแล้ว") + PartnerPaymentPlanRule.GatewayFallback;
        else
        {
            // ฝ่ายค้าน M5: ยอดของผู้ขายเทียบแผนยอด/ภาษีหักของเรา (ตัวคิดเดียวกับปุ่มเดียว/ท่อ)
            var (gwPlan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, target.AmountDue, DateTime.UtcNow), ct);
            gatewayProblem = PartnerPaymentPlanRule.GatewayAmountProblem(target.AmountDue, gwPlan) is string ap ? ap + PartnerPaymentPlanRule.GatewayFallback : null;
            gatewayAmount = target.AmountDue;
            foreach (var k in GatewayKinds.Where(k => target.AllowedMethods == null || target.AllowedMethods.Contains(k.ToString(), StringComparer.OrdinalIgnoreCase)))
                gatewayMethods.Add(new PartnerPaymentOptionDto(k.ToString(), PlatformPayMethods.Label(k)));
            if (gatewayProblem == null && gatewayMethods.Count == 0)
                gatewayProblem = "ช่องทางออนไลน์ที่ผู้ขายเปิดไว้ต้องจ่ายที่หน้าชำระของผู้ขาย (เช่น บัตร)" + PartnerPaymentPlanRule.GatewayFallback;
        }
        // M6: ความยินยอมบันทึกจ่ายเป็นแค่ "หลังผู้ขายยืนยันจะเกิดอะไร" (ข้อมูล ไม่ใช่ด่าน)
        var autoConsent = _readConsent == null ? null
            : await _readConsent.CheckAsync(companyId, c.PartnershipId, Scope.AutoRecordPayment, PartnerPayablePipeline.CapOf(c.Purchase), ct);
        var afterNote = autoConsent is { Ok: true }
            ? "ผู้ขายยืนยันรับเงินแล้ว ⇒ ระบบบันทึกจ่ายเองตามความยินยอมของเรา (เฉพาะโมบาย/อินเทอร์เน็ตแบงก์กิ้ง · ช่องทางอื่นกดยืนยันเองครั้งเดียว)"
            : "ผู้ขายยืนยันรับเงินแล้ว ⇒ การแจ้งขึ้นที่หน้าคู่ค้า บัญชีที่เลือกถูกเลือกไว้ให้ — กด “บันทึกจ่าย” ครั้งเดียว";
        // L8: ป้ายปุ่มแจ้งโอนตามความยินยอมรับสลิปของผู้ขาย (อ่านอย่างเดียว — ส่งจริงตรวจใหม่ใน outbox)
        var sellerSlip = _readConsent == null ? null : await _readConsent.CheckAsync(c.SourceCompanyId, c.PartnershipId, Scope.AcceptPayerSlipCopy, c.SourceCap, ct);
        var transferLabel = sellerSlip is { Ok: true } ? "บันทึกจ่าย + ส่งสลิปให้ผู้ขาย" : "บันทึกจ่าย (ผู้ขายยังไม่เปิดรับสลิปจากระบบ — แจ้งผู้ขายเอง)";
        // ยอดเงินที่โอน = ยอดสุทธิหลังภาษีหักของใบ (ตัวคิดเดียวกับปุ่มเดียว/ท่อ) — แสดงเป็นค่าแนะนำ ผู้แจ้งแก้ได้ (ด่านเดิมตรวจซ้ำตอนบันทึก)
        var cap = await DocumentService.PaymentWhtFiguresAsync(_db, companyId, c.Purchase, c.Purchase.BalanceDue, ct);
        var (netPlan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, cap.NetCap, DateTime.UtcNow), ct);
        return new PartnerOutboundOptionsDto(true, null, c.SellerName, c.SourceNumber, c.Purchase.BalanceDue, accounts, gatewayProblem == null, gatewayProblem, gatewayAmount,
            gatewayMethods, PartnerPaymentPlanRule.GatewayCaveat, transferMethods, netPlan.Amount, netPlan.Wht, netPlan.Problem, plans, afterNote, transferLabel);
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
        var notices = await _db.PartnerPaymentNotices.AsNoTracking().Where(n => n.CompanyId == companyId && n.TargetDocumentId == purchaseId)
            .Select(n => new { n.PaymentIntentId, n.RecordedPaymentId, n.Status }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var notice = notices.FirstOrDefault(n => (r.SellerIntentId != null && n.PaymentIntentId == r.SellerIntentId)
                || (r.BuyerPaymentId != null && n.RecordedPaymentId == r.BuyerPaymentId));
            // สถานะจากข้อเท็จจริงเท่านั้น (ไม่ประทับ "ชำระแล้ว" เอง) · ฝ่ายค้าน L6: การแจ้งที่ถอน/ปิดไม่ใช่ "ผู้ขายยืนยันรับเงินแล้ว"
            var state = notice?.Status switch
            {
                PartnerPaymentNoticeRule.StatusWithdrawn => "ผู้ขายยกเลิกการรับเงินแล้ว — ตรวจการจ่ายของเรา",
                PartnerPaymentNoticeRule.StatusDismissed => "ปิดการแจ้งของผู้ขายแล้ว",
                string s => "ผู้ขายยืนยันรับเงินแล้ว (" + PartnerPaymentNoticeRule.StatusLabel(s) + ")",
                null when r.Kind == PartnerPaymentPlanRule.KindGateway => "รอผู้ให้บริการ/ผู้ขายยืนยันรับเงิน",
                null when r.BuyerPaymentId == null => "กำลังบันทึกจ่ายของเรา",
                null when r.SlipCopyState == PartnerPaymentPlanRule.CopyPending => "บันทึกจ่ายของเราแล้ว · กำลังส่งสำเนาสลิปให้ผู้ขาย",
                null when r.SellerIntentId != null => "บันทึกจ่ายของเราแล้ว · รอผู้ขายยืนยันรับเงิน",
                _ => "บันทึกจ่ายของเราแล้ว · ผู้ขายยังไม่ได้รับสำเนาสลิป",
            };
            return new PartnerPaymentPlanDto(r.Id, r.Kind, r.Kind == PartnerPaymentPlanRule.KindGateway ? "จ่ายจากระบบของเรา" : "แจ้งโอนให้ผู้ขาย", r.Amount,
                Enum.TryParse<PaymentMethodKind>(r.Method, out var k) ? PlatformPayMethods.Label(k)
                    : PartnerPaymentNoticeRule.Methods.FirstOrDefault(m => m.Method.ToString() == r.Method).Label ?? r.Method,
                usable.TryGetValue(r.BankAccountId, out var l) ? l : "บัญชี (ใช้ไม่ได้แล้ว)", names.FirstOrDefault(n => n.UserId == r.ChosenByUserId)?.FullName,
                r.CreatedAt, state, r.SlipCopyNote, r.BuyerPaymentId);
        }).ToList();
    }

    // ═══════════════ A2 — จ่ายจากระบบของเรา ═══════════════

    /// <summary>ฝ่ายค้าน M6 (คำตัดสิน 264-5-11 ถูกกลับ): ไม่ใช้ความยินยอม · ไม่ถือธุรกรรมข้ามการเรียกผู้ให้บริการ (L7) · ยอดตรงแผนภาษีหักของเรา (M5)</summary>
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
        if (await _db.PartnerPaymentPlans.AsNoTracking().CountAsync(x => x.CompanyId == companyId && x.PurchaseDocumentId == purchaseId
                && x.Kind == PartnerPaymentPlanRule.KindGateway && x.CreatedAt >= since, ct) >= PartnerPaymentPlanRule.MaxStartsPerHour)
            throw new BusinessRuleException(PartnerPaymentPlanRule.TooManyStartsReason, PartnerPaymentPlanRule.RuleCode, 429);

        var target = await _payLinks.PartnerPayTargetAsync(c.SourceCompanyId, c.SourceDocumentId, ct);
        if (target == null || !target.CanPayNow)
            throw new BusinessRuleException((target?.BlockedNote ?? "ผู้ขายยังไม่ได้เปิดลิงก์ชำระออนไลน์ของเอกสารนี้") + PartnerPaymentPlanRule.GatewayFallback,
                PartnerPaymentPlanRule.RuleCode, 409);
        if (target.AllowedMethods != null && !target.AllowedMethods.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase))
            throw new BusinessRuleException("ผู้ขายไม่ได้เปิดช่องทางนี้สำหรับเอกสารนี้ — เลือกช่องทางอื่น", PartnerPaymentPlanRule.RuleCode, 400);
        // ฝ่ายค้าน M5: ไม่เชื่อยอดสุทธิของผู้ขาย — แผนยอด/ภาษีหักของเราบนยอดนั้นต้องไม่มีเหตุ/คำเตือน
        var (gwPlan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, target.AmountDue, DateTime.UtcNow), ct);
        if (PartnerPaymentPlanRule.GatewayAmountProblem(target.AmountDue, gwPlan) is string amountProblem)
            throw new BusinessRuleException(amountProblem + PartnerPaymentPlanRule.GatewayFallback, PartnerPaymentPlanRule.RuleCode, 409);

        PaymentIntent intent;
        try
        {
            intent = await _intents.StartAsync(c.SourceCompanyId, new Accounting.Services.Payments.StartPaymentRequest(
                target.SourceKind, target.SourceId, target.AmountDue, kind, Description: target.Description,
                CustomerEmail: target.CustomerEmail, CustomerPhone: target.CustomerPhone, ReturnUrl: SafeReturnUrl(returnUrl), ContactId: target.ContactId), ct: ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or BusinessRuleException or HttpRequestException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // L7: ผู้ให้บริการล่ม/หมดเวลา ⇒ ข้อความไทย (ไม่ใช่ 500) · ข้อความภายในไม่ถึงผู้ใช้เมื่อเป็นเครือข่าย
            if (ex is HttpRequestException or TaskCanceledException) _logger.LogError(ex, "เปิดรายการชำระของผู้ขาย {Seller} ให้ใบซื้อ {Purchase} ล้มที่ผู้ให้บริการ", c.SourceCompanyId, purchaseId);
            var msg = ex is HttpRequestException or TaskCanceledException ? "ผู้ให้บริการชำระเงินไม่ตอบ — ลองใหม่อีกครั้ง" : ex.Message;
            throw new BusinessRuleException("เปิดรายการชำระของผู้ขายไม่สำเร็จ: " + msg + PartnerPaymentPlanRule.GatewayFallback, PartnerPaymentPlanRule.RuleCode, 409);
        }
        // รายการชำระเดิมที่ยังเปิด (ผู้ขายใช้ใบเดิม) ⇒ แผนเดิมของเราคงไว้ (unique ต่อรายการของผู้ขาย) — ไม่สร้างแถวที่สอง
        var plan = await _db.PartnerPaymentPlans.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SellerIntentId == intent.Id, ct);
        if (plan == null)
        {
            plan = new PartnerPaymentPlan
            {
                CompanyId = companyId, TradingPartnershipId = c.PartnershipId, LinkId = c.LinkId, PurchaseDocumentId = purchaseId,
                SourceCompanyId = c.SourceCompanyId, SourceDocumentId = c.SourceDocumentId, Kind = PartnerPaymentPlanRule.KindGateway,
                BankAccountId = bank, Method = kind.ToString(), Amount = intent.Amount, ChosenByUserId = userId, SellerIntentId = intent.Id,
                AccountOrigin = nameof(PartnerPaySourceAccount.Origin.PayerDeclared), CreatedBy = userId.ToString(),
            };
            _db.PartnerPaymentPlans.Add(plan);
        }
        else
        {
            plan.BankAccountId = bank; plan.Method = kind.ToString(); plan.ChosenByUserId = userId; plan.UpdatedAt = DateTime.UtcNow;
        }
        AuditPlan(_db, companyId, userId, "PartnerPaymentPlanStarted", plan, new
        {
            intentStatus = intent.Status.ToString(), accountBound = PartnerPaymentPlanRule.MethodBindsAccount(plan.Method), buyerWht = gwPlan.Wht,
        });
        await _db.SaveChangesAsync(ct);
        return new PartnerGatewayStartResult(plan.Id, intent.Id, intent.Status.ToString(), intent.Amount, PartnerPaymentPlanRule.SafeQrPayload(intent.QrPayload), intent.QrExpiresAt,
            intent.AuthorizeUrl != null && intent.AuthorizeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? intent.AuthorizeUrl : null,
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

        var (plan, _) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, TransientNotice(c, amount, transferredAtUtc), ct);
        if (plan.Amount is not decimal cash || plan.Problem != null)
            throw new BusinessRuleException(plan.Problem ?? "บันทึกจ่ายจากยอดนี้ไม่ได้ — บันทึกเองที่หน้าใบซื้อ", PartnerPaymentPlanRule.RuleCode, 409);

        // H1: กุญแจกันซ้ำคงที่ (เอกสารผู้ขาย · แฮชสลิป · ยอด) — ตรวจเร็วก่อนเขียนไฟล์ แล้วตรวจซ้ำใต้ล็อก
        var sha = SlipEvidenceRule.Sha256Hex(file.Bytes);
        var key = PartnerPaymentPlanRule.TransferIdempotencyKey(c.SourceDocumentId, sha, cash);
        await RefuseIfDuplicateAsync(companyId, key, null, ct);

        // M2: ไฟล์สลิปเข้าที่เก็บก่อนแตะฐาน (ล้ม = ยังไม่มีอะไรเกิด) · ผูกไฟล์แนบ/ส่งสำเนาทีหลังจากที่เก็บนี้ (outbox)
        var storagePath = System.IO.Path.Combine(Storage.Locator.RelativeBase, "wwwroot", $"uploads/document-slips/{DateTime.UtcNow:yyyy-MM}", $"{Guid.NewGuid():N}{ext}");
        try { await Storage.WriteAsync(storagePath, file.Bytes, sniffed, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _logger.LogError(ex, "เก็บไฟล์สลิปแจ้งโอนของใบซื้อ {Purchase} บริษัท {Company} ไม่สำเร็จ", purchaseId, companyId);
            throw new BusinessRuleException("เก็บไฟล์สลิปไม่สำเร็จ — ยังไม่ได้บันทึกจ่าย ลองใหม่อีกครั้ง", PartnerPaymentPlanRule.RuleCode, 503);
        }

        // ── ล็อกต่อใบซื้อ (connection แยก · advisory) ครอบตั้งแต่ตรวจกุญแจจนผูก PV กับแผน — กดซ้ำพร้อมกันได้ทีละคำขอ ──
        Guid planId;
        Models.DTOs.Document.PaymentResponse pay;
        using (var lockScope = _scopes.CreateScope())
        {
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
            await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
            await lockDb.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", new object[] { "partner-transfer:" + purchaseId.ToString("N") }, ct);
            var reserved = await RefuseIfDuplicateAsync(companyId, key, lockTx, ct);   // ใต้ล็อก: มี PV แล้ว ⇒ 409 · แถวจองค้าง (ล้มก่อน PV) ⇒ ใช้แถวเดิม
            var row = reserved;
            if (row == null)
            {
                // จองแถวแผนก่อน PV (M2): PV ของแผนหาเจอเสมอด้วยเลขอ้างอิงของแผน แม้ขั้นผูกล้ม · unique index กันคำขอที่หลุดล็อก
                row = new PartnerPaymentPlan
                {
                    CompanyId = companyId, TradingPartnershipId = c.PartnershipId, LinkId = c.LinkId, PurchaseDocumentId = purchaseId,
                    SourceCompanyId = c.SourceCompanyId, SourceDocumentId = c.SourceDocumentId, Kind = PartnerPaymentPlanRule.KindTransfer, BankAccountId = bankId,
                    Method = payMethod.ToString(), Amount = cash, ChosenByUserId = userId, TransferredAt = transferredAtUtc, AccountOrigin = origin.ToString(),
                    IdempotencyKey = key, SlipSha256 = sha, SlipStoragePath = storagePath, SlipContentType = sniffed,
                    SlipBankRef = Trim64(bankRef ?? reading.Ref), SlipCopyState = PartnerPaymentPlanRule.CopyPending, SlipCopyNote = PartnerPaymentPlanRule.SlipCopyPendingNote,
                    CreatedBy = userId.ToString(),
                };
                _db.PartnerPaymentPlans.Add(row);
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(ex, "แจ้งโอนซ้ำของใบซื้อ {Purchase} ชน unique index กุญแจกันซ้ำ", purchaseId);
                    await lockTx.RollbackAsync(CancellationToken.None);
                    throw new BusinessRuleException(PartnerPaymentPlanRule.DuplicateTransferPrefix + "รีเฟรชหน้าเพื่อดูรายการเดิม", PartnerPaymentPlanRule.RuleCode, 409);
                }
            }
            planId = row.Id;

            // (1) PV ของเราจากหลักฐานของเรา (การกระทำของคน · เส้นจ่ายเดิม · เลขอ้างอิงของแผน)
            try
            {
                pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: purchaseId, PaymentDate: transferredAtUtc, Amount: cash, PaymentMethod: payMethod,
                    Reference: PartnerPaymentPlanRule.BuyerReference(planId), BankAccount: null,
                    Notes: $"แจ้งโอนให้ผู้ขายคู่ค้าจากระบบของเรา — {c.SourceNumber} ({usable[bankId]})",
                    OverrideBankAccountId: bankId, WithholdingTaxAmount: plan.Wht), userId.ToString(), PaymentEntrySource.Manual);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or DbUpdateException or BusinessRuleException)
            {
                // เส้นจ่ายปฏิเสธ (งวดปิด · ยอดเกิน ฯลฯ) ⇒ ปลดแถวจอง (ลบแบบ soft — กุญแจว่างให้แก้แล้วลองใหม่) · ไม่มี PV
                _db.ChangeTracker.Clear();
                await _db.PartnerPaymentPlans.Where(x => x.Id == planId && x.CompanyId == companyId && x.BuyerPaymentId == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDeleted, true).SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), CancellationToken.None);
                await lockTx.RollbackAsync(CancellationToken.None);
                if (ex is DbUpdateException) _logger.LogError(ex, "บันทึกจ่ายจากการแจ้งโอนของใบซื้อ {Purchase} บริษัท {Company} ล้มที่ฐานข้อมูล", purchaseId, companyId);
                throw new BusinessRuleException("บันทึกจ่ายไม่สำเร็จ: " + PartnerPayablePipeline.UserMessageOf(ex) + " — แก้แล้วแนบใหม่ หรือบันทึกเองที่หน้าใบซื้อ",
                    PartnerPaymentPlanRule.RuleCode, 409);
            }

            // (2) ผูก PV กับแผน + ไฟล์แนบของใบซื้อ + audit — ล้มตรงนี้ PV ยังหาเจอด้วยเลขอ้างอิง (ตัวหา PV ของแผน + outbox ซ่อมไฟล์แนบ)
            try
            {
                await _db.PartnerPaymentPlans.Where(x => x.Id == planId && x.CompanyId == companyId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.BuyerPaymentId, (Guid?)pay.Id).SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
                row.BuyerPaymentId = pay.Id;
                await EnsureOwnAttachmentAsync(companyId, row, ct);
                AuditPlan(_db, companyId, userId, "PartnerTransferNotified", row, new { paymentId = pay.Id, wht = plan.Wht, slipKind = slip?.Kind.ToString(), slipSha256 = sha });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is DbUpdateException or IOException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "ผูก PV {Payment} กับแผนแจ้งโอน {Plan} ไม่ครบ — PV หาเจอด้วยเลขอ้างอิง · outbox ซ่อมไฟล์แนบ", pay.Id, planId);
                warning = (warning == null ? "" : warning + " · ") + "บันทึกจ่ายแล้ว แต่ผูกสลิปกับใบซื้อยังไม่ครบ — ระบบซ่อมเองรอบถัดไป";
            }
            await lockTx.CommitAsync(ct);
        }

        // (3) outbox: ส่งสำเนาสลิปทันทีครั้งหนึ่ง (ล้ม/ยังไม่ยินยอม = สถานะบนแผน · งานเบื้องหลังลองต่อ)
        var copy = await DeliverSlipCopyAsync(companyId, planId, DateTime.UtcNow, ct);
        var sent = copy.State == PartnerPaymentPlanRule.CopySent;
        return new PartnerTransferResult(planId, pay.Id, usable[bankId], origin.ToString(), sent, copy.Note, warning,
            "บันทึกจ่ายใบซื้อของเราแล้ว" + (sent ? " · ส่งสำเนาสลิปให้ผู้ขายตรวจแล้ว (ผู้ขายยังต้องยืนยันรับเงิน)" : " · " + copy.Note));
    }

    private static string? Trim64(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().Length > 64 ? v.Trim()[..64] : v.Trim();

    /// <summary>H1: แผนแจ้งโอนของกุญแจนี้ (บริษัทผู้เรียก · ไม่ถูกลบ) — มี PV แล้ว ⇒ ซ่อมการผูกถ้าขาด แล้ว 409 · ยังไม่มี PV (แถวจองค้างจากคำขอที่ล้มก่อน PV) ⇒ คืนแถวให้ใช้ต่อ ·
    /// ไม่มี ⇒ null</summary>
    private async Task<PartnerPaymentPlan?> RefuseIfDuplicateAsync(Guid companyId, string key, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? lockTx, CancellationToken ct)
    {
        var existing = await _db.PartnerPaymentPlans.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.IdempotencyKey == key, ct);
        if (existing == null) return null;
        var pv = existing.BuyerPaymentId ?? await PlanPaymentByReferenceAsync(_db, companyId, existing.Id, ct);
        if (pv == null) return existing;
        if (existing.BuyerPaymentId == null)
        {
            existing.BuyerPaymentId = pv;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        if (lockTx != null) await lockTx.RollbackAsync(CancellationToken.None);
        throw new BusinessRuleException(PartnerPaymentPlanRule.DuplicateTransferPrefix
            + $"บันทึกไว้เมื่อ {existing.CreatedAt.AddHours(7):dd/MM/yyyy HH:mm} — ดูที่ “ที่ทำไปแล้ว”", PartnerPaymentPlanRule.RuleCode, 409);
    }

    /// <summary>PV ของแผนจากเลขอ้างอิง canonical (บริษัทผู้เรียก · ยังไม่ถูกลบ) — ทางสำรองเมื่อ <c>BuyerPaymentId</c> ยังไม่ถูกผูก (M2)</summary>
    internal static Task<Guid?> PlanPaymentByReferenceAsync(AccountingDbContext db, Guid companyId, Guid planId, CancellationToken ct)
    {
        var reference = PartnerPaymentPlanRule.BuyerReference(planId);
        return db.Payments.AsNoTracking().Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference == reference).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>สลิปของเราเองแนบกับใบซื้อ (หลักฐานประกอบการจ่าย — ม.14) · idempotent ต่อที่เก็บไฟล์ · ไม่ SaveChanges (ผู้เรียก)</summary>
    private async Task EnsureOwnAttachmentAsync(Guid companyId, PartnerPaymentPlan p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.SlipStoragePath)) return;
        if (await _db.Set<FileAttachment>().AnyAsync(f => f.CompanyId == companyId && f.EntityId == p.PurchaseDocumentId && f.StoragePath == p.SlipStoragePath, ct)) return;
        var size = (await Storage.ReadAllBytesAsync(p.SlipStoragePath, ct))?.LongLength ?? 0L;
        var ext = System.IO.Path.GetExtension(p.SlipStoragePath);
        _db.Set<FileAttachment>().Add(new FileAttachment
        {
            CompanyId = companyId, FileName = System.IO.Path.GetFileName(p.SlipStoragePath), OriginalFileName = "slip" + ext, ContentType = p.SlipContentType ?? "",
            FileSize = size, StoragePath = p.SlipStoragePath, EntityType = "Document", EntityId = p.PurchaseDocumentId, UploadedByUserId = p.ChosenByUserId,
            CreatedBy = "partner-transfer-own",
        });
    }

    // ═══════════════ M2 outbox — สำเนาสลิปเข้าคิวรอยืนยันของผู้ขาย (H2: ความยินยอม AcceptPayerSlipCopy ของผู้ขาย) ═══════════════

    public sealed record SlipCopyOutcome(string State, string Note);

    /// <summary>ส่งสำเนาสลิปของแผนแจ้งโอนหนึ่งแถว (ผู้เรียก: <see cref="NotifyTransferAsync"/> ทันทีหลัง PV · <see cref="DeliverPendingSlipCopiesAsync"/> งานเบื้องหลัง) ·
    /// ล็อกต่อแผน + ความยินยอมของผู้ขายตรวจเพื่อเขียนในธุรกรรมที่ถือจนส่งเสร็จ · ไม่ยินยอม ⇒ Skipped + เหตุมุมผู้ซื้อ + กระดิ่งถึงผู้ขาย (M7) ·
    /// ล้มชั่วคราว ⇒ คง Pending ลองต่อ (≤ <see cref="PartnerPaymentPlanRule.MaxCopyAttempts"/>) · ฝั่งผู้ขายเป็นรายการรอยืนยันเท่านั้น (ไม่ใช่เงินเข้า)</summary>
    public async Task<SlipCopyOutcome> DeliverSlipCopyAsync(Guid companyId, Guid planId, DateTime nowUtc, CancellationToken ct = default)
    {
        using var lockScope = _scopes.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
        await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
        await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
        await lockDb.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", new object[] { "partner-slip-copy:" + planId.ToString("N") }, ct);
        var p = await _db.PartnerPaymentPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == planId && x.CompanyId == companyId, ct);
        if (p == null || p.SlipCopyState != PartnerPaymentPlanRule.CopyPending)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            return new SlipCopyOutcome(p?.SlipCopyState ?? PartnerPaymentPlanRule.CopyFailed, p?.SlipCopyNote ?? "ไม่พบแผน");
        }
        var pv = p.BuyerPaymentId ?? await PlanPaymentByReferenceAsync(_db, companyId, p.Id, ct);
        if (pv == null)
        {
            // ยังไม่มี PV (แถวจองที่คำขอล้มก่อนบันทึกจ่าย) ⇒ ไม่ส่งสลิปของการจ่ายที่ไม่มีอยู่
            await lockTx.RollbackAsync(CancellationToken.None);
            return new SlipCopyOutcome(PartnerPaymentPlanRule.CopyPending, p.SlipCopyNote ?? PartnerPaymentPlanRule.SlipCopyPendingNote);
        }
        if (p.BuyerPaymentId == null)
            await _db.PartnerPaymentPlans.Where(x => x.Id == p.Id && x.CompanyId == companyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.BuyerPaymentId, pv), ct);
        p.BuyerPaymentId = pv;
        try { await EnsureOwnAttachmentAsync(companyId, p, ct); await _db.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is DbUpdateException or IOException) { _db.ChangeTracker.Clear(); _logger.LogError(ex, "ผูกไฟล์สลิปของแผน {Plan} กับใบซื้อไม่สำเร็จ", p.Id); }

        string state, note;
        Guid? sellerIntent = null;
        var consent = await consentSvc.CheckForWriteAsync(p.SourceCompanyId, p.TradingPartnershipId, Scope.AcceptPayerSlipCopy, null, ct);
        if (!consent.Ok)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            state = PartnerPaymentPlanRule.CopySkipped;
            note = PartnerPaymentPlanRule.SellerNotAcceptingNoteForBuyer(consent.Code);
            if (PartnerPaymentPlanRule.SellerShouldBeNotified(consent.Code)) await NotifySellerConsentNeededAsync(p, consent.Code, consent.Reason, nowUtc, ct);
        }
        else if (_slips == null)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            (state, note) = Retry(p, "ระบบรับสลิปของผู้ขายยังไม่พร้อม");
        }
        else
        {
            byte[]? bytes = null;
            try { bytes = await Storage.ReadAllBytesAsync(p.SlipStoragePath, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException) { _logger.LogError(ex, "อ่านไฟล์สลิปของแผน {Plan} ไม่สำเร็จ", p.Id); }
            if (bytes == null || bytes.Length == 0)
            {
                await lockTx.RollbackAsync(CancellationToken.None);
                (state, note) = Retry(p, "อ่านไฟล์สลิปจากที่เก็บไม่ได้");
            }
            else
            {
                try
                {
                    var r = await _slips.SubmitAsync(p.SourceCompanyId, p.SourceDocumentId, null, new SlipFile(bytes, p.SlipContentType ?? "", "slip" + System.IO.Path.GetExtension(p.SlipStoragePath)),
                        p.Amount, p.SlipBankRef, SlipEvidenceService.ChannelPartnerNextAcc, ct);
                    sellerIntent = r.Intent.Id;
                    state = PartnerPaymentPlanRule.CopySent;
                    note = PartnerPaymentPlanRule.SlipCopySent;
                    await lockTx.CommitAsync(ct);
                }
                catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException or ArgumentException)
                {
                    // ผู้ขายปฏิเสธ (สลิปซ้ำ · เอกสารปิด ฯลฯ) = ผลถาวร
                    await lockTx.RollbackAsync(CancellationToken.None);
                    _db.ChangeTracker.Clear();
                    state = PartnerPaymentPlanRule.CopyFailed;
                    note = PartnerPaymentPlanRule.SlipCopyFailedPrefix + ex.Message;
                }
                catch (Exception ex) when (ex is DbUpdateException or IOException or HttpRequestException)
                {
                    await lockTx.RollbackAsync(CancellationToken.None);
                    _db.ChangeTracker.Clear();
                    _logger.LogError(ex, "ส่งสำเนาสลิปของแผน {Plan} ให้ผู้ขาย {Seller} ล้ม — ลองต่อรอบถัดไป", p.Id, p.SourceCompanyId);
                    (state, note) = Retry(p, "ระบบของผู้ขายรับไม่สำเร็จชั่วคราว");
                }
            }
        }
        var n500 = note.Length > 500 ? note[..500] : note;
        await _db.PartnerPaymentPlans.Where(x => x.Id == p.Id && x.CompanyId == companyId && x.SlipCopyState == PartnerPaymentPlanRule.CopyPending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipCopyState, state).SetProperty(x => x.SlipCopyNote, n500)
                .SetProperty(x => x.SellerIntentId, x => sellerIntent ?? x.SellerIntentId)
                .SetProperty(x => x.SlipCopyAttempts, x => x.SlipCopyAttempts + 1).SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (state != PartnerPaymentPlanRule.CopyPending)
        {
            AuditPlan(_db, companyId, p.ChosenByUserId, "PartnerTransferSlipCopy", p, new { state, consentCode = consent.Code, sellerIntentId = sellerIntent });
            await _db.SaveChangesAsync(ct);
        }
        return new SlipCopyOutcome(state, n500);
    }

    private static (string State, string Note) Retry(PartnerPaymentPlan p, string why)
        => p.SlipCopyAttempts + 1 >= PartnerPaymentPlanRule.MaxCopyAttempts
            ? (PartnerPaymentPlanRule.CopyFailed, PartnerPaymentPlanRule.SlipCopyFailedPrefix + why + " (ลองครบแล้ว) — แจ้งผู้ขายเองพร้อมสลิป")
            : (PartnerPaymentPlanRule.CopyPending, PartnerPaymentPlanRule.SlipCopyPendingNote + " — " + why);

    /// <summary>ฝ่ายค้าน M7: ผู้ขายต้องให้/ยืนยันความยินยอมรับสลิป ⇒ กระดิ่งถึงบริษัทผู้ขาย (ข้อความมุมผู้ขาย · ลิงก์หน้างานอัตโนมัติ) ครั้งเดียวต่อคู่ค้าต่อ 7 วัน ·
    /// ล้ม = LogError (สลิปของผู้ซื้อยังมีเหตุบนแผน)</summary>
    private async Task NotifySellerConsentNeededAsync(PartnerPaymentPlan p, string code, string reason, DateTime nowUtc, CancellationToken ct)
    {
        if (_notify == null) return;
        var since = nowUtc - PartnerPaymentPlanRule.SellerNoticeInterval;
        if (await _db.PartnerPaymentPlans.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(x => x.TradingPartnershipId == p.TradingPartnershipId && x.SourceCompanyId == p.SourceCompanyId && x.SellerNotifiedAt >= since, ct)) return;
        var buyer = await _db.Companies.AsNoTracking().Where(x => x.Id == p.CompanyId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "คู่ค้า";
        try
        {
            await _notify.DispatchAsync(p.SourceCompanyId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = $"{buyer} แจ้งโอนพร้อมสลิปแต่ระบบของเรายังไม่รับสลิปจากคู่ค้า",
                Message = $"เหตุ: {reason} · ยอด {p.Amount:N2} บาท — เปิดรับที่ “งานอัตโนมัติกับคู่ค้า” (ขอบเขต “รับสลิปที่คู่ค้าแนบตอนแจ้งโอน”) แล้วสลิปถัดไปจะเข้าคิวรอยืนยันรับเงิน · "
                    + "สลิปรายการนี้ให้ขอจากคู่ค้าโดยตรง",
                ActionUrl = "/pages/partner-automation.html",
                EntityType = nameof(Document), EntityId = p.SourceDocumentId,
            });
            await _db.PartnerPaymentPlans.Where(x => x.Id == p.Id && x.CompanyId == p.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SellerNotifiedAt, (DateTime?)nowUtc), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งผู้ขาย {Seller} ให้เปิดรับสลิปจากคู่ค้า ({Code}) ไม่สำเร็จ", p.SourceCompanyId, code);
        }
    }

    /// <summary>งานเบื้องหลัง (AutopilotJob ขั้น PartnerSlipCopies): แผนแจ้งโอนที่สำเนาสลิปยังค้าง Pending (ทุกบริษัท · batch · เก่าก่อน) ⇒ <see cref="DeliverSlipCopyAsync"/></summary>
    public async Task<int> DeliverPendingSlipCopiesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var before = nowUtc.AddMinutes(-1);   // คำขอที่เพิ่งบันทึกส่งเองก่อน
        var rows = await _db.PartnerPaymentPlans.AsNoTracking().IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && x.Kind == PartnerPaymentPlanRule.KindTransfer && x.SlipCopyState == PartnerPaymentPlanRule.CopyPending && x.CreatedAt < before)
            .OrderBy(x => x.UpdatedAt ?? x.CreatedAt).Select(x => new { x.Id, x.CompanyId }).Take(SlipCopyBatch).ToListAsync(ct);
        var done = 0;
        foreach (var r in rows)
            if ((await DeliverSlipCopyAsync(r.CompanyId, r.Id, nowUtc, ct)).State != PartnerPaymentPlanRule.CopyPending) done++;
        return done;
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
