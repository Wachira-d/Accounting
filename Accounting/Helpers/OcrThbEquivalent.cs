using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ยอดเงินบาทที่ใบต่างสกุลพิมพ์ไว้เอง · null = ไม่พบบนกระดาษ</summary>
/// <param name="Rate">อัตราที่พิมพ์ ("1 USD = 36.50 THB" · "Exchange rate 36.50" · "อัตราแลกเปลี่ยน 36.50")</param>
/// <param name="SubTotalThb">ยอดก่อน VAT เป็นบาท</param>
/// <param name="VatThb">VAT เป็นบาท</param>
/// <param name="TotalThb">ยอดรวมเป็นบาท (Total/Grand total/เทียบเท่าเงินบาท)</param>
public readonly record struct OcrThbReading(decimal? Rate, decimal? SubTotalThb, decimal? VatThb, decimal? TotalThb)
{
    public bool Any => Rate != null || SubTotalThb != null || VatThb != null || TotalThb != null;
}

/// <summary>อัตรา + ฐานบาทที่ตัดสินจากกระดาษ · <see cref="Rate"/> null = ใช้ไม่ได้ (ให้ชั้นถัดไป — อัตรา ธปท. — ตัดสิน)</summary>
/// <param name="Rate">อัตราที่จะใช้กับเอกสาร (6 ตำแหน่ง)</param>
/// <param name="ThbBase">ฐานก่อน VAT เป็นบาทตามกระดาษ (<c>Document.DocumentThbBase</c>) · null = กระดาษไม่บอกฐาน (มีแต่อัตรา)</param>
/// <param name="Basis">ที่มา (ข้อความสั้นสำหรับบันทึก)</param>
/// <param name="Problem">เหตุที่ไม่ใช้ (ยอดบาทขัดกันเอง) — ผู้เรียกเขียนแท็กให้คนดู</param>
public readonly record struct OcrThbFxPlan(decimal? Rate, decimal? ThbBase, string Basis, string? Problem);

/// <summary>
/// **อ่าน "ยอดเงินบาท" ที่ใบต่างสกุลพิมพ์ไว้** (pure · ทีม F · <c>docs/PER_LINE_WHT_DESIGN.md</c> §FX)
///
/// <para>═══ ที่มา (คำขอผู้ใช้) ═══ ใบแจ้งหนี้สกุลต่างประเทศจำนวนมาก (Google/Meta/AWS ฝั่งไทย · ผู้ขายต่างประเทศที่จด VAT ไทย) พิมพ์ยอดเทียบเท่า
/// เงินบาทไว้แล้ว — นั่นคือฐาน VAT/WHT ที่ผู้ออกเอกสารประกาศ · เดิม OCR ใช้อัตรา ธปท. ของวันที่เอกสารเสมอ ⇒ ฐานบาทในระบบไม่ตรงกระดาษ</para>
///
/// <para>═══ กติกา ═══ อ่านเฉพาะบรรทัดที่<b>ระบุบาท</b> (THB · บาท · ฿) <b>และ</b>มีป้ายยอด (ก่อน VAT / VAT / ยอดรวม / เทียบเท่า) · ตัวเลขที่ใช้ =
/// ตัวเลขที่<b>ติดกับคำบ่งบาท</b> (บรรทัดที่พิมพ์ทั้ง USD และ THB) หรือตัวเลขสุดท้ายของบรรทัด · ข้ามบรรทัดจำนวนเงินตัวอักษร ("…บาทถ้วน") ·
/// บรรทัดอัตราแลกเปลี่ยนไม่นับเป็นยอด · ไม่พบ = null (ห้ามแต่ง — หลักการ #3)</para>
///
/// <para><see cref="Plan"/> ตรวจความสอดคล้อง: อัตราจากฐาน · จากยอดรวม · ที่พิมพ์ ต้องห่างกันไม่เกิน <see cref="ConsistencyTolerance"/> — ขัดกัน ⇒ ไม่ใช้ (คืนเหตุ)</para>
/// </summary>
public static class OcrThbEquivalent
{
    /// <summary>แท็กใน ProcessingNotes — ใช้ยอดบาทบนเอกสารเป็นฐาน</summary>
    public const string UsedTag = "[FX-DOC]";
    /// <summary>แท็ก — กระดาษมียอดบาทแต่ขัดกันเอง (ใช้อัตรา ธปท. แทน · คนต้องตรวจ)</summary>
    public const string ConflictTag = "[FX-DOC-CONFLICT]";

