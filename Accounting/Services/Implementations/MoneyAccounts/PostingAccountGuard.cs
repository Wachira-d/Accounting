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
    /// <b>ลำดับความชอบของ "ผังตั้งต้น" — ตัวตั้งตัวเดียว</b>: ผังที่<b>อยู่ในโครงสร้าง</b> (มีบัญชีแม่) ก่อน → ผังจากแม่แบบระบบ (<c>IsSystemAccount</c>)
    /// → รหัสน้อยไปมาก → Id (ตัดสินเสมอ ไม่ขึ้นกับลำดับที่ฐานข้อมูลคืน)
    /// <para>ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2: ผังที่นำเข้าเดิมไม่มี <c>ParentAccountId</c> ⇒ หัวบัญชีที่นำเข้า ("1130" · "4100") ถูกหยิบก่อนผังแม่แบบ
    /// ("11310" · "41100") เพราะรหัสเรียงก่อน · <b>รอบ 2 P1-3:</b> รอบแรกเรียง "ผังระบบก่อน" ⇒ ผังเฉพาะกิจที่ระบบใส่ให้ทุกบริษัท (54960 ไม่มีแม่ ·
    /// 21240 · 1134x · ผังย่อยธนาคาร) ชนะผังของผู้ใช้ในบริษัทที่ใช้ผังของตัวเอง — ตอนนี้ "มีแม่" มาก่อน "ผังระบบ" และการหาตามหมวด/รหัสต้น
    /// เลื่อนผังเฉพาะกิจไปท้ายสุด (<see cref="OrderByGeneralPurposeFirst"/>) · ผังผสม (แม่แบบ + นำเข้าที่ผูกแม่แล้ว) ยังได้ผังแม่แบบ · ผังของผู้ใช้ล้วนเรียงตามรหัส</para>
    /// <para>ใช้ตรงกับการหาด้วย<b>รหัสเต็ม</b> (<see cref="DefaultPostingCandidates"/>) — การหาตามหมวด/รหัสต้นใช้ <see cref="OrderByGeneralPurposeFirst"/></para>
    /// </summary>
    public static IOrderedQueryable<ChartOfAccount> OrderByDefaultPreference(this IQueryable<ChartOfAccount> query)
        => query.OrderByDescending(a => a.ParentAccountId != null)
            .ThenByDescending(a => a.IsSystemAccount)
            .ThenBy(a => a.AccountCode)
            .ThenBy(a => a.Id);

    /// <summary>
    /// <b>ลำดับของการหาผังตั้งต้น "ตามหมวด/ตามรหัสต้น"</b> = ผังทั่วไปก่อน → ผังเฉพาะกิจที่<b>ระบบสร้าง</b>ไว้ท้ายสุด
    /// (<see cref="SpecialPurposeAccount.IsSystemCreated"/>) → ลำดับความชอบเดียวกับ <see cref="OrderByDefaultPreference"/>
    /// <para><b>ฝ่ายค้านรอบส่งออก รอบ 3 P1</b> (แทน <c>WhereGeneralPurpose</c> ของรอบ 2): รอบ 2 <b>ตัด</b>ผังทิ้งด้วย (1) รหัสอย่างเดียว ⇒ ผังของลูกค้า
    /// ที่บังเอิญใช้รหัส 54960/53170/21240 ในความหมายอื่นถูกตัด (2) <b>ตัวชี้ใน config</b> (ผังพักของผู้ให้บริการรับชำระเงิน · ผังกันเงินของช่องทาง ·
    /// ผังที่ผูกบัญชีธนาคาร) ซึ่งผู้ใช้เลือกผังใดก็ได้ ⇒ ตั้งผังพักเป็น 11310 แล้วลูกหนี้ตั้งต้นพลิกไป 11320 (ลูกหนี้แตกสองผัง) · ผูกธนาคารกับ 11111
    /// แล้วเงินสดตั้งต้นไป 11112 · ผูกครบทุกผัง 111x ⇒ หาไม่เจอแล้วล้ม "ไม่มีผังเงินสดตั้งต้น" ทั้งที่ 09da513a ทำงานได้</para>
    /// <para><b>กติกา (ตัวตั้งที่นี่):</b> ไม่ตัดอะไรทิ้ง — แค่เลื่อนผังที่ระบบสร้างเพื่อจุดประสงค์เดียวไปท้ายลำดับ ⇒ (ก) ผังที่ config ชี้แต่ผู้ใช้/แม่แบบ
    /// เป็นคนสร้าง ยังเป็นผังตั้งต้นได้ตามเดิม (ข) <b>การกันผังเฉพาะกิจออกไม่เคยเปลี่ยน "หาเจอ" ให้เป็น "ไม่เจอ"</b> — ถ้าผู้สมัครที่ตรงเงื่อนไขมีแต่ผังเฉพาะกิจ
    /// ได้ผังนั้นตามลำดับเดิม (ลำดับก่อนรอบ 3)</para>
    /// </summary>
    public static IOrderedQueryable<ChartOfAccount> OrderByGeneralPurposeFirst(this IQueryable<ChartOfAccount> query)
        => query.OrderBy(SpecialPurposeAccount.IsSystemCreated)
            .ThenByDescending(a => a.ParentAccountId != null)
            .ThenByDescending(a => a.IsSystemAccount)
            .ThenBy(a => a.AccountCode)
            .ThenBy(a => a.Id);

    /// <summary>ผู้สมัคร "ผังตั้งต้น" เมื่อหาด้วย<b>รหัสเต็ม</b> = ไม่ใช่บัญชีคุม (<see cref="WhereNotControlAccount"/>) + ลำดับความชอบ
    /// (<see cref="OrderByDefaultPreference"/>) — การหาตามหมวด/รหัสต้นใช้ <see cref="DefaultFallbackCandidates"/></summary>
    public static IOrderedQueryable<ChartOfAccount> DefaultPostingCandidates(this IQueryable<ChartOfAccount> query, AccountingDbContext db)
        => query.WhereNotControlAccount(db).OrderByDefaultPreference();

    /// <summary>ผู้สมัคร "ผังตั้งต้น" เมื่อหา<b>ตามหมวด (AccountType) หรือรหัสต้น (StartsWith)</b> = ไม่ใช่บัญชีคุม + ผังเฉพาะกิจที่ระบบสร้างไว้ท้ายสุด
    /// (<see cref="OrderByGeneralPurposeFirst"/> · รอบ 3 P1) — ตัวหาผังตั้งต้นแบบ fallback ทุกตัวใช้ตัวนี้ (ห้ามเรียงเอง) ·
    /// ผู้เรียกที่ต้องกรองก่อนเรียงใช้ <c>WhereNotControlAccount(db).Where(…).OrderByGeneralPurposeFirst()</c></summary>
    public static IOrderedQueryable<ChartOfAccount> DefaultFallbackCandidates(this IQueryable<ChartOfAccount> query, AccountingDbContext db)
        => query.WhereNotControlAccount(db).OrderByGeneralPurposeFirst();

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
