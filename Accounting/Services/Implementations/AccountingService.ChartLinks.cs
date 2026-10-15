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

    public async Task<AccountResponse> RestoreAccountAsync(Guid companyId, Guid accountId, string actor)
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
        if (account.ParentAccountId is Guid parentId)
        {
            var parent = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Id == parentId && a.CompanyId == companyId)
                .Select(a => new { a.Id, a.AccountCode, a.IsDeleted })
                .FirstOrDefaultAsync();
            if (parent != null && parent.IsDeleted)
                throw new BusinessRuleException(
                    $"ผังแม่ {parent.AccountCode} ของ {account.AccountCode} ถูกลบอยู่ — กู้คืนผังแม่ก่อน (POST accounting/accounts/{parent.Id}/restore) แล้วกู้คืนผังนี้",
                    OpeningRoll.RuleCode);
        }

        account.IsDeleted = false;
        account.UpdatedBy = actor;
        account.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            EntityType = nameof(ChartOfAccount),
            EntityId = account.Id.ToString(),
            Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { accountCode = account.AccountCode, isDeleted = true }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = OpeningRoll.AccountRestoreAction, accountCode = account.AccountCode, isDeleted = false,
                ruleCode = OpeningRoll.RuleCode, by = actor,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();
        return MapAccountToResponse(account);
    }
}
