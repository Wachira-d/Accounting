using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินเจ้าของข้อ 156 (2026-10-23 · ทีม P) — จังหวะออกเอกสารภาษีของที่พัก · ตัวตัดสินเดียว <see cref="LodgingTaxInvoiceTimingPolicy"/> ·
/// ล็อกสองทิศ: <b>ค่าเริ่มต้น = ผลเดิมทุกเคส</b> (ไม่ออกใบกำกับล่วงหน้าในทุกทางเข้า/ทุกสถานะ · ใบรับเงินตามประเภทเงินมัดจำ) ·
/// โหมด "ใบกำกับเมื่อยืนยัน" ออกเมื่อยืนยัน/รับเงินครั้งแรก · ไม่ออกซ้ำ · ใบถูกยกเลิก/มัดจำมี VAT/OTA/เว็บยืนยันเอง ไม่ออกเอง · ยอดลดหนี้/ส่วนต่างถูกต้อง
/// </summary>
public class LodgingTaxInvoiceTimingPolicyTests
{
    private const BookingInvoiceTiming Default = BookingInvoiceTiming.TaxInvoiceOnPaymentOrService;
    private const BookingInvoiceTiming AtConfirm = BookingInvoiceTiming.TaxInvoiceAtBooking;

    private static LodgingAdvanceFacts F(BookingInvoiceTiming timing,
        LodgingReservationStatus status = LodgingReservationStatus.Confirmed,
        LodgingAccountingMode mode = LodgingAccountingMode.Full,
        LodgingReservationSource source = LodgingReservationSource.Phone,
        LodgingAdvanceState advance = LodgingAdvanceState.None,
        bool vatDeposit = false, bool final = false)
        => new(timing, mode, status, source, advance, vatDeposit, final);

    public static IEnumerable<object[]> AllTriggersAndStatuses()
    {
        foreach (var t in Enum.GetValues<LodgingTaxTrigger>())
            foreach (var s in Enum.GetValues<LodgingReservationStatus>())
                yield return new object[] { t, s };
    }

    // ───────────── ค่าเริ่มต้น = พฤติกรรมเดิมทุกเคส ─────────────

    [Fact]
    public void ค่าเริ่มต้นของที่พักคือพฤติกรรมเดิม_ไม่ใช่ค่าเริ่มต้นของ_enum()
    {
        // คอลัมน์ DEFAULT 1 · entity ตั้งค่าเริ่มต้นเดียวกัน — ห้ามพึ่ง default(enum) = 0 (= ใบกำกับตอนจองของ CMS)
        Assert.Equal(BookingInvoiceTiming.TaxInvoiceOnPaymentOrService, LodgingTaxInvoiceTimingPolicy.Default);
        Assert.Equal(1, (int)LodgingTaxInvoiceTimingPolicy.Default);
        Assert.Equal(LodgingTaxInvoiceTimingPolicy.Default, new Accounting.Models.Entities.LodgingProperty().TaxInvoiceTiming);
    }

