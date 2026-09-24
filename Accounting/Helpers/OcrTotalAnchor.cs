using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลของขั้น "หายอดรวมทั้งสิ้น" — ค่าที่ไม่ใช่ <see cref="Proven"/> <b>ห้ามแตะค่าของ engine</b></summary>
public enum OcrTotalVerdict
{
    /// <summary>ไม่ได้ตรวจ (ไม่มีข้อความ / มาจาก e-Tax XML ที่ลงนาม — XML ชนะ)</summary>
    NotChecked = 0,
    /// <summary>ไม่มียอดไหนมีหลักฐานพอ — พฤติกรรมเดิมทุกประการ (ไม่แตะ ไม่ติดแท็ก)</summary>
    Unknown = 1,
    /// <summary>ยอดที่ engine อ่าน<b>คือ</b>ยอดรวมทั้งสิ้นที่พิสูจน์แล้ว — ไม่ต้องทำอะไร</summary>
    Confirmed = 2,
    /// <summary>ยอดอื่นพิสูจน์แล้ว (≥2 ชั้นอิสระ รวมชั้น VAT) <b>และ</b>ค่าที่ engine อ่านถูกอธิบายบทบาทได้ ⇒ เขียนทับได้</summary>
    Proven = 3,
    /// <summary>มีหลักฐานแข็งที่ขัดกันและอธิบายกันไม่ได้ — ไม่แตะค่า · <c>[TOTAL-CONFLICT]</c> ห้ามอนุมัติเอง</summary>
    Conflict = 4,
    /// <summary>ยอดอื่นมีหลักฐานมากกว่าแต่ยังไม่ถึงขั้นพิสูจน์ (ไม่มีชั้น VAT) — ไม่แตะค่า · ลดความมั่นใจ · <c>[TOTAL-UNSURE]</c></summary>
    Unsure = 5,
}

/// <summary>บทบาทของยอดที่<b>แพ้</b> (ค่าที่ engine อ่านเมื่อไม่ใช่ยอดรวมทั้งสิ้น) — ต้องอธิบายได้ก่อนเขียนทับ</summary>
public enum OcrTotalRole
{
    /// <summary>อธิบายไม่ได้ — ห้ามเขียนทับ (G3: ไม่รู้ = บอกว่าไม่รู้)</summary>
    Unknown = 0,
    /// <summary>ยอดรวมทั้งสิ้นของใบกำกับ (ยอดที่ VAT ถูกคิด)</summary>
    TaxInvoiceTotal = 1,
    /// <summary>ยอดก่อนหักส่วนลด (Makro: ป้าย "TOTAL 24,110.00" แล้วหักส่วนลด 297.75)</summary>
    PreDiscountTotal = 2,
    /// <summary>เงินที่จ่ายจริงหลังการปรับที่อยู่นอกใบกำกับ (Shopee: 536 − ส่วนลดพิเศษ 98 = 438)</summary>
    AmountSettled = 3,
    /// <summary>ยอดหลังหัก ณ ที่จ่ายที่กระดาษพิมพ์</summary>
    WhtDeducted = 4,
    // 5 เว้นไว้: ใบหักมัดจำไม่ถูกตัดสินที่ชั้นนี้เลย (ดู Find) — ไม่มีบทบาท "หลังหักมัดจำ"
    /// <summary>ยอดก่อน VAT (engine หยิบฐานมาเป็นยอดรวม)</summary>
    NetBeforeVat = 6,
}

/// <summary>ชนิดหลักฐานของยอดหนึ่ง — จัดเป็น "ชั้นอิสระ" 4 ชั้น: VAT · ตัวอักษร · ป้าย · แถวชำระ</summary>
public enum OcrTotalEvidenceKind
{
    /// <summary>ชั้น VAT: ฐานที่พิมพ์ + VAT ที่พิมพ์ = ยอดนี้ หรือ VAT ที่พิมพ์ = 7/107 ของยอดนี้</summary>
    VatClosure = 1,
    /// <summary>ชั้น VAT: แถว "รวม" ของตารางสรุปตามกลุ่มภาษี</summary>
    VatSummaryTotal = 2,
    /// <summary>ชั้นตัวอักษร: จำนวนเงินตัวอักษรบนกระดาษ</summary>
    AmountInWords = 3,
    /// <summary>ชั้นป้าย: รวมทั้งสิ้น / จำนวนเงินรวมสุทธิ / NET AMOUNT / TOTAL / ยอดชำระ</summary>
    GrandTotalLabel = 4,
    /// <summary>ชั้นแถวชำระ: บัตร/โอน/QR/CC_… (ไม่รวมเงินสดรับ/เงินทอน — บทเรียน Makro 951/49/1,000)</summary>
    PaymentRow = 5,
}

