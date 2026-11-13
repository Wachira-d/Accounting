using Accounting.Data;

namespace Accounting.Helpers;

/// <summary>
/// **ผังพัก "เงินรับจากลูกค้ารอจัดสรร" (21630) — ตัวหาตัวเดียว** (รอบ 258 · DECISIONS 258-1-1)
/// <para><b>บั๊กที่ปิด (P-1):</b> เส้นรับชำระหลายใบเดิมหา <c>21710</c> (ไม่มีในผังมาตรฐาน) แล้วตก "ผังแรกใต้ 217" ⇒ เงินส่วนเกินไปปนกับ
/// ค่าเช่ารับล่วงหน้า/รายได้รับล่วงหน้าที่กลไกมัดจำใช้ · ฝั่งจ่ายตก <c>11400 เงินให้กู้ยืมระยะสั้น</c></para>
/// <para>แถวผังมาจาก <c>ChartOfAccountTemplates.GetCommonAccounts</c> แหล่งเดียว · บริษัทเดิมได้ผังจาก
/// <see cref="MigrationSeedSql"/> ตอนบูต และ <see cref="EnsureAsync"/> สร้างให้ตอนใช้ถ้ายังไม่มี (ใต้กลุ่ม 216) ·
/// ผังไม่ใช่มาตรฐาน (ไม่มีกลุ่ม 216) ⇒ <b>ล้มดังพร้อมทางไปต่อ</b> — ไม่ตก prefix อื่นอีก</para>
/// </summary>
public static class UnappliedReceiptAccount
{
    /// <summary>21630 เงินรับจากลูกค้ารอจัดสรร (หนี้สินหมุนเวียน · ใต้ 216)</summary>
    public const string Code = "21630";
    public const string RuleCode = "CC-ACCOUNT";

    /// <summary>ข้อความเมื่อบริษัทไม่มีทั้งผัง 21630 และกลุ่มแม่ 216 (ผังกำหนดเอง)</summary>
    public const string MissingMessage =
        "ไม่พบผัง 21630 “เงินรับจากลูกค้ารอจัดสรร” และผังของบริษัทไม่มีกลุ่ม 216 ให้ระบบสร้างให้ — "
        + "เพิ่มผังรหัส 21630 (หมวดหนี้สิน) ที่หน้า “ผังบัญชี” แล้วบันทึกใหม่ หรือบันทึกรับชำระเท่ายอดค้างของใบ";

    /// <summary>คืนรหัสผัง 21630 ของบริษัท — สร้างให้ถ้ายังไม่มี (idempotent · ล็อกต่อบริษัท+รหัส) · สร้างไม่ได้ = <see cref="BusinessRuleException"/></summary>
    public static async Task<Guid> EnsureAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var id = await SettlementChartSeed.EnsureStandardAccountAsync(db, companyId, Code, ct);
        return id ?? throw new BusinessRuleException(MissingMessage, RuleCode);
    }

    /// <summary>อ่านอย่างเดียว: ผัง 21630 มีหรือสร้างได้ไหม (ด่านก่อนเส้นหลายขั้นของตัวช่วยใบซ้ำ — ไม่สร้างผังตอนพรีวิว)</summary>
    public static Task<bool> IsAvailableAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
        => SettlementChartSeed.CanProvideAsync(db, companyId, Code, ct);

    /// <summary>SQL ใส่ผัง 21630 ให้บริษัทเดิมที่มีกลุ่ม 216 (<c>ON CONFLICT DO NOTHING</c> — รันทุกบูตได้)</summary>
    public static string MigrationSeedSql() => SettlementChartSeed.SeedSqlFor(new[] { Code });
}