    /// <summary>แท็ก — กระดาษมียอดบาทแหล่งเดียว (ตรวจไขว้บนกระดาษไม่ได้) และเทียบอัตรา ธปท. ไม่ผ่าน/ไม่มีอัตรา ธปท. ⇒ ไม่ใช้ (ฝ่ายค้านทีม F ข้อ 2)</summary>
    public const string UnverifiedTag = "[FX-DOC-UNVERIFIED]";

    /// <summary>หลักฐานแหล่งเดียว: ห่างจากอัตรา ธปท. ของวันที่เอกสารได้ไม่เกิน 3% (อัตราของผู้ออกเอกสาร/ธนาคารต่างจาก mid-rate ได้ แต่ไม่ถึงขั้นอ่านผิดหลัก)</summary>
    public const decimal BotTolerance = 0.03m;

    /// <summary>ความต่างสัมพัทธ์สูงสุดระหว่างอัตราที่ได้จากหลายแหล่งบนกระดาษ (0.5%)</summary>
    public const decimal ConsistencyTolerance = 0.005m;

    private static readonly Regex ThbMarkRx = new(@"(\bTHB\b|บาท|฿)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountWordsRx = new(@"(บาท\s*ถ้วน|สตางค์)", RegexOptions.Compiled);   // บรรทัดจำนวนเงินตัวอักษร (ตัวอ่านขากลับ — ไม่ใช่สูตรประกอบ)
    private static readonly Regex RateLabelRx = new(@"(EXCHANGE\s*RATE|EX\.?\s*RATE|FX\s*RATE|CONVERSION\s*RATE|อัตราแลกเปลี่ยน|อัตราแลก)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OneUnitRateRx = new(
        @"\b1(?:\.0+)?\s*(?:USD|EUR|JPY|CNY|GBP|SGD|HKD|AUD|MYR|KRW)\s*=\s*(?<r>\d{1,5}(?:[.,]\d+)?)\s*(?:THB|บาท|฿)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VatLabelRx = new(@"(\bVAT\b|ภาษีมูลค่าเพิ่ม)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubTotalLabelRx = new(
        @"(SUB\s*-?\s*TOTAL|BEFORE\s*VAT|EXCL(?:UDING)?\.?\s*VAT|NET\s*AMOUNT|ยอดก่อนภาษี|ก่อน\s*VAT|ก่อนภาษี|มูลค่าสินค้า|มูลค่าบริการ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TotalLabelRx = new(
        @"(GRAND\s*TOTAL|\bTOTAL\b|AMOUNT\s*DUE|EQUIVALENT|AMOUNT\s*IN\s*THB|เทียบเท่า|คิดเป็นเงินบาท|รวมทั้งสิ้น|รวมเงินทั้งสิ้น|ยอดรวม|ยอดสุทธิ|จำนวนเงินรวม)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>"รวม VAT" — ยอดรวม ไม่ใช่ยอด VAT (ต้องตรวจก่อนป้าย VAT)</summary>
    private static readonly Regex InclVatLabelRx = new(@"(INCL(?:UDING)?\.?\s*VAT|รวม\s*VAT|รวมภาษี)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>จำนวนเงิน — ไม่ใช่ส่วนของตัวเลขที่ยาวกว่า · ไม่ใช่เปอร์เซ็นต์ ("VAT 7%")</summary>
    private static readonly Regex NumberRx = new(@"(?<![\d.,])(?<n>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)(?![\d.,]*\d)(?!\s*%)", RegexOptions.Compiled);
    private static readonly Regex DecimalRx = new(@"(?<n>\d{1,5}[.,]\d{1,6})", RegexOptions.Compiled);

    /// <summary>อ่านยอดบาท/อัตราจากข้อความทั้งหน้า</summary>
    public static OcrThbReading Read(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return default;
        decimal? rate = null, sub = null, vat = null, total = null;
        foreach (var raw in rawText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // ── อัตรา ── "1 USD = 36.50 THB" ชนะ · ไม่งั้นบรรทัดป้ายอัตรา → เลขทศนิยมตัวแรกหลังป้าย
            var one = OneUnitRateRx.Match(line);
            if (one.Success)
            {
                rate ??= ParseRate(one.Groups["r"].Value);
                continue;
            }
            var rl = RateLabelRx.Match(line);
            if (rl.Success)
            {
                var after = line[(rl.Index + rl.Length)..];
                var dm = DecimalRx.Match(after);
                if (dm.Success) rate ??= ParseRate(dm.Groups["n"].Value);
                continue;   // บรรทัดอัตราไม่ใช่ยอด
            }

            if (!ThbMarkRx.IsMatch(line) || AmountWordsRx.IsMatch(line)) continue;
            var amount = AmountNearThb(line);
            if (amount is not decimal a || a <= 0m) continue;
            // ลำดับมีความหมาย: ก่อน VAT ("Total excl. VAT") → รวม VAT ("Total incl. VAT") → VAT → ยอดรวม
            if (SubTotalLabelRx.IsMatch(line)) sub ??= a;
            else if (InclVatLabelRx.IsMatch(line)) total ??= a;
            else if (VatLabelRx.IsMatch(line)) vat ??= a;
            else if (TotalLabelRx.IsMatch(line)) total ??= a;
        }
        return new(rate, sub, vat, total);
    }

    /// <summary>ตัดสินอัตรา + ฐานบาทของใบ จากยอดบาทบนกระดาษเทียบยอดสกุลเอกสารที่อ่านได้ · ดูกติกาที่ตัวคลาส</summary>
    /// <param name="botRate">อัตรา ธปท. ของวันที่เอกสาร (null = ไม่มี) — ใช้ตรวจหลักฐานแหล่งเดียว</param>
    public static OcrThbFxPlan Plan(OcrThbReading reading, decimal? foreignSubTotal, decimal? foreignVat, decimal? foreignTotal, decimal? botRate)
    {
        if (!reading.Any) return new(null, null, "", null);
        decimal? thbBase = reading.SubTotalThb;
        if (thbBase == null && reading.TotalThb is decimal t && reading.VatThb is decimal v && t > v) thbBase = t - v;
        if (thbBase == null && reading.TotalThb is decimal t0 && (foreignVat ?? 0m) == 0m && (reading.VatThb ?? 0m) == 0m) thbBase = t0;

        var candidates = new List<(string Name, decimal Rate)>();
        if (thbBase is decimal b && foreignSubTotal is > 0m)
            candidates.Add(("ฐานก่อน VAT", b / foreignSubTotal.Value));
        if (reading.TotalThb is decimal tt && foreignTotal is > 0m)
            candidates.Add(("ยอดรวม", tt / foreignTotal.Value));
        if (reading.Rate is decimal pr)
            candidates.Add(("อัตราที่พิมพ์", pr));
        if (candidates.Count == 0) return new(null, null, "", null);

        var chosen = candidates[0].Rate;
        foreach (var c in candidates.Skip(1))
            if (Math.Abs(c.Rate - chosen) > chosen * ConsistencyTolerance)
                return new(null, null, "",
                    $"{ConflictTag} ยอดเงินบาทบนเอกสารไม่สอดคล้องกัน ({string.Join(" · ", candidates.Select(x => $"{x.Name} ≈ {x.Rate:0.####}"))}) — "
                    + "ระบบใช้อัตรา ธปท. แทน ตรวจอัตรากับกระดาษก่อนอนุมัติ");

        // ── หลักฐานแหล่งเดียว (ตัวเลขบาท/อัตราที่พิมพ์อิสระกันน้อยกว่า 2 ตัว) ⇒ ต้องใกล้อัตรา ธปท. (ฝ่ายค้านทีม F ข้อ 2 — เดิมรับทันทีแล้วประทับ "จากเอกสาร")
        var independent = (reading.SubTotalThb != null ? 1 : 0) + (reading.TotalThb != null ? 1 : 0) + (reading.Rate != null ? 1 : 0);
        if (independent < 2)
        {
            if (botRate is not decimal bot || bot <= 0m)
                return new(null, null, "", $"{UnverifiedTag} กระดาษมียอดเงินบาทแหล่งเดียว ({candidates[0].Name} ≈ {chosen:0.####}) และไม่มีอัตรา ธปท. ให้เทียบ — "
                    + "ระบบไม่ใช้ยอดนี้ ตรวจแล้วกรอกอัตรา/ยอดบาทเองก่อนอนุมัติ");
            if (Math.Abs(chosen - bot) > bot * BotTolerance)
                return new(null, null, "", $"{UnverifiedTag} ยอดเงินบาทบนกระดาษให้อัตรา ≈ {chosen:0.####} ห่างจากอัตรา ธปท. {bot:0.####} เกิน {BotTolerance:P0} — "
                    + "อาจอ่านตัวเลขผิด ระบบใช้อัตรา ธปท. แทน ตรวจกับกระดาษก่อนอนุมัติ");
        }

        decimal rate;
        if (thbBase is decimal tb && foreignSubTotal is > 0m)
        {
            rate = DocumentFx.RateFromPrintedBaht(foreignSubTotal.Value, tb)!.Value;
            // อัตรา 6 ตำแหน่งต้องแปลงฐานกลับได้ยอดบาทบนกระดาษ (สกุลหน่วยเล็ก — ฝ่ายค้านข้อ 1) ไม่งั้น 50 ทวิ ≠ GL/ภ.พ.30
            if (!DocumentFx.PrintedBahtRoundTrips(foreignSubTotal.Value, rate, tb))
                return new(null, null, "", $"{ConflictTag} " + DocumentFx.RoundTripRefusal(foreignSubTotal.Value, rate, tb));
        }
        else
            rate = Math.Round(chosen, DocumentFx.RateDecimals, MidpointRounding.AwayFromZero);
        // ฐานบาทเก็บได้เฉพาะเมื่อรู้ฐานสกุลเอกสาร (ไม่งั้นเฉลี่ยลงบรรทัดไม่ได้)
        var keptBase = foreignSubTotal is > 0m ? thbBase : null;
        return new(rate, keptBase, candidates[0].Name, null);
    }

    /// <summary>ข้อความบันทึกเมื่อใช้ยอดบาทบนเอกสาร</summary>
    public static string UsedNote(string currency, OcrThbFxPlan plan)
        => $"{UsedTag} ใช้ยอดเงินบาทที่พิมพ์บนเอกสาร ({plan.Basis}) — {currency} @ {plan.Rate:0.######}"
           + (plan.ThbBase is decimal b ? $" · ฐานก่อน VAT {b:N2} บาท (ฐานหัก ณ ที่จ่าย/VAT)" : "")
           + " — ตรวจกับกระดาษก่อนยืนยัน";

    private static decimal? AmountNearThb(string line)
    {
        var nums = NumberRx.Matches(line).Cast<Match>().ToList();
        if (nums.Count == 0) return null;
        var mark = ThbMarkRx.Match(line);
        // บรรทัดที่พิมพ์ทั้งสองสกุล: เลขที่อยู่ติดคำบ่งบาท (หลังหรือก่อน) ชนะ · ไม่งั้นเลขสุดท้ายของบรรทัด
        Match? best = null; var bestDist = int.MaxValue;
        foreach (var n in nums)
        {
            var dist = n.Index >= mark.Index + mark.Length
                ? n.Index - (mark.Index + mark.Length)
                : mark.Index - (n.Index + n.Length);
            if (dist < 0) dist = 0;
            if (dist < bestDist) { bestDist = dist; best = n; }
        }
        var pick = bestDist <= 3 ? best! : nums[^1];
        return decimal.TryParse(pick.Groups["n"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static decimal? ParseRate(string s)
    {
        var norm = s.Replace(",", ".");
        if (!decimal.TryParse(norm, NumberStyles.Number, CultureInfo.InvariantCulture, out var r)) return null;
        return r is > 0.0001m and < 10000m ? r : null;
    }
}
