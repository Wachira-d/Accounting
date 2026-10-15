using System.Globalization;
using Accounting.Models.DTOs;

namespace Accounting.Helpers;

/// <summary>
/// **ส่งออกหน้ารายการโมดูล (เงินเดือน/HR/ที่พัก/รอบโอน/POS) — "ทุกแถวตามตัวกรอง" + ส่งต่อให้ตัวเขียนกลาง** (ทีม X4 · 2026-10-14)
///
/// <para>═══ ทำไม ═══ หน้ารายการเหล่านี้ดึงทีละหน้า (20–100 แถว) และ <c>PagedRequest</c> ตัด pageSize ที่ 200 เงียบ ๆ —
/// export จากแถวที่หน้าโหลดไว้ = ได้แค่หน้าปัจจุบัน (บั๊กคลาสสิก) · ที่นี่ผู้เรียกส่ง lambda ที่เรียก <b>service ตัวเดียวกับหน้าจอ</b>
/// (ตัวกรอง/การจัดเรียง/ขอบเขตสิทธิ์ชุดเดียวกัน) แล้ว <see cref="CollectAllPagesAsync{T}"/> เดินทุกหน้าจนครบ <c>TotalCount</c></para>
///
/// <para>═══ รูปแบบไฟล์ ═══ เป็นของ <see cref="TabularExport"/> ตัวเดียว (ทีม X3: BOM · CRLF · กันสูตรเฉพาะข้อความ ·
/// ยอดติดลบเป็นตัวเลข · MiniExcel) — ที่นี่ไม่มีตัวเขียนของตัวเอง</para>
/// </summary>
public static class ModuleExport
{
    /// <summary>เพดานแถวต่อไฟล์ — เกินแล้ว<b>ล้มดังพร้อมทางไปต่อ</b> (กรองช่วงให้แคบลง) ไม่ตัดเงียบ ๆ</summary>
    public const int MaxRows = 100_000;

    /// <summary>ขนาดหน้าที่ใช้เดินดึงทุกแถว (เท่าเพดานของ <c>PagedRequest</c>)</summary>
    public const int PageSize = 200;

    public const string BadFormatMessage = "รูปแบบไฟล์ไม่รองรับ — ใช้ format=csv หรือ format=xlsx";

    /// <summary>"csv" / "xlsx" / "excel" → รูปแบบ · อื่น ๆ (รวมค่าว่าง) → null ⇒ ผู้เรียกตอบ 400 (ตัวตัดสิน = <see cref="TabularExport.TryParseFormat"/>)</summary>
    public static TabularFormat? ParseFormat(string? format)
        => TabularExport.TryParseFormat(format, out var f) ? f : null;

    /// <summary>ไฟล์แผ่นเดียวจากคอลัมน์ + แถว (+ แถวรวมท้ายตาราง) ผ่านตัวเขียนกลาง</summary>
    public static TabularFile File<T>(TabularFormat format, string baseName, string sheetName,
        IReadOnlyList<TabularColumn<T>> columns, IEnumerable<T> rows, IEnumerable<T>? footer = null)
        => TabularExport.Render(format, baseName, null, null, TabularExport.Sheet(sheetName, columns, rows, footer));

    /// <summary>
    /// เดินทุกหน้าของ service รายการตัวเดิมจนครบ <c>TotalCount</c> — ห้าม export แค่หน้าปัจจุบัน ·
    /// เกิน <paramref name="maxRows"/> = <see cref="BusinessRuleException"/> พร้อมจำนวนจริง
    /// </summary>
    public static async Task<List<T>> CollectAllPagesAsync<T>(Func<int, Task<PagedResponse<T>>> fetchPage, int maxRows = MaxRows)
    {
        var all = new List<T>();
        for (var page = 1; ; page++)
        {
            var res = await fetchPage(page);
            if (res.TotalCount > maxRows)
                throw new BusinessRuleException(
                    $"ข้อมูลตามตัวกรองนี้มี {res.TotalCount.ToString("N0", CultureInfo.InvariantCulture)} แถว เกินเพดานไฟล์ละ {maxRows.ToString("N0", CultureInfo.InvariantCulture)} แถว — กรองช่วงวันที่/สถานะให้แคบลงแล้วส่งออกทีละส่วน",
                    "EXPORT-ROW-CAP");
            all.AddRange(res.Items);
            if (res.Items.Count == 0 || all.Count >= res.TotalCount || page >= res.TotalPages) break;
        }
        return all;
    }

    /// <summary>แบบเดียวกับ <see cref="CollectAllPagesAsync{T}"/> สำหรับ service ที่แบ่งหน้าแบบ skip/take (ไม่มี TotalCount) —
    /// ขอทีละ <see cref="PageSize"/> จนได้น้อยกว่าที่ขอ · เกินเพดาน = ล้มดัง</summary>
    public static async Task<List<T>> CollectAllSkipTakeAsync<T>(Func<int, int, Task<IReadOnlyList<T>>> fetch, int maxRows = MaxRows)
    {
        var all = new List<T>();
        while (true)
        {
            var chunk = await fetch(all.Count, PageSize);
            all.AddRange(chunk);
            if (all.Count > maxRows)
                throw new BusinessRuleException(
                    $"ข้อมูลตามตัวกรองนี้เกินเพดานไฟล์ละ {maxRows.ToString("N0", CultureInfo.InvariantCulture)} แถว — กรองให้แคบลงแล้วส่งออกทีละส่วน", "EXPORT-ROW-CAP");
            if (chunk.Count < PageSize) break;
        }
        return all;
    }
}
