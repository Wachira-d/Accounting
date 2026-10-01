using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ภาษีซื้อของค่าธรรมเนียมไปทางไหน (ผลของ <see cref="SettlementFeeTax.Compute"/>) — เป็นคีย์แบ่ง "ใบค่าธรรมเนียมต่อกลุ่มภาษี"</summary>
public enum SettlementFeeVatTreatment
{
    /// <summary>ไม่มี VAT บนค่าธรรมเนียม (โหมด None · ไฟล์ระบุ VAT = 0 · ประเภทที่ไม่มี VAT)</summary>
    NoVat = 0,
    /// <summary>ผู้ให้บริการไทยเก็บ VAT 7% — Dr 11630 ภาษีซื้อรอเครดิต จนได้ใบกำกับรายเดือน (report-S1 G2/G3)</summary>
    InputVatPending = 1,
    /// <summary>ผู้ให้บริการต่างประเทศ — ประเมินเอง ภ.พ.36: Dr 11640 / Cr 21912 (§83/6 · ForeignServiceVat)</summary>
    SelfAssessedPp36 = 2,
    /// <summary>บริษัทไม่จด VAT — VAT ที่ถูกเรียกเก็บรวมอยู่ในค่าใช้จ่าย ไม่มีขาภาษีซื้อ (CompanyVatStatus · report-S1 §7 ข้อ 7)</summary>
    VatNotClaimable = 3,
    /// <summary>ผู้ให้บริการต่างประเทศ + บริษัท<b>ไม่จด VAT</b> — §83/6 ให้<b>ผู้จ่าย</b>ประเมิน VAT และนำส่ง ภ.พ.36 ไม่ว่าจะจด VAT หรือไม่
    /// (หน้าที่อยู่ที่ผู้จ่าย · ผู้ไม่จดแค่เคลมภาษีซื้อไม่ได้) ⇒ Cr 21912 เท่า VAT ที่ประเมิน · VAT นั้นเป็น<b>ต้นทุน</b> (ไม่มีขา 11640/11610)
    /// — คำตัดสิน main agent รอบ 198 (review198-A R-A4) · report-S1 §5 ที่เขียนว่า "โรงแรมไม่จด VAT ไม่ต้อง ภ.พ.36" ขัดถ้อยคำ §83/6</summary>
    SelfAssessedPp36NotClaimable = 4,
}

/// <summary>ผลแยกภาษีของค่าธรรมเนียม 1 ก้อน (ยอดบวกเสมอ — เครื่องหมายเป็นเรื่องของผู้เรียก)</summary>
/// <param name="Deducted">ยอดที่ถูกหักจาก wallet จริง (= ยอดในไฟล์ · รวม VAT ไทยถ้ามี)</param>
/// <param name="Expense">ค่าใช้จ่ายที่ลงผังค่าธรรมเนียม — ไม่รวมภาษีซื้อที่เคลมได้ · รวม VAT ที่เคลมไม่ได้ · <b>ไม่รวม</b>ภาษีที่ออกแทน (ดู <paramref name="WhtBorneExpense"/>)</param>
/// <param name="InputVat">ภาษีซื้อที่เคลมได้ (11630 หรือ 11640 ของ ภ.พ.36)</param>
/// <param name="Pp36Payable">หนี้ ภ.พ.36 ที่ประเมินเอง (21912) — มีเฉพาะ <see cref="SettlementFeeVatTreatment.SelfAssessedPp36"/> · <see cref="SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable"/></param>
/// <param name="WhtBase">ฐานหัก ณ ที่จ่าย = ค่าบริการ<b>ก่อน VAT</b> (ท.ป.4/2528) · 0 เมื่อไม่หัก</param>
/// <param name="WhtAmount">ภาษีหัก ณ ที่จ่ายของก้อนนี้ (ยอดบน 50 ทวิ)</param>
/// <param name="WhtCertIncome">เงินได้ที่พิมพ์บน 50 ทวิ — ปกติ = ฐาน · ออกภาษีแทน (W3) = ฐาน + ภาษี</param>
/// <param name="WhtBorneExpense">ภาษีที่เราออกแทน (W3) — เป็นค่าใช้จ่ายเพิ่มจากค่าธรรมเนียม (Dr ผังค่าธรรมเนียม / Cr 21917)</param>
public readonly record struct SettlementFeeTaxResult(
    decimal Deducted,
    decimal Expense,
    decimal InputVat,
    decimal Pp36Payable,
    SettlementFeeVatTreatment VatTreatment,
    SettlementFeeWhtMode WhtMode,
    string? WhtIncomeCode,
    decimal WhtRatePercent,
    decimal WhtBase,
    decimal WhtAmount,
    decimal WhtCertIncome,
    decimal WhtBorneExpense);

