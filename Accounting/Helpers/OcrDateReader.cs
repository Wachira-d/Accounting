using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ป้ายที่กำกับวันที่ก้อนหนึ่งบนกระดาษ</summary>
public enum OcrDateLabel
{
    /// <summary>ไม่มีป้ายในระยะสายตา</summary>
    None = 0,
    /// <summary>ป้าย "วันที่ / Date / ลงวันที่" = วันที่ของเอกสาร</summary>
    DocumentDate = 1,
    /// <summary>ป้ายของวันที่ชนิดอื่น — ครบกำหนด · พิมพ์ · หมดอายุ · ส่งของ ฯลฯ
    /// (ห้ามเอามาเป็นวันที่เอกสาร: tax point §78 และงวด ภ.พ.30 จะผิด)</summary>
    OtherDate = 2,
}

/// <summary>วันที่หนึ่งก้อนที่พบบนกระดาษ</summary>
/// <param name="Date">การตีความที่เลือก (แบบไทย วัน/เดือน/ปี ก่อน — ถ้าไม่สมเหตุสมผลค่อยใช้แบบอื่น)</param>
/// <param name="Position">ตำแหน่งในข้อความ</param>
/// <param name="Token">ข้อความตามที่พิมพ์บนกระดาษ</param>
/// <param name="Label">ป้ายที่กำกับ</param>
/// <param name="Readings">ทุกการตีความที่เป็นวันที่จริงได้ของก้อนนี้ (วัน/เดือนสลับ · ปี 2 หลักไว้หน้า)
/// — ใช้ตรวจว่า engine "อ่านก้อนเดียวกันแต่เรียงผิด" หรือไม่</param>
/// <param name="Plausible">อยู่ในช่วงที่สมเหตุสมผลเทียบวันอัปโหลดไหม</param>
public sealed record OcrDateCandidate(
    DateTime Date, int Position, string Token, OcrDateLabel Label,
    IReadOnlyList<DateTime> Readings, bool Plausible);

/// <summary>ตัวตรวจทำอะไรกับวันที่ที่ engine ให้มา</summary>
public enum OcrDateVerdict
{
    /// <summary>ไม่แตะ (ไม่มีหลักฐานบนกระดาษที่ดีกว่า)</summary>
    NoChange = 0,
    /// <summary>engine ไม่ได้วันที่ — เติมจากกระดาษ</summary>
    Filled = 1,
    /// <summary>engine กับกระดาษตรงกัน</summary>
    Confirmed = 2,
    /// <summary>engine อ่านผิด (เรียงวัน/เดือน/ปีผิด · หยิบวันครบกำหนด/วันพิมพ์) — ใช้ค่าจากกระดาษ</summary>
    Replaced = 3,
    /// <summary>คงค่าเดิมแต่ลดความมั่นใจให้ขึ้นไฮไลต์ (ห่างจากวันอัปโหลดมาก / ขัดกับป้ายบนกระดาษ)</summary>
    Doubtful = 4,
}

/// <summary>ผลการตรวจวันที่เอกสาร</summary>
/// <param name="Verdict">ทำอะไร</param>
/// <param name="Date">วันที่หลังตรวจ (Kind=Utc เที่ยงคืน = วันตามปฏิทิน)</param>
/// <param name="Confidence">ความมั่นใจของช่องวันที่หลังตรวจ</param>
/// <param name="Reason">เหตุผลภาษาไทย — ลง ReasoningTrace ได้ตรง ๆ</param>
public readonly record struct OcrDateCheck(OcrDateVerdict Verdict, DateTime? Date, decimal Confidence, string Reason)
{
    /// <summary>ค่าที่เติม/ทับมาจากวันที่ที่มี<b>ป้ายวันที่เอกสาร</b>บนกระดาษ (ผู้เรียกใช้เลือกชั้นที่มา)</summary>
    public bool FromPaperLabel { get; init; }
}

