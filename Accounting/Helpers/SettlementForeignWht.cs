using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **หัก ณ ที่จ่ายค่าธรรมเนียมที่จ่ายให้ผู้ให้บริการต่างประเทศในรอบโอน settlement** (รอบ 200 ทีม W · คำตัดสินข้อ 13 · แทนการบล็อกเหมาของ review198-A R-A5)
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item>สัญญาณ "ผู้รับอยู่ต่างประเทศ" บนช่องทาง = โหมด VAT ค่าธรรมเนียม <see cref="SettlementFeeVatMode.ForeignPp36"/> (ตัวเดียวที่ช่องทางมี) ⇒ WHT ของช่องทางนี้
/// เป็น <b>ภ.ง.ด.54</b> ผัง <see cref="WhtPayableAccount.Pnd54Code"/> · อัตราจาก <see cref="ForeignWhtRateResolver"/> ตัวเดียว (ม.70 → อนุสัญญาเมื่อมีแถว + CoR)</item>
/// <item>ประเภทเงินได้นอก ม.70 (ค่าบริการ/ค่าโฆษณา/ค่าขนส่งที่ตารางประเภทบรรทัดจัดเป็น 40(8)) หรือไม่รู้ ⇒ <b>บล็อกพร้อมทางไปต่อ</b> — ห้ามตกไปอัตราในประเทศ
/// และห้ามเงียบเป็น 0 (การจำแนกเป็นงานของผู้ทำบัญชี)</item>
/// <item>โหมด "แพลตฟอร์มเป็นตัวแทนหักแทน" (W1) กับผู้ให้บริการต่างประเทศ ⇒ บล็อก (ผู้รับเงินต่างประเทศยื่น ภ.ง.ด.54 แทนผู้จ่ายไม่ได้ · หน้าที่อยู่ที่ผู้จ่ายตาม ม.70)</item>
/// <item>ผู้ติดต่อของช่องทางอยู่ต่างประเทศ แต่ช่องทางไม่ได้ตั้งเป็นต่างประเทศ (เช่น ผู้ให้บริการจด e-Service ที่เก็บ VAT ไทยเอง) + มีขา WHT ⇒ บล็อก
/// (ไม่งั้นได้ ภ.ง.ด.53 อัตราในประเทศกับผู้รับต่างประเทศ = R-A5 ในอีกรูป) — ตัดสินที่ <see cref="CounterpartyCountryIssue"/> (ผู้ลงบัญชีรู้ประเทศของผู้ติดต่อ)</item>
/// </list></para>
/// </summary>
public static class SettlementForeignWht
{
    /// <summary>ช่องทางนี้จ่ายค่าธรรมเนียมให้ผู้ให้บริการต่างประเทศไหม (สัญญาณบนช่องทาง)</summary>
    public static bool IsForeignChannel(SettlementFeeVatMode vatMode) => vatMode == SettlementFeeVatMode.ForeignPp36;

    /// <summary>แบบ ภ.ง.ด. ของ WHT ค่าธรรมเนียมของช่องทาง — ต่างประเทศ = ภ.ง.ด.54 · ไทย = ภ.ง.ด.53 (แพลตฟอร์มเป็นนิติบุคคล)</summary>
    public static TaxType WhtForm(SettlementFeeVatMode vatMode)
        => IsForeignChannel(vatMode) ? TaxType.WithholdingTax54 : TaxType.WithholdingTax53;

    /// <summary>
    /// **แบบ ภ.ง.ด. ของขา WHT รอบโอน — ตัวตั้งเดียวของด่าน "เดือนที่ยื่นแล้ว"** (รอบ 200 ทีม WF · ฝ่ายค้าน W-1)
    /// <para>กติกา: ขา WHT ค่าธรรมเนียมผู้ให้บริการต่างประเทศ ⇒ <b>แผนเป็นเจ้าของแบบ</b> (<c>fee.WhtForm</c> = ภ.ง.ด.54 จาก <see cref="WhtForm"/> ·
    /// ตัวเดียวกับที่ 50 ทวิ และขา 21918 ใช้) · ในประเทศ ⇒ <b>ผู้ลงบัญชีเป็นเจ้าของ</b> (ภ.ง.ด.3/53 ตามผู้รับ — ตัวเลือกของ 50 ทวิ ·
    /// review198-C C-11) · ผู้เรียกทุกตัว (ด่าน pure + ตัวโหลดเดือนที่ยื่นแล้ว) ต้องผ่านที่นี่ — เดิมสองที่ตัดสินแยกกัน ⇒ ผู้เรียกที่ลืมส่งแบบ
    /// ได้ข้อความ/ชุดเดือนของ ภ.ง.ด.53 กับรอบโอนต่างประเทศโดยไม่มีอะไรฟ้อง</para>
    /// </summary>
    /// <param name="domesticPayeeForm">แบบในประเทศตามผู้รับ (3/53) ที่ผู้ลงบัญชีหาได้ — ไม่ใช้เมื่อแผนมีขา ภ.ง.ด.54</param>
    public static TaxType GateWhtForm(SettlementPostingPlan plan, TaxType domesticPayeeForm)
        => plan.FeeDocuments.Any(d => d.WhtForm == TaxType.WithholdingTax54) ? TaxType.WithholdingTax54 : domesticPayeeForm;

