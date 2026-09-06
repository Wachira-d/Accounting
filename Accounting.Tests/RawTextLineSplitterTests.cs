using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แตกรายการจากข้อความล้วนเมื่อไม่มี AI (kill-switch ตามกฎเหล็ก #1) — ผลลัพธ์ต้อง
/// เดินผ่าน <see cref="OcrLineSplitGuard"/> ตัวเดียวกับคำตอบของโมเดลเสมอ
/// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T3-02)
/// </summary>
public class RawTextLineSplitterTests
{
    private const string Receipt = """
    ร้านวัสดุก่อสร้าง รุ่งเรือง
    เลขที่ IV-6809-0021    วันที่ 05/09/2026
    ปูนซีเมนต์ตราช้าง 10 ถุง x 150.00 1,500.00
    ทรายหยาบ 2 คิว 400.00 800.00
    ค่าขนส่ง 700.00
    รวมเงิน 3,000.00
    ภาษีมูลค่าเพิ่ม 7% 210.00
    รวมทั้งสิ้น 3,210.00
    เงินสด 3,500.00
    เงินทอน 290.00
    """;

    [Fact]
    public void แตกบรรทัดสินค้าได้_และไม่เอาบรรทัดสรุปยอด()
    {
        var lines = RawTextLineSplitter.Split(Receipt);
        Assert.Equal(3, lines.Count);
        Assert.Equal(3000m, lines.Sum(x => x.Amount));
        Assert.DoesNotContain(lines, l => l.Description.Contains("รวม"));
        Assert.DoesNotContain(lines, l => l.Description.Contains("เงินทอน"));
        Assert.DoesNotContain(lines, l => l.Description.Contains("ภาษี"));
    }

    [Fact]
    public void จำนวนกับราคาต่อหน่วยถูกแยกเมื่อคูณแล้วตรงยอดบรรทัด()
    {
        var lines = RawTextLineSplitter.Split(Receipt);
        var cement = lines[0];
        Assert.Equal(10m, cement.Quantity);
        Assert.Equal(150m, cement.UnitPrice);
        Assert.Equal(1500m, cement.Amount);
        Assert.Equal("ถุง", cement.Unit);
        Assert.Contains("ปูนซีเมนต์", cement.Description);
    }

    [Fact]
    public void บรรทัดที่ไม่มีจำนวน_ได้qty1และราคาเท่ายอด()
    {
        var freight = RawTextLineSplitter.Split(Receipt)[2];
        Assert.Equal(1m, freight.Quantity);
        Assert.Equal(700m, freight.UnitPrice);
        Assert.Equal(700m, freight.Amount);
    }

    [Fact]
    public void ผลลัพธ์ผ่านด่านเดิมได้เมื่อผลรวมตรงยอดก่อนภาษี()
    {
        var json = RawTextLineSplitter.SplitToJson(Receipt);
        Assert.NotNull(json);
        var guard = OcrLineSplitGuard.Evaluate(json, subTotal: 3000m, totalAmount: 3210m);
        Assert.True(guard.Accepted, guard.Reason);
        Assert.Equal(3, guard.Lines.Count);
        Assert.Equal(3000m, guard.Sum);
    }

    [Fact]
    public void ผลรวมไม่ตรงยอดบนกระดาษ_ด่านต้องปฏิเสธ_ไม่ใช่บันทึกมั่ว()
    {
        var json = RawTextLineSplitter.SplitToJson(Receipt);
        var guard = OcrLineSplitGuard.Evaluate(json, subTotal: 9999m, totalAmount: 10698.93m);
        Assert.False(guard.Accepted);
    }

    [Fact]
    public void แถวที่เป็นตัวเลขล้วน_ไม่ถูกนับเป็นรายการ()
    {
        // บาร์โค้ด/เลขที่/เลขผู้เสียภาษี ไม่มีตัวอักษรพอ
        var lines = RawTextLineSplitter.Split("8859991446166 1,200.00\n0105561012345 500.00");
        Assert.Empty(lines);
    }

    [Fact]
    public void ข้อความว่างหรือไม่มีจำนวนเงิน_คืนค่าว่าง()
    {
        Assert.Empty(RawTextLineSplitter.Split(null));
        Assert.Empty(RawTextLineSplitter.Split("ใบกำกับภาษี\nบริษัท ทดสอบ จำกัด"));
        Assert.Null(RawTextLineSplitter.SplitToJson("ไม่มีตัวเลขอะไรเลย"));
    }

    [Fact]
    public void ข้อความยาวผิดปกติ_ไม่ตอบดีกว่าจับมั่ว()
    {
        var many = string.Join("\n", Enumerable.Range(1, RawTextLineSplitter.MaxLines + 5)
            .Select(i => $"สินค้า {i} รายการ 10.00"));
        Assert.Empty(RawTextLineSplitter.Split(many));
    }
}
