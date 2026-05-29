using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// CRUD + bulk upsert for ภ.พ.30 historical filings — the periods that
/// happened BEFORE the company started using this system. Lets the
/// year-to-date VAT report keep a continuous view even when the in-system
/// ledger doesn't reach back to January.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/vat-history")]
[Authorize]
public class VatFilingHistoryController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public VatFilingHistoryController(AccountingDbContext db) { _db = db; }

    public record VatHistoryDto(Guid Id, int Year, int Month,
        decimal SalesTotal, decimal OutputVat,
        decimal PurchaseTotal, decimal InputVat,
        decimal NetPayable, bool IsFiled,
        DateTime? FiledAt, string? FilingReference, string? Notes);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<VatHistoryDto>>>> List(Guid companyId, [FromQuery] int? year = null)
    {
        var q = _db.VatFilingHistories.Where(v => v.CompanyId == companyId && !v.IsDeleted);
        if (year.HasValue) q = q.Where(v => v.Year == year.Value);
        var rows = await q.OrderByDescending(v => v.Year).ThenByDescending(v => v.Month)
            .Select(v => new VatHistoryDto(v.Id, v.Year, v.Month, v.SalesTotal, v.OutputVat,
                v.PurchaseTotal, v.InputVat, v.NetPayable, v.IsFiled, v.FiledAt, v.FilingReference, v.Notes))
            .ToListAsync();
        return Ok(new ApiResponse<List<VatHistoryDto>>(true, rows));
    }

    public record UpsertVatHistoryRequest(int Year, int Month,
        decimal SalesTotal, decimal OutputVat,
        decimal PurchaseTotal, decimal InputVat,
        bool IsFiled = true, DateTime? FiledAt = null,
        string? FilingReference = null, string? Notes = null);

    /// <summary>Upsert by (Year, Month). Recompute NetPayable defensively so
    /// even rows entered with a mis-typed payable end up arithmetically correct.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<VatHistoryDto>>> Upsert(Guid companyId, [FromBody] UpsertVatHistoryRequest req)
    {
        if (req.Year < 2000 || req.Year > 2100) return BadRequest(new ApiResponse<VatHistoryDto>(false, null!, "ปีไม่ถูกต้อง"));
        if (req.Month < 1 || req.Month > 12) return BadRequest(new ApiResponse<VatHistoryDto>(false, null!, "เดือนต้อง 1-12"));

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var existing = await _db.VatFilingHistories
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.Year == req.Year && v.Month == req.Month);
        var netPayable = req.OutputVat - req.InputVat;

        if (existing != null)
        {
            existing.SalesTotal = req.SalesTotal;
            existing.OutputVat = req.OutputVat;
            existing.PurchaseTotal = req.PurchaseTotal;
            existing.InputVat = req.InputVat;
            existing.NetPayable = netPayable;
            existing.IsFiled = req.IsFiled;
            existing.FiledAt = req.FiledAt;
            existing.FilingReference = req.FilingReference;
            existing.Notes = req.Notes;
            existing.UpdatedBy = userId;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.IsDeleted = false;
        }
        else
        {
            existing = new VatFilingHistory
            {
                CompanyId = companyId, Year = req.Year, Month = req.Month,
                SalesTotal = req.SalesTotal, OutputVat = req.OutputVat,
                PurchaseTotal = req.PurchaseTotal, InputVat = req.InputVat,
                NetPayable = netPayable, IsFiled = req.IsFiled, FiledAt = req.FiledAt,
                FilingReference = req.FilingReference, Notes = req.Notes,
                CreatedBy = userId
            };
            _db.VatFilingHistories.Add(existing);
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<VatHistoryDto>(true,
            new VatHistoryDto(existing.Id, existing.Year, existing.Month,
                existing.SalesTotal, existing.OutputVat,
                existing.PurchaseTotal, existing.InputVat,
                existing.NetPayable, existing.IsFiled,
                existing.FiledAt, existing.FilingReference, existing.Notes),
            "บันทึกประวัติ ภ.พ.30 สำเร็จ"));
    }

    public record BulkUpsertRequest(List<UpsertVatHistoryRequest> Rows);

    /// <summary>Bulk path for paste-from-Excel — accepts an array, upserts each
    /// (Year, Month) row. Returns counts so the UI can confirm how many of the
    /// pasted rows were created vs updated.</summary>
    [HttpPost("bulk")]
    public async Task<ActionResult<ApiResponse<object>>> Bulk(Guid companyId, [FromBody] BulkUpsertRequest req)
    {
        if (req?.Rows == null || req.Rows.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่มีรายการ"));

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        int created = 0, updated = 0, skipped = 0;
        foreach (var r in req.Rows)
        {
            if (r.Year < 2000 || r.Year > 2100 || r.Month < 1 || r.Month > 12) { skipped++; continue; }
            var existing = await _db.VatFilingHistories
                .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.Year == r.Year && v.Month == r.Month);
            var net = r.OutputVat - r.InputVat;
            if (existing != null)
            {
                existing.SalesTotal = r.SalesTotal; existing.OutputVat = r.OutputVat;
                existing.PurchaseTotal = r.PurchaseTotal; existing.InputVat = r.InputVat;
                existing.NetPayable = net; existing.IsFiled = r.IsFiled;
                existing.FiledAt = r.FiledAt; existing.FilingReference = r.FilingReference;
                existing.Notes = r.Notes;
                existing.UpdatedBy = userId; existing.UpdatedAt = DateTime.UtcNow;
                existing.IsDeleted = false; updated++;
            }
            else
            {
                _db.VatFilingHistories.Add(new VatFilingHistory
                {
                    CompanyId = companyId, Year = r.Year, Month = r.Month,
                    SalesTotal = r.SalesTotal, OutputVat = r.OutputVat,
                    PurchaseTotal = r.PurchaseTotal, InputVat = r.InputVat,
                    NetPayable = net, IsFiled = r.IsFiled, FiledAt = r.FiledAt,
                    FilingReference = r.FilingReference, Notes = r.Notes,
                    CreatedBy = userId
                });
                created++;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true,
            new { created, updated, skipped, total = req.Rows.Count },
            $"สร้างใหม่ {created} · อัพเดต {updated} · ข้าม {skipped}"));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid companyId, Guid id)
    {
        var row = await _db.VatFilingHistories.FirstOrDefaultAsync(v => v.Id == id && v.CompanyId == companyId);
        if (row == null) return NotFound();
        row.IsDeleted = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
