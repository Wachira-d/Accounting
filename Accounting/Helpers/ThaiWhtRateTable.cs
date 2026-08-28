namespace Accounting.Helpers;

/// <summary>
/// ตารางประเภทเงินได้ + อัตราหัก ณ ที่จ่าย ตาม ท.ป.4/2528 (+ §3 เตรส)
/// — <b>ตัวอ้างอิงกลางตัวเดียวของระบบ</b>
///
/// ═══ ทำไมต้องมี ═══
/// ผลตรวจพบว่าคำถามเดียวกันถูกตอบด้วยตารางคนละชุด <b>2 ที่</b> และไม่ตรงกัน
/// ทั้งคู่ยังไม่ตรงกับกฎหมายด้วย:
/// <list type="bullet">
/// <item><c>WithholdingTaxCertController.GetIncomeTypes</c> — 40(3) ค่าสิทธิ = <b>5%</b>
///   (กฎหมายคือ 3%) และ 40(1) เงินเดือน = <b>3% แบบคงที่</b> ทั้งที่กฎหมายใช้
///   <b>อัตราขั้นบันได</b></item>
/// <item><c>wht-credit.html</c> dropdown — 40(4) รวม "ดอกเบี้ย/เงินปันผล" เป็นตัวเลือกเดียว
///   ที่ <b>1%</b> ทั้งที่ดอกเบี้ยบุคคล = 15% · ดอกเบี้ยนิติบุคคล = 1% · ปันผล = 10%</item>
/// </list>
/// dropdown เป็นคำแนะนำเดียวที่ผู้ใช้เห็นตอนกรอกอัตรา ⇒ ป้ายผิด = ยอดหักผิด
/// ตั้งแต่ต้นทาง แล้วไหลไป ภ.ง.ด.3/53 และเครดิต CIT ทั้งสาย
///
/// ═══ กติกา ═══
/// อัตราของผู้รับที่เป็น <b>บุคคลธรรมดา</b> กับ <b>นิติบุคคล</b> ต่างกันได้ จึงเก็บ
/// แยกสองช่อง. ประเภทที่กฎหมายไม่ได้กำหนดอัตราคงที่ (เงินเดือน = ขั้นบันได)
/// เก็บ <c>null</c> — <b>ห้ามใส่ตัวเลขปลอมเพื่อให้ช่องไม่ว่าง</b>
/// </summary>
public static class ThaiWhtRateTable
{
    /// <param name="IndividualRate">อัตราเมื่อผู้รับเป็นบุคคลธรรมดา (%) — null = ไม่มีอัตราคงที่</param>
    /// <param name="JuristicRate">อัตราเมื่อผู้รับเป็นนิติบุคคลไทย (%) — null = ไม่ใช้กับนิติบุคคล</param>
    public sealed record IncomeType(
        string Code,
        string Name,
        string TaxSection,
        decimal? IndividualRate,
        decimal? JuristicRate,
        IReadOnlyList<string> ApplicableForms,
        string? Note = null);

    /// <summary>ทุกประเภทที่ระบบรองรับ เรียงตามลำดับมาตรา</summary>
    public static readonly IReadOnlyList<IncomeType> All = new[]
    {
        new IncomeType("1", "เงินเดือน ค่าจ้าง บำนาญ", "40(1)",
            IndividualRate: null, JuristicRate: null,
            new[] { "ภ.ง.ด.1" },
            Note: "อัตราขั้นบันไดตามฐานภาษีเงินได้บุคคลธรรมดา — ไม่มีอัตราคงที่"),
        new IncomeType("2", "ค่านายหน้า/ค่าบริการ", "40(2)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("3", "ค่าแห่งลิขสิทธิ์/ค่าสิทธิ", "40(3)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("4a", "ดอกเบี้ย", "40(4)(ก)",
            IndividualRate: 15m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" },
            Note: "บุคคลธรรมดา 15% · นิติบุคคลไทย 1%"),
        new IncomeType("4b", "เงินปันผล", "40(4)(ข)",
            IndividualRate: 10m, JuristicRate: 10m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" }),
        new IncomeType("5", "ค่าเช่าทรัพย์สิน", "40(5)",
            IndividualRate: 5m, JuristicRate: 5m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("6", "ค่าวิชาชีพอิสระ", "40(6)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("7", "ค่ารับเหมา", "40(7)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8", "ค่าจ้างทำของ/ค่าบริการอื่น", "40(8)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8ad", "ค่าโฆษณา", "40(8)",
            IndividualRate: 2m, JuristicRate: 2m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8tr", "ค่าขนส่ง (ไม่ใช่ขนส่งสาธารณะ)", "40(8)",
            IndividualRate: 1m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
    };

    /// <summary>หาตามรหัส หรือตามมาตรา ("40(5)") — คืน null เมื่อไม่รู้จัก</summary>
    public static IncomeType? Find(string? codeOrSection)
    {
        if (string.IsNullOrWhiteSpace(codeOrSection)) return null;
        var key = codeOrSection.Trim();
        return All.FirstOrDefault(t =>
                   string.Equals(t.Code, key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(t =>
                   string.Equals(t.TaxSection, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>อัตราที่ควรใช้กับผู้รับรายนี้ — null = ไม่มีอัตราคงที่ (ต้องให้คนกรอก)</summary>
    public static decimal? RateFor(string? codeOrSection, bool payeeIsJuristic)
    {
        var t = Find(codeOrSection);
        if (t == null) return null;
        return payeeIsJuristic ? t.JuristicRate : t.IndividualRate;
    }

    /// <summary>
    /// ด่าน ฿1,000 (ท.ป.4/2528 ข้อ 12) — <b>สะสมต่อคู่สัญญา ไม่ใช่ต่อบรรทัด</b>
    ///
    /// <para>จ่ายงวดละ 800 สามงวดในสัญญาเดียวกัน ต้องหักตั้งแต่งวดที่ยอดสะสม
    /// ถึง 1,000 — ระบบที่ดูยอดต่อบรรทัดอย่างเดียวจะไม่หักเลยทั้งสามงวด</para>
    /// </summary>
    public const decimal MinimumThresholdBaht = 1000m;

    /// <summary>ต้องหักภาษีสำหรับการจ่ายครั้งนี้หรือไม่</summary>
    /// <param name="thisPaymentAmount">ยอดจ่ายงวดนี้</param>
    /// <param name="alreadyPaidUnderSameContract">ยอดที่จ่ายไปแล้วภายใต้สัญญา/คู่สัญญาเดียวกันในปีภาษีนี้</param>
    /// <param name="contractTotalKnown">รู้ยอดสัญญาทั้งก้อนแล้วและยอดนั้น ≥ 1,000</param>
    public static bool ShouldWithhold(
        decimal thisPaymentAmount,
        decimal alreadyPaidUnderSameContract = 0m,
        bool contractTotalKnown = false)
    {
        if (contractTotalKnown) return true;
        return thisPaymentAmount + alreadyPaidUnderSameContract >= MinimumThresholdBaht;
    }
}
