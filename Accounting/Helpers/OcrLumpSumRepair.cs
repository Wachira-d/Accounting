using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ซ่อมข้อมูลที่ผลแกะรุ่น 6/7/8 ทิ้งไว้ — กติกาตัวเดียว** (pure · ทีม OCR 11 · ฝ่ายค้าน OCR 9 P1-5 · CLAUDE.md F2 ข้อ 9 "Persist แล้วต้องมี migration")
///
/// <para>═══ ที่มา ═══ ทีม OCR 8 (13a4d788 · 2026-10-16 13:09 UTC · รุ่น 6) ลงบรรทัดสินค้าที่ไม่มีราคาต่อหน่วยเป็น 1 × ยอด + "(12 ขวด)" · ทีม OCR 9 (รุ่น 7/8) แก้บรรทัดสินค้าทั่วไป
/// แต่ยังลง 1 × ยอด ให้คอนกรีต "(2.75 ลบ.ม.)" · เคสของ AWN "(จำนวน 3)" · ค่าน้ำดื่ม "(12 ลบ.ม.)" · การเพิ่มรุ่นกันแค่ "อัปไฟล์เดิมซ้ำ" — สแกนที่เก็บไว้แล้ว
/// ยังเป็นจำนวน 1 / หน่วย "รายการ" และตอนสร้างเอกสารตัวกันรันซ้ำได้ Keep (1 × ยอด ลงตัวพอดี) ⇒ เอกสาร/สต็อกจำนวน 1 ต่อไป · ตอนนำเข้าสต็อก (ก่อนทีม 11) alias/ชื่อสินค้าใหม่/คีย์ตัวเรียน
/// ใช้คำอธิบายดิบที่ติดท้าย</para>
///
/// <para>═══ กติกา ═══ บรรทัดที่เสีย = ท้ายของตัวกัน (<see cref="OcrLineQuantityGuard.LumpSumSuffix"/>) บนบรรทัดที่<b>ไม่มีหลักฐานมิเตอร์</b> (บิล PEA "(4,272 หน่วย)" ไม่ถูกแตะ) ·
/// คีย์ที่เสีย (alias · ชื่อสินค้า · คีย์ตัวเรียน) = มีท้ายของตัวกันใด ๆ (มิเตอร์ด้วย — จำนวนหน่วยเปลี่ยนทุกเดือน) และสร้างหลัง <see cref="PollutionStartUtc"/> ·
/// <b>ซ่อมเองเฉพาะส่วนที่ปลอดภัย</b>: ติดแท็กบล็อกให้สแกน (ต้องแกะใหม่/แก้บรรทัดก่อนอนุมัติ) + คืนสิทธิ์ "แกะใหม่" · เปลี่ยนชื่อ/รวม alias และคีย์ตัวเรียน ·
/// <b>ไม่แตะ</b>เอกสารที่ออกแล้ว · การเคลื่อนไหวสต็อก · ชื่อสินค้า (รายงานพร้อมทางไปต่อ — คำตัดสินข้อ 150) · global pattern ข้ามบริษัทไม่อยู่ในรายงานต่อบริษัท</para>
/// </summary>
public static class OcrLumpSumRepair
{
    /// <summary>รหัสกติกาใน audit/หมายเหตุ</summary>
    public const string RuleCode = "OCR-LUMPSUM-REPAIR";

    /// <summary>เวลาที่ 13a4d788 (ทีม OCR 8) เริ่มเขียนท้าย "(จำนวน หน่วย)" — alias/สินค้า/คีย์ตัวเรียนก่อนเวลานี้ไม่ได้มาจากตัวกัน</summary>
    public static readonly DateTime PollutionStartUtc = new(2026, 10, 16, 13, 9, 0, DateTimeKind.Utc);