/// <summary>หลักฐานหนึ่งชิ้นของยอดหนึ่ง — <paramref name="PaperText"/> = ข้อความบนกระดาษที่ยืนยัน</summary>
public readonly record struct OcrTotalEvidence(OcrTotalEvidenceKind Kind, string PaperText);

/// <summary>ผู้สมัครยอดรวมหนึ่งตัว + หลักฐานทุกชิ้น</summary>
/// <param name="IndependentClasses">จำนวน<b>ชั้น</b>อิสระที่ชี้ยอดนี้ (ป้ายสองแถวที่ยอดเดียวกัน = 1 ชั้น)</param>
/// <param name="HasVatEvidence">มีชั้น VAT (ยอดนี้คือยอดที่ VAT ถูกคิด) — เงื่อนไขบังคับของ "พิสูจน์แล้ว"</param>
public sealed record OcrTotalCandidate(
    decimal Amount, IReadOnlyList<OcrTotalEvidence> Evidence, int IndependentClasses, bool HasVatEvidence)
{
    /// <summary>พิสูจน์แล้ว = ชั้น VAT + อีกอย่างน้อยหนึ่งชั้นอิสระ</summary>
    public bool Strong => HasVatEvidence && IndependentClasses >= OcrTotalAnchor.ProvenMinClasses;
}

/// <summary>ผลของ <see cref="OcrTotalAnchor.Find"/></summary>
/// <param name="Total">ยอดรวมทั้งสิ้นที่ยึด (Confirmed/Proven) · ยอดที่ขัด (Conflict/Unsure) · null = ไม่รู้</param>
/// <param name="EngineRole">บทบาทของค่าที่ engine อ่าน เมื่อค่านั้นไม่ใช่ยอดรวมทั้งสิ้น</param>
/// <param name="PrintedBase">ฐานก่อน VAT ที่พิมพ์คู่กับยอดรวม (จากตารางกลุ่ม/แถวฐาน) — null = ไม่มีบนกระดาษ</param>
/// <param name="PrintedVat">VAT ที่พิมพ์ซึ่งปิดยอดรวม</param>
/// <param name="Reason">เหตุผลภาษาไทยพร้อมตัวเลข — ใส่ trace/หมายเหตุได้ตรง ๆ</param>
public sealed record OcrTotalAnchorResult(
    OcrTotalVerdict Verdict,
    decimal? Total,
    decimal? EngineTotal,
    OcrTotalRole EngineRole,
    decimal? PrintedBase,
    decimal? PrintedVat,
    IReadOnlyList<OcrTotalCandidate> Candidates,
    string Reason);

