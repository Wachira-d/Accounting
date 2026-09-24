using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ═══ ด่านสิทธิ์ของ POS (D8-7 · รอบ 184) ═══
/// <para><c>[Authorize]</c> ระดับคลาสตอบแค่ "ล็อกอินอยู่ไหม" — เดิมทั้งไฟล์มีด่าน
/// สิทธิ์จริงแค่ปุ่มเดียว (<c>issue-tax-invoice</c>) ⇒ <b>สมาชิกคนไหนของบริษัทก็กด
/// คืนเงินจากลิ้นชัก · ยกเลิกบิลที่ปิดแล้ว · แก้เมนู/ราคา · ปิดกะ ได้ทั้งหมด</b></para>
///
/// <para>ทุก endpoint ที่ **เขียนข้อมูล** ผูกกับคีย์ที่มีอยู่แล้วใน
/// <see cref="PermissionKeys"/> (ไม่สร้างคีย์ใหม่) แบ่ง 4 ชั้นตาม "ความเสียหาย
/// ถ้าคนผิดกด":</para>
/// <list type="bullet">
/// <item><c>POS.Cashier</c> — งานขายประจำวัน (เปิดกะ · บิล · รายการ · ส่วนลดรายการ ·
///   ทิป · คูปอง · รวม/แยกบิล · ย้ายโต๊ะ · รับเงิน · ปิดบิล · sync ออฟไลน์ · ส่งใบเสร็จ)</item>
/// <item><c>POS.Refund</c> — <b>เงินออก/กลับรายการ</b>: คืนเงิน + ยกเลิกบิล
///   (ยกเลิกบิลที่ปิดแล้ว = reverse JE + คืนสต็อก ⇒ อันตรายเท่าคืนเงิน)</item>
/// <item><c>POS.CloseDay</c> — ปิดกะ/กระทบยอดลิ้นชัก</item>
/// <item><c>POS.Manager</c> — ตั้งค่าเครื่อง · แพ็กเกจบริการ · ตัวเลือกเสริม (ราคา)</item>
/// <item><c>Document.Revenue.Approve</c> — ออกใบกำกับเต็มรูป (อนุมัติเอกสารรายได้จริง)</item>
/// </list>
///
/// <para><b>ผู้ใช้ที่ถูกกันทำอะไรได้แทน</b>: <c>HasPermissionAsync</c> ให้
/// Owner/SystemAdmin ผ่านอัตโนมัติ · role อื่นที่ยังไม่ได้รับสิทธิ์จะได้ 403 พร้อม
/// **ชื่อคีย์ที่ต้องขอ** (บิล/กะยังอยู่ ทำต่อได้ทันทีที่เจ้าของติ๊กคีย์ให้ใน
/// หน้า "บทบาทและสิทธิ์" — เทมเพลต "POS Cashier" มีคีย์ชุดนี้อยู่แล้ว
/// ดู <c>PermissionCatalogController</c>)</para>
///
/// <para><b>ตำแหน่ง attribute อยู่ใต้ <c>[Http…]</c> โดยตั้งใจ</b> —
/// <c>tools/write_permission_gate_check.py</c> อ่าน "ตัวของ action" นับจากบรรทัด
/// <c>[Http…]</c> ลงไป ถ้าวางไว้**เหนือ**มัน ด่านจะถูกนับให้ action **ก่อนหน้า**
/// ⇒ checker เขียวทั้งที่ endpoint จริงไม่มีด่าน (checker ที่ฟ้องผิด = checker ที่พัง)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/pos")]
[Authorize]
public class PosController : ControllerBase
{
    private readonly IPosService _pos;
    public PosController(IPosService pos) => _pos = pos;

    // ===== Terminal =====
    [HttpGet("terminals")]
    public async Task<ActionResult<ApiResponse<List<TerminalResponse>>>> GetTerminals(Guid companyId)
        => Ok(new ApiResponse<List<TerminalResponse>>(true, await _pos.GetTerminalsAsync(companyId)));

