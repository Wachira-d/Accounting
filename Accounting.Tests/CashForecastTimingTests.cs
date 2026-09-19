using System;
using System.Collections.Generic;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// การพยากรณ์กระแสเงินสดต้องดู **พฤติกรรมการจ่ายจริง** ไม่ใช่ `DueDate` ล้วน
/// (`DECISION_AUDIT_2026-09-18.md` §3 D4-8). สองครึ่ง:
/// (ก) ลูกค้าที่จ่ายช้าประจำต้องถูกเลื่อน · (ข) ลูกค้าที่ไม่มีประวัติ/จ่ายตรง
/// ต้อง **ไม่ถูกแตะ** (กราฟของคนที่ถูกอยู่แล้วต้องไม่ขยับ)
/// </summary>
public class CashForecastTimingTests
{
    private static readonly DateTime Today = new(2026, 9, 1);
    private static readonly DateTime Due = new(2026, 9, 20);

    // ── (ข) ของเดิมที่ถูกอยู่แล้วต้องไม่ถูกแตะ ─────────────────────────
    [Fact]
    public void ไม่มีประวัติเลย_ใช้วันครบกำหนดเดิมเป๊ะ()
    {
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 20), Today, null);
        Assert.Equal(Due, t.ExpectedDate);
        Assert.Equal(CashTimingBasis.DueDate, t.Basis);
        Assert.Equal(0, t.LagDaysApplied);
        Assert.Null(t.Note);
    }

    [Fact]
    public void ประวัติน้อยกว่าขั้นต่ำ_ยังไม่เชื่อ()
    {
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 20), Today, new[] { 40, 45 });
        Assert.Equal(Due, t.ExpectedDate);
        Assert.Equal(CashTimingBasis.DueDate, t.Basis);
    }

    [Fact]
    public void จ่ายตรงเวลามาตลอด_ต้องไม่เลื่อน()
    {
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 20), Today, new[] { 0, 0, 0, 1 });
        Assert.Equal(Due, t.ExpectedDate);
        Assert.Equal(0, t.LagDaysApplied);
    }

    [Fact]
    public void เคยจ่ายก่อนกำหนด_ห้ามดึงเงินเข้ามาเร็วกว่ากำหนด()
    {
        // มองโลกในแง่ดีโดยไม่มีสิทธิ์ = ทิศที่ความเสียหายมองไม่เห็น
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 20), Today, new[] { -5, -7, -6 });
        Assert.Equal(Due, t.ExpectedDate);
        Assert.Equal(0, t.LagDaysApplied);
    }

    // ── (ก) ครึ่งที่พิสูจน์ว่าพฤติกรรมจริงถูกนับ ────────────────────────
    [Fact]
    public void ลูกค้าจ่ายช้าประจำ25วัน_ต้องเลื่อนตามมัธยฐาน()
    {
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today,
            new[] { 24, 25, 26, 25 });
        Assert.Equal(25, t.LagDaysApplied);
        Assert.Equal(Due.AddDays(25), t.ExpectedDate);
        Assert.Equal(CashTimingBasis.DueDatePlusHistory, t.Basis);
        Assert.Contains("พฤติกรรมการชำระจริง", t.Note);
    }

    [Fact]
    public void ใบเดียวที่ช้ามาก_ห้ามลากทั้งกราฟ_ใช้มัธยฐานไม่ใช่ค่าเฉลี่ย()
    {
        // mean ของ {2,3,4,300} = 77 · median = 3 → ต้องได้ 3
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today,
            new[] { 2, 3, 4, 300 });
        Assert.Equal(3, t.LagDaysApplied);
    }

    [Fact]
    public void มัธยฐานเกินเพดาน_ถูกตัดที่เพดาน()
    {
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today,
            new[] { 300, 320, 400 });
        Assert.Equal(CashForecastTiming.MaxLagDays, t.LagDaysApplied);
    }

    [Fact]
    public void จำนวนตัวอย่างคู่_มัธยฐานปัดแบบAwayFromZero()
    {
        // {10,11,12,13} → (11+12)/2 = 11.5 → 12 (ไม่ใช่ banker's = 12 เหมือนกัน
        // แต่ {10,11,12,14} → 11.5 → 12 เช่นกัน; ใช้ {9,10,11,12} → 10.5 → 11)
        var t = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today,
            new[] { 9, 10, 11, 12 });
        Assert.Equal(11, t.LagDaysApplied);
    }

    [Fact]
    public void ตัวอย่างน้อยเกินไป_ต้องไม่เลื่อนเลย_ไม่ใช่เลื่อน0วันแบบเงียบ()
    {
        var one = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today, new[] { 5 });
        Assert.Equal(CashTimingBasis.DueDate, one.Basis);
        Assert.Equal(0, one.LagDaysApplied);

        var none = CashForecastTiming.Resolve(Due, new DateTime(2026, 8, 1), Today, Array.Empty<int>());
        Assert.Equal(CashTimingBasis.DueDate, none.Basis);
    }

    // ── ไม่มีวันครบกำหนด / เลยกำหนด ───────────────────────────────────
    [Fact]
    public void ไม่มีวันครบกำหนด_ใช้วันที่บนใบและบอกว่าที่มาอ่อน()
    {
        var docDate = new DateTime(2026, 9, 10);
        var t = CashForecastTiming.Resolve(null, docDate, Today, null);
        Assert.Equal(docDate, t.ExpectedDate);
        Assert.Equal(CashTimingBasis.DocumentDateOnly, t.Basis);
    }

    [Fact]
    public void เลยกำหนดแล้ว_ต้องบอกว่าเกินมากี่วัน_ไม่ใช่พยากรณ์ว่าจะได้()
    {
        var t = CashForecastTiming.Resolve(new DateTime(2026, 8, 1), new DateTime(2026, 7, 1),
            Today, null);
        Assert.Equal(CashTimingBasis.Overdue, t.Basis);
        Assert.Contains("เลยกำหนด", t.Note);
    }

    [Fact]
    public void ใบที่เลยกำหนดแต่ถูกเลื่อนจนถึงอนาคต_ไม่ใช่Overdue()
    {
        // ครบกำหนด 20 ส.ค. · คู่ค้ารายนี้ช้าประจำ 30 วัน → 19 ก.ย. (อนาคต)
        var t = CashForecastTiming.Resolve(new DateTime(2026, 8, 20), new DateTime(2026, 7, 20),
            Today, new[] { 29, 30, 31 });
        Assert.Equal(CashTimingBasis.DueDatePlusHistory, t.Basis);
        Assert.Equal(new DateTime(2026, 9, 19), t.ExpectedDate);
    }
}
