namespace Accounting.Helpers;

/// <summary>
/// สีหัวเอกสารเมื่อเอกสารผูกแบรนด์ — <b>ตัวเดียวของทั้งสอง renderer</b> (HTML <c>BuildCss</c>/<c>BuildLayoutCss</c> ·
/// QuestPDF <c>BuildBranding</c>) · รอบ 201 ทีม PL (A-PL6 · team-RF Q1)
///
/// <para>═══ ที่มา (defect class "สอง renderer ห้าม drift" — กฎเหล็ก #4 A ข้อแรก) ═══ QuestPDF ใช้สีแบรนด์ทับสีเทมเพลต แต่ HTML
/// <c>BuildCss</c> ไม่รู้จักแบรนด์ ⇒ PDF จาก Chromium (ตรงพรีวิว) กับ PDF สำรอง/e-Tax ของใบเดียวกันหัวคนละสี</para>
///
/// <para>กติกา (คงพฤติกรรม QuestPDF เดิม): สีแบรนด์ที่<b>ถูกรูป</b> (<see cref="DocumentTemplateStyle.Hex"/>) ชนะทั้งสีเน้นและสีหลัก ·
/// แบรนด์ไม่มีสี/สีไม่ถูกรูป ⇒ สีของเทมเพลต (ค่าดิบ — ผู้เรียกตรวจรูป/ใส่ค่าเริ่มต้นของตัวเองต่อ) · สีตารางไม่ขึ้นกับแบรนด์</para>
/// </summary>
public static class DocumentBrandColor
{
    /// <summary>สีเน้น (ชื่อบริษัท · หัวเรื่อง · เส้นของ layout)</summary>
    public static string? Accent(string? brandPrimaryColor, string? templateAccentColor)
        => DocumentTemplateStyle.Hex(brandPrimaryColor) ?? templateAccentColor;

    /// <summary>สีตัวอักษรหลักของเอกสาร</summary>
    public static string? Primary(string? brandPrimaryColor, string? templatePrimaryColor)
        => DocumentTemplateStyle.Hex(brandPrimaryColor) ?? templatePrimaryColor;
}
