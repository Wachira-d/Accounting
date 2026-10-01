using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ค่าหน้าตาของเทมเพลตเอกสารที่ต่อเข้า <c>&lt;style&gt;</c> ของ HTML renderer และเข้า QuestPDF — ตัวตรวจตัวเดียว** (รอบ 200 ทีม RF · R200-X1)
///
/// <para>═══ ที่มา ═══ <c>PdfGenerationService.BuildCss</c>/<c>BuildLayoutCss</c> ต่อ <c>FontFamily</c> · สีทุกช่อง · ขนาดตัวอักษร ·
/// ขนาดกระดาษ/แนว เข้า <c>&lt;style&gt;</c> <b>ดิบ</b> และ <c>DocumentTemplateService</c> รับค่าตามที่ส่งมา ⇒ ค่า
/// <c>x'&lt;/style&gt;&lt;img src=x onerror=…&gt;</c> ปิดแท็ก style (tokenizer ของ HTML จบ <c>&lt;style&gt;</c> ที่ <c>&lt;/style&gt;</c> เสมอ)
/// แล้วยิงสคริปต์ในหน้าพรีวิวเอกสาร (JWT อยู่ใน localStorage ⇒ ขโมย token · กฎเหล็ก #4 C) · ส่วน QuestPDF มีตัวตรวจสีของตัวเอง
/// (<c>SanitizeHex</c>) ⇒ สองตัวตรวจ = สองความจริง (F2 ข้อ 4)</para>
///
/// <para>═══ กติกา ═══ ฝั่งเขียน (<see cref="RejectReasons"/>): ค่าที่ส่งมาแล้วไม่ถูกรูป = ปฏิเสธพร้อมเหตุผลภาษาไทย (ไม่แก้ค่าเงียบ) ·
/// <c>null</c> = ไม่ได้ส่ง (ไม่แตะ) · ว่าง = ใช้ค่าเริ่มต้น · ฝั่งอ่าน (ตอน render ทั้งสอง renderer): ค่าที่เก็บไว้ก่อนมีด่าน
/// ⇒ ค่าปลอดภัย (สีไม่ถูกรูป = สีเริ่มต้นของช่องนั้น · ฟอนต์นอกรายการ = <see cref="DefaultFont"/> · ขนาดนอกช่วง = ตัดเข้าขอบ)</para>
///
/// <para>ไม่มีตัวเลขเงิน/ภาษี — เป็นค่าการแสดงผลล้วน · G6: pure · ไม่มี I/O</para>
/// </summary>
public static class DocumentTemplateStyle
{
    /// <summary>ฟอนต์ที่หน้าแก้เทมเพลตเสนอ (<c>document-templates.html</c> <c>#fontFamily</c>) — รายการอนุญาตชุดเดียว</summary>
    public static readonly IReadOnlyList<string> Fonts = new[] { "THSarabunNew", "Prompt", "NotoSansThai", "Sarabun" };

    /// <summary>ฟอนต์เริ่มต้น = ค่าเริ่มต้นของ entity <c>DocumentTemplate.FontFamily</c></summary>
    public const string DefaultFont = "THSarabunNew";

    /// <summary>ขนาดกระดาษที่ทั้งสอง renderer รู้จัก (QuestPDF <c>ResolvePageSize</c> · CSS <c>@page size</c>)</summary>
    public static readonly IReadOnlyList<string> PaperSizes = new[] { "A4", "A5", "Letter" };

    /// <summary>แนวกระดาษ</summary>
    public static readonly IReadOnlyList<string> Orientations = new[] { "Portrait", "Landscape" };

    /// <summary>ช่วงขนาดตัวอักษรเนื้อความ (ตรงกับ input min/max บนหน้าแก้เทมเพลต) และค่าเริ่มต้นของ entity</summary>
    public const int BodyFontMin = 8, BodyFontMax = 24, BodyFontDefault = 14;

