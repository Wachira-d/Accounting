using System;
using System.Collections.Generic;

namespace Accounting.Helpers;

/// <summary>ชนิดของตัวเลขที่เก็บไว้แล้วผิดเพราะตรรกะส่วนลด/ยอดรวมแบบเดิม</summary>
public enum OcrStoredAmountIssue
{
    /// <summary>ยอดรวมของสแกนที่เก็บไว้ ≠ ยอดรวมทั้งสิ้นที่กระดาษพิสูจน์ได้ (Makro เก็บ 24,110 = ยอดก่อนส่วนลด)</summary>
    ScanTotalNotPaperTotal = 1,
    /// <summary>ส่วนลดที่สแกนเก็บไว้ เป็นการปรับตอนชำระ ไม่ใช่ส่วนลดในใบกำกับ (Shopee "ส่วนลดพิเศษ 98") — ฐาน/VAT ที่ลดตามนั้นผิด</summary>
    ScanDiscountIsSettlement = 2,
    /// <summary>ฐานภาษีหัวเอกสาร (SubTotal) ≠ ผลรวมรายการ (+ผลต่างปัดเศษ) — รายงานภาษี §87 อ่าน SubTotal</summary>
    DocSubTotalNotLines = 3,
    /// <summary>ยอดรวมเอกสาร ≠ รายการ + VAT − หัก ณ ที่จ่าย — JE ไม่สมดุล/ลงยอดคนละตัวกับรายการ</summary>
    DocTotalNotLines = 4,
}

/// <summary>ข้อค้นพบหนึ่งข้อ</summary>
public sealed record OcrStoredAmountFinding(OcrStoredAmountIssue Kind, decimal Stored, decimal Expected, string Message);

/// <summary>ข้อมูลหนึ่งแถว (สแกน + เอกสารที่สร้างจากสแกนนั้น ถ้ามี) — ค่าที่เก็บในฐานตามจริง ไม่ผ่านการคำนวณใหม่</summary>
/// <param name="NormalizedText">ข้อความดิบของสแกน (normalize แล้ว) — null/ว่าง = ข้ามข้อที่ต้องอ่านกระดาษ</param>
/// <param name="IsSignedXml">สแกนจาก e-Tax XML ที่ลงนาม — ยอดคือความจริงตามกฎหมาย ไม่ตรวจกับข้อความ</param>
/// <param name="HasDocument">มีเอกสารที่สร้างจากสแกนนี้</param>
/// <param name="LinesAmount">Σ ยอดบรรทัด (ก่อน VAT) ของเอกสาร</param>
/// <param name="LinesVat">Σ VAT บรรทัด</param>
public sealed record OcrStoredAmountRow(
    string? NormalizedText, bool IsSignedXml,
    decimal? ScanSubTotal, decimal? ScanVat, decimal? ScanTotal, decimal? ScanDiscount,
    bool HasDocument, decimal DocSubTotal, decimal DocVat, decimal DocWht, decimal DocTotal, decimal DocRounding,
    decimal LinesAmount, decimal LinesVat);

/// <summary>
/// **รายงานให้บัญชีตรวจ: สแกน/เอกสารเก่าที่ตัวเลขที่เก็บไว้ผิดเพราะตรรกะส่วนลด/ยอดรวมแบบเดิม** (pure · อ่านอย่างเดียว)
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 14) ═══
/// <para>รอบ 190–192 แก้ตัวอ่านส่วนลด/ยอดรวม (Total-first) แต่แก้ที่ต้นทางอย่างเดียว — แถวที่สแกน/สร้างเอกสารไปก่อนหน้า
/// ยังเก็บ "ยอดก่อนส่วนลด" / "ส่วนลดที่จริง ๆ เป็นคูปองแพลตฟอร์ม" / "ฐานภาษีหัวเอกสารที่ยังไม่หักส่วนลด" อยู่ (F2 ข้อ 9)
/// · เจ้าของตัดสิน: <b>รายงานให้บัญชีตรวจ ห้ามแก้หลังบ้าน</b> ⇒ ตัวนี้แค่ "ชี้" ด้วยตัวตัดสินชุดเดียวกับไปป์ไลน์ปัจจุบัน
/// (<see cref="OcrTotalAnchor"/> · <see cref="OcrTotalDecomposer"/>) — ไม่มีสูตรที่สอง</para>
/// </summary>
public static class OcrStoredAmountAudit
{
    /// <summary>ต่างเกินนี้จึงนับ (1 สตางค์ — ตัวเลขที่เก็บเป็น 2 ตำแหน่ง)</summary>
    public const decimal Tol = 0.01m;

