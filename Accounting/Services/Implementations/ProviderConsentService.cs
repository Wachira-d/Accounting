using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 แพ็ก P2 — <b>ผู้เขียนเดียว</b>ของ <see cref="ProviderAutomationConsent"/> / <see cref="ProviderConsentRequest"/> และ<b>ตัวตรวจตอนเขียนตัวเดียว</b>
/// <see cref="CheckForWriteAsync"/> ที่ทุกการเขียนลงบัญชีลูกค้าจากเส้นสลิปภาษีต้องเรียกภายในธุรกรรม (ล็อก FOR SHARE จน commit — การถอนแทรกไม่ได้) ·
/// กติกาทั้งหมดอยู่ใน <see cref="ProviderConsent"/> (pure) · ทุกการเปลี่ยนเข้า audit chain ทั้งบริษัทลูกค้าและบริษัทผู้ให้บริการ (<c>AddChainedAuditLog</c>) ·
/// ผู้ให้บริการ = บริษัทผู้ให้บริการของแพลตฟอร์ม (<see cref="PlatformCompanyLookup"/>) ตัวเดียว
/// </summary>
public class ProviderConsentService
{
    public const string ViaGroupBind = "GroupBind";
    public const string ViaFirstPaymentRequest = "FirstPaymentRequest";
    public const string ViaFirstSlip = "FirstSlip";
    public const string ViaStaff = "Staff";

    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly ILogger<ProviderConsentService> _logger;
    private readonly INotificationEngine? _notify;

    public ProviderConsentService(AccountingDbContext db, IPermissionService perms, ILogger<ProviderConsentService> logger, INotificationEngine? notify = null)
    { _db = db; _perms = perms; _logger = logger; _notify = notify; }

    /// <summary>บริษัทผู้ให้บริการของแพลตฟอร์ม (null = ยังไม่ตั้ง ⇒ เส้นนี้ปิดทั้งหมด)</summary>
    public Task<Guid?> ProviderIdAsync(CancellationToken ct = default) => PlatformCompanyLookup.IdAsync(_db, ct);

    // ═══════════ สิทธิ์ผู้ให้ (272-1-7) ═══════════