    /// <summary>ช่วงขนาดหัวเอกสาร — หน้าเว็บเสนอ 14–40 · เผื่อค่าเก่าจาก wizard ลงถึง 12 · ค่าเริ่มต้นของ entity = 18</summary>
    public const int TitleFontMin = 12, TitleFontMax = 40, TitleFontDefault = 18;

    private static readonly Regex HexRe = new("^#?([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant);

    /// <summary>รหัสสี hex <c>#RGB</c>/<c>#RRGGBB</c> (มีหรือไม่มี <c>#</c>) ⇒ <c>"#RRGGBB"</c> ตัวพิมพ์ใหญ่ · อย่างอื่นทั้งหมด = <c>null</c>
    /// — ตัวตรวจสีตัวเดียวของทั้ง HTML (<c>BuildCss</c>) และ QuestPDF (<c>PdfGenerationService.SanitizeHex</c> เรียกตัวนี้)</summary>
    public static string? Hex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = HexRe.Match(raw.Trim());
        if (!m.Success) return null;
        var d = m.Groups[1].Value.ToUpperInvariant();
        if (d.Length == 3) d = new string(new[] { d[0], d[0], d[1], d[1], d[2], d[2] });
        return "#" + d;
    }

    /// <summary>เหตุที่ปฏิเสธค่าสีจากผู้ใช้ (null = รับได้ · ว่าง = ใช้ค่าเริ่มต้น/ล้างค่า ผ่าน) — ข้อความเดียวของทั้งเทมเพลตและ
    /// ค่าตั้งบริษัท (<c>CompanySettings.PrimaryColor/SecondaryColor</c> · รอบ 201 ทีม IN A-IN6)</summary>
    public static string? ColorRejectReason(string? raw, string label)
        => !string.IsNullOrWhiteSpace(raw) && Hex(raw) == null
            ? $"{label} ต้องเป็นรหัสสีแบบ #RRGGBB (เช่น #4472C4) หรือเว้นว่าง"
            : null;

    /// <summary>สีที่ต่อเข้า CSS ได้เสมอ — ค่าไม่ถูกรูป/ว่าง ⇒ <paramref name="fallback"/> (ผู้เรียกส่งค่าคงที่ของตัวเอง)</summary>
    public static string Color(string? raw, string fallback) => Hex(raw) ?? fallback;

