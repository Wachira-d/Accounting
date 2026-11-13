using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// ผังมาตรฐานที่ Settlement (wallet → ธนาคาร · รอบ 198) ต้องมี — <b>แหล่งเดียว</b>คือแถวใน
/// <see cref="ChartOfAccountTemplates.GetCommonAccounts"/> (บริษัทใหม่ได้จากการ seed ปกติ) · ตัวนี้แค่ดึงแถวเหล่านั้นมาทำ
/// SQL ใส่ให้<b>บริษัทเดิม</b> (DECISIONS ข้อ 4 · report-S1 §1.2)
/// <para>11341–11349 (ลูกหนี้แพลตฟอร์มต่อช่องทาง) <b>ไม่อยู่ที่นี่</b> — สร้างตอนผูกช่องทางผ่าน
/// <see cref="SettlementChannelAccounts.EnsureClearingAccountAsync"/></para>
/// </summary>
public static class SettlementChartSeed
{
    /// <summary>เงินที่ผู้ให้บริการกัน/ระงับไว้ (reserve/holdback · report-S1 G6)</summary>
    public const string ReserveAccountCode = "11350";
    /// <summary>ค่าธรรมเนียมรับชำระเงิน (MDR/gateway/payment fee · report-S1 D6)</summary>
    public const string PaymentFeeAccountCode = "53170";
    /// <summary>ขาดทุนจาก chargeback (report-S1 G5/D8) — ห้ามรวม 57130</summary>
    public const string ChargebackLossAccountCode = "57140";
    /// <summary>บัญชีพักผู้ให้บริการรับชำระเงิน (gateway · ขาเงินเข้าของ PaymentIntent) — ผังมาตรฐานที่ <c>GatewayAccountResolver</c> ใช้เมื่อ config ไม่ได้ตั้ง ·
    /// รอบ 232: บริษัทที่สร้างก่อนผังนี้เข้าแม่แบบไม่มีแถว ⇒ ผูกช่องทาง Omise ล้ม "ไม่มีผัง 11340" ⇒ seed ให้บริษัทเดิม + สร้างให้เองตอนใช้
    /// (<see cref="EnsureStandardAccountAsync"/>)</summary>
    public const string GatewayClearingAccountCode = "11340";

    /// <summary>รหัสที่ migration ต้องใส่ให้บริษัทเดิม (เรียงตามเลข)</summary>
    public static readonly IReadOnlyList<string> SeededCodes = new[]
    {
        GatewayClearingAccountCode, ReserveAccountCode, PaymentFeeAccountCode, ChargebackLossAccountCode,
    };

    private const string LockScope = "settlement-chart-seed";

