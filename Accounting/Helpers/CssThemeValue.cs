using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ค่าธีมเว็บไซต์ CMS ที่ผู้ใช้ส่งมาตอนสร้าง/แก้ธีม (ช่องที่ต่อเข้า CSS) — ตัวตรวจฝั่งเขียน <see cref="CssThemeValue.RejectReasons"/></summary>
public readonly record struct CmsThemeStyleInput(
    string? PrimaryColor, string? SecondaryColor, string? AccentColor, string? BackgroundColor,
    string? SurfaceColor, string? TextColor, string? TextSecondaryColor,
    string? HeadingFont, string? BodyFont, string? MaxContentWidth, int BorderRadius);

/// <summary>
/// **ค่าธีมที่ต่อเข้า CSS (ธีมเว็บไซต์ CMS · สีแบรนด์เอกสาร) — ตัวตรวจตัวเดียว** (รอบ 200 ทีม Z · ฝ่ายค้านรอบสอง RF-2)
///
/// <para>═══ ที่มา ═══ defect class เดียวกับ R200-X1 (ทีม RF) นอก diff: <c>CmsRenderingService.GenerateThemeCssFromEntity</c> ต่อสี 10 ช่อง · ฟอนต์ 3 ช่อง ·
/// ขนาดตัวอักษร · ความกว้าง เข้า <c>:root</c> <b>ดิบ</b> และ <c>CmsSiteService</c> บันทึกตามที่ส่งมา ⇒ ค่าสีที่ปิดกฎด้วยวงเล็บปีกกาแล้วต่อกฎใหม่ (<c>background:url(//x)</c>)
/// เขียน CSS ทับทั้งเว็บไซต์สาธารณะของลูกค้า · ค่า <c>'&lt;/style&gt;&lt;script&gt;</c> ถ้าวันหนึ่งผู้บริโภคของ <c>RenderedPageResponse.ThemeCss</c> วางลง
/// <c>&lt;style&gt;</c> = XSS (วันนี้ไม่มีผู้บริโภคใน wwwroot — <c>theme.css</c> เสิร์ฟเป็น text/css) · สีแบรนด์เอกสาร (<c>DocumentBrandController</c>) ไม่ตรวจรูป ⇒
/// เข้า <c>style="background:…"</c> ของหน้า document-brands (CSS injection ใน attribute)</para>
///
/// <para>═══ กติกา ═══ สี = <see cref="DocumentTemplateStyle.Hex"/> (ตัวตรวจสีตัวเดียวกับเทมเพลตเอกสาร) หรือ <c>rgb()/rgba()/hsl()/hsla()</c> ที่มีแต่ตัวเลข ·
/// ฟอนต์ = ตัวอักษร/ตัวเลข/ช่องว่าง/ขีด ≤ 60 ตัว (ต่อเข้าในเครื่องหมาย <c>'…'</c>) · ความยาว = ตัวเลข + หน่วย (px/rem/em/%/vw/vh/ch) หรือ <c>none</c> ·
/// ฝั่งเขียน: ค่าที่ส่งมาแล้วไม่ถูกรูป = ปฏิเสธพร้อมเหตุผลไทย (ไม่แก้ค่าเงียบ) · ฝั่ง render: ค่าเก่าที่ไม่ถูกรูป = ค่าเริ่มต้นของช่องนั้น (ไม่ต้อง migration)</para>
/// <para>G6: pure · ไม่มี I/O · ไม่มีตัวเลขเงิน/ภาษี</para>
/// </summary>
public static class CssThemeValue
{
    private static readonly Regex FunctionalColorRx = new(
        @"^(rgb|rgba|hsl|hsla)\([ \t]*[0-9.]+%?([ \t]*[, \t/][ \t]*[0-9.]+%?){2,3}[ \t]*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FontNameRx = new(@"^[\p{L}\p{M}\p{N} \-]{1,60}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LengthRx = new(@"^\d{1,4}(\.\d{1,3})?(px|rem|em|%|vw|vh|ch)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>ขอบมนสูงสุดที่ยอมรับ (px)</summary>
    public const int MaxBorderRadius = 200;

    /// <summary>สีที่ต่อเข้า CSS ได้ (ค่ามาตรฐาน) หรือ <c>null</c> — hex ผ่าน <see cref="DocumentTemplateStyle.Hex"/> · rgb/hsl ที่มีแต่ตัวเลข · <c>transparent</c></summary>
    public static string? SafeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (DocumentTemplateStyle.Hex(t) is { } hex) return hex;
        if (FunctionalColorRx.IsMatch(t)) return t.ToLowerInvariant();
        return string.Equals(t, "transparent", StringComparison.OrdinalIgnoreCase) ? "transparent" : null;
    }

    /// <summary>CSS กำหนดเอง (<c>SiteTheme.CustomCss</c>) ฝั่ง <b>render</b> — รอบ 201 ทีม PL (A-PL7 · team-Z Z-3): ฝั่งเขียนปฏิเสธ
    /// <c>&lt;/style</c>/<c>&lt;script</c> ตั้งแต่รอบ 200 แต่แถวเก่าที่บันทึกก่อนนั้นยังต่อดิบเข้า <c>ThemeCss</c> ⇒ แทน <c>&lt;</c> ทุกตัวด้วย CSS escape
    /// <c>\3c </c> (ใน string/content ของ CSS แสดงผลเป็น <c>&lt;</c> เหมือนเดิม · นอก string <c>&lt;</c> ไม่ใช่ CSS ที่ถูกอยู่แล้ว) ⇒ tokenizer HTML
    /// ไม่มีทางเจอแท็กปิด style ไม่ว่าผู้บริโภคจะวางลง <c>&lt;style&gt;</c> หรือเสิร์ฟเป็น text/css · ไม่ต้อง migration (กรองตอนอ่าน) · ว่าง ⇒ null</summary>
    public static string? SafeCustomCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        return css.Replace("<", "\\3c ");
    }

