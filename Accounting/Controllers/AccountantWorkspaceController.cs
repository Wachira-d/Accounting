using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>มุมมองสำนักงานบัญชี (multi-client workspace) — นักบัญชี 1 คน
/// ดูงานคงค้างของ "ทุกบริษัทที่ตัวเองเป็นสมาชิก" ในหน้าเดียว: เอกสารร่าง
/// ค้างอนุมัติ, รายการธนาคารรอ match, สถานะยื่น ภ.พ.30 เดือนก่อน,
/// เอกสารเกินกำหนดชำระ — จุดขายระดับ PEAK สำหรับสำนักงานบัญชี.
/// สิทธิ์: เห็นเฉพาะบริษัทที่มี CompanyUser membership เท่านั้น.</summary>
[ApiController]
[Route("api/accountant-workspace")]
[Authorize]
public class AccountantWorkspaceController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public AccountantWorkspaceController(AccountingDbContext db) { _db = db; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetWorkspace()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);

        var companyIds = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.UserId == userId)
            .Select(cu => cu.CompanyId)
            .Distinct()
            .ToListAsync();
        if (companyIds.Count == 0)
            return Ok(new ApiResponse<object>(true, new { clients = new List<object>() }));

        var companies = await _db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Name, c.TaxId })
            .ToListAsync();

        // เดือนภาษีล่าสุดที่ครบกำหนดยื่น = เดือนก่อนหน้า
        var now = DateTime.UtcNow.AddHours(7);   // มุมมองเวลาไทย
        var prevMonth = now.AddMonths(-1);

        // ==== aggregate ครั้งเดียวต่อมิติ (ไม่ N+1 ต่อบริษัท) ====
        var draftCounts = await _db.Documents.AsNoTracking()
            .Where(d => companyIds.Contains(d.CompanyId) && !d.IsDeleted
                && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval))
            .GroupBy(d => d.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync();

        var overdueCounts = await _db.Documents.AsNoTracking()
            .Where(d => companyIds.Contains(d.CompanyId) && !d.IsDeleted
                && d.BalanceDue > 0 && d.DueDate != null && d.DueDate < DateTime.UtcNow
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Sent
                    || d.Status == DocumentStatus.PartiallyPaid || d.Status == DocumentStatus.Overdue))
            .GroupBy(d => d.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count(), Amount = g.Sum(x => x.BalanceDue) })
            .ToListAsync();

        var unmatchedBank = await _db.Set<Models.Entities.BankTransaction>().AsNoTracking()
            .Where(t => companyIds.Contains(t.CompanyId) && !t.IsDeleted
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.TransactionDate >= DateTime.UtcNow.AddDays(-45))
            .GroupBy(t => t.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync();

        var vatFilings = await _db.TaxReports.AsNoTracking()
            .Where(r => companyIds.Contains(r.CompanyId) && !r.IsDeleted
                && r.TaxType == TaxType.VAT
                && r.Year == prevMonth.Year && r.Month == prevMonth.Month)
            .Select(r => new { r.CompanyId, r.Status, r.FiledDate, r.NetVat })
            .ToListAsync();

        var draftBy = draftCounts.ToDictionary(x => x.CompanyId);
        var overdueBy = overdueCounts.ToDictionary(x => x.CompanyId);
        var bankBy = unmatchedBank.ToDictionary(x => x.CompanyId);
        var vatBy = vatFilings
            .GroupBy(x => x.CompanyId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.FiledDate).First());

        var clients = companies.Select(c =>
        {
            var vat = vatBy.GetValueOrDefault(c.Id);
            return new
            {
                companyId = c.Id,
                name = c.Name,
                taxId = c.TaxId,
                draftPending = draftBy.GetValueOrDefault(c.Id)?.Count ?? 0,
                overdueCount = overdueBy.GetValueOrDefault(c.Id)?.Count ?? 0,
                overdueAmount = overdueBy.GetValueOrDefault(c.Id)?.Amount ?? 0m,
                bankUnmatched = bankBy.GetValueOrDefault(c.Id)?.Count ?? 0,
                vatMonth = $"{prevMonth:MM/yyyy}",
                vatStatus = vat == null ? "NotStarted" : vat.Status.ToString(),
                vatNet = vat?.NetVat,
                vatFiledDate = vat?.FiledDate,
            };
        })
        .OrderByDescending(c => c.draftPending + c.bankUnmatched + (c.vatStatus == "NotStarted" ? 5 : 0))
        .ToList();

        return Ok(new ApiResponse<object>(true, new
        {
            asOf = DateTime.UtcNow,
            vatDeadline = new DateTime(now.Year, now.Month, Math.Min(15, DateTime.DaysInMonth(now.Year, now.Month))),
            clients,
        }));
    }
}
