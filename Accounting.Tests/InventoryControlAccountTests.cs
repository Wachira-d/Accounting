using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// DECISION_AUDIT_2026-09-18 D5-2 — บัญชีคุมสต็อกต้องเป็นใบเดียวกันทั้งฝั่งซื้อ
/// (Dr ตอนรับเข้า) และฝั่งเบิกใช้ (Cr ตอนตัดออก) มิฉะนั้นสองบัญชีไม่มีวันหักล้าง
/// </summary>
public class InventoryControlAccountTests
{
    private static readonly Guid Inv = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Sup = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InvDefault = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupDefault = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void วัสดุสิ้นเปลือง_ใช้บัญชีวัสดุที่ผู้ใช้เลือก()
        => Assert.Equal(Sup, InventoryControlAccount.Resolve(
            ProductType.Supplies, Inv, Sup, InvDefault, SupDefault));

    [Fact]
    public void วัสดุสิ้นเปลือง_ไม่ได้เลือกไว้_ตกไปบัญชีวัสดุdefault_118xx()
        => Assert.Equal(SupDefault, InventoryControlAccount.Resolve(
            ProductType.Supplies, Inv, null, InvDefault, SupDefault));

    [Fact]
    public void สินค้า_ใช้บัญชีสินค้าคงเหลือ_ไม่แตะบัญชีวัสดุแม้ตั้งไว้()
        => Assert.Equal(Inv, InventoryControlAccount.Resolve(
            ProductType.Product, Inv, Sup, InvDefault, SupDefault));

    [Fact]
    public void สินค้า_ไม่ได้เลือกไว้_ตกไปสินค้าคงเหลือdefault_115xx()
        => Assert.Equal(InvDefault, InventoryControlAccount.Resolve(
            ProductType.Product, null, null, InvDefault, SupDefault));

    [Theory]
    [InlineData(ProductType.RawMaterial)]
    [InlineData(ProductType.NonStock)]
    [InlineData(ProductType.Service)]
    public void ชนิดอื่นทั้งหมด_เดินเส้นสินค้าคงเหลือ(ProductType t)
        => Assert.Equal(InvDefault, InventoryControlAccount.Resolve(t, null, null, InvDefault, SupDefault));

    [Fact]
    public void วัสดุสิ้นเปลือง_ผังไม่มี118เลย_ตกไปสินค้าคงเหลือ_เพื่อให้สองฝั่งยังลงใบเดียวกัน()
        => Assert.Equal(InvDefault, InventoryControlAccount.Resolve(
            ProductType.Supplies, null, null, InvDefault, defaultSuppliesAccountId: null));

    [Fact]
    public void ไม่มีบัญชีใดใช้ได้_คืนnull_ให้ผู้เรียกตัดสินใจเอง_ห้ามแต่งเลขผัง()
        => Assert.Null(InventoryControlAccount.Resolve(
            ProductType.Product, null, null, null, null));

    /// <summary>หัวใจของ D5-2: ฝั่งซื้อกับฝั่งเบิกถามคำถามเดียวกัน ต้องได้ใบเดียวกัน</summary>
    [Fact]
    public void ฝั่งซื้อกับฝั่งเบิกของวัสดุเดียวกัน_ต้องได้บัญชีเดียวกัน()
    {
        var purchaseSide = InventoryControlAccount.Resolve(
            ProductType.Supplies, inventoryAccountId: null, suppliesAccountId: null,
            defaultInventoryAccountId: InvDefault, defaultSuppliesAccountId: SupDefault);
        var issueSide = InventoryControlAccount.Resolve(
            ProductType.Supplies, inventoryAccountId: null, suppliesAccountId: null,
            defaultInventoryAccountId: InvDefault, defaultSuppliesAccountId: SupDefault);
        Assert.Equal(purchaseSide, issueSide);
        Assert.Equal(SupDefault, purchaseSide);
    }

    [Fact]
    public void คำนำหน้ารหัสบัญชีdefault_วัสดุ118_สินค้า115()
    {
        Assert.Equal("118", InventoryControlAccount.DefaultAccountPrefix(ProductType.Supplies));
        Assert.Equal("115", InventoryControlAccount.DefaultAccountPrefix(ProductType.Product));
        Assert.Equal("115", InventoryControlAccount.DefaultAccountPrefix(ProductType.RawMaterial));
    }
}