    /// <summary>คำตัดสินอัตราของค่าธรรมเนียมประเภทหนึ่ง — ผู้รับ = แพลตฟอร์มต่างประเทศ · ยังไม่มีข้อมูลประเทศ/CoR ในช่องทาง ⇒ ม.70 เสมอ
    /// (ดูสถานะใน <see cref="DtaTreatyRates"/>)</summary>
    public static ForeignWhtDecision Decide(string? whtIncomeCode, DateTime paymentDate)
        => ForeignWhtRateResolver.ResolveForIncomeCode(whtIncomeCode, null, ResidenceCertificate.None, paymentDate);

    /// <summary>ปัญหาของแผนระดับช่องทาง (pure) — ช่องทางต่างประเทศ + โหมดหัก + มีค่าธรรมเนียมที่มีรหัสประเภทเงินได้</summary>
    public static IReadOnlyList<SettlementPlanIssue> PlanIssues(SettlementChannel channel, IReadOnlyList<SettlementLine> lines, DateTime paymentDate)
    {
        var issues = new List<SettlementPlanIssue>();
        // ค่าตั้งประเภทเงินได้ของช่องทางอ่านไม่ได้ ⇒ บล็อก (ทุกช่องทาง — ห้ามคิดภาษีจากค่าที่ข้ามไปเงียบ ๆ · คำตัดสินข้อ 41)
        if (channel.FeeWhtMode != SettlementFeeWhtMode.None && SettlementWhtIncomeType.MapIssue(channel) is SettlementPlanIssue badMap)
            issues.Add(badMap);
        if (!IsForeignChannel(channel.FeeVatMode) || channel.FeeWhtMode == SettlementFeeWhtMode.None) return issues;
        // รหัสประเภทเงินได้จากตัวตัดสินเดียวกับผู้คิดภาษี (ค่าตั้งของช่องทาง → 40(2) ตั้งต้นของต่างประเทศ → ตารางประเภทบรรทัด)
        var feeLines = lines
            .Select(l => (Line: l, Rule: SettlementLineTypeRules.For(l.LineType), Code: SettlementWhtIncomeType.For(l.LineType, channel).Code))
            .Where(x => x.Line.Amount != 0m && x.Rule.IsFee && x.Code != null)
            .ToList();
        if (feeLines.Count == 0) return issues;

        if (channel.FeeWhtMode == SettlementFeeWhtMode.AgentWithholds)
        {
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ForeignWhtNotSupported, true,
                $"ช่องทาง \"{channel.DisplayName}\" เป็นผู้ให้บริการต่างประเทศ แต่ตั้งว่า \"แพลตฟอร์มเป็นตัวแทนหัก ณ ที่จ่ายแทนเรา\" — "
                + $"ภาษีของเงินได้ที่จ่ายไปต่างประเทศเป็นหน้าที่ของผู้จ่าย ยื่น ภ.ง.ด.54 เอง ({ForeignWhtRateResolver.Section70Reference})",
                "ถ้าต้องหัก ให้ตั้งโหมดหักเป็น \"หักเอง\" หรือ \"ออกภาษีแทน\" (ระบบคิดตาม ม.70 ลง ภ.ง.ด.54 ให้) · "
                + "ถ้าผู้ทำบัญชีจำแนกแล้วว่าไม่ต้องหัก ให้ตั้งเป็น \"ไม่หัก\"" + GatewayConfigHint(channel),
                feeLines.Select(x => x.Line.Id).ToList(), null));
            return issues;
        }

        foreach (var g in feeLines.GroupBy(x => x.Code))
        {
            var decision = Decide(g.Key, paymentDate);
            if (decision.HasRate) continue;
            var labels = string.Join(", ", g.Select(x => x.Rule.LabelTh).Distinct());
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ForeignWhtNotSupported, true,
                $"[{decision.RuleCode}] {labels} ของผู้ให้บริการต่างประเทศ \"{channel.DisplayName}\" — {decision.Explanation}",
                // คำตัดสินข้อ 41: ทางไปต่อระดับช่องทาง (จำแนกครั้งเดียว ใช้ทุกรอบ) แทนการทำใบมือทุกรอบโอน
                $"ให้ผู้ทำบัญชีจำแนกประเภทเงินได้ของ \"{labels}\" ครั้งเดียวที่หน้าตั้งค่าช่องทาง → \"ประเภทเงินได้ของค่าธรรมเนียม\" "
                + "(ค่าธรรมเนียม/ค่านายหน้า = 40(2) ⇒ ระบบหักตาม ม.70 ลง ภ.ง.ด.54 ให้ · ไม่ต้องหัก = \"ไม่หัก ณ ที่จ่าย\") แล้วดูตัวอย่างใหม่"
                + GatewayConfigHint(channel),
                g.Select(x => x.Line.Id).ToList(), null));
        }
        return issues;
    }

    /// <summary>ฝ่ายค้าน W-6 → ฝ่ายค้านรอบสอง R2M-7: ช่องทาง<b>ต่างประเทศ</b>ที่ผูกการตั้งค่า gateway ไม่มีโหมดใดที่ "ตรงกัน" ได้ (X-3 · คำตัดสินข้อ 26 —
    /// config ของ gateway ไม่มีโหมด ภ.พ.36) ⇒ เดิมบอกให้ "แก้การตั้งค่า gateway ให้ตรงกัน" ซึ่งทำตามแล้วยังถูกบล็อก ขัดกับข้อความด่านโหมดบนพรีวิวเดียวกัน ·
    /// ตอนนี้ใช้ทางไปต่อตัวเดียวกับด่านโหมด (<see cref="GatewayBatchIntentRules.ForeignPp36BoundNextStep"/>) · ไม่ผูก ⇒ ไม่ต่อท้าย</summary>
    private static string GatewayConfigHint(SettlementChannel channel)
    {
        if (channel.PaymentProviderConfigId is null) return "";
        return " · ช่องทางนี้ผูกการตั้งค่า gateway ซึ่งใช้กับผู้ให้บริการต่างประเทศไม่ได้ — " + GatewayBatchIntentRules.ForeignPp36BoundNextStep;
    }

    /// <summary>
    /// ผู้ติดต่อของช่องทางอยู่ต่างประเทศ แต่ช่องทางไม่ได้ตั้งเป็นต่างประเทศ และแผนมีขาหัก ณ ที่จ่ายที่เราต้องยื่นเอง ⇒ บล็อก (null = ไม่มีปัญหา)
    /// <para>"ต่างประเทศ" ตัดสินด้วย <see cref="WhtPayeeKind.IsForeignPayee"/> ตัวเดียวกับทะเบียน 50 ทวิ/รายงาน · ประเทศว่าง = ไม่รู้ ⇒ ไม่บล็อก (สัญญาณเดิมของช่องทางยังใช้)</para>
    /// </summary>
    public static SettlementPlanIssue? CounterpartyCountryIssue(SettlementChannel channel, SettlementPostingPlan plan, string? counterpartyCountryCode)
    {
        if (IsForeignChannel(channel.FeeVatMode)) return null;
        if (!WhtPayeeKind.IsForeignPayee(false, counterpartyCountryCode)) return null;
        var whtLegs = plan.FeeDocuments.Where(d => d.WhtAmount > 0m
            && d.WhtMode is SettlementFeeWhtMode.SelfWithholdReimbursed or SettlementFeeWhtMode.SelfWithholdPayerBorne).ToList();
        if (whtLegs.Count == 0) return null;
        return new SettlementPlanIssue(SettlementPlanIssueCode.ForeignWhtNotSupported, true,
            $"ผู้ติดต่อของช่องทาง \"{channel.DisplayName}\" (ผู้รับค่าธรรมเนียม) อยู่ต่างประเทศ ({counterpartyCountryCode!.Trim().ToUpperInvariant()}) "
            + "แต่ช่องทางคิดหัก ณ ที่จ่ายด้วยอัตราในประเทศลง ภ.ง.ด.53 — ผู้รับต่างประเทศต้องหักตาม "
            + $"{ForeignWhtRateResolver.Section70Reference} ยื่น ภ.ง.ด.54",
            "ถ้าผู้ให้บริการไม่ได้เก็บ VAT ไทย ให้ตั้งโหมด VAT ค่าธรรมเนียมเป็น \"ผู้ให้บริการต่างประเทศ (ภ.พ.36)\" (ระบบจะหักตาม ม.70 ลง ภ.ง.ด.54 ให้) · "
            + "ถ้าผู้ให้บริการจด e-Service และเก็บ VAT ไทยในยอดแล้ว ให้ตั้งโหมดหักเป็น \"ไม่หัก\" แล้วบันทึกใบสำคัญจ่ายหัก ภ.ง.ด.54 ด้วยมือ · "
            + "ถ้าประเทศของผู้ติดต่อผิด แก้ที่หน้าผู้ติดต่อ",
            whtLegs.SelectMany(d => d.Lines).SelectMany(l => l.LineIds).ToList(), whtLegs.Sum(d => d.WhtAmount));
    }
}
