using Accounting.Models.DTOs.Tax;

namespace Accounting.Helpers;

/// <summary>ยอดภาษีหัก ณ ที่จ่ายของฐานหนึ่งก้อน/หนึ่งบรรทัด ตามเงื่อนไขการหัก (50 ทวิ ช่อง (1)(2)(3))</summary>
/// <param name="Tax">ภาษีที่นำส่งสรรพากร (ลงช่อง "ภาษีที่หักและนำส่งไว้" บน 50 ทวิ · เครดิต 21916/21917/21918)</param>
/// <param name="PayerBorne">ส่วนที่ผู้จ่าย "ออกแทน" (ไม่หักจากเงินที่จ่ายผู้รับ — เป็นค่าใช้จ่ายของผู้จ่าย)</param>
/// <param name="CertIncome">เงินได้ที่พิมพ์บน 50 ทวิ / ไฟล์ ภ.ง.ด. = ฐานที่ตกลงจ่าย + ภาษีที่ออกแทน (ม.39 ภาษีที่ออกแทนเป็นเงินได้)</param>
public sealed record WhtGrossUpAmounts(decimal Tax, decimal PayerBorne, decimal CertIncome)
{
    /// <summary>ส่วนที่หักจากเงินที่จ่ายผู้รับจริง = ภาษีนำส่ง − ส่วนที่ผู้จ่ายออกแทน (หัก ณ ที่จ่าย = ภาษีทั้งก้อน · ตลอดไป = 0 · ครั้งเดียว = ภาษีของภาษี)</summary>
    public decimal PayeeBorne => Tax - PayerBorne;
}

/// <summary>
/// **ภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทน (gross-up) — ตัวตั้งเดียวของทุกเส้น** (รอบ 204 · คำตัดสินเจ้าของ DECISIONS ข้อ 148 (4))
///
/// <para>═══ หลักกฎหมาย ═══ ป.รัษฎากร ม.39 นิยาม "เงินได้พึงประเมิน" ให้<b>รวมถึงเงินค่าภาษีอากรที่ผู้จ่ายเงินหรือผู้อื่นออกแทนให้</b>สำหรับเงินได้
/// ตาม ม.40 ⇒ ภาษีที่ออกแทนต้องบวกเข้าเงินได้แล้วหักภาษีจากยอดรวม · คำสั่งกรมสรรพากรที่ ป.7/2528 (การคำนวณเงินค่าภาษีที่ผู้จ่ายออกแทนให้) ·
/// แบบ 50 ทวิ ช่อง "ผู้จ่ายเงิน": (1) หัก ณ ที่จ่าย (2) ออกให้ตลอดไป (3) ออกให้ครั้งเดียว · ไฟล์ ภ.ง.ด.3/53 คอลัมน์เงื่อนไข 1/2/3</para>
///
/// <para>═══ สูตร (อัตราคงที่ r% — ภ.ง.ด.2/3/53/54 · <b>ไม่ใช้กับ ภ.ง.ด.1 ขั้นบันได</b>) ═══ ปัด 2 ตำแหน่ง AwayFromZero
/// <list type="bullet">
/// <item><b>หัก ณ ที่จ่าย</b>: ภาษี = ฐาน × r/100 · ออกแทน 0 · เงินได้ = ฐาน (พฤติกรรมเดิมทุกสตางค์)</item>
/// <item><b>ออกให้ตลอดไป</b> (ผู้รับได้ยอดที่ตกลงเต็ม — ภาษีที่ออกแทนเป็นเงินได้ซ้ำทุกรอบจนลู่เข้า): ภาษี = ฐาน × r/(100 − r) · ออกแทน = ภาษีทั้งก้อน ·
///   เงินได้ = ฐาน + ภาษี · ตัวอย่าง 10,000 @3% ⇒ ภาษี 309.28 · เงินได้ 10,309.28 · ผู้รับได้ 10,000.00</item>
/// <item><b>ออกให้ครั้งเดียว</b> (ผู้จ่ายออกภาษีรอบแรกให้ครั้งเดียว — ภาษีของภาษีที่ออกแทนผู้รับรับเอง): ออกแทน = ฐาน × r/100 ·
///   เงินได้ = ฐาน + ออกแทน · ภาษี = เงินได้ × r/100 · ตัวอย่าง 10,000 @3% ⇒ ออกแทน 300 · เงินได้ 10,300 · ภาษี 309 · ผู้รับได้ 9,991</item>
/// </list>
/// แหล่ง: ม.39 · ป.7/2528 (rd.go.th/3616.html) · ตัวอย่างตัวเลขตรงกับคู่มือโปรแกรมบัญชี PEAK "การออกหัก ณ ที่จ่ายแทนให้ครั้งเดียว" / "กรณีออกให้ตลอดไป"
/// และสูตรเดิมของรอบโอน settlement (<see cref="SettlementFeeTax.WhtOnBase"/> · ค่าธรรมเนียม 3/97 · ม.70 15/85) — รายละเอียดใน DECISIONS ข้อ 148</para>
///
/// <para>═══ ปัดเศษหลายบรรทัด ═══ <see cref="ForLines"/> คิดต่อกลุ่มอัตราจากฐานรวมของกลุ่ม (วิธีเดียวกับ <c>ReconcileTaxRounding</c> ของหัก ณ ที่จ่าย)
/// แล้วเกลี่ยเศษเข้าบรรทัดฐานใหญ่สุด ⇒ Σ บรรทัด = การคิดจากฐานรวม (ตรงกับเครื่องคิดเลข/50 ทวิ)</para>
/// </summary>
public static class WhtGrossUp
{
    public const string RuleCode = "RD-39-WHT-BORNE";
    public const string LegalReference = "ป.รัษฎากร ม.39 (ภาษีที่ผู้จ่ายออกแทนเป็นเงินได้) · คำสั่งกรมสรรพากรที่ ป.7/2528 · ท.ป.4/2528";

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ค่าที่ระบบเข้าใจ — ค่านอกชุด (0 · ค่าเก่าที่ไม่รู้จัก) ⇒ หัก ณ ที่จ่าย (พฤติกรรมเดิม)</summary>
    public static WithholdingTaxCertType Normalize(WithholdingTaxCertType type)
        => type is WithholdingTaxCertType.PayAlways or WithholdingTaxCertType.PayOnce ? type : WithholdingTaxCertType.Withhold;

