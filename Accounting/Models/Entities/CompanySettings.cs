using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// การตั้งค่าบริษัท (Logo, Templates, Defaults)
/// เทียบเท่า FlowAccount & PEAK: Company settings
/// </summary>
public class CompanySettings : TenantEntity
{
    // Branding
    public string? LogoPath { get; set; }
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }

    // Document Defaults
    public string? DefaultPaymentTerms { get; set; }     // e.g. "Net 30"
    public int DefaultPaymentDueDays { get; set; } = 30;
    public string? InvoiceNotes { get; set; }
    public string? ReceiptNotes { get; set; }
    public string? QuotationNotes { get; set; }
    public string? InvoiceFooter { get; set; }
    public string? ReceiptFooter { get; set; }

    // Tax Settings
    public decimal DefaultVatRate { get; set; } = 7;
    public bool VatRegistered { get; set; } = true;
    public string? VatRegistrationDate { get; set; }

    // Email Templates
    public string? EmailFromName { get; set; }
    public string? EmailReplyTo { get; set; }
    public string? InvoiceEmailSubject { get; set; }
    public string? InvoiceEmailBody { get; set; }

    // Security
    public bool RequireApprovalForDocuments { get; set; } = false;
    public decimal? ApprovalThresholdAmount { get; set; }
    public bool AllowFreelanceAccess { get; set; } = false;
    public int MaxFreelanceUsers { get; set; } = 3;
    public bool RequireTwoFactorForFreelance { get; set; } = false;
    public bool EnableApiAccess { get; set; } = false;
    public int MaxApiKeys { get; set; } = 5;

    // Closing Settings
    public bool AutoCloseMonthEnd { get; set; } = false;
    public int MonthEndClosingDay { get; set; } = 15;    // วันสุดท้ายที่บันทึกเดือนก่อนได้
    public bool PreventPostToClosedPeriod { get; set; } = true;

    // e-Tax Invoice Settings (per-company)
    public bool EtaxEnabled { get; set; } = false;
    public EtaxMode EtaxMode { get; set; } = EtaxMode.None;  // ByEmail, Direct, or Both
    public string? EtaxCertificatePath { get; set; }        // path to .p12/.pfx certificate (Direct mode)
    public string? EtaxCertificatePassword { get; set; }    // encrypted certificate password
    public string? EtaxRdApiKey { get; set; }               // Revenue Department API Key (Direct mode)
    public string? EtaxRdApiSecret { get; set; }            // Revenue Department API Secret
    public bool EtaxTestMode { get; set; } = true;          // true = ทดสอบ, false = production
    public bool EtaxAutoSign { get; set; } = false;         // ลงนามอัตโนมัติเมื่อสร้าง
    public bool EtaxAutoSubmit { get; set; } = false;       // ส่งสรรพากรอัตโนมัติหลังลงนาม
    public string? EtaxServiceProvider { get; set; }        // "RD" or third-party provider
    public string? EtaxXmlOutputPath { get; set; }          // custom XML output path

    // e-Tax by Email Settings (Mode = ByEmail) — ต้องสมัครกับสรรพากรก่อน (รสภ.01-1)
    public bool EtaxByEmailRdRegistered { get; set; } = false;        // ลงทะเบียนกับสรรพากรแล้วหรือยัง
    public DateTime? EtaxByEmailRegistrationDate { get; set; }        // วันที่ได้รับอนุมัติ
    public string? EtaxByEmailRegistrationNumber { get; set; }        // เลขรหัสรับรอง (ถ้ามี)
    public string? EtaxByEmailSenderEmail { get; set; }               // อีเมลผู้ส่งที่ลงทะเบียนกับสรรพากร
    public string EtaxByEmailRdTimestampAddress { get; set; } = "csemail@etax.teda.th"; // CC ไป RD timestamp
    public bool EtaxByEmailEmbedXml { get; set; } = true;             // ฝัง XML ใน PDF/A-3 (สรรพากรกำหนด)
    public bool EtaxByEmailAutoSendOnApprove { get; set; } = false;   // ส่งอัตโนมัติเมื่อ approve เอกสาร

    // ===== Email Sending Configuration (per-company) =====
    public EmailProvider EmailProvider { get; set; } = EmailProvider.Smtp;
    public string? EmailFromAddress { get; set; }
    public bool EmailConfigured { get; set; } = false;                // ตั้งค่าและ test ผ่านแล้ว
    public DateTime? EmailLastTestedAt { get; set; }
    public string? EmailLastTestStatus { get; set; }                  // "OK" or error message

    // SMTP-specific (works with Gmail App Password, Office365, etc.)
    public string? EmailSmtpHost { get; set; }
    public int EmailSmtpPort { get; set; } = 587;
    public string? EmailSmtpUsername { get; set; }
    public string? EmailSmtpPassword { get; set; }                    // encrypted at rest (TODO)
    public bool EmailSmtpUseSsl { get; set; } = true;

    // Microsoft Graph API (OAuth2 client credentials, app-only)
    public string? EmailMsTenantId { get; set; }
    public string? EmailMsClientId { get; set; }
    public string? EmailMsClientSecret { get; set; }                  // encrypted at rest (TODO)
    public string? EmailMsSenderUpn { get; set; }                     // mailbox to send from (UserPrincipalName)

    // Gmail API (OAuth2 with refresh token, or service account)
    public string? EmailGmailClientId { get; set; }
    public string? EmailGmailClientSecret { get; set; }
    public string? EmailGmailRefreshToken { get; set; }
    public string? EmailGmailServiceAccountJson { get; set; }         // alt: service account credentials JSON

    // Landing Page – Accounting Services
    public string? LandingContactPhone { get; set; }           // เบอร์ติดต่อแสดงหน้าแรก
    public string? LandingContactLine { get; set; }            // LINE ID
    public string? LandingContactEmail { get; set; }           // อีเมลติดต่อ
    public string? LandingServicesJson { get; set; }           // JSON array of accounting service packages
}
