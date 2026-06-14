using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Cash Forecast (พยากรณ์เงินสด 7/14/30 วัน) — SME owner ต้องการเห็นภาพ
/// "อาทิตย์หน้าเงินจะพอจ่ายเงินเดือนมั้ย" ใน 5 วินาที. รวม:
///   • Bank balance ปัจจุบัน (จาก JE บัญชี 111 cash/bank)
///   • AR ครบกำหนดในช่วง (Expected inflow)
///   • AP ครบกำหนดในช่วง (Expected outflow)
///   • PDC ที่ ScheduledDepositDate อยู่ในช่วง (inbound = inflow, outbound = outflow)
/// คืน projection breakdown per period + traffic-light alert.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cash-forecast")]
[Authorize]
public class CashForecastController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public CashForecastController(AccountingDbContext db) { _db = db; }

    public sealed record CashForecastBand(
        string Label, DateTime FromDate, DateTime ToDate,
        decimal ExpectedInflow, decimal ExpectedOutflow,
        decimal NetCashFlow, decimal ProjectedBalance);

    public sealed record CashForecastResponse(
        decimal CurrentCashBalance,
        IReadOnlyList<CashForecastBand> Bands,
        decimal TotalArDueAll, decimal TotalApDueAll,
        decimal PdcInboundHeld, decimal PdcOutboundHeld,
        bool HasNegativeProjection, string? AlertMessage);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<CashForecastResponse>>> Get(
        Guid companyId, [FromQuery] DateTime? asOfDate)
    {
        var today = (asOfDate ?? DateTime.UtcNow).Date;

        var cashBalance = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.Account != null && l.Account.AccountCode.StartsWith("111")
                && l.JournalEntry.EntryDate <= today)
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        var openStates = new[] {
            DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue
        };
        var arTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote
        };
        var apTypes = new[] {
            DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.DebitNote
        };

        var openAr = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && openStates.Contains(d.Status)
                && arTypes.Contains(d.DocumentType)
                && d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue })
            .ToListAsync();

        var openAp = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && openStates.Contains(d.Status)
                && apTypes.Contains(d.DocumentType)
                && d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue })
            .ToListAsync();

        var heldPdcs = await _db.PostDatedChecks.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.Status == PdcStatus.Held)
            .Select(p => new { p.Direction, p.ScheduledDepositDate, p.Amount })
            .ToListAsync();

        var ranges = new[] {
            ("เลยกำหนด", DateTime.MinValue, today.AddDays(-1)),
            ("ภายใน 7 วัน", today, today.AddDays(7)),
            ("8-14 วัน", today.AddDays(8), today.AddDays(14)),
            ("15-30 วัน", today.AddDays(15), today.AddDays(30)),
            ("31-60 วัน", today.AddDays(31), today.AddDays(60)),
        };

        decimal projectedBalance = cashBalance;
        var bands = new List<CashForecastBand>();
        foreach (var (label, from, to) in ranges)
        {
            var inflow = openAr.Where(a => DueOrDoc(a.DueDate, a.DocumentDate) >= from && DueOrDoc(a.DueDate, a.DocumentDate) <= to).Sum(a => a.BalanceDue)
                       + heldPdcs.Where(p => p.Direction == PdcDirection.Inbound && p.ScheduledDepositDate >= from && p.ScheduledDepositDate <= to).Sum(p => p.Amount);
            var outflow = openAp.Where(a => DueOrDoc(a.DueDate, a.DocumentDate) >= from && DueOrDoc(a.DueDate, a.DocumentDate) <= to).Sum(a => a.BalanceDue)
                        + heldPdcs.Where(p => p.Direction == PdcDirection.Outbound && p.ScheduledDepositDate >= from && p.ScheduledDepositDate <= to).Sum(p => p.Amount);
            var net = inflow - outflow;
            projectedBalance += net;
            bands.Add(new CashForecastBand(label, from, to, inflow, outflow, net, projectedBalance));
        }

        var firstNegBand = bands.FirstOrDefault(b => b.ProjectedBalance < 0);
        return Ok(new ApiResponse<CashForecastResponse>(true, new CashForecastResponse(
            CurrentCashBalance: cashBalance,
            Bands: bands,
            TotalArDueAll: openAr.Sum(a => a.BalanceDue),
            TotalApDueAll: openAp.Sum(a => a.BalanceDue),
            PdcInboundHeld: heldPdcs.Where(p => p.Direction == PdcDirection.Inbound).Sum(p => p.Amount),
            PdcOutboundHeld: heldPdcs.Where(p => p.Direction == PdcDirection.Outbound).Sum(p => p.Amount),
            HasNegativeProjection: firstNegBand != null,
            AlertMessage: firstNegBand == null ? null
                : $"⚠️ Cash gap คาดว่าจะเกิดในช่วง \"{firstNegBand.Label}\" — projected balance {firstNegBand.ProjectedBalance:N2}. แนะนำ: เร่งเก็บ AR หรือเลื่อน AP ที่ไม่เร่งด่วน"
        )));

        static DateTime DueOrDoc(DateTime? due, DateTime doc) => due ?? doc;
    }
}
