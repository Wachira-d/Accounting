using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>คำตัดสินของชั้น "อัตรา VAT รายบรรทัดตามรหัส ภ.พ. ของกระดาษ"</summary>
public enum OcrVatCodeVerdict
{
    /// <summary>กระดาษไม่มีตารางรหัส ภ.พ. ที่ผสมกลุ่มภาษี (หรือไม่มีบรรทัด) — ชั้นนี้ไม่พูดอะไร · เส้นเดิมทุกตัวอักษร</summary>
    NotApplicable = 0,
    /// <summary>อัตรารายบรรทัด (และส่วนลดต่อรหัส ถ้ามี) พิสูจน์ด้วยตารางรหัส ภ.พ. บนกระดาษแล้ว</summary>
    Applied = 1,
    /// <summary>กระดาษผสมกลุ่มภาษี แต่บรรทัดกระทบกับตารางไม่ได้ — ไม่แตะอะไร (ตกชั้นเดิม) · ด่าน <see cref="OcrVatCodeLines.TableMismatch"/> ฟ้องผลสุดท้าย</summary>
    Unreconciled = 2,
}

/// <summary>หนึ่งบรรทัดที่ป้อนชั้นนี้</summary>
/// <param name="PrintedAmount">ยอดก่อนส่วนลดท้ายบิลของบรรทัดตามที่พิมพ์ (ตัวเดียวกับที่ตัวกระทบยอดใช้ — <c>lineGross</c>)</param>
/// <param name="PresetRate">อัตราที่ชั้นบนตั้งไว้แล้ว (engine · ผู้ใช้ในหน้ารีวิว · สัญลักษณ์ท้ายบรรทัด) · null = ยังไม่มี</param>
public readonly record struct OcrVatCodeLineInput(decimal PrintedAmount, decimal? PresetRate);

/// <summary>ผลของชั้นนี้</summary>
/// <param name="Rates">อัตราต่อบรรทัดตามลำดับเดิม — ผู้เรียกเติมเฉพาะบรรทัดที่ยังไม่มีอัตรา <b>หรือ</b> <paramref name="ReplaceEngineRate"/> = true · null = ไม่แตะ</param>
/// <param name="ReplaceEngineRate">อัตราที่ engine ส่งมาคือ<b>รหัส ภ.พ.</b> ไม่ใช่อัตรา (Azure อ่านคอลัมน์ "VAT CODE" = 2 เป็น TaxRate 2%) ⇒ แทนด้วยอัตราตามรหัส</param>
/// <param name="AmountsAfterDiscount">ยอดหลังส่วนลดต่อบรรทัด เมื่อกระดาษมีส่วนลดท้ายบิล — ส่วนลดแบ่งตามรหัสให้ Σ ต่อรหัส = ตาราง · null = ไม่มีส่วนลดให้แบ่ง</param>
/// <param name="DiscountPercents">% ส่วนลดของบรรทัด (ส่วนลดของรหัสนั้น ÷ ยอดก่อนลดของรหัส) · null คู่กับ <paramref name="AmountsAfterDiscount"/></param>
/// <param name="PricesIncludeVat">ยอดบรรทัดรวม VAT แล้วไหม (Σ บรรทัด − ส่วนลด = แถว "รวม" ของตาราง ⇒ true · = ฐาน ⇒ false)</param>
/// <param name="Note">เหตุผลภาษาไทยพร้อมตัวเลข — ทั้งตอนใช้และตอนไม่ใช้</param>
public sealed record OcrVatCodePlan(
    OcrVatCodeVerdict Verdict, decimal?[] Rates, bool[] ReplaceEngineRate,
    decimal[]? AmountsAfterDiscount, decimal[]? DiscountPercents, bool? PricesIncludeVat, string Note)
{
    public bool Applied => Verdict == OcrVatCodeVerdict.Applied;
}

