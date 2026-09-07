using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>เอกสารที่มีอยู่แล้วซึ่งใช้เลขที่เดียวกัน (ข้อมูลเท่าที่ต้องใช้ตัดสิน)</summary>
/// <param name="ContactTaxIdDigits">เลขผู้เสียภาษีของคู่ค้าบนเอกสารนั้น (ตัวเลขล้วน,
/// ว่าง = ไม่ทราบ) — <b>ไม่ทราบ ≠ ไม่ตรง</b></param>
public sealed record DuplicateCandidate(
    Guid DocumentId, string DocumentNumber, DateTime DocumentDate,
    decimal TotalAmount, string ContactTaxIdDigits);

public enum OcrDuplicateVerdict
{
    /// <summary>ไม่มีใบไหนใช้เลขที่นี้ (หรือคู่ค้าคนละราย) — สร้างได้</summary>
    None = 0,
    /// <summary>เลขที่ + ยอด ตรงกันทั้งคู่ — ใบเดียวกันแน่นอน</summary>
    SameNumberAndAmount = 1,
    /// <summary>เลขที่ + คู่ค้า ตรง แต่ยอดต่าง — ตาม §86/4 เลขใบกำกับ unique ต่อผู้ขาย
    /// ⇒ เป็นใบเดียวกันที่ OCR อ่านยอดผิด ไม่ใช่คนละใบ</summary>
    SameNumberDifferentAmount = 2,
}

/// <param name="Match">ใบที่ตรง (null เมื่อ <see cref="OcrDuplicateVerdict.None"/>)</param>
/// <param name="AmountGap">ผลต่างยอด (บวกเสมอ) — 0 เมื่อยอดตรง</param>
public sealed record OcrDuplicateDecision(
    OcrDuplicateVerdict Verdict, DuplicateCandidate? Match, decimal AmountGap);

/// <summary>
/// ด่านกันสร้างเอกสารซ้ำจากผลสแกน — pure, ทดสอบด้วยตัวเลขจริงได้
///
/// ═══ ทำไมต้องแก้ (ผลตรวจ OCR 2026-09-06 · T1-14) ═══
/// เดิมต้อง <b>เลขที่ + ยอดตรงถึงสตางค์</b> ถึงจะเตือน — ยอดต่างแม้บาทเดียว
/// <c>return null</c> เงียบ ⇒ OCR อ่าน 1,070 เป็น 1,010 (หลักเดียวเพี้ยน ซึ่ง
/// เป็นความผิดพลาดที่พบบ่อยที่สุดของ OCR) = ใบซ้ำหลุดเข้าระบบ ⇒ <b>เคลมภาษีซื้อ
/// สองครั้ง</b> จากใบกำกับใบเดียว (§87 รายงานภาษีซื้อเกินจริง)
///
/// นักบัญชีตัดสินอีกแบบ: §86/4 บังคับให้เลขใบกำกับ <b>unique ต่อผู้ขาย</b>
/// ⇒ "ผู้ขายเดียวกัน + เลขเดียวกัน" = ใบเดียวกัน<b>เสมอ</b> · ยอดต่างแปลว่า
/// อ่านผิด ไม่ใช่คนละใบ — ระบบจึงต้องเตือนแล้วบอกส่วนต่างให้คนดู
///
/// ═══ ทำไมยังต้องมีขอบเขตเวลา ═══
/// ผู้ขายจำนวนมากเริ่มเลขวิ่งใหม่ทุกปี ⇒ เลขเดียวกันข้ามปีเป็นคนละใบจริง —
/// ผู้เรียกจึงต้องจำกัดผู้สมัครให้อยู่ในกรอบ <see cref="MaxMonthsApart"/> เดือน
/// ก่อนส่งเข้ามา (ตัวกรองอยู่ที่ <see cref="WithinWindow"/> เพื่อให้กติกาเดียวกัน
/// ใช้ได้ทั้งฝั่ง query และฝั่งเทสต์)
/// </summary>
public static class OcrDuplicateScanRule
{
    /// <summary>ยอมรับผลต่างระดับปัดเศษเท่านั้นว่าเป็น "ยอดเดียวกัน"</summary>
    public const decimal AmountTolerance = 0.01m;

    /// <summary>กรอบเวลาที่ถือว่าเลขเดียวกัน = ใบเดียวกัน (ผู้ขายมักรีเซ็ตเลขรายปี)</summary>
    public const int MaxMonthsApart = 12;

    public static bool WithinWindow(DateTime? scanDate, DateTime candidateDate)
        => scanDate is not DateTime d
           || Math.Abs((candidateDate.Date - d.Date).TotalDays) <= MaxMonthsApart * 31;

    /// <param name="candidates">เอกสารที่ใช้เลขที่เดียวกัน (ผู้เรียกกรองเลขที่มาแล้ว)</param>
    /// <param name="scannedTotal">ยอดรวมที่ OCR อ่านได้ (null = อ่านไม่ได้)</param>
    /// <param name="scannedVendorTaxIdDigits">เลขผู้เสียภาษีผู้ขายจากผลสแกน (ว่าง = ไม่ทราบ)</param>
    /// <param name="scannedDate">วันที่บนเอกสารที่สแกน (null = อ่านไม่ได้ → ไม่กรองเวลา)</param>
    public static OcrDuplicateDecision Decide(
        IReadOnlyList<DuplicateCandidate> candidates,
        decimal? scannedTotal,
        string? scannedVendorTaxIdDigits,
        DateTime? scannedDate)
    {
        if (candidates == null || candidates.Count == 0)
            return new(OcrDuplicateVerdict.None, null, 0m);

        var vendor = (scannedVendorTaxIdDigits ?? "").Trim();

        // คู่ค้าต้องไม่ขัดกัน — "ไม่ทราบ" ผ่าน (ห้ามปล่อยใบซ้ำหลุดเพราะข้อมูลไม่ครบ)
        // แต่ "ทราบทั้งสองฝั่งแล้วไม่ตรง" = คนละผู้ขายที่บังเอิญใช้เลขรันเดียวกัน
        var pool = candidates
            .Where(c => WithinWindow(scannedDate, c.DocumentDate))
            .Where(c => vendor.Length == 0 || c.ContactTaxIdDigits.Length == 0
                        || c.ContactTaxIdDigits == vendor)
            .ToList();
        if (pool.Count == 0) return new(OcrDuplicateVerdict.None, null, 0m);

        if (scannedTotal is decimal t && t != 0m)
        {
            var exact = pool.FirstOrDefault(c => Math.Abs(c.TotalAmount - t) <= AmountTolerance);
            if (exact != null)
                return new(OcrDuplicateVerdict.SameNumberAndAmount, exact, 0m);

            // ยอดไม่ตรง → ใบที่ยอด**ใกล้ที่สุด** คือใบที่น่าจะเป็นใบเดียวกัน
            var near = pool.OrderBy(c => Math.Abs(c.TotalAmount - t)).First();
            return new(OcrDuplicateVerdict.SameNumberDifferentAmount, near,
                Math.Abs(near.TotalAmount - t));
        }

        // OCR อ่านยอดไม่ได้เลย — เลขที่ตรงก็พอที่จะเตือน (เดิมเงียบทั้งดุ้น)
        var first = pool.OrderByDescending(c => c.DocumentDate).First();
        return new(OcrDuplicateVerdict.SameNumberDifferentAmount, first, 0m);
    }
}
