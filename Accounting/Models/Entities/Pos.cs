using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ==================== POS Terminal ====================
/// <summary>จุดขาย / เครื่อง POS</summary>
public class PosTerminal : TenantEntity
{
    public string Name { get; set; } = null!;                    // "POS-1", "แคชเชียร์ 1"
    public PosBusinessMode BusinessMode { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>⚠️ ข้อความอิสระ (ของเดิม) — **ห้ามใช้ตัดสินอะไร** ใช้ได้แค่แสดง
    /// ตัวที่บอกสาขาจริงคือ <see cref="BranchId"/></summary>
    public string? Location { get; set; }
    public string? SettingsJson { get; set; }                    // JSON config (receipt format, tax, etc.)

    // ── สาขา + คลัง (POS_MULTI_BRANCH_ANALYSIS.md เฟส 1) ──
    // เดิมมีแค่ `Location` เป็นข้อความ ⇒ ระบบไม่รู้ว่าเครื่องไหนอยู่สาขาไหน จึงทำ
    // รายงานรายสาขา/รหัสสาขาบนใบกำกับ/ตัดสต็อกของสาขาไม่ได้เลย
    /// <summary>สาขาที่เครื่องนี้ตั้งอยู่ — ใช้กับรหัสสาขาบนใบกำกับ (§86/4) ·
    /// มิติบัญชีรายสาขา · รายงานแยกสาขา · null = ยังไม่ระบุ (ขึ้นป้ายเตือนในหน้า POS)</summary>
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }

    /// <summary>คลังที่เครื่องนี้ตัดสต็อก — null = คลังหลักของบริษัท
    /// (ปกติ = คลังที่ผูกกับสาขาเดียวกัน)</summary>
    public Guid? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }

    /// <summary>บัญชีเงินสด/ธนาคารของเครื่องนี้ — null = ใช้ค่าที่ resolve จาก
    /// ผังบัญชีตามวิธีจ่าย (พฤติกรรมเดิม) · สาขาที่มีบัญชีธนาคารคนละบัญชีตั้งที่นี่
    /// ไม่งั้นเงินโอนของทุกสาขาลงบัญชีเดียวกันหมดแล้วกระทบยอดธนาคารไม่ได้</summary>
    public Guid? CashAccountId { get; set; }
    public Guid? BankAccountId { get; set; }

    /// <summary>เลขรันใบกำกับภาษี**อย่างย่อ** (§86/6) — ต้อง gap-free **ต่อสาขา**
    /// ห้ามใช้ `OrderNumber` แทน (นับต่อบริษัท และนับใบที่ void ด้วย)</summary>
    public string? AbbreviatedInvoicePrefix { get; set; }

    public ICollection<PosSession> Sessions { get; set; } = new List<PosSession>();
}

// ==================== POS Session (กะ/รอบ) ====================
/// <summary>เปิด-ปิดกะ / Cash drawer session</summary>
public class PosSession : TenantEntity
{
    public Guid TerminalId { get; set; }
    public PosTerminal Terminal { get; set; } = null!;

    public Guid OpenedByUserId { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public decimal OpeningBalance { get; set; }                  // เงินเปิดกะ
    public decimal ClosingBalance { get; set; }                  // เงินปิดกะ (นับจริง)
    public decimal ExpectedBalance { get; set; }                 // เงินที่ควรจะมี (คำนวณ)

    public PosSessionStatus Status { get; set; } = PosSessionStatus.Open;
    public string? Notes { get; set; }

    public ICollection<PosOrder> Orders { get; set; } = new List<PosOrder>();
}

// ==================== POS Order (บิลขาย) ====================
/// <summary>บิลขาย / ออเดอร์</summary>
public class PosOrder : TenantEntity
{
    public Guid SessionId { get; set; }
    public PosSession Session { get; set; } = null!;

    public string OrderNumber { get; set; } = null!;             // Running number: POS-202603-0001

    // ── snapshot สาขา/คลัง ณ เวลาเปิดออเดอร์ ──
    // **ห้าม resolve สดจาก terminal ตอนทำรายงาน** — เครื่องย้ายสาขาได้ แล้วยอดขาย
    // ย้อนหลังจะย้ายตามไปทั้งก้อน (defect class เดียวกับ IssuerBranchCode บนเอกสาร)
    public Guid? BranchId { get; set; }
    public Guid? WarehouseId { get; set; }

