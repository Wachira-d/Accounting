namespace Accounting.Helpers;

/// <summary>บรรทัดหนึ่งของเอกสารในสายตาตัวคำนวณหัก ณ ที่จ่ายรายบรรทัด — ยอดทุกช่องเป็น<b>สกุลเอกสาร</b></summary>
/// <param name="Amount">ฐานก่อน VAT ของบรรทัด (<c>DocumentLine.Amount</c> — หลังส่วนลด)</param>
/// <param name="RatePercent">อัตราหักของบรรทัด (<c>DocumentLine.WithholdingTaxRate</c>)</param>
/// <param name="WhtAmount">ภาษีหักที่เก็บไว้บนบรรทัด (<c>DocumentLine.WithholdingTaxAmount</c> — ผ่านการกระทบเศษ/ภาษีออกแทนมาแล้ว)</param>
/// <param name="IncomeTypeCode">ประเภทเงินได้ ม.40 ของบรรทัด (null = ไม่ระบุ)</param>
/// <param name="PayerBorne">ส่วนของภาษีที่ผู้จ่ายออกแทนของบรรทัด (<c>WhtGrossUp.ForLines</c> · หัก ณ ที่จ่าย = 0)</param>
public readonly record struct PerLineWhtLine(decimal Amount, decimal RatePercent, decimal WhtAmount, string? IncomeTypeCode,
    decimal PayerBorne = 0m);

/// <summary>ผลต่อบรรทัดเป็น<b>บาท</b> — <see cref="IncomeThb"/> = เงินได้บน 50 ทวิ (ฐาน + ภาษีที่ออกแทน · ม.39)</summary>
public readonly record struct PerLineWhtBaht(int Index, string? IncomeTypeCode, decimal RatePercent,
    decimal BaseThb, decimal PayerBorneThb, decimal WhtThb)
{
    public decimal IncomeThb => BaseThb + PayerBorneThb;
}

/// <summary>ยอดรวมต่อ (ประเภทเงินได้, อัตรา) — แถวของ 50 ทวิ / ภ.ง.ด. / สรุปบนเอกสาร</summary>
public readonly record struct WhtIncomeGroup(string IncomeTypeCode, decimal RatePercent, decimal IncomeThb, decimal WhtThb, int LineCount);

/// <summary>ที่มาของฐานบาท</summary>
public enum WhtBahtBasis
{
    /// <summary>เอกสารบาท (อัตรา 1 · ไม่มียอดบาทพิมพ์) — ยอดคือยอดที่เก็บไว้ทุกสตางค์ (พฤติกรรมเดิม)</summary>
    Thb = 0,
    /// <summary>แปลงด้วย <c>Document.ExchangeRate</c> (ตัวแปลงเดียวกับ JE — <see cref="DocumentFx.ToBaht"/>)</summary>
    ExchangeRate = 1,
    /// <summary>ยอดบาทที่เอกสารพิมพ์ไว้เอง (<c>Document.DocumentThbBase</c>) เฉลี่ยลงบรรทัดตามสัดส่วน — รวมแล้วเท่ายอดบนกระดาษพอดี</summary>
    PrintedThb = 2,
}

