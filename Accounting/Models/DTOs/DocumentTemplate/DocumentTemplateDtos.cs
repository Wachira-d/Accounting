using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.DocumentTemplate;

// ===== Template CRUD =====
public record CreateDocumentTemplateRequest(
    string Name,
    string? Description,
    DocumentType DocumentType,
    bool IsDefault,

    // Page Layout
    string? PaperSize,
    string? Orientation,
    decimal? MarginTop,
    decimal? MarginBottom,
    decimal? MarginLeft,
    decimal? MarginRight,

    // Header
    bool? ShowLogo,
    string? LogoPosition,
    decimal? LogoWidth,
    decimal? LogoHeight,
    bool? ShowCompanyName,
    bool? ShowCompanyNameEn,
    bool? ShowCompanyAddress,
    bool? ShowCompanyTaxId,
    bool? ShowCompanyPhone,
    bool? ShowCompanyEmail,
    string? HeaderBackgroundColor,
    string? HeaderTextColor,

    // Title
    string? CustomTitle,
    string? CustomTitleEn,
    string? TitleFontSize,
    bool? ShowDocumentNumber,
    bool? ShowDocumentDate,
    bool? ShowDueDate,
    bool? ShowReference,

    // Contact
    bool? ShowContactTaxId,
    bool? ShowContactBranch,
    bool? ShowContactAddress,
    bool? ShowContactPhone,
    bool? ShowContactEmail,
    string? ContactSectionTitle,
    string? ContactSectionTitleEn,

    // Table
    bool? ShowLineNumber,
    bool? ShowItemCode,
    bool? ShowUnit,
    bool? ShowDiscount,
    bool? ShowVatPerLine,
    bool? ShowWithholdingTax,
    string? TableHeaderColor,
    string? TableHeaderTextColor,
    string? TableStripedColor,
    string? TableBorderStyle,

    // Summary
    bool? ShowSubTotal,
    bool? ShowDiscountTotal,
    bool? ShowVatSummary,
    bool? ShowWithholdingTaxSummary,
    bool? ShowAmountInWords,
    string? AmountInWordsLanguage,

    // Footer
    string? FooterNotes,
    string? FooterNotesEn,
    bool? ShowPaymentTerms,
    bool? ShowBankDetails,
    string? BankDetailsText,
    string? BankDetailsTextEn,

    // Signature
    bool? ShowSignature,
    int? SignatureCount,
    string? SignatureLabel1,
    string? SignatureLabel2,
    string? SignatureLabel3,
    string? SignatureLabel1En,
    string? SignatureLabel2En,
    string? SignatureLabel3En,
    bool? ShowCompanyStamp,
    string? StampImagePath,

    // Watermark
    bool? ShowWatermark,
    string? WatermarkText,
    decimal? WatermarkOpacity,

    // Font
    string? LayoutStyle,
    string? FontFamily,
    string? BodyFontSize,
    string? PrimaryColor,
    string? AccentColor,

    // Language
    string? Language,
    bool? ShowBilingual,

    // e-Tax
    bool? IsEtaxTemplate,
    string? EtaxServiceProvider,
    bool? AutoGenerateEtaxXml,

    // QR Code
    bool? ShowQrCode,
    string? QrCodeType,
    string? PromptPayId,
    string? QrCodeCustomData,

    // Copy
    int? DefaultCopies,
    string? CopyLabels,
    // ตำแหน่งป้าย ต้นฉบับ/สำเนา: Watermark (ลายน้ำกลางหน้า) / TopRight / TopLeft
    string? CopyLabelPosition = null);

