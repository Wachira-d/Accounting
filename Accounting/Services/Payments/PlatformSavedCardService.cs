using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>บัตรที่บันทึกไว้ที่ใช้เริ่มรายการชำระ — เลขอ้างอิงของผู้ให้บริการเท่านั้น (ไม่ใช่เลขบัตร) · ผู้จ่าย/ผู้ให้บริการ/โหมด ให้ตัวตัดสินตรวจว่าใช้กับรายการนี้ได้</summary>
public sealed record PlatformCardRefs(Guid SavedCardId, Guid PayerCompanyId, string ProviderCode, bool IsTestMode, string CustomerRef, string CardRef);

/// <summary>ผู้กระทำ (ผู้ใช้ JWT) — อีเมล/IP/UA เข้าหลักฐานความยินยอม + audit</summary>
public sealed record PlatformCardActor(Guid? UserId, string? Email, string? Ip, string? UserAgent)
{
    public string Name => UserId?.ToString() ?? Email ?? "system";
}

public sealed record SavedCardView(Guid Id, string Label, string? Brand, string? Last4, DateTime? ValidUntil, bool IsDefault, bool Verified,
    bool IsTestMode, string? Problem, DateTime CreatedAt);

public sealed record AutoChargeAttemptView(Guid Id, Guid DocumentId, string? DocumentNumber, int AttemptNo, decimal Amount, string Outcome,
    string OutcomeLabel, string? Detail, DateTime AttemptedAt);

/// <summary>ทุกค่าที่หน้าศูนย์บิลแสดง (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บแสดงอย่างเดียว)</summary>
public sealed record SavedCardsSummary(
    bool Available, string? UnavailableReason, IReadOnlyList<SavedCardView> Cards,
    bool GlobalEnabled, bool MandateEnabled, bool AdminBlocked, string? AdminBlockedReason,
    string? AutoChargeBlockReason, string ConsentText, string ConsentVersion, IReadOnlyList<AutoChargeAttemptView> RecentAttempts,
    // เปิดอยู่แต่ยินยอมไว้คนละฉบับ (นโยบายเปลี่ยน) ⇒ หน้าแสดงปุ่ม "ยืนยันความยินยอมฉบับปัจจุบัน"
    bool NeedsReconsent = false,
    // ฝ่ายค้าน S1: ข้อความรับทราบภาษีหัก ณ ที่จ่ายของผู้จ่ายนิติบุคคล (null = ไม่ใช่นิติบุคคล) · รับทราบแล้วหรือยัง
    string? WhtWarning = null, bool WhtAcknowledged = false,
    // ฝ่ายค้าน S3: มีบัตรที่ยินยอมแล้วรอการชำระครั้งแรกสำเร็จ (คำสั่งตัดเปิดเองเมื่อยืนยัน)
    bool PendingActivation = false);

/// <summary>ข้อเสนอ "บันทึกบัตรเพื่อตัดอัตโนมัติ" บนหน้าชำระด้วยบัตร — null = ไม่เสนอ (ปิดทั้งระบบ/ผู้ให้บริการไม่รองรับ/ช่องทางไม่เปิด) ·
/// <c>WhtWarning</c> = ช่องรับทราบแยกของผู้จ่ายนิติบุคคล (ฝ่ายค้าน S1)</summary>
public sealed record SavedCardOffer(string Text, string Version, string? WhtWarning = null);