    /// <summary>ชื่อฟอนต์ในรายการอนุญาต (เทียบไม่สนตัวพิมพ์) ⇒ ชื่อมาตรฐาน · นอกรายการ/ว่าง = <c>null</c></summary>
    private static string? KnownFont(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        foreach (var f in Fonts)
            if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>ฟอนต์ที่ใช้ render — นอกรายการ ⇒ <see cref="DefaultFont"/></summary>
    public static string Font(string? raw) => KnownFont(raw) ?? DefaultFont;

    /// <summary>ขนาดตัวอักษรจากข้อความ (ทศนิยมปัดแบบ AwayFromZero) · อ่านไม่ออก ⇒ <paramref name="fallback"/> · นอกช่วง ⇒ ตัดเข้าขอบ</summary>
    private static int FontSize(string? raw, int min, int max, int fallback)
    {
        if (!TryParseSize(raw, out var v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }

    /// <summary>ขนาดเนื้อความที่ใช้ render</summary>
    public static int BodyFontSize(string? raw) => FontSize(raw, BodyFontMin, BodyFontMax, BodyFontDefault);

    /// <summary>ขนาดหัวเอกสารที่ใช้ render</summary>
    public static int TitleFontSize(string? raw) => FontSize(raw, TitleFontMin, TitleFontMax, TitleFontDefault);

    /// <summary>ขนาดกระดาษมาตรฐาน — นอกรายการ ⇒ <c>"A4"</c></summary>
    public static string PaperSize(string? raw) => Pick(raw, PaperSizes) ?? "A4";

    /// <summary>แนวกระดาษมาตรฐาน — นอกรายการ ⇒ <c>"Portrait"</c></summary>
    public static string Orientation(string? raw) => Pick(raw, Orientations) ?? "Portrait";

    /// <summary>
    /// เหตุผลที่ปฏิเสธการบันทึกเทมเพลต (ข้อความถึงผู้ใช้ · ภาษาไทย) — ว่าง = บันทึกได้ ·
    /// <c>null</c> ของช่องใด = ไม่ได้ส่งช่องนั้นมา (ไม่ตรวจ) · สตริงว่าง = ใช้ค่าเริ่มต้น (ผ่าน)
    /// </summary>
    public static IReadOnlyList<string> RejectReasons(TemplateStyleInput i)
    {
        var errs = new List<string>();
        void CheckColor(string? v, string label)
        {
            if (ColorRejectReason(v, label) is { } why) errs.Add(why);
        }
        void CheckSize(string? v, string label, int min, int max)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            if (!TryParseSize(v, out var n) || n < min || n > max)
                errs.Add($"{label} ต้องเป็นตัวเลข {min}–{max}");
        }

        if (!string.IsNullOrWhiteSpace(i.PaperSize) && Pick(i.PaperSize, PaperSizes) == null)
            errs.Add($"ขนาดกระดาษต้องเป็นหนึ่งใน {string.Join(" / ", PaperSizes)}");
        if (!string.IsNullOrWhiteSpace(i.Orientation) && Pick(i.Orientation, Orientations) == null)
            errs.Add($"แนวกระดาษต้องเป็นหนึ่งใน {string.Join(" / ", Orientations)}");
        if (!string.IsNullOrWhiteSpace(i.FontFamily) && KnownFont(i.FontFamily) == null)
            errs.Add($"ฟอนต์ต้องเป็นหนึ่งใน {string.Join(" / ", Fonts)}");
        CheckSize(i.BodyFontSize, "ขนาดตัวอักษรเนื้อความ", BodyFontMin, BodyFontMax);
        CheckSize(i.TitleFontSize, "ขนาดตัวอักษรหัวเอกสาร", TitleFontMin, TitleFontMax);
        CheckColor(i.PrimaryColor, "สีหลัก (Primary color)");
        CheckColor(i.AccentColor, "สีเน้น (Accent color)");
        CheckColor(i.TableHeaderColor, "สีพื้นหัวตาราง (Table header bg)");
        CheckColor(i.TableHeaderTextColor, "สีตัวอักษรหัวตาราง (Table header text)");
        CheckColor(i.HeaderBackgroundColor, "สีพื้นหัวกระดาษ (Header bg)");
        CheckColor(i.HeaderTextColor, "สีตัวอักษรหัวกระดาษ");
        CheckColor(i.TableStripedColor, "สีแถวสลับ (Striped color)");
        return errs;
    }

    private static string? Pick(string? raw, IReadOnlyList<string> set)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        foreach (var s in set)
            if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    private static bool TryParseSize(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim();
        if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase) || t.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
            t = t[..^2].Trim();
        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return false;
        value = (int)Math.Round(d, 0, MidpointRounding.AwayFromZero);
        return true;
    }
}

/// <summary>ช่องหน้าตาของเทมเพลตที่ต้องตรวจตอนบันทึก — แยกจาก DTO สร้าง/แก้ไข (สองชนิด) ให้ตัวตรวจมีลายเซ็นเดียว</summary>
public sealed record TemplateStyleInput(
    string? PaperSize = null,
    string? Orientation = null,
    string? FontFamily = null,
    string? BodyFontSize = null,
    string? TitleFontSize = null,
    string? PrimaryColor = null,
    string? AccentColor = null,
    string? TableHeaderColor = null,
    string? TableHeaderTextColor = null,
    string? HeaderBackgroundColor = null,
    string? HeaderTextColor = null,
    string? TableStripedColor = null);