    [HttpPost("terminals")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<TerminalResponse>>> CreateTerminal(Guid companyId, [FromBody] CreateTerminalRequest request)
        => StatusCode(201, new ApiResponse<TerminalResponse>(true, await _pos.CreateTerminalAsync(companyId, request)));

    [HttpPut("terminals/{terminalId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<TerminalResponse>>> UpdateTerminal(Guid companyId, Guid terminalId, [FromBody] UpdateTerminalRequest request)
        => Ok(new ApiResponse<TerminalResponse>(true, await _pos.UpdateTerminalAsync(companyId, terminalId, request)));

    // ===== Session =====
    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<SessionResponse>>>> GetSessions(Guid companyId, [FromQuery] Guid? terminalId = null, [FromQuery] bool? isOpen = null)
        => Ok(new ApiResponse<List<SessionResponse>>(true, await _pos.GetSessionsAsync(companyId, terminalId, isOpen)));

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> GetSession(Guid companyId, Guid sessionId)
        => Ok(new ApiResponse<SessionResponse>(true, await _pos.GetSessionAsync(companyId, sessionId)));

    [HttpPost("sessions/open")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> OpenSession(Guid companyId, [FromBody] OpenSessionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return StatusCode(201, new ApiResponse<SessionResponse>(true, await _pos.OpenSessionAsync(companyId, userId, request)));
    }

    [HttpPost("sessions/{sessionId:guid}/close")]
    [RequirePermission(PermissionKeys.PosCloseDay)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> CloseSession(Guid companyId, Guid sessionId, [FromBody] CloseSessionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<SessionResponse>(true, await _pos.CloseSessionAsync(companyId, userId, sessionId, request)));
    }

    // ===== Order =====
    [HttpGet("orders")]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderResponse>>>> GetOrders(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] Guid? sessionId = null, [FromQuery] string? status = null)
        => Ok(new ApiResponse<PagedResponse<OrderResponse>>(true, await _pos.GetOrdersAsync(companyId, new PagedRequest(page, pageSize, search), sessionId, status)));

    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetOrder(Guid companyId, Guid orderId)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.GetOrderAsync(companyId, orderId)));

    [HttpPost("orders")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CreateOrder(Guid companyId, [FromBody] CreateOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return StatusCode(201, new ApiResponse<OrderResponse>(true, await _pos.CreateOrderAsync(companyId, request, userId)));
    }

    [HttpPut("orders/{orderId:guid}")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrder(Guid companyId, Guid orderId, [FromBody] UpdateOrderRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateOrderAsync(companyId, orderId, request)));

    [HttpPost("orders/{orderId:guid}/status")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrderStatus(Guid companyId, Guid orderId, [FromBody] UpdateOrderStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateOrderStatusAsync(companyId, orderId, request, userId)));
    }

    [HttpPost("orders/{orderId:guid}/void")]
    [RequirePermission(PermissionKeys.PosRefund)]
    public async Task<ActionResult<ApiResponse<string>>> VoidOrder(Guid companyId, Guid orderId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _pos.VoidOrderAsync(companyId, orderId, userId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกออเดอร์สำเร็จ"));
    }

    [HttpPost("orders/{orderId:guid}/refund")]
    [RequirePermission(PermissionKeys.PosRefund)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> RefundOrder(Guid companyId, Guid orderId, [FromBody] RefundOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.RefundOrderAsync(companyId, orderId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "คืนเงินสำเร็จ"));
    }

    /// <summary>ออกใบกำกับภาษี**เต็มรูป** (§86/4) จากบิล POS ที่ปิดแล้ว
    ///
    /// <para>★ D8-1(ง) — ด่านสิทธิ์: การกดปุ่มนี้คือการ **อนุมัติเอกสารรายได้**
    /// (เอกสารเกิดพร้อมสถานะ Approved + เลขที่ในเล่มใบกำกับ ซึ่งแก้ย้อนหลังไม่ได้
    /// ตาม §86/4) จึงใช้คีย์เดียวกับเส้นเอกสาร ไม่ใช่คีย์ POS ทั่วไป —
    /// Owner/SystemAdmin/Accountant ผ่านอัตโนมัติ · แคชเชียร์ที่ไม่ได้รับสิทธิ์จะได้ 403
    /// พร้อมชื่อคีย์ที่ต้องขอ (บิลยังอยู่ ออกใบใหม่ได้เมื่อได้สิทธิ์)</para></summary>
    [HttpPost("orders/{orderId:guid}/issue-tax-invoice")]
    [RequirePermission(PermissionKeys.DocumentRevenueApprove)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> IssueTaxInvoice(Guid companyId, Guid orderId, [FromBody] IssueTaxInvoiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.IssueTaxInvoiceAsync(companyId, orderId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "ออกใบกำกับภาษีเต็มรูปสำเร็จ"));
    }

    [HttpPost("orders/sync-offline")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> SyncOfflineOrder(Guid companyId, [FromBody] OfflineOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.SyncOfflineOrderAsync(companyId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "Sync ออเดอร์ออฟไลน์สำเร็จ"));
    }

    // ===== Order Items =====
    [HttpPost("orders/{orderId:guid}/items")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> AddOrderItem(Guid companyId, Guid orderId, [FromBody] CreateOrderItemRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.AddOrderItemAsync(companyId, orderId, request)));

    [HttpDelete("orders/{orderId:guid}/items/{itemId:guid}")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult> RemoveOrderItem(Guid companyId, Guid orderId, Guid itemId)
    {
        await _pos.RemoveOrderItemAsync(companyId, orderId, itemId);
        return NoContent();
    }

    [HttpPost("orders/{orderId:guid}/items/{itemId:guid}/status")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateItemStatus(Guid companyId, Guid orderId, Guid itemId, [FromBody] UpdateItemStatusRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateItemStatusAsync(companyId, orderId, itemId, request)));

    public record UpdateItemQtyRequest(decimal Quantity);
    [HttpPut("orders/{orderId:guid}/items/{itemId:guid}/qty")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateItemQty(Guid companyId, Guid orderId, Guid itemId, [FromBody] UpdateItemQtyRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateOrderItemQuantityAsync(companyId, orderId, itemId, request.Quantity)));

    public record SetItemDiscountRequest(decimal? DiscountAmount, decimal? DiscountPercent);
    [HttpPut("orders/{orderId:guid}/items/{itemId:guid}/discount")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> SetItemDiscount(Guid companyId, Guid orderId, Guid itemId, [FromBody] SetItemDiscountRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.SetItemDiscountAsync(companyId, orderId, itemId, request.DiscountAmount, request.DiscountPercent)));

    public record SetTipRequest(decimal TipAmount);
    [HttpPut("orders/{orderId:guid}/tip")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> SetTip(Guid companyId, Guid orderId, [FromBody] SetTipRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.SetTipAsync(companyId, orderId, request.TipAmount)));

    public record ApplyCouponRequest2(string? Code);
    [HttpPost("orders/{orderId:guid}/coupon")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> ApplyCoupon(Guid companyId, Guid orderId, [FromBody] ApplyCouponRequest2 request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.ApplyCouponAsync(companyId, orderId, request.Code)));

    public record MergeOrdersRequest(List<Guid> SourceOrderIds);
    [HttpPost("orders/{orderId:guid}/merge")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Merge(Guid companyId, Guid orderId, [FromBody] MergeOrdersRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.MergeOrdersAsync(companyId, orderId, request.SourceOrderIds, userId)));
    }

    public record TransferTableRequest(string? TableNumber);
    [HttpPut("orders/{orderId:guid}/table")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> TransferTable(Guid companyId, Guid orderId, [FromBody] TransferTableRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.TransferTableAsync(companyId, orderId, request.TableNumber, userId)));
    }

    public record EmailReceiptRequest(string Email);
    [HttpPost("orders/{orderId:guid}/email-receipt")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<string>>> EmailReceipt(Guid companyId, Guid orderId, [FromBody] EmailReceiptRequest request)
    {
        await _pos.EmailReceiptAsync(companyId, orderId, request.Email);
        return Ok(new ApiResponse<string>(true, null, "ส่งใบเสร็จทางอีเมลแล้ว"));
    }

    public record SplitOrderRequest(List<List<Guid>> Checks);
    /// <summary>Split an order into multiple checks. Caller sends a list of
    /// item-id groups — each group becomes a new child order. The original
    /// order is voided. Returns all child orders in order.</summary>
    [HttpPost("orders/{orderId:guid}/split")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<List<OrderResponse>>>> SplitOrder(Guid companyId, Guid orderId, [FromBody] SplitOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<List<OrderResponse>>(true, await _pos.SplitOrderAsync(companyId, orderId, request.Checks, userId)));
    }

    // ===== Payment =====
    [HttpPost("payments")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> AddPayment(Guid companyId, [FromBody] CreatePaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.AddPaymentAsync(companyId, request, userId)));
    }

    [HttpPost("orders/{orderId:guid}/complete")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CompleteOrder(Guid companyId, Guid orderId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.CompleteOrderAsync(companyId, orderId, userId), "ปิดบิลสำเร็จ"));
    }

