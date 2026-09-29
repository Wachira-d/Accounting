using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · B-06 — ด่านลบถาวร §87/3 / ม.10 ต้องไม่เป็น no-op กับใบที่ไม่ได้ผ่าน ApproveDocumentAsync
/// สองทิศ: ใบกำกับจาก POS/API/นำเข้า (RetentionUntil = null) ต้องถูกกัน · ร่าง/รออนุมัติ/เลข DRAFT- ยังลบได้เหมือนเดิม</summary>
public class DocumentRetentionTests
{
    private static readonly DateTime Today = new(2026, 9, 29);

    [Fact]
    public void ใบกำกับจาก_POS_API_ที่ไม่มีค่าบันทึก_ถูกกันด้วยวันที่คำนวณ()
    {
        // ใบกำกับ POS ลงวันที่ 5 ม.ค. 2026 · รอบบัญชี ม.ค.–ธ.ค. ⇒ เก็บถึง 31 ธ.ค. 2031
        var until = DocumentRetention.EffectiveUntil(DocumentStatus.Approved, "TIV-202601-0001", null,
            new DateTime(2026, 1, 5), 1);
        Assert.Equal(new DateTime(2031, 12, 31), until);
        Assert.True(DocumentRetention.InRetention(until, Today));
    }

    [Theory]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Voided)]
    [InlineData(DocumentStatus.Sent)]
    public void ใบที่ออกแล้วทุกสถานะ_ถูกกัน(DocumentStatus status)
    {
        var until = DocumentRetention.EffectiveUntil(status, "INV-0001", null, new DateTime(2025, 6, 1), 1);
        Assert.True(DocumentRetention.InRetention(until, Today));
    }

    [Fact]
    public void รอบบัญชีไม่ตรงปีปฏิทิน_นับจากสิ้นรอบ()
    {
        // รอบ ต.ค.–ก.ย. · ใบ 15 พ.ย. 2025 อยู่รอบ ต.ค.2025–ก.ย.2026 ⇒ 30 ก.ย. 2031
        Assert.Equal(new DateTime(2031, 9, 30), DocumentRetention.ComputeUntil(new DateTime(2025, 11, 15), 10));
    }

    [Fact]
    public void ค่าที่บันทึกไว้ตอนอนุมัติ_ชนะค่าที่คำนวณ()
    {
        var stored = new DateTime(2032, 12, 31);
        Assert.Equal(stored, DocumentRetention.EffectiveUntil(DocumentStatus.Approved, "INV-1", stored,
            new DateTime(2026, 1, 5), 1));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft, "DRAFT-abc")]
    [InlineData(DocumentStatus.WaitingApproval, "DRAFT-abc")]
    [InlineData(DocumentStatus.Rejected, "DRAFT-abc")]
    [InlineData(DocumentStatus.Voided, "DRAFT-3f2a")]   // ร่างที่ถูกยกเลิกก่อนออกเลข
    public void ร่างและใบที่ยังไม่เคยออกเลข_ยังลบได้เหมือนเดิม(DocumentStatus status, string number)
    {
        var until = DocumentRetention.EffectiveUntil(status, number, null, new DateTime(2026, 1, 5), 1);
        Assert.Null(until);
        Assert.False(DocumentRetention.InRetention(until, Today));
    }

    [Fact]
    public void พ้นช่วงเก็บแล้ว_ลบได้()
    {
        var until = DocumentRetention.EffectiveUntil(DocumentStatus.Approved, "INV-1", null, new DateTime(2019, 3, 1), 1);
        Assert.Equal(new DateTime(2024, 12, 31), until);
        Assert.False(DocumentRetention.InRetention(until, Today));
    }
}