/// <summary>
/// **หัก ณ ที่จ่ายรายบรรทัด — ตัวคำนวณกลางตัวเดียว** (ทีม F · <c>docs/PER_LINE_WHT_DESIGN.md</c>)
///
/// <para>═══ ทำไมต้องมี ═══ ใบเดียวมีได้หลายประเภทเงินได้/หลายอัตรา (ค่าบริการ 3% + ค่าโฆษณา 2% + ค่าสินค้า 0%) — ยอดต่อบรรทัดอยู่บน
/// <c>DocumentLine</c> แล้ว (สูตร ComputeLineAmounts + กระทบเศษต่อกลุ่มอัตรา) แต่<b>ทุกผู้อ่านที่ส่งยอดออกไปเป็นแบบภาษี</b> (50 ทวิ · แถว ภ.ง.ด. ·
/// สรุปบนเอกสาร) ใช้ยอด<b>สกุลเอกสาร</b>ตรง ๆ ⇒ ใบ USD ที่อัตรา 36 ออก 50 ทวิ "เงินได้ 1,000 บาท ภาษี 30 บาท" แทน 36,000 / 1,080 —
/// แบบยื่นต้องเป็นบาทเสมอ (ป.รัษฎากร ม.3 เตรส · ท.ป.4/2528 · ม.70 คิดเป็นเงินตราไทย)</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item><b>ฐาน = ก่อน VAT</b> (VAT ไม่ใช่เงินได้ของผู้รับ · ท.ป.4/2528 ข้อ 12) = <c>DocumentLine.Amount</c></item>
/// <item><b>ฐานบาท</b>: เอกสารพิมพ์ยอดบาทไว้แล้ว (<c>DocumentThbBase</c>) ⇒ ยึดยอดนั้น (เฉลี่ยลงบรรทัดตามสัดส่วน · เศษไปบรรทัดยอดสูงสุด ⇒ Σ = ยอดบนกระดาษพอดี) ·
///   ไม่มี ⇒ <see cref="DocumentFx.ToBaht"/> ด้วยอัตราของเอกสาร (ตัวเดียวกับ JE) · บาท ⇒ ยอดเดิมทุกสตางค์</item>
/// <item><b>ภาษีบาท</b>: หัก ณ ที่จ่ายปกติ ⇒ <c>round(ฐานบาท × อัตรา, 2, AwayFromZero)</c> (นิยามตามกฎหมาย — ไม่ใช่แปลงภาษีสกุลเอกสารที่ปัดมาแล้ว) ·
///   ผู้จ่ายออกภาษีแทน ⇒ แปลงยอดที่เก็บไว้ตามสัดส่วนฐานบาท/ฐานสกุลเอกสาร (สูตรออกแทนคิดบนสกุลเอกสารมาแล้วใน <c>WhtGrossUp.ForLines</c>)</item>
/// <item><b>เอกสารบาท</b> ⇒ คืนยอดที่เก็บไว้ตรงตัว (ไม่คำนวณใหม่) — ปิด/เปิดฟีเจอร์ก็ไม่มีสตางค์ไหนขยับ</item>
/// <item><b>เกณฑ์ 1,000 บาท</b> ดู<b>ทั้งการจ่าย/ทั้งสัญญา</b> ไม่ใช่ต่อบรรทัด — ตัวคำนวณนี้<b>ไม่ตัดสินเกณฑ์ใหม่ต่อบรรทัด</b> (บรรทัดค่าโฆษณา 600 ในใบที่หักอยู่แล้ว
///   ก็หัก) · ด่านเกณฑ์อยู่ที่ระดับใบ/คู่สัญญาตัวเดิม (<see cref="ThaiWhtRateTable.ShouldWithhold"/> · <c>WhtCumulativeScope</c> · <c>OcrWhtThresholdBase</c>)</item>
/// </list></para>
/// </summary>
public static class PerLineWht
{
    /// <summary>รหัสประเภทเงินได้เมื่อบรรทัดหักแต่ไม่ระบุ — ตัวเดียวกับที่ 50 ทวิ ใช้มาตลอด (<c>line.IncomeTypeCode ?? "8"</c>)</summary>
    public const string DefaultIncomeTypeCode = "8";

    public const string RuleCode = "RD-3TER-PER-LINE-WHT";
    public const string LegalReference = "ป.รัษฎากร ม.3 เตรส · ม.50 · ม.70 · ท.ป.4/2528 ข้อ 12 (ฐานก่อน VAT · เกณฑ์ 1,000 บาทต่อการจ่าย)";

