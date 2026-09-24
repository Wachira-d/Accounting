using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>หน้าที่กระดาษประกาศเอง ("หน้า 3 จาก 3") เทียบกับหน้าที่อยู่ในไฟล์</summary>
/// <param name="DeclaredPages">จำนวนหน้าทั้งชุดที่กระดาษพิมพ์ (null = ไม่พิมพ์ / พิมพ์ขัดกันเอง ⇒ ไม่รู้)</param>
/// <param name="SeenPages">เลขหน้าที่พบในข้อความของไฟล์นี้</param>
/// <param name="MissingPages">หน้าที่กระดาษบอกว่ามีแต่ไม่อยู่ในไฟล์ — ว่าง = ครบ หรือไม่รู้</param>
public sealed record OcrPageCoverage(int? DeclaredPages, IReadOnlyList<int> SeenPages, IReadOnlyList<int> MissingPages);

/// <summary>
/// **"ไฟล์นี้มีครบทุกหน้าที่กระดาษบอกไหม"** (pure) — รอบ 192 · ใบ Makro ที่อัปโหลดมาแค่หน้า 3 จาก 3
///
/// <para>หน้า 1–2 มีรายการสินค้า หน้า 3 มีแต่ตารางสรุปตามรหัส ภ.พ. ⇒ ยอดรวม/VAT ยืนยันได้จากตาราง แต่
/// รายการสินค้า (ผังบัญชีรายบรรทัด) หายไป และต้นฉบับที่ต้องเก็บตาม ม.87/3 ไม่ครบชุด — ระบบเดิมไม่รู้เลยว่า
/// ขาดหน้า (Azure นับ <c>PageCount</c> แต่ไม่ได้เก็บ) · ตัวนี้อ่าน "หน้า N จาก M / Page N of M" ที่<b>กระดาษ
/// พิมพ์เอง</b> — ไม่พิมพ์ = ไม่รู้ ⇒ ไม่เตือน (ห้ามเดาว่าขาด)</para>
/// </summary>
public static class OcrPageSet
{
    /// <summary>แท็กข้อสังเกต (ไม่บล็อกการอนุมัติ — จะบล็อกไหมเป็นคำถามเจ้าของ)</summary>
    public const string PartialTag = "[PAGES-PARTIAL]";

    private const int MaxPages = 50;

    private static readonly Regex PageMarker = new(
        @"(?:หน้า(?:ที่)?|(?<![A-Za-z])page(?![A-Za-z]))[ \t.:]*(?<n>\d{1,2})[ \t]*(?:จาก|/|of)[ \t]*(?<m>\d{1,2})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static OcrPageCoverage Read(string? rawText)
    {
        var none = new OcrPageCoverage(null, Array.Empty<int>(), Array.Empty<int>());
        if (string.IsNullOrWhiteSpace(rawText)) return none;
        var seen = new SortedSet<int>();
        var totals = new HashSet<int>();
        foreach (Match m in PageMarker.Matches(rawText))
        {
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            var total = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (n < 1 || total < 1 || n > total || total > MaxPages) continue;
            seen.Add(n);
            totals.Add(total);
        }
        // ไม่พิมพ์ หรือพิมพ์จำนวนหน้าทั้งชุดไม่ตรงกัน (อ่านผิด/คนละชุด) ⇒ ไม่รู้
        if (totals.Count != 1) return new OcrPageCoverage(null, seen.ToList(), Array.Empty<int>());
        var declared = totals.First();
        var missing = Enumerable.Range(1, declared).Where(p => !seen.Contains(p)).ToList();
        return new OcrPageCoverage(declared, seen.ToList(), missing);
    }

    /// <summary>ข้อความ <c>[PAGES-PARTIAL]</c> — null เมื่อครบ/ไม่รู้</summary>
    public static string? PartialNote(string? rawText)
    {
        var c = Read(rawText);
        if (c.DeclaredPages is not int total || c.MissingPages.Count == 0) return null;
        return $"{PartialTag} กระดาษพิมพ์ว่ามี {total} หน้า แต่ไฟล์นี้มีแค่หน้า {string.Join(", ", c.SeenPages)} "
            + $"(ขาดหน้า {string.Join(", ", c.MissingPages)}) — รายการสินค้าอาจอยู่ในหน้าที่ไม่ได้อัปโหลด · "
            + "ยอดรวม/VAT ตรวจจากหน้าที่มีได้ แต่ควรอัปโหลดให้ครบทุกหน้าเพื่อเก็บต้นฉบับครบชุด (ม.87/3)";
    }
}