    /// <summary>ผู้จ่ายออกภาษีแทน (ทั้งก้อนหรือบางส่วน) ไหม</summary>
    public static bool IsPayerBorne(WithholdingTaxCertType type) => Normalize(type) != WithholdingTaxCertType.Withhold;

    /// <summary>ป้ายไทยของเงื่อนไข (ตรงแบบ 50 ทวิ ทางการ)</summary>
    public static string Label(WithholdingTaxCertType type) => Normalize(type) switch
    {
        WithholdingTaxCertType.PayAlways => "ออกให้ตลอดไป",
        WithholdingTaxCertType.PayOnce => "ออกให้ครั้งเดียว",
        _ => "หัก ณ ที่จ่าย",
    };

    /// <summary>**คอลัมน์เงื่อนไขการหักภาษี (Col11) ของไฟล์ ภ.ง.ด. และรายงาน** — 1 หัก ณ ที่จ่าย · 2 ออกให้ตลอดไป · 3 ออกให้ครั้งเดียว ·
    /// ตัวเดียวของ TaxService/TaxFilingExportService (เดิมเขียน <c>is 2 ? 2 : 1</c> สี่ที่ ⇒ ใบ "ครั้งเดียว" ยื่นเป็นเงื่อนไข 1)</summary>
    public static int FilingCondition(WithholdingTaxCertType type) => (int)Normalize(type);

    /// <summary>ใบลดหนี้/ใบเพิ่มหนี้ — เงื่อนไขภาษีหัก ณ ที่จ่ายไม่ได้ตั้งเอง แต่<b>สืบทอดจากใบต้นทางฝั่งผู้จ่าย</b> (ฝ่ายค้าน P1-2 · รอบ 204):
    /// ใบลดหนี้ของใบ "ออกให้ตลอดไป" ต้องคิดภาษีแบบ gross-up และกลับขา Dr ส่วนที่ออกแทนด้วย — ไม่งั้น GL ค้าง AP/WHT/ค่าใช้จ่ายไม่ตรงกัน</summary>
    public static bool IsAdjustmentNote(Accounting.Models.Enums.DocumentType documentType)
        => documentType is Accounting.Models.Enums.DocumentType.CreditNote or Accounting.Models.Enums.DocumentType.DebitNote;