    /// <summary>เลขที่ใบกำกับภาษีอย่างย่อที่ออกให้บิลนี้ (§86/6) — null = ยังไม่ออก
    /// หรือบริษัทไม่ได้รับอนุมัติ ภ.พ.06 (พิมพ์หัวว่า "ใบเสร็จรับเงิน" แทน)</summary>
    public string? AbbreviatedInvoiceNumber { get; set; }
    /// <summary>รหัสสาขาสรรพากรที่ตรึงไว้ตอนออกใบ — §86/4 ห้ามเปลี่ยนย้อนหลัง</summary>
    public string? IssuerBranchCode { get; set; }
    public PosOrderType OrderType { get; set; }
    public PosOrderStatus Status { get; set; } = PosOrderStatus.Open;

    // Customer (optional)
    public Guid? CustomerId { get; set; }                        // Link to Contact
    public Contact? Customer { get; set; }
    public string? CustomerName { get; set; }                    // Quick entry name

    // Restaurant-specific
    public string? TableNumber { get; set; }
    public int? GuestCount { get; set; }

    // Cafe-specific
    public string? QueueNumber { get; set; }

    // Service-specific (appointment)
    public DateTime? AppointmentTime { get; set; }
    public Guid? PrimaryStaffId { get; set; }                    // พนักงานหลัก

    // Pricing
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal ServiceChargePercent { get; set; }
    public decimal ServiceChargeAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal RoundingAmount { get; set; }                  // ปัดเศษ
    public decimal NetAmount { get; set; }                       // ยอดสุทธิหลังปัดเศษ

    /// <summary>ทิปที่ลูกค้าให้ — รวมในยอดที่ลูกค้าจ่าย (NetAmount + Tip)
    /// แต่ลงบัญชี Cr "เงินรับฝาก-ทิปพนักงาน" (Liability) แทน Revenue
    /// เพื่อให้กำไรขั้นต้นไม่บวมจากทิป + จ่ายต่อพนักงานในรอบเงินเดือน.</summary>
    public decimal TipAmount { get; set; }

    /// <summary>คูปองที่ใช้ (ถ้ามี) — โค้ดจาก CmsCoupon (cross-channel)
    /// หรือโค้ดที่พนักงานสร้างเฉพาะหน้า. CouponDiscountAmount ลงในส่วนลด
    /// (รวมใน DiscountAmount) แต่เก็บแยกเพื่อ report.</summary>
    public string? CouponCode { get; set; }
    public decimal CouponDiscountAmount { get; set; }

    /// <summary>POS deposit/มัดจำ — order ที่รับเงินมัดจำก่อนส่งสินค้า/บริการ.
    /// True → JE Cr "ขายรอรับรู้" (217xx) แทน Revenue + Cr "ภาษีขาย" (21911)
    /// ถ้า tax point เกิดแล้ว. ใช้กับร้านอาหารรับจอง / spa จองล่วงหน้า /
    /// คาเฟ่ pre-order. เมื่อใช้บริการจริง → realize via POS-Realize endpoint.</summary>
    public bool IsDeposit { get; set; } = false;

    /// <summary>วันที่ realize มัดจำ (= ลูกค้ามาใช้บริการ/รับสินค้า) →
    /// trigger ตัด 217xx → Revenue. Null = ยังไม่ realize.</summary>
    public DateTime? DepositRealizedAt { get; set; }

    public string? Notes { get; set; }
    public string? Reference { get; set; }                       // เลขอ้างอิง (delivery order#, etc.)

    // Accounting link
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public Guid? DocumentId { get; set; }                        // Link to receipt/tax invoice

    /// <summary>Idempotency key set by the POS client for sales rung up
    /// while offline. SyncOfflineOrderAsync uses (CompanyId, ClientOrderId)
    /// to detect a retry of an already-synced sale and return it instead
    /// of creating a duplicate. Null for normal online orders.</summary>
    public Guid? ClientOrderId { get; set; }

    public DateTime? CompletedAt { get; set; }

