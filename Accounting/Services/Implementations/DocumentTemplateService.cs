using Accounting.Data;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DocumentTemplateService : IDocumentTemplateService
{
    private readonly AccountingDbContext _db;

    public DocumentTemplateService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<DocumentTemplateResponse> CreateAsync(Guid companyId, CreateDocumentTemplateRequest request)
    {
        var template = new DocumentTemplate
        {
            CompanyId = companyId,
            Name = request.Name,
            LayoutStyle = request.LayoutStyle ?? "Classic",
            Description = request.Description,
            DocumentType = request.DocumentType,
            IsDefault = request.IsDefault
        };

        ApplyRequestToTemplate(template, request);

        // If set as default, unset other defaults for this document type
        if (request.IsDefault)
            await UnsetDefaultsAsync(companyId, request.DocumentType);

        _db.DocumentTemplates.Add(template);
        await _db.SaveChangesAsync();

        return MapToResponse(template);
    }

    public DocumentTemplate BuildTransient(Guid companyId, CreateDocumentTemplateRequest request)
    {
        var template = new DocumentTemplate
        {
            CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Preview" : request.Name,
            LayoutStyle = request.LayoutStyle ?? "Classic",
            Description = request.Description,
            DocumentType = request.DocumentType,
            IsDefault = request.IsDefault,
        };
        ApplyRequestToTemplate(template, request);   // entity defaults preserved for unsent fields
        return template;
    }

    public async Task<DocumentTemplateResponse> GetByIdAsync(Guid companyId, Guid templateId)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");

        return MapToResponse(template);
    }

    public async Task<List<DocumentTemplateListResponse>> GetAllAsync(Guid companyId, DocumentType? documentType = null)
    {
        var query = _db.DocumentTemplates.Where(t => t.CompanyId == companyId);
        if (documentType.HasValue)
            query = query.Where(t => t.DocumentType == documentType.Value);

        return await query.OrderBy(t => t.DocumentType).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name)
            .Select(t => new DocumentTemplateListResponse(
                t.Id, t.Name, t.DocumentType, t.IsDefault, t.IsActive, t.IsEtaxTemplate, t.CreatedAt))
            .ToListAsync();
    }

    public async Task<DocumentTemplateResponse> UpdateAsync(Guid companyId, Guid templateId, UpdateDocumentTemplateRequest request)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");

        if (request.Name != null) template.Name = request.Name;
        if (request.Description != null) template.Description = request.Description;
        if (request.IsActive.HasValue) template.IsActive = request.IsActive.Value;

        if (request.IsDefault == true)
        {
            await UnsetDefaultsAsync(companyId, template.DocumentType);
            template.IsDefault = true;
        }

        ApplyUpdateToTemplate(template, request);

        await _db.SaveChangesAsync();
        return MapToResponse(template);
    }

    public async Task DeleteAsync(Guid companyId, Guid templateId)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");

        if (template.IsDefault)
            throw new InvalidOperationException("ไม่สามารถลบเทมเพลตที่เป็นค่าเริ่มต้นได้");

        template.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<DocumentTemplateResponse> GetDefaultTemplateAsync(Guid companyId, DocumentType documentType)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.CompanyId == companyId && t.DocumentType == documentType && t.IsDefault && t.IsActive);

        if (template == null)
        {
            // Auto-create default template
            template = CreateDefaultTemplate(companyId, documentType);
            _db.DocumentTemplates.Add(template);
            await _db.SaveChangesAsync();
        }

        return MapToResponse(template);
    }

    public async Task SetDefaultAsync(Guid companyId, Guid templateId)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");

        await UnsetDefaultsAsync(companyId, template.DocumentType);
        template.IsDefault = true;
        await _db.SaveChangesAsync();
    }

    public async Task<DocumentTemplateResponse> DuplicateAsync(Guid companyId, Guid templateId, string newName)
    {
        var source = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเทมเพลต");

        var clone = new DocumentTemplate
        {
            CompanyId = companyId,
            Name = newName,
            Description = $"คัดลอกจาก {source.Name}",
            DocumentType = source.DocumentType,
            IsDefault = false,
            PaperSize = source.PaperSize,
            Orientation = source.Orientation,
            MarginTop = source.MarginTop,
            MarginBottom = source.MarginBottom,
            MarginLeft = source.MarginLeft,
            MarginRight = source.MarginRight,
            ShowLogo = source.ShowLogo,
            LogoPosition = source.LogoPosition,
            LogoWidth = source.LogoWidth,
            LogoHeight = source.LogoHeight,
            ShowCompanyName = source.ShowCompanyName,
            ShowCompanyNameEn = source.ShowCompanyNameEn,
            ShowCompanyAddress = source.ShowCompanyAddress,
            ShowCompanyTaxId = source.ShowCompanyTaxId,
            ShowCompanyPhone = source.ShowCompanyPhone,
            ShowCompanyEmail = source.ShowCompanyEmail,
            HeaderBackgroundColor = source.HeaderBackgroundColor,
            HeaderTextColor = source.HeaderTextColor,
            CustomTitle = source.CustomTitle,
            CustomTitleEn = source.CustomTitleEn,
            TitleFontSize = source.TitleFontSize,
            ShowDocumentNumber = source.ShowDocumentNumber,
            ShowDocumentDate = source.ShowDocumentDate,
            ShowDueDate = source.ShowDueDate,
            ShowReference = source.ShowReference,
            ShowContactTaxId = source.ShowContactTaxId,
            ShowContactBranch = source.ShowContactBranch,
            ShowContactAddress = source.ShowContactAddress,
            ShowContactPhone = source.ShowContactPhone,
            ShowContactEmail = source.ShowContactEmail,
            ContactSectionTitle = source.ContactSectionTitle,
            ContactSectionTitleEn = source.ContactSectionTitleEn,
            ShowLineNumber = source.ShowLineNumber,
            ShowItemCode = source.ShowItemCode,
            ShowUnit = source.ShowUnit,
            ShowDiscount = source.ShowDiscount,
            ShowVatPerLine = source.ShowVatPerLine,
            ShowWithholdingTax = source.ShowWithholdingTax,
            TableHeaderColor = source.TableHeaderColor,
            TableHeaderTextColor = source.TableHeaderTextColor,
            TableStripedColor = source.TableStripedColor,
            TableBorderStyle = source.TableBorderStyle,
            ShowSubTotal = source.ShowSubTotal,
            ShowDiscountTotal = source.ShowDiscountTotal,
            ShowVatSummary = source.ShowVatSummary,
            ShowWithholdingTaxSummary = source.ShowWithholdingTaxSummary,
            ShowAmountInWords = source.ShowAmountInWords,
            AmountInWordsLanguage = source.AmountInWordsLanguage,
            FooterNotes = source.FooterNotes,
            FooterNotesEn = source.FooterNotesEn,
            ShowPaymentTerms = source.ShowPaymentTerms,
            ShowBankDetails = source.ShowBankDetails,
            BankDetailsText = source.BankDetailsText,
            BankDetailsTextEn = source.BankDetailsTextEn,
            ShowSignature = source.ShowSignature,
            SignatureCount = source.SignatureCount,
            SignatureLabel1 = source.SignatureLabel1,
            SignatureLabel2 = source.SignatureLabel2,
            SignatureLabel3 = source.SignatureLabel3,
            ShowCompanyStamp = source.ShowCompanyStamp,
            ShowWatermark = source.ShowWatermark,
            WatermarkText = source.WatermarkText,
            WatermarkOpacity = source.WatermarkOpacity,
            LayoutStyle = source.LayoutStyle,
            FontFamily = source.FontFamily,
            BodyFontSize = source.BodyFontSize,
            PrimaryColor = source.PrimaryColor,
            AccentColor = source.AccentColor,
            Language = source.Language,
            ShowBilingual = source.ShowBilingual,
            IsEtaxTemplate = source.IsEtaxTemplate,
            ShowQrCode = source.ShowQrCode,
            QrCodeType = source.QrCodeType,
            PromptPayId = source.PromptPayId,
            DefaultCopies = source.DefaultCopies,
            CopyLabels = source.CopyLabels
        };

        _db.DocumentTemplates.Add(clone);
        await _db.SaveChangesAsync();
        return MapToResponse(clone);
    }

    // ===== Helpers =====

    private async Task UnsetDefaultsAsync(Guid companyId, DocumentType documentType)
    {
        var defaults = await _db.DocumentTemplates
            .Where(t => t.CompanyId == companyId && t.DocumentType == documentType && t.IsDefault)
            .ToListAsync();
        foreach (var t in defaults) t.IsDefault = false;
    }

    private static DocumentTemplate CreateDefaultTemplate(Guid companyId, DocumentType documentType)
    {
        var title = documentType switch
        {
            DocumentType.Quotation => "ใบเสนอราคา",
            DocumentType.Invoice => "ใบแจ้งหนี้",
            DocumentType.Receipt => "ใบเสร็จรับเงิน",
            DocumentType.TaxInvoice => "ใบกำกับภาษี",
            DocumentType.DebitNote => "ใบเพิ่มหนี้",
            DocumentType.CreditNote => "ใบลดหนี้",
            DocumentType.DeliveryNote => "ใบส่งของ",
            DocumentType.BillingNote => "ใบวางบิล",
            DocumentType.ReceiptVoucher => "ใบสำคัญรับ",
            DocumentType.PurchaseRequisition => "ใบขอซื้อ",
            DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
            DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
            DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
            DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
            _ => "เอกสาร"
        };

        return new DocumentTemplate
        {
            CompanyId = companyId,
            Name = $"เทมเพลตมาตรฐาน - {title}",
            DocumentType = documentType,
            IsDefault = true,
            CustomTitle = title
        };
    }

    private static void ApplyRequestToTemplate(DocumentTemplate t, CreateDocumentTemplateRequest r)
    {
        if (r.PaperSize != null) t.PaperSize = r.PaperSize;
        if (r.Orientation != null) t.Orientation = r.Orientation;
        if (r.MarginTop.HasValue) t.MarginTop = r.MarginTop.Value;
        if (r.MarginBottom.HasValue) t.MarginBottom = r.MarginBottom.Value;
        if (r.MarginLeft.HasValue) t.MarginLeft = r.MarginLeft.Value;
        if (r.MarginRight.HasValue) t.MarginRight = r.MarginRight.Value;
        if (r.ShowLogo.HasValue) t.ShowLogo = r.ShowLogo.Value;
        if (r.LogoPosition != null) t.LogoPosition = r.LogoPosition;
        if (r.LogoWidth.HasValue) t.LogoWidth = r.LogoWidth.Value;
        if (r.LogoHeight.HasValue) t.LogoHeight = r.LogoHeight.Value;
        if (r.ShowCompanyName.HasValue) t.ShowCompanyName = r.ShowCompanyName.Value;
        if (r.ShowCompanyNameEn.HasValue) t.ShowCompanyNameEn = r.ShowCompanyNameEn.Value;
        if (r.ShowCompanyAddress.HasValue) t.ShowCompanyAddress = r.ShowCompanyAddress.Value;
        if (r.ShowCompanyTaxId.HasValue) t.ShowCompanyTaxId = r.ShowCompanyTaxId.Value;
        if (r.ShowCompanyPhone.HasValue) t.ShowCompanyPhone = r.ShowCompanyPhone.Value;
        if (r.ShowCompanyEmail.HasValue) t.ShowCompanyEmail = r.ShowCompanyEmail.Value;
        if (r.HeaderBackgroundColor != null) t.HeaderBackgroundColor = r.HeaderBackgroundColor;
        if (r.HeaderTextColor != null) t.HeaderTextColor = r.HeaderTextColor;
        if (r.CustomTitle != null) t.CustomTitle = r.CustomTitle;
        if (r.CustomTitleEn != null) t.CustomTitleEn = r.CustomTitleEn;
        if (r.TitleFontSize != null) t.TitleFontSize = r.TitleFontSize;
        if (r.ShowDocumentNumber.HasValue) t.ShowDocumentNumber = r.ShowDocumentNumber.Value;
        if (r.ShowDocumentDate.HasValue) t.ShowDocumentDate = r.ShowDocumentDate.Value;
        if (r.ShowDueDate.HasValue) t.ShowDueDate = r.ShowDueDate.Value;
        if (r.ShowReference.HasValue) t.ShowReference = r.ShowReference.Value;
        if (r.ShowContactTaxId.HasValue) t.ShowContactTaxId = r.ShowContactTaxId.Value;
        if (r.ShowContactBranch.HasValue) t.ShowContactBranch = r.ShowContactBranch.Value;
        if (r.ShowContactAddress.HasValue) t.ShowContactAddress = r.ShowContactAddress.Value;
        if (r.ShowContactPhone.HasValue) t.ShowContactPhone = r.ShowContactPhone.Value;
        if (r.ShowContactEmail.HasValue) t.ShowContactEmail = r.ShowContactEmail.Value;
        if (r.ContactSectionTitle != null) t.ContactSectionTitle = r.ContactSectionTitle;
        if (r.ContactSectionTitleEn != null) t.ContactSectionTitleEn = r.ContactSectionTitleEn;
        if (r.ShowLineNumber.HasValue) t.ShowLineNumber = r.ShowLineNumber.Value;
        if (r.ShowItemCode.HasValue) t.ShowItemCode = r.ShowItemCode.Value;
        if (r.ShowUnit.HasValue) t.ShowUnit = r.ShowUnit.Value;
        if (r.ShowDiscount.HasValue) t.ShowDiscount = r.ShowDiscount.Value;
        if (r.ShowVatPerLine.HasValue) t.ShowVatPerLine = r.ShowVatPerLine.Value;
        if (r.ShowWithholdingTax.HasValue) t.ShowWithholdingTax = r.ShowWithholdingTax.Value;
        if (r.TableHeaderColor != null) t.TableHeaderColor = r.TableHeaderColor;
        if (r.TableHeaderTextColor != null) t.TableHeaderTextColor = r.TableHeaderTextColor;
        if (r.TableStripedColor != null) t.TableStripedColor = r.TableStripedColor;
        if (r.TableBorderStyle != null) t.TableBorderStyle = r.TableBorderStyle;
        if (r.ShowSubTotal.HasValue) t.ShowSubTotal = r.ShowSubTotal.Value;
        if (r.ShowDiscountTotal.HasValue) t.ShowDiscountTotal = r.ShowDiscountTotal.Value;
        if (r.ShowVatSummary.HasValue) t.ShowVatSummary = r.ShowVatSummary.Value;
        if (r.ShowWithholdingTaxSummary.HasValue) t.ShowWithholdingTaxSummary = r.ShowWithholdingTaxSummary.Value;
        if (r.ShowAmountInWords.HasValue) t.ShowAmountInWords = r.ShowAmountInWords.Value;
        if (r.AmountInWordsLanguage != null) t.AmountInWordsLanguage = r.AmountInWordsLanguage;
        if (r.FooterNotes != null) t.FooterNotes = r.FooterNotes;
        if (r.FooterNotesEn != null) t.FooterNotesEn = r.FooterNotesEn;
        if (r.ShowPaymentTerms.HasValue) t.ShowPaymentTerms = r.ShowPaymentTerms.Value;
        if (r.ShowBankDetails.HasValue) t.ShowBankDetails = r.ShowBankDetails.Value;
        if (r.BankDetailsText != null) t.BankDetailsText = r.BankDetailsText;
        if (r.BankDetailsTextEn != null) t.BankDetailsTextEn = r.BankDetailsTextEn;
        if (r.ShowSignature.HasValue) t.ShowSignature = r.ShowSignature.Value;
        if (r.SignatureCount.HasValue) t.SignatureCount = r.SignatureCount.Value;
        if (r.SignatureLabel1 != null) t.SignatureLabel1 = r.SignatureLabel1;
        if (r.SignatureLabel2 != null) t.SignatureLabel2 = r.SignatureLabel2;
        if (r.SignatureLabel3 != null) t.SignatureLabel3 = r.SignatureLabel3;
        if (r.ShowCompanyStamp.HasValue) t.ShowCompanyStamp = r.ShowCompanyStamp.Value;
        if (r.ShowWatermark.HasValue) t.ShowWatermark = r.ShowWatermark.Value;
        if (r.WatermarkText != null) t.WatermarkText = r.WatermarkText;
        if (r.WatermarkOpacity.HasValue) t.WatermarkOpacity = r.WatermarkOpacity.Value;
        if (r.LayoutStyle != null) t.LayoutStyle = r.LayoutStyle;
        if (r.FontFamily != null) t.FontFamily = r.FontFamily;
        if (r.BodyFontSize != null) t.BodyFontSize = r.BodyFontSize;
        if (r.PrimaryColor != null) t.PrimaryColor = r.PrimaryColor;
        if (r.AccentColor != null) t.AccentColor = r.AccentColor;
        if (r.Language != null) t.Language = r.Language;
        if (r.ShowBilingual.HasValue) t.ShowBilingual = r.ShowBilingual.Value;
        if (r.IsEtaxTemplate.HasValue) t.IsEtaxTemplate = r.IsEtaxTemplate.Value;
        if (r.EtaxServiceProvider != null) t.EtaxServiceProvider = r.EtaxServiceProvider;
        if (r.AutoGenerateEtaxXml.HasValue) t.AutoGenerateEtaxXml = r.AutoGenerateEtaxXml.Value;
        if (r.ShowQrCode.HasValue) t.ShowQrCode = r.ShowQrCode.Value;
        if (r.QrCodeType != null) t.QrCodeType = r.QrCodeType;
        if (r.PromptPayId != null) t.PromptPayId = r.PromptPayId;
        if (r.QrCodeCustomData != null) t.QrCodeCustomData = r.QrCodeCustomData;
        if (r.DefaultCopies.HasValue) t.DefaultCopies = r.DefaultCopies.Value;
        if (r.CopyLabels != null) t.CopyLabels = r.CopyLabels;
    }

    private static void ApplyUpdateToTemplate(DocumentTemplate t, UpdateDocumentTemplateRequest r)
    {
        if (r.PaperSize != null) t.PaperSize = r.PaperSize;
        if (r.Orientation != null) t.Orientation = r.Orientation;
        if (r.MarginTop.HasValue) t.MarginTop = r.MarginTop.Value;
        if (r.MarginBottom.HasValue) t.MarginBottom = r.MarginBottom.Value;
        if (r.MarginLeft.HasValue) t.MarginLeft = r.MarginLeft.Value;
        if (r.MarginRight.HasValue) t.MarginRight = r.MarginRight.Value;
        if (r.ShowLogo.HasValue) t.ShowLogo = r.ShowLogo.Value;
        if (r.LogoPosition != null) t.LogoPosition = r.LogoPosition;
        if (r.LogoWidth.HasValue) t.LogoWidth = r.LogoWidth.Value;
        if (r.LogoHeight.HasValue) t.LogoHeight = r.LogoHeight.Value;
        if (r.ShowCompanyName.HasValue) t.ShowCompanyName = r.ShowCompanyName.Value;
        if (r.ShowCompanyNameEn.HasValue) t.ShowCompanyNameEn = r.ShowCompanyNameEn.Value;
        if (r.ShowCompanyAddress.HasValue) t.ShowCompanyAddress = r.ShowCompanyAddress.Value;
        if (r.ShowCompanyTaxId.HasValue) t.ShowCompanyTaxId = r.ShowCompanyTaxId.Value;
        if (r.ShowCompanyPhone.HasValue) t.ShowCompanyPhone = r.ShowCompanyPhone.Value;
        if (r.ShowCompanyEmail.HasValue) t.ShowCompanyEmail = r.ShowCompanyEmail.Value;
        if (r.HeaderBackgroundColor != null) t.HeaderBackgroundColor = r.HeaderBackgroundColor;
        if (r.HeaderTextColor != null) t.HeaderTextColor = r.HeaderTextColor;
        if (r.CustomTitle != null) t.CustomTitle = r.CustomTitle;
        if (r.CustomTitleEn != null) t.CustomTitleEn = r.CustomTitleEn;
        if (r.TitleFontSize != null) t.TitleFontSize = r.TitleFontSize;
        if (r.ShowDocumentNumber.HasValue) t.ShowDocumentNumber = r.ShowDocumentNumber.Value;
        if (r.ShowDocumentDate.HasValue) t.ShowDocumentDate = r.ShowDocumentDate.Value;
        if (r.ShowDueDate.HasValue) t.ShowDueDate = r.ShowDueDate.Value;
        if (r.ShowReference.HasValue) t.ShowReference = r.ShowReference.Value;
        if (r.ShowContactTaxId.HasValue) t.ShowContactTaxId = r.ShowContactTaxId.Value;
        if (r.ShowContactBranch.HasValue) t.ShowContactBranch = r.ShowContactBranch.Value;
        if (r.ShowContactAddress.HasValue) t.ShowContactAddress = r.ShowContactAddress.Value;
        if (r.ShowContactPhone.HasValue) t.ShowContactPhone = r.ShowContactPhone.Value;
        if (r.ShowContactEmail.HasValue) t.ShowContactEmail = r.ShowContactEmail.Value;
        if (r.ContactSectionTitle != null) t.ContactSectionTitle = r.ContactSectionTitle;
        if (r.ContactSectionTitleEn != null) t.ContactSectionTitleEn = r.ContactSectionTitleEn;
        if (r.ShowLineNumber.HasValue) t.ShowLineNumber = r.ShowLineNumber.Value;
        if (r.ShowItemCode.HasValue) t.ShowItemCode = r.ShowItemCode.Value;
        if (r.ShowUnit.HasValue) t.ShowUnit = r.ShowUnit.Value;
        if (r.ShowDiscount.HasValue) t.ShowDiscount = r.ShowDiscount.Value;
        if (r.ShowVatPerLine.HasValue) t.ShowVatPerLine = r.ShowVatPerLine.Value;
        if (r.ShowWithholdingTax.HasValue) t.ShowWithholdingTax = r.ShowWithholdingTax.Value;
        if (r.TableHeaderColor != null) t.TableHeaderColor = r.TableHeaderColor;
        if (r.TableHeaderTextColor != null) t.TableHeaderTextColor = r.TableHeaderTextColor;
        if (r.TableStripedColor != null) t.TableStripedColor = r.TableStripedColor;
        if (r.TableBorderStyle != null) t.TableBorderStyle = r.TableBorderStyle;
        if (r.ShowSubTotal.HasValue) t.ShowSubTotal = r.ShowSubTotal.Value;
        if (r.ShowDiscountTotal.HasValue) t.ShowDiscountTotal = r.ShowDiscountTotal.Value;
        if (r.ShowVatSummary.HasValue) t.ShowVatSummary = r.ShowVatSummary.Value;
        if (r.ShowWithholdingTaxSummary.HasValue) t.ShowWithholdingTaxSummary = r.ShowWithholdingTaxSummary.Value;
        if (r.ShowAmountInWords.HasValue) t.ShowAmountInWords = r.ShowAmountInWords.Value;
        if (r.AmountInWordsLanguage != null) t.AmountInWordsLanguage = r.AmountInWordsLanguage;
        if (r.FooterNotes != null) t.FooterNotes = r.FooterNotes;
        if (r.FooterNotesEn != null) t.FooterNotesEn = r.FooterNotesEn;
        if (r.ShowPaymentTerms.HasValue) t.ShowPaymentTerms = r.ShowPaymentTerms.Value;
        if (r.ShowBankDetails.HasValue) t.ShowBankDetails = r.ShowBankDetails.Value;
        if (r.BankDetailsText != null) t.BankDetailsText = r.BankDetailsText;
        if (r.BankDetailsTextEn != null) t.BankDetailsTextEn = r.BankDetailsTextEn;
        if (r.ShowSignature.HasValue) t.ShowSignature = r.ShowSignature.Value;
        if (r.SignatureCount.HasValue) t.SignatureCount = r.SignatureCount.Value;
        if (r.SignatureLabel1 != null) t.SignatureLabel1 = r.SignatureLabel1;
        if (r.SignatureLabel2 != null) t.SignatureLabel2 = r.SignatureLabel2;
        if (r.SignatureLabel3 != null) t.SignatureLabel3 = r.SignatureLabel3;
        if (r.ShowCompanyStamp.HasValue) t.ShowCompanyStamp = r.ShowCompanyStamp.Value;
        if (r.ShowWatermark.HasValue) t.ShowWatermark = r.ShowWatermark.Value;
        if (r.WatermarkText != null) t.WatermarkText = r.WatermarkText;
        if (r.WatermarkOpacity.HasValue) t.WatermarkOpacity = r.WatermarkOpacity.Value;
        if (r.LayoutStyle != null) t.LayoutStyle = r.LayoutStyle;
        if (r.FontFamily != null) t.FontFamily = r.FontFamily;
        if (r.BodyFontSize != null) t.BodyFontSize = r.BodyFontSize;
        if (r.PrimaryColor != null) t.PrimaryColor = r.PrimaryColor;
        if (r.AccentColor != null) t.AccentColor = r.AccentColor;
        if (r.Language != null) t.Language = r.Language;
        if (r.ShowBilingual.HasValue) t.ShowBilingual = r.ShowBilingual.Value;
        if (r.IsEtaxTemplate.HasValue) t.IsEtaxTemplate = r.IsEtaxTemplate.Value;
        if (r.EtaxServiceProvider != null) t.EtaxServiceProvider = r.EtaxServiceProvider;
        if (r.AutoGenerateEtaxXml.HasValue) t.AutoGenerateEtaxXml = r.AutoGenerateEtaxXml.Value;
        if (r.ShowQrCode.HasValue) t.ShowQrCode = r.ShowQrCode.Value;
        if (r.QrCodeType != null) t.QrCodeType = r.QrCodeType;
        if (r.PromptPayId != null) t.PromptPayId = r.PromptPayId;
        if (r.QrCodeCustomData != null) t.QrCodeCustomData = r.QrCodeCustomData;
        if (r.DefaultCopies.HasValue) t.DefaultCopies = r.DefaultCopies.Value;
        if (r.CopyLabels != null) t.CopyLabels = r.CopyLabels;
    }

    private static DocumentTemplateResponse MapToResponse(DocumentTemplate t) => new(
        t.Id, t.Name, t.Description, t.DocumentType, t.IsDefault, t.IsActive,
        t.PaperSize, t.Orientation, t.ShowLogo, t.LogoPosition,
        t.ShowCompanyName, t.ShowCompanyAddress, t.ShowCompanyTaxId,
        t.CustomTitle, t.CustomTitleEn,
        t.ShowLineNumber, t.ShowUnit, t.ShowDiscount, t.ShowVatPerLine, t.ShowWithholdingTax,
        t.TableHeaderColor, t.TableBorderStyle, t.ShowAmountInWords,
        t.FooterNotes, t.ShowPaymentTerms, t.ShowBankDetails,
        t.ShowSignature, t.SignatureCount, t.ShowQrCode, t.QrCodeType, t.PromptPayId,
        t.LayoutStyle, t.FontFamily, t.Language, t.ShowBilingual,
        t.IsEtaxTemplate, t.AutoGenerateEtaxXml, t.DefaultCopies, t.CreatedAt);
}
