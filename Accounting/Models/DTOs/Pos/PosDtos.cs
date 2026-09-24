using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Pos;

// ===== POS Terminal =====
// สาขา/คลัง/บัญชีรับเงิน — เพิ่มพร้อมกันทั้ง 3 record ตามเช็กลิสต์ "เก็บแล้วต้อง
// echo กลับ" (ลืม Response = เปิดแก้แล้วบันทึก ค่าหายเงียบ ๆ)
public record CreateTerminalRequest(
    string Name,
    PosBusinessMode BusinessMode,
    string? Location,
    string? SettingsJson,
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    Guid? CashAccountId = null,
    Guid? BankAccountId = null,
    string? AbbreviatedInvoicePrefix = null);

public record UpdateTerminalRequest(
    string? Name,
    PosBusinessMode? BusinessMode,
    string? Location,
    bool? IsActive,
    string? SettingsJson,
    // Guid.Empty = "ล้างค่า" (null = ไม่แตะ) — ต้องแยกสองความหมายนี้ ไม่งั้นถอด
    // สาขาออกจากเครื่องไม่ได้เลย
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    Guid? CashAccountId = null,
    Guid? BankAccountId = null,
    string? AbbreviatedInvoicePrefix = null);

public record TerminalResponse(
    Guid Id,
    string Name,
    PosBusinessMode BusinessMode,
    bool IsActive,
    string? Location,
    string? SettingsJson,
    int OpenSessionCount,
    Guid? BranchId = null,
    string? BranchName = null,
    // รหัสสาขาสรรพากรของสาขานี้ (§86/4) — "00000" = สำนักงานใหญ่
    string? BranchTaxCode = null,
    Guid? WarehouseId = null,
    string? WarehouseName = null,
    Guid? CashAccountId = null,
    Guid? BankAccountId = null,
    string? AbbreviatedInvoicePrefix = null);

// ===== POS Session =====
public record OpenSessionRequest(
    Guid? TerminalId,
    decimal OpeningBalance,
    string? Notes);

public record CloseSessionRequest(
    decimal ClosingBalance,
    string? Notes);

public record SessionResponse(
    Guid Id,
    Guid TerminalId,
    string TerminalName,
    Guid OpenedByUserId,
    Guid? ClosedByUserId,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal ExpectedBalance,
    PosSessionStatus Status,
    string? Notes,
    int OrderCount,
    decimal TotalSales);

// ===== POS Order =====
public record CreateOrderRequest(
    Guid SessionId,
    PosOrderType OrderType,
    Guid? CustomerId,
    string? CustomerName,
    string? TableNumber,
    int? GuestCount,
    string? QueueNumber,
    DateTime? AppointmentTime,
    Guid? PrimaryStaffId,
    decimal DiscountPercent = 0,
    decimal ServiceChargePercent = 0,
    string? Notes = null,
    string? Reference = null,
    List<CreateOrderItemRequest>? Items = null);

public record UpdateOrderRequest(
    PosOrderType? OrderType,
    Guid? CustomerId,
    string? CustomerName,
    string? TableNumber,
    int? GuestCount,
    string? QueueNumber,
    DateTime? AppointmentTime,
    Guid? PrimaryStaffId,
    decimal? DiscountPercent,
    decimal? ServiceChargePercent,
    string? Notes,
    string? Reference);

public record OrderResponse(
    Guid Id,
    Guid SessionId,
    string OrderNumber,
    PosOrderType OrderType,
    PosOrderStatus Status,
    Guid? CustomerId,
    string? CustomerName,
    string? TableNumber,
    int? GuestCount,
    string? QueueNumber,
    DateTime? AppointmentTime,
    Guid? PrimaryStaffId,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal DiscountPercent,
    decimal ServiceChargePercent,
    decimal ServiceChargeAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal RoundingAmount,
    decimal NetAmount,
    string? Notes,
    string? Reference,
    Guid? JournalEntryId,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    List<OrderItemResponse> Items,
    List<PaymentResponse> Payments,
    Guid? DocumentId = null,
    string? DocumentNumber = null,
    decimal TipAmount = 0,
    string? CouponCode = null,
    decimal CouponDiscountAmount = 0,
    // ── สาขา/คลัง (snapshot ตอนเปิดบิล) + หัวสลิปที่เซิร์ฟเวอร์คำนวณให้ ──
    // หน้าเว็บ **แสดง** อย่างเดียว ห้ามคำนวณเอง (defect class "สำเนามือฝั่ง JS"
    // เดียวกับ docHeaderLabel / complianceIssues / MENU_SECTIONS)
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    string? AbbreviatedInvoiceNumber = null,
    string? IssuerBranchCode = null,
    // ข้อความรหัสสาขาที่พิมพ์บนกระดาษ ("สำนักงานใหญ่" / "สาขาที่ 00003") —
    // null = ยังไม่รู้รหัสสาขา (ห้ามเดา)
    string? IssuerBranchLabel = null,
    // หัวสลิป: "ใบเสร็จรับเงิน" หรือ "ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ"
    string? SlipTitle = null,
    // เหตุผลที่ออกอย่างย่อไม่ได้ — โชว์เป็นคำเตือนพร้อมทางไปต่อ (null = ออกได้ปกติ)
    string? AbbreviatedBlockedReason = null);