public sealed record AdminMandateRow(Guid PayerCompanyId, string? CompanyName, bool Enabled, DateTime? EnabledAt, string? ConsentVersion,
    bool AdminBlocked, string? AdminBlockedReason, int ActiveCards, string? DefaultCardLabel, string? BlockReason);

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — <b>บัตรที่บันทึกไว้ + คำสั่งตัดบัตรอัตโนมัติ</b> ของค่าบริการ NextAcc (ตัวเดียวของทุกทางเข้า: หน้าชำระ · ศูนย์บิล · แอดมิน · งานตัดอัตโนมัติ)
/// <list type="bullet">
/// <item>บันทึกได้เฉพาะตอนชำระด้วยบัตร + ติ๊กยินยอม (ข้อความ/ฉบับจาก <see cref="PlatformSavedCardRules"/> · ฉบับที่แสดงบังคับ · นิติบุคคลรับทราบภาษีหัก ณ ที่จ่ายแยก) ⇒
///   <c>PdpaConsentRecord</c> ใต้บริษัทผู้ให้บริการ (ผู้ควบคุมข้อมูล) พร้อม hash หลักฐานรวม user-agent · เราเก็บเฉพาะเลขอ้างอิง/ยี่ห้อ/4 ตัวท้าย/วันหมดอายุ</item>
/// <item>ฝ่ายค้าน S3: คำสั่งตัด<b>เปิดเมื่อบัตรยืนยันแล้วเท่านั้น</b> (รายการที่ตัดด้วยบัตรนั้นจริงสำเร็จ — <see cref="ActivateOnVerifyAsync"/>) · เริ่มชำระไม่สำเร็จหลังบันทึก ⇒ ทิ้งบัตร (<see cref="DiscardUnusedAsync"/>)</item>
/// <item>ปิดตัดอัตโนมัติ/ลบบัตรใบสุดท้าย ⇒ ถอนความยินยอม (WithdrawnAt) + เสนอ "ลบบัตรด้วย" · เปิดใหม่ ⇒ ยินยอมใหม่ (แถวใหม่ — append-only)</item>
/// <item>ทุกการเปลี่ยนแปลง: ล็อกต่อบริษัทผู้จ่าย (<see cref="AdvisoryLockKey.PlatformSavedCard"/>) + audit chain (RuleCode + LegalReference)</item>
/// </list>
/// </summary>
public interface IPlatformSavedCardService
{
    Task<SavedCardOffer?> OfferAsync(Guid payerCompanyId, CancellationToken ct = default);
    Task<SavedCardsSummary> SummaryAsync(Guid payerCompanyId, CancellationToken ct = default);
    /// <summary>บันทึกบัตรจากโทเคน (ตอนชำระด้วยบัตร) — ไม่ผ่านด่าน ⇒ BusinessRuleException · ผู้ให้บริการล้ม ⇒ InvalidOperationException (ยังไม่ตัดเงิน) ·
    /// <b>ไม่เปิดคำสั่งตัด</b> (เปิดเมื่อบัตรยืนยันแล้ว)</summary>
    Task<PlatformCardRefs> SaveFromTokenAsync(Guid payerCompanyId, string cardToken, bool consentAccepted, string? consentVersionShown,
        bool whtAcknowledged, PlatformCardActor actor, CancellationToken ct = default);
    /// <summary>ผูกรายการชำระครั้งแรกของบัตร — เฉพาะรายการที่<b>สร้างในคำขอนี้</b> (CreatedAt ≥ <paramref name="startedAtUtc"/>) และตัดด้วยบัตรใบนี้ · สำเร็จทันที ⇒ ยืนยัน + เปิดคำสั่งตัด</summary>
    Task BindFirstIntentAsync(Guid payerCompanyId, Guid savedCardId, PaymentIntent intent, DateTime startedAtUtc, CancellationToken ct = default);
    /// <summary>ฝ่ายค้าน S3: บันทึกบัตรแล้วแต่เริ่มชำระไม่สำเร็จ ⇒ ทิ้งบัตรที่ยังไม่เคยใช้ (ลบที่ผู้ให้บริการ · ถอนความยินยอม)</summary>
    Task DiscardUnusedAsync(Guid payerCompanyId, Guid savedCardId, string reason, CancellationToken ct = default);
    /// <summary>ยืนยันบัตรที่รายการครั้งแรกสำเร็จแล้ว (หลัง 3-D Secure/webhook) — งานตัดอัตโนมัติ + หน้าศูนย์บิลเรียกก่อนอ่าน</summary>
    Task RefreshVerificationAsync(Guid payerCompanyId, CancellationToken ct = default);
    /// <summary>บัตรตั้งต้นที่ตัดอัตโนมัติได้ตอนนี้ — null + เหตุผล</summary>
    Task<(PlatformCardRefs? Card, string? Problem)> UsableDefaultCardAsync(Guid payerCompanyId, PaymentProviderConfig config, CancellationToken ct = default);
    Task<SavedCardsSummary> RemoveAsync(Guid payerCompanyId, Guid cardId, PlatformCardActor actor, CancellationToken ct = default);
    Task<SavedCardsSummary> SetDefaultAsync(Guid payerCompanyId, Guid cardId, PlatformCardActor actor, CancellationToken ct = default);
    /// <summary>เปิด (ยินยอมใหม่ · ต้องมีบัตรยืนยันแล้ว) / ปิด (ถอนความยินยอม · <paramref name="removeCards"/> = ลบบัตรทุกใบด้วย)</summary>
    Task<SavedCardsSummary> SetAutoChargeAsync(Guid payerCompanyId, bool enabled, bool consentAccepted, string? consentVersionShown,
        bool whtAcknowledged, bool removeCards, PlatformCardActor actor, CancellationToken ct = default);
    /// <summary>ฉบับความยินยอมปัจจุบัน (ข้อความ + ฉบับนโยบายยกเลิก/คืนเงิน)</summary>
    Task<string> CurrentConsentVersionAsync(CancellationToken ct = default);
    Task<bool> PayerIsJuristicAsync(Guid payerCompanyId, CancellationToken ct = default);
    // ── แอดมินแพลตฟอร์ม ──
    Task<bool> GlobalEnabledAsync(CancellationToken ct = default);
    Task SetGlobalAsync(bool enabled, PlatformCardActor actor, CancellationToken ct = default);
    Task AdminBlockAsync(Guid payerCompanyId, bool blocked, string? reason, PlatformCardActor actor, CancellationToken ct = default);
    Task<IReadOnlyList<AdminMandateRow>> AdminListAsync(CancellationToken ct = default);
}

public class PlatformSavedCardService : IPlatformSavedCardService
{
    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly ILegalDocumentService _legal;
    private readonly ILogger<PlatformSavedCardService> _logger;

    public PlatformSavedCardService(AccountingDbContext db, IPlatformPaymentService platform, IEnumerable<IPaymentProvider> providers,
        ILegalDocumentService legal, ILogger<PlatformSavedCardService> logger)
    { _db = db; _platform = platform; _providers = providers; _legal = legal; _logger = logger; }

    private const string SystemActor = "system:card-verified";

    private IPaymentProvider? ProviderOf(string? code) => _providers.FirstOrDefault(p => p.ProviderCode == code);

    public async Task<string> CurrentConsentVersionAsync(CancellationToken ct = default)
        => PlatformSavedCardRules.Version((await _legal.GetPurchasePolicyVersionAsync(ct)).Version);

    public async Task<bool> GlobalEnabledAsync(CancellationToken ct = default)
        => await _db.PlatformAutoChargeSettings.AsNoTracking()
            .Where(s => s.Id == PlatformAutoChargeSettings.SingletonId).Select(s => s.AutoChargeEnabled).FirstOrDefaultAsync(ct);

    /// <summary>ฝ่ายค้าน S1: นิติบุคคล/หน่วยราชการ (ผู้มีหน้าที่หักภาษี ณ ที่จ่าย) — ตัวแยกเดียวกับศูนย์บิล (<see cref="ContactTypeResolver.FromTaxId"/>)</summary>
    public async Task<bool> PayerIsJuristicAsync(Guid payerCompanyId, CancellationToken ct = default)
    {
        var taxId = await _db.Companies.AsNoTracking().Where(c => c.Id == payerCompanyId).Select(c => c.TaxId).FirstOrDefaultAsync(ct);
        return ContactTypeResolver.FromTaxId(taxId) is ContactType.JuristicPerson or ContactType.GovernmentAgency;
    }

    /// <summary>ใช้บัตรที่บันทึกได้ไหม (ไม่ขึ้นกับบริษัท) — null = ได้</summary>
    private async Task<string?> UnavailableReasonAsync(CancellationToken ct)
    {
        if (!await GlobalEnabledAsync(ct)) return "ผู้ดูแลระบบ NextAcc ยังไม่เปิดการบันทึกบัตร/ตัดบัตรอัตโนมัติ";
        var status = await _platform.StatusAsync(ct);
        if (!status.Online) return status.OfflineReason ?? "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์";
        if (!status.Methods.Contains(nameof(PaymentMethodKind.Card))) return "ช่องทางรับชำระของ NextAcc ยังไม่เปิดรับบัตร";
        if (ProviderOf(status.ProviderCode)?.SupportsSavedCards != true) return "ผู้ให้บริการรับชำระเงินของ NextAcc ไม่รองรับการบันทึกบัตร";
        return null;
    }

    public async Task<SavedCardOffer?> OfferAsync(Guid payerCompanyId, CancellationToken ct = default)
    {
        if (await UnavailableReasonAsync(ct) != null) return null;
        var pv = (await _legal.GetPurchasePolicyVersionAsync(ct)).Version;
        return new SavedCardOffer(PlatformSavedCardRules.Text(pv), PlatformSavedCardRules.Version(pv),
            await PayerIsJuristicAsync(payerCompanyId, ct) ? PlatformSavedCardRules.WhtWarningText : null);
    }

