using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Scope = Accounting.Helpers.PartnerConsent.Scope;
using Need = Accounting.Helpers.PartnerConsent.Need;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 P1 — ผู้เขียนเดียวของ <see cref="PartnerAutomationConsent"/> (ให้สองขั้น · ถอนทันทีต่อขอบเขต · ขอจากฝั่งคู่ค้า) และ
/// <b>ตัวตรวจตอนใช้ตัวเดียว</b> <see cref="CheckAsync"/> ที่ท่ออัตโนมัติ (P3 บันทึกจ่าย/อนุมัติใบซื้อ · P2/P4 สลิป · P5 50 ทวิ) ต้องเรียกทุกครั้งก่อนเขียน ·
/// กติกาทั้งหมดอยู่ใน <see cref="PartnerConsent"/> (pure) · ทุกการเปลี่ยนเข้า audit chain ของบริษัทผู้ให้ (<c>AddChainedAuditLog</c>) · ทุก query ผูก <c>CompanyId</c>
/// </summary>
public class PartnerConsentService
{
    /// <summary>คำขอจากคู่ค้าต่อคู่ต่อวัน (กันสแปมกระดิ่งของอีกบริษัท)</summary>
    public const int RequestsPerPartnerPerDay = 3;

    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly ILogger<PartnerConsentService> _logger;
    private readonly INotificationEngine? _notify;

    public PartnerConsentService(AccountingDbContext db, IPermissionService perms, ILogger<PartnerConsentService> logger, INotificationEngine? notify = null)
    { _db = db; _perms = perms; _logger = logger; _notify = notify; }

    // ═══════════ สิทธิ์ (ด่านเดิม — ไม่เขียนกติกาสิทธิ์ซ้ำ) ═══════════