// ===== Offline sale sync — atomic create+pay+complete, idempotent =====
public record OfflineOrderRequest(
    Guid ClientOrderId,
    Guid SessionId,
    PosOrderType OrderType,
    Guid? CustomerId,
    string? CustomerName,
    string? TableNumber,
    string? QueueNumber,
    decimal DiscountPercent,
    string? Notes,
    DateTime CompletedAt,
    List<CreateOrderItemRequest> Items,
    List<OfflinePaymentRequest> Payments);

public record OfflinePaymentRequest(
    PaymentMethod PaymentMethod,
    decimal Amount,
    decimal ReceivedAmount,
    string? ReferenceNo);

// ===== Issue full tax invoice for a completed POS order =====
public record IssueTaxInvoiceRequest(
    string BuyerName,
    string? BuyerTaxId,
    string? BuyerBranchCode,
    string? BuyerAddress,
    string? Notes);

// ===== POS Order Item =====
public record CreateOrderItemRequest(
    Guid? ProductId,
    Guid? ServicePackageId,
    string ItemName,
    string? ItemCode,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal DiscountPercent = 0,
    string? Notes = null,
    List<CreateItemModifierRequest>? Modifiers = null);

public record OrderItemResponse(
    Guid Id,
    Guid? ProductId,
    Guid? ServicePackageId,
    string ItemName,
    string? ItemCode,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal DiscountPercent,
    decimal SubTotal,
    decimal VatAmount,
    decimal TotalAmount,
    int LineOrder,
    PosItemStatus Status,
    string? Notes,
    List<ItemModifierResponse> Modifiers,
    List<ServiceActivityResponse> ServiceActivities,
    decimal RefundedQuantity = 0);

// ===== POS Order Item Modifier =====
public record CreateItemModifierRequest(
    Guid? ModifierOptionId,
    string ModifierGroupName,
    string ModifierName,
    decimal PriceAdjustment);

public record ItemModifierResponse(
    Guid Id,
    Guid? ModifierOptionId,
    string ModifierGroupName,
    string ModifierName,
    decimal PriceAdjustment);

// ===== POS Payment =====
public record CreatePaymentRequest(
    Guid OrderId,
    PaymentMethod PaymentMethod,
    decimal Amount,
    decimal ReceivedAmount,
    string? ReferenceNo,
    string? CardLastFour);

// ===== POS Refund (partial / full) =====
public record RefundOrderRequest(
    List<RefundLineRequest> Lines,
    PaymentMethod RefundMethod,
    string? Reason);

public record RefundLineRequest(Guid ItemId, decimal Quantity);

public record PaymentResponse(
    Guid Id,
    PaymentMethod PaymentMethod,
    decimal Amount,
    decimal ReceivedAmount,
    decimal ChangeAmount,
    string? ReferenceNo,
    string? CardLastFour,
    DateTime PaidAt);

// ===== Service Package =====
public record CreateServicePackageRequest(
    string Name,
    string? NameEn,
    string? Description,
    string? Sku,
    string? Category,
    decimal Price,
    decimal? CostPrice,
    int DurationMinutes,
    bool IsVatIncluded = true,
    Guid? RevenueAccountId = null,
    string? ImageUrl = null,
    int SortOrder = 0,
    List<CreateServiceComponentRequest>? Components = null);

public record UpdateServicePackageRequest(
    string? Name,
    string? NameEn,
    string? Description,
    string? Sku,
    string? Category,
    decimal? Price,
    decimal? CostPrice,
    int? DurationMinutes,
    bool? IsVatIncluded,
    bool? IsActive,
    Guid? RevenueAccountId,
    string? ImageUrl,
    int? SortOrder);

