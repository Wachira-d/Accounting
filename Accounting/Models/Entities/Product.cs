using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// สินค้า/บริการ (Product/Service Catalog)
/// เทียบเท่า FlowAccount & PEAK: Product management
/// </summary>
public class Product : TenantEntity
{
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public ProductType ProductType { get; set; }
    public string? SKU { get; set; }
    public string? Barcode { get; set; }
    public string? Category { get; set; }
    public string Unit { get; set; } = "ชิ้น";

    // Pricing
    public decimal SellingPrice { get; set; }
    public decimal CostPrice { get; set; }

    // Tax
    public decimal VatRate { get; set; } = 7;
    public bool IsVatIncluded { get; set; } = false;

    // Account mapping
    public Guid? SalesAccountId { get; set; }
    public Guid? PurchaseAccountId { get; set; }
    public Guid? InventoryAccountId { get; set; }

    // Stock (สำหรับ ProductType = Product)
    public decimal CurrentStock { get; set; }
    public decimal MinimumStock { get; set; }
    public bool TrackStock { get; set; } = false;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Stock Movement (ประวัติเคลื่อนไหวสินค้า)
/// </summary>
public class StockMovement : TenantEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public DateTime MovementDate { get; set; }
    public string MovementType { get; set; } = null!;  // IN, OUT, ADJUST
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Reference { get; set; }
    public Guid? DocumentId { get; set; }
    public string? Notes { get; set; }
}
