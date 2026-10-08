using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีมตรวจงานค้าง D4 + ฝ่ายค้านชุดสาม: ชุดตรวจรายงาน ภ.พ.30 ที่ล้มเขียนคำเตือนบนหมายเหตุ — "สร้างรายงานใหม่" ต้องล้างคำเตือนเก่า
/// และคงคำเตือนของรอบใหม่ (เดิมทับด้วยหมายเหตุเก่าทั้งก้อน ⇒ เตือนค้างตลอดไป · เตือนใหม่หายเงียบ) โดยหมายเหตุของผู้ใช้อยู่ครบ
/// </summary>
public class TaxReportNotesMergeTests
{
    private const string W = TaxService.SystemCheckWarningPrefix;

    [Fact]
    public void สร้างใหม่ผ่าน_คำเตือนเก่าหาย_หมายเหตุผู้ใช้อยู่()
        => Assert.Equal("ยื่นพร้อมใบแนบ", TaxService.MergeRegeneratedNotes($"ยื่นพร้อมใบแนบ\n{W} \"ภาษีซื้อยกมา\" ไม่สำเร็จ", null));

    [Fact]
    public void ทิศตรงข้าม_สร้างใหม่ยังล้ม_คำเตือนรอบใหม่ต้องอยู่()
    {
        var merged = TaxService.MergeRegeneratedNotes("ยื่นพร้อมใบแนบ", $"{W} \"เอกสารมาช้า\" ไม่สำเร็จ");
        Assert.Contains("ยื่นพร้อมใบแนบ", merged);
        Assert.Contains($"{W} \"เอกสารมาช้า\"", merged);
    }

    [Fact]
    public void ไม่มีอะไรเลย_ได้null()
        => Assert.Null(TaxService.MergeRegeneratedNotes(null, null));
}
