using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// e-Tax Invoice Service - สร้าง XML ตามมาตรฐาน ETDA / กรมสรรพากร
/// รองรับ e-Tax Invoice / e-Receipt พร้อม X.509 Digital Signing + RD API Submission
/// </summary>
public partial class EtaxInvoiceService : IEtaxInvoiceService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EtaxInvoiceService> _logger;
    private readonly IPdfGenerationService _pdfService;
    private readonly IWebHostEnvironment _env;
    private readonly ISecretProtector _secrets;

    public EtaxInvoiceService(
        AccountingDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        ILogger<EtaxInvoiceService> logger,
        IPdfGenerationService pdfService,
        IWebHostEnvironment env,
        ISecretProtector secrets)
    {
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _pdfService = pdfService;
        _env = env;
        _secrets = secrets;
    }

    /// <summary>Get e-Tax config for a company: per-company settings override global config</summary>
    private async Task<(string? CertPath, string? CertPassword, string? ApiKey, string? ApiSecret,
        bool TestMode, bool AutoSign, bool AutoSubmit, string ServiceProvider)> GetEtaxConfig(Guid companyId)
    {
        var companySettings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId);

        // Per-company settings override global config
        if (companySettings?.EtaxEnabled == true)
        {
            return (
                companySettings.EtaxCertificatePath,
                _secrets.Unprotect(companySettings.EtaxCertificatePassword),
                companySettings.EtaxRdApiKey,
                _secrets.Unprotect(companySettings.EtaxRdApiSecret),
                companySettings.EtaxTestMode,
                companySettings.EtaxAutoSign,
                companySettings.EtaxAutoSubmit,
                companySettings.EtaxServiceProvider ?? _config["Etax:ServiceProvider"] ?? "RD"
            );
        }

        // Fallback to global appsettings.json
        return (
            _config["Etax:CertificatePath"],
            _config["Etax:CertificatePassword"],
            _config["Etax:RdApiKey"],
            _config["Etax:RdApiSecret"],
            _config.GetValue<bool>("Etax:RdTestMode"),
            _config.GetValue<bool>("Etax:AutoSign"),
            _config.GetValue<bool>("Etax:AutoSubmit"),
            _config["Etax:ServiceProvider"] ?? "RD"
        );
    }

    public async Task<EtaxInvoiceResponse> GenerateAsync(Guid companyId, GenerateEtaxRequest request)
    {
        var document = await _db.Documents
            .Include(d => d.Lines)
            // สาขาผู้ออกใบ — ไม่ include แล้ว TXID/ที่อยู่ผู้ขายใน XML จะตกกลับไป
            // เป็นของสำนักงานใหญ่เงียบ ๆ ทั้งที่ใบออกจากสาขา (§86/4 + ETDA)
            .Include(d => d.Branch)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, document);

        // Only TaxInvoice, Receipt, DebitNote, CreditNote can be e-Tax
        if (document.DocumentType != DocumentType.TaxInvoice && document.DocumentType != DocumentType.Receipt
            && document.DocumentType != DocumentType.DebitNote && document.DocumentType != DocumentType.CreditNote)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะใบกำกับภาษี, ใบเสร็จรับเงิน, ใบเพิ่มหนี้, ใบลดหนี้ เท่านั้น");

        if (document.Status == DocumentStatus.Draft)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารฉบับร่างได้ กรุณาอนุมัติเอกสารก่อน");
        if (document.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารที่ยกเลิกแล้ว");
        // ⚠️ เดิมบังคับ `Status == Approved` **เป๊ะ ๆ** ซึ่งเป็นสถานะชั่วคราวมาก:
        // พอส่งอีเมล/รับชำระ เอกสารเดินไป Sent → PartiallyPaid → Paid ทันที ⇒
        // **ใบกำกับที่รับเงินแล้วสร้าง e-Tax ไม่ได้เลย** ทั้งที่มันคือใบที่อนุมัติ
        // แล้วแท้ ๆ (และใบเสร็จ settlement เกิดมาเป็น Paid ตั้งแต่แรก จึงไม่มีวัน
        // ผ่านด่านนี้). สิ่งที่ต้องกันจริงคือ "ยังไม่ผ่านการอนุมัติ" กับ "ถูกยกเลิก"
        // ซึ่งดักไว้หมดแล้วด้านบน + WaitingApproval/Rejected ด้านล่าง
        if (document.Status is DocumentStatus.WaitingApproval or DocumentStatus.Rejected)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะเอกสารที่อนุมัติแล้วเท่านั้น");

        if (await _db.EtaxInvoices.AnyAsync(e => e.DocumentId == document.Id && e.CompanyId == companyId && e.Status != EtaxStatus.Error))
            throw new InvalidOperationException("เอกสารนี้มี e-Tax Invoice แล้ว");

        // ===== e-Tax = "ใบกำกับภาษีในรูปอิเล็กทรอนิกส์" — ใบที่ไม่ใช่ใบกำกับ ห้ามส่ง =====
        // DocumentType.Receipt ถูก map เป็น TypeCode T03 "ใบเสร็จรับเงิน/ใบกำกับภาษี"
        // เสมอ ซึ่งประกาศต่อกรมสรรพากรว่าใบนี้ "เป็นใบกำกับภาษี" ถ้าปล่อยให้ใบที่
        // ตั้งใจไม่ให้เป็นใบกำกับหลุดออกไป จะขัดกันเองสามทาง:
        //   • กระดาษ/PDF พิมพ์หัวว่า "ใบเสร็จรับเงิน" (ไม่มีคำว่าใบกำกับภาษี)
        //   • ภ.พ.30 ยังไม่มี VAT ก้อนนี้ (มัดจำ deferred พักที่ 21913)
        //   • แต่ XML บอก RD ว่าเป็นใบกำกับ T03 → ผู้ซื้อเคลมภาษีซื้อจาก e-Tax
        //     ที่ผู้ขายไม่เคยนำส่ง = ทั้งสองฝั่งโดนประเมิน
        if (document.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher)
        {
            // มัดจำที่ VAT ยังพักรอ (tax point ยังไม่เกิด) — ใบกำกับตัวจริงจะออก
            // ตอนส่งมอบ/รับรู้ ค่อยสร้าง e-Tax ที่ใบนั้น
            if (document.IsDeposit && document.DepositOutputVatDeferred
                && document.DepositOutputVatRecognizedAt == null)
                throw new InvalidOperationException(
                    $"ใบมัดจำ {document.DocumentNumber} ตั้งค่าเป็น \"VAT รอเรียกเก็บ\" (ยังไม่ถึงจุดรับผิด §78) "
                    + "จึงยังไม่ใช่ใบกำกับภาษี — ออก e-Tax ไม่ได้. "
                    + "e-Tax จะออกที่ใบกำกับภาษีตอนส่งมอบ/รับรู้รายได้ "
                    + "(หรือเปลี่ยนใบนี้เป็นมัดจำที่เก็บ VAT ทันที ถ้าเป็นการรับชำระราคาจริง)");

            // ใบเสร็จที่ "รับชำระใบกำกับภาษี" — VAT ถูกรายงานที่ใบกำกับต้นทางแล้ว
            // และกระดาษก็พิมพ์หัว "ใบเสร็จรับเงิน" เปล่า ๆ โดยตั้งใจ (กติกา
            // SettlesTaxInvoiceSource ใน ComputeDocumentTitle — กันลูกค้าถือกระดาษ
            // ที่มีคำว่าใบกำกับภาษี 2 ใบจากการขายครั้งเดียว = เคลมภาษีซื้อซ้ำ).
            // ปล่อยให้ export จะได้ T03 "ใบเสร็จรับเงิน/ใบกำกับภาษี" = ประกาศต่อ RD
            // ว่าเป็นใบกำกับใบที่สอง ซึ่งขัดกับทั้งกระดาษและ ภ.พ.30 ของตัวเอง
            var settlesTaxInvoice = document.RelatedDocumentId.HasValue
                && await _db.Documents.AsNoTracking().AnyAsync(s =>
                    s.Id == document.RelatedDocumentId.Value && s.CompanyId == companyId
                    && s.DocumentType == DocumentType.TaxInvoice);
            if (settlesTaxInvoice)
                throw new InvalidOperationException(
                    $"ใบเสร็จ {document.DocumentNumber} เป็นหลักฐานรับชำระของใบกำกับภาษีที่ออกไปแล้ว "
                    + "— ใบกำกับตัวจริงคือใบต้นทาง (VAT อยู่ใน ภ.พ.30 ที่ใบนั้น). "
                    + "ให้สร้าง e-Tax ที่ **ใบกำกับภาษีต้นทาง** แทน มิฉะนั้นจะเป็นการแจ้งใบกำกับ "
                    + "ซ้ำสองใบจากการขายครั้งเดียว");

            // ใบที่ข้อมูลผู้ซื้อไม่ครบ §86/4 / ผู้ซื้อไม่ประสงค์รับใบกำกับ →
            // หัวกระดาษไม่มีคำว่าใบกำกับ ส่ง T03 ไม่ได้เช่นกัน
            if (document.VatAmount > 0.005m && TaxService.NotFullTaxInvoice(document))
            {
                var missing = document.Contact == null
                    ? "ข้อมูลผู้ซื้อ"
                    : string.Join(", ", Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(document.Contact));
                throw new InvalidOperationException(
                    $"เอกสาร {document.DocumentNumber} ยังไม่ใช่ใบกำกับภาษีเต็มรูป "
                    + (document.BuyerDeclinedTaxInvoice
                        ? "(ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี)"
                        : $"— ขาด {missing}")
                    + " — e-Tax คือใบกำกับภาษีในรูปอิเล็กทรอนิกส์ จึงส่งไม่ได้. "
                    + "เติมข้อมูลผู้ซื้อให้ครบแล้วสร้างใหม่ (หัวเอกสารจะเป็น "
                    + "\"ใบกำกับภาษี/ใบเสร็จรับเงิน\" เอง)");
            }
        }

        // ===== CN/DN gate: ต้องมีใบอ้างอิง + ต้องเป็นฝั่งขายเท่านั้น =====
        // §86/9-10 + Schematron DCN-AdditionalReferencedDocument: ใบลดหนี้/เพิ่มหนี้
        // ต้องอ้างเลขที่+วันที่ใบกำกับเดิม — ไม่มี ref = XML ใส่ "-" + มูลค่าเดิมเท็จ
        // และ CN/DN "ฝั่งซื้อ" (อ้าง PI/Expense/CIL/PV) คือใบที่ *ผู้ขาย* เป็นผู้ออก
        // เราเป็นเพียงผู้บันทึก — ห้ามสร้าง/เซ็น e-Tax แทนผู้ขาย (บทบาท Seller/Buyer
        // ใน XML จะสลับผิดด้วย)
        if (document.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote)
        {
            var cnDnLabel = document.DocumentType == DocumentType.CreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้";
            if (!document.RelatedDocumentId.HasValue)
                throw new InvalidOperationException(
                    $"{cnDnLabel}ยังไม่ได้อ้างอิงใบกำกับภาษีต้นฉบับ — e-Tax {cnDnLabel} (ETDA) บังคับต้องมี"
                    + "เลขที่+วันที่+มูลค่าใบเดิม (§86/9-10) กรุณาระบุเอกสารต้นฉบับก่อน");
            var cnDnSrcType = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == document.RelatedDocumentId.Value && d.CompanyId == companyId)
                .Select(d => (DocumentType?)d.DocumentType)
                .FirstOrDefaultAsync();
            if (cnDnSrcType is DocumentType.PurchaseInvoice or DocumentType.Expense
                or DocumentType.CertificateInLieu or DocumentType.PaymentVoucher)
                throw new InvalidOperationException(
                    $"{cnDnLabel}นี้เป็นฝั่งซื้อ (อ้างเอกสารซื้อ) — ผู้ขายเป็นผู้ออกใบจริง เราเป็นเพียงผู้บันทึก"
                    + " จึงออก e-Tax แทนผู้ขายไม่ได้ (ไฟล์ e-Tax ต้องมาจากระบบของผู้ขาย)");
        }

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        // ===== Pre-flight validation & auto-fix (prevent Schematron errors) =====
        if (string.IsNullOrWhiteSpace(company.TaxId))
            throw new InvalidOperationException("กรุณาตั้งค่าเลขประจำตัวผู้เสียภาษีของบริษัทก่อน");

        // Auto-clean: strip dashes/dots/spaces from TaxId (users often enter "0-1234-56789-01-2")
        var sellerTaxClean = new string((company.TaxId ?? "").Where(char.IsDigit).ToArray());
        if (sellerTaxClean.Length != 13)
            throw new InvalidOperationException(
                $"เลขประจำตัวผู้เสียภาษีของบริษัทต้องเป็นตัวเลข 13 หลัก (ปัจจุบัน: \"{company.TaxId}\" → {sellerTaxClean.Length} หลัก)");

        // Auto-clean: strip non-digits from branch code
        var sellerBranchClean = new string((company.BranchCode ?? "").Where(char.IsDigit).ToArray());
        if (sellerBranchClean.Length == 0) sellerBranchClean = "00000";
        else if (sellerBranchClean.Length > 5) sellerBranchClean = sellerBranchClean.Substring(0, 5);
        else if (sellerBranchClean.Length < 5) sellerBranchClean = sellerBranchClean.PadLeft(5, '0');

        // Persist cleaned values so future calls don't hit the same issue
        var dirty = false;
        if (company.TaxId != sellerTaxClean) { company.TaxId = sellerTaxClean; dirty = true; }
        if (company.BranchCode != sellerBranchClean) { company.BranchCode = sellerBranchClean; dirty = true; }
        if (dirty) await _db.SaveChangesAsync();

        if (string.IsNullOrWhiteSpace(document.Contact?.TaxId))
            throw new InvalidOperationException("กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ");

        // Auto-clean buyer TaxId too
        var buyerTaxClean = new string((document.Contact.TaxId ?? "").Where(char.IsDigit).ToArray());
        if (document.Contact.TaxId != buyerTaxClean && buyerTaxClean.Length > 0)
        {
            document.Contact.TaxId = buyerTaxClean;
            await _db.SaveChangesAsync();
        }
        // ต้องครบ 13 หลักเหมือนฝั่ง seller (หรือ 18 = TaxId+สาขาที่ผู้ใช้ต่อมาเอง)
        // — ถ้าปล่อยผ่าน ComposeTxId จะ "เติมศูนย์แต่งเลขให้ครบ 13" แล้วยื่นเลข
        // ประจำตัวผู้เสียภาษีปลอมไปกรมสรรพากร ซึ่งแก้ย้อนหลังไม่ได้
        if (buyerTaxClean.Length != 13 && buyerTaxClean.Length != 18)
            throw new InvalidOperationException(
                $"เลขประจำตัวผู้เสียภาษีของผู้ซื้อ \"{document.Contact.Name}\" ต้องเป็นตัวเลข 13 หลัก "
                + $"(ปัจจุบัน {buyerTaxClean.Length} หลัก: \"{document.Contact.TaxId}\") — แก้ที่ข้อมูลผู้ติดต่อก่อนออก e-Tax");

        if (string.IsNullOrWhiteSpace(company.Address) && string.IsNullOrWhiteSpace(company.SubDistrict))
            throw new InvalidOperationException("กรุณาตั้งค่าที่อยู่บริษัทก่อนสร้าง e-Tax (ต้องมีอย่างน้อย ตำบล อำเภอ จังหวัด รหัสไปรษณีย์)");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var count = await _db.EtaxInvoices.CountAsync(e => e.CompanyId == companyId);
            var etaxRef = $"ETAX-{company.TaxId}-{DateTime.UtcNow:yyyyMMdd}-{(count + 1):D6}";

            // For CN/DN, load original document for OriginalDocumentReference per ETDA spec
            Document? originalDoc = null;
            decimal priorCnDnNet = 0m;
            if ((document.DocumentType == DocumentType.CreditNote || document.DocumentType == DocumentType.DebitNote)
                && document.RelatedDocumentId.HasValue)
            {
                originalDoc = await _db.Documents.FirstOrDefaultAsync(d =>
                    d.Id == document.RelatedDocumentId.Value && d.CompanyId == companyId);
                // มูลค่า "ตามใบเดิม" บน XML ต้องสะท้อน CN/DN ที่ออกก่อนหน้าแล้ว
                // (§86/9-10: ใบที่สองแสดงมูลค่าหลังปรับใบแรก ไม่ใช่ฐานดิบ) —
                // net = ΣCN ก่อนหน้า (ลด) − ΣDN ก่อนหน้า (เพิ่ม)
                if (originalDoc != null)
                {
                    var priorAdjusts = await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId
                            && d.RelatedDocumentId == originalDoc.Id
                            && d.Id != document.Id
                            && (d.DocumentType == DocumentType.CreditNote
                                || d.DocumentType == DocumentType.DebitNote)
                            && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                            && d.Status != DocumentStatus.Draft && !d.IsDeleted
                            && d.CreatedAt < document.CreatedAt)
                        .Select(d => new { d.DocumentType, d.SubTotal })
                        .ToListAsync();
                    priorCnDnNet = priorAdjusts.Sum(a =>
                        a.DocumentType == DocumentType.CreditNote ? a.SubTotal : -a.SubTotal);
                }
            }

            var xml = BuildEtaxXml(document, company, etaxRef, originalDoc, priorCnDnNet);

            var etax = new EtaxInvoice
            {
                CompanyId = companyId,
                DocumentId = document.Id,
                EtaxRefNumber = etaxRef,
                XmlContent = xml,
                Status = EtaxStatus.Generated,
                SellerName = company.Name,
                SellerTaxId = company.TaxId,
                // สาขาผู้ออกใบ (snapshot ลง EtaxInvoice) — ต้องตรงกับที่อยู่ใน XML
                SellerBranch = DocumentIssuerBranch.ResolveCode(
                    document.IssuerBranchCode, document.Branch?.TaxBranchCode, company.BranchCode),
                SellerAddress = document.Branch != null && !document.Branch.IsDeleted
                    && DocumentIssuerBranch.UseBranchAddress(document.Branch.Address)
                    ? $"{document.Branch.Address} {document.Branch.SubDistrict} {document.Branch.District} {document.Branch.Province} {document.Branch.PostalCode}"
                    : $"{company.Address} {company.SubDistrict} {company.District} {company.Province} {company.PostalCode}",
                BuyerName = document.Contact.Name,
                BuyerTaxId = document.Contact.TaxId,
                BuyerBranch = document.Contact.BranchCode,
                BuyerAddress = document.Contact.Address
            };

            _db.EtaxInvoices.Add(etax);
            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            // Auto-sign if configured (per-company or global)
            var etaxConfig = await GetEtaxConfig(companyId);
            if (etaxConfig.AutoSign && request.SignDigitally)
            {
                return await SignAsync(companyId, etax.Id);
            }

            return MapToResponse(etax);
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<EtaxInvoiceResponse> GetByIdAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices
            .FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        return MapToResponse(etax);
    }

    public async Task<EtaxInvoiceResponse> GetByDocumentIdAsync(Guid companyId, Guid documentId)
    {
        var etax = await _db.EtaxInvoices
            .FirstOrDefaultAsync(e => e.DocumentId == documentId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice สำหรับเอกสารนี้");
        return MapToResponse(etax);
    }

    public async Task<PagedResponse<EtaxInvoiceResponse>> GetAllAsync(Guid companyId, EtaxStatus? status, PagedRequest request)
    {
        var query = _db.EtaxInvoices.Where(e => e.CompanyId == companyId);
        if (status.HasValue) query = query.Where(e => e.Status == status.Value);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<EtaxInvoiceResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    // ===== X.509 Digital Signing =====

    public async Task<EtaxInvoiceResponse> SignAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (etax.Status != EtaxStatus.Generated && etax.Status != EtaxStatus.Error)
            throw new InvalidOperationException("สามารถลงนามได้เฉพาะ e-Tax ที่สร้างแล้วหรือที่เกิดข้อผิดพลาดเท่านั้น");

        var etaxConfig = await GetEtaxConfig(companyId);
        var certPath = etaxConfig.CertPath;
        var certPassword = etaxConfig.CertPassword;

        try
        {
            string signedXml;
            string certSerialNumber;
            bool usedRealCert;

            if (!string.IsNullOrEmpty(certPath) && System.IO.File.Exists(certPath))
            {
                // Production: Use real X.509 certificate
                (signedXml, certSerialNumber) = SignXmlWithCertificate(etax.XmlContent, certPath, certPassword);
                usedRealCert = true;
                _logger.LogInformation("e-Tax {EtaxRef} signed with X.509 certificate {CertSerial}",
                    etax.EtaxRefNumber, certSerialNumber);
            }
            else
            {
                // ── ไม่มี X.509 cert จริง ──
                // Production **ห้าม** dev-sign: SignXmlDevelopment สร้าง RSA key
                // ทิ้งขว้างต่อครั้ง (ไร้ความหมายทางกฎหมายต่อ RD) — ถ้าประทับ Signed
                // + AutoSubmit จะส่งเอกสารเซ็นปลอมไปสรรพากร. บล็อกด้วย env.
                if (_env.IsProduction())
                    throw new InvalidOperationException(
                        "ยังไม่ได้ตั้งค่าใบรับรองดิจิทัล X.509 (Etax:CertificatePath) — " +
                        "ลงนาม e-Tax บน production ไม่ได้ (การเซ็นแบบ development ใช้กับสรรพากรไม่ได้). " +
                        "กรุณาติดตั้ง cert ที่ ETDA/CA รับรอง ก่อนออก e-Tax จริง");
                // non-production: dev-sign ได้เพื่อทดสอบ flow (แต่ห้าม auto-submit ไป RD)
                _logger.LogWarning("No X.509 certificate configured. Using DEVELOPMENT signing for e-Tax {EtaxRef} (non-production only, will NOT auto-submit)",
                    etax.EtaxRefNumber);
                (signedXml, certSerialNumber) = SignXmlDevelopment(etax.XmlContent, etax.EtaxRefNumber);
                usedRealCert = false;
            }

            var signatureHash = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(signedXml)));

            etax.XmlContent = signedXml;
            etax.DigitalSignature = signatureHash;
            etax.CertificateSerialNumber = certSerialNumber;
            etax.SignedAt = DateTime.UtcNow;
            etax.Status = EtaxStatus.Signed;
            etax.ErrorMessage = null;
            etax.ErrorCode = null;

            await _db.SaveChangesAsync();

            // Auto-submit if configured — **เฉพาะที่เซ็นด้วย cert จริง** เท่านั้น
            // (กันส่งเอกสารเซ็นแบบ development ไปสรรพากร แม้ AutoSubmit เปิด)
            if (etaxConfig.AutoSubmit && usedRealCert)
            {
                return await SubmitToRevenueAsync(companyId, etax.Id);
            }
            if (etaxConfig.AutoSubmit && !usedRealCert)
                _logger.LogWarning("e-Tax {EtaxRef}: AutoSubmit ข้าม — เซ็นแบบ development ยังส่ง RD ไม่ได้", etax.EtaxRefNumber);

            return MapToResponse(etax);
        }
        catch (Exception ex)
        {
            etax.Status = EtaxStatus.Error;
            etax.ErrorMessage = $"ลงนามล้มเหลว: {ex.Message}";
            etax.ErrorCode = "SIGN_ERROR";
            await _db.SaveChangesAsync();
            throw new InvalidOperationException($"ลงนามดิจิทัลล้มเหลว: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sign XML with real X.509 certificate (.p12/.pfx)
    /// ใช้ RSA-SHA256 ตามมาตรฐาน W3C XML Signature (XMLDsig)
    /// </summary>
    private static (string signedXml, string certSerialNumber) SignXmlWithCertificate(
        string xmlContent, string certPath, string? certPassword)
    {
        var cert = new X509Certificate2(certPath, certPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

        if (cert.NotAfter < DateTime.UtcNow)
            throw new InvalidOperationException($"ใบรับรองดิจิทัลหมดอายุแล้ว (หมดอายุ: {cert.NotAfter:yyyy-MM-dd})");

        var rsaKey = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("ใบรับรองดิจิทัลไม่มี Private Key กรุณาใช้ไฟล์ .p12 หรือ .pfx ที่มี Private Key");

        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xmlContent);

        var signedXmlObj = new SignedXml(xmlDoc) { SigningKey = rsaKey };

        // Reference to entire document
        var reference = new Reference("") { DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXmlObj.AddReference(reference);

        // Signature method
        signedXmlObj.SignedInfo!.CanonicalizationMethod = "http://www.w3.org/2001/10/xml-exc-c14n#";
        signedXmlObj.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

        // Include X.509 certificate data
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXmlObj.KeyInfo = keyInfo;

        signedXmlObj.ComputeSignature();

        // Append signature to document
        var signatureElement = signedXmlObj.GetXml();
        xmlDoc.DocumentElement!.AppendChild(xmlDoc.ImportNode(signatureElement, true));

        return (xmlDoc.OuterXml, cert.SerialNumber);
    }

    /// <summary>
    /// Development signing fallback when no certificate is configured.
    /// Uses RSA key pair generated in-memory. NOT suitable for production.
    /// </summary>
    private static (string signedXml, string certSerialNumber) SignXmlDevelopment(string xmlContent, string etaxRef)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xmlContent);

        using var rsa = RSA.Create(2048);
        var signedXmlObj = new SignedXml(xmlDoc) { SigningKey = rsa };

        var reference = new Reference("") { DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXmlObj.AddReference(reference);

        signedXmlObj.SignedInfo!.CanonicalizationMethod = "http://www.w3.org/2001/10/xml-exc-c14n#";
        signedXmlObj.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

        // Add RSA key value info
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new RSAKeyValue(rsa));
        signedXmlObj.KeyInfo = keyInfo;

        signedXmlObj.ComputeSignature();

        var signatureElement = signedXmlObj.GetXml();
        xmlDoc.DocumentElement!.AppendChild(xmlDoc.ImportNode(signatureElement, true));

        var devSerial = $"DEV-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(etaxRef)))[..16]}";
        return (xmlDoc.OuterXml, devSerial);
    }

    // ===== RD API Submission =====

    public async Task<EtaxInvoiceResponse> SubmitToRevenueAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        if (etax.Status != EtaxStatus.Signed)
            throw new InvalidOperationException("ต้องลงนามดิจิทัลก่อนส่งกรมสรรพากร");

        if (string.IsNullOrWhiteSpace(etax.XmlContent))
            throw new InvalidOperationException("เนื้อหา XML ว่างเปล่า ไม่สามารถส่งกรมสรรพากรได้");
        if (string.IsNullOrWhiteSpace(etax.DigitalSignature))
            throw new InvalidOperationException("ไม่พบลายเซ็นดิจิทัล กรุณาลงนามเอกสารก่อน");

        var etaxConfig = await GetEtaxConfig(companyId);
        var isTestMode = etaxConfig.TestMode;
        var apiKey = etaxConfig.ApiKey;
        var apiSecret = etaxConfig.ApiSecret;
        var baseUrl = isTestMode
            ? _config["Etax:RdTestApiBaseUrl"]
            : _config["Etax:RdApiBaseUrl"];

        // If no API key configured, use offline mode (mark as submitted without calling API)
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogWarning("RD API not configured. Using offline submission mode for e-Tax {EtaxRef}", etax.EtaxRefNumber);
            etax.Status = EtaxStatus.Submitted;
            etax.SubmittedAt = DateTime.UtcNow;
            etax.SubmissionId = $"OFFLINE-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";
            etax.ErrorMessage = "โหมดออฟไลน์: กรุณาตั้งค่า RD API Key เพื่อส่งข้อมูลจริง";
            await _db.SaveChangesAsync();
            return MapToResponse(etax);
        }

        try
        {
            var submissionResult = await CallRdApi(baseUrl, apiKey, apiSecret!, etax);

            etax.Status = submissionResult.Accepted ? EtaxStatus.Accepted : EtaxStatus.Submitted;
            etax.SubmittedAt = DateTime.UtcNow;
            etax.SubmissionId = submissionResult.SubmissionId;
            etax.AcceptanceNumber = submissionResult.AcceptanceNumber;
            etax.AcceptedAt = submissionResult.Accepted ? DateTime.UtcNow : null;
            etax.ErrorMessage = submissionResult.ErrorMessage;
            etax.ErrorCode = submissionResult.ErrorCode;

            await _db.SaveChangesAsync();

            _logger.LogInformation("e-Tax {EtaxRef} submitted to RD. Status: {Status}, SubmissionId: {SubId}",
                etax.EtaxRefNumber, etax.Status, etax.SubmissionId);

            return MapToResponse(etax);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit e-Tax {EtaxRef} to RD", etax.EtaxRefNumber);
            etax.Status = EtaxStatus.Error;
            etax.ErrorMessage = $"ส่งข้อมูลล้มเหลว: {ex.Message}";
            etax.ErrorCode = "SUBMIT_ERROR";
            await _db.SaveChangesAsync();
            throw new InvalidOperationException($"ส่งข้อมูลกรมสรรพากรล้มเหลว: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Call Revenue Department e-Tax API
    /// API Reference: https://etax.rd.go.th/etax_developer
    /// </summary>
    private async Task<RdSubmissionResult> CallRdApi(string baseUrl, string apiKey, string apiSecret, EtaxInvoice etax)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        // Step 1: Get access token
        var tokenUrl = $"{baseUrl}/auth/token";
        var tokenRequest = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", apiKey),
            new KeyValuePair<string, string>("client_secret", apiSecret)
        });

        var tokenResponse = await client.PostAsync(tokenUrl, tokenRequest);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorBody = await tokenResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"RD Authentication failed ({tokenResponse.StatusCode}): {errorBody}");
        }

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenJson);
        var accessToken = tokenData.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("RD API ไม่ส่ง access_token กลับมา");

        // Step 2: Submit e-Tax XML
        var submitUrl = $"{baseUrl}/etaxinvoice/submit";
        var xmlBytes = Encoding.UTF8.GetBytes(etax.XmlContent);

        using var content = new MultipartFormDataContent();
        var xmlContent = new ByteArrayContent(xmlBytes);
        xmlContent.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        content.Add(xmlContent, "file", $"{etax.EtaxRefNumber}.xml");
        content.Add(new StringContent(etax.SellerTaxId), "taxId");
        content.Add(new StringContent(etax.EtaxRefNumber), "referenceNumber");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var submitResponse = await client.PostAsync(submitUrl, content);
        var responseBody = await submitResponse.Content.ReadAsStringAsync();

        if (submitResponse.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
            return new RdSubmissionResult
            {
                Accepted = result.TryGetProperty("status", out var statusProp) &&
                           statusProp.GetString() == "accepted",
                SubmissionId = result.TryGetProperty("submissionId", out var subProp)
                    ? subProp.GetString() ?? "" : $"RD-{DateTime.UtcNow:yyyyMMddHHmmss}",
                AcceptanceNumber = result.TryGetProperty("acceptanceNumber", out var accProp)
                    ? accProp.GetString() : null,
                ErrorMessage = null,
                ErrorCode = null
            };
        }

        // Handle RD error response
        var errorResult = new RdSubmissionResult
        {
            Accepted = false,
            SubmissionId = $"ERR-{DateTime.UtcNow:yyyyMMddHHmmss}",
            ErrorCode = $"RD_{(int)submitResponse.StatusCode}"
        };

        try
        {
            var errorJson = JsonSerializer.Deserialize<JsonElement>(responseBody);
            errorResult.ErrorMessage = errorJson.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString()
                : $"RD API Error: {submitResponse.StatusCode}";
            errorResult.ErrorCode = errorJson.TryGetProperty("errorCode", out var codeProp)
                ? codeProp.GetString()
                : errorResult.ErrorCode;
        }
        catch
        {
            errorResult.ErrorMessage = $"RD API Error ({submitResponse.StatusCode}): {responseBody[..Math.Min(200, responseBody.Length)]}";
        }

        return errorResult;
    }

    public async Task<string> GetXmlAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");
        return etax.XmlContent;
    }

    /// <summary>
    /// ยกเลิก e-Tax Invoice — set Status=Voided + บันทึกวันที่ยกเลิก
    /// เก็บ XML/PDF ไว้เพื่อ audit trail (ห้ามลบเอกสารที่ส่งกรมสรรพากรแล้ว)
    /// </summary>
    public async Task VoidAsync(Guid companyId, Guid etaxId)
    {
        var etax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.Id == etaxId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ e-Tax Invoice");

        // block เฉพาะ Accepted (ได้รับตอบรับจาก RD จริง) — Submitted (ยังไม่ตอบรับ)
        // ยกเลิกได้ก่อนนำส่ง ภ.พ.30 (สอดคล้องกับ guard ใน VoidDocumentAsync)
        if (etax.Status == EtaxStatus.Accepted)
            throw new InvalidOperationException(
                "ไม่สามารถยกเลิก e-Tax ที่ได้รับการตอบรับจากกรมสรรพากรแล้ว (Accepted) " +
                "ต้องดำเนินการขอยกเลิกที่กรมสรรพากรก่อน");

        if (etax.Status == EtaxStatus.Voided)
            throw new InvalidOperationException("e-Tax นี้ถูกยกเลิกไปแล้ว");

        etax.Status = EtaxStatus.Voided;
        etax.VoidedAt = DateTime.UtcNow;
        etax.VoidReason ??= "ยกเลิกโดยผู้ใช้";
        etax.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== XML Builder - ตามมาตรฐาน ETDA v2.0 / กรมสรรพากร =====
    // Conforms to: ETDA Recommendation 3-2560 v2.0 (Thai e-Tax Invoice & e-Receipt)
    // Schema: UN/CEFACT Cross Industry Invoice (CII) D16B subset
    // Validation: สรรพากร Schematron + XSD

    private string BuildEtaxXml(Document doc, Company company, string etaxRef, Document? originalDoc = null,
        decimal priorCnDnNet = 0m)
    {
        // Document-type-specific root element + RAM namespace prefix.
        // Per ETDA Schematron rules:
        //   TaxInvoice schema accepts TypeCode: 388 (ใบกำกับภาษี), T02 (ใบแจ้งหนี้/ใบกำกับภาษี),
        //                              T03 (ใบเสร็จรับเงิน/ใบกำกับภาษี), T04 (ใบส่งของ/ใบกำกับภาษี)
        //   Receipt schema accepts only T01 (ใบเสร็จรับเงิน standalone, non-VAT)
        //   DebitCreditNote schema accepts 80 (ใบเพิ่มหนี้), 81 (ใบลดหนี้)
        //
        // Our DocumentType.Receipt represents the Thai-VAT-compliant
        // "ใบเสร็จรับเงิน/ใบกำกับภาษี" combo → use TaxInvoice schema with T03.
        // (Pure standalone Receipt T01 isn't currently exposed; companies that
        // aren't VAT-registered shouldn't be issuing e-Tax anyway.)
        var rootElementName = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "TaxInvoice_CrossIndustryInvoice",
            DocumentType.Receipt => "TaxInvoice_CrossIndustryInvoice",   // T03 → TaxInvoice schema
            DocumentType.DebitNote => "DebitCreditNote_CrossIndustryInvoice",
            DocumentType.CreditNote => "DebitCreditNote_CrossIndustryInvoice",
            _ => "TaxInvoice_CrossIndustryInvoice"
        };
        var ramSuffix = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "TaxInvoice_ReusableAggregateBusinessInformationEntity",
            DocumentType.Receipt => "TaxInvoice_ReusableAggregateBusinessInformationEntity",
            DocumentType.DebitNote => "DebitCreditNote_ReusableAggregateBusinessInformationEntity",
            DocumentType.CreditNote => "DebitCreditNote_ReusableAggregateBusinessInformationEntity",
            _ => "TaxInvoice_ReusableAggregateBusinessInformationEntity"
        };
        var rsm = XNamespace.Get($"urn:etda:uncefact:data:standard:{rootElementName}:2");
        var ram = XNamespace.Get($"urn:etda:uncefact:data:standard:{ramSuffix}:2");

        // TypeCode per UN/EDIFACT 1001 + ETDA Schematron-validated codelist
        // ใบแจ้งหนี้/ใบกำกับภาษี (combined header) ต้องใช้ T02 ให้ชื่อใน XML
        // ตรงกับหัวกระดาษ PDF (TypeCode-name pairing บังคับ exact ตาม Schematron)
        var docTypeCode = doc.DocumentType switch
        {
            // ขายเงินสด B2B (IssuedAsCashReceipt) → T03 ใบเสร็จรับเงิน/ใบกำกับภาษี
            // (เช็คก่อน Combined/388 เพราะ cash sale = receipt+tax invoice ในใบเดียว)
            DocumentType.TaxInvoice when doc.IssuedAsCashReceipt => "T03",
            DocumentType.TaxInvoice when doc.CombinedInvoiceTaxInvoice => "T02",  // ใบแจ้งหนี้/ใบกำกับภาษี
            DocumentType.TaxInvoice => "388",
            DocumentType.Receipt => "T03",       // ใบเสร็จรับเงิน/ใบกำกับภาษี
            DocumentType.DebitNote => "80",
            DocumentType.CreditNote => "81",
            _ => "388"
        };
        // Name MUST match the TypeCode-name pairing per Schematron TIV-Document-003 /
        // DCN equivalents — exact strings, no extra qualifiers
        var docTypeName = doc.DocumentType switch
        {
            DocumentType.TaxInvoice when doc.IssuedAsCashReceipt => "ใบเสร็จรับเงิน/ใบกำกับภาษี",  // T03 pairing
            DocumentType.TaxInvoice when doc.CombinedInvoiceTaxInvoice => "ใบแจ้งหนี้/ใบกำกับภาษี",
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.Receipt => "ใบเสร็จรับเงิน/ใบกำกับภาษี",   // exact match required
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            DocumentType.CreditNote => "ใบลดหนี้",
            _ => "ใบกำกับภาษี"
        };
        // PurposeCode per ETDA ThaiMessageFunctionCode (rd1225) — required for CN/DN.
        // ต้อง map ตามเหตุผลจริงของใบ (สินค้า CDNG* / บริการ CDNS*) — เดิม hardcode
        // CDNG01 ทุกใบ ทำให้ CN คืนสินค้าแจ้ง RD เป็น "ลดราคา" ผิดประเภท
        var cnIsGoods = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.ProductCode));
        var purposeCode = doc.DocumentType switch
        {
            DocumentType.CreditNote => (doc.CreditNoteReason, cnIsGoods) switch
            {
                (CreditNoteReason.Return, _) => "CDNG05",       // รับคืนสินค้า
                (CreditNoteReason.Discount, true) => "CDNG01",  // ลดราคาสินค้า
                (CreditNoteReason.Discount, false) => "CDNS01", // ลดราคาค่าบริการ
                (CreditNoteReason.Adjustment, true) => "CDNG04",  // คำนวณราคาสูงกว่าจริง
                (CreditNoteReason.Adjustment, false) => "CDNS03",
                (CreditNoteReason.Writeoff, true) => "CDNG99",  // เหตุอื่น
                (CreditNoteReason.Writeoff, false) => "CDNS99",
                _ => cnIsGoods ? "CDNG99" : "CDNS99"
            },
            DocumentType.DebitNote => cnIsGoods ? "DBNG01" : "DBNS01",  // มูลค่าจริงสูงกว่าที่ระบุ
            _ => (string?)null
        };

        var vatRate = doc.Lines.Any(l => l.VatRate > 0)
            ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate)
            : 0m;
        var currency = doc.Currency ?? "THB";

        // Tax IDs: ETDA Schematron requires TXID = 13-digit TaxID + 5-digit branch (18 total)
        // Detect if user entered 13 (need branch suffix) or 18 (already concatenated).
        var sellerTaxIdSchemeId = DetermineTaxIdSchemeId(company.TaxId, isSeller: true);
        var buyerTaxIdSchemeId = DetermineTaxIdSchemeId(doc.Contact.TaxId,
            contactType: doc.Contact.ContactType);
        // สาขาผู้ออกใบ — resolver กลาง (snapshot > ทะเบียนสาขา > Company.BranchCode)
        // กิจการสาขาเดียว: doc.Branch = null ⇒ ได้ company.BranchCode เหมือนเดิมเป๊ะ
        var sellerBranchCode = DocumentIssuerBranch.ResolveCode(
            doc.IssuerBranchCode, doc.Branch?.TaxBranchCode, company.BranchCode);
        var sellerTxId = ComposeTxId(company.TaxId, sellerBranchCode, sellerTaxIdSchemeId);
        var buyerTxId = ComposeTxId(doc.Contact.TaxId, doc.Contact.BranchCode, buyerTaxIdSchemeId);

        // Per Schematron TIV-SellerTradeParty-009..012: when CountryID=TH, seller MUST
        // have BuildingNumber, CityName (อำเภอ), CitySubDivisionName (ตำบล), CountrySubDivisionID
        // (province) and 5-digit PostcodeCode. Provide safe fallbacks if data missing.
        var sellerParty = BuildSellerParty(ram, company, doc.Branch, sellerBranchCode);

        // Buyer rules are looser (TIV-BuyerTradeParty-007): structured OR unstructured
        // address acceptable. PostcodeCode required if CountryID=TH.
        var buyerParty = BuildBuyerParty(ram, doc.Contact, buyerTaxIdSchemeId, buyerTxId);

        // ShipToTradeParty: same address as buyer (delivery to billing address by default)
        var shipPostCode = NormalizePostcode(doc.Contact.PostalCode, doc.Contact.Address);
        var shipAddr = doc.Contact.Address ?? "-";
        var shipToParty = new XElement(ram + "ShipToTradeParty",
            new XElement(ram + "PostalTradeAddress",
                new XElement(ram + "PostcodeCode", shipPostCode),
                new XElement(ram + "LineOne", shipAddr),
                new XElement(ram + "CountryID",
                    new XAttribute("schemeID", "3166-1 alpha-2"), "TH")));

        // Header settlement
        var summationElements = new List<object?>();
        // For CN/DN: include OriginalInformationAmount + DifferenceInformationAmount
        if (doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote)
        {
            // มูลค่าตามใบเดิม "หลังปรับ CN/DN ก่อนหน้า" (§86/9-10) — ใบที่สอง
            // ต้องไม่แสดงฐานดิบซ้ำ ไม่งั้น Original − Difference ≠ ยอดคงเหลือจริง
            var originalAmount = Math.Max(0m, (originalDoc?.SubTotal ?? doc.SubTotal) - priorCnDnNet);
            summationElements.Add(new XElement(ram + "OriginalInformationAmount",
                originalAmount.ToString("0.##", CultureInfo.InvariantCulture)));
        }
        // ⚠️ doc.SubTotal เป็น "net of discount" อยู่แล้ว (DocumentService:562
        // subTotal += NetAmount = gross − discount) และ VatAmount คิดบน net.
        // ดังนั้น:
        //   LineTotalAmount = SubTotal (net, = Σ NetLineTotalAmount ต่อบรรทัด)
        //   AllowanceTotalAmount = 0 (ส่วนลดเป็น line-level แสดงใน
        //     SpecifiedTradeAllowanceCharge ต่อบรรทัดแล้ว — ไม่ใช่ doc-level)
        //   TaxBasisTotalAmount = SubTotal (ไม่หักส่วนลดซ้ำ — เดิม
        //     SubTotal − DiscountAmount หักซ้ำ → TaxBasis+Tax ≠ Grand → RD reject)
        //   GrandTotalAmount = SubTotal + VAT (= TaxBasis + Tax). ไม่ใช้
        //     doc.TotalAmount เพราะหัก WHT ออก (WHT แยกตอนจ่าย ไม่ใช่ face value
        //     ของใบกำกับ) → ถ้าใช้จะทำให้ TaxBasis+Tax ≠ Grand เมื่อมี WHT
        var inv2 = CultureInfo.InvariantCulture;
        summationElements.Add(new XElement(ram + "LineTotalAmount",
            doc.SubTotal.ToString("0.##", inv2)));
        if (doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote)
        {
            // ผลต่าง (§86/9-10) = มูลค่าของ CN/DN ใบนี้เอง — บรรทัดใน CN คือ
            // "ยอดที่ลด/เพิ่ม" ไม่ใช่มูลค่าที่ถูกต้องหลังปรับ. สูตรเดิม
            // (doc.SubTotal − ใบเดิม.SubTotal) ให้ค่าติดลบผิดความหมาย
            // (ใบเดิม 48,000 ลด 5,000 → −43,000 แทนที่จะเป็น 5,000)
            summationElements.Add(new XElement(ram + "DifferenceInformationAmount",
                doc.SubTotal.ToString("0.##", inv2)));
        }
        summationElements.Add(new XElement(ram + "AllowanceTotalAmount", "0.00"));
        summationElements.Add(new XElement(ram + "TaxBasisTotalAmount",
            doc.SubTotal.ToString("0.##", inv2)));
        summationElements.Add(new XElement(ram + "TaxTotalAmount",
            doc.VatAmount.ToString("0.##", inv2)));
        summationElements.Add(new XElement(ram + "GrandTotalAmount",
            (doc.SubTotal + doc.VatAmount).ToString("0.##", inv2)));

        // Header trade tax
        var headerTradeTax = new XElement(ram + "ApplicableTradeTax",
            new XElement(ram + "TypeCode", "VAT"),
            new XElement(ram + "CalculatedRate",
                vatRate.ToString("0.##", CultureInfo.InvariantCulture)),
            // SubTotal = Σ line.NetAmount (หักส่วนลดรายบรรทัด+ท้ายบิลแล้ว) —
            // ห้ามหัก DiscountAmount ซ้ำ ไม่งั้น BasisAmount ≠ TaxBasisTotalAmount
            // และ CalculatedAmount ≠ Basis×Rate → Schematron TIV reject
            new XElement(ram + "BasisAmount",
                doc.SubTotal.ToString("0.##", CultureInfo.InvariantCulture)),
            new XElement(ram + "CalculatedAmount",
                doc.VatAmount.ToString("0.##", CultureInfo.InvariantCulture)));

        // Reference to original — REQUIRED for CN/DN per Schematron DCN-AdditionalReferencedDocument-001..002.
        // IssuerAssignedID = original document number; ReferenceTypeCode = 388 (original tax invoice).
        // FormattedIssueDateTime uses udt: namespace per ETDA reference samples.
        XNamespace udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:6";
        XElement? additionalRef = null;
        if (doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote)
        {
            var origRef = originalDoc?.DocumentNumber ?? doc.Reference ?? "-";
            var origDate = originalDoc?.DocumentDate ?? doc.DocumentDate;
            additionalRef = new XElement(ram + "AdditionalReferencedDocument",
                new XElement(ram + "IssuerAssignedID", origRef),
                new XElement(ram + "ReferenceTypeCode", "388"),
                new XElement(ram + "FormattedIssueDateTime",
                    new XElement(udt + "DateTimeString",
                        new XAttribute("format", "102"),
                        origDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
        }

        // Line items (last in CII per ETDA — after Settlement)
        var lineItems = doc.Lines.OrderBy(l => l.LineOrder).Select((line, idx) =>
            BuildLineItem(ram, line, idx + 1, currency, doc.PricesIncludeVat)).ToList<object>();

        // Context parameter — ER3-2560 with scheme attributes
        var contextParameter = new XElement(ram + "GuidelineSpecifiedDocumentContextParameter",
            new XElement(ram + "ID",
                new XAttribute("schemeAgencyID", "ETDA"),
                new XAttribute("schemeVersionID", "v2.0"),
                "ER3-2560"));

        // ExchangedDocument — order: ID, Name, TypeCode, IssueDateTime, [PurposeCode], CreationDateTime, [IncludedNote]
        var exchangedDoc = new XElement(rsm + "ExchangedDocument",
            new XElement(ram + "ID", doc.DocumentNumber),
            new XElement(ram + "Name", docTypeName),
            new XElement(ram + "TypeCode", docTypeCode),
            new XElement(ram + "IssueDateTime", FormatIso(doc.DocumentDate)),
            purposeCode != null ? new XElement(ram + "PurposeCode", purposeCode) : null,
            new XElement(ram + "CreationDateTime", FormatIso(DateTime.UtcNow)),
            !string.IsNullOrWhiteSpace(doc.Notes)
                ? new XElement(ram + "IncludedNote",
                    new XElement(ram + "Subject", doc.Notes))
                : null);

        // Build full document — declare udt only when CN/DN (FormattedIssueDateTime present)
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var rootAttrs = new List<object>
        {
            new XAttribute(XNamespace.Xmlns + "rsm", rsm),
            new XAttribute(XNamespace.Xmlns + "ram", ram),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            // xsi:schemaLocation = namespace ของ root (ตรงกับ TakeTime/ETDA reference)
            new XAttribute(xsi + "schemaLocation", rsm.NamespaceName)
        };
        if (additionalRef != null)
            rootAttrs.Add(new XAttribute(XNamespace.Xmlns + "udt", udt));
        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(rsm + rootElementName,
                rootAttrs,

                new XElement(rsm + "ExchangedDocumentContext", contextParameter),

                exchangedDoc,

                // SupplyChainTradeTransaction order: Agreement → Delivery → Settlement → LineItem
                new XElement(rsm + "SupplyChainTradeTransaction",
                    new XElement(ram + "ApplicableHeaderTradeAgreement",
                        sellerParty,
                        buyerParty,
                        additionalRef),
                    new XElement(ram + "ApplicableHeaderTradeDelivery", shipToParty),
                    new XElement(ram + "ApplicableHeaderTradeSettlement",
                        new XElement(ram + "InvoiceCurrencyCode",
                            new XAttribute("listID", "ISO 4217 3A"),
                            currency),
                        headerTradeTax,
                        new XElement(ram + "SpecifiedTradeSettlementHeaderMonetarySummation",
                            summationElements.Where(e => e != null))),
                    lineItems
                )));

        return xml.ToString();
    }

    /// <summary>ISO 8601 เวลาไทยพร้อม offset +07:00 (ขมธอ.3-2560 บังคับ) —
    /// ค่าที่เก็บในระบบเป็น UTC (กฎโปรเจกต์) จึงต้องบวก 7 ชม. ก่อน format
    /// ไม่งั้นเอกสารที่ออกช่วง 00:00-07:00 น. จะได้วันที่ "เมื่อวาน" และ
    /// CreationDateTime (UtcNow) จะเพี้ยนจาก IssueDateTime 7 ชม.</summary>
    private static string FormatIso(DateTime dt)
    {
        var th = (dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            .AddHours(7);
        return th.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "+07:00";
    }

    /// <summary>ram:DefinedTradeContact (email + โทร) ตาม ETDA — ตรงกับ TakeTime.
    /// null เมื่อไม่มีทั้ง email/phone (ผู้เรียกใส่เป็น content แล้ว null จะถูกข้าม).
    /// ลำดับ: EmailURIUniversalCommunication ก่อน TelephoneUniversalCommunication.</summary>
    private static XElement? BuildDefinedTradeContact(XNamespace ram, string? email, string? phone)
    {
        var children = new List<object>();
        if (!string.IsNullOrWhiteSpace(email))
            children.Add(new XElement(ram + "EmailURIUniversalCommunication",
                new XElement(ram + "URIID", email!.Trim())));
        var ph = FormatEtdaPhone(phone);
        if (ph != null)
            children.Add(new XElement(ram + "TelephoneUniversalCommunication",
                new XElement(ram + "CompleteNumber", ph)));
        return children.Count == 0 ? null : new XElement(ram + "DefinedTradeContact", children);
    }

    /// <summary>เบอร์ไทยรูปแบบ ETDA: ตัด 0 นำหน้า + ใส่ "+66-" (ตรง TakeTime:
    /// 0634161496 → +66-634161496). null เมื่อไม่มีเลข.</summary>
    private static string? FormatEtdaPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.Length == 0) return null;
        if (d.StartsWith("0")) d = d.Substring(1);
        return "+66-" + d;
    }

    /// <summary>
    /// Compose ID per Schematron rules:
    ///   TXID  → 13-digit TaxID + 5-digit branch (18 chars total)
    ///   NIDN  → 13-digit national ID
    ///   CCPT  → passport (≤35 chars)
    ///   OTHR  → must be exactly "N/A"
    /// If user entered 18 digits already (TaxID+Branch concatenated), use as-is.
    /// </summary>
    private static string ComposeTxId(string? taxId, string? branchCode, string schemeId)
    {
        // Strip everything except digits for all numeric schemes
        var digits = new string((taxId ?? "").Where(char.IsDigit).ToArray());
        switch (schemeId)
        {
            case "TXID":
                // If already 18 digits (TaxID+Branch pre-concatenated), use as-is
                if (digits.Length == 18) return digits;
                // Ensure exactly 13-digit tax ID
                var tid = digits.Length >= 13 ? digits.Substring(0, 13) : digits.PadRight(13, '0');
                // Ensure exactly 5-digit branch (all-numeric per TIV-SellerTradeParty-013)
                var bd = new string((branchCode ?? "").Where(char.IsDigit).ToArray());
                if (bd.Length == 0) bd = "00000";
                else if (bd.Length > 5) bd = bd.Substring(0, 5);
                else if (bd.Length < 5) bd = bd.PadLeft(5, '0');
                var result = tid + bd;
                // Final sanity: must be exactly 18 digits
                if (result.Length != 18 || !result.All(char.IsDigit))
                    throw new InvalidOperationException(
                        $"e-Tax: เลขประจำตัวผู้เสียภาษี+สาขาต้องเป็นตัวเลข 18 หลัก (ได้: {result})");
                return result;
            case "NIDN":
                if (digits.Length < 13)
                    throw new InvalidOperationException(
                        $"e-Tax: เลขบัตรประชาชนต้องเป็นตัวเลข 13 หลัก (ได้ {digits.Length} หลัก)");
                return digits.Substring(0, 13);
            case "CCPT":
                var raw = (taxId ?? "").Trim();
                return raw.Length > 35 ? raw.Substring(0, 35) : raw;
            case "OTHR":
                return "N/A";
            default:
                return (taxId ?? "").Trim();
        }
    }

    /// <summary>
    /// Pick Schematron-allowed schemeID using ContactType when available.
    ///   JuristicPerson / GovernmentAgency → TXID (13-digit tax + 5-digit branch = 18)
    ///   Individual → NIDN (13-digit national ID)
    ///   No tax ID → OTHR ("N/A")
    /// Seller always falls back to TXID.
    /// </summary>
    private static string DetermineTaxIdSchemeId(string? taxId, bool isSeller = false,
        ContactType? contactType = null)
    {
        if (string.IsNullOrWhiteSpace(taxId))
            return isSeller ? "TXID" : "OTHR";
        var raw = taxId.Trim().Replace("-", "").Replace(" ", "").Replace(".", "");

        // Use ContactType when explicitly set
        if (contactType.HasValue)
        {
            return contactType.Value switch
            {
                ContactType.Individual => raw.All(char.IsDigit) && raw.Length == 13 ? "NIDN" : "OTHR",
                ContactType.JuristicPerson => "TXID",
                ContactType.GovernmentAgency => "TXID",
                _ => "OTHR"
            };
        }

        // Fallback: infer from digit pattern
        if (raw.All(char.IsDigit) && raw.Length == 13) return "TXID";
        if (raw.All(char.IsDigit) && raw.Length == 18) return "TXID";
        if (isSeller) return "TXID";
        return "OTHR";
    }

    /// <summary>
    /// Build SellerTradeParty per Schematron TIV-SellerTradeParty-001..012.
    /// All address fields required for CountryID=TH; provides "00" fallbacks where data
    /// missing so the document still validates structurally (user should fill company info).
    /// </summary>
    /// <param name="branch">สาขาที่ออกใบนี้ — null = กิจการสาขาเดียว (ใช้ค่าบนตัวบริษัท
    /// เหมือนเดิมทุกช่อง)</param>
    /// <param name="branchCode">รหัสสาขาที่ resolve แล้ว (snapshot &gt; ทะเบียน &gt; บริษัท)</param>
    private static XElement BuildSellerParty(XNamespace ram, Company company, Branch? branch, string branchCode)
    {
        var taxIdSchemeId = DetermineTaxIdSchemeId(company.TaxId, isSeller: true);
        var taxId = ComposeTxId(company.TaxId, branchCode, taxIdSchemeId);

        // ที่อยู่สถานประกอบการที่ออกใบ (ป.86/2542) — "ทั้งชุดหรือไม่ใช้เลย"
        // ห้ามผสมช่องของสาขากับของบริษัท จะได้ที่อยู่ที่ไม่มีอยู่จริง
        var useBranch = branch != null && !branch.IsDeleted
            && DocumentIssuerBranch.UseBranchAddress(branch.Address);
        var address     = useBranch ? branch!.Address     : company.Address;
        var subDistrict = useBranch ? branch!.SubDistrict : company.SubDistrict;
        var district    = useBranch ? branch!.District    : company.District;
        var province    = useBranch ? branch!.Province    : company.Province;
        var postalCode  = useBranch ? branch!.PostalCode  : company.PostalCode;
        // Branch ไม่มีช่องแยกเลขที่/อาคาร/หมู่/ถนน → แกะจาก free-text ของสาขา
        var buildingName = useBranch ? null : company.BuildingName;
        var moo          = useBranch ? null : company.Moo;
        var streetName   = useBranch ? null : company.StreetName;
        var buildingNumberField = useBranch ? null : company.BuildingNumber;
        var phone = useBranch ? (branch!.Phone ?? company.Phone) : company.Phone;
        var email = useBranch ? (branch!.Email ?? company.Email) : company.Email;

        var postCode = NormalizePostcode(postalCode, address);
        // Prefer the dedicated structured field; fall back to extracting from address text
        var buildingNo = !string.IsNullOrEmpty(buildingNumberField)
            ? buildingNumberField!
            : (ExtractBuildingNumber(address) ?? "0");
        // Compose a line-one address. ETDA has no dedicated Moo element so we
        // prepend "หมู่ X" to the street line — without this, provincial
        // sellers / buyers silently lose Moo from every e-Tax XML.
        var streetLine = ComposeStreetLine(moo, streetName, address);

        // ETDA XSD constrains CityName/CitySubDivisionName/CountrySubDivisionID to
        // numeric TISI 1099 codes (free-text Thai names FAIL XSD validation).
        // Lookup full TISI entry from embedded ThaiAdmin.csv when name+postcode match,
        // else fall back to province-prefix + "01" placeholders (still valid in enum).
        var provinceCode = ThaiAdminCodes.ResolveProvinceCode(province, postCode);
        var districtCode = ThaiAdminCodes.ResolveDistrictCode(
            district, subDistrict, postCode, provinceCode, province);
        var subDistrictCode = ThaiAdminCodes.ResolveSubDistrictCode(
            subDistrict, district, postCode, districtCode, province);

        return new XElement(ram + "SellerTradeParty",
            // ชื่อยังเป็น "ผู้ประกอบการจดทะเบียน" เสมอ — สาขาไม่ใช่นิติบุคคลแยก §86/4(2)
            new XElement(ram + "Name", company.Name),
            new XElement(ram + "SpecifiedTaxRegistration",
                new XElement(ram + "ID",
                    new XAttribute("schemeID", taxIdSchemeId),
                    taxId)),
            // email + โทร (หลัง TaxRegistration ก่อน Address — ตาม TakeTime/ETDA)
            BuildDefinedTradeContact(ram, email, phone),
            new XElement(ram + "PostalTradeAddress",
                new XElement(ram + "PostcodeCode", postCode),
                !string.IsNullOrEmpty(buildingName)
                    ? new XElement(ram + "BuildingName", buildingName) : null,
                !string.IsNullOrEmpty(streetLine) ? new XElement(ram + "LineOne", streetLine) : null,
                new XElement(ram + "CityName", districtCode),
                new XElement(ram + "CitySubDivisionName", subDistrictCode),
                new XElement(ram + "CountryID",
                    new XAttribute("schemeID", "3166-1 alpha-2"), "TH"),
                new XElement(ram + "CountrySubDivisionID", provinceCode),
                new XElement(ram + "BuildingNumber", buildingNo)));
    }

    /// <summary>
    /// Build BuyerTradeParty per Schematron TIV-BuyerTradeParty-007..009.
    /// If contact has structured fields (BuildingNumber/SubDistrict/District/Province),
    /// emit fully structured form (preferred for ETDA validation).
    /// Otherwise fall back to unstructured (LineOne only) — Schematron-007 allows this.
    /// </summary>
    private static XElement BuildBuyerParty(XNamespace ram, Contact contact, string schemeId, string txId)
    {
        // เฉพาะที่อยู่ไทย (CountryID=TH) เท่านั้นที่ใช้โครงสร้าง TISI geo-code —
        // ผู้ซื้อต่างประเทศต้องไปทาง unstructured (LineOne + CountryID ต่างชาติ)
        // ไม่งั้นจะยัด Thai province/district code ให้ที่อยู่ต่างประเทศ = ผิด
        var isThai = string.IsNullOrEmpty(contact.CountryCode)
            || string.Equals(contact.CountryCode, "TH", StringComparison.OrdinalIgnoreCase);
        var hasStructured = isThai
            && !string.IsNullOrEmpty(contact.BuildingNumber)
            && !string.IsNullOrEmpty(contact.SubDistrict)
            && !string.IsNullOrEmpty(contact.District)
            && !string.IsNullOrEmpty(contact.Province);

        var postCode = NormalizePostcode(contact.PostalCode, contact.Address);
        var addressElements = new List<object?>
        {
            !string.IsNullOrEmpty(postCode) ? new XElement(ram + "PostcodeCode", postCode) : null
        };

        if (hasStructured)
        {
            // Structured form — passes Schematron strictly. TISI 1099 codes for
            // CityName/CitySubDivisionName/CountrySubDivisionID per XSD enum constraint.
            var provinceCode = ThaiAdminCodes.ResolveProvinceCode(contact.Province, postCode);
            var districtCode = ThaiAdminCodes.ResolveDistrictCode(
                contact.District, contact.SubDistrict, postCode, provinceCode, contact.Province);
            var subDistrictCode = ThaiAdminCodes.ResolveSubDistrictCode(
                contact.SubDistrict, contact.District, postCode, districtCode, contact.Province);

            if (!string.IsNullOrEmpty(contact.BuildingName))
                addressElements.Add(new XElement(ram + "BuildingName", contact.BuildingName));
            // Moo prepended to street line — see ComposeStreetLine note above.
            var streetLine = ComposeStreetLine(contact.Moo, contact.StreetName, contact.Address)
                ?? "-";
            addressElements.Add(new XElement(ram + "LineOne", streetLine));
            addressElements.Add(new XElement(ram + "CityName", districtCode));
            addressElements.Add(new XElement(ram + "CitySubDivisionName", subDistrictCode));
            addressElements.Add(new XElement(ram + "CountryID",
                new XAttribute("schemeID", "3166-1 alpha-2"),
                contact.CountryCode ?? "TH"));
            addressElements.Add(new XElement(ram + "CountrySubDivisionID", provinceCode));
            addressElements.Add(new XElement(ram + "BuildingNumber", contact.BuildingNumber));
        }
        else
        {
            // Unstructured fallback — only LineOne required per TIV-BuyerTradeParty-007
            addressElements.Add(!string.IsNullOrEmpty(contact.Address)
                ? new XElement(ram + "LineOne", contact.Address)
                : new XElement(ram + "LineOne", "-"));
            addressElements.Add(new XElement(ram + "CountryID",
                new XAttribute("schemeID", "3166-1 alpha-2"),
                contact.CountryCode ?? "TH"));
        }

        return new XElement(ram + "BuyerTradeParty",
            new XElement(ram + "Name", contact.Name),
            new XElement(ram + "SpecifiedTaxRegistration",
                new XElement(ram + "ID",
                    new XAttribute("schemeID", schemeId),
                    txId)),
            // email + โทร ผู้ซื้อ (ถ้ามี) — ตาม TakeTime/ETDA
            BuildDefinedTradeContact(ram, contact.Email, contact.Phone),
            new XElement(ram + "PostalTradeAddress", addressElements.Where(e => e != null)));
    }

    /// <summary>Normalize postcode to 5 digits — first try the field, then extract from address.</summary>
    /// <summary>Compose the LineOne street fragment for a TradeParty address.
    /// ETDA Schematron has no dedicated Moo / VillageNo element, so we
    /// prepend "หมู่ X" to whatever street fragment exists (or use it as
    /// the only content when there's no street). Returns null when none of
    /// the inputs are usable — caller decides how to fall back.</summary>
    private static string? ComposeStreetLine(string? moo, string? streetName, string? freeTextFallback)
    {
        var hasMoo = !string.IsNullOrWhiteSpace(moo);
        var hasStreet = !string.IsNullOrWhiteSpace(streetName);
        if (hasMoo && hasStreet) return $"หมู่ {moo} ถ.{streetName}";
        if (hasStreet) return $"ถ.{streetName}";
        if (hasMoo) return $"หมู่ {moo}";
        return string.IsNullOrWhiteSpace(freeTextFallback) ? null : freeTextFallback;
    }

    private static string NormalizePostcode(string? postCode, string? addressFallback)
    {
        var pc = (postCode ?? "").Trim();
        if (pc.Length == 5 && pc.All(char.IsDigit)) return pc;
        var extracted = ExtractPostcodeOrEmpty(addressFallback);
        if (!string.IsNullOrEmpty(extracted)) return extracted;
        // Last resort: pad/truncate to 5 (validator will flag if invalid)
        if (pc.Length > 5) return pc.Substring(0, 5);
        return string.IsNullOrEmpty(pc) ? "00000" : pc.PadLeft(5, '0');
    }

    /// <summary>Find a 5-digit postcode in free-text address (Thai postcodes are 5 digits).</summary>
    private static string ExtractPostcodeOrEmpty(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(address, @"\b(\d{5})\b");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>Extract leading building number from address ("123/45 ถนน..." → "123/45").</summary>
    private static string? ExtractBuildingNumber(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(address.Trim(), @"^(\d+(?:/\d+)?)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static XElement BuildLineItem(XNamespace ram, DocumentLine line, int lineNo, string currency, bool pricesIncludeVat)
    {
        var inv = CultureInfo.InvariantCulture;
        var unitCode = MapUnitCode(line.Unit);
        var lineSubTotal = line.Amount;                 // ex-VAT (DocumentService คำนวณแยก VAT แล้ว)
        var lineWithVat = line.Amount + line.VatAmount; // incl-VAT
        // e-Tax XML ใช้ฐาน ex-VAT ทั้งบรรทัด. เมื่อ pricesIncludeVat=true ค่า
        // UnitPrice/DiscountAmount ที่เก็บเป็น "รวม VAT" → ต้องถอด VAT ก่อนใส่
        // XML ไม่งั้น ChargeAmount/ActualAmount (incl) จะไม่สอดคล้องกับ
        // BasisAmount/NetLineTotal (ex) → math ในบรรทัดไม่ตรง RD reject ได้
        var rateFactor = 1m + (line.VatRate / 100m);
        var chargeAmount = pricesIncludeVat && line.VatRate > 0
            ? Math.Round(line.UnitPrice / rateFactor, 2, MidpointRounding.AwayFromZero)
            : line.UnitPrice;
        var discountActual = pricesIncludeVat && line.VatRate > 0
            ? Math.Round(line.DiscountAmount / rateFactor, 2, MidpointRounding.AwayFromZero)
            : line.DiscountAmount;

        return new XElement(ram + "IncludedSupplyChainTradeLineItem",
            new XElement(ram + "AssociatedDocumentLineDocument",
                new XElement(ram + "LineID", lineNo.ToString())),
            new XElement(ram + "SpecifiedTradeProduct",
                line.ProductCode != null ? new XElement(ram + "ID", line.ProductCode) : null,
                new XElement(ram + "Name", line.Description)),
            new XElement(ram + "SpecifiedLineTradeAgreement",
                new XElement(ram + "GrossPriceProductTradePrice",
                    new XElement(ram + "ChargeAmount",
                        chargeAmount.ToString("0.##", inv)))),
            new XElement(ram + "SpecifiedLineTradeDelivery",
                new XElement(ram + "BilledQuantity",
                    new XAttribute("unitCode", unitCode),
                    line.Quantity.ToString("0.##", inv))),
            new XElement(ram + "SpecifiedLineTradeSettlement",
                new XElement(ram + "ApplicableTradeTax",
                    new XElement(ram + "TypeCode", "VAT"),
                    new XElement(ram + "CalculatedRate", line.VatRate.ToString("0.##", inv)),
                    new XElement(ram + "BasisAmount", lineSubTotal.ToString("0.##", inv)),
                    new XElement(ram + "CalculatedAmount", line.VatAmount.ToString("0.##", inv))),
                new XElement(ram + "SpecifiedTradeAllowanceCharge",
                    // ChargeIndicator: false = allowance (discount), true = charge (surcharge)
                    // Per ETDA samples: always "false" with ActualAmount=0 when no discount.
                    // We only emit allowance lines (discounts), so always false.
                    new XElement(ram + "ChargeIndicator", "false"),
                    new XElement(ram + "ActualAmount", discountActual.ToString("0.##", inv))),
                new XElement(ram + "SpecifiedTradeSettlementLineMonetarySummation",
                    new XElement(ram + "TaxTotalAmount", line.VatAmount.ToString("0.##", inv)),
                    new XElement(ram + "NetLineTotalAmount",
                        new XAttribute("currencyID", currency),
                        lineSubTotal.ToString("0.##", inv)),
                    new XElement(ram + "NetIncludingTaxesLineTotalAmount",
                        new XAttribute("currencyID", currency),
                        lineWithVat.ToString("0.##", inv)))));
    }

    /// <summary>
    // Legacy BuildTradeParty removed — replaced by BuildSellerParty / BuildBuyerParty
    // which apply ETDA Schematron-required address fields per side.

    /// <summary>
    /// Map Thai unit names to ETDA-accepted unit codes.
    /// ETDA samples primarily use simple "Unit" — UN/CEFACT codes (C62, MTR, etc.) are accepted.
    /// </summary>
    private static string MapUnitCode(string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return "Unit";
        return unit.ToLowerInvariant() switch
        {
            "ชิ้น" or "ea" or "pcs" or "หน่วย" => "Unit",
            "กล่อง" or "box" => "BX",
            "ชุด" or "set" => "SET",
            "เมตร" or "m" => "MTR",
            "ลิตร" or "l" => "LTR",
            "กิโลกรัม" or "kg" => "KGM",
            "ชั่วโมง" or "hr" => "HUR",
            "วัน" or "day" => "DAY",
            "เดือน" or "month" => "MON",
            "ปี" or "year" => "ANN",
            "งาน" or "job" => "E49",
            "รายการ" or "item" => "Unit",
            _ => "Unit"
        };
    }

    private static EtaxInvoiceResponse MapToResponse(EtaxInvoice e) => new(
        e.Id, e.DocumentId, e.EtaxRefNumber.Split('-').Length > 2 ? e.EtaxRefNumber : "",
        e.EtaxRefNumber, e.XmlContent.Length > 500 ? e.XmlContent[..500] + "..." : e.XmlContent,
        e.DigitalSignature, e.Status, e.SubmissionId, e.SubmittedAt,
        e.AcceptanceNumber, e.AcceptedAt, e.ErrorMessage, e.ErrorCode,
        e.SellerName, e.SellerTaxId, e.BuyerName, e.BuyerTaxId,
        e.SignedAt, e.CertificateSerialNumber, e.CreatedAt);

    private class RdSubmissionResult
    {
        public bool Accepted { get; set; }
        public string SubmissionId { get; set; } = "";
        public string? AcceptanceNumber { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorCode { get; set; }
    }
}
