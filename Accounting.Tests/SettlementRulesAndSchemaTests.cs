using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 เฟส 1 ทีม A — สัญญาที่ทีม B/C/D ใช้: ตารางกติกาประเภทบรรทัด · ภาษีค่าธรรมเนียม · ผังพักย่อย 11341–11349 ·
/// ผังมาตรฐาน 11350/53170/57140 · migration ต้องตรงกับ model ของ EF (ชื่อ index + เงื่อนไข partial)
/// </summary>
public class SettlementRulesAndSchemaTests
{
    // ═════════════════ SettlementLineTypeRules ═════════════════

    [Fact]
    public void ทุกค่าของenum_มีกติกาแถวเดียว_และUnclassifiedเท่านั้นที่ลงบัญชีไม่ได้()
    {
        foreach (var t in Enum.GetValues<SettlementLineType>())
        {
            Assert.Single(SettlementLineTypeRules.All, r => r.Type == t);
            Assert.Equal(t, SettlementLineTypeRules.For(t).Type);
            Assert.Equal(t != SettlementLineType.Unclassified, SettlementLineTypeRules.For(t).Postable);
        }
        Assert.Equal(19, Enum.GetValues<SettlementLineType>().Length);
        Assert.Equal(SettlementLineType.Unclassified, SettlementLineTypeRules.For((SettlementLineType)999).Type);
    }

    [Fact]
    public void ค่าธรรมเนียมทุกประเภทมีบทบาทผังที่มีผังมาตรฐาน_และอัตราWHTมาจากตารางกฎหมาย()
    {
        foreach (var r in SettlementLineTypeRules.All.Where(r => r.IsFee))
        {
            Assert.Equal(SettlementPostingKind.FeeDocument, r.Posting);
            Assert.NotNull(r.AccountRole);
            Assert.NotNull(SettlementAccountRoles.DefaultCode(r.AccountRole!));
            if (r.WhtIncomeCode != null)
                Assert.NotNull(ThaiWhtRateTable.RateFor(r.WhtIncomeCode, payeeIsJuristic: true));
        }
        Assert.True(SettlementLineTypeRules.For(SettlementLineType.Adjustment).RequiresReason);
        Assert.False(SettlementLineTypeRules.For(SettlementLineType.SellerVoucher).IsFee);   // ส่วนลดร้าน = ลดยอดขาย
        Assert.Equal(SettlementPostingKind.SaleComponent, SettlementLineTypeRules.For(SettlementLineType.PlatformVoucherSubsidy).Posting);
    }

    [Fact]
    public void ผังมาตรฐานของทุกบทบาทมีอยู่จริงในผังมาตรฐาน_ความหมายตรงชื่อ()
    {
        var chart = ChartOfAccountTemplates.GetCommonAccounts().ToDictionary(a => a.Code, a => a.NameTh);
        var expectWord = new Dictionary<string, string>
        {
            [SettlementAccountRoles.Reserve] = "กัน", [SettlementAccountRoles.Dispute] = "ลูกหนี้",
            [SettlementAccountRoles.Commission] = "นายหน้า", [SettlementAccountRoles.PaymentFee] = "รับชำระ",
            [SettlementAccountRoles.Shipping] = "ขนส่ง", [SettlementAccountRoles.Ads] = "โฆษณา",
            [SettlementAccountRoles.WithdrawalFee] = "ธรรมเนียม", [SettlementAccountRoles.WhtCredit] = "ถูกหัก",
            [SettlementAccountRoles.FxGain] = "แลกเปลี่ยน", [SettlementAccountRoles.FxLoss] = "แลกเปลี่ยน",
            [SettlementAccountRoles.ChargebackLoss] = "chargeback", [SettlementAccountRoles.WhtPayable] = "หัก ณ ที่จ่าย",
            [SettlementAccountRoles.InputVatPending] = "ภาษีซื้อ", [SettlementAccountRoles.Pp36InputVat] = "ภาษีซื้อ",
            [SettlementAccountRoles.Pp36Payable] = "36",
        };
        foreach (var (role, word) in expectWord)
        {
            var code = SettlementAccountRoles.DefaultCode(role);
            Assert.True(code != null && chart.ContainsKey(code), $"บทบาท {role} → {code} ไม่มีในผังมาตรฐาน");
            Assert.Contains(word, chart[code!]);
        }
        Assert.Null(SettlementAccountRoles.DefaultCode(SettlementAccountRoles.Adjustment));   // ผู้ใช้ต้องเลือก
        Assert.Null(SettlementAccountRoles.DefaultCode(SettlementAccountRoles.Clearing));     // มาจากช่องทาง
    }

    [Theory]
    [InlineData("Commission", SettlementLineType.Commission)]
    [InlineData("  paymentfee ", SettlementLineType.PaymentFee)]
    [InlineData("SellerVoucher", SettlementLineType.SellerVoucher)]
    public void คำตอบตัวจัดประเภท_ชื่อในชุดที่ลงบัญชีได้_ผ่าน(string answer, SettlementLineType expected)
        => Assert.Equal(expected, SettlementLineTypeRules.ParseClassifierAnswer(answer));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unclassified")]
    [InlineData("5")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("ค่าคอม")]
    [InlineData("Commission, PaymentFee")]
    public void คำตอบตัวจัดประเภท_แต่งหรือไม่รู้_ถูกปัดตก_คงรอจัดประเภท(string? answer)
        => Assert.Null(SettlementLineTypeRules.ParseClassifierAnswer(answer));

