using System.Security.Cryptography;
using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Import;

/// <summary>ผลดูไฟล์ขั้น Extract — สิ่งที่เก็บลง SmartImportSession (Kind · ชีต · encoding · ตัวคั่น · SHA-256) + แถวต้นไฟล์ให้ตัวหาแถวหัวตาราง</summary>
/// <param name="HeadRows">≤ <c>previewRows</c> แถวแรกของชีต/ไฟล์ (ก่อนตัดหัวตาราง) — ป้อน <c>ImportHeaderDetector.Detect</c> (ทีม B)</param>
public sealed record ImportSourcePreview(
    ImportFileKind Kind,
    string? SheetName,
    IReadOnlyList<string> SheetNames,
    string? Encoding,
    string? Delimiter,
    IReadOnlyList<IReadOnlyList<string>> HeadRows,
    string Sha256);

/// <summary>
/// **ขั้น Extract ของ pipeline** (รอบ 205 T0) — ดูไฟล์ด้วยตัวอ่านกลาง <see cref="ImportTabularReader"/> ตัวเดียว: ชนิดจากเนื้อไฟล์ · รายชื่อชีต ·
/// encoding · ตัวคั่น · แถวต้นไฟล์ · SHA-256 (เตือนไฟล์ซ้ำ) · ไฟล์ที่ไม่ใช่ตาราง (.xls/XML/PDF/QIF/OFX) ⇒ <see cref="ImportFormatException"/>
/// พร้อมทางไปต่อ (เป็น BusinessRuleException ⇒ 400 ข้อความไทย) · ผู้เรียก: <c>ImportPipelineService.CreateAsync/AnalyzeAsync</c> (ทีม B)
/// </summary>
public static class ImportSourceInspector
{
    /// <summary>ดูไฟล์ — <paramref name="sheetName"/> null = ชีตแรก (xlsx) · ไม่อ่านเกิน <paramref name="previewRows"/> แถว</summary>
    public static ImportSourcePreview Inspect(string fileName, byte[] bytes, string? sheetName = null, int previewRows = 30)
    {
        var kind = ImportTabularReader.Sniff(fileName, bytes);
        var sheets = kind == ImportFileKind.Xlsx ? ImportTabularReader.ListSheets(bytes) : Array.Empty<string>();
        var opened = ImportTabularReader.Open(fileName, bytes, sheetName);
        var head = opened.Rows.Take(Math.Max(1, previewRows)).ToList();
        return new ImportSourcePreview(opened.Kind, opened.SheetName, sheets, opened.Encoding,
            opened.Delimiter is { } d ? d.ToString() : null, head, Sha256Hex(bytes));
    }

    /// <summary>SHA-256 ของไบต์ไฟล์ — hex ตัวเล็ก 64 ตัว (<c>SmartImportSession.FileSha256</c>)</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
