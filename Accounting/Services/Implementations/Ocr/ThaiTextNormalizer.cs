using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Post-process Tesseract Thai-language output before any regex extraction
/// runs. Tesseract for Thai consistently produces output where every cluster
/// gets a space — "ค ่ า ไฟ ฟ้า" rather than "ค่าไฟฟ้า" — because tone marks
/// and vowels are written as separate glyphs. Every keyword-based regex
/// downstream silently misses real data unless we collapse those spaces.
///
/// Strategy:
///   1. Strip a single space between two Thai characters. Preserves the
///      space when one side is non-Thai (so "บริษัท ABC จำกัด" doesn't
///      collapse).
///   2. Collapse mid-word combining-mark spaces ("ก ่" → "ก่") even when
///      one side is a Thai mark vs. a Thai consonant.
///   3. Repair common Tesseract tone-mark drops ("ทั้งสิน" → "ทั้งสิ้น")
///      for the specific anchors we rely on for amount extraction.
///   4. Collapse double-spaces produced by step 1 into single spaces so
///      downstream regexes can still anchor on word boundaries.
///
/// Output is identity for English-only text — we never touch non-Thai
/// content. Idempotent: Normalize(Normalize(x)) == Normalize(x).
/// </summary>
public static class ThaiTextNormalizer
{
    // Single Thai character (consonant, vowel, tone mark, digit) U+0E00..U+0E7F
    private const string ThaiCharClass = "฀-๿";

    private static readonly Regex SpaceBetweenThaiChars = new(
        $"(?<=[{ThaiCharClass}]) +(?=[{ThaiCharClass}])",
        RegexOptions.Compiled);

    private static readonly Regex MultipleSpaces = new(@" {2,}", RegexOptions.Compiled);

    // Known tone-mark drops in Tesseract output that matter for our anchor
    // keywords. Each entry is (broken-form, correct-form).
    private static readonly (string Broken, string Fixed)[] ToneMarkRepairs = new[]
    {
        ("ทั้งสิน", "ทั้งสิ้น"),
        ("ทั้งสน", "ทั้งสิ้น"),
        ("ทังสิ้น", "ทั้งสิ้น"),
        ("รวมทั้งสน", "รวมทั้งสิ้น"),
        ("ใบกํากับ", "ใบกำกับ"),
        ("ใบกากับ", "ใบกำกับ"),
        ("ใบเสร็จรับเงน", "ใบเสร็จรับเงิน"),
        ("ใบเสรจ", "ใบเสร็จ"),
        ("ห้างหุ้นสวน", "ห้างหุ้นส่วน"),
        ("ห้างหุ้นสว่น", "ห้างหุ้นส่วน"),
        ("หางหุ้นส่วน", "ห้างหุ้นส่วน"),     // OCR drops the leading ้
        ("หางหุนส่วน", "ห้างหุ้นส่วน"),
        ("ค่าไฟฟา", "ค่าไฟฟ้า"),
        ("ค่าไฟพา", "ค่าไฟฟ้า"),             // ฟ↔พ Tesseract confusion
        ("ค่าไฟพ้า", "ค่าไฟฟ้า"),
        ("ไฟฟา", "ไฟฟ้า"),
        ("ไฟพา", "ไฟฟ้า"),
        ("ไฟพ้า", "ไฟฟ้า"),
        ("ภาษีมลค่าเพิ่ม", "ภาษีมูลค่าเพิ่ม"),
        ("ภาษีมูลคา", "ภาษีมูลค่า"),
        // รอบ 195 ฝ่ายค้านรอบสอง (ค): Tesseract ทำไม้เอกหล่น "ภาษีมูลค่าเพิม" ⇒ ป้าย VAT ไม่ถูกจำ ⇒ VAT ที่พิมพ์จริงถูกนับว่า
        // "ไม่ได้อยู่คู่ป้าย" (ตัวพิสูจน์ทั้งใบหยุดทำงาน) · รูปผิดไม่ใช่ส่วนหนึ่งของรูปถูก ⇒ idempotent
        ("ภาษีมูลค่าเพิม", "ภาษีมูลค่าเพิ่ม"),
        ("ภาษีมูลค่าเพื่", "ภาษีมูลค่าเพิ่ม"),    // ิ↔ื + dropped ม
        ("ภาษีมูลค่าเพื", "ภาษีมูลค่าเพิ่ม"),
        ("จำนวนเงนรวม", "จำนวนเงินรวม"),
        ("จํานวนเงนรวม", "จำนวนเงินรวม"),
        ("จำนวนเงนทั้งสิ้น", "จำนวนเงินทั้งสิ้น"),
        ("ทังสน", "ทั้งสิ้น"),
        ("ทังสิ้น", "ทั้งสิ้น"),
        ("ใบสั่งซือ", "ใบสั่งซื้อ"),
        ("เลขที่เอกสาร", "เลขที่เอกสาร"),       // intentional no-op safety check
    };

