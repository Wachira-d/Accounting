using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลการอ่าน "ที่อยู่ผู้ซื้อ" จากข้อความบนกระดาษ</summary>
/// <param name="Address">ที่อยู่ (null = หาไม่พบ/ไม่มีหลักฐานพอ)</param>
/// <param name="Confidence">ความมั่นใจ</param>
/// <param name="Reason">เหตุผลภาษาไทย ลง ReasoningTrace ได้ตรง ๆ</param>
public readonly record struct OcrBuyerAddressReading(string? Address, decimal Confidence, string Reason)
{
    public bool Found => !string.IsNullOrWhiteSpace(Address);
}

/// <summary>
/// **อ่าน "บล็อกที่อยู่ใต้ป้ายผู้ซื้อ" จากข้อความล้วน — สำหรับ engine ที่ไม่คืนโครงสร้าง**
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11: "local OCR จับที่อยู่ผู้ซื้อไม่ได้") ═══
/// เส้น Azure ได้ที่อยู่ผู้ซื้อจากช่อง <c>CustomerAddress</c> ที่<b>โมเดลของ Azure</b> แยกให้ —
/// ฝั่งเรา<b>ไม่มีตัวอ่านที่อยู่ผู้ซื้อเลยสักตัว</b> (python ไม่มีช่อง <c>buyer_address</c> ·
/// <c>ParseThaiDocument</c> มีแต่ <c>VendorAddressRegex</c> ซึ่งหยิบป้าย "ที่อยู่/ADDRESS"
/// <b>ตัวแรกของหน้า</b> แล้วยัดเป็นที่อยู่<b>ผู้ขาย</b>) ⇒ บนกระดาษที่ผู้ขายไม่พิมพ์ป้าย "ที่อยู่"
/// ในหัวร้าน (ใบ POS Wine Pro · ใบเขียนมือ Radisson — ป้าย "ที่อยู่ Address" มีแค่ในบล็อกลูกค้า)
/// ที่อยู่ของ<b>เรา</b>ไปโผล่ในช่องผู้ขาย และช่องผู้ซื้อว่าง</para>
///
/// <para>═══ ขอบเขตที่ตั้งใจให้แคบ ═══ ไม่ใช่ตัวแยกโซนชุดที่สอง — ใช้ป้ายฝั่งผู้ซื้อจาก
/// <see cref="OcrPartyLabels"/> (ตัวค้นป้ายตัวเดียวของระบบ) เป็นจุดยึด แล้วมองหาป้าย "ที่อยู่/Address"
/// <b>ใต้จุดยึดไม่เกิน <see cref="MaxLinesAfterAnchor"/> บรรทัด</b> และไม่ข้ามป้ายฝั่งผู้ขาย ·
/// ค่าที่ได้ต้อง "หน้าตาเป็นที่อยู่" (รหัสไปรษณีย์ หรือคำบอกที่อยู่ ≥ 2 คำ) มิฉะนั้นคืน null ·
/// ไม่มีป้ายฝั่งผู้ซื้อ = ไม่เดา</para>
///
/// <para>pure ไม่มี I/O ไม่ throw · ผู้เรียกส่งข้อความที่ normalize แล้ว</para>
/// </summary>
public static class OcrBuyerAddressReader
{
    /// <summary>มองหาป้าย "ที่อยู่" ใต้ป้ายผู้ซื้อกี่บรรทัด (ใบ Wine Pro: ชื่อ · เลขภาษี · สาขา · ป้าย = 4)</summary>
    public const int MaxLinesAfterAnchor = 8;

    /// <summary>ที่อยู่หนึ่งแห่งยาวได้กี่บรรทัด (ใบ POS ตัดบรรทัดตามความกว้างกระดาษ)</summary>
    public const int MaxAddressLines = 3;

    /// <summary>เลขภาษีผู้ซื้ออยู่ห่างใต้ป้ายได้ไม่เกินนี้ (ตัวอักษร) ถึงจะใช้เลือกป้ายตัวที่ใกล้</summary>
    private const int TaxIdAnchorReach = 400;

    /// <summary>ความมั่นใจเมื่อพบใต้ป้าย "ที่อยู่" ในบล็อกผู้ซื้อ — ต่ำกว่า 0.85 ตั้งใจ: เป็นการอ่านจาก
    /// ข้อความล้วนที่ไม่มีพิกัด ให้ผู้ใช้เห็นไฮไลต์ (กฎเหล็ก #3 ข้อ 3)</summary>
    public const decimal LabelledConfidence = 0.80m;

    private const int MinLength = 10;
    private const int MaxLength = 250;

