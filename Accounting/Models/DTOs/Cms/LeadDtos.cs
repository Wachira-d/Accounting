using Accounting.Models.Entities;

namespace Accounting.Models.DTOs.Cms;

/// <summary>Public-storefront submission. <see cref="LeadType"/>
/// chosen per form (RFQ form → Rfq, viewing form → Viewing, …).
/// Storefront ContactForm block defaults to Contact.</summary>
public record CreateLeadRequest(
    LeadType LeadType,
    string? SourceSlug,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? CustomerCompany,
    string? CustomerTaxId,
    string? Message,
    /// <summary>Form-specific extras: budget, sqm, weight, course
    /// id, property id, etc. Stored verbatim as JSON on the lead row.</summary>
    Dictionary<string, object?>? Fields = null,
    /// <summary>captcha token if captchaProvider is configured for
    /// the site (hcaptcha or recaptcha) — verified server-side.</summary>
    string? CaptchaToken = null,
    // รอบ 209 P6 (ฑ): ติ๊กยินยอม PDPA บนฟอร์มติดต่อ — บังคับเมื่อเว็บตั้ง RequireDataConsent (ตรวจที่เซิร์ฟเวอร์)
    bool ConsentGiven = false);

public record LeadListResponse(
    Guid Id,
    string LeadNumber,
    LeadType LeadType,
    LeadStatus Status,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? SourceSlug,
    DateTime CreatedAt,
    // R-45 มุมมองทุกไซต์ — เว็บเจ้าของแถว + ชื่อ (null = หน้ารายเว็บ) · ต่อท้ายเพื่อให้ผู้เรียกเดิมคอมไพล์ต่อได้
    Guid? SiteId = null,
    string? SiteName = null);

public record LeadDetailResponse(
    Guid Id,
    string LeadNumber,
    LeadType LeadType,
    LeadStatus Status,
    string? SourceSlug,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? CustomerCompany,
    string? CustomerTaxId,
    string? Message,
    Dictionary<string, object?>? Fields,
    Guid? AssignedToUserId,
    string? AssignedToName,
    DateTime? QualifiedAt,
    DateTime? QuotedAt,
    DateTime? WonAt,
    DateTime? LostAt,
    string? LostReason,
    string? InternalNotes,
    Guid? ErpDocumentId,
    Guid? ContactId,
    DateTime CreatedAt);

public record UpdateLeadStatusRequest(
    LeadStatus Status,
    string? InternalNotes = null,
    string? LostReason = null,
    Guid? AssignedToUserId = null,
    string? AssignedToName = null);

public record ConvertLeadToQuotationRequest(
    /// <summary>Optional override of the customer name when creating
    /// the Contact row. Defaults to lead.CustomerName.</summary>
    string? ContactName = null,
    decimal? EstimatedAmount = null,
    string? QuotationNotes = null);
