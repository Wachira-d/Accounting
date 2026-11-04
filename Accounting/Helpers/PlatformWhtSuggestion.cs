namespace Accounting.Helpers;

/// <summary>
/// รอบ 234 (คำตัดสิน 234-1…234-3 · เจ้าของ: "ถ้าลูกค้าและยอดเข้าเกณฑ์หัก ณ ที่จ่าย ระบบคำนวณให้อัตโนมัติรึยัง") — <b>ตัวตัดสินเดียว</b>ของ
/// "ผู้เช่าควรหักภาษี ณ ที่จ่ายจากค่าบริการ NextAcc ไหม · เท่าไร · ยอดโอน/ตัดบัตรสุทธิเท่าไร" · pure
/// <list type="bullet">
/// <item>อัตรา = ม.40(2) ค่าบริการ ผู้รับนิติบุคคล จาก <see cref="ThaiWhtRateTable"/> (ตารางเดียว · ท.ป.4/2528 · NextAcc เป็นนิติบุคคล)</item>
/// <item>ฐาน = ยอดก่อน VAT: ใบแจ้งหนี้ ⇒ <see cref="BaseOfInvoice"/> (สัดส่วนยอดค้าง ÷ ยอดรวม × ยอดก่อน VAT) · รายการซื้อแพ็กเกจ (เงินรวม VAT) ⇒
/// <see cref="BaseOfReceived"/> (ตัวแยกเดียวกับตัวออกใบ <c>PlatformMoneyInAccount.SplitReceived</c> — ถอด 7/107)</item>
/// <item>เกณฑ์ ฿1,000 = <see cref="ThaiWhtRateTable.ShouldWithhold"/> ต่อครั้งที่จ่าย (คำตัดสิน 234-2 — ค่าบริการรายรอบไม่มียอดสัญญาทั้งก้อนที่รู้แน่)</item>
/// <item>ผู้จ่ายต้องเป็นนิติบุคคล (เลขผู้เสียภาษี) + ข้อมูลบริษัทครบ (<see cref="PlatformPayerProfileGate"/>) · ผู้เช่าเลือกหัก/ไม่หัก (หน้าเว็บส่งแค่ true/false —
/// เซิร์ฟเวอร์คิดยอดเอง)</item>
/// <item>ตัวตรวจยอดที่ผู้เช่า/แอดมินกรอกเอง (<see cref="PlatformInvoiceRules.WithholdingProblem"/>) ใช้เพดานจาก <see cref="MaxWithholding"/> ตัวนี้ — ไม่มีสูตรที่สอง</item>
/// </list>
/// ผู้เรียก (ล็อกด้วย <c>tools/required_call_site_check.py</c>): <c>SubscriptionService.RequestPlanPaymentAsync</c> · <c>SubmitPaymentAsync</c> · <c>QuotePlanChangeAsync</c> ·
/// <c>PlatformBillingCenterService</c> (ยอดเสนอของใบ) · <c>PlatformAutoChargeService</c> · <c>PublicCheckoutService.QuoteAsync</c>
/// </summary>
public static class PlatformWhtSuggestion
{
    /// <summary>ประเภทเงินได้ของค่าบริการซอฟต์แวร์/ระบบบัญชีออนไลน์ (ค่าบริการ ม.40(2) — ตามที่ศูนย์บิลใช้มาตั้งแต่รอบ 219)</summary>
    public const string IncomeTypeCode = "40(2)";

    public enum Outcome { Withhold = 0, OptedOut = 1, NotJuristic = 2, BelowThreshold = 3, ProfileIncomplete = 4, NothingDue = 5 }

    /// <summary>ผล — <see cref="Eligible"/> = ผู้เช่าหักได้ (นิติบุคคล · ข้อมูลครบ · มียอด) ⇒ หน้าเว็บแสดงช่องติ๊ก · <see cref="DefaultChecked"/> = เข้าเกณฑ์ ฿1,000
    /// (ค่าตั้งต้นของช่องติ๊ก — ต่ำกว่าเกณฑ์ = ไม่ต้องหัก แต่ไม่ห้ามหัก ให้ตรงกับตัวตรวจ <see cref="PlatformInvoiceRules.WithholdingProblem"/>) ·
    /// <see cref="Withhold"/> = หักจริงตามที่ผู้เช่าเลือก · <see cref="SuggestedAmount"/> = ยอดหักถ้าหัก · <see cref="WithholdingAmount"/>/<see cref="NetAmount"/> = ยอดที่ใช้จริง ·
    /// <see cref="ContractAnnualBase"/> = มูลค่าสัญญาต่อปีก่อน VAT ที่ใช้ตัดสินเกณฑ์ (null = ไม่รู้ ⇒ ดูยอดครั้งนี้)</summary>
    public sealed record Result(
        string Outcome, bool Eligible, bool Withhold, decimal RatePercent, decimal BaseAmount, decimal GrossAmount,
        decimal SuggestedAmount, decimal WithholdingAmount, decimal NetAmount, string Reason, string? Line, string? ProfileProblem,
        bool DefaultChecked = false, bool MeetsThreshold = false, decimal? ContractAnnualBase = null);