public record ServicePackageResponse(
    Guid Id,
    string Name,
    string? NameEn,
    string? Description,
    string? Sku,
    string? Category,
    decimal Price,
    decimal? CostPrice,
    int DurationMinutes,
    bool IsActive,
    bool IsVatIncluded,
    Guid? RevenueAccountId,
    string? ImageUrl,
    int SortOrder,
    List<ServiceComponentResponse> Components);

// ===== Service Component =====
public record CreateServiceComponentRequest(
    int StepOrder,
    string Name,
    string? NameEn,
    string? Description,
    int DurationMinutes,
    CommissionType CommissionType = CommissionType.Fixed,
    decimal CommissionValue = 0,
    bool RequiresStaff = true);

public record UpdateServiceComponentRequest(
    int? StepOrder,
    string? Name,
    string? NameEn,
    string? Description,
    int? DurationMinutes,
    CommissionType? CommissionType,
    decimal? CommissionValue,
    bool? RequiresStaff);

public record ServiceComponentResponse(
    Guid Id,
    int StepOrder,
    string Name,
    string? NameEn,
    string? Description,
    int DurationMinutes,
    CommissionType CommissionType,
    decimal CommissionValue,
    bool RequiresStaff,
    // รอบ 193 (M2): เซิร์ฟเวอร์ตัดสินว่าประเภทคอมมิชชันของแถวนี้เชื่อได้ไหม (Helpers/ServiceCommissionTypeReview)
    // หน้าเว็บแค่แสดงป้าย + บังคับเลือกใหม่ — ห้ามเดาจากตัวเลขเอง
    bool CommissionTypeNeedsReview = false,
    string? CommissionTypeReviewNote = null);

/// <summary>รายงานอ่านอย่างเดียว: ขั้นตอนบริการที่ประเภทคอมมิชชันต้องตรวจ (รอบ 193 · M2) — ไม่แก้ข้อมูลใด ๆ</summary>
public record ServiceCommissionReviewRow(
    Guid PackageId, string PackageName, Guid ComponentId, string ComponentName,
    int StoredValue, string Clarity, string Note);

public record ServiceCommissionReviewReport(
    int UndefinedValueCount, int AmbiguousLegacyCount, int TotalToReview,
    List<ServiceCommissionReviewRow> Rows);

// ===== POS Service Activity =====
public record UpdateServiceActivityRequest(
    Guid? StaffId,
    string? StaffName,
    ServiceActivityStatus? Status,
    string? Notes);

public record ServiceActivityResponse(
    Guid Id,
    Guid ComponentId,
    string ComponentName,
    int StepOrder,
    Guid? StaffId,
    string? StaffName,
    ServiceActivityStatus Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    decimal CommissionAmount,
    string? Notes);

// ===== Product Modifier Group =====
public record CreateModifierGroupRequest(
    string Name,
    string? NameEn,
    bool IsRequired,
    bool AllowMultiple,
    int SortOrder = 0,
    List<Guid>? ProductIds = null,
    List<CreateModifierOptionRequest>? Options = null);

public record UpdateModifierGroupRequest(
    string? Name,
    string? NameEn,
    bool? IsRequired,
    bool? AllowMultiple,
    int? SortOrder);

public record ModifierGroupResponse(
    Guid Id,
    string Name,
    string? NameEn,
    bool IsRequired,
    bool AllowMultiple,
    int SortOrder,
    List<ModifierOptionResponse> Options);

// ===== Product Modifier Option =====
public record CreateModifierOptionRequest(
    string Name,
    string? NameEn,
    decimal PriceAdjustment = 0,
    bool IsDefault = false,
    int SortOrder = 0);

public record UpdateModifierOptionRequest(
    string? Name,
    string? NameEn,
    decimal? PriceAdjustment,
    bool? IsDefault,
    bool? IsActive,
    int? SortOrder);

public record ModifierOptionResponse(
    Guid Id,
    string Name,
    string? NameEn,
    decimal PriceAdjustment,
    bool IsDefault,
    int SortOrder,
    bool IsActive);

// ===== Staff Commission Summary =====
public record CommissionSummaryResponse(
    Guid Id,
    Guid StaffId,
    string StaffName,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    int TotalActivities,
    decimal TotalCommission,
    decimal PaidAmount,
    decimal RemainingAmount,
    bool IsPaid);

