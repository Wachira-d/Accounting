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
    public bool ShowContactEmail { get; set; } = true;   // default on — show as complete info as possible
    public string ContactSectionTitle { get; set; } = "ลูกค้า";
    public string? ContactSectionTitleEn { get; set; } = "Customer";

    // ===== Table / Line Items =====
    public bool ShowLineNumber { get; set; } = true;
    public bool ShowItemCode { get; set; } = false;
    public bool ShowUnit { get; set; } = true;
    public bool ShowDiscount { get; set; } = true;

    /// <summary>ซ่อนคอลัมน์ส่วนลดอัตโนมัติเมื่อ <b>ไม่มีบรรทัดไหนมีส่วนลดเลย</b>
    /// (default true) — เอกสารส่วนใหญ่ไม่มีส่วนลด คอลัมน์ที่เป็น 0.00 ทั้งแถว
    /// กินความกว้างไปเปล่า ๆ ทำให้ช่องรายการแคบจนคำอธิบายตัดบรรทัด
    ///
    /// <para>แยกจาก <see cref="ShowDiscount"/> โดยตั้งใจ: ตัวนั้นคือ "ไม่อยากให้มี
    /// คอลัมน์นี้เลย" (ปิดถาวรแม้มีส่วนลดจริง — ซึ่งจะทำให้ยอดบนใบอธิบายไม่ได้
    /// จึงไม่ใช่ค่าปกติ) ส่วนตัวนี้คือ "มีเมื่อจำเป็น". ปิดตัวนี้ = คอลัมน์โผล่
    /// เสมอ (เหมาะกับกิจการที่อยากให้ทุกใบหน้าตาเหมือนกันเป๊ะ)</para></summary>
    public bool HideEmptyDiscountColumn { get; set; } = true;

    /// <summary>เอกสารหลายหน้า → พิมพ์ <b>หัวกระดาษ 3 ส่วน</b> (ข้อมูลบริษัทเรา ·
    /// หัวเอกสาร · ข้อมูลลูกค้า) ซ้ำทุกหน้า (default true) — หน้า 2 ที่มีแต่ตาราง
    /// ลอย ๆ อ่านไม่รู้ว่าเป็นเอกสารอะไรของใคร และถ้าหน้าหลุดจากชุดก็สืบไม่ได้
    ///
    /// <para>ปิดได้เผื่อเทมเพลตที่จัดหน้าเองแล้ว/ต้องการประหยัดพื้นที่หน้า 2
    /// (ปิด = พฤติกรรมเดิมก่อนเวอร์ชันนี้ หัวขึ้นเฉพาะหน้าแรก)</para></summary>
    public bool RepeatHeaderEveryPage { get; set; } = true;

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
    /// <summary>ตำแหน่งป้าย "ต้นฉบับ/สำเนา" บน PDF — "Watermark" = ลายน้ำ
    /// กลางหน้า (ค่าเดิม), "TopRight"/"TopLeft" = ป้ายกรอบเล็กมุมบนของเอกสาร.</summary>
    public string CopyLabelPosition { get; set; } = "Watermark";

    // ===== Font & Style =====
    /// <summary>Overall layout/structure of the document — controls the
    /// arrangement of header, title, doc-info and contact blocks (not just
    /// colours). One of: Classic, ModernLeft, BannerHeader, Compact, Minimal,
    /// CenteredFormal. Drives both the HTML view and the print layout.</summary>
    public string LayoutStyle { get; set; } = "Classic";
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

    // ===== ค่าเริ่มต้นของ "ฟอร์มสร้างเอกสาร" ต่อชนิด =====
    // template เป็น per-DocumentType + IsDefault อยู่แล้ว จึงเป็นบ้านธรรมชาติ
    // ของ default ฝั่งฟอร์มด้วย — ไม่ต้องมี entity ใหม่ให้ผู้ใช้ตั้งค่า 2 ที่.
    // ฟอร์มดึง template default ของชนิดนั้นมา pre-fill เมื่อช่องยังว่าง
    // (ผู้ใช้พิมพ์ทับได้เสมอ — เป็นค่าตั้งต้น ไม่ใช่ค่าบังคับ)

    /// <summary>ผังบัญชีเงินรับ/จ่าย (แหล่งเงิน) ที่เลือกให้อัตโนมัติ เช่น
    /// ใบเสนอราคา/ใบแจ้งหนี้ตั้งบัญชีธนาคารรับเงินประจำ — โยงกับกล่อง
    /// ShowBankDetails/BankDetailsText ฝั่ง PDF ให้เล่าเรื่องเดียวกัน</summary>
    public Guid? DefaultPaymentAccountId { get; set; }

    /// <summary>เงื่อนไขการชำระเงิน default เช่น "ชำระภายใน 30 วัน" —
    /// ขึ้นทั้งบนฟอร์ม (ช่อง PaymentTerms) และบน PDF</summary>
    public string? DefaultPaymentTerms { get; set; }

    /// <summary>จำนวนวันเครดิต default — ฟอร์มคำนวณวันครบกำหนดให้ทันที</summary>
    public int? DefaultCreditDays { get; set; }
}