    public static decimal RatePercent => ThaiWhtRateTable.Find(IncomeTypeCode)?.JuristicRate ?? 3m;

    private static decimal R2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ฐานก่อน VAT ของยอดค้างใบแจ้งหนี้ = ยอดก่อน VAT ตามสัดส่วน (ยอดค้าง ÷ ยอดรวม) · ไม่รู้ยอดก่อน VAT ⇒ ยอดค้าง (ฝ่ายค้าน S4 + รอบสุดท้าย 4b)</summary>
    public static decimal BaseOfInvoice(decimal balanceDue, decimal totalAmount, decimal subTotal)
        => subTotal > 0m && totalAmount > 0m ? R2(subTotal * Math.Min(1m, balanceDue / totalAmount)) : balanceDue;

    /// <summary>ฐานก่อน VAT ของเงินที่จ่ายเต็ม (รวม VAT เมื่อผู้ให้บริการออกใบกำกับ) — ตัวแยกเดียวกับใบที่ออก (<see cref="PlatformMoneyInAccount.SplitReceived"/>)</summary>
    public static decimal BaseOfReceived(decimal grossReceived, bool vatIncluded)
        => PlatformMoneyInAccount.SplitReceived(R2(grossReceived), vatIncluded).Base;

    /// <summary>เพดาน/ยอดหัก = อัตรา ม.40(2) × ฐานก่อน VAT · ปัด AwayFromZero</summary>
    public static decimal MaxWithholding(decimal baseBeforeVat) => R2(Math.Max(0m, baseBeforeVat) * RatePercent / 100m);

    /// <summary>ยอดรวมก่อนหัก (เงินที่รับ + ภาษีถูกหัก) — ตัวเดียวที่ด่านเทียบราคา/ช่องทางออนไลน์/handler ใช้กับรายการที่ผู้เช่าหักภาษี</summary>
    public static decimal GrossOf(decimal amountReceived, decimal withholdingTax) => R2(amountReceived + Math.Max(0m, withholdingTax));

    /// <summary>มูลค่าสัญญาค่าแพ็กเกจต่อปี (ยอดรวม VAT ถ้ามี) = ราคาต่อรอบ × จำนวนรอบต่อปี — subscription ที่ต่ออายุอัตโนมัติ = สัญญาต่อเนื่อง (คำตัดสิน 234-2) ·
    /// รอบไม่รู้จัก ⇒ null (ไม่รู้ = ไม่แต่ง)</summary>
    public static decimal? AnnualContractGross(decimal pricePerCycle, int monthsPerCycle)
        => monthsPerCycle > 0 && pricePerCycle > 0m ? R2(pricePerCycle * 12m / monthsPerCycle) : null;

