namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-1 · เจ้าของ: "หน้า policy นโยบายต่าง ๆ ปรับให้กดดูได้ 2 ภาษา") — <b>ตัวตัดสินภาษาของเอกสารกฎหมาย ตัวเดียว</b>
/// (แพลตฟอร์ม privacy/terms/refund · นโยบายของร้าน CMS · หน้านโยบายของหน้าจองโต๊ะ)
/// <list type="bullet">
/// <item><b>ฉบับเดียว ภาษาเป็นแค่การแสดง</b> — เลขฉบับ/วันมีผล/หลักฐานการยอมรับ (<c>PolicyVersion</c>) ไม่แยกตามภาษา ·
/// ลิงก์ที่ widget/หน้าจ่ายอ้าง (<c>terms 1.0 · refund 1.0</c>) ชี้ฉบับเดียวกันทั้งสองภาษา</item>
/// <item><b>ไม่มีฉบับอังกฤษ = ไม่แต่ง</b> (F2 ข้อ 3) — แสดงไทยพร้อมประกาศ <see cref="EnglishUnavailableNotice"/> ให้ผู้อ่านรู้ว่ากำลังอ่านฉบับไทย</item>
/// <item>ค่าภาษาที่รับ: <c>th</c> (ค่าเริ่มต้น) · <c>en</c> — อื่น ๆ/ว่าง = ไทย (ไม่โยน — เป็น query string ของหน้าสาธารณะ)</item>
/// </list>
/// </summary>
public static class LegalDocumentLanguage
{
    public const string Thai = "th";
    public const string English = "en";
    public static readonly IReadOnlyList<string> Supported = new[] { Thai, English };

    /// <summary>ข้อความเมื่อขออังกฤษแต่ยังไม่มีฉบับอังกฤษ — แสดงเป็นภาษาอังกฤษเพราะคนที่กดขอคืออ่านอังกฤษ</summary>
    public const string EnglishUnavailableNotice = "English version not available yet — showing the Thai version, which is the binding text.";

    /// <summary>ภาษาที่ใช้จริงจากค่าที่หน้าเว็บส่งมา (query <c>?lang=</c> · เก็บใน localStorage) — ไม่รู้จัก/ว่าง = ไทย</summary>
    public static string Normalize(string? requested)
        => string.Equals(requested?.Trim(), English, StringComparison.OrdinalIgnoreCase) ? English : Thai;

    /// <summary>ผลการเลือกเนื้อความ — <see cref="Language"/> = ภาษาที่ <b>แสดงจริง</b> (ขอ en แต่ไม่มี ⇒ th) ·
    /// <see cref="EnglishAvailable"/> = มีฉบับอังกฤษให้สลับไหม (หน้าเว็บวาดสวิตช์จากค่านี้) · <see cref="Notice"/> = ประกาศถึงผู้อ่าน (null = ไม่มี)</summary>
    public sealed record Choice(string Markdown, string Language, bool EnglishAvailable, string? Notice);

    /// <summary>เลือกเนื้อความตามภาษาที่ขอ — อังกฤษว่าง/ช่องว่างล้วน = ไม่มีฉบับอังกฤษ</summary>
    public static Choice Pick(string? thaiMarkdown, string? englishMarkdown, string? requestedLanguage)
    {
        var lang = Normalize(requestedLanguage);
        var hasEn = !string.IsNullOrWhiteSpace(englishMarkdown);
        if (lang == English && hasEn) return new Choice(englishMarkdown!, English, true, null);
        if (lang == English) return new Choice(thaiMarkdown ?? "", Thai, false, EnglishUnavailableNotice);
        return new Choice(thaiMarkdown ?? "", Thai, hasEn, null);
    }

    /// <summary>ชื่อเอกสารภาษาอังกฤษ (หัวหน้า/แท็บ เมื่อแสดงฉบับอังกฤษ) — คู่กับ <see cref="LegalDocumentDefaults.Title"/></summary>
    public static string TitleEn(string kind) => kind switch
    {
        LegalDocumentDefaults.KindPrivacy => "Privacy Policy",
        LegalDocumentDefaults.KindTerms => "Terms of Service",
        LegalDocumentDefaults.KindRefund => "Cancellation & Refund Policy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "ชนิดเอกสารกฎหมายไม่รู้จัก — ต้องเป็นค่าใน LegalDocumentDefaults.Kinds"),
    };

    /// <summary>รอบ 232 (ฝ่ายค้าน N3 · คำตัดสิน 232-Checkout-1): เติม/แก้ฉบับอังกฤษของฉบับที่<b>เผยแพร่แล้ว</b> โดย<b>ไม่ขยับเลขฉบับ</b> — ไทยเป็นฉบับผูกพัน
    /// (หลักฐานการยอมรับผูกเลขฉบับไทย · อังกฤษเป็นคำแปลประกอบ) · ต้องผ่าน audit chain ทุกครั้ง · pure — null = ทำได้</summary>
    /// <param name="status">สถานะแถว (<c>LegalDocumentPolicy.StatusPublished</c> เท่านั้น — ร่างใช้เส้นบันทึกร่าง)</param>
    /// <param name="hasRow">มีแถวในฐาน (ฉบับตั้งต้นในโค้ด "1.0" ไม่มีแถว ⇒ ต้องออกฉบับใหม่ผ่านร่าง)</param>
    public static string? PublishedEnglishRefusal(bool hasRow, string? status, string? englishMarkdown, int maxLength)
    {
        if (!hasRow) return "ไม่พบฉบับที่เผยแพร่เลขนี้ (ฉบับตั้งต้นในระบบเติมอังกฤษไม่ได้ — ออกฉบับใหม่ผ่านร่างพร้อมข้อความอังกฤษ)";
        if (status != LegalDocumentPolicy.StatusPublished) return "ฉบับนี้ยังเป็นร่าง — แก้ข้อความอังกฤษที่ช่องของร่างแล้วบันทึกร่าง";
        if (englishMarkdown is { Length: var n } && n > maxLength) return $"เนื้อความภาษาอังกฤษยาวเกิน {maxLength:N0} ตัวอักษร";
        return null;
    }

    /// <summary>ชื่อที่แสดงตามภาษาที่แสดงจริง (ผลจาก <see cref="Pick"/>) — ตัวเดียวของทุกหน้า</summary>
    public static string TitleFor(string kind, string shownLanguage)
        => shownLanguage == English ? TitleEn(kind) : LegalDocumentDefaults.Title(kind);
}
