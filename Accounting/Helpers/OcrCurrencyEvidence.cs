using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ที่มาของสกุลเงินที่ <see cref="OcrCurrencyEvidence.Read"/> ตัดสิน</summary>
public enum OcrCurrencyBasis
{
    /// <summary>ไม่มีรหัสสกุลต่างประเทศบนหน้า ⇒ บาท</summary>
    NoForeign,
    /// <summary>มีสกุลต่างประเทศ และไม่มีคำบ่งบาทเลยทั้งหน้า</summary>
    ForeignOnly,
    /// <summary>หน้ามีทั้งบาทและสกุลต่างประเทศ — บริเวณยอดรวมบอกสกุลต่างประเทศ (ไม่มีบาท)</summary>
    TotalAreaForeign,
    /// <summary>หน้ามีทั้งบาทและสกุลต่างประเทศ — บริเวณยอดรวมบอกบาท</summary>
    TotalAreaThb,
    /// <summary>หน้ามีทั้งบาทและสกุลต่างประเทศ และบริเวณยอดรวมไม่บอก/บอกทั้งคู่ ⇒ <b>ไม่รู้</b> (คงบาทตามเดิม + ติดธงให้คนตรวจ)</summary>
    Ambiguous,
}

/// <summary>ผลอ่านสกุลเงิน · <see cref="Code"/> null = บาท (ค่าที่เก็บลง <c>OcrScanResult.Currency</c>)</summary>
public readonly record struct OcrCurrencyReading(string? Code, OcrCurrencyBasis Basis)
{
    /// <summary>ต้องติดธง <see cref="OcrCurrencyEvidence.UnsureTag"/> ไหม</summary>
    public bool Unsure => Basis == OcrCurrencyBasis.Ambiguous;
}

/// <summary>
/// **สกุลเงินของกระดาษ** (pure · รอบ 200 ทีม K2 · ผลตรวจรอบ 189 C-09 — ย้ายจาก <c>OcrService.InferCurrency</c> ที่ไม่มีเทสต์)
///
/// <para>═══ ที่มา ═══ ตัวเดิมตัดสินด้วย "มีคำว่า THB/บาท <b>ที่ไหนก็ได้</b>บนหน้า ⇒ บาท" — ใบ USD ที่มีบรรทัดอ้างอิงอัตราแลกเปลี่ยน
/// ("1 USD = 36.50 THB") · บรรทัดโอนเงิน · ตราประทับธนาคาร ⇒ บันทึกเป็นบาทด้วยตัวเลขของ USD (ตัวเลขเท่าเดิม ความหมายต่าง ~36 เท่า)
/// = defect class เดียวกับ RG-02 (ตัวสแกนคำอ่านทั้งหน้า · CLAUDE.md §H ข้อ 2)</para>
///
/// <para>กติกา (กันถดถอย: <b>คำตอบเดิมเปลี่ยนเฉพาะเคสเดียว</b>): ไม่มีรหัสต่างประเทศ → บาท (เดิม) · มีต่างประเทศไม่มีบาท → ต่างประเทศ (เดิม) ·
/// มีทั้งคู่ → ดู<b>บริเวณยอดรวม</b> (บรรทัดที่มีป้ายยอดรวม · ป้ายลอยไม่มีตัวเลข ⇒ + บรรทัดถัดไป): บอกต่างประเทศอย่างเดียว → ต่างประเทศ (<b>ใหม่</b> — เดิมบาท) ·
/// บอกบาทอย่างเดียว → บาท (เดิม) · ไม่บอก/บอกทั้งคู่ → บาท<b>พร้อมธง</b> <see cref="UnsureTag"/> (เดิมบาทเงียบ — ตอนนี้ด่านอนุมัติอัตโนมัติหยุด)</para>
/// <para>"$" อย่างเดียวไม่นับ (บางใบพิมพ์ THB ด้วย $) — ต้องเป็นรหัส/ชื่อสกุลเป็นคำเต็ม · "¥" ไม่นับ (เป็นได้ทั้ง JPY และ CNY — ไม่เดา)</para>
///
/// <para>═══ รอบ 200 ทีม Z (ฝ่ายค้านรอบสอง K2-5b/K2-5a) ═══ ชื่อสกุลที่เป็น<b>คำเดี่ยว</b> (<c>EURO</c> · <c>YEN</c> · <c>YUAN</c> · <c>RMB</c>) เป็นชื่อสินค้าได้
/// (<c>YEN TA FO</c> เย็นตาโฟ · <c>EURO</c> คัสตาร์ดเค้ก) — ตัวเดิมนับ "มีคำนี้ที่ไหนก็ได้บนหน้า" ⇒ ใบเสร็จร้านอาหารไทยที่ไม่พิมพ์คำว่าบาท = <b>JPY</b>
/// (ยอดความหมายต่างหลายสิบเท่า — ผิดเงียบ) · ใบบาทที่มี "บาทถ้วน" + ชื่อสินค้า EURO = <c>[CURRENCY-UNSURE]</c> ผิด ๆ ·
/// กติกาใหม่: คำเดี่ยวนับเป็นหลักฐาน<b>เฉพาะ</b>เมื่อ (1) อยู่ติดตัวเลขยอดเงิน (<c>YEN 500</c> · <c>12,000 YEN</c> — คำข้างตัวเลขอีกฝั่งต้องไม่ใช่คำอักษรละติน
/// ⇒ <c>1 YEN TA FO</c> ที่พิมพ์จำนวนก่อนชื่อสินค้าไม่นับ) หรือ (2) อยู่บนบรรทัดป้ายยอดรวม หรือ (3) อยู่บนบรรทัดป้ายสกุลเงิน (<c>Currency</c> · <c>สกุลเงิน</c>) ·
/// รหัส ISO (<c>USD</c> · <c>EUR</c> · <c>JPY</c> · <c>CNY</c> · <c>GBP</c> · <c>SGD</c>) และชื่อหลายคำ (<c>US DOLLAR</c> …) นับทุกที่เหมือนเดิม (ไม่ใช่ชื่อสินค้า) ·
/// สัญลักษณ์ <c>€</c> / <c>£</c> ติดตัวเลข = หลักฐานใหม่ (ไม่กำกวม) · ใบไทยที่ไม่มีคำบ่งสกุลใดเลย ⇒ บาท (เดิม)</para>
/// </summary>
public static class OcrCurrencyEvidence
{
    /// <summary>ธงใน ProcessingNotes — หน้ามีทั้งบาทและสกุลต่างประเทศ และบริเวณยอดรวมไม่บอกสกุล · อยู่ใน <see cref="OcrPostingReadiness.BlockingTags"/></summary>
    public const string UnsureTag = "[CURRENCY-UNSURE]";

