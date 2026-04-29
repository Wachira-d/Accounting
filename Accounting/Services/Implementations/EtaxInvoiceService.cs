using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Accounting.Data;
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

    public EtaxInvoiceService(
        AccountingDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        ILogger<EtaxInvoiceService> logger,
        IPdfGenerationService pdfService,
        IWebHostEnvironment env)
    {
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _pdfService = pdfService;
        _env = env;
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
                companySettings.EtaxCertificatePassword,
                companySettings.EtaxRdApiKey,
                companySettings.EtaxRdApiSecret,
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
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Only TaxInvoice, Receipt, DebitNote, CreditNote can be e-Tax
        if (document.DocumentType != DocumentType.TaxInvoice && document.DocumentType != DocumentType.Receipt
            && document.DocumentType != DocumentType.DebitNote && document.DocumentType != DocumentType.CreditNote)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะใบกำกับภาษี, ใบเสร็จรับเงิน, ใบเพิ่มหนี้, ใบลดหนี้ เท่านั้น");

        if (document.Status == DocumentStatus.Draft)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารฉบับร่างได้ กรุณาอนุมัติเอกสารก่อน");
        if (document.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถสร้าง e-Tax จากเอกสารที่ยกเลิกแล้ว");
        if (document.Status != DocumentStatus.Approved)
            throw new InvalidOperationException("สามารถสร้าง e-Tax ได้เฉพาะเอกสารที่อนุมัติแล้วเท่านั้น");

        if (await _db.EtaxInvoices.AnyAsync(e => e.DocumentId == document.Id && e.CompanyId == companyId && e.Status != EtaxStatus.Error))
            throw new InvalidOperationException("เอกสารนี้มี e-Tax Invoice แล้ว");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        if (string.IsNullOrWhiteSpace(company.TaxId))
            throw new InvalidOperationException("กรุณาตั้งค่าเลขประจำตัวผู้เสียภาษีของบริษัทก่อน");
        if (string.IsNullOrWhiteSpace(document.Contact?.TaxId))
            throw new InvalidOperationException("กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var count = await _db.EtaxInvoices.CountAsync(e => e.CompanyId == companyId);
            var etaxRef = $"ETAX-{company.TaxId}-{DateTime.UtcNow:yyyyMMdd}-{(count + 1):D6}";

            // For CN/DN, load original document for OriginalDocumentReference per ETDA spec
            Document? originalDoc = null;
            if ((document.DocumentType == DocumentType.CreditNote || document.DocumentType == DocumentType.DebitNote)
                && document.RelatedDocumentId.HasValue)
            {
                originalDoc = await _db.Documents.FirstOrDefaultAsync(d =>
                    d.Id == document.RelatedDocumentId.Value && d.CompanyId == companyId);
            }

            var xml = BuildEtaxXml(document, company, etaxRef, originalDoc);

            var etax = new EtaxInvoice
            {
                CompanyId = companyId,
                DocumentId = document.Id,
                EtaxRefNumber = etaxRef,
                XmlContent = xml,
                Status = EtaxStatus.Generated,
                SellerName = company.Name,
                SellerTaxId = company.TaxId,
                SellerBranch = company.BranchCode,
                SellerAddress = $"{company.Address} {company.SubDistrict} {company.District} {company.Province} {company.PostalCode}",
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

            if (!string.IsNullOrEmpty(certPath) && System.IO.File.Exists(certPath))
            {
                // Production: Use real X.509 certificate
                (signedXml, certSerialNumber) = SignXmlWithCertificate(etax.XmlContent, certPath, certPassword);
                _logger.LogInformation("e-Tax {EtaxRef} signed with X.509 certificate {CertSerial}",
                    etax.EtaxRefNumber, certSerialNumber);
            }
            else
            {
                // Development fallback: Sign with SHA256 + embedded key
                _logger.LogWarning("No X.509 certificate configured. Using development signing for e-Tax {EtaxRef}",
                    etax.EtaxRefNumber);
                (signedXml, certSerialNumber) = SignXmlDevelopment(etax.XmlContent, etax.EtaxRefNumber);
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

            // Auto-submit if configured (per-company or global)
            if (etaxConfig.AutoSubmit)
            {
                return await SubmitToRevenueAsync(companyId, etax.Id);
            }

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

        if (etax.Status == EtaxStatus.Accepted || etax.Status == EtaxStatus.Submitted)
            throw new InvalidOperationException(
                "ไม่สามารถยกเลิก e-Tax ที่ส่ง/อนุมัติโดยกรมสรรพากรแล้ว " +
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

    private string BuildEtaxXml(Document doc, Company company, string etaxRef, Document? originalDoc = null)
    {
        var rootElementName = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "TaxInvoice_CrossIndustryInvoice",
            DocumentType.Receipt => "Receipt_CrossIndustryInvoice",
            DocumentType.DebitNote => "DebitCreditNote_CrossIndustryInvoice",
            DocumentType.CreditNote => "DebitCreditNote_CrossIndustryInvoice",
            _ => "TaxInvoice_CrossIndustryInvoice"
        };
        var ns = XNamespace.Get($"urn:etda:uncefact:data:standard:{rootElementName}:2");
        var ram = XNamespace.Get("urn:etda:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:2");

        // ETDA TypeCodes per UN/EDIFACT 1001 — สรรพากร accepts these specific codes
        var docTypeCode = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "388",
            DocumentType.Receipt => "T03",
            DocumentType.DebitNote => "80",
            DocumentType.CreditNote => "81",
            _ => "388"
        };

        var docTypeName = doc.DocumentType switch
        {
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.Receipt => "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ",
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            DocumentType.CreditNote => "ใบลดหนี้",
            _ => "ใบกำกับภาษี"
        };

        var vatRate = doc.Lines.Any(l => l.VatRate > 0)
            ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate)
            : 0m;

        var currency = doc.Currency ?? "THB";
        var sellerBranch = company.BranchCode ?? "00000";
        var buyerBranch = doc.Contact.BranchCode ?? "00000";
        var isBranch = sellerBranch != "00000";

        // Build TradeParty elements in ETDA-required order
        var sellerParty = BuildTradeParty(ram, "SellerTradeParty", company.Name, company.TaxId!,
            sellerBranch, isBranch ? (company.BranchName ?? "สาขา") : "สำนักงานใหญ่",
            company.Address, company.SubDistrict, company.District, company.Province,
            company.PostalCode, "TH", company.Phone, company.Email);

        var buyerBranchName = buyerBranch != "00000" ? "สาขา" : "สำนักงานใหญ่";
        var buyerParty = BuildTradeParty(ram, "BuyerTradeParty", doc.Contact.Name, doc.Contact.TaxId ?? "",
            buyerBranch, buyerBranchName,
            doc.Contact.Address, null, null, null, null,
            "TH", doc.Contact.Phone, doc.Contact.Email);

        // Line items per ETDA spec
        var lineItems = doc.Lines.OrderBy(l => l.LineOrder).Select((line, idx) =>
            new XElement(ram + "IncludedSupplyChainTradeLineItem",
                new XElement(ram + "AssociatedDocumentLineDocument",
                    new XElement(ram + "LineID", (idx + 1).ToString())),
                new XElement(ram + "SpecifiedTradeProduct",
                    line.ProductCode != null ? new XElement(ram + "GlobalID", line.ProductCode) : null,
                    new XElement(ram + "Name", line.Description)),
                new XElement(ram + "SpecifiedLineTradeAgreement",
                    new XElement(ram + "GrossPriceProductTradePrice",
                        new XElement(ram + "ChargeAmount", line.UnitPrice.ToString("F2")))),
                new XElement(ram + "SpecifiedLineTradeDelivery",
                    new XElement(ram + "BilledQuantity",
                        new XAttribute("unitCode", MapUnitCode(line.Unit)),
                        line.Quantity.ToString("F4"))),
                new XElement(ram + "SpecifiedLineTradeSettlement",
                    new XElement(ram + "ApplicableTradeTax",
                        new XElement(ram + "TypeCode", "VAT"),
                        new XElement(ram + "RateApplicablePercent", line.VatRate.ToString("F2"))),
                    new XElement(ram + "SpecifiedTradeSettlementLineMonetarySummation",
                        new XElement(ram + "NetLineTotalAmount", line.Amount.ToString("F2")),
                        new XElement(ram + "NetIncludingTaxesLineTotalAmount",
                            (line.Amount + line.VatAmount).ToString("F2"))))));

        // Build additional reference for CN/DN
        XElement? additionalRef = null;
        if (originalDoc != null && (doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote))
        {
            additionalRef = new XElement(ram + "AdditionalReferencedDocument",
                new XElement(ram + "IssuerAssignedID", originalDoc.DocumentNumber),
                new XElement(ram + "IssueDateTime",
                    new XElement(ram + "DateTimeString",
                        new XAttribute("format", "102"),
                        originalDoc.DocumentDate.ToString("yyyyMMdd"))),
                new XElement(ram + "ReferenceTypeCode", "AWR"));
        }

        // CN/DN reason
        XElement? cnDnReason = null;
        if (doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote)
        {
            cnDnReason = new XElement(ram + "AdditionalReferencedDocument",
                new XElement(ram + "IssuerAssignedID", doc.DocumentNumber),
                new XElement(ram + "ReferenceTypeCode", "ANG"),
                new XElement(ram + "Name", doc.Notes ?? "ปรับปรุงรายการ"));
        }

        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + rootElementName,
                new XAttribute(XNamespace.Xmlns + "rsm", ns),
                new XAttribute(XNamespace.Xmlns + "ram", ram),

                // ExchangedDocumentContext — ETDA required structure
                new XElement(ns + "ExchangedDocumentContext",
                    new XElement(ram + "GuidelineSpecifiedDocumentContextParameter",
                        new XElement(ram + "ID", etaxRef))),

                // ExchangedDocument — ETDA required structure and element order
                new XElement(ns + "ExchangedDocument",
                    new XElement(ram + "ID", doc.DocumentNumber),
                    new XElement(ram + "Name", docTypeName),
                    new XElement(ram + "TypeCode", docTypeCode),
                    new XElement(ram + "IssueDateTime",
                        new XElement(ram + "DateTimeString",
                            new XAttribute("format", "102"),
                            doc.DocumentDate.ToString("yyyyMMdd"))),
                    doc.DocumentType == DocumentType.CreditNote || doc.DocumentType == DocumentType.DebitNote
                        ? new XElement(ram + "Purpose", doc.Notes ?? "ปรับปรุงรายการ") : null,
                    doc.Notes != null && doc.DocumentType != DocumentType.CreditNote && doc.DocumentType != DocumentType.DebitNote
                        ? new XElement(ram + "IncludedNote",
                            new XElement(ram + "Content", doc.Notes)) : null),

                // SupplyChainTradeTransaction
                new XElement(ns + "SupplyChainTradeTransaction",

                    // ApplicableHeaderTradeAgreement
                    new XElement(ram + "ApplicableHeaderTradeAgreement",
                        sellerParty,
                        buyerParty,
                        additionalRef,
                        cnDnReason),

                    // IncludedSupplyChainTradeLineItem (all line items)
                    lineItems,

                    // ApplicableHeaderTradeDelivery
                    new XElement(ram + "ApplicableHeaderTradeDelivery",
                        new XElement(ram + "ActualDeliverySupplyChainEvent",
                            new XElement(ram + "OccurrenceDateTime",
                                new XElement(ram + "DateTimeString",
                                    new XAttribute("format", "102"),
                                    doc.DocumentDate.ToString("yyyyMMdd"))))),

                    // ApplicableHeaderTradeSettlement
                    new XElement(ram + "ApplicableHeaderTradeSettlement",
                        new XElement(ram + "InvoiceCurrencyCode", currency),
                        new XElement(ram + "ApplicableTradeTax",
                            new XElement(ram + "TypeCode", "VAT"),
                            new XElement(ram + "CalculatedAmount", doc.VatAmount.ToString("F2")),
                            new XElement(ram + "BasisAmount", doc.SubTotal.ToString("F2")),
                            new XElement(ram + "RateApplicablePercent", vatRate.ToString("F2"))),
                        new XElement(ram + "SpecifiedTradeSettlementHeaderMonetarySummation",
                            new XElement(ram + "LineTotalAmount", doc.SubTotal.ToString("F2")),
                            doc.DiscountAmount > 0
                                ? new XElement(ram + "AllowanceTotalAmount", doc.DiscountAmount.ToString("F2")) : null,
                            new XElement(ram + "TaxBasisTotalAmount", (doc.SubTotal - doc.DiscountAmount).ToString("F2")),
                            new XElement(ram + "TaxTotalAmount",
                                new XAttribute("currencyID", currency),
                                doc.VatAmount.ToString("F2")),
                            new XElement(ram + "GrandTotalAmount", doc.TotalAmount.ToString("F2")),
                            new XElement(ram + "DuePayableAmount", doc.TotalAmount.ToString("F2")))))));

        return xml.ToString();
    }

    private static XElement BuildTradeParty(XNamespace ram, string partyElement, string name, string taxId,
        string branchCode, string branchName, string? address, string? subDistrict,
        string? district, string? province, string? postalCode, string countryId,
        string? phone, string? email)
    {
        var elements = new List<object?>
        {
            new XElement(ram + "Name", name),
            new XElement(ram + "SpecifiedTaxRegistration",
                new XElement(ram + "ID",
                    new XAttribute("schemeID", "TXID"),
                    taxId)),
            new XElement(ram + "SpecifiedTaxRegistration",
                new XElement(ram + "ID",
                    new XAttribute("schemeID", "NIDN"),
                    branchCode)),
        };

        // PostalTradeAddress
        var addressElements = new List<object?>();
        addressElements.Add(new XElement(ram + "PostcodeCode", postalCode ?? ""));
        if (!string.IsNullOrEmpty(branchName))
            addressElements.Add(new XElement(ram + "BuildingName", branchName));
        if (!string.IsNullOrEmpty(address))
            addressElements.Add(new XElement(ram + "LineOne", address));
        if (!string.IsNullOrEmpty(subDistrict))
            addressElements.Add(new XElement(ram + "LineTwo", subDistrict));
        if (!string.IsNullOrEmpty(district))
            addressElements.Add(new XElement(ram + "CityName", district));
        if (!string.IsNullOrEmpty(province))
            addressElements.Add(new XElement(ram + "CitySubDivisionName", province));
        addressElements.Add(new XElement(ram + "CountryID", countryId));
        elements.Add(new XElement(ram + "PostalTradeAddress", addressElements.Where(e => e != null)));

        // DefinedTradeContact
        if (!string.IsNullOrEmpty(phone) || !string.IsNullOrEmpty(email))
        {
            var contactElements = new List<object?>();
            if (!string.IsNullOrEmpty(phone))
                contactElements.Add(new XElement(ram + "TelephoneUniversalCommunication",
                    new XElement(ram + "CompleteNumber", phone)));
            if (!string.IsNullOrEmpty(email))
                contactElements.Add(new XElement(ram + "EmailURIUniversalCommunication",
                    new XElement(ram + "URIID", email)));
            elements.Add(new XElement(ram + "DefinedTradeContact", contactElements.Where(e => e != null)));
        }

        return new XElement(ram + partyElement, elements.Where(e => e != null));
    }

    private static string MapUnitCode(string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return "C62";
        return unit.ToLowerInvariant() switch
        {
            "ชิ้น" or "ea" or "pcs" => "C62",
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
            "รายการ" or "item" => "C62",
            _ => "C62"
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
