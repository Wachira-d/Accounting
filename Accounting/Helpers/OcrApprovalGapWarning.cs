using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>
/// **"เอกสารที่สร้างจากสแกนนี้ ตอนสร้างระบบพบยอดไม่ตรงกระดาษ ([Σ-GAP])" — คำเตือนตอนอนุมัติด้วยมือ** (pure · ไม่ throw)
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 12) ═══
/// <para><c>[Σ-GAP]</c> หยุดการอนุมัติ<b>เอง</b> (<see cref="OcrPostingReadiness"/> — เว็บ "สร้าง+อนุมัติ" · การ์ด LINE · ปุ่ม LINE)
/// แต่การอนุมัติด้วยมือ (หน้าเอกสาร · มือถือ · API) ไม่เคยเห็นเลย ⇒ ใบที่ระบบรู้อยู่แล้วว่า "ยอดที่จะลงไม่ตรงกระดาษ"
/// ลง JE ได้ในคลิกเดียวโดยไม่มีร่องรอยว่ามีคนรับรู้ · เจ้าของตัดสิน: <b>เว็บ/มือถือ = คำเตือนที่ต้องกด "รับทราบ"</b>
/// (กลไกเดิม <c>DocumentApprovalWarningsException</c> + audit <c>APPROVE-ACK-WARNINGS</c>) · <b>API ห้ามขัดจังหวะ</b> —
/// คืนธง/ข้อความในโครงสร้างเดิมแทน (<see cref="IsGapWarning"/> ให้ผู้เรียกแยกคำเตือนชุดนี้ออกจากชุดอื่นได้)</para>
///
/// <para>ข้อความต้องบอก "ตอนนี้ยอดเอกสารเท่าไร กระดาษเท่าไร" ด้วย — คำเตือนจากตอนสร้างอาจถูกแก้ไปแล้ว ผู้ใช้ต้องตัดสินได้จากตัวเลข
/// ไม่ใช่เชื่อข้อความเก่า (F2 ข้อ 7: ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง)</para>
/// </summary>
public static class OcrApprovalGapWarning
{
    /// <summary>คำขึ้นต้นของคำเตือนชุดนี้ — ตัวแยกของผู้เรียก (API) · ห้ามแก้โดยไม่แก้ <see cref="IsGapWarning"/></summary>
    public const string Prefix = "ยอดจากสแกนไม่ตรงกระดาษ";

    private const string GapTag = "[Σ-GAP]";

    /// <summary>คำเตือนจากหมายเหตุของสแกนที่สร้างเอกสารนี้ — ว่าง = ไม่มี [Σ-GAP] (ไม่เตือน)
    ///
    /// <para>รอบ 195 (ใบ Scommerce — คำเตือน 3 ข้อรากเดียว · ท่อน "ตอนนี้…" ต่อท้ายทุกข้อ · ทุกข้อ "ยังไม่มีคำแนะนำ"):
    /// (1) ท่อน "ตอนนี้รายการรวม … · กระดาษ …" อยู่ที่<b>ข้อแรกข้อเดียว</b> (ตัวเลขชุดเดียวกันทุกข้อ = เสียงรบกวน)
    /// (2) เมื่อ <paramref name="rateAdvice"/> มีค่า (ตัวเลขหัวใบพิสูจน์ว่าทั้งใบ 7% — <see cref="OcrLineVatPlanner.RateAdvice"/>)
    /// ข้อที่พูดเรื่องเดียวกัน (ยอดรวม · VAT รวม · อัตรารายบรรทัด — <see cref="OcrAmountIntegrity.KindOf"/>) ถูกรวมเป็น<b>ข้อเดียว</b>
    /// ที่บอกทางแก้เป็นตัวเลข · ข้อที่ไม่รู้ชนิด/คนละเรื่อง (บรรทัดติดลบ · ตัวกระทบยอด) คงเป็นข้อแยกเสมอ · ยังต้องกด "รับทราบ"
    /// เหมือนเดิม (ไม่ถอดด่าน — แค่ไม่ให้เรื่องเดียวกันนับเป็นสามเรื่อง)</para></summary>
    /// <param name="scanNotes"><c>OcrScanResult.ProcessingNotes</c> ของสแกนที่ <c>CreatedDocumentId</c> = เอกสารนี้</param>
    /// <param name="paperTotal">ยอดรวมทั้งสิ้นบนกระดาษ (<c>ExtractedTotalAmount</c>) — null = ไม่รู้</param>
    /// <param name="linesTotalInclVat">ยอดที่บรรทัดของเอกสาร<b>ตอนนี้</b>รวมกันได้ = Σ (ยอดบรรทัด + VAT บรรทัด) + ผลต่างปัดเศษ
    /// — ไม่ใช้ <c>TotalAmount</c> เพราะเส้นสร้างจากสแกนตั้งยอดหัวเอกสารตามกระดาษเสมอ (จะ "ตรง" ทุกใบ = ข้อความโกหก)</param>
    /// <param name="rateAdvice">คำแนะนำเรื่องอัตรา VAT รายบรรทัดที่พิสูจน์จากตัวเลขแล้ว · null = ไม่มี (พฤติกรรมเดิม)</param>
    public static IReadOnlyList<string> Build(string? scanNotes, decimal? paperTotal, decimal linesTotalInclVat,
        string? rateAdvice = null)
    {
        if (string.IsNullOrWhiteSpace(scanNotes)) return Array.Empty<string>();
        var gaps = scanNotes.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(GapTag, StringComparison.Ordinal))
            .Select(l => l[GapTag.Length..].Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (gaps.Count == 0) return Array.Empty<string>();

        var now = paperTotal is decimal p
            ? $" · ตอนนี้รายการในเอกสารรวม {linesTotalInclVat:N2} · กระดาษ {p:N2}"
              + (Math.Abs(linesTotalInclVat - p) <= DocumentSettlementState.Tolerance ? " (ตรงกันแล้ว)" : $" (ต่าง {linesTotalInclVat - p:+#,##0.00;−#,##0.00})")
            : "";

        var bodies = new List<string>();
        if (!string.IsNullOrWhiteSpace(rateAdvice))
        {
            var sameRoot = gaps.Where(g => OcrAmountIntegrity.KindOf(g) is OcrAmountIntegrityKind.TotalMismatch
                or OcrAmountIntegrityKind.VatMismatch or OcrAmountIntegrityKind.VatRateMismatch).ToList();
            var merged = sameRoot.Count > 1 ? $" (รวม {sameRoot.Count} ข้อที่มาจากเรื่องเดียวกัน: ยอดรวม · VAT รวม · อัตรารายบรรทัด)" : "";
            bodies.Add($"อัตรา VAT รายบรรทัดไม่ตรงกระดาษ — {rateAdvice}{merged}");
            bodies.AddRange(gaps.Where(g => !sameRoot.Contains(g)).Select(g => Trim(g)));
        }
        else
            bodies.AddRange(gaps.Select(g => Trim(g)));

        return bodies.Select((b, i) => $"{Prefix}: {b}{(i == 0 ? now : "")} — ตรวจรายการกับกระดาษก่อนอนุมัติ").ToList();
    }

    /// <summary>คำเตือนนี้มาจากตัวนี้ไหม (ผู้เรียกฝั่ง API ใช้ตัดสินว่า "ไม่ขัดจังหวะ")</summary>
    public static bool IsGapWarning(string? warning)
        => warning is not null && warning.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Trim(string s) => s.Length > 240 ? s[..240] + "…" : s;
}
