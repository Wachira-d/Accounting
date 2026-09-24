using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Enums;
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
    private readonly IConfiguration _configuration;
    private readonly Data.AccountingDbContext _db;
    private readonly IDocumentEmailService _docEmailService;
    private readonly IPermissionService _permissions;

    public EtaxController(IEtaxInvoiceService etaxService, IConfiguration configuration,
        Data.AccountingDbContext db, IDocumentEmailService docEmailService,
        IPermissionService permissions)
    {
        _etaxService = etaxService;
        _configuration = configuration;
        _db = db;
        _docEmailService = docEmailService;
        _permissions = permissions;
    }

    /// <summary>ด่านสิทธิ์ตัวเดียวของคอนโทรลเลอร์นี้
    ///
    /// <para>⚠️ เดิมมีแค่ <c>[Authorize]</c> ระดับคลาส ซึ่งตอบแค่ "ล็อกอินอยู่ไหม"
    /// ไม่ใช่ "มีสิทธิ์ทำสิ่งนี้ไหม" ⇒ สมาชิกคนไหนของบริษัทก็ **ยื่นเอกสารต่อ
    /// กรมสรรพากรแทนบริษัทได้** ทั้งที่การยื่นที่ RD ตอบรับแล้ว **ย้อนกลับไม่ได้**
    /// (โค้ดเองเขียนไว้ใน <c>VoidAsync</c>) · และ
    /// <c>tools/write_permission_gate_check.py</c> เป็น allow-list ที่ไม่เคยมอง
    /// ไฟล์นี้ จึงรายงานเขียวตลอด — เพิ่มเข้าลิสต์แล้ว (ผลตรวจรอบ 147)</para>
    ///
    /// <para>รวมเป็นเมธอดเดียวเพราะข้อความปฏิเสธต้องบอก<b>ชื่อคีย์ที่ต้องขอ</b>
    /// ให้ตรงกันทุกจุด — 10 endpoint ที่ต่างคนต่างแต่งข้อความจะ drift แน่นอน</para>
    ///
    /// <para>Owner/SystemAdmin ผ่านอัตโนมัติ ⇒ ผู้ที่เปิดบริษัทเองไม่กระทบ</para>
    /// </summary>
    private async Task<ActionResult?> RequireEtaxAsync(Guid companyId, string permKey, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, permKey))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = permKey.Replace("perm:", ""),
        }, $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \u201c{permKey.Replace("perm:", "")}\u201d จากเจ้าของบริษัทก่อน"));
    }

    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Generate(
        Guid companyId, [FromBody] GenerateEtaxRequest request)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxIssue, "สร้าง e-Tax Invoice") is { } d) return d;
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
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxIssue, "ลงนาม e-Tax") is { } d) return d;
        var result = await _etaxService.SignAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ลงนามดิจิทัลสำเร็จ"));
    }

    [HttpPost("{etaxId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Submit(Guid companyId, Guid etaxId)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxSubmit, "นำส่ง e-Tax ต่อกรมสรรพากร") is { } d) return d;
        var result = await _etaxService.SubmitToRevenueAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ส่งกรมสรรพากรสำเร็จ"));
    }

    /// <summary>Bulk-retry e-Tax ทั้งหมดที่ Status=Failed — ใช้เมื่อ RD portal
    /// กลับมา online หลังขัดข้อง หรือแก้ certificate แล้ว. คืน count
    /// success/fail per id. ภายในวันที่ 15 ของเดือนถัดไปต้องส่งครบ
    /// (ETDA 3-2560). admin dashboard เรียกได้ทีเดียว.</summary>
    [HttpPost("retry-failed")]
    public async Task<ActionResult<ApiResponse<EtaxRetryResultDto>>> RetryFailed(Guid companyId,
        [FromQuery] int limit = 100)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxSubmit, "นำส่ง e-Tax ต่อกรมสรรพากร") is { } d) return d;
        // EtaxStatus.Error = submission ล้มเหลวเชิงเทคนิค (network/cert/RD portal)
        // ที่ retry มีโอกาสสำเร็จ. Rejected = RD ปฏิเสธเชิงธุรกิจ retry ไม่ช่วย
        var failed = await _etaxService.GetAllAsync(companyId, Models.Enums.EtaxStatus.Error,
            new PagedRequest { Page = 1, PageSize = Math.Clamp(limit, 1, 500) });
        var successes = new List<string>(); var failures = new List<EtaxRetryFailureDto>();
        foreach (var e in failed.Items)
        {
            try
            {
                await _etaxService.SubmitToRevenueAsync(companyId, e.Id);
                successes.Add(e.DocumentNumber);
            }
            catch (Exception ex)
            { failures.Add(new EtaxRetryFailureDto(e.Id, e.DocumentNumber, ex.Message)); }
        }
        return Ok(new ApiResponse<EtaxRetryResultDto>(true,
            new EtaxRetryResultDto(failed.TotalCount, successes.Count, failures.Count, successes, failures),
            $"Retry e-Tax: สำเร็จ {successes.Count}/{failed.TotalCount} ฉบับ"));
    }

    public sealed record EtaxRetryResultDto(int TotalAttempted, int SuccessCount,
        int FailureCount, List<string> SuccessDocNumbers, List<EtaxRetryFailureDto> Failures);
    public sealed record EtaxRetryFailureDto(Guid Id, string DocNumber, string ErrorMessage);

    [HttpGet("{etaxId:guid}/xml")]
    public async Task<ActionResult> GetXml(Guid companyId, Guid etaxId)
    {
        var xml = await _etaxService.GetXmlAsync(companyId, etaxId);
        return Content(xml, "application/xml");
    }

    /// <summary>
    /// Download PDF/A-3 with embedded ETDA XML (per Thai RD e-Tax by Email spec).
    /// Generates the PDF if not yet generated, persists it to disk, returns the bytes.
    /// </summary>
    [HttpGet("{etaxId:guid}/pdf")]
    public async Task<ActionResult> GetPdf(Guid companyId, Guid etaxId)
    {
        // Guard against empty/zero Guid (frontend bug or stale data) — return clean 404
        if (etaxId == Guid.Empty)
            return NotFound(new ApiResponse<object>(false, null,
                "เอกสารนี้ยังไม่มี e-Tax Invoice — กรุณาสร้าง e-Tax ก่อน (กดปุ่ม 'สร้าง e-Tax' ที่หน้าเอกสาร)"));

        var (pdf, fileName) = await _etaxService.GeneratePdfA3Async(companyId, etaxId);
        return File(pdf, "application/pdf", fileName);
    }

    /// <summary>
    /// Generate (or re-generate) the PDF/A-3 for an eTax invoice.
    /// Useful after configuration changes or as a preparation step before sending email.
    /// </summary>
    [HttpPost("{etaxId:guid}/generate-pdf")]
    public async Task<ActionResult<ApiResponse<object>>> GeneratePdf(Guid companyId, Guid etaxId)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxIssue, "ออกไฟล์ PDF/A-3 ของ e-Tax") is { } d) return d;
        var (pdf, fileName) = await _etaxService.GeneratePdfA3Async(companyId, etaxId);
        return Ok(new ApiResponse<object>(true, new
        {
            FileName = fileName,
            SizeBytes = pdf.Length,
            Format = "PDF/A-3 conformance level U",
            EmbeddedXml = true
        }, "สร้าง PDF/A-3 พร้อมฝัง XML สำเร็จ"));
    }

    [HttpPost("{etaxId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid etaxId)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxVoid, "ยกเลิก e-Tax") is { } d) return d;
        await _etaxService.VoidAsync(companyId, etaxId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }

    /// <summary>ลงนาม + ส่งสรรพากร ในขั้นตอนเดียว</summary>
    [HttpPost("{etaxId:guid}/sign-and-submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> SignAndSubmit(Guid companyId, Guid etaxId)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxSubmit, "ลงนามและนำส่ง e-Tax") is { } d) return d;
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
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxSubmit, "ออกและนำส่ง e-Tax ในขั้นตอนเดียว") is { } d) return d;
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
        // Check per-company settings first
        var companySettings = await _db.CompanySettings
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
            certPath = _configuration["Etax:CertificatePath"];
            hasCert = !string.IsNullOrEmpty(certPath) && System.IO.File.Exists(certPath);
            hasApiKey = !string.IsNullOrEmpty(_configuration["Etax:RdApiKey"]);
            isTestMode = _configuration.GetValue<bool>("Etax:RdTestMode");
            autoSign = _configuration.GetValue<bool>("Etax:AutoSign");
            autoSubmit = _configuration.GetValue<bool>("Etax:AutoSubmit");
            serviceProvider = _configuration["Etax:ServiceProvider"] ?? "RD";
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
            CompanyEtaxEnabled = companySettings?.EtaxEnabled ?? false,
            Mode = companySettings?.EtaxMode.ToString() ?? "None",
            ByEmailRdRegistered = companySettings?.EtaxByEmailRdRegistered ?? false,
            ByEmailSenderEmail = companySettings?.EtaxByEmailSenderEmail
        }));
    }

    /// <summary>ส่ง e-Tax ทางอีเมล (พร้อม CC csemail@etax.teda.th สำหรับ time-stamping)</summary>
    [HttpPost("{etaxId:guid}/send-email")]
    public async Task<ActionResult<ApiResponse<DocumentEmailLogResponse>>> SendByEmail(
        Guid companyId, Guid etaxId, [FromBody] SendEtaxByEmailRequest request)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.EtaxSubmit, "ส่ง e-Tax ทางอีเมล") is { } d) return d;
        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var log = await _docEmailService.SendEtaxByEmailAsync(companyId, etaxId, request, actor);
        var dto = MapLog(log);
        return log.Status == EmailLogStatus.Sent
            ? Ok(new ApiResponse<DocumentEmailLogResponse>(true, dto, "ส่งอีเมลสำเร็จ"))
            : Ok(new ApiResponse<DocumentEmailLogResponse>(false, dto, log.ErrorMessage ?? "ส่งอีเมลไม่สำเร็จ"));
    }

    /// <summary>ประวัติการส่งอีเมลของ e-Tax</summary>
    [HttpGet("{etaxId:guid}/email-logs")]
    public async Task<ActionResult<ApiResponse<List<DocumentEmailLogResponse>>>> GetEmailLogs(
        Guid companyId, Guid etaxId)
    {
        var logs = await _docEmailService.GetEtaxEmailLogsAsync(companyId, etaxId);
        return Ok(new ApiResponse<List<DocumentEmailLogResponse>>(true,
            logs.Select(MapLog).ToList()));
    }

    /// <summary>ดึงการตั้งค่า e-Tax mode (ByEmail / Direct / Both)</summary>
    [HttpGet("config")]
    public async Task<ActionResult<ApiResponse<EtaxConfigResponse>>> GetConfig(Guid companyId)
    {
        var s = await GetOrCreateSettings(companyId);
        return Ok(new ApiResponse<EtaxConfigResponse>(true, BuildEtaxConfigResponse(s)));
    }

    /// <summary>บันทึกการตั้งค่า e-Tax mode + RD registration</summary>
    [HttpPut("config")]
    public async Task<ActionResult<ApiResponse<EtaxConfigResponse>>> UpdateConfig(
        Guid companyId, [FromBody] UpdateEtaxConfigRequest req)
    {
        if (await RequireEtaxAsync(companyId, PermissionKeys.CompanySettingsEdit, "แก้ไขการตั้งค่า e-Tax") is { } d) return d;
        var s = await GetOrCreateSettings(companyId);
        if (req.Mode.HasValue) s.EtaxMode = req.Mode.Value;
        if (req.Enabled.HasValue) s.EtaxEnabled = req.Enabled.Value;
        if (req.TestMode.HasValue) s.EtaxTestMode = req.TestMode.Value;
        if (req.AutoSign.HasValue) s.EtaxAutoSign = req.AutoSign.Value;
        if (req.AutoSubmit.HasValue) s.EtaxAutoSubmit = req.AutoSubmit.Value;
        if (req.ByEmailRdRegistered.HasValue) s.EtaxByEmailRdRegistered = req.ByEmailRdRegistered.Value;
        if (req.ByEmailRegistrationDate.HasValue) s.EtaxByEmailRegistrationDate = req.ByEmailRegistrationDate;
        if (req.ByEmailRegistrationNumber != null) s.EtaxByEmailRegistrationNumber = req.ByEmailRegistrationNumber;
        if (req.ByEmailSenderEmail != null) s.EtaxByEmailSenderEmail = req.ByEmailSenderEmail;
        // RD timestamp address is fixed by ETDA — never allow user override
        if (req.ByEmailEmbedXml.HasValue) s.EtaxByEmailEmbedXml = req.ByEmailEmbedXml.Value;
        if (req.ByEmailAutoSendOnApprove.HasValue) s.EtaxByEmailAutoSendOnApprove = req.ByEmailAutoSendOnApprove.Value;
        if (req.ServiceProvider != null) s.EtaxServiceProvider = req.ServiceProvider;
        s.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<EtaxConfigResponse>(true, BuildEtaxConfigResponse(s), "บันทึกการตั้งค่าเรียบร้อย"));
    }

    private async Task<Models.Entities.CompanySettings> GetOrCreateSettings(Guid companyId)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
        if (s == null)
        {
            s = await CompanySettingsFactory.AddNewAsync(_db, companyId);   // seed VAT จากบริษัท (S-01)
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private static EtaxConfigResponse BuildEtaxConfigResponse(Models.Entities.CompanySettings s) => new(
        Mode: s.EtaxMode,
        Enabled: s.EtaxEnabled,
        TestMode: s.EtaxTestMode,
        AutoSign: s.EtaxAutoSign,
        AutoSubmit: s.EtaxAutoSubmit,
        ByEmailRdRegistered: s.EtaxByEmailRdRegistered,
        ByEmailRegistrationDate: s.EtaxByEmailRegistrationDate,
        ByEmailRegistrationNumber: s.EtaxByEmailRegistrationNumber,
        ByEmailSenderEmail: s.EtaxByEmailSenderEmail,
        ByEmailRdTimestampAddress: s.EtaxByEmailRdTimestampAddress,
        ByEmailEmbedXml: s.EtaxByEmailEmbedXml,
        ByEmailAutoSendOnApprove: s.EtaxByEmailAutoSendOnApprove,
        DirectCertificateInstalled: !string.IsNullOrEmpty(s.EtaxCertificatePath) && System.IO.File.Exists(s.EtaxCertificatePath),
        DirectApiCredentialsSet: !string.IsNullOrEmpty(s.EtaxRdApiKey) && !string.IsNullOrEmpty(s.EtaxRdApiSecret),
        ServiceProvider: s.EtaxServiceProvider);

    private static DocumentEmailLogResponse MapLog(Models.Entities.DocumentEmailLog l) => new(
        Id: l.Id,
        DocumentId: l.DocumentId,
        EtaxInvoiceId: l.EtaxInvoiceId,
        ToEmail: l.ToEmail,
        CcEmail: l.CcEmail,
        Subject: l.Subject,
        AttachedPdf: l.AttachedPdf,
        AttachedXml: l.AttachedXml,
        Provider: l.Provider,
        Status: l.Status,
        SentAt: l.SentAt,
        ErrorMessage: l.ErrorMessage,
        IsEtaxByEmail: l.IsEtaxByEmail,
        IncludedRdTimestamp: l.IncludedRdTimestamp,
        CreatedAt: l.CreatedAt);
}
