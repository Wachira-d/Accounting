using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Data migration wizard (Task 1 of ERP upgrade). Three-stage flow:
///
///   1. COA Mapper — operator pastes/uploads legacy account list,
///      service creates a MigrationSession with AccountMapping rows
///      (LegacyCode + LegacyName + LegacyDebit/Credit). รอบ 205 ทีม D: ระบบแนะนำบัญชีให้ทุกแถว
///      (<see cref="ImportAccountCodeResolver"/> → AI ImportAccountMap) · ถึงเกณฑ์ = เลือกให้ · ต่ำกว่า = แนะนำ
///   2. Validation — <see cref="OpeningTrialBalanceRules"/> (ตัวตัดสินเดียวกับศูนย์นำเข้า) แยก <b>บล็อก</b> (ไม่ลงตัว · ยังไม่เลือกบัญชี ·
///      งวดปิด · บัญชีนอกผังบริษัท — ทุกข้อมีทางไปต่อ) กับ<b>คำเตือน</b>ที่รับทราบตอน commit (คำตัดสินข้อ 150 — เดิมคำเตือน "⚠️" ถูกนับเป็น
///      issue ที่บล็อก commit)
///   3. Commit — atomically writes OpeningBalance rows for the target
///      fiscal period; locks the session as Committed. เก็บยอดเดิมที่ถูกทับไว้ ⇒ ย้อนคืนได้ (เดิมย้อนลบเฉพาะแถวที่สร้างใหม่
///      แถวที่ถูกทับค้างยอดของ session ที่ย้อนแล้ว)
/// </summary>
public class MigrationWizardService : IMigrationWizardService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<MigrationWizardService> _logger;
    private readonly ImportAccountMatcher _accounts;
    private readonly Accounting.Services.Ai.IAiFeedbackRecorder? _feedback;

    public MigrationWizardService(AccountingDbContext db, ILogger<MigrationWizardService> logger,
        ImportAccountMatcher? accounts = null, Accounting.Services.Ai.IAiFeedbackRecorder? feedback = null)
    {
        _db = db;
        _logger = logger;
        _accounts = accounts ?? new ImportAccountMatcher(db);
        _feedback = feedback;
    }

    public async Task<MigrationSession> CreateSessionAsync(Guid companyId, string name, MigrationType type, Guid? targetPeriodId, string userId)
    {
        if (targetPeriodId.HasValue && !await _db.FiscalPeriods.AnyAsync(p => p.Id == targetPeriodId.Value && p.CompanyId == companyId))
            throw new BusinessRuleException("ไม่พบงวดบัญชีที่เลือกในบริษัทนี้ — เลือกงวดใหม่", "MIGRATION-PERIOD");
        var session = new MigrationSession
        {
            CompanyId = companyId,
            SessionName = string.IsNullOrWhiteSpace(name) ? "ย้ายระบบ " + DateTime.UtcNow.ToString("yyyy-MM-dd") : name.Trim(),
            MigrationType = type,
            Status = MigrationStatus.Draft,
            TargetFiscalPeriodId = targetPeriodId,
            CreatedBy = userId,
        };
        _db.MigrationSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    /// <summary>Bulk-insert legacy accounts as a starting point for COA mapping.</summary>
    public async Task UploadLegacyAccountsAsync(Guid companyId, Guid sessionId,
        List<(string Code, string? Name, decimal Debit, decimal Credit)> rows, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        EnsureEditable(session);

        // Replace any existing rows — re-upload supersedes the previous draft.
        var existing = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        _db.AccountMappings.RemoveRange(existing);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Code)) continue;
            _db.AccountMappings.Add(new AccountMapping
            {
                MigrationSessionId = sessionId,
                LegacyCode = row.Code.Trim(),
                LegacyName = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim(),
                LegacyDebit = Math.Round(row.Debit, 2, MidpointRounding.AwayFromZero),
                LegacyCredit = Math.Round(row.Credit, 2, MidpointRounding.AwayFromZero),
                CreatedBy = userId,
            });
        }
        session.Status = MigrationStatus.Mapping;
        await _db.SaveChangesAsync();
    }

    public async Task<MigrationPasteResult> UploadLegacyTextAsync(Guid companyId, Guid sessionId, string? text, string userId,
        CancellationToken ct = default)
    {
        var parsed = TrialBalancePasteParser.Parse(text);
        if (parsed.Rows.Count == 0)
        {
            var why = parsed.Skipped.Count == 0 ? "ไม่มีข้อมูล"
                : string.Join(" · ", parsed.Skipped.Take(3).Select(s => $"บรรทัด {s.LineNo}: {s.Reason}"));
            throw new BusinessRuleException(
                $"ไม่พบบรรทัดงบทดลองที่อ่านได้ ({why}) — วางคอลัมน์ รหัส · ชื่อ · เดบิต · เครดิต (คัดลอกจาก Excel ได้เลย)", "MIGRATION-PASTE");
        }
        await UploadLegacyAccountsAsync(companyId, sessionId,
            parsed.Rows.Select(r => (r.Code, r.Name, r.Debit, r.Credit)).ToList(), userId);
        var suggestions = await SuggestMappingsAsync(companyId, sessionId, userId, allowAi: true, ct);
        return new MigrationPasteResult(parsed.Rows.Count, parsed.Skipped, parsed.Delimiter, suggestions);
    }

    /// <summary>Apply a batch of mapping decisions. Idempotent.</summary>
    public async Task ApplyMappingsAsync(Guid companyId, Guid sessionId,
        Dictionary<string, Guid> codeToAccountId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        EnsureEditable(session);

        // ด่านข้ามบริษัท (คำตัดสินข้อ 150 (3)) — เดิมรับ id ใด ๆ ⇒ ผูกยอดยกมาเข้าบัญชีของบริษัทอื่นได้
        var ids = codeToAccountId.Values.Where(v => v != Guid.Empty).Distinct().ToList();
        var valid = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountCode })
            .ToDictionaryAsync(a => a.Id, a => a.AccountCode);
        var bad = ids.Where(i => !valid.ContainsKey(i)).ToList();
        if (bad.Count > 0)
            throw new BusinessRuleException("บัญชีที่เลือกไม่อยู่ในผังบัญชีของบริษัทนี้ (หรือถูกลบแล้ว) — เลือกบัญชีใหม่", "MIGRATION-ACCOUNT");

        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        var learned = new List<(Guid FeedbackId, string Chosen, bool Accepted)>();
        foreach (var r in rows)
        {
            if (!codeToAccountId.TryGetValue(r.LegacyCode, out var newAccount)) continue;
            r.MappedAccountId = newAccount == Guid.Empty ? null : newAccount;
            r.UpdatedBy = userId;
            r.UpdatedAt = DateTime.UtcNow;
            // กฎเหล็ก #1: ปิดลูปของคำแนะนำ AI ImportAccountMap — คำตอบที่ผู้ใช้เลือก (ตรง = ยอมรับ · ต่าง = แก้)
            if (r.AccountMapAiFeedbackId is { } fid && r.MappedAccountId is { } chosen)
                learned.Add((fid, valid[chosen], r.SuggestedAccountId == chosen));
        }
        if (session.Status == MigrationStatus.ReadyToCommit) session.Status = MigrationStatus.Mapping;   // ต้องตรวจใหม่
        await _db.SaveChangesAsync();

        if (_feedback != null)
            foreach (var (fid, chosen, accepted) in learned)
                await _feedback.RecordUserChoiceAsync(fid, chosen, accepted, CancellationToken.None, UserChoiceSource.Explicit);
    }

    public async Task<IReadOnlyList<MigrationMappingSuggestion>> SuggestMappingsAsync(Guid companyId, Guid sessionId, string userId,
        bool allowAi = true, CancellationToken ct = default)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        EnsureEditable(session);
        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId)
            .OrderBy(a => a.LegacyCode).ToListAsync(ct);
        var chart = await _accounts.LoadChartAsync(companyId, ct, excludeSessionId: sessionId);
        var pending = rows.Where(r => r.MappedAccountId == null && r.LegacyCode != OpeningTrialBalanceRules.DifferenceLineKey).ToList();
        var queries = pending.Select(r => new ImportAccountQuery(r.Id.ToString("N"), r.LegacyCode, r.LegacyName)).ToList();
        var results = await _accounts.ResolveAsync(companyId, queries, new ImportResolveOptions(allowAi), ct, chart);

        var outList = new List<MigrationMappingSuggestion>(results.Count);
        foreach (var (row, res) in pending.Zip(results))
        {
            var top = res.Candidates.FirstOrDefault();
            row.SuggestedAccountId = res.AccountId ?? top?.EntityId;
            row.SuggestionKind = res.AccountId.HasValue ? res.Kind : top?.Kind;
            row.SuggestionScore = res.AccountId.HasValue ? res.Score : top?.Score;
            row.AccountMapAiFeedbackId = res.AiFeedbackId;
            row.AccountMapUsedAi = res.UsedAi;
            if (res.AccountId.HasValue)
            {
                // ถึงเกณฑ์ ⇒ ระบบเลือกให้ (ผู้ใช้เปลี่ยนได้ — ค่าผู้ใช้ชนะเสมอ: แถวที่ผู้ใช้เลือกแล้วไม่อยู่ใน pending)
                row.MappedAccountId = res.AccountId;
                row.UpdatedBy = "system:suggest";
                row.UpdatedAt = DateTime.UtcNow;
            }
            outList.Add(ToSuggestion(row, res, chart));
        }
        if (session.Status == MigrationStatus.Draft) session.Status = MigrationStatus.Mapping;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Migration {Session}: แนะนำบัญชี {Count} แถว · เลือกให้ {Auto} · UsedAi {Ai}",
            sessionId, outList.Count, outList.Count(s => s.AutoApplied), outList.Count(s => s.UsedAi));
        return outList;
    }

    private static MigrationMappingSuggestion ToSuggestion(AccountMapping row, ImportAccountMatchResult res, ImportAccountChart chart)
    {
        var sugg = row.SuggestedAccountId is { } sid ? chart.Find(sid) : null;
        var kind = row.SuggestionKind ?? "None";
        return new MigrationMappingSuggestion(row.Id, row.LegacyCode, row.LegacyName, row.SuggestedAccountId, sugg?.Code, sugg?.Name,
            kind, SuggestionLabel(kind, row.AccountMapUsedAi), row.SuggestionScore ?? 0m, res.AccountId.HasValue, row.AccountMapUsedAi,
            res.AccountId.HasValue
                ? $"ระบบเลือกให้ ({ImportAccountCodeResolver.KindLabel(kind)} · มั่นใจ {res.Score:0.00}) — เปลี่ยนได้"
                : sugg != null ? $"แนะนำ {sugg.Code} {sugg.Name} (มั่นใจ {row.SuggestionScore:0.00}) — กด \"ใช้คำแนะนำ\" หรือเลือกเอง"
                : "ไม่พบบัญชีที่ใกล้เคียง — เลือกเอง หรือสร้างบัญชีในผังบัญชีก่อน",
            res.Candidates);
    }

    /// <summary>ป้ายแหล่งของคำแนะนำ — "🤖 AI แนะนำ" เฉพาะเมื่อ AI ตอบจริง (ป้ายซื่อสัตย์ · กฎเหล็ก #1)</summary>
    public static string SuggestionLabel(string? kind, bool usedAi)
        => usedAi && kind == "Ai" ? ImportAccountCodeResolver.KindLabel("Ai")
            : kind is null or "None" ? "—"
            : ImportAccountCodeResolver.KindLabel(kind == "Ai" ? null : kind);

    public async Task<MigrationValidationResult> ValidateSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        var verdict = await EvaluateAsync(session);
        if (MigrationWizardLabels.IsEditable(session.Status))
        {
            session.Status = verdict.CanCommit ? MigrationStatus.ReadyToCommit : MigrationStatus.Validating;
            await _db.SaveChangesAsync();
        }
        return ToResult(verdict);
    }

    public async Task<MigrationValidationResult> PostDifferenceToAccountAsync(Guid companyId, Guid sessionId, Guid accountId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        EnsureEditable(session);
        var account = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            ?? throw new BusinessRuleException("ไม่พบบัญชีที่เลือกในผังบัญชีของบริษัทนี้ — เลือกบัญชีผลต่างยกมาใหม่", "MIGRATION-ACCOUNT");

        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        var oldDiff = rows.Where(r => r.LegacyCode == OpeningTrialBalanceRules.DifferenceLineKey).ToList();
        _db.AccountMappings.RemoveRange(oldDiff);
        var others = rows.Except(oldDiff).ToList();
        var (dr, cr) = OpeningTrialBalanceRules.BalancingAmounts(others.Sum(r => r.LegacyDebit), others.Sum(r => r.LegacyCredit));
        if (dr != 0m || cr != 0m)
            _db.AccountMappings.Add(new AccountMapping
            {
                MigrationSessionId = sessionId,
                LegacyCode = OpeningTrialBalanceRules.DifferenceLineKey,
                LegacyName = $"ผลต่างยกมา → {account.AccountCode} {account.AccountName} (ผู้ใช้เลือก)",
                LegacyDebit = dr,
                LegacyCredit = cr,
                MappedAccountId = account.Id,
                SuggestionKind = "User",
                CreatedBy = userId,
            });
        if (session.Status == MigrationStatus.ReadyToCommit) session.Status = MigrationStatus.Mapping;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Migration {Session}: ลงผลต่าง Dr {Dr} Cr {Cr} เข้าบัญชี {Code} (ผู้ใช้ {User})",
            sessionId, dr, cr, account.AccountCode, userId);
        return await ValidateSessionAsync(companyId, sessionId);
    }

    /// <summary>ตรวจด้วยตัวตัดสินกลาง — ไม่บันทึกอะไร (ผู้เรียกตัดสินสถานะเอง)</summary>
    private async Task<OpeningTbVerdict> EvaluateAsync(MigrationSession session)
    {
        var companyId = session.CompanyId;
        var rows = await _db.AccountMappings.AsNoTracking().Where(a => a.MigrationSessionId == session.Id).ToListAsync();
        var chart = await _accounts.LoadChartAsync(companyId, excludeSessionId: session.Id);
        FiscalPeriod? period = session.TargetFiscalPeriodId is { } pid
            ? await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.CompanyId == companyId)
            : null;
        var mappedIds = rows.Where(r => r.MappedAccountId.HasValue).Select(r => r.MappedAccountId!.Value).Distinct().ToList();
        var existing = period == null ? new HashSet<Guid>()
            : (await _db.OpeningBalances.AsNoTracking()
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == period.Id && mappedIds.Contains(o.AccountId))
                .Select(o => o.AccountId).ToListAsync()).ToHashSet();
        var lines = rows.Select(r =>
        {
            var acc = r.MappedAccountId is { } id ? chart.Find(id) : null;
            return new OpeningTbLine(r.LegacyCode, r.LegacyName, r.LegacyDebit, r.LegacyCredit, acc,
                MappedButMissing: r.MappedAccountId.HasValue && acc == null);
        }).ToList();
        return OpeningTrialBalanceRules.Evaluate(lines,
            new OpeningTbContext(period != null, period?.Name, period?.Status, existing));
    }

    private static MigrationValidationResult ToResult(OpeningTbVerdict v) => new(v.Errors, v.Warnings, v.TotalDebit, v.TotalCredit);

    /// <summary>บันทึกสรุปการ commit — ใช้ย้อน (คืนยอดเดิมที่ถูกทับ) · ตัวเลขที่เขียนจริงเก็บไว้เทียบว่าแถวถูกแก้หลัง commit ไหม</summary>
    private sealed record CommitSummary(
        int TotalAccounts, decimal TotalDebit, decimal TotalCredit,
        IReadOnlyList<string>? AcknowledgedWarnings,
        IReadOnlyList<CreatedOpening>? Created,
        IReadOnlyList<OverwrittenOpening>? Overwritten);
    private sealed record CreatedOpening(Guid Id, Guid AccountId, decimal Debit, decimal Credit);
    private sealed record OverwrittenOpening(Guid Id, Guid AccountId, decimal PriorDebit, decimal PriorCredit, string? PriorNotes,
        decimal Debit, decimal Credit);

    private static readonly JsonSerializerOptions SummaryJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Undo a committed migration session. Deletes the OpeningBalance
    /// rows the commit created and RESTORES the prior values of rows it overwrote (รอบ 205 ทีม D — เดิมคืนไม่ได้).
    /// Refuses if the target fiscal period has been closed/locked. Rows edited by hand after the commit are left alone
    /// and reported. Sets session.Status = RolledBack and keeps both summaries for the audit trail.</summary>
    public async Task<MigrationSession> RollbackAsync(Guid companyId, Guid sessionId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        if (session.Status != MigrationStatus.Committed)
            throw new BusinessRuleException("ย้อนได้เฉพาะชุดที่ลงยอดแล้ว (Committed)", "MIGRATION-ROLLBACK");
        if (!session.TargetFiscalPeriodId.HasValue)
            throw new BusinessRuleException("ชุดนี้ไม่มีงวดบัญชีปลายทาง — ไม่มีอะไรให้ย้อน", "MIGRATION-ROLLBACK");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.Id == session.TargetFiscalPeriodId.Value && p.CompanyId == companyId)
            ?? throw new BusinessRuleException("ไม่พบงวดบัญชีเป้าหมาย", "MIGRATION-ROLLBACK");
        if (period.Status == FiscalPeriodStatus.Closed || period.Status == FiscalPeriodStatus.Locked)
            throw new BusinessRuleException($"งวดบัญชี {period.Name} ปิดแล้ว — เปิดงวดที่หน้า งวดบัญชี ก่อนย้อน", "MIGRATION-ROLLBACK");

        var summary = ReadSummary(session.ImportSummaryJson);
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            int removed = 0, restored = 0, skipped = 0;
            if (summary is { } sum && (sum.Created != null || sum.Overwritten != null))
            {
                var ids = (sum.Created ?? Array.Empty<CreatedOpening>()).Select(c => c.Id)
                    .Concat((sum.Overwritten ?? Array.Empty<OverwrittenOpening>()).Select(o => o.Id)).ToList();
                var current = await _db.OpeningBalances
                    .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == period.Id && ids.Contains(o.Id))
                    .ToDictionaryAsync(o => o.Id);
                foreach (var c in sum.Created ?? Array.Empty<CreatedOpening>())
                {
                    if (!current.TryGetValue(c.Id, out var ob)) continue;
                    if (ob.OpeningDebit != c.Debit || ob.OpeningCredit != c.Credit) { skipped++; continue; }   // แก้มือหลัง commit — ไม่แตะ
                    _db.OpeningBalances.Remove(ob);
                    removed++;
                }
                foreach (var o in sum.Overwritten ?? Array.Empty<OverwrittenOpening>())
                {
                    if (!current.TryGetValue(o.Id, out var ob)) { skipped++; continue; }
                    if (ob.OpeningDebit != o.Debit || ob.OpeningCredit != o.Credit) { skipped++; continue; }
                    ob.OpeningDebit = o.PriorDebit;
                    ob.OpeningCredit = o.PriorCredit;
                    ob.Notes = o.PriorNotes;
                    ob.UpdatedBy = userId;
                    ob.UpdatedAt = DateTime.UtcNow;
                    restored++;
                }
            }
            else
            {
                // ชุดที่ commit ก่อนรอบ 205 (ไม่มีภาพยอดเดิม) — พฤติกรรมเดิม: ลบเฉพาะแถวที่ติดป้ายของ session นี้
                var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId && a.MappedAccountId.HasValue).ToListAsync();
                var accountIds = rows.Select(r => r.MappedAccountId!.Value).Distinct().ToList();
                var toRemove = await _db.OpeningBalances
                    .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == period.Id && accountIds.Contains(o.AccountId))
                    .ToListAsync();
                var sessionTag = session.Id.ToString("N")[..8];
                var ours = toRemove.Where(o => o.Notes != null && o.Notes.Contains(sessionTag)).ToList();
                _db.OpeningBalances.RemoveRange(ours);
                removed = ours.Count;
                skipped = toRemove.Count - ours.Count;
            }

            session.Status = MigrationStatus.RolledBack;
            session.ImportSummaryJson = JsonSerializer.Serialize(new
            {
                originalCommit = session.ImportSummaryJson,
                rollback = new { rolledBackAt = DateTime.UtcNow, rolledBackBy = userId, rowsRemoved = removed, rowsRestored = restored, rowsSkipped = skipped },
            });
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(userId, out var uid) ? uid : null,
                Action = AuditAction.Delete,
                EntityType = nameof(MigrationSession),
                EntityId = session.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { ruleCode = "MIGRATION-TB-ROLLBACK", removed, restored, skipped }),
                Timestamp = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return session;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<MigrationSession> CommitAsync(Guid companyId, Guid sessionId, string userId,
        IReadOnlyCollection<string>? acknowledgedWarningCodes = null)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        EnsureEditable(session);

        // ตรวจใหม่ทุกครั้ง (ไม่เชื่อสถานะที่เคยตรวจ — ข้อมูลอาจเปลี่ยนหลังตรวจ) · ตัวตัดสินเดียวกับหน้าตรวจ/ศูนย์นำเข้า
        var verdict = await EvaluateAsync(session);
        if (!verdict.CanCommit)
            throw new BusinessRuleException(
                "ลงยอดยกมาไม่ได้: " + string.Join(" · ", verdict.Errors.Select(e => e.Message + (e.WayForward != null ? " → " + e.WayForward : ""))),
                "MIGRATION-TB-BLOCKED");
        var pendingAck = OpeningTrialBalanceRules.Unacknowledged(verdict, acknowledgedWarningCodes);
        if (pendingAck.Count > 0)
            throw new BusinessRuleException(
                "ยังไม่ได้รับทราบคำเตือน: " + string.Join(" · ", pendingAck.Select(w => OpeningTrialBalanceRules.Label(w.Code)))
                + " — ติ๊กรับทราบที่หน้าตรวจแล้วกดลงยอดอีกครั้ง", "MIGRATION-TB-ACK");

        var periodId = session.TargetFiscalPeriodId!.Value;   // verdict ผ่าน ⇒ มีงวดแน่
        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Aggregate any duplicate target accounts (multiple legacy codes
            // can map to one) — collapse to a single OpeningBalance row per
            // (period, account) so we don't violate the unique index.
            var aggregated = rows
                .Where(r => r.MappedAccountId.HasValue)
                .GroupBy(r => r.MappedAccountId!.Value)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(r => r.LegacyDebit), Credit = g.Sum(r => r.LegacyCredit) })
                .ToList();
            var accountIds = aggregated.Select(a => a.AccountId).ToList();
            var existingRows = await _db.OpeningBalances
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == periodId && accountIds.Contains(o.AccountId))
                .ToDictionaryAsync(o => o.AccountId);

            var created = new List<OpeningBalance>();
            var overwritten = new List<OverwrittenOpening>();
            var tag = $"Migrated via session {session.Id.ToString("N")[..8]}";
            foreach (var a in aggregated)
            {
                if (existingRows.TryGetValue(a.AccountId, out var existing))
                {
                    overwritten.Add(new OverwrittenOpening(existing.Id, a.AccountId, existing.OpeningDebit, existing.OpeningCredit,
                        existing.Notes, a.Debit, a.Credit));
                    existing.OpeningDebit = a.Debit;
                    existing.OpeningCredit = a.Credit;
                    existing.Notes = tag;
                    existing.UpdatedBy = userId;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    var ob = new OpeningBalance
                    {
                        CompanyId = companyId,
                        FiscalPeriodId = periodId,
                        AccountId = a.AccountId,
                        OpeningDebit = a.Debit,
                        OpeningCredit = a.Credit,
                        Notes = tag,
                        CreatedBy = userId,
                    };
                    _db.OpeningBalances.Add(ob);
                    created.Add(ob);
                }
            }

            var summary = new CommitSummary(rows.Count, verdict.TotalDebit, verdict.TotalCredit,
                verdict.Warnings.Select(w => w.Code).Distinct().ToList(),
                created.Select(c => new CreatedOpening(c.Id, c.AccountId, c.OpeningDebit, c.OpeningCredit)).ToList(),
                overwritten);
            session.Status = MigrationStatus.Committed;
            session.CompletedAt = DateTime.UtcNow;
            session.CompletedBy = userId;
            session.ImportSummaryJson = JsonSerializer.Serialize(summary, SummaryJson);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(userId, out var uid) ? uid : null,
                Action = AuditAction.Create,
                EntityType = nameof(MigrationSession),
                EntityId = session.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    ruleCode = "MIGRATION-TB-COMMIT",
                    totalDebit = verdict.TotalDebit,
                    totalCredit = verdict.TotalCredit,
                    created = created.Count,
                    overwritten = overwritten.Count,
                    acknowledgedWarnings = summary.AcknowledgedWarnings,
                }),
                Timestamp = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return session;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            // ถอยเฉพาะของที่เมธอดนี้แตะ (ยอดยกมา · แถว audit · session) — ไม่ Clear ทั้ง tracker เพราะ DbContext เป็น scoped ร่วมกับ
            // ผู้เรียก (pipeline ของศูนย์นำเข้า) ซึ่งอาจมีของค้างที่ไม่เกี่ยวกับเรา
            foreach (var e in _db.ChangeTracker.Entries().Where(e => e.Entity is OpeningBalance or AuditLog or MigrationSession).ToList())
            {
                if (e.State == EntityState.Added) e.State = EntityState.Detached;
                else if (e.State != EntityState.Unchanged && e.State != EntityState.Detached) await e.ReloadAsync();
            }
            var failed = await LoadSessionAsync(companyId, sessionId);
            failed.Status = MigrationStatus.Failed;
            failed.ImportSummaryJson = JsonSerializer.Serialize(new { error = ex.Message });
            await _db.SaveChangesAsync();
            throw;
        }
    }

    private static CommitSummary? ReadSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CommitSummary>(json, SummaryJson); }
        catch (JsonException) { return null; }   // สรุปรูปเดิม (ก่อนรอบ 205) — ผู้เรียกตกไปทางเดิม
    }

    public async Task<List<MigrationSession>> ListSessionsAsync(Guid companyId)
        => await _db.MigrationSessions
            .Where(m => m.CompanyId == companyId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

    public async Task<MigrationSession> GetSessionAsync(Guid companyId, Guid sessionId)
        => await LoadSessionAsync(companyId, sessionId);

    public async Task<List<AccountMapping>> GetMappingsAsync(Guid companyId, Guid sessionId)
    {
        await LoadSessionAsync(companyId, sessionId);  // ownership check
        return await _db.AccountMappings
            .Include(a => a.MappedAccount)
            .Where(a => a.MigrationSessionId == sessionId)
            .OrderBy(a => a.LegacyCode)
            .ToListAsync();
    }

    private static void EnsureEditable(MigrationSession session)
    {
        if (!MigrationWizardLabels.IsEditable(session.Status))
            throw new BusinessRuleException(
                $"ชุดนี้{MigrationWizardLabels.Status(session.Status)}แล้ว — แก้ไม่ได้ สร้างชุดใหม่ (หรือย้อนการลงยอดก่อน)", "MIGRATION-STATUS");
    }

    private async Task<MigrationSession> LoadSessionAsync(Guid companyId, Guid sessionId)
        => await _db.MigrationSessions
            .FirstOrDefaultAsync(m => m.Id == sessionId && m.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ migration session");
}

/// <summary>ป้ายภาษาไทยของ migration wizard — หน้าเว็บแสดงจากเซิร์ฟเวอร์ (F2 ข้อ 5 · เดิมหน้าเว็บถือตารางสถานะเอง)</summary>
public static class MigrationWizardLabels
{
    public static string Status(MigrationStatus s) => s switch
    {
        MigrationStatus.Draft => "ร่าง",
        MigrationStatus.Mapping => "กำลังจับคู่บัญชี",
        MigrationStatus.Validating => "ตรวจแล้ว — มีสิ่งที่ต้องแก้",
        MigrationStatus.ReadyToCommit => "พร้อมลงยอด",
        MigrationStatus.Committed => "ลงยอดแล้ว",
        MigrationStatus.Failed => "ลงยอดไม่สำเร็จ",
        MigrationStatus.Cancelled => "ยกเลิก",
        MigrationStatus.RolledBack => "ย้อนแล้ว",
        _ => s.ToString(),
    };

    public static string Type(MigrationType t) => t switch
    {
        MigrationType.ChartOfAccounts => "ผังบัญชี",
        MigrationType.TrialBalance => "งบทดลอง",
        MigrationType.SubLedgerAR => "ลูกหนี้คงค้าง",
        MigrationType.SubLedgerAP => "เจ้าหนี้คงค้าง",
        MigrationType.SubLedgerFixedAssets => "ทะเบียนสินทรัพย์",
        MigrationType.SubLedgerInventory => "สินค้าคงเหลือ",
        MigrationType.OpeningBalances => "ยอดยกมา",
        _ => t.ToString(),
    };

    /// <summary>ขั้นของ wizard ที่ทำอยู่ (0 อัปโหลด · 1 จับคู่ · 2 ตรวจ/ลงยอด) — หน้าเว็บไม่ถือตารางสถานะเอง</summary>
    public static int Step(MigrationStatus s) => s switch
    {
        MigrationStatus.Draft or MigrationStatus.Cancelled => 0,
        MigrationStatus.Mapping => 1,
        _ => 2,
    };

    public static bool IsEditable(MigrationStatus s)
        => s is MigrationStatus.Draft or MigrationStatus.Mapping or MigrationStatus.Validating or MigrationStatus.ReadyToCommit or MigrationStatus.Failed;
}
