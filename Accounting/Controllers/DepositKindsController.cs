using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Settings;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ประเภทเงินมัดจำต่อบริษัท (รอบ 194 ทีม C) — อ่าน: สมาชิกบริษัททุกคน (ฟอร์มเอกสารต้องใช้เลือกประเภท) ·
/// เขียน: สิทธิ์ <c>CompanySettings.Edit</c> ชุดเดียวกับหน้าตั้งค่าบริษัท + ปฏิเสธ API key (เปลี่ยนนโยบายภาษี = งานของคน ·
/// แบบเดียวกับ <c>SettingsController.UpdateSettings</c>) · ตัวตัดสินทั้งหมดอยู่ใน <c>DepositPolicyResolver</c>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/deposit-kinds")]
[Authorize]
public class DepositKindsController : ControllerBase
{
    private readonly IDepositKindService _service;

    public DepositKindsController(IDepositKindService service) { _service = service; }

    private string UserId => JwtHelper.GetUserIdFromClaims(User).ToString();

    [HttpGet]
    public async Task<ActionResult<ApiResponse<DepositKindListResponse>>> List(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<DepositKindListResponse>(true, await _service.ListAsync(companyId, ct)));

    [HttpPost]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าประเภทเงินมัดจำ")]
    public async Task<ActionResult<ApiResponse<DepositKindSaveResponse>>> Create(
        Guid companyId, [FromBody] SaveDepositKindRequest request, CancellationToken ct)
        => Ok(new ApiResponse<DepositKindSaveResponse>(true, await _service.CreateAsync(companyId, request, UserId, ct),
            "เพิ่มประเภทเงินมัดจำแล้ว"));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าประเภทเงินมัดจำ")]
    public async Task<ActionResult<ApiResponse<DepositKindSaveResponse>>> Update(
        Guid companyId, Guid id, [FromBody] SaveDepositKindRequest request, CancellationToken ct)
        => Ok(new ApiResponse<DepositKindSaveResponse>(true, await _service.UpdateAsync(companyId, id, request, UserId, ct),
            "บันทึกประเภทเงินมัดจำแล้ว"));

    /// <summary>ลบ (soft) — ประเภทที่มีใบ/ที่พักอ้างอยู่ ⇒ ปิดใช้แทน (ผลบอก <c>deactivated</c> + เหตุ)</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าประเภทเงินมัดจำ")]
    public async Task<ActionResult<ApiResponse<DepositKindDeleteResponse>>> Delete(Guid companyId, Guid id, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(companyId, id, UserId, ct);
        return Ok(new ApiResponse<DepositKindDeleteResponse>(true, result, result.Message));
    }

    [HttpPost("{id:guid}/default")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าประเภทเงินมัดจำ")]
    public async Task<ActionResult<ApiResponse<DepositKindSaveResponse>>> SetDefault(Guid companyId, Guid id, CancellationToken ct)
        => Ok(new ApiResponse<DepositKindSaveResponse>(true, await _service.SetDefaultAsync(companyId, id, UserId, ct),
            "ตั้งเป็นประเภทเริ่มต้นแล้ว"));
}
