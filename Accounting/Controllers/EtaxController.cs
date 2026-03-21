using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/etax")]
[Authorize]
public class EtaxController : ControllerBase
{
    private readonly IEtaxInvoiceService _etaxService;

    public EtaxController(IEtaxInvoiceService etaxService)
    {
        _etaxService = etaxService;
    }

    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Generate(
        Guid companyId, [FromBody] GenerateEtaxRequest request)
    {
        var result = await _etaxService.GenerateAsync(companyId, request);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "สร้าง e-Tax Invoice สำเร็จ"));
    }

    [HttpGet("{etaxId:guid}")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> GetById(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.GetByIdAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result));
    }

    [HttpGet("document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> GetByDocument(Guid companyId, Guid documentId)
    {
        var result = await _etaxService.GetByDocumentIdAsync(companyId, documentId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<EtaxInvoiceResponse>>>> GetAll(
        Guid companyId, [FromQuery] EtaxStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _etaxService.GetAllAsync(companyId, status, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<EtaxInvoiceResponse>>(true, result));
    }

    [HttpPost("{etaxId:guid}/sign")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Sign(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.SignAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ลงนามดิจิทัลสำเร็จ"));
    }

    [HttpPost("{etaxId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Submit(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.SubmitToRevenueAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ส่งกรมสรรพากรสำเร็จ"));
    }

    [HttpGet("{etaxId:guid}/xml")]
    public async Task<ActionResult> GetXml(Guid companyId, Guid etaxId)
    {
        var xml = await _etaxService.GetXmlAsync(companyId, etaxId);
        return Content(xml, "application/xml");
    }

    [HttpPost("{etaxId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid etaxId)
    {
        await _etaxService.VoidAsync(companyId, etaxId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }

    /// <summary>ลงนาม + ส่งสรรพากร ในขั้นตอนเดียว</summary>
    [HttpPost("{etaxId:guid}/sign-and-submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> SignAndSubmit(Guid companyId, Guid etaxId)
    {
        var signed = await _etaxService.SignAsync(companyId, etaxId);
        if (signed.Status == EtaxStatus.Signed)
        {
            var submitted = await _etaxService.SubmitToRevenueAsync(companyId, etaxId);
            return Ok(new ApiResponse<EtaxInvoiceResponse>(true, submitted, "ลงนามและส่งกรมสรรพากรสำเร็จ"));
        }
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, signed, "ลงนามสำเร็จ แต่ยังไม่ได้ส่ง"));
    }

    /// <summary>สร้าง + ลงนาม + ส่ง ในขั้นตอนเดียว (Quick Submit)</summary>
    [HttpPost("quick-submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> QuickSubmit(
        Guid companyId, [FromBody] GenerateEtaxRequest request)
    {
        var etax = await _etaxService.GenerateAsync(companyId, request);
        var signed = await _etaxService.SignAsync(companyId, etax.Id);
        if (signed.Status == EtaxStatus.Signed)
        {
            var submitted = await _etaxService.SubmitToRevenueAsync(companyId, etax.Id);
            return Ok(new ApiResponse<EtaxInvoiceResponse>(true, submitted, "สร้าง ลงนาม ส่งสรรพากรสำเร็จ"));
        }
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, signed, "สร้างและลงนามสำเร็จ"));
    }

    /// <summary>ดึงการตั้งค่า e-Tax (รวมทั้ง global config และ per-company settings)</summary>
    [HttpGet("config-status")]
    public async Task<ActionResult<ApiResponse<object>>> GetConfigStatus(Guid companyId)
    {
        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var db = HttpContext.RequestServices.GetRequiredService<Data.AccountingDbContext>();

        // Check per-company settings first
        var companySettings = await db.CompanySettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);

        var useCompanyConfig = companySettings?.EtaxEnabled == true;

        // Determine effective config
        string? certPath;
        bool hasCert, hasApiKey, isTestMode, autoSign, autoSubmit;
        string serviceProvider;

        if (useCompanyConfig)
        {
            certPath = companySettings!.EtaxCertificatePath;
            hasCert = !string.IsNullOrEmpty(certPath) && System.IO.File.Exists(certPath);
            hasApiKey = !string.IsNullOrEmpty(companySettings.EtaxRdApiKey);
            isTestMode = companySettings.EtaxTestMode;
            autoSign = companySettings.EtaxAutoSign;
            autoSubmit = companySettings.EtaxAutoSubmit;
            serviceProvider = companySettings.EtaxServiceProvider ?? "RD";
        }
        else
        {
            certPath = config["Etax:CertificatePath"];
            hasCert = !string.IsNullOrEmpty(certPath) && System.IO.File.Exists(certPath);
            hasApiKey = !string.IsNullOrEmpty(config["Etax:RdApiKey"]);
            isTestMode = config.GetValue<bool>("Etax:RdTestMode");
            autoSign = config.GetValue<bool>("Etax:AutoSign");
            autoSubmit = config.GetValue<bool>("Etax:AutoSubmit");
            serviceProvider = config["Etax:ServiceProvider"] ?? "RD";
        }

        return Ok(new ApiResponse<object>(true, new
        {
            CertificateConfigured = hasCert,
            CertificatePath = hasCert ? certPath : null,
            RdApiConfigured = hasApiKey,
            TestMode = isTestMode,
            AutoSign = autoSign,
            AutoSubmit = autoSubmit,
            ServiceProvider = serviceProvider,
            UsingCompanyConfig = useCompanyConfig,
            CompanyEtaxEnabled = companySettings?.EtaxEnabled ?? false
        }));
    }
}
