using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แผนที่ "ชนิดเอกสาร → ฝั่งซื้อ/ขาย" ที่ส่งให้หน้าเว็บใช้แทนสำเนามือ
///
/// ═══ ที่มา ═══
/// <c>DocumentSide.cs</c> เขียนไว้ในหมายเหตุตั้งแต่วันที่สร้างว่า
/// <c>document-scan.html salesTypes</c> เป็นหนึ่งใน 3 สำเนาที่ตอบไม่ตรงกัน —
/// ฝั่ง C# ถูกยุบเป็นตัวเดียวแล้ว แต่ **สำเนา JS ไม่เคยถูกแตะ** ⇒
///   • ใบลดหนี้ที่เราเป็นผู้ขาย (OurRole=Seller) เปิดฟอร์ม<b>รายจ่าย</b>
///   • ใบส่งของจากผู้ขาย เปิดฟอร์ม<b>รายได้</b>
/// แก้โดยให้เซิร์ฟเวอร์ส่งแผนที่มาให้ (BuildSideMap) หน้าเว็บมีหน้าที่อ่านอย่างเดียว
///
/// เทสต์ชุดนี้ล็อกว่าแผนที่ที่ส่งออกไป **ตรงกับตัวตัดสินจริง** ทุกชนิด —
/// ถ้าใครย้ายชนิดระหว่างเซ็ตแล้วลืมอะไรสักอย่าง เทสต์นี้ต้องแดง
/// </summary>
public class DocumentSideMapTests
{
    [Fact]
    public void แผนที่ต้องตรงกับตัวตัดสินจริงทุกชนิดที่อยู่ในแผนที่()
    {
        var map = DocumentSide.BuildSideMap();
        Assert.NotEmpty(map);

        foreach (var (name, side) in map)
        {
            var type = Enum.Parse<DocumentType>(name);
            switch (side)
            {
                case "Sales":
                    // ฝั่งขายเสมอ — บทบาทไม่มีผล
                    Assert.True(DocumentSide.IsSales(type));
                    Assert.True(DocumentSide.IsSales(type, "Buyer"));
                    Assert.False(DocumentSide.IsAmbiguous(type));
                    break;
                case "Purchase":
                    Assert.False(DocumentSide.IsSales(type));
                    Assert.False(DocumentSide.IsSales(type, "Seller"));
                    Assert.False(DocumentSide.IsAmbiguous(type));
                    break;
                case "Both":
                    Assert.True(DocumentSide.IsAmbiguous(type));
                    Assert.True(DocumentSide.IsSales(type, "Seller"));
                    Assert.False(DocumentSide.IsSales(type, "Buyer"));
                    // ไม่รู้บทบาท = ถือเป็นฝั่งซื้อ (default ที่ปลอดภัยกว่า)
                    Assert.False(DocumentSide.IsSales(type, null));
                    break;
                default:
                    Assert.Fail($"ค่าฝั่งที่ไม่รู้จัก '{side}' สำหรับ {name}");
                    break;
            }
        }
    }

    [Theory]
    // บั๊กจริงที่สำเนา JS ตอบผิด
    [InlineData(DocumentType.CreditNote, "Both")]
    [InlineData(DocumentType.DebitNote, "Both")]
    [InlineData(DocumentType.DeliveryNote, "Both")]
    // ชนิดที่สำเนา JS ไม่เคยมีเลย
    [InlineData(DocumentType.PurchaseOrder, "Purchase")]
    [InlineData(DocumentType.PurchaseRequisition, "Purchase")]
    [InlineData(DocumentType.GoodsReceiptNote, "Purchase")]
    [InlineData(DocumentType.CertificateInLieu, "Purchase")]
    // ฝั่งขายมาตรฐาน
    [InlineData(DocumentType.TaxInvoice, "Sales")]
    [InlineData(DocumentType.ReceiptVoucher, "Sales")]
    public void ชนิดที่เคยตอบผิดต้องอยู่ฝั่งที่ถูก(DocumentType type, string expected)
    {
        var map = DocumentSide.BuildSideMap();
        Assert.True(map.ContainsKey(type.ToString()),
            $"{type} ต้องอยู่ในแผนที่ — ถ้าหายไป หน้าเว็บจะตกไปใช้ fallback แล้วเดาผิด");
        Assert.Equal(expected, map[type.ToString()]);
    }

    [Fact]
    public void ทุกชนิดใน_enum_ต้องถูกจัดฝั่งไว้แล้ว()
    {
        // เพิ่ม DocumentType ใหม่แล้วลืมจัดฝั่ง = หน้าเว็บตกไปใช้ fallback แล้ว
        // เดาเป็น "ฝั่งซื้อ" เงียบ ๆ — เทสต์นี้บังคับให้ตัดสินใจตอนเพิ่ม ไม่ใช่
        // ตอนผู้ใช้เจอเอกสารเปิดผิดฟอร์ม
        var map = DocumentSide.BuildSideMap();
        var missing = Enum.GetValues<DocumentType>()
            .Where(t => !map.ContainsKey(t.ToString()))
            .Select(t => t.ToString())
            .ToList();
        Assert.True(missing.Count == 0,
            "DocumentType ที่ยังไม่ได้จัดฝั่งใน DocumentSide: " + string.Join(", ", missing));
    }
}
