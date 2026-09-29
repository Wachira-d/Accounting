using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม W — อัตราหัก ณ ที่จ่ายจ่ายต่างประเทศ (ม.70 ภ.ง.ด.54) + ตารางอนุสัญญาภาษีซ้อน (คำตัดสินข้อ 13) · สองทิศทุกเรื่อง:
/// มีอนุสัญญา + หนังสือรับรองถิ่นที่อยู่ ⇒ อัตราอนุสัญญา · ไม่มี CoR / ไม่มีแถว / ไม่รู้ประเทศ ⇒ ม.70 เต็ม (ไม่ใช่ 0) ·
/// ประเภทเงินได้นอก ม.70 / ไม่รู้ ⇒ "ไม่มีอัตรา" (ห้ามตกอัตราในประเทศ)
/// </summary>
public class ForeignWhtRateResolverTests
{
    private static readonly DateTime Pay = new(2026, 9, 20);

    /// <summary>แถวสมมติสำหรับเทสต์เท่านั้น (ประเทศรหัส "ZZ" ไม่มีจริง) — ตารางจริงต้องมาจากตัวบททางการ</summary>
    private static readonly DtaTreatyRate[] FakeTable =
    {
        new("ZZ", ForeignIncomeCategory.Royalty, 5m, "ผู้รับเป็นเจ้าของผลประโยชน์", "อนุสัญญาทดสอบ ข้อ 12", "test://", Pay, new DateTime(2020, 1, 1)),
        new("ZZ", ForeignIncomeCategory.FeesCommission, 0m, "ไม่มีสถานประกอบการถาวรในไทย", "อนุสัญญาทดสอบ ข้อ 7", "test://", Pay, new DateTime(2020, 1, 1)),
        new("ZZ", ForeignIncomeCategory.Interest, 20m, "อัตราอนุสัญญาสูงกว่ากฎหมายไทย", "อนุสัญญาทดสอบ ข้อ 11", "test://", Pay, new DateTime(2020, 1, 1)),
    };