public record UpdateDocumentTemplateRequest(
    string? Name,
    string? Description,
    bool? IsDefault,
    bool? IsActive,

    // All same optional fields as Create
    string? PaperSize,
    string? Orientation,
    decimal? MarginTop,
    decimal? MarginBottom,
    decimal? MarginLeft,
    decimal? MarginRight,

    bool? ShowLogo,
    string? LogoPosition,
    decimal? LogoWidth,
    decimal? LogoHeight,
    bool? ShowCompanyName,
    bool? ShowCompanyNameEn,
    bool? ShowCompanyAddress,
    bool? ShowCompanyTaxId,
    bool? ShowCompanyPhone,
    bool? ShowCompanyEmail,
    string? HeaderBackgroundColor,
    string? HeaderTextColor,

    string? CustomTitle,
    string? CustomTitleEn,
    string? TitleFontSize,
    bool? ShowDocumentNumber,
    bool? ShowDocumentDate,
    bool? ShowDueDate,
    bool? ShowReference,

    bool? ShowContactTaxId,
    bool? ShowContactBranch,
    bool? ShowContactAddress,
    bool? ShowContactPhone,
    bool? ShowContactEmail,
    string? ContactSectionTitle,
    string? ContactSectionTitleEn,

    bool? ShowLineNumber,
    bool? ShowItemCode,
    bool? ShowUnit,
    bool? ShowDiscount,
    bool? ShowVatPerLine,
    bool? ShowWithholdingTax,
    string? TableHeaderColor,
    string? TableHeaderTextColor,
    string? TableStripedColor,
    string? TableBorderStyle,

    bool? ShowSubTotal,
    bool? ShowDiscountTotal,
    bool? ShowVatSummary,
    bool? ShowWithholdingTaxSummary,
    bool? ShowAmountInWords,
    string? AmountInWordsLanguage,

    string? FooterNotes,
    string? FooterNotesEn,
    bool? ShowPaymentTerms,
    bool? ShowBankDetails,
    string? BankDetailsText,
    string? BankDetailsTextEn,

    bool? ShowSignature,
    int? SignatureCount,
    string? SignatureLabel1,
    string? SignatureLabel2,
    string? SignatureLabel3,
    string? SignatureLabel1En,
    string? SignatureLabel2En,
    string? SignatureLabel3En,
    bool? ShowCompanyStamp,
    string? StampImagePath,

    bool? ShowWatermark,
    string? WatermarkText,
    decimal? WatermarkOpacity,

    string? LayoutStyle,
    string? FontFamily,
    string? BodyFontSize,
    string? PrimaryColor,
    string? AccentColor,

    string? Language,
    bool? ShowBilingual,

    bool? IsEtaxTemplate,
    string? EtaxServiceProvider,
    bool? AutoGenerateEtaxXml,

    bool? ShowQrCode,
    string? QrCodeType,
    string? PromptPayId,
    string? QrCodeCustomData,

    int? DefaultCopies,
    string? CopyLabels,
    string? CopyLabelPosition = null);

public record DocumentTemplateResponse(
    Guid Id,
    string Name,
    string? Description,
    DocumentType DocumentType,
    bool IsDefault,
    bool IsActive,
    string PaperSize,
    string Orientation,
    bool ShowLogo,
    string LogoPosition,
    bool ShowCompanyName,
    bool ShowCompanyAddress,
    bool ShowCompanyTaxId,
    string? CustomTitle,
    string? CustomTitleEn,
    bool ShowLineNumber,
    bool ShowUnit,
    bool ShowDiscount,
    bool ShowVatPerLine,
    bool ShowWithholdingTax,
    string? TableHeaderColor,
    string TableBorderStyle,
    bool ShowAmountInWords,
    string? FooterNotes,
    bool ShowPaymentTerms,
    bool ShowBankDetails,
    bool ShowSignature,
    int SignatureCount,
    bool ShowQrCode,
    string? QrCodeType,
    string? PromptPayId,
    string LayoutStyle,
    string FontFamily,
    string Language,
    bool ShowBilingual,
    bool IsEtaxTemplate,
    bool AutoGenerateEtaxXml,
    int DefaultCopies,
    DateTime CreatedAt,
    // Watermark + ตำแหน่งป้าย ต้นฉบับ/สำเนา (optional ท้าย record — ไม่กระทบ caller เดิม)
    bool ShowWatermark = false,
    string? WatermarkText = null,
    decimal WatermarkOpacity = 0.15m,
    string? CopyLabelPosition = null);

public record DocumentTemplateListResponse(
    Guid Id,
    string Name,
    DocumentType DocumentType,
    bool IsDefault,
    bool IsActive,
    bool IsEtaxTemplate,
    DateTime CreatedAt);

// ===== PDF Generation =====
public record GeneratePdfRequest(
    Guid DocumentId,
    Guid? TemplateId,               // null = ใช้ default template
    string? WatermarkOverride,       // override watermark ชั่วคราว e.g. "สำเนา"
    int? CopyNumber,                 // เลขที่สำเนา
    string? Language);               // override language

public record GeneratePdfResponse(
    Guid DocumentId,
    string DocumentNumber,
    string FileName,
    string ContentType,
    long FileSize,
    byte[] PdfData,
    DateTime GeneratedAt);

public record PdfPreviewRequest(
    Guid? TemplateId,
    string? Language);

// ===== e-Tax Invoice =====
public record GenerateEtaxRequest(
    Guid DocumentId,
    bool SignDigitally = true);

public record EtaxInvoiceResponse(
    Guid Id,
    Guid DocumentId,
    string DocumentNumber,
    string EtaxRefNumber,
    string XmlContent,
    string? DigitalSignature,
    EtaxStatus Status,
    string? SubmissionId,
    DateTime? SubmittedAt,
    string? AcceptanceNumber,
    DateTime? AcceptedAt,
    string? ErrorMessage,
    string? ErrorCode,
    string? SellerName,
    string? SellerTaxId,
    string? BuyerName,
    string? BuyerTaxId,
    DateTime? SignedAt,
    string? CertificateSerialNumber,
    DateTime CreatedAt);
