using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// D-4 ขั้นที่ 1 — สมุด "ค่านี้มาจากไหน" ต้องรายงาน<b>ค่าที่อยู่ในฟอร์มจริง</b>
/// และ <c>arbiterAgreed</c> ต้องเป็นตัวเลขที่<b>โกหกไม่ได้</b>
///
/// <para>เทสต์ทิศตรงข้าม (G7): ถ้าใครปิดด่านด้วยการให้ที่มาตกเป็นผู้ชนะของ arbiter
/// เสมอ เทสต์ <c>ไม่มีใครเสนอค่าที่ใช้จริง_ต้องได้_Unknown</c> จะล้มทันที</para>
/// </summary>
public class OcrFieldProvenanceTests
{
    private const string F = OcrFieldKeys.SellerName;

    private static OcrFieldCandidate C(string v, OcrFieldSource s, decimal conf = 0.8m)
        => new(F, v, s, conf);

    [Fact]
    public void ค่าที่ใช้จริงตรงกับผู้ชนะ_นับเป็นเห็นด้วย()
    {
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.PaperLabel), C("บจก. ข", OcrFieldSource.Guess) },
            new Dictionary<string, string?> { [F] = "บจก. ก" });
        Assert.Equal(1, rep.Compared);
        Assert.Equal(1, rep.Agreed);
        Assert.Equal(1.0m, rep.ArbiterAgreed);
        Assert.Equal(OcrFieldSource.PaperLabel, rep.Fields[0].Source);
        Assert.True(rep.Fields[0].Agrees);
    }

    [Fact]
    public void ค่าที่ใช้จริงเป็นของผู้แพ้_ต้องรายงานที่มาของผู้แพ้_ไม่ใช่ผู้ชนะ()
    {
        // นี่คืออาการ V1 ตรง ๆ: ไปป์ไลน์เลือกค่าของ Guess (เพราะเขียนทีหลัง)
        // แต่สมุดเดิมพิมพ์ว่า "มาจากป้ายกำกับบนกระดาษ"
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.PaperLabel), C("บจก. ข", OcrFieldSource.Guess) },
            new Dictionary<string, string?> { [F] = "บจก. ข" });
        Assert.Equal(OcrFieldSource.Guess, rep.Fields[0].Source);
        Assert.False(rep.Fields[0].Agrees);
        Assert.Equal(0m, rep.ArbiterAgreed);
        Assert.Equal("บจก. ก", rep.Fields[0].DecidedValue);   // บอกด้วยว่าตัวตัดสินเลือกอะไร
    }

    [Fact]
    public void ไม่มีใครเสนอค่าที่ใช้จริง_ต้องได้_Unknown_ห้ามเดา()
    {
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.PaperLabel) },
            new Dictionary<string, string?> { [F] = "บจก. ค (ชั้นที่ไม่ Note)" });
        Assert.Equal(OcrFieldSource.Unknown, rep.Fields[0].Source);
        Assert.Equal(0m, rep.Fields[0].Confidence);
        Assert.False(rep.Fields[0].Agrees);
    }

    [Fact]
    public void ช่องว่าง_เทียบไม่ได้_ต้องไม่เข้าตัวหาร()
    {
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.PaperLabel) },
            new Dictionary<string, string?> { [F] = null });
        Assert.Equal(0, rep.Compared);
        Assert.Null(rep.ArbiterAgreed);          // ไม่ใช่ 0 — "ยังไม่ได้ตรวจ" ≠ "ไม่เห็นด้วย"
        Assert.Null(rep.Fields[0].Agrees);
    }

    [Fact]
    public void ช่องที่ผู้เรียกไม่ได้ส่งค่าจริงมา_ก็ไม่เข้าตัวหาร()
    {
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.PaperLabel) },
            new Dictionary<string, string?>());
        Assert.Equal(0, rep.Compared);
        Assert.Null(rep.ArbiterAgreed);
    }

    [Theory]
    [InlineData("3", "3.00")]
    [InlineData("3.00", "3")]
    [InlineData(" 3.0 ", "3.000")]
    [InlineData("1,000.00", "1,000.00")]
    public void ตัวเลขรูปต่างกันคือค่าเดียวกัน(string a, string b)
        => Assert.True(OcrFieldProvenance.SameValue(a, b));

    [Theory]
    [InlineData("3", "5")]
    [InlineData("บจก. ก", "บจก. ข")]
    [InlineData("", "3")]
    public void ค่าต่างกันต้องไม่ถือว่าเท่ากัน(string a, string b)
        => Assert.False(OcrFieldProvenance.SameValue(a, b));

    [Fact]
    public void JSON_ต้องพกป้ายภาษาไทยมาให้หน้าเว็บ_ไม่ต้องมีตารางสำเนาที่สอง()
    {
        var json = OcrFieldProvenance.ToJson(OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.Statute) },
            new Dictionary<string, string?> { [F] = "บจก. ก" }));
        Assert.Contains("sourceLabel", json);
        Assert.Contains("arbiterAgreed", json);
        Assert.Contains(OcrFieldArbiter.SourceLabel(OcrFieldSource.Statute), json);
    }

    [Fact]
    public void สองแหล่งเสนอค่าเดียวกัน_ต้องโชว์ชั้นที่น่าเชื่อกว่า()
    {
        var rep = OcrFieldProvenance.Build(
            new[] { C("บจก. ก", OcrFieldSource.Guess), C("บจก. ก", OcrFieldSource.PaperLabel) },
            new Dictionary<string, string?> { [F] = "บจก. ก" });
        Assert.Equal(OcrFieldSource.PaperLabel, rep.Fields[0].Source);
    }

    [Fact]
    public void หลายช่อง_ตัวชี้วัดต้องนับรวมถูก()
    {
        var cands = new[]
        {
            new OcrFieldCandidate(OcrFieldKeys.SellerName, "ก", OcrFieldSource.PaperLabel, 0.9m),
            new OcrFieldCandidate(OcrFieldKeys.TotalAmount, "100.00", OcrFieldSource.Engine, 0.9m),
            new OcrFieldCandidate(OcrFieldKeys.TotalAmount, "200.00", OcrFieldSource.Guess, 0.9m),
        };
        var rep = OcrFieldProvenance.Build(cands, new Dictionary<string, string?>
        {
            [OcrFieldKeys.SellerName] = "ก",
            [OcrFieldKeys.TotalAmount] = "200.00",   // ไปป์ไลน์เลือกของ Guess
        });
        Assert.Equal(2, rep.Compared);
        Assert.Equal(1, rep.Agreed);
        Assert.Equal(0.5m, rep.ArbiterAgreed);
    }
}
