namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 ฝ่ายค้าน S5 — ผลการประทับ <c>SourceLineId</c> ของสแกนที่ผูกใบสั่งซื้อ (<c>/link-po</c>) แต่ไม่ได้จับคู่รายบรรทัด: ตัวสร้างบรรทัด
/// (<c>OcrService.BuildScanLinesAsync</c>) เขียนแท็ก <see cref="Tag"/> ลง <c>ProcessingNotes</c> (ช่องสะสมธงของสแกน · ล้างทุกครั้งที่สร้างบรรทัดใหม่
/// ผ่าน <see cref="OcrLineBuildNotes.StripRecomputed"/>) แล้ว DTO อ่านกลับเป็น <c>SourceLinesStamped</c>/<c>SourceLineWarning</c> ให้หน้าตรวจ OCR แสดง —
/// หน้าเว็บไม่แปลเอง (F2 ข้อ 5)
/// </summary>
public static class OcrSourceLineNote
{
    public const string Tag = "[SRC-LINE]";

    public static string Stamped(int lines, string? poNumber) =>
        $"{Tag} จับคู่รายการกับใบสั่งซื้อ {poNumber} ให้แล้ว {lines} บรรทัด (รายการ/จำนวนตรงทุกบรรทัด) — ใบสั่งซื้อจะนับยอดของใบนี้";

    public static string Unmatched(string? poNumber) =>
        $"{Tag} ยังไม่ได้จับคู่รายการกับใบสั่งซื้อ {poNumber} (รายการ/จำนวนไม่ตรงทุกบรรทัด) — ตรวจจำนวนให้ตรงแล้วบันทึกอีกครั้ง หรือใช้จับคู่รายบรรทัด "
        + "มิฉะนั้นใบสั่งซื้อจะขึ้น \"ไม่ทราบสัดส่วน\" และด่านกันรับเกินมองไม่เห็นใบนี้";

    /// <summary>อ่านแท็กกลับ — (จำนวนบรรทัดที่ประทับ, คำเตือนเมื่อยังไม่ได้จับคู่) · ไม่มีแท็ก = (0, null)</summary>
    public static (int Stamped, string? Warning) Parse(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return (0, null);
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Tag, StringComparison.Ordinal)) continue;
            var body = line[Tag.Length..].Trim();
            if (body.StartsWith("ยังไม่ได้จับคู่", StringComparison.Ordinal)) return (0, body);
            var m = System.Text.RegularExpressions.Regex.Match(body, @"ให้แล้ว (\d+) บรรทัด");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return (n, null);
        }
        return (0, null);
    }
}