    [Fact]
    public void FeeAccountMap_รับเฉพาะบทบาทที่ตั้งได้และGuid_ที่เหลือรายงานไม่ทิ้งเงียบ()
    {
        var id = Guid.NewGuid();
        var (map, rejected) = SettlementLineTypeRules.ParseFeeAccountMap(
            "{\"commission\":\"" + id + "\",\"clearing\":\"" + Guid.NewGuid() + "\",\"ads\":\"not-a-guid\"}");
        Assert.Equal(id, Assert.Single(map).Value);
        Assert.Contains("clearing", rejected);
        Assert.Contains("ads", rejected);
        Assert.Equal(new[] { "(json)" }, SettlementLineTypeRules.ParseFeeAccountMap("{oops").Rejected);
        Assert.Empty(SettlementLineTypeRules.ParseFeeAccountMap(null).Map);
    }

    // ═════════════════ SettlementFeeTax ═════════════════

    [Fact]
    public void FeeTax_รวมVAT_แยกด้วย100ส่วน107_ปัดครั้งเดียว_ผลรวมเท่ายอดจริง()
    {
        var r = SettlementFeeTax.Compute(41.79m, null, SettlementFeeVatMode.ThaiVat7, true, true, SettlementFeeWhtMode.None, "8");
        Assert.Equal(39.06m, r.Expense);
        Assert.Equal(2.73m, r.InputVat);
        Assert.Equal(r.Deducted, r.Expense + r.InputVat);
        Assert.Equal(0m, r.WhtAmount);
        Assert.Null(r.WhtIncomeCode);
    }

    [Fact]
    public void FeeTax_ไฟล์ให้ยอดก่อนVAT_คิดVATทับแบบAwayFromZero()
    {
        Assert.Equal((41.79m, 2.73m), SettlementFeeTax.FromExclusive(39.06m));
        Assert.Equal((10.70m, 0.70m), SettlementFeeTax.FromExclusive(10m));
        Assert.Equal((0.54m, 0.04m), SettlementFeeTax.FromExclusive(0.50m));   // 0.035 → 0.04 (banker's จะได้ 0.04 เช่นกัน — ดูเคสถัดไป)
        Assert.Equal((1.61m, 0.11m), SettlementFeeTax.FromExclusive(1.50m));   // 0.105 → 0.11 (banker's = 0.10)
    }