    // ===== Service Package =====
    [HttpGet("packages")]
    public async Task<ActionResult<ApiResponse<List<ServicePackageResponse>>>> GetPackages(Guid companyId, [FromQuery] string? category = null)
        => Ok(new ApiResponse<List<ServicePackageResponse>>(true, await _pos.GetServicePackagesAsync(companyId, category)));

    /// <summary>อ่านอย่างเดียว: นับขั้นตอนบริการที่ประเภทคอมมิชชันต้องตรวจ (แถวเก่าจากฟอร์มที่ option 0/1
    /// กลับด้าน) — ไม่แปลงข้อมูล (เจ้าของต้องตัดสิน) · รอบ 193 M2</summary>
    [HttpGet("packages/commission-review")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ServiceCommissionReviewReport>>> GetCommissionReview(Guid companyId)
        => Ok(new ApiResponse<ServiceCommissionReviewReport>(true, await _pos.GetServiceCommissionReviewAsync(companyId)));

    [HttpGet("packages/{packageId:guid}")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> GetPackage(Guid companyId, Guid packageId)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.GetServicePackageAsync(companyId, packageId)));

    [HttpPost("packages")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> CreatePackage(Guid companyId, [FromBody] CreateServicePackageRequest request)
        => StatusCode(201, new ApiResponse<ServicePackageResponse>(true, await _pos.CreateServicePackageAsync(companyId, request)));

    [HttpPut("packages/{packageId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> UpdatePackage(Guid companyId, Guid packageId, [FromBody] UpdateServicePackageRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.UpdateServicePackageAsync(companyId, packageId, request)));

    [HttpDelete("packages/{packageId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult> DeletePackage(Guid companyId, Guid packageId)
    {
        await _pos.DeleteServicePackageAsync(companyId, packageId);
        return NoContent();
    }

    // ===== Service Component =====
    [HttpPost("packages/{packageId:guid}/components")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> AddComponent(Guid companyId, Guid packageId, [FromBody] CreateServiceComponentRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.AddComponentAsync(companyId, packageId, request)));

    [HttpPut("packages/{packageId:guid}/components/{componentId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> UpdateComponent(Guid companyId, Guid packageId, Guid componentId, [FromBody] UpdateServiceComponentRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.UpdateComponentAsync(companyId, packageId, componentId, request)));

    [HttpDelete("packages/{packageId:guid}/components/{componentId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult> RemoveComponent(Guid companyId, Guid packageId, Guid componentId)
    {
        await _pos.RemoveComponentAsync(companyId, packageId, componentId);
        return NoContent();
    }

    // ===== Service Activity =====
    [HttpPut("activities/{activityId:guid}")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<ServiceActivityResponse>>> UpdateActivity(Guid companyId, Guid activityId, [FromBody] UpdateServiceActivityRequest request)
        => Ok(new ApiResponse<ServiceActivityResponse>(true, await _pos.UpdateServiceActivityAsync(companyId, activityId, request)));

    // ===== Modifier Group =====
    [HttpGet("modifier-groups")]
    public async Task<ActionResult<ApiResponse<List<ModifierGroupResponse>>>> GetModifierGroups(Guid companyId, [FromQuery] Guid? productId = null)
        => Ok(new ApiResponse<List<ModifierGroupResponse>>(true, await _pos.GetModifierGroupsAsync(companyId, productId)));

    [HttpPost("modifier-groups")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> CreateModifierGroup(Guid companyId, [FromBody] CreateModifierGroupRequest request)
        => StatusCode(201, new ApiResponse<ModifierGroupResponse>(true, await _pos.CreateModifierGroupAsync(companyId, request)));

    [HttpPut("modifier-groups/{groupId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> UpdateModifierGroup(Guid companyId, Guid groupId, [FromBody] UpdateModifierGroupRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.UpdateModifierGroupAsync(companyId, groupId, request)));

    [HttpDelete("modifier-groups/{groupId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult> DeleteModifierGroup(Guid companyId, Guid groupId)
    {
        await _pos.DeleteModifierGroupAsync(companyId, groupId);
        return NoContent();
    }

    // ===== Modifier Option =====
    [HttpPost("modifier-groups/{groupId:guid}/options")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> AddOption(Guid companyId, Guid groupId, [FromBody] CreateModifierOptionRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.AddModifierOptionAsync(companyId, groupId, request)));

    [HttpPut("modifier-groups/{groupId:guid}/options/{optionId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> UpdateOption(Guid companyId, Guid groupId, Guid optionId, [FromBody] UpdateModifierOptionRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.UpdateModifierOptionAsync(companyId, groupId, optionId, request)));

    [HttpDelete("modifier-groups/{groupId:guid}/options/{optionId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult> RemoveOption(Guid companyId, Guid groupId, Guid optionId)
    {
        await _pos.RemoveModifierOptionAsync(companyId, groupId, optionId);
        return NoContent();
    }

    // ===== Reports =====
    [HttpGet("daily-summary")]
    public async Task<ActionResult<ApiResponse<PosDailySummaryResponse>>> GetDailySummary(Guid companyId, [FromQuery] DateTime? date = null)
        => Ok(new ApiResponse<PosDailySummaryResponse>(true, await _pos.GetDailySummaryAsync(companyId, date ?? DateTime.UtcNow)));

    /// <summary>ยอดขายแยกรายสาขา — คำถามแรกของเจ้าของร้านหลายสาขา
    /// (POS_MULTI_BRANCH_ANALYSIS เฟส 5)</summary>
    [HttpGet("reports/branches")]
    public async Task<ActionResult<ApiResponse<PosBranchSummaryResponse>>> BranchSummary(
        Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var toDate = to ?? DateTime.UtcNow;
        var fromDate = from ?? toDate;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
        return Ok(new ApiResponse<PosBranchSummaryResponse>(
            true, await _pos.GetBranchSummaryAsync(companyId, fromDate, toDate)));
    }

    /// <summary>Z-Report สิ้นกะ — สรุปยอดทั้ง session (cash variance,
    /// payment breakdown, top products). เทียบเงินในลิ้นชักก่อนปิดงาน.</summary>
    [HttpGet("sessions/{sessionId:guid}/z-report")]
    public async Task<ActionResult<ApiResponse<PosZReportResponse>>> GetZReport(Guid companyId, Guid sessionId)
        => Ok(new ApiResponse<PosZReportResponse>(true, await _pos.GetZReportAsync(companyId, sessionId)));

    /// <summary>X-Report ระหว่างกะ — ยอดวิ่งทันที ไม่ปิด session.
    /// filter ได้ตาม terminalId/from/to</summary>
    [HttpGet("x-report")]
    public async Task<ActionResult<ApiResponse<PosZReportResponse>>> GetXReport(Guid companyId,
        [FromQuery] Guid? terminalId = null, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        => Ok(new ApiResponse<PosZReportResponse>(true, await _pos.GetXReportAsync(companyId, terminalId, from, to)));

    [HttpGet("commission-summary")]
    public async Task<ActionResult<ApiResponse<List<CommissionSummaryResponse>>>> GetCommissionSummary(
        Guid companyId, [FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd)
        => Ok(new ApiResponse<List<CommissionSummaryResponse>>(true, await _pos.GetCommissionSummariesAsync(companyId, periodStart, periodEnd)));

    [HttpGet("commission-detail")]
    public async Task<ActionResult<ApiResponse<List<CommissionDetailResponse>>>> GetCommissionDetail(
        Guid companyId, [FromQuery] Guid staffId, [FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd)
        => Ok(new ApiResponse<List<CommissionDetailResponse>>(true,
            await _pos.GetCommissionDetailsAsync(companyId, staffId, periodStart, periodEnd)));
}
