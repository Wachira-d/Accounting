using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Per-company LINE Messaging API configuration. Each tenant supplies
/// their own Channel Access Token + Secret so notifications go through
/// their own LINE Official Account (separate billing, branding, opt-in).
/// Secrets are encrypted at rest via SecretProtector.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/line-config")]
[Authorize]
public class LineConfigController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ILineNotifyService _line;

    public LineConfigController(AccountingDbContext db, ISecretProtector secrets, ILineNotifyService line)
    {
        _db = db;
        _secrets = secrets;
        _line = line;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<LineConfigResponse>>> Get(Guid companyId)
    {
        var s = await GetOrCreateSettings(companyId);
        return Ok(new ApiResponse<LineConfigResponse>(true, Build(s)));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<LineConfigResponse>>> Update(
        Guid companyId, [FromBody] UpdateLineConfigRequest req)
    {
        var s = await GetOrCreateSettings(companyId);
        s.LineEnabled = req.Enabled;
        if (!string.IsNullOrEmpty(req.ChannelAccessToken))
            s.LineChannelAccessToken = _secrets.Protect(req.ChannelAccessToken);
        if (!string.IsNullOrEmpty(req.ChannelSecret))
            s.LineChannelSecret = _secrets.Protect(req.ChannelSecret);
        if (req.DefaultGroupId != null) s.LineDefaultGroupId = req.DefaultGroupId;
        if (req.OaBasicId != null) s.LineOaBasicId = string.IsNullOrWhiteSpace(req.OaBasicId) ? null : req.OaBasicId.Trim();
        s.UpdatedAt = DateTime.UtcNow;
        // Reset configured flag after change — admin must re-test
        s.LineConfigured = false;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<LineConfigResponse>(true, Build(s), "บันทึกการตั้งค่า LINE เรียบร้อย"));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<LineTestResult>>> Test(
        Guid companyId, [FromBody] TestLineConfigRequest req)
    {
        var (ok, msg) = await _line.TestConfigAsync(companyId, req.ToLineId);
        if (ok)
        {
            var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
            if (s != null)
            {
                s.LineConfigured = true;
                s.LineLastTestedAt = DateTime.UtcNow;
                s.LineLastTestStatus = "OK";
                await _db.SaveChangesAsync();
            }
        }
        else
        {
            var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
            if (s != null)
            {
                s.LineLastTestedAt = DateTime.UtcNow;
                s.LineLastTestStatus = msg;
                await _db.SaveChangesAsync();
            }
        }
        return Ok(new ApiResponse<LineTestResult>(ok, new LineTestResult(ok, msg), msg));
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

    private static LineConfigResponse Build(CompanySettings s) => new(
        Enabled: s.LineEnabled,
        HasAccessToken: !string.IsNullOrEmpty(s.LineChannelAccessToken),
        HasChannelSecret: !string.IsNullOrEmpty(s.LineChannelSecret),
        DefaultGroupId: s.LineDefaultGroupId,
        Configured: s.LineConfigured,
        LastTestedAt: s.LineLastTestedAt,
        LastTestStatus: s.LineLastTestStatus,
        OaBasicId: s.LineOaBasicId);
}
