using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลตรวจผังที่ยังไม่ได้ผูกบัญชีแม่ (หนึ่งแถว) — รายงานอ่านอย่างเดียว</summary>
public sealed record ChartParentLinkSuspect(Guid AccountId, string Code, string Name, string? ProposedParentCode, string? BlockedReason);

public sealed record ChartParentLinkRepairResult(int Linked, List<string> Skipped);

/// <summary>ผังที่ถูกผูกใต้ผังลงรายการของแม่แบบ (หนึ่งแถว · รอบ 3 P2-7) — รายงานอ่านอย่างเดียว · <c>BlockedReason</c> ≠ null = ย้ายอัตโนมัติไม่ได้</summary>
public sealed record TemplateLeafChildSuspect(Guid AccountId, string Code, string Name, string LeafCode, string? ProposedParentCode, string? BlockedReason);

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

    /// <summary>ผังที่ถูกใช้อยู่ (รายการบัญชี · ยอดยกมา · สินค้า/ผู้ติดต่อ/ค่าตั้ง/บัญชีธนาคารที่ปักผัง) — ตัวไล่ตัวเดียว <see cref="ChartAccountReferences"/>
    /// (รอบ 2 P1-4: เดิมดูแค่บรรทัด JE ⇒ ผังที่มียอดยกมา/ถูกปักเป็นค่าตั้งถูกทำเป็นบัญชีคุมได้)
    /// <para>ตรวจเฉพาะผังที่ <see cref="ChartParentLink.InUseCheckCandidates"/> คืน (ผังเดิมที่อาจถูกทำเป็นบัญชีแม่ + ผู้สมัครที่บันทึกแล้ว) ·
    /// รอบ 3 P2-2: ผังที่เพิ่งสร้าง (<paramref name="unsavedIds"/>) ไม่ถูกตรวจ และไม่มีผังให้ตรวจ ⇒ ไม่ยิงคิวรีเลย (เดิมคอมเมนต์บอก "ไม่ไล่ทุกตาราง"
    /// แต่ผังใหม่อยู่ในรายการเสมอ ⇒ นำเข้าผังไล่ทุก FK ทุกแถว)</para></summary>
    private static async Task<HashSet<Guid>> LoadInUseAccountIdsAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid> candidateIds, IReadOnlyCollection<Guid> unsavedIds, CancellationToken ct)
    {
        var potential = ChartParentLink.InUseCheckCandidates(chart, candidateIds, unsavedIds);
        if (potential.Count == 0) return new HashSet<Guid>();
        return await ChartAccountReferences.ReferencedAccountIdsAsync(db, companyId, potential, ct);
    }

    private static AuditLog LinkAudit(Guid companyId, Guid? actorId, string actor, ChartOfAccount row, Guid? oldParentId, int oldLevel,
        string action, string? parentCode)
        => new()
        {
            CompanyId = companyId,
            UserId = actorId,
            EntityType = nameof(ChartOfAccount),
            EntityId = row.Id.ToString(),
            Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { accountCode = row.AccountCode, parentAccountId = oldParentId, level = oldLevel }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action, accountCode = row.AccountCode, parentAccountId = row.ParentAccountId, parentCode,
                level = row.Level, ruleCode = ChartParentLink.RuleCode, by = actor,
            }),
            Timestamp = DateTime.UtcNow,
        };

    /// <summary>
    /// ผูกบัญชีแม่ให้ผังที่เพิ่งสร้างจากการนำเข้า (ติดตามอยู่ใน context · ยังไม่บันทึกก็ได้) + รับผังเดิมที่ควรอยู่ใต้หัวบัญชีใหม่ ·
    /// คืนคำเตือนของผังที่ผูกไม่ได้ (ให้ผู้เรียกแสดง — ไม่โยน) · รอบ 2 P2-8: ผัง<b>เดิม</b>ที่ถูกย้ายแม่/ปรับระดับมี audit chain ทุกแถว
    /// (ผังที่สร้างใหม่ได้แม่ตั้งแต่เกิด — ประวัติการสร้างอยู่ที่การนำเข้า) · ลูกหลานของผังที่ระดับเปลี่ยนปรับระดับตามทุกชั้น
    /// </summary>
    public static async Task<List<string>> LinkImportedAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyList<ChartOfAccount> created, CancellationToken ct = default, string? actor = null)
    {
        var warnings = new List<string>();
        if (created.Count == 0) return warnings;
        var chart = await LoadChartAsync(db, companyId, ct);
        var createdIds = created.Select(c => c.Id).ToHashSet();
        chart.RemoveAll(c => createdIds.Contains(c.Id));
        chart.AddRange(created.Select(a => new ChartNode(a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount)));
        var inUse = await LoadInUseAccountIdsAsync(db, companyId, chart, createdIds, createdIds, ct);

        var plan = ChartParentLink.Plan(chart, createdIds, inUse, allowRehome: true);
        var levels = ChartParentLink.Relevel(chart, plan);
        var toLoad = plan.Where(p => p.BlockedReason == null).Select(p => p.AccountId)
            .Concat(levels.Keys)
            .Where(id => !createdIds.Contains(id))
            .Distinct()
            .ToList();
        var existing = toLoad.Count == 0
            ? new Dictionary<Guid, ChartOfAccount>()
            : await db.ChartOfAccounts.Where(a => a.CompanyId == companyId && toLoad.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var createdById = created.ToDictionary(c => c.Id);
        var before = existing.Values.ToDictionary(a => a.Id, a => (a.ParentAccountId, a.Level));
        var parentCodes = new Dictionary<Guid, string?>();
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
            parentCodes[target.Id] = p.ParentCode;
        }
        foreach (var (id, level) in levels)
        {
            var target = createdById.GetValueOrDefault(id) ?? existing.GetValueOrDefault(id);
            if (target != null) target.Level = level;
        }
        Guid? actorId = Guid.TryParse(actor, out var uid) ? uid : null;
        foreach (var row in existing.Values)
        {
            var (oldParent, oldLevel) = before[row.Id];
            if (oldParent == row.ParentAccountId && oldLevel == row.Level) continue;
            row.UpdatedBy = actor ?? row.UpdatedBy;
            row.UpdatedAt = DateTime.UtcNow;
            db.AddChainedAuditLog(LinkAudit(companyId, actorId, actor ?? "import", row, oldParent, oldLevel,
                oldParent == row.ParentAccountId ? "coa-relevel" : ChartParentLink.ParentLinkImportAction, parentCodes.GetValueOrDefault(row.Id)));
        }
        return warnings;
    }

    /// <summary>รายงานอ่านอย่างเดียว: ผัง (ไม่ใช่ของแม่แบบระบบ) ที่ยังไม่มีบัญชีแม่ทั้งที่มีผังรหัสต้นตรงกัน — รวมผังที่ผูกไม่ได้พร้อมเหตุผล</summary>
    public static async Task<List<ChartParentLinkSuspect>> FindUnlinkedAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var chart = await LoadChartAsync(db, companyId, ct);
        var candidates = chart.Where(c => c.ParentId == null && !c.IsSystem).Select(c => c.Id).ToList();
        if (candidates.Count == 0) return new List<ChartParentLinkSuspect>();
        var inUse = await LoadInUseAccountIdsAsync(db, companyId, chart, candidates, Array.Empty<Guid>(), ct);
        var plan = ChartParentLink.Plan(chart, candidates, inUse, allowRehome: false);
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
        var wanted = accountIds.ToHashSet();
        var inUse = await LoadInUseAccountIdsAsync(db, companyId, chart, wanted, Array.Empty<Guid>(), ct);
        var plan = ChartParentLink.Plan(chart, wanted, inUse, allowRehome: false);
        var skipped = new List<string>();
        foreach (var id in wanted.Where(w => plan.All(p => p.AccountId != w)))
        {
            var node = chart.FirstOrDefault(c => c.Id == id);
            skipped.Add(node == null ? $"{id}: ไม่พบผังในบริษัทนี้"
                : node.ParentId != null ? $"{node.Code}: ผูกบัญชีแม่แล้ว"
                : node.IsSystem ? $"{node.Code}: ผังของแม่แบบระบบ — ไม่ย้าย"
                : $"{node.Code}: ไม่มีผังรหัสต้นที่เป็นบัญชีแม่ได้");
        }
        var linkable = plan.Where(p => p.BlockedReason == null).ToList();
        // รอบ 2 P2-8: ลูกหลานของผังที่ผูก (เช่น หัวบัญชีนำเข้าที่มีลูกอยู่แล้ว) ปรับระดับตามทุกชั้น + audit
        var levels = ChartParentLink.Relevel(chart, linkable);
        var ids = linkable.Select(p => p.AccountId).Concat(levels.Keys).Distinct().ToList();
        var rows = await db.ChartOfAccounts.Where(a => a.CompanyId == companyId && ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var linkedIds = new HashSet<Guid>();
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
                    action = ChartParentLink.ParentLinkAction, accountCode = row.AccountCode, parentAccountId = p.ParentId, parentCode = p.ParentCode,
                    level = p.NewLevel, ruleCode = ChartParentLink.RuleCode, by = actor,
                }),
                Timestamp = DateTime.UtcNow,
            });
            linked++;
            linkedIds.Add(row.Id);
        }
        foreach (var (id, level) in levels)
        {
            if (linkedIds.Contains(id) || !rows.TryGetValue(id, out var row) || row.Level == level) continue;
            var oldLevel = row.Level;
            row.Level = level;
            row.UpdatedBy = actor;
            row.UpdatedAt = DateTime.UtcNow;
            db.AddChainedAuditLog(LinkAudit(companyId, actorId, actor, row, row.ParentAccountId, oldLevel, "coa-relevel", null));
        }
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return new ChartParentLinkRepairResult(linked, skipped);
    }

    // ═══ รอบ 3 P2-7: ผังที่การผูกรอบแรก (825f5c0e) วางไว้ใต้ผังลงรายการของแม่แบบ ═══ รายงาน → ย้ายเฉพาะที่เลือก + audit · ไม่มีงานไหนย้ายเอง

    /// <summary>
    /// ผังใน <paramref name="accountIds"/> ที่<b>มีหลักฐานว่าแม่ถูกตั้งโดยการผูกของระบบ</b> (รอบ 4 P2-c — ตัวตัดสินเดียวของรายงาน/ปุ่มย้ายผังใต้ผังลงรายการของแม่แบบ):
    /// (1) สร้างจากการนำเข้า (<see cref="ChartParentLink.IsImportCreated"/> — ผังนำเข้าเกิดมาไม่มีแม่) (2) แถวพักของศูนย์นำเข้าที่<b>สร้าง</b>ผังนี้
    /// (<c>ImportStagedRow</c> Create + Committed) (3) audit การผูกบัญชีแม่ของระบบ (<see cref="ChartParentLink.ParentLinkAuditMarker"/>) ·
    /// ผังที่ผู้ใช้สร้างใต้ผังแม่แบบเอง (เช่น 111221 ใต้ 11122) ไม่มีหลักฐาน ⇒ ไม่ใช่ผลของบั๊ก ไม่ถูกรายงาน/ย้าย · ไม่มีหลักฐาน = ไม่ใช่ (ไม่เดาจากความคล้าย)
    /// </summary>
    private static async Task<HashSet<Guid>> ImportLinkedAccountIdsAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var result = new HashSet<Guid>();
        if (accountIds.Count == 0) return result;
        var ids = accountIds.Distinct().ToList();
        foreach (var a in await db.ChartOfAccounts.AsNoTracking()
                     .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                     .Select(a => new { a.Id, a.CreatedBy })
                     .ToListAsync(ct))
            if (ChartParentLink.IsImportCreated(a.CreatedBy)) result.Add(a.Id);
        var chartEntity = nameof(ChartOfAccount);
        result.UnionWith(await db.ImportStagedRows.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.CommittedEntityType == chartEntity && r.CommittedEntityId != null
                && r.Action == ImportRowAction.Create && r.Status == ImportRowStatus.Committed && ids.Contains(r.CommittedEntityId.Value))
            .Select(r => r.CommittedEntityId!.Value)
            .ToListAsync(ct));
        var keys = ids.Select(i => i.ToString()).ToList();
        foreach (var key in await db.AuditLogs.AsNoTracking()
                     .Where(a => a.CompanyId == companyId && a.EntityType == chartEntity && a.EntityId != null && keys.Contains(a.EntityId)
                         && a.NewValues != null && a.NewValues.Contains(ChartParentLink.ParentLinkAuditMarker))
                     .Select(a => a.EntityId!)
                     .Distinct()
                     .ToListAsync(ct))
            if (Guid.TryParse(key, out var g)) result.Add(g);
        return result;
    }

    /// <summary>แผนย้ายพร้อมตรวจ "หัวบัญชีปลายทางถูกใช้อยู่ไหม" เฉพาะหัวบัญชีของผู้ใช้ที่ยังไม่มีลูก (ตัวไล่เดียว <see cref="ChartAccountReferences"/>) ·
    /// รอบ 4 P2-c: เฉพาะลูกที่มีหลักฐานว่าระบบผูก (<see cref="ImportLinkedAccountIdsAsync"/>) · <c>UserCreated</c> = ลูกใต้ผังลงรายการของแม่แบบที่ไม่มีหลักฐาน (ผู้ใช้สร้างเอง)</summary>
    private static async Task<(List<ChartLinkProposal> Plan, HashSet<Guid> UserCreated)> PlanLeafRescueAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid>? onlyChildIds, CancellationToken ct)
    {
        var underLeaf = ChartParentLink.PlanTemplateLeafRescue(chart, onlyChildIds, new HashSet<Guid>()).Select(p => p.AccountId).ToList();
        var evidenced = await ImportLinkedAccountIdsAsync(db, companyId, underLeaf, ct);
        var userCreated = underLeaf.Where(id => !evidenced.Contains(id)).ToHashSet();
        var scope = underLeaf.Where(evidenced.Contains).ToList();
        if (scope.Count == 0) return (new List<ChartLinkProposal>(), userCreated);
        var draft = ChartParentLink.PlanTemplateLeafRescue(chart, scope, new HashSet<Guid>());
        var withChildren = chart.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        var check = draft.Where(p => p.ParentId is Guid t && !withChildren.Contains(t)).Select(p => p.ParentId!.Value).Distinct().ToList();
        var inUse = check.Count == 0 ? new HashSet<Guid>() : await ChartAccountReferences.ReferencedAccountIdsAsync(db, companyId, check, ct);
        return (inUse.Count == 0 ? draft : ChartParentLink.PlanTemplateLeafRescue(chart, scope, inUse), userCreated);
    }

    /// <summary>รายงานอ่านอย่างเดียว: ผังของผู้ใช้ที่อยู่ใต้ผังลงรายการของแม่แบบ (ผังลงรายการกลายเป็นบัญชีคุม) + หัวบัญชีที่ควรย้ายไป</summary>
    public static async Task<List<TemplateLeafChildSuspect>> FindTemplateLeafChildrenAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var chart = await LoadChartAsync(db, companyId, ct);
        var (plan, _) = await PlanLeafRescueAsync(db, companyId, chart, null, ct);
        if (plan.Count == 0) return new List<TemplateLeafChildSuspect>();
        var ids = plan.Select(p => p.AccountId).ToList();
        var names = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AccountName, ct);
        var byId = chart.ToDictionary(c => c.Id);
        return plan.Select(p => new TemplateLeafChildSuspect(p.AccountId, p.Code, names.GetValueOrDefault(p.AccountId) ?? "",
                byId[byId[p.AccountId].ParentId!.Value].Code, p.ParentCode, p.BlockedReason))
            .ToList();
    }

    /// <summary>ย้ายผังที่ผู้ใช้เลือกออกจากใต้ผังลงรายการของแม่แบบไปหัวบัญชีถัดไป (คิดแผนใหม่ ณ ตอนกด) + ปรับระดับลูกหลาน + audit chain ทุกแถว ·
    /// ล็อกเดียวกับการผูกบัญชีแม่ · เจ้าของบริษัทเท่านั้น (ผู้เรียกตรวจสิทธิ์)</summary>
    public static async Task<ChartParentLinkRepairResult> RescueTemplateLeafChildrenAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> accountIds, string actor, CancellationToken ct = default)
    {
        if (accountIds == null || accountIds.Count == 0)
            throw new BusinessRuleException("ยังไม่ได้เลือกผังที่จะย้าย — เปิดรายงานตรวจข้อมูลก่อน", ChartParentLink.RuleCode);

        await using var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.AccountCodeSequence, "parent-link") }, ct);

        var chart = await LoadChartAsync(db, companyId, ct);
        var wanted = accountIds.ToHashSet();
        var (plan, userCreated) = await PlanLeafRescueAsync(db, companyId, chart, wanted, ct);
        var skipped = wanted.Where(w => plan.All(p => p.AccountId != w))
            .Select(w => chart.FirstOrDefault(c => c.Id == w) is not { } n ? $"{w}: ไม่พบผังในบริษัทนี้"
                : userCreated.Contains(w) ? $"{n.Code}: ผู้ใช้สร้างใต้ผังนี้เอง (ไม่มีหลักฐานว่าระบบผูก) — ไม่ย้าย"
                : $"{n.Code}: ไม่ได้อยู่ใต้ผังลงรายการของแม่แบบ")
            .ToList();
        var movable = plan.Where(p => p.BlockedReason == null).ToList();
        skipped.AddRange(plan.Where(p => p.BlockedReason != null).Select(p => $"{p.Code}: {p.BlockedReason}"));
        var levels = ChartParentLink.Relevel(chart, movable);
        var ids = movable.Select(p => p.AccountId).Concat(levels.Keys).Distinct().ToList();
        var rows = await db.ChartOfAccounts.Where(a => a.CompanyId == companyId && ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        Guid? actorId = Guid.TryParse(actor, out var uid) ? uid : null;
        var moved = new HashSet<Guid>();
        foreach (var p in movable)
        {
            if (!rows.TryGetValue(p.AccountId, out var row)) continue;
            var (oldParent, oldLevel) = (row.ParentAccountId, row.Level);
            row.ParentAccountId = p.ParentId;
            row.Level = p.NewLevel;
            row.UpdatedBy = actor;
            row.UpdatedAt = DateTime.UtcNow;
            db.AddChainedAuditLog(LinkAudit(companyId, actorId, actor, row, oldParent, oldLevel, ChartParentLink.LeafRescueAction, p.ParentCode));
            moved.Add(row.Id);
        }
        foreach (var (id, level) in levels)
        {
            if (moved.Contains(id) || !rows.TryGetValue(id, out var row) || row.Level == level) continue;
            var oldLevel = row.Level;
            row.Level = level;
            row.UpdatedBy = actor;
            row.UpdatedAt = DateTime.UtcNow;
            db.AddChainedAuditLog(LinkAudit(companyId, actorId, actor, row, row.ParentAccountId, oldLevel, "coa-relevel", null));
        }
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return new ChartParentLinkRepairResult(moved.Count, skipped);
    }
}