/// <summary>
/// **ขั้นที่ 1 ของ Total-first: "ยอดไหนคือยอดรวมทั้งสิ้นของเอกสาร"** (pure · ไม่มี I/O · ไม่ throw)
///
/// ═══ ที่มา (รอบ 192 · เจ้าของ: "สิ่งแรกที่ต้องถูกต้องก่อนเลยคือการหาว่าอันไหนคือยอด total ของเอกสาร") ═══
/// <para>ทีม B ไล่โค้ดพบว่าระบบ<b>ไม่มีขั้นเลือกยอดรวม</b> — ยอดรวม = ป้ายแรกที่ engine หยิบ แล้วชั้นหลังทำได้แค่
/// เติมเมื่อว่าง/ลดคะแนน ⇒ ใบ Makro ที่ป้าย "TOTAL 24,110.00" เป็นยอด<b>ก่อน</b>ส่วนลด ลงบัญชีเป็น 24,110 ทั้งที่
/// กระดาษยืนยัน 23,812.25 ถึง 5 ทาง (AMOUNT · NET AMOUNT · ตัวอักษร · แถวชำระ · ตารางรหัส ภ.พ.)</para>
///
/// ═══ กติกา (DECISION_DOCTRINE §1 · ทีม C §2) ═══
/// <list type="number">
/// <item><b>ยอดรวมทั้งสิ้น = ยอดที่ VAT ถูกคิด</b> (ใบกำกับ §86/4) — ไม่ใช่ยอดที่จ่าย (ใบ Shopee จ่าย 438 แต่ใบกำกับ 536)</item>
/// <item>หลักฐานนับเป็น<b>ชั้นอิสระ</b> 4 ชั้น (VAT · ตัวอักษร · ป้าย · แถวชำระ) — ป้ายสองแถวยอดเดียวกัน = ชั้นเดียว ·
///   ค่าที่ engine อ่านไม่ใช่ชั้นอิสระ (engine อ่านป้ายเดียวกัน)</item>
/// <item><b>พิสูจน์แล้ว</b> = ชั้น VAT + อีก ≥1 ชั้น · เขียนทับค่า engine ได้<b>เฉพาะ</b>เมื่อค่า engine ถูกอธิบาย
///   บทบาทได้ด้วยตัวเลขบนกระดาษ (ยอดก่อนลด = รวม + ส่วนลดที่พิมพ์ · ยอดจ่าย = รวม − ส่วนลด/ปรับที่พิมพ์ ·
///   หลังหัก ณ ที่จ่าย · หลังหักมัดจำ · ยอดก่อน VAT) — อธิบายไม่ได้ = <see cref="OcrTotalVerdict.Conflict"/></item>
/// <item>แถวเงินสดรับ/เงินทอนไม่ใช่หลักฐาน (Makro 951/49/1,000) · แถวยอด 0 ไม่ใช่หลักฐาน (RG-02) ·
///   ใบไม่มี VAT ไม่มีชั้น VAT ⇒ ไม่มีวัน "พิสูจน์" ⇒ พฤติกรรมเดิม</item>
/// <item>ป้ายสลับ Sub↔Total เป็นหน้าที่ของ <see cref="OcrHeaderAmounts.Normalize"/> (รันก่อนหน้านี้) — ที่นี่ไม่สลับเอง</item>
/// </list>
/// </summary>
public static class OcrTotalAnchor
{
    /// <summary>จำนวนชั้นอิสระขั้นต่ำของ "พิสูจน์แล้ว"</summary>
    public const int ProvenMinClasses = 2;

    /// <summary>ความมั่นใจของยอดที่พิสูจน์แล้ว</summary>
    public const double ProvenConfidence = 0.95;

    /// <summary>เพดานความมั่นใจเมื่อหลักฐานขัดกัน — ต่ำกว่า 0.85 ⇒ ไฮไลต์เหลือง (กฎเหล็ก #3 ข้อ 3)</summary>
    public const double DisputedConfidenceCap = 0.80;

    /// <summary>แท็กห้ามอนุมัติเอง — เจ้าของคือ <see cref="OcrPostingReadiness.BlockingTags"/></summary>
    public const string ConflictTag = "[TOTAL-CONFLICT]";

    /// <summary>แท็กเตือน (ไม่บล็อก)</summary>
    public const string UnsureTag = "[TOTAL-UNSURE]";

    /// <summary>ข้อสังเกตเมื่อยึดยอดใหม่ (ไม่บล็อก)</summary>
    public const string AnchoredTag = "[TOTAL]";

    private const decimal Tol = OcrPaperAmounts.ExactTol;
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>ป้ายยอดรวม/ยอดสุทธิ/ยอดชำระ</summary>
    private static readonly Regex GrandLabel = new(
        @"รวม(?:เงิน)?ทั้งสิ้น|จำนวนเงินรวม(?:ทั้งสิ้น|สุทธิ)|ยอดรวม(?:ทั้งสิ้น|สุทธิ)|รวมสุทธิ|ยอดสุทธิ|จำนวนเงินสุทธิ|"
        + @"ยอดชำระ|ที่ต้องชำระ|ชำระสุทธิ|grand[ \t]*total|total[ \t]*amount|net[ \t]*amount|amount[ \t]*due|net[ \t]*total|"
        + @"(?<![A-Za-z])total(?![A-Za-z])", Opt);