    [Fact]
    public void FeeTax_ภพ36_ผู้จ่ายประเมินเสมอ_จดVATเคลมได้_ไม่จดเป็นต้นทุน()
    {
        var reg = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true, SettlementFeeWhtMode.None, "2");
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36, reg.VatTreatment);
        Assert.Equal(450m, reg.Expense);
        Assert.Equal(31.50m, reg.InputVat);
        Assert.Equal(31.50m, reg.Pp36Payable);
        // รอบ 198 ทีม C (review198-A R-A4 · คำตัดสิน main agent): §83/6 หน้าที่นำส่ง ภ.พ.36 อยู่ที่ผู้จ่ายแม้ไม่จด VAT —
        // เดิมคืน NoVat/ไม่มีหนี้ ⇒ ไม่เคยนำส่ง · ตอนนี้ตั้งหนี้ 21912 แต่ VAT เป็นต้นทุน (เคลมไม่ได้)
        var nonReg = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, false, SettlementFeeWhtMode.None, "2");
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable, nonReg.VatTreatment);
        Assert.Equal(31.50m, nonReg.Pp36Payable);
        Assert.Equal(0m, nonReg.InputVat);
        Assert.Equal(481.50m, nonReg.Expense);
    }

    [Fact]
    public void FeeTax_WHTประเภทที่ไม่หัก_หรือโหมดNone_ไม่มีภาษี()
    {
        var withdrawal = SettlementFeeTax.Compute(10.70m, null, SettlementFeeVatMode.ThaiVat7, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, null);
        Assert.Equal(SettlementFeeWhtMode.None, withdrawal.WhtMode);
        Assert.Equal(0m, withdrawal.WhtAmount);
        var ads = SettlementFeeTax.Compute(107m, null, SettlementFeeVatMode.ThaiVat7, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, "8ad");
        Assert.Equal(2m, ads.WhtRatePercent);
        Assert.Equal(2.00m, ads.WhtAmount);            // 100 × 2%
    }

    [Fact]
    public void SplitInclusive_ไม่จดVAT_ไม่แยก()
    {
        Assert.Equal((900m, 63m), SettlementFeeTax.SplitInclusive(963m, true));
        Assert.Equal((963m, 0m), SettlementFeeTax.SplitInclusive(963m, false));
    }

    // ═════════════════ ผังพักย่อย 11341–11349 ═════════════════

    [Fact]
    public void ผังพักย่อย_รหัสถัดไปที่ว่าง_นับแถวที่ลบแล้ว_ครบ9คืนnull()
    {
        Assert.Equal("11341", SettlementChannelAccounts.NextClearingCode(Array.Empty<string>()));
        Assert.Equal("11341", SettlementChannelAccounts.NextClearingCode(new[] { "11340", "11350", "113" }));
        Assert.Equal("11342", SettlementChannelAccounts.NextClearingCode(new[] { "11341", "11343" }));
        Assert.Null(SettlementChannelAccounts.NextClearingCode(Enumerable.Range(1, 9).Select(i => "1134" + i)));
        Assert.True(SettlementChannelAccounts.IsClearingCode("11349"));
        Assert.False(SettlementChannelAccounts.IsClearingCode("11340"));
        Assert.False(SettlementChannelAccounts.IsClearingCode("113410"));
        Assert.Equal("ลูกหนี้แพลตฟอร์ม Shopee ร้านหลัก", SettlementChannelAccounts.ClearingAccountName(" Shopee ร้านหลัก "));
        Assert.True(SettlementChannelAccounts.ClearingAccountName(new string('ก', 400)).Length <= 256);
    }

    // ═════════════════ ผังมาตรฐาน + migration ═════════════════

    [Fact]
    public void ผังมาตรฐานมี11350_53170_57140_ใต้กลุ่มที่ถูกต้อง_และไม่seedผังพักย่อย()
    {
        var chart = ChartOfAccountTemplates.GetCommonAccounts();
        Assert.Equal(AccountType.Asset, chart.Single(a => a.Code == "11350").Type);
        Assert.Equal(AccountType.Expense, chart.Single(a => a.Code == "53170").Type);
        Assert.Equal(AccountType.Expense, chart.Single(a => a.Code == "57140").Type);
        foreach (var code in SettlementChartSeed.SeededCodes)
            Assert.Contains(chart, a => a.Code == SettlementChartSeed.ParentGroupCode(code));
        Assert.DoesNotContain(chart, a => SettlementChannelAccounts.IsClearingCode(a.Code));
    }

    [Fact]
    public void Migration_ใส่ผังให้บริษัทเดิม_ONCONFLICTไม่ทับ_ไม่ย้ายยอด_อยู่เส้นหลักที่logความล้มเหลว()
    {
        var sql = SettlementChartSeed.MigrationSeedSql();
        foreach (var code in new[] { "'11350','113'", "'53170','531'", "'57140','571'" })
            Assert.Contains(code, sql);
        Assert.EndsWith("ON CONFLICT DO NOTHING;", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.DoesNotContain("1134" + "1", sql);

        var stmts = DatabaseMigrationHelper.SettlementSchemaStatements();
        var all = DatabaseMigrationHelper.GetAlterStatements();
        Assert.All(stmts, s => Assert.Contains(s, all));
        Assert.Contains(stmts, s => s.Contains("ADD COLUMN IF NOT EXISTS \"SettlementBatchId\" uuid NULL", StringComparison.Ordinal));
        Assert.DoesNotContain(stmts, s => s.Contains("RefundedAmount", StringComparison.Ordinal));   // ของทีม E
        // ตารางก่อน index ก่อนผัง
        var list = stmts.ToList();
        Assert.True(list.FindIndex(s => s.Contains("CREATE TABLE IF NOT EXISTS \"SettlementLines\"")) <
                    list.FindIndex(s => s.Contains("UX_SettlementLines_Channel_ExternalTxnId")));
        Assert.Equal(sql, list[^1]);
    }

    [Fact]
    public void Migration_indexตรงกับmodelของEF_ชื่อและเงื่อนไขpartial()
    {
        using var db = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);
        var migration = string.Join("\n", DatabaseMigrationHelper.SettlementSchemaStatements());
        foreach (var t in new[] { typeof(SettlementChannel), typeof(SettlementBatch), typeof(SettlementLine) })
        {
            var et = db.Model.FindEntityType(t)!;
            Assert.Contains($"CREATE TABLE IF NOT EXISTS \"{et.GetTableName()}\"", migration);
            foreach (var ix in et.GetIndexes().Where(i => i.GetDatabaseName()!.Contains("Settlement")))
            {
                var name = ix.GetDatabaseName()!;
                var stmt = DatabaseMigrationHelper.SettlementSchemaStatements().Single(s => s.Contains($"\"{name}\""));
                Assert.Equal(ix.IsUnique, stmt.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal));
                if (ix.GetFilter() is string f) Assert.Contains("WHERE " + f + ";", stmt);
                foreach (var p in ix.Properties) Assert.Contains($"\"{p.GetColumnName()}\"", stmt);
            }
            foreach (var p in et.GetProperties())
                Assert.Contains($"\"{p.GetColumnName()}\"", migration);
        }
    }

    [Fact]
    public void AiFeatureKey_ใหม่ต่อท้าย_ค่าคงที่57()
        => Assert.Equal(57, (int)AiFeatureKey.SettlementLineClassify);
}
