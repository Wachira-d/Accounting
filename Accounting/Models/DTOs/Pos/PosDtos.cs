using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Pos;

// ===== POS Terminal =====
public record CreateTerminalRequest(
    string Name,
    PosBusinessMode BusinessMode,
    string? Location,
    string? SettingsJson);

public record UpdateTerminalRequest(
    string? Name,
    PosBusinessMode? BusinessMode,
    string? Location,
    bool? IsActive,
    string? SettingsJson);

public record TerminalResponse(
    Guid Id,
    string Name,
    PosBusinessMode BusinessMode,
    bool IsActive,
    string? Location,
    string? SettingsJson,
    int OpenSessionCount);

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
    string? DocumentNumber = null);

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
    bool RequiresStaff);

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

public record PaymentMethodSummary(
    PaymentMethod Method,
    string MethodName,
    int Count,
    decimal Amount);

// ===== Order Status Update =====
public record UpdateOrderStatusRequest(PosOrderStatus Status);
public record UpdateItemStatusRequest(PosItemStatus Status);
