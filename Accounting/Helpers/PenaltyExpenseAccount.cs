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
/// (ผังมาตรฐาน 58000 = ภาษีเงินได้นิติบุคคล ⇒ เงินเพิ่มไปปนกับค่าใช้จ่ายภาษี) · ไม่เจอเลย ⇒ หน้านำส่งตั้งเงินเพิ่ม 0 <b>เงียบ</b>
/// ส่วนหน้าเงินเดือน Cr ธนาคารรวมเงินเพิ่มแต่ไม่มีขา Dr ⇒ JE ไม่ดุล ล้มด้วยข้อความที่ผู้ใช้แก้เองไม่ได้</para>
///
/// <para>ลำดับ: ผังที่ตั้งค่าไว้ (<c>CompanySettings.PenaltyExpenseAccountCode</c> · ตั้งแล้วไม่เจอ = ล้มพร้อมทางไปต่อ ไม่เดาผังอื่น)
/// → 54820 (ผังมาตรฐาน "ค่าปรับ/เบี้ยปรับ") → 54xxx ที่ชื่อมี เงินเพิ่ม/เบี้ยปรับ/ค่าปรับ → <b>สร้าง 54820 ใต้หมวด 548</b> ให้อัตโนมัติ
/// (บริษัทที่ผังเก่าไม่มี) · ไม่มีหมวด 548/54 ⇒ ล้มพร้อมทางไปต่อ</para>
/// </summary>
public static class PenaltyExpenseAccount
{
    public const string DefaultCode = "54820";
    public const string ParentCode = "548";
    public const string DefaultName = "ค่าปรับ/เบี้ยปรับ/เงินเพิ่ม";
    public const string RuleCode = "PENALTY-ACCOUNT";

    /// <summary>รหัสที่จะลองตามลำดับ (ค่าตั้ง → ค่าแนะนำ) — echo บนหน้าตั้งค่าว่า "ตอนนี้ระบบใช้ผังใด"</summary>
    public static string EffectiveCode(string? configuredCode)
        => string.IsNullOrWhiteSpace(configuredCode) ? DefaultCode : configuredCode.Trim();

    /// <summary>หา (หรือสร้าง) ผังค่าใช้จ่ายเงินเพิ่ม/ค่าปรับ — เรียกเฉพาะเมื่อมียอดต้องลง (> 0)</summary>
    public static async Task<ChartOfAccount> ResolveOrCreateAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var configured = (await db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId).Select(s => s.PenaltyExpenseAccountCode)
            .FirstOrDefaultAsync(ct))?.Trim();

        if (!string.IsNullOrEmpty(configured))
            return await db.ChartOfAccounts.DefaultPostingCandidates(db).FirstOrDefaultAsync(a => a.CompanyId == companyId
                       && a.AccountCode == configured && a.IsActive && !a.IsDeleted, ct)
                ?? throw new BusinessRuleException(
                    $"ตั้งค่าผังเงินเพิ่ม/ค่าปรับไว้เป็น {configured} แต่ไม่พบผังนี้ (หรือปิดใช้/เป็นบัญชีคุม) — "
                    + "แก้ที่ ตั้งค่า › ภาษี › “ผังเงินเพิ่ม/เบี้ยปรับ/ค่าปรับ” หรือเว้นว่างให้ใช้ 54820", RuleCode);

        var acc = await db.ChartOfAccounts.DefaultPostingCandidates(db).FirstOrDefaultAsync(a => a.CompanyId == companyId
                      && a.AccountCode == DefaultCode && a.IsActive && !a.IsDeleted, ct)
            ?? await db.ChartOfAccounts.DefaultFallbackCandidates(db).FirstOrDefaultAsync(a => a.CompanyId == companyId
                      && a.IsActive && !a.IsDeleted && a.AccountType == AccountType.Expense
                      && a.AccountCode.StartsWith("54") && a.AccountCode.Length >= 5   // หัวหมวด 548 "ค่าธรรมเนียมและค่าปรับ" ไม่ใช่ผังลงรายการ
                      && (a.AccountName.Contains("เงินเพิ่ม") || a.AccountName.Contains("เบี้ยปรับ") || a.AccountName.Contains("ค่าปรับ")), ct);
        if (acc != null) return acc;

        // ── ผังเก่าไม่มี 54820 — สร้างให้ใต้หมวด 548 (หรือ 54) ──
        // รหัสที่เคยมีแล้วถูกลบ (unique index ไม่กรอง IsDeleted) ⇒ สร้างซ้ำไม่ได้ ต้องบอกทางไปต่อ
        var codeTaken = await db.ChartOfAccounts.IgnoreQueryFilters()
            .AnyAsync(a => a.CompanyId == companyId && a.AccountCode == DefaultCode, ct);
        var parent = await db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && !a.IsDeleted
                         && a.AccountCode == ParentCode, ct)
                     ?? await db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && !a.IsDeleted
                         && a.AccountCode == "54", ct);
        if (codeTaken || parent == null)
            throw new BusinessRuleException(
                (codeTaken ? $"ผัง {DefaultCode} ถูกปิดใช้/ลบไว้" : "ไม่พบหมวดผัง 548 (ค่าธรรมเนียมและค่าปรับ)")
                + " — สร้าง/เปิดใช้ผังค่าใช้จ่าย \"ค่าปรับ/เบี้ยปรับ/เงินเพิ่ม\" ที่หน้า “ผังบัญชี” แล้วเลือกที่ ตั้งค่า › ภาษี "
                + "หรือนำส่งโดยใส่เงินเพิ่ม/เบี้ยปรับ/ค่าปรับเป็น 0", RuleCode);

        var created = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = DefaultCode,
            AccountName = DefaultName,
            AccountNameEn = "Fines, Penalties and Surcharges",
            AccountType = AccountType.Expense,
            ParentAccountId = parent.Id,
            Level = parent.Level + 1,
            IsActive = true,
            IsSystemAccount = true,
            Description = "สร้างอัตโนมัติเมื่อนำส่งภาษี/ประกันสังคมที่มีเงินเพิ่ม/เบี้ยปรับ — รายจ่ายต้องห้าม §65 ตรี(6) (บวกกลับ ภ.ง.ด.50)",
        };
        db.ChartOfAccounts.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }
}