/// <summary>คอมมิชชั่นรายกิจกรรม — ใช้ตรวจสอบ / audit ว่ามาจากออเดอร์ใด ทำเมื่อไหร่ คิดยังไง</summary>
public record CommissionDetailResponse(
    Guid ActivityId,
    Guid OrderId,
    string OrderNumber,
    DateTime OrderDate,
    Guid OrderItemId,
    string ItemName,           // ชื่อบริการ/แพ็กเกจ
    string ComponentName,      // ขั้นตอนย่อยที่ทำ
    string Status,             // Pending / InProgress / Completed
    DateTime? CompletedAt,
    decimal CommissionAmount,
    string? Notes);

// ===== POS Daily Summary =====
public record PosDailySummaryResponse(
    DateTime Date,
    int TotalOrders,
    int CompletedOrders,
    int VoidedOrders,
    decimal TotalSales,
    decimal TotalDiscount,
    decimal TotalVat,
    decimal TotalServiceCharge,
    decimal NetSales,
    List<PaymentMethodSummary> PaymentBreakdown);

/// <summary>สรุปยอดขาย POS **รายสาขา** ของวันหนึ่ง — คำถามแรกของเจ้าของร้านหลายสาขา
/// ("สาขาไหนขายดี · สาขาไหนต้นทุน/waste สูงผิดปกติ") ซึ่งเดิมตอบไม่ได้เลยเพราะ
/// `GetDailySummaryAsync` รวมทั้งบริษัท และ JE ของ POS ไม่มีมิติสาขา</summary>
public record PosBranchSummaryRow(
    Guid? BranchId,
    string BranchName,
    // null = สาขายังไม่กรอกรหัสสาขาสรรพากร (ห้ามเดาเป็น 00000)
    string? TaxBranchCode,
    int CompletedOrders,
    int VoidedOrders,
    decimal NetSales,
    decimal VatAmount,
    decimal DiscountAmount,
    // ยอดเฉลี่ยต่อบิล — ตัวเทียบสาขาที่ใช้บ่อยที่สุด
    decimal AveragePerOrder,
    List<PaymentMethodSummary> PaymentBreakdown);

public record PosBranchSummaryResponse(
    DateTime FromDate,
    DateTime ToDate,
    List<PosBranchSummaryRow> Branches,
    decimal GrandTotalNetSales,
    // true = มีบิลที่ยังไม่ผูกสาขา (เครื่องที่ยังไม่ตั้งค่า) — UI ต้องเตือน ไม่ใช่ซ่อน
    // มิฉะนั้นผลรวมรายสาขาจะไม่เท่ายอดรวมบริษัทโดยไม่มีใครรู้ว่าทำไม
    bool HasUnassignedBranch);

public record PaymentMethodSummary(
    PaymentMethod Method,
    string MethodName,
    int Count,
    decimal Amount);

/// <summary>Z/X-Report — รายงานสิ้นกะ (Z = ปิด session, X = ระหว่างกะ).
/// Aggregate ของ JE/Order ที่เกิดในช่วง (จาก session.OpenedAt..ClosedAt
/// หรือช่วงเวลาที่ caller ระบุ). ใช้เทียบเงินสดในลิ้นชัก + audit ก่อนปิดงาน.</summary>
public record PosZReportResponse(
    DateTime PeriodFrom,
    DateTime PeriodTo,
    Guid? SessionId,
    Guid? TerminalId,
    string? TerminalName,
    string? OpenedByName,
    string? ClosedByName,
    // Order counts
    int TotalOrders,
    int CompletedOrders,
    int VoidedOrders,
    int RefundedOrders,
    int GuestCount,
    // Sales aggregate (excl VAT)
    decimal GrossSales,
    decimal TotalDiscount,
    decimal NetSales,
    decimal TotalVat,
    decimal TotalServiceCharge,
    decimal TotalTip,
    // Refunds/voids
    decimal RefundedAmount,
    decimal VoidedAmount,
    // Payment breakdown by method
    List<PaymentMethodSummary> PaymentBreakdown,
    // Cash drawer reconcile
    decimal OpeningCash,
    decimal ClosingCash,
    decimal CashSales,
    decimal CashRefunds,
    decimal ExpectedCash,
    decimal CashVariance,
    // Top sellers
    List<PosTopProductSummary> TopProducts);

public record PosTopProductSummary(
    Guid ProductId,
    string ProductName,
    decimal Quantity,
    decimal Revenue);

// ===== Order Status Update =====
public record UpdateOrderStatusRequest(PosOrderStatus Status);
public record UpdateItemStatusRequest(PosItemStatus Status);
