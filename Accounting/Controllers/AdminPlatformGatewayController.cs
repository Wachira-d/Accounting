using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — หน้าแอดมิน <b>"ช่องทางรับชำระของ NextAcc"</b> (<c>/admin/platform-gateway.html</c>):
/// คีย์ Omise (เข้ารหัสด้วย <see cref="ISecretProtector"/>) · ทดสอบคีย์ทั้งสองชุด · ทดลองจ่าย ฿20 + รอ webhook · URL webhook ·
/// เปิดรับเงินจริง (ด่าน <see cref="PaymentLiveGate.PlatformRefusal"/>: ทดสอบผ่านหลังแก้คีย์ + คีย์ live ผ่าน + นโยบายคืนเงินเผยแพร่แล้ว + พิมพ์ยืนยัน) ·
/// ปิดฉุกเฉิน · โหมดหน้าทดลองจ่าย · ทุกการเปลี่ยนเข้า audit hash chain
/// <para>เก็บใน <c>PaymentProviderConfig</c> ของบริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) — <b>ไม่มีตาราง config ที่สอง</b> ·
/// งานคีย์/ทดสอบ/โหมดผ่าน <see cref="IPaymentProviderConfigService"/> ตัวเดียวกับหน้าตั้งค่าของร้าน</para>
/// </summary>
[ApiController]
[Route("api/admin/platform-gateway")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformGatewayController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentProviderConfigService _configs;
    private readonly IPlatformPaymentService _platform;
    private readonly IPaymentIntentService _intents;
    private readonly ILegalDocumentService _legal;
    private readonly ISecretProtector _secrets;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IGatewayAccountResolver? _accounts;
    private readonly IConfiguration? _config;

    public AdminPlatformGatewayController(AccountingDbContext db, IPaymentProviderConfigService configs, IPlatformPaymentService platform,
        IPaymentIntentService intents, ILegalDocumentService legal, ISecretProtector secrets, IEnumerable<IPaymentProvider> providers,
        IGatewayAccountResolver? accounts = null, IConfiguration? config = null)
    {
        _db = db; _configs = configs; _platform = platform; _intents = intents; _legal = legal; _secrets = secrets; _providers = providers;
        _accounts = accounts; _config = config;
    }

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT C-1..C-5): ข้อเท็จจริงของรายการตรวจความพร้อม (บัญชี · URL ระบบ · วิธีชำระ) — ตัวเดียวของ GET และด่านเปิดรับเงินจริง</summary>
    private async Task<PlatformGatewayReadinessFacts> ReadinessFactsAsync(SiteSettings? settings, Guid? platformId, PaymentProviderConfig? cfg,
        int payableMethods, CancellationToken ct)
    {
        var revenueCode = string.IsNullOrWhiteSpace(settings?.PlatformRevenueAccountCode) ? "41000" : settings!.PlatformRevenueAccountCode!.Trim();
        var cashCode = settings?.PlatformCashAccountCode?.Trim();
        Task<bool> FoundAsync(Guid pid, string code) => _db.ChartOfAccounts.AsNoTracking()
            .AnyAsync(a => a.CompanyId == pid && a.AccountCode == code && a.IsActive && !a.IsDeleted, ct);
        var clearing = platformId is Guid p1 && cfg != null && _accounts != null
            && await _accounts.ResolveClearingAccountAsync(p1, cfg.ProviderCode, cfg.Id, ct) != null;
        var fee = platformId is Guid p2 && cfg != null && _accounts != null
            && await _accounts.ResolveFeeExpenseAccountAsync(p2, cfg.ProviderCode, cfg.Id, ct) != null;
        var sellerTax = settings?.PlatformSellerTaxId?.Trim();
        return new PlatformGatewayReadinessFacts(
            SystemBaseUrl: SystemBaseUrl.IsConfigured(settings?.AppBaseUrl, _config?["App:BaseUrl"])
                ? SystemBaseUrl.Pick(settings?.AppBaseUrl, _config?["App:BaseUrl"]) : null,
            ClearingAccountFound: clearing,
            FeeExpenseAccountFound: fee,
            RevenueAccountCode: revenueCode,
            RevenueAccountFound: platformId is Guid p3 && await FoundAsync(p3, revenueCode),
            CashAccountCode: string.IsNullOrWhiteSpace(cashCode) ? null : cashCode,
            CashAccountFound: platformId is Guid p4 && !string.IsNullOrWhiteSpace(cashCode) && await FoundAsync(p4, cashCode!),
            VatRegistered: settings?.PlatformIsVatRegistered == true,
            SellerTaxIdValid: ThaiTaxId.IsValid(sellerTax),   // ฝ่ายค้าน N3: ตัวตรวจเดียว (checksum mod-11)
            BankTransferInstructionsSet: !string.IsNullOrWhiteSpace(settings?.PlatformBankTransferInstructions),
            PayableMethodCount: payableMethods,
            PriceIncludesVat: settings?.PlatformPriceIncludesVat ?? true);
    }

    private PaymentConfigActor Actor() => new(User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    private string? Hint(string? protectedValue)
    {
        var v = _secrets.Unprotect(protectedValue);
        return string.IsNullOrEmpty(v) ? null : "…" + v[^Math.Min(4, v.Length)..];
    }

    private static List<string> ParseMethods(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    /// <summary>สถานะทั้งหมดของหน้า — เซิร์ฟเวอร์ตัดสินทุกป้าย/ปุ่ม (หน้าเว็บแสดงอย่างเดียว)</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var settings = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var platformId = settings?.PlatformCompanyId;
        var platformName = platformId is Guid pid
            ? await _db.Companies.AsNoTracking().Where(c => c.Id == pid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var cfg = await _platform.PlatformConfigAsync(ct);
        var purchase = await _legal.GetPurchasePolicyVersionAsync(ct);
        var status = await _platform.StatusAsync(ct);
        var facts = cfg == null ? null : _configs.GateFacts(cfg);
        var missing = facts == null ? new List<string>() : PaymentLiveGate.Missing(facts);
        if (!purchase.RefundPublished)
            missing.Add("ยังไม่ได้เผยแพร่ \"นโยบายการยกเลิกและคืนเงิน\" ฉบับของ NextAcc (เอกสารกฎหมาย → นโยบายการยกเลิกและคืนเงิน)");
        // รอบ 227: รายการตรวจความพร้อม (บัญชี · URL ระบบ · วิธีชำระ) — ข้อที่ด่านเปิดรับเงินจริงใช้ (Blocking) รวมเข้า liveGateMissing ด้วย
        var readiness = PlatformGatewayReadiness.Checks(await ReadinessFactsAsync(settings, platformId, cfg, status.Methods.Count, ct));
        if (cfg != null) missing.AddRange(PlatformGatewayReadiness.BlockingMissing(readiness));
        var provider = cfg == null ? null : _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        var configured = PlatformPaymentService.ConfiguredMethods(cfg?.EnabledMethodsJson);
        var testMode = PlatformPayTestPage.Normalize(settings?.PlatformPayTestPageMode);
        return Ok(new ApiResponse<object>(true, new
        {
            platformCompanyId = platformId,
            platformCompanyName = platformName,
            providers = _providers.Where(p => p.ProviderCode != Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code)
                .Select(p => new
                {
                    code = p.ProviderCode,
                    methods = p.Capabilities.Methods.Where(m => m != PaymentMethodKind.ManualSlip).Select(m => m.ToString()).ToList(),
                    // รอบ 227 (B-2): ป้ายไทย + หน้าชำระของ NextAcc ทำจบได้ไหม (เหตุผล) — เซิร์ฟเวอร์ตัดสิน (PlatformPayMethods)
                    methodInfo = p.Capabilities.Methods.Where(m => m != PaymentMethodKind.ManualSlip).Select(m => new
                    {
                        code = m.ToString(), label = PlatformPayMethods.Label(m), payable = PlatformPayMethods.IsPayable(m),
                        note = PlatformPayMethods.NotPayableReason(m),
                    }).ToList(),
                })
                .ToList(),
            config = cfg == null ? null : new
            {
                cfg.Id, cfg.ProviderCode, cfg.DisplayName, mode = cfg.Mode.ToString(), cfg.IsActive,
                cfg.TestPublicKey, cfg.LivePublicKey,
                testSecretHint = Hint(cfg.TestSecretKeyProtected), liveSecretHint = Hint(cfg.LiveSecretKeyProtected),
                cfg.LastTestPassedAt, cfg.LiveKeyVerifiedAt, cfg.LastWebhookAt, cfg.LastLiveWebhookAt, cfg.LastSelfTestPaidAt, cfg.LiveEnabledAt,
                cfg.PlatformLiveConfirmedAt,   // ฝ่ายค้าน S1: ผ่านด่านแพลตฟอร์มแล้ว (ผู้เช่าเห็นปุ่มออนไลน์เฉพาะเมื่อมีค่านี้)
                // รอบ 232: origin จาก URL ของระบบที่ตั้งไว้ (SystemBaseUrl.PublicOrigin) — Request.Host หลัง reverse proxy เป็นชื่อภายใน ⇒ URL ที่ผู้ให้บริการเรียกไม่ถึง
                webhookUrl = SystemBaseUrl.PublicOrigin(SystemBaseUrl.Pick(settings?.AppBaseUrl, _config?["App:BaseUrl"]), Request.Scheme, Request.Host.ToString())
                    + GatewayWebhookRoute.Path(cfg.ProviderCode, cfg.WebhookToken),
                enabledMethods = ParseMethods(cfg.EnabledMethodsJson),
                // รอบ 227: วิธีที่ผู้เช่าเห็นจริง (ติ๊ก ∩ รองรับ ∩ หน้าชำระทำจบได้) — ไม่ติ๊กเลย = ทุกวิธีที่ใช้ได้
                effectiveMethods = provider == null ? new List<string>() : PlatformPayMethods.Effective(provider.Capabilities.Methods, configured,
                    cardClientReady: PaymentClientSetup.For(provider, cfg) != null),
                // รอบ 228 (§5.1): รับบัตรในเบราว์เซอร์ได้ไหม (มีคีย์สาธารณะของโหมดนี้) — ไม่ได้ ⇒ เหตุผล (ตัวเดียว PlatformPayMethods)
                cardNote = provider == null ? null : PlatformPayMethods.NotPayableReason(PaymentMethodKind.Card,
                    cardClientReady: PaymentClientSetup.For(provider, cfg) != null),
                providerNotice = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode)?.PendingVerificationNotice,
                liveWebhookMissing = cfg.Mode == PaymentProviderMode.Live && cfg.LastLiveWebhookAt == null,
            },
            canEnableLive = missing.Count == 0 && cfg != null,
            liveGateMissing = missing,
            confirmPhrase = PaymentLiveGate.ConfirmPhrase,
            refundPolicy = new { version = purchase.RefundVersion, published = purchase.RefundPublished,
                url = LegalDocumentDefaults.PlatformPath(LegalDocumentDefaults.KindRefund) },
            online = status.Online,
            offlineReason = status.OfflineReason,
            testPage = new { mode = testMode, modes = PlatformPayTestPage.Modes, url = "/platform-pay-test.html" },
            selfTestAmount = PaymentLiveGate.SelfTestAmount,
            readiness = readiness.Select(r => new { r.Key, r.Ok, r.Blocking, r.Text }).ToList(),
        }));
    }

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT C-6): รายการรับชำระค่าบริการออนไลน์ล่าสุดของ NextAcc — มุมมองเดียวที่เทียบ "ผู้ให้บริการ ↔ ระบบ" ได้:
    /// สถานะ · เลขอ้างอิงผู้ให้บริการ · ค่าธรรมเนียม · ผู้จ่าย · ต้นทาง (เลขรายการชำระ/ส่วนเสริม) · ใบเสร็จที่ออก · เข้ารอบโอนแล้วไหม · เงินเข้าแต่ต้นทางยังไม่บันทึก (⚠️) ·
    /// อ่านอย่างเดียว (งานแก้ = หน้ารายการรับชำระออนไลน์ของบริษัทผู้ให้บริการ / ศูนย์บิลแอดมิน)</summary>
    [HttpGet("intents")]
    public async Task<ActionResult<ApiResponse<object>>> RecentIntents([FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid)
            return Ok(new ApiResponse<object>(true, new { items = Array.Empty<object>() }));
        take = Math.Clamp(take, 1, 200);
        // ฝ่ายค้านรอบ 229 S5: ชุดเดียวกับ PlatformRevenueSource.Kinds (เดิมเขียนซ้ำเอง ⇒ โควตาเสริมหายจากหน้านี้) + ทดลองจ่าย
        var kinds = PlatformRevenueSource.Kinds.Append(PaymentSourceKind.GatewaySelfTest).ToList();
        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == pid && kinds.Contains(i.SourceKind))
            .OrderByDescending(i => i.CreatedAt).Take(take)
            .Select(i => new
            {
                i.Id, i.CreatedAt, i.ConfirmedAt, i.SourceKind, i.SourceId, i.PayerCompanyId, i.Amount, i.FeeActual, i.Status, i.IsTestMode,
                i.ProviderRef, i.MethodKind, i.FailureMessage, i.QrExpiresAt,
                InSettlement = i.SettlementBatchId != null || i.SettlementJournalEntryId != null,
                i.DisputeState, i.DisputeAmount, i.DisputeReason, i.DisputeUpdatedAt, i.DisputeAcknowledgedAt, i.RefundedAmount,
                i.RefundCreditNoteProblem, i.RefundCreditNotePendingAmount,
            })
            .ToListAsync(ct);
        var payerIds = rows.Where(r => r.PayerCompanyId != null).Select(r => r.PayerCompanyId!.Value).Distinct().ToList();
        var payers = await _db.Companies.AsNoTracking().IgnoreQueryFilters().Where(c => payerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var subIds = rows.Where(r => r.SourceKind == PaymentSourceKind.SubscriptionPayment).Select(r => r.SourceId).ToList();
        var subs = await _db.SubscriptionPayments.AsNoTracking().IgnoreQueryFilters().Where(p => subIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PaymentNumber, p.ReceiptNumber, p.Status, p.PlatformDocumentId }).ToDictionaryAsync(p => p.Id, ct);
        var addOnIds = rows.Where(r => r.SourceKind == PaymentSourceKind.AddOnPurchase).Select(r => r.SourceId).ToList();
        var addOns = await _db.CompanyFeatures.AsNoTracking().IgnoreQueryFilters().Where(f => addOnIds.Contains(f.Id))
            .Select(f => new { f.Id, f.FeatureCode, f.PaymentReceiptNumber, f.PaymentIntentId }).ToDictionaryAsync(f => f.Id, ct);
        var quotaIds = rows.Where(r => r.SourceKind == PaymentSourceKind.QuotaAddOnPurchase).Select(r => r.SourceId).ToList();
        var quotas = await _db.CompanyQuotaAddOns.AsNoTracking().IgnoreQueryFilters().Where(q => quotaIds.Contains(q.Id))
            .Select(q => new { q.Id, q.ResourceKey, q.ReceiptNumber, q.PaymentIntentId }).ToDictionaryAsync(q => q.Id, ct);
        var redispatch = await _intents.RedispatchStatesAsync(pid, rows.Select(r => r.Id).ToList(), ct);
        var items = rows.Select(r =>
        {
            subs.TryGetValue(r.SourceId, out var sp);
            addOns.TryGetValue(r.SourceId, out var ao);
            quotas.TryGetValue(r.SourceId, out var qa);
            var receipt = sp?.ReceiptNumber ?? (ao != null && ao.PaymentIntentId == r.Id ? ao.PaymentReceiptNumber : null)
                ?? (qa != null && qa.PaymentIntentId == r.Id ? qa.ReceiptNumber : null);
            // ฝ่ายค้าน S3: เงินจริงเข้าแล้วแต่ไม่มีใบ ⇒ ติดธง + ปุ่มออกใบอีกครั้ง (ตัวตัดสินเดียว PlatformReceiptGap)
            var receiptGap = PlatformReceiptGap.Reason(r.SourceKind, r.Status, PaymentIntentTestMode.Decide(r.IsTestMode, null),
                receipt != null || sp?.PlatformDocumentId != null);
            var unfulfilled = redispatch.TryGetValue(r.Id, out var st) && st == PaymentRedispatchState.Pending;
            return new
            {
                id = r.Id, createdAt = r.CreatedAt, confirmedAt = r.ConfirmedAt,
                kind = r.SourceKind.ToString(), kindLabel = PaymentIntentPolicy.SourceKindLabel(r.SourceKind),
                payer = r.PayerCompanyId is Guid p && payers.TryGetValue(p, out var n) ? n : null,
                reference = sp?.PaymentNumber ?? ao?.FeatureCode ?? (qa != null ? "quota." + qa.ResourceKey : null),
                receiptNumber = receipt,
                amount = r.Amount, fee = r.FeeActual,
                status = r.Status.ToString(), statusLabel = TestModePaymentPolicy.StatusLabel(r.Status, PaymentIntentTestMode.Decide(r.IsTestMode, null)),
                method = PlatformPayMethods.Label(r.MethodKind),
                isTestMode = PaymentIntentTestMode.Decide(r.IsTestMode, null),
                providerRef = r.ProviderRef, failureMessage = r.FailureMessage,
                inSettlement = r.InSettlement,
                // เงินเข้าแล้วแต่ต้นทางยังไม่บันทึก (ส่งต่อไม่สำเร็จ/ยอดไม่ตรง/จ่ายซ้อน) — ตัวตัดสินเดียวกับปุ่ม "ส่งต่อใหม่"
                // รอบ 228 (§5.4): ข้อพิพาท/chargeback จาก webhook ที่ยืนยันแล้ว — ป้าย/ทางไปต่อจากตัวตัดสินเดียว PaymentDisputeRules
                dispute = r.DisputeState == null ? null : new
                {
                    state = r.DisputeState.ToString(), label = PaymentDisputeRules.Label(r.DisputeState), amount = r.DisputeAmount,
                    reason = r.DisputeReason, updatedAt = r.DisputeUpdatedAt, acknowledgedAt = r.DisputeAcknowledgedAt,
                    nextStep = PaymentDisputeRules.NextStep(r.DisputeState),
                },
                refundedAmount = r.RefundedAmount,
                // ฝ่ายค้าน S2/S5: เรื่องค้างทุกชนิดผ่านตัวตัดสินเดียว (ส่งต่อ · ใบเสร็จขาด · ใบลดหนี้ค้าง · ข้อพิพาทที่ยังไม่รับทราบ)
                needsAttention = unfulfilled || receiptGap != null || r.RefundCreditNoteProblem != null
                    || PaymentDisputeRules.NeedsAttention(r.DisputeState, r.DisputeUpdatedAt, r.DisputeAcknowledgedAt),
                attentionReason = unfulfilled ? "เงินเข้าแล้วแต่ต้นทางยังไม่บันทึก — ส่งต่อใหม่ที่หน้ารายการรับชำระออนไลน์ของบริษัทผู้ให้บริการ"
                    : receiptGap
                      ?? PlatformReceiptGap.CreditNoteReason(r.RefundCreditNoteProblem, r.RefundCreditNotePendingAmount)
                      ?? (PaymentDisputeRules.NeedsAttention(r.DisputeState, r.DisputeUpdatedAt, r.DisputeAcknowledgedAt)
                        ? $"{PaymentDisputeRules.Label(r.DisputeState)} — {PaymentDisputeRules.NextStep(r.DisputeState)}" : null),
                canReissueCreditNote = r.RefundCreditNoteProblem != null,
                canAcknowledgeDispute = PaymentDisputeRules.NeedsAttention(r.DisputeState, r.DisputeUpdatedAt, r.DisputeAcknowledgedAt),
                canReissueReceipt = !unfulfilled && receiptGap != null,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, new { items }));
    }

    public sealed record ReissueRequest(string? Reason);

    /// <summary>ฝ่ายค้านรอบ 227 S3: "ออกใบเสร็จอีกครั้ง" ของรายการค่าบริการออนไลน์ที่เงินเข้าแล้วแต่ไม่มีใบ — เรียกตัวออกเอกสารตัวเดิม (idempotent ด้วยเลขอ้างอิงเดิม) ·
    /// SystemAdmin (ทั้ง controller) · audit chain ทุกครั้ง · ไม่ขาด/ทดสอบ/ยังไม่สำเร็จ ⇒ 409</summary>
    [HttpPost("intents/{intentId:guid}/reissue-receipt")]
    public async Task<ActionResult<ApiResponse<object>>> ReissueReceipt(Guid intentId, [FromBody] ReissueRequest? req,
        [FromServices] ISaasBillingDocumentService billing, [FromServices] IAddOnPurchaseService addons,
        [FromServices] IQuotaAddOnPurchaseService quotaAddOns, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ"));
        var i = await _db.PaymentIntents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == intentId && x.CompanyId == pid, ct);
        if (i == null || i.PayerCompanyId is not Guid payer) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ"));
        string? receipt = null;
        Guid? platformDoc = null;
        if (i.SourceKind == PaymentSourceKind.SubscriptionPayment)
        {
            var sp = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.Id == i.SourceId && p.Subscription.CompanyId == payer)
                .Select(p => new { p.ReceiptNumber, p.PlatformDocumentId }).FirstOrDefaultAsync(ct);
            receipt = sp?.ReceiptNumber; platformDoc = sp?.PlatformDocumentId;
        }
        else if (i.SourceKind == PaymentSourceKind.AddOnPurchase)
            receipt = await _db.CompanyFeatures.AsNoTracking().Where(f => f.Id == i.SourceId && f.CompanyId == payer && f.PaymentIntentId == i.Id)
                .Select(f => f.PaymentReceiptNumber).FirstOrDefaultAsync(ct);
        else if (i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase)
            receipt = await _db.CompanyQuotaAddOns.AsNoTracking().Where(q => q.Id == i.SourceId && q.CompanyId == payer && q.PaymentIntentId == i.Id)
                .Select(q => q.ReceiptNumber).FirstOrDefaultAsync(ct);
        var gap = PlatformReceiptGap.Reason(i.SourceKind, i.Status, PaymentIntentTestMode.Decide(i.IsTestMode, null), receipt != null || platformDoc != null);
        if (gap == null) return StatusCode(409, new ApiResponse<object>(false, null, "รายการนี้ไม่ต้องออกใบเสร็จเพิ่ม (มีใบแล้ว · โหมดทดสอบ · หรือยังไม่ได้รับเงิน)"));

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = pid, EntityType = nameof(PaymentIntent), EntityId = i.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = PlatformReceiptGap.AuditAction, kind = i.SourceKind.ToString(), i.SourceId, i.Amount, reason = req?.Reason?.Trim() }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        if (i.SourceKind == PaymentSourceKind.SubscriptionPayment)
        {
            // เส้นเดิมของการอนุมัติ: ผูกใบแจ้งหนี้ ⇒ ตัดชำระใบเดิม + ใบเสร็จ · ไม่ผูก ⇒ ใบเสร็จขายเงินสด (ทั้งสองเส้น idempotent)
            var linked = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.Id == i.SourceId).Select(p => p.PlatformInvoiceDocumentId).FirstOrDefaultAsync(ct);
            if (linked != null) await billing.SettlePlatformInvoiceAsync(i.SourceId);
            else await billing.GenerateReceiptForApprovedPaymentAsync(i.SourceId);
            receipt = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.Id == i.SourceId).Select(p => p.ReceiptNumber).FirstOrDefaultAsync(ct);
        }
        else if (i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase)
            receipt = await quotaAddOns.ReissueGatewayReceiptAsync(payer, i.SourceId, i.Id, ct);
        else receipt = await addons.ReissueGatewayReceiptAsync(payer, i.SourceId, i.Id, ct);

        return receipt == null
            ? Ok(new ApiResponse<object>(false, null, "ยังออกใบไม่สำเร็จ — ดูสาเหตุในบันทึกตรวจสอบ/หมายเหตุบนใบร่างในบริษัทผู้ให้บริการ แล้วลองใหม่"))
            : Ok(new ApiResponse<object>(true, new { receiptNumber = receipt }, $"ออกใบแล้ว: {receipt}"));
    }

    /// <summary>รอบ 228 ฝ่ายค้าน S2: "ออกใบลดหนี้อีกครั้ง" ของการคืนเงินค่าบริการที่ออกอัตโนมัติไม่สำเร็จ — เลขกันซ้ำเดิม · SystemAdmin (ทั้ง controller) · audit chain</summary>
    [HttpPost("intents/{intentId:guid}/reissue-credit-note")]
    public async Task<ActionResult<ApiResponse<object>>> ReissueCreditNote(Guid intentId, [FromBody] ReissueRequest? req,
        [FromServices] IGatewayRefundService refunds, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ"));
        var r = await refunds.RetryPlatformCreditNoteAsync(pid, intentId, $"admin:{User?.Identity?.Name}", req?.Reason, ct);
        return r.Ok ? Ok(new ApiResponse<object>(true, null, r.Message)) : StatusCode(409, new ApiResponse<object>(false, null, r.Message));
    }

    /// <summary>รอบ 228 ฝ่ายค้าน S5: รับทราบ/จัดการข้อพิพาทของรายการค่าบริการแล้ว — SystemAdmin · audit chain</summary>
    [HttpPost("intents/{intentId:guid}/dispute/acknowledge")]
    public async Task<ActionResult<ApiResponse<object>>> AcknowledgeDispute(Guid intentId, [FromBody] ReissueRequest? req,
        [FromServices] IPaymentDisputeService disputes, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ"));
        return await disputes.AcknowledgeAsync(pid, intentId, $"admin:{User?.Identity?.Name}", req?.Reason, ct)
            ? Ok(new ApiResponse<object>(true, null, "บันทึกการรับทราบข้อพิพาทแล้ว"))
            : NotFound(new ApiResponse<object>(false, null, "ไม่พบข้อพิพาทของรายการนี้"));
    }

    public sealed record SaveRequest(string ProviderCode, string? DisplayName, string? TestPublicKey, string? TestSecretKey,
        string? LivePublicKey, string? LiveSecretKey, List<string>? EnabledMethods);

    /// <summary>บันทึกคีย์/วิธีชำระ — คีย์ลับที่เว้นว่าง = ไม่เปลี่ยน · แก้คีย์ใด ⇒ ต้องทดสอบใหม่ก่อนเปิดรับเงินจริง</summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<object>>> Save([FromBody] SaveRequest req, CancellationToken ct)
    {
        if (_providers.All(p => p.ProviderCode != req.ProviderCode) || req.ProviderCode == Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่รู้จักผู้ให้บริการนี้"));
        // รอบ 227 (B-3): ชื่อวิธีต้องเป็นวิธีที่ผู้ให้บริการรองรับ — เดิมเก็บสตริงอะไรก็ได้ (หน้าเว็บส่งผิด/เรียก API ตรง = ค่าที่ไม่มีผู้อ่าน)
        var providerForSave = _providers.First(p => p.ProviderCode == req.ProviderCode);
        if (req.EnabledMethods != null && PlatformPayMethods.Unknown(providerForSave.Capabilities.Methods, req.EnabledMethods) is { Count: > 0 } unknownMethods)
            return BadRequest(new ApiResponse<object>(false, null, "ผู้ให้บริการนี้ไม่รองรับวิธีชำระ: " + string.Join(", ", unknownMethods)));
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid)
            return BadRequest(new ApiResponse<object>(false, null,
                "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ตั้งที่ ตั้งค่าเว็บไซต์ → ใบเสร็จค่าบริการ ก่อน (เงินค่าบริการลงสมุดของบริษัทนั้น)"));
        var cfg = await _configs.FindAsync(pid, req.ProviderCode, ct);
        var created = cfg == null;
        if (cfg == null)
        {
            cfg = new PaymentProviderConfig { CompanyId = pid, ProviderCode = req.ProviderCode, Mode = PaymentProviderMode.Test, IsActive = true };
            _db.PaymentProviderConfigs.Add(cfg);
        }
        cfg.WebhookToken ??= GatewayWebhookRoute.NewToken();
        var before = new { cfg.DisplayName, methods = ParseMethods(cfg.EnabledMethodsJson), cfg.TestPublicKey, cfg.LivePublicKey, mode = cfg.Mode.ToString() };
        if (req.DisplayName != null) cfg.DisplayName = req.DisplayName.Trim();
        _configs.ApplyKeys(cfg, req.TestPublicKey, req.TestSecretKey, req.LivePublicKey, req.LiveSecretKey);
        if (req.EnabledMethods != null) cfg.EnabledMethodsJson = System.Text.Json.JsonSerializer.Serialize(req.EnabledMethods);
        cfg.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = pid,
            EntityType = nameof(PaymentProviderConfig),
            EntityId = cfg.Id.ToString(),
            Action = created ? AuditAction.Create : AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            // ไม่บันทึกตัวคีย์ลับ — บันทึกแค่ว่าช่องไหนถูกเปลี่ยน · รอบ 227: ค่าก่อนแก้ของช่องที่ไม่ใช่ความลับ (ชื่อ · วิธีชำระ · คีย์สาธารณะ · โหมด — คีย์จริงที่ถูกแก้ถอยโหมดอัตโนมัติ)
            OldValues = created ? null : System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "platform-gateway-save", provider = req.ProviderCode,
                testPublicKeyChanged = req.TestPublicKey != null, testSecretChanged = !string.IsNullOrWhiteSpace(req.TestSecretKey),
                livePublicKeyChanged = req.LivePublicKey != null, liveSecretChanged = !string.IsNullOrWhiteSpace(req.LiveSecretKey),
                methods = req.EnabledMethods, displayName = cfg.DisplayName, mode = cfg.Mode.ToString(),
                testPublicKey = cfg.TestPublicKey, livePublicKey = cfg.LivePublicKey,
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "บันทึกแล้ว — กด \"ทดสอบการเชื่อมต่อ\" ก่อนเปิดรับเงินจริง"));
    }

    private async Task<PaymentProviderConfig?> ConfigOrNullAsync(CancellationToken ct) => await _platform.PlatformConfigAsync(ct);

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> Test(CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        var r = await _configs.TestAsync(cfg, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            passed = r.Passed, liveKeyVerified = r.LiveKeyVerified,
            test = new { r.Test.KeysValid, r.Test.WebhookReceived, r.Test.Message },
            live = r.Live == null ? null : new { r.Live.KeysValid, r.Live.Message },
        }, r.Message));
    }

    public sealed record SelfTestRequest(string? ReturnUrl);

    /// <summary>ทดลองจ่าย ฿20 (โหมดทดสอบเท่านั้น) — หน้าแอดมินเปิดหน้าจ่ายกลาง (PayWidget) แล้ว poll <c>intents/{id}</c></summary>
    [HttpPost("self-test/intents")]
    public async Task<ActionResult<ApiResponse<object>>> SelfTest([FromBody] SelfTestRequest? req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        try { return Ok(new ApiResponse<object>(true, MapIntent(await _configs.StartSelfTestAsync(cfg, req?.ReturnUrl, ct)))); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("self-test/intents/{intentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> SelfTestStatus(Guid intentId, CancellationToken ct)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ"));
        var isSelfTest = await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.Id == intentId && i.CompanyId == pid && i.SourceKind == PaymentSourceKind.GatewaySelfTest, ct);
        if (!isSelfTest) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการทดลองจ่าย"));
        var i = await _intents.RefreshAsync(pid, intentId, ct);
        var cfg = await ConfigOrNullAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
            qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
            isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
            // webhook มาถึงหลังสร้างรายการนี้ไหม — ข้อที่ "ทดสอบเชื่อมต่อ" ต้องการ
            webhookReceivedSinceStart = cfg?.LastWebhookAt != null && cfg.LastWebhookAt >= i.CreatedAt,
        }));
    }

    public sealed record ModeRequest(PaymentProviderMode Mode, string? TypedConfirmation);

    [HttpPost("mode")]
    public async Task<ActionResult<ApiResponse<object>>> SetMode([FromBody] ModeRequest req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        var purchase = await _legal.GetPurchasePolicyVersionAsync(ct);
        // รอบ 227 (B-4 · ฝ่ายค้าน S4/S2): ด่านแพลตฟอร์มเพิ่ม "URL ของระบบตั้งแล้ว" + "มีวิธีชำระที่ทำจบได้" + ข้อที่ด่านใช้จากรายการตรวจความพร้อม
        // (บัญชีพักผู้ให้บริการรับชำระ · ราคารวม VAT) — ข้อเท็จจริงชุดเดียวกับการ์ดบนหน้า (ReadinessFactsAsync)
        var settings = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var payable = (await _platform.StatusAsync(ct)).Methods.Count;
        var readinessMissing = PlatformGatewayReadiness.BlockingMissing(PlatformGatewayReadiness.Checks(
            await ReadinessFactsAsync(settings, cfg.CompanyId, cfg, payable, ct)));
        await _configs.SetModeAsync(cfg, req.Mode, new PlatformLiveExtras(purchase.RefundPublished, req.TypedConfirmation,
            SystemBaseUrl.IsConfigured(settings?.AppBaseUrl, _config?["App:BaseUrl"]), payable, readinessMissing), Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, req.Mode == PaymentProviderMode.Live
            ? "เปิดรับเงินค่าบริการจริงแล้ว — ผู้เช่าเห็นปุ่ม \"ชำระออนไลน์\" ทันที · ตรวจว่ามีการแจ้งเตือนโหมดจริงมาถึงหลังรายการแรก"
            : "กลับสู่โหมดทดสอบแล้ว — ผู้เช่ากลับไปใช้เส้นโอน + แนบสลิป"));
    }

    public sealed record ActiveRequest(bool Active, string? Reason);

    /// <summary>ปิดฉุกเฉิน / เปิดใหม่ — ปิด ⇒ ปุ่มชำระออนไลน์ของผู้เช่าหายทันที (เส้นโอน + แนบสลิปยังใช้ได้) · ต้องระบุเหตุผลตอนปิด</summary>
    [HttpPost("active")]
    public async Task<ActionResult<ApiResponse<object>>> SetActive([FromBody] ActiveRequest req, CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        await _configs.SetActiveAsync(cfg, req.Active, req.Reason, Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, req.Active ? "เปิดช่องทางรับชำระแล้ว" : "ปิดช่องทางรับชำระชั่วคราวแล้ว — ผู้เช่าใช้เส้นโอน + แนบสลิป"));
    }

    [HttpPost("webhook-token/rotate")]
    public async Task<ActionResult<ApiResponse<object>>> RotateToken(CancellationToken ct)
    {
        var cfg = await ConfigOrNullAsync(ct);
        if (cfg == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้ตั้งค่าช่องทางรับชำระของ NextAcc"));
        cfg.LegacyWebhookEligible = GatewayWebhookRoute.LegacyEligibleAfterRotate(
            new GatewayWebhookConfigFacts(cfg.Id, cfg.WebhookToken, cfg.LastTokenWebhookAt, cfg.LastTokenWebhookMode, cfg.Mode, cfg.LegacyWebhookEligible));
        cfg.WebhookToken = GatewayWebhookRoute.NewToken();
        cfg.LastTokenWebhookAt = null;
        cfg.LastTokenWebhookMode = null;
        cfg.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = cfg.CompanyId, EntityType = nameof(PaymentProviderConfig), EntityId = cfg.Id.ToString(),
            Action = AuditAction.Update, UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "platform-rotate-webhook-token", provider = cfg.ProviderCode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ออกรหัสใหม่แล้ว — คัดลอก URL ใหม่ไปตั้งในแดชบอร์ดผู้ให้บริการทั้งโหมดทดสอบและโหมดใช้งานจริง"));
    }

    public sealed record TestPageRequest(string Mode);

    [HttpPut("test-page")]
    public async Task<ActionResult<ApiResponse<object>>> SetTestPage([FromBody] TestPageRequest req, CancellationToken ct)
    {
        if (!PlatformPayTestPage.Modes.Contains(req.Mode))
            return BadRequest(new ApiResponse<object>(false, null, "โหมดต้องเป็น Off · AdminsOnly · Public"));
        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<object>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));
        var before = PlatformPayTestPage.Normalize(s.PlatformPayTestPageMode);
        s.PlatformPayTestPageMode = req.Mode;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId, EntityType = nameof(SiteSettings), EntityId = s.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "platform-pay-test-page", from = before, to = req.Mode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, req.Mode switch
        {
            PlatformPayTestPage.Off => "ปิดหน้าทดลองจ่ายแล้ว",
            PlatformPayTestPage.Public => "เปิดหน้าทดลองจ่ายให้ทุกคน (ใช้คีย์โหมดทดสอบ — ไม่มีเงินจริง)",
            _ => "เปิดหน้าทดลองจ่ายเฉพาะผู้ดูแลระบบ",
        }));
    }

    private static object MapIntent(PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
        isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
    };
}
