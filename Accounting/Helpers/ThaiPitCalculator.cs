namespace Accounting.Helpers;

/// <summary>ขั้นภาษีเงินได้บุคคลธรรมดา — <c>UpperBound</c> คือขอบบนของขั้น
/// (ขั้นสุดท้ายใช้ <see cref="decimal.MaxValue"/>)</summary>
public readonly record struct PitBracket(decimal UpperBound, decimal Rate);

/// <summary>ค่าลดหย่อน §47/§47ทวิ ที่ใช้กับพนักงานคนหนึ่งในปีภาษีหนึ่ง (ยอดรายปี)</summary>
public readonly record struct PitAllowances(
    decimal Personal,
    decimal Spouse,
    decimal Children,
    decimal Parents,
    decimal LifeInsurance,
    decimal ProvidentFund,
    decimal SocialSecurity)
{
    /// <summary>รวมทุกช่องที่หักได้ "ก่อน" เงินบริจาค (บริจาคคิดเป็น % ของยอดหลังนี้)</summary>
    public decimal Total => Personal + Spouse + Children + Parents
                            + LifeInsurance + ProvidentFund + SocialSecurity;
}

/// <summary>ผลการคำนวณของงวดหนึ่ง — แยกทุกขั้นเพื่อให้ตรวจสอบย้อนกลับได้</summary>
public readonly record struct PitResult(
    decimal EstimatedAnnualIncome,
    decimal ExpenseDeduction,
    decimal TotalAllowances,
    decimal DonationDeduction,
    decimal TaxableIncome,
    decimal EstimatedAnnualTax,
    decimal WithholdingThisPeriod);

/// <summary>
/// **ภาษีเงินได้บุคคลธรรมดา (หัก ณ ที่จ่ายจากเงินเดือน) — ฟังก์ชันบริสุทธิ์**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ D-T1 … D-T4) ═══
/// เครื่องคิดภาษีเดิมฝังอยู่กลาง <c>PayrollService.CalculatePayrollAsync</c>
/// ยาว ~470 บรรทัด <b>ไม่มีเทสต์เลยสักตัว</b> และผิด 4 ทางพร้อมกัน:
/// <list type="number">
/// <item><b>ไม่หักค่าใช้จ่าย §42ทวิ</b> (50% ของเงินได้ ไม่เกิน 100,000) —
///   <c>Section42TwiCap</c> มีอยู่ในตารางตั้งค่ามาตลอดแต่ <b>ไม่มีใครอ่าน</b>
///   ⇒ เงินเดือน 50,000 หักภาษี 31,925/ปี ทั้งที่ควรเป็น 20,450
///   (เกินไปเดือนละ ~956 บาท ต่อพนักงาน 1 คน)</item>
/// <item><b>ค่าลดหย่อน 8 ช่องไม่มีจุดเขียน</b> — ทุกคนได้แค่ 60,000 ⇒ พนักงาน
///   ที่มีคู่สมรส + บุตร 2 คน เงินเดือน 50,000 ควรเสีย 6,475 แต่ถูกหัก 31,925
///   (4.9 เท่า)</item>
/// <item><b>โบนัสถูกคูณเป็นรายปี</b> — สูตรเดิม <c>ytd × 12 ÷ เดือน</c> ทำให้
///   โบนัสก้อนเดียวถูกอ่านว่า "ได้ทุกเดือน" ⇒ เงินเดือน 50,000 + โบนัส 300,000
///   ในเดือน 6 ถูกหักรวม 82,221 ทั้งที่ควรเป็น 61,925 (ม.50(1) เงินได้ครั้งคราว
///   รวมครั้งเดียว ไม่ประมาณการซ้ำ)</item>
/// <item><b>คนเข้ากลางปีถูกหักพุ่งเดือนสุดท้าย</b> — สูตรเดียวกันประมาณการจาก
///   เดือนที่ผ่านมาแทนงวดที่เหลือจริง ⇒ เข้า 1 ก.ค. เงินเดือน 150,000
///   ถูกหักเดือนสุดท้าย 36,759 (25% ของเงินเดือน)</item>
/// </list>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>ประมาณการจากงวดที่เหลือ ไม่ใช่จากเดือนที่ผ่านมา</b> —
///   <c>เงินได้ทั้งปี = สะสมถึงงวดนี้ + ฐานประจำ × งวดที่เหลือ</c>
///   ⇒ เงินได้ครั้งคราว (โบนัส/ค่าคอมพิเศษ) ที่อยู่ในยอดสะสมแล้ว
///   <b>ไม่ถูกฉายไปข้างหน้าอีก</b> และคนเข้ากลางปีได้จำนวนงวดที่ถูกต้องเอง
///   — แก้ทั้ง (3) และ (4) ด้วยสูตรเดียว</item>
/// <item><b>คืนภาษีที่หักเกินได้ แต่ไม่เกินที่หักไปแล้วในปีนี้</b> — เดิม
///   <c>Math.Max(0, …)</c> ทำให้เดือนที่มีโบนัสหักเกินแล้วคืนไม่ได้เลยตลอดปี
///   (ลูกจ้างออกเงินให้บริษัทไปก่อนจนกว่าจะยื่นแบบเอง)</item>
/// <item>ปัดเศษด้วย <c>MidpointRounding.AwayFromZero</c> ทุกจุด</item>
/// </list>
/// </summary>
public static class ThaiPitCalculator
{
    /// <summary>ขั้นภาษีตามประมวลรัษฎากร §48(1) (ใช้ตั้งแต่ปีภาษี 2560)</summary>
    public static readonly PitBracket[] DefaultBrackets =
    {
        new(150_000m, 0.00m),
        new(300_000m, 0.05m),
        new(500_000m, 0.10m),
        new(750_000m, 0.15m),
        new(1_000_000m, 0.20m),
        new(2_000_000m, 0.25m),
        new(5_000_000m, 0.30m),
        new(decimal.MaxValue, 0.35m),
    };