    /// <summary>แถวที่มีคำว่ารวม/total แต่เป็นยอดย่อย (ส่วนลด · ก่อนภาษี · ยกเว้น · มัดจำ · หัก ณ ที่จ่าย · sub-total)</summary>
    private static readonly Regex NotGrand = new(
        @"ส่วนลด|discount|sub[ \t-]*total|ก่อน[ \t]*(?:หัก|ภาษี)|excl|exempt|ยกเว้น|มัดจำ|deposit|หัก[ \t]*ณ[ \t]*ที่[ \t]*จ่าย|withholding|"
        + @"ไม่รวม|จำนวนชิ้น|qty|quantity|รายการ|vatable|taxable|non[- \t]?vat|มีภาษี|ต้องเสียภาษี|ไม่เสียภาษี", Opt);

    /// <summary>แถวชำระที่ไม่ใช่เงินสด</summary>
    private static readonly Regex PaymentRow = new(
        @"(?<![A-Za-z])CC_|credit[ \t]*card|debit[ \t]*card|(?<![A-Za-z])card(?![A-Za-z])|บัตรเครดิต|บัตรเดบิต|โอนเงิน|เงินโอน|"
        + @"promptpay|พร้อมเพย์|(?<![A-Za-z])qr(?![A-Za-z])|^[ \t]*ช่องทาง|ยอดเงินรวม|ชำระโดย|paid[ \t]*by", Opt);

    /// <summary>แถวเงินสดรับ/เงินทอน — ไม่ใช่หลักฐานของยอดรวม (Tendered ≠ Total)</summary>
    private static readonly Regex TenderRow = new(
        @"เงินสด|(?<![A-Za-z])cash(?![A-Za-z])|รับเงิน|tender|เงินทอน|ทอน|(?<![A-Za-z])change(?![A-Za-z])", Opt);

    private static readonly Regex PureMoneyLine = new(
        @"^[ \t]*\(?(?:\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2})\)?[ \t]*(?:บาท|฿|thb)?[ \t]*$", Opt);

    /// <param name="rawText">ข้อความทั้งใบ (normalize แล้ว)</param>
    /// <param name="engineTotal">ยอดรวมที่ไปป์ไลน์ถืออยู่ตอนนี้ (หลังซ่อมป้ายสลับแล้ว)</param>
    /// <param name="engineAmountDue">ยอด AmountDue ของ Azure (ถ้ามี) — ใช้เป็นผู้สมัครเพิ่ม ไม่ใช่หลักฐาน</param>
    public static OcrTotalAnchorResult Find(string? rawText, decimal? engineTotal, decimal? engineAmountDue = null)
    {
        var e = engineTotal is > 0m ? engineTotal : null;
        if (string.IsNullOrWhiteSpace(rawText))
            return new(OcrTotalVerdict.NotChecked, null, e, OcrTotalRole.Unknown, null, null,
                Array.Empty<OcrTotalCandidate>(), "ไม่มีข้อความให้ตรวจยอดรวม");

        // ใบที่หักเงินมัดจำ (มีเงินจริง) — ความหมายของ "ยอดหลังหักมัดจำ" เป็นของ OcrDepositMarker/วงจรมัดจำ
        // ชั้นนี้ไม่ตัดสินแทน (ทีม C §2.3 ข้อ 4) · แถวฟอร์ม "หักเงินมัดจำ 0.00" ไม่นับ (RG-02)
        if (OcrPaperAmounts.DepositRows(rawText).Count > 0)
            return new(OcrTotalVerdict.Unknown, null, e, OcrTotalRole.Unknown, null, null,
                Array.Empty<OcrTotalCandidate>(), "ใบมีแถวหักเงินมัดจำ — ไม่ตัดสินยอดรวมแทนวงจรมัดจำ คงค่าที่อ่านได้");

        var lines = OcrPaperAmounts.Lines(rawText);
        var printed = OcrPaperAmounts.AllPrinted(rawText);
        var vats = OcrPaperAmounts.VatAmounts(rawText);
        var table = OcrLineVatMarks.ReadGroups(rawText);
        var words = ThaiAmountInWords.FindInText(rawText);

        // ── เก็บผู้สมัคร + หลักฐานชั้นป้าย/ชำระ/ตัวอักษร ──
        var evidence = new List<(decimal Amount, OcrTotalEvidence Ev)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (TenderRow.IsMatch(line)) continue;
            if (PaymentRow.IsMatch(line))
            {
                foreach (var paid in OcrPaperAmounts.MoneyOn(line).Where(m => m > 0m).Distinct())
                    evidence.Add((paid, new OcrTotalEvidence(OcrTotalEvidenceKind.PaymentRow, line.Trim())));
                continue;
            }
            var gm = GrandLabel.Match(line);
            if (!gm.Success || NotGrand.IsMatch(line)) continue;
            var after = OcrPaperAmounts.MoneyOn(line[(gm.Index + gm.Length)..]);
            decimal? amt = after.Count > 0 ? after[after.Count - 1] : null;
            var text = line.Trim();
            // ป้ายกับตัวเลขคนละบรรทัด (engine แยก cell) — รับเฉพาะบรรทัดถัดไปที่เป็นตัวเลขล้วน
            if (amt is null && i + 1 < lines.Count && PureMoneyLine.IsMatch(lines[i + 1]))
            {
                var next = OcrPaperAmounts.MoneyOn(lines[i + 1]);
                if (next.Count > 0) { amt = next[next.Count - 1]; text += " / " + lines[i + 1].Trim(); }
            }
            if (amt is > 0m) evidence.Add((amt.Value, new OcrTotalEvidence(OcrTotalEvidenceKind.GrandTotalLabel, text)));
        }
        if (words is > 0m)
            evidence.Add((words.Value, new OcrTotalEvidence(OcrTotalEvidenceKind.AmountInWords, "จำนวนเงินตัวอักษรบนกระดาษ")));

