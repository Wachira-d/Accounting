using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PR2 — ผลฝ่ายค้านรอบสอง: เลข 50 ทวิ ภ.ง.ด.1 อัตโนมัติ (P1-c) · แถวรายคนไม่ถูกโหลด (P1-b) ·
/// การนำส่ง ภ.ง.ด. = หลักฐาน "ยื่นแล้ว" (P1-a) · กลับรายการจ่ายได้แต่แก้ยอดถูกบล็อก (P2-g · คำตัดสินข้อ 112)
/// </summary>
public class PayrollPnd1CertsTests
{
    [Fact]
    public void ใบแรกของงวด_คงรูปเลขเดิม()
        => Assert.Equal("PND1-202609-E001",
            PayrollPnd1Certs.NextNumber(2026, 9, "E001", new HashSet<string>()));

    [Fact]
    public void ยกเลิกรอบแล้วจ่ายรอบเดือนเดิมใหม่_เลขเดิมยังถูกใช้โดยใบVoided_ได้ต่อท้าย2_แล้ว3()
    {
        var taken = new HashSet<string> { "PND1-202609-E001" };
        Assert.Equal("PND1-202609-E001-2", PayrollPnd1Certs.NextNumber(2026, 9, "E001", taken));
        taken.Add("PND1-202609-E001-2");
        Assert.Equal("PND1-202609-E001-3", PayrollPnd1Certs.NextNumber(2026, 9, "E001", taken));
    }

    [Fact]
    public void เลขของคนอื่นหรือเดือนอื่น_ไม่ทำให้ต่อท้าย()
    {
        var taken = new HashSet<string> { "PND1-202609-E002", "PND1-202608-E001" };
        Assert.Equal("PND1-202609-E001", PayrollPnd1Certs.NextNumber(2026, 9, "E001", taken));
        Assert.Equal("PND1-202609-", PayrollPnd1Certs.Prefix(2026, 9));
    }

    [Theory]
    [InlineData(0, 5, true)]    // รอบมี 5 คนแต่ไม่มีแถวรายคนที่โหลดมา = ผู้เรียกลืม Include ⇒ ต้องล้มดัง
    [InlineData(5, 5, false)]
    [InlineData(0, 0, false)]   // รอบว่างจริง — ไม่ใช่บั๊ก
    [InlineData(3, 5, false)]   // โหลดมาแล้ว (บางคนอาจถูกเอาออก) — ไม่ใช่ "ไม่ได้โหลด"
    public void แถวรายคนไม่ถูกโหลด_ตัดสินจากจำนวนคนของรอบ(int loaded, int employeeCount, bool expected)
        => Assert.Equal(expected, PayrollPnd1Certs.DetailsNotLoaded(loaded, employeeCount));

    [Theory]
    [InlineData("WhtPnd1", TaxType.WithholdingTax1)]
    [InlineData("WhtPnd3", TaxType.WithholdingTax3)]
    [InlineData("WhtPnd53", TaxType.WithholdingTax53)]
    [InlineData("WhtPnd54", TaxType.WithholdingTax54)]   // ฝ่ายค้านรอบสาม P2-5 — เดิมหลุด
    public void การนำส่งภงด_แมปเป็นแบบของหนังสือรับรอง(string type, TaxType form)
        => Assert.Equal(form, WhtCertVoidGuard.RemittanceForm(type));

    [Theory]
    [InlineData("SsoSps110")]
    [InlineData("VatPp30")]
    [InlineData(null)]
    public void การนำส่งที่ไม่ใช่ภาษีหักณที่จ่าย_ไม่ใช่หลักฐานของ50ทวิ(string? type)
        => Assert.Null(WhtCertVoidGuard.RemittanceForm(type));

    [Fact]
    public void นำส่งภงด1แล้ว_ยกเลิกรอบไม่ได้_และทางปลดชี้รายการนำส่ง()
    {
        var ev = PayrollRunLockEvidence.From(
            new[] { new PayrollFilingMark(PayrollRunLockEvidence.Pnd1Label, PayrollFilingSource.StatutoryRemittance) }, false, 0);
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, null, ev);
        Assert.False(can);
        Assert.Contains("รายการนำส่ง ภ.ง.ด.1", reason);
        Assert.False(PayrollRunEditPolicy.CanEditAmounts(PayrollRunEditPolicy.Approved, ev).Can);
    }

    [Fact]
    public void คำตัดสิน112_ยื่นภงด1แล้ว_กลับรายการจ่ายได้_แต่แก้ยอดหลังกลับรายการถูกบล็อก()
    {
        var filed = PayrollRunLockEvidence.From(
            new[] { new PayrollFilingMark(PayrollRunLockEvidence.Pnd1Label, PayrollFilingSource.TaxCalendar) }, false, 0);
        Assert.True(PayrollRunEditPolicy.CanReopen(PayrollRunEditPolicy.Paid, null).Can);         // กลับรายการจ่ายไม่แก้ยอดที่ยื่น
        Assert.False(PayrollRunEditPolicy.CanEditAmounts(PayrollRunEditPolicy.Approved, filed).Can); // หลังกลับรายการ (Approved) แก้ยอดไม่ได้
        Assert.True(PayrollRunEditPolicy.CanEditAmounts(PayrollRunEditPolicy.Approved, PayrollRunLockEvidence.None).Can);
    }

    [Fact]
    public void ปันต้นทุนแล้ว_ข้อความทางไปต่อแยกตามสถานะ_รอบจ่ายแล้วต้องกลับรายการจ่ายก่อน()
    {
        var allocated = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), false, allocatedRows: 2);
        var paid = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, null, allocated).Reason;
        var calc = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Calculated, null, allocated).Reason;
        Assert.Contains("กลับรายการจ่าย", paid);
        Assert.DoesNotContain("กลับรายการจ่าย", calc);
        Assert.Contains("แก้ยอด", calc);
    }
}