    /// <summary>สีสำหรับ render — ไม่ถูกรูป/ว่าง ⇒ <paramref name="fallback"/></summary>
    public static string Color(string? raw, string fallback) => SafeColor(raw) ?? fallback;

    /// <summary>ชื่อฟอนต์ที่ต่อในเครื่องหมาย <c>'…'</c> ได้ หรือ <c>null</c> (มี <c>' " ; { } &lt; &gt; \</c> ฯลฯ = null)</summary>
    internal static string? SafeFontName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        return FontNameRx.IsMatch(t) ? t : null;
    }

    /// <summary>ฟอนต์สำหรับ render — ไม่ถูกรูป ⇒ <paramref name="fallback"/></summary>
    public static string FontName(string? raw, string fallback) => SafeFontName(raw) ?? fallback;

    /// <summary>ความยาว CSS (ตัวเลข + หน่วย · <c>none</c>) หรือ <c>null</c></summary>
    internal static string? SafeLength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (string.Equals(t, "none", StringComparison.OrdinalIgnoreCase)) return "none";
        return LengthRx.IsMatch(t) ? t.ToLowerInvariant() : null;
    }

    /// <summary>ความยาวสำหรับ render — ไม่ถูกรูป ⇒ <paramref name="fallback"/></summary>
    public static string Length(string? raw, string fallback) => SafeLength(raw) ?? fallback;

    /// <summary>ขอบมน (px) ตัดเข้าช่วง 0–<see cref="MaxBorderRadius"/></summary>
    public static int Radius(int px) => px < 0 ? 0 : px > MaxBorderRadius ? MaxBorderRadius : px;

    /// <summary>เหตุผลที่ปฏิเสธการบันทึกธีม (ภาษาไทย) — ว่าง = บันทึกได้ · ช่องว่าง/null = ใช้ค่าเริ่มต้น (ผ่าน)</summary>
    public static IReadOnlyList<string> RejectReasons(CmsThemeStyleInput i)
    {
        var errs = new List<string>();
        void CheckColor(string? v, string label)
        {
            if (!string.IsNullOrWhiteSpace(v) && SafeColor(v) == null)
                errs.Add($"{label} ต้องเป็นรหัสสีแบบ #RRGGBB (เช่น #4F46E5) หรือ rgb(…)/hsl(…) ที่มีแต่ตัวเลข");
        }
        CheckColor(i.PrimaryColor, "สีหลัก");
        CheckColor(i.SecondaryColor, "สีรอง");
        CheckColor(i.AccentColor, "สีเน้น");
        CheckColor(i.BackgroundColor, "สีพื้นหลัง");
        CheckColor(i.SurfaceColor, "สีพื้นกล่อง");
        CheckColor(i.TextColor, "สีตัวอักษร");
        CheckColor(i.TextSecondaryColor, "สีตัวอักษรรอง");
        void CheckFont(string? v, string label)
        {
            if (!string.IsNullOrWhiteSpace(v) && SafeFontName(v) == null)
                errs.Add($"{label} ต้องเป็นชื่อฟอนต์ (ตัวอักษร ตัวเลข ช่องว่าง ขีด ไม่เกิน 60 ตัว)");
        }
        CheckFont(i.HeadingFont, "ฟอนต์หัวข้อ");
        CheckFont(i.BodyFont, "ฟอนต์เนื้อหา");
        if (!string.IsNullOrWhiteSpace(i.MaxContentWidth) && SafeLength(i.MaxContentWidth) == null)
            errs.Add("ความกว้างเนื้อหาสูงสุด ต้องเป็นตัวเลขพร้อมหน่วย (เช่น 1280px · 90%) หรือ none");
        if (i.BorderRadius < 0 || i.BorderRadius > MaxBorderRadius)
            errs.Add($"ขอบมน ต้องอยู่ระหว่าง 0–{MaxBorderRadius} px");
        return errs;
    }
}
