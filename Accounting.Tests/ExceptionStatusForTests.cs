using Accounting.Helpers;
using Accounting.Middleware;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-08: Error Logs บันทึก BusinessRuleException เป็น 500 ทั้งที่ผู้ใช้ได้ 400/409 (ตารางสถานะสองชุดใน ExceptionMiddleware)
/// ⇒ ยุบเป็น <see cref="ExceptionMiddleware.StatusFor"/> ตัวเดียว · ล็อกค่าที่ผู้ใช้ได้รับจริงต่อชนิด
/// </summary>
public class ExceptionStatusForTests
{
    [Fact]
    public void กฎธุรกิจ_ใช้สถานะของตัวเอง_ไม่ใช่500()
    {
        Assert.Equal(400, ExceptionMiddleware.StatusFor(new BusinessRuleException("x")));
        Assert.Equal(409, ExceptionMiddleware.StatusFor(new BusinessRuleException("ซ้ำ", "PAYROLL-DETAIL-DUPLICATE", 409)));
        Assert.Equal(422, ExceptionMiddleware.StatusFor(new BusinessRuleException("ยืนยัน", PartialConvertPolicy.OverAmountRule, 422)));
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อผิดพลาดระบบจริงยังเป็น500()
    {
        Assert.Equal(500, ExceptionMiddleware.StatusFor(new NullReferenceException()));
        Assert.Equal(404, ExceptionMiddleware.StatusFor(new KeyNotFoundException()));
        Assert.Equal(400, ExceptionMiddleware.StatusFor(new InvalidOperationException("x")));
    }

    [Fact]
    public void คำถามยืนยัน409_422ที่มีรหัสกฎ_ไม่ลงErrorLogs()
    {
        Assert.True(ExceptionMiddleware.IsExpectedPrompt(new BusinessRuleException("ยอดเกิน", "CONVERT-OVER-AMOUNT", 422)));
        Assert.True(ExceptionMiddleware.IsExpectedPrompt(new BusinessRuleException("ยังไม่เป็นลูกค้า", "CONTACT-ROLE-CUSTOMER", 409)));
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อผิดพลาดอื่นยังลงErrorLogs()
    {
        Assert.False(ExceptionMiddleware.IsExpectedPrompt(new BusinessRuleException("ชื่อว่าง", "CONTACT-NAME-REQUIRED")));   // 400
        Assert.False(ExceptionMiddleware.IsExpectedPrompt(new BusinessRuleException("ไม่มีรหัส", null, 409)));
        Assert.False(ExceptionMiddleware.IsExpectedPrompt(new InvalidOperationException("x")));
        Assert.False(ExceptionMiddleware.IsExpectedPrompt(new Exception("boom")));
    }

    [Fact]
    public void คำเตือนก่อนอนุมัติจากทางที่ไม่มีหน้าต่างรับทราบ_ได้409_ไม่ใช่500()
        => Assert.Equal(409, ExceptionMiddleware.StatusFor(
            new Accounting.Services.Implementations.DocumentApprovalWarningsException(new[] { "ภาษีซื้อจะถูกพัก 11640" })));

    [Fact]
    public void ทิศตรงข้าม_Concurrencyยังเป็น500_ให้บั๊กจริงดัง()
        => Assert.Equal(500, ExceptionMiddleware.StatusFor(
            new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("affected 0")));
}
