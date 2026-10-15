using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ตัวดึงข้อมูลของ <see cref="PostingAccountRule"/> — "ผังบัญชีนี้ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม" (2026-10-14 · ID#13)
///
/// <para>ทุกทางที่รับ id ผังบัญชีจากผู้ใช้/API แล้วเขียนลง <c>JournalEntryLine.AccountId</c> หรือเก็บเป็นค่าตั้งที่จะลงรายการทีหลัง
/// เรียก <see cref="EnsurePostingAccountAsync"/> (โยน <see cref="BusinessRuleException"/> พร้อมชื่อช่อง + ที่แก้) ·
/// ห้ามเขียน query ตรวจซ้ำเอง (ตัวตั้งตัวเดียว — CLAUDE.md F2 ข้อ 4)</para>
/// </summary>
public static class PostingAccountGuard
{
    /// <summary>
    /// ตัวกรองฝั่ง query ของ <see cref="PostingAccountRule"/> ข้อ "ไม่ใช่บัญชีคุม" — ผังที่<b>ไม่มีบัญชีย่อย</b> (ไม่ดู Level) · รอบ 206:
    /// ตัวหาผังตั้งต้นตามรหัส/หมวด (ลงบัญชีอัตโนมัติ · ตัวเลือก AI · ค่าแนะนำ) เดิมกรอง <c>Level &gt;= 4</c> เอง ⇒ ผังของบริษัทที่ลงรายการได้ระดับ 2–3
    /// ถูกข้าม และผังระดับ 4 ที่มีบัญชีย่อย (เช่น 11122 ที่มีผังเงินฝากย่อย) ถูกเลือกทั้งที่ด่านลงรายการปฏิเสธ · ผังมาตรฐานสองกติกาให้ผลเท่ากัน
    /// (ผังระดับ &lt; 4 ทุกตัวมีบัญชีย่อย) · ผู้เรียกกรอง CompanyId/IsActive เอง · ห้ามเขียน <c>Level &gt;= 4</c> ใหม่ (<c>tools/posting_level_check.py</c>)
    /// </summary>
    public static IQueryable<ChartOfAccount> WhereNotControlAccount(this IQueryable<ChartOfAccount> query, AccountingDbContext db)
        => query.Where(a => !db.ChartOfAccounts.Any(c => c.CompanyId == a.CompanyId && !c.IsDeleted && c.ParentAccountId == a.Id));

    /// <summary>
    /// <b>ลำดับความชอบของ "ผังตั้งต้น" — ตัวตั้งตัวเดียว</b> (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2): ผังจากแม่แบบระบบ (<c>IsSystemAccount</c>)
    /// ก่อน → ผังที่ผูกบัญชีแม่แล้ว (อยู่ในโครงสร้าง) → รหัสน้อยไปมาก → Id (ตัดสินเสมอ ไม่ขึ้นกับลำดับที่ฐานข้อมูลคืน)
    /// <para>ที่มา: ผังที่นำเข้า (ไฟล์ผังบัญชี/Express/PEAK/FlowAccount) เดิมไม่มี <c>ParentAccountId</c> ⇒ หัวบัญชีที่นำเข้า ("1130" · "4100")
    /// ดู "ไม่มีบัญชีย่อย" แล้วถูกหยิบก่อนผังจริงของแม่แบบ ("11310" · "41100") เพราะรหัสเรียงก่อน ⇒ ลงบัญชีอัตโนมัติไปหัวบัญชี ·
    /// ผังที่ผู้ใช้สร้างเองทั้งชุด (ไม่มีผังระบบ) ได้ลำดับเดิม (เรียงตามรหัส) — ไม่เปลี่ยนผลของบริษัทที่ไม่มีผังผสม</para>
    /// </summary>
    public static IOrderedQueryable<ChartOfAccount> OrderByDefaultPreference(this IQueryable<ChartOfAccount> query)
        => query.OrderByDescending(a => a.IsSystemAccount)
            .ThenByDescending(a => a.ParentAccountId != null)
            .ThenBy(a => a.AccountCode)
            .ThenBy(a => a.Id);

    /// <summary>ผู้สมัคร "ผังตั้งต้น" ของการลงบัญชีอัตโนมัติ = ไม่ใช่บัญชีคุม (<see cref="WhereNotControlAccount"/>) + ลำดับความชอบ
    /// (<see cref="OrderByDefaultPreference"/>) — ตัวหาผังตั้งต้นทุกตัวที่ <c>FirstOrDefaultAsync</c> ใช้ตัวนี้ (ห้ามเรียงเอง)</summary>
    public static IOrderedQueryable<ChartOfAccount> DefaultPostingCandidates(this IQueryable<ChartOfAccount> query, AccountingDbContext db)
        => query.WhereNotControlAccount(db).OrderByDefaultPreference();

    /// <summary>ข้อเท็จจริงของผังตาม id (กรอง CompanyId + !IsDeleted) — id ที่ไม่อยู่ในผลลัพธ์ = ไม่พบในบริษัทนี้</summary>
    public static async Task<Dictionary<Guid, PostingAccountFacts>> LoadPostingFactsAsync(
        this AccountingDbContext db, Guid companyId, IEnumerable<Guid> accountIds, CancellationToken ct = default)
    {
        var ids = accountIds.Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, PostingAccountFacts>();
        var rows = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && ids.Contains(a.Id))
            .Select(a => new
            {
                a.Id, a.AccountCode, a.AccountName, a.IsActive,
                IsGroup = db.ChartOfAccounts.Any(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId == a.Id),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new PostingAccountFacts(r.Id, r.AccountCode, r.AccountName, r.IsActive, r.IsGroup));
    }

    /// <summary>ข้อความปัญหา (null = ลงรายการได้) — ไม่โยน · ใช้กับทางที่ต้องรวบรวมหลายปัญหาก่อนตอบ</summary>
    public static async Task<string?> PostingAccountProblemAsync(
        this AccountingDbContext db, Guid companyId, Guid accountId, string fieldLabel, CancellationToken ct = default)
    {
        var facts = await db.LoadPostingFactsAsync(companyId, new[] { accountId }, ct);
        facts.TryGetValue(accountId, out var f);
        return PostingAccountRule.Problem(f, fieldLabel);
    }

    /// <summary>บล็อกเมื่อผังใช้ลงรายการไม่ได้ — ข้อความบอกช่อง + ที่แก้ (คำตัดสินข้อ 150: ความถูกต้องของสมุดบัญชี + tenant)</summary>
    public static async Task EnsurePostingAccountAsync(
        this AccountingDbContext db, Guid companyId, Guid accountId, string fieldLabel, CancellationToken ct = default)
    {
        var problem = await db.PostingAccountProblemAsync(companyId, accountId, fieldLabel, ct);
        if (problem != null) throw new BusinessRuleException(problem, PostingAccountRule.RuleCode);
    }

    /// <summary>หลายช่องในครั้งเดียว (query เดียว) — ข้าม null · โยนปัญหาแรกตามลำดับที่ส่งมา</summary>
    public static async Task EnsurePostingAccountsAsync(
        this AccountingDbContext db, Guid companyId, IEnumerable<(Guid? AccountId, string FieldLabel)> fields, CancellationToken ct = default)
    {
        var list = fields.Where(f => f.AccountId is Guid g && g != Guid.Empty).Select(f => (Id: f.AccountId!.Value, f.FieldLabel)).ToList();
        if (list.Count == 0) return;
        var facts = await db.LoadPostingFactsAsync(companyId, list.Select(f => f.Id), ct);
        foreach (var (id, label) in list)
        {
            facts.TryGetValue(id, out var f);
            var problem = PostingAccountRule.Problem(f, label);
            if (problem != null) throw new BusinessRuleException(problem, PostingAccountRule.RuleCode);
        }
    }
}
