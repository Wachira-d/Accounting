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
    Task<OrderResponse> SetItemDiscountAsync(Guid companyId, Guid orderId, Guid itemId, decimal? discountAmount, decimal? discountPercent);
    Task<OrderResponse> SetTipAsync(Guid companyId, Guid orderId, decimal tipAmount);
    Task<OrderResponse> ApplyCouponAsync(Guid companyId, Guid orderId, string? code);
    Task<List<OrderResponse>> SplitOrderAsync(Guid companyId, Guid orderId, List<List<Guid>> itemGroupsPerCheck, string userId);
    Task EmailReceiptAsync(Guid companyId, Guid orderId, string email);
    Task<OrderResponse> MergeOrdersAsync(Guid companyId, Guid destinationOrderId, List<Guid> sourceOrderIds, string userId);
    Task<OrderResponse> TransferTableAsync(Guid companyId, Guid orderId, string? newTableNumber, string userId);
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

    /// <summary>Z-Report (รายงานปิดยอดสิ้นกะ/วัน) — aggregate ทุกออเดอร์ Completed
    /// ของ session แล้วสรุป: ยอดขายแบ่งตาม PaymentMethod, VAT, COGS, tip,
    /// refund, void, จำนวนออเดอร์, จำนวนแขก. read-only (ไม่ post JE เพิ่ม —
    /// JE เกิดต่อออเดอร์อยู่แล้ว) ใช้สำหรับเทียบเงินสดในลิ้นชัก + ส่งหัวหน้า
    /// + งบประจำวันร้านค้าปลีก ตามมาตรฐาน RD ระบบเงินสด.</summary>
    Task<PosZReportResponse> GetZReportAsync(Guid companyId, Guid sessionId);

    /// <summary>X-Report (รายงานยอดระหว่างกะ — ไม่ปิด session) — สำหรับ
    /// shift manager ดูยอดวิ่งทันที ไม่ต้องรอ session.Close</summary>
    Task<PosZReportResponse> GetXReportAsync(Guid companyId, Guid? terminalId = null, DateTime? from = null, DateTime? to = null);

    /// <summary>คอมมิชชั่นรายกิจกรรม (per-activity audit trail) — ใช้ตรวจสอบว่ามาจากออเดอร์ใด ขั้นตอนใด คิดยังไง</summary>
    Task<List<CommissionDetailResponse>> GetCommissionDetailsAsync(Guid companyId, Guid staffId, DateTime periodStart, DateTime periodEnd);
}