    /// <summary>ต่อสกุล (ลำดับเดิม — สกุลแรกที่มีหลักฐานชนะ): <c>Strong</c> = รหัส/ชื่อหลายคำ นับทุกที่ · <c>Weak</c> = ชื่อคำเดี่ยวที่เป็นชื่อสินค้าได้
    /// (นับเมื่อมีหลักฐานข้างเคียง — <see cref="WeakWordHasEvidence"/>) · <c>Symbol</c> = สัญลักษณ์ที่ไม่กำกวม นับเมื่อติดตัวเลข</summary>
    private static readonly (string Code, string[] Strong, string[] Weak, char? Symbol)[] Foreign =
    {
        ("USD", new[] { "USD", "US DOLLAR", "U.S. DOLLAR" }, Array.Empty<string>(), null),
        ("EUR", new[] { "EUR" }, new[] { "EURO" }, '€'),
        ("JPY", new[] { "JPY" }, new[] { "YEN" }, null),
        ("CNY", new[] { "CNY" }, new[] { "RMB", "YUAN" }, null),
        ("GBP", new[] { "GBP", "POUND STERLING" }, Array.Empty<string>(), '£'),
        ("SGD", new[] { "SGD", "SINGAPORE DOLLAR" }, Array.Empty<string>(), null),
    };

