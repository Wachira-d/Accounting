using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Webhook;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/webhooks")]
[Authorize]
public class WebhookController : ControllerBase
{
    private readonly IWebhookService _service;
    private readonly ICompanyService _companyService;
    public WebhookController(IWebhookService service, ICompanyService companyService)
    {
        _service = service;
        _companyService = companyService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<WebhookRegistrationResponse>>> Register(Guid companyId, [FromBody] CreateWebhookRequest request)
    {
        // Webhook registration receives every financial event the company emits —
        // restrict to Owner so a curious staff member can't quietly forward billing.
        await _companyService.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        return StatusCode(201, new ApiResponse<WebhookRegistrationResponse>(true, await _service.RegisterAsync(companyId, request)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<WebhookRegistrationResponse>>>> GetAll(Guid companyId)
        => Ok(new ApiResponse<List<WebhookRegistrationResponse>>(true, await _service.GetRegistrationsAsync(companyId)));

    [HttpPut("{webhookId:guid}")]
    public async Task<ActionResult<ApiResponse<WebhookRegistrationResponse>>> Update(Guid companyId, Guid webhookId, [FromBody] UpdateWebhookRequest request)
    {
        await _companyService.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<WebhookRegistrationResponse>(true, await _service.UpdateAsync(companyId, webhookId, request)));
    }

    [HttpDelete("{webhookId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid webhookId)
    {
        await _companyService.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        await _service.DeleteAsync(companyId, webhookId);
        return NoContent();
    }

    // รอบ 193 (ฝ่ายค้าน C1 · เพิ่ม WebhookController เข้า write_permission_gate_check): ทดสอบ/ส่งซ้ำ = ยิงข้อมูลออกไป
    // ปลายทางที่เจ้าของตั้ง — ใช้ด่านเดียวกับการลงทะเบียน (เดิมสมาชิกคนไหน/คีย์ไหนก็สั่งส่งซ้ำเหตุการณ์การเงินได้)
    [HttpPost("{webhookId:guid}/test")]
    public async Task<ActionResult<ApiResponse<WebhookRegistrationResponse>>> Test(Guid companyId, Guid webhookId)
    {
        await _companyService.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<WebhookRegistrationResponse>(true, await _service.TestAsync(companyId, webhookId)));
    }

    [HttpGet("{webhookId:guid}/deliveries")]
    public async Task<ActionResult<ApiResponse<PagedResponse<WebhookDeliveryResponse>>>> GetDeliveries(Guid companyId, Guid webhookId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<WebhookDeliveryResponse>>(true, await _service.GetDeliveriesAsync(companyId, webhookId, new PagedRequest(page, pageSize))));

    [HttpPost("deliveries/{deliveryId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<bool>>> Retry(Guid companyId, Guid deliveryId)
    {
        await _companyService.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        await _service.RetryAsync(companyId, deliveryId);
        return Ok(new ApiResponse<bool>(true, true));
    }

    [HttpGet("event-types")]
    public async Task<ActionResult<ApiResponse<List<WebhookEventTypeResponse>>>> GetEventTypes()
        => Ok(new ApiResponse<List<WebhookEventTypeResponse>>(true, await _service.GetEventTypesAsync()));
}