    /// <summary>ที่มาของฐานบาทของเอกสาร — ยอดบาทที่พิมพ์ชนะอัตรา · อัตรา ≤ 0 (ข้อมูลเก่า) ถือเป็น 1 เหมือน <see cref="DocumentFx.ToBaht"/></summary>
    public static WhtBahtBasis BasisOf(decimal exchangeRate, decimal? documentThbBase)
    {
        if (documentThbBase is > 0m) return WhtBahtBasis.PrintedThb;
        var fx = exchangeRate <= 0m ? 1m : exchangeRate;
        return fx == 1m ? WhtBahtBasis.Thb : WhtBahtBasis.ExchangeRate;
    }

    /// <summary>ฐานบาทต่อบรรทัด · ยอดบาทพิมพ์ + Σ ฐานสกุลเอกสาร = 0 (เฉลี่ยไม่ได้) ⇒ ตกไปใช้อัตรา</summary>
    private static IReadOnlyList<decimal> BahtBases(IReadOnlyList<decimal> lineAmounts, decimal exchangeRate, decimal? documentThbBase)
    {
        var sum = lineAmounts.Sum();
        if (documentThbBase is decimal printed && printed > 0m && sum != 0m)
        {
            var bases = lineAmounts
                .Select(a => Math.Round(printed * a / sum, 2, MidpointRounding.AwayFromZero)).ToArray();
            var diff = printed - bases.Sum();
            if (diff != 0m && bases.Length > 0)
            {
                // เศษสตางค์ไปบรรทัดที่ยอดสูงสุด (กำหนดแน่นอน — ไม่ขึ้นกับลำดับ response)
                var idx = 0;
                for (var i = 1; i < lineAmounts.Count; i++)
                    if (Math.Abs(lineAmounts[i]) > Math.Abs(lineAmounts[idx])) idx = i;
                bases[idx] += diff;
            }
            return bases;
        }
        return lineAmounts.Select(a => DocumentFx.ToBaht(a, exchangeRate)).ToArray();
    }

