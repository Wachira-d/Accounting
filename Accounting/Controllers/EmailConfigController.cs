using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/email-config")]
[Authorize]
public class EmailConfigController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentEmailService _emailService;
    private readonly ISecretProtector _secrets;

    public EmailConfigController(AccountingDbContext db, IDocumentEmailService emailService, ISecretProtector secrets)
    {
        _db = db;
        _emailService = emailService;
        _secrets = secrets;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<EmailConfigResponse>>> Get(Guid companyId)
    {
        var s = await GetOrCreateSettings(companyId);
        return Ok(new ApiResponse<EmailConfigResponse>(true, BuildResponse(s)));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<EmailConfigResponse>>> Update(
        Guid companyId, [FromBody] UpdateEmailConfigRequest req)
    {
        var s = await GetOrCreateSettings(companyId);
        s.EmailProvider = req.Provider;
        if (req.FromAddress != null) s.EmailFromAddress = req.FromAddress;
        if (req.FromName != null) s.EmailFromName = req.FromName;
        if (req.ReplyTo != null) s.EmailReplyTo = req.ReplyTo;

        if (req.Smtp != null)
        {
            if (req.Smtp.Host != null) s.EmailSmtpHost = req.Smtp.Host;
            if (req.Smtp.Port.HasValue) s.EmailSmtpPort = req.Smtp.Port.Value;
            if (req.Smtp.Username != null) s.EmailSmtpUsername = req.Smtp.Username;
            if (!string.IsNullOrEmpty(req.Smtp.Password)) s.EmailSmtpPassword = _secrets.Protect(req.Smtp.Password);
            if (req.Smtp.UseSsl.HasValue) s.EmailSmtpUseSsl = req.Smtp.UseSsl.Value;
        }
        if (req.Microsoft != null)
        {
            if (req.Microsoft.TenantId != null) s.EmailMsTenantId = req.Microsoft.TenantId;
            if (req.Microsoft.ClientId != null) s.EmailMsClientId = req.Microsoft.ClientId;
            if (!string.IsNullOrEmpty(req.Microsoft.ClientSecret)) s.EmailMsClientSecret = _secrets.Protect(req.Microsoft.ClientSecret);
            if (req.Microsoft.SenderUpn != null) s.EmailMsSenderUpn = req.Microsoft.SenderUpn;
        }
        if (req.Gmail != null)
        {
            if (req.Gmail.ClientId != null) s.EmailGmailClientId = req.Gmail.ClientId;
            if (!string.IsNullOrEmpty(req.Gmail.ClientSecret)) s.EmailGmailClientSecret = _secrets.Protect(req.Gmail.ClientSecret);
            if (!string.IsNullOrEmpty(req.Gmail.RefreshToken)) s.EmailGmailRefreshToken = _secrets.Protect(req.Gmail.RefreshToken);
        }

        s.UpdatedAt = DateTime.UtcNow;
        // Reset configured flag — must re-test after changes
        s.EmailConfigured = false;

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<EmailConfigResponse>(true, BuildResponse(s), "บันทึกการตั้งค่าเรียบร้อย"));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<EmailTestResult>>> TestSend(
        Guid companyId, [FromBody] TestEmailRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ToAddress))
            return BadRequest(new ApiResponse<EmailTestResult>(false, null, "กรุณาระบุอีเมลผู้รับ"));

        var result = await _emailService.TestEmailConfigAsync(companyId, req.ToAddress);
        return Ok(new ApiResponse<EmailTestResult>(result.Success, result,
            result.Success ? "ส่งอีเมลทดสอบสำเร็จ" : (result.ErrorMessage ?? "ส่งทดสอบไม่สำเร็จ")));
    }

    private async Task<CompanySettings> GetOrCreateSettings(Guid companyId)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
        if (s == null)
        {
            s = new CompanySettings { CompanyId = companyId };
            _db.CompanySettings.Add(s);
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private static EmailConfigResponse BuildResponse(CompanySettings s) => new(
        Provider: s.EmailProvider,
        FromAddress: s.EmailFromAddress,
        FromName: s.EmailFromName,
        ReplyTo: s.EmailReplyTo,
        Configured: s.EmailConfigured,
        LastTestedAt: s.EmailLastTestedAt,
        LastTestStatus: s.EmailLastTestStatus,
        Smtp: new SmtpConfigDto(s.EmailSmtpHost, s.EmailSmtpPort, s.EmailSmtpUsername,
            !string.IsNullOrEmpty(s.EmailSmtpPassword), s.EmailSmtpUseSsl),
        Microsoft: new MicrosoftGraphConfigDto(s.EmailMsTenantId, s.EmailMsClientId,
            !string.IsNullOrEmpty(s.EmailMsClientSecret), s.EmailMsSenderUpn),
        Gmail: new GmailConfigDto(s.EmailGmailClientId,
            !string.IsNullOrEmpty(s.EmailGmailClientSecret),
            !string.IsNullOrEmpty(s.EmailGmailRefreshToken)));
}