    public async Task<string?> GrantProblemForAsync(Guid clientId, Guid providerId, Guid userId, CancellationToken ct = default)
    {
        var active = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);
        var membership = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == clientId && cu.UserId == userId).Select(cu => (UserRole?)cu.Role).FirstOrDefaultAsync(ct);
        if (membership == null) return "ผู้ใช้นี้ไม่ได้เป็นสมาชิกของบริษัท — ให้ความยินยอมไม่ได้";
        var providerMember = providerId != clientId && await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == providerId && cu.UserId == userId, ct);
        var taxFile = await _perms.HasPermissionAsync(clientId, userId, PermissionKeys.TaxFile);
        var settings = await _perms.HasPermissionAsync(clientId, userId, PermissionKeys.CompanySettingsEdit);
        return ProviderConsent.GrantProblem(active, membership == UserRole.Owner,
            n => n == PartnerConsent.Need.TaxFile ? taxFile : n == PartnerConsent.Need.CompanySettingsEdit && settings, providerMember);
    }

    // ═══════════ ตัวตรวจตอนใช้ ═══════════

    /// <summary>อ่านอย่างเดียว (หน้า/ป้าย/ตัดสินเบื้องต้น) — <b>ท่อที่เขียนบัญชีห้ามใช้</b> ใช้ <see cref="CheckForWriteAsync"/></summary>
    public Task<ProviderConsent.Verdict> CheckAsync(Guid clientId, Guid providerId, string? remittanceType, decimal? amount, CancellationToken ct = default)
        => EvaluateAsync(clientId, providerId, remittanceType, amount, ct);

    /// <summary>
    /// **API ของการเขียนลงบัญชีลูกค้า** — เรียก<b>ภายในธุรกรรม</b>ก่อนเขียนทุกครั้ง: ล็อก <c>FOR SHARE</c> แถวความยินยอมที่ใช้อยู่ ⇒ การถอน (UPDATE) รอจน commit/rollback ·
    /// ไม่มีธุรกรรม = ใช้ผิด ⇒ <see cref="InvalidOperationException"/> (ข้อผิดพลาดของโปรแกรม)
    /// </summary>
    public async Task<ProviderConsent.Verdict> CheckForWriteAsync(Guid clientId, Guid providerId, string? remittanceType, decimal? amount, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("CheckForWriteAsync ต้องเรียกภายในธุรกรรมของการเขียน (ล็อกความยินยอมไว้จน commit)");
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"ProviderAutomationConsents\" WHERE \"CompanyId\" = {clientId} AND \"ProviderCompanyId\" = {providerId} AND \"WithdrawnAt\" IS NULL FOR SHARE", ct);
        return await EvaluateAsync(clientId, providerId, remittanceType, amount, ct);
    }

    /// <summary>แถวที่ใช้อยู่ — ความยินยอมบนเว็บก่อน แล้วหนังสือมอบอำนาจ</summary>
    private async Task<ProviderAutomationConsent?> ActiveRowAsync(Guid clientId, Guid providerId, CancellationToken ct)
    {
        var rows = await _db.ProviderAutomationConsents.AsNoTracking()
            .Where(x => x.CompanyId == clientId && x.ProviderCompanyId == providerId && x.Scope == ProviderConsent.Scope && x.WithdrawnAt == null)
            .ToListAsync(ct);
        return rows.FirstOrDefault(r => r.EvidenceSource == ProviderConsent.SourceWeb) ?? rows.FirstOrDefault();
    }

    private async Task<ProviderConsent.Verdict> EvaluateAsync(Guid clientId, Guid providerId, string? type, decimal? amount, CancellationToken ct)
    {
        var row = await ActiveRowAsync(clientId, providerId, ct);
        var grantorOk = row != null && (row.EvidenceSource != ProviderConsent.SourceWeb
            || await GrantProblemForAsync(clientId, providerId, row.GrantedByUserId, ct) == null);
        return ProviderConsent.Allows(row, clientId, providerId, type, amount, grantorOk);
    }

    /// <summary>บัญชีชำระภาษีหลักที่ประกาศไว้ในความยินยอม (ใช้ได้ ณ ตอนนี้) · null = ไม่ประกาศ/ใช้ไม่ได้</summary>
    public async Task<(Guid? AccountId, string? BankCode)> DefaultPayAccountAsync(Guid clientId, Guid providerId, CancellationToken ct = default)
    {
        var row = await ActiveRowAsync(clientId, providerId, ct);
        if (row?.DefaultTaxPayBankAccountId is not Guid id) return (null, null);
        var b = await _db.BankAccounts.AsNoTracking().Where(x => x.Id == id && x.CompanyId == clientId && x.IsActive && !x.IsDeleted)
            .Select(x => new { x.Id, x.BankName }).FirstOrDefaultAsync(ct);
        return b == null ? (null, null) : (b.Id, ThaiBankCodes.FromText(b.BankName));
    }

    // ═══════════ คำขอ (ทาง B · T1/T2/T3 + ส่งซ้ำ) ═══════════

    /// <summary>เปิดคำขอถ้ายังไม่มีความยินยอมที่ใช้ได้และยังไม่มีคำขอ Open · คืน (คำขอ, เพิ่งสร้าง) · มีความยินยอมแล้ว ⇒ (null, false) ·
    /// คำขอใหม่ ⇒ กระดิ่ง/อีเมลถึงเจ้าของบริษัทลูกค้า (ส่งล้ม = audit แถวที่สอง ไม่ย้อนคำขอ)</summary>
    public async Task<(ProviderConsentRequest? Row, bool IsNew)> EnsureRequestAsync(Guid clientId, Guid providerId, string via, Guid? byUserId, CancellationToken ct = default)
    {
        if (clientId == providerId) return (null, false);
        if ((await CheckAsync(clientId, providerId, null, null, ct)).Ok) return (null, false);
        var now = DateTime.UtcNow;
        var open = await _db.ProviderConsentRequests.FirstOrDefaultAsync(r => r.CompanyId == clientId && r.ProviderCompanyId == providerId && r.Status == "Open", ct);
        if (open != null && open.ExpiresAt > now) return (open, false);
        if (open != null) { open.Status = "Expired"; open.ResolvedAt = now; open.UpdatedAt = now; }
        var row = new ProviderConsentRequest
        {
            CompanyId = clientId, ProviderCompanyId = providerId, Status = "Open", RequestedVia = via, RequestedByUserId = byUserId,
            ExpiresAt = now.AddDays(ProviderConsent.RequestLifetimeDays), CreatedBy = byUserId?.ToString() ?? "system:provider-consent",
        };
        _db.ProviderConsentRequests.Add(row);
        foreach (var cid in new[] { clientId, providerId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserId = byUserId, Action = AuditAction.Create, EntityType = nameof(ProviderConsentRequest), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderConsentRequested", via, clientId, providerId, ruleCode = ProviderConsent.RuleCode, legalReference = ProviderConsent.LegalReference }),
                Timestamp = now,
            });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();   // คำขอพร้อมกันสองทาง — อีกทางสร้างแล้ว
            return (await _db.ProviderConsentRequests.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == clientId && r.ProviderCompanyId == providerId && r.Status == "Open", ct), false);
        }
        await NotifyClientAsync(clientId, providerId, row, ct);
        return (row, true);
    }

    private async Task NotifyClientAsync(Guid clientId, Guid providerId, ProviderConsentRequest row, CancellationToken ct)
    {
        try
        {
            if (_notify == null) throw new InvalidOperationException("ระบบแจ้งเตือนไม่พร้อม");
            var provider = await _db.Companies.AsNoTracking().Where(c => c.Id == providerId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "NextAcc";
            await _notify.DispatchAsync(clientId, NotificationEvents.ProviderConsentRequested, new NotificationContext
            {
                Title = "🧾 " + provider + " ขออนุญาตบันทึกการชำระภาษีจากสลิปแทนบริษัทนี้",
                Message = "ระบบจะไม่บันทึกอะไรลงบัญชีของบริษัทจนกว่าเจ้าของ (หรือผู้มีสิทธิ์ยื่นภาษี + ตั้งค่าบริษัท) จะอนุญาตบนเว็บ · คำขอหมดอายุใน "
                    + ProviderConsent.RequestLifetimeDays + " วัน · ถอนได้ทุกเมื่อ",
                ActionUrl = ProviderConsent.PageUrl(clientId), EntityType = nameof(ProviderConsentRequest), EntityId = row.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งคำขอความยินยอม (สลิปภาษี) ถึงบริษัท {Client} ไม่สำเร็จ", clientId);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = providerId, Action = AuditAction.Update, EntityType = nameof(ProviderConsentRequest), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderConsentRequestNotifyFailed", clientId, ruleCode = ProviderConsent.RuleCode }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>สลิปที่พักรอความยินยอมของบริษัทลูกค้านี้</summary>
    public Task<int> HeldCountAsync(Guid clientId, Guid providerId, CancellationToken ct = default)
        => _db.TaxSlipEvidences.AsNoTracking().CountAsync(s => s.CompanyId == providerId && s.ClientCompanyId == clientId && s.Status == TaxSlipIntakeService.Statuses.AwaitingConsent, ct);

    // ═══════════ การ์ดในศูนย์บิล (server computes · page displays) ═══════════

    public sealed record TypeOption(string Type, string Label, bool Enabled, bool Selected);
    public sealed record AccountOption(Guid Id, string Label);
    public sealed record HeldSlip(Guid Id, string Label, DateTime ReceivedAt);
    public sealed record ActiveView(Guid Id, string Source, string SourceLabel, IReadOnlyList<string> TaxTypeLabels, decimal? MaxAmount, string? PayAccountLabel,
        string GrantedByName, DateTime GrantedAt, int TextVersion, bool HashOk);
    public sealed record CardView(bool Available, string? UnavailableReason, string ProviderName, string StatusLabel, ProviderConsent.Verdict Verdict,
        ActiveView? Active, ActiveView? PowerOfAttorney, IReadOnlyList<TypeOption> Types, IReadOnlyList<AccountOption> PayAccounts, IReadOnlyList<HeldSlip> Held,
        int TextVersion, string Text, string ConfirmPhrase, string? CallerCannotGrantReason, IReadOnlyList<string> Grantors, bool OpenRequest);

    public async Task<CardView> CardAsync(Guid clientId, Guid userId, CancellationToken ct = default)
    {
        var providerId = await ProviderIdAsync(ct);
        var text = ProviderConsent.TextFor(ProviderConsent.CurrentTextVersion)!;
        var types = ProviderConsent.TaxTypes.Select(t => new TypeOption(t.Type, t.Label, t.ReaderWired, t.ReaderWired)).ToList();
        if (providerId is not Guid pid || pid == clientId)
            return new CardView(false, providerId == clientId ? "บริษัทนี้คือสำนักงานบัญชีผู้ให้บริการเอง" : "ยังไม่ได้ตั้งบริษัทผู้ให้บริการของแพลตฟอร์ม", "NextAcc", "ไม่ใช้กับบริษัทนี้",
                new ProviderConsent.Verdict(false, ProviderConsent.Codes.NoConsent, "-", null), null, null, types, Array.Empty<AccountOption>(), Array.Empty<HeldSlip>(),
                ProviderConsent.CurrentTextVersion, text, ProviderConsent.ConfirmPhrase, null, Array.Empty<string>(), false);
        var providerName = await _db.Companies.AsNoTracking().Where(c => c.Id == pid).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "NextAcc";
        var verdict = await CheckAsync(clientId, pid, null, null, ct);
        var rows = await _db.ProviderAutomationConsents.AsNoTracking()
            .Where(x => x.CompanyId == clientId && x.ProviderCompanyId == pid && x.WithdrawnAt == null).ToListAsync(ct);
        var accounts = await _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == clientId && b.IsActive && !b.IsDeleted)
            .Select(b => new { b.Id, b.BankName, b.AccountNumber }).ToListAsync(ct);
        string AccLabel(Guid? id)
        {
            var a = accounts.FirstOrDefault(x => x.Id == id);
            if (a == null) return null!;
            var digits = new string((a.AccountNumber ?? "").Where(char.IsAsciiDigit).ToArray());
            return (ThaiBankCodes.ByCode(ThaiBankCodes.FromText(a.BankName))?.ThaiName ?? a.BankName ?? "บัญชี") + (digits.Length >= 4 ? " ••" + digits[^4..] : "");
        }
        var userIds = rows.Select(r => r.GrantedByUserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.FullName }).ToListAsync(ct);
        ActiveView? View(ProviderAutomationConsent? r) => r == null ? null : new ActiveView(r.Id, r.EvidenceSource,
            r.EvidenceSource == ProviderConsent.SourceWeb ? "ยืนยันบนเว็บ" : "หนังสือมอบอำนาจ (แนบโดยพนักงาน · ไม่เปิดการบันทึกเอง)",
            ProviderConsent.ParseTypes(r.TaxTypes).Select(ProviderConsent.LabelOf).ToList(), r.MaxAmount, r.DefaultTaxPayBankAccountId is Guid g ? AccLabel(g) : null,
            ContactLineBindRule.MaskName(names.Where(n => n.Id == r.GrantedByUserId).Select(n => n.FullName).FirstOrDefault()) ?? "-",
            r.GrantedAt, r.TextVersion, ProviderConsent.HashMatches(r));
        var web = rows.FirstOrDefault(r => r.EvidenceSource == ProviderConsent.SourceWeb);
        var poa = rows.FirstOrDefault(r => r.EvidenceSource == ProviderConsent.SourcePowerOfAttorney);
        var held = await _db.TaxSlipEvidences.AsNoTracking()
            .Where(s => s.CompanyId == pid && s.ClientCompanyId == clientId && s.Status == TaxSlipIntakeService.Statuses.AwaitingConsent)
            .OrderBy(s => s.CreatedAt).Select(s => new { s.Id, s.Amount, s.ControlCodeLast4, s.CreatedAt }).ToListAsync(ct);
        var problem = await GrantProblemForAsync(clientId, pid, userId, ct);
        var grantors = await GrantorNamesAsync(clientId, pid, ct);
        var openReq = await _db.ProviderConsentRequests.AsNoTracking().AnyAsync(r => r.CompanyId == clientId && r.ProviderCompanyId == pid && r.Status == "Open" && r.ExpiresAt > DateTime.UtcNow, ct);
        var status = verdict.Ok ? "อนุญาตแล้ว" : poa != null ? "มีหนังสือมอบอำนาจ — พนักงานกดบันทึกทีละใบ" : "ยังไม่อนุญาต";
        if (held.Count > 0 && !verdict.Ok) status += $" · มีสลิปรออยู่ {held.Count} ใบ";
        return new CardView(true, null, providerName, status, verdict, View(web), View(poa),
            web == null ? types : types.Select(t => t with { Selected = t.Enabled && ProviderConsent.ParseTypes(web.TaxTypes).Contains(t.Type) }).ToList(),
            accounts.Select(a => new AccountOption(a.Id, AccLabel(a.Id))).ToList(),
            held.Select(h => new HeldSlip(h.Id, (h.Amount is decimal a ? a.ToString("N2") + " บาท" : "ยอดอ่านไม่ได้") + (h.ControlCodeLast4 != null ? " · Control ••" + h.ControlCodeLast4 : ""), h.CreatedAt)).ToList(),
            ProviderConsent.CurrentTextVersion, text, ProviderConsent.ConfirmPhrase, problem, grantors, openReq);
    }

    /// <summary>ผู้มีสิทธิ์ให้ความยินยอม (ชื่อปิดบัง) — การ์ดบอกว่า "ต้องให้ใครกด" แทนปุ่มที่กดแล้วล้ม</summary>
    private async Task<IReadOnlyList<string>> GrantorNamesAsync(Guid clientId, Guid providerId, CancellationToken ct)
    {
        var members = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == clientId)
            .Select(cu => new { cu.UserId, cu.User.FullName }).ToListAsync(ct);
        var list = new List<string>();
        foreach (var m in members.Take(50))
            if (await GrantProblemForAsync(clientId, providerId, m.UserId, ct) == null)
                list.Add(ContactLineBindRule.MaskName(m.FullName) ?? "-");
        return list;
    }

    // ═══════════ ให้ · ถอน · หนังสือมอบอำนาจ ═══════════

    public sealed record GrantInput(IReadOnlyList<string>? TaxTypes, decimal? MaxAmount, Guid? PayBankAccountId, bool IncludeHeld, int ShownVersion, string? ShownHash, string? Typed);

    /// <summary>ขั้นดู — hash ที่ผู้ให้ต้องส่งกลับในขั้นยืนยัน (เนื้อเดียวกับที่จะเก็บ)</summary>
    public async Task<(string Hash, string Text, int Version, string TaxTypes)> PreviewAsync(Guid clientId, Guid userId, GrantInput req, CancellationToken ct = default)
    {
        var pid = await ProviderIdAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการของแพลตฟอร์ม", ProviderConsent.RuleCode, 409);
        var (csv, problem) = ProviderConsent.NormalizeTypes(req.TaxTypes);
        if (problem != null) throw new BusinessRuleException(problem, ProviderConsent.RuleCode, 400);
        await ValidateInputAsync(clientId, req, ct);
        return (ProviderConsent.PreviewHash(clientId, pid, csv, req.MaxAmount, req.PayBankAccountId, req.IncludeHeld, userId),
            ProviderConsent.TextFor(ProviderConsent.CurrentTextVersion)!, ProviderConsent.CurrentTextVersion, csv);
    }

    private async Task ValidateInputAsync(Guid clientId, GrantInput req, CancellationToken ct)
    {
        if (req.MaxAmount is decimal m && (m <= 0m || m > 999_999_999_999m)) throw new BusinessRuleException("เพดานต่อสลิปต้องมากกว่า 0 บาท", ProviderConsent.RuleCode, 400);
        if (req.PayBankAccountId is Guid b && !await _db.BankAccounts.AsNoTracking().AnyAsync(x => x.Id == b && x.CompanyId == clientId && x.IsActive && !x.IsDeleted, ct))
            throw new BusinessRuleException("บัญชีชำระภาษีที่เลือกไม่ใช่บัญชีที่ใช้งานของบริษัทนี้", ProviderConsent.RuleCode, 400);
    }

    public async Task<ProviderAutomationConsent> GrantAsync(Guid clientId, Guid userId, GrantInput req, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var pid = await ProviderIdAsync(ct) ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการของแพลตฟอร์ม", ProviderConsent.RuleCode, 409);
        if (pid == clientId) throw new BusinessRuleException("บริษัทผู้ให้บริการให้ความยินยอมกับตัวเองไม่ได้", ProviderConsent.RuleCode, 400);
        if (await GrantProblemForAsync(clientId, pid, userId, ct) is { } gp) throw new BusinessRuleException(gp, ProviderConsent.RuleCode, 403);
        var (csv, problem) = ProviderConsent.NormalizeTypes(req.TaxTypes);
        if (problem != null) throw new BusinessRuleException(problem, ProviderConsent.RuleCode, 400);
        await ValidateInputAsync(clientId, req, ct);
        var preview = ProviderConsent.PreviewHash(clientId, pid, csv, req.MaxAmount, req.PayBankAccountId, req.IncludeHeld, userId);
        if (ProviderConsent.ConfirmProblem(req.ShownVersion, req.ShownHash, preview, req.Typed) is { } cp) throw new BusinessRuleException(cp, ProviderConsent.RuleCode, 409);
        var now = PartnerConsent.TruncateToSecond(DateTime.UtcNow);
        var text = ProviderConsent.TextFor(ProviderConsent.CurrentTextVersion)!;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var prev = await _db.ProviderAutomationConsents
            .Where(x => x.CompanyId == clientId && x.ProviderCompanyId == pid && x.Scope == ProviderConsent.Scope && x.EvidenceSource == ProviderConsent.SourceWeb && x.WithdrawnAt == null)
            .ToListAsync(ct);
        foreach (var p in prev) { p.WithdrawnAt = now; p.WithdrawnByUserId = userId; p.WithdrawReason = "แทนด้วยความยินยอมใหม่"; }
        if (prev.Count > 0) await _db.SaveChangesAsync(ct);
        var row = new ProviderAutomationConsent
        {
            CompanyId = clientId, ProviderCompanyId = pid, Scope = ProviderConsent.Scope, TaxTypes = csv, MaxAmount = req.MaxAmount,
            DefaultTaxPayBankAccountId = req.PayBankAccountId, IncludesHeldSlips = req.IncludeHeld, TextVersion = ProviderConsent.CurrentTextVersion,
            HashVersion = ProviderConsent.HashVersion, EvidenceSource = ProviderConsent.SourceWeb, GrantedByUserId = userId, GrantedAt = now,
            IpAddress = Clip(ip, 64), UserAgent = Clip(userAgent, 300), CreatedBy = userId.ToString(),
        };
        row.EvidenceHash = ProviderConsent.Hash(clientId, pid, csv, req.MaxAmount, req.PayBankAccountId, req.IncludeHeld, row.TextVersion, text, userId, now);
        _db.ProviderAutomationConsents.Add(row);
        await _db.ProviderConsentRequests.Where(r => r.CompanyId == clientId && r.ProviderCompanyId == pid && r.Status == "Open")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Granted").SetProperty(r => r.ResolvedAt, (DateTime?)now).SetProperty(r => r.ConsentId, (Guid?)row.Id), ct);
        // สลิปที่พักไว้ + ผู้ให้ไม่ติ๊ก "รวมสลิปที่รออยู่" ⇒ คิวพนักงาน (ไม่ลบเงียบ · ดีไซน์ §5.7)
        var movedHeld = 0;
        if (!req.IncludeHeld)
            movedHeld = await _db.TaxSlipEvidences.Where(s => s.CompanyId == pid && s.ClientCompanyId == clientId && s.Status == TaxSlipIntakeService.Statuses.AwaitingConsent)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TaxSlipIntakeService.Statuses.ToStaff)
                    .SetProperty(x => x.Reason, "ผู้อนุญาตไม่รวมสลิปที่ส่งก่อนวันอนุญาต — ให้พนักงานตรวจ").SetProperty(x => x.UpdatedAt, (DateTime?)now), ct);
        foreach (var cid in new[] { clientId, pid })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserId = userId, Action = AuditAction.Create, EntityType = nameof(ProviderAutomationConsent), EntityId = row.Id.ToString(), IpAddress = row.IpAddress,
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderConsentGranted", clientId, providerId = pid, taxTypes = csv, row.MaxAmount, includeHeld = req.IncludeHeld,
                    payAccount = req.PayBankAccountId, textVersion = row.TextVersion, hash = row.EvidenceHash, ruleCode = ProviderConsent.RuleCode, legalReference = ProviderConsent.LegalReference }),
                Timestamp = now,
            });
        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" or Round272Migration.TriggerSqlState })
        {
            throw new BusinessRuleException("ความยินยอมถูกเปลี่ยนระหว่างนี้ — โหลดหน้าใหม่แล้วลองอีกครั้ง", ProviderConsent.RuleCode, 409);
        }
        // รอบ 275 แพ็ก D (L6): สลิปที่พักไว้ย้ายเข้าคิวพนักงาน ⇒ กระดิ่งผู้ให้บริการ (เดิมย้ายเงียบ — พนักงานไม่รู้ว่ามีงานรอ)
        if (movedHeld > 0 && _notify != null)
        {
            try
            {
                await _notify.DispatchAsync(pid, NotificationEvents.TaxSlipNeedsStaff, new NotificationContext
                {
                    Title = "🧾 สลิปภาษีที่พักไว้ " + movedHeld + " ใบเข้าคิวพนักงาน",
                    Message = "ลูกค้าอนุญาตแล้วแต่ไม่รวมสลิปที่ส่งก่อนวันอนุญาต — ตรวจและบันทึกเองที่โต๊ะสลิป",
                    ActionUrl = "/pages/tax-slip-desk.html",
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "แจ้งพนักงานเรื่องสลิปที่พักไว้เข้าคิวไม่สำเร็จ"); }
        }
        return row;
    }

    public async Task<ProviderAutomationConsent> WithdrawAsync(Guid clientId, Guid userId, Guid consentId, string? reason, CancellationToken ct = default)
    {
        // ผู้มีสิทธิ์ให้ = ผู้มีสิทธิ์ถอน · เจ้าของถอนได้เสมอ (ดีไซน์ §5.7) — ผู้ให้บริการของแถวคือตัวที่ตรวจ (ไม่พึ่งค่าตั้งปัจจุบัน)
        var provider = await _db.ProviderAutomationConsents.AsNoTracking().Where(x => x.Id == consentId && x.CompanyId == clientId).Select(x => (Guid?)x.ProviderCompanyId)
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("ไม่พบความยินยอม");
        var isOwner = await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == clientId && cu.UserId == userId && cu.Role == UserRole.Owner, ct);
        if (!isOwner && await GrantProblemForAsync(clientId, provider, userId, ct) is { } gp) throw new BusinessRuleException(gp, ProviderConsent.RuleCode, 403);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"ProviderAutomationConsents\" WHERE \"Id\" = {consentId} AND \"CompanyId\" = {clientId} FOR UPDATE", ct);
        var row = await _db.ProviderAutomationConsents.FirstOrDefaultAsync(x => x.Id == consentId && x.CompanyId == clientId, ct)
            ?? throw new KeyNotFoundException("ไม่พบความยินยอม");
        if (row.WithdrawnAt != null) throw new BusinessRuleException("ถอนไปแล้ว", ProviderConsent.RuleCode, 409);
        var now = DateTime.UtcNow;
        row.WithdrawnAt = now; row.WithdrawnByUserId = userId; row.WithdrawReason = Clip(reason, 500);
        foreach (var cid in new[] { clientId, row.ProviderCompanyId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserId = userId, Action = AuditAction.Update, EntityType = nameof(ProviderAutomationConsent), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderConsentWithdrawn", clientId, providerId = row.ProviderCompanyId, ruleCode = ProviderConsent.RuleCode }),
                Timestamp = now,
            });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    /// <summary>272-1-6 (D-6): พนักงานของผู้ให้บริการแนบหนังสือมอบอำนาจที่ลูกค้าลงนาม ⇒ แถว <c>staff-poa</c> — <b>ไม่</b>เปิดการบันทึกเอง ·
    /// ผู้แนบต้องเป็นสมาชิกบริษัทผู้ให้บริการ · ไฟล์ต้องอยู่ในบริษัทผู้ให้บริการ</summary>
    public async Task<ProviderAutomationConsent> RecordPowerOfAttorneyAsync(Guid providerId, Guid clientId, Guid staffUserId, Guid attachmentId, CancellationToken ct = default)
    {
        if (await ProviderIdAsync(ct) != providerId || providerId == clientId)
            throw new BusinessRuleException("ใช้ได้เฉพาะบริษัทผู้ให้บริการกับบริษัทลูกค้า", ProviderConsent.RuleCode, 403);
        if (!await _db.FileAttachments.AsNoTracking().AnyAsync(f => f.Id == attachmentId && f.CompanyId == providerId, ct))
            throw new BusinessRuleException("ไม่พบไฟล์หนังสือมอบอำนาจ", ProviderConsent.RuleCode, 404);
        var now = PartnerConsent.TruncateToSecond(DateTime.UtcNow);
        var text = ProviderConsent.TextFor(ProviderConsent.CurrentTextVersion)!;
        var csv = string.Join(",", ProviderConsent.TaxTypes.Where(t => t.ReaderWired).Select(t => t.Type).OrderBy(x => x, StringComparer.Ordinal));
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var prev = await _db.ProviderAutomationConsents.Where(x => x.CompanyId == clientId && x.ProviderCompanyId == providerId
            && x.EvidenceSource == ProviderConsent.SourcePowerOfAttorney && x.WithdrawnAt == null).ToListAsync(ct);
        foreach (var p in prev) { p.WithdrawnAt = now; p.WithdrawnByUserId = staffUserId; p.WithdrawReason = "แทนด้วยหนังสือมอบอำนาจฉบับใหม่"; }
        if (prev.Count > 0) await _db.SaveChangesAsync(ct);
        var row = new ProviderAutomationConsent
        {
            CompanyId = clientId, ProviderCompanyId = providerId, Scope = ProviderConsent.Scope, TaxTypes = csv, TextVersion = ProviderConsent.CurrentTextVersion,
            HashVersion = ProviderConsent.HashVersionWithEvidence, EvidenceSource = ProviderConsent.SourcePowerOfAttorney, EvidenceAttachmentId = attachmentId,
            GrantedByUserId = staffUserId, GrantedAt = now, CreatedBy = staffUserId.ToString(),
        };
        // ฝ่ายค้าน 272 M4: ไฟล์หลักฐาน + แหล่งหลักฐานอยู่ใน hash (สลับไฟล์ภายหลัง = Tampered)
        row.EvidenceHash = ProviderConsent.HashV2(clientId, providerId, csv, null, null, false, row.TextVersion, text, staffUserId, now,
            ProviderConsent.SourcePowerOfAttorney, attachmentId);
        _db.ProviderAutomationConsents.Add(row);
        foreach (var cid in new[] { clientId, providerId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserId = staffUserId, Action = AuditAction.Create, EntityType = nameof(ProviderAutomationConsent), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderPowerOfAttorneyRecorded", clientId, providerId, attachmentId, autoRecord = false, ruleCode = ProviderConsent.RuleCode }),
                Timestamp = now,
            });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyClientPoaAsync(clientId, row, "แนบหนังสือมอบอำนาจ — รอพนักงานอีกคนของสำนักงานบัญชีอนุมัติ", ct);
        return row;
    }

    /// <summary>ฝ่ายค้าน 272 M4: ผู้อนุมัติคนที่สองของหนังสือมอบอำนาจ — สมาชิกบริษัทผู้ให้บริการที่<b>ไม่ใช่ผู้แนบ</b> · ประทับครั้งเดียว (trigger) · audit สองบริษัท + กระดิ่งเจ้าของลูกค้า</summary>
    public async Task<ProviderAutomationConsent> ApprovePowerOfAttorneyAsync(Guid providerId, Guid clientId, Guid approverUserId, Guid consentId, CancellationToken ct = default)
    {
        if (await ProviderIdAsync(ct) != providerId || providerId == clientId)
            throw new BusinessRuleException("ใช้ได้เฉพาะบริษัทผู้ให้บริการกับบริษัทลูกค้า", ProviderConsent.RuleCode, 403);
        if (!await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == providerId && cu.UserId == approverUserId, ct))
            throw new BusinessRuleException("ผู้อนุมัติต้องเป็นพนักงานของสำนักงานบัญชี", ProviderConsent.RuleCode, 403);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"ProviderAutomationConsents\" WHERE \"Id\" = {consentId} AND \"CompanyId\" = {clientId} FOR UPDATE", ct);
        var row = await _db.ProviderAutomationConsents.FirstOrDefaultAsync(x => x.Id == consentId && x.CompanyId == clientId && x.ProviderCompanyId == providerId
            && x.EvidenceSource == ProviderConsent.SourcePowerOfAttorney, ct) ?? throw new KeyNotFoundException("ไม่พบหนังสือมอบอำนาจ");
        if (row.WithdrawnAt != null) throw new BusinessRuleException("หนังสือมอบอำนาจนี้ถูกแทน/ถอนแล้ว", ProviderConsent.RuleCode, 409);
        if (row.ApprovedAt != null) throw new BusinessRuleException("อนุมัติไปแล้ว", ProviderConsent.RuleCode, 409);
        if (row.GrantedByUserId == approverUserId) throw new BusinessRuleException("ผู้แนบหนังสือมอบอำนาจอนุมัติเองไม่ได้ — ให้พนักงานอีกคนตรวจ", ProviderConsent.RuleCode, 403);
        var now = DateTime.UtcNow;
        row.ApprovedByUserId = approverUserId; row.ApprovedAt = now;
        foreach (var cid in new[] { clientId, providerId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserId = approverUserId, Action = AuditAction.Update, EntityType = nameof(ProviderAutomationConsent), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "ProviderPowerOfAttorneyApproved", clientId, providerId, uploader = row.GrantedByUserId, ruleCode = ProviderConsent.RuleCode }),
                Timestamp = now,
            });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyClientPoaAsync(clientId, row, "หนังสือมอบอำนาจได้รับการอนุมัติแล้ว — พนักงานกดบันทึกการชำระภาษีจากสลิปแทนบริษัททีละใบได้ (ระบบไม่บันทึกเอง)", ct);
        return row;
    }

    private async Task NotifyClientPoaAsync(Guid clientId, ProviderAutomationConsent row, string message, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(clientId, NotificationEvents.ProviderConsentRequested, new NotificationContext
            {
                Title = "🧾 หนังสือมอบอำนาจถึงสำนักงานบัญชี NextAcc", Message = message + " · ตรวจ/ถอนได้ที่ศูนย์บิล",
                ActionUrl = ProviderConsent.PageUrl(clientId), EntityType = nameof(ProviderAutomationConsent), EntityId = row.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "แจ้งเจ้าของบริษัท {Client} เรื่องหนังสือมอบอำนาจไม่สำเร็จ", clientId); }
    }

    private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > n ? s.Trim()[..n] : s.Trim());
}
