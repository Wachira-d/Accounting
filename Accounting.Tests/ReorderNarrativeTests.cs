using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// D-5 — รายงานจุดสั่งซื้อต้องเล่าเรื่องได้<b>โดยไม่มี AI</b> (kill-switch)
/// และทุกตัวเลขในประโยคต้องมาจากแถวที่รับเข้ามา ห้ามแต่งเพิ่ม
/// </summary>
public class ReorderNarrativeTests
{
    private static ReorderRow R(string sku, decimal stock, decimal daily,
        decimal days, decimal order, string urgency)
        => new(sku, "สินค้า " + sku, stock, daily, days, order, urgency);

    [Fact]
    public void ไม่มีแถว_ต้องบอกว่าไม่มีรายการเร่งด่วน_ไม่ใช่สตริงว่าง()
    {
        var s = ReorderNarrative.Build(System.Array.Empty<ReorderRow>());
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.Contains("ไม่มีรายการ", s);
    }

    [Fact]
    public void ประโยคต้องมีรหัสสินค้าและจำนวนที่แนะนำ()
    {
        var s = ReorderNarrative.Build(new[]
        {
            R("A-001", 5m, 2m, 2.5m, 50m, "Critical"),
            R("B-002", 40m, 1m, 40m, 0m, "OK"),
        });
        Assert.Contains("A-001", s);
        Assert.Contains("50", s);
        Assert.Contains("Critical", s);   // จำนวนรายการเร่งด่วนอยู่ในประโยคแรก
    }

    [Fact]
    public void เรียงตามใกล้หมดที่สุดก่อน()
    {
        var s = ReorderNarrative.Build(new[]
        {
            R("SLOW", 100m, 1m, 100m, 20m, "Warning"),
            R("FAST", 2m, 2m, 1m, 40m, "Critical"),
        });
        Assert.True(s.IndexOf("FAST", System.StringComparison.Ordinal)
                    < s.IndexOf("SLOW", System.StringComparison.Ordinal),
            "รายการที่ใกล้หมดที่สุดต้องถูกเอ่ยก่อน");
    }

    [Fact]
    public void ไม่มียอดขาย_ต้องบอกว่าประเมินไม่ได้_ห้ามแต่งจำนวนวัน()
    {
        var s = ReorderNarrative.Build(new[] { R("IDLE", 10m, 0m, -1m, 5m, "Idle") });
        Assert.Contains("ประเมินวันหมดไม่ได้", s);
        Assert.DoesNotContain("จะหมดใน -1", s);
    }

    [Fact]
    public void รายการเยอะ_เอ่ยชื่อไม่เกินเพดาน_แล้วสรุปที่เหลือ()
    {
        var rows = new List<ReorderRow>();
        for (var i = 0; i < ReorderNarrative.MaxNamedSkus + 3; i++)
            rows.Add(R($"SKU{i:00}", 1m, 1m, i + 1, 10m, "Critical"));
        var s = ReorderNarrative.Build(rows);
        Assert.Contains("อีก 3 รายการ", s);
        Assert.DoesNotContain($"SKU0{ReorderNarrative.MaxNamedSkus}", s);
    }

    [Fact]
    public void ทุกอย่างพอ_ต้องบอกว่ายังไม่ต้องสั่ง_ไม่ใช่เงียบ()
    {
        var s = ReorderNarrative.Build(new[] { R("OK-1", 500m, 1m, 500m, 0m, "OK") });
        Assert.Contains("ยังไม่มีรายการใดที่คำนวณแล้วต้องสั่งเพิ่ม", s);
    }

    /// <summary>ครึ่ง "ห้ามพัง": ตัวเลขที่พิมพ์ออกมาต้องเป็นตัวเลขที่ส่งเข้าไปเป๊ะ
    /// (ญาติของ anti-hallucination — ที่นี่พิสูจน์ว่าไม่มีการคำนวณใหม่/ปัดเศษเอง)</summary>
    [Fact]
    public void ตัวเลขในประโยคต้องเท่ากับตัวเลขที่รับเข้ามา()
    {
        var s = ReorderNarrative.Build(new[] { R("X", 12.5m, 3m, 4.17m, 37m, "Warning") });
        Assert.Contains("12.5", s);
        Assert.Contains("4.17", s);
        Assert.Contains("37", s);
    }
}