    private static readonly ResidenceCertificate CoR = new(true, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

    [Theory]
    [InlineData("2", ForeignIncomeCategory.FeesCommission)]
    [InlineData("3", ForeignIncomeCategory.Royalty)]
    [InlineData("40(3)", ForeignIncomeCategory.Royalty)]
    [InlineData("4a", ForeignIncomeCategory.Interest)]
    [InlineData("4b", ForeignIncomeCategory.Dividend)]
    [InlineData("5", ForeignIncomeCategory.Rent)]
    [InlineData("6", ForeignIncomeCategory.Professional)]
    [InlineData("7", ForeignIncomeCategory.OutsideSection70)]
    [InlineData("8", ForeignIncomeCategory.OutsideSection70)]
    [InlineData("8ad", ForeignIncomeCategory.OutsideSection70)]
    [InlineData("8tr", ForeignIncomeCategory.OutsideSection70)]
    [InlineData("1", ForeignIncomeCategory.OutsideSection70)]
    [InlineData("40(4)", ForeignIncomeCategory.Unknown)]        // ดอกเบี้ย 15% หรือปันผล 10% — ชี้ขาดไม่ได้ ห้ามเดา
    [InlineData(null, ForeignIncomeCategory.Unknown)]
    [InlineData("xyz", ForeignIncomeCategory.Unknown)]
    public void จำแนกประเภทเงินได้จากตารางประเภทเงินได้ตัวเดียว(string? code, ForeignIncomeCategory expected)
        => Assert.Equal(expected, ForeignWhtRateResolver.CategoryOf(code));

    [Fact]
    public void ไม่มีแถวอนุสัญญา_ได้อัตรา_ม70_เต็ม_ไม่ใช่ศูนย์()
    {
        var fee = ForeignWhtRateResolver.ResolveForIncomeCode("2", "SG", CoR, Pay);
        Assert.Equal(ForeignWhtOutcome.Section70, fee.Outcome);
        Assert.Equal(15m, fee.RatePercent);
        Assert.Equal("RD-70", fee.RuleCode);
        Assert.Equal("ป.รัษฎากร ม.70", fee.LegalReference);

        var dividend = ForeignWhtRateResolver.ResolveForIncomeCode("4b", "SG", CoR, Pay);
        Assert.Equal(10m, dividend.RatePercent);
        Assert.Equal("RD-70-DIV", dividend.RuleCode);

        // ไม่รู้ประเทศ ⇒ หาแถวไม่ได้ ⇒ ม.70
        Assert.Equal(15m, ForeignWhtRateResolver.ResolveForIncomeCode("3", null, CoR, Pay).RatePercent);
    }

    [Fact]
    public void ตารางจริงยังว่าง_ทุกประเทศได้_ม70()
    {
        Assert.Empty(DtaTreatyRates.Rows);   // ยังไม่มีแถวที่ยืนยันกับตัวบททางการ (team-W.md §3) — เติมแถวแล้วต้องแก้เทสต์นี้พร้อมช่อง CoR
        foreach (var cc in new[] { "SG", "IE", "NL", "US", "CN", "HK", "JP" })
            Assert.Equal(ForeignWhtOutcome.Section70, ForeignWhtRateResolver.ResolveForIncomeCode("3", cc, CoR, Pay).Outcome);
    }

    [Fact]
    public void มีแถวอนุสัญญา_และCoRครอบวันจ่าย_ได้อัตราอนุสัญญา_พร้อมอ้างข้อ()
    {
        var d = ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.Royalty, "zz", CoR, Pay, FakeTable);
        Assert.Equal(ForeignWhtOutcome.TreatyRate, d.Outcome);
        Assert.Equal(5m, d.RatePercent);
        Assert.Equal("DTA-ZZ", d.RuleCode);
        Assert.Equal("อนุสัญญาทดสอบ ข้อ 12", d.LegalReference);

        // กำไรธุรกิจไม่มีสถานประกอบการถาวร = 0% (ไทยไม่มีสิทธิเก็บ) — ต้องมี CoR
        Assert.Equal(0m, ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.FeesCommission, "ZZ", CoR, Pay, FakeTable).RatePercent);
    }

    [Fact]
    public void มีแถวอนุสัญญา_แต่ไม่มีCoR_หรือCoRไม่ครอบวันจ่าย_ได้_ม70()
    {
        var none = ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.Royalty, "ZZ", ResidenceCertificate.None, Pay, FakeTable);
        Assert.Equal(ForeignWhtOutcome.Section70, none.Outcome);
        Assert.Equal(15m, none.RatePercent);
        Assert.Contains("หนังสือรับรองถิ่นที่อยู่", none.Explanation);

        var expired = new ResidenceCertificate(true, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31));
        Assert.Equal(15m, ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.Royalty, "ZZ", expired, Pay, FakeTable).RatePercent);

        var notHeld = new ResidenceCertificate(false, null, null);
        Assert.Equal(15m, ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.FeesCommission, "ZZ", notHeld, Pay, FakeTable).RatePercent);
    }

    [Fact]
    public void อนุสัญญาที่มีผลหลังวันจ่าย_หรืออัตราสูงกว่ากฎหมายไทย_ไม่ถูกใช้()
    {
        var before = ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.Royalty, "ZZ", CoR, new DateTime(2019, 6, 1), FakeTable);
        Assert.Equal(15m, before.RatePercent);    // ฉบับที่อ้างมีผล 2020 — จ่ายปี 2019 ใช้ไม่ได้

        var higher = ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.Interest, "ZZ", CoR, Pay, FakeTable);
        Assert.Equal(ForeignWhtOutcome.Section70, higher.Outcome);
        Assert.Equal(15m, higher.RatePercent);    // อนุสัญญาจำกัดเพดาน ไม่ได้เพิ่มภาษีเกินกฎหมายไทย
    }

    [Fact]
    public void ประเภทเงินได้นอก_ม70_หรือไม่รู้_ไม่มีอัตรา_ห้ามตกอัตราในประเทศ()
    {
        var ads = ForeignWhtRateResolver.ResolveForIncomeCode("8ad", "IE", CoR, Pay);
        Assert.Equal(ForeignWhtOutcome.NotSection70, ads.Outcome);
        Assert.Null(ads.RatePercent);
        Assert.False(ads.HasRate);
        Assert.Equal("RD-70-NA", ads.RuleCode);

        var unknown = ForeignWhtRateResolver.ResolveForIncomeCode(null, "IE", CoR, Pay);
        Assert.Equal(ForeignWhtOutcome.UnknownIncomeType, unknown.Outcome);
        Assert.Null(unknown.RatePercent);
    }

    [Fact]
    public void เตือนเมื่อหักขาดหรือหักเกิน_เงียบเมื่อตรง()
    {
        var d = ForeignWhtRateResolver.ResolveForIncomeCode("3", "US", ResidenceCertificate.None, Pay);
        Assert.Null(ForeignWhtRateResolver.RateWarning(d, 15m));                         // ใบถูก — ห้ามเตือน (F2 ข้อ 8)
        var under = ForeignWhtRateResolver.RateWarning(d, 3m);                            // อัตราในประเทศกับผู้รับต่างประเทศ
        Assert.NotNull(under);
        Assert.Contains("§54", under);
        Assert.Contains("RD-70", under);
        Assert.Contains("ม.70", under);
        Assert.Contains("สูงกว่า", ForeignWhtRateResolver.RateWarning(d, 20m));

        var outside = ForeignWhtRateResolver.ResolveForIncomeCode("8", "US", ResidenceCertificate.None, Pay);
        Assert.NotNull(ForeignWhtRateResolver.RateWarning(outside, 15m));                // หักทั้งที่ประเภทนอก ม.70 ⇒ ให้คนจำแนก
        Assert.Null(ForeignWhtRateResolver.RateWarning(outside, 0m));                    // ไม่ได้หัก ⇒ ไม่มีอะไรเตือน
    }

    [Fact]
    public void หนังสือรับรองถิ่นที่อยู่_ครอบวัน()
    {
        Assert.False(ResidenceCertificate.None.Covers(Pay));
        Assert.True(CoR.Covers(Pay));
        Assert.True(new ResidenceCertificate(true, null, null).Covers(Pay));
        Assert.False(CoR.Covers(new DateTime(2027, 1, 1)));
    }
}
