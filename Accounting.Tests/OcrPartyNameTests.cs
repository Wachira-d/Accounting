using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ชื่อจริงจากสแกน 2026-09-10: Azure คืน “แอม แฮปปี้” จากบรรทัด “หจก. แอม แฮปปี้เนส”
/// — ขยายได้เฉพาะเมื่อบรรทัดกระดาษ**ครอบ**ค่าเดิม ห้ามสลับไปเป็นชื่อคนละบริษัท
/// </summary>
public class OcrPartyNameTests
{
    private static readonly string[] Paper = { "บริษัท ลักกี้เวย์ จำกัด", "หจก. แอม แฮปปี้เนส" };

    [Fact]
    public void ชื่อผู้ซื้อที่ถูกตัด_ขยายเป็นบรรทัดเต็ม()
    {
        var full = OcrPartyName.ExpandTruncated("แอม แฮปปี้", Paper, exclude: "บริษัท ลักกี้เวย์ จำกัด");
        Assert.Equal("หจก. แอม แฮปปี้เนส", full);
    }

    [Fact]
    public void ชื่อที่ครบอยู่แล้ว_ไม่ขยาย()
    {
        Assert.Null(OcrPartyName.ExpandTruncated("บริษัท ลักกี้เวย์ จำกัด", Paper));
        Assert.Null(OcrPartyName.ExpandTruncated("หจก. แอม แฮปปี้เนส", Paper));
    }

    [Fact]
    public void ชื่อคนละบริษัท_ห้ามสลับ()
    {
        Assert.Null(OcrPartyName.ExpandTruncated("บริษัท เอ จำกัด", new[] { "บริษัท บี จำกัด" }));
    }

    [Fact]
    public void ห้ามขยายไปชนชื่ออีกฝั่ง()
    {
        // ค่าเดิม “ลักกี้” เป็นส่วนของชื่อผู้ขาย — ถ้าฝั่งผู้ซื้อถือค่านี้ ห้ามขยายไปเป็นชื่อผู้ขาย
        Assert.Null(OcrPartyName.ExpandTruncated("ลักกี้เวย์", Paper, exclude: "บริษัท ลักกี้เวย์ จำกัด"));
    }

    [Fact]
    public void ค่าเดิมสั้นเกินจะเป็นหลักฐาน_ไม่ขยาย()
    {
        Assert.Null(OcrPartyName.ExpandTruncated("แอ", Paper));
        Assert.Null(OcrPartyName.ExpandTruncated(null, Paper));
    }

    [Fact]
    public void Normalize_ตัดช่องว่างจุดวงเล็บ()
    {
        Assert.Equal("หจกแอมแฮปปี้เนส", OcrPartyName.Normalize("หจก. แอม แฮปปี้เนส"));
        Assert.Equal("บริษัทเอจำกัดมหาชน", OcrPartyName.Normalize("บริษัท เอ จำกัด (มหาชน)"));
    }
}