/// <summary>
/// **อัตรา VAT รายบรรทัดตาม "รหัส ภ.พ." ที่กระดาษพิมพ์ — ตารางรหัส ภ.พ. ของกระดาษเป็นตัวตั้ง ไม่ใช่ชื่อสินค้า** (2026-10-16 · คำสั่งเจ้าของ
/// "อัตรา VAT ต่อบรรทัดยังเดาจากชื่อสินค้า ยังไม่ได้ยึดตารางรหัส ภ.พ.")
///
/// ═══ ที่มา ═══
/// <para>ใบกำกับห้าง (Makro/CP Axtra · Lotus's · Big C · Tops) พิมพ์คอลัมน์ "รหัส ภ.พ. / VAT CODE" ต่อแถวสินค้า และตารางสรุปต่อรหัส
/// (ฐาน · VAT · รวม — <see cref="OcrLineVatMarks.ReadGroups"/>) พร้อมคำอธิบายรหัส ("1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสีย…") ·
/// เดิมเมื่อ engine คืนบรรทัดรายการ อัตรารายบรรทัดมาจาก <see cref="ThaiVatTypeRule.Suggest"/> (เดาจากชื่อ — "ไข่ไก่" ไม่อยู่ในรายการคำ ⇒ 7%) ·
/// ตารางถูกใช้แค่บอกยอดยกเว้นให้ชั้นพิสูจน์ทั้งใบ "ไม่ตัดสิน" ⇒ ใบผสมได้ [Σ-GAP] VatRateMismatch ทุกใบ ให้คนแยกเอง ·
/// ส่วนลดท้ายบิลถูกกระจาย % เดียวทุกบรรทัด ⇒ แม้อัตราถูก Σ ยกเว้นก็ไม่เท่าตาราง (ส่วนลดของกลุ่ม VAT ไหลไปกลุ่มยกเว้น)</para>
///
/// ═══ ลำดับชั้น (DECISION_DOCTRINE §1 — หลักฐานใกล้ของจริงชนะการเดา) ═══
/// <list type="number">
/// <item>อัตราที่ชั้นบนตั้งแล้ว (engine/ผู้ใช้/สัญลักษณ์ท้ายบรรทัด) — ชนะ · <b>ยกเว้น</b>ค่าที่เป็นรหัสบนตารางและไม่ใช่อัตราไทย (1/2) = engine อ่านรหัสเป็นอัตรา</item>
/// <item><b>รหัสรายบรรทัดที่พิมพ์บนแถวสินค้า</b> (<c>ราคาต่อหน่วย รหัส ยอด</c> หรือ <c>ยอด รหัส</c> ท้ายแถว · จับคู่ด้วยยอดตรงถึงสตางค์ตามลำดับ)
///   แปลความหมายด้วย<b>คำอธิบาย/ตารางของกระดาษเอง</b> (<see cref="OcrVatGroupKind"/>) — ไม่มีตารางรหัสตายตัวต่อห้างในเส้นทั่วไป</item>
/// <item><b>ผลรวมของตารางเป็นข้อบังคับ</b>: บรรทัดที่ไม่มีรหัส (engine ตัดคอลัมน์รหัสทิ้ง) แบ่งเข้ากลุ่มได้เมื่อมี<b>ทางเดียว</b>ที่ Σ ต่อกลุ่ม = ตาราง
///   (ไม่มีส่วนลด · สองกลุ่ม · ≤ 20 บรรทัด) — สองทางขึ้นไป = ไม่เดา</item>
/// <item>ตัวพิสูจน์ทั้งใบ / ตัวเดาจากชื่อ (ชั้นเดิม) — เฉพาะบรรทัดที่ยังว่าง</item>
/// </list>
///
/// ═══ เงื่อนไขก่อนใช้ (ไม่ครบ = <see cref="OcrVatCodeVerdict.Unreconciled"/> · ไม่แตะอะไร) ═══
/// <list type="bullet">
/// <item>ทุกกลุ่มในตารางรู้ความหมาย (<see cref="OcrVatGroupTable.AllKnown"/>) — รหัสที่คำอธิบายไม่บอก/ขัดกับตัวเลข = ไม่เดา</item>
/// <item>VAT ของตาราง = VAT หัวใบ · รวมของตาราง = ยอดรวมหัวใบ (±0.02) — กระดาษกับหัวสแกนต้องเป็นเรื่องเดียวกัน</item>
/// <item>Σ บรรทัด − ส่วนลดท้ายบิล = แถว "รวม" ของตาราง (ราคารวม VAT) หรือ = ฐาน (ราคาก่อน VAT) และตรงกับที่ตัวกระทบยอดตัดสิน</item>
/// <item>ต่อกลุ่ม: Σ บรรทัดของกลุ่ม − ส่วนลดของกลุ่ม = ยอดกลุ่มในตาราง · ส่วนลดของกลุ่ม ≥ 0 · Σ ส่วนลดทุกกลุ่ม = ส่วนลดท้ายบิลบนกระดาษ ·
///   ไม่มีส่วนลด ⇒ Σ บรรทัดของกลุ่ม = ยอดกลุ่มพอดี</item>
/// <item>ไม่มีบรรทัดติดลบ (ส่วนลดเป็นบรรทัดแยกรหัสไม่ได้)</item>
/// </list>
/// <para>ส่วนลดต่อกลุ่มกระจายลงบรรทัดในกลุ่มตามสัดส่วน เศษลงบรรทัดสุดท้ายของกลุ่ม ⇒ Σ ต่อกลุ่ม = ตาราง<b>ทุกสตางค์</b> (ไม่ใช่ % เดียวทั้งใบ)</para>
/// </summary>
public static class OcrVatCodeLines
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;
    private const decimal HeaderTol = 0.02m;
    /// <summary>เพดานจำนวนบรรทัดที่ไม่มีรหัสสำหรับการหาทางแบ่งกลุ่มเดียว (2^n)</summary>
    public const int MaxFreeLines = 20;
    private const string Money = @"\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2}";

    /// <summary>แถวสินค้าที่พิมพ์รหัสระหว่างราคาต่อหน่วยกับยอด (คอลัมน์ "ราคาต่อหน่วย | รหัส ภ.พ. | จำนวนเงินรวม" ของ Makro)</summary>
    private static readonly Regex CodeBetween = new(
        @"(?<![\d.,])(?:" + Money + @")[ \t]+(?<code>[0-9A-Z])[ \t]+(?<amt>" + Money + @")(?:[ \t]+[,.;:'`|]{1,2})?[ \t]*$",
        RegexOptions.CultureInvariant);

    /// <summary>แถวสินค้าที่พิมพ์รหัสท้ายยอด (<c>220.00 1</c>)</summary>
    private static readonly Regex CodeTrailing = new(
        @"(?<![\d.,])(?<amt>" + Money + @")[ \t]+(?<code>[0-9A-Z])[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>หัวข้อความของด่าน <see cref="TableMismatch"/> — ตัวเดียวทั้งฝั่งเขียนและผู้อ่านข้อความ</summary>
    public const string MismatchLead = "อัตรา VAT รายบรรทัดไม่ตรงตารางรหัส ภ.พ. บนกระดาษ";

    /// <summary>ตารางรหัส ภ.พ. ที่ "ผสมกลุ่มภาษี" (มีกลุ่มที่ไม่ใช่ 7%) — เงื่อนไขเดียวที่ชั้นนี้และด่าน <see cref="TableMismatch"/> ทำงาน ·
    /// ตารางกลุ่มเดียว 7% (Wine Pro "V 7 …") = ไม่ผสม ⇒ ชั้นพิสูจน์ทั้งใบเดิมตัดสิน</summary>
    internal static bool IsMixed(OcrVatGroupTable table)
        => table.Found && table.Groups.Any(g => g.Kind != OcrVatGroupKind.Standard7);

    /// <summary>อัตราตามกลุ่ม (<c>7</c> · <c>0</c> · <c>-1</c>) · null = ไม่รู้</summary>
    private static decimal? RateOf(OcrVatGroupKind kind) => kind switch
    {
        OcrVatGroupKind.Standard7 => 7m,
        OcrVatGroupKind.ZeroRated => 0m,
        OcrVatGroupKind.Exempt => ThaiVatTypeRule.ExemptRate,
        _ => null,
    };

    private static OcrVatGroupKind? KindOfRate(decimal rate) => rate switch
    {
        7m => OcrVatGroupKind.Standard7,
        0m => OcrVatGroupKind.ZeroRated,
        ThaiVatTypeRule.ExemptRate => OcrVatGroupKind.Exempt,
        _ => null,
    };

    /// <summary>รหัส ภ.พ. ที่พิมพ์บนแถวสินค้า (ยอด · รหัส) ตามลำดับบนกระดาษ — รับเฉพาะรหัสที่อยู่ใน <paramref name="codes"/> (รหัสของตารางบนกระดาษเดียวกัน)
    /// · ข้ามแถวตารางสรุป/คำอธิบาย (ไม่มีรูป "เงิน รหัส เงิน")</summary>
    internal static IReadOnlyList<(decimal Amount, string Code)> ReadRowCodes(string? rawText, IReadOnlyCollection<string> codes)
    {
        var list = new List<(decimal, string)>();
        if (string.IsNullOrWhiteSpace(rawText) || codes.Count == 0) return list;
        foreach (var raw in rawText.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var m = CodeBetween.Match(line);
            if (!m.Success || !codes.Contains(m.Groups["code"].Value))
                m = CodeTrailing.Match(line);
            if (!m.Success || !codes.Contains(m.Groups["code"].Value)) continue;
            list.Add((decimal.Parse(m.Groups["amt"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture),
                m.Groups["code"].Value));
        }
        return list;
    }

    /// <summary>ตัดสินอัตรา VAT รายบรรทัด (และส่วนลดต่อรหัส) จากรหัส ภ.พ. + ตารางรหัส ภ.พ. ของกระดาษ (pure)</summary>
    /// <param name="lines">บรรทัดตามลำดับเดิม</param>
    /// <param name="rawText">ข้อความทั้งใบ (อ่านรหัสบนแถวสินค้า)</param>
    /// <param name="table">ตารางรหัส ภ.พ. (<see cref="OcrLineVatMarks.ReadGroups"/>)</param>
    /// <param name="headerVat">VAT หัวใบ</param>
    /// <param name="headerTotal">ยอดรวมทั้งสิ้นหัวใบ (0 = ไม่รู้)</param>
    /// <param name="headerDiscount">ส่วนลดท้ายบิลที่กระดาษพิมพ์ (0 = ไม่มี)</param>
    /// <param name="reconPricesIncludeVat">ตัวกระทบยอด (<see cref="OcrLineReconciler"/>) ตัดสินว่ายอดบรรทัดรวม VAT</param>
    /// <param name="reconTargetLineSum">เป้า Σ บรรทัดหลังส่วนลดของตัวกระทบยอด (null = ไม่มีส่วนลดให้กระจาย)</param>
    public static OcrVatCodePlan Plan(
        IReadOnlyList<OcrVatCodeLineInput> lines, string? rawText, OcrVatGroupTable table,
        decimal headerVat, decimal headerTotal, decimal headerDiscount,
        bool reconPricesIncludeVat, decimal? reconTargetLineSum)
    {
        var n = lines.Count;
        var rates = new decimal?[n];
        var replace = new bool[n];
        OcrVatCodePlan Result(OcrVatCodeVerdict v, string why)
            => new(v, new decimal?[n], new bool[n], null, null, null, why);
        OcrVatCodePlan No(string why) => Result(OcrVatCodeVerdict.Unreconciled, "รหัส ภ.พ.: " + why + " — ไม่ใช้ตาราง แบ่งอัตรารายบรรทัด");

        if (n == 0 || !IsMixed(table)) return Result(OcrVatCodeVerdict.NotApplicable, "");
        if (!table.AllKnown)
            return No("ตารางมีรหัสที่กระดาษไม่บอกความหมาย ("
                + string.Join(" · ", table.Groups.Where(g => g.Kind == OcrVatGroupKind.Unknown).Select(g => g.Evidence)) + ")");
        if (Math.Abs(table.Vat - headerVat) > HeaderTol)
            return No($"VAT ของตาราง {table.Vat:N2} ≠ VAT หัวใบ {headerVat:N2}");
        if (headerTotal > 0m && Math.Abs(table.Gross - headerTotal) > HeaderTol)
            return No($"รวมของตาราง {table.Gross:N2} ≠ ยอดรวมหัวใบ {headerTotal:N2}");
        if (lines.Any(l => l.PrintedAmount < 0m))
            return No("มีบรรทัดติดลบ (ส่วนลดเป็นบรรทัด) — แบ่งตามรหัสไม่ได้");

        // ── 1) กลุ่มของแต่ละบรรทัด: อัตราที่ตั้งแล้ว > รหัสที่ engine ส่งมาเป็น "อัตรา" > รหัสบนแถวสินค้า ──
        var codes = table.Groups.Select(g => g.PaperCode).ToHashSet(StringComparer.Ordinal);
        var kindOfCode = table.Groups.GroupBy(g => g.PaperCode).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        var kinds = new OcrVatGroupKind?[n];
        var marks = ReadRowCodes(rawText, codes);
        var j = 0;
        var fromRow = 0;
        for (var i = 0; i < n; i++)
        {
            if (lines[i].PresetRate is decimal pr)
            {
                var asCode = pr == decimal.Truncate(pr) ? pr.ToString("0", CultureInfo.InvariantCulture) : null;
                if (KindOfRate(pr) is null && asCode != null && kindOfCode.TryGetValue(asCode, out var ck))
                {
                    kinds[i] = ck; replace[i] = true;
                    continue;
                }
                if (KindOfRate(pr) is OcrVatGroupKind pk) { kinds[i] = pk; continue; }
                return No($"บรรทัดที่ {i + 1} มีอัตรา {pr:0.##}% ที่ไม่ใช่อัตราไทยและไม่ใช่รหัสบนตาราง");
            }
            var a = Math.Round(lines[i].PrintedAmount, 2, R);
            for (var k = j; k < marks.Count; k++)
            {
                if (marks[k].Amount != a) continue;
                kinds[i] = kindOfCode[marks[k].Code];
                j = k + 1; fromRow++;
                break;
            }
        }

        // ── 2) ราคารวม/ก่อน VAT: Σ บรรทัด − ส่วนลด = แถว "รวม" หรือ ฐาน ของตาราง ──
        var sum = lines.Sum(l => l.PrintedAmount);
        var sumTol = Math.Max(0.02m, 0.01m * lines.Count(l => l.PrintedAmount != 0m));
        bool inclusive;
        if (table.Vat > 0m && Math.Abs(sum - headerDiscount - table.Gross) <= sumTol) inclusive = true;
        else if (Math.Abs(sum - headerDiscount - table.Net) <= sumTol) inclusive = false;
        else
            return No($"Σ บรรทัด {sum:N2}{(headerDiscount > 0m ? $" − ส่วนลด {headerDiscount:N2}" : "")} ไม่เท่าตาราง (ฐาน {table.Net:N2} · รวม {table.Gross:N2}) "
                + "— อ่านบรรทัดขาด/เกิน หรือส่วนลดอยู่ในบรรทัด");
        if (table.Vat > 0m && inclusive != reconPricesIncludeVat)
            return No($"ตารางบอกว่ายอดบรรทัด{(inclusive ? "รวม" : "ไม่รวม")} VAT แต่ตัวกระทบยอดตัดสินตรงข้าม");
        var tableAmt = inclusive ? table.Gross : table.Net;
        if (headerDiscount > 0m && (reconTargetLineSum is not decimal tgt || Math.Abs(tgt - tableAmt) > HeaderTol))
            return No($"ส่วนลดท้ายบิล {headerDiscount:N2} แต่ตัวกระทบยอดไม่ได้กระจายส่วนลดไปที่ {tableAmt:N2}");

        decimal Target(OcrVatGroupKind k) => table.Groups.Where(g => g.Kind == k).Sum(g => inclusive ? g.Gross : g.Net);
        var tableKinds = table.Groups.Select(g => g.Kind).Distinct().ToList();
        if (kinds.Any(k => k is OcrVatGroupKind kk && !tableKinds.Contains(kk)))
            return No("บรรทัดที่ตั้งอัตราไว้แล้วอยู่กลุ่มที่ตารางไม่มี");

        var free = Enumerable.Range(0, n).Where(i => kinds[i] is null && lines[i].PrintedAmount != 0m).ToList();
        decimal Fixed(OcrVatGroupKind k) => Enumerable.Range(0, n).Where(i => kinds[i] == k).Sum(i => lines[i].PrintedAmount);
        string source;

        // ── 3) บรรทัดที่ไม่มีรหัส: ผลรวมของตารางบังคับ "ทางเดียว" (ไม่มีส่วนลด · สองกลุ่ม) ──
        if (free.Count > 0)
        {
            if (headerDiscount > 0m)
                return No($"มีส่วนลดท้ายบิล {headerDiscount:N2} และ {free.Count} บรรทัดไม่มีรหัส ภ.พ. — แบ่งส่วนลดตามรหัสไม่ได้");
            if (tableKinds.Count != 2 || !tableKinds.Contains(OcrVatGroupKind.Standard7))
                return No($"{free.Count} บรรทัดไม่มีรหัส ภ.พ. และตารางมี {tableKinds.Count} กลุ่ม — แบ่งด้วยผลรวมได้เฉพาะสองกลุ่ม (7% + อีกกลุ่ม)");
            if (free.Count > MaxFreeLines)
                return No($"{free.Count} บรรทัดไม่มีรหัส ภ.พ. (เกิน {MaxFreeLines}) — ไม่ค้นทางแบ่ง");
            var other = tableKinds.Single(k => k != OcrVatGroupKind.Standard7);
            var need = Target(other) - Fixed(other);
            var solutions = SubsetsSumming(free.Select(i => lines[i].PrintedAmount).ToList(), need, 2);
            if (solutions.Count != 1)
                return No(solutions.Count == 0
                    ? $"ไม่มีชุดบรรทัดที่รวมได้ยอดกลุ่ม{KindText(other)} {need:N2} พอดี"
                    : $"บรรทัดรวมได้ยอดกลุ่ม{KindText(other)} {need:N2} ได้มากกว่าหนึ่งทาง — ไม่เดาว่าบรรทัดไหน");
            var picked = solutions[0];
            for (var f = 0; f < free.Count; f++)
                kinds[free[f]] = picked.Contains(f) ? other : OcrVatGroupKind.Standard7;
            source = $"บรรทัดที่ไม่มีรหัส {free.Count} บรรทัดแบ่งด้วยผลรวมของตาราง (ทางเดียวที่ลงตัว)";
        }
        else
            source = fromRow > 0 ? $"รหัสที่พิมพ์บนแถวสินค้า {fromRow} บรรทัด" : "รหัส/อัตราที่ชั้นบนตั้งไว้";

        // ── 4) ต่อกลุ่ม: Σ บรรทัด − ส่วนลดของกลุ่ม = ตาราง · Σ ส่วนลด = ส่วนลดท้ายบิล ──
        decimal[]? after = null, pcts = null;
        var discByKind = new Dictionary<OcrVatGroupKind, decimal>();
        foreach (var k in tableKinds)
        {
            var have = Fixed(k);
            var d = have - Target(k);
            var kTol = Math.Max(0.02m, 0.01m * Enumerable.Range(0, n).Count(i => kinds[i] == k && lines[i].PrintedAmount != 0m));
            if (headerDiscount <= 0m)
            {
                if (Math.Abs(d) > kTol)
                    return No($"กลุ่ม{KindText(k)}: Σ บรรทัด {have:N2} ≠ ตาราง {Target(k):N2}");
                continue;
            }
            if (d < -kTol)
                return No($"กลุ่ม{KindText(k)}: Σ บรรทัด {have:N2} น้อยกว่าตาราง {Target(k):N2} — อ่านบรรทัดขาด");
            discByKind[k] = Math.Max(0m, d);
        }
        if (headerDiscount > 0m)
        {
            var dSum = discByKind.Values.Sum();
            if (Math.Abs(dSum - headerDiscount) > HeaderTol)
                return No($"ส่วนลดที่ตารางบอกต่อกลุ่มรวม {dSum:N2} ≠ ส่วนลดท้ายบิลบนกระดาษ {headerDiscount:N2}");
            after = lines.Select(l => l.PrintedAmount).ToArray();
            pcts = new decimal[n];
            foreach (var (k, d) in discByKind)
            {
                if (d <= 0m) continue;
                var idx = Enumerable.Range(0, n).Where(i => kinds[i] == k && lines[i].PrintedAmount != 0m).ToList();
                var baseSum = idx.Sum(i => lines[i].PrintedAmount);
                var pct = Math.Round(d / baseSum * 100m, 2, R);
                decimal given = 0m;
                for (var x = 0; x < idx.Count; x++)
                {
                    var i = idx[x];
                    var share = x == idx.Count - 1 ? d - given : Math.Round(d * lines[i].PrintedAmount / baseSum, 2, R);
                    given += share;
                    after[i] = lines[i].PrintedAmount - share;
                    pcts[i] = pct;
                }
            }
        }

        for (var i = 0; i < n; i++)
            if (kinds[i] is OcrVatGroupKind k && (lines[i].PresetRate is null || replace[i]))
                rates[i] = RateOf(k);
        var groupsText = string.Join(" · ", tableKinds.Select(k =>
            $"{KindText(k)} {Target(k):N2}" + (discByKind.TryGetValue(k, out var dk) && dk > 0m ? $" (ส่วนลด {dk:N2})" : "")));
        var engineText = replace.Any(x => x) ? $" · อัตราจาก engine {replace.Count(x => x)} บรรทัดเป็นรหัส ภ.พ. (ไม่ใช่ %) ⇒ แทนด้วยอัตราตามรหัส" : "";
        return new OcrVatCodePlan(OcrVatCodeVerdict.Applied, rates, replace, after, pcts, inclusive,
            $"อัตรา VAT รายบรรทัดตามตารางรหัส ภ.พ. บนกระดาษ ({source}{engineText}): {groupsText} — ยอด{(inclusive ? "รวม" : "ก่อน")} VAT ต่อกลุ่มตรงตาราง");
    }

    /// <summary>
    /// **ด่านผลสุดท้าย**: บรรทัดที่จะเขียนจริง แบ่งตามอัตราแล้ว Σ ฐานต่อกลุ่ม = ฐานต่อกลุ่มในตารางรหัส ภ.พ. ไหม — ไม่ว่าอัตรามาจากชั้นไหน
    /// (engine · ผู้ใช้ · ตัวเดาจากชื่อ) · null = ตรง / กระดาษไม่มีตารางผสม · ข้อความ (ไม่มีแท็ก) = ผู้เรียกเขียนเป็น <c>[Σ-GAP]</c>
    /// <para>ตารางที่มีรหัสไม่รู้ความหมาย ⇒ ฟ้องเสมอ (อัตรารายบรรทัดพิสูจน์ไม่ได้ — ไม่เดา ให้คนดู) · ไม่แก้ตัวเลขใด ๆ</para>
    /// </summary>
    public static string? TableMismatch(IReadOnlyList<OcrPlannedLine> planned, OcrVatGroupTable table)
    {
        if (!IsMixed(table) || planned.Count == 0) return null;
        if (!table.AllKnown)
            return $"{MismatchLead}: ตารางมีรหัสที่กระดาษไม่บอกความหมาย ("
                + string.Join(" · ", table.Groups.Where(g => g.Kind == OcrVatGroupKind.Unknown).Select(g => g.Evidence))
                + ") — อัตรารายบรรทัดยังมาจากการเดา ตรวจกับคอลัมน์รหัส ภ.พ. บนกระดาษก่อนอนุมัติ";
        var problems = new List<string>();
        var kinds = table.Groups.Select(g => g.Kind).Distinct().ToList();
        foreach (var k in kinds)
        {
            var mine = planned.Where(l => KindOfRate(l.VatRate) == k).ToList();
            var doc = mine.Sum(l => l.NetAmount);
            var paper = table.Groups.Where(g => g.Kind == k).Sum(g => g.Net);
            if (Math.Abs(doc - paper) > Math.Max(0.02m, 0.01m * mine.Count))
                problems.Add($"{KindText(k)} เอกสาร {doc:N2} · กระดาษ {paper:N2}");
        }
        var stray = planned.Where(l => l.NetAmount != 0m && (KindOfRate(l.VatRate) is not OcrVatGroupKind sk || !kinds.Contains(sk))).ToList();
        if (stray.Count > 0)
            problems.Add($"บรรทัดอัตราที่ตารางไม่มี {stray.Count} บรรทัด ({string.Join("/", stray.Select(l => RateText(l.VatRate)).Distinct())}) รวม {stray.Sum(l => l.NetAmount):N2}");
        return problems.Count == 0 ? null
            : $"{MismatchLead}: {string.Join(" · ", problems)} — แก้อัตรา VAT รายบรรทัดตามคอลัมน์รหัส ภ.พ. ({table.Evidence})";
    }

    /// <summary>ชุดดัชนีที่ผลรวมยอด = <paramref name="target"/> ถึงสตางค์ — หยุดเมื่อพบครบ <paramref name="limit"/> ชุด (พอรู้ว่า "ทางเดียว" ไหม)</summary>
    private static List<HashSet<int>> SubsetsSumming(IReadOnlyList<decimal> amounts, decimal target, int limit)
    {
        var cents = amounts.Select(a => (long)Math.Round(a * 100m, 0, R)).ToArray();
        var goal = (long)Math.Round(target * 100m, 0, R);
        var found = new List<HashSet<int>>();
        if (goal < 0 || cents.Any(c => c < 0)) return found;
        // ผลรวมคงเหลือของบรรทัดที่ยังไม่พิจารณา — ตัดกิ่งที่ไปไม่ถึงเป้า
        var suffix = new long[cents.Length + 1];
        for (var i = cents.Length - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + cents[i];
        var chosen = new Stack<int>();
        void Walk(int i, long acc)
        {
            if (found.Count >= limit) return;
            if (i == cents.Length)
            {
                if (acc == goal) found.Add(chosen.ToHashSet());
                return;
            }
            if (acc > goal || acc + suffix[i] < goal) return;
            chosen.Push(i);
            Walk(i + 1, acc + cents[i]);
            chosen.Pop();
            Walk(i + 1, acc);
        }
        Walk(0, 0);
        return found;
    }

    private static string KindText(OcrVatGroupKind k) => k switch
    {
        OcrVatGroupKind.Standard7 => "มี VAT 7%",
        OcrVatGroupKind.ZeroRated => "อัตรา 0%",
        OcrVatGroupKind.Exempt => "ยกเว้น VAT",
        _ => "ไม่รู้ความหมาย",
    };

    private static string RateText(decimal r) => r == ThaiVatTypeRule.ExemptRate ? "ยกเว้น" : $"{r:0.##}%";
}
