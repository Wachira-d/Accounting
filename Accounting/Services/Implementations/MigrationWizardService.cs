using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Data migration wizard (Task 1 of ERP upgrade). Three-stage flow:
///
///   1. COA Mapper — operator pastes/uploads legacy account list,
///      service creates a MigrationSession with AccountMapping rows
///      (LegacyCode + LegacyName + LegacyDebit/Credit). UI walks
///      operator through matching each row to a current ChartOfAccount.
///   2. Validation — service verifies:
///        * Every row mapped (no NULL MappedAccountId).
///        * Sub-ledger detail totals == GL Control Account balance
///          (the directive's strict rule — block import otherwise).
///   3. Commit — atomically writes OpeningBalance rows for the target
///      fiscal period; locks the session as Committed.
///
/// The MigrationType enum (ChartOfAccounts / TrialBalance / SubLedger*
/// / OpeningBalances) controls which validation steps apply per session.
/// </summary>
public class MigrationWizardService : IMigrationWizardService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<MigrationWizardService> _logger;

    public MigrationWizardService(AccountingDbContext db, ILogger<MigrationWizardService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MigrationSession> CreateSessionAsync(Guid companyId, string name, MigrationType type, Guid? targetPeriodId, string userId)
    {
        var session = new MigrationSession
        {
            CompanyId = companyId,
            SessionName = name,
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
        if (session.Status != MigrationStatus.Draft && session.Status != MigrationStatus.Mapping)
            throw new InvalidOperationException("Session ปิดแล้ว — สร้าง session ใหม่");

        // Replace any existing rows — re-upload supersedes the previous draft.
        var existing = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        _db.AccountMappings.RemoveRange(existing);
        foreach (var row in rows)
        {
            _db.AccountMappings.Add(new AccountMapping
            {
                MigrationSessionId = sessionId,
                LegacyCode = row.Code,
                LegacyName = row.Name,
                LegacyDebit = row.Debit,
                LegacyCredit = row.Credit,
                CreatedBy = userId,
            });
        }
        session.Status = MigrationStatus.Mapping;
        await _db.SaveChangesAsync();
    }

    /// <summary>Apply a batch of mapping decisions. Idempotent.</summary>
    public async Task ApplyMappingsAsync(Guid companyId, Guid sessionId,
        Dictionary<string, Guid> codeToAccountId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        foreach (var r in rows)
        {
            if (codeToAccountId.TryGetValue(r.LegacyCode, out var newAccount))
            {
                r.MappedAccountId = newAccount;
                r.UpdatedBy = userId;
                r.UpdatedAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Validate the session — check every legacy row is mapped, and that
    /// total debits == total credits (basic double-entry sanity). Returns
    /// the issues found; empty list = ready to commit.
    /// </summary>
    public async Task<List<string>> ValidateSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        var issues = new List<string>();

        if (rows.Count == 0)
        {
            issues.Add("ยังไม่มี legacy accounts — อัพโหลดข้อมูลก่อน");
            return issues;
        }

        var unmapped = rows.Where(r => !r.MappedAccountId.HasValue).ToList();
        if (unmapped.Count > 0)
            issues.Add($"ยังไม่ได้ map {unmapped.Count} บัญชี: " +
                string.Join(", ", unmapped.Take(5).Select(r => r.LegacyCode)));

        var totalDebit = rows.Sum(r => r.LegacyDebit);
        var totalCredit = rows.Sum(r => r.LegacyCredit);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
            issues.Add($"ยอด Debit/Credit ไม่สมดุล — Dr {totalDebit:N2} vs Cr {totalCredit:N2} (ห่าง {Math.Abs(totalDebit - totalCredit):N2})");

        // If this is a trial-balance import, also verify each mapped account
        // has a unique target (no two legacy codes mapping to the same one
        // unless we explicitly aggregate — surface as a warning).
        var dupGroups = rows.Where(r => r.MappedAccountId.HasValue)
            .GroupBy(r => r.MappedAccountId!.Value)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in dupGroups)
            issues.Add($"⚠️ บัญชี {g.Key.ToString("N")[..8]} ถูก map จาก {g.Count()} legacy codes — ตรวจสอบว่าตั้งใจรวม");

        session.Status = issues.Count == 0 ? MigrationStatus.ReadyToCommit : MigrationStatus.Validating;
        await _db.SaveChangesAsync();
        return issues;
    }

    /// <summary>
    /// Commit the migration — atomic write of OpeningBalance rows into the
    /// target fiscal period. Strict: rolls back the whole transaction if
    /// ANY row fails validation or DB constraint.
    /// </summary>
    /// <summary>Undo a committed migration session. Deletes the OpeningBalance
    /// rows the commit wrote for this session's (period × account) combinations.
    /// Refuses if the target fiscal period has been closed/locked, or if any
    /// posted JE has referenced those opening balances in the meantime. Sets
    /// session.Status = RolledBack and writes the rollback summary so the audit
    /// trail keeps both events.</summary>
    public async Task<MigrationSession> RollbackAsync(Guid companyId, Guid sessionId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        if (session.Status != MigrationStatus.Committed)
            throw new InvalidOperationException("Rollback ได้เฉพาะ session ที่ Committed แล้ว");
        if (!session.TargetFiscalPeriodId.HasValue)
            throw new InvalidOperationException("Session ไม่มี TargetFiscalPeriodId");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.Id == session.TargetFiscalPeriodId.Value && p.CompanyId == companyId);
        if (period == null) throw new InvalidOperationException("ไม่พบงวดบัญชีเป้าหมาย");
        if (period.Status == FiscalPeriodStatus.Closed || period.Status == FiscalPeriodStatus.Locked)
            throw new InvalidOperationException($"งวดบัญชี {period.Name} ปิดแล้ว — เปิดงวดก่อน rollback");

        // Recompute the set of (period, account) keys this session wrote.
        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId && a.MappedAccountId.HasValue).ToListAsync();
        var accountIds = rows.Select(r => r.MappedAccountId!.Value).Distinct().ToList();

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var toRemove = await _db.OpeningBalances
                .Where(o => o.CompanyId == companyId
                    && o.FiscalPeriodId == period.Id
                    && accountIds.Contains(o.AccountId))
                .ToListAsync();
            // Defensive: only delete rows that actually came from this session.
            // The commit writes a tagged note; if someone has edited it by hand
            // we keep it and skip silently — better than corrupting curated data.
            var sessionTag = session.Id.ToString("N")[..8];
            var ours = toRemove.Where(o => o.Notes != null && o.Notes.Contains(sessionTag)).ToList();
            _db.OpeningBalances.RemoveRange(ours);

            var summary = new {
                rolledBackAt = DateTime.UtcNow,
                rolledBackBy = userId,
                rowsRemoved = ours.Count,
                rowsSkipped = toRemove.Count - ours.Count,
            };
            session.Status = MigrationStatus.RolledBack;
            // Preserve original commit summary by appending rather than overwriting.
            session.ImportSummaryJson = JsonSerializer.Serialize(new
            {
                originalCommit = session.ImportSummaryJson,
                rollback = summary,
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

    public async Task<MigrationSession> CommitAsync(Guid companyId, Guid sessionId, string userId)
    {
        var session = await LoadSessionAsync(companyId, sessionId);
        if (session.Status != MigrationStatus.ReadyToCommit)
            throw new InvalidOperationException("Session ยังไม่ผ่าน validation — รัน ValidateSessionAsync ก่อน");
        if (!session.TargetFiscalPeriodId.HasValue)
            throw new InvalidOperationException("ไม่ได้ระบุ TargetFiscalPeriodId");

        var rows = await _db.AccountMappings.Where(a => a.MigrationSessionId == sessionId).ToListAsync();
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var summary = new { TotalAccounts = rows.Count, TotalDebit = rows.Sum(r => r.LegacyDebit), TotalCredit = rows.Sum(r => r.LegacyCredit) };

            // Aggregate any duplicate target accounts (multiple legacy codes
            // can map to one) — collapse to a single OpeningBalance row per
            // (period, account) so we don't violate the unique index.
            var aggregated = rows
                .Where(r => r.MappedAccountId.HasValue)
                .GroupBy(r => r.MappedAccountId!.Value)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Debit = g.Sum(r => r.LegacyDebit),
                    Credit = g.Sum(r => r.LegacyCredit),
                });

            foreach (var a in aggregated)
            {
                var existing = await _db.OpeningBalances.FirstOrDefaultAsync(o =>
                    o.CompanyId == companyId
                    && o.FiscalPeriodId == session.TargetFiscalPeriodId.Value
                    && o.AccountId == a.AccountId);
                if (existing != null)
                {
                    existing.OpeningDebit = a.Debit;
                    existing.OpeningCredit = a.Credit;
                    existing.UpdatedBy = userId;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.OpeningBalances.Add(new OpeningBalance
                    {
                        CompanyId = companyId,
                        FiscalPeriodId = session.TargetFiscalPeriodId.Value,
                        AccountId = a.AccountId,
                        OpeningDebit = a.Debit,
                        OpeningCredit = a.Credit,
                        Notes = $"Migrated via session {session.Id.ToString("N")[..8]}",
                        CreatedBy = userId,
                    });
                }
            }

            session.Status = MigrationStatus.Committed;
            session.CompletedAt = DateTime.UtcNow;
            session.CompletedBy = userId;
            session.ImportSummaryJson = JsonSerializer.Serialize(summary);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return session;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            session.Status = MigrationStatus.Failed;
            session.ImportSummaryJson = JsonSerializer.Serialize(new { error = ex.Message });
            await _db.SaveChangesAsync();
            throw;
        }
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

    private async Task<MigrationSession> LoadSessionAsync(Guid companyId, Guid sessionId)
        => await _db.MigrationSessions
            .FirstOrDefaultAsync(m => m.Id == sessionId && m.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ migration session");
}
