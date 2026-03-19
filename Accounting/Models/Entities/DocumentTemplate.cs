using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// เทมเพลตเอกสาร PDF - แต่ละบริษัทตั้งค่าเองได้
/// รองรับ: ใบเสนอราคา, ใบแจ้งหนี้, ใบเสร็จ, ใบกำกับภาษี, ใบลดหนี้, ใบเพิ่มหนี้, ใบสั่งซื้อ ฯลฯ
/// </summary>
public class DocumentTemplate : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DocumentType DocumentType { get; set; }
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;

    // ===== Page Layout =====
    public string PaperSize { get; set; } = "A4";           // A4, Letter, A5
    public string Orientation { get; set; } = "Portrait";    // Portrait, Landscape
    public decimal MarginTop { get; set; } = 15;             // mm
    public decimal MarginBottom { get; set; } = 15;
    public decimal MarginLeft { get; set; } = 15;
    public decimal MarginRight { get; set; } = 15;

    // ===== Header Section =====
    public bool ShowLogo { get; set; } = true;
    public string LogoPosition { get; set; } = "Left";      // Left, Center, Right
    public decimal LogoWidth { get; set; } = 60;             // mm
    public decimal LogoHeight { get; set; } = 30;            // mm
    public bool ShowCompanyName { get; set; } = true;
    public bool ShowCompanyNameEn { get; set; } = false;
    public bool ShowCompanyAddress { get; set; } = true;
    public bool ShowCompanyTaxId { get; set; } = true;
    public bool ShowCompanyPhone { get; set; } = true;
    public bool ShowCompanyEmail { get; set; } = true;
    public string? HeaderBackgroundColor { get; set; }
    public string? HeaderTextColor { get; set; }

    // ===== Document Title =====
    public string? CustomTitle { get; set; }                 // ถ้า null ใช้ชื่อมาตรฐาน
    public string? CustomTitleEn { get; set; }
    public string TitleFontSize { get; set; } = "18";
    public bool ShowDocumentNumber { get; set; } = true;
    public bool ShowDocumentDate { get; set; } = true;
    public bool ShowDueDate { get; set; } = true;
    public bool ShowReference { get; set; } = true;

    // ===== Customer/Contact Section =====
    public bool ShowContactTaxId { get; set; } = true;
    public bool ShowContactBranch { get; set; } = true;
    public bool ShowContactAddress { get; set; } = true;
    public bool ShowContactPhone { get; set; } = true;
    public bool ShowContactEmail { get; set; } = false;
    public string ContactSectionTitle { get; set; } = "ลูกค้า";
    public string? ContactSectionTitleEn { get; set; } = "Customer";

    // ===== Table / Line Items =====
    public bool ShowLineNumber { get; set; } = true;
    public bool ShowItemCode { get; set; } = false;
    public bool ShowUnit { get; set; } = true;
    public bool ShowDiscount { get; set; } = true;
    public bool ShowVatPerLine { get; set; } = false;
    public bool ShowWithholdingTax { get; set; } = true;
    public string? TableHeaderColor { get; set; } = "#4472C4";
    public string? TableHeaderTextColor { get; set; } = "#FFFFFF";
    public string? TableStripedColor { get; set; }           // null = no stripes
    public string TableBorderStyle { get; set; } = "Full";   // Full, HeaderOnly, None

    // ===== Summary Section =====
    public bool ShowSubTotal { get; set; } = true;
    public bool ShowDiscountTotal { get; set; } = true;
    public bool ShowVatSummary { get; set; } = true;
    public bool ShowWithholdingTaxSummary { get; set; } = true;
    public bool ShowAmountInWords { get; set; } = true;      // จำนวนเงินเป็นตัวอักษร
    public string AmountInWordsLanguage { get; set; } = "th"; // th, en

    // ===== Footer Section =====
    public string? FooterNotes { get; set; }
    public string? FooterNotesEn { get; set; }
    public bool ShowPaymentTerms { get; set; } = true;
    public bool ShowBankDetails { get; set; } = true;
    public string? BankDetailsText { get; set; }             // ข้อมูลบัญชีธนาคาร
    public string? BankDetailsTextEn { get; set; }

    // ===== Signature Section =====
    public bool ShowSignature { get; set; } = true;
    public int SignatureCount { get; set; } = 2;             // จำนวนช่องลงนาม
    public string? SignatureLabel1 { get; set; } = "ผู้รับ";
    public string? SignatureLabel2 { get; set; } = "ผู้จ่าย";
    public string? SignatureLabel3 { get; set; }
    public string? SignatureLabel1En { get; set; } = "Receiver";
    public string? SignatureLabel2En { get; set; } = "Payer";
    public string? SignatureLabel3En { get; set; }
    public bool ShowCompanyStamp { get; set; } = true;
    public string? StampImagePath { get; set; }

    // ===== Watermark =====
    public bool ShowWatermark { get; set; } = false;
    public string? WatermarkText { get; set; }               // e.g. "สำเนา", "COPY", "DRAFT"
    public decimal WatermarkOpacity { get; set; } = 0.15m;

    // ===== Font & Style =====
    public string FontFamily { get; set; } = "THSarabunNew"; // THSarabunNew, Prompt, NotoSansThai
    public string BodyFontSize { get; set; } = "14";
    public string PrimaryColor { get; set; } = "#333333";
    public string AccentColor { get; set; } = "#4472C4";

    // ===== Language =====
    public string Language { get; set; } = "th";             // th, en, th-en (bilingual)
    public bool ShowBilingual { get; set; } = false;

    // ===== e-Tax Invoice Settings =====
    public bool IsEtaxTemplate { get; set; } = false;
    public string? EtaxServiceProvider { get; set; }         // ผู้ให้บริการ e-Tax
    public string? DigitalCertificatePath { get; set; }
    public string? DigitalCertificatePassword { get; set; }
    public bool AutoGenerateEtaxXml { get; set; } = false;

    // ===== QR Code =====
    public bool ShowQrCode { get; set; } = false;
    public string QrCodeType { get; set; } = "PromptPay";    // PromptPay, EtaxRef, Custom
    public string? PromptPayId { get; set; }                 // เลขบัญชี PromptPay
    public string? QrCodeCustomData { get; set; }

    // ===== Copy Settings =====
    public int DefaultCopies { get; set; } = 1;
    public string? CopyLabels { get; set; }                  // JSON: ["ต้นฉบับ","สำเนา 1","สำเนา 2"]
}
