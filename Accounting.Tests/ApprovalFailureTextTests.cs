using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทางอนุมัติที่ไม่มีหน้าต่างยืนยันคำเตือน (OCR อนุมัติอัตโนมัติ → <c>[APPROVE-FAIL]</c> · ปุ่ม LINE)
/// ต้องบอก**ตัวคำเตือน** — ฝ่ายค้านรอบ 190: คำเตือน "ภาษีซื้อจะถูกพัก 11640" ทำให้ใบจากสแกนค้าง Draft
/// แต่ผู้ใช้เห็นแค่ "มีจุดที่ต้องตรวจ (1 รายการ)"
/// </summary>
public class ApprovalFailureTextTests
{
    [Fact]
    public void คำเตือนพักภาษีซื้อ_ต้องไปถึงผู้ใช้ทั้งข้อความ_ไม่ใช่แค่จำนวน()
    {
        var notice = InputVatParkingNotice.Build(
            isPurchaseInputVatType: true, isForeignService: false, inputVatAccountCodeOverride: null,
            claimableVat: 70m, claimIntentDeclared: true,
            missingFields: new[] { "ที่อยู่ผู้ขาย" }, missingContactFields: new[] { "ที่อยู่" });
        Assert.NotNull(notice);
        var text = DocumentApprovalWarningsException.DescribeForUser(
            new DocumentApprovalWarningsException(new[] { notice! }));
        Assert.Contains("11640", text);
        Assert.Contains("ที่อยู่ผู้ขาย", text);
        Assert.Contains("(1 รายการ)", text);
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อผิดพลาดชนิดอื่น_ข้อความเดิมไม่ถูกแตะ()
    {
        var ex = new InvalidOperationException("งวดบัญชีปิดแล้ว");
        Assert.Equal("งวดบัญชีปิดแล้ว", DocumentApprovalWarningsException.DescribeForUser(ex));
    }
}