    /// <summary>ตัดสิน — <paramref name="grossDue"/> = ยอดที่ต้องจ่ายเต็ม (ก่อนหัก) · <paramref name="baseBeforeVat"/> = ฐานก่อน VAT ของยอดนั้น ·
    /// <paramref name="tenantChoosesWithhold"/> = ผู้เช่าติ๊ก "หัก ณ ที่จ่าย" ·
    /// <paramref name="contractAnnualBase"/> = มูลค่าสัญญาต่อปีก่อน VAT (null = ไม่รู้ยอดสัญญา ⇒ เกณฑ์ดูยอดครั้งนี้) — ส่งเข้า
    /// <see cref="ThaiWhtRateTable.ShouldWithhold"/> ตัวเดิม (รวมทั้งสัญญา ≥ 1,000 ⇒ หักทุกงวด · กฎเหล็ก #2 §E) ห้ามสูตรที่สอง</summary>
    public static Result Decide(PlatformPayerProfileGate.Result payer, decimal grossDue, decimal baseBeforeVat, bool tenantChoosesWithhold,
        decimal? contractAnnualBase = null)
    {
        var rate = RatePercent;
        var gross = R2(Math.Max(0m, grossDue));
        var b = R2(Math.Max(0m, Math.Min(baseBeforeVat, gross)));
        var suggested = MaxWithholding(b);
        var contract = contractAnnualBase is decimal c ? R2(Math.Max(0m, c)) : (decimal?)null;
        // เกณฑ์ ฿1,000: รู้ยอดสัญญา ⇒ ตัดสินจากยอดสัญญาทั้งก้อน (ShouldWithhold ของยอดสัญญา) · ไม่รู้ ⇒ ยอดครั้งนี้
        var meets = contract is decimal ct
            ? ThaiWhtRateTable.ShouldWithhold(b, contractTotalKnown: ThaiWhtRateTable.ShouldWithhold(ct))
            : ThaiWhtRateTable.ShouldWithhold(b);
        Result R(Outcome o, bool eligible, bool withhold, decimal sug, string reason, string? line, string? profile)
        {
            var w = withhold ? sug : 0m;
            return new Result(o.ToString(), eligible, withhold, rate, b, gross, sug, w, R2(gross - w), reason, line, profile,
                DefaultChecked: eligible && meets, MeetsThreshold: meets, ContractAnnualBase: contract);
        }
        if (gross <= 0m || suggested <= 0m)
            return R(Outcome.NothingDue, false, false, 0m, "ไม่มียอดที่ต้องหัก", null, null);
        if (!payer.IsJuristic)
            return R(Outcome.NotJuristic, false, false, 0m,
                payer.TaxIdValid ? "ผู้ซื้อเป็นบุคคลธรรมดา — ไม่ต้องหักภาษี ณ ที่จ่าย" : PlatformPayerProfileGate.NoTaxIdWarning, null, null);
        if (PlatformPayerProfileGate.WithholdingRefusal(payer) is string problem)
            return R(Outcome.ProfileIncomplete, false, false, suggested,
                $"บริษัทของท่านหักภาษี ณ ที่จ่าย {rate:0.##}% (฿{suggested:N2}) ได้ แต่ข้อมูลบริษัทยังไม่ครบ — กรอกข้อมูลก่อนจึงจะหักได้", null, problem);
        var net = R2(gross - suggested);
        var line = meets
            ? $"บริษัทของท่านต้องหักภาษี ณ ที่จ่าย {rate:0.##}% (฿{suggested:N2}) — ชำระ ฿{net:N2} · ส่งหนังสือรับรอง 50 ทวิ ภายหลัง"
              + (contract is decimal cm && b < ThaiWhtRateTable.MinimumThresholdBaht ? $" (มูลค่าสัญญาต่อปีก่อน VAT ฿{cm:N2} ถึงเกณฑ์ {ThaiWhtRateTable.MinimumThresholdBaht:N0} บาท ⇒ หักทุกงวด)" : "")
            : (contract is decimal cl
                ? $"มูลค่าสัญญาต่อปีก่อน VAT ฿{cl:N2} — ต่ำกว่าเกณฑ์ {ThaiWhtRateTable.MinimumThresholdBaht:N0} บาท จึงไม่ต้องหัก"
                : $"ค่าบริการก่อน VAT ฿{b:N2} — ต่ำกว่าเกณฑ์ {ThaiWhtRateTable.MinimumThresholdBaht:N0} บาท จึงไม่ต้องหัก")
              + $" (ถ้าบริษัทเลือกหัก {rate:0.##}% = ฿{suggested:N2} — ชำระ ฿{net:N2} · ส่ง 50 ทวิ ภายหลัง)";
        if (!tenantChoosesWithhold)
            return R(meets ? Outcome.OptedOut : Outcome.BelowThreshold, true, false, suggested,
                meets ? "ท่านเลือกไม่หักภาษี ณ ที่จ่าย — ชำระเต็มจำนวน" : "ต่ำกว่าเกณฑ์ ฿1,000 — ไม่ต้องหัก (ชำระเต็มจำนวน)", line, null);
        return R(Outcome.Withhold, true, true, suggested,
            $"หักภาษี ณ ที่จ่าย {rate:0.##}% ของค่าบริการก่อน VAT {b:N2} บาท (ม.40(2) · ท.ป.4/2528)", line, null);
    }
}
