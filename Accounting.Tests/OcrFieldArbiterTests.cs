using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เลิก "ใครมาหลังชนะ" — ผลลัพธ์ต้องขึ้นกับ**คุณภาพของหลักฐาน** ไม่ใช่ลำดับบรรทัด
/// ในเมธอด (สถาปัตยกรรมเป้าหมาย D1) · บั๊กจริงที่คลาสนี้เคยทำให้เกิดมีอย่างน้อยสามตัว
/// ในคลัง defect class: ทะเบียน DBD ทับชื่อที่อ่านถูกเพราะคีย์ผิด · known-good ทับ
/// เลขที่เอกสารของใบใหม่ด้วยของใบเก่า · สามค่าที่ "ลงตัว" ทับยอดที่ engine อ่านถูก
/// </summary>
public class OcrFieldArbiterTests
{
    private const string F = OcrFieldKeys.SellerTaxId;

    [Fact]
    public void แหล่งที่น่าเชื่อกว่าชนะ_แม้อีกฝั่งจะมั่นใจกว่า()
    {
        // นี่คือหัวใจ: ค่าที่ "แต่งขึ้นแล้วตั้ง confidence 0.95" ต้องไม่ชนะ
        // ค่าที่อ่านมาได้จริงจากกระดาษ
        var d = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "0105556123456", OcrFieldSource.VendorHistory, 0.99m),
            new OcrFieldCandidate(F, "0994000123456", OcrFieldSource.PaperLabel, 0.70m),
        });
        Assert.NotNull(d);
        Assert.Equal("0994000123456", d!.Value);
        Assert.Equal(OcrFieldSource.PaperLabel, d.Source);
        Assert.Single(d.Alternatives);
    }

    [Fact]
    public void สลับลำดับที่เสนอ_ผลต้องเหมือนเดิม()
    {
        var a = new OcrFieldCandidate(F, "A", OcrFieldSource.Ai, 0.95m);
        var b = new OcrFieldCandidate(F, "B", OcrFieldSource.Engine, 0.40m);
        var first = OcrFieldArbiter.Decide(F, new[] { a, b })!;
        var second = OcrFieldArbiter.Decide(F, new[] { b, a })!;
        Assert.Equal(first.Value, second.Value);
        Assert.Equal("B", first.Value);   // engine ชนะ AI ตามตาราง Precedence
    }

    [Fact]
    public void แหล่งเดียวกัน_มั่นใจกว่าชนะ()
    {
        var d = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "ต่ำ", OcrFieldSource.Engine, 0.40m),
            new OcrFieldCandidate(F, "สูง", OcrFieldSource.Engine, 0.90m),
        })!;
        Assert.Equal("สูง", d.Value);
    }

    [Fact]
    public void เท่ากันทุกอย่าง_ตัวที่เสนอมาก่อนชนะ_ผลต้องเสถียร()
    {
        var d = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "แรก", OcrFieldSource.Engine, 0.80m),
            new OcrFieldCandidate(F, "หลัง", OcrFieldSource.Engine, 0.80m),
        })!;
        Assert.Equal("แรก", d.Value);
    }

    [Fact]
    public void ไม่มีผู้เสนอที่มีค่า_ต้องไม่ตอบ_ไม่ใช่ตอบค่าว่าง()
    {
        Assert.Null(OcrFieldArbiter.Decide(F, System.Array.Empty<OcrFieldCandidate>()));
        Assert.Null(OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, null, OcrFieldSource.Engine, 0.9m),
            new OcrFieldCandidate(F, "   ", OcrFieldSource.PaperLabel, 0.9m),
        }));
    }

    [Fact]
    public void ลำดับความน่าเชื่อต้องมาจากตาราง_ไม่ใช่ค่าตัวเลขของ_enum()
    {
        // ตาราง Precedence คือแหล่งความจริงเดียว — ถ้าเรียงด้วย (int)source ตรง ๆ
        // ตารางจะกลายเป็น "ของที่ประกาศไว้แต่ไม่มีใครใช้" ทันทีที่เพิ่มแหล่งใหม่
        for (var i = 1; i < OcrFieldArbiter.Precedence.Count; i++)
            Assert.True(OcrFieldArbiter.Rank(OcrFieldArbiter.Precedence[i - 1])
                      > OcrFieldArbiter.Rank(OcrFieldArbiter.Precedence[i]),
                $"{OcrFieldArbiter.Precedence[i - 1]} ต้องน่าเชื่อกว่า {OcrFieldArbiter.Precedence[i]}");
        Assert.Equal(OcrFieldSource.EtaxXml, OcrFieldArbiter.Precedence[0]);
    }

    [Fact]
    public void ตัดสินหลายช่องพร้อมกัน_แต่ละช่องแยกกันเด็ดขาด()
    {
        var all = OcrFieldArbiter.DecideAll(new[]
        {
            new OcrFieldCandidate(OcrFieldKeys.TotalAmount, "1070.00", OcrFieldSource.Engine, 0.9m),
            new OcrFieldCandidate(OcrFieldKeys.TotalAmount, "1000.00", OcrFieldSource.Guess, 0.99m),
            new OcrFieldCandidate(OcrFieldKeys.DocumentNumber, "INV-1", OcrFieldSource.PaperLabel, 0.85m),
        });
        Assert.Equal(2, all.Count);
        Assert.Equal("1070.00", all.First(d => d.Field == OcrFieldKeys.TotalAmount).Value);
        Assert.Equal("INV-1", all.First(d => d.Field == OcrFieldKeys.DocumentNumber).Value);
    }

    /// <summary>รูป JSON ที่หน้าเว็บอ่าน **ย้ายไป <c>OcrFieldProvenance.ToJson</c>** แล้ว
    /// (รอบ 184 · D-4 ขั้นที่ 1) เพราะสมุดต้องรายงาน "ค่าที่อยู่ในฟอร์มจริง" ไม่ใช่
    /// ผู้ชนะของ arbiter เพียว ๆ — <c>OcrFieldArbiter.ToJson</c> จึงถูกลบทิ้ง
    /// (ของที่ไม่มีใครเรียก = ไม่มี · F2 ข้อ 2) · เทสต์รูป JSON อยู่ที่
    /// <c>OcrFieldProvenanceTests</c> · ที่นี่เหลือล็อกว่า <c>DecideAll</c> ยังคืนผู้ชนะถูก</summary>
    [Fact]
    public void DecideAll_ยังคืนผู้ชนะของแต่ละช่องพร้อมตัวเลือกที่แพ้()
    {
        var all = OcrFieldArbiter.DecideAll(new[]
        {
            new OcrFieldCandidate(OcrFieldKeys.SellerName, "บจก. ก", OcrFieldSource.PaperLabel, 0.9m, "หัวกระดาษ"),
            new OcrFieldCandidate(OcrFieldKeys.SellerName, "บจก. ข", OcrFieldSource.VendorHistory, 0.8m),
        });
        var d = Assert.Single(all);
        Assert.Equal("บจก. ก", d.Value);
        Assert.Equal(OcrFieldSource.PaperLabel, d.Source);
        Assert.Equal("หัวกระดาษ", d.Evidence);
        Assert.Equal(OcrFieldSource.VendorHistory, Assert.Single(d.Alternatives).Source);
    }
}
