using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 230 (erp-review/2026-11-01/TAX_PENALTY_AUDIT.md §6) — เครื่องคำนวณ CIT หน้าบริหารการเงิน: กติกาตัวตั้งเดียว <see cref="CitCalculation"/> +
/// มิติรอบบัญชีของ <see cref="CitRateTable"/> · ทุกกติกาล็อกสองทิศ (ใบที่พังกลับมาถูก + ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class CitCalculationTests
{
    // ── ตารางอัตราตามรอบบัญชี (CitRateBracket) ──

    [Fact]
    public void ตารางอัตรา_ตามรอบบัญชี_SMEขั้นบันได_ทั่วไป20()
    {
        Assert.Equal(105_000m, CitRateTable.Compute(1_000_000m, isSme: true, fiscalYear: 2026));
        Assert.Equal(200_000m, CitRateTable.Compute(1_000_000m, isSme: false, fiscalYear: 2026));
        Assert.Equal(0m, CitRateTable.Compute(300_000m, isSme: true, fiscalYear: 2026));
        Assert.Equal(945_000m, CitRateTable.Compute(5_700_000m, isSme: true, fiscalYear: 2026));   // 405,000 + 2,700,000 × 20%
    }

    [Theory]
    [InlineData(0.30, true)]
    [InlineData(450_000.70, true)]
    [InlineData(2_999_999.99, true)]
    [InlineData(12_345_678.91, false)]
    public void ตารางอัตรา_overloadเดิมไม่มีปี_เท่ากับตารางล่าสุด(double profit, bool sme)
        => Assert.Equal(CitRateTable.Compute((decimal)profit, sme), CitRateTable.Compute((decimal)profit, sme, 2026));

    [Fact]
    public void ตารางอัตรา_รอบก่อนตาราง_ใช้ชุดที่เก่าที่สุด_และบอกว่าไม่มีตาราง()
    {
        Assert.False(CitRateTable.HasScheduleFor(2016));
        Assert.True(CitRateTable.HasScheduleFor(CitRateTable.EarliestScheduledFiscalYear));
        Assert.Equal(CitRateTable.Compute(1_000_000m, true, 2017), CitRateTable.Compute(1_000_000m, true, 2016));
        // แถวทั่วไปต้องเป็นอัตราเดียวกับค่าคงที่ (ห้ามสองความจริง)
        Assert.All(CitRateTable.RateBrackets.Where(b => !b.Sme), b => Assert.Equal(CitRateTable.StandardRate, b.Rate));
        Assert.Equal(3, CitRateTable.SmeBrackets.Count);
    }

    // ── (1) SME ต่อบริษัท ──

    [Theory]
    [InlineData(4_000_000d, 20_000_000d, true)]      // เข้าทั้งสองข้อ
    [InlineData(5_000_000d, 30_000_000d, true)]      // ขอบ = เข้า
    [InlineData(6_000_000d, 20_000_000d, false)]     // ทุนเกิน
    [InlineData(4_000_000d, 31_000_000d, false)]     // รายได้เกิน
    [InlineData(0d, 1_000_000d, false)]              // ไม่รู้ทุน = ไม่ใช่ SME (Q10)
    public void SME_ระบบตัดสินจากทุนและรายได้(double capital, double revenue, bool expected)
    {
        var d = CitCalculation.DecideSme((decimal)capital, (decimal)revenue, null, null);
        Assert.Equal(expected, d.IsSme);
        Assert.False(d.Overridden);
    }

    [Fact]
    public void SME_กำหนดเองต้องมีเหตุผล_ไม่มีถูกปฏิเสธ()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => CitCalculation.DecideSme(10_000_000m, 1m, true, "  "));
        Assert.Equal(CitCalculation.SmeOverrideRuleCode, ex.RuleCode);
    }

    [Fact]
    public void SME_กำหนดเองสองทิศ_พร้อมเหตุผลไปถึงข้อความ()
    {
        var up = CitCalculation.DecideSme(10_000_000m, 1_000_000m, true, "ทุนชำระ ณ วันสิ้นรอบ 4 ล้าน");
        Assert.True(up.IsSme);
        Assert.True(up.Overridden);
        Assert.Contains("ทุนชำระ ณ วันสิ้นรอบ 4 ล้าน", up.Reason);
        var down = CitCalculation.DecideSme(1_000_000m, 1_000_000m, false, "ถือหุ้นโดยบริษัทใหญ่");
        Assert.False(down.IsSme);
        Assert.True(down.Overridden);
    }

    // ── (2) รอบบัญชีของบริษัท ──

    [Fact]
    public void รอบบัญชี_เมษายน_ครึ่งปีถึงกันยายน_ทั้งปีถึงมีนาคมปีถัดไป()
    {
        var half = CitCalculation.PeriodFor(2026, 4, CitCalculation.HalfYear);
        Assert.Equal(new DateTime(2026, 4, 1), half.Start);
        Assert.Equal(new DateTime(2026, 9, 30), half.EndInclusive);
        Assert.True(half.IsHalfYear);
        var annual = CitCalculation.PeriodFor(2026, 4, CitCalculation.Annual);
        Assert.Equal(new DateTime(2027, 3, 31), annual.EndInclusive);
        Assert.Equal(new DateTime(2027, 3, 31), annual.PostingDate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]     // ยังไม่ตั้งค่า = ปีปฏิทิน (ไม่แตะบริษัทรอบ ม.ค.–ธ.ค.)
    public void รอบบัญชี_ปีปฏิทิน_เหมือนเดิม(int startMonth)
    {
        Assert.Equal(new DateTime(2026, 6, 30), CitCalculation.PeriodFor(2026, startMonth, CitCalculation.HalfYear).EndInclusive);
        Assert.Equal(new DateTime(2026, 12, 31), CitCalculation.PeriodFor(2026, startMonth, CitCalculation.Annual).EndInclusive);
        Assert.Equal(new DateTime(2026, 1, 1), CitCalculation.PeriodFor(2026, startMonth, CitCalculation.Annual).Start);
    }

    // ── (5) วันที่ลงบัญชี = วันสิ้นงวด + งวดปิด ──

    [Fact]
    public void ลงบัญชี_งวดยังไม่จบ_ปฏิเสธ_งวดปิด_ปฏิเสธ_เปิดและจบแล้ว_ผ่าน()
    {
        var p = CitCalculation.PeriodFor(2026, 1, CitCalculation.HalfYear);
        var (m1, c1) = CitCalculation.PostingRefusal(p, new DateTime(2026, 6, 29), null);
        Assert.Equal(CitCalculation.PeriodNotEndedRuleCode, c1);
        Assert.Contains("30/06/2026", m1);
        var (m2, c2) = CitCalculation.PostingRefusal(p, new DateTime(2026, 8, 10), "งวด '2026/06' ปิดแล้ว");
        Assert.Equal(CitCalculation.PeriodClosedRuleCode, c2);
        Assert.Contains("2026/06", m2);
        Assert.Null(CitCalculation.PostingRefusal(p, new DateTime(2026, 6, 30), null).Message);   // วันสิ้นงวดเอง = ลงได้
        Assert.Null(CitCalculation.PostingRefusal(p, new DateTime(2026, 8, 10), null).RuleCode);
    }

    // ── (3) ภ.ง.ด.51 ──

    [Fact]
    public void ภงด51_ค่าตั้งต้น_กำไรครึ่งปีคูณสอง_ภาษีทั้งปีหารสอง_ไม่ใช่ภาษีของกำไร6เดือน()
    {
        // SME กำไรครึ่งปี 600,000: เดิม = ภาษีของ 600,000 = 45,000 · ถูก = ภาษีของ 1,200,000 (135,000) ÷ 2 = 67,500
        var r = CitCalculation.HalfYearTax(600_000m, null, null, isSme: true, fiscalYear: 2026);
        Assert.Equal(CitCalculation.BasisEstimate, r.Basis);
        Assert.Equal(1_200_000m, r.EstimatedAnnualProfit);
        Assert.Equal(135_000m, r.AnnualTaxOnBasis);
        Assert.Equal(67_500m, r.HalfYearTax);
        Assert.NotEqual(CitRateTable.Compute(600_000m, true, 2026), r.HalfYearTax);
    }

    [Fact]
    public void ภงด51_ทั่วไป_ผลเท่ากับวิธีเดิมเมื่อไม่มีขั้นบันได()
    {
        // ทิศตรงข้าม: บริษัททั่วไป 20% ตรง ๆ — ประมาณการ × 2 ÷ 2 = ภาษีของกำไรครึ่งปี (ไม่ถูกแตะ)
        var r = CitCalculation.HalfYearTax(600_000m, null, null, isSme: false, fiscalYear: 2026);
        Assert.Equal(120_000m, r.HalfYearTax);
    }

    [Fact]
    public void ภงด51_ประมาณการที่ผู้ใช้กรอก_ชนะค่าตั้งต้น()
    {
        var r = CitCalculation.HalfYearTax(600_000m, 2_000_000m, CitCalculation.BasisEstimate, isSme: false, fiscalYear: 2026);
        Assert.Equal(2_000_000m, r.EstimatedAnnualProfit);
        Assert.Equal(200_000m, r.HalfYearTax);
        Assert.Contains("ผู้ใช้กรอก", r.Note);
        Assert.Equal(0m, CitCalculation.HalfYearTax(600_000m, -5m, null, true, 2026).HalfYearTax);
    }

    [Fact]
    public void ภงด51_ตามกำไรจริงครึ่งปี_67ทวิ2_ไม่รับช่องประมาณการ()
    {
        var r = CitCalculation.HalfYearTax(600_000m, null, CitCalculation.BasisActualHalfYear, isSme: false, fiscalYear: 2026);
        Assert.Equal(CitCalculation.BasisActualHalfYear, r.Basis);
        Assert.Equal(120_000m, r.HalfYearTax);
        var ex = Assert.Throws<BusinessRuleException>(() =>
            CitCalculation.HalfYearTax(600_000m, 1_000_000m, CitCalculation.BasisActualHalfYear, false, 2026));
        Assert.Equal("CIT-PND51-BASIS", ex.RuleCode);
    }

    [Fact]
    public void ภงด51_รอบไม่ถึง12เดือน_ยกเว้นเมื่อผู้ใช้ประกาศ_ระบบไม่ยกเว้นเอง()
    {
        var fy = FiscalYear.RangeFor(2026, 1);
        Assert.True(CitCalculation.Pnd51Exemption(true, null, fy).Exempt);
        // เริ่มใช้ระบบกลางรอบ ⇒ เตือน แต่ไม่ยกเว้น (CreatedAt ไม่ใช่วันจดทะเบียน)
        var (ex2, note2) = CitCalculation.Pnd51Exemption(null, new DateTime(2026, 5, 1), fy);
        Assert.False(ex2);
        Assert.Contains("ไม่ถึง 12 เดือน", note2);
        var (ex3, note3) = CitCalculation.Pnd51Exemption(false, new DateTime(2026, 5, 1), fy);
        Assert.False(ex3);
        Assert.Null(note3);
        var (ex4, note4) = CitCalculation.Pnd51Exemption(null, new DateTime(2020, 5, 1), fy);
        Assert.False(ex4);
        Assert.Null(note4);
    }

    // ── (4) กำไรสุทธิทางภาษี ──

    // ฝ่ายค้าน S7: เฉพาะหัวหมวด 580 · 58000–58099 (5 หลัก) · ผังลูกของ 580 — "5800"/"580500" ที่ลูกค้าตั้งเองยังหักได้
    [Theory]
    [InlineData("58000", AccountType.Expense, null, true)]
    [InlineData("58010", AccountType.Expense, null, true)]
    [InlineData("580", AccountType.Expense, null, true)]
    [InlineData("58199", AccountType.Expense, "580", true)]     // ผังลูกของหัวหมวด 580
    [InlineData("5800", AccountType.Expense, null, false)]      // รหัสที่ลูกค้าตั้งเอง 4 หลัก — ไม่ใช่ภาษีเงินได้
    [InlineData("580500", AccountType.Expense, null, false)]
    [InlineData("58100", AccountType.Expense, null, false)]
    [InlineData("54820", AccountType.Expense, null, false)]     // ค่าปรับ — บวกกลับผ่าน §65 ตรี(6) ไม่ใช่ตัดทิ้ง
    [InlineData("58000", AccountType.Liability, null, false)]
    public void ผังค่าใช้จ่ายภาษีเงินได้_กติกาpureกับตัวกรองSQL_ชุดเดียวกัน(string code, AccountType type, string? parent, bool isCit)
    {
        Assert.Equal(isCit, CitCalculation.IsCitExpenseAccount(code, type, parent));
        var line = new JournalEntryLine { Account = new ChartOfAccount { AccountCode = code, AccountType = type, AccountName = "x",
            ParentAccount = parent == null ? null : new ChartOfAccount { AccountCode = parent, AccountName = "หัวหมวด" } } };
        Assert.Equal(!isCit, new[] { line }.AsQueryable().ExceptCitExpense().Any());
    }

    [Fact]
    public void กำไรสุทธิทางภาษี_บวกกลับอัตโนมัติและที่กรอก_หักรายได้ยกเว้น()
    {
        Assert.Equal(670_000m, CitCalculation.TaxableProfit(660_000m, 10_000m, 0m, 0m, 0m));
        Assert.Equal(600_000m, CitCalculation.TaxableProfit(660_000m, 10_000m, 5_000m, 50_000m, 25_000m));
        Assert.Equal(-100m, CitCalculation.TaxableProfit(-100m, 0m, 0m, 0m, 0m));   // ขาดทุน = ติดลบ (ภาษี 0 ที่ตาราง)
        Assert.Null(CitCalculation.RejectNegative(("a", 0m), ("b", null)));
        Assert.Contains("รายได้ยกเว้น", CitCalculation.RejectNegative(("รายได้ยกเว้น", -1m)));
    }

    [Fact]
    public void ค่ารับรอง_เพดานMAXรายได้หรือทุน_ไม่เกิน10ล้าน_ครึ่งปีใช้ครึ่งเพดาน()
    {
        Assert.Equal(30_000m, CitCalculation.EntertainmentCap(1_000_000m, 10_000_000m));          // ฐานทุนสูงกว่า
        Assert.Equal(30_000m, CitCalculation.EntertainmentCap(10_000_000m, 1_000_000m));          // ฐานรายได้สูงกว่า
        Assert.Equal(10_000_000m, CitCalculation.EntertainmentCap(5_000_000_000m, 0m));           // เพดาน 10 ล้าน
        Assert.Equal(10_000m, CitCalculation.EntertainmentExcess(40_000m, 1_000_000m, 10_000_000m, halfYear: false));
        Assert.Equal(0m, CitCalculation.EntertainmentExcess(20_000m, 1_000_000m, 10_000_000m, halfYear: false));
        Assert.Equal(25_000m, CitCalculation.EntertainmentExcess(40_000m, 1_000_000m, 10_000_000m, halfYear: true));   // เพดานครึ่งปี 15,000
        // ฝ่ายค้าน S6: ระบุชัด (54460 · ชื่อ "รับรอง"/entertainment) — ไม่ใช้ "ภาษีซื้อขอคืนไม่ได้" (รถยนต์นั่ง ฯลฯ ไม่ใช่ค่ารับรอง)
        Assert.True(CitCalculation.IsEntertainmentAccount("54460", "ค่าใช้จ่ายอื่น"));
        Assert.True(CitCalculation.IsEntertainmentAccount("544601", null));
        Assert.True(CitCalculation.IsEntertainmentAccount("53100", "ค่ารับรองลูกค้า"));
        Assert.True(CitCalculation.IsEntertainmentAccount("53101", "Client Entertainment"));
        Assert.False(CitCalculation.IsEntertainmentAccount("53300", "ค่าน้ำมันรถยนต์นั่ง"));
        Assert.False(CitCalculation.IsEntertainmentAccount("53000", "ค่าใช้จ่ายบริหาร"));
    }

    // ── §67 ตรี กับวิธี §67 ทวิ(2) ──

    [Fact]
    public void มาตรา67ตรี_ยื่นตามกำไรจริงครึ่งปี_ไม่คิด_ทิศตรงข้ามยังคิด()
    {
        var facts = new CitPayment.EstimateFacts(1_000_000m, 2_000_000m, 400_000m, 100_000m);
        Assert.Equal(20_000m, CitPayment.Section67Ter(facts).Amount);
        var actual = CitPayment.Section67Ter(facts with { ActualHalfYearBasis = true });
        Assert.False(actual.Applies);
        Assert.Equal(0m, actual.Amount);
        Assert.Contains("67 ทวิ(2)", actual.Note);
    }

    // ── ฝ่ายค้าน S4 ผลขาดทุนยกมา ──

    [Fact]
    public void ขาดทุนยกมา_FIFO_5รอบ_หมดอายุ_และใช้ไม่เกินกำไร()
    {
        var prior = new[] { (2019, -100_000m), (2020, 30_000m), (2021, -50_000m), (2022, 0m) };
        // 2019 ขาด 100k → 2020 ใช้ 30k เหลือ 70k · 2021 ขาด 50k ⇒ รอบ 2024 มี 120k
        Assert.Equal(120_000m, CitCalculation.LossCarryForwardAvailable(prior, 2024));
        // รอบ 2025: ขาดทุน 2019 ใช้ได้ถึง 2024 ⇒ หมดอายุ เหลือ 50k ของ 2021
        Assert.Equal(50_000m, CitCalculation.LossCarryForwardAvailable(prior, 2025));
        Assert.Equal(0m, CitCalculation.LossCarryForwardAvailable(Array.Empty<(int, decimal)>(), 2025));
        Assert.Equal(40_000m, CitCalculation.LossCarryForwardUsed(120_000m, 40_000m));
        Assert.Equal(0m, CitCalculation.LossCarryForwardUsed(120_000m, -1m));
        // รอบที่ไม่อยู่ก่อนปีที่คำนวณไม่นับ (ส่งมาทั้งชุดได้)
        Assert.Equal(120_000m, CitCalculation.LossCarryForwardAvailable(prior.Append((2024, -999m)), 2024));
    }

    // ── ฝ่ายค้าน S5 เครดิตภาษีถูกหัก ──

    [Fact]
    public void เครดิตภาษีถูกหัก_วันที่หนังสือรับรองในงวด_สถานะได้รับแล้ว_ไม่มีวันที่ไม่นับ()
    {
        var cid = Guid.NewGuid();
        WhtCreditReceived W(DateTime? d, WhtCreditStatus st, decimal amt, Guid? company = null)
            => new() { CompanyId = company ?? cid, CertificateDate = d, Status = st, WhtAmount = amt, TaxYear = 2026 };
        var rows = new[]
        {
            W(new DateTime(2026, 4, 1), WhtCreditStatus.Received, 100m),          // วันแรกของรอบ เม.ย.
            W(new DateTime(2027, 3, 31, 15, 0, 0), WhtCreditStatus.Claimed, 200m), // วันสุดท้าย 15:00 — นับ
            W(new DateTime(2026, 3, 31), WhtCreditStatus.Received, 400m),         // ก่อนรอบ
            W(new DateTime(2026, 5, 1), WhtCreditStatus.Pending, 800m),           // ยังไม่ได้ใบ
            W(null, WhtCreditStatus.Received, 1_600m),                            // ไม่มีวันที่
            W(new DateTime(2026, 5, 1), WhtCreditStatus.Received, 3_200m, Guid.NewGuid()),   // บริษัทอื่น
        }.AsQueryable();
        Assert.Equal(300m, rows.CreditableInPeriod(cid, new DateTime(2026, 4, 1), new DateTime(2027, 3, 31)).Sum(w => w.WhtAmount));
        Assert.Equal(1, rows.CreditableUndated(cid, 2026).Count());
    }

    // ── ฝ่ายค้าน S2 วันครบกำหนดจากวันสิ้นงวด ──

    [Fact]
    public void วันครบกำหนดภงด51_50_จากวันสิ้นงวดจริง_รอบเมษายน()
    {
        var k51 = CitPayment.DueKey(CitPayment.Pnd51Type, 2026, new DateTime(2026, 9, 30), 4);
        Assert.Equal((2026, 9, false), k51);
        // สิ้นเดือนที่ 2 หลังสิ้นครึ่งปี (30 พ.ย.) — เลื่อนพ้นเสาร์/อาทิตย์ได้ไม่กี่วัน · ไม่ใช่ 31 ส.ค. ของรอบ ม.ค.–ธ.ค.
        Assert.True(TaxFilingDeadline.For(CitPayment.Pnd51Type, 2026, 9).Paper.Date >= new DateTime(2026, 11, 30));
        Assert.True(TaxFilingDeadline.For(CitPayment.Pnd51Type, 2026, 9).Paper.Date < new DateTime(2026, 12, 5));
        var k50 = CitPayment.DueKey(CitPayment.Pnd50Type, 2026, new DateTime(2027, 3, 31), 4);
        Assert.Equal((2027, 3, false), k50);
        Assert.True(TaxFilingDeadline.For(CitPayment.Pnd50Type, 2027, 3).Paper.Date >= new DateTime(2027, 3, 31).AddDays(150));
    }

    [Fact]
    public void วันครบกำหนด_แถวเดิมไม่มีวันสิ้นงวด_ปีปฏิทินไม่legacy_รอบอื่นlegacy()
    {
        Assert.Equal((2026, 6, false), CitPayment.DueKey(CitPayment.Pnd51Type, 2026, null, 1));
        Assert.Equal((2026, 12, false), CitPayment.DueKey(CitPayment.Pnd50Type, 2026, null, 0));
        Assert.Equal((2026, 12, true), CitPayment.DueKey(CitPayment.Pnd50Type, 2026, null, 4));
    }

    // ── ฝ่ายค้าน S9 เงินทดแทนเมื่อรายได้เปลี่ยน ──

    [Theory]
    [InlineData(15_000, 15_000, 18_000, 20_000, true)]    // ฐานชนเพดาน + รายได้เปลี่ยน ⇒ คิดใหม่
    [InlineData(10_000, 10_000, 15_000, 17_000, false)]   // ฐานต่ำกว่าเพดาน + แก้แค่รายได้ ⇒ ไม่คิดใหม่ (ฐานที่ประกาศเป็นตัวตั้ง)
    [InlineData(10_000, 12_000, 15_000, 15_000, true)]    // ฐานเปลี่ยน (เดิม)
    [InlineData(15_000, 15_000, 18_000, 18_000, false)]   // ไม่มีอะไรเปลี่ยน
    public void เงินทดแทน_ต้องคิดใหม่เมื่อไร(int baseBefore, int baseAfter, int grossBefore, int grossAfter, bool expected)
        => Assert.Equal(expected, PayrollDetailAmounts.WorkersCompensationInputsChanged(baseBefore, baseAfter, grossBefore, grossAfter, 15_000m));

    // ── ค่าเสื่อมที่ยังไม่ลงบัญชี (คำตัดสินรอบ 230) ──

    [Theory]
    [InlineData(false, false, true)]    // ยังไม่ลงบัญชี ⇒ นับ
    [InlineData(true, true, false)]     // ลงบัญชีแล้ว (อยู่ใน GL) ⇒ ไม่นับซ้ำ
    [InlineData(true, false, false)]    // ธงใดธงหนึ่งบอกว่าลงแล้ว ⇒ ไม่นับ (ทิศไม่หักซ้ำ)
    [InlineData(false, true, false)]
    public void ค่าเสื่อม_นับเฉพาะแถวที่ยังไม่ลงบัญชี_ตัวกรองSQL(bool isPosted, bool hasJe, bool counted)
    {
        Guid? je = hasJe ? Guid.NewGuid() : null;
        var row = new AssetDepreciation { IsPosted = isPosted, JournalEntryId = je, Amount = 1m };
        Assert.Equal(counted, new[] { row }.AsQueryable().Unbooked().Any());
    }
}
