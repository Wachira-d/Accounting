using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ผังของบรรทัดใบสำคัญ — รวมผังที่ถูกลบแบบ soft** (ฝ่ายค้านรอบส่งออก รอบ 5 P1 · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 5")
///
/// <para><b>บั๊กที่มา:</b> <c>JournalEntryLine.Account</c> เป็น navigation แบบ required และ <c>ChartOfAccount</c> มีตัวกรอง <c>!IsDeleted</c> ⇒
/// <c>Include(j =&gt; j.Lines).ThenInclude(l =&gt; l.Account)</c> ถูกแปลเป็น INNER JOIN ผ่านตัวกรอง ⇒ <b>บรรทัดของผังที่ถูกลบหายเงียบ</b> ·
/// ใบปิดปีรอบ 4 ล้างผังรายได้/ค่าใช้จ่ายที่ถูกลบ ⇒ เปิดดูใบปิดปีเห็นเดบิต 8,000 / เครดิต 7,000 ทั้งที่หัวใบ 8,000/8,000 และ
/// <b>กลับรายการใบนั้นเขียนตัวกลับไม่ลงตัว</b> (GL คลาด 1,000)</para>
///
/// <para><b>กติกา:</b> ทางที่แสดง/กลับรายการ/ยกเลิก/คัดลอก/ส่งออก/ปรับปรุงใบสำคัญ<b>ทีละใบ</b> โหลด <c>Include(j =&gt; j.Lines)</c> (ไม่ ThenInclude ผัง)
/// แล้วเรียก <see cref="HydrateAsync"/> — เติมผังของทุกบรรทัดจากผังของบริษัทนี้รวมแถวที่ลบ (กฎ M: กรอง CompanyId เสมอ) · ชื่อผังที่ลบแสดงพร้อม
/// <see cref="DeletedSuffix"/> (<see cref="DisplayName"/>) · รายงานยอดรวม (งบทดลอง/งบกำไรขาดทุน) ยังกรองผังที่ลบทั้งชุด — ดู DOCUMENT_FLOW §6.0a-y รอบ 5</para>
/// </summary>
public static class JournalLineAccounts
{
    /// <summary>ต่อท้ายชื่อผังที่ถูกลบเมื่อแสดงบรรทัดใบสำคัญ (ตัวตั้งเดียว)</summary>
    public const string DeletedSuffix = " (ผังที่ลบแล้ว)";

    /// <summary>ชื่อผังที่ใช้แสดงบนบรรทัดใบสำคัญ — ผังที่ถูกลบมีป้าย <see cref="DeletedSuffix"/> (pure)</summary>
    public static string DisplayName(ChartOfAccount account)
        => account.IsDeleted ? account.AccountName + DeletedSuffix : account.AccountName;

    /// <summary>
    /// เติม <c>l.Account</c> ของทุกบรรทัดใน <paramref name="entries"/> จากผังของบริษัท <paramref name="companyId"/> <b>รวมผังที่ถูกลบ</b> ·
    /// โหลดแบบติดตาม (identity resolution — ใบที่ติดตามอยู่ไม่ชนตัวที่ติดตามแล้ว) · ผังที่ไม่พบในบริษัทนี้เลย = ล้มดัง (ข้อมูลข้ามบริษัท/เสีย ห้ามแสดงบรรทัดว่าง)
    /// </summary>
    public static async Task HydrateAsync(this AccountingDbContext db, Guid companyId, IEnumerable<JournalEntry> entries, CancellationToken ct = default)
    {
        var lines = entries.SelectMany(e => e.Lines).ToList();
        if (lines.Count == 0) return;
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        foreach (var l in lines)
        {
            if (!accounts.TryGetValue(l.AccountId, out var account))
                throw new BusinessRuleException(
                    "บรรทัดใบสำคัญอ้างผังบัญชีที่ไม่พบในผังของบริษัทนี้ (แม้แต่ผังที่ถูกลบ) — ข้อมูลเสีย/ข้ามบริษัท ห้ามแสดงหรือกลับรายการต่อ · แจ้งผู้ดูแลระบบพร้อมเลขที่ใบสำคัญ",
                    "GL-JOURNAL-LINE-ACCOUNT");
            l.Account = account;
        }
    }

    /// <summary>ใบเดียว — <see cref="HydrateAsync(AccountingDbContext, Guid, IEnumerable{JournalEntry}, CancellationToken)"/></summary>
    public static Task HydrateAsync(this AccountingDbContext db, Guid companyId, JournalEntry entry, CancellationToken ct = default)
        => db.HydrateAsync(companyId, new[] { entry }, ct);

    /// <summary>รหัสกฎของด่าน <see cref="ReversalMismatch"/></summary>
    public const string ReversalRuleCode = "GL-REVERSAL-BALANCE";

    /// <summary>
    /// ตัวกลับรายการต้องมาจาก<b>บรรทัดที่บันทึกไว้จริงทุกบรรทัด</b> และลงตัว (pure · ด่านป้องกันชั้นสุดท้าย) — <c>loaded*</c> = บรรทัดที่โหลดมาสร้างตัวกลับ ·
    /// <c>persisted*</c> = ยอดรวมบรรทัดของใบนั้นในฐาน (คิวรีที่ไม่ join ผัง) · ไม่ผ่าน = ข้อความ (null = ผ่าน): บรรทัดหายระหว่างโหลด (เช่น ตัวกรองผังที่ลบ)
    /// ⇒ ห้ามเขียนตัวกลับ · <b>ใบเดิมไม่ลงตัวกลับรายการได้</b> (ตัวกลับ = กระจกของบรรทัดที่บันทึกจริงทุกบรรทัด ⇒ รวมกันเป็นศูนย์ = ล้างส่วนต่างออกจาก GL ·
    /// ฝ่ายค้านรอบ 5 P1-1: เดิมบล็อก ⇒ ยกเลิกเอกสาร/POS/เงินเดือนที่ JE เก่าต่าง 0.01 ไปต่อไม่ได้) · <b>ไม่เทียบหัวใบ</b> (TotalDebit/TotalCredit) — ใบเก่าบางทางไม่ได้ปรับหัวใบตามบรรทัด ⇒ บล็อกยกเลิกเอกสารโดยไม่มีทางไปต่อ
    /// </summary>
    public static string? ReversalMismatch(string originalNumber, int loadedCount, decimal loadedDebit, decimal loadedCredit,
        int persistedCount, decimal persistedDebit, decimal persistedCredit)
    {
        static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        if (loadedCount != persistedCount || R(loadedDebit) != R(persistedDebit) || R(loadedCredit) != R(persistedCredit))
            return $"กลับรายการ {originalNumber} ไม่ได้: โหลดบรรทัดได้ {loadedCount} บรรทัด (เดบิต {loadedDebit:N2} / เครดิต {loadedCredit:N2}) "
                + $"แต่ใบนี้มี {persistedCount} บรรทัด (เดบิต {persistedDebit:N2} / เครดิต {persistedCredit:N2}) — ยังไม่มีอะไรถูกบันทึก (กันตัวกลับไม่ลงตัว) · "
                + "แจ้งผู้ดูแลระบบพร้อมเลขที่ใบนี้";
        return null;
    }
}
