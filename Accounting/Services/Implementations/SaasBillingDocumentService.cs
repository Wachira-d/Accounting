using System.Net;
using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>WP-B2: ออกใบเสร็จ/ใบกำกับค่าบริการ SaaS ตอน payment ได้รับอนุมัติ.
/// platform = ผู้ขาย (จาก SiteSettings), บริษัทลูกค้า = ผู้ซื้อ.</summary>
public class SaasBillingDocumentService : ISaasBillingDocumentService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService _pdf;
    private readonly IFileAttachmentService _files;
    private readonly IEmailService? _email;
    private readonly ILogger<SaasBillingDocumentService> _logger;

    private const string EntityType = "SubscriptionReceipt";

    public SaasBillingDocumentService(AccountingDbContext db, IPdfGenerationService pdf,
        IFileAttachmentService files, ILogger<SaasBillingDocumentService> logger,
        IEmailService? email = null)
    {
        _db = db; _pdf = pdf; _files = files; _logger = logger; _email = email;
    }

    public async Task GenerateReceiptForApprovedPaymentAsync(Guid paymentId)
    {
        try
        {
            var payment = await _db.SubscriptionPayments
                .Include(p => p.Subscription)
                .FirstOrDefaultAsync(p => p.Id == paymentId);
            if (payment == null) return;

            // idempotent — ออกใบแล้วไม่ออกซ้ำ (กัน 2 ใบ = เลขซ้ำ/อีเมลซ้ำ)
            if (!string.IsNullOrEmpty(payment.ReceiptNumber)) return;
            if (payment.Status != SubscriptionPaymentStatus.Approved) return;

            var companyId = payment.Subscription.CompanyId;
            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
            if (buyer == null) return;

            var settings = await _db.Set<SiteSettings>().AsNoTracking()
                .OrderBy(s => s.CreatedAt).FirstOrDefaultAsync();

            var isTaxInvoice = settings != null
                && settings.PlatformIsVatRegistered
                && IsValidTaxId(settings.PlatformSellerTaxId);

            var now = DateTime.UtcNow;
            var receiptNumber = await NextReceiptNumberAsync(isTaxInvoice, now);

            var html = BuildHtml(payment, buyer, settings, isTaxInvoice, receiptNumber, now);
            byte[] pdfBytes;
            try { pdfBytes = _pdf.ConvertHtmlToPdfBytes(html); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "แปลง PDF ใบเสร็จ SaaS ไม่สำเร็จ payment {PaymentId}", paymentId);
                return;
            }

            // uploader = เจ้าของบริษัทลูกค้า (FK ต้องเป็น user จริง)
            var ownerUserId = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerUserId == Guid.Empty)
                ownerUserId = await _db.CompanyUsers.AsNoTracking()
                    .Where(cu => cu.CompanyId == companyId)
                    .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerUserId == Guid.Empty) return; // ไม่มี user ผูก company → แนบไม่ได้

            var fileName = $"{receiptNumber}.pdf";
            var att = await _files.UploadBytesAsync(companyId, EntityType, payment.Id,
                fileName, "application/pdf", pdfBytes, ownerUserId);

            // stamp payment (ผ่าน tracked entity)
            var tracked = await _db.SubscriptionPayments.FirstAsync(p => p.Id == paymentId);
            tracked.ReceiptNumber = receiptNumber;
            tracked.ReceiptAttachmentId = att.Id;
            tracked.ReceiptIssuedAt = now;
            tracked.ReceiptIsTaxInvoice = isTaxInvoice;
            await _db.SaveChangesAsync();

            await SendEmailAsync(companyId, receiptNumber, isTaxInvoice);
        }
        catch (Exception ex)
        {
            // ห้ามทำให้ approval พัง — log แล้วปล่อย
            _logger.LogError(ex, "GenerateReceiptForApprovedPaymentAsync ล้มเหลว payment {PaymentId}", paymentId);
        }
    }

    public async Task<(byte[] Bytes, string FileName)?> GetReceiptPdfAsync(Guid paymentId)
    {
        var payment = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null) return null;
        var companyId = payment.Subscription.CompanyId;

        // ไฟล์ที่เก็บไว้
        if (payment.ReceiptAttachmentId.HasValue)
        {
            var att = await _files.GetByIdAsync(companyId, payment.ReceiptAttachmentId.Value);
            if (att != null)
            {
                var fullPath = Path.IsPathRooted(att.StoragePath)
                    ? att.StoragePath
                    : Path.Combine(Directory.GetCurrentDirectory(), att.StoragePath);
                if (File.Exists(fullPath))
                    return (await File.ReadAllBytesAsync(fullPath), att.OriginalFileName);
            }
        }

        // fallback: regenerate จากข้อมูลที่ stamp ไว้ (ไฟล์หาย/ยังไม่ออก)
        if (string.IsNullOrEmpty(payment.ReceiptNumber)) return null;
        var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        if (buyer == null) return null;
        var settings = await _db.Set<SiteSettings>().AsNoTracking()
            .OrderBy(s => s.CreatedAt).FirstOrDefaultAsync();
        var issuedAt = payment.ReceiptIssuedAt ?? payment.ReviewedAt ?? payment.CreatedAt;
        var html = BuildHtml(payment, buyer, settings, payment.ReceiptIsTaxInvoice, payment.ReceiptNumber, issuedAt);
        try { return (_pdf.ConvertHtmlToPdfBytes(html), $"{payment.ReceiptNumber}.pdf"); }
        catch { return null; }
    }

    // ---- running number: แยก sequence จากเอกสาร tenant (WP-B4) ----
    // {prefix}-{yyyyMM}-{seq:D4} · seq = จำนวนใบที่ออกในเดือนนั้น + 1.
    // platform volume ต่ำ + ออกโดย admin ทีละใบ (single-writer) → collision แทบเป็นศูนย์.
    private async Task<string> NextReceiptNumberAsync(bool isTaxInvoice, DateTime now)
    {
        var prefix = isTaxInvoice ? "TINV" : "RCPT";
        var period = now.ToString("yyyyMM");
        var like = $"{prefix}-{period}-%";
        var count = await _db.SubscriptionPayments
            .CountAsync(p => p.ReceiptNumber != null && EF.Functions.Like(p.ReceiptNumber, like));
        return $"{prefix}-{period}-{count + 1:D4}";
    }

    private async Task SendEmailAsync(Guid companyId, string receiptNumber, bool isTaxInvoice)
    {
        if (_email == null) return;
        try
        {
            if (!await _email.IsSystemEmailConfiguredAsync()) return;
            var owner = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Join(_db.Users, cu => cu.UserId, u => u.Id, (cu, u) => new { u.Email, u.FullName })
                .FirstOrDefaultAsync();
            if (owner == null || string.IsNullOrWhiteSpace(owner.Email)) return;

            var docLabel = isTaxInvoice ? "ใบกำกับภาษี/ใบเสร็จรับเงิน" : "ใบเสร็จรับเงิน";
            var msg = $"{docLabel}เลขที่ {receiptNumber} สำหรับค่าบริการที่ชำระเรียบร้อยแล้ว "
                    + "ออกให้เรียบร้อย — ดาวน์โหลดได้จากหน้าการชำระเงินในระบบ ขอบคุณที่ใช้บริการ";
            await _email.SendNotificationEmailAsync(owner.Email, owner.FullName ?? "ลูกค้า",
                $"{docLabel} {receiptNumber}", msg, "/pages/subscription.html");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ส่งอีเมลใบเสร็จ SaaS ไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    private static bool IsValidTaxId(string? taxId)
        => !string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13 && taxId.All(char.IsDigit);

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private string BuildHtml(SubscriptionPayment payment, Company buyer, SiteSettings? s,
        bool isTaxInvoice, string receiptNumber, DateTime issuedAt)
    {
        var sellerName = !string.IsNullOrWhiteSpace(s?.PlatformSellerName)
            ? s!.PlatformSellerName!
            : (s?.SiteName ?? "ผู้ให้บริการ");
        var sellerAddr = s?.PlatformSellerAddress;
        var sellerTaxId = s?.PlatformSellerTaxId;
        var sellerBranch = string.IsNullOrWhiteSpace(s?.PlatformSellerBranchCode) ? "00000" : s!.PlatformSellerBranchCode!;
        var sellerPhone = s?.PlatformSellerPhone;
        var sellerEmail = s?.PlatformSellerEmail ?? s?.ContactEmail;

        var gross = payment.Amount;
        decimal subtotal, vat, grandTotal;
        if (isTaxInvoice)
        {
            if (s?.PlatformPriceIncludesVat ?? true)
            {
                vat = Math.Round(gross * 7m / 107m, 2);
                subtotal = gross - vat;
                grandTotal = gross;
            }
            else
            {
                subtotal = gross;
                vat = Math.Round(gross * 0.07m, 2);
                grandTotal = gross + vat;
            }
        }
        else { subtotal = gross; vat = 0m; grandTotal = gross; }

        var title = isTaxInvoice ? "ใบกำกับภาษี / ใบเสร็จรับเงิน" : "ใบเสร็จรับเงิน";
        var buyerBranch = string.IsNullOrWhiteSpace(buyer.BranchCode) ? "00000" : buyer.BranchCode!;
        var buyerBranchLabel = buyerBranch == "00000" ? "สำนักงานใหญ่" : $"สาขาที่ {buyerBranch}";
        var sellerBranchLabel = sellerBranch == "00000" ? "สำนักงานใหญ่" : $"สาขาที่ {sellerBranch}";
        var periodLabel = $"{payment.RequestedPlan} · {payment.RequestedBillingCycle} · {payment.RequestedPeriodMonths} เดือน";
        var extendedTo = payment.SubscriptionExtendedTo;

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='th'><head><meta charset='utf-8'/><style>");
        sb.Append(@"
            *{box-sizing:border-box;}
            body{font-family:'THSarabunNew','Noto Sans Thai',sans-serif;font-size:14px;color:#1e293b;margin:0;padding:28px;}
            .doc{max-width:720px;margin:0 auto;}
            .head{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:2px solid #334155;padding-bottom:10px;}
            .seller-name{font-size:20px;font-weight:700;}
            .muted{color:#64748b;font-size:12px;line-height:1.5;}
            .title{text-align:right;}
            .title h1{font-size:20px;margin:0;color:#0f172a;}
            .meta{display:flex;justify-content:space-between;margin:14px 0;gap:16px;}
            .box{flex:1;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px 12px;}
            .box .label{font-size:11px;color:#64748b;text-transform:uppercase;letter-spacing:.4px;margin-bottom:4px;}
            table{width:100%;border-collapse:collapse;margin-top:8px;}
            th,td{padding:8px 10px;border-bottom:1px solid #e2e8f0;text-align:left;}
            th{background:#f1f5f9;font-size:12px;}
            td.r,th.r{text-align:right;}
            .sum{margin-top:10px;margin-left:auto;width:260px;}
            .sum .row{display:flex;justify-content:space-between;padding:3px 0;}
            .sum .grand{border-top:2px solid #334155;margin-top:4px;padding-top:6px;font-weight:700;font-size:16px;}
            .foot{margin-top:26px;display:flex;justify-content:space-between;color:#64748b;font-size:12px;}
            .stamp{margin-top:40px;text-align:center;}
            .sign{margin-top:44px;border-top:1px dashed #94a3b8;width:220px;text-align:center;padding-top:4px;}
        ");
        sb.Append("</style></head><body><div class='doc'>");

        sb.Append("<div class='head'><div>");
        sb.Append($"<div class='seller-name'>{Enc(sellerName)}</div>");
        if (!string.IsNullOrWhiteSpace(sellerAddr)) sb.Append($"<div class='muted'>{Enc(sellerAddr)}</div>");
        if (isTaxInvoice && IsValidTaxId(sellerTaxId))
            sb.Append($"<div class='muted'>เลขประจำตัวผู้เสียภาษี: {Enc(sellerTaxId)} ({Enc(sellerBranchLabel)})</div>");
        var contact = string.Join("  ", new[] {
            string.IsNullOrWhiteSpace(sellerPhone) ? null : $"โทร {Enc(sellerPhone)}",
            string.IsNullOrWhiteSpace(sellerEmail) ? null : Enc(sellerEmail) }.Where(x => x != null));
        if (contact.Length > 0) sb.Append($"<div class='muted'>{contact}</div>");
        sb.Append("</div>");
        sb.Append($"<div class='title'><h1>{Enc(title)}</h1>");
        sb.Append($"<div class='muted'>เลขที่: <strong>{Enc(receiptNumber)}</strong></div>");
        sb.Append($"<div class='muted'>วันที่: {issuedAt.AddHours(7):dd/MM/yyyy}</div>");
        sb.Append("</div></div>");

        // buyer
        sb.Append("<div class='meta'>");
        sb.Append("<div class='box'><div class='label'>ลูกค้า (ผู้ซื้อ)</div>");
        sb.Append($"<div><strong>{Enc(buyer.Name)}</strong></div>");
        if (!string.IsNullOrWhiteSpace(buyer.Address)) sb.Append($"<div class='muted'>{Enc(buyer.Address)}</div>");
        if (isTaxInvoice && IsValidTaxId(buyer.TaxId))
            sb.Append($"<div class='muted'>เลขผู้เสียภาษี: {Enc(buyer.TaxId)} ({Enc(buyerBranchLabel)})</div>");
        sb.Append("</div>");
        sb.Append("<div class='box'><div class='label'>การชำระเงิน</div>");
        sb.Append($"<div class='muted'>วิธี: {Enc(payment.PaymentMethod.ToString())}</div>");
        sb.Append($"<div class='muted'>วันที่รับเงิน: {payment.PaymentDate.AddHours(7):dd/MM/yyyy}</div>");
        if (!string.IsNullOrWhiteSpace(payment.TransferReference))
            sb.Append($"<div class='muted'>อ้างอิง: {Enc(payment.TransferReference)}</div>");
        sb.Append("</div></div>");

        // line
        sb.Append("<table><thead><tr><th>รายการ</th><th class='r'>จำนวนเงิน</th></tr></thead><tbody>");
        var extLabel = extendedTo.HasValue ? $" (ต่ออายุถึง {extendedTo.Value.AddHours(7):dd/MM/yyyy})" : "";
        sb.Append($"<tr><td>ค่าบริการระบบบัญชีออนไลน์ — {Enc(periodLabel)}{Enc(extLabel)}</td>");
        sb.Append($"<td class='r'>{subtotal:N2}</td></tr>");
        sb.Append("</tbody></table>");

        // summary
        sb.Append("<div class='sum'>");
        if (isTaxInvoice)
        {
            sb.Append($"<div class='row'><span>มูลค่าก่อนภาษี</span><span>{subtotal:N2}</span></div>");
            sb.Append($"<div class='row'><span>ภาษีมูลค่าเพิ่ม 7%</span><span>{vat:N2}</span></div>");
        }
        sb.Append($"<div class='row grand'><span>รวมทั้งสิ้น</span><span>{grandTotal:N2} บาท</span></div>");
        sb.Append("</div>");

        sb.Append("<div class='foot'><div>");
        if (payment.Kind == SubscriptionPaymentKind.Waived)
            sb.Append("<div>* เอกสารนี้ออกจากการยกเว้นค่าบริการ (ยอด 0)</div>");
        sb.Append("<div>เอกสารออกโดยระบบอัตโนมัติ</div></div>");
        sb.Append("<div class='sign'>ผู้รับเงิน</div>");
        sb.Append("</div>");

        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