/// <summary>
/// **ตัวอ่าน "วันที่เอกสาร" จากข้อความบนกระดาษ + ตัวตรวจค่าที่ engine ให้มา — ตัวเดียวของทุก engine**
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11: "ทั้ง Azure และ local ยังจับวันที่เพี้ยน") ═══
/// ระบบมีตัวอ่านวันที่ <b>6 ชุดที่กติกาต่างกัน</b> — Azure (<c>valueDate</c> ที่ Azure ตีความเอง
/// ตาม locale ของมัน) · python (<c>ai_engine.py</c> รู้จักแต่ปี 4 หลัก หยิบตัวแรกของหน้า) ·
/// <c>ParseThaiDocument</c> (ป้าย "Date" ก็แมตช์ "Due Date") · <c>FieldPatternLibrary</c>
/// (ปี 2 หลัก +2000 ⇒ "69" = 2069) · K-V fallback · zone analyzer — และไม่มีชั้นไหน
/// <b>เทียบกลับกับกระดาษ</b>. ใบจริงสองใบของรอบนี้พิมพ์ปี ค.ศ. 2 หลัก:
/// <c>Date: 18/09/26</c> (Wine Pro) · <c>วันที่ DATE 12/09/26</c> (Radisson) — ตีความได้ 3 แบบ
/// (18 ก.ย. 2026 · 26 ก.ย. 2018 · 9 ธ.ค. 2026) และ engine ที่ใช้ locale อเมริกันเลือกผิดได้เงียบ ๆ
/// เพราะทุกแบบเป็นวันที่ที่ "ดูใช้ได้"</para>
///
/// <para>═══ วิธีคิดที่เพิ่ม (ไม่ได้รื้อตัวเดิม — วางทับเป็นขั้นตรวจท้ายสุด) ═══
/// <list type="number">
/// <item><b>ป้ายชนะตำแหน่ง</b>: วันที่ที่มีป้าย "วันที่/Date" ชนะวันที่ลอย ๆ · วันที่ที่มีป้าย
///   ครบกำหนด/พิมพ์/หมดอายุ/ส่งของ <b>ไม่มีสิทธิ์</b>เป็นวันที่เอกสาร (ป้าย "Due Date" มีคำว่า
///   Date อยู่ข้างใน — ต้องตรวจป้ายชนิดอื่นก่อน)</item>
/// <item><b>แบบไทยก่อน</b>: ตัวเลขสามก้อนอ่านเป็น วัน/เดือน/ปี · ปีผ่าน <see cref="ThaiDate.NormalizeYear"/>
///   ตัวเดียว (69 = พ.ศ. ย่อ · 26 = ค.ศ. ย่อ) · ถ้าแบบไทยไม่สมเหตุสมผลค่อยลองแบบอื่น</item>
/// <item><b>ปีต้องอยู่ในช่วงเทียบวันอัปโหลด</b> (<see cref="MaxPastDays"/> · <see cref="MaxFutureDays"/>)
///   — ใช้<b>เลือกการตีความ</b> และ<b>ลดความมั่นใจ</b> ไม่ใช่ล้างทิ้ง (เอกสารเก่าที่สแกนตามเก็บมีจริง)</item>
/// <item><b>engine อ่านก้อนเดียวกันแต่เรียงผิด</b> (ค่าของ engine เป็นหนึ่งใน <see cref="OcrDateCandidate.Readings"/>
///   ของก้อนที่มีป้าย) ⇒ ใช้แบบไทย · <b>engine หยิบวันที่ที่มีป้ายชนิดอื่น</b> ⇒ ใช้ก้อนที่มีป้ายวันที่เอกสาร</item>
/// <item>ค่าที่ทับ engine ได้คะแนน <see cref="ReplacedConfidence"/> (&lt; 0.85) ⇒ ไฮไลต์เหลืองให้ตรวจ
///   (DECISION_DOCTRINE G4: ชนะแบบยังขัดกัน ห้ามประทับมั่นใจเต็ม)</item>
/// </list></para>
///
/// <para>ไม่ใช้กับ e-Tax XML (ลงนามดิจิทัล = ความจริงตามกฎหมาย ไม่ใช่ผลอ่าน) — ผู้เรียกข้ามเอง ·
/// pure ไม่มี I/O ไม่ throw (DECISION_DOCTRINE G6) · ผู้เรียกส่งข้อความที่ normalize แล้วเข้ามา</para>
/// </summary>
public static class OcrDateReader
{
    /// <summary>เก่ากว่าวันอัปโหลดเกินนี้ = น่าสงสัย (2 ปี: ครอบการสแกนตามเก็บย้อนหลังทั้งปีบัญชี
    /// + ภาษีซื้อยื่นช้าได้ 6 เดือน §82/3 · เกินนี้มักเป็นปีที่ตีความผิด เช่น 26/09/2018)</summary>
    public const int MaxPastDays = 730;

