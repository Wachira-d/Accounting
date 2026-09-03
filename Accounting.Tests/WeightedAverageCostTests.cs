using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สูตรต้นทุนถัวเฉลี่ยถ่วงน้ำหนัก (TFRS for NPAEs บทที่ 8)
///
/// ═══ ที่มา ═══
/// สูตรนี้เคยถูกเขียนซ้ำ 3 ที่ (<c>InventoryCostingService.RegisterReceiptAsync</c> ·
/// <c>DocumentService.ApplyStockMovementsAsync</c> inline · และเกือบจะเป็นที่ที่ 4
/// ใน <c>StockLedger</c>) — defect class "สูตร/ตารางที่คัดลอกไปเขียนใหม่ = drift
/// แน่นอน แค่รอเวลา" ที่ CLAUDE.md ห้ามไว้. รอบนี้ยุบเหลือฟังก์ชันบริสุทธิ์ตัวเดียว
/// เทสต์ล็อกพฤติกรรมที่ทั้งสามจุดต้องเห็นตรงกัน
/// </summary>
public class WeightedAverageCostTests
{
    [Fact]
    public void ถัวเฉลี่ยตามสูตร_เมื่อมีของเดิมอยู่()
    {
        // 10 ชิ้น @ 100 + รับเข้า 10 ชิ้น @ 120 ⇒ 110
        Assert.Equal(110m, WeightedAverageCost.Next(10m, 100m, 0m, 10m, 120m));
    }

    [Fact]
    public void ไม่มีของเดิม_ต้นทุนที่รับเข้าเป็นค่าเฉลี่ยตั้งต้น()
    {
        Assert.Equal(120m, WeightedAverageCost.Next(0m, 0m, 0m, 5m, 120m));
    }

    [Fact]
    public void ค่าเฉลี่ยเดิมเป็นศูนย์_ตกไปใช้ราคาทุนมาตรฐาน()
    {
        // สินค้าใหม่ที่ยังไม่เคยรับเข้า: AverageUnitCost = 0 แต่ CostPrice ตั้งไว้ 50
        // 10 @ 50 + 10 @ 70 ⇒ 60 (ไม่ใช่ 35 ซึ่งจะได้ถ้าถ่วงด้วย 0)
        Assert.Equal(60m, WeightedAverageCost.Next(10m, 0m, 50m, 10m, 70m));
    }

    [Fact]
    public void สต็อกติดลบถูกปัดเป็นศูนย์ก่อนถ่วงน้ำหนัก()
    {
        // บริษัทที่เปิด AllowNegativeStock อาจมี CurrentStock = -5
        // ถ้าเอา -5 มาถ่วง จะได้ค่าเฉลี่ยที่ไม่มีความหมาย (หรือหารด้วยศูนย์)
        Assert.Equal(120m, WeightedAverageCost.Next(-5m, 100m, 0m, 10m, 120m));
    }

    [Fact]
    public void ยอดรวมหลังรับเป็นศูนย์_คืนต้นทุนที่รับเข้า()
    {
        Assert.Equal(120m, WeightedAverageCost.Next(0m, 100m, 0m, 0m, 120m));
    }

    [Fact]
    public void ปัดสี่ตำแหน่งแบบ_AwayFromZero_ไม่ใช่ธนาคาร()
    {
        // (1×0.00005 + 1×0.00015)/2 = 0.0001 — เลือกเคสที่จุดกึ่งกลางจริง:
        // 3 ชิ้น @ 1 + 1 ชิ้น @ 1.0002 ⇒ 4.0002/4 = 1.00005 ⇒ AwayFromZero = 1.0001
        // (banker's rounding จะได้ 1.0000 ซึ่งทำให้ต้นทุนเพี้ยนสะสม)
        Assert.Equal(1.0001m, WeightedAverageCost.Next(3m, 1m, 0m, 1m, 1.0002m));
    }
}