    public ICollection<PosOrderItem> Items { get; set; } = new List<PosOrderItem>();
    public ICollection<PosPayment> Payments { get; set; } = new List<PosPayment>();
}

// ==================== POS Order Item (รายการในบิล) ====================
/// <summary>รายการสินค้า/บริการในบิล</summary>
public class PosOrderItem : BaseEntity
{
    public Guid OrderId { get; set; }
    public PosOrder Order { get; set; } = null!;

    // Link to product or service package
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid? ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    public string ItemName { get; set; } = null!;
    public string? ItemCode { get; set; }
    public decimal Quantity { get; set; } = 1;
    /// <summary>How much of this line has been refunded (partial refunds).
    /// Refundable remaining = Quantity − RefundedQuantity.</summary>
    public decimal RefundedQuantity { get; set; } = 0;
    public string? Unit { get; set; }                            // หน่วย: ชิ้น, แก้ว, ครั้ง
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal SubTotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public int LineOrder { get; set; }
    public PosItemStatus Status { get; set; } = PosItemStatus.Pending;
    public string? Notes { get; set; }

    // Modifiers (size, sweetness, extra shot, etc.)
    public ICollection<PosOrderItemModifier> Modifiers { get; set; } = new List<PosOrderItemModifier>();
    // Service activities (for service packages)
    public ICollection<PosServiceActivity> ServiceActivities { get; set; } = new List<PosServiceActivity>();
}

// ==================== POS Order Item Modifier (ตัวเลือกเพิ่มเติม) ====================
/// <summary>ตัวเลือก/ปรับแต่ง เช่น ขนาด ความหวาน เพิ่มช็อต</summary>
public class PosOrderItemModifier : BaseEntity
{
    public Guid OrderItemId { get; set; }
    public PosOrderItem OrderItem { get; set; } = null!;

    public Guid? ModifierOptionId { get; set; }
    public ProductModifierOption? ModifierOption { get; set; }

    public string ModifierGroupName { get; set; } = null!;       // "ขนาด", "ความหวาน"
    public string ModifierName { get; set; } = null!;            // "L", "50%"
    public decimal PriceAdjustment { get; set; }                 // +15, 0, -5
}

// ==================== POS Payment (การชำระเงิน) ====================
/// <summary>การชำระเงินในบิล (1 บิลจ่ายได้หลายวิธี)</summary>
public class PosPayment : BaseEntity
{
    public Guid OrderId { get; set; }
    public PosOrder Order { get; set; } = null!;

    public PaymentMethod PaymentMethod { get; set; }
    public decimal Amount { get; set; }
    public decimal ReceivedAmount { get; set; }                  // เงินที่ได้รับ (สำหรับเงินสด)
    public decimal ChangeAmount { get; set; }                    // เงินทอน

    public string? ReferenceNo { get; set; }                     // เลขอ้างอิง (card approval, transfer ref)
    public string? CardLastFour { get; set; }                    // บัตร 4 หลักท้าย
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}

// ==================== Service Package (แพ็คเกจบริการ) ====================
/// <summary>แพ็คเกจบริการ เช่น "สระผม+ทำเล็บ 399฿"</summary>
public class ServicePackage : TenantEntity
{
    public string Name { get; set; } = null!;                    // "สระผม+ทำเล็บ"
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }                             // รหัสแพ็คเกจ
    public string? Category { get; set; }                        // หมวดหมู่: "ทำเล็บ", "ทำผม"

    public decimal Price { get; set; }                           // ราคาขาย
    public decimal? CostPrice { get; set; }                      // ต้นทุน (ถ้ามี)
    public int DurationMinutes { get; set; }                     // เวลาโดยประมาณ (นาที)
    public bool IsActive { get; set; } = true;
    public bool IsVatIncluded { get; set; } = true;              // ราคารวม VAT หรือไม่

    // Revenue account mapping
    public Guid? RevenueAccountId { get; set; }                  // บัญชีรายได้ที่ผูก
    public ChartOfAccount? RevenueAccount { get; set; }

    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }

    public ICollection<ServiceComponent> Components { get; set; } = new List<ServiceComponent>();
}

// ==================== Service Component (ขั้นตอนในแพ็คเกจ) ====================
/// <summary>ขั้นตอน/กิจกรรมย่อยในแพ็คเกจ เช่น ขั้นตอนที่ 1: สระผม</summary>
public class ServiceComponent : BaseEntity
{
    public Guid PackageId { get; set; }
    public ServicePackage Package { get; set; } = null!;

