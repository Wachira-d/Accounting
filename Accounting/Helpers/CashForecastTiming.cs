using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>ความน่าเชื่อของ "วันที่คาดว่าเงินจะเข้า/ออก" — ต้องเป็นค่าใน enum
/// ไม่ใช่ตัวเลข confidence ที่แต่งขึ้น (`DECISION_DOCTRINE` §1 G1/G3)</summary>
public enum CashTimingBasis
{
    /// <summary>ไม่รู้วันครบกำหนดเลย — ใช้วันที่บนเอกสาร (อ่อนสุด)</summary>
    DocumentDateOnly = 0,
    /// <summary>ใช้วันครบกำหนดตามใบ — ยังไม่มีประวัติของคู่ค้ารายนี้</summary>
    DueDate = 1,
    /// <summary>ใช้วันครบกำหนด + พฤติกรรมการจ่ายจริงของคู่ค้ารายนี้</summary>
    DueDatePlusHistory = 2,
    /// <summary>เลยกำหนดไปแล้ว — เงินยังไม่เข้า/ออก (ไม่ใช่การพยากรณ์)</summary>
    Overdue = 3,
}

/// <summary>
/// **ตัวคำนวณ "วันที่เงินจะเข้า/ออกจริง" ของการพยากรณ์กระแสเงินสด** (pure).
///
/// ═══ ของเดิมพังตรงไหน (`DECISION_AUDIT_2026-09-18.md` §3 D4-8) ═══
/// `CashForecastService` ใช้ `DueDate` **ล้วน ๆ** ⇒ ลูกค้าที่จ่ายช้าเฉลี่ย
/// 25 วันทุกใบมาตลอดปี ยังถูกพยากรณ์ว่าจะจ่ายตรงวันครบกำหนด ⇒ กราฟบอกว่า
/// เงินพอ ทั้งที่จริงจะขาดมือ — **ทิศที่อันตรายที่สุดของโดเมนนี้**
/// (พยากรณ์ที่มองโลกในแง่ดีเกินจริง ผู้ใช้ไม่เห็นความเสียหายจนเงินหมด)
///
/// ═══ กติกา ═══
/// เลื่อนวันด้วย **ค่ามัธยฐาน (median) ของความช้าในอดีต** ของคู่ค้ารายนั้น
/// ไม่ใช่ค่าเฉลี่ย — ใบเดียวที่ช้า 300 วันต้องไม่ลากทั้งกราฟ (mean พัง, median ไม่พัง)
/// และใช้เฉพาะเมื่อมีประวัติ **อย่างน้อย <see cref="MinSamples"/> ใบ**
/// มิฉะนั้น "ไม่รู้" ⇒ คงวันครบกำหนดเดิมไว้ (ห้ามแต่งค่า — F2 ข้อ 3)
///
/// เลื่อนได้เฉพาะ **ช้าลง** เท่านั้น (`lag ≥ 0`): คู่ค้าที่เคยจ่ายก่อนกำหนด
/// ไม่ใช่คำมั่นว่าจะจ่ายก่อนกำหนดอีก — การดึงเงินเข้ามาเร็วกว่ากำหนดคือการ
/// มองโลกในแง่ดีโดยไม่มีสิทธิ์ (ทิศที่ความเสียหายมองไม่เห็น)
/// </summary>
public static class CashForecastTiming
{
    /// <summary>จำนวนใบในอดีตขั้นต่ำก่อนจะเชื่อพฤติกรรมของคู่ค้ารายนั้น</summary>
    public const int MinSamples = 3;

    /// <summary>เพดานการเลื่อน (วัน) — กันใบเก่าค้างปีลากกราฟออกนอกกรอบ</summary>
    public const int MaxLagDays = 120;

    /// <param name="ExpectedDate">วันที่คาดว่าเงินจะเข้า/ออก</param>
    /// <param name="Basis">ใช้อะไรตัดสิน — หน้าจอต้องบอกผู้ใช้ตรง ๆ</param>
    /// <param name="LagDaysApplied">จำนวนวันที่เลื่อนออกไปจากวันครบกำหนด</param>
    /// <param name="Note">ข้อความไทยอธิบายที่มา (null = ไม่ได้เลื่อน)</param>
    public sealed record Timing(
        DateTime ExpectedDate,
        CashTimingBasis Basis,
        int LagDaysApplied,
        string? Note);

    /// <summary>
    /// ค่ามัธยฐานของ "จำนวนวันที่จ่ายช้ากว่ากำหนด" — คืน null เมื่อตัวอย่างน้อย
    /// เกินไป ("ไม่รู้" ไม่ใช่ 0).
    /// **private โดยตั้งใจ** — ผู้เรียกทั้งระบบต้องเข้าทาง <see cref="Resolve"/>
    /// ตัวเดียว ไม่งั้นจะมีคนเอามัธยฐานไปใช้โดยข้ามกติกา "เลื่อนเฉพาะช้าลง"
    /// และเพดาน <see cref="MaxLagDays"/> (public ที่ไม่มีผู้เรียกนอกไฟล์ = ของที่
    /// ไม่มีใครเรียก — `tools/dead_helper_check.py`)
    /// </summary>
    private static int? MedianLagDays(IReadOnlyCollection<int> historicalLagDays)
    {
        if (historicalLagDays == null || historicalLagDays.Count < MinSamples) return null;
        var sorted = historicalLagDays.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1
            ? sorted[mid]
            : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
        return median;
    }

    /// <summary>
    /// คำนวณวันที่คาดว่าเงินจะเข้า/ออกของเอกสาร 1 ใบ.
    /// </summary>
    /// <param name="dueDate">วันครบกำหนดตามใบ (null = ไม่มี)</param>
    /// <param name="documentDate">วันที่บนใบ — ใช้เมื่อไม่มีวันครบกำหนด</param>
    /// <param name="today">วันนี้ (ตัดเวลาออกแล้ว)</param>
    /// <param name="historicalLagDays">ความช้าของใบที่ชำระแล้วของคู่ค้ารายนี้ (วัน)</param>
    public static Timing Resolve(
        DateTime? dueDate, DateTime documentDate, DateTime today,
        IReadOnlyCollection<int>? historicalLagDays)
    {
        var baseDate = (dueDate ?? documentDate).Date;
        var basis = dueDate.HasValue ? CashTimingBasis.DueDate : CashTimingBasis.DocumentDateOnly;

        var median = MedianLagDays(historicalLagDays ?? Array.Empty<int>());
        // เลื่อนเฉพาะ "ช้าลง" — เคยจ่ายก่อนกำหนดไม่ใช่คำมั่นว่าจะจ่ายก่อนอีก
        var lag = median.HasValue && median.Value > 0
            ? Math.Min(median.Value, MaxLagDays)
            : 0;

        var expected = baseDate.AddDays(lag);
        if (lag > 0)
            basis = CashTimingBasis.DueDatePlusHistory;

        if (expected < today.Date)
            return new Timing(expected, CashTimingBasis.Overdue, lag,
                $"เลยกำหนดมาแล้ว {(int)(today.Date - expected).TotalDays} วัน");

        return new Timing(expected, basis, lag,
            lag > 0
                ? $"เลื่อนจากวันครบกำหนด {lag} วัน ตามพฤติกรรมการชำระจริงของคู่ค้ารายนี้ "
                  + $"(มัธยฐานจาก {historicalLagDays!.Count} ใบที่ผ่านมา)"
                : null);
    }
}