    /// <summary>§42ทวิ — หักค่าใช้จ่ายเงินได้ ม.40(1)(2) ได้ 50% แต่ไม่เกิน 100,000</summary>
    public const decimal DefaultExpenseRatePercent = 50m;
    public const decimal DefaultExpenseCap = 100_000m;

    // ══════════════════════════════════════════════════════════════════════
    //  ค่าลดหย่อน §47 + เพดาน §47(7)/§47ทวิ — **ตัวตั้งตัวเดียวของระบบ**
    // ══════════════════════════════════════════════════════════════════════
    //
    // ═══ ที่มา (งานค้าง "ลดหย่อน 3 สำเนา" · ทีม E ข้อ 3) ═══ ตัวเลขชุดเดียวกัน
    // ถูกพิมพ์ไว้ **3 ที่** และแต่ละที่แก้ได้อิสระ:
    //   1. `Models/Entities/Payroll.cs` — property initializer ของ `TaxRuleConfig`
    //   2. `Controllers/PayrollController.cs` — `ov?.X ?? 60_000m` ตอนสร้างตาราง
    //      ให้หน้าตั้งค่า (บริษัทที่ยังไม่เคย override เห็นค่าจากที่นี่)
    //   3. `Services/Implementations/PayrollService.cs` — `PitPersonalAllowance`
    //      ฯลฯ ที่ใช้ตอน "ไม่มี TaxRuleConfig ของปีนั้น"
    // ⇒ สรรพากรปรับค่าลดหย่อนเมื่อไร ต้องแก้ครบสามที่ถึงจะตรง; แก้ไม่ครบ =
    //    หน้าตั้งค่าโชว์เลขหนึ่ง เครื่องคิดภาษีใช้อีกเลขหนึ่ง โดยไม่มีอะไรฟ้อง
    //
    // ที่นี่คือเจ้าของตัวเลข — ผู้เรียกทุกที่ต้องอ้างค่าคงที่เหล่านี้
    // (ห้ามพิมพ์ literal ซ้ำ · หลักการ 10 ข้อ #4 "ตัวตั้งตัวเดียว")

    /// <summary>§47(1)(ก) ค่าลดหย่อนส่วนตัว</summary>
    public const decimal DefaultPersonalAllowance = 60_000m;