    public int StepOrder { get; set; }                           // ลำดับขั้นตอน: 1, 2, 3
    public string Name { get; set; } = null!;                    // "สระผม"
    public string? NameEn { get; set; }
    public string? Description { get; set; }

    public int DurationMinutes { get; set; }                     // เวลาต่อขั้นตอน

    // Commission settings
    public CommissionType CommissionType { get; set; } = CommissionType.Fixed;
    public decimal CommissionValue { get; set; }                 // จำนวนเงิน หรือ เปอร์เซ็นต์
    public bool RequiresStaff { get; set; } = true;              // ต้องระบุพนักงานหรือไม่
}

// ==================== POS Service Activity (กิจกรรมที่ทำจริง) ====================
/// <summary>บันทึกกิจกรรมบริการจริง: ใครทำ เมื่อไหร่ คอมเท่าไหร่</summary>
public class PosServiceActivity : BaseEntity
{
    public Guid OrderItemId { get; set; }
    public PosOrderItem OrderItem { get; set; } = null!;

    public Guid ComponentId { get; set; }
    public ServiceComponent Component { get; set; } = null!;

    public Guid? StaffId { get; set; }                           // พนักงาน/ช่างที่ทำ (link to User)
    public string? StaffName { get; set; }                       // ชื่อพนักงาน (denormalized)

    public ServiceActivityStatus Status { get; set; } = ServiceActivityStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public decimal CommissionAmount { get; set; }                // ค่าคอมมิชชั่นที่คำนวณได้
    public string? Notes { get; set; }
}

// ==================== Product Modifier Group (กลุ่มตัวเลือก) ====================
/// <summary>กลุ่มตัวเลือกสินค้า เช่น "ขนาด", "ความหวาน", "ระดับความเผ็ด"</summary>
public class ProductModifierGroup : TenantEntity
{
    public string Name { get; set; } = null!;                    // "ขนาด"
    public string? NameEn { get; set; }
    public bool IsRequired { get; set; }                         // ต้องเลือกหรือไม่
    public bool AllowMultiple { get; set; }                      // เลือกได้หลายตัว
    public int SortOrder { get; set; }

    // Products that use this modifier group
    public ICollection<ProductModifierGroupLink> ProductLinks { get; set; } = new List<ProductModifierGroupLink>();
    public ICollection<ProductModifierOption> Options { get; set; } = new List<ProductModifierOption>();
}

// ==================== Product Modifier Group Link ====================
/// <summary>เชื่อมสินค้ากับกลุ่มตัวเลือก (M:N)</summary>
public class ProductModifierGroupLink : BaseEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid ModifierGroupId { get; set; }
    public ProductModifierGroup ModifierGroup { get; set; } = null!;
}

// ==================== Product Modifier Option (ตัวเลือก) ====================
/// <summary>ตัวเลือกย่อย เช่น S/M/L, 0%/25%/50%/100%</summary>
public class ProductModifierOption : BaseEntity
{
    public Guid GroupId { get; set; }
    public ProductModifierGroup Group { get; set; } = null!;

    public string Name { get; set; } = null!;                    // "S", "M", "L"
    public string? NameEn { get; set; }
    public decimal PriceAdjustment { get; set; }                 // +0, +10, +15
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

// ==================== Staff Commission Summary ====================
/// <summary>สรุปค่าคอมมิชชั่นพนักงานรายวัน/รายเดือน</summary>
public class StaffCommissionSummary : TenantEntity
{
    public Guid StaffId { get; set; }                            // UserId ของพนักงาน
    public string StaffName { get; set; } = null!;

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public int TotalActivities { get; set; }                     // จำนวนกิจกรรมทั้งหมด
    public decimal TotalCommission { get; set; }                 // รวมค่าคอมมิชชั่น
    public decimal PaidAmount { get; set; }                      // จ่ายแล้ว
    public decimal RemainingAmount { get; set; }                 // ค้างจ่าย

    public bool IsPaid { get; set; }
    public Guid? JournalEntryId { get; set; }                    // ลิงก์บัญชี (ค่าคอมมิชชั่น)
}