    /// <summary>ใบชนิดนี้ตั้ง "ผู้จ่ายออกภาษีแทน" ได้ไหม — null = ได้/เป็นค่าตั้งต้น · ข้อความ = เหตุผลภาษาไทย + ทางไปต่อให้ผู้เรียกโยน (400)
    /// <list type="bullet">
    /// <item>ใบฝั่งผู้จ่าย (<see cref="WhtCertIssuePlan.AppliesTo"/>) — ได้ <b>รวมบริการต่างประเทศ</b> (รอบ 204 คำสั่งเจ้าของ 2026-10-13 ·
    /// เดิมปฏิเสธเพราะ VAT ประเมินขาด — ตอนนี้ตอนบันทึกคิด VAT ภ.พ.36 บนฐานรวมภาษีที่ออกแทนด้วย <see cref="ForeignServiceVat.VatWithBorne"/>) ·
    /// <paramref name="isForeignService"/> คงไว้ให้จุดเรียกเดิม (ไม่มีผลกับคำตัดสินแล้ว)</item>
    /// <item>ใบลด/เพิ่มหนี้ (<see cref="IsAdjustmentNote"/>) — ได้เฉพาะเมื่อมีใบต้นทางฝั่งผู้จ่าย (<paramref name="hasPayerSideSource"/> · ค่าต้องตรงใบต้นทาง —
    /// <see cref="SourceMismatchReason"/>)</item>
    /// <item>ใบฝั่งรับเงิน — ไม่ได้ (ผู้จ่ายของเราเป็นคนตัดสินว่าจะออกภาษีแทนไหม ไม่ใช่เรา) · ทางไปต่อ: ลงยอดที่ gross-up แล้วด้วยแบบหัก ณ ที่จ่ายปกติ</item>
    /// </list></summary>
    public static string? TypeError(Accounting.Models.Enums.DocumentType documentType, WithholdingTaxCertType requested,
        bool isForeignService, bool hasPayerSideSource)
    {
        _ = isForeignService;   // รอบ 204: บริการต่างประเทศ + ออกแทนใช้ได้แล้ว (VAT ภ.พ.36 คิดบนฐานรวมภาษีที่ออกแทนตอนบันทึก)
        if (!IsPayerBorne(requested)) return null;
        if (WhtCertIssuePlan.AppliesTo(documentType)) return null;
        if (IsAdjustmentNote(documentType))
            return hasPayerSideSource ? null
                : $"ใบลดหนี้/ใบเพิ่มหนี้ตั้ง \"ผู้จ่ายออกภาษีแทน ({Label(requested)})\" เองไม่ได้ — เงื่อนไขสืบทอดจากใบต้นทางฝั่งผู้จ่าย "
                  + "(ใบแจ้งหนี้ซื้อ/บันทึกค่าใช้จ่าย/ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จ) เท่านั้น · อ้างใบต้นทาง หรือเลือก \"หัก ณ ที่จ่าย\"";
        var example = OnBase(10_000m, 3m, WithholdingTaxCertType.PayAlways);
        return $"\"ผู้จ่ายออกภาษีแทน ({Label(requested)})\" ใช้ได้เฉพาะใบสำคัญจ่าย/บันทึกค่าใช้จ่าย/ใบแจ้งหนี้ซื้อ/ใบรับรองแทนใบเสร็จ — "
              + "เอกสารฝั่งรับเงิน (ผู้จ่ายของเราเป็นผู้ออก 50 ทวิ ให้เรา) · ทางไปต่อ: ลงรายการด้วยยอดที่ gross-up แล้ว "
              + $"(10,000 @3% ⇒ {example.CertIncome:N2}) และใช้แบบหัก ณ ที่จ่ายปกติ";
    }

    /// <summary>**ธงระดับเอกสารที่มีความหมายเฉพาะใบฝั่งผู้จ่าย** (รอบ 204 — "ผู้จ่ายรับภาระตามสัญญา" <c>Document.WhtBorneByContract</c> ·
    /// "หักผ่าน e-Withholding ของธนาคาร" <c>Document.WhtViaEWithholding</c>) — ค่าที่เก็บตอนสร้าง: ไม่ใช่ใบฝั่งผู้จ่าย ⇒ false เสมอ (ไม่มีผล ·
    /// หน้าเว็บซ่อนช่อง) · ส่งค่า ⇒ ค่านั้น · ไม่ส่ง ⇒ <paramref name="fallback"/> (สัญญา = ค่าตั้งต้นของผู้ถูกหัก · e-Withholding = false)</summary>
    public static bool PayerSideFlagOnCreate(bool? requested, bool fallback, Accounting.Models.Enums.DocumentType documentType)
        => WhtCertIssuePlan.AppliesTo(documentType) && (requested ?? fallback);

    /// <summary>ค่าที่เก็บตอนแก้ไขใบ — ไม่ส่ง (null) ⇒ คงค่าเดิม · ไม่ใช่ใบฝั่งผู้จ่าย ⇒ false</summary>
    public static bool PayerSideFlagOnUpdate(bool? requested, bool current, Accounting.Models.Enums.DocumentType documentType)
        => WhtCertIssuePlan.AppliesTo(documentType) && (requested ?? current);