    /// <summary>รุ่นตัวแกะที่ลง 1 × ยอด บนบรรทัดที่ไม่ใช่มิเตอร์ได้ (6 = ทีม 8 · 7/8 = ทีม 9 · 9/10 = ทีม 11/10: "ค่าน้ำขวด/ค่าน้ำหนักเกิน" ยังเป็นน้ำประปา ⇒ "(12 ลบ.ม.)" ·
    /// 11 = ทีม OCR 12 แก้แล้ว — น้ำประปาเป็น whitelist) · บรรทัดมิเตอร์จริงของรุ่น 9/10 ไม่ถูกฟ้อง เพราะตัวตรวจเล่นหลักฐานมิเตอร์ด้วยบริบทของสแกน (<see cref="IsPollutedLine"/>)</summary>
    public static readonly IReadOnlyList<int> AffectedVersions = new[] { 6, 7, 8, 9, 10 };

    /// <summary>บรรทัดของสแกนที่ตัวกันรุ่น 6–8 ลงเป็น 1 × ยอด โดยไม่มีหลักฐานมิเตอร์ (จำนวนสินค้าหาย)
    /// <para>ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2): ท้าย "(จำนวน N)" แยกบิลมิเตอร์จริงกับสินค้าที่จำนวนหายไม่ได้จากบรรทัดอย่างเดียว (ตัวเขียนดูบรรทัด · ตัวตัดสินดูป้ายกระดาษ + ผู้ขาย) ⇒
    /// ผู้เรียกส่ง <paramref name="amount"/> + ข้อความกระดาษ (<c>RawTextContent</c>) + ชนิดผู้ขายของสแกน (<see cref="OcrUtilityVendor.KindOf"/>) — บริบทเดียวกับตอนเขียน ·
    /// เล่นหลักฐานมิเตอร์แล้วใช่ (<see cref="OcrLineQuantityGuard.WasMeterLine"/>: PEA "ค่าพลังงาน" ป้าย "จำนวนหน่วยที่ใช้" · "ค่า Ft" ผู้ขาย กฟภ. · "Water charge" ผู้ขาย กปภ.) ⇒ ไม่เสีย ·
    /// ไม่ส่งบริบท ⇒ ตัดสินจากบรรทัดอย่างเดียว</para></summary>
    public static bool IsPollutedLine(string? description, decimal? quantity, string? unit,
        decimal amount = 0m, OcrLinePaper? paper = null, OcrUtilityKind seller = OcrUtilityKind.None)
    {
        return OcrLineQuantityGuard.LumpSumSuffix(description, quantity, unit) is { Meter: false } s
               && !OcrLineQuantityGuard.WasMeterLine(s, amount, paper, seller);
    }

    /// <summary>คีย์ (alias · ชื่อสินค้า · คีย์ตัวเรียน) ที่ติดท้ายของตัวกัน ⇒ คีย์ที่ตัดท้ายแล้ว · ไม่ติด/ไม่แน่ใจ = null (ไม่แตะ)</summary>
    public static string? CleanKey(string? text)
        => OcrLineQuantityGuard.LumpSumSuffix(text, 1m, OcrLineQuantityGuard.LumpSumUnit)?.Base;

    /// <summary>หมายเหตุบล็อกที่ติดให้สแกน (หนึ่งข้อต่อบรรทัด) — ร่องรอย ⟦1×ยอด⟧ ⇒ <see cref="OcrLineQuantityGuard.LiveNotes"/> ปลดเมื่อคนแก้บรรทัดนั้นในเอกสารแล้วทุกบรรทัดลงตัว</summary>
    public static string ScanNote(int version, int lineNo, string? description, decimal amount)
        => $"{OcrLineQuantityGuard.Tag} {RuleCode}: ผลแกะรุ่น {version} ลงบรรทัดที่ {lineNo} \"{description}\" เป็น 1 × {amount:N2} ทั้งที่ไม่มีหลักฐานมิเตอร์ "
           + "(จำนวนสินค้าตามกระดาษหายไป) — กด \"แกะใหม่\" ที่หน้าสแกน (ไม่ใช้โควต้า) หรือแก้จำนวน/ราคาของบรรทัดตามกระดาษก่อนอนุมัติ"
           + OcrLineQuantityGuard.Fingerprint(1m, amount);