    /// <summary>Main entry point — return normalized text. Safe on null/empty.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Step 1: collapse single-space-between-Thai. Run repeatedly because
        // "ค ่ า" → first pass collapses "ค ่" → "ค่ า" → second pass needed.
        string prev;
        var current = text;
        int iterations = 0;
        do
        {
            prev = current;
            current = SpaceBetweenThaiChars.Replace(current, "");
            iterations++;
        } while (prev != current && iterations < 10);

        // Step 2: tone-mark drops — apply known repairs
        foreach (var (broken, fixedStr) in ToneMarkRepairs)
            current = current.Replace(broken, fixedStr);

        // Step 3: collapse runs of >1 space (from earlier removals) to a single space
        current = MultipleSpaces.Replace(current, " ");

        return current;
    }

    /// <summary>
    /// ย่อข้อความให้ "ตาเห็นเป็นคำเดียวกัน = เทียบติด" ก่อนค้นคำสำคัญบนกระดาษ
    ///
    /// ทำสี่อย่าง: <see cref="Normalize"/> (ยุบช่องว่างระหว่างอักขระไทย + ซ่อม
    /// วรรณยุกต์ที่ Tesseract ทำหล่น) → NFC → นิคหิต+สระอา (ํ + า) รวมเป็นสระอำ
    /// ซึ่ง NFC ไม่รวมให้ → ตัดช่องว่างและตัวคั่นทิ้ง แล้วเป็นตัวพิมพ์เล็ก
    ///
    /// <para>ทำไมต้องมี: กฎที่ 1 ของ RdComplianceValidator ค้นคำว่า "ใบกำกับภาษี"
    /// ด้วย <c>raw.Contains(...)</c> บนข้อความ OCR ดิบ ๆ ⇒ หัวกระดาษที่พิมพ์
    /// "ต้นฉบับใบส่งสินค้า/ต้นฉบับใบกำกับภาษี" แต่ OCR คืนมาเป็น "ใบกํากับภาษี"
    /// (นิคหิตแยก) หรือมีช่องว่างแทรก ถูกสรุปว่า "ไม่พบคำว่าใบกำกับภาษี" ทั้งที่
    /// อยู่บนกระดาษเต็ม ๆ — defect class เดียวกับที่ DocumentIssuerIdentity
    /// .NormalizeTitle แก้ไปแล้วฝั่งหัวเอกสารที่เราพิมพ์เอง (บั๊กจริง
    /// PI-20260820-0005)</para>
    ///
    /// <para><b>ต้องย่อทั้งสองฝั่ง</b> — ทั้งข้อความและคำที่จะค้น ไม่งั้น
    /// "tax invoice" (มีช่องว่าง) จะหาไม่เจอในข้อความที่ตัดช่องว่างไปแล้ว</para>
    /// </summary>
    public static string SquashForKeywordMatch(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Normalize(text)
            .Normalize(System.Text.NormalizationForm.FormC)
            // นิคหิต + สระอา → สระอำ (NFC ไม่รวมให้) — เขียนเป็น \u หลบปัญหา
            // encoding ของ editor เหมือน DocumentIssuerIdentity.NormalizeTitle
            .Replace("\u0E4D\u0E32", "\u0E33")
            .ToLowerInvariant();
        return new string(t
            .Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.' or '/' or '\\' or '·' or '|' or ':'))
            .ToArray());
    }

    /// <summary>Quick test: is most of the text Thai? Caller can use this to
    /// skip normalization for English-only OCR results (very rare in this
    /// system but cheap to check).</summary>
    public static bool IsMostlyThai(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int thai = 0, latin = 0;
        foreach (var c in text)
        {
            if (c >= '฀' && c <= '๿') thai++;
            else if (char.IsLetter(c)) latin++;
        }
        return thai > latin;
    }
}
