using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกกติกา "ออกแล้ว/ยังไม่ออก" ของสถานะเอกสาร (ERP_REVIEW_2026-09-05 A-01/A-02)
/// — ด่านที่เขียน <c>== Approved</c> เป๊ะ ๆ ทำให้ใบ Paid/Sent/Overdue หลุด (ค่ารับรอง YTD)
/// และ portal ลูกค้าเห็นใบร่างเป็น "รอชำระ" พร้อมปุ่มจ่าย
/// </summary>
public class DocumentStatusRulesTests
{
    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void ร่าง_รออนุมัติ_ตีกลับ_ยังไม่ออก(DocumentStatus s)
    {
        Assert.False(DocumentStatusRules.IsIssued(s));
        Assert.False(DocumentStatusRules.IsEffective(s));
        Assert.Contains(s, DocumentStatusRules.NotIssued);
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Overdue)]
    public void สถานะหลังอนุมัติทุกตัว_ออกแล้วและมีผล(DocumentStatus s)
    {
        Assert.True(DocumentStatusRules.IsIssued(s));
        Assert.True(DocumentStatusRules.IsEffective(s));
    }

    [Fact]
    public void ยกเลิก_ออกแล้วแต่ไม่มีผล()
    {
        Assert.True(DocumentStatusRules.IsIssued(DocumentStatus.Voided));
        Assert.False(DocumentStatusRules.IsEffective(DocumentStatus.Voided));
    }

    [Fact]
    public void ทุกค่าใน_enum_ถูกจัดหมวดครบ_ไม่มีค่าใหม่ที่ลืมตัดสิน()
    {
        foreach (var s in Enum.GetValues<DocumentStatus>())
        {
            var issued = DocumentStatusRules.IsIssued(s);
            var notIssued = DocumentStatusRules.NotIssued.Contains(s);
            Assert.NotEqual(issued, notIssued);
        }
    }

    [Fact]
    public void บั๊กเดิม_A01_ใบสำคัญจ่ายที่จ่ายแล้วเป็น_Paid_เคยหลุดจาก_YTD()
    {
        // ด่านเดิม: Status == Approved
        var paidPv = DocumentStatus.Paid;
        Assert.False(paidPv == DocumentStatus.Approved);          // reproduce: หลุด
        Assert.True(DocumentStatusRules.IsEffective(paidPv));    // หลังแก้: นับ
    }
}
