using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 220 (คำตัดสินข้อ 181 · erp-review/2026-11-01/TAX_PENALTY_AUDIT.md) — เงินเพิ่ม/เบี้ยปรับ/ค่าปรับ ของการนำส่งภาษี-ประกันสังคมช้า
/// <para>ทั้งสองทิศ: จ่ายทันกำหนด ⇒ 0 ทุกก้อน · ช้า N วัน ⇒ ยอดตามกฎหมาย · เพดาน · การนับเดือน (เศษของเดือน = 1 เดือน) ·
/// ค่าที่ผู้ใช้แก้ชนะ · บรรทัด JE · ยอดบวกกลับ §65 ตรี(6) ไม่นับซ้ำ</para>
/// </summary>
public class LatePaymentPenaltyPolicyTests
{
    private static LatePaymentPenaltyPolicy.Suggestion S(string type, decimal tax, Func<DateTime, DateTime> payFromDue, int y = 2026, int m = 3)
    {
        var due = LatePaymentPenaltyPolicy.DueDate(type, y, m, null);
        return LatePaymentPenaltyPolicy.Suggest(type, y, m, tax, payFromDue(due), null);
    }

    // ═══ ทันกำหนด ⇒ 0 (ทิศที่ห้ามแตะ) ═══

    [Theory]
    [InlineData("WhtPnd1")] [InlineData("WhtPnd2")] [InlineData("WhtPnd3")] [InlineData("WhtPnd53")] [InlineData("WhtPnd54")]
    [InlineData("VatPp30")] [InlineData("VatPp36")] [InlineData("SsoSps110")]
    public void จ่ายตรงวันครบกำหนด_ไม่มีเงินเพิ่มและเบี้ยปรับ(string type)
    {
        var onDue = S(type, 10_000m, d => d);
        Assert.Equal(0m, onDue.Surcharge);
        Assert.Equal(0m, onDue.Penalty);
        Assert.Equal(0, onDue.MonthsLate);
        var early = S(type, 10_000m, d => d.AddDays(-5));
        Assert.Equal(0m, early.Total);
    }

    [Fact]
    public void ภาษีเป็นศูนย์หรือติดลบ_แม้จ่ายช้า_ไม่มีเงินเพิ่ม()
    {
        Assert.Equal(0m, S("VatPp30", 0m, d => d.AddDays(40)).Total);
        Assert.Equal(0m, S("WhtPnd53", -50m, d => d.AddMonths(3)).Total);
    }

    // ═══ ช้า ⇒ ม.27 / ม.89/1 1.5% ต่อเดือนหรือเศษ ═══

    [Fact]
    public void หักณที่จ่าย_ช้าหนึ่งวัน_เงินเพิ่ม_1_เดือน_150บาท_จากภาษี10000()
    {
        var s = S("WhtPnd3", 10_000m, d => d.AddDays(1));
        Assert.Equal(1, s.MonthsLate);
        Assert.Equal(150m, s.Surcharge);
        Assert.Equal(0m, s.Penalty);              // หัก ณ ที่จ่ายไม่มีเบี้ยปรับตามแบบ
        Assert.Equal("RD-27", s.SurchargeReference);
    }

    [Fact]
    public void นับเดือน_ครบเดือนพอดีเป็น1_เกินวันเดียวเป็น2()
    {
        Assert.Equal(1, S("WhtPnd53", 10_000m, d => d.AddMonths(1)).MonthsLate);
        var two = S("WhtPnd53", 10_000m, d => d.AddMonths(1).AddDays(1));
        Assert.Equal(2, two.MonthsLate);
        Assert.Equal(300m, two.Surcharge);
    }

    [Fact]
    public void เพดานเงินเพิ่ม_ไม่เกินจำนวนภาษี()
    {
        var s = S("WhtPnd1", 1_000m, d => d.AddMonths(80));
        Assert.Equal(1_000m, s.Surcharge);
        Assert.True(s.SurchargeCapped);
        Assert.Contains("ชนเพดาน", s.SurchargeNote);
        var notCapped = S("WhtPnd1", 1_000m, d => d.AddMonths(10));
        Assert.False(notCapped.SurchargeCapped);
    }

    [Fact]
    public void ปัดเศษ_AwayFromZero()
    {
        // 333.33 × 1.5% = 4.99995 → 5.00 (banker's ก็ 5.00 · เคสนี้ล็อกว่าไม่ตัดทิ้ง)
        Assert.Equal(5.00m, S("WhtPnd3", 333.33m, d => d.AddDays(3)).Surcharge);
        // 4,500.30 × 1.5% = 67.5045 → 67.50
        Assert.Equal(67.50m, S("WhtPnd3", 4_500.30m, d => d.AddDays(3)).Surcharge);
    }

    // ═══ ภ.พ.30 เบี้ยปรับลดตามระเบียบ (2/5/10/20%) ═══