    /// <summary>ล่วงหน้าวันอัปโหลดเกินนี้ = น่าสงสัย (ใบกำกับลงวันที่อนาคตไกลไม่ใช่เรื่องปกติ
    /// · 9 ธ.ค. 2026 ที่อ่านจาก 12/09/26 ตอนอัปโหลด 24 ก.ย. = +76 วัน)</summary>
    public const int MaxFutureDays = 31;

    /// <summary>ป้ายวันที่เอกสาร + อยู่ในช่วง</summary>
    public const decimal LabelledConfidence = 0.90m;
    /// <summary>ไม่มีป้าย แต่วันที่บนกระดาษมีค่าเดียว</summary>
    public const decimal UnlabelledSingleConfidence = 0.80m;
    /// <summary>ไม่มีป้าย และมีหลายวันที่ต่างกัน</summary>
    public const decimal UnlabelledAmbiguousConfidence = 0.60m;
    /// <summary>ทับค่าที่ engine ให้ — ต่ำกว่า 0.85 ตั้งใจ ให้ขึ้นไฮไลต์</summary>
    public const decimal ReplacedConfidence = 0.80m;
    /// <summary>คงค่าเดิมแต่ขัดกับกระดาษ</summary>
    public const decimal ConflictConfidence = 0.60m;
    /// <summary>ห่างจากวันอัปโหลดเกินช่วง</summary>
    public const decimal ImplausibleConfidence = 0.40m;

    /// <summary>ระยะมองย้อนหาป้ายในบรรทัดเดียวกัน (ตัวอักษร)</summary>
    private const int LabelLookBehind = 40;

    // ตัวคั่นระหว่างตัวเลขใช้ [ \t] เท่านั้น — \s กลืนขึ้นบรรทัดใหม่ (tools/regex_line_span_check.py)
    // ใช้ [0-9] ไม่ใช่ \d: .NET ให้ \d แมตช์เลขไทย ๐-๙ ด้วย แล้ว int.Parse โยน exception
    private static readonly Regex NumericDmy = new(
        @"(?<![0-9/.\-])([0-9]{1,2})[ \t]*([/.\-])[ \t]*([0-9]{1,2})[ \t]*\2[ \t]*([0-9]{4}|[0-9]{2})(?![0-9/.\-])",
        RegexOptions.Compiled);
    private static readonly Regex NumericYmd = new(
        @"(?<![0-9/.\-])([0-9]{4})([/.\-])([0-9]{1,2})\2([0-9]{1,2})(?![0-9/.\-])",
        RegexOptions.Compiled);
    private static readonly Regex NamedDayFirst = new(
        @"(?<![0-9])([0-9]{1,2})[ \t]*([ก-๙A-Za-z][ก-๙A-Za-z.]{1,11})[ \t,]*([0-9]{4}|[0-9]{2})(?![0-9])",
        RegexOptions.Compiled);
    private static readonly Regex NamedMonthFirst = new(
        @"(?<![A-Za-z])([A-Za-z]{3,9})\.?[ \t]+([0-9]{1,2})(?:st|nd|rd|th)?,?[ \t]+([0-9]{4})(?![0-9])",
        RegexOptions.Compiled);

    /// <summary>ป้ายวันที่ชนิดอื่น — ตรวจ<b>ก่อน</b>ป้ายวันที่เอกสารเสมอ ("Due Date"/"วันที่ครบกำหนด"
    /// มีป้ายวันที่เอกสารซ้อนอยู่ข้างใน) · เทียบแบบตัดช่องว่าง + ตัวพิมพ์เล็ก</summary>
    private static readonly string[] OtherDateLabels =
    {
        "ครบกำหนด", "กำหนดชำระ", "กำหนดส่ง", "due", "print", "พิมพ์",
        "expir", "expdate", "exp.", "exp:", "หมดอายุ", "valid", "ใช้ได้ถึง", "ถึงวันที่",
        "delivery", "วันส่ง", "ส่งของ", "orderdate", "podate", "วันเกิด", "birth",
        "เริ่ม", "สิ้นสุด", "period",
    };

    private static readonly string[] DocumentDateLabels =
    {
        "วันที่", "ลงวันที่", "date", "วันออก", "issued",
    };