    public async Task<SavedCardsSummary> SummaryAsync(Guid payerCompanyId, CancellationToken ct = default)
    {
        await RefreshVerificationAsync(payerCompanyId, ct);
        var now = DateTime.UtcNow;
        var pid = await _platform.PlatformCompanyIdAsync(ct);
        var unavailable = pid == null ? "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ" : await UnavailableReasonAsync(ct);
        var cfg = await _platform.PlatformConfigAsync(ct);
        var cfgTest = cfg?.Mode == PaymentProviderMode.Test;
        var pv = (await _legal.GetPurchasePolicyVersionAsync(ct)).Version;
        var version = PlatformSavedCardRules.Version(pv);
        var juristic = await PayerIsJuristicAsync(payerCompanyId, ct);
        var cards = pid == null ? new List<PlatformSavedCard>() : await _db.PlatformSavedCards.AsNoTracking()
            .Where(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == pid.Value && c.RemovedAt == null)
            .OrderByDescending(c => c.IsDefault).ThenByDescending(c => c.CreatedAt).ToListAsync(ct);
        var mandate = pid == null ? null : await _db.PlatformAutoChargeMandates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.PayerCompanyId == payerCompanyId && m.PlatformCompanyId == pid.Value, ct);
        var views = cards.Select(c => new SavedCardView(c.Id, PlatformSavedCardRules.Label(c.Brand, c.Last4, c.ValidUntil), c.Brand, c.Last4,
            c.ValidUntil, c.IsDefault, c.VerifiedAt != null, c.IsTestMode,
            PlatformSavedCardRules.CardUnusableReason(false, c.VerifiedAt != null, c.ValidUntil, cfg != null && c.IsTestMode == cfgTest
                && c.ProviderCode == cfg.ProviderCode, now), c.CreatedAt)).ToList();
        var def = views.FirstOrDefault(v => v.IsDefault);
        var global = await GlobalEnabledAsync(ct);
        var block = PlatformSavedCardRules.AutoChargeBlockReason(global, mandate?.Enabled == true, mandate?.AdminBlockedAt != null,
            mandate?.ConsentVersion, version, def == null ? "ยังไม่มีบัตรตั้งต้น" : def.Problem, juristic, mandate?.WhtAcknowledgedAt != null);
        var attempts = pid == null ? new List<AutoChargeAttemptView>() : await RecentAttemptsAsync(pid.Value, payerCompanyId, 10, ct);
        var pendingActivation = mandate?.Enabled != true && cards.Any(c => c.VerifiedAt == null && c.ConsentRecordId != null);
        return new SavedCardsSummary(unavailable == null, unavailable, views, global, mandate?.Enabled == true, mandate?.AdminBlockedAt != null,
            mandate?.AdminBlockedReason, block, PlatformSavedCardRules.Text(pv), version, attempts,
            NeedsReconsent: mandate?.Enabled == true && mandate.ConsentVersion != version,
            WhtWarning: juristic ? PlatformSavedCardRules.WhtWarningText : null, WhtAcknowledged: mandate?.WhtAcknowledgedAt != null,
            PendingActivation: pendingActivation);
    }

    private async Task<List<AutoChargeAttemptView>> RecentAttemptsAsync(Guid pid, Guid payerCompanyId, int take, CancellationToken ct)
    {
        var rows = await _db.PlatformAutoChargeAttempts.AsNoTracking()
            .Where(a => a.PlatformCompanyId == pid && a.PayerCompanyId == payerCompanyId)
            .OrderByDescending(a => a.AttemptedAt).Take(take).ToListAsync(ct);
        var docIds = rows.Select(r => r.DocumentId).Distinct().ToList();
        var numbers = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == pid && docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        return rows.Select(r => new AutoChargeAttemptView(r.Id, r.DocumentId, numbers.GetValueOrDefault(r.DocumentId), r.AttemptNo, r.Amount,
            r.Outcome, PlatformAutoChargePolicy.OutcomeLabel(r.Outcome), r.Detail, r.AttemptedAt)).ToList();
    }

    /// <summary>ล็อกต่อบริษัทผู้จ่ายในธุรกรรมของผู้เรียก (xact lock — ปล่อยตอน commit)</summary>
    private Task LockPayerAsync(Guid payerCompanyId, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(payerCompanyId, AdvisoryLockKey.PlatformSavedCard, payerCompanyId.ToString("N")) }, ct);

    public async Task<PlatformCardRefs> SaveFromTokenAsync(Guid payerCompanyId, string cardToken, bool consentAccepted, string? consentVersionShown,
        bool whtAcknowledged, PlatformCardActor actor, CancellationToken ct = default)
    {
        if (await UnavailableReasonAsync(ct) is string no) throw new BusinessRuleException(no, PlatformSavedCardRules.RuleSave, 409);
        var version = await CurrentConsentVersionAsync(ct);
        if (PlatformSavedCardRules.ConsentProblem(consentAccepted, consentVersionShown, version) is string consentNo)
            throw new BusinessRuleException(consentNo, PlatformSavedCardRules.RuleSave);
        var juristic = await PayerIsJuristicAsync(payerCompanyId, ct);
        if (PlatformSavedCardRules.WhtAckProblem(juristic, whtAcknowledged) is string whtNo)
            throw new BusinessRuleException(whtNo, PlatformSavedCardRules.RuleSave);
        if (string.IsNullOrWhiteSpace(cardToken)) throw new BusinessRuleException("ไม่พบโทเคนบัตร — กรอกบัตรใหม่อีกครั้ง", PlatformSavedCardRules.RuleSave);
        var pid = await _platform.PlatformCompanyIdAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ", PlatformSavedCardRules.RuleSave, 409);
        var cfg = await _platform.PlatformConfigAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งช่องทางรับชำระของ NextAcc", PlatformSavedCardRules.RuleSave, 409);
        var provider = ProviderOf(cfg.ProviderCode) ?? throw new BusinessRuleException("ไม่รู้จักผู้ให้บริการของช่องทาง NextAcc", PlatformSavedCardRules.RuleSave, 409);
        var isTest = cfg.Mode == PaymentProviderMode.Test;

        // ลูกค้าของผู้ให้บริการเดิมของบริษัทนี้ (ผู้ให้บริการ/โหมดเดียวกัน) — ผูกบัตรเพิ่ม ไม่สร้างลูกค้าซ้ำ
        var existingCustomer = await _db.PlatformSavedCards.AsNoTracking()
            .Where(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == pid && c.ProviderCode == cfg.ProviderCode && c.IsTestMode == isTest)
            .OrderByDescending(c => c.CreatedAt).Select(c => c.CustomerRef).FirstOrDefaultAsync(ct);

        // เรียกผู้ให้บริการก่อนเปิดธุรกรรม (ไม่ถือล็อกระหว่างรอเครือข่าย) — ล้ม ⇒ ยังไม่มีอะไรถูกบันทึก/ตัดเงิน
        var saved = await provider.SaveCardAsync(cfg, cardToken.Trim(), existingCustomer, $"NextAcc tenant {payerCompanyId:N}", actor.Email, ct);
        if (PaymentLiveModeGuard.Mismatch(saved.LiveMode, isTest) is string modeBad)
            throw new InvalidOperationException("บัตรที่บันทึกไม่ตรงโหมดของช่องทางรับชำระ: " + modeBad);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockPayerAsync(payerCompanyId, ct);
        var now = DateTime.UtcNow;
        var card = await _db.PlatformSavedCards.FirstOrDefaultAsync(c => c.ProviderCode == cfg.ProviderCode && c.CardRef == saved.CardRef
            && c.RemovedAt == null, ct);
        if (card != null && card.PayerCompanyId != payerCompanyId)
            throw new BusinessRuleException("บัตรใบนี้ผูกกับบริษัทอื่นอยู่แล้ว — ใช้บัตรอื่น หรือติดต่อผู้ดูแลระบบ", PlatformSavedCardRules.RuleSave, 409);
        var hasActive = await _db.PlatformSavedCards.AnyAsync(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == pid
            && c.RemovedAt == null && (card == null || c.Id != card.Id), ct);
        if (card == null)
        {
            card = new PlatformSavedCard
            {
                PlatformCompanyId = pid, PayerCompanyId = payerCompanyId, ProviderCode = cfg.ProviderCode, ProviderConfigId = cfg.Id, IsTestMode = isTest,
                CustomerRef = saved.CustomerRef, CardRef = saved.CardRef, Brand = PlatformSavedCardRules.Brand(saved.Brand),
                Last4 = PlatformSavedCardRules.Last4(saved.Last4), ValidUntil = PlatformSavedCardRules.ValidUntil(saved.ValidMonth, saved.ValidYear),
                // บัตรใบแรก = ตั้งต้นทันที · ใบเพิ่มเป็นตั้งต้นเมื่อยืนยันแล้ว (ใบที่ยังไม่ผ่านการชำระไม่แทนใบที่ใช้ได้อยู่)
                IsDefault = !hasActive,
                CreatedBy = actor.Name,
            };
            _db.PlatformSavedCards.Add(card);
        }
        // ยินยอมตอนบันทึก — คำสั่งตัด<b>ยังไม่เปิด</b> (ฝ่ายค้าน S3: เปิดเมื่อบัตรยืนยันแล้ว — ActivateOnVerifyAsync ใช้แถวนี้)
        var consent = StageConsent(pid, payerCompanyId, PlatformSavedCardRules.EvidenceVersion(consentVersionShown!.Trim(), juristic && whtAcknowledged), actor, now);
        card.ConsentRecordId = consent.Id;
        card.WhtAcknowledgedAt = juristic && whtAcknowledged ? now : null;
        card.UpdatedAt = now;
        Audit(payerCompanyId, actor, "PlatformSavedCard", card.Id, null,
            new { card = PlatformSavedCardRules.Label(card.Brand, card.Last4, card.ValidUntil), isTestMode = isTest, consentVersion = consent.PolicyVersion,
                  ruleCode = PlatformSavedCardRules.RuleSave, legalReference = PlatformSavedCardRules.LegalReference });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PlatformCardRefs(card.Id, payerCompanyId, card.ProviderCode, card.IsTestMode, card.CustomerRef, card.CardRef);
    }

    /// <summary>แถวความยินยอม PDPA ใต้บริษัทผู้ให้บริการ — เวลาที่เก็บ = เวลาที่ hash (normalize) · ช่องทางผูกบริษัทผู้จ่าย · IP/UA ตัดความยาวก่อน hash
    /// (ค่าที่เก็บ = ค่าที่ hash · ฝ่ายค้าน S2)</summary>
    private PdpaConsentRecord StageConsent(Guid pid, Guid payerCompanyId, string versionShown, PlatformCardActor actor, DateTime now)
    {
        var at = PdpaConsentEvidence.NormalizeTimestamp(now);
        var channel = $"{PlatformSavedCardRules.ConsentChannel}:{payerCompanyId:N}";
        var subject = string.IsNullOrWhiteSpace(actor.Email) ? actor.UserId?.ToString() : actor.Email.Trim().ToLowerInvariant();
        var ip = Trunc(string.IsNullOrWhiteSpace(actor.Ip) ? null : actor.Ip, PlatformPolicyAcceptanceRules.MaxIp);
        var ua = Trunc(string.IsNullOrEmpty(actor.UserAgent) ? null : actor.UserAgent, PlatformPolicyAcceptanceRules.MaxUserAgent);
        var row = new PdpaConsentRecord
        {
            CompanyId = pid,
            SubjectUserId = actor.UserId,
            SubjectContact = subject,
            Purpose = PlatformSavedCardRules.ConsentPurpose,
            PolicyVersion = versionShown,
            GrantedAt = at,
            Channel = channel,
            IpAddress = ip,
            UserAgent = ua,
            EvidenceHash = PdpaConsentEvidence.ComputeHash(subject, PlatformSavedCardRules.ConsentPurpose, versionShown, at, channel, ip, ua),
            EvidenceSource = PdpaConsentEvidence.SourceSystem,
            CreatedBy = actor.Name,
        };
        _db.PdpaConsentRecords.Add(row);
        return row;
    }

    /// <summary>ฉบับฐาน (ข้อความ + นโยบาย) ของหลักฐาน — ตัดเครื่องหมายรับทราบภาษีหัก ณ ที่จ่ายออก</summary>
    private static string BaseVersion(string policyVersion)
        => policyVersion.EndsWith(PlatformSavedCardRules.WhtAckVersionSuffix, StringComparison.Ordinal)
            ? policyVersion[..^PlatformSavedCardRules.WhtAckVersionSuffix.Length] : policyVersion;

    private async Task EnableMandateAsync(Guid pid, Guid payerCompanyId, PdpaConsentRecord consent, DateTime? whtAckAt, string actorName,
        PlatformCardActor? actor, DateTime now, CancellationToken ct)
    {
        var m = await _db.PlatformAutoChargeMandates.FirstOrDefaultAsync(x => x.PlatformCompanyId == pid && x.PayerCompanyId == payerCompanyId, ct);
        if (m == null)
        {
            m = new PlatformAutoChargeMandate { PlatformCompanyId = pid, PayerCompanyId = payerCompanyId, CreatedBy = actorName };
            _db.PlatformAutoChargeMandates.Add(m);
        }
        else if (m.ConsentRecordId is Guid old && old != consent.Id)
            await WithdrawConsentAsync(old, "แทนด้วยความยินยอมฉบับใหม่", now, ct);
        m.Enabled = true;
        m.ConsentRecordId = consent.Id;
        m.ConsentVersion = BaseVersion(consent.PolicyVersion);
        m.WhtAcknowledgedAt = whtAckAt;
        m.EnabledAt = now;
        m.EnabledBy = actorName;
        m.DisabledAt = null; m.DisabledBy = null; m.DisabledReason = null;
        m.UpdatedAt = now;
        Audit(payerCompanyId, actor ?? new PlatformCardActor(null, actorName, null, null), "PlatformAutoChargeMandate", m.Id, null,
            new { enabled = true, consentVersion = consent.PolicyVersion, ruleCode = PlatformSavedCardRules.RuleMandate, legalReference = PlatformSavedCardRules.LegalReference });
    }

    private async Task WithdrawConsentAsync(Guid consentId, string reason, DateTime now, CancellationToken ct)
    {
        var c = await _db.PdpaConsentRecords.FirstOrDefaultAsync(x => x.Id == consentId, ct);
        if (c == null || c.WithdrawnAt != null) return;
        c.WithdrawnAt = now;
        c.WithdrawnReason = reason.Length > 300 ? reason[..300] : reason;
        c.UpdatedAt = now;
    }

    private async Task DisableMandateAsync(Guid pid, Guid payerCompanyId, string reason, PlatformCardActor actor, DateTime now, CancellationToken ct)
    {
        var m = await _db.PlatformAutoChargeMandates.FirstOrDefaultAsync(x => x.PlatformCompanyId == pid && x.PayerCompanyId == payerCompanyId, ct);
        if (m == null)
        {
            // ยังไม่เคยเปิด (บัตรรอยืนยัน) — ประทับ "ปิด" ไว้ ⇒ ยืนยันบัตรภายหลังไม่เปิดเอง (ActivateOnVerifyAsync)
            m = new PlatformAutoChargeMandate { PlatformCompanyId = pid, PayerCompanyId = payerCompanyId, CreatedBy = actor.Name };
            _db.PlatformAutoChargeMandates.Add(m);
        }
        // ปิด/ลบใบสุดท้ายระหว่างรอยืนยัน ⇒ ห้ามเปิดเองภายหลัง (DisabledAt ใหม่กว่าบัตร) แม้คำสั่งยังไม่เคยเปิด
        var wasEnabled = m.Enabled;
        m.Enabled = false;
        m.DisabledAt = now; m.DisabledBy = actor.Name; m.DisabledReason = reason.Length > 300 ? reason[..300] : reason;
        m.UpdatedAt = now;
        if (m.ConsentRecordId is Guid cid) await WithdrawConsentAsync(cid, reason, now, ct);
        if (wasEnabled)
            Audit(payerCompanyId, actor, "PlatformAutoChargeMandate", m.Id, new { enabled = true },
                new { enabled = false, reason, ruleCode = PlatformSavedCardRules.RuleMandate, legalReference = PlatformSavedCardRules.LegalReference });
    }

    /// <summary>ฝ่ายค้าน S3: บัตรเพิ่งยืนยัน (รายการที่ตัดด้วยบัตรนี้สำเร็จ) ⇒ เป็นบัตรตั้งต้น + เปิดคำสั่งตัดด้วยความยินยอมตอนบันทึก ·
    /// ไม่เปิดเมื่อ: ความยินยอมถูกถอน · ผู้เช่าปิดคำสั่งหลังบันทึกบัตรนี้ · คำสั่งเปิดอยู่แล้ว (แค่เปลี่ยนบัตรตั้งต้น)</summary>
    private async Task ActivateOnVerifyAsync(PlatformSavedCard card, DateTime now, CancellationToken ct)
    {
        foreach (var other in await _db.PlatformSavedCards.Where(c => c.PayerCompanyId == card.PayerCompanyId && c.PlatformCompanyId == card.PlatformCompanyId
                     && c.RemovedAt == null && c.IsDefault && c.Id != card.Id).ToListAsync(ct))
        { other.IsDefault = false; other.UpdatedAt = now; }
        card.IsDefault = true;
        if (card.ConsentRecordId is not Guid cid) return;
        var consent = await _db.PdpaConsentRecords.FirstOrDefaultAsync(c => c.Id == cid, ct);
        if (consent == null || consent.WithdrawnAt != null) return;
        var m = await _db.PlatformAutoChargeMandates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PlatformCompanyId == card.PlatformCompanyId && x.PayerCompanyId == card.PayerCompanyId, ct);
        if (m?.Enabled == true) return;
        if (m?.DisabledAt is DateTime off && off > card.CreatedAt) return;
        await EnableMandateAsync(card.PlatformCompanyId, card.PayerCompanyId, consent, card.WhtAcknowledgedAt, SystemActor, null, now, ct);
    }

    public async Task BindFirstIntentAsync(Guid payerCompanyId, Guid savedCardId, PaymentIntent intent, DateTime startedAtUtc, CancellationToken ct = default)
    {
        var card = await _db.PlatformSavedCards.FirstOrDefaultAsync(c => c.Id == savedCardId && c.PayerCompanyId == payerCompanyId, ct);
        if (card == null || card.VerifiedAt != null) return;
        // ฝ่ายค้าน B2: ผูกเฉพาะรายการที่สร้างในคำขอนี้ + ตัดด้วยบัตรใบนี้ + ผู้จ่ายรายนี้ (รายการเปิดเดิมที่ถูกใช้ซ้ำไม่ใช่หลักฐานของบัตรใบนี้)
        if (!PlatformSavedCardRules.IntentProvesCard(intent.CreatedAt, startedAtUtc, intent.SavedCardRef, card.CardRef, intent.PayerCompanyId, payerCompanyId))
            return;
        var now = DateTime.UtcNow;
        card.FirstIntentId = intent.Id;
        if (PaymentIntentPolicy.IsSettledPositive(intent.Status))
        {
            card.VerifiedAt = intent.ConfirmedAt ?? now;
            await ActivateOnVerifyAsync(card, now, ct);
        }
        card.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DiscardUnusedAsync(Guid payerCompanyId, Guid savedCardId, string reason, CancellationToken ct = default)
    {
        var card = await _db.PlatformSavedCards.FirstOrDefaultAsync(c => c.Id == savedCardId && c.PayerCompanyId == payerCompanyId
            && c.RemovedAt == null && c.VerifiedAt == null && c.FirstIntentId == null, ct);
        if (card == null) return;
        var cfg = await _db.PaymentProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == card.ProviderConfigId, ct);
        var provider = ProviderOf(card.ProviderCode);
        var removal = cfg == null || provider == null
            ? new ProviderCardRemoval(false, "ไม่พบการตั้งค่าผู้ให้บริการเดิมของบัตรนี้")
            : await provider.RemoveSavedCardAsync(PaymentConfigForIntent.For(cfg, card.IsTestMode), card.CustomerRef, card.CardRef, ct);
        var now = DateTime.UtcNow;
        card.RemovedAt = now; card.RemovedBy = SystemActor; card.RemovedReason = Trunc(reason, 300);
        card.ProviderRemoveProblem = removal.Removed ? null : Trunc(removal.Problem, 500);
        card.IsDefault = false;
        card.UpdatedAt = now;
        if (card.ConsentRecordId is Guid cid) await WithdrawConsentAsync(cid, reason, now, ct);
        Audit(payerCompanyId, new PlatformCardActor(null, SystemActor, null, null), "PlatformSavedCard", card.Id, new { removed = false },
            new { removed = true, reason, providerRemoved = removal.Removed, ruleCode = PlatformSavedCardRules.RuleRemove });
        await _db.SaveChangesAsync(ct);
    }

    public async Task RefreshVerificationAsync(Guid payerCompanyId, CancellationToken ct = default)
    {
        var pending = await _db.PlatformSavedCards
            .Where(c => c.PayerCompanyId == payerCompanyId && c.RemovedAt == null && c.VerifiedAt == null && c.FirstIntentId != null)
            .ToListAsync(ct);
        if (pending.Count == 0) return;
        var ids = pending.Select(c => c.FirstIntentId!.Value).ToList();
        // ฝ่ายค้าน B2: นับเฉพาะรายการที่ตัดด้วยบัตรใบนั้นจริง (SavedCardRef ตรง)
        var paid = (await _db.PaymentIntents.AsNoTracking()
                .Where(i => ids.Contains(i.Id) && i.PayerCompanyId == payerCompanyId
                    && (i.Status == PaymentIntentStatus.Succeeded || i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded))
                .Select(i => new { i.Id, i.ConfirmedAt, i.SavedCardRef }).ToListAsync(ct))
            .ToDictionary(i => i.Id);
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var c in pending)
            if (paid.TryGetValue(c.FirstIntentId!.Value, out var i) && i.SavedCardRef == c.CardRef)
            {
                c.VerifiedAt = i.ConfirmedAt ?? now; c.UpdatedAt = now; changed = true;
                await ActivateOnVerifyAsync(c, now, ct);
            }
        if (changed) await _db.SaveChangesAsync(ct);
    }

    public async Task<(PlatformCardRefs? Card, string? Problem)> UsableDefaultCardAsync(Guid payerCompanyId, PaymentProviderConfig config,
        CancellationToken ct = default)
    {
        var c = await _db.PlatformSavedCards.AsNoTracking()
            .Where(x => x.PayerCompanyId == payerCompanyId && x.PlatformCompanyId == config.CompanyId && x.RemovedAt == null && x.IsDefault)
            .FirstOrDefaultAsync(ct);
        if (c == null) return (null, "ยังไม่มีบัตรตั้งต้น");
        var sameMode = c.ProviderCode == config.ProviderCode && c.IsTestMode == (config.Mode == PaymentProviderMode.Test);
        if (PlatformSavedCardRules.CardUnusableReason(false, c.VerifiedAt != null, c.ValidUntil, sameMode, DateTime.UtcNow) is string why)
            return (null, why);
        return (new PlatformCardRefs(c.Id, payerCompanyId, c.ProviderCode, c.IsTestMode, c.CustomerRef, c.CardRef), null);
    }

    /// <summary>ลบที่ผู้ให้บริการ (นอกธุรกรรม) — ไม่สำเร็จ: คืนข้อความ (ผู้เรียกประทับบนแถว · ระบบเลิกใช้บัตรทันที)</summary>
    private async Task<ProviderCardRemoval> RemoveAtProviderAsync(PlatformSavedCard card, Guid payerCompanyId, CancellationToken ct)
    {
        var cfg = await _db.PaymentProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == card.ProviderConfigId, ct);
        var provider = ProviderOf(card.ProviderCode);
        var removal = cfg == null || provider == null
            ? new ProviderCardRemoval(false, "ไม่พบการตั้งค่าผู้ให้บริการเดิมของบัตรนี้")
            : await provider.RemoveSavedCardAsync(PaymentConfigForIntent.For(cfg, card.IsTestMode), card.CustomerRef, card.CardRef, ct);
        if (!removal.Removed)
            _logger.LogError("ลบบัตรที่ผู้ให้บริการไม่สำเร็จ บัตร {Card} บริษัท {Payer}: {Why}", card.Id, payerCompanyId, removal.Problem);
        return removal;
    }

    public async Task<SavedCardsSummary> RemoveAsync(Guid payerCompanyId, Guid cardId, PlatformCardActor actor, CancellationToken ct = default)
    {
        var card = await _db.PlatformSavedCards.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cardId && c.PayerCompanyId == payerCompanyId && c.RemovedAt == null, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรที่บันทึกไว้ของบริษัทนี้");
        var removal = await RemoveAtProviderAsync(card, payerCompanyId, ct);
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockPayerAsync(payerCompanyId, ct);
            await MarkRemovedAsync(payerCompanyId, cardId, removal, "ผู้เช่าลบบัตร", actor, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await SummaryAsync(payerCompanyId, ct);
    }

    /// <summary>ประทับลบ + เลือกบัตรตั้งต้นใหม่ (ใบที่ยืนยันแล้วก่อน) · ลบใบสุดท้าย ⇒ ปิดคำสั่ง + ถอนความยินยอม (ผู้เรียกถือล็อก + SaveChanges)</summary>
    private async Task MarkRemovedAsync(Guid payerCompanyId, Guid cardId, ProviderCardRemoval removal, string reason, PlatformCardActor actor,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await _db.PlatformSavedCards.FirstAsync(c => c.Id == cardId, ct);
        if (row.RemovedAt != null) return;
        row.RemovedAt = now; row.RemovedBy = actor.Name; row.RemovedReason = Trunc(reason, 300);
        row.ProviderRemoveProblem = removal.Removed ? null : Trunc(removal.Problem, 500);
        var wasDefault = row.IsDefault;
        row.IsDefault = false;
        row.UpdatedAt = now;
        if (row.VerifiedAt == null && row.ConsentRecordId is Guid pendingConsent)
            await WithdrawConsentAsync(pendingConsent, reason, now, ct);
        var rest = await _db.PlatformSavedCards
            .Where(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == row.PlatformCompanyId && c.RemovedAt == null && c.Id != row.Id)
            .OrderByDescending(c => c.VerifiedAt != null).ThenByDescending(c => c.CreatedAt).ToListAsync(ct);
        if (wasDefault && rest.Count > 0) { rest[0].IsDefault = true; rest[0].UpdatedAt = now; }
        if (rest.Count == 0) await DisableMandateAsync(row.PlatformCompanyId, payerCompanyId, "ลบบัตรใบสุดท้าย", actor, now, ct);
        Audit(payerCompanyId, actor, "PlatformSavedCard", row.Id, new { removed = false },
            new { removed = true, reason, providerRemoved = removal.Removed, providerProblem = removal.Problem,
                  ruleCode = PlatformSavedCardRules.RuleRemove, legalReference = PlatformSavedCardRules.LegalReference });
    }

    public async Task<SavedCardsSummary> SetDefaultAsync(Guid payerCompanyId, Guid cardId, PlatformCardActor actor, CancellationToken ct = default)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockPayerAsync(payerCompanyId, ct);
            var card = await _db.PlatformSavedCards.FirstOrDefaultAsync(c => c.Id == cardId && c.PayerCompanyId == payerCompanyId && c.RemovedAt == null, ct)
                ?? throw new KeyNotFoundException("ไม่พบบัตรที่บันทึกไว้ของบริษัทนี้");
            var now = DateTime.UtcNow;
            foreach (var other in await _db.PlatformSavedCards.Where(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == card.PlatformCompanyId
                         && c.RemovedAt == null && c.IsDefault && c.Id != card.Id).ToListAsync(ct))
            { other.IsDefault = false; other.UpdatedAt = now; }
            if (!card.IsDefault)
            {
                card.IsDefault = true; card.UpdatedAt = now;
                Audit(payerCompanyId, actor, "PlatformSavedCard", card.Id, new { isDefault = false },
                    new { isDefault = true, ruleCode = PlatformSavedCardRules.RuleDefault });
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await SummaryAsync(payerCompanyId, ct);
    }

    public async Task<SavedCardsSummary> SetAutoChargeAsync(Guid payerCompanyId, bool enabled, bool consentAccepted, string? consentVersionShown,
        bool whtAcknowledged, bool removeCards, PlatformCardActor actor, CancellationToken ct = default)
    {
        var pid = await _platform.PlatformCompanyIdAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ", PlatformSavedCardRules.RuleMandate, 409);
        var version = await CurrentConsentVersionAsync(ct);
        var juristic = await PayerIsJuristicAsync(payerCompanyId, ct);
        if (enabled)
        {
            if (await UnavailableReasonAsync(ct) is string no) throw new BusinessRuleException(no, PlatformSavedCardRules.RuleMandate, 409);
            if (PlatformSavedCardRules.ConsentProblem(consentAccepted, consentVersionShown, version) is string consentNo)
                throw new BusinessRuleException(consentNo, PlatformSavedCardRules.RuleMandate);
            if (PlatformSavedCardRules.WhtAckProblem(juristic, whtAcknowledged) is string whtNo)
                throw new BusinessRuleException(whtNo, PlatformSavedCardRules.RuleMandate);
        }
        // ปิด + ลบบัตรด้วย: ลบที่ผู้ให้บริการก่อน (นอกธุรกรรม)
        var removals = new List<(Guid CardId, ProviderCardRemoval Removal)>();
        if (!enabled && removeCards)
            foreach (var c in await _db.PlatformSavedCards.AsNoTracking()
                         .Where(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == pid && c.RemovedAt == null).ToListAsync(ct))
                removals.Add((c.Id, await RemoveAtProviderAsync(c, payerCompanyId, ct)));
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockPayerAsync(payerCompanyId, ct);
            var now = DateTime.UtcNow;
            if (enabled)
            {
                // ฝ่ายค้าน S3: เปิดได้เมื่อมีบัตรที่ยืนยันแล้ว (ชำระด้วยบัตรนั้นสำเร็จ) เท่านั้น
                var hasVerified = await _db.PlatformSavedCards.AnyAsync(c => c.PayerCompanyId == payerCompanyId && c.PlatformCompanyId == pid
                    && c.RemovedAt == null && c.VerifiedAt != null, ct);
                if (!hasVerified)
                    throw new BusinessRuleException("ยังไม่มีบัตรที่ยืนยันแล้ว — ชำระใบแจ้งหนี้ด้วยบัตรแล้วติ๊ก \"บันทึกบัตรเพื่อตัดอัตโนมัติ\" (ระบบเปิดให้เองเมื่อชำระสำเร็จ)",
                        PlatformSavedCardRules.RuleMandate, 409);
                var ack = juristic && whtAcknowledged;
                var consent = StageConsent(pid, payerCompanyId, PlatformSavedCardRules.EvidenceVersion(consentVersionShown!.Trim(), ack), actor, now);
                await EnableMandateAsync(pid, payerCompanyId, consent, ack ? now : null, actor.Name, actor, now, ct);
            }
            else
            {
                await DisableMandateAsync(pid, payerCompanyId, "ผู้เช่าปิดการตัดบัตรอัตโนมัติ", actor, now, ct);
                foreach (var (cardId, removal) in removals)
                    await MarkRemovedAsync(payerCompanyId, cardId, removal, "ผู้เช่าปิดการตัดอัตโนมัติและลบบัตร", actor, ct);
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await SummaryAsync(payerCompanyId, ct);
    }

    public async Task SetGlobalAsync(bool enabled, PlatformCardActor actor, CancellationToken ct = default)
    {
        var s = await _db.PlatformAutoChargeSettings.FirstOrDefaultAsync(x => x.Id == PlatformAutoChargeSettings.SingletonId, ct);
        if (s == null) { s = new PlatformAutoChargeSettings { Id = PlatformAutoChargeSettings.SingletonId, CreatedBy = actor.Name }; _db.PlatformAutoChargeSettings.Add(s); }
        var before = s.AutoChargeEnabled;
        s.AutoChargeEnabled = enabled;
        s.ChangedAt = DateTime.UtcNow; s.ChangedBy = actor.Name; s.UpdatedAt = s.ChangedAt;
        Audit(PdpaPolicy.PlatformScopeCompanyId, actor, "PlatformAutoChargeSettings", s.Id, new { enabled = before },
            new { enabled, ruleCode = PlatformSavedCardRules.RuleGlobal });
        await _db.SaveChangesAsync(ct);
    }

    public async Task AdminBlockAsync(Guid payerCompanyId, bool blocked, string? reason, PlatformCardActor actor, CancellationToken ct = default)
    {
        if (blocked && string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("กรุณาระบุเหตุผลที่ระงับ", PlatformSavedCardRules.RuleAdminBlock);
        var pid = await _platform.PlatformCompanyIdAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ", PlatformSavedCardRules.RuleAdminBlock, 409);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockPayerAsync(payerCompanyId, ct);
        var m = await _db.PlatformAutoChargeMandates.FirstOrDefaultAsync(x => x.PlatformCompanyId == pid && x.PayerCompanyId == payerCompanyId, ct);
        if (m == null) { m = new PlatformAutoChargeMandate { PlatformCompanyId = pid, PayerCompanyId = payerCompanyId, CreatedBy = actor.Name }; _db.PlatformAutoChargeMandates.Add(m); }
        var now = DateTime.UtcNow;
        var why = Trunc(reason?.Trim(), 300);
        m.AdminBlockedAt = blocked ? now : null;
        m.AdminBlockedBy = blocked ? actor.Name : null;
        m.AdminBlockedReason = blocked ? why : null;
        m.UpdatedAt = now;
        Audit(payerCompanyId, actor, "PlatformAutoChargeMandate", m.Id, null,
            new { adminBlocked = blocked, reason = why, ruleCode = PlatformSavedCardRules.RuleAdminBlock });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AdminMandateRow>> AdminListAsync(CancellationToken ct = default)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return Array.Empty<AdminMandateRow>();
        var cfg = await _platform.PlatformConfigAsync(ct);
        var global = await GlobalEnabledAsync(ct);
        var version = await CurrentConsentVersionAsync(ct);
        var mandates = await _db.PlatformAutoChargeMandates.AsNoTracking().Where(m => m.PlatformCompanyId == pid)
            .OrderByDescending(m => m.EnabledAt).Take(500).ToListAsync(ct);
        var payers = mandates.Select(m => m.PayerCompanyId).ToList();
        var companies = await _db.Companies.AsNoTracking().Where(c => payers.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId }).ToDictionaryAsync(c => c.Id, ct);
        var cards = await _db.PlatformSavedCards.AsNoTracking()
            .Where(c => c.PlatformCompanyId == pid && payers.Contains(c.PayerCompanyId) && c.RemovedAt == null).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return mandates.Select(m =>
        {
            var mine = cards.Where(c => c.PayerCompanyId == m.PayerCompanyId).ToList();
            var def = mine.FirstOrDefault(c => c.IsDefault);
            var defProblem = def == null ? "ยังไม่มีบัตรตั้งต้น"
                : PlatformSavedCardRules.CardUnusableReason(false, def.VerifiedAt != null, def.ValidUntil,
                    cfg != null && def.ProviderCode == cfg.ProviderCode && def.IsTestMode == (cfg.Mode == PaymentProviderMode.Test), now);
            var co = companies.GetValueOrDefault(m.PayerCompanyId);
            var juristic = ContactTypeResolver.FromTaxId(co?.TaxId) is ContactType.JuristicPerson or ContactType.GovernmentAgency;
            return new AdminMandateRow(m.PayerCompanyId, co?.Name, m.Enabled, m.EnabledAt, m.ConsentVersion,
                m.AdminBlockedAt != null, m.AdminBlockedReason, mine.Count,
                def == null ? null : PlatformSavedCardRules.Label(def.Brand, def.Last4, def.ValidUntil),
                PlatformSavedCardRules.AutoChargeBlockReason(global, m.Enabled, m.AdminBlockedAt != null, m.ConsentVersion, version, defProblem,
                    juristic, m.WhtAcknowledgedAt != null));
        }).ToList();
    }

    /// <summary>ฝ่ายค้าน S6: แถวความยินยอมตัดบัตรของผู้ใช้ — รายงาน DSR ของบริษัท (<paramref name="payerCompanyId"/> · ไม่ส่ง IP/UA ข้ามบริษัท)
    /// และหน้า "ข้อมูลของฉัน" (ทุกบริษัท · ครบทุกช่อง) · ตัวเดียวของทั้งสองทาง</summary>
    public static async Task<List<Dictionary<string, object?>>> ConsentRowsForUserAsync(AccountingDbContext db, Guid userId, Guid? payerCompanyId,
        bool includeNetwork, CancellationToken ct = default)
    {
        var q = db.PdpaConsentRecords.AsNoTracking()
            .Where(c => c.SubjectUserId == userId && c.Purpose == PlatformSavedCardRules.ConsentPurpose);
        if (payerCompanyId is Guid pc)
        {
            var channel = $"{PlatformSavedCardRules.ConsentChannel}:{pc:N}";
            q = q.Where(c => c.Channel == channel);
        }
        var rows = await q.OrderBy(c => c.GrantedAt).ToListAsync(ct);
        return rows.Select(c =>
        {
            var d = new Dictionary<string, object?>
            {
                ["purpose"] = c.Purpose, ["policyVersion"] = c.PolicyVersion, ["grantedAt"] = c.GrantedAt, ["channel"] = c.Channel,
                ["withdrawnAt"] = c.WithdrawnAt, ["withdrawnReason"] = c.WithdrawnReason, ["evidenceHash"] = c.EvidenceHash,
                ["evidenceCheck"] = PlatformPolicyAcceptanceRules.CheckLabel(c),
            };
            if (includeNetwork) { d["ipAddress"] = c.IpAddress; d["userAgent"] = c.UserAgent; }
            return d;
        }).ToList();
    }

    private static string? Trunc(string? s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);

    private void Audit(Guid companyId, PlatformCardActor actor, string entityType, Guid entityId, object? before, object after)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actor.UserId,
            UserEmail = actor.Email ?? "system:platform-card",
            Action = AuditAction.Update,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            OldValues = before == null ? null : JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(after),
            IpAddress = Trunc(actor.Ip, PlatformPolicyAcceptanceRules.MaxIp),   // ฝ่ายค้าน (nice): ตัดความยาว
            UserAgent = Trunc(actor.UserAgent, PlatformPolicyAcceptanceRules.MaxUserAgent),
            Timestamp = DateTime.UtcNow,
        });
}