    /// <summary>§47(1)(ข) คู่สมรสที่ไม่มีเงินได้</summary>
    public const decimal DefaultSpouseAllowance = 60_000m;

    /// <summary>§47(1)(ค) บุตรคนละ</summary>
    public const decimal DefaultChildAllowance = 30_000m;

    /// <summary>§47(1)(ค) วรรคสอง — บุตรคนที่ 2 ขึ้นไปที่เกิดตั้งแต่ปี 2561</summary>
    public const decimal DefaultChildAllowancePost2561 = 60_000m;

    /// <summary>§47(1)(ง) บิดามารดาคนละ (อายุ 60+ · รายได้ไม่เกิน 30,000/ปี)</summary>
    public const decimal DefaultParentAllowance = 30_000m;

    /// <summary>เพดานเบี้ยประกันชีวิต §47(1)(ง)</summary>
    public const decimal DefaultLifeInsuranceCap = 100_000m;

    /// <summary>เพดานเบี้ยประกันสุขภาพ (รวมกับประกันชีวิตแล้วไม่เกิน 100,000)</summary>
    public const decimal DefaultHealthInsuranceCap = 25_000m;

    /// <summary>เพดานรวม PVD + RMF + SSF + กบข.</summary>
    public const decimal DefaultPvdCap = 500_000m;

    /// <summary>เพดานดอกเบี้ยเงินกู้ที่อยู่อาศัย</summary>
    public const decimal DefaultMortgageInterestCap = 100_000m;

    /// <summary>เพดานเงินบริจาคทั่วไป — % ของเงินได้หลังหักค่าลดหย่อน</summary>
    public const decimal DefaultDonationCapPercent = 10m;

    /// <summary>ขั้นภาษี §48(1) ในรูป JSON ที่ <c>TaxRuleConfig.BracketsJson</c> ใช้
    /// — <b>สร้างจาก <see cref="DefaultBrackets"/> โดยตรง</b> ไม่ใช่สตริงที่พิมพ์มือ
    ///
    /// <para>ขั้นสุดท้ายใช้ <c>upperBound = 0</c> ตามที่ตัวอ่านฝั่ง
    /// <c>PayrollService</c> คาดไว้ (0 = catch-all — <c>decimal.MaxValue</c>
    /// เขียนลง JSON ไม่ได้)</para></summary>
    public static string DefaultBracketsJson()
        => "[" + string.Join(",", DefaultBrackets.Select(b =>
            {
                var upper = b.UpperBound == decimal.MaxValue ? 0m : b.UpperBound;
                return $"{{\"upperBound\":{upper:0.##},\"rate\":{b.Rate:0.####}}}";
            })) + "]";

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// เงินได้ทั้งปีโดยประมาณ = <b>สะสมถึงงวดนี้</b> + <b>ฐานประจำ × งวดที่เหลือ</b>
    /// </summary>
    /// <param name="taxableIncomeYtd">เงินได้ที่ต้องเสียภาษีสะสม <b>รวมงวดนี้แล้ว</b>
    /// (รวมโบนัส/ครั้งคราวที่จ่ายไปแล้วด้วย)</param>
    /// <param name="recurringMonthlyIncome">ฐานประจำต่องวดที่คาดว่าจะได้ต่อไป
    /// (เงินเดือน + เบี้ยเลี้ยงประจำ — <b>ไม่รวม</b>โบนัส/ครั้งคราว)</param>
    /// <param name="remainingPeriodsAfterThis">จำนวนงวดที่เหลือ <b>หลังงวดนี้</b>
    /// — คนเข้ากลางปี/ลาออกกลางปีส่งค่าที่เหลือจริง ไม่ใช่ <c>12 − เดือน</c></param>
    public static decimal EstimateAnnualIncome(
        decimal taxableIncomeYtd, decimal recurringMonthlyIncome, int remainingPeriodsAfterThis)
        => R(taxableIncomeYtd + (recurringMonthlyIncome * Math.Max(0, remainingPeriodsAfterThis)));

