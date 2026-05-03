using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/customers")]
[Authorize]
public class CmsCustomerController : ControllerBase
{
    private readonly ICmsCustomerService _customerService;

    public CmsCustomerController(ICmsCustomerService customerService)
    {
        _customerService = customerService;
    }

    // ===== Customers =====

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SiteCustomerResponse>>> Create(
        Guid companyId, Guid siteId, [FromBody] CreateSiteCustomerRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _customerService.CreateCustomerAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<SiteCustomerResponse>(true, result, "สร้างลูกค้าสำเร็จ"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<SiteCustomerListResponse>>>> GetAll(
        Guid companyId, Guid siteId,
        [FromQuery] string? search = null, [FromQuery] string? group = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _customerService.GetCustomersAsync(companyId, siteId, search, group, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<SiteCustomerListResponse>>(true, result));
    }

    [HttpGet("{customerId:guid}")]
    public async Task<ActionResult<ApiResponse<SiteCustomerResponse>>> Get(Guid companyId, Guid siteId, Guid customerId)
    {
        var result = await _customerService.GetCustomerAsync(companyId, siteId, customerId);
        if (result == null) return NotFound(new ApiResponse<SiteCustomerResponse>(false, null, "ไม่พบลูกค้า"));
        return Ok(new ApiResponse<SiteCustomerResponse>(true, result));
    }

    [HttpPut("{customerId:guid}")]
    public async Task<ActionResult<ApiResponse<SiteCustomerResponse>>> Update(
        Guid companyId, Guid siteId, Guid customerId, [FromBody] UpdateSiteCustomerRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _customerService.UpdateCustomerAsync(companyId, siteId, customerId, request, userId);
        return Ok(new ApiResponse<SiteCustomerResponse>(true, result, "อัปเดตลูกค้าสำเร็จ"));
    }

    [HttpDelete("{customerId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid siteId, Guid customerId)
    {
        var result = await _customerService.DeleteCustomerAsync(companyId, siteId, customerId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบลูกค้า"));
        return Ok(new ApiResponse<bool>(true, true, "ลบลูกค้าสำเร็จ"));
    }

    // ===== Customer Portal Auth (public) =====

    [HttpPost("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CustomerLoginResponse>>> Login(
        Guid companyId, Guid siteId, [FromBody] CustomerLoginRequest request)
    {
        var result = await _customerService.CustomerLoginAsync(companyId, siteId, request);
        return Ok(new ApiResponse<CustomerLoginResponse>(true, result, "เข้าสู่ระบบสำเร็จ"));
    }

    [HttpPost("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/register")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteCustomerResponse>>> Register(
        Guid companyId, Guid siteId, [FromBody] CustomerRegisterRequest request)
    {
        var result = await _customerService.CustomerRegisterAsync(companyId, siteId, request);
        return StatusCode(201, new ApiResponse<SiteCustomerResponse>(true, result, "สมัครสมาชิกสำเร็จ"));
    }

    // ===== Addresses =====

    [HttpPost("{customerId:guid}/addresses")]
    public async Task<ActionResult<ApiResponse<CustomerAddressResponse>>> AddAddress(
        Guid companyId, Guid siteId, Guid customerId, [FromBody] CreateCustomerAddressRequest request)
    {
        var result = await _customerService.AddAddressAsync(companyId, siteId, customerId, request);
        return StatusCode(201, new ApiResponse<CustomerAddressResponse>(true, result, "เพิ่มที่อยู่สำเร็จ"));
    }

    [HttpGet("{customerId:guid}/addresses")]
    public async Task<ActionResult<ApiResponse<List<CustomerAddressResponse>>>> GetAddresses(
        Guid companyId, Guid siteId, Guid customerId)
    {
        var result = await _customerService.GetAddressesAsync(companyId, siteId, customerId);
        return Ok(new ApiResponse<List<CustomerAddressResponse>>(true, result));
    }

    [HttpDelete("{customerId:guid}/addresses/{addressId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteAddress(
        Guid companyId, Guid siteId, Guid customerId, Guid addressId)
    {
        var result = await _customerService.DeleteAddressAsync(companyId, siteId, customerId, addressId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบที่อยู่"));
        return Ok(new ApiResponse<bool>(true, true, "ลบที่อยู่สำเร็จ"));
    }

    // ===== PDPA / Data Privacy =====

    [HttpPut("{customerId:guid}/consent")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateConsent(
        Guid companyId, Guid siteId, Guid customerId, [FromBody] ConsentUpdateRequest request)
    {
        var result = await _customerService.UpdateConsentAsync(companyId, siteId, customerId, request);
        return Ok(new ApiResponse<bool>(true, result, "อัปเดตความยินยอมสำเร็จ"));
    }

    [HttpPost("data-deletion")]
    public async Task<ActionResult<ApiResponse<bool>>> ProcessDataDeletion(
        Guid companyId, Guid siteId, [FromBody] DataDeletionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _customerService.ProcessDataDeletionAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<bool>(true, result, "ดำเนินการลบข้อมูลสำเร็จ"));
    }

    // ===== CRM Merge (company-level, no site context needed) =====

    [HttpPost("~/api/companies/{companyId:guid}/cms/customers/merge")]
    public async Task<ActionResult<ApiResponse<bool>>> MergeCustomers(
        Guid companyId, [FromBody] MergeCustomersRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _customerService.MergeCustomersAsync(companyId, request, userId);
        return Ok(new ApiResponse<bool>(true, result, "รวมข้อมูลลูกค้าสำเร็จ"));
    }

    [HttpPost("{customerId:guid}/link-erp")]
    public async Task<ActionResult<ApiResponse<bool>>> LinkToErp(Guid companyId, Guid siteId, Guid customerId)
    {
        var result = await _customerService.AutoLinkToErpContactAsync(companyId, siteId, customerId);
        return Ok(new ApiResponse<bool>(true, result, result ? "เชื่อมต่อ ERP สำเร็จ" : "ไม่พบผู้ติดต่อ ERP ที่ตรงกัน"));
    }

    // ===== Forms =====

    [HttpPost("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms")]
    public async Task<ActionResult<ApiResponse<FormResponse>>> CreateForm(
        Guid companyId, Guid siteId, [FromBody] CreateFormRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _customerService.CreateFormAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<FormResponse>(true, result, "สร้างฟอร์มสำเร็จ"));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms")]
    public async Task<ActionResult<ApiResponse<List<FormResponse>>>> GetForms(Guid companyId, Guid siteId)
    {
        var result = await _customerService.GetFormsAsync(companyId, siteId);
        return Ok(new ApiResponse<List<FormResponse>>(true, result));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms/{formId:guid}")]
    public async Task<ActionResult<ApiResponse<FormResponse>>> GetForm(Guid companyId, Guid siteId, Guid formId)
    {
        var result = await _customerService.GetFormAsync(companyId, siteId, formId);
        if (result == null) return NotFound(new ApiResponse<FormResponse>(false, null, "ไม่พบฟอร์ม"));
        return Ok(new ApiResponse<FormResponse>(true, result));
    }

    [HttpDelete("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms/{formId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteForm(Guid companyId, Guid siteId, Guid formId)
    {
        var result = await _customerService.DeleteFormAsync(companyId, siteId, formId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบฟอร์ม"));
        return Ok(new ApiResponse<bool>(true, true, "ลบฟอร์มสำเร็จ"));
    }

    // ===== Form Submissions =====

    [HttpPost("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms/{formId:guid}/submit")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<FormSubmissionResponse>>> SubmitForm(
        Guid companyId, Guid siteId, Guid formId, [FromBody] SubmitFormRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = HttpContext.Request.Headers.UserAgent.ToString();
        var result = await _customerService.SubmitFormAsync(companyId, siteId, formId, request, ipAddress, userAgent);
        return StatusCode(201, new ApiResponse<FormSubmissionResponse>(true, result, "ส่งฟอร์มสำเร็จ"));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms/{formId:guid}/submissions")]
    public async Task<ActionResult<ApiResponse<PagedResponse<FormSubmissionResponse>>>> GetSubmissions(
        Guid companyId, Guid siteId, Guid formId,
        [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _customerService.GetSubmissionsAsync(companyId, siteId, formId, status, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<FormSubmissionResponse>>(true, result));
    }

    [HttpPut("~/api/companies/{companyId:guid}/cms/sites/{siteId:guid}/forms/{formId:guid}/submissions/{submissionId:guid}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateSubmissionStatus(
        Guid companyId, Guid siteId, Guid formId, Guid submissionId,
        [FromBody] UpdateSubmissionStatusRequest request)
    {
        var result = await _customerService.UpdateSubmissionStatusAsync(companyId, siteId, formId, submissionId, request.Status, request.Notes);
        return Ok(new ApiResponse<bool>(true, result, "อัปเดตสถานะสำเร็จ"));
    }
}