    /// <summary>ยอดบาทต่อบรรทัด (เงินได้ · ภาษีที่ออกแทน · ภาษีหัก) — ดูกติกาที่ตัวคลาส</summary>
    public static IReadOnlyList<PerLineWhtBaht> ToBaht(IReadOnlyList<PerLineWhtLine> lines, decimal exchangeRate, decimal? documentThbBase)
    {
        var basis = BasisOf(exchangeRate, documentThbBase);
        var result = new PerLineWhtBaht[lines.Count];
        if (basis == WhtBahtBasis.Thb)
        {
            // เอกสารบาท = ยอดที่เก็บไว้ตรงตัว (พฤติกรรมเดิม — ไม่คำนวณใหม่แม้แต่สตางค์เดียว)
            for (var i = 0; i < lines.Count; i++)
                result[i] = new(i, lines[i].IncomeTypeCode, lines[i].RatePercent, lines[i].Amount, lines[i].PayerBorne, lines[i].WhtAmount);
            return result;
        }
        var bases = BahtBases(lines.Select(l => l.Amount).ToList(), exchangeRate, documentThbBase);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var baseThb = bases[i];
            decimal Scale(decimal docAmount) => l.Amount != 0m
                ? Math.Round(docAmount * baseThb / l.Amount, 2, MidpointRounding.AwayFromZero)
                : DocumentFx.ToBaht(docAmount, exchangeRate);
            var borneThb = l.PayerBorne == 0m ? 0m : Scale(l.PayerBorne);
            decimal whtThb;
            if (l.WhtAmount == 0m) whtThb = 0m;
            else if (l.PayerBorne == 0m && l.RatePercent > 0m)
                whtThb = Math.Round(baseThb * l.RatePercent / 100m, 2, MidpointRounding.AwayFromZero);   // นิยามตามกฎหมาย: ฐานบาท × อัตรา
            else whtThb = Scale(l.WhtAmount);   // ออกแทน/ยอดที่ไม่มีอัตรา — คงสัดส่วนของยอดที่เก็บไว้
            result[i] = new(i, l.IncomeTypeCode, l.RatePercent, baseThb, borneThb, whtThb);
        }
        return result;
    }

    /// <summary>รวมต่อ (ประเภทเงินได้, อัตรา) เฉพาะบรรทัดที่มีภาษีหัก · บรรทัดหักไม่ระบุประเภท = <see cref="DefaultIncomeTypeCode"/> ·
    /// เรียงตามรหัสแล้วอัตรา (ผลไม่ขึ้นกับลำดับบรรทัด)</summary>
    private static IReadOnlyList<WhtIncomeGroup> GroupByIncomeType(IEnumerable<PerLineWhtBaht> lines)
        => lines.Where(l => l.WhtThb != 0m)
            .GroupBy(l => (Code: string.IsNullOrWhiteSpace(l.IncomeTypeCode) ? DefaultIncomeTypeCode : l.IncomeTypeCode!.Trim(), l.RatePercent))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal).ThenBy(g => g.Key.RatePercent)
            .Select(g => new WhtIncomeGroup(g.Key.Code, g.Key.RatePercent, g.Sum(x => x.IncomeThb), g.Sum(x => x.WhtThb), g.Count()))
            .ToList();

    /// <summary>ยอดบาทที่พิมพ์ยังใช้เป็นฐานได้ไหม — ฐานสกุลเอกสาร × อัตรา (ตัวที่ JE/ภ.พ.30 ใช้) ต้องได้ยอดนั้นกลับมา (± <see cref="DocumentFx.PrintedBahtRoundTripTolerance"/>) ·
    /// ฝ่ายค้านทีม F ข้อ 1: เดิมยอม 1 บาท/0.1% ⇒ สกุลหน่วยเล็ก (VND) อัตรา 6 ตำแหน่งแปลงกลับต่าง 24.69 บาท แต่ 50 ทวิ ยังยึดยอดพิมพ์ ⇒ 50 ทวิ ≠ GL/ภ.พ.30 ·
    /// ผู้ใช้แก้บรรทัดจนฐานเปลี่ยน (อัตราตรึงตั้งแต่สร้าง) ⇒ ไม่ผ่านเช่นกัน ⇒ ใช้อัตรา (ไม่เฉลี่ยยอดเก่าลงบรรทัดใหม่)</summary>
    private static bool PrintedBaseUsable(decimal? documentThbBase, decimal docSubTotal, decimal exchangeRate)
        => documentThbBase is decimal printed && printed > 0m && docSubTotal > 0m
           && DocumentFx.PrintedBahtRoundTrips(docSubTotal, exchangeRate, printed);

    /// <summary>ภาษีหักของทั้งเอกสาร/งวดเป็นบาท = ยอดที่ JE ลงผัง 2191x (ภาษีสกุลเอกสาร × อัตราของเอกสาร) — <b>เป้าเดียว</b>ที่ 50 ทวิ ·
    /// สรุปต่อประเภท · แถวเตือน ภ.ง.ด. · หน้านำส่ง/ปฏิทิน · ตัวกระทบ GL ผูกยอดเข้าหา (ห้ามเขียน <c>WithholdingTaxAmount</c> สกุลเอกสารตรง ๆ ลงที่เป็นบาท)</summary>
    public static decimal DocumentWhtThb(decimal docCurrencyWht, decimal exchangeRate) => DocumentFx.ToBaht(docCurrencyWht, exchangeRate);

    /// <summary>งวดจ่ายของเอกสาร (ภาษีหักสกุลเอกสารของงวด · อัตราวันจ่ายของงวด) — ข้อมูลเข้าของ <see cref="UncertifiedWhtThb"/></summary>
    public readonly record struct WhtPaymentSlice(decimal WhtAmount, decimal? ExchangeRate);

    /// <summary>
    /// รอบ 208 (ฝ่ายค้าน W7 ข้อ 8): ภาษีหักบาทของเอกสาร<b>ที่ยังไม่มี 50 ทวิ</b> (แถวช่องโหว่หน้านำส่ง/ปฏิทิน) — เป้าเดียวกับ 50 ทวิ ที่จะออก:
    /// แต่ละงวดจ่ายแปลงด้วย <see cref="DocumentFx.WhtRateFor"/> ของงวดนั้น (ตัวเดียวกับ 50 ทวิ รายงวด + ขา 2191x) · ส่วนที่ยังไม่จ่ายแปลงด้วยอัตราเอกสาร ·
    /// <paramref name="whtPart"/> = ส่วนของผู้ถูกหักรายหนึ่ง (ใบหลายผู้ถูกหัก) ⇒ สัดส่วนของยอดบาททั้งใบ ·
    /// <b>ทุกงวดได้อัตราเอกสาร (ค่าตั้งต้น · Accrual · ไม่มีอัตราวันจ่าย) ⇒ <see cref="DocumentWhtThb"/>(ส่วนนั้น, อัตราเอกสาร) เดิมทุกสตางค์</b> · ใบบาท ⇒ ยอดเดิม
    /// </summary>
    public static decimal UncertifiedWhtThb(decimal whtPart, decimal documentWht, decimal documentRate,
        IReadOnlyList<WhtPaymentSlice> payments, Models.Enums.WhtFxRateBasis basis, Models.Enums.WhtRecognitionBasis recognition)
    {
        var docFx = documentRate <= 0m ? 1m : documentRate;
        if (docFx == 1m) return whtPart;
        var paid = payments.Where(p => p.WhtAmount > 0m).ToList();
        if (paid.All(p => DocumentFx.WhtRateFor(basis, recognition, docFx, p.ExchangeRate) == docFx))
            return DocumentWhtThb(whtPart, docFx);
        var paidWht = paid.Sum(p => p.WhtAmount);
        var totalThb = paid.Sum(p => DocumentWhtThb(p.WhtAmount, DocumentFx.WhtRateFor(basis, recognition, docFx, p.ExchangeRate)))
            + DocumentWhtThb(Math.Max(0m, documentWht - paidWht), docFx);
        if (whtPart == documentWht || documentWht <= 0m) return totalThb;
        return Math.Round(totalThb * whtPart / documentWht, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// รอบ 208 (ฝ่ายค้าน W7 ข้อ 9 · R-18): อัตราที่ใช้แปลงยอดของ<b>ใบลดหนี้</b>เพื่อเทียบ/ลด 50 ทวิ ที่ออกแล้ว — ต้องเป็นอัตราที่ใบรับรองใช้จริง
    /// (<c>WithholdingTaxCert.ExchangeRateUsed</c> ทุกใบที่ไม่ยกเลิกเป็นค่าเดียวกัน) ไม่ใช่อัตราของใบลดหนี้เอง (คนละวัน ⇒ 50 ทวิ ใหม่ไม่เท่ายอดเดิม − ส่วนลด) ·
    /// ใบรับรองไม่บอกอัตรา (ใบเก่า) / หลายอัตรา ⇒ อัตราใบต้นทาง · ใบลดหนี้สกุลบาท ⇒ 1 เสมอ (ไม่คูณบาทด้วยอัตรา)
    /// </summary>
    public static decimal CreditNoteWhtRate(decimal creditNoteRate, decimal sourceRate, IEnumerable<decimal?> certRates)
    {
        var cnFx = creditNoteRate <= 0m ? 1m : creditNoteRate;
        if (cnFx == 1m) return 1m;
        var distinct = certRates.Where(r => r is > 0m).Select(r => r!.Value).Distinct().ToList();
        if (distinct.Count == 1 && distinct[0] != 1m) return distinct[0];
        var srcFx = sourceRate <= 0m ? 1m : sourceRate;
        return srcFx == 1m ? cnFx : srcFx;
    }

    /// <summary>ฐานบาทของเอกสารที่ใช้จริง (ยอดบาทที่พิมพ์เมื่อยังใช้ได้ · ไม่งั้น null)</summary>
    public static decimal? UsablePrintedBase(Accounting.Models.Entities.Document doc)
        => PrintedBaseUsable(doc.DocumentThbBase, doc.SubTotal, doc.ExchangeRate) ? doc.DocumentThbBase : null;

    /// <summary>ที่มาของฐานบาทของเอกสาร (ตามที่ <see cref="ForDocument"/> ใช้) · <paramref name="whtRateOverride"/> = อัตราฐานภาษีของงวด
    /// (<see cref="DocumentFx.WhtRateFor"/> · คำตัดสินข้อ 162) — ต่างจากอัตราเอกสาร ⇒ ยอดบาทที่พิมพ์ (อัตราวันใบ) ไม่ใช่ฐานอีกต่อไป</summary>
    public static WhtBahtBasis BasisOf(Accounting.Models.Entities.Document doc, decimal? whtRateOverride = null)
        => BasisOf(EffectiveRate(doc, whtRateOverride), EffectivePrintedBase(doc, whtRateOverride));

    /// <summary>อัตราที่ใช้จริง — override ที่ส่งมา (&gt; 0) ชนะอัตราเอกสาร · null/≤ 0 = อัตราเอกสาร (เดิม) · <b>ใบบาท (อัตรา 1/ไม่มี) ไม่รับ override</b>
    /// (ใบบาทต้องคืนยอดที่เก็บไว้ตรงตัวเสมอ — กติกา 5)</summary>
    private static decimal EffectiveRate(Accounting.Models.Entities.Document doc, decimal? whtRateOverride)
        => doc.ExchangeRate <= 0m || doc.ExchangeRate == 1m ? doc.ExchangeRate
            : whtRateOverride is decimal r && r > 0m ? r : doc.ExchangeRate;

    /// <summary>ยอดบาทที่พิมพ์ใช้ได้เฉพาะเมื่อยังคิดด้วยอัตราเอกสาร (ยอดบนกระดาษเป็นอัตราวันใบ — อัตราวันจ่ายต้องคิดจากฐานสกุลเอกสาร × อัตรา)</summary>
    private static decimal? EffectivePrintedBase(Accounting.Models.Entities.Document doc, decimal? whtRateOverride)
        => EffectiveRate(doc, whtRateOverride) == doc.ExchangeRate ? UsablePrintedBase(doc) : null;

    /// <summary>
    /// <b>ทางเข้าเดียวของผู้อ่านระดับเอกสาร</b> (50 ทวิ · แถว ภ.ง.ด. · สรุปต่อประเภทบนฟอร์ม) — ทุกบรรทัดที่ยังไม่ลบ เรียงตาม <c>LineOrder</c> ·
    /// ส่วนที่ผู้จ่ายออกแทนคิดด้วย <see cref="WhtGrossUp.ForLines"/> (สูตรเดียวกับตอนบันทึก/ลงบัญชี) · คืนตาม <c>DocumentLine.Id</c>
    /// (ผู้เรียกเลือกบรรทัดของผู้ถูกหักรายนั้นเอง — ฐานบาทยังเฉลี่ยจากทั้งใบ)
    /// </summary>
    /// <param name="whtRateOverride">อัตราฐานภาษีของงวด (คำตัดสินข้อ 162 · <see cref="DocumentFx.WhtRateFor"/>) · null = อัตราเอกสาร (เดิมทุกสตางค์)</param>
    public static IReadOnlyDictionary<Guid, PerLineWhtBaht> ForDocument(Accounting.Models.Entities.Document doc, decimal? whtRateOverride = null)
    {
        var fxRate = EffectiveRate(doc, whtRateOverride);
        var printedBase = EffectivePrintedBase(doc, whtRateOverride);
        var lines = doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();
        var borne = WhtGrossUp.IsPayerBorne(doc.WhtCertType)
            ? WhtGrossUp.ForLines(lines.Select(l => (l.Amount, l.WithholdingTaxRate)).ToList(), doc.WhtCertType).Select(p => p.PayerBorne).ToList()
            : lines.Select(_ => 0m).ToList();
        var inputs = lines.Select((l, i) => new PerLineWhtLine(l.Amount, l.WithholdingTaxRate, l.WithholdingTaxAmount, l.IncomeTypeCode, borne[i])).ToList();
        var baht = ToBaht(inputs, fxRate, printedBase).ToArray();
        // ใบต่างสกุล: Σ ภาษีบาทผูกกับยอด GL (เศษจากฐานบาท × อัตรา) — ทุกผู้อ่านเห็นตัวเลขชุดเดียวกับ 50 ทวิ (ฝ่ายค้านข้อ 5) · ใบบาท = ยอดที่เก็บไว้อยู่แล้ว
        if (BasisOf(fxRate, printedBase) != WhtBahtBasis.Thb)
        {
            var idx = Enumerable.Range(0, baht.Length).Where(i => baht[i].WhtThb != 0m).ToList();
            if (idx.Count > 0)
            {
                var tied = TieOut(idx.Select(i => baht[i].WhtThb).ToList(), DocumentWhtThb(doc.WithholdingTaxAmount, fxRate));
                for (var k = 0; k < idx.Count; k++) baht[idx[k]] = baht[idx[k]] with { WhtThb = tied[k] };
            }
        }
        var map = new Dictionary<Guid, PerLineWhtBaht>(lines.Count);
        for (var i = 0; i < lines.Count; i++) map[lines[i].Id] = baht[i];
        return map;
    }

    /// <summary>แถวย่อยบนเอกสารที่พิมพ์ (สกุลเอกสาร — ตัวเลขเดียวกับบรรทัดบนกระดาษ) : ภาษีหักรวมต่ออัตรา เรียงอัตรามาก→น้อย ·
    /// คืนรายการว่างเมื่อมีอัตราเดียว (กระดาษเดิมไม่เปลี่ยนแม้แต่บรรทัดเดียว) · ผู้อ่าน: HTML renderer + QuestPDF renderer (คู่กัน ห้าม drift)</summary>
    public static IReadOnlyList<(decimal RatePercent, decimal WhtAmount)> RateBreakdownForPrint(Accounting.Models.Entities.Document doc)
    {
        var groups = doc.Lines.Where(l => !l.IsDeleted && l.WithholdingTaxAmount != 0m)
            .GroupBy(l => l.WithholdingTaxRate)
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key, g.Sum(l => l.WithholdingTaxAmount)))
            .ToList();
        return groups.Count >= 2 ? groups : new List<(decimal, decimal)>();
    }

    /// <summary>สรุปต่อประเภทเงินได้ของเอกสาร (บาท) — ผู้อ่าน: <c>DocumentResponse.WhtByIncomeType</c> · ไม่มีบรรทัด (ไม่ได้โหลด/หักระดับหัวใบ) ⇒ null = "ยังไม่ได้ตรวจ" (ไม่ใช่ [])</summary>
    public static IReadOnlyList<WhtIncomeGroup>? GroupsOf(Accounting.Models.Entities.Document doc)
        => doc.Lines == null || doc.Lines.Count == 0 ? null : GroupByIncomeType(ForDocument(doc).Values);

    /// <summary>กระทบ Σ ภาษีบาทกับยอดเป้า (เช่น ยอดที่นำส่งจริงของงวด) — ผลต่างระดับเศษ (≤ 0.05 × จำนวนบรรทัด) ไปบรรทัดภาษีสูงสุด ·
    /// เกินกว่านั้น ⇒ คืนตามเดิม (ผู้เรียกต้องล้มดัง ไม่ใช่ดูดเงียบ)</summary>
    public static IReadOnlyList<decimal> TieOut(IReadOnlyList<decimal> whtThb, decimal target)
    {
        var arr = whtThb.ToArray();
        var diff = target - arr.Sum();
        if (diff == 0m || arr.Length == 0 || Math.Abs(diff) > 0.05m * arr.Length) return arr;
        var idx = 0;
        for (var i = 1; i < arr.Length; i++) if (Math.Abs(arr[i]) > Math.Abs(arr[idx])) idx = i;
        arr[idx] += diff;
        return arr;
    }
}
