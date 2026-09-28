using Accounting.Services;

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

    /// <summary>รหัสที่ migration ต้องใส่ให้บริษัทเดิม (เรียงตามเลข)</summary>
    public static readonly IReadOnlyList<string> SeededCodes = new[]
    {
        ReserveAccountCode, PaymentFeeAccountCode, ChargebackLossAccountCode,
    };

    /// <summary>INSERT ผังทั้งสามให้บริษัทที่มีกลุ่มแม่ในผังมาตรฐานแล้ว (113 · 531 · 571) — ผูก <c>ParentAccountId</c> กับกลุ่มแม่ของบริษัทนั้น ·
    /// <c>ON CONFLICT DO NOTHING</c> (unique CompanyId+AccountCode) ⇒ รันทุกบูตได้ ไม่ทับรหัสที่ลูกค้าตั้งเอง · ไม่ย้ายยอดใด ๆ</summary>
    public static string MigrationSeedSql()
    {
        var common = ChartOfAccountTemplates.GetCommonAccounts();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rows = SeededCodes.Select(code =>
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
