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
    // ออกเอกสารผ่าน tenant ผู้ให้บริการ (ACCOUNT_STRUCTURE §6.1) — optional
    // เพื่อให้ระบบเดิมยังทำงานได้ถ้ายังไม่ได้ตั้งค่า/ยังไม่ register
    private readonly IPlatformBillingDocumentIssuer? _issuer;
    private readonly IPdfGenerationService? _docPdf;

    private const string EntityType = "SubscriptionReceipt";

    public SaasBillingDocumentService(AccountingDbContext db, IPdfGenerationService pdf,
        IFileAttachmentService files, ILogger<SaasBillingDocumentService> logger,
        IEmailService? email = null, IPlatformBillingDocumentIssuer? issuer = null)
    {
        _db = db; _pdf = pdf; _files = files; _logger = logger; _email = email;
        _issuer = issuer; _docPdf = pdf;
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

            // ─── ทางหลัก: ออกเอกสารจริงใน tenant ของผู้ให้บริการ ───
            // ได้เลข gap-free ตามชุดเอกสารจริง + JE รายได้ + เข้ารายงานภาษีขาย/
            // ภ.พ.30 + ออก e-Tax ได้ (โหมด PDF เดี่ยวด้านล่างทำไม่ได้สักอย่าง)
            if (_issuer != null && await _issuer.IsEnabledAsync())
            {
                var issued = await _issuer.IssuePaidReceiptAsync(payment, buyer);
                if (issued != null)
                {
                    var t = await _db.SubscriptionPayments.FirstAsync(p => p.Id == paymentId);
                    t.PlatformDocumentId = issued.DocumentId;
                    t.ReceiptNumber = issued.DocumentNumber;
                    t.ReceiptIssuedAt = DateTime.UtcNow;
                    t.ReceiptIsTaxInvoice = issued.IsTaxInvoice;
                    // ไม่ต้องแนบไฟล์ — PDF ดึงสด ๆ จากเอกสารจริงตอนดาวน์โหลด
                    // (แนบไว้จะกลายเป็นสำเนาที่ค้างเมื่อเอกสารถูกแก้/ยกเลิก)
                    t.ReceiptAttachmentId = null;
                    await _db.SaveChangesAsync();
                    await SendEmailAsync(companyId, issued.DocumentNumber, issued.IsTaxInvoice);
                    return;
                }
                // ออกไม่สำเร็จ → ตกลงไปโหมด PDF เดิม (เงินเข้าแล้วต้องมีหลักฐานเสมอ)
                _logger.LogWarning("ออกเอกสารจริงไม่สำเร็จ (payment {PaymentId}) — ใช้ PDF เดี่ยวแทน", paymentId);
            }

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

        // ออกเป็นเอกสารจริงใน tenant ผู้ให้บริการ → เรนเดอร์จากเอกสารนั้นโดยตรง
        // (ได้เทมเพลต/ลายเซ็น/ตราประทับชุดเดียวกับเอกสารอื่นของบริษัทเรา และ
        //  สะท้อนการแก้ไขล่าสุดเสมอ ไม่ใช่สำเนาที่ค้างไว้)
        if (payment.PlatformDocumentId.HasValue && _docPdf != null)
        {
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == payment.PlatformDocumentId.Value && !d.IsDeleted)
                .Select(d => new { d.CompanyId, d.DocumentNumber })
                .FirstOrDefaultAsync();
            if (doc != null)
            {
                try
                {
                    var gen = await _docPdf.GenerateDocumentPdfAsync(doc.CompanyId,
                        new Models.DTOs.DocumentTemplate.GeneratePdfRequest(
                            payment.PlatformDocumentId.Value, null, null, null, null));
                    return (gen.PdfData, gen.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "เรนเดอร์ PDF เอกสารค่าบริการ {DocNo} ไม่สำเร็จ", doc.DocumentNumber);
                }
            }
        }

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

    private static string StyleBlock() =>
        "<!DOCTYPE html><html lang='th'><head><meta charset='utf-8'/><style>" + @"
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
            .pay{margin-top:16px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;padding:12px 14px;color:#1e3a8a;font-size:13px;}
            .sign{margin-top:44px;border-top:1px dashed #94a3b8;width:220px;text-align:center;padding-top:4px;}
        " + "</style></head><body>";

    private (string Name, string? Address, string? TaxId, string Branch, string? Phone, string? Email)
        Seller(SiteSettings? s) => (
            !string.IsNullOrWhiteSpace(s?.PlatformSellerName) ? s!.PlatformSellerName! : (s?.SiteName ?? "ผู้ให้บริการ"),
            s?.PlatformSellerAddress, s?.PlatformSellerTaxId,
            string.IsNullOrWhiteSpace(s?.PlatformSellerBranchCode) ? "00000" : s!.PlatformSellerBranchCode!,
            s?.PlatformSellerPhone, s?.PlatformSellerEmail ?? s?.ContactEmail);

    // ---- WP-B1: renewal invoice ----
    public async Task<string?> GenerateRenewalInvoiceAsync(Guid subscriptionId)
    {
        try
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
            if (sub == null) return null;

            // idempotent ต่อ EndDate ของรอบนี้
            if (sub.RenewalInvoiceForEndDate.HasValue
                && sub.RenewalInvoiceForEndDate.Value == sub.EndDate
                && !string.IsNullOrEmpty(sub.RenewalInvoiceNumber))
                return sub.RenewalInvoiceNumber;

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sub.CompanyId);
            if (buyer == null) return null;

            var template = await _db.PlanTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive);
            var amount = PriceFor(template, sub.BillingCycle, sub.PricePerCycle);
            if (amount <= 0) return null; // ฟรี/ไม่มีราคา → ไม่ต้องออกใบแจ้งหนี้

            var settings = await _db.Set<SiteSettings>().AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync();
            var now = DateTime.UtcNow;
            var months = MonthsFor(sub.BillingCycle);

            // ─── ทางหลัก: ออกใบแจ้งหนี้จริงใน tenant ของผู้ให้บริการ ───
            // ได้ลูกหนี้ใน GL ของเราเอง (ตามเก็บได้จริง) + เลขในชุดเดียวกับเอกสารอื่น
            if (_issuer != null && await _issuer.IsEnabledAsync())
            {
                var invoiced = await _issuer.IssueRenewalInvoiceAsync(sub.CompanyId,
                    $"ค่าบริการระบบบัญชีออนไลน์ แพ็กเกจ {sub.Plan} ({months} เดือน) "
                        + $"— รอบถัดไปถึง {sub.EndDate.AddMonths(months):dd/MM/yyyy}",
                    amount, now, sub.EndDate, $"RENEW-{sub.Id.ToString()[..8].ToUpper()}");
                if (invoiced != null)
                {
                    sub.RenewalInvoiceNumber = invoiced.DocumentNumber;
                    sub.RenewalInvoiceIssuedAt = now;
                    sub.RenewalInvoiceForEndDate = sub.EndDate;
                    sub.RenewalInvoiceDocumentId = invoiced.DocumentId;
                    await _db.SaveChangesAsync();
                    await SendInvoiceEmailAsync(sub.CompanyId, invoiced.DocumentNumber, amount, sub.EndDate);
                    return invoiced.DocumentNumber;
                }
                _logger.LogWarning("ออกใบแจ้งหนี้ต่ออายุจริงไม่สำเร็จ (sub {Sub}) — ใช้ PDF เดี่ยวแทน", subscriptionId);
            }

            var number = await NextInvoiceNumberAsync(now);

            var html = BuildInvoiceHtml(buyer, settings, number, now, sub.EndDate, amount, sub.Plan, sub.BillingCycle, months);
            byte[] pdfBytes;
            try { pdfBytes = _pdf.ConvertHtmlToPdfBytes(html); }
            catch (Exception ex) { _logger.LogWarning(ex, "แปลง PDF ใบแจ้งหนี้ต่ออายุไม่สำเร็จ sub {Sub}", subscriptionId); return null; }

            var ownerUserId = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerUserId == Guid.Empty)
                ownerUserId = await _db.CompanyUsers.AsNoTracking()
                    .Where(cu => cu.CompanyId == sub.CompanyId).Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerUserId == Guid.Empty) return null;

            var att = await _files.UploadBytesAsync(sub.CompanyId, "SubscriptionInvoice", sub.Id,
                $"{number}.pdf", "application/pdf", pdfBytes, ownerUserId);

            sub.RenewalInvoiceNumber = number;
            sub.RenewalInvoiceIssuedAt = now;
            sub.RenewalInvoiceForEndDate = sub.EndDate;
            await _db.SaveChangesAsync();

            await SendInvoiceEmailAsync(sub.CompanyId, number, amount, sub.EndDate);
            return number;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateRenewalInvoiceAsync ล้มเหลว sub {Sub}", subscriptionId);
            return null;
        }
    }

    public async Task<(byte[] Bytes, string FileName)?> GetRenewalInvoicePdfAsync(Guid subscriptionId)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (sub == null || string.IsNullOrEmpty(sub.RenewalInvoiceNumber)) return null;

        // ออกเป็นเอกสารจริง → เรนเดอร์จากเอกสารนั้น (เหมือนเส้นใบเสร็จ)
        if (sub.RenewalInvoiceDocumentId.HasValue && _docPdf != null)
        {
            var docCompanyId = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == sub.RenewalInvoiceDocumentId.Value && !d.IsDeleted)
                .Select(d => (Guid?)d.CompanyId).FirstOrDefaultAsync();
            if (docCompanyId.HasValue)
            {
                try
                {
                    var gen = await _docPdf.GenerateDocumentPdfAsync(docCompanyId.Value,
                        new Models.DTOs.DocumentTemplate.GeneratePdfRequest(
                            sub.RenewalInvoiceDocumentId.Value, null, null, null, null));
                    return (gen.PdfData, gen.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "เรนเดอร์ PDF ใบแจ้งหนี้ต่ออายุไม่สำเร็จ (sub {Sub})", subscriptionId);
                }
            }
        }

        // หาไฟล์ที่เก็บไว้
        var atts = await _files.GetByEntityAsync(sub.CompanyId, "SubscriptionInvoice", sub.Id);
        var stored = atts.OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault(a => a.OriginalFileName == $"{sub.RenewalInvoiceNumber}.pdf");
        if (stored != null)
        {
            var fullPath = Path.IsPathRooted(stored.StoragePath)
                ? stored.StoragePath : Path.Combine(Directory.GetCurrentDirectory(), stored.StoragePath);
            if (File.Exists(fullPath))
                return (await File.ReadAllBytesAsync(fullPath), stored.OriginalFileName);
        }

        // regenerate
        var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sub.CompanyId);
        if (buyer == null) return null;
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive);
        var amount = PriceFor(template, sub.BillingCycle, sub.PricePerCycle);
        var settings = await _db.Set<SiteSettings>().AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync();
        var issuedAt = sub.RenewalInvoiceIssuedAt ?? DateTime.UtcNow;
        var forEnd = sub.RenewalInvoiceForEndDate ?? sub.EndDate;
        var html = BuildInvoiceHtml(buyer, settings, sub.RenewalInvoiceNumber!, issuedAt, forEnd, amount,
            sub.Plan, sub.BillingCycle, MonthsFor(sub.BillingCycle));
        try { return (_pdf.ConvertHtmlToPdfBytes(html), $"{sub.RenewalInvoiceNumber}.pdf"); }
        catch { return null; }
    }

    private static int MonthsFor(BillingCycle c) => c switch
    {
        BillingCycle.Monthly => 1, BillingCycle.Quarterly => 3,
        BillingCycle.SemiAnnual => 6, BillingCycle.Annual => 12, _ => 1
    };

    private static decimal PriceFor(PlanTemplate? t, BillingCycle c, decimal fallback)
    {
        if (t == null) return fallback;
        return c switch
        {
            BillingCycle.Monthly => t.MonthlyPrice,
            BillingCycle.Quarterly => t.QuarterlyPrice,
            BillingCycle.SemiAnnual => t.SemiAnnualPrice,
            BillingCycle.Annual => t.AnnualPrice,
            _ => t.MonthlyPrice
        };
    }

    private async Task<string> NextInvoiceNumberAsync(DateTime now)
    {
        var period = now.ToString("yyyyMM");
        var like = $"SINV-{period}-%";
        var count = await _db.Subscriptions
            .CountAsync(s => s.RenewalInvoiceNumber != null && EF.Functions.Like(s.RenewalInvoiceNumber, like));
        return $"SINV-{period}-{count + 1:D4}";
    }

    private async Task SendInvoiceEmailAsync(Guid companyId, string number, decimal amount, DateTime dueDate)
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
            var msg = $"ใบแจ้งหนี้ค่าบริการเลขที่ {number} ยอด {amount:N2} บาท "
                    + $"(ต่ออายุก่อน {dueDate.AddHours(7):dd/MM/yyyy}) — คลิกเพื่อชำระและต่ออายุ";
            await _email.SendNotificationEmailAsync(owner.Email, owner.FullName ?? "ลูกค้า",
                $"ใบแจ้งหนี้ค่าบริการ {number}", msg, "/pages/subscription.html");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ส่งอีเมลใบแจ้งหนี้ไม่สำเร็จ company {CompanyId}", companyId); }
    }

    private string BuildInvoiceHtml(Company buyer, SiteSettings? s, string number, DateTime issuedAt,
        DateTime dueDate, decimal amount, SubscriptionPlan plan, BillingCycle cycle, int months)
    {
        var seller = Seller(s);
        var isVat = s != null && s.PlatformIsVatRegistered && IsValidTaxId(seller.TaxId);
        decimal subtotal, vat, grand;
        if (isVat)
        {
            if (s!.PlatformPriceIncludesVat) { vat = Math.Round(amount * 7m / 107m, 2, MidpointRounding.AwayFromZero); subtotal = amount - vat; grand = amount; }
            else { subtotal = amount; vat = Math.Round(amount * 0.07m, 2, MidpointRounding.AwayFromZero); grand = amount + vat; }
        }
        else { subtotal = amount; vat = 0m; grand = amount; }

        var sellerBranchLabel = seller.Branch == "00000" ? "สำนักงานใหญ่" : $"สาขาที่ {seller.Branch}";

        var sb = new StringBuilder();
        sb.Append(StyleBlock());
        sb.Append("<div class='doc'>");
        sb.Append("<div class='head'><div>");
        sb.Append($"<div class='seller-name'>{Enc(seller.Name)}</div>");
        if (!string.IsNullOrWhiteSpace(seller.Address)) sb.Append($"<div class='muted'>{Enc(seller.Address)}</div>");
        if (isVat) sb.Append($"<div class='muted'>เลขประจำตัวผู้เสียภาษี: {Enc(seller.TaxId)} ({Enc(sellerBranchLabel)})</div>");
        var contact = string.Join("  ", new[] {
            string.IsNullOrWhiteSpace(seller.Phone) ? null : $"โทร {Enc(seller.Phone)}",
            string.IsNullOrWhiteSpace(seller.Email) ? null : Enc(seller.Email) }.Where(x => x != null));
        if (contact.Length > 0) sb.Append($"<div class='muted'>{contact}</div>");
        sb.Append("</div>");
        sb.Append("<div class='title'><h1>ใบแจ้งหนี้ค่าบริการ</h1>");
        sb.Append($"<div class='muted'>เลขที่: <strong>{Enc(number)}</strong></div>");
        sb.Append($"<div class='muted'>วันที่: {issuedAt.AddHours(7):dd/MM/yyyy}</div>");
        sb.Append($"<div class='muted'>กำหนดชำระ: {dueDate.AddHours(7):dd/MM/yyyy}</div>");
        sb.Append("</div></div>");

        sb.Append("<div class='meta'><div class='box'><div class='label'>เรียกเก็บจาก</div>");
        sb.Append($"<div><strong>{Enc(buyer.Name)}</strong></div>");
        if (!string.IsNullOrWhiteSpace(buyer.Address)) sb.Append($"<div class='muted'>{Enc(buyer.Address)}</div>");
        if (isVat && IsValidTaxId(buyer.TaxId)) sb.Append($"<div class='muted'>เลขผู้เสียภาษี: {Enc(buyer.TaxId)}</div>");
        sb.Append("</div></div>");

        sb.Append("<table><thead><tr><th>รายการ</th><th class='r'>จำนวนเงิน</th></tr></thead><tbody>");
        sb.Append($"<tr><td>ค่าบริการระบบบัญชีออนไลน์ — {Enc(plan.ToString())} · {Enc(cycle.ToString())} · {months} เดือน</td>");
        sb.Append($"<td class='r'>{subtotal:N2}</td></tr></tbody></table>");

        sb.Append("<div class='sum'>");
        if (isVat)
        {
            sb.Append($"<div class='row'><span>มูลค่าก่อนภาษี</span><span>{subtotal:N2}</span></div>");
            sb.Append($"<div class='row'><span>ภาษีมูลค่าเพิ่ม 7%</span><span>{vat:N2}</span></div>");
        }
        sb.Append($"<div class='row grand'><span>ยอดชำระ</span><span>{grand:N2} บาท</span></div></div>");

        sb.Append("<div class='pay'>💳 ชำระเงินและต่ออายุได้ที่หน้า \"การสมัครสมาชิก\" ในระบบ — "
                + "อัปโหลดสลิปเพื่อให้ทีมงานตรวจสอบและต่ออายุอัตโนมัติ</div>");
        sb.Append("<div class='foot'><div>เอกสารออกโดยระบบอัตโนมัติ (ยังไม่ใช่ใบเสร็จ — จะออกใบเสร็จเมื่อชำระเงินแล้ว)</div></div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

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
                vat = Math.Round(gross * 7m / 107m, 2, MidpointRounding.AwayFromZero);
                subtotal = gross - vat;
                grandTotal = gross;
            }
            else
            {
                subtotal = gross;
                vat = Math.Round(gross * 0.07m, 2, MidpointRounding.AwayFromZero);
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
        sb.Append(StyleBlock());
        sb.Append("<div class='doc'>");

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
