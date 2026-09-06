using System.Text.Json;
using Accounting.Services.Ai.Distillation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// นักเรียนของ <c>OcrLineItemSplit</c> — ชั้นกติกาต้องตอบได้ตั้งแต่ใบแรก (cold-start)
/// และลายนิ้วมือ "โครงข้อความ" ต้องจับบิลประจำที่ต่างกันแค่ตัวเลขได้
/// (กฎเหล็ก #1 ข้อ 2 feature parity + ข้อ 3 cold-start)
/// </summary>
public class LineSplitStudentTests
{
    private static readonly string BillMay = string.Join('\n', new[]
    {
        "บริษัท การไฟฟ้านครหลวง จำกัด",
        "ใบแจ้งหนี้ค่าไฟฟ้า เลขที่ EL-2026-05-0001",
        "ค่าพลังงานไฟฟ้า 1,240.00",
        "ค่าบริการรายเดือน 38.22",
        "รวมเงิน 1,278.22",
        "ภาษีมูลค่าเพิ่ม 7% 89.48",
        "รวมทั้งสิ้น 1,367.70",
    });

    private static readonly string BillJune = string.Join('\n', new[]
    {
        "บริษัท การไฟฟ้านครหลวง จำกัด",
        "ใบแจ้งหนี้ค่าไฟฟ้า เลขที่ EL-2026-06-0001",
        "ค่าพลังงานไฟฟ้า 1,510.50",
        "ค่าบริการรายเดือน 38.22",
        "รวมเงิน 1,548.72",
        "ภาษีมูลค่าเพิ่ม 7% 108.41",
        "รวมทั้งสิ้น 1,657.13",
    });

    [Fact]
    public void บิลประจำที่ต่างกันแค่ตัวเลข_ต้องได้ลายนิ้วมือเดียวกัน()
    {
        var a = LineSplitDistillationModel.ShapeFingerprint(BillMay);
        var b = LineSplitDistillationModel.ShapeFingerprint(BillJune);
        Assert.NotEqual("", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void บิลของผู้ขายคนละราย_ต้องได้ลายนิ้วมือคนละอัน()
    {
        var other = string.Join('\n', new[]
        {
            "ห้างหุ้นส่วนจำกัด สหกล วัสดุก่อสร้าง",
            "ใบกำกับภาษี/ใบส่งของ เลขที่ PI-20260601-0007",
            "ปูนซีเมนต์ถุง 50 กก. 10 ถุง x 145.00 1,450.00",
            "ทรายหยาบ 2 คิว x 520.00 1,040.00",
            "รวมเงิน 2,490.00",
        });
        Assert.NotEqual(
            LineSplitDistillationModel.ShapeFingerprint(BillMay),
            LineSplitDistillationModel.ShapeFingerprint(other));
    }

    [Fact]
    public void ข้อความสั้นเกินไป_ต้องไม่ถูกใช้เป็นคีย์()
    {
        // คีย์ที่กว้างเกินไปจะจับใบคนละใบมาชนกัน — ยอมไม่ตอบดีกว่าตอบผิดใบ
        Assert.Equal("", LineSplitDistillationModel.ShapeFingerprint("รวม 100"));
        Assert.Equal("", LineSplitDistillationModel.ShapeFingerprint(null));
        Assert.Equal("", LineSplitDistillationModel.ShapeFingerprint("   "));
    }

    [Fact]
    public async Task ชั้นกติกาตอบได้ตั้งแต่ใบแรก_และผลรวมต้องเทียบยอดกระดาษได้()
    {
        var model = new LineSplitDistillationModel(
            new NoServices(), NullLogger<LineSplitDistillationModel>.Instance);
        Assert.True(model.IsReady);   // cold-start: ไม่ต้องรอครูสอน

        var input = JsonSerializer.Serialize(new
        {
            task = "ocr_line_item_split",
            ocr_raw_text = BillMay,
            known_totals = new { sub_total = 1278.22m, vat_amount = 89.48m, total_amount = 1367.70m },
        });

        var pred = await model.PredictAsync(Guid.NewGuid(), input, CancellationToken.None);
        Assert.NotNull(pred);
        Assert.NotNull(pred!.StructuredJson);

        // ต้องเป็นรูปเดียวกับที่ครูตอบ เพื่อเดินผ่านด่าน OcrLineSplitGuard ตัวเดิม
        using var doc = JsonDocument.Parse(pred.StructuredJson!);
        var lines = doc.RootElement.GetProperty("lines");
        Assert.True(lines.GetArrayLength() >= 2);

        // บรรทัดสรุป (รวมเงิน/ภาษี/รวมทั้งสิ้น) ต้องไม่กลายเป็นรายการสินค้า
        foreach (var l in lines.EnumerateArray())
        {
            var d = l.GetProperty("description").GetString() ?? "";
            Assert.DoesNotContain("รวมทั้งสิ้น", d);
            Assert.DoesNotContain("ภาษีมูลค่าเพิ่ม", d);
        }
    }

    [Fact]
    public async Task ไม่มีข้อความ_ต้องไม่ตอบ_ปล่อยให้ครูตอบ()
    {
        var model = new LineSplitDistillationModel(
            new NoServices(), NullLogger<LineSplitDistillationModel>.Instance);
        var input = JsonSerializer.Serialize(new { task = "ocr_line_item_split", ocr_raw_text = "" });
        Assert.Null(await model.PredictAsync(Guid.NewGuid(), input, CancellationToken.None));
    }

    /// <summary>ชั้นกติกาไม่แตะฐานข้อมูล — เทสต์นี้จึงไม่ต้องมี DbContext</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
