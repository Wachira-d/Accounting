namespace Accounting.Helpers;

/// <summary>ผลจัดประเภทหัก ณ ที่จ่ายของบรรทัดสแกนหนึ่งบรรทัด (จากคำอธิบายของบรรทัดนั้นเท่านั้น)</summary>
public enum OcrLineWhtKind
{
    /// <summary>ตัดสินไม่ได้ ⇒ ตามหัวใบ (ไม่แต่งว่า 0 · ไม่แต่งว่าเป็นบริการ — หลักการ #3)</summary>
    Unknown = 0,
    /// <summary>บรรทัดเป็นเงินได้ที่ต้องหัก (มีรหัส ม.40 จากตัวจัดหมวด)</summary>
    Withholdable = 1,
    /// <summary>บรรทัดเป็นสินค้า/ค่าสาธารณูปโภคที่ไม่อยู่ในข่าย ท.ป.4/2528 ⇒ 0%</summary>
    NotWithholdable = 2,
}

/// <param name="Amount">ฐานก่อน VAT ของบรรทัด (สกุลเอกสาร)</param>
/// <param name="Kind">ผลจัดประเภทของบรรทัด</param>
/// <param name="IncomeTypeCode">รหัส ม.40 เมื่อ <see cref="OcrLineWhtKind.Withholdable"/></param>
public readonly record struct OcrLineWhtInput(decimal Amount, OcrLineWhtKind Kind, string? IncomeTypeCode);

/// <summary>อัตรา/ประเภทของบรรทัดที่ตัดสินแล้ว</summary>
public readonly record struct OcrLineWhtPick(decimal RatePercent, string? IncomeTypeCode, decimal WhtAmount);

/// <summary>ผลแผน · <see cref="Applied"/> false = ผู้เรียกใช้การเฉลี่ยแบบเดิม (อัตราหัวใบทุกบรรทัด)</summary>
public readonly record struct OcrLineWhtResult(bool Applied, IReadOnlyList<OcrLineWhtPick> Lines, string? Note);

/// <summary>
/// **OCR: หัก ณ ที่จ่าย "ตามรายการ"** (pure · ทีม F · เปิดเฉพาะบริษัทที่ตั้ง <c>CompanySettings.PerLineWhtEnabled</c>)
///
/// <para>═══ ที่มา ═══ ตัวสร้างเอกสารจากสแกนเฉลี่ยภาษีหักของหัวใบลง<b>ทุกบรรทัดด้วยอัตราเดียว</b> ⇒ ใบ "ค่าบริการ + ค่าโฆษณา + ค่าสินค้า" ได้
/// 3% ทุกบรรทัด (ค่าสินค้าถูกหัก · ค่าโฆษณาผิดอัตรา · 50 ทวิ แถวเดียว) — ผู้ใช้ต้องแก้ทุกบรรทัดเอง (ขัดกฎเหล็ก #3)</para>
///
/// <para>═══ กติกา (ทิศปลอดภัย — ไม่เปลี่ยนยอดภาษีของใบ) ═══
/// <list type="number">
/// <item>ใช้เฉพาะเมื่อหัวใบมีหัก ณ ที่จ่ายอยู่แล้ว (กระดาษพิมพ์/ผู้ใช้ยืนยัน) — แผนนี้<b>กระจาย</b>ยอดเดิม ไม่ได้ตัดสินว่าใบต้องหักไหม</item>
/// <item>บรรทัดที่จัดได้ ⇒ อัตราตามกฎหมายของประเภท (<see cref="ThaiWhtRateTable.RateFor"/> · อัตราที่ขึ้นกับชนิดผู้รับแต่ไม่รู้ชนิด ⇒ ตามหัวใบ) ·
///   สินค้า ⇒ 0% · ตัดสินไม่ได้ ⇒ ตามหัวใบ</item>
/// <item>Σ ภาษีตามแผนต้องอธิบายยอดของหัวใบได้ (ต่างไม่เกิน <see cref="MatchTolerance"/>) — ไม่ได้ ⇒ ไม่ใช้แผน (เฉลี่ยแบบเดิม) + แท็ก
///   <see cref="MismatchTag"/> ให้คนตรวจ · ได้ ⇒ เศษไปบรรทัดภาษีสูงสุด ⇒ Σ = ยอดหัวใบพอดี (ยอดใบ/ยอดจ่าย/JE ไม่ขยับ)</item>
/// <item>ทุกบรรทัดได้คำตอบเดียวกับหัวใบ ⇒ ไม่ใช้แผน (ผลเท่าเดิมทุกสตางค์)</item>
/// </list></para>
/// </summary>
public static class OcrLineWhtPlan
{
    /// <summary>ภาษีหักต่อบรรทัดตามแผนอธิบายยอดหัวใบไม่ได้</summary>
    public const string MismatchTag = "[WHT-LINE-MISMATCH]";
    /// <summary>ใช้แผนรายบรรทัดแล้ว (บอกผู้ใช้ว่าบรรทัดไหนได้อะไร)</summary>
    public const string AppliedTag = "[WHT-LINE]";