    /// <summary>ผู้ใช้มีสิ่งที่ขอบเขตต้องการไหม — แปลง <see cref="Need"/> เป็นด่านเดิมของระบบ (ชนิดเอกสารฝั่งซื้อ = ใบแจ้งหนี้ซื้อ)</summary>
    public async Task<bool> HasNeedAsync(Guid companyId, Guid userId, Need need)
    {
        return need switch
        {
        Need.CompanySettingsEdit => await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.CompanySettingsEdit),
        Need.PurchaseView => (await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, userId)).Purchase,
        Need.PurchaseCreate => await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, userId, DocumentType.PurchaseInvoice),
        Need.PurchaseApprove => await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PurchaseInvoice),
        Need.TaxFile => await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.TaxFile),
        Need.SalesApprove => await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.Invoice),
        _ => false,
        };
    }

    /// <summary>ข้อความปฏิเสธของขอบเขตนี้สำหรับผู้ใช้ (ตัวตัดสิน <see cref="PartnerConsent.GrantProblem"/>) · null = มีครบ · ผู้ใช้ไม่ได้ใช้งานแล้ว = ปฏิเสธ</summary>
    public async Task<string?> GrantProblemForAsync(Guid companyId, Guid userId, Scope scope)
    {
        if (!await _db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active))
            return "ผู้ใช้นี้ไม่ได้ใช้งานอยู่ — ให้ความยินยอมไม่ได้";
        var have = new Dictionary<Need, bool>();
        foreach (var n in PartnerConsent.Def(scope).Needs) have[n] = await HasNeedAsync(companyId, userId, n);
        return PartnerConsent.GrantProblem(scope, n => have.TryGetValue(n, out var v) && v);
    }

    // ═══════════ ตัวตรวจตอนใช้ (API ของแพ็ก P3/P5) ═══════════

    /// <summary>
    /// **อ่านอย่างเดียว** — ขอบเขตนี้ของคู่นี้ใช้ได้ตอนนี้ไหม (หน้า/ป้าย/รายงาน) · <b>ท่อที่จะเขียนบัญชีห้ามใช้ตัวนี้</b> — ใช้ <see cref="CheckForWriteAsync"/>
    /// (ฝ่ายค้าน M1: อ่านแล้วถอนแทรกได้ก่อนท่อเขียน)
    /// </summary>
    public Task<PartnerConsent.UseCheck> CheckAsync(Guid companyId, Guid partnershipId, Scope scope, PartnerConsent.CapAmount? amount, CancellationToken ct = default)
        => EvaluateAsync(companyId, partnershipId, scope, amount, ct);

    /// <summary>
    /// **API ของท่ออัตโนมัติ (P3/P5)** — เรียก<b>ภายในธุรกรรมของท่อ</b> ก่อนเขียนทุกครั้ง: ล็อก <c>FOR SHARE</c> แถวคู่ค้า + แถวความยินยอมที่ใช้อยู่ ⇒
    /// การถอน (<see cref="WithdrawAsync"/> · UPDATE) และการระงับคู่รอจนท่อ commit/rollback — งานที่เขียนในธุรกรรมนี้จึงเกิดภายใต้ความยินยอมที่ยังใช้อยู่จริง ·
    /// ไม่มีธุรกรรม = ใช้ผิด ⇒ <see cref="InvalidOperationException"/> (ข้อผิดพลาดของโปรแกรม ไม่ใช่ของผู้ใช้) ·
    /// ไม่ผ่าน ⇒ ท่อกลับเป็น "ปุ่มเดียว" พร้อม <see cref="PartnerConsent.UseCheck.Reason"/> · ใช้ <see cref="PartnerConsent.UseCheck.GrantorUserId"/>/<c>ConsentId</c> เป็นผู้รับผิดชอบใน audit (264-2-3) ·
    /// <paramref name="amount"/> = <see cref="PartnerConsent.CapAmount"/> (ยอดรวมบาทก่อนหัก ณ ที่จ่าย — M3)
    /// </summary>
    public async Task<PartnerConsent.UseCheck> CheckForWriteAsync(Guid companyId, Guid partnershipId, Scope scope, PartnerConsent.CapAmount? amount, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("CheckForWriteAsync ต้องเรียกภายในธุรกรรมของท่อที่จะเขียน (ล็อกความยินยอมไว้จน commit)");
        var name = scope.ToString();
        // ล็อกคู่ก่อนแถวความยินยอม (ลำดับเดียวกันทุกผู้เรียก) · ไม่ใช่สมาชิกของคู่ = ไม่ล็อกอะไร (ตัวตรวจคืน NoPartner)
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"TradingPartnerships\" WHERE \"Id\" = {partnershipId} AND (\"CompanyAId\" = {companyId} OR \"CompanyBId\" = {companyId}) FOR SHARE", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PartnerAutomationConsents\" WHERE \"CompanyId\" = {companyId} AND \"TradingPartnershipId\" = {partnershipId} AND \"Scope\" = {name} AND \"WithdrawnAt\" IS NULL FOR SHARE", ct);
        return await EvaluateAsync(companyId, partnershipId, scope, amount, ct);
    }

    /// <summary>ตัวประเมินเดียวของสองทางเข้าข้างบน — อ่านสด (AsNoTracking) ทุกค่า: คู่ · บริษัทคู่ค้า · แถว · สิทธิ์ผู้ให้ · บัญชีจ่ายที่ประกาศ</summary>
    private async Task<PartnerConsent.UseCheck> EvaluateAsync(Guid companyId, Guid partnershipId, Scope scope, PartnerConsent.CapAmount? amount, CancellationToken ct)
    {
        var p = await _db.TradingPartnerships.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId), ct);
        var def = PartnerConsent.Def(scope);
        if (def.CompanyWideToggle)
        {
            var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId, ct);
            return PartnerConsent.UseCheck.From(PartnerConsent.AllowsCompanyWide(scope, PartnerConsent.CompanyWideToggleOn(scope, cfg), p, companyId), null);
        }
        // ฝ่ายค้าน L7: บริษัทคู่ค้าถูกลบ (query filter ตัดออก) / ระงับ ⇒ หยุด
        var partnerActive = p != null && await _db.Companies.AsNoTracking()
            .AnyAsync(c => c.Id == (p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId) && c.Status == CompanyStatus.Active, ct);
        var name = scope.ToString();
        var row = await _db.PartnerAutomationConsents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.TradingPartnershipId == partnershipId && x.Scope == name && x.WithdrawnAt == null, ct);
        var grantorOk = row != null && await GrantProblemForAsync(companyId, row.GrantedByUserId, scope) == null;
        // ฝ่ายค้าน M2: บัญชีจ่ายที่ประกาศไว้ต้องยังใช้ได้ ณ ตอนนี้ (ของบริษัท · ใช้งาน · ไม่ถูกลบ) — ไม่ได้ ⇒ ไม่ส่งออก + เหตุ
        var payOk = row?.DefaultPayBankAccountId is not Guid bank
            || await _db.BankAccounts.AsNoTracking().AnyAsync(b => b.Id == bank && b.CompanyId == companyId && b.IsActive && !b.IsDeleted, ct);
        return PartnerConsent.UseCheck.From(PartnerConsent.Allows(scope, row, p, companyId, amount, grantorOk, partnerActive), row, payOk);
    }

    // ═══════════ หน้า "งานอัตโนมัติกับคู่ค้า" ═══════════

    public sealed record Option(string Value, string Label);
    public sealed record ActiveView(Guid Id, decimal? MaxAmount, Guid? DefaultPayBankAccountId, string? PayAccountLabel, string GrantedByName, DateTime GrantedAt, int TextVersion);
    public sealed record ScopeView(string Scope, string Label, bool RequiresCap, bool AllowsPayAccount, bool NeedsTypedConfirm, bool CompanyWide, bool ReaderWired,
        string? EffectNote, ActiveView? Active, bool Ok, string VerdictCode, string VerdictReason, string? CallerCannotGrantReason, decimal? SuggestedCap);
    public sealed record HistoryRow(Guid Id, string Scope, string ScopeLabel, decimal? MaxAmount, string GrantedByName, DateTime GrantedAt,
        string? WithdrawnByName, DateTime? WithdrawnAt, string? WithdrawReason, bool HashOk);
    public sealed record PartnerCard(Guid PartnershipId, string PartnerName, string? PartnerTaxId, string Status, string StatusLabel,
        List<ScopeView> Scopes, List<HistoryRow> History);
    public sealed record PageView(List<PartnerCard> Partners, List<Option> PayAccounts, string ConfirmPhrase, bool CompanyWideDrafts, bool CompanyWideWht);

    public async Task<PageView> ListAsync(Guid companyId, Guid callerUserId, CancellationToken ct = default)
    {
        // ฝ่ายค้าน H1: แถว Pending ที่เรามีความยินยอม (เคยเชื่อมแล้วกำลังเชื่อมใหม่) ต้องเห็น/ถอนได้ · Pending อื่นไม่แสดง (ไม่รั่วชื่อผู้ขอ)
        var parts = await _db.TradingPartnerships.AsNoTracking()
            .Where(p => (p.CompanyAId == companyId || p.CompanyBId == companyId)
                && (p.Status == TradingPartnershipStatus.Accepted || p.Status == TradingPartnershipStatus.Suspended
                    || (p.Status == TradingPartnershipStatus.Pending
                        && _db.PartnerAutomationConsents.Any(x => x.CompanyId == companyId && x.TradingPartnershipId == p.Id))))
            .OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
        var partnerIds = parts.Select(p => p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId).Distinct().ToList();
        var companies = await _db.Companies.AsNoTracking().Where(c => partnerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId }).ToDictionaryAsync(c => c.Id, ct);
        var pids = parts.Select(p => p.Id).ToList();
        // ฝ่ายค้าน L8(a): แถวที่ใช้อยู่ดึงครบเสมอ · จำกัดเฉพาะประวัติ
        var activeRows = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && pids.Contains(x.TradingPartnershipId) && x.WithdrawnAt == null).ToListAsync(ct);
        var rows = activeRows.Concat(await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && pids.Contains(x.TradingPartnershipId) && x.WithdrawnAt != null)
            .OrderByDescending(x => x.GrantedAt).Take(2000).ToListAsync(ct)).OrderByDescending(x => x.GrantedAt).ToList();
        var userIds = rows.Select(r => r.GrantedByUserId).Concat(rows.Where(r => r.WithdrawnByUserId != null).Select(r => r.WithdrawnByUserId!.Value)).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        string NameOf(Guid id) => names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "ผู้ใช้ที่ไม่อยู่แล้ว";
        var accounts = await PayAccountOptionsAsync(companyId, ct);
        var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId, ct);

        // สิทธิ์ของผู้เปิดหน้า (ปุ่ม "ยินยอม" ล็อก + เหตุ — ไม่ใช่กดแล้วเงียบ) · สิทธิ์ผู้ให้ของแต่ละแถว (ตรวจซ้ำ ณ ตอนนี้)
        var callerProblem = new Dictionary<Scope, string?>();
        foreach (var d in PartnerConsent.Scopes) callerProblem[d.Scope] = await GrantProblemForAsync(companyId, callerUserId, d.Scope);
        var grantorCache = new Dictionary<(Guid, Scope), bool>();

        var cards = new List<PartnerCard>();
        foreach (var p in parts)
        {
            var partnerId = p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId;
            companies.TryGetValue(partnerId, out var co);
            var mine = rows.Where(r => r.TradingPartnershipId == p.Id).ToList();
            var scopes = new List<ScopeView>();
            foreach (var d in PartnerConsent.Scopes)
            {
                PartnerConsent.Verdict v;
                ActiveView? av = null;
                if (d.CompanyWideToggle)
                    v = PartnerConsent.AllowsCompanyWide(d.Scope, PartnerConsent.CompanyWideToggleOn(d.Scope, cfg), p, companyId);
                else
                {
                    var active = mine.FirstOrDefault(r => r.Scope == d.Scope.ToString() && r.WithdrawnAt == null);
                    var ok = false;
                    if (active != null)
                    {
                        var key = (active.GrantedByUserId, d.Scope);
                        if (!grantorCache.TryGetValue(key, out ok))
                            grantorCache[key] = ok = await GrantProblemForAsync(companyId, active.GrantedByUserId, d.Scope) == null;
                        av = new ActiveView(active.Id, active.MaxAmount, active.DefaultPayBankAccountId,
                            active.DefaultPayBankAccountId is Guid b ? accounts.FirstOrDefault(a => a.Value == b.ToString())?.Label ?? "บัญชีที่ปิดใช้งานแล้ว" : null,
                            NameOf(active.GrantedByUserId), active.GrantedAt, active.TextVersion);
                    }
                    // ตัวแทนยอด: แสดงสถานะ "ถ้ายอดอยู่ในเพดาน" — ยอดจริงตรวจตอนท่อใช้
                    v = PartnerConsent.Allows(d.Scope, active, p, companyId, PartnerConsent.CapAmount.Thb(active?.MaxAmount ?? 0m), ok);
                }
                scopes.Add(new ScopeView(d.Scope.ToString(), d.Label, d.RequiresCap, d.AllowsPayAccount, d.NeedsTypedConfirm, d.CompanyWideToggle, d.ReaderWired,
                    PartnerConsent.EffectNote(d), av, v.Ok, v.Code, v.Reason, callerProblem[d.Scope],
                    d.RequiresCap ? PartnerExchangeRules.ReceiverLimit(companyId, p.CompanyAId, p.PurchaseDraftLimitA, p.PurchaseDraftLimitB) : null));
            }
            var history = mine.Take(50).Select(r => new HistoryRow(r.Id, r.Scope,
                PartnerConsent.TryParse(r.Scope, out var sc) ? PartnerConsent.Def(sc).Label : r.Scope, r.MaxAmount, NameOf(r.GrantedByUserId), r.GrantedAt,
                r.WithdrawnByUserId is Guid w ? NameOf(w) : null, r.WithdrawnAt, r.WithdrawReason, PartnerConsent.HashMatches(r))).ToList();
            var state = PartnerExchangeRules.StateOf(p.Status, p.InvitedByCompanyId == companyId);
            cards.Add(new PartnerCard(p.Id, co?.Name ?? "คู่ค้า", co?.TaxId, p.Status.ToString(), PartnerExchangeRules.StateLabel(state), scopes, history));
        }
        return new PageView(cards, accounts, PartnerConsent.ConfirmPhrase,
            cfg?.CreatePurchaseDraftsFromPartners == true, cfg?.SendWhtCertsToPartners == true);
    }

    /// <summary>บัญชีธนาคารของบริษัทผู้ให้ที่ใช้งานอยู่ (ตัวเลือก "จ่ายคู่ค้ารายนี้จากบัญชี X") — ไม่มีตัวเลือกเงินสด/บัญชีพัก (264-2-4)</summary>
    private async Task<List<Option>> PayAccountOptionsAsync(Guid companyId, CancellationToken ct)
    {
        var rows = await _db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .OrderBy(b => b.BankName).ThenBy(b => b.AccountName)
            .Select(b => new { b.Id, b.BankName, b.AccountName, b.AccountNumber }).ToListAsync(ct);
        return rows.Select(b => new Option(b.Id.ToString(),
            $"{b.BankName} · {b.AccountName} · ••{(b.AccountNumber.Length > 4 ? b.AccountNumber[^4..] : b.AccountNumber)}")).ToList();
    }

    // ═══════════ ให้ความยินยอม (สองขั้น) ═══════════

    public sealed record PreviewRequest(Guid PartnershipId, string? Scope, decimal? MaxAmount, Guid? DefaultPayBankAccountId);
    public sealed record PreviewView(string Scope, string Label, int TextVersion, string Text, string EvidenceHash, bool NeedsTypedConfirm, string ConfirmPhrase,
        decimal? MaxAmount, Guid? DefaultPayBankAccountId, string? PayAccountLabel = null);
    public sealed record GrantRequest(Guid PartnershipId, string? Scope, decimal? MaxAmount, Guid? DefaultPayBankAccountId,
        int TextVersion, string? EvidenceHash, string? TypedConfirm);

    /// <summary>ตรวจทุกอย่างที่ไม่ขึ้นกับขั้นยืนยัน — ใช้ร่วมระหว่างขั้นดูข้อความและขั้นยินยอม (ด่านเดียว)</summary>
    private async Task<(TradingPartnership P, Scope S, decimal? Max)> ValidateAsync(Guid companyId, Guid userId, Guid partnershipId, string? scopeName,
        decimal? maxAmount, Guid? payBank, CancellationToken ct)
    {
        if (!PartnerConsent.TryParse(scopeName, out var scope))
            throw new BusinessRuleException("ไม่รู้จักขอบเขตนี้", PartnerConsent.RuleCode, 400);
        var max = maxAmount is decimal m ? Math.Round(m, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
        if (PartnerConsent.RequestProblem(scope, max, payBank) is string rp)
            throw new BusinessRuleException(rp, PartnerConsent.RuleCode, PartnerConsent.Def(scope).CompanyWideToggle ? 409 : 400);
        var p = await _db.TradingPartnerships.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId), ct)
            ?? throw new KeyNotFoundException("ไม่พบคู่ค้า");
        if (p.Status != TradingPartnershipStatus.Accepted)
            throw new BusinessRuleException("ให้ความยินยอมได้เฉพาะคู่ค้าที่เชื่อมอยู่ (สถานะปัจจุบัน: " + p.Status + ")", PartnerConsent.RuleCode, 409);
        if (await GrantProblemForAsync(companyId, userId, scope) is string gp)
            throw new BusinessRuleException(gp, PartnerConsent.RuleCode, 403);
        if (payBank is Guid b && !await _db.BankAccounts.AsNoTracking().AnyAsync(x => x.Id == b && x.CompanyId == companyId && x.IsActive && !x.IsDeleted, ct))
            throw new BusinessRuleException("ไม่พบบัญชีจ่ายนี้ในบริษัท (หรือปิดใช้งานแล้ว)", PartnerConsent.RuleCode, 400);
        return (p, scope, max);
    }

    /// <summary>ขั้นที่หนึ่ง — ข้อความที่ผู้ให้จะเห็น + hash ของ (ข้อความ · ขอบเขต · เพดาน · บัญชี) · ไม่เขียนอะไร</summary>
    public async Task<PreviewView> PreviewAsync(Guid companyId, Guid userId, PreviewRequest req, CancellationToken ct = default)
    {
        var (p, scope, max) = await ValidateAsync(companyId, userId, req.PartnershipId, req.Scope, req.MaxAmount, req.DefaultPayBankAccountId, ct);
        var text = PartnerConsent.TextFor(scope, PartnerConsent.CurrentTextVersion)!;
        var d = PartnerConsent.Def(scope);
        var payLabel = req.DefaultPayBankAccountId is Guid b
            ? (await PayAccountOptionsAsync(companyId, ct)).FirstOrDefault(o => o.Value == b.ToString())?.Label : null;
        return new PreviewView(scope.ToString(), d.Label, PartnerConsent.CurrentTextVersion, text,
            PartnerConsent.Hash(companyId, p.Id, scope, max, req.DefaultPayBankAccountId, PartnerConsent.CurrentTextVersion, text),
            d.NeedsTypedConfirm, PartnerConsent.ConfirmPhrase, max, req.DefaultPayBankAccountId, payLabel);
    }

    /// <summary>
    /// ขั้นที่สอง — hash ที่ผู้ให้เห็นต้องตรงกับที่คำนวณจากค่าที่ส่งมา (ข้อความ/เพดาน/บัญชีไม่ถูกสลับระหว่างขั้น) + พิมพ์ยืนยัน (ขอบเขตลงบัญชีอัตโนมัติ) ·
    /// มีแถวที่ใช้อยู่แล้ว ⇒ ถอนแถวเดิม ("แทนที่") แล้วเพิ่มแถวใหม่ในธุรกรรมเดียว (append-only — ไม่แก้ทับ) · กดซ้ำพร้อมกัน ⇒ unique บางส่วนกัน ⇒ 409
    /// </summary>
    public async Task<PartnerAutomationConsent> GrantAsync(Guid companyId, Guid userId, GrantRequest req, CancellationToken ct = default)
    {
        var (p, scope, max) = await ValidateAsync(companyId, userId, req.PartnershipId, req.Scope, req.MaxAmount, req.DefaultPayBankAccountId, ct);
        var text = PartnerConsent.TextFor(scope, PartnerConsent.CurrentTextVersion)!;
        var hash = PartnerConsent.Hash(companyId, p.Id, scope, max, req.DefaultPayBankAccountId, PartnerConsent.CurrentTextVersion, text);
        if (PartnerConsent.ConfirmProblem(scope, req.TextVersion, req.EvidenceHash, hash, req.TypedConfirm) is string cp)
            throw new BusinessRuleException(cp, PartnerConsent.RuleCode, 409);

        var partnerId = p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId;
        var now = PartnerConsent.TruncateToSecond(DateTime.UtcNow);
        var name = scope.ToString();
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // ฝ่ายค้าน M4: ล็อกแถวที่ใช้อยู่ก่อนอ่าน — แทนที่/ถอนพร้อมกันรอกัน แล้วอ่านค่าล่าสุด (ไม่ชน trigger เป็น 500)
            await LockActiveForUpdateAsync(companyId, p.Id, name, ct);
            var prev = await _db.PartnerAutomationConsents
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.TradingPartnershipId == p.Id && x.Scope == name && x.WithdrawnAt == null, ct);
            if (prev != null)
            {
                MarkWithdrawn(prev, userId, now, "แทนที่ด้วยความยินยอมใหม่ (เปลี่ยนเพดาน/บัญชี/ยืนยันใหม่)", replaced: true);
                await _db.SaveChangesAsync(ct);   // ถอนก่อน — unique บางส่วนรับแถวใหม่ได้
            }
            var row = new PartnerAutomationConsent
            {
                CompanyId = companyId, TradingPartnershipId = p.Id, PartnerCompanyId = partnerId, Scope = name,
                MaxAmount = max, DefaultPayBankAccountId = req.DefaultPayBankAccountId, TextVersion = PartnerConsent.CurrentTextVersion,
                GrantedByUserId = userId, GrantedAt = now, CreatedBy = userId.ToString(), HashVersion = PartnerConsent.CurrentHashVersion,
                EvidenceHash = PartnerConsent.HashV2(companyId, p.Id, partnerId, scope, max, req.DefaultPayBankAccountId, PartnerConsent.CurrentTextVersion, text, userId, now),
            };
            _db.PartnerAutomationConsents.Add(row);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Create, EntityType = nameof(PartnerAutomationConsent), EntityId = row.Id.ToString(),
                OldValues = prev == null ? null : JsonSerializer.Serialize(new { replacedConsentId = prev.Id, maxAmount = prev.MaxAmount, defaultPayBankAccountId = prev.DefaultPayBankAccountId }),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "PartnerConsentGranted", partnershipId = p.Id, partnerCompanyId = partnerId, scope = name, maxAmount = max,
                    defaultPayBankAccountId = req.DefaultPayBankAccountId, textVersion = row.TextVersion, shownHash = hash, evidenceHash = row.EvidenceHash, hashVersion = row.HashVersion,
                    ruleCode = PartnerConsent.RuleCode, legalReference = PartnerConsent.LegalReference,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);
            return row;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" or PartnerConsentMigration.TriggerSqlState })
        {
            throw new BusinessRuleException("มีการเปลี่ยนความยินยอมขอบเขตนี้พร้อมกันอีกรายการ — รีเฟรชหน้าแล้วตรวจอีกครั้ง", PartnerConsent.RuleCode, 409);
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    // ═══════════ ถอน (ทันที · ต่อขอบเขต) ═══════════

    /// <summary>ถอนความยินยอม — มีผลทันทีกับงานที่ยังไม่ทำ (ท่อตรวจ <see cref="CheckAsync"/> ทุกรอบ) · งานที่ทำไปแล้วไม่ย้อน (F7) ·
    /// การถอน = แคบลง ⇒ ต้องการแค่สิทธิ์ตั้งค่าบริษัท (ด่านของ controller) ไม่ต้องมีสิทธิ์ของขอบเขต · แถวของบริษัทอื่น = ไม่พบ</summary>
    public async Task<PartnerAutomationConsent> WithdrawAsync(Guid companyId, Guid userId, Guid consentId, string? reason, CancellationToken ct = default)
    {
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // ฝ่ายค้าน M1/M4: FOR UPDATE — รอท่อที่ถือ FOR SHARE (CheckForWriteAsync) จนจบ · ถอนซ้อนกันอ่านค่าล่าสุดหลังได้ล็อก ⇒ 409 ไม่ใช่ 500
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"PartnerAutomationConsents\" WHERE \"Id\" = {consentId} AND \"CompanyId\" = {companyId} FOR UPDATE", ct);
            var row = await _db.PartnerAutomationConsents.FirstOrDefaultAsync(x => x.Id == consentId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบความยินยอมนี้");
            if (row.WithdrawnAt != null)
                throw new BusinessRuleException("ถอนความยินยอมนี้ไปแล้ว (หรือถูกแทนที่พร้อมกัน) — รีเฟรชหน้า", PartnerConsent.RuleCode, 409);
            MarkWithdrawn(row, userId, DateTime.UtcNow, PartnerConsent.CleanReason(reason), replaced: false);
            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);
            return row;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: PartnerConsentMigration.TriggerSqlState })
        {
            throw new BusinessRuleException("ความยินยอมนี้เปลี่ยนไปพร้อมกัน — รีเฟรชหน้าแล้วตรวจอีกครั้ง", PartnerConsent.RuleCode, 409);
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    private Task<int> LockActiveForUpdateAsync(Guid companyId, Guid partnershipId, string scope, CancellationToken ct)
        => _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PartnerAutomationConsents\" WHERE \"CompanyId\" = {companyId} AND \"TradingPartnershipId\" = {partnershipId} AND \"Scope\" = {scope} AND \"WithdrawnAt\" IS NULL FOR UPDATE", ct);

    /// <summary>
    /// ฝ่ายค้าน H1 — ระงับการเชื่อม ⇒ ถอนความยินยอมที่ใช้อยู่ทุกแถวของคู่นี้ (<b>ทั้งสองบริษัท</b> — ความยินยอมของอีกฝ่ายก็ใช้ไม่ได้แล้ว) ในธุรกรรมเดียวกับการระงับ ·
    /// audit ลง chain ของบริษัทเจ้าของแถวแต่ละแถว (replaced=false · เหตุ "ระงับการเชื่อม") · ผู้เรียกต้อง SaveChanges เอง ·
    /// ผู้เรียกเดียว: <c>CrossTenantWorkflowService.SuspendPartnershipAsync</c> · คู่กับ <see cref="PartnerConsent.Allows"/> ที่ตัดแถวที่ให้ก่อนการยอมรับครั้งล่าสุด (กันสองชั้น)
    /// </summary>
    public static async Task<int> WithdrawAllOnSuspendAsync(AccountingDbContext db, Guid partnershipId, Guid userId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PartnerAutomationConsents\" WHERE \"TradingPartnershipId\" = {partnershipId} AND \"WithdrawnAt\" IS NULL FOR UPDATE", ct);
        var rows = await db.PartnerAutomationConsents.Where(x => x.TradingPartnershipId == partnershipId && x.WithdrawnAt == null).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var r in rows) MarkWithdrawnCore(db, r, userId, now, SuspendReason, replaced: false);
        return rows.Count;
    }

    public const string SuspendReason = "ระงับการเชื่อมกับคู่ค้า — ความยินยอมหยุดทุกขอบเขต (ให้ใหม่ได้หลังเชื่อมกลับ)";

    private void MarkWithdrawn(PartnerAutomationConsent row, Guid userId, DateTime now, string? reason, bool replaced)
        => MarkWithdrawnCore(_db, row, userId, now, reason, replaced);

    private static void MarkWithdrawnCore(AccountingDbContext db, PartnerAutomationConsent row, Guid userId, DateTime now, string? reason, bool replaced)
    {
        row.WithdrawnAt = now;
        row.WithdrawnByUserId = userId;
        row.WithdrawReason = reason;
        row.UpdatedAt = now;
        row.UpdatedBy = userId.ToString();
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = row.CompanyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(PartnerAutomationConsent), EntityId = row.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { withdrawnAt = (DateTime?)null }),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerConsentWithdrawn", partnershipId = row.TradingPartnershipId, scope = row.Scope, reason, replaced,
                ruleCode = PartnerConsent.RuleCode, legalReference = PartnerConsent.LegalReference,
            }),
            Timestamp = now,
        });
    }

    // ═══════════ คู่ค้า "ขอ" (F9 — ไม่มีแถวความยินยอมเกิดจากฝั่งผู้ขอ) ═══════════

    /// <summary>บริษัท <paramref name="companyId"/> ขอให้คู่ค้าเปิดงานอัตโนมัติ — แจ้งกระดิ่งของคู่ค้าพร้อมลิงก์หน้าความยินยอมของเขา · ไม่เขียนอะไรในบริษัทคู่ค้านอกจากการแจ้ง ·
    /// ส่งไม่สำเร็จ = ผู้เรียกได้ข้อผิดพลาด (ไม่ตอบ "ส่งแล้ว" ทั้งที่ไม่ได้ส่ง)</summary>
    public async Task RequestFromPartnerAsync(Guid companyId, Guid userId, Guid partnershipId, IReadOnlyList<string>? scopeNames,
        Func<Task<bool>>? consumeQuota = null, CancellationToken ct = default)
    {
        var scopes = new List<Scope>();
        foreach (var n in scopeNames ?? Array.Empty<string>())
        {
            if (!PartnerConsent.TryParse(n, out var s)) throw new BusinessRuleException("ไม่รู้จักขอบเขต " + n, PartnerConsent.RuleCode, 400);
            if (PartnerConsent.Def(s).CompanyWideToggle) throw new BusinessRuleException("“" + PartnerConsent.Def(s).Label + "” เป็นค่าตั้งของทั้งบริษัทคู่ค้า — ขอเป็นรายคู่ไม่ได้", PartnerConsent.RuleCode, 400);
            if (!scopes.Contains(s)) scopes.Add(s);
        }
        if (scopes.Count == 0) throw new BusinessRuleException("เลือกอย่างน้อยหนึ่งขอบเขตที่จะขอ", PartnerConsent.RuleCode, 400);
        var p = await _db.TradingPartnerships.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId), ct)
            ?? throw new KeyNotFoundException("ไม่พบคู่ค้า");
        if (p.Status != TradingPartnershipStatus.Accepted)
            throw new BusinessRuleException("ขอได้เฉพาะคู่ค้าที่เชื่อมอยู่", PartnerConsent.RuleCode, 409);
        if (_notify == null) throw new BusinessRuleException("ระบบแจ้งเตือนไม่พร้อม — ส่งคำขอไม่ได้ในตอนนี้", PartnerConsent.RuleCode, 503);
        // ฝ่ายค้าน L8(d): นับโควตาหลังคำขอผ่านการตรวจแล้วเท่านั้น (คำขอผิดไม่กินโควตา)
        if (consumeQuota != null && !await consumeQuota())
            throw new BusinessRuleException($"ส่งคำขอถึงคู่ค้ารายนี้ครบ {RequestsPerPartnerPerDay} ครั้งของวันนี้แล้ว — ลองใหม่พรุ่งนี้", PartnerConsent.RuleCode, 429);
        var partnerId = p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId;
        var me = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "คู่ค้า";
        var labels = string.Join(" · ", scopes.Select(s => PartnerConsent.Def(s).Label));
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Create, EntityType = nameof(TradingPartnership), EntityId = p.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerConsentRequested", scopes = scopes.Select(s => s.ToString()).ToList(),
                ruleCode = PartnerConsent.RuleCode, legalReference = PartnerConsent.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        try
        {
            await _notify.DispatchAsync(partnerId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = $"🤝 {me} ขอให้เปิดงานอัตโนมัติกับคู่ค้า",
                Message = $"คำขอ: {labels} — ระบบของคุณจะไม่ทำอะไรจนกว่าผู้มีสิทธิ์ของคุณจะให้ความยินยอมเองที่หน้า “งานอัตโนมัติกับคู่ค้า” (ถอนได้ทุกเมื่อ)",
                ActionUrl = PartnerConsent.PageUrl(p.Id),
                EntityType = nameof(TradingPartnership), EntityId = p.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งคำขอความยินยอมจากบริษัท {Company} ถึงคู่ค้า {Partner} ไม่สำเร็จ", companyId, partnerId);
            // ฝ่ายค้าน L8(c): audit "ขอ" เขียนก่อนส่ง · ส่งล้ม ⇒ แถวที่สองบอกว่าล้ม (ร่องรอยครบทั้งสองทาง)
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(TradingPartnership), EntityId = p.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "PartnerConsentRequestFailed", ruleCode = PartnerConsent.RuleCode }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new BusinessRuleException("ส่งคำขอถึงคู่ค้าไม่สำเร็จ — ลองใหม่ภายหลัง", PartnerConsent.RuleCode, 502);
        }
    }
}
