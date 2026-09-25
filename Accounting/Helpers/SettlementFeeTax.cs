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
}

/// <summary>ผลแยกภาษีของค่าธรรมเนียม 1 ก้อน (ยอดบวกเสมอ — เครื่องหมายเป็นเรื่องของผู้เรียก)</summary>
/// <param name="Deducted">ยอดที่ถูกหักจาก wallet จริง (= ยอดในไฟล์ · รวม VAT ไทยถ้ามี)</param>
/// <param name="Expense">ค่าใช้จ่ายที่ลงผังค่าธรรมเนียม — ไม่รวมภาษีซื้อที่เคลมได้ · รวม VAT ที่เคลมไม่ได้ · <b>ไม่รวม</b>ภาษีที่ออกแทน (ดู <paramref name="WhtBorneExpense"/>)</param>
/// <param name="InputVat">ภาษีซื้อที่เคลมได้ (11630 หรือ 11640 ของ ภ.พ.36)</param>
/// <param name="Pp36Payable">หนี้ ภ.พ.36 ที่ประเมินเอง (21912) — มีเฉพาะ <see cref="SettlementFeeVatTreatment.SelfAssessedPp36"/></param>
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
/// <item><b>ออกภาษีแทน (W3)</b>: ภาษี = round(ฐาน × r/(100−r)) → 3% = ฐาน × 3/97 · เงินได้บน 50 ทวิ = ฐาน + ภาษี</item>
/// <item><b>บริษัทไม่จด VAT</b>: ไม่มีขาภาษีซื้อ — VAT ที่ถูกเก็บรวมเป็นค่าใช้จ่าย · ภ.พ.36 ไม่สร้าง (ผู้ให้บริการต่างประเทศที่จด e-Service
/// เก็บ VAT ในยอดแล้ว — report-S1 §5 · ความมั่นใจกลาง รอนักบัญชียืนยัน)</item>
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
    public static SettlementFeeTaxResult Compute(
        decimal deducted,
        decimal? explicitVat,
        SettlementFeeVatMode vatMode,
        bool vatApplicable,
        bool companyVatRegistered,
        SettlementFeeWhtMode whtMode,
        string? whtIncomeCode)
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
            // ผู้ให้บริการต่างประเทศไม่เก็บ VAT ไทย ⇒ ยอดที่หัก = ฐาน · เราประเมินเอง 7% ของฐาน
            preVat = deducted; chargedVat = 0m;
            if (companyVatRegistered)
            {
                pp36 = R(deducted * VatRate / 100m);
                treatment = pp36 > 0m ? SettlementFeeVatTreatment.SelfAssessedPp36 : SettlementFeeVatTreatment.NoVat;
            }
            else treatment = SettlementFeeVatTreatment.NoVat;
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

        var inputVat = treatment switch
        {
            SettlementFeeVatTreatment.InputVatPending => chargedVat,
            SettlementFeeVatTreatment.SelfAssessedPp36 => pp36,
            _ => 0m,
        };
        var expense = treatment == SettlementFeeVatTreatment.VatNotClaimable ? deducted : preVat;

        // ── WHT: ฐานก่อน VAT · อัตราจากตารางกฎหมายตัวเดียว (ผู้รับ = นิติบุคคล) ──
        var rate = whtMode == SettlementFeeWhtMode.None ? 0m
            : ThaiWhtRateTable.RateFor(whtIncomeCode, payeeIsJuristic: true) ?? 0m;
        decimal whtBase = 0m, wht = 0m, certIncome = 0m, borne = 0m;
        if (rate > 0m && preVat > 0m)
        {
            whtBase = preVat;
            if (whtMode == SettlementFeeWhtMode.SelfWithholdPayerBorne)
            {
                wht = R(preVat * rate / (100m - rate));
                certIncome = preVat + wht;
                borne = wht;
            }
            else
            {
                wht = R(preVat * rate / 100m);
                certIncome = preVat;
            }
        }
        else whtMode = SettlementFeeWhtMode.None;

        return new SettlementFeeTaxResult(deducted, expense, inputVat, pp36, treatment, whtMode,
            whtMode == SettlementFeeWhtMode.None ? null : whtIncomeCode, rate, whtBase, wht, certIncome, borne);
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
