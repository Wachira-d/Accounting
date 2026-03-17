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
}