    private static readonly Regex AddressLabel = new(
        @"(?:ที่อยู่|address)[ \t]*[:：]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ป้ายของช่องอื่นที่บอกว่า "ที่อยู่จบแล้ว"</summary>
    private static readonly Regex StopLabel = new(
        @"(เลขประจำตัว|เลขผู้เสียภาษี|tax[ \t]*id|vat[ \t]*reg|โทร|\btel\b|\bfax\b|แฟกซ์|e-?mail|อีเมล|สาขา|\bbranch\b|รายการ|description|วันที่|\bdate\b|เลขที่ใบ|\bqty\b|จำนวน)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostalCode = new(@"(?<![0-9])[0-9]{5}(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex AddressMarker = new(
        @"(หมู่|ม\.|ซอย|ซ\.|ถนน|ถ\.|ตำบล|ต\.|แขวง|อำเภอ|อ\.|เขต|จังหวัด|จ\.|กรุงเทพ|กทม|\bmoo\b|\broad\b|\brd\b|\bsoi\b|district|province)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>บรรทัดที่ขึ้นต้นด้วยคำบอกที่อยู่ = มีช่องว่างคั่นกับบรรทัดก่อนจริง</summary>
    private static readonly Regex LeadingMarker = new(
        @"^(อ\.|ต\.|จ\.|ถ\.|ซ\.|ม\.|อำเภอ|ตำบล|จังหวัด|แขวง|เขต|ถนน|ซอย|หมู่)", RegexOptions.Compiled);

    /// <summary>บรรทัดที่จบด้วย "ต.บาง" (ชื่อสถานที่หลังคำย่อ) = เครื่องพิมพ์ตัดกลางชื่อ</summary>
    private static readonly Regex TrailingPlaceToken = new(
        @"(?:^|[ \t])(?:ต|อ|จ|ถ|ซ)\.[ก-๙]+$", RegexOptions.Compiled);

    /// <param name="text">ข้อความทั้งหน้า (normalize แล้ว)</param>
    /// <param name="buyerTaxId">เลขผู้ซื้อที่อ่านได้แล้ว (ถ้ามี) — ใช้เลือกป้ายผู้ซื้อตัวที่อยู่เหนือเลขนี้</param>
    public static OcrBuyerAddressReading Read(string? text, string? buyerTaxId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, 0m, "ไม่มีข้อความ");
        var (buyerLabels, sellerLabels) = OcrPartyLabels.FindAll(text);
        if (buyerLabels.Count == 0)
            return new(null, 0m, "ไม่มีป้ายฝั่งผู้ซื้อบนกระดาษ — ไม่เดาที่อยู่ผู้ซื้อ");

        var anchor = buyerLabels[0];
        var taxDigits = ThaiTaxId.Normalize(buyerTaxId);
        if (taxDigits.Length == 13)
        {
            var at = FindTaxId(text, taxDigits);
            if (at >= 0)
            {
                var above = buyerLabels.Where(p => p <= at && at - p <= TaxIdAnchorReach).ToList();
                if (above.Count > 0) anchor = above[^1];
            }
        }
        var limit = sellerLabels.Where(p => p > anchor).DefaultIfEmpty(text.Length).First();

        var lines = SplitLines(text);
        var anchorLine = 0;
        for (var i = 0; i < lines.Count; i++) if (lines[i].Start <= anchor) anchorLine = i;
        var last = Math.Min(lines.Count - 1, anchorLine + MaxLinesAfterAnchor);

        for (var k = anchorLine; k <= last; k++)
        {
            var (start, line) = lines[k];
            if (start >= limit) break;
            var from = k == anchorLine ? Math.Max(0, anchor - start) : 0;
            if (from > line.Length) continue;
            var m = AddressLabel.Match(line, from);
            if (!m.Success) continue;

            // ป้ายสองภาษา "ที่อยู่ Address" / "ที่อยู่ / ADDRESS" — กินป้ายซ้ำ
            var rest = line[(m.Index + m.Length)..];
            var again = Regex.Match(rest, @"^[ \t/]*(?:ที่อยู่|address)[ \t]*[:：]?", RegexOptions.IgnoreCase);
            if (again.Success) rest = rest[again.Length..];

            var parts = new List<string>();
            var cutFirst = CutAtStop(rest);
            // เจอป้ายช่องอื่นในบรรทัดเดียวกับป้ายที่อยู่ ("… 20110 โทร 02-…") = ที่อยู่จบในบรรทัดนี้
            var stopped = cutFirst.Length < rest.Length;
            var first = cutFirst.Trim(' ', '\t', ':', '：', '/', '-');
            if (first.Length > 0) parts.Add(first);
            var j = k + 1;
            while (!stopped && parts.Count < MaxAddressLines && !(parts.Count > 0 && PostalCode.IsMatch(parts[^1]))
                   && j < lines.Count && lines[j].Start < limit)
            {
                var next = lines[j].Text.Trim();
                if (next.Length == 0) break;
                var cut = CutAtStop(next).Trim();
                if (cut.Length == 0) break;
                parts.Add(cut);
                if (cut.Length < next.Length) break;   // เจอป้ายช่องอื่นกลางบรรทัด = ที่อยู่จบแล้ว
                j++;
            }
            if (parts.Count == 0) continue;

            var value = parts[0];
            for (var pi = 1; pi < parts.Count; pi++) value = Join(value, parts[pi]);
            value = Regex.Replace(value, @"[ \t]{2,}", " ").Trim(' ', '\t', ',', '-');
            if (value.Length is < MinLength or > MaxLength || !LooksLikeAddress(value)) continue;
            return new(value, LabelledConfidence,
                "ที่อยู่ผู้ซื้อจากป้าย “ที่อยู่/Address” ใต้ป้ายฝั่งผู้ซื้อบนกระดาษ");
        }
        return new(null, 0m, "ไม่พบป้าย “ที่อยู่/Address” ในบล็อกผู้ซื้อ");
    }

    /// <summary>ที่อยู่สองค่านี้เป็นที่อยู่เดียวกันไหม — ตัดป้าย/ช่องว่าง/เครื่องหมายแล้วตัวหนึ่ง<b>ครอบ</b>อีกตัว
    /// (ใช้บังคับ invariant "ที่อยู่เดียวกันเป็นของสองฝั่งไม่ได้" · ไม่ใช่ fuzzy: ที่อยู่คนละแห่งที่คล้ายกัน
    /// ต้องไม่ถูกนับว่าเหมือน)</summary>
    public static bool SameAddress(string? a, string? b) => IsPartOf(a, b) || IsPartOf(b, a);

    /// <summary><paramref name="candidate"/> เป็น<b>ส่วนหนึ่ง</b>ของ <paramref name="block"/> ทั้งก้อนไหม
    /// (ตัดป้าย/ช่องว่าง/เครื่องหมายแล้ว) — ใช้ตัดสินว่า "ที่อยู่ผู้ขาย" ที่ตัวอ่านเดิมหยิบมา<b>ไม่มีอะไรเลย
    /// นอกจากบล็อกผู้ซื้อ</b> · ที่อยู่ผู้ขายที่ยาวกว่า (มีที่อยู่ร้านต่อหน้า) ไม่นับ — ห้ามล้างทิ้งทั้งก้อน</summary>
    public static bool IsPartOf(string? candidate, string? block)
    {
        var x = Squash(candidate);
        var y = Squash(block);
        if (x.Length < MinLength || y.Length < MinLength) return false;
        return y.Contains(x, StringComparison.Ordinal);
    }

    private static string Squash(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(s, @"ที่อยู่|address", "", RegexOptions.IgnoreCase);
        return new string(t.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static bool LooksLikeAddress(string s)
        => PostalCode.IsMatch(s) || AddressMarker.Matches(s).Count >= 2;

    private static string CutAtStop(string s)
    {
        var m = StopLabel.Match(s);
        return m.Success ? s[..m.Index] : s;
    }

    /// <summary>ต่อบรรทัดที่เครื่องพิมพ์ตัดไว้ — "ต.บาง" + "พระ อ.ศรีราชา" = "ต.บางพระ อ.ศรีราชา"
    /// (ตัดกลางชื่อสถานที่) · กรณีอื่นคั่นด้วยช่องว่าง</summary>
    private static string Join(string prev, string next)
    {
        if (prev.Length > 0 && next.Length > 0 && IsThaiLetter(prev[^1]) && IsThaiLetter(next[0])
            && !LeadingMarker.IsMatch(next) && TrailingPlaceToken.IsMatch(prev))
            return prev + next;
        return prev + " " + next;
    }

    private static bool IsThaiLetter(char c) => c >= 'ก' && c <= '๎';

    private static int FindTaxId(string text, string digits)
    {
        foreach (Match m in Regex.Matches(text, @"(?<![0-9])[0-9](?:[- \t]?[0-9]){12}(?![0-9])"))
            if (new string(m.Value.Where(char.IsDigit).ToArray()) == digits) return m.Index;
        return -1;
    }

    private static List<(int Start, string Text)> SplitLines(string text)
    {
        var result = new List<(int Start, string Text)>();
        var pos = 0;
        foreach (var raw in text.Split('\n'))
        {
            result.Add((pos, raw.TrimEnd('\r')));
            pos += raw.Length + 1;
        }
        return result;
    }
}