    [Theory]
    [InlineData(1, 200)] [InlineData(15, 200)]
    [InlineData(16, 500)] [InlineData(30, 500)]
    [InlineData(31, 1000)] [InlineData(60, 1000)]
    [InlineData(61, 2000)] [InlineData(400, 2000)]
    public void ภพ30_เบี้ยปรับตามจำนวนวันที่ช้า(int daysLate, decimal expected)
    {
        var s = S("VatPp30", 10_000m, d => d.AddDays(daysLate));
        Assert.Equal(expected, s.Penalty);
        Assert.Equal("RD-89", s.PenaltyReference);
        Assert.Equal(daysLate, s.DaysLate);
    }

    [Fact]
    public void ภพ30_ช้า20วัน_เงินเพิ่ม1เดือน_และเบี้ยปรับ5เปอร์เซ็นต์()
    {
        var s = S("VatPp30", 10_000m, d => d.AddDays(20));
        Assert.Equal(150m, s.Surcharge);
        Assert.Equal(500m, s.Penalty);
        Assert.Equal(650m, s.Total);
        Assert.Equal("RD-89/1", s.SurchargeReference);
    }

    [Fact]
    public void ภพ36_สปส_หักณที่จ่าย_ไม่เสนอเบี้ยปรับ_ค่าปรับอาญาไม่เดา()
    {
        Assert.Equal(0m, S("VatPp36", 10_000m, d => d.AddDays(20)).Penalty);
        Assert.Equal(0m, S("SsoSps110", 10_000m, d => d.AddDays(20)).Penalty);
        var wht = S("WhtPnd53", 10_000m, d => d.AddDays(20));
        Assert.Equal(0m, wht.Penalty);
        Assert.Contains("2,000", wht.FineNote);     // ม.35 — ให้คนกรอก
    }

    // ═══ ตัวตั้งเดิมต้องตรงกัน (ห้ามสูตรสองชุด) ═══

    [Theory]
    [InlineData(2026, 2, 2026, 3, 16)] [InlineData(2026, 2, 2026, 4, 15)] [InlineData(2026, 2, 2026, 4, 16)]
    [InlineData(2025, 12, 2026, 6, 1)] [InlineData(2026, 1, 2026, 2, 10)]
    public void สปส_ตรงกับ_SsoLateFee_ทุกสตางค์(int py, int pm, int y, int m, int d)
    {
        var pay = new DateTime(y, m, d);
        var expected = PayrollService.ComputeSsoLateFee(py, pm, pay, 12_345.67m);
        var got = LatePaymentPenaltyPolicy.Suggest("SsoSps110", py, pm, 12_345.67m, pay, null);
        Assert.Equal(expected, got.Surcharge);
        Assert.Equal(SsoLateFee.DueDate(py, pm), got.DueDate);
    }

    [Fact]
    public void ภพ36_ตรงกับ_Pp36Lifecycle_SuggestedSurcharge()
    {
        var due = TaxFilingDeadline.WarnBy("VatPp36", 2026, 3, null);
        var pay = due.AddDays(45);
        Assert.Equal(Pp36Lifecycle.SuggestedSurcharge(413.56m, due, pay),
            LatePaymentPenaltyPolicy.Suggest("VatPp36", 2026, 3, 413.56m, pay, null).Surcharge);
    }

    // ═══ ค่าที่ผู้ใช้แก้ชนะ · ไม่ส่ง = พฤติกรรมเดิม ═══

    [Fact]
    public void ค่าที่ผู้ใช้แก้_ชนะค่าเสนอทุกก้อน()
    {
        var sug = S("VatPp30", 10_000m, d => d.AddDays(20));
        var a = LatePaymentPenaltyPolicy.Resolve("VatPp30", sug, true, 123.45m, 99m, 200m);
        Assert.Equal((123.45m, 99m, 200m), (a.Surcharge, a.Penalty, a.OtherFine));
        Assert.Equal(422.45m, a.Total);
    }

    [Fact]
    public void ไม่ส่งเงินเพิ่ม_สปสและภพ36ใช้ค่าเสนอ_แบบอื่นเป็นศูนย์()
    {
        var sso = S("SsoSps110", 10_000m, d => d.AddDays(3));
        Assert.Equal(sso.Surcharge, LatePaymentPenaltyPolicy.Resolve("SsoSps110", sso, true, null, null, null).Surcharge);
        Assert.True(sso.Surcharge > 0);
        var pp36 = S("VatPp36", 10_000m, d => d.AddDays(3));
        Assert.Equal(pp36.Surcharge, LatePaymentPenaltyPolicy.Resolve("VatPp36", pp36, true, null, null, null).Surcharge);
        // ภ.พ.30/ภ.ง.ด. เดิมลง 0 — API ที่ไม่ส่งช่องใหม่ต้องไม่ถูกประทับค่าใช้จ่ายที่ไม่เห็น
        var pp30 = S("VatPp30", 10_000m, d => d.AddDays(3));
        var a = LatePaymentPenaltyPolicy.Resolve("VatPp30", pp30, true, null, null, null);
        Assert.Equal(0m, a.Total);
    }