    public static IReadOnlyList<OcrStoredAmountFinding> Evaluate(OcrStoredAmountRow r)
    {
        var list = new List<OcrStoredAmountFinding>();
        var hasText = !r.IsSignedXml && !string.IsNullOrWhiteSpace(r.NormalizedText);

        if (hasText && r.ScanTotal is decimal storedTotal && storedTotal > 0m)
        {
            var anchor = OcrTotalAnchor.Find(r.NormalizedText, storedTotal);
            if (anchor.Verdict == OcrTotalVerdict.Proven && anchor.Total is decimal paper
                && Math.Abs(paper - storedTotal) > Tol)
                list.Add(new(OcrStoredAmountIssue.ScanTotalNotPaperTotal, storedTotal, paper,
                    $"ยอดรวมที่สแกนเก็บไว้ {storedTotal:N2} ไม่ใช่ยอดรวมทั้งสิ้นบนกระดาษ {paper:N2} — {anchor.Reason}"));
        }

        if (hasText && r.ScanDiscount is decimal disc && disc > 0m)
        {
            var d = OcrTotalDecomposer.Decompose(r.NormalizedText, r.ScanSubTotal, r.ScanVat, r.ScanTotal, disc);
            if (d.Placement == OcrDiscountPlacement.PostInvoice)
                list.Add(new(OcrStoredAmountIssue.ScanDiscountIsSettlement, disc, 0m,
                    $"ส่วนลด {disc:N2} ที่สแกนเก็บไว้เป็นการปรับตอนชำระ (หลังยอดรวมทั้งสิ้น) ไม่ใช่ส่วนลดในใบกำกับ — "
                    + (r.HasDocument ? "ถ้าเอกสารลดฐาน/VAT ตามส่วนลดนี้ ฐานภาษีซื้อต่ำกว่าใบกำกับ · " : "")
                    + d.Reason));
        }

        if (r.HasDocument)
        {
            var expectedSub = r.LinesAmount + r.DocRounding;
            if (Math.Abs(r.DocSubTotal - expectedSub) > Tol)
                list.Add(new(OcrStoredAmountIssue.DocSubTotalNotLines, r.DocSubTotal, expectedSub,
                    $"ฐานภาษีหัวเอกสาร {r.DocSubTotal:N2} ≠ ผลรวมรายการ {expectedSub:N2} "
                    + (r.DocSubTotal > expectedSub
                        ? $"(สูงกว่า {r.DocSubTotal - expectedSub:N2} — มักเป็นยอดก่อนหักส่วนลด ⇒ ฐานในรายงานภาษี §87 เกินจริง)"
                        : $"(ต่ำกว่า {expectedSub - r.DocSubTotal:N2})")));
            var expectedTotal = r.LinesAmount + r.LinesVat + r.DocRounding - r.DocWht;
            if (Math.Abs(r.DocTotal - expectedTotal) > Tol)
                list.Add(new(OcrStoredAmountIssue.DocTotalNotLines, r.DocTotal, expectedTotal,
                    $"ยอดรวมเอกสาร {r.DocTotal:N2} ≠ รายการ + VAT − หัก ณ ที่จ่าย {expectedTotal:N2} "
                    + "— JE ฝั่งรายการกับฝั่งยอดรวมจะไม่เท่ากัน (อนุมัติไม่ผ่าน หรือเคยลงผ่านทางอื่น)"));
        }
        return list;
    }
}
