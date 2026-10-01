using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 201 ทีม PL — ผลยกเลิกเอกสารจากทางเข้า CMS (ออเดอร์/การจอง) ต้องถึงเจ้าของร้าน ไม่ทิ้งเงียบ · ทิศตรงข้าม: ไม่มีธง = ไม่มีข้อความ</summary>
public class VoidResultNoticeTests
{
    [Fact]
    public void Flags_BecomeLines_WithDocumentNumber()
    {
        var lines = VoidResultNotice.Lines(new PaymentVoidResult("ต้องยกเลิกทาง e-Tax", "ภาษีขายถอยในงวด 09/2026"), "INV-0001");
        Assert.Equal(2, lines.Count);
        Assert.Equal("เอกสาร INV-0001: ต้องยกเลิกทาง e-Tax", lines[0]);
        Assert.StartsWith("เอกสาร INV-0001: ภาษีขาย", lines[1]);
        Assert.Single(VoidResultNotice.Lines(new PaymentVoidResult(null, "x"), null));
    }

    [Fact]
    public void NoFlags_NoLines()
    {
        Assert.Empty(VoidResultNotice.Lines(PaymentVoidResult.None, "INV-0001"));
        Assert.Empty(VoidResultNotice.Lines(new PaymentVoidResult("  ", ""), "INV-0001"));
        Assert.Empty(VoidResultNotice.Lines(null, null));
    }
}