/// <summary>
/// **ภาษีของค่าธรรมเนียมแพลตฟอร์ม — ฟังก์ชันบริสุทธิ์ตัวเดียว** (รอบ 198 · report-S1 §2/§4 · แก้ G-3/G-4 ของ report-S2 ในเส้นใหม่)
///
/// <para>═══ สูตร ═══
/// <list type="bullet">
/// <item><b>VAT ไทยรวมในยอด</b>: ก่อน VAT = round(ยอด × 100/107, AwayFromZero) · VAT = ยอด − ก่อน VAT (ผลรวมเท่ายอดจริงเสมอ ไม่มีเศษหลุด)</item>
/// <item><b>ไฟล์ระบุ VAT เอง</b>: เชื่อไฟล์ (ยอดจริงที่แพลตฟอร์มออกใบกำกับ) — ต้องไม่เกินยอดและไม่ติดลบ ไม่งั้นผู้เรียกแจ้งปัญหา</item>
/// <item><b>ฐาน WHT = ก่อน VAT</b> (G-4: เส้นเดิม gross-up จากยอดรวม VAT ⇒ ภาษีเกิน ~7% · 50 ทวิ ผิด) · อัตราจาก <see cref="ThaiWhtRateTable"/>
/// ผู้รับเป็นนิติบุคคล</item>
/// <item><b>ออกภาษีแทน (W3)</b>: ภาษี = round(ฐาน × r/(100−r)) → 3% = ฐาน × 3/97 · ม.70 15% = ฐาน × 15/85 · เงินได้บนหนังสือรับรอง = ฐาน + ภาษี</item>
/// <item><b>ฐาน ภ.พ.36 รวมภาษีที่ออกแทน</b> (คำตัดสินรอบ 200 ข้อ 40 · <see cref="ForeignServiceVat.Pp36Base"/>): ลำดับ = เงินได้รวมภาษีออกแทน → WHT → ภ.พ.36 ·
/// 450 ออกภาษีแทน 15/85 ⇒ ฐาน 529.41 ⇒ ภ.พ.36 37.06 · หักจากเงินที่จ่าย (W2) ฐานเท่าเดิม 450 ⇒ 31.50</item>
/// <item><b>บริษัทไม่จด VAT</b>: ไม่มีขาภาษีซื้อ — VAT ที่ถูกเก็บรวมเป็นค่าใช้จ่าย · ผู้ให้บริการ<b>ต่างประเทศ</b>: ยังต้องประเมิน ภ.พ.36 (§83/6
/// หน้าที่ของผู้จ่าย) แต่ VAT นั้นเป็นต้นทุน (<see cref="SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable"/> · review198-A R-A4) ·
/// ผู้ให้บริการต่างประเทศที่จด e-Service และเก็บ VAT ไทยในยอดแล้ว ⇒ ตั้งช่องทางเป็น ThaiVat7 (ไม่ใช่ ForeignPp36)</item>
/// <item><b>ผู้ให้บริการต่างประเทศไม่หัก WHT ด้วยอัตราในประเทศ</b> — ผู้รับเงินต่างประเทศ = §70 ภ.ง.ด.54 (ไม่ใช่ภ.ง.ด.53) · อัตราจาก
/// <see cref="ForeignWhtRateResolver"/> ตัวเดียว (ผ่าน <see cref="SettlementForeignWht.Decide"/> · ม.70 15% → อนุสัญญาเมื่อมีแถว + CoR · รอบ 200 ทีม W) ·
/// ประเภทเงินได้นอก ม.70/ไม่รู้ ⇒ WHT 0 และ <c>SettlementBatchMath.Plan</c> บล็อกพร้อมทางไปต่อ (<see cref="SettlementForeignWht.PlanIssues"/>) ·
/// โหมดตัวแทนหักแทน (W1) ⇒ 0 + บล็อก</item>
/// </list></para>
/// </summary>
public static class SettlementFeeTax
{
    /// <summary>อัตรา VAT ตามกฎหมาย (ตัวตั้งเดียว <see cref="PartnerVatRate.StatutoryRate"/>)</summary>
    private static decimal VatRate => PartnerVatRate.StatutoryRate;