    /// <summary>วันที่ทุกก้อนบนกระดาษ เรียงตามตำแหน่ง</summary>
    /// <param name="text">ข้อความทั้งหน้า (normalize ช่องว่างไทยแล้ว)</param>
    /// <param name="reference">วันอัปโหลด — ใช้เลือกการตีความและตัดสินความสมเหตุสมผล</param>
    internal static IReadOnlyList<OcrDateCandidate> FindCandidates(string? text, DateTime reference)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<OcrDateCandidate>();
        var raw = new List<(int Start, int End, string Token, List<DateTime> Readings)>();
        var inv = CultureInfo.InvariantCulture;

        foreach (Match m in NumericDmy.Matches(text))
        {
            var a = int.Parse(m.Groups[1].Value, inv);
            var b = int.Parse(m.Groups[3].Value, inv);
            var cRaw = m.Groups[4].Value;
            var c = int.Parse(cRaw, inv);
            var readings = new List<DateTime>();
            // ลำดับ = ลำดับความน่าจะเป็นบนกระดาษไทย: วัน/เดือน/ปี · เดือน/วัน/ปี · ปี 2 หลัก/เดือน/วัน
            AddIfValid(readings, ThaiDate.NormalizeYear(c), b, a);
            if (a != b) AddIfValid(readings, ThaiDate.NormalizeYear(c), a, b);
            if (cRaw.Length == 2 && m.Groups[1].Value.Length == 2)
                AddIfValid(readings, ThaiDate.NormalizeYear(a), b, c);
            if (readings.Count > 0) raw.Add((m.Index, m.Index + m.Length, m.Value, readings));
        }
        foreach (Match m in NumericYmd.Matches(text))
        {
            var readings = new List<DateTime>();
            AddIfValid(readings, ThaiDate.NormalizeYear(int.Parse(m.Groups[1].Value, inv)),
                int.Parse(m.Groups[3].Value, inv), int.Parse(m.Groups[4].Value, inv));
            if (readings.Count > 0) raw.Add((m.Index, m.Index + m.Length, m.Value, readings));
        }
        foreach (Match m in NamedDayFirst.Matches(text))
        {
            if (ThaiMonthName.TryParseExact(m.Groups[2].Value) is not int mo) continue;
            var readings = new List<DateTime>();
            AddIfValid(readings, ThaiDate.NormalizeYear(int.Parse(m.Groups[3].Value, inv)), mo,
                int.Parse(m.Groups[1].Value, inv));
            if (readings.Count > 0) raw.Add((m.Index, m.Index + m.Length, m.Value, readings));
        }
        foreach (Match m in NamedMonthFirst.Matches(text))
        {
            if (ThaiMonthName.TryParseExact(m.Groups[1].Value) is not int mo) continue;
            var readings = new List<DateTime>();
            AddIfValid(readings, ThaiDate.NormalizeYear(int.Parse(m.Groups[3].Value, inv)), mo,
                int.Parse(m.Groups[2].Value, inv));
            if (readings.Count > 0) raw.Add((m.Index, m.Index + m.Length, m.Value, readings));
        }

