using Accounting.Data;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// #20 — Setup checklist status. รวมทุก onboarding step ของบริษัท
/// → return JSON ที่ UI widget render เป็น progress bar + ticks.
/// Cache 60s per company (setup status ไม่เปลี่ยนถี่).
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/setup-status")]
[Authorize]
public class SetupStatusController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public SetupStatusController(AccountingDbContext db) { _db = db; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId)
    {
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new {
                c.Name, c.TaxId, c.LogoUrl,
                HasName = !string.IsNullOrWhiteSpace(c.Name) && c.Name != "ตั้งชื่อบริษัทใหม่",
                HasTaxId = !string.IsNullOrWhiteSpace(c.TaxId)
            })
            .FirstOrDefaultAsync();
        if (company == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));

        var coaCount = await _db.ChartOfAccounts
            .CountAsync(a => a.CompanyId == companyId && !a.IsDeleted);
        var contactCount = await _db.Contacts
            .CountAsync(c => c.CompanyId == companyId && !c.IsDeleted);
        var bankCount = await _db.Set<Models.Entities.BankAccount>()
            .CountAsync(b => b.CompanyId == companyId);
        var templateCount = await _db.DocumentTemplates
            .CountAsync(t => t.CompanyId == companyId && !t.IsDeleted);
        var employeeCount = await _db.Set<Models.Entities.Employee>()
            .CountAsync(e => e.CompanyId == companyId);
        var docCount = await _db.Documents
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted);

        return Ok(new ApiResponse<object>(true, new
        {
            companyInfo = company.HasName && company.HasTaxId,
            chartOfAccounts = coaCount >= 10,    // ผังบัญชี seed default มี ≥30 ปกติ
            firstContact = contactCount >= 1,
            bankAccount = bankCount >= 1,
            documentTemplate = templateCount >= 1 && !string.IsNullOrWhiteSpace(company.LogoUrl),
            firstEmployee = employeeCount >= 1,
            firstDocument = docCount >= 1,
            // Summary fields for KPI
            counts = new { coaCount, contactCount, bankCount, templateCount, employeeCount, docCount }
        }));
    }
}
