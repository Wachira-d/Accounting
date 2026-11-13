using System.Globalization;
using System.Net;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Services.Implementations.Pdf;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก C (คำตัดสิน 266-1-6 · 266-1-39) — <b>"ใบแจ้งการนำเงินไปตัดชำระ"</b> (Payment application advice) ของแถวจัดสรรที่เกิดหลังรับเงิน
/// (จัดสรรเงินรอจัดสรรทีหลัง · ย้ายการรับชำระ) — <b>ไม่ใช่ใบรับ/ใบกำกับภาษี</b> · ไม่บันทึกเป็นแถวเอกสาร · ไม่มีเลขชุดภาษี ⇒ ไม่เข้า ภ.พ.30 โดยโครงสร้าง
/// <para>กฎเหล็ก #4 A (สอง renderer ห้าม drift): ตัวโหลด (<see cref="LoadPaymentApplicationAdviceAsync"/>) · เนื้อหา (<see cref="PaymentApplicationAdvice.Build"/>) ·
/// แถวข้อความ (<see cref="AdviceRows"/>) ใช้ร่วม · ตัววาด HTML (<see cref="GeneratePaymentApplicationAdviceHtmlAsync"/>) และ QuestPDF
/// (<see cref="GeneratePaymentApplicationAdvicePdfAsync"/>) อยู่ไฟล์เดียวกัน ข้อความจาก <see cref="DocumentLabels"/> ตัวเดียว · ค่าที่ผู้ใช้คุมได้ผ่าน HtmlEncode (กฎเหล็ก #4 C)</para>
/// </summary>
public partial class PdfGenerationService
{
    internal sealed record AdviceParty(string Name, string? TaxId, string? Branch, string? Address);

    internal sealed record AdviceView(PaymentApplicationAdviceModel Model, AdviceParty Company, AdviceParty Customer, DocumentLabels Labels);

