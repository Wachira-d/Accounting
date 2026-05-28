using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;

namespace Accounting.Services.Interfaces;

public interface IPosService
{
    // Terminal
    Task<TerminalResponse> CreateTerminalAsync(Guid companyId, CreateTerminalRequest request);
    Task<List<TerminalResponse>> GetTerminalsAsync(Guid companyId);
    Task<TerminalResponse> UpdateTerminalAsync(Guid companyId, Guid terminalId, UpdateTerminalRequest request);

    // Session
    Task<SessionResponse> OpenSessionAsync(Guid companyId, Guid userId, OpenSessionRequest request);
    Task<SessionResponse> CloseSessionAsync(Guid companyId, Guid userId, Guid sessionId, CloseSessionRequest request);
    Task<SessionResponse> GetSessionAsync(Guid companyId, Guid sessionId);
    Task<List<SessionResponse>> GetSessionsAsync(Guid companyId, Guid? terminalId = null, bool? isOpen = null);

    // Order
    Task<OrderResponse> CreateOrderAsync(Guid companyId, CreateOrderRequest request, string createdBy);
    Task<OrderResponse> GetOrderAsync(Guid companyId, Guid orderId);
    Task<PagedResponse<OrderResponse>> GetOrdersAsync(Guid companyId, PagedRequest request, Guid? sessionId = null, string? status = null);
    Task<OrderResponse> UpdateOrderAsync(Guid companyId, Guid orderId, UpdateOrderRequest request);
    Task<OrderResponse> UpdateOrderStatusAsync(Guid companyId, Guid orderId, UpdateOrderStatusRequest request, string userId);
    Task<OrderResponse> AddOrderItemAsync(Guid companyId, Guid orderId, CreateOrderItemRequest request);
    Task RemoveOrderItemAsync(Guid companyId, Guid orderId, Guid itemId);
    Task<OrderResponse> UpdateItemStatusAsync(Guid companyId, Guid orderId, Guid itemId, UpdateItemStatusRequest request);
    Task<OrderResponse> UpdateOrderItemQuantityAsync(Guid companyId, Guid orderId, Guid itemId, decimal newQuantity);
    Task VoidOrderAsync(Guid companyId, Guid orderId, string userId);
    /// <summary>คืนเงินบางส่วน/ทั้งหมดของออเดอร์ที่ปิดบิลแล้ว — กลับรายการ GL ตามสัดส่วน + คืนสต็อก</summary>
    Task<OrderResponse> RefundOrderAsync(Guid companyId, Guid orderId, RefundOrderRequest request, string userId);
    /// <summary>ออกใบกำกับภาษีเต็มรูปสำหรับออเดอร์ POS — สร้าง Document แบบ TaxInvoice + ผูก JE เดิมเข้ากับเอกสาร (กัน VAT ซ้ำใน ภพ.30)</summary>
    Task<OrderResponse> IssueTaxInvoiceAsync(Guid companyId, Guid orderId, IssueTaxInvoiceRequest request, string userId);
    /// <summary>Sync ออเดอร์ที่บันทึกตอนออฟไลน์ — atomic create+pay+complete พร้อม idempotency จาก ClientOrderId</summary>
    Task<OrderResponse> SyncOfflineOrderAsync(Guid companyId, OfflineOrderRequest request, string createdBy);

    // Payment
    Task<OrderResponse> AddPaymentAsync(Guid companyId, CreatePaymentRequest request, string userId);
    Task<OrderResponse> CompleteOrderAsync(Guid companyId, Guid orderId, string userId);

    // Service Package
    Task<ServicePackageResponse> CreateServicePackageAsync(Guid companyId, CreateServicePackageRequest request);
    Task<List<ServicePackageResponse>> GetServicePackagesAsync(Guid companyId, string? category = null);
    Task<ServicePackageResponse> GetServicePackageAsync(Guid companyId, Guid packageId);
    Task<ServicePackageResponse> UpdateServicePackageAsync(Guid companyId, Guid packageId, UpdateServicePackageRequest request);
    Task DeleteServicePackageAsync(Guid companyId, Guid packageId);

    // Service Component
    Task<ServicePackageResponse> AddComponentAsync(Guid companyId, Guid packageId, CreateServiceComponentRequest request);
    Task<ServicePackageResponse> UpdateComponentAsync(Guid companyId, Guid packageId, Guid componentId, UpdateServiceComponentRequest request);
    Task RemoveComponentAsync(Guid companyId, Guid packageId, Guid componentId);

    // Service Activity
    Task<ServiceActivityResponse> UpdateServiceActivityAsync(Guid companyId, Guid activityId, UpdateServiceActivityRequest request);

    // Modifier Group
    Task<ModifierGroupResponse> CreateModifierGroupAsync(Guid companyId, CreateModifierGroupRequest request);
    Task<List<ModifierGroupResponse>> GetModifierGroupsAsync(Guid companyId, Guid? productId = null);
    Task<ModifierGroupResponse> UpdateModifierGroupAsync(Guid companyId, Guid groupId, UpdateModifierGroupRequest request);
    Task DeleteModifierGroupAsync(Guid companyId, Guid groupId);

    // Modifier Option
    Task<ModifierGroupResponse> AddModifierOptionAsync(Guid companyId, Guid groupId, CreateModifierOptionRequest request);
    Task<ModifierGroupResponse> UpdateModifierOptionAsync(Guid companyId, Guid groupId, Guid optionId, UpdateModifierOptionRequest request);
    Task RemoveModifierOptionAsync(Guid companyId, Guid groupId, Guid optionId);

    // Reports
    Task<PosDailySummaryResponse> GetDailySummaryAsync(Guid companyId, DateTime date);
    Task<List<CommissionSummaryResponse>> GetCommissionSummariesAsync(Guid companyId, DateTime periodStart, DateTime periodEnd);

    /// <summary>คอมมิชชั่นรายกิจกรรม (per-activity audit trail) — ใช้ตรวจสอบว่ามาจากออเดอร์ใด ขั้นตอนใด คิดยังไง</summary>
    Task<List<CommissionDetailResponse>> GetCommissionDetailsAsync(Guid companyId, Guid staffId, DateTime periodStart, DateTime periodEnd);
}
