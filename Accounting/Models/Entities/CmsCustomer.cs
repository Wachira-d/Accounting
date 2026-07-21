using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Site Customer (Portal User) =====

public class SiteCustomer : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    // Identity
    public string Email { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string? FullName { get; set; }
    public string? Phone { get; set; }

    // SSO
    public string? AuthProvider { get; set; }
    public string? AuthProviderId { get; set; }

    // Unified CRM: link to ERP Contact for accounting/tax integration
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    // Business info
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? CompanyName { get; set; }

    // Preferences
    public string PreferredLanguage { get; set; } = "th";
    public string PreferredCurrency { get; set; } = "THB";
    public bool AcceptMarketing { get; set; } = false;

    // PDPA consent
    public bool ConsentGiven { get; set; } = false;
    public DateTime? ConsentGivenAt { get; set; }
    public DateTime? ConsentWithdrawnAt { get; set; }
    public bool RightToBeforgotten { get; set; } = false;
    public DateTime? DataDeletionRequestedAt { get; set; }

    // Account status
    public bool IsActive { get; set; } = true;
    public bool EmailVerified { get; set; } = false;
    public string? EmailVerificationToken { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }

    // Token
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }

    // Tags for segmentation
    public string? Tags { get; set; }
    public string? CustomerGroup { get; set; }

    public ICollection<SiteCustomerAddress> Addresses { get; set; } = new List<SiteCustomerAddress>();
    public ICollection<SiteOrder> Orders { get; set; } = new List<SiteOrder>();
    public ICollection<SiteBooking> Bookings { get; set; } = new List<SiteBooking>();
    public ICollection<SiteCart> Carts { get; set; } = new List<SiteCart>();
    public ICollection<SiteWishlistItem> WishlistItems { get; set; } = new List<SiteWishlistItem>();
}

// ===== Customer Addresses =====

public class SiteCustomerAddress : TenantEntity
{
    public Guid CustomerId { get; set; }
    public SiteCustomer Customer { get; set; } = null!;

    public string? Label { get; set; }
    public string? RecipientName { get; set; }
    public string? Phone { get; set; }

    // Structured (Thai address fields)
    public string? BuildingNumber { get; set; }
    public string? BuildingName { get; set; }
    public string? StreetName { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; } = "TH";

    // Free-text (for international or display)
    public string? AddressLine { get; set; }

    public bool IsDefault { get; set; } = false;
    public SiteAddressType AddressType { get; set; } = SiteAddressType.Shipping;
}

// ===== Wishlist =====

public class SiteWishlistItem : TenantEntity
{
    public Guid CustomerId { get; set; }
    public SiteCustomer Customer { get; set; } = null!;

    public Guid SiteProductId { get; set; }
    public SiteProduct SiteProduct { get; set; } = null!;
}

// ===== Dynamic Forms (Contact, RFQ, etc.) =====

public class SiteForm : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public SiteFormType FormType { get; set; } = SiteFormType.Contact;

    // Email notification
    public string? NotifyEmails { get; set; }
    public bool SendAutoReply { get; set; } = false;
    public string? AutoReplySubject { get; set; }
    public string? AutoReplyBody { get; set; }

    // Success behavior
    public string? SuccessMessage { get; set; }
    public string? RedirectUrl { get; set; }

    // Anti-spam
    public bool RequireCaptcha { get; set; } = true;
    public int? RateLimitPerHour { get; set; }

    // ERP mapping: RFQ → create draft Quotation
    public bool CreateErpDocument { get; set; } = false;
    public DocumentType? ErpDocumentType { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<SiteFormField> Fields { get; set; } = new List<SiteFormField>();
    public ICollection<SiteFormSubmission> Submissions { get; set; } = new List<SiteFormSubmission>();
}

public class SiteFormField : TenantEntity
{
    public Guid FormId { get; set; }
    public SiteForm Form { get; set; } = null!;

    public string FieldName { get; set; } = "";
    public string? Label { get; set; }
    public string? LabelEn { get; set; }
    public string? Placeholder { get; set; }
    public FormFieldType FieldType { get; set; } = FormFieldType.Text;

    public bool IsRequired { get; set; } = false;
    public string? ValidationPattern { get; set; }
    public string? ValidationMessage { get; set; }

    // For Select/Radio/Checkbox
    public string? OptionsJson { get; set; }

    // Default value
    public string? DefaultValue { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // Width (full, half)
    public string Width { get; set; } = "full";
}

public class SiteFormSubmission : TenantEntity
{
    public Guid FormId { get; set; }
    public SiteForm Form { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public string DataJson { get; set; } = "{}";
    public FormSubmissionStatus Status { get; set; } = FormSubmissionStatus.New;

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    // ERP link
    public Guid? ErpDocumentId { get; set; }
    public Document? ErpDocument { get; set; }

    public string? Notes { get; set; }
    public string? RepliedBy { get; set; }
    public DateTime? RepliedAt { get; set; }
}

// ===== Customer Identity Merge Log =====

public class SiteCustomerMerge : TenantEntity
{
    public Guid PrimaryCustomerId { get; set; }
    public SiteCustomer PrimaryCustomer { get; set; } = null!;

    public Guid MergedCustomerId { get; set; }

    public Guid? MergedFromSiteId { get; set; }
    public string MergeReason { get; set; } = "email_match";
    public string? MergedBy { get; set; }
    public string? MergedDataJson { get; set; }
}