    [Theory]
    [MemberData(nameof(AllTriggersAndStatuses))]
    public void ค่าเริ่มต้น_ไม่ออกใบกำกับล่วงหน้าเลย_ทุกทางเข้า_ทุกสถานะ(LodgingTaxTrigger trigger, LodgingReservationStatus status)
    {
        foreach (var source in Enum.GetValues<LodgingReservationSource>())
            Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(Default, status, source: source), trigger).Issue);
    }

    [Fact]
    public void ค่าเริ่มต้น_ใบรับเงินตามประเภทเงินมัดจำเดิม_และไม่มีปุ่ม()
    {
        Assert.False(LodgingTaxInvoiceTimingPolicy.DepositCoveredByAdvance(LodgingAdvanceState.None));
        Assert.False(LodgingTaxInvoiceTimingPolicy.CanOfferManualIssue(F(Default)));
        var manual = LodgingTaxInvoiceTimingPolicy.Decide(F(Default), LodgingTaxTrigger.ManualIssue);
        Assert.Contains("ค่าเริ่มต้น", manual.Notice);
    }

    // ───────────── โหมดใบกำกับเมื่อยืนยัน/รับเงินครั้งแรก ─────────────

    [Theory]
    [InlineData(LodgingTaxTrigger.StaffConfirm, LodgingReservationStatus.Confirmed)]
    [InlineData(LodgingTaxTrigger.StaffConfirm, LodgingReservationStatus.Pending)]
    [InlineData(LodgingTaxTrigger.MoneyReceived, LodgingReservationStatus.Pending)]     // เงินเข้าแต่ที่พักไม่ยืนยันอัตโนมัติ = tax point แล้ว
    [InlineData(LodgingTaxTrigger.MoneyReceived, LodgingReservationStatus.Confirmed)]
    [InlineData(LodgingTaxTrigger.StaffCreateConfirmed, LodgingReservationStatus.Confirmed)]
    [InlineData(LodgingTaxTrigger.ManualIssue, LodgingReservationStatus.Confirmed)]
    public void โหมดยืนยัน_ออกเมื่อยืนยันหรือรับเงินครั้งแรก(LodgingTaxTrigger trigger, LodgingReservationStatus status)
        => Assert.True(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, status), trigger).Issue);

    [Theory]
    [InlineData(LodgingReservationStatus.CheckedIn)]
    [InlineData(LodgingReservationStatus.CheckedOut)]
    [InlineData(LodgingReservationStatus.Cancelled)]
    [InlineData(LodgingReservationStatus.NoShow)]
    public void โหมดยืนยัน_ไม่ออกเมื่อเช็คอินแล้วหรือปิดแล้ว_ใบกำกับออกตอนเช็คเอาต์ตามเดิม(LodgingReservationStatus status)
    {
        foreach (var t in Enum.GetValues<LodgingTaxTrigger>())
            Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, status), t).Issue);
        Assert.NotNull(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, status), LodgingTaxTrigger.ManualIssue).Notice);
    }

    [Fact]
    public void ไม่ออกซ้ำ_ใบล่วงหน้ามีผลแล้ว_ทุกทางเข้า()
    {
        foreach (var t in Enum.GetValues<LodgingTaxTrigger>())
            Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, advance: LodgingAdvanceState.Live), t).Issue);
        Assert.True(LodgingTaxInvoiceTimingPolicy.DepositCoveredByAdvance(LodgingAdvanceState.Live));
    }

    [Fact]
    public void ใบล่วงหน้าที่มีผล_ยังครอบเงินที่รับแม้สลับค่าตั้งกลับเป็นค่าเริ่มต้น()
    {
        // ตัดสินจากเอกสารที่ผูก ไม่ใช่ค่าตั้ง — สลับกลางทางแล้วเงินที่รับต้องไม่ได้ใบรับเงินมี VAT ซ้อน
        Assert.True(LodgingTaxInvoiceTimingPolicy.DepositCoveredByAdvance(
            LodgingTaxInvoiceTimingPolicy.StateOf(true, DocumentStatus.Approved)));
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(Default, advance: LodgingAdvanceState.Live), LodgingTaxTrigger.MoneyReceived).Issue);
    }

    [Fact]
    public void ใบล่วงหน้าถูกยกเลิก_ไม่ออกใหม่เอง_แต่กดเองได้_และเงินที่รับกลับเป็นแบบเดิม()
    {
        Assert.Equal(LodgingAdvanceState.Gone, LodgingTaxInvoiceTimingPolicy.StateOf(true, DocumentStatus.Voided));
        Assert.Equal(LodgingAdvanceState.Gone, LodgingTaxInvoiceTimingPolicy.StateOf(true, null));   // ลิงก์ชี้ใบที่ถูกลบ
        Assert.Equal(LodgingAdvanceState.None, LodgingTaxInvoiceTimingPolicy.StateOf(false, null));
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, advance: LodgingAdvanceState.Gone), LodgingTaxTrigger.MoneyReceived).Issue);
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, advance: LodgingAdvanceState.Gone), LodgingTaxTrigger.StaffConfirm).Issue);
        Assert.True(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, advance: LodgingAdvanceState.Gone), LodgingTaxTrigger.ManualIssue).Issue);
        Assert.False(LodgingTaxInvoiceTimingPolicy.DepositCoveredByAdvance(LodgingAdvanceState.Gone));
    }

    [Fact]
    public void มีมัดจำที่มี_VAT_อยู่แล้ว_คงจังหวะเดิม_กัน_VAT_ซ้อน()
    {
        foreach (var t in Enum.GetValues<LodgingTaxTrigger>())
            Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, vatDeposit: true), t).Issue);
        Assert.Contains("VAT ซ้อน", LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, vatDeposit: true), LodgingTaxTrigger.ManualIssue).Notice);
    }

    [Fact]
    public void มีใบเช็คเอาต์แล้ว_หรือโหมดไม่ออกเอกสาร_ไม่ออก()
    {
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, final: true), LodgingTaxTrigger.ManualIssue).Issue);
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, mode: LodgingAccountingMode.Off), LodgingTaxTrigger.MoneyReceived).Issue);
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, mode: LodgingAccountingMode.ReceiptOnly), LodgingTaxTrigger.StaffConfirm).Issue);
    }

    [Fact]
    public void OTA_ไม่ออกเองตอนสร้าง_แต่กดเองได้()
    {
        Assert.False(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, source: LodgingReservationSource.Ota), LodgingTaxTrigger.StaffCreateConfirmed).Issue);
        Assert.True(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, source: LodgingReservationSource.Agent), LodgingTaxTrigger.StaffCreateConfirmed).Issue);
        Assert.True(LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, source: LodgingReservationSource.Ota), LodgingTaxTrigger.ManualIssue).Issue);
    }

    [Fact]
    public void กดเองตอนรอยืนยัน_ปฏิเสธพร้อมทางไปต่อ()
    {
        var d = LodgingTaxInvoiceTimingPolicy.Decide(F(AtConfirm, LodgingReservationStatus.Pending), LodgingTaxTrigger.ManualIssue);
        Assert.False(d.Issue);
        Assert.Contains("ยืนยัน", d.Notice);
    }

    // ───────────── ด่านค่าตั้ง ─────────────

    [Fact]
    public void ด่านค่าตั้ง_โหมดยืนยันต้องออกเอกสารเต็มรูป_ค่าเริ่มต้นผ่านทุกโหมด()
    {
        foreach (var mode in Enum.GetValues<LodgingAccountingMode>())
            Assert.Null(LodgingTaxInvoiceTimingPolicy.TimingProblem(Default, mode));
        Assert.Null(LodgingTaxInvoiceTimingPolicy.TimingProblem(AtConfirm, LodgingAccountingMode.Full));
        Assert.NotNull(LodgingTaxInvoiceTimingPolicy.TimingProblem(AtConfirm, LodgingAccountingMode.Off));
        Assert.NotNull(LodgingTaxInvoiceTimingPolicy.TimingProblem((BookingInvoiceTiming)99, LodgingAccountingMode.Full));
    }

    // ───────────── ส่วนต่างตอนเช็คเอาต์ · ใบลดหนี้ ─────────────

    [Fact]
    public void ส่วนต่าง_เลื่อนวันแพงขึ้น_เรียกเก็บเพิ่ม_ตามรูปราคาของที่พัก()
    {
        var incl = LodgingTaxInvoiceTimingPolicy.StayDelta(true, 2803.74m, 3000m, 1869.16m, 2000m);
        Assert.True(incl.IsCharge); Assert.Equal(1000m, incl.Gross); Assert.Equal(1000m, incl.InPriceConvention);
        var excl = LodgingTaxInvoiceTimingPolicy.StayDelta(false, 3000m, 3210m, 2000m, 2140m);
        Assert.True(excl.IsCharge); Assert.Equal(1070m, excl.Gross); Assert.Equal(1000m, excl.InPriceConvention);
    }

    [Fact]
    public void ส่วนต่าง_ยอดเท่าเดิม_ไม่ออกอะไร_ยอดลด_ออกใบลดหนี้()
    {
        var same = LodgingTaxInvoiceTimingPolicy.StayDelta(true, 1869.16m, 2000m, 1869.16m, 2000m);
        Assert.False(same.IsCharge); Assert.False(same.IsCredit);
        var less = LodgingTaxInvoiceTimingPolicy.StayDelta(true, 934.58m, 1000m, 1869.16m, 2000m);
        Assert.True(less.IsCredit); Assert.Equal(-1000m, less.Gross);
    }

    [Theory]
    [InlineData(3000, 0, 3000)]       // ไม่ได้รับเงินเลย ⇒ ค่าปรับ 0 (ข้อ 126) ⇒ ลดหนี้เต็มใบ
    [InlineData(3000, 1500, 1500)]    // ริบมัดจำ 1,500 ⇒ ใบคงไว้ 1,500
    [InlineData(3000, 3000, 0)]       // ริบเต็ม ⇒ ไม่ออกใบลดหนี้
    [InlineData(3000, 3500, 0)]       // กันติดลบ
    public void ยกเลิก_ใบลดหนี้เท่าส่วนที่ไม่เรียกเก็บ(decimal advance, decimal forfeit, decimal expected)
        => Assert.Equal(expected, LodgingTaxInvoiceTimingPolicy.CancelCreditGross(advance, forfeit));

    [Fact]
    public void กระจายใบลดหนี้_เต็มใบเท่ายอดเดิมทุกบรรทัด()
    {
        var lines = new[] { 1000m, 500.55m, 99.45m };
        Assert.Equal(lines, LodgingTaxInvoiceTimingPolicy.AllocateCredit(lines, 1600m).ToArray());
    }

    [Fact]
    public void กระจายใบลดหนี้_บางส่วน_ผลรวมตรงพอดี_ไม่เกินยอดบรรทัด_ไม่ติดลบ()
    {
        var lines = new[] { 1000m, 333.33m, 0m, 166.67m };
        foreach (var credit in new[] { 0.01m, 1m, 333.33m, 750m, 1499.99m })
        {
            var alloc = LodgingTaxInvoiceTimingPolicy.AllocateCredit(lines, credit);
            Assert.Equal(credit, alloc.Sum());
            for (var i = 0; i < lines.Length; i++)
            {
                Assert.True(alloc[i] >= 0m);
                Assert.True(alloc[i] <= lines[i]);
            }
            Assert.Equal(0m, alloc[2]);
        }
    }

    [Fact]
    public void กระจายใบลดหนี้_ยอดเกินใบ_ตัดที่ยอดใบ_ไม่มีบรรทัด_คืนศูนย์()
    {
        Assert.Equal(new[] { 100m, 50m }, LodgingTaxInvoiceTimingPolicy.AllocateCredit(new[] { 100m, 50m }, 999m).ToArray());
        Assert.Empty(LodgingTaxInvoiceTimingPolicy.AllocateCredit(Array.Empty<decimal>(), 10m));
        Assert.Equal(new[] { 0m, 0m }, LodgingTaxInvoiceTimingPolicy.AllocateCredit(new[] { 100m, 50m }, 0m).ToArray());
    }

    // ───────────── ข้อความบนหน้าตั้งค่า (server computes) ─────────────

    [Fact]
    public void ตัวเลือกบนหน้าตั้งค่า_มีสองค่า_ค่าเริ่มต้นอยู่ก่อน_และอธิบายผลภาษีอ้างมาตรา()
    {
        var opts = LodgingTaxInvoiceTimingPolicy.Options();
        Assert.Equal(2, opts.Count);
        Assert.Equal(nameof(BookingInvoiceTiming.TaxInvoiceOnPaymentOrService), opts[0].Value);
        Assert.Equal(nameof(BookingInvoiceTiming.TaxInvoiceAtBooking), opts[1].Value);
        Assert.All(opts, o => Assert.Contains("78/1", o.TaxEffect));
        Assert.Contains("86/10", opts[1].TaxEffect);
        Assert.All(opts, o => Assert.True(Enum.TryParse<BookingInvoiceTiming>(o.Value, out _)));
    }

    [Fact]
    public void แบบใบกำกับทุกครั้งที่รับเงิน_อธิบายจากประเภทเงินมัดจำ_ไม่ใช่ค่าใหม่()
    {
        Assert.Contains("ใช้ได้ทันที", LodgingTaxInvoiceTimingPolicy.PaymentInvoiceNote(DepositVatTreatment.VatImmediate, false, true));
        Assert.Contains("รับรู้ VAT ทันที", LodgingTaxInvoiceTimingPolicy.PaymentInvoiceNote(DepositVatTreatment.FullDeposit, false, true));
        Assert.Contains("90/2", LodgingTaxInvoiceTimingPolicy.PaymentInvoiceNote(DepositVatTreatment.VatImmediate, false, false));
        Assert.Contains("90/2", LodgingTaxInvoiceTimingPolicy.PaymentInvoiceNote(DepositVatTreatment.VatImmediate, true, true));
    }
}
