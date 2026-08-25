using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>รายงานการใช้งานรายบริษัท (ฝั่งแอดมินแพลตฟอร์ม) — ตอบคำถาม
/// "เดือนนี้แต่ละบริษัทออกเอกสารอะไรไปเท่าไหร่ ใช้ OCR/AI/อีเมล/e-Tax แค่ไหน"
/// ในหน้าเดียว. ตัวเลข AI มาจาก rollup AiUsageDailyTenant ชุดเดียวกับหน้า
/// "รายงานการใช้งาน AI" (แหล่งเดียวกัน — ยอดต้องตรงกัน) ส่วนเอกสาร/OCR/อีเมล
/// นับจากตารางจริงตามเดือนที่ **สร้าง** (มิเตอร์การใช้งาน — ไม่ใช่เดือนภาษี
/// ของเอกสาร ซึ่งเป็นคนละมุมกับรายงานบัญชี).</summary>
[ApiController]
[Route("api/admin/company-usage")]
[Authorize(Roles = "SystemAdmin")]
public class AdminCompanyUsageController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminCompanyUsageController(AccountingDbContext db) => _db = db;

    public record DocTypeCount(string Type, int Count, int Approved, decimal Amount);

    public record CompanyUsageRow(
        Guid CompanyId,
        string CompanyName,
        string? TaxId,
        // ── เอกสาร (สร้างในเดือนนั้น ไม่นับที่ถูกลบ) ──
        int DocumentsTotal,
        int DocumentsApproved,          // อนุมัติแล้ว/เดินต่อจากอนุมัติ (มีเลขจริง)
        decimal DocumentsAmount,        // รวม TotalAmount ของใบที่ไม่ใช่ร่าง/ปฏิเสธ
        List<DocTypeCount> DocumentsByType,
        // ── ฟีเจอร์อื่น ──
        int OcrScans, int OcrCompleted,
        int AiCallsTotal,               // เรียก orchestrator ทั้งหมด (รวม local ตอบเอง)
        int AiCallsPaid,                // ยิงถึง provider จริง = เสียเงิน
        decimal AiCostUsd,
        int EmailsSent,
        int EtaxIssued);

    public record CompanyUsageResponse(
        int Year, int Month,
        int CompanyCount,
        int DocumentsTotal, int OcrScans, int AiCallsPaid, decimal AiCostUsd,
        List<CompanyUsageRow> Companies);

    /// <summary>สรุปการใช้งานทุกบริษัทของเดือนที่เลือก — เรียงบริษัทที่ใช้งาน
    /// มากสุดก่อน. บริษัทที่ไม่มีการใช้งานเลยในเดือนนั้นไม่ขึ้น (ลด noise —
    /// จำนวนบริษัทั้งหมดดูได้จากหน้า Companies เดิม).</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<CompanyUsageResponse>>> Get(
        [FromQuery] int year, [FromQuery] int month)
    {
        if (year < 2020 || year > DateTime.UtcNow.Year + 1 || month < 1 || month > 12)
            return BadRequest(new ApiResponse<CompanyUsageResponse>(false, null,
                "งวดไม่ถูกต้อง — ปี ค.ศ. 2020..ปีหน้า, เดือน 1-12"));

        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);

        // ── เอกสารต่อบริษัทต่อชนิด — query เดียว group ที่ DB ──
        // สถานะ "มีเลขจริงแล้ว" = ทุกอย่างที่ผ่านอนุมัติ (Approved/Sent/Paid/
        // PartiallyPaid/Overdue/Voided — Voided ก็เคยออกเลขจริง)
        var docGroups = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.CreatedAt >= from && d.CreatedAt < to)
            .GroupBy(d => new { d.CompanyId, d.DocumentType })
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.DocumentType,
                Count = g.Count(),
                Approved = g.Count(d => d.Status != Models.Enums.DocumentStatus.Draft
                    && d.Status != Models.Enums.DocumentStatus.WaitingApproval
                    && d.Status != Models.Enums.DocumentStatus.Rejected),
                Amount = g.Where(d => d.Status != Models.Enums.DocumentStatus.Draft
                        && d.Status != Models.Enums.DocumentStatus.WaitingApproval
                        && d.Status != Models.Enums.DocumentStatus.Rejected
                        && d.Status != Models.Enums.DocumentStatus.Voided)
                    .Sum(d => (decimal?)d.TotalAmount) ?? 0m,
            })
            .ToListAsync();

        var ocr = await _db.OcrScanResults.AsNoTracking()
            .Where(o => !o.IsDeleted && o.CreatedAt >= from && o.CreatedAt < to)
            .GroupBy(o => o.CompanyId)
            .Select(g => new
            {
                CompanyId = g.Key,
                Total = g.Count(),
                Completed = g.Count(o => o.ScanStatus == "Completed"),
            })
            .ToListAsync();

        var ai = await _db.AiUsageDailyTenants.AsNoTracking()
            .Where(u => u.UsageDate >= from && u.UsageDate < to && !u.IsSandbox)
            .GroupBy(u => u.CompanyId)
            .Select(g => new
            {
                CompanyId = g.Key,
                CallsTotal = g.Sum(u => u.CallsTotal),
                CallsAi = g.Sum(u => u.CallsAi),
                CostUsd = g.Sum(u => u.CostUsdTotal),
            })
            .ToListAsync();

        var emails = await _db.EmailQueues.AsNoTracking()
            .Where(q => !q.IsDeleted && q.Status == "Sent"
                && q.SentAt >= from && q.SentAt < to)
            .GroupBy(q => q.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync();

        var etax = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => !e.IsDeleted && e.CreatedAt >= from && e.CreatedAt < to)
            .GroupBy(e => e.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync();

        // ── รวมทุกแหล่งเป็นแถวต่อบริษัท ──
        var companyIds = docGroups.Select(d => d.CompanyId)
            .Concat(ocr.Select(o => o.CompanyId))
            .Concat(ai.Select(a => a.CompanyId))
            .Concat(emails.Select(e => e.CompanyId))
            .Concat(etax.Select(e => e.CompanyId))
            .Distinct().ToList();

        var companies = await _db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId })
            .ToDictionaryAsync(c => c.Id);

        var ocrMap = ocr.ToDictionary(x => x.CompanyId);
        var aiMap = ai.ToDictionary(x => x.CompanyId);
        var emailMap = emails.ToDictionary(x => x.CompanyId, x => x.Count);
        var etaxMap = etax.ToDictionary(x => x.CompanyId, x => x.Count);
        var docMap = docGroups.GroupBy(d => d.CompanyId).ToDictionary(g => g.Key, g => g.ToList());

        var rows = companyIds.Select(cid =>
        {
            var docs = docMap.TryGetValue(cid, out var dl) ? dl : new();
            var byType = docs
                .OrderByDescending(d => d.Count)
                .Select(d => new DocTypeCount(d.DocumentType.ToString(), d.Count, d.Approved, d.Amount))
                .ToList();
            var o = ocrMap.TryGetValue(cid, out var ov) ? ov : null;
            var a = aiMap.TryGetValue(cid, out var av) ? av : null;
            return new CompanyUsageRow(
                cid,
                companies.TryGetValue(cid, out var c) ? c.Name : "(บริษัทถูกลบ)",
                companies.TryGetValue(cid, out var c2) ? c2.TaxId : null,
                docs.Sum(d => d.Count),
                docs.Sum(d => d.Approved),
                docs.Sum(d => d.Amount),
                byType,
                o?.Total ?? 0, o?.Completed ?? 0,
                a?.CallsTotal ?? 0, a?.CallsAi ?? 0, a?.CostUsd ?? 0m,
                emailMap.TryGetValue(cid, out var em) ? em : 0,
                etaxMap.TryGetValue(cid, out var et) ? et : 0);
        })
        .OrderByDescending(r => r.DocumentsTotal)
        .ThenByDescending(r => r.OcrScans)
        .ToList();

        return Ok(new ApiResponse<CompanyUsageResponse>(true, new CompanyUsageResponse(
            year, month, rows.Count,
            rows.Sum(r => r.DocumentsTotal), rows.Sum(r => r.OcrScans),
            rows.Sum(r => r.AiCallsPaid), rows.Sum(r => r.AiCostUsd),
            rows)));
    }
}
