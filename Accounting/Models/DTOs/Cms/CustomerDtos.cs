using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Site Customer ====================

public class CreateSiteCustomerRequest
{
    [Required, MaxLength(256), EmailAddress]
    public string Email { get; set; } = "";
    [MaxLength(256)]
    public string? FullName { get; set; }
    [MaxLength(20)]
    public string? Phone { get; set; }
    public string? Password { get; set; }
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? CompanyName { get; set; }
    public string PreferredLanguage { get; set; } = "th";
    public string PreferredCurrency { get; set; } = "THB";
    public bool AcceptMarketing { get; set; } = false;
    public string? Tags { get; set; }
    public string? CustomerGroup { get; set; }
}

public class UpdateSiteCustomerRequest
{
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? CompanyName { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? PreferredCurrency { get; set; }
    public bool? AcceptMarketing { get; set; }
    public bool? IsActive { get; set; }
    public string? Tags { get; set; }
    public string? CustomerGroup { get; set; }
}

public class SiteCustomerResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? CompanyName { get; set; }
    public Guid? ContactId { get; set; }
    public string? ContactName { get; set; }
    public string PreferredLanguage { get; set; } = "th";
    public string PreferredCurrency { get; set; } = "THB";
    public bool AcceptMarketing { get; set; }
    public bool ConsentGiven { get; set; }
    public DateTime? ConsentGivenAt { get; set; }
    public bool IsActive { get; set; }
    public bool EmailVerified { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? Tags { get; set; }
    public string? CustomerGroup { get; set; }
    public int OrderCount { get; set; }
    public int BookingCount { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SiteCustomerListResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; }
    public string? CustomerGroup { get; set; }
    public int OrderCount { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Customer Login (Storefront Portal) ====================

public class CustomerLoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
    [Required]
    public string Password { get; set; } = "";
}

public class CustomerLoginResponse
{
    public string Token { get; set; } = "";
    public string? FullName { get; set; }
    public string Email { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public class CustomerRegisterRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
    [Required, MinLength(8)]
    public string Password { get; set; } = "";
    [MaxLength(256)]
    public string? FullName { get; set; }
    [MaxLength(20)]
    public string? Phone { get; set; }
    public bool AcceptMarketing { get; set; } = false;
    public bool ConsentGiven { get; set; } = false;
}

// ==================== Customer Address ====================

public class CreateCustomerAddressRequest
{
    public string? Label { get; set; }
    public string? RecipientName { get; set; }
    public string? Phone { get; set; }
    public string? BuildingNumber { get; set; }
    public string? BuildingName { get; set; }
    public string? StreetName { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; } = "TH";
    public string? AddressLine { get; set; }
    public bool IsDefault { get; set; } = false;
    public SiteAddressType AddressType { get; set; } = SiteAddressType.Shipping;
}

public class CustomerAddressResponse
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public string? RecipientName { get; set; }
    public string? Phone { get; set; }
    public string? BuildingNumber { get; set; }
    public string? BuildingName { get; set; }
    public string? StreetName { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public string? AddressLine { get; set; }
    public bool IsDefault { get; set; }
    public SiteAddressType AddressType { get; set; }
}

// ==================== PDPA / Right to be Forgotten ====================

public class DataDeletionRequest
{
    [Required]
    public Guid CustomerId { get; set; }
    public string? Reason { get; set; }
}

public class ConsentUpdateRequest
{
    public bool ConsentGiven { get; set; }
    public bool AcceptMarketing { get; set; }
}

// ==================== Form ====================

public class CreateFormRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public SiteFormType FormType { get; set; } = SiteFormType.Contact;
    public string? NotifyEmails { get; set; }
    public bool SendAutoReply { get; set; } = false;
    public string? AutoReplySubject { get; set; }
    public string? AutoReplyBody { get; set; }
    public string? SuccessMessage { get; set; }
    public string? RedirectUrl { get; set; }
    public bool RequireCaptcha { get; set; } = true;
    public int? RateLimitPerHour { get; set; }
    public bool CreateErpDocument { get; set; } = false;
    public DocumentType? ErpDocumentType { get; set; }
    public List<CreateFormFieldRequest>? Fields { get; set; }
}

public class FormResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public SiteFormType FormType { get; set; }
    public bool SendAutoReply { get; set; }
    public bool RequireCaptcha { get; set; }
    public bool CreateErpDocument { get; set; }
    public DocumentType? ErpDocumentType { get; set; }
    public bool IsActive { get; set; }
    public int SubmissionCount { get; set; }
    public List<FormFieldResponse>? Fields { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateFormFieldRequest
{
    [Required, MaxLength(128)]
    public string FieldName { get; set; } = "";
    public string? Label { get; set; }
    public string? LabelEn { get; set; }
    public string? Placeholder { get; set; }
    public FormFieldType FieldType { get; set; } = FormFieldType.Text;
    public bool IsRequired { get; set; } = false;
    public string? ValidationPattern { get; set; }
    public string? ValidationMessage { get; set; }
    public string? OptionsJson { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
    public string Width { get; set; } = "full";
}

public class FormFieldResponse
{
    public Guid Id { get; set; }
    public string FieldName { get; set; } = "";
    public string? Label { get; set; }
    public string? LabelEn { get; set; }
    public string? Placeholder { get; set; }
    public FormFieldType FieldType { get; set; }
    public bool IsRequired { get; set; }
    public string? ValidationPattern { get; set; }
    public string? OptionsJson { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
    public string Width { get; set; } = "full";
}

// ==================== Form Submission ====================

public class SubmitFormRequest
{
    [Required]
    public string DataJson { get; set; } = "{}";
    public string? CaptchaToken { get; set; }
}

public class FormSubmissionResponse
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public string FormName { get; set; } = "";
    public string DataJson { get; set; } = "{}";
    public FormSubmissionStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? ErpDocumentId { get; set; }
    public string? Notes { get; set; }
    public string? RepliedBy { get; set; }
    public DateTime? RepliedAt { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Customer Merge ====================

public class MergeCustomersRequest
{
    [Required]
    public Guid PrimaryCustomerId { get; set; }
    [Required]
    public Guid MergedCustomerId { get; set; }
    public string MergeReason { get; set; } = "manual";
}