    [Fact]
    public void ปิดเงินเพิ่ม_IncludeLateFee_false_เป็นศูนย์แม้ส่งค่ามา()
    {
        var sso = S("SsoSps110", 10_000m, d => d.AddDays(3));
        var a = LatePaymentPenaltyPolicy.Resolve("SsoSps110", sso, false, 50m, 10m, null);
        Assert.Equal(0m, a.Surcharge);
        Assert.Equal(10m, a.Penalty);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ยอดติดลบ_ปฏิเสธ_ไม่ตัดเป็นศูนย์เงียบ(int which)
    {
        var sug = S("WhtPnd3", 1_000m, d => d.AddDays(3));
        var ex = Assert.Throws<BusinessRuleException>(() => LatePaymentPenaltyPolicy.Resolve("WhtPnd3", sug, true,
            which == 0 ? -1m : null, which == 1 ? -0.01m : null, which == 2 ? -5m : null));
        Assert.Equal(LatePaymentPenaltyPolicy.NegativeRuleCode, ex.RuleCode);
    }

    [Fact]
    public void ยอดที่ผู้ใช้กรอก_ปัดสองตำแหน่ง_AwayFromZero()
    {
        var sug = S("WhtPnd3", 1_000m, d => d);
        var a = LatePaymentPenaltyPolicy.Resolve("WhtPnd3", sug, true, 0.125m, 2.005m, 0.0049m);
        Assert.Equal((0.13m, 2.01m, 0m), (a.Surcharge, a.Penalty, a.OtherFine));
    }

    // ═══ บรรทัด JE ═══

    [Fact]
    public void บรรทัดJE_แยกสามก้อน_รวมเท่ายอดรวม_ก้อนศูนย์ไม่ออกบรรทัด()
    {
        var all = LatePaymentPenaltyPolicy.JournalLines("ภ.พ.30", new LatePaymentPenaltyPolicy.Applied(150m, 500m, 200m), "RD-89/1").ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(850m, all.Sum(x => x.Amount));
        Assert.All(all, x => Assert.Contains("§65 ตรี(6)", x.Description));
        Assert.Contains("เบี้ยปรับ", all[1].Description);
        Assert.Contains("ค่าปรับ", all[2].Description);

        Assert.Single(LatePaymentPenaltyPolicy.JournalLines("ภ.ง.ด.3", new LatePaymentPenaltyPolicy.Applied(15m, 0m, 0m), "RD-27"));
        Assert.Empty(LatePaymentPenaltyPolicy.JournalLines("ภ.ง.ด.3", new LatePaymentPenaltyPolicy.Applied(0m, 0m, 0m), null));
    }

    // ═══ บวกกลับ §65 ตรี(6) — ไม่นับซ้ำ ═══

    [Fact]
    public void บวกกลับ_แถวนำส่งรวมทุกก้อน_เงินเพิ่มหน้าเงินเดือนนับเฉพาะJEที่ไม่ใช่ของแถวนำส่ง()
    {
        var jeHub = Guid.NewGuid();
        var jePayroll = Guid.NewGuid();
        var total = LatePaymentPenaltyPolicy.AddBackTotal(
            new (Guid?, decimal)[] { (jeHub, 850m), (Guid.NewGuid(), 0m) },
            new (Guid?, decimal)[]
            {
                (jeHub, 200m), (jeHub, 200m),        // หน้านำส่งรวมประทับลงทุกรอบของเดือน — นับแล้วในแถวนำส่ง
                (jePayroll, 120m),                   // นำส่งจากหน้าเงินเดือน (ไม่มีแถวนำส่ง)
                (null, 50m),                         // ไม่มี JE = ไม่ได้ลงบัญชี
            });
        Assert.Equal(970m, total);
    }

    [Fact]
    public void บวกกลับ_ไม่มีรายการ_เป็นศูนย์()
        => Assert.Equal(0m, LatePaymentPenaltyPolicy.AddBackTotal(
            Array.Empty<(Guid?, decimal)>(), Array.Empty<(Guid?, decimal)>()));

    // ═══ ผังค่าปรับ ═══

    [Fact]
    public void ผังค่าปรับ_ค่าแนะนำ54820_ค่าตั้งชนะ()
    {
        Assert.Equal("54820", PenaltyExpenseAccount.EffectiveCode(null));
        Assert.Equal("54820", PenaltyExpenseAccount.EffectiveCode("  "));
        Assert.Equal("54830", PenaltyExpenseAccount.EffectiveCode(" 54830 "));
    }
}