    /// <summary>คืน AccountId ของผังมาตรฐาน <paramref name="code"/> ของบริษัท — <b>สร้างให้ถ้ายังไม่มี</b> จากแถวใน
    /// <see cref="ChartOfAccountTemplates.GetCommonAccounts"/> ใต้กลุ่มแม่ 3 หลักของบริษัทนั้น (ตัวเดียวกับ <see cref="MigrationSeedSql"/>) ·
    /// idempotent (ล็อก advisory ต่อบริษัท+รหัส · แถวที่ลบแล้ว = กู้คืนให้ใช้ได้ ไม่สร้างซ้ำเพราะ unique CompanyId+AccountCode) ·
    /// ไม่มีกลุ่มแม่ (ผังไม่ใช่มาตรฐาน) ⇒ คืน null — ผู้เรียกตัดสินเอง ("ไม่รู้ต้องบอกว่าไม่รู้") · บันทึกเอง (SaveChanges · ธุรกรรมของตัวเองเมื่อผู้เรียกไม่มี)</summary>
    public static async Task<Guid?> EnsureStandardAccountAsync(AccountingDbContext db, Guid companyId, string code, CancellationToken ct = default)
    {
        var existing = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == code && a.IsActive)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (existing != null) return existing;
        if (db.Database.CurrentTransaction != null) return await CreateUnderLockAsync(db, companyId, code, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = await CreateUnderLockAsync(db, companyId, code, ct);
            await tx.CommitAsync(ct);
            return id;
        });
    }

    /// <summary>อ่านอย่างเดียว: <see cref="EnsureStandardAccountAsync"/> จะคืนผังได้ไหม (มีแถวแล้ว รวมที่ลบ/ปิด — กู้คืนได้ · หรือมีกลุ่มแม่ที่ใช้งาน) —
    /// สำหรับพรีวิว/ด่านก่อนเริ่มเส้นหลายขั้น (รอบ 266 แพ็ก C · ไม่สร้างผังตอนพรีวิว) · เงื่อนไขเดียวกับตัวสร้าง</summary>
    public static async Task<bool> CanProvideAsync(AccountingDbContext db, Guid companyId, string code, CancellationToken ct = default)
    {
        if (await db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.AccountCode == code, ct)) return true;
        var parentCode = ParentGroupCode(code);
        return await db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.AccountCode == parentCode && a.IsActive, ct);
    }

    private static async Task<Guid?> CreateUnderLockAsync(AccountingDbContext db, Guid companyId, string code, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { AdvisoryLockKey.For(companyId, LockScope, code) }, ct);
        // แถวเดิม (รวมที่ลบ/ปิดใช้ — unique index ไม่กรอง IsDeleted) ⇒ เปิดใช้คืน ไม่สร้างซ้ำ
        var row = await db.ChartOfAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == code, ct);
        if (row != null)
        {
            if (row.IsDeleted || !row.IsActive)
            {
                row.IsDeleted = false; row.IsActive = true; row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return row.Id;
        }
        var t = ChartOfAccountTemplates.GetCommonAccounts().Single(a => a.Code == code);
        var parentCode = ParentGroupCode(code);
        var parent = await db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == parentCode && a.IsActive, ct);
        if (parent == null) return null;
        var account = new ChartOfAccount
        {
            CompanyId = companyId, AccountCode = t.Code, AccountName = t.NameTh, AccountNameEn = t.NameEn, AccountType = t.Type,
            ParentAccountId = parent.Id, Level = t.Level, IsActive = true, IsSystemAccount = true,
            Description = "สร้างอัตโนมัติจากผังมาตรฐานเมื่อระบบต้องใช้ (รอบ 232)",
        };
        db.ChartOfAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account.Id;
    }

    /// <summary>INSERT ผังทั้งสามให้บริษัทที่มีกลุ่มแม่ในผังมาตรฐานแล้ว (113 · 531 · 571) — ผูก <c>ParentAccountId</c> กับกลุ่มแม่ของบริษัทนั้น ·
    /// <c>ON CONFLICT DO NOTHING</c> (unique CompanyId+AccountCode) ⇒ รันทุกบูตได้ ไม่ทับรหัสที่ลูกค้าตั้งเอง · ไม่ย้ายยอดใด ๆ</summary>
    public static string MigrationSeedSql() => SeedSqlFor(SeededCodes);

    /// <summary>ตัวสร้าง SQL ตัวเดียวของ "ใส่แถวผังมาตรฐานให้บริษัทเดิม" — ผูกกลุ่มแม่ 3 หลักของบริษัทนั้น · <c>ON CONFLICT DO NOTHING</c> ·
    /// รหัสต้องมีใน <see cref="ChartOfAccountTemplates.GetCommonAccounts"/> (ไม่มี ⇒ ล้มตอนสร้าง SQL — ห้ามแต่งชื่อผังเอง) ·
    /// ผู้ใช้: ผังของ Settlement (รอบ 198) · ผัง 21915 ภ.ง.ด.2 (<see cref="WhtPayableAccount.MigrationSeedSql"/> · รอบ 204)</summary>
    public static string SeedSqlFor(IEnumerable<string> codes)
    {
        var common = ChartOfAccountTemplates.GetCommonAccounts();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rows = codes.Select(code =>
        {
            var t = common.Single(a => a.Code == code);
            return "('" + Q(t.Code) + "','" + Q(ParentGroupCode(t.Code)) + "','" + Q(t.NameTh) + "','" + Q(t.NameEn) + "',"
                 + ((int)t.Type).ToString(inv) + "," + t.Level.ToString(inv) + ")";
        });
        return "INSERT INTO \"ChartOfAccounts\" (\"Id\",\"CompanyId\",\"AccountCode\",\"AccountName\",\"AccountNameEn\",\"AccountType\","
             + "\"ParentAccountId\",\"Level\",\"IsActive\",\"IsSystemAccount\",\"InputVatClaimable\",\"CashFlowSection\",\"CreatedAt\",\"IsDeleted\") "
             + "SELECT gen_random_uuid(), p.\"CompanyId\", v.code, v.name_th, v.name_en, v.acct_type, p.\"Id\", v.lvl, true, true, true, 0, "
             + "now() at time zone 'utc', false FROM (VALUES " + string.Join(",", rows) + ") AS v(code,parent,name_th,name_en,acct_type,lvl) "
             + "JOIN \"ChartOfAccounts\" p ON p.\"AccountCode\" = v.parent AND p.\"IsDeleted\" = false "
             + "ON CONFLICT DO NOTHING;";
    }

    /// <summary>กลุ่มแม่ 3 หลักของรหัส 5 หลัก ("11350" → "113")</summary>
    public static string ParentGroupCode(string code) => code.Length >= 3 ? code[..3] : code;

    private static string Q(string s) => s.Replace("'", "''", StringComparison.Ordinal);
}