    /// <summary>**เอกสารลูกสืบทอดธงฝั่งผู้จ่าย** (แปลง · คัดลอก · รายการประจำ — กฎ #4 A) แบบเดียวกับ <see cref="Inherit"/>: ฝั่งผู้จ่าย → ฝั่งผู้จ่าย
    /// ถือค่าของต้นทาง (true/false ตามจริง) · ข้ามฝั่ง ⇒ null (ใบปลายทางตัดสินเอง — สัญญา = ค่าตั้งต้นของผู้ถูกหัก)</summary>
    public static bool? InheritPayerSideFlag(bool sourceFlag,
        Accounting.Models.Enums.DocumentType sourceDocumentType, Accounting.Models.Enums.DocumentType targetDocumentType)
        => WhtCertIssuePlan.AppliesTo(sourceDocumentType) && WhtCertIssuePlan.AppliesTo(targetDocumentType) ? sourceFlag : null;

    /// <summary>**เอกสารลูกสืบทอด** (แปลง · คัดลอก · รายการประจำ — กฎ #4 A): ใบฝั่งผู้จ่าย → ใบฝั่งผู้จ่าย (PI/ค่าใช้จ่าย → ใบสำคัญจ่าย) ถือเงื่อนไขเดียวกับต้นทาง
    /// (ใบที่จ่ายจริง/ออก 50 ทวิ ต้องคิดภาษีแบบเดียวกับที่ตั้งหนี้ไว้ ไม่งั้นยอดจ่ายไม่ตรงเจ้าหนี้) · ข้ามฝั่ง/ใบปลายทางฝั่งรับเงิน ⇒ null (= หัก ณ ที่จ่าย)</summary>
    public static WithholdingTaxCertType? Inherit(WithholdingTaxCertType sourceType,
        Accounting.Models.Enums.DocumentType sourceDocumentType, Accounting.Models.Enums.DocumentType targetDocumentType)
        => IsPayerBorne(sourceType) && WhtCertIssuePlan.AppliesTo(sourceDocumentType) && WhtCertIssuePlan.AppliesTo(targetDocumentType)
            ? Normalize(sourceType)
            : null;

    /// <summary>ใบที่ปิดหนี้ใบต้นทางฝั่งผู้จ่าย (ใบสำคัญจ่ายของ PI/ค่าใช้จ่าย) ต้องถือเงื่อนไขเดียวกับใบต้นทาง — ไม่งั้นยอดจ่าย (TotalAmount) ≠ เจ้าหนี้ที่ใบต้นทางตั้ง
    /// (ตัดเจ้าหนี้ไม่หมด/เกิน) · null = ตรง · ข้อความ = ทางไปต่อ (แก้ที่ใบต้นทาง)</summary>
    public static string? SourceMismatchReason(WithholdingTaxCertType sourceType, WithholdingTaxCertType childType, string? sourceNumber)
        => Normalize(sourceType) == Normalize(childType)
            ? null
            : $"ใบต้นทาง {sourceNumber} ตั้งเงื่อนไขภาษีหัก ณ ที่จ่ายเป็น \"{Label(sourceType)}\" — ใบที่จ่ายชำระต้องเป็นเงื่อนไขเดียวกัน "
              + $"(เลือก \"{Label(childType)}\" ไม่ได้ เพราะยอดจ่ายจะไม่ตรงเจ้าหนี้ที่ตั้งไว้) · ถ้าต้องการเปลี่ยน ให้แก้ที่ใบต้นทางก่อน";

    /// <summary>ภาษีของฐานหนึ่งก้อน (อัตราเป็นเปอร์เซ็นต์ เช่น 3) — ฐาน/อัตรา ≤ 0 หรืออัตรา ≥ 100 ⇒ ศูนย์ (ฐานเป็นเงินได้)</summary>
    public static WhtGrossUpAmounts OnBase(decimal baseAmount, decimal ratePercent, WithholdingTaxCertType type)
    {
        if (ratePercent <= 0m || ratePercent >= 100m || baseAmount == 0m) return new(0m, 0m, baseAmount);
        switch (Normalize(type))
        {
            case WithholdingTaxCertType.PayAlways:
            {
                var tax = R(baseAmount * ratePercent / (100m - ratePercent));
                return new(tax, tax, baseAmount + tax);
            }
            case WithholdingTaxCertType.PayOnce:
            {
                var borne = R(baseAmount * ratePercent / 100m);
                var income = baseAmount + borne;
                return new(R(income * ratePercent / 100m), borne, income);
            }
            default:
                return new(R(baseAmount * ratePercent / 100m), 0m, baseAmount);
        }
    }