        var amounts = new List<decimal>();
        void AddAmount(decimal? a)
        {
            if (a is > 0m && !amounts.Any(x => Math.Abs(x - a.Value) <= Tol)) amounts.Add(a.Value);
        }
        foreach (var item in evidence) AddAmount(item.Amount);
        AddAmount(e);
        AddAmount(engineAmountDue);
        if (table.Found) AddAmount(table.Gross);

        // ── ชั้น VAT ของแต่ละผู้สมัคร ──
        var candidates = new List<OcrTotalCandidate>();
        var closures = new Dictionary<int, (decimal Base, decimal Vat)>();
        for (var k = 0; k < amounts.Count; k++)
        {
            var x = amounts[k];
            var ev = evidence.Where(t => Math.Abs(t.Amount - x) <= Tol).Select(t => t.Ev).ToList();
            if (table.Found && Math.Abs(table.Gross - x) <= Tol)
            {
                ev.Add(new(OcrTotalEvidenceKind.VatSummaryTotal, table.Evidence));
                closures[k] = (table.Net, table.Vat);
            }
            foreach (var v in vats)
            {
                var b = x - v.Amount;
                if (b > 0m && OcrPaperAmounts.IsPrinted(printed, b))
                {
                    ev.Add(new(OcrTotalEvidenceKind.VatClosure, $"ฐาน {b:N2} + VAT {v.Amount:N2} = {x:N2} (พิมพ์ทั้งคู่)"));
                    if (!closures.ContainsKey(k)) closures[k] = (b, v.Amount);
                    break;
                }
                if (Math.Abs(Math.Round(x * 7m / 107m, 2, MidpointRounding.AwayFromZero) - v.Amount) <= Tol)
                {
                    ev.Add(new(OcrTotalEvidenceKind.VatClosure, $"VAT {v.Amount:N2} = 7/107 ของ {x:N2}"));
                    if (!closures.ContainsKey(k)) closures[k] = (x - v.Amount, v.Amount);
                    break;
                }
            }
            var hasVat = ev.Any(t => t.Kind is OcrTotalEvidenceKind.VatClosure or OcrTotalEvidenceKind.VatSummaryTotal);
            var classes = (hasVat ? 1 : 0)
                + (ev.Any(t => t.Kind == OcrTotalEvidenceKind.AmountInWords) ? 1 : 0)
                + (ev.Any(t => t.Kind == OcrTotalEvidenceKind.GrandTotalLabel) ? 1 : 0)
                + (ev.Any(t => t.Kind == OcrTotalEvidenceKind.PaymentRow) ? 1 : 0);
            candidates.Add(new OcrTotalCandidate(x, ev, classes, hasVat));
        }

