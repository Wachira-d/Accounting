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
    /// <summary>ค่าเสนอเมื่อจ่ายห่างจากวันครบกำหนด — ค่าตั้งต้น "ยื่นแบบกระดาษ" (วันครบกำหนด = วันเริ่มนับ ⇒ ตัวเลขในเทสต์อ่านตรง ๆ) ·
    /// เคสสิทธิ์ขยาย e-Filing อยู่ในหมวดของมันเอง</summary>
    private static LatePaymentPenaltyPolicy.Suggestion S(string type, decimal tax, Func<DateTime, DateTime> payFromDue, int y = 2026, int m = 3,
        bool paper = true, DateTime? filedAt = null, IReadOnlySet<DateTime>? holidays = null)
    {
        var due = LatePaymentPenaltyPolicy.DueDate(type, y, m, holidays, paper);
        return LatePaymentPenaltyPolicy.Suggest(type, y, m, tax, payFromDue(due), holidays, paper, filedAt);
    }

    // ═══ ทันกำหนด ⇒ 0 (ทิศที่ห้ามแตะ) ═══

    [Theory]
    [InlineData("WhtPnd1")] [InlineData("WhtPnd2")] [InlineData("WhtPnd3")] [InlineData("WhtPnd53")] [InlineData("WhtPnd54")]
    [InlineData("VatPp30")] [InlineData("VatPp36")] [InlineData("SsoSps110")]
    public void จ่ายตรงวันครบกำหนด_ไม่มีเงินเพิ่มและเบี้ยปรับ(string type)
    {
        Assert.Equal(0m, S(type, 10_000m, d => d, paper: false).Total);   // ยื่นออนไลน์ — ตรงวันขยาย
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
    public void สปส_หักณที่จ่าย_ไม่เสนอเบี้ยปรับ_ค่าปรับอาญาไม่เดา()
    {
        // รอบ 228 ทีมภาษี: ภ.พ.36 เสนอเบี้ยปรับตารางลด (ต้องยืนยัน) — ล็อกที่ Round228TaxRemittanceTests · ที่นี่เหลือแบบที่ไม่มีเบี้ยปรับตามแบบ
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

    // ═══ ฝ่ายค้านข้อ 1: สิทธิ์ขยาย e-Filing ═══

    [Fact]
    public void ยื่นออนไลน์_จ่ายภายในวันขยาย_ไม่มีเงินเพิ่ม_แม้เลยวันกระดาษแล้ว()
    {
        // ภ.ง.ด.3 งวด มี.ค. 2569: กระดาษ 7 เม.ย. · e-Filing 15 เม.ย.
        var s = LatePaymentPenaltyPolicy.Suggest("WhtPnd3", 2026, 3, 10_000m, new DateTime(2026, 4, 15), null);
        Assert.Equal(0m, s.Total);
        Assert.Equal(new DateTime(2026, 4, 15), s.DueDate);
    }

    [Fact]
    public void พ้นวันขยาย_นับเดือนจากวันกระดาษ_ไม่ใช่วันขยาย()
    {
        // จ่าย 8 พ.ค. — นับจาก 7 เม.ย. = 2 เดือน (นับจาก 15 เม.ย. จะได้ 1 เดือน)
        var s = LatePaymentPenaltyPolicy.Suggest("WhtPnd3", 2026, 3, 10_000m, new DateTime(2026, 5, 8), null);
        Assert.Equal(2, s.MonthsLate);
        Assert.Equal(300m, s.Surcharge);
        Assert.Contains("พ้นวันขยาย e-Filing", s.SurchargeNote);
        // พ้นวันขยายวันเดียว — ยังเป็น 1 เดือน (7 เม.ย. → 16 เม.ย.)
        Assert.Equal(150m, LatePaymentPenaltyPolicy.Suggest("WhtPnd3", 2026, 3, 10_000m, new DateTime(2026, 4, 16), null).Surcharge);
    }

    [Fact]
    public void ยื่นกระดาษ_เลยวันกระดาษวันเดียวก็มีเงินเพิ่ม_ยื่นออนไลน์วันเดียวกันไม่มี()
    {
        var pay = new DateTime(2026, 4, 8);
        Assert.Equal(150m, LatePaymentPenaltyPolicy.Suggest("WhtPnd53", 2026, 3, 10_000m, pay, null, paperFiling: true).Surcharge);
        Assert.Equal(0m, LatePaymentPenaltyPolicy.Suggest("WhtPnd53", 2026, 3, 10_000m, pay, null, paperFiling: false).Surcharge);
    }

    [Fact]
    public void ภพ30_พ้นวันขยาย_เบี้ยปรับนับวันจากวันกระดาษ()
    {
        // ภ.พ.30 มี.ค. 2569: กระดาษ 15 เม.ย. · e-Filing 23 เม.ย. · ยื่น/จ่าย 24 เม.ย. = ช้า 9 วันจากวันกระดาษ ⇒ 2%
        var s = LatePaymentPenaltyPolicy.Suggest("VatPp30", 2026, 3, 10_000m, new DateTime(2026, 4, 24), null);
        Assert.Equal(9, s.DaysLate);
        Assert.Equal(200m, s.Penalty);
        Assert.Equal(150m, s.Surcharge);
        // ช้า 17 วันจากวันกระดาษ (2 พ.ค.) ⇒ 5% (นับจากวันขยายจะได้ 9 วัน = 2%)
        Assert.Equal(500m, LatePaymentPenaltyPolicy.Suggest("VatPp30", 2026, 3, 10_000m, new DateTime(2026, 5, 2), null).Penalty);
    }

    // ═══ ฝ่ายค้านข้อ 7: ยื่นแบบทันแล้ว จ่ายช้า ═══

    [Fact]
    public void ภพ30_ยื่นแบบทันกำหนดแล้ว_จ่ายช้า_มีแต่เงินเพิ่ม_ไม่มีเบี้ยปรับ()
    {
        var s = LatePaymentPenaltyPolicy.Suggest("VatPp30", 2026, 3, 10_000m, new DateTime(2026, 5, 20), null,
            filedAt: new DateTime(2026, 4, 20));
        Assert.True(s.Surcharge > 0);
        Assert.Equal(0m, s.Penalty);
        Assert.Contains("ยื่นแบบทันกำหนด", s.PenaltyNote);
    }

    [Fact]
    public void ภพ30_ยื่นแบบช้า_เบี้ยปรับคิดจากวันยื่น_ไม่ใช่วันจ่าย()
    {
        // ยื่น 5 พ.ค. (ช้า 20 วันจาก 15 เม.ย. ⇒ 5%) แต่จ่าย 24 มิ.ย. (70 วัน — ถ้านับวันจ่ายจะได้ 20%)
        var s = LatePaymentPenaltyPolicy.Suggest("VatPp30", 2026, 3, 10_000m, new DateTime(2026, 6, 24), null,
            filedAt: new DateTime(2026, 5, 5));
        Assert.Equal(500m, s.Penalty);
    }

    // ═══ ฝ่ายค้านข้อ 2: สปส. เลื่อนวันหยุดราชการ ═══

    private static readonly IReadOnlySet<DateTime> Songkran2026 = new HashSet<DateTime>
        { new(2026, 4, 13), new(2026, 4, 14), new(2026, 4, 15) };

    [Fact]
    public void สปส_สงกรานต์_ครบกำหนดเลื่อนไป16เมย_จ่าย16ไม่มีเงินเพิ่ม()
    {
        var pay = new DateTime(2026, 4, 16);
        var withHol = LatePaymentPenaltyPolicy.Suggest("SsoSps110", 2026, 3, 10_000m, pay, Songkran2026);
        Assert.Equal(new DateTime(2026, 4, 16), withHol.DueDate);
        Assert.Equal(0m, withHol.Surcharge);
        // ป้ายบนหน้าเงินเดือน = ตัวเดียวกัน
        Assert.Equal(withHol.DueDate, SsoLateFee.DueDate(2026, 3, Songkran2026));
        Assert.Equal(0m, SsoLateFee.Compute(2026, 3, pay, 10_000m, Songkran2026).Fee);
        // ไม่ส่งวันหยุด = พฤติกรรมเดิม (15 เม.ย. เป็นวันพุธ ⇒ 16 = ช้า)
        Assert.Equal(200m, LatePaymentPenaltyPolicy.Suggest("SsoSps110", 2026, 3, 10_000m, pay, null).Surcharge);
        Assert.Equal(200m, PayrollService.ComputeSsoLateFee(2026, 3, pay, 10_000m));
    }

    [Fact]
    public void สปส_สงกรานต์_จ่าย17เมย_ช้า1เดือน()
        => Assert.Equal(200m, LatePaymentPenaltyPolicy.Suggest("SsoSps110", 2026, 3, 10_000m, new DateTime(2026, 4, 17), Songkran2026).Surcharge);

    // ═══ ฝ่ายค้านข้อ 3: เครื่องหมาย JE ของเงินเพิ่ม (ตัวบวกกลับอ่านบรรทัดจริงของ JE เหล่านี้) ═══

    [Fact]
    public void เครื่องหมายJE_เติมท้ายไม่ซ้ำ_อ่านข้ามค่าขยะ()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var s1 = LatePaymentPenaltyPolicy.AppendJournalId(null, a);
        var s2 = LatePaymentPenaltyPolicy.AppendJournalId(s1, b);
        var s3 = LatePaymentPenaltyPolicy.AppendJournalId(s2, a);
        Assert.Equal(new[] { a, b }, LatePaymentPenaltyPolicy.ParseJournalIds(s3));
        Assert.Equal(new[] { a }, LatePaymentPenaltyPolicy.ParseJournalIds($" {a} ,xx,,"));
        Assert.Empty(LatePaymentPenaltyPolicy.ParseJournalIds(null));
    }

    // ═══ ผังค่าปรับ ═══

    [Fact]
    public void ผังค่าปรับ_สร้าง54820ได้เฉพาะใต้หัวหมวด548ที่เป็นค่าใช้จ่ายเปิดใช้()
    {
        // ได้: 548 ค่าใช้จ่าย เปิดใช้ มีผังย่อย / ไม่มีผังย่อยแต่ไม่เคยมีรายการ
        Assert.Null(PenaltyExpenseAccount.CreateRefusal(false, false, true, Accounting.Models.Enums.AccountType.Expense, true, true, true));
        Assert.Null(PenaltyExpenseAccount.CreateRefusal(false, false, true, Accounting.Models.Enums.AccountType.Expense, true, false, false));
        // ไม่ได้: ไม่มีหมวด · ไม่ใช่ค่าใช้จ่าย · ปิดใช้ · เป็นผังที่มีรายการลงอยู่ (ไม่ใช่หัวหมวด)
        Assert.Contains("ไม่พบหมวด", PenaltyExpenseAccount.CreateRefusal(false, false, false, Accounting.Models.Enums.AccountType.Expense, false, false, false));
        Assert.NotNull(PenaltyExpenseAccount.CreateRefusal(false, false, true, Accounting.Models.Enums.AccountType.Asset, true, true, false));
        Assert.NotNull(PenaltyExpenseAccount.CreateRefusal(false, false, true, Accounting.Models.Enums.AccountType.Expense, false, true, false));
        Assert.Contains("มีรายการบัญชีลงอยู่แล้ว", PenaltyExpenseAccount.CreateRefusal(false, false, true, Accounting.Models.Enums.AccountType.Expense, true, false, true));
        // 54820 มีอยู่แล้วแต่ใช้ไม่ได้ — ข้อความแยก "เป็นบัญชีคุม" กับ "ปิดใช้/ลบ"
        Assert.Contains("บัญชีคุม", PenaltyExpenseAccount.CreateRefusal(true, true, true, Accounting.Models.Enums.AccountType.Expense, true, true, false));
        Assert.Contains("ปิดใช้หรือลบ", PenaltyExpenseAccount.CreateRefusal(true, false, true, Accounting.Models.Enums.AccountType.Expense, true, true, false));
        Assert.All(new[]
        {
            PenaltyExpenseAccount.CreateRefusal(false, false, false, Accounting.Models.Enums.AccountType.Expense, false, false, false),
            PenaltyExpenseAccount.CreateRefusal(true, true, true, Accounting.Models.Enums.AccountType.Expense, true, true, false),
        }, m => Assert.Contains("ตั้งค่า › ภาษี", m));
    }

    [Fact]
    public void ผังค่าปรับ_ค่าแนะนำ54820_ค่าตั้งชนะ()
    {
        Assert.Equal("54820", PenaltyExpenseAccount.EffectiveCode(null));
        Assert.Equal("54820", PenaltyExpenseAccount.EffectiveCode("  "));
        Assert.Equal("54830", PenaltyExpenseAccount.EffectiveCode(" 54830 "));
    }

    /// <summary>รอบ 252 WP-13 (AUDIT_249 §3.3 N5): เบี้ยปรับ ภ.พ.30 ที่เสนอจากตารางลด (2/5/10/20%) ต้องบอกว่า "ยังไม่ยืนยันเลขระเบียบ" ในข้อความที่หน้าแสดง ·
    /// ทิศตรงข้าม: ชำระทันกำหนด (ไม่มีเบี้ยปรับเสนอ) ไม่ติดป้ายนี้</summary>
    [Fact]
    public void Pp30_reduced_penalty_note_says_regulation_number_not_confirmed()
    {
        var late = S("VatPp30", 10_000m, due => due.AddDays(10));
        Assert.True(late.Penalty > 0);
        Assert.Contains(LatePaymentPenaltyPolicy.PendingRegulationNote, late.PenaltyNote);
        var onTime = S("VatPp30", 10_000m, due => due);
        Assert.DoesNotContain(LatePaymentPenaltyPolicy.PendingRegulationNote, onTime.PenaltyNote);
    }
}