        var result = new List<OcrDateCandidate>();
        var prevEnd = 0;
        var prevLineStart = -1;
        foreach (var r in raw.OrderBy(x => x.Start))
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, r.Start - 1)) + 1;
            if (r.Start == 0) lineStart = 0;
            var from = prevLineStart == lineStart ? prevEnd : 0;
            var label = LabelOf(text, r.Start, from, lineStart);
            var chosen = r.Readings.FirstOrDefault(d => IsPlausible(d, reference));
            var plausible = chosen != default;
            if (!plausible) chosen = r.Readings[0];
            result.Add(new OcrDateCandidate(chosen, r.Start, r.Token, label, r.Readings, plausible));
            prevEnd = r.End;
            prevLineStart = lineStart;
        }
        return result;
    }

    /// <summary>วันที่อยู่ในช่วงที่สมเหตุสมผลเทียบวันอัปโหลดไหม</summary>
    private static bool IsPlausible(DateTime date, DateTime reference)
    {
        var d = date.Date;
        var r = reference.Date;
        return d >= r.AddDays(-MaxPastDays) && d <= r.AddDays(MaxFutureDays);
    }

    /// <summary>เลือก "วันที่เอกสาร" จากกระดาษ — ป้ายวันที่เอกสาร &gt; ไม่มีป้าย · สมเหตุสมผลก่อน ·
    /// บนก่อนล่าง · <b>วันที่ที่มีป้ายชนิดอื่นไม่มีสิทธิ์</b> (คืน null แทนการหยิบวันครบกำหนดมาใส่)</summary>
    private static OcrDateCandidate? PickDocumentDate(IReadOnlyList<OcrDateCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;
        var best = candidates
            .OrderByDescending(c => c.Label switch
            {
                OcrDateLabel.DocumentDate => 2,
                OcrDateLabel.None => 1,
                _ => 0,
            })
            .ThenByDescending(c => c.Plausible)
            .ThenBy(c => c.Position)
            .First();
        return best.Label == OcrDateLabel.OtherDate ? null : best;
    }

    /// <summary>ความมั่นใจของวันที่ที่เลือกจากกระดาษ (ยังไม่เทียบกับ engine)</summary>
    private static decimal PickConfidence(OcrDateCandidate pick, IReadOnlyList<OcrDateCandidate> candidates)
    {
        if (!pick.Plausible) return ImplausibleConfidence;
        if (pick.Label == OcrDateLabel.DocumentDate) return LabelledConfidence;
        var distinct = candidates
            .Where(c => c.Label == OcrDateLabel.None && c.Plausible)
            .Select(c => c.Date.Date).Distinct().Count();
        return distinct <= 1 ? UnlabelledSingleConfidence : UnlabelledAmbiguousConfidence;
    }

    /// <summary>ผลตรวจนี้ต้องให้คนยืนยันก่อนลงบัญชีไหม (ห้ามอนุมัติอัตโนมัติ) — จริงเมื่อระบบ<b>เติม/ทับ/สงสัย</b>
    /// วันที่ด้วยความมั่นใจต่ำกว่า 0.85 (= ช่องขึ้นไฮไลต์เหลือง) · "ตรงกับป้าย" และ "ไม่แตะ" ไม่ต้อง.
    /// <para>ที่มา (ฝ่ายค้านรอบ 190): ก่อนมีตัวตรวจ engine ไม่ได้วันที่ ⇒ <c>[DATE-UNKNOWN]</c> ⇒ ไม่อนุมัติเอง ·
    /// พอตัวตรวจเติมวันที่จากตัวเลขลอย ๆ บนกระดาษ แท็กนั้นไม่เกิด ⇒ ใบอนุมัติเองด้วยวันที่เดา ⇒ งวด ภ.พ.30 ·
    /// tax point · นาฬิกา §82/3 ผิดเงียบ</para></summary>
    public static bool NeedsHumanConfirm(OcrDateCheck check)
        => check.Verdict is not (OcrDateVerdict.NoChange or OcrDateVerdict.Confirmed)
           && check.Confidence < 0.85m;


    /// <summary>
    /// <b>ขั้นตรวจท้ายสุด</b> — เทียบวันที่ที่ engine ให้ กับวันที่ที่มีป้ายบนกระดาษ
    /// </summary>
    /// <param name="engineDate">วันที่ที่ engine/ตัวสกัดชั้นก่อนให้มา (null = ไม่ได้)</param>
    /// <param name="text">ข้อความทั้งหน้า (normalize แล้ว)</param>
    /// <param name="reference">วันอัปโหลด</param>
    public static OcrDateCheck CrossCheck(DateTime? engineDate, string? text, DateTime reference)
    {
        var cands = FindCandidates(text, reference);
        var pick = PickDocumentDate(cands);

        if (!engineDate.HasValue)
        {
            if (pick == null)
                return new(OcrDateVerdict.NoChange, null, 0m, "ไม่พบวันที่เอกสารบนกระดาษ");
            var conf = PickConfidence(pick, cands);
            return new OcrDateCheck(OcrDateVerdict.Filled, Utc(pick.Date), conf,
                $"engine ไม่ได้วันที่ — ใช้ “{pick.Token}” {Describe(pick)} = {Show(pick.Date)}"
                + (pick.Plausible ? "" : " (⚠️ ห่างจากวันอัปโหลดเกินช่วง — ตรวจปีอีกครั้ง)"))
                { FromPaperLabel = pick.Label == OcrDateLabel.DocumentDate };
        }

        var e = Utc(engineDate.Value);
        var ePlausible = IsPlausible(e, reference);
        if (pick == null)
            return ePlausible
                ? new(OcrDateVerdict.NoChange, e, 0m, "ไม่มีวันที่เอกสารบนกระดาษให้เทียบ — คงค่า engine")
                : Doubt(e, reference, "ไม่มีวันที่เอกสารบนกระดาษให้เทียบ");

        if (pick.Date.Date == e.Date)
            return ePlausible
                ? new(OcrDateVerdict.Confirmed, e, PickConfidence(pick, cands),
                    $"วันที่ engine ตรงกับ “{pick.Token}” บนกระดาษ ✓")
                : Doubt(e, reference, $"ตรงกับ “{pick.Token}” บนกระดาษ");

        // (ก) engine อ่าน "ก้อนเดียวกัน" แต่เรียงวัน/เดือน/ปีต่างจากแบบไทย
        if (pick.Plausible && pick.Readings.Any(r => r.Date == e.Date))
            return new OcrDateCheck(OcrDateVerdict.Replaced, Utc(pick.Date), ReplacedConfidence,
                $"engine ตีความ “{pick.Token}” เป็น {Show(e)} — กระดาษไทยเรียง วัน/เดือน/ปี "
                + $"และปีต้องใกล้วันอัปโหลด ⇒ ใช้ {Show(pick.Date)} (ตรวจอีกครั้ง)")
                { FromPaperLabel = pick.Label == OcrDateLabel.DocumentDate };

        // (ข) engine หยิบวันที่อีกก้อนบนกระดาษ
        var other = cands.FirstOrDefault(c => !ReferenceEquals(c, pick) && c.Readings.Any(r => r.Date == e.Date));
        if (other != null)
        {
            if (pick.Label == OcrDateLabel.DocumentDate && other.Label != OcrDateLabel.DocumentDate && pick.Plausible)
                return new OcrDateCheck(OcrDateVerdict.Replaced, Utc(pick.Date), ReplacedConfidence,
                    $"engine หยิบ “{other.Token}” {Describe(other)} แต่กระดาษมีป้ายวันที่เอกสารที่ “{pick.Token}” "
                    + $"⇒ ใช้ {Show(pick.Date)}")
                    { FromPaperLabel = true };
            return ePlausible
                ? new(OcrDateVerdict.NoChange, e, 0m, $"engine เลือก “{other.Token}” — ไม่มีหลักฐานว่าผิด")
                : Doubt(e, reference, $"engine เลือก “{other.Token}”");
        }

        // (ค) ค่าของ engine ไม่อยู่บนกระดาษเลย
        if (!ePlausible && pick.Label == OcrDateLabel.DocumentDate && pick.Plausible)
            return new OcrDateCheck(OcrDateVerdict.Replaced, Utc(pick.Date), ReplacedConfidence,
                $"engine ให้ {Show(e)} ซึ่งไม่อยู่บนกระดาษและห่างจากวันอัปโหลดเกินช่วง — "
                + $"กระดาษมีป้ายวันที่ “{pick.Token}” ⇒ ใช้ {Show(pick.Date)}")
                { FromPaperLabel = true };
        if (pick.Label == OcrDateLabel.DocumentDate)
            return new(OcrDateVerdict.Doubtful, e, ConflictConfidence,
                $"engine ให้ {Show(e)} แต่ป้ายวันที่บนกระดาษเขียน “{pick.Token}” ({Show(pick.Date)}) — ตรวจว่าวันไหนถูก");
        return ePlausible
            ? new(OcrDateVerdict.NoChange, e, 0m, "วันที่ engine ไม่ขัดกับหลักฐานที่มีป้ายบนกระดาษ")
            : Doubt(e, reference, "ไม่มีป้ายวันที่บนกระดาษให้เทียบ");
    }

    private static OcrDateCheck Doubt(DateTime e, DateTime reference, string context)
    {
        var days = (int)(e.Date - reference.Date).TotalDays;
        var gap = days < 0 ? $"เก่ากว่าวันอัปโหลด {-days} วัน" : $"ล่วงหน้าวันอัปโหลด {days} วัน";
        return new(OcrDateVerdict.Doubtful, e, ImplausibleConfidence,
            $"{context} · วันที่ {Show(e)} {gap} — คงค่าไว้แต่ต้องตรวจปี/เดือนก่อนอนุมัติ (งวด ภ.พ.30 · §82/3)");
    }

    private static string LabelOfText(OcrDateLabel l) => l switch
    {
        OcrDateLabel.DocumentDate => "(ป้ายวันที่เอกสาร)",
        OcrDateLabel.OtherDate => "(ป้ายวันที่ชนิดอื่น เช่น ครบกำหนด/พิมพ์/หมดอายุ)",
        _ => "(ไม่มีป้าย)",
    };

    private static string Describe(OcrDateCandidate c) => LabelOfText(c.Label);

    private static string Show(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime d) => new(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);

    private static void AddIfValid(List<DateTime> list, int year, int month, int day)
    {
        if (year is < 1900 or > 2400 || month is < 1 or > 12 || day < 1) return;
        if (day > DateTime.DaysInMonth(year, month)) return;
        var d = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        if (!list.Contains(d)) list.Add(d);
    }

    /// <summary>ป้ายของวันที่ที่ตำแหน่ง <paramref name="pos"/> — มองย้อนในบรรทัดเดียวกัน (ไม่ข้ามวันที่ก้อนก่อน
    /// ในบรรทัดเดียวกัน) · ถ้าหน้าวันที่ไม่มีอะไรเลย ดูท้ายบรรทัดก่อนหน้า (แบบฟอร์มที่ป้ายอยู่บรรทัดบน)</summary>
    private static OcrDateLabel LabelOf(string text, int pos, int prevEndSameLine, int lineStart)
    {
        var start = Math.Min(pos, Math.Max(Math.Max(lineStart, prevEndSameLine), pos - LabelLookBehind));
        var prefix = Squash(text.Substring(start, pos - start));
        if (prefix.Trim(':', '：', '.', '-', '/').Length == 0 && lineStart > 0)
        {
            var prevLineEnd = lineStart - 1;
            var prevLineStart = text.LastIndexOf('\n', Math.Max(0, prevLineEnd - 1)) + 1;
            if (prevLineEnd <= 0) prevLineStart = 0;
            var prevLine = text.Substring(prevLineStart, Math.Max(0, prevLineEnd - prevLineStart));
            prefix = Squash(prevLine.Length > 30 ? prevLine[^30..] : prevLine);
        }
        return ClassifyPrefix(prefix);
    }

    /// <summary>ตัดสินป้ายจากข้อความหน้าวันที่ (ตัดช่องว่างแล้ว) — <b>ป้ายที่ใกล้วันที่ที่สุดชนะ</b>
    /// ยกเว้นป้ายวันที่เอกสารที่มีป้ายชนิดอื่น<b>ติดอยู่ข้างหน้า</b> ("Due Date" · "Print Date" ·
    /// "Expiry Date" = ป้ายคำเดียวที่มีคำว่า Date เป็นส่วนท้าย) · ป้ายชนิดอื่นที่อยู่ห่างออกไป
    /// (ชื่อ "โรงพิมพ์" ก่อน "วันที่") ไม่ทำให้วันที่เอกสารกลายเป็นวันที่ชนิดอื่น</summary>
    internal static OcrDateLabel ClassifyPrefix(string squashedPrefix)
    {
        var p = squashedPrefix ?? "";
        var otherEnd = -1;
        var otherEnds = new List<int>();
        foreach (var l in OtherDateLabels)
        {
            var i = p.LastIndexOf(l, StringComparison.Ordinal);
            if (i < 0) continue;
            otherEnds.Add(i + l.Length);
            otherEnd = Math.Max(otherEnd, i + l.Length);
        }
        int docStart = -1, docEnd = -1;
        foreach (var l in DocumentDateLabels)
        {
            var i = p.LastIndexOf(l, StringComparison.Ordinal);
            if (i < 0 || i + l.Length <= docEnd) continue;
            docStart = i; docEnd = i + l.Length;
        }
        if (docEnd < 0) return otherEnd >= 0 ? OcrDateLabel.OtherDate : OcrDateLabel.None;
        // ≥ (ไม่ใช่ >): ป้ายชนิดอื่นที่ "ครอบ" ป้ายวันที่ไว้ท้ายคำ ("expdate" · "podate" · "ถึงวันที่")
        // จบตำแหน่งเดียวกับป้ายวันที่ ⇒ ต้องนับเป็นป้ายชนิดอื่น
        if (otherEnd >= docEnd) return OcrDateLabel.OtherDate;
        // ป้ายชนิดอื่นที่จบชิดหน้าป้ายวันที่ (ห่าง ≤ 2 ตัว: "due date" · "expiry date") = ป้ายเดียวกัน
        return otherEnds.Any(end => end <= docStart && docStart - end <= 2)
            ? OcrDateLabel.OtherDate
            : OcrDateLabel.DocumentDate;
    }

    private static string Squash(string s)
        => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}
