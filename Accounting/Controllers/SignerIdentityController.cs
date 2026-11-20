using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 278 — กุญแจผูกช่องลายเซ็นผู้จัดทำ ↔ ผู้มีอำนาจลงนาม (ตรรกะทั้งหมดใน <see cref="SignerIdentityService"/> · กติกาใน <c>Helpers/SignatureSlotIdentity</c>) ·
/// ชื่อเล่น/สิทธิ์เป็นของตัวผู้ใช้เอง ⇒ API key ใช้ไม่ได้ (คีย์ของคนอื่นห้ามตั้งชื่อเล่นแทน) · อนุมัติ/ปรับผู้ลงนาม = เจ้าของบริษัท
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/signer-identity")]
[Authorize]
[RejectApiKey]
public class SignerIdentityController : ControllerBase
{
    private readonly SignerIdentityService _svc;
    public SignerIdentityController(SignerIdentityService svc) { _svc = svc; }

    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<SignerIdentityService.MySignerIdentity>>> Mine(Guid companyId)
        => Ok(new ApiResponse<SignerIdentityService.MySignerIdentity>(true, await _svc.GetMineAsync(companyId, JwtHelper.GetUserIdFromClaims(User))));

    [HttpPut("me")]
    public async Task<ActionResult<ApiResponse<SignerIdentityService.MySignerIdentity>>> SaveMine(Guid companyId, [FromBody] SignerIdentityService.SaveMyRequest req)
        => Ok(new ApiResponse<SignerIdentityService.MySignerIdentity>(true, await _svc.SaveMineAsync(companyId, JwtHelper.GetUserIdFromClaims(User), req),
            "บันทึกแล้ว — ชื่อเล่นใหม่มีผลเมื่อเจ้าของบริษัทอนุมัติ"));

    [HttpPost("aliases/{aliasId:guid}/approve")]
    [RequireOwner("อนุมัติชื่อเล่นสำหรับช่องลายเซ็น")]
    public async Task<ActionResult<ApiResponse<List<SignerIdentityService.PendingAliasItem>>>> Approve(Guid companyId, Guid aliasId)
        => Ok(new ApiResponse<List<SignerIdentityService.PendingAliasItem>>(true, await _svc.DecideAsync(companyId, JwtHelper.GetUserIdFromClaims(User), aliasId, true), "อนุมัติแล้ว"));

    [HttpPost("aliases/{aliasId:guid}/reject")]
    [RequireOwner("ปฏิเสธชื่อเล่นสำหรับช่องลายเซ็น")]
    public async Task<ActionResult<ApiResponse<List<SignerIdentityService.PendingAliasItem>>>> Reject(Guid companyId, Guid aliasId)
        => Ok(new ApiResponse<List<SignerIdentityService.PendingAliasItem>>(true, await _svc.DecideAsync(companyId, JwtHelper.GetUserIdFromClaims(User), aliasId, false), "ปฏิเสธแล้ว"));

    public sealed record RefreshRequest(string? Reason);

    [HttpPost("documents/{documentId:guid}/refresh")]
    [RequireOwner("ปรับผู้ลงนามตามข้อมูลปัจจุบัน")]
    public async Task<ActionResult<ApiResponse<SignerIdentityService.RefreshResult>>> Refresh(Guid companyId, Guid documentId, [FromBody] RefreshRequest req)
        => Ok(new ApiResponse<SignerIdentityService.RefreshResult>(true,
            await _svc.RefreshDocumentSignersAsync(companyId, JwtHelper.GetUserIdFromClaims(User), documentId, req?.Reason), "ปรับผู้ลงนามแล้ว"));
}
