using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีมตรวจงานค้าง 2026-10-08 (C-02): ใบวางบิลรวมใบแจ้งหนี้แปลงเป็นใบเสร็จ ⇒ ใบเสร็จลงแบบขายสด = รายได้ซ้ำ + ลูกหนี้ใบแจ้งหนี้ไม่ถูกตัด
/// </summary>
public class BillingNoteKindTests
{
    [Fact]
    public void ใบวางบิลที่บรรทัดอ้างใบแจ้งหนี้_เป็นใบรวม_ห้ามแปลง()
    {
        Assert.True(BillingNoteKind.IsRollup(DocumentType.BillingNote, anyLineHasSourceDocument: true));
        Assert.Contains("รับชำระตามใบวางบิล", BillingNoteKind.ConvertBlockedMessage("BN-2026-0001"));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบวางบิลจากใบเสนอราคา_ไม่ใช่ใบรวม_แปลงได้ตามเดิม()
        => Assert.False(BillingNoteKind.IsRollup(DocumentType.BillingNote, anyLineHasSourceDocument: false));

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Quotation)]
    public void ทิศตรงข้าม_เอกสารชนิดอื่นที่มีบรรทัดอ้างเอกสาร_ไม่ถูกนับเป็นใบวางบิลรวม(DocumentType t)
        => Assert.False(BillingNoteKind.IsRollup(t, anyLineHasSourceDocument: true));
}