    private static readonly System.Text.RegularExpressions.Regex NoteDescriptionRe = new(
        "บรรทัดที่ \\d+ \"(?<d>.*)\" เป็น 1 × ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>คำอธิบายบรรทัดที่หมายเหตุ <see cref="ScanNote"/> อ้าง (คู่อ่านกลับของรูปข้อความ — ตัวเดียว) · ไม่ใช่หมายเหตุของการซ่อม/อ่านไม่ได้ = null</summary>
    public static string? NoteDescription(string? note)
    {
        if (note is null || !note.Contains(OcrLineQuantityGuard.Tag + " " + RuleCode, StringComparison.Ordinal)) return null;
        var m = NoteDescriptionRe.Match(note);
        return m.Success ? m.Groups["d"].Value : null;
    }

    /// <summary>
    /// **คีย์ตัวเรียนผังที่ถูกตัด 50 ตัวอักษรจนท้ายของตัวกันขาด** (ทีม OCR 12 · ฝ่ายค้านรอบ 3 P2-3) — <c>ExpenseCategoryLearner</c> เก็บคีย์ตัวพิมพ์เล็ก 50 ตัวแรก ⇒
    /// "…(12 ข" ไม่มี ")" ⇒ <see cref="CleanKey"/> ไม่รู้จัก · ตัดสินด้วย<b>ที่มา</b>: คีย์เท่ากับ 50 ตัวแรกของคำอธิบายบรรทัดสแกนรุ่น 6–8 ที่ติดท้ายของตัวกัน
    /// (<paramref name="suffixedDescriptions"/>) ⇒ คีย์ = 50 ตัวแรกของคำอธิบายที่ตัดท้ายแล้ว · ไม่พบที่มา = null (ไม่เดาจากวงเล็บครึ่งเดียว)
    /// </summary>
    public static string? CleanTruncatedLearnerKey(string? key, IEnumerable<string?> suffixedDescriptions)
    {
        if (string.IsNullOrEmpty(key) || key.Length < LearnerKeyLength) return null;
        foreach (var d in suffixedDescriptions)
        {
            if (string.IsNullOrWhiteSpace(d) || LearnerKey(d) != key) continue;
            if (CleanKey(d) is string clean && LearnerKey(clean) != key) return LearnerKey(clean);
        }
        return null;
    }

    /// <summary>ความยาวคีย์ตัวเรียนผัง (<c>ExpenseCategoryLearner.NormalizeDescription</c>: ตัวพิมพ์เล็ก 50 ตัวแรก)</summary>
    public const int LearnerKeyLength = 50;

    /// <summary>คีย์ตัวเรียนรูปเดียวกับ <c>ExpenseCategoryLearner.NormalizeDescription</c> (ตัวพิมพ์เล็ก · ตัด <see cref="LearnerKeyLength"/>)</summary>
    public static string LearnerKey(string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        return lowered.Length > LearnerKeyLength ? lowered[..LearnerKeyLength] : lowered;
    }

    /// <summary>คำเตือนของข้อเสนอตัดท้าย alias/คีย์ตัวเรียน (ทีม OCR 12 · ฝ่ายค้านรอบ 3 P2-3) — "(จำนวน N)" และท้ายมิเตอร์ "(4,272 หน่วย)" เป็นเครื่องหมายของตัวกันเสมอ · "(12 ขวด)" อาจเป็นขนาดแพ็กจริง
    /// ที่ผู้ใช้/กระดาษตั้ง (ตัดแล้วขวดเดี่ยวถูกจับคู่กับสินค้าแพ็ก) ⇒ เตือน เว้นแต่คีย์ตรงกับบรรทัดที่ตัวกันรุ่น 6–8 เขียนเอง (<paramref name="fromGuardScanLine"/>) · null = ไม่มีคำเตือน</summary>
    public static string? KeyWarning(string? text, bool fromGuardScanLine)
    {
        if (fromGuardScanLine) return null;
        if (OcrLineQuantityGuard.LumpSumSuffix(text, 1m, OcrLineQuantityGuard.LumpSumUnit) is { Meter: false, UnitWord: not null } s)
            return $"ท้าย {s.Suffix} อาจเป็นขนาดแพ็กจริงที่ผู้ใช้หรือกระดาษตั้งไว้ (ไม่พบบรรทัดสแกนรุ่น 6–8 ที่ตัวกันเขียนท้ายนี้) — ตัดแล้วสินค้าเดี่ยวจะถูกจับคู่กับสินค้าแพ็ก · ติ๊กเฉพาะเมื่อแน่ใจ";
        return null;
    }

    /// <summary>สแกนนี้ติดหมายเหตุของการซ่อมแล้ว (ซ่อมซ้ำ = ข้าม)</summary>
    public static bool AlreadyMarked(string? processingNotes)
        => (processingNotes ?? "").Contains(OcrLineQuantityGuard.Tag + " " + RuleCode, StringComparison.Ordinal);

    /// <summary>ทางไปต่อของเอกสารที่สร้างจากสแกนที่เสีย — ระบบไม่แก้เอกสารเอง (คำตัดสินข้อ 150: บล็อกเฉพาะเอกสาร/GL ที่ออกแล้ว พร้อมทางไปต่อภาษาไทย)</summary>
    public static string DocumentWayForward(DocumentStatus status) => status switch
    {
        DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected =>
            // ทีม OCR 12 (ฝ่ายค้านรอบ 3 P1-4): ข้อความเดิมสัญญาด่านที่ไม่มีจริง — ตอนนี้ด่านมีจริงทุกทางเข้าที่อนุมัติ (DocumentService.ApproveDocumentAsync →
            // OcrPostingReadiness.EvaluateHumanApproval) หลังกด "ซ่อม" สแกนนี้ (ติดหมายเหตุ OCR-LUMPSUM-REPAIR)
            "ยังไม่ออก — หลังกดซ่อมสแกนนี้ การอนุมัติ draft (ทุกช่องทาง) จะถูกบล็อกจนกว่าบรรทัด 1 × ยอด จะถูกแก้: แก้จำนวน/ราคาบรรทัดใน draft ตามกระดาษ "
            + "หรือลบ draft แล้วกด \"แกะใหม่\" ที่สแกน (ไม่ใช้โควต้า)",
        DocumentStatus.Voided => "ยกเลิกแล้ว — ไม่ต้องทำอะไร",
        _ => "ออกแล้ว — ระบบไม่แก้ย้อนหลัง: ถ้ายอดเงินถูกแต่จำนวนผิด ให้ปรับสต็อก/ต้นทุนด้วยมือที่หน้าสินค้า · ถ้าต้องแก้เอกสารต่อคู่ค้า ออกใบลดหนี้/เพิ่มหนี้ตามจริง",
    };

    /// <summary>ทางไปต่อของการเคลื่อนไหวสต็อกที่มาจากสแกนที่เสีย (จำนวนเข้า 1 แทนจำนวนจริง · ทุนต่อหน่วย = ยอดทั้งบรรทัด)</summary>
    public const string StockWayForward =
        "จำนวนเข้าอาจเป็น 1 แทนจำนวนจริงบนกระดาษ และทุนต่อหน่วยเท่ายอดทั้งบรรทัด — ตรวจกับกระดาษแล้วปรับสต็อกด้วยมือ (รับเข้าส่วนที่ขาดที่ทุนจริง) ระบบไม่แก้ประวัติสต็อกเอง";

    /// <summary>ทางไปต่อของสินค้าที่ชื่อติดท้ายจำนวน (สร้างจากหน้านำเข้าสต็อกก่อนทีม 11)</summary>
    public const string ProductWayForward = "ชื่อสินค้าติดจำนวนจากบรรทัดสแกน — แก้ชื่อที่หน้าสินค้า (ระบบไม่เปลี่ยนทะเบียนสินค้าเอง)";
}
