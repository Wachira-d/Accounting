using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Customer / Vendor Statement of Account (ใบแจ้งยอด) — ปลายเดือนทุกบริษัท
/// SME ส่งให้ลูกค้าสรุปยอด: ค้างยกมา + ใบแจ้งหนี้/ลดหนี้/รับชำระเดือนนี้ +
/// ยอดค้างยกไป. รองรับ CSV export + auto-email ผ่าน EmailScheduleService
/// (Trigger: MonthlyStatement ที่ commit ก่อนหน้าได้รับการ wire ไว้).
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/statements")]
[Authorize]
public class CustomerStatementController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public CustomerStatementController(AccountingDbContext db) { _db = db; }

    public sealed record StatementLine(
        Guid DocumentId, string DocumentNumber, DocumentType Type,
        DateTime Date, DateTime? DueDate, decimal Debit, decimal Credit, decimal RunningBalance,
        string Description);

    public sealed record ContactStatement(
        Guid ContactId, string ContactName, string? TaxId, string? Address,
        DateTime FromDate, DateTime ToDate,
        decimal OpeningBalance, decimal ClosingBalance,
        decimal TotalDebit, decimal TotalCredit,
        // Aging snapshot
        decimal Current, decimal D1to30, decimal D31to60, decimal D61to90, decimal Over90,
        IReadOnlyList<StatementLine> Lines);

    /// <summary>คืนใบแจ้งยอดของ contact ใน period. AR side ใช้ DocumentTypes
    /// ฝั่งขาย (Invoice/TaxInvoice/CreditNote/Receipt). AP side ใช้ฝั่งซื้อ.
    /// Side: "AR" | "AP".</summary>
    [HttpGet("contact/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactStatement>>> GetContactStatement(
        Guid companyId, Guid contactId,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        [FromQuery] string side = "AR")
    {
        var contact = await _db.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId);
        if (contact == null)
            return NotFound(new ApiResponse<ContactStatement>(false, null!, "ไม่พบผู้ติดต่อ"));

        // ฝั่งขาย = Invoice/TaxInvoice/BillingNote เพิ่ม AR (debit lookup),
        // CreditNote ลด, Receipt ตัด AR (credit lookup)
        // ฝั่งซื้อ = PI/Expense เพิ่ม AP, CN ลด, PV ตัด AP
        DocumentType[] debitTypes = side == "AP"
            ? new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.DebitNote }
            : new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote };
        DocumentType[] creditTypes = side == "AP"
            ? new[] { DocumentType.CreditNote, DocumentType.PaymentVoucher }
            : new[] { DocumentType.CreditNote, DocumentType.Receipt, DocumentType.ReceiptVoucher };
        var allTypes = debitTypes.Concat(creditTypes).Distinct().ToArray();
        var openStates = new[] {
            DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid,
            DocumentStatus.Paid, DocumentStatus.Overdue
        };

        // Opening balance = ยอด NET ของ docs ที่ DocumentDate < fromDate
        var openingDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && !d.IsDeleted && openStates.Contains(d.Status)
                && allTypes.Contains(d.DocumentType)
                && d.DocumentDate < fromDate)
            .Select(d => new { d.DocumentType, d.TotalAmount })
            .ToListAsync();
        var openingBalance = openingDocs.Sum(o => debitTypes.Contains(o.DocumentType) ? o.TotalAmount : -o.TotalAmount);

        // Period docs
        var periodDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && !d.IsDeleted && openStates.Contains(d.Status)
                && allTypes.Contains(d.DocumentType)
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .OrderBy(d => d.DocumentDate)
            .Select(d => new {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.DueDate, d.TotalAmount,
                d.RelatedDocumentId
            })
            .ToListAsync();

        var lines = new List<StatementLine>();
        decimal running = openingBalance;
        decimal totalDebit = 0, totalCredit = 0;
        foreach (var d in periodDocs)
        {
            var isDebit = debitTypes.Contains(d.DocumentType);
            var debit = isDebit ? d.TotalAmount : 0;
            var credit = isDebit ? 0 : d.TotalAmount;
            running += debit - credit;
            totalDebit += debit;
            totalCredit += credit;
            lines.Add(new StatementLine(
                d.Id, d.DocumentNumber, d.DocumentType,
                d.DocumentDate, d.DueDate, debit, credit, running,
                Description: DescribeDoc(d.DocumentType)));
        }

        // Aging snapshot ของยอดค้าง (ยอดที่ยัง BalanceDue > 0 ณ toDate)
        var openDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && !d.IsDeleted && openStates.Contains(d.Status)
                && debitTypes.Contains(d.DocumentType)
                && d.BalanceDue > 0 && d.DocumentDate <= toDate)
            .Select(d => new { d.DocumentDate, d.DueDate, d.BalanceDue })
            .ToListAsync();
        decimal cur = 0, d30 = 0, d60 = 0, d90 = 0, over90 = 0;
        foreach (var od in openDocs)
        {
            var due = od.DueDate ?? od.DocumentDate;
            var days = Math.Max(0, (int)(toDate - due).TotalDays);
            if (days <= 0) cur += od.BalanceDue;
            else if (days <= 30) d30 += od.BalanceDue;
            else if (days <= 60) d60 += od.BalanceDue;
            else if (days <= 90) d90 += od.BalanceDue;
            else over90 += od.BalanceDue;
        }

        var statement = new ContactStatement(
            ContactId: contact.Id,
            ContactName: contact.Name,
            TaxId: contact.TaxId,
            Address: contact.Address,
            FromDate: fromDate, ToDate: toDate,
            OpeningBalance: openingBalance,
            ClosingBalance: running,
            TotalDebit: totalDebit, TotalCredit: totalCredit,
            Current: cur, D1to30: d30, D31to60: d60, D61to90: d90, Over90: over90,
            Lines: lines);

        return Ok(new ApiResponse<ContactStatement>(true, statement));
    }

    /// <summary>Export CSV ของใบแจ้งยอด — เปิดใน Excel ได้เลย (UTF-8 BOM).</summary>
    [HttpGet("contact/{contactId:guid}/export")]
    public async Task<ActionResult> ExportContactStatement(
        Guid companyId, Guid contactId,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        [FromQuery] string side = "AR")
    {
        var r = await GetContactStatement(companyId, contactId, fromDate, toDate, side);
        var stmt = (r.Result as OkObjectResult)?.Value as ApiResponse<ContactStatement>;
        if (stmt?.Data == null) return NotFound();

        var sb = new System.Text.StringBuilder();
        sb.Append('﻿');  // UTF-8 BOM
        sb.AppendLine($"ใบแจ้งยอด — {(side == "AP" ? "เจ้าหนี้" : "ลูกหนี้")}");
        sb.AppendLine($"ผู้ติดต่อ,{Esc(stmt.Data.ContactName)}");
        if (!string.IsNullOrWhiteSpace(stmt.Data.TaxId))
            sb.AppendLine($"เลขผู้เสียภาษี,{stmt.Data.TaxId}");
        sb.AppendLine($"ช่วงเวลา,{fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("วันที่,เลขที่,ประเภท,รายละเอียด,ครบกำหนด,เดบิต,เครดิต,ยอดคงค้าง");
        sb.AppendLine($"01/01,—,—,ยอดยกมา,—,—,—,{stmt.Data.OpeningBalance:N2}");
        foreach (var l in stmt.Data.Lines)
            sb.AppendLine($"{l.Date:yyyy-MM-dd},{Esc(l.DocumentNumber)},{l.Type},{Esc(l.Description)},{l.DueDate:yyyy-MM-dd},{l.Debit:N2},{l.Credit:N2},{l.RunningBalance:N2}");
        sb.AppendLine($",,,,รวม,{stmt.Data.TotalDebit:N2},{stmt.Data.TotalCredit:N2},{stmt.Data.ClosingBalance:N2}");
        sb.AppendLine();
        sb.AppendLine("Aging Snapshot,Amount");
        sb.AppendLine($"ปัจจุบัน,{stmt.Data.Current:N2}");
        sb.AppendLine($"1-30 วัน,{stmt.Data.D1to30:N2}");
        sb.AppendLine($"31-60 วัน,{stmt.Data.D31to60:N2}");
        sb.AppendLine($"61-90 วัน,{stmt.Data.D61to90:N2}");
        sb.AppendLine($">90 วัน,{stmt.Data.Over90:N2}");

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"Statement_{stmt.Data.ContactName}_{toDate:yyyyMMdd}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private static string Esc(string? s) => s == null ? "" : (s.Contains(',') || s.Contains('"') || s.Contains('\n')
        ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);

    private static string DescribeDoc(DocumentType t) => t switch
    {
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.BillingNote => "ใบวางบิล",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.Receipt => "ใบเสร็จ",
        DocumentType.ReceiptVoucher => "ใบสำคัญรับ",
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ค่าใช้จ่าย",
        DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
        _ => t.ToString()
    };
}
