using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 260-3 (คำตัดสิน 260-3-5) — กล่อง "ใบซื้อร่างรอตรวจ" กล่องเดียว (หน้า purchases.html) · ตัวนับ + รายการจาก <see cref="PurchaseDraftInbox.LoadAsync"/> ตัวเดียว ·
/// ผู้ดูต้องเห็นเอกสารฝั่งซื้อ (ตัวตัดสินเดียว <see cref="DocumentPermissionHelper.VisibleDirectionsAsync"/>) · ใบชั้นความลับที่ผู้ดูไม่เห็น = ไม่อยู่ในตัวนับและรายการ · tenant
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/purchase-inbox")]
[Authorize]
public class PurchaseInboxController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly ISensitivityService _sensitivity;

    public PurchaseInboxController(AccountingDbContext db, IPermissionService perms, ISensitivityService sensitivity)
    {
        _db = db; _perms = perms; _sensitivity = sensitivity;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PurchaseDraftInbox.Result>>> Get(Guid companyId, [FromQuery] int limit = PurchaseDraftInbox.DefaultLimit,
        CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, userId)).Purchase)
            return StatusCode(403, new ApiResponse<PurchaseDraftInbox.Result>(false, null!, PermissionKeys.DeniedMessage(PermissionKeys.DocumentPurchaseView)));
        var kinds = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        return Ok(new ApiResponse<PurchaseDraftInbox.Result>(true, await PurchaseDraftInbox.LoadAsync(_db, companyId, kinds, limit, ct)));
    }
}