        OcrTotalCandidate? Of(decimal? a) => a is null ? null
            : candidates.FirstOrDefault(c => Math.Abs(c.Amount - a.Value) <= Tol);
        (decimal? B, decimal? V) ClosureOf(OcrTotalCandidate c)
        {
            if (closures.TryGetValue(candidates.IndexOf(c), out var p)) return (p.Base, p.Vat);
            return (null, null);
        }

        var strong = candidates.Where(c => c.Strong).ToList();
        var eCand = Of(e);

        // ── engine อ่านถูกอยู่แล้ว ──
        if (eCand is { Strong: true })
        {
            var rivals = strong.Where(c => c != eCand
                && Explain(c.Amount, eCand.Amount, rawText, vats) == OcrTotalRole.Unknown).ToList();
            if (rivals.Count == 0)
            {
                var (b0, v0) = ClosureOf(eCand);
                return new(OcrTotalVerdict.Confirmed, eCand.Amount, e, OcrTotalRole.TaxInvoiceTotal, b0, v0, candidates,
                    $"ยอดรวมทั้งสิ้น {eCand.Amount:N2} ยืนยัน {eCand.IndependentClasses} ทาง: {Describe(eCand)}");
            }
            return Conflict(rivals[0], eCand, e, candidates);
        }

        // ── ยอดอื่นพิสูจน์แล้ว ──
        if (strong.Count == 1)
        {
            var winner = strong[0];
            var (wb, wv) = ClosureOf(winner);
            if (e is null)
                return new(OcrTotalVerdict.Proven, winner.Amount, null, OcrTotalRole.Unknown, wb, wv, candidates,
                    $"ยอดรวมทั้งสิ้น {winner.Amount:N2} ยืนยัน {winner.IndependentClasses} ทาง: {Describe(winner)} (engine ไม่ได้ยอดรวม)");
            var role = Explain(e.Value, winner.Amount, rawText, vats);
            if (role == OcrTotalRole.Unknown)
                return Conflict(winner, eCand, e, candidates);
            return new(OcrTotalVerdict.Proven, winner.Amount, e, role, wb, wv, candidates,
                $"ยอดรวมทั้งสิ้น {winner.Amount:N2} ยืนยัน {winner.IndependentClasses} ทาง: {Describe(winner)} — "
                + $"ค่าที่อ่านได้เดิม {e.Value:N2} คือ{RoleText(role, e.Value, winner.Amount)}");
        }
        if (strong.Count >= 2)
            return Conflict(strong[0], strong[1], e, candidates);