    /// <summary>§42ทวิ — ค่าใช้จ่ายที่หักได้จากเงินได้ ม.40(1)(2)</summary>
    public static decimal ExpenseDeduction(
        decimal annualIncome,
        decimal ratePercent = DefaultExpenseRatePercent,
        decimal? cap = null)
    {
        if (annualIncome <= 0) return 0m;
        var byRate = annualIncome * (ratePercent / 100m);
        return R(Math.Min(byRate, cap ?? DefaultExpenseCap));
    }

    /// <summary>ภาษีทั้งปีจากเงินได้สุทธิ — เดินขั้นแบบ progressive</summary>
    public static decimal AnnualTax(decimal taxableIncome, IReadOnlyList<PitBracket>? brackets = null)
    {
        if (taxableIncome <= 0) return 0m;
        var b = brackets ?? DefaultBrackets;
        decimal tax = 0m, previous = 0m;
        foreach (var (upper, rate) in b)
        {
            if (taxableIncome <= previous) break;
            var inBracket = Math.Min(taxableIncome, upper) - previous;
            if (inBracket > 0) tax += inBracket * rate;
            previous = upper;
        }
        return R(tax);
    }

    /// <summary>
    /// ภาษีที่ต้องหักในงวดนี้ = (ภาษีทั้งปี − ที่หักไปแล้ว) ÷ งวดที่เหลือ <b>รวมงวดนี้</b>
    ///
    /// <para>ค่าติดลบ = คืนภาษีที่หักเกิน (เกิดหลังเดือนที่มีโบนัส) — คืนได้ไม่เกิน
    /// ยอดที่หักไปแล้วในปีนี้ เพื่อให้ยอดสะสมไม่ติดลบ</para>
    /// </summary>
    public static decimal WithholdingForPeriod(
        decimal estimatedAnnualTax, decimal taxWithheldYtdBeforeThisPeriod, int periodsRemainingIncludingThis)
    {
        if (periodsRemainingIncludingThis <= 0) return 0m;
        var raw = R((estimatedAnnualTax - taxWithheldYtdBeforeThisPeriod) / periodsRemainingIncludingThis);
        return Math.Max(raw, -taxWithheldYtdBeforeThisPeriod);
    }

    /// <summary>คำนวณครบวงจรของงวดหนึ่ง — จุดเรียกเดียวที่ payroll ควรใช้</summary>
    /// <param name="donationAmount">เงินบริจาค — หักได้ไม่เกิน
    /// <paramref name="donationCapPercent"/>% ของเงินได้หลังหักค่าใช้จ่าย+ลดหย่อน</param>
    public static PitResult Compute(
        decimal taxableIncomeYtd,
        decimal recurringMonthlyIncome,
        int remainingPeriodsAfterThis,
        PitAllowances allowances,
        decimal taxWithheldYtdBeforeThisPeriod,
        decimal donationAmount = 0m,
        decimal donationCapPercent = 10m,
        decimal expenseRatePercent = DefaultExpenseRatePercent,
        decimal? expenseCap = null,
        IReadOnlyList<PitBracket>? brackets = null)
    {
        var annualIncome = EstimateAnnualIncome(
            taxableIncomeYtd, recurringMonthlyIncome, remainingPeriodsAfterThis);
        var expense = ExpenseDeduction(annualIncome, expenseRatePercent, expenseCap);
        var allowTotal = allowances.Total;

        var afterBase = Math.Max(0m, annualIncome - expense - allowTotal);
        var donation = R(Math.Min(Math.Max(0m, donationAmount), afterBase * (donationCapPercent / 100m)));

        var taxable = Math.Max(0m, afterBase - donation);
        var annualTax = AnnualTax(taxable, brackets);
        var withholding = WithholdingForPeriod(
            annualTax, taxWithheldYtdBeforeThisPeriod, remainingPeriodsAfterThis + 1);

        return new PitResult(
            EstimatedAnnualIncome: annualIncome,
            ExpenseDeduction: expense,
            TotalAllowances: allowTotal,
            DonationDeduction: donation,
            TaxableIncome: taxable,
            EstimatedAnnualTax: annualTax,
            WithholdingThisPeriod: withholding);
    }
}
