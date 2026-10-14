using Accounting.Data;
using Accounting.Helpers;
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
            : ArApScope.ReceivableTypes;   // ใบวางบิลไม่ใช่ลูกหนี้ — ครอบใบแจ้งหนี้ที่อยู่ในใบแจ้งยอดแล้ว (ERP_REVIEW F-08)
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

    private sealed record StatementExportRow(DateTime? Date, string? DocNo, string? Type, string Description, DateTime? Due,
        decimal? Debit, decimal? Credit, decimal? Balance);

    /// <summary>ส่งออกใบแจ้งยอด CSV / Excel (<c>format=csv|xlsx</c> · ไม่ส่ง = csv ตามเดิม) — ผ่านตัวเขียนกลาง <see cref="TabularExport"/>
    ///
    /// <para>2026-10-14 ทีม X3 · erp-review/2026-10-14/EXPORT_AUDIT.md §X3 — ของเดิมเขียน CSV เอง: ยอดเงิน <c>{x:N2}</c> =
    /// "1,234.00" <b>ไม่ใส่อัญประกาศ ⇒ คอลัมน์เลื่อนทุกแถวที่ยอด ≥ 1,000</b> · วันที่ยอดยกมาเป็นตัวอักษร "01/01" ตายตัว ·
    /// ไม่กันสูตร · และหน้าเรียกด้วย <c>window.location.href</c> ซึ่งไม่แนบ JWT ⇒ 401 ทุกครั้ง (หน้าแก้เป็น API.download แล้ว)</para></summary>
    [HttpGet("contact/{contactId:guid}/export")]
    public async Task<ActionResult> ExportContactStatement(
        Guid companyId, Guid contactId,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        [FromQuery] string side = "AR",
        [FromQuery] string? format = null)
    {
        var fmt = TabularFormat.Csv;
        if (!string.IsNullOrWhiteSpace(format) && !TabularExport.TryParseFormat(format, out fmt))
            return BadRequest(new ApiResponse<object>(false, null, "รูปแบบไฟล์ต้องเป็น csv หรือ xlsx"));
        var r = await GetContactStatement(companyId, contactId, fromDate, toDate, side);
        var stmt = (r.Result as OkObjectResult)?.Value as ApiResponse<ContactStatement>;
        if (stmt?.Data == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบผู้ติดต่อ"));
        var d = stmt.Data;

        var cols = new[]
        {
            new TabularColumn<StatementExportRow>("วันที่", x => x.Date, TabularCellKind.Date),
            new TabularColumn<StatementExportRow>("เลขที่", x => x.DocNo),
            new TabularColumn<StatementExportRow>("ประเภท", x => x.Type),
            new TabularColumn<StatementExportRow>("รายละเอียด", x => x.Description),
            new TabularColumn<StatementExportRow>("ครบกำหนด", x => x.Due, TabularCellKind.Date),
            new TabularColumn<StatementExportRow>("เดบิต", x => x.Debit, TabularCellKind.Money),
            new TabularColumn<StatementExportRow>("เครดิต", x => x.Credit, TabularCellKind.Money),
            new TabularColumn<StatementExportRow>("ยอดคงค้าง", x => x.Balance, TabularCellKind.Money),
        };
        var rows = new List<StatementExportRow> { new(d.FromDate, null, null, "ยอดยกมา", null, null, null, d.OpeningBalance) };
        rows.AddRange(d.Lines.Select(l => new StatementExportRow(l.Date, l.DocumentNumber, DescribeDoc(l.Type), l.Description, l.DueDate,
            l.Debit, l.Credit, l.RunningBalance)));
        var footer = new List<StatementExportRow>
        {
            new(d.ToDate, null, null, "รวม / ยอดยกไป", null, d.TotalDebit, d.TotalCredit, d.ClosingBalance),
            new(null, null, null, "อายุหนี้: ยังไม่ถึงกำหนด", null, null, null, d.Current),
            new(null, null, null, "อายุหนี้: 1-30 วัน", null, null, null, d.D1to30),
            new(null, null, null, "อายุหนี้: 31-60 วัน", null, null, null, d.D31to60),
            new(null, null, null, "อายุหนี้: 61-90 วัน", null, null, null, d.D61to90),
            new(null, null, null, "อายุหนี้: เกิน 90 วัน", null, null, null, d.Over90),
        };
        var sideTh = side == "AP" ? "เจ้าหนี้" : "ลูกหนี้";
        var title = new List<string>
        {
            $"ใบแจ้งยอด — {sideTh}",
            $"ผู้ติดต่อ: {d.ContactName}" + (string.IsNullOrWhiteSpace(d.TaxId) ? "" : $" (เลขประจำตัวผู้เสียภาษี {d.TaxId})"),
            $"ช่วง {ThaiDate.ToThaiDisplayString(d.FromDate)} – {ThaiDate.ToThaiDisplayString(d.ToDate)}",
        };
        var sheet = TabularExport.Sheet("ใบแจ้งยอด", cols, rows, footer, title);
        var f = TabularExport.Render(fmt, $"ใบแจ้งยอด{sideTh}_{d.ContactName}", d.FromDate, d.ToDate, sheet);
        return File(f.Content, f.ContentType, f.FileName);
    }

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
