using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class AccountingService
{
    // ═══ ผูกบัญชีแม่ของผังที่นำเข้าไว้ก่อน 2026-10-15 (ฝ่ายค้านรอบส่งออก P1-2 · Helpers/ChartParentLink · F2 ข้อ 9) ═══
    // รายงานก่อน (อ่านอย่างเดียว) → ผูกเฉพาะผังที่เลือก + audit chain · ไม่มีงานไหนผูกเองตอนเปิดระบบ

    public Task<List<ChartParentLinkSuspect>> FindUnlinkedAccountsAsync(Guid companyId)
        => ChartParentLinker.FindUnlinkedAsync(_db, companyId);

    public Task<ChartParentLinkRepairResult> RepairAccountParentLinksAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, string actor)
        => ChartParentLinker.RepairAsync(_db, companyId, accountIds, actor);

    // ═══ รอบ 3 P2-7: ผังที่การผูกรอบแรกวางไว้ใต้ผังลงรายการของแม่แบบ — รายงาน → ย้ายเฉพาะที่เลือก + audit ═══

    public Task<List<TemplateLeafChildSuspect>> FindTemplateLeafChildrenAsync(Guid companyId)
        => ChartParentLinker.FindTemplateLeafChildrenAsync(_db, companyId);

    public Task<ChartParentLinkRepairResult> RescueTemplateLeafChildrenAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, string actor)
        => ChartParentLinker.RescueTemplateLeafChildrenAsync(_db, companyId, accountIds, actor);

    // ═══ รอบ 4 P2-b: กู้คืนผังที่ถูกลบแบบ soft (ทางไปต่อของ "ผังที่ถูกลบยังมียอด ⇒ ปิดปี/ยกยอดไม่ได้") ═══
    // เจ้าของบริษัทเท่านั้น (ผู้เรียกตรวจสิทธิ์) · ล็อกเดียวกับการผูก/ย้ายบัญชีแม่ (ต้นไม้ผังเปลี่ยน) · audit chain

    public async Task<AccountResponse> RestoreAccountAsync(Guid companyId, Guid accountId, string actor, Guid? newParentId = null)
    {
        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            AdvisoryLockKey.For(companyId, AdvisoryLockKey.AccountCodeSequence, "parent-link"));

        var account = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีในบริษัทนี้");
        if (!account.IsDeleted)
            throw new BusinessRuleException($"ผัง {account.AccountCode} {account.AccountName} ไม่ได้ถูกลบ — ไม่ต้องกู้คืน", OpeningRoll.RuleCode);
        // ดัชนีรหัสผังรวมแถวที่ลบแล้ว ⇒ ปกติชนไม่ได้ · ตรวจไว้ให้ข้อความชัดแทน error ของฐานข้อมูล
        if (await _db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId && a.Id != accountId && a.AccountCode == account.AccountCode))
            throw new BusinessRuleException(
                $"มีผังรหัส {account.AccountCode} ที่ใช้งานอยู่แล้ว — กู้คืนผังที่ถูกลบซึ่งรหัสซ้ำไม่ได้ · โอนยอดของผังที่ถูกลบด้วยสมุดรายวันไม่ได้เช่นกัน "
                + "(ผังที่ลบลงรายการไม่ได้) — แจ้งผู้ดูแลระบบพร้อมรหัสนี้", OpeningRoll.RuleCode);

        var oldParentId = account.ParentAccountId;
        Guid? targetParentId = oldParentId;
        if (newParentId is Guid wanted)
        {
            // รอบ 5 P2-3: กู้คืนไปไว้ใต้หัวบัญชีอื่น (ทางไปต่อเมื่อผังแม่เดิมกลายเป็นผังลงรายการ) — ของบริษัทนี้ · ไม่ถูกลบ · ประเภทเดียวกัน · ไม่ใช่ลูกหลานของผังนี้ ·
            // รับลูกได้ (กติกาเดียวกับผังแม่เดิม)
            var target = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == wanted && a.CompanyId == companyId)
                .Select(a => new { a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount })
                .FirstOrDefaultAsync()
                ?? throw new BusinessRuleException(
                    "ไม่พบหัวบัญชีที่เลือกในผังของบริษัทนี้ (อาจถูกลบหรือมาจากบริษัทอื่น) — เลือกหัวบัญชีใหม่จากรายการ", OpeningRoll.RuleCode);
            if (target.Id == account.Id || target.AccountType != account.AccountType)
                throw new BusinessRuleException(
                    $"หัวบัญชี {target.AccountCode} ใช้กับผัง {account.AccountCode} ไม่ได้ — ต้องเป็นผังประเภทเดียวกัน ({account.AccountType}) และไม่ใช่ผังตัวเอง", OpeningRoll.RuleCode);
            var ancestors = await AncestorIdsAsync(companyId, target.Id);
            if (ancestors.Contains(account.Id))
                throw new BusinessRuleException(
                    $"หัวบัญชี {target.AccountCode} อยู่ใต้ผัง {account.AccountCode} เอง — วางผังไว้ใต้ลูกหลานของตัวเองไม่ได้ (ต้นไม้ผังวนซ้ำ)", OpeningRoll.RuleCode);
            targetParentId = target.Id;
        }

        if (targetParentId is Guid parentId)
        {
            var parent = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Id == parentId && a.CompanyId == companyId)
                .Select(a => new { a.Id, a.AccountCode, a.IsDeleted })
                .FirstOrDefaultAsync();
            if (parent != null && parent.IsDeleted)
                throw new BusinessRuleException(
                    $"ผังแม่ {parent.AccountCode} ของ {account.AccountCode} ถูกลบอยู่ — กู้คืนผังแม่ก่อน (POST accounting/accounts/{parent.Id}/restore) แล้วกู้คืนผังนี้",
                    OpeningRoll.RuleCode);
            // รอบ 5 P2-3: ผังแม่ที่ระหว่างนั้นกลายเป็นผังลงรายการ (ไม่มีลูกเหลือ + ถูกใช้/ผังระบบ เช่น 11310) ⇒ กู้คืนแล้วผังแม่กลายเป็นบัญชีคุม
            // (ด่านลงรายการเข้มปฏิเสธผังแม่ · ยอดค้างในบัญชีคุม) — ล้มพร้อมทางไปต่อ: กู้คืนไว้ใต้หัวบัญชีอื่น (newParentId · ปุ่มหน้าตรวจข้อมูลส่งหัวบัญชีที่เสนอ)
            var plan = await PlanRestoreUnderAsync(companyId, account, parentId);
            if (plan.BlockedReason != null)
                throw new BusinessRuleException(RestoreBlockedMessage(account.AccountCode, plan), ChartParentLink.RuleCode);
        }

        account.IsDeleted = false;
        if (targetParentId != oldParentId)
        {
            account.ParentAccountId = targetParentId;
            await RelevelRestoredAsync(companyId, account);
        }
        account.UpdatedBy = actor;
        account.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            EntityType = nameof(ChartOfAccount),
            EntityId = account.Id.ToString(),
            Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { accountCode = account.AccountCode, isDeleted = true, parentAccountId = oldParentId }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = OpeningRoll.AccountRestoreAction, accountCode = account.AccountCode, isDeleted = false,
                parentAccountId = account.ParentAccountId, level = account.Level,
                ruleCode = OpeningRoll.RuleCode, by = actor,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();
        return MapAccountToResponse(account);
    }

    /// <summary>รอบ 5 P2-3 — แผนกู้คืน (อ่านอย่างเดียว · หน้าตรวจข้อมูลใช้ทำปุ่ม "แก้": ผังแม่เดิมรับไม่ได้ ⇒ ปุ่มส่งหัวบัญชีที่เสนอเป็น newParentId)</summary>
    public async Task<AccountRestorePlan> PlanAccountRestoreAsync(Guid companyId, Guid accountId)
    {
        var account = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีในบริษัทนี้");
        if (account.ParentAccountId is not Guid parentId)
            return new AccountRestorePlan(account.Id, account.AccountCode, null, null, null);
        return await PlanRestoreUnderAsync(companyId, account, parentId);
    }

    /// <summary>ผังแม่ <paramref name="parentId"/> รับ <paramref name="account"/> ที่กู้คืนได้ไหม + หัวบัญชีที่เสนอเมื่อรับไม่ได้ — ตัวตัดสินเดียว
    /// (<see cref="ChartParentLink.RestoreParentBlockedReason"/> · <see cref="ChartParentLink.RestoreAlternativeParent"/> · ผังที่ถูกใช้ = <see cref="ChartAccountReferences"/>)</summary>
    private async Task<AccountRestorePlan> PlanRestoreUnderAsync(Guid companyId, ChartOfAccount account, Guid parentId)
    {
        var chart = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.Id != account.Id)
            .Select(a => new ChartNode(a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount))
            .ToListAsync();
        var parent = chart.FirstOrDefault(c => c.Id == parentId);
        if (parent == null) return new AccountRestorePlan(account.Id, account.AccountCode, null, null, null);   // ผังแม่ที่ลบ/หาย — ผู้เรียกบอกเหตุเอง
        var hasChildren = chart.Any(c => c.ParentId == parentId);
        // ผังที่ถูกใช้: ผังแม่เดิม + ผู้สมัครหัวบัญชี (prefix ประเภทเดียวกันที่ยังไม่มีลูก) — ไล่ FK เฉพาะชุดนี้
        var candidates = chart.Where(c => c.Id == parentId
                || (c.Type == account.AccountType && c.Code.Length < account.AccountCode.Length && account.AccountCode.StartsWith(c.Code, StringComparison.Ordinal)))
            .Select(c => c.Id).ToList();
        var inUse = await ChartAccountReferences.ReferencedAccountIdsAsync(_db, companyId, candidates);
        var reason = ChartParentLink.RestoreParentBlockedReason(parent, hasChildren, inUse.Contains(parentId), account.AccountCode);
        if (reason == null) return new AccountRestorePlan(account.Id, account.AccountCode, null, null, null);
        var node = new ChartNode(account.Id, account.AccountCode, account.AccountType, account.ParentAccountId, account.Level, account.IsSystemAccount);
        var alt = ChartParentLink.RestoreAlternativeParent(node, chart, inUse, parentId);
        return new AccountRestorePlan(account.Id, account.AccountCode, reason, alt?.Id, alt?.Code);
    }

    /// <summary>ข้อความ "กู้คืนไว้ใต้ผังแม่เดิมไม่ได้" + ทางไปต่อ (ตัวตั้งเดียวของ RestoreAccountAsync)</summary>
    private static string RestoreBlockedMessage(string accountCode, AccountRestorePlan plan)
        => $"กู้คืนผัง {accountCode} ไว้ที่เดิมไม่ได้: {plan.BlockedReason} · ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: "
            + (plan.SuggestedParentId is Guid sid
                ? $"กู้คืนไว้ใต้หัวบัญชี {plan.SuggestedParentCode} แทน (หน้า \"ตรวจข้อมูลจากบั๊กที่แก้แล้ว\" ปุ่ม \"แก้\" ของผังนี้ทำให้ · หรือ POST accounting/accounts/{plan.AccountId}/restore?newParentId={sid})"
                : $"เลือกหัวบัญชีประเภทเดียวกันที่ยังรับบัญชีย่อยได้ แล้วกู้คืนด้วย POST accounting/accounts/{plan.AccountId}/restore?newParentId={{id ของหัวบัญชี}} (เจ้าของบริษัท)")
            + " · ผังแม่เดิมยังเป็นผังลงรายการตามเดิม";

    /// <summary>ผังบรรพบุรุษของ <paramref name="accountId"/> (รวมตัวเอง · กันวน)</summary>
    private async Task<HashSet<Guid>> AncestorIdsAsync(Guid companyId, Guid accountId)
    {
        var parentOf = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .Select(a => new { a.Id, a.ParentAccountId })
            .ToDictionaryAsync(a => a.Id, a => a.ParentAccountId);
        var seen = new HashSet<Guid>();
        Guid? cur = accountId;
        while (cur is Guid id && seen.Add(id))
            cur = parentOf.TryGetValue(id, out var p) ? p : null;
        return seen;
    }

    /// <summary>ระดับของผังที่กู้คืนไปไว้ใต้หัวบัญชีใหม่ + ลูกหลานที่ยังใช้งาน (กติกาเดียวกับการย้ายแม่ — <see cref="ChartParentLink.Relevel"/>)</summary>
    private async Task RelevelRestoredAsync(Guid companyId, ChartOfAccount account)
    {
        var tracked = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.Id != account.Id)
            .ToListAsync();
        var chart = tracked.Select(a => new ChartNode(a.Id, a.AccountCode, a.AccountType, a.ParentAccountId, a.Level, a.IsSystemAccount))
            .Append(new ChartNode(account.Id, account.AccountCode, account.AccountType, account.ParentAccountId, account.Level, account.IsSystemAccount))
            .ToList();
        var parentLevel = tracked.First(a => a.Id == account.ParentAccountId).Level;
        var levels = ChartParentLink.Relevel(chart, new[]
        {
            new ChartLinkProposal(account.Id, account.AccountCode, account.ParentAccountId, null, parentLevel + 1, null),
        });
        foreach (var (id, level) in levels)
        {
            if (id == account.Id) account.Level = level;
            else tracked.First(a => a.Id == id).Level = level;
        }
    }
}