    /// <summary>คิดภาษีของค่าธรรมเนียม 1 ก้อน</summary>
    /// <param name="deducted">ยอดที่ถูกหักจาก wallet (ค่าบวก — ผู้เรียกใส่ |Amount|)</param>
    /// <param name="explicitVat">VAT ที่ไฟล์ระบุ (รวมอยู่ใน <paramref name="deducted"/>) · null = ไม่ระบุ</param>
    /// <param name="vatMode">โหมด VAT ของช่องทาง</param>
    /// <param name="vatApplicable">ประเภทบรรทัดมี VAT ได้ (<see cref="SettlementLineTypeRule.VatApplicable"/>)</param>
    /// <param name="companyVatRegistered">บริษัทจด VAT (<see cref="CompanyVatStatus"/>)</param>
    /// <param name="whtMode">โหมดหัก ณ ที่จ่ายของช่องทาง</param>
    /// <param name="whtIncomeCode">รหัสประเภทเงินได้ของประเภทบรรทัด · null = ประเภทนี้ไม่หัก</param>
    /// <param name="paymentDate">วันที่จ่าย (วันที่รอบโอน) — ใช้เลือกฉบับอนุสัญญาภาษีซ้อนของผู้ให้บริการต่างประเทศ · null = ไม่รู้ ⇒ อัตรา ม.70</param>
    public static SettlementFeeTaxResult Compute(
        decimal deducted,
        decimal? explicitVat,
        SettlementFeeVatMode vatMode,
        bool vatApplicable,
        bool companyVatRegistered,
        SettlementFeeWhtMode whtMode,
        string? whtIncomeCode,
        DateTime? paymentDate = null)
    {
        deducted = Math.Abs(deducted);

        // ── VAT: แยกก่อน VAT ออกจากยอดที่ถูกหัก ──
        decimal preVat, chargedVat, pp36 = 0m;
        SettlementFeeVatTreatment treatment;
        if (!vatApplicable || vatMode == SettlementFeeVatMode.None)
        {
            preVat = deducted; chargedVat = 0m; treatment = SettlementFeeVatTreatment.NoVat;
        }
        else if (vatMode == SettlementFeeVatMode.ForeignPp36)
        {
            // ผู้ให้บริการต่างประเทศไม่เก็บ VAT ไทย ⇒ ยอดที่หัก = ฐาน · เราประเมินเอง 7% — ยอด ภ.พ.36 คิดหลังขั้น WHT
            // (คำตัดสินข้อ 40: ฐานรวมภาษีที่ออกแทน · ForeignServiceVat.Pp36Base ตัวเดียว) · ที่นี่ตั้งแค่ฐาน/ธงว่ามี VAT
            // §83/6: หน้าที่ประเมิน+นำส่งอยู่ที่ผู้จ่ายทั้งผู้จด/ไม่จด VAT — ต่างกันแค่ "เคลมภาษีซื้อได้ไหม" (R-A4)
            preVat = deducted; chargedVat = 0m;
            treatment = deducted <= 0m ? SettlementFeeVatTreatment.NoVat
                : companyVatRegistered ? SettlementFeeVatTreatment.SelfAssessedPp36
                : SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable;
        }
        else
        {
            chargedVat = explicitVat is decimal v ? Math.Abs(v) : deducted - R(deducted * 100m / (100m + VatRate));
            if (chargedVat > deducted) chargedVat = deducted;   // ข้อมูลเสีย — ผู้เรียกฟ้องด้วย ExplicitVatInvalid
            preVat = deducted - chargedVat;
            treatment = chargedVat == 0m ? SettlementFeeVatTreatment.NoVat
                : companyVatRegistered ? SettlementFeeVatTreatment.InputVatPending
                : SettlementFeeVatTreatment.VatNotClaimable;
        }

        // ── WHT: ฐานก่อน VAT · อัตราจากตารางกฎหมายตัวเดียว (ผู้รับ = นิติบุคคลไทย) · ต่างประเทศ = ม.70 ผ่านตัวตัดสินเดียว ไม่ใช้อัตราในประเทศ (R-A5 · ทีม W) ──
        decimal rate;
        if (whtMode == SettlementFeeWhtMode.None) rate = 0m;
        else if (SettlementForeignWht.IsForeignChannel(vatMode))
            rate = whtMode is SettlementFeeWhtMode.SelfWithholdReimbursed or SettlementFeeWhtMode.SelfWithholdPayerBorne
                ? SettlementForeignWht.Decide(whtIncomeCode, paymentDate ?? DateTime.MinValue).RatePercent ?? 0m
                : 0m;   // W1 ตัวแทนหักแทน — ต่างประเทศยื่น ภ.ง.ด.54 แทนเราไม่ได้ (แผนบล็อก)
        else rate = ThaiWhtRateTable.RateFor(whtIncomeCode, payeeIsJuristic: true) ?? 0m;
        decimal whtBase = 0m, wht = 0m, certIncome = 0m, borne = 0m;
        if (rate > 0m && preVat > 0m)
        {
            whtBase = preVat;
            (wht, certIncome, borne) = WhtOnBase(preVat, rate, whtMode);
        }
        else whtMode = SettlementFeeWhtMode.None;

        // ── ภ.พ.36 (หลัง WHT): ฐาน = มูลค่าบริการ + ภาษีที่ออกแทน (คำตัดสินข้อ 40 · W-3) — หักจากเงินที่จ่าย (W2) borne = 0 ⇒ ฐานเดิม ──
        decimal inputVat, expense;
        if (IsSelfAssessed(treatment))
        {
            (pp36, inputVat, expense) = Pp36Legs(treatment, preVat, borne);
            if (pp36 <= 0m) treatment = SettlementFeeVatTreatment.NoVat;
        }
        else
        {
            inputVat = treatment == SettlementFeeVatTreatment.InputVatPending ? chargedVat : 0m;
            expense = treatment == SettlementFeeVatTreatment.VatNotClaimable ? deducted : preVat;
        }

        return new SettlementFeeTaxResult(deducted, expense, inputVat, pp36, treatment, whtMode,
            whtMode == SettlementFeeWhtMode.None ? null : whtIncomeCode, rate, whtBase, wht, certIncome, borne);
    }

