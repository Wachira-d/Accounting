using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>บอร์ดตัดสิน 2026-10-13 (ทีม B · A3) — ใบลดหนี้ซื้อมาก่อน 50 ทวิ (เกณฑ์เงินสด): ป้าย "50 ทวิ ต้องตรวจ" ต้องบอกตัวเลขที่ต้องกรอก
/// ตอนบันทึกจ่าย ("ภาษีหักที่เหลือหลังใบลดหนี้ = X (ใบเดิม Y − ใบลดหนี้ Z)") — เดิมบอกแค่ "ตอนออกใบให้ลดยอดตามใบลดหนี้" ให้ผู้ใช้คิดเอง ·
/// ตัวคิด/ข้อความตัวเดียว <see cref="WhtCreditNoteRemainder"/> ที่ <c>AdjustCertsForCreditNoteAsync</c> เรียกทั้งสองกิ่ง</summary>
public class WhtCreditNoteRemainderTests
{
    [Fact]
    public void A3_ใบลดหนี้ใบเดียวก่อนออก50ทวิ_ป้ายบอกยอดที่ต้องแก้ตรงตัว()
    {
        // ใบเดิมหัก 3% ของ 10,000 = 300 · ใบลดหนี้ 1,000 หัก 30 ⇒ เหลือ 270
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 30m });

        Assert.Equal(270m, f.Remaining);
        Assert.Equal(30m, f.CreditNoteTax);
        Assert.Equal(1, f.CreditNoteCount);
        Assert.Equal(0m, f.Excess);
        var reason = WhtCreditNoteRemainder.BeforeAnyCertReason("ใบลดหนี้ CN-001 ลดภาษีหัก ณ ที่จ่าย 30.00 บาท", f);
        Assert.Contains("ภาษีหักที่เหลือหลังใบลดหนี้ = 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00)", reason);
        Assert.Contains("ตอนบันทึกจ่ายให้แก้ช่องภาษีหักเป็น 270.00", reason);
        Assert.StartsWith("ใบลดหนี้ CN-001", reason);
        // ข้อความเดิมที่ให้ผู้ใช้คิดเองต้องไม่กลับมา
        Assert.DoesNotContain("ตอนออกใบให้ลดยอดตามใบลดหนี้", reason);
    }

    [Fact]
    public void A3_ใบลดหนี้ใบที่สอง_นับใบแรกด้วย_ไม่บอกยอดที่ลืมใบก่อน()
    {
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 30m, 45m });

        Assert.Equal(225m, f.Remaining);
        Assert.Equal(75m, f.CreditNoteTax);
        Assert.Equal(2, f.CreditNoteCount);
        Assert.Contains("ภาษีหักที่เหลือหลังใบลดหนี้ = 225.00 (ใบเดิม 300.00 − ใบลดหนี้รวม 2 ใบ 75.00)", WhtCreditNoteRemainder.BeforeAnyCertReason("p", f));
    }

    [Fact]
    public void A3_ไม่มีใบลดหนี้ที่ลดภาษี_ยอดเท่าใบเดิม_ค่าศูนย์และติดลบไม่นับ()
    {
        // ทิศตรงข้าม: ใบลดหนี้ที่ไม่ลดภาษีหัก (0) หรือค่าผิดรูป (ติดลบ) ต้องไม่ขยับยอด/ไม่นับเป็นใบ
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 0m, -10m });

        Assert.Equal(300m, f.Remaining);
        Assert.Equal(0m, f.CreditNoteTax);
        Assert.Equal(0, f.CreditNoteCount);
        Assert.Equal(300m, f.LeftToWithhold);
    }

    [Fact]
    public void A3_ใบลดหนี้ลดเกินใบเดิม_เหลือศูนย์พร้อมบอกยอดเกิน_ไม่ใช่ตัวเลขติดลบ()
    {
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 400m });

        Assert.Equal(0m, f.Remaining);
        Assert.Equal(100m, f.Excess);
        var text = WhtCreditNoteRemainder.BeforeAnyCertReason("p", f);
        Assert.Contains("= 0.00 (ใบเดิม 300.00 − ใบลดหนี้ 400.00 — ใบลดหนี้ลดเกินภาษีหักของใบเดิม 100.00", text);
        Assert.Contains("แก้ช่องภาษีหักเป็น 0.00", text);
        Assert.DoesNotContain("-100", text);
    }

    [Fact]
    public void A3_จ่ายไม่ครบ_บอกยอดที่เหลือให้หักงวดถัดไป_หักที่ออกใบแล้ว()
    {
        // ใบเดิม 300 · ใบลดหนี้ 30 ⇒ 270 · งวดแรกจ่ายแล้วออก 50 ทวิ 150 ⇒ งวดที่เหลือหักอีก 120
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 30m }, alreadyCertified: 150m);

        Assert.Equal(270m, f.Remaining);
        Assert.Equal(150m, f.AlreadyCertified);
        Assert.Equal(120m, f.LeftToWithhold);
        Assert.Equal(0m, f.OverCertified);
        var reason = WhtCreditNoteRemainder.PartlyPaidReason("p", f);
        Assert.Contains("ภาษีหักที่เหลือหลังใบลดหนี้ = 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00)", reason);
        Assert.Contains("50 ทวิ ของงวดที่จ่ายแล้ว 150.00", reason);
        Assert.Contains("แก้ช่องภาษีหักรวมเป็น 120.00", reason);
        Assert.DoesNotContain("เกินยอดที่เหลือ", reason);
    }

    [Fact]
    public void A3_จ่ายไม่ครบ_50ทวิที่ออกแล้วเกินยอดที่เหลือ_บอกยอดเกินและทางไปต่อ()
    {
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 250m }, alreadyCertified: 120m);

        Assert.Equal(50m, f.Remaining);
        Assert.Equal(0m, f.LeftToWithhold);
        Assert.Equal(70m, f.OverCertified);
        var reason = WhtCreditNoteRemainder.PartlyPaidReason("p", f);
        Assert.Contains("แก้ช่องภาษีหักรวมเป็น 0.00", reason);
        Assert.Contains("เกินยอดที่เหลืออยู่ 70.00", reason);
        Assert.Contains("ยกเลิกแล้วออกใบของงวดที่จ่ายแล้วใหม่", reason);
    }

    [Fact]
    public void A3_ปัดสองตำแหน่งแบบปัดขึ้นที่ครึ่ง_และหมายเหตุบนใบลดหนี้บอกเลขใบต้นทางพร้อมยอด()
    {
        var f = WhtCreditNoteRemainder.Compute(100.005m, new[] { 0.005m });

        Assert.Equal(100.01m, f.OriginalTax);   // AwayFromZero ไม่ใช่ banker's (100.00)
        Assert.Equal(0.01m, f.CreditNoteTax);
        Assert.Equal(100m, f.Remaining);
        var notice = WhtCreditNoteRemainder.Notice("PI-2026-0001", f);
        Assert.StartsWith("PI-2026-0001: ติดป้าย \"50 ทวิ ต้องตรวจ\" — ภาษีหักที่เหลือหลังใบลดหนี้ = 100.00", notice);
    }
}