    /// <summary>
    /// ภาษีรายบรรทัดของเอกสาร (ฐาน = ยอดบรรทัดก่อน VAT · อัตรา = <c>WithholdingTaxRate</c>) — คิดต่อกลุ่มอัตราจากฐานรวม แล้วเกลี่ยเศษเข้าบรรทัดฐานใหญ่สุด ·
    /// ลำดับผลลัพธ์ตรงกับลำดับบรรทัดที่ส่งเข้า · บรรทัดอัตรา 0 ⇒ (0, 0, ฐาน)
    /// </summary>
    public static IReadOnlyList<WhtGrossUpAmounts> ForLines(IReadOnlyList<(decimal Base, decimal RatePercent)> lines, WithholdingTaxCertType type)
    {
        var result = new WhtGrossUpAmounts[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            result[i] = OnBase(lines[i].Base, lines[i].RatePercent, type);

        foreach (var g in Enumerable.Range(0, lines.Count)
                     .Where(i => lines[i].RatePercent > 0m && lines[i].RatePercent < 100m && lines[i].Base != 0m)
                     .GroupBy(i => lines[i].RatePercent))
        {
            var idx = g.ToList();
            if (idx.Count < 2) continue;
            var target = OnBase(idx.Sum(i => lines[i].Base), g.Key, type);
            var taxDiff = target.Tax - idx.Sum(i => result[i].Tax);
            var borneDiff = target.PayerBorne - idx.Sum(i => result[i].PayerBorne);
            if (taxDiff == 0m && borneDiff == 0m) continue;
            var j = idx.OrderByDescending(i => Math.Abs(lines[i].Base)).ThenBy(i => i).First();
            var borne = result[j].PayerBorne + borneDiff;
            result[j] = new WhtGrossUpAmounts(result[j].Tax + taxDiff, borne, lines[j].Base + borne);
        }
        return result;
    }

    /// <summary>ยอดรวมของเอกสาร (Σ บรรทัด) — ภาษีนำส่ง · ส่วนที่ผู้จ่ายออกแทน</summary>
    public static (decimal Tax, decimal PayerBorne) Totals(IReadOnlyList<WhtGrossUpAmounts> perLine)
        => (perLine.Sum(a => a.Tax), perLine.Sum(a => a.PayerBorne));

    /// <summary>ยอดจ่ายผู้รับ (ยอดสุทธิของเอกสาร) = ฐาน + VAT − ภาษีนำส่ง + ส่วนที่ผู้จ่ายออกแทน · หัก ณ ที่จ่าย ⇒ สูตรเดิม (ฐาน + VAT − ภาษี)</summary>
    public static decimal PayeeTotal(decimal subTotal, decimal vat, decimal tax, decimal payerBorne)
        => subTotal + vat - tax + payerBorne;

    /// <summary>ข้อความเหตุผลเมื่อยอดที่คิดใหม่จากบรรทัดที่บันทึก (ภาษีนำส่ง · ส่วนที่ออกแทน) ไม่ตรงยอดบนหัวเอกสาร — null = ตรง ·
    /// ผู้เรียก (ลงบัญชี/ออก 50 ทวิ) ต้อง<b>ล้มดัง</b> ไม่ใช้ค่าใดค่าหนึ่งเงียบ ๆ (บรรทัด/ยอดถูกเขียนทับทางอื่นหลังบันทึก เช่นซิงค์ภายนอกคิดยอดแบบหัก ณ ที่จ่าย)</summary>
    public static string? MismatchReason(string? documentNumber, decimal storedTax, decimal storedPayerBorne, IReadOnlyList<WhtGrossUpAmounts> perLine)
    {
        var (tax, borne) = Totals(perLine);
        if (Math.Abs(borne - storedPayerBorne) <= 0.005m && Math.Abs(tax - storedTax) <= 0.005m) return null;
        return $"ภาษีหัก ณ ที่จ่ายบนเอกสาร {documentNumber} (นำส่ง {storedTax:N2} · ผู้จ่ายออกแทน {storedPayerBorne:N2}) ไม่ตรงกับที่คิดจากรายการ "
            + $"(นำส่ง {tax:N2} · ออกแทน {borne:N2}) — เปิดแก้ไขเอกสารแล้วบันทึกอีกครั้งเพื่อคำนวณใหม่ก่อนอนุมัติ/ออก 50 ทวิ";
    }
}
