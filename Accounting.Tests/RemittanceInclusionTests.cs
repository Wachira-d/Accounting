using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PR2 — ฝ่ายค้านรอบสาม P1-1/P2-5: บันทึกนำส่ง ภ.ง.ด. เป็นหลักฐาน "ยื่นแล้ว" เฉพาะของที่เกิด<b>ก่อน</b>การนำส่ง
/// (<see cref="RemittanceInclusion"/> + <c>WhtCertVoidGuard.Reason</c> แบบ 8 อาร์กิวเมนต์)
/// · สองทิศ: ใบ/รอบที่อยู่ในการนำส่งต้องยังล็อก และใบ/รอบที่เกิดหลังนำส่งต้องปลด
/// </summary>
public class RemittanceInclusionTests
{
    private static readonly DateTime RemittedAt = new(2026, 10, 5, 3, 30, 0, DateTimeKind.Utc);   // 05/10/2569 10:30 น. เวลาไทย

    [Fact]
    public void ของที่เกิดก่อนหรือพร้อมเวลานำส่ง_อยู่ในการนำส่ง()
    {
        Assert.True(RemittanceInclusion.Includes(RemittedAt, RemittedAt.AddDays(-3)));
        Assert.True(RemittanceInclusion.Includes(RemittedAt, RemittedAt));
    }

    [Fact]
    public void ของที่เกิดหลังนำส่ง_หรืองวดที่ยังไม่นำส่ง_ไม่อยู่ในการนำส่ง()
    {
        Assert.False(RemittanceInclusion.Includes(RemittedAt, RemittedAt.AddSeconds(1)));
        Assert.False(RemittanceInclusion.Includes(null, RemittedAt.AddDays(-30)));
    }

    [Fact]
    public void เวลานับเข้ายอดของใบ_ใช้เวลาออกใบก่อน_ไม่มีจึงใช้เวลาสร้างแถว()
    {
        var created = RemittedAt.AddDays(-10);
        Assert.Equal(RemittedAt.AddDays(1), RemittanceInclusion.CertCountedAt(RemittedAt.AddDays(1), created)); // ร่างก่อนนำส่ง ออกหลังนำส่ง
        Assert.Equal(created, RemittanceInclusion.CertCountedAt(null, created));
    }

    [Fact]
    public void เวลานำส่งล่าสุดต่องวด_แยกกุญแจงวด()
    {
        var latest = RemittanceInclusion.LatestByPeriod(new[]
        {
            ((TaxType.WithholdingTax53, 2026, 9), RemittedAt.AddDays(-1)),
            ((TaxType.WithholdingTax53, 2026, 9), RemittedAt),
            ((TaxType.WithholdingTax3, 2026, 9), RemittedAt.AddDays(-7)),
        });
        Assert.Equal(RemittedAt, latest[(TaxType.WithholdingTax53, 2026, 9)]);
        Assert.Equal(RemittedAt.AddDays(-7), latest[(TaxType.WithholdingTax3, 2026, 9)]);
        Assert.False(latest.ContainsKey((TaxType.WithholdingTax53, 2026, 8)));
    }