    /// <summary>ผลต่างสูงสุดระหว่าง Σ แผน กับยอดหัวใบ (บาท/หน่วยสกุลเอกสาร) — เศษปัดของการพิมพ์ยอดหักต่อประเภท</summary>
    public const decimal MatchTolerance = 1.00m;

    /// <param name="lines">บรรทัดของใบ (ลำดับเดียวกับบรรทัดเอกสาร)</param>
    /// <param name="headerRate">อัตราหัวใบ (%) · ≤ 0 ⇒ ไม่มีหัก ⇒ ไม่ใช้แผน</param>
    /// <param name="headerIncomeTypeCode">ประเภทเงินได้ของหัวใบ</param>
    /// <param name="headerWht">ยอดภาษีหักของหัวใบ (สกุลเอกสาร)</param>
    /// <param name="payeeIsJuristic">ผู้ถูกหักเป็นนิติบุคคลไหม (null = ไม่รู้)</param>
    public static OcrLineWhtResult Decide(IReadOnlyList<OcrLineWhtInput> lines, decimal headerRate, string? headerIncomeTypeCode,
        decimal headerWht, bool? payeeIsJuristic)
    {
        var none = new OcrLineWhtResult(false, Array.Empty<OcrLineWhtPick>(), null);
        if (lines.Count == 0 || headerRate <= 0m || headerWht <= 0m) return none;

        var picks = new OcrLineWhtPick[lines.Count];
        var differs = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            decimal rate; string? code;
            switch (l.Kind)
            {
                case OcrLineWhtKind.NotWithholdable:
                    rate = 0m; code = null; break;
                case OcrLineWhtKind.Withholdable when RateOf(l.IncomeTypeCode, payeeIsJuristic) is decimal r:
                    rate = r; code = l.IncomeTypeCode; break;
                default:
                    rate = headerRate; code = headerIncomeTypeCode; break;
            }
            if (rate != headerRate || !string.Equals(code ?? "", headerIncomeTypeCode ?? "", StringComparison.Ordinal)) differs = true;
            picks[i] = new(rate, code, rate > 0m ? Math.Round(l.Amount * rate / 100m, 2, MidpointRounding.AwayFromZero) : 0m);
        }
        if (!differs) return none;

        var planned = picks.Sum(p => p.WhtAmount);
        if (Math.Abs(planned - headerWht) > MatchTolerance)
            return new(false, Array.Empty<OcrLineWhtPick>(),
                $"{MismatchTag} หัก ณ ที่จ่ายรายบรรทัดตามประเภทรายการ รวม {planned:N2} ≠ ยอดหักของหัวใบ {headerWht:N2} — "
                + "ระบบคงอัตราหัวใบทุกบรรทัดไว้ ตรวจอัตรา/ประเภทเงินได้ต่อบรรทัดกับกระดาษก่อนอนุมัติ");
        if (picks.All(p => p.WhtAmount == 0m))
            return new(false, Array.Empty<OcrLineWhtPick>(), null);

        // เศษ (≤ MatchTolerance) ไปบรรทัดที่ภาษีสูงสุด ⇒ Σ = ยอดหัวใบพอดี
        var diff = headerWht - planned;
        if (diff != 0m)
        {
            var idx = -1;
            for (var i = 0; i < picks.Length; i++)
                if (picks[i].WhtAmount > 0m && (idx < 0 || picks[i].WhtAmount > picks[idx].WhtAmount)) idx = i;
            picks[idx] = picks[idx] with { WhtAmount = picks[idx].WhtAmount + diff };
        }
        var groups = picks.Where(p => p.WhtAmount > 0m)
            .GroupBy(p => (p.IncomeTypeCode ?? PerLineWht.DefaultIncomeTypeCode, p.RatePercent))
            .Select(g => $"{ThaiWhtRateTable.Resolve(g.Key.Item1)?.Name ?? g.Key.Item1} {g.Key.RatePercent:0.##}% = {g.Sum(x => x.WhtAmount):N2}");
        var zero = picks.Count(p => p.RatePercent == 0m);
        return new(true, picks,
            $"{AppliedTag} หัก ณ ที่จ่ายตามรายการ: {string.Join(" · ", groups)}"
            + (zero > 0 ? $" · ไม่หัก {zero} บรรทัด (สินค้า/ไม่อยู่ในข่าย)" : "")
            + " — ตรวจประเภทเงินได้ต่อบรรทัดก่อนยืนยัน");
    }

    /// <summary>อัตราตามกฎหมายของรหัส · อัตราที่ขึ้นกับชนิดผู้รับ (ดอกเบี้ย) และไม่รู้ชนิด ⇒ null (ตามหัวใบ) · รหัสที่ไม่มีอัตราคงที่ (เงินเดือน) ⇒ null</summary>
    private static decimal? RateOf(string? code, bool? payeeIsJuristic)
    {
        if (ThaiWhtRateTable.Resolve(code) is not { } t) return null;
        if (ThaiWhtRateTable.RateDependsOnPayeeKind(t.Code))
            return payeeIsJuristic is bool j ? ThaiWhtRateTable.RateFor(t.Code, j) : null;
        return ThaiWhtRateTable.RateFor(t.Code, payeeIsJuristic ?? true);
    }
}
