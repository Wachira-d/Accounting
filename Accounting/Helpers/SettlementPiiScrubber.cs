using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ตัด PII ของผู้ซื้อปลายทางออกจากข้อความรายการ settlement ก่อนเก็บ/ส่งออก** (รอบ 198 เฟส 1 ทีม B · PDPA ม.26/ม.37 ·
/// CLAUDE.md §J · report-S2 §3 "ตัด PII")
///
/// <para>ที่มา: settlement report ของ marketplace มักแนบ "ชื่อผู้ซื้อ · เบอร์ · ที่อยู่จัดส่ง · อีเมล" มาในคอลัมน์รายละเอียด —
/// ข้อมูลของบุคคลภายนอกที่เราไม่มีวัตถุประสงค์ทางบัญชีต้องเก็บ (บัญชีต้องการแค่ยอด · ประเภท · เลขออเดอร์) ·
/// ข้อความนี้ยังถูกป้อนเป็นป้ายให้ตัวจัดประเภท/AI ⇒ ถ้าไม่ตัดจะรั่วออกไปหา provider ด้วย</para>
///
/// <para>═══ กติกา (pure · ไม่ throw · idempotent) ═══
/// <list type="bullet">
/// <item>อีเมล → <c>[อีเมล]</c> · เบอร์โทรไทย (0x-xxx-xxxx · +66 · 10 หลักติดกัน) → <c>[เบอร์]</c> · เลข 13 หลัก (บัตรประชาชน) → <c>[เลขบัตร]</c></item>
/// <item>ส่วนที่มีป้ายกำกับบุคคล ("ชื่อผู้ซื้อ:" · "ผู้รับ:" · "Buyer:" · "Recipient:" · "ที่อยู่:" · "Address:" · "Tel:") → ตัดตั้งแต่ป้าย
/// จนถึงตัวคั่นถัดไป ( | ; , ขึ้นบรรทัด ) — ป้ายเก็บไว้ ค่าถูกแทนด้วย <c>[ตัดข้อมูลส่วนบุคคล]</c> ให้ผู้ตรวจรู้ว่าเคยมี</item>
/// <item>คำนำหน้าชื่อบุคคล (นาย · นาง · นางสาว · น.ส. · คุณ · Mr. · Mrs. · Ms. · Miss) + ชื่อ/นามสกุลถัดไปไม่เกิน 2 คำ → <c>[ชื่อบุคคล]</c></item>
/// <item>ชิ้นส่วนที่อยู่ไทย (เลขที่/หมู่/ซอย/ถนน/ตำบล/แขวง/อำเภอ/เขต/จังหวัด + รหัสไปรษณีย์ 5 หลัก) → <c>[ที่อยู่]</c></item>
/// <item><b>ไม่แตะ</b>: เลขออเดอร์/เลขรายการ (ตัวอักษร+ตัวเลข) · ยอดเงิน · ป้ายประเภทรายการ (Commission fee ฯลฯ)</item>
/// <item>ตัดความยาวให้พอดีคอลัมน์ (500 ตัวอักษร)</item>
/// </list></para>
/// <para>ข้อจำกัดที่ตั้งใจ (เขียนไว้ตรง ๆ): ชื่อคนที่ไม่มีคำนำหน้าและไม่มีป้ายกำกับ ("สมชาย ใจดี" ลอย ๆ) ตัดไม่ได้ด้วยกติกา — ชั้นที่กันจริงคือ
/// <b>การจับคู่คอลัมน์</b>: คอลัมน์ชื่อ/ที่อยู่ผู้ซื้อไม่ถูกอ่านเลย (อยู่ในรายการ "ไม่ใช้") ตัวนี้คือตาข่ายชั้นที่สองของคอลัมน์รายละเอียด</para>
/// </summary>
public static class SettlementPiiScrubber
{
    public const int MaxLength = 500;
    public const string RedactedLabelValue = "[ตัดข้อมูลส่วนบุคคล]";

    private static readonly RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", O);
    // เลข 13 หลัก (มี/ไม่มีขีด) — บัตรประชาชน · ต้องมาก่อนเบอร์ (เบอร์ 10 หลักเป็นส่วนย่อยของ 13 หลักได้)
    private static readonly Regex CitizenId = new(@"(?<![\dA-Za-z])\d[\- ]?\d{4}[\- ]?\d{5}[\- ]?\d{2}[\- ]?\d(?![\dA-Za-z])", O);
    // เบอร์ไทย: +66 / 0 นำหน้า · คั่นด้วย - หรือช่องว่าง "ในบรรทัดเดียว" (ห้าม \s ที่กลืนขึ้นบรรทัดใหม่ — tools/regex_line_span_check.py)
    private static readonly Regex Phone = new(
        @"(?<![\dA-Za-z])(?:\+66[\- ]?|0)\d{1,2}[\- ]?\d{3}[\- ]?\d{3,4}(?![\dA-Za-z])", O);
    private static readonly Regex LabeledPii = new(
        @"(?<label>(?:(?<![ก-๙])(?:ชื่อ(?:ผู้ซื้อ|ลูกค้า|ผู้รับ)|ผู้ซื้อ|ผู้รับ(?:สินค้า)?|ลูกค้า|ที่อยู่(?:จัดส่ง)?|เบอร์(?:โทร)?|โทรศัพท์|โทร)"
        + @"|(?<![A-Za-z])(?:buyer(?: ?name)?|customer(?: ?name)?|recipient(?: ?name)?|receiver(?: ?name)?|ship(?:ping)? ?to"
        + @"|shipping ?address|delivery ?address|address|tel|phone|mobile))[ \t]*[:：=][ \t]*)(?<val>[^|;\r\n]*)",
        O | RegexOptions.IgnoreCase);
    // คำนำหน้าไทยต้องไม่ใช่ต้นคำอื่น (คุณภาพ · คุณสมบัติ · นายหน้า · นายจ้าง · นางฟ้า ...) — ฟ้องผิด = ตัดข้อความสินค้าทิ้ง
    private static readonly Regex Honorific = new(
        @"(?:(?<![ก-๙A-Za-z])(?:นางสาว|น\.ส\.|นาย(?!หน้า|จ้าง|ทุน|ก|อำเภอ|ตำรวจ)|นาง(?!ฟ้า|สาว)|คุณ(?!ภาพ|สมบัติ|ค่า|ประโยชน์|ลักษณะ|ธรรม|วุฒิ))[ \t]*"
        + @"|(?<![A-Za-z])(?:Mr|Mrs|Ms|Miss|Dr)\.?[ \t]+)"
        + @"[ก-๙A-Za-z][ก-๙A-Za-z.\-]*(?:[ \t]+[ก-๙A-Za-z][ก-๙A-Za-z.\-]*)?",
        O | RegexOptions.IgnoreCase);
    // ชิ้นส่วนที่อยู่ — ตัวย่อ (ม. ซ. ถ. ต. อ. จ.) ต้องไม่ใช่ตัวย่ออื่นที่มีจุดตามอีกชุด (ม.ค. · อ.ย. — วันที่/เลขทะเบียนสินค้า)
    private const string AddrPart =
        @"(?:หมู่(?:ที่)?|ซอย|ถนน|ตำบล|แขวง|อำเภอ|เขต|จังหวัด|(?:ม|ซ|ถ|ต|อ|จ)\.(?![ก-๙]{1,2}\.))[ \t]*[ก-๙A-Za-z0-9/]+";
    private static readonly Regex ThaiAddress = new(
        @"(?:(?:เลขที่|บ้านเลขที่)[ \t]*\d[\d/]*|(?<![ก-๙])" + AddrPart + @")"
        + @"(?:[ \t,]*" + AddrPart + @")*"
        + @"(?:[ \t,]*\d{5}(?!\d))?", O);
    private static readonly Regex PostalTail = new(@"(?<=\[ที่อยู่\])[ \t,]*\d{5}(?!\d)", O);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", O);

    /// <summary>ตัด PII — null/ว่าง ⇒ null</summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        s = Email.Replace(s, "[อีเมล]");
        s = LabeledPii.Replace(s, m => m.Groups["val"].Value.Trim().Length == 0
            ? m.Value
            : m.Groups["label"].Value + RedactedLabelValue);
        s = CitizenId.Replace(s, "[เลขบัตร]");
        s = Phone.Replace(s, "[เบอร์]");
        s = Honorific.Replace(s, "[ชื่อบุคคล]");
        s = ThaiAddress.Replace(s, "[ที่อยู่]");
        s = PostalTail.Replace(s, "");
        s = Spaces.Replace(s, " ").Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }
}
