using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// <b>ผังค่าใช้จ่าย "เงินเพิ่ม/เบี้ยปรับ/ค่าปรับ" ของการนำส่งภาษี-ประกันสังคม</b> — ตัวหาตัวเดียว (รอบ 220 · คำตัดสินข้อ 181)
/// ใช้ทั้งหน้านำส่งรวม (<c>StatutoryRemittanceService.RemitAsync</c>) และนำส่ง สปส. จากหน้าเงินเดือน (<c>PayrollService.SettleSocialSecurityAsync</c>)
///
/// <para>═══ ที่มา ═══ เดิมสองไฟล์มีสำเนาตัวหาของตัวเอง: 54xxx ที่ชื่อมี "เงินเพิ่ม/ค่าปรับ" → <b>ไม่เจอก็ตก 58xxx</b>
/// (ผังมาตรฐาน 58000 = ภาษีเงินได้นิติบุคคล) · ไม่เจอเลย ⇒ หน้านำส่งตั้งเงินเพิ่ม 0 <b>เงียบ</b> · หน้าเงินเดือน JE ไม่ดุล</para>
///
/// <para>ลำดับ (ฝ่ายค้านรอบ 220 ข้อ 4 — <b>เลิกเดาจากชื่อผัง</b>: ชื่อ "ค่าปรับปรุง…"/"ค่าเผื่อ…" ถูกหยิบได้):
/// ผังที่ตั้งค่าไว้ (<c>CompanySettings.PenaltyExpenseAccountCode</c> · ไม่เจอ = ปฏิเสธ) → 54820 ที่ลงรายการได้ →
/// <b>สร้าง 54820 ใต้หมวด 548</b> เฉพาะเมื่อ 548 เป็นผังค่าใช้จ่ายที่เปิดใช้และเป็น "หัวหมวด" (มีผังย่อย หรือไม่เคยมีรายการ) ·
/// ไม่เข้าเงื่อนไข ⇒ ปฏิเสธพร้อมบอกให้ไปตั้งผังที่หน้าตั้งค่า (ข้อ 5)</para>
/// </summary>
public static class PenaltyExpenseAccount
{
    public const string DefaultCode = "54820";
    public const string ParentCode = "548";
    public const string DefaultName = "ค่าปรับ/เบี้ยปรับ/เงินเพิ่ม";
    public const string RuleCode = "PENALTY-ACCOUNT";

    private const string SettingsPath = "ตั้งค่า › ภาษี › “ผังเงินเพิ่ม/เบี้ยปรับ/ค่าปรับ”";

    /// <summary>รหัสที่ตัวหาจะใช้ (ค่าตั้ง → 54820) — ตัวหาไม่เดาจากชื่อแล้ว ⇒ รหัสนี้คือรหัสเดียวที่ <see cref="ResolveOrCreateAsync"/> ลงจริง</summary>
    public static string EffectiveCode(string? configuredCode)
        => string.IsNullOrWhiteSpace(configuredCode) ? DefaultCode : configuredCode.Trim();

    /// <summary>ตัดสินว่าสร้าง 54820 ใต้ 548 ได้ไหม (pure) — null = ได้ · อื่น ๆ = ข้อความปฏิเสธ</summary>
    internal static string? CreateRefusal(bool codeExists, bool codeIsParent, bool parentFound, AccountType parentType, bool parentActive,
        bool parentHasChildren, bool parentHasPostings)
    {
        if (codeExists)
            return codeIsParent
                ? $"ผัง {DefaultCode} เป็นบัญชีคุม (มีผังย่อย) ลงรายการไม่ได้ — เลือกผังย่อยที่ใช้ลงค่าปรับที่ {SettingsPath}"
                : $"ผัง {DefaultCode} ถูกปิดใช้หรือลบไว้ — เปิดใช้ผังนี้ที่หน้า “ผังบัญชี” หรือเลือกผังอื่นที่ {SettingsPath}";
        if (!parentFound)
            return $"ไม่พบผัง {DefaultCode} และไม่พบหมวด {ParentCode} (ค่าธรรมเนียมและค่าปรับ) — เลือกผังค่าใช้จ่ายสำหรับค่าปรับที่ {SettingsPath}";
        if (parentType != AccountType.Expense || !parentActive)
            return $"หมวด {ParentCode} ไม่ใช่ผังค่าใช้จ่ายที่เปิดใช้ — ระบบไม่สร้างผังใต้หมวดนี้ · เลือกผังค่าปรับที่ {SettingsPath}";
        if (!parentHasChildren && parentHasPostings)
            return $"ผัง {ParentCode} มีรายการบัญชีลงอยู่แล้ว (ไม่ใช่หัวหมวด) — ระบบไม่สร้างผังย่อยใต้ผังที่ใช้งาน · เลือกผังค่าปรับที่ {SettingsPath}";
        return null;
    }