    /// <summary>ชนิดภาษีของค่าธรรมเนียมที่ตั้งหนี้ ภ.พ.36 เอง (ผู้ให้บริการต่างประเทศ · เคลมได้/ไม่ได้)</summary>
    public static bool IsSelfAssessed(SettlementFeeVatTreatment treatment)
        => treatment is SettlementFeeVatTreatment.SelfAssessedPp36 or SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable;

    /// <summary>
    /// ขา ภ.พ.36 ของค่าธรรมเนียมผู้ให้บริการต่างประเทศ — <b>สูตรตัวเดียว</b>ของทั้งรายก้อน (<see cref="Compute"/>) และรายบรรทัดใบ
    /// (<c>SettlementBatchMath.BuildFeeLines</c> คิดซ้ำจากฐานรวม + ภาษีออกแทนของบรรทัดใบ) · ฝ่ายค้านรอบสอง R2M-4: X-4 ทำให้ WHT/50 ทวิ คิดจากฐานรวมของบรรทัดใบ
    /// แต่ ภ.พ.36 ยังรวมจากรายส่วนที่ปัดภาษีออกแทนทีละรายการ ⇒ 100 × 3.65 ออกภาษีแทน ม.70 ได้ ภ.พ.36 30.00 แทน 30.06 (ลำดับข้อ 40 ไม่ได้ใช้ WHT ตัวที่ลง 50 ทวิ)
    /// <para>ภ.พ.36 = round((ค่าบริการ + ภาษีออกแทน) × 7%) · ภาษีซื้อ (11640) = ภ.พ.36 เมื่อเคลมได้ · ค่าใช้จ่าย = ค่าบริการ (+ ภ.พ.36 เมื่อเคลมไม่ได้ —
    /// ยอดที่หักจาก wallet เท่าเดิม ส่วน VAT จ่ายสรรพากรผ่าน 21912) · ชนิดอื่น ⇒ (0, 0, ค่าบริการ)</para>
    /// </summary>
    /// <param name="serviceValue">ค่าบริการ (ผู้ให้บริการต่างประเทศไม่เก็บ VAT ไทย ⇒ = ยอดที่ถูกหัก)</param>
    /// <param name="payerBorneTax">ภาษีเงินได้ที่เราออกแทน (W3) ของก้อน/บรรทัดใบเดียวกัน · หักจากเงินที่จ่าย = 0</param>
    public static (decimal Pp36, decimal InputVat, decimal Expense) Pp36Legs(SettlementFeeVatTreatment treatment, decimal serviceValue,
        decimal payerBorneTax)
    {
        if (!IsSelfAssessed(treatment) || serviceValue <= 0m) return (0m, 0m, serviceValue);
        var pp36 = ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(serviceValue, payerBorneTax));
        return treatment == SettlementFeeVatTreatment.SelfAssessedPp36
            ? (pp36, pp36, serviceValue)
            : (pp36, 0m, serviceValue + pp36);
    }

    /// <summary>ภาษีหัก ณ ที่จ่ายของฐานก่อน VAT หนึ่งก้อน — สูตรตัวเดียวของทั้งรายก้อน (<see cref="Compute"/>) และรายบรรทัดใบ
    /// (<c>SettlementBatchMath.BuildFeeLines</c> คิดซ้ำจากฐานรวมของบรรทัดใบ — ฝ่ายค้านรอบ 200 X-4: ปัดทีละรายการแล้วรวม ⇒ 50 ทวิ/ภ.ง.ด.53 สูงเกิน) ·
    /// ออกภาษีแทน (W3) = ฐาน × อัตรา/(100 − อัตรา) · เงินได้บน 50 ทวิ = ฐาน + ภาษี · อื่น ๆ = ฐาน × อัตรา/100 · ปัด AwayFromZero</summary>
    public static (decimal Wht, decimal CertIncome, decimal Borne) WhtOnBase(decimal preVatBase, decimal ratePercent, SettlementFeeWhtMode whtMode)
    {
        if (ratePercent <= 0m || preVatBase <= 0m || whtMode == SettlementFeeWhtMode.None) return (0m, 0m, 0m);
        if (whtMode == SettlementFeeWhtMode.SelfWithholdPayerBorne)
        {
            var borneWht = R(preVatBase * ratePercent / (100m - ratePercent));
            return (borneWht, preVatBase + borneWht, borneWht);
        }
        return (R(preVatBase * ratePercent / 100m), preVatBase, 0m);
    }

    /// <summary>ไฟล์ให้ค่าธรรมเนียม<b>ก่อน VAT</b> (VAT แยกคอลัมน์/คิดทับ) — คืน (ยอดที่ถูกหักรวม VAT, VAT) ให้ adapter เก็บลง
    /// <c>SettlementLine.Amount</c>/<c>VatAmount</c> · VAT = round(ก่อน VAT × 7%, AwayFromZero)</summary>
    public static (decimal Deducted, decimal Vat) FromExclusive(decimal preVat)
    {
        var net = Math.Abs(preVat);
        var vat = R(net * VatRate / 100m);
        return (net + vat, vat);
    }

    /// <summary>แยกยอดที่รวม VAT เป็น (ก่อน VAT, VAT) — ใบขายสรุปรายวัน/ค่าธรรมเนียม · บริษัทไม่จด VAT ⇒ (ยอด, 0)
    /// <para>ก่อน VAT = round(ยอด × 100/107, AwayFromZero) · VAT = ยอด − ก่อน VAT (ห้ามปัดสองครั้ง)</para></summary>
    public static (decimal Net, decimal Vat) SplitInclusive(decimal gross, bool vatApplies)
    {
        if (!vatApplies || gross == 0m) return (gross, 0m);
        var net = R(gross * 100m / (100m + VatRate));
        return (net, gross - net);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