    [Fact]
    public void วันที่นำส่งในข้อความเป็นเวลาไทยปีพศ_ข้ามเที่ยงคืนถูกวัน()
    {
        Assert.Equal("05/10/2569 10:30 น.", RemittanceInclusion.ThaiStamp(RemittedAt));
        Assert.Equal("01/10/2569 01:00 น.", RemittanceInclusion.ThaiStamp(new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)));
    }

    // ── ตัวตัดสินยกเลิก 50 ทวิ ──

    [Fact]
    public void ใบที่ออกก่อนนำส่งภงด53_ยกเลิกไม่ได้_ข้อความบอกว่าอยู่ในการนำส่งณวันที่()
    {
        var why = WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-0007", TaxType.WithholdingTax53, 2026, 9,
            periodDeclaredOrFiled: false, remittedAtUtc: RemittedAt, certCountedAtUtc: RemittedAt.AddDays(-2));
        Assert.NotNull(why);
        Assert.Contains("อยู่ในการนำส่ง ภ.ง.ด.53 เดือน 09/2569 ณ วันที่ 05/10/2569 10:30 น.", why);
        Assert.Contains("ยื่น ภ.ง.ด.53 เพิ่มเติม", why);
    }

    [Fact]
    public void ใบที่ออกหลังนำส่ง_PVลงวันที่ย้อนเข้างวดที่นำส่งแล้ว_ยังค้างนำส่ง_ยกเลิกได้()
    {
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-0009", TaxType.WithholdingTax53, 2026, 9,
            periodDeclaredOrFiled: false, remittedAtUtc: RemittedAt, certCountedAtUtc: RemittedAt.AddHours(5)));
        // ภ.ง.ด.1: ใบของรอบที่ยกเลิกแล้วสร้าง/จ่ายใหม่หลังนำส่ง
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Printed, "PND1-202609-E001-2", TaxType.WithholdingTax1, 2026, 9,
            periodDeclaredOrFiled: false, remittedAtUtc: RemittedAt, certCountedAtUtc: RemittedAt.AddDays(1)));
    }

    [Fact]
    public void ภงด54_อยู่ในการนำส่ง_ยกเลิกไม่ได้_ป้ายแบบถูก()
    {
        var why = WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-54-1", TaxType.WithholdingTax54, 2026, 9,
            periodDeclaredOrFiled: false, remittedAtUtc: RemittedAt, certCountedAtUtc: RemittedAt.AddDays(-1));
        Assert.Contains("อยู่ในการนำส่ง ภ.ง.ด.54", why);
    }

    [Fact]
    public void ร่าง_หรืองวดที่ยังไม่นำส่ง_ยกเลิกได้()
    {
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Draft, "WHT-1", TaxType.WithholdingTax53, 2026, 9,
            false, RemittedAt, RemittedAt.AddDays(-2)));
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-1", TaxType.WithholdingTax53, 2026, 9,
            false, null, RemittedAt.AddDays(-2)));
    }

    [Fact]
    public void รายงานภาษีที่ประกาศว่ายื่น_ยังล็อกทั้งงวดตามเดิม_ไม่ขึ้นกับเวลานำส่ง()
    {
        var why = WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-1", TaxType.WithholdingTax3, 2026, 9,
            periodDeclaredOrFiled: true, remittedAtUtc: null, certCountedAtUtc: RemittedAt.AddDays(9));
        Assert.NotNull(why);
        Assert.Contains("ที่ยื่นแล้ว", why);
        Assert.DoesNotContain("อยู่ในการนำส่ง", why);
    }

    // ── รอบเงินเดือน: รอบที่ยกเลิกแล้วสร้างใหม่ในเดือนที่นำส่ง ภ.ง.ด.1 แล้ว ──

    [Fact]
    public void รอบเดิมที่สร้างก่อนนำส่ง_ล็อก_รอบใหม่ที่สร้างหลังนำส่ง_ไม่ล็อก()
    {
        var oldRunCreated = new DateTime(2026, 9, 25, 2, 0, 0, DateTimeKind.Utc);
        var newRunCreated = RemittedAt.AddDays(2);
        Assert.True(RemittanceInclusion.Includes(RemittedAt, oldRunCreated));
        Assert.False(RemittanceInclusion.Includes(RemittedAt, newRunCreated));

        // หลักฐานที่ service ประกอบได้จากสองทิศ ⇒ นโยบายยกเลิก/แก้ยอดตัดสินต่างกันจริง
        var locked = PayrollRunLockEvidence.From(
            new[] { new PayrollFilingMark(PayrollRunLockEvidence.Pnd1Label, PayrollFilingSource.StatutoryRemittance) }, false, 0);
        Assert.False(PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, null, locked).Can);
        Assert.True(PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, null, PayrollRunLockEvidence.None).Can);
    }

    // ── คำตัดสินข้อ 115 Q1 (2026-10-08): รอบเงินเดือนนับด้วยเวลาจ่ายจริง ──
    [Fact]
    public void รอบที่สร้างก่อนนำส่งแต่จ่ายหลังนำส่ง_ไม่อยู่ในการนำส่ง()
    {
        var created = new DateTime(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);
        var remitted = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        var paid = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
        Assert.False(RemittanceInclusion.Includes(remitted, RemittanceInclusion.RunCountedAt(paid, created)));
    }

    [Fact]
    public void ทิศตรงข้าม_รอบเก่าที่ไม่มีเวลาจ่าย_ใช้เวลาสร้างรอบ_ล็อกไว้เหมือนเดิม()
    {
        var created = new DateTime(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);
        var remitted = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(created, RemittanceInclusion.RunCountedAt(null, created));
        Assert.True(RemittanceInclusion.Includes(remitted, RemittanceInclusion.RunCountedAt(null, created)));
    }
}
