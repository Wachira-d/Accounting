using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>ฝ่ายค้าน W1c P2-5 (ทีม W1d) — ข้อความ "ไฟล์แนบยื่นแบบของรอบเงินเดือนไม่ถูกสร้าง" (ตัวเดียว · ไปแบนเนอร์/แจ้งเตือน/คำตอบปุ่มจ่าย)</summary>
public class PayrollFilingAttachmentNoticeTests
{
    [Fact]
    public void เลขผู้หักผิด_คำตอบปุ่มจ่ายบอกว่าไม่มีไฟล์แนบภงด1_พร้อมเหตุและทางไปต่อ()
    {
        var n = PayrollFilingAttachmentNotice.PredictAtPayment(375m, "0105556000001", "00000");
        Assert.NotNull(n);
        Assert.StartsWith("⚠️ ไม่มีไฟล์แนบ ภ.ง.ด.1 ของรอบนี้", n);
        Assert.Contains("ตั้งค่า → ข้อมูลบริษัท", n);          // เหตุ + ทางแก้ (ข้อความของด่านเดียวกับไฟล์นำเข้า)
        Assert.Contains("Export ยื่นภาษี", n);                  // ทางไปต่อ
    }

    [Fact]
    public void สาขาผู้หัก5หลัก_คำตอบปุ่มจ่ายบอกเหตุ()
        => Assert.Contains("10001", PayrollFilingAttachmentNotice.PredictAtPayment(1m, "0105561012346", "10001"));

    [Theory]
    [InlineData(0, "0105556000001")]       // ไม่มีภาษีหัก ⇒ ไม่สร้างไฟล์ ภ.ง.ด.1 อยู่แล้ว — ไม่เตือน
    [InlineData(375, "0105561012346")]     // ผู้หักถูก ⇒ สร้างได้
    public void ทิศตรงข้าม_ไม่มีภาษีหักหรือผู้หักถูก_ไม่เตือน(decimal wht, string taxId)
        => Assert.Null(PayrollFilingAttachmentNotice.PredictAtPayment(wht, taxId, "00000"));

    [Fact]
    public void ข้อผิดพลาดที่ไม่ใช่ธุรกิจ_ไม่echoข้อความภายใน()
    {
        var m = PayrollFilingAttachmentNotice.Pnd1Missing(new NullReferenceException("Object reference not set"));
        Assert.DoesNotContain("Object reference", m);
        Assert.Contains("แจ้งผู้ดูแลระบบ", m);
    }

    [Fact]
    public void รวมหลายไฟล์_ไม่มีปัญหาเป็นnull()
    {
        Assert.Null(PayrollFilingAttachmentNotice.Combine(Array.Empty<string>()));
        var both = PayrollFilingAttachmentNotice.Combine(new[]
        {
            PayrollFilingAttachmentNotice.Pnd1Missing(new BusinessRuleException("ก", "X")),
            PayrollFilingAttachmentNotice.Sso110Missing(new BusinessRuleException("ข", "Y")),
        });
        Assert.Contains("ภ.ง.ด.1", both);
        Assert.Contains("สปส.1-10", both);
    }
}
