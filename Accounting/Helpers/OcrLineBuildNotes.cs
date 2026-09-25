using System;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>
/// **หมายเหตุที่ "ตัวสร้างบรรทัดเอกสารจากสแกน" คำนวณใหม่ทุกครั้ง — ล้างก่อนสร้างซ้ำ** (pure)
///
/// ═══ ที่มา (รอบ 195 ฝ่ายค้าน P3) ═══
/// <para><c>OcrService.RepopulateDocumentLinesFromScanAsync</c> ("ดึงรายการจากสแกนซ้ำ") เรียก <c>BuildScanLinesAsync</c>
/// ซึ่ง<b>ต่อท้าย</b> <c>[Σ-GAP]</c> ลง <c>ProcessingNotes</c> ของสแกน — ของรอบก่อนไม่เคยถูกล้าง ⇒ หลังตัวสร้างรุ่นใหม่ทำให้
/// บรรทัดตรงกระดาษแล้ว คำเตือน "ยอดไม่ตรงกระดาษ" ของรุ่นเก่ายังขึ้นตอนอนุมัติ (<see cref="OcrApprovalGapWarning"/>) และยังหยุด
/// การอนุมัติเอง (<see cref="OcrPostingReadiness"/>) — ทิศปลอดภัย แต่เป็นคำเตือนที่โกหก (F2 ข้อ 7)</para>
///
/// <para>ล้างเฉพาะแท็กที่ตัวสร้างบรรทัด<b>เขียนใหม่เองทุกครั้ง</b> จากตัวเลขชุดเดียวกัน (<see cref="GapTag"/> ·
/// <see cref="OcrHeaderVatEvidence.DerivedTag"/>) — ถ้าบรรทัดใหม่ยังไม่ตรง ตัวสร้างเขียนกลับมาเอง · ธงอื่นทุกตัว
/// ([VAT-CLAIM] · [APPROVE-SKIP] · [Reasoning] · [Σ] ข้อสังเกต) คงไว้ทุกตัวอักษร (<c>tools/flag_field_overwrite_check.py</c>)</para>
/// </summary>
public static class OcrLineBuildNotes
{
    /// <summary>แท็ก "ยอดที่จะสร้างไม่ตรงกระดาษ" ของตัวสร้างบรรทัด (ตัวกระทบยอด + <see cref="OcrAmountIntegrity"/>)</summary>
    public const string GapTag = "[Σ-GAP]";

    private static readonly string[] Recomputed = { GapTag, OcrHeaderVatEvidence.DerivedTag };

    /// <summary>ตัดบรรทัดที่ขึ้นต้นด้วยแท็กที่ตัวสร้างบรรทัดคำนวณใหม่ · บรรทัดอื่นคงเดิมทุกตัวอักษร · ไม่มีอะไรให้ตัด = คืนค่าเดิม</summary>
    public static string? StripRecomputed(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return notes;
        var lines = notes.Split('\n');
        var kept = lines.Where(l => !Recomputed.Any(t => l.TrimStart().StartsWith(t, StringComparison.Ordinal))).ToArray();
        return kept.Length == lines.Length ? notes : string.Join("\n", kept);
    }
}