        // ── ไม่มีใครพิสูจน์ได้ — เตือนเฉพาะเมื่อยอดอื่นมีหลักฐานแข็ง (ตัวอักษร) มากกว่าค่า engine ชัดเจน ──
        if (e is not null)
        {
            var eHard = eCand is not null && (eCand.HasVatEvidence
                || eCand.Evidence.Any(t => t.Kind == OcrTotalEvidenceKind.AmountInWords));
            var better = candidates.Where(c => Math.Abs(c.Amount - e.Value) > Tol && c.IndependentClasses >= ProvenMinClasses
                    && c.Evidence.Any(t => t.Kind == OcrTotalEvidenceKind.AmountInWords)
                    && Explain(e.Value, c.Amount, rawText, vats) == OcrTotalRole.Unknown)
                .ToList();
            if (!eHard && better.Count == 1)
                return new(OcrTotalVerdict.Unsure, better[0].Amount, e, OcrTotalRole.Unknown, null, null, candidates,
                    $"ยอดรวมที่อ่านได้ {e.Value:N2} ไม่มีหลักฐานบนกระดาษยืนยัน แต่ {better[0].Amount:N2} มี "
                    + $"{better[0].IndependentClasses} ทาง ({Describe(better[0])}) — ยังไม่ถึงขั้นพิสูจน์ (ไม่มียอด VAT ยืนยัน) "
                    + "ระบบจึงไม่เปลี่ยนให้ ตรวจกับกระดาษก่อนอนุมัติ");
        }
        return new(OcrTotalVerdict.Unknown, null, e, OcrTotalRole.Unknown, null, null, candidates,
            "ไม่มียอดใดมีหลักฐานพอจะยึดเป็นยอดรวมทั้งสิ้น — คงค่าที่อ่านได้");
    }

    /// <summary>ข้อความหนึ่งบรรทัดสำหรับ ProcessingNotes ตามผลตัดสิน — null = ไม่ต้องเขียนอะไร</summary>
    public static string? Note(OcrTotalAnchorResult r) => r.Verdict switch
    {
        OcrTotalVerdict.Proven => $"{AnchoredTag} {r.Reason}",
        OcrTotalVerdict.Conflict => $"{ConflictTag} {r.Reason}",
        OcrTotalVerdict.Unsure => $"{UnsureTag} {r.Reason}",
        _ => null,
    };

    /// <summary>บทบาทของ <paramref name="loser"/> เมื่อยอดรวมทั้งสิ้นคือ <paramref name="total"/> —
    /// ต้องอธิบายด้วยตัวเลขที่พิมพ์บนกระดาษเท่านั้น</summary>
    private static OcrTotalRole Explain(decimal loser, decimal total, string rawText, IReadOnlyList<OcrPrintedAmount> vats)
    {
        var diff = loser - total;
        if (Math.Abs(diff) <= Tol) return OcrTotalRole.TaxInvoiceTotal;
        var discounts = OcrPaperAmounts.DiscountRows(rawText);
        if (diff > 0m && discounts.Any(d => Math.Abs(d.Amount - diff) <= Tol)) return OcrTotalRole.PreDiscountTotal;
        if (diff < 0m)
        {
            var gap = -diff;
            if (discounts.Any(d => Math.Abs(d.Amount - gap) <= Tol)) return OcrTotalRole.AmountSettled;
            if (PaperWhtReader.Read(rawText).Amount is decimal w && Math.Abs(w - gap) <= Tol) return OcrTotalRole.WhtDeducted;
            if (vats.Any(v => Math.Abs(v.Amount - gap) <= Tol)) return OcrTotalRole.NetBeforeVat;
        }
        return OcrTotalRole.Unknown;
    }

    private static OcrTotalAnchorResult Conflict(
        OcrTotalCandidate a, OcrTotalCandidate? b, decimal? e, IReadOnlyList<OcrTotalCandidate> all)
    {
        var bText = b is null
            ? $"ค่าที่อ่านได้ {e:N2} (ไม่มีหลักฐานบนกระดาษและอธิบายไม่ได้ว่าเป็นยอดอะไร)"
            : $"{b.Amount:N2} ({Describe(b)})";
        return new(OcrTotalVerdict.Conflict, a.Amount, e, OcrTotalRole.Unknown, null, null, all,
            $"พบยอดรวมที่ขัดกัน: {a.Amount:N2} ({Describe(a)}) กับ {bText} — ระบบไม่เลือกให้ "
            + "ตรวจกับกระดาษว่ายอดไหนคือยอดรวมทั้งสิ้นก่อนอนุมัติ");
    }

    private static string Describe(OcrTotalCandidate c)
        => c.Evidence.Count == 0
            ? "ไม่มีหลักฐานบนกระดาษ"
            : string.Join(" · ", c.Evidence.Select(ev => ev.Kind switch
            {
                OcrTotalEvidenceKind.VatSummaryTotal => "ตารางสรุปตามกลุ่มภาษี",
                OcrTotalEvidenceKind.VatClosure => ev.PaperText,
                OcrTotalEvidenceKind.AmountInWords => "จำนวนเงินตัวอักษร",
                OcrTotalEvidenceKind.GrandTotalLabel => $"ป้าย “{Short(ev.PaperText)}”",
                OcrTotalEvidenceKind.PaymentRow => $"แถวชำระ “{Short(ev.PaperText)}”",
                _ => ev.PaperText,
            }).Distinct());

    private static string RoleText(OcrTotalRole role, decimal loser, decimal total) => role switch
    {
        OcrTotalRole.PreDiscountTotal => $"ยอดก่อนหักส่วนลด {loser - total:N2} (ส่วนลดพิมพ์บนกระดาษ)",
        OcrTotalRole.AmountSettled => $"ยอดที่ชำระหลังหักส่วนลด/ปรับ {total - loser:N2} นอกใบกำกับ",
        OcrTotalRole.WhtDeducted => $"ยอดหลังหัก ณ ที่จ่าย {total - loser:N2}",
        OcrTotalRole.NetBeforeVat => $"ยอดก่อน VAT (VAT {total - loser:N2})",
        _ => "ยอดที่อธิบายไม่ได้",
    };

    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