    /// <summary>ตัวโหลดเดียวของทั้งสอง renderer — tenant ทุก query · แถวที่ถูกปล่อย/ยกเลิก = ปฏิเสธ (<see cref="PaymentApplicationAdvice.Refusal"/>)</summary>
    private async Task<AdviceView> LoadPaymentApplicationAdviceAsync(Guid companyId, Guid allocationId, string? language)
    {
        // แถวที่ถูกปล่อยถูกซ่อนด้วย query filter (IsDeleted) — อ่านข้ามตัวกรองเพื่อให้ปฏิเสธด้วยเหตุจริง ไม่ใช่ "ไม่พบ"
        var a = await _db.PaymentAllocations.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == allocationId && x.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบการตัดชำระ");
        var p = await _db.Payments.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == a.PaymentId && x.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบการรับชำระ");
        var inv = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                      .FirstOrDefaultAsync(x => x.Id == a.DocumentId && x.CompanyId == companyId)
                  ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้");
        var journalRef = a.JournalEntryId is Guid jeId
            ? await _db.JournalEntries.AsNoTracking().Where(j => j.Id == jeId && j.CompanyId == companyId).Select(j => j.Reference).FirstOrDefaultAsync()
            : null;
        var receiptNumber = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != Accounting.Models.Enums.DocumentStatus.Voided
                && (d.Id == p.ReceiptDocumentId || (d.SettlementPaymentId == p.Id && d.IsSettlementReceipt)))
            .OrderBy(d => d.CreatedAt).Select(d => d.DocumentNumber).FirstOrDefaultAsync();
        var facts = new PaymentApplicationAdviceFacts(
            p.PaymentNumber, p.PaymentDate.Date, p.Amount, receiptNumber,
            inv.DocumentNumber, inv.DocumentDate.Date, inv.TotalAmount, a.AllocatedAmount, inv.BalanceDue,
            (a.AppliedAt ?? a.CreatedAt).Date, journalRef,
            IsLateAllocation: a.IsLateAllocation, IsMovedAllocation: a.MovedFromAllocationId != null,
            Released: a.ReleasedAt != null, AllocationDeleted: a.IsDeleted, PaymentVoided: p.IsDeleted,
            RemainingUnapplied: Math.Max(0m, p.UnappliedAmount),
            InvoiceVoided: inv.IsDeleted || inv.Status == Accounting.Models.Enums.DocumentStatus.Voided,
            InvoiceIsSalesSide: CustomerCreditAllocation.TargetTypes.Contains(inv.DocumentType));
        var model = PaymentApplicationAdvice.Build(facts);   // ไม่ผ่าน = throw (ข้อความไทย + ทางไปต่อ)

        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == inv.ContactId && c.CompanyId == companyId);
        var labels = DocumentLabels.For(ResolveDocumentLanguage(language, inv, null, settings));
        return new AdviceView(model,
            new AdviceParty(company.Name, company.TaxId, labels.Branch(company.BranchCode), company.Address),
            new AdviceParty(contact?.Name ?? "-", contact?.TaxId, labels.Branch(contact?.BranchCode), contact?.Address),
            labels);
    }

    /// <summary>แถว "ป้าย : ค่า" ของใบแจ้ง (ยังไม่ escape) — ตัวเดียวของทั้งสอง renderer</summary>
    internal static IReadOnlyList<(string Label, string Value)> AdviceRows(PaymentApplicationAdviceModel m, DocumentLabels l)
    {
        static string M(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
        var f = m.Facts;
        var rows = new List<(string, string)>
        {
            (l["advice_reference"], m.Reference),
            (l["advice_applied_on"], l.Date(m.AppliedOn)),
            (l["advice_payment"], f.PaymentNumber),
            (l["advice_payment_date"], l.Date(f.PaymentDate)),
            (l["advice_payment_amount"], M(f.PaymentAmount)),
        };
        if (!string.IsNullOrWhiteSpace(f.ReceiptNumber)) rows.Add((l["advice_receipt_no"], f.ReceiptNumber!));
        rows.Add((l["advice_invoice"], f.InvoiceNumber));
        rows.Add((l["advice_invoice_date"], l.Date(f.InvoiceDate)));
        rows.Add((l["advice_invoice_total"], M(f.InvoiceTotal)));
        rows.Add((l["advice_applied_amount"], M(f.AppliedAmount)));
        rows.Add((l["advice_invoice_balance"], M(f.InvoiceBalanceNow)));
        rows.Add((l["advice_remaining_credit"], M(f.RemainingUnapplied)));
        return rows;
    }

    private static IEnumerable<string> PartyLines(AdviceParty p, DocumentLabels l) => new[]
    {
        p.Name,
        string.IsNullOrWhiteSpace(p.TaxId) ? null : $"{l["advice_tax_id"]} {p.TaxId}" + (string.IsNullOrWhiteSpace(p.Branch) ? "" : $" ({p.Branch})"),
        p.Address,
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!);

    /// <summary>renderer HTML (พรีวิวในเบราว์เซอร์/พิมพ์) — เนื้อหาเดียวกับ QuestPDF · ทุกค่าผ่าน HtmlEncode</summary>
    public async Task<string> GeneratePaymentApplicationAdviceHtmlAsync(Guid companyId, Guid allocationId, string? language)
    {
        var v = await LoadPaymentApplicationAdviceAsync(companyId, allocationId, language);
        var l = v.Labels;
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.AppendLine($"<title>{E(l["advice_title"])} {E(v.Model.Reference)}</title>");
        sb.AppendLine("<style>body{font-family:Sarabun,'Noto Sans Thai',sans-serif;color:#0f172a;margin:24px;background:#fff}"
            + "h1{font-size:20px;margin:0 0 4px}.note{border:1px solid #fcd34d;background:#fffbeb;padding:8px 10px;font-size:12px;margin:10px 0}"
            + ".cols{display:flex;gap:24px;flex-wrap:wrap;font-size:13px}.cols div{flex:1;min-width:220px}table{border-collapse:collapse;width:100%;font-size:13px;margin-top:12px}"
            + "td{border-bottom:1px solid #e2e8f0;padding:5px 8px}td.l{color:#475569;width:45%}</style></head><body>");
        sb.AppendLine($"<h1>{E(l["advice_title"])}</h1>");
        sb.AppendLine($"<div class='note'><b>{E(l["advice_not_tax_document"])}</b></div>");
        sb.AppendLine("<div class='cols'>");
        sb.AppendLine("<div>" + string.Join("<br>", PartyLines(v.Company, l).Select(E)) + "</div>");
        sb.AppendLine($"<div><b>{E(l["advice_customer"])}</b><br>" + string.Join("<br>", PartyLines(v.Customer, l).Select(E)) + "</div>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<p style='font-size:13px'>{E(l["advice_intro"])}</p>");
        sb.AppendLine("<table>");
        foreach (var (label, value) in AdviceRows(v.Model, l))
            sb.AppendLine($"<tr><td class='l'>{E(label)}</td><td>{E(value)}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>renderer QuestPDF (ดาวน์โหลด PDF) — เนื้อหาเดียวกับ HTML (<see cref="AdviceRows"/> · <see cref="PartyLines"/>)</summary>
    public async Task<GeneratePdfResponse> GeneratePaymentApplicationAdvicePdfAsync(Guid companyId, Guid allocationId, string? language)
    {
        var v = await LoadPaymentApplicationAdviceAsync(companyId, allocationId, language);
        var l = v.Labels;
        EnsureThaiFontsRegistered();
        var fontChain = GetFontFamilyChain(null);
        var rows = AdviceRows(v.Model, l);
        var pdf = QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(16, Unit.Millimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(10.5f).FontColor("#0F172A"));
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    col.Item().Text(l["advice_title"]).FontSize(18).Bold();
                    col.Item().Border(1).BorderColor("#FCD34D").Background("#FFFBEB").Padding(6).Text(l["advice_not_tax_document"]).FontSize(9.5f).Bold();
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Column(c => { foreach (var line in PartyLines(v.Company, l)) c.Item().Text(line); });
                        r.ConstantItem(16);
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text(l["advice_customer"]).Bold();
                            foreach (var line in PartyLines(v.Customer, l)) c.Item().Text(line);
                        });
                    });
                    col.Item().Text(l["advice_intro"]);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(45); c.RelativeColumn(55); });
                        foreach (var (label, value) in rows)
                        {
                            t.Cell().BorderBottom(1).BorderColor("#E2E8F0").Padding(4).Text(label).FontColor("#475569");
                            t.Cell().BorderBottom(1).BorderColor("#E2E8F0").Padding(4).Text(value);
                        }
                    });
                });
            });
        }).GeneratePdf();
        // ใบแจ้งไม่ใช่แถวเอกสาร — ไม่มี DocumentId ของตัวเอง (ส่ง id ของแถวจัดสรรแทน)
        return new GeneratePdfResponse(allocationId, v.Model.Reference, $"{v.Model.Reference.Replace('/', '-')}.pdf", "application/pdf",
            pdf.Length, pdf, DateTime.UtcNow);
    }
}
