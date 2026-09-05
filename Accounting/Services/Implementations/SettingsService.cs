using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class SettingsService : ISettingsService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IImageProcessingService _images;
    private readonly IWebHostEnvironment _env;

    public SettingsService(AccountingDbContext db, ISecretProtector secrets, IImageProcessingService images, IWebHostEnvironment env)
    {
        _db = db;
        _secrets = secrets;
        _images = images;
        _env = env;
    }

    public async Task<CompanySettingsResponse> GetSettingsAsync(Guid companyId)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        return MapToResponse(companyId, settings);
    }

    public async Task<CompanySettingsResponse> UpdateSettingsAsync(Guid companyId, UpdateCompanySettingsRequest request)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);

        if (request.PrimaryColor != null) settings.PrimaryColor = request.PrimaryColor;
        if (request.SecondaryColor != null) settings.SecondaryColor = request.SecondaryColor;
        if (request.DefaultPaymentTerms != null) settings.DefaultPaymentTerms = request.DefaultPaymentTerms;
        if (request.DefaultPaymentDueDays.HasValue) settings.DefaultPaymentDueDays = request.DefaultPaymentDueDays.Value;
        if (request.InvoiceNotes != null) settings.InvoiceNotes = request.InvoiceNotes;
        if (request.ReceiptNotes != null) settings.ReceiptNotes = request.ReceiptNotes;
        if (request.QuotationNotes != null) settings.QuotationNotes = request.QuotationNotes;
        if (request.InvoiceFooter != null) settings.InvoiceFooter = request.InvoiceFooter;
        if (request.ReceiptFooter != null) settings.ReceiptFooter = request.ReceiptFooter;
        if (request.ShowGlEntryOnDocument.HasValue) settings.ShowGlEntryOnDocument = request.ShowGlEntryOnDocument.Value;
        if (request.ShowProjectOnDocuments.HasValue) settings.ShowProjectOnDocuments = request.ShowProjectOnDocuments.Value;
        if (request.ShowCostCenterOnDocuments.HasValue) settings.ShowCostCenterOnDocuments = request.ShowCostCenterOnDocuments.Value;
        if (request.UseCustomAuthorizedSignatory.HasValue) settings.UseCustomAuthorizedSignatory = request.UseCustomAuthorizedSignatory.Value;
        // "" = ล้างค่า, null = ไม่แก้ (ให้ user ลบชื่อ/รูปได้)
        if (request.AuthorizedSignatoryName != null) settings.AuthorizedSignatoryName = string.IsNullOrWhiteSpace(request.AuthorizedSignatoryName) ? null : request.AuthorizedSignatoryName.Trim();
        if (request.AuthorizedSignatoryTitle != null) settings.AuthorizedSignatoryTitle = string.IsNullOrWhiteSpace(request.AuthorizedSignatoryTitle) ? null : request.AuthorizedSignatoryTitle.Trim();
        if (request.AuthorizedSignatorySignatureBase64 != null) settings.AuthorizedSignatorySignatureBase64 = string.IsNullOrWhiteSpace(request.AuthorizedSignatorySignatureBase64) ? null : request.AuthorizedSignatorySignatureBase64.Trim();
        if (request.DocumentTitleOverridesJson != null)
            settings.DocumentTitleOverridesJson = string.IsNullOrWhiteSpace(request.DocumentTitleOverridesJson) ? null : request.DocumentTitleOverridesJson;
        // รูปแบบการออกใบกำกับ/ใบเสร็จ — รับเฉพาะค่าที่นิยามไว้จริง (กันเลขขยะจาก
        // client ที่จะทำให้ policy ตกไป default เงียบ ๆ)
        if (request.ReceiptIssueMode.HasValue
            && Enum.IsDefined(typeof(ReceiptIssueMode), request.ReceiptIssueMode.Value))
            settings.ReceiptIssueMode = request.ReceiptIssueMode.Value;
        if (request.UnifyTaxInvoiceNumberSeries.HasValue)
            settings.UnifyTaxInvoiceNumberSeries = request.UnifyTaxInvoiceNumberSeries.Value;
        // ใบเสร็จ standalone ที่มีสินค้าคงคลัง — รับเฉพาะค่าที่นิยามไว้จริง
        if (request.CashSaleStockPolicy.HasValue
            && Enum.IsDefined(typeof(CashSaleStockPolicy), request.CashSaleStockPolicy.Value))
            settings.CashSaleStockPolicy = request.CashSaleStockPolicy.Value;
        // บัญชีทิปพนักงานค้างจ่าย: "" = ล้างกลับค่าแนะนำ · ต้องมีในผังบัญชีของบริษัทและเป็นบัญชีลงรายการได้
        // (ไม่งั้น POS จะ fold ทิปเข้ารายได้เงียบ ๆ — ห้ามรับรหัสที่ไม่มีจริง)
        if (request.PosTipPayableAccountCode != null)
        {
            var code = request.PosTipPayableAccountCode.Trim();
            if (code.Length == 0) settings.PosTipPayableAccountCode = null;
            else
            {
                var ok = await _db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.CompanyId == companyId
                    && a.AccountCode == code && a.IsActive && !a.IsDeleted && a.Level >= 4);
                if (!ok)
                    throw new Accounting.Helpers.BusinessRuleException(
                        $"ไม่พบบัญชี {code} ในผังบัญชี (หรือไม่ใช่บัญชีที่ลงรายการได้) — เลือกบัญชีหนี้สิน \"ค้างจ่ายพนักงาน\" ที่มีอยู่จริง",
                        "SET-TIP-ACCOUNT");
                settings.PosTipPayableAccountCode = code;
            }
        }
        // ภาษาเอกสาร — รับเฉพาะ th/en (ค่าอื่น = ไม่แก้ กันค่าขยะจาก client)
        if (request.DocumentLanguage != null)
        {
            var langReq = request.DocumentLanguage.Trim().ToLowerInvariant();
            if (langReq is "th" or "en") settings.DocumentLanguage = langReq;
        }
        if (request.LeaveQuotasJson != null) settings.LeaveQuotasJson = request.LeaveQuotasJson;
        if (request.EnforceManagerApproval.HasValue) settings.EnforceManagerApproval = request.EnforceManagerApproval.Value;
        if (request.DefaultVatRate.HasValue) settings.DefaultVatRate = request.DefaultVatRate.Value;
        if (request.VatRegistered.HasValue) settings.VatRegistered = request.VatRegistered.Value;
        // เกณฑ์รับรู้ WHT — เปลี่ยนแล้วมีผลกับ JE ของ "เอกสารที่อนุมัติหลังจากนี้"
        // เท่านั้น (ใบเก่าที่ post ไปแล้วไม่ถูกแก้ย้อนหลัง — ถ้าจะย้ายเกณฑ์กลางปี
        // ต้องกลับรายการใบเก่าเอง) จึงไม่ทำ migration อัตโนมัติที่นี่
        if (request.WhtRecognitionBasis.HasValue)
            settings.WhtRecognitionBasis = request.WhtRecognitionBasis.Value;
        // Sync กลับไปที่ Company.IsVatRegistered/VatRate — flag คู่ที่ POS/
        // ECommerce/AI อ่าน ต้องตรงกับ CompanySettings เสมอ (ดูหมายเหตุใน
        // CompanyService.UpdateCompany) มิฉะนั้นบางช่องทางคิด VAT บางช่องบล็อก
        if (request.VatRegistered.HasValue || request.DefaultVatRate.HasValue)
        {
            var comp = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId);
            if (comp != null)
            {
                if (request.VatRegistered.HasValue) comp.IsVatRegistered = request.VatRegistered.Value;
                if (request.DefaultVatRate.HasValue) comp.VatRate = request.DefaultVatRate.Value;
            }
        }
        if (request.VatRegistrationDate != null) settings.VatRegistrationDate = request.VatRegistrationDate;
        if (request.IsVehicleDealer.HasValue) settings.IsVehicleDealer = request.IsVehicleDealer.Value;
        if (request.EmailFromName != null) settings.EmailFromName = request.EmailFromName;
        if (request.EmailReplyTo != null) settings.EmailReplyTo = request.EmailReplyTo;
        if (request.InvoiceEmailSubject != null) settings.InvoiceEmailSubject = request.InvoiceEmailSubject;
        if (request.InvoiceEmailBody != null) settings.InvoiceEmailBody = request.InvoiceEmailBody;
        if (request.RequireApprovalForDocuments.HasValue) settings.RequireApprovalForDocuments = request.RequireApprovalForDocuments.Value;
        if (request.ApprovalThresholdAmount.HasValue) settings.ApprovalThresholdAmount = request.ApprovalThresholdAmount.Value;
        if (request.EnableApiAccess.HasValue) settings.EnableApiAccess = request.EnableApiAccess.Value;
        if (request.MaxApiKeys.HasValue) settings.MaxApiKeys = request.MaxApiKeys.Value;
        if (request.AutoCloseMonthEnd.HasValue) settings.AutoCloseMonthEnd = request.AutoCloseMonthEnd.Value;
        if (request.MonthEndClosingDay.HasValue) settings.MonthEndClosingDay = request.MonthEndClosingDay.Value;
        if (request.PreventPostToClosedPeriod.HasValue) settings.PreventPostToClosedPeriod = request.PreventPostToClosedPeriod.Value;
        if (request.SodBlockSelfApproval.HasValue) settings.SodBlockSelfApproval = request.SodBlockSelfApproval.Value;
        if (request.BudgetCommitmentMode != null && request.BudgetCommitmentMode is "Off" or "Warn" or "Block")
            settings.BudgetCommitmentMode = request.BudgetCommitmentMode;
        if (request.AllowNegativeStock.HasValue) settings.AllowNegativeStock = request.AllowNegativeStock.Value;
        if (request.EclEnabled.HasValue) settings.EclEnabled = request.EclEnabled.Value;
        // ตราประทับบริษัท — ขนาด/ตำแหน่ง (clamp กันค่าเพี้ยน; รูปอัปโหลดแยก endpoint)
        if (request.StampWidthMm.HasValue) settings.StampWidthMm = Math.Clamp(request.StampWidthMm.Value, 0m, 120m);
        if (request.StampHeightMm.HasValue) settings.StampHeightMm = Math.Clamp(request.StampHeightMm.Value, 5m, 120m);
        if (request.StampAlign != null && request.StampAlign is "Right" or "Left" or "Center")
            settings.StampAlign = request.StampAlign;

        // e-Tax settings
        if (request.EtaxEnabled.HasValue) settings.EtaxEnabled = request.EtaxEnabled.Value;
        if (request.EtaxCertificatePath != null) settings.EtaxCertificatePath = request.EtaxCertificatePath;
        if (request.EtaxCertificatePassword != null) settings.EtaxCertificatePassword = _secrets.Protect(request.EtaxCertificatePassword);
        if (request.EtaxRdApiKey != null) settings.EtaxRdApiKey = request.EtaxRdApiKey;
        if (request.EtaxRdApiSecret != null) settings.EtaxRdApiSecret = _secrets.Protect(request.EtaxRdApiSecret);
        if (request.EtaxTestMode.HasValue) settings.EtaxTestMode = request.EtaxTestMode.Value;
        if (request.EtaxAutoSign.HasValue) settings.EtaxAutoSign = request.EtaxAutoSign.Value;
        if (request.EtaxAutoSubmit.HasValue) settings.EtaxAutoSubmit = request.EtaxAutoSubmit.Value;
        if (request.EtaxServiceProvider != null) settings.EtaxServiceProvider = request.EtaxServiceProvider;

        // Landing Page – Accounting Services
        if (request.LandingContactPhone != null) settings.LandingContactPhone = request.LandingContactPhone;
        if (request.LandingContactLine != null) settings.LandingContactLine = request.LandingContactLine;
        if (request.LandingContactEmail != null) settings.LandingContactEmail = request.LandingContactEmail;
        if (request.LandingServicesJson != null) settings.LandingServicesJson = request.LandingServicesJson;

        // OCR document-target preference
        if (request.OcrBuyerInvoiceDefaultTarget.HasValue)
            settings.OcrBuyerInvoiceDefaultTarget = request.OcrBuyerInvoiceDefaultTarget.Value;

        // OCR แหล่งเงิน default. Guid.Empty = ล้างค่า (กลับ auto-pick);
        // null = ไม่แตะ; ค่าอื่น = ตั้งบัญชีนั้น.
        if (request.DefaultPaymentAccountId.HasValue)
            settings.DefaultPaymentAccountId = request.DefaultPaymentAccountId.Value == Guid.Empty
                ? null : request.DefaultPaymentAccountId.Value;

        if (request.AutoAttachWhtCertPdf.HasValue)
            settings.AutoAttachWhtCertPdf = request.AutoAttachWhtCertPdf.Value;
        // กองทุนเงินทดแทน (กท.20ก)
        if (request.WorkersCompensationEnabled.HasValue)
            settings.WorkersCompensationEnabled = request.WorkersCompensationEnabled.Value;
        if (request.WorkersCompensationRatePercent.HasValue
            && request.WorkersCompensationRatePercent.Value >= 0.2m
            && request.WorkersCompensationRatePercent.Value <= 1.0m)
            settings.WorkersCompensationRatePercent = request.WorkersCompensationRatePercent.Value;

        // ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7) — null = ไม่แตะ, ค่าว่าง = ล้าง
        if (request.BookkeeperName != null)
            settings.BookkeeperName = string.IsNullOrWhiteSpace(request.BookkeeperName) ? null : request.BookkeeperName.Trim();
        if (request.BookkeeperCpdNumber != null)
            settings.BookkeeperCpdNumber = string.IsNullOrWhiteSpace(request.BookkeeperCpdNumber) ? null : request.BookkeeperCpdNumber.Trim();

        await _db.SaveChangesAsync();
        return MapToResponse(companyId, settings);
    }

    // ===== Logo Management =====

    public async Task<CompanySettingsResponse> UploadLogoAsync(Guid companyId, Stream fileStream, string fileName, string contentType)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);

        // Validate content type — ไม่รับ SVG (stored XSS ผ่าน <script> ในไฟล์
        // ที่ serve จาก origin เดียวกับแอป — กฎเดียวกับตราประทับ/โลโก้แบรนด์)
        var allowedTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" };
        if (!allowedTypes.Contains(contentType.ToLower()))
            throw new InvalidOperationException("รองรับเฉพาะไฟล์ PNG, JPEG, GIF, WebP เท่านั้น — ไม่รับ SVG ด้วยเหตุผลด้านความปลอดภัย");

        // WebRootPath is null in environments where wwwroot doesn't exist
        // (slim deployments, certain Docker setups). Fall back to ContentRoot
        // + "wwwroot" so the upload still has a home; the resulting public
        // URL still resolves via UseStaticFiles when the path eventually
        // exists. Without this guard, Path.Combine throws and bubbles up as
        // "เกิดข้อผิดพลาดภายในระบบ".
        var webRoot = _env.WebRootPath
            ?? Path.Combine(_env.ContentRootPath ?? Directory.GetCurrentDirectory(), "wwwroot");

        // Delete old logo if exists
        try
        {
            if (!string.IsNullOrEmpty(settings.LogoPath) && File.Exists(settings.LogoPath))
                File.Delete(settings.LogoPath);
        }
        catch { /* old file may be locked / missing — keep going with the new upload */ }

        var dir = Path.Combine(webRoot, "uploads", "logos", companyId.ToString());
        var web = $"/uploads/logos/{companyId}";
        ProcessedImageResult processed;
        try
        {
            processed = await _images.ProcessAndSaveAsync(fileStream, contentType, fileName, dir, web, ImageProfile.Logo);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"ระบบไม่มีสิทธิ์เขียนไฟล์ลงโฟลเดอร์ uploads ({dir}) — โปรดติดต่อผู้ดูแลระบบเพื่อเพิ่มสิทธิ์", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"ไม่พบโฟลเดอร์ปลายทาง ({dir}) — โปรดให้ผู้ดูแลระบบสร้างโฟลเดอร์ก่อน", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"บันทึกไฟล์ไม่สำเร็จ: {ex.Message}", ex);
        }

        settings.LogoPath = processed.AbsolutePath;
        settings.LogoUrl = processed.RelativeUrl;
        await _db.SaveChangesAsync();

        return MapToResponse(companyId, settings);
    }

    public async Task DeleteLogoAsync(Guid companyId)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        if (!string.IsNullOrEmpty(settings.LogoPath) && File.Exists(settings.LogoPath))
            File.Delete(settings.LogoPath);
        settings.LogoPath = null;
        settings.LogoUrl = null;
        await _db.SaveChangesAsync();
    }

    // ===== ตราประทับบริษัท (company seal) — mirror ของ logo แต่ใช้ ImageProfile.Logo
    // (คงความโปร่งใส PNG — ตราส่วนใหญ่พื้นหลังโปร่ง) เก็บลง /uploads/stamps/{companyId} =====
    public async Task<CompanySettingsResponse> UploadStampAsync(Guid companyId, Stream fileStream, string fileName, string contentType)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);

        // ⚠️ ไม่รับ SVG — SVG ฝัง <script> ได้ และไฟล์ถูก serve จาก origin
        // เดียวกับแอปที่เก็บ JWT ใน localStorage = stored XSS (กฎเดียวกับ
        // โลโก้แบรนด์/โลโก้บริษัท — CLAUDE.md กฎเหล็ก #4 C)
        var allowedTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" };
        if (!allowedTypes.Contains(contentType.ToLower()))
            throw new InvalidOperationException("รองรับเฉพาะไฟล์ PNG, JPEG, GIF, WebP เท่านั้น — ไม่รับ SVG ด้วยเหตุผลด้านความปลอดภัย (แนะนำ PNG พื้นหลังโปร่งใส)");

        var webRoot = _env.WebRootPath
            ?? Path.Combine(_env.ContentRootPath ?? Directory.GetCurrentDirectory(), "wwwroot");

        try
        {
            if (!string.IsNullOrEmpty(settings.StampPath) && File.Exists(settings.StampPath))
                File.Delete(settings.StampPath);
        }
        catch { /* old file may be locked / missing — keep going */ }

        var dir = Path.Combine(webRoot, "uploads", "stamps", companyId.ToString());
        var web = $"/uploads/stamps/{companyId}";
        ProcessedImageResult processed;
        try
        {
            processed = await _images.ProcessAndSaveAsync(fileStream, contentType, fileName, dir, web, ImageProfile.Logo);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"ระบบไม่มีสิทธิ์เขียนไฟล์ลงโฟลเดอร์ uploads ({dir}) — โปรดติดต่อผู้ดูแลระบบเพื่อเพิ่มสิทธิ์", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"ไม่พบโฟลเดอร์ปลายทาง ({dir}) — โปรดให้ผู้ดูแลระบบสร้างโฟลเดอร์ก่อน", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"บันทึกไฟล์ไม่สำเร็จ: {ex.Message}", ex);
        }

        settings.StampPath = processed.AbsolutePath;
        settings.StampUrl = processed.RelativeUrl;
        await _db.SaveChangesAsync();

        return MapToResponse(companyId, settings);
    }

    public async Task DeleteStampAsync(Guid companyId)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        if (!string.IsNullOrEmpty(settings.StampPath) && File.Exists(settings.StampPath))
            File.Delete(settings.StampPath);
        settings.StampPath = null;
        settings.StampUrl = null;
        await _db.SaveChangesAsync();
    }

    // ===== Number Series =====
    //
    // ⚠️ ใช้เฉพาะ **ตัวย่อ** (Prefix) เท่านั้น — ดู doc-comment ของ
    // DocumentNumberGenerator.ResolvePrefixAsync. field Format/CurrentNumber/
    // ResetPeriod ยังอยู่ในตารางเพื่อไม่ทำลายแถวเดิม แต่ไม่มีผลกับเลขที่ออกจริง
    // อีกต่อไป (เดิมมันสร้างเลขคนละทรงที่ไม่มี advisory lock)

    /// <summary>ตัวย่อต้องเป็น A-Z/0-9 ไม่เกิน 8 ตัว — เข้าไปเป็นส่วนหนึ่งของ
    /// เลขที่เอกสารตามกฎหมายและเป็น key ของ advisory lock: อักขระอย่างช่องว่าง/
    /// ขีด จะทำให้ตัวตัดเลขลำดับ (<c>Substring</c> หลัง "PREFIX-yyyyMMdd-")
    /// อ่านผิด และเลขบนกระดาษกลายเป็นอะไรก็ได้. ฝั่งฟอร์มเช็คแล้วแต่ API เปิด
    /// อยู่ — ด่านจริงต้องอยู่ที่นี่</summary>
    private static string NormalizePrefix(string? raw)
    {
        var p = (raw ?? "").Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Z0-9]{1,8}$"))
            throw new InvalidOperationException(
                "ตัวย่อเลขที่เอกสารต้องเป็นตัวอักษร A-Z หรือตัวเลข 0-9 ความยาว 1-8 ตัว "
                + "(ห้ามเว้นวรรค/ขีด/อักขระพิเศษ เพราะตัวย่อเป็นส่วนหนึ่งของเลขที่เอกสารตามกฎหมาย)");
        return p;
    }

    public async Task<NumberSeriesResponse> CreateNumberSeriesAsync(Guid companyId, CreateNumberSeriesRequest request)
    {
        var existing = await _db.Set<NumberSeries>()
            .AnyAsync(n => n.CompanyId == companyId && n.DocumentType == request.DocumentType && n.IsActive);
        if (existing)
            throw new InvalidOperationException("มี number series สำหรับประเภทเอกสารนี้อยู่แล้ว");
        request = request with { Prefix = NormalizePrefix(request.Prefix) };

        var series = new NumberSeries
        {
            CompanyId = companyId,
            DocumentType = request.DocumentType,
            Prefix = request.Prefix,
            Suffix = request.Suffix,
            Format = request.Format,
            CurrentNumber = request.StartNumber - 1,
            ResetPeriod = request.ResetPeriod
        };

        _db.Set<NumberSeries>().Add(series);
        await _db.SaveChangesAsync();

        return MapSeriesToResponse(series);
    }

    public async Task<List<NumberSeriesResponse>> GetNumberSeriesAsync(Guid companyId)
    {
        var series = await _db.Set<NumberSeries>()
            .Where(n => n.CompanyId == companyId)
            .OrderBy(n => n.DocumentType)
            .ToListAsync();

        return series.Select(MapSeriesToResponse).ToList();
    }

    public async Task<NumberSeriesResponse> UpdateNumberSeriesAsync(Guid companyId, Guid seriesId, UpdateNumberSeriesRequest request)
    {
        var series = await _db.Set<NumberSeries>()
            .FirstOrDefaultAsync(n => n.Id == seriesId && n.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ number series");

        if (request.Prefix != null) series.Prefix = NormalizePrefix(request.Prefix);
        if (request.Suffix != null) series.Suffix = request.Suffix;
        if (request.Format != null) series.Format = request.Format;
        if (request.CurrentNumber.HasValue) series.CurrentNumber = request.CurrentNumber.Value;
        if (request.ResetPeriod.HasValue) series.ResetPeriod = request.ResetPeriod.Value;
        if (request.IsActive.HasValue) series.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapSeriesToResponse(series);
    }

    public Task<string> GetNextNumberAsync(Guid companyId, DocumentType documentType)
        => GetNextNumberAsync(companyId, documentType, documentDate: null);

    /// <summary>
    /// เลขที่เอกสารถัดไป — <b>ส่งต่อให้ <c>DocumentNumberGenerator</c> ตัวเดียว
    /// ของระบบ</b>
    ///
    /// ═══ ทำไมต้องยุบ ═══
    /// เดิมเมธอดนี้เป็น "เครื่องออกเลขเครื่องที่สอง" ที่เดินคู่ขนานกับตัวหลัก
    /// (UI/POS/OCR ใช้ <c>DocumentNumberGenerator</c>, เส้น integration 6 จุดใช้
    /// ตัวนี้) และตัวนี้พลาด 4 อย่างที่ตัวหลักแก้ไปแล้ว:
    ///   • <b>ไม่มี advisory lock</b> ⇒ สอง request พร้อมกันได้เลขซ้ำ (§86/4
    ///     บังคับไม่ซ้ำ ไม่ขาดช่วง)
    ///   • <b>ไม่ <c>IgnoreQueryFilters()</c></b> ⇒ ใบที่ soft-delete มองไม่เห็น
    ///     → ออกเลขทับของเดิม
    ///   • <c>OrderByDescending(DocumentNumber)</c> = lexicographic max (พังวันที่
    ///     ทะลุ 9999 ใบ ซึ่งตัวหลักแก้ไว้ด้วย integer-max แล้ว)
    ///   • ตาราง prefix สำรองของตัวเองที่ <b>ขาด GoodsReceiptNote</b> → ตก "DOC-"
    /// และเมื่อมีแถว <c>NumberSeries</c> มันยังสร้างเลข**คนละทรง** (รายเดือน
    /// นับเอง) ⇒ บริษัทเดียวมีเลขสองรูปแบบปนกัน
    ///
    /// ตัวย่อที่ผู้ใช้ตั้งเองยังใช้ได้ — <c>DocumentNumberGenerator.ResolvePrefixAsync</c>
    /// อ่าน <c>NumberSeries.Prefix</c> ให้แล้ว (override เฉพาะตัวย่อ ไม่ใช่รูปแบบ)
    ///
    /// ⚠️ <b>ยังเหลืออีกครึ่ง</b>: <c>pg_advisory_xact_lock</c> กันได้จริงเฉพาะเมื่อ
    /// ผู้เรียกอยู่ใน transaction — <c>IntegrationService</c> ทั้ง 6 จุดยัง**ไม่เปิด
    /// transaction เลย** ล็อกจึงถูกปล่อยทันทีที่ statement จบ. ตอนนี้ตัวกันชั้น
    /// สุดท้ายคือ unique index <c>(CompanyId, DocumentNumber)</c> ⇒ ชนกันแล้ว
    /// **error ดัง** ไม่ใช่เลขซ้ำเงียบ ๆ (ดีกว่าเดิมที่ไม่มีทั้งล็อกและ integer-max
    /// ที่ถูกต้อง). งานที่เหลือคือห่อ create ของ IntegrationService ด้วย transaction
    /// — จดไว้ใน DOCUMENT_FLOW รอบ 112 ไม่ทำครึ่ง ๆ กลาง ๆ ในคอมมิตนี้
    /// </summary>
    public Task<string> GetNextNumberAsync(Guid companyId, DocumentType documentType, DateTime? documentDate)
        => Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, documentType, documentDate);

    // ===== API Key Management =====

    public async Task<ApiKeyCreatedResponse> CreateApiKeyAsync(Guid companyId, Guid userId, CreateApiKeyRequest request)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        if (!settings.EnableApiAccess)
            throw new InvalidOperationException("API access ยังไม่เปิดใช้งาน กรุณาเปิดในการตั้งค่า");

        var currentKeys = await _db.Set<ApiKey>()
            .CountAsync(k => k.CompanyId == companyId && k.Status == ApiKeyStatus.Active);
        if (currentKeys >= settings.MaxApiKeys)
            throw new InvalidOperationException($"จำนวน API key สูงสุด ({settings.MaxApiKeys}) เต็มแล้ว");

        // Generate raw key
        var rawKey = $"acc_{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}".Replace("=", "").Replace("+", "").Replace("/", "");
        var keyPrefix = rawKey[..8];
        var keyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);

        var apiKey = new ApiKey
        {
            CompanyId = companyId,
            CreatedByUserId = userId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            ExpiresAt = request.ExpiresAt,
            AllowedFeatures = request.AllowedFeatures,
            AllowedIpAddresses = request.AllowedIpAddresses,
            RateLimitPerMinute = request.RateLimitPerMinute,
            CanRead = request.CanRead,
            CanWrite = request.CanWrite,
            CanDelete = request.CanDelete,
            // ★ ช่องนี้ไม่เคยถูกเซ็ตมาก่อน ⇒ /api/v1 ปฏิเสธคีย์ทุกใบที่ scope ว่าง
            // ⇒ Connected API เข้าไม่ได้เลยสักเส้นตั้งแต่วันแรก (ผลตรวจ H-A4)
            Scopes = string.IsNullOrWhiteSpace(request.Scopes) ? null : request.Scopes!.Trim(),
        };

        _db.Set<ApiKey>().Add(apiKey);
        await _db.SaveChangesAsync();

        // Return raw key only this one time
        return new ApiKeyCreatedResponse(apiKey.Id, apiKey.Name, rawKey, keyPrefix, apiKey.CreatedAt);
    }

    public async Task<List<ApiKeyResponse>> GetApiKeysAsync(Guid companyId)
    {
        var keys = await _db.Set<ApiKey>()
            .Where(k => k.CompanyId == companyId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        return keys.Select(k => new ApiKeyResponse(
            k.Id, k.Name, k.KeyPrefix, k.Status, k.ExpiresAt, k.LastUsedAt,
            k.AllowedFeatures, k.CanRead, k.CanWrite, k.CanDelete, k.CreatedAt, k.Scopes)).ToList();
    }

    public async Task RevokeApiKeyAsync(Guid companyId, Guid apiKeyId)
    {
        var key = await _db.Set<ApiKey>()
            .FirstOrDefaultAsync(k => k.Id == apiKeyId && k.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ API key");

        key.Status = ApiKeyStatus.Revoked;
        await _db.SaveChangesAsync();
    }

    // ===== Helpers =====

    private async Task<CompanySettings> GetOrCreateSettingsAsync(Guid companyId)
    {
        var settings = await _db.Set<CompanySettings>().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings == null)
        {
            settings = new CompanySettings { CompanyId = companyId };
            _db.Set<CompanySettings>().Add(settings);
            await _db.SaveChangesAsync();
        }
        return settings;
    }

    public async Task<LandingServicesResponse?> GetLandingServicesAsync()
    {
        // Get the first company's settings (for single-tenant landing page)
        var settings = await _db.Set<CompanySettings>()
            .Where(s => !s.IsDeleted && s.LandingServicesJson != null)
            .FirstOrDefaultAsync();

        if (settings == null)
            return null;

        var services = new List<LandingServiceItem>();
        if (!string.IsNullOrEmpty(settings.LandingServicesJson))
        {
            try
            {
                services = JsonSerializer.Deserialize<List<LandingServiceItem>>(
                    settings.LandingServicesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to deserialize LandingServicesJson: {ex.Message}"); }
        }

        return new LandingServicesResponse(
            settings.LandingContactPhone,
            settings.LandingContactLine,
            settings.LandingContactEmail,
            services);
    }

    private static CompanySettingsResponse MapToResponse(Guid companyId, CompanySettings s) => new(
        companyId, s.LogoUrl, s.PrimaryColor, s.SecondaryColor,
        s.DefaultPaymentTerms, s.DefaultPaymentDueDays,
        // Document notes/footer
        s.InvoiceNotes, s.ReceiptNotes, s.QuotationNotes, s.InvoiceFooter, s.ReceiptFooter,
        // Email
        s.EmailFromName, s.EmailReplyTo, s.InvoiceEmailSubject, s.InvoiceEmailBody,
        // Tax
        s.DefaultVatRate, s.VatRegistered, s.VatRegistrationDate, s.WhtRecognitionBasis,
        // Security
        s.RequireApprovalForDocuments, s.ApprovalThresholdAmount,
        s.EnableApiAccess, s.MaxApiKeys,
        // Closing
        s.AutoCloseMonthEnd, s.MonthEndClosingDay, s.PreventPostToClosedPeriod,
        // e-Tax
        s.EtaxEnabled, s.EtaxTestMode, s.EtaxAutoSign, s.EtaxAutoSubmit,
        s.EtaxServiceProvider,
        !string.IsNullOrEmpty(s.EtaxCertificatePath),
        !string.IsNullOrEmpty(s.EtaxRdApiKey),
        // Landing Page
        s.LandingContactPhone, s.LandingContactLine, s.LandingContactEmail,
        s.LandingServicesJson,
        // OCR preference
        s.OcrBuyerInvoiceDefaultTarget,
        s.DefaultPaymentAccountId,
        s.WorkersCompensationEnabled,
        s.WorkersCompensationRatePercent,
        s.AutoAttachWhtCertPdf,
        s.BookkeeperName,
        s.BookkeeperCpdNumber,
        // Print layout
        s.ShowGlEntryOnDocument,
        s.DocumentTitleOverridesJson,
        string.IsNullOrWhiteSpace(s.DocumentLanguage) ? "th" : s.DocumentLanguage,
        s.ReceiptIssueMode,
        Accounting.Helpers.ReceiptIssuePolicy.Describe(s.ReceiptIssueMode),
        s.UnifyTaxInvoiceNumberSeries,
        // HR
        s.LeaveQuotasJson,
        s.EnforceManagerApproval,
        s.IsVehicleDealer,
        // Internal control
        s.SodBlockSelfApproval,
        s.BudgetCommitmentMode,
        s.AllowNegativeStock,
        s.EclEnabled,
        // ตราประทับบริษัท
        s.StampUrl,
        s.StampWidthMm,
        s.StampHeightMm,
        s.StampAlign,
        s.ShowProjectOnDocuments,
        s.ShowCostCenterOnDocuments,
        s.UseCustomAuthorizedSignatory,
        s.AuthorizedSignatoryName,
        s.AuthorizedSignatoryTitle,
        s.AuthorizedSignatorySignatureBase64,
        s.CashSaleStockPolicy,
        Accounting.Helpers.CashSaleStockRules.Describe(s.CashSaleStockPolicy),
        s.PosTipPayableAccountCode,
        Accounting.Helpers.TipAccountResolver.CodeCandidates(s.PosTipPayableAccountCode)[0]);

    private static NumberSeriesResponse MapSeriesToResponse(NumberSeries n) => new(
        n.Id, n.DocumentType, n.Prefix, n.Suffix, n.Format,
        n.CurrentNumber, n.ResetPeriod, n.IsActive);
}
