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
    public ChartOfAccount? SalesAccount { get; set; }
    public Guid? PurchaseAccountId { get; set; }
    public ChartOfAccount? PurchaseAccount { get; set; }
    public Guid? InventoryAccountId { get; set; }
    public ChartOfAccount? InventoryAccount { get; set; }

    // Supplies-specific account mapping (วัสดุสิ้นเปลือง)
    public Guid? SuppliesAccountId { get; set; }        // บัญชีวัสดุสิ้นเปลือง (118xx)
    public ChartOfAccount? SuppliesAccount { get; set; }
    public Guid? SuppliesExpenseAccountId { get; set; }  // บัญชีค่าวัสดุสิ้นเปลือง (5xxxxx)
    public ChartOfAccount? SuppliesExpenseAccount { get; set; }

    // Stock (สำหรับ ProductType = Product)
    public decimal CurrentStock { get; set; }
    public decimal MinimumStock { get; set; }
    public bool TrackStock { get; set; } = false;

    public bool IsActive { get; set; } = true;

    // Relationships
    public ICollection<UnitConversion> UnitConversions { get; set; } = new List<UnitConversion>();
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

/// <summary>
/// การแปลงหน่วยสินค้า เช่น 1 ลัง = 12 ชิ้น, 1 โหล = 12 ชิ้น
/// </summary>
public class UnitConversion : TenantEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string FromUnit { get; set; } = null!;    // เช่น "ลัง"
    public string ToUnit { get; set; } = null!;      // เช่น "ชิ้น"
    public decimal ConversionRate { get; set; }       // เช่น 12 (1 ลัง = 12 ชิ้น)
    public decimal? SellingPrice { get; set; }        // ราคาขายต่อหน่วยนี้
    public decimal? CostPrice { get; set; }           // ราคาทุนต่อหน่วยนี้
    public string? Barcode { get; set; }              // Barcode สำหรับหน่วยนี้
}

/// <summary>
/// หมวดหมู่สินค้า
/// </summary>
public class ProductCategory : TenantEntity
{
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public ProductCategory? ParentCategory { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// ตรวจนับสินค้า (Physical Inventory Count)
/// </summary>
public class StockCount : TenantEntity
{
    public string CountNumber { get; set; } = null!;
    public DateTime CountDate { get; set; }
    public string Status { get; set; } = "Draft";  // Draft, InProgress, Completed, Cancelled
    public string? Notes { get; set; }
    public Guid? WarehouseId { get; set; }
    public ICollection<StockCountLine> Lines { get; set; } = new List<StockCountLine>();
}

public class StockCountLine : TenantEntity
{
    public Guid StockCountId { get; set; }
    public StockCount StockCount { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
    public decimal Variance { get; set; }       // CountedQty - SystemQty
    public string? Notes { get; set; }
}

/// <summary>
/// สรุปมูลค่าสินค้าคงเหลือ ณ สิ้นงวด (Inventory Period Snapshot)
/// ใช้สำหรับปิดงบประจำเดือน/ปี
/// </summary>
public class InventorySnapshot : TenantEntity
{
    public DateTime SnapshotDate { get; set; }
    public string Status { get; set; } = "Draft";  // Draft, Finalized
    public string? Description { get; set; }
    public decimal TotalValue { get; set; }
    public int TotalProducts { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public ICollection<InventorySnapshotLine> Lines { get; set; } = new List<InventorySnapshotLine>();
}

public class InventorySnapshotLine : TenantEntity
{
    public Guid SnapshotId { get; set; }
    public InventorySnapshot Snapshot { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
}

/// <summary>
/// บันทึกการเบิกใช้วัสดุสิ้นเปลือง (Supplies Usage Log)
/// ทุกครั้งที่เบิก → Dr ค่าวัสดุสิ้นเปลือง (5xxxxx) / Cr วัสดุสิ้นเปลือง (118xx)
/// </summary>
public class SuppliesUsageLog : TenantEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public DateTime UsageDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public string? Department { get; set; }      // แผนก/ห้องที่เบิก
    public string? Purpose { get; set; }         // วัตถุประสงค์
    public string? Reference { get; set; }       // เลขที่อ้างอิง
    public string? Notes { get; set; }           // หมายเหตุเพิ่มเติม (multi-line)
    public string? IssuedToUserId { get; set; }  // ผู้รับวัสดุ (UserId)
    public string? IssuedToName { get; set; }    // ชื่อผู้รับ (สำหรับเบิกให้คนนอกระบบ เช่น ผู้รับเหมา)
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}
