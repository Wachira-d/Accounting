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

    /// <summary>คำตัดสินอัตราของค่าธรรมเนียมประเภทหนึ่ง — ผู้รับ = แพลตฟอร์มต่างประเทศ · ยังไม่มีข้อมูลประเทศ/CoR ในช่องทาง ⇒ ม.70 เสมอ
    /// (ดูสถานะใน <see cref="DtaTreatyRates"/>)</summary>
    public static ForeignWhtDecision Decide(string? whtIncomeCode, DateTime paymentDate)
        => ForeignWhtRateResolver.ResolveForIncomeCode(whtIncomeCode, null, ResidenceCertificate.None, paymentDate);

    /// <summary>ปัญหาของแผนระดับช่องทาง (pure) — ช่องทางต่างประเทศ + โหมดหัก + มีค่าธรรมเนียมที่มีรหัสประเภทเงินได้</summary>
    public static IReadOnlyList<SettlementPlanIssue> PlanIssues(SettlementChannel channel, IReadOnlyList<SettlementLine> lines, DateTime paymentDate)
    {
        var issues = new List<SettlementPlanIssue>();
        if (!IsForeignChannel(channel.FeeVatMode) || channel.FeeWhtMode == SettlementFeeWhtMode.None) return issues;
        var feeLines = lines
            .Select(l => (Line: l, Rule: SettlementLineTypeRules.For(l.LineType)))
            .Where(x => x.Line.Amount != 0m && x.Rule.IsFee && x.Rule.WhtIncomeCode != null)
            .ToList();
        if (feeLines.Count == 0) return issues;

        if (channel.FeeWhtMode == SettlementFeeWhtMode.AgentWithholds)
        {
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ForeignWhtNotSupported, true,
                $"ช่องทาง \"{channel.DisplayName}\" เป็นผู้ให้บริการต่างประเทศ แต่ตั้งว่า \"แพลตฟอร์มเป็นตัวแทนหัก ณ ที่จ่ายแทนเรา\" — "
                + $"ภาษีของเงินได้ที่จ่ายไปต่างประเทศเป็นหน้าที่ของผู้จ่าย ยื่น ภ.ง.ด.54 เอง ({ForeignWhtRateResolver.Section70Reference})",
                "ถ้าต้องหัก ให้ตั้งโหมดหักเป็น \"หักเอง\" หรือ \"ออกภาษีแทน\" (ระบบคิดตาม ม.70 ลง ภ.ง.ด.54 ให้) · "
                + "ถ้าผู้ทำบัญชีจำแนกแล้วว่าไม่ต้องหัก ให้ตั้งเป็น \"ไม่หัก\"",
                feeLines.Select(x => x.Line.Id).ToList(), null));
            return issues;
        }

        foreach (var g in feeLines.GroupBy(x => x.Rule.WhtIncomeCode))
        {
            var decision = Decide(g.Key, paymentDate);
            if (decision.HasRate) continue;
            var labels = string.Join(", ", g.Select(x => x.Rule.LabelTh).Distinct());
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ForeignWhtNotSupported, true,
                $"[{decision.RuleCode}] {labels} ของผู้ให้บริการต่างประเทศ \"{channel.DisplayName}\" — {decision.Explanation}",
                "ให้ผู้ทำบัญชีจำแนกประเภทเงินได้: ถ้าไม่ต้องหัก ตั้งโหมดหัก ณ ที่จ่ายของช่องทางเป็น \"ไม่หัก\" · ถ้าต้องหัก "
                + "บันทึกใบสำคัญจ่ายบริการต่างประเทศด้วยมือ (ติ๊ก ภ.พ.36 + หัก ภ.ง.ด.54 ตามประเภทเงินได้ที่จำแนก) แล้วเปลี่ยนบรรทัดนั้นเป็น "
                + "\"ปรับปรุงอื่น\" ที่ชี้ผังของใบนั้น",
                g.Select(x => x.Line.Id).ToList(), null));
        }
        return issues;
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