    /// <summary>หา (หรือสร้าง) ผังค่าใช้จ่ายเงินเพิ่ม/ค่าปรับ — เรียกเฉพาะเมื่อมียอดต้องลง (> 0)</summary>
    public static async Task<ChartOfAccount> ResolveOrCreateAsync(AccountingDbContext db, Guid companyId, string performedBy = "", CancellationToken ct = default)
    {
        var configured = (await db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId).Select(s => s.PenaltyExpenseAccountCode)
            .FirstOrDefaultAsync(ct))?.Trim();

        if (!string.IsNullOrEmpty(configured))
            return await FindPostableAsync(db, companyId, configured, ct)
                ?? throw new BusinessRuleException(
                    $"ตั้งค่าผังเงินเพิ่ม/ค่าปรับไว้เป็น {configured} แต่ไม่พบผังนี้ (หรือปิดใช้/เป็นบัญชีคุม) — "
                    + $"แก้ที่ {SettingsPath} หรือเว้นว่างให้ใช้ {DefaultCode}", RuleCode);

        var acc = await FindPostableAsync(db, companyId, DefaultCode, ct);
        if (acc != null) return acc;

        // ── ไม่มี 54820 ที่ลงรายการได้ — ตรวจก่อนสร้าง ──
        var existing = await db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == DefaultCode)
            .Select(a => new { a.Id, IsParent = db.ChartOfAccounts.Any(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId == a.Id) })
            .FirstOrDefaultAsync(ct);
        var parent = await db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.AccountCode == ParentCode, ct);
        var parentHasChildren = parent != null && await db.ChartOfAccounts.AnyAsync(c => c.CompanyId == companyId
            && !c.IsDeleted && c.ParentAccountId == parent.Id, ct);
        var parentHasPostings = parent != null && await db.JournalEntryLines.AnyAsync(l => l.AccountId == parent.Id
            && l.JournalEntry.CompanyId == companyId, ct);
        var refusal = CreateRefusal(existing != null, existing?.IsParent == true, parent != null,
            parent?.AccountType ?? AccountType.Expense, parent?.IsActive == true, parentHasChildren, parentHasPostings);
        if (refusal != null) throw new BusinessRuleException(refusal, RuleCode);

        var created = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = DefaultCode,
            AccountName = DefaultName,
            AccountNameEn = "Fines, Penalties and Surcharges",
            AccountType = AccountType.Expense,
            ParentAccountId = parent!.Id,
            Level = parent.Level + 1,
            IsActive = true,
            IsSystemAccount = true,
            Description = "สร้างอัตโนมัติเมื่อนำส่งภาษี/ประกันสังคมที่มีเงินเพิ่ม/เบี้ยปรับ — รายจ่ายต้องห้าม §65 ตรี(6) (บวกกลับ ภ.ง.ด.50)",
            CreatedBy = performedBy,
        };
        db.ChartOfAccounts.Add(created);
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(performedBy, out var actor) ? actor : (Guid?)null,
            Action = AuditAction.Create,
            EntityType = "ChartOfAccount",
            EntityId = created.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                Operation = "AutoCreatePenaltyExpenseAccount", created.AccountCode, created.AccountName, ParentCode,
                Reason = "นำส่งภาษี/ประกันสังคมที่มีเงินเพิ่ม/เบี้ยปรับ/ค่าปรับ แต่บริษัทยังไม่มีผัง 54820",
            }),
            Timestamp = DateTime.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException dbe) when (dbe.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // คำขออื่นสร้าง 54820 ไปพร้อมกัน — ถอดของเราออก (รวมแถว audit) แล้วใช้ตัวที่ชนะ
            foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added
                         && (ReferenceEquals(e.Entity, created) || e.Entity is AuditLog { EntityType: "ChartOfAccount" } al && al.EntityId == created.Id.ToString())).ToList())
                e.State = EntityState.Detached;
            return await FindPostableAsync(db, companyId, DefaultCode, ct)
                ?? throw new BusinessRuleException($"สร้างผัง {DefaultCode} ไม่สำเร็จ (มีผังรหัสนี้อยู่แล้วแต่ลงรายการไม่ได้) — เลือกผังค่าปรับที่ {SettingsPath}", RuleCode);
        }
    }

    private static Task<ChartOfAccount?> FindPostableAsync(AccountingDbContext db, Guid companyId, string code, CancellationToken ct)
        => db.ChartOfAccounts.DefaultPostingCandidates(db).FirstOrDefaultAsync(a => a.CompanyId == companyId
            && a.AccountCode == code && a.IsActive && !a.IsDeleted, ct);
}
