using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลตรวจผังที่ยังไม่ได้ผูกบัญชีแม่ (หนึ่งแถว) — รายงานอ่านอย่างเดียว</summary>
public sealed record ChartParentLinkSuspect(Guid AccountId, string Code, string Name, string? ProposedParentCode, string? BlockedReason);

public sealed record ChartParentLinkRepairResult(int Linked, List<string> Skipped);

/// <summary>
/// ตัวดึง/เขียนของ <see cref="ChartParentLink"/> (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2) — ทุกทางเข้าที่สร้างผังจากการนำเข้าเรียก
/// <see cref="LinkImportedAsync"/> · ผังที่นำเข้าไว้ก่อนรอบนี้: <see cref="FindUnlinkedAsync"/> (รายงาน) → <see cref="RepairAsync"/>
/// (ผูกเฉพาะผังที่ผู้ใช้เลือก + audit chain) · ไม่มีงานไหนผูกเองตอนเปิดระบบ
/// </summary>
public static class ChartParentLinker
{
    private static async Task<List<ChartNode>> LoadChartAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
        => await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new ChartNode(a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount))
            .ToListAsync(ct);

    private static async Task<HashSet<Guid>> LoadPostedAccountIdsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
        => (await db.JournalEntryLines.AsNoTracking()
                .Where(l => l.JournalEntry.CompanyId == companyId)
                .Select(l => l.AccountId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

    /// <summary>
    /// ผูกบัญชีแม่ให้ผังที่เพิ่งสร้างจากการนำเข้า (ติดตามอยู่ใน context · ยังไม่บันทึกก็ได้) + รับผังเดิมที่ควรอยู่ใต้หัวบัญชีใหม่ ·
    /// คืนคำเตือนของผังที่ผูกไม่ได้ (ให้ผู้เรียกแสดง — ไม่โยน)
    /// </summary>
    public static async Task<List<string>> LinkImportedAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyList<ChartOfAccount> created, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (created.Count == 0) return warnings;
        var chart = await LoadChartAsync(db, companyId, ct);
        var createdIds = created.Select(c => c.Id).ToHashSet();
        chart.RemoveAll(c => createdIds.Contains(c.Id));
        chart.AddRange(created.Select(a => new ChartNode(a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount)));
        var posted = await LoadPostedAccountIdsAsync(db, companyId, ct);

        var plan = ChartParentLink.Plan(chart, createdIds, posted, allowRehome: true);
        var toLoad = plan.Where(p => p.BlockedReason == null && !createdIds.Contains(p.AccountId)).Select(p => p.AccountId).ToList();
        var existing = toLoad.Count == 0
            ? new Dictionary<Guid, ChartOfAccount>()
            : await db.ChartOfAccounts.Where(a => a.CompanyId == companyId && toLoad.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var createdById = created.ToDictionary(c => c.Id);
        foreach (var p in plan)
        {
            if (p.BlockedReason != null)
            {
                warnings.Add($"{p.Code}: {p.BlockedReason}");
                continue;
            }
            var target = createdById.GetValueOrDefault(p.AccountId) ?? existing.GetValueOrDefault(p.AccountId);
            if (target == null) continue;
            target.ParentAccountId = p.ParentId;
            target.Level = p.NewLevel;
        }
        return warnings;
    }

    /// <summary>รายงานอ่านอย่างเดียว: ผัง (ไม่ใช่ของแม่แบบระบบ) ที่ยังไม่มีบัญชีแม่ทั้งที่มีผังรหัสต้นตรงกัน — รวมผังที่ผูกไม่ได้พร้อมเหตุผล</summary>
    public static async Task<List<ChartParentLinkSuspect>> FindUnlinkedAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var chart = await LoadChartAsync(db, companyId, ct);
        var candidates = chart.Where(c => c.ParentId == null && !c.IsSystem).Select(c => c.Id).ToList();
        if (candidates.Count == 0) return new List<ChartParentLinkSuspect>();
        var posted = await LoadPostedAccountIdsAsync(db, companyId, ct);
        var plan = ChartParentLink.Plan(chart, candidates, posted, allowRehome: false);
        var names = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && candidates.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AccountName, ct);
        return plan.Select(p => new ChartParentLinkSuspect(p.AccountId, p.Code, names.GetValueOrDefault(p.AccountId) ?? "",
                p.ParentCode, p.BlockedReason))
            .ToList();
    }

    /// <summary>ผูกบัญชีแม่ให้ผังที่ผู้ใช้เลือก (คิดแผนใหม่ ณ ตอนกด — ไม่เชื่อรายงานเก่า) + audit chain ทุกแถว</summary>
    public static async Task<ChartParentLinkRepairResult> RepairAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> accountIds, string actor, CancellationToken ct = default)
    {
        if (accountIds == null || accountIds.Count == 0)
            throw new BusinessRuleException("ยังไม่ได้เลือกผังที่จะผูกบัญชีแม่ — เปิดรายงานตรวจก่อน", ChartParentLink.RuleCode);

        await using var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.AccountCodeSequence, "parent-link") }, ct);

        var chart = await LoadChartAsync(db, companyId, ct);
        var posted = await LoadPostedAccountIdsAsync(db, companyId, ct);
        var wanted = accountIds.ToHashSet();
        var plan = ChartParentLink.Plan(chart, wanted, posted, allowRehome: false);
        var skipped = new List<string>();
        foreach (var id in wanted.Where(w => plan.All(p => p.AccountId != w)))
        {
            var node = chart.FirstOrDefault(c => c.Id == id);
            skipped.Add(node == null ? $"{id}: ไม่พบผังในบริษัทนี้"
                : node.ParentId != null ? $"{node.Code}: ผูกบัญชีแม่แล้ว"
                : node.IsSystem ? $"{node.Code}: ผังของแม่แบบระบบ — ไม่ย้าย"
                : $"{node.Code}: ไม่มีผังรหัสต้นที่เป็นบัญชีแม่ได้");
        }
        var ids = plan.Where(p => p.BlockedReason == null).Select(p => p.AccountId).ToList();
        var rows = await db.ChartOfAccounts.Where(a => a.CompanyId == companyId && ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        Guid? actorId = Guid.TryParse(actor, out var uid) ? uid : null;
        var linked = 0;
        foreach (var p in plan)
        {
            if (p.BlockedReason != null) { skipped.Add($"{p.Code}: {p.BlockedReason}"); continue; }
            if (!rows.TryGetValue(p.AccountId, out var row) || row.ParentAccountId != null) continue;
            var oldLevel = row.Level;
            row.ParentAccountId = p.ParentId;
            row.Level = p.NewLevel;
            row.UpdatedBy = actor;
            row.UpdatedAt = DateTime.UtcNow;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = actorId,
                EntityType = nameof(ChartOfAccount),
                EntityId = row.Id.ToString(),
                Action = AuditAction.Update,
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { accountCode = row.AccountCode, parentAccountId = (Guid?)null, level = oldLevel }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "coa-parent-link", accountCode = row.AccountCode, parentAccountId = p.ParentId, parentCode = p.ParentCode,
                    level = p.NewLevel, ruleCode = ChartParentLink.RuleCode, by = actor,
                }),
                Timestamp = DateTime.UtcNow,
            });
            linked++;
        }
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return new ChartParentLinkRepairResult(linked, skipped);
    }
}