    /// <summary>ป้ายสกุลเงินบนกระดาษ ("Currency: USD" · "สกุลเงิน เยน") — คำเดี่ยวบนบรรทัดนี้ = หลักฐาน</summary>
    private static readonly Regex CurrencyLabelRx = new(@"(\bCURRENCY\b|สกุลเงิน)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>โทเคนที่เป็นตัวเลขจำนวนเงิน/จำนวน (หลังตัดเครื่องหมายวงเล็บ/โคลอนที่ขอบ)</summary>
    private static readonly Regex NumberTokenRx = new(@"^\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);

    private static readonly char[] TokenEdge = { ':', '(', ')', '[', ']', ';', ',' };

    /// <summary>ป้ายยอดรวม (ไทย/อังกฤษ) — บรรทัดที่มีป้ายนี้คือ "บริเวณยอดรวม" (ป้ายที่ไม่มีตัวเลขในบรรทัด ⇒ รวมบรรทัดถัดไปด้วย)</summary>
    private static readonly Regex TotalLabelRx = new(
        @"(ยอดรวม|รวมทั้งสิ้น|รวมเงิน|จำนวนเงินรวม|ยอดสุทธิ|ยอดชำระ|ยอดที่ต้องชำระ|\bGRAND\s*TOTAL\b|\bTOTAL\b|\bAMOUNT\s*DUE\b|\bBALANCE\s*DUE\b|\bNET\s*AMOUNT\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ตัดสินสกุลเงินพร้อมที่มา — ดูกติกาที่ตัวคลาส</summary>
    public static OcrCurrencyReading Read(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new(null, OcrCurrencyBasis.NoForeign);
        var foreign = ForeignCode(rawText);
        if (foreign == null) return new(null, OcrCurrencyBasis.NoForeign);
        // ทั้งหน้า: คำบ่งบาทชุดเดิมของ InferCurrency ตัวเก่า (THB · บาท) — ไม่ขยาย ⇒ ใบที่เคยได้สกุลต่างประเทศยังได้เท่าเดิม
        if (!PageMentionsThb(rawText)) return new(foreign, OcrCurrencyBasis.ForeignOnly);

        var lines = rawText.Split('\n');
        string? areaForeign = null;
        var areaThb = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TotalLabelRx.IsMatch(lines[i])) continue;
            // ตัวเลขอยู่บรรทัดป้ายเอง ⇒ ดูบรรทัดนั้นบรรทัดเดียว (บรรทัดถัดไปมักเป็นเรื่องอื่น เช่นบัญชีโอนเงิน) · ป้ายลอย ⇒ รวมบรรทัดถัดไป
            var area = lines[i].Any(char.IsDigit) || i + 1 >= lines.Length ? lines[i] : lines[i] + "\n" + lines[i + 1];
            areaForeign ??= ForeignCode(area);
            areaThb |= PageMentionsThb(area) || area.Contains('฿');
        }
        if (areaForeign != null && !areaThb) return new(areaForeign, OcrCurrencyBasis.TotalAreaForeign);
        if (areaThb && areaForeign == null) return new(null, OcrCurrencyBasis.TotalAreaThb);
        return new(null, OcrCurrencyBasis.Ambiguous);
    }

    /// <summary>รหัสสกุล (null = บาท) — ผู้เรียกที่ต้องการค่าอย่างเดียว (สร้างเอกสาร · DTO)</summary>
    public static string? Infer(string? rawText) => Read(rawText).Code;

    /// <summary>ข้อความธงเมื่อ <see cref="OcrCurrencyReading.Unsure"/> — ผู้ใช้เห็นว่าต้องตรวจสกุลเงินก่อนอนุมัติ</summary>
    public static string UnsureNote(string? rawText)
        => $"{UnsureTag} กระดาษมีทั้งจำนวนเงินบาทและสกุล {ForeignCode(rawText)} แต่บริเวณยอดรวมไม่ระบุสกุล — ระบบบันทึกเป็นบาทไว้ก่อน "
           + "ตรวจสกุลเงินกับกระดาษ (ถ้าเป็นสกุลต่างประเทศ ให้เลือกสกุลในฟอร์มเอกสารก่อนอนุมัติ)";

    private static bool PageMentionsThb(string text)
        => text.ToUpperInvariant().Contains("THB", StringComparison.Ordinal) || text.Contains("บาท", StringComparison.Ordinal);

    private static string? ForeignCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToUpperInvariant();
        foreach (var (code, strong, weak, symbol) in Foreign)
        {
            foreach (var w in strong)
                if (Regex.IsMatch(t, $@"\b{Regex.Escape(w)}\b")) return code;
            foreach (var w in weak)
                if (WeakWordHasEvidence(t, w)) return code;
            if (symbol is char sym && Regex.IsMatch(t, $@"{Regex.Escape(sym.ToString())}[ \t]?\d|\d[ \t]?{Regex.Escape(sym.ToString())}")) return code;
        }
        return null;
    }

    /// <summary>
    /// คำเดี่ยว (<c>EURO</c>/<c>YEN</c>/<c>YUAN</c>/<c>RMB</c>) เป็นหลักฐานสกุลเงินไหม — ต่อบรรทัด: บรรทัดป้ายยอดรวม/ป้ายสกุลเงิน = ใช่ ·
    /// ไม่งั้นต้องติดตัวเลข: คำ→ตัวเลข (โทเคนก่อนหน้าคำต้องไม่ใช่คำละติน — "CAKE EURO 60" ไม่นับ) หรือ ตัวเลข→คำ (โทเคนถัดไปต้องไม่ใช่คำละติน —
    /// "1 YEN TA FO" ที่พิมพ์จำนวนก่อนชื่อสินค้าไม่นับ) · <paramref name="upperText"/> = ข้อความตัวพิมพ์ใหญ่แล้ว
    /// </summary>
    private static bool WeakWordHasEvidence(string upperText, string word)
    {
        foreach (var line in upperText.Split('\n'))
        {
            if (!line.Contains(word, StringComparison.Ordinal)) continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim(TokenEdge)).Where(x => x.Length > 0).ToArray();
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!string.Equals(tokens[i], word, StringComparison.Ordinal)) continue;
                if (TotalLabelRx.IsMatch(line) || CurrencyLabelRx.IsMatch(line)) return true;
                var prev = i > 0 ? tokens[i - 1] : null;
                var next = i + 1 < tokens.Length ? tokens[i + 1] : null;
                if (next != null && NumberTokenRx.IsMatch(next) && (prev == null || !HasLatinLetter(prev))) return true;
                if (prev != null && NumberTokenRx.IsMatch(prev) && (next == null || !HasLatinLetter(next))) return true;
            }
        }
        return false;
    }

    private static bool HasLatinLetter(string token) => token.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
}
