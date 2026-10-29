using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 209 P3 — หลังบ้านออเดอร์หน้าเว็บ: คิวผลิต (A5) + บันทึกรับเงินหน้าร้าน/ปลายทาง (E7) ·
/// แยกคอนโทรลเลอร์จาก <c>CmsCommerceController</c> (route prefix เดียวกัน) · ทุก write มีด่านสิทธิ์บริษัท (<c>write_permission_gate_check</c>)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/orders")]
[Authorize]
public class CmsOrderFulfilmentController : ControllerBase
{
    private readonly ICmsCommerceService _commerceService;

    public CmsOrderFulfilmentController(ICmsCommerceService commerceService) => _commerceService = commerceService;

    /// <summary>คิวผลิต — ออเดอร์ที่ยังไม่ส่งมอบ เรียงตามวันพร้อม · ค่าเริ่มต้น วันนี้ถึง +7 วัน (+ ของค้างเลยกำหนด)</summary>
    [HttpGet("production-queue")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderListResponse>>>> GetProductionQueue(Guid companyId, Guid siteId,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _commerceService.GetProductionQueueAsync(companyId, siteId, from, to, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<OrderListResponse>>(true, result));
    }

    /// <summary>บันทึกรับเงินหน้าร้าน/ปลายทาง — สร้างรายการรับเงินแล้วเดินเส้นยืนยันรับเงินเดิม (เอกสาร/JE/ตัดสต็อกตามโหมดของออเดอร์)</summary>
    [HttpPost("{orderId:guid}/counter-payment")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]   // รับเงิน = ลงบัญชี ⇒ ด่านสิทธิ์ระดับบริษัทด้วย
    public async Task<ActionResult<ApiResponse<OrderResponse>>> RecordCounterPayment(Guid companyId, Guid siteId, Guid orderId,
        [FromBody] RecordCounterPaymentRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _commerceService.RecordCounterPaymentAsync(companyId, siteId, orderId, request ?? new RecordCounterPaymentRequest(), userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "บันทึกรับเงินแล้ว — ระบบออกเอกสาร/ลงบัญชีตามวิธีของออเดอร์"));
    }
}
