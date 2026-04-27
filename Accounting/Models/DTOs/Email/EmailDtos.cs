using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Email;

// ===== Send document via email (regular, no e-Tax) =====
public record SendDocumentEmailRequest(
    string To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Body,
    bool AttachPdf = true,
    bool AttachXml = false);

// ===== Send e-Tax via email (RD spec) =====
public record SendEtaxByEmailRequest(
    string To,
    string? Cc,                       // optional additional CC; system always adds RD timestamp
    string? Bcc,
    string? Subject,
    string? Body,
    bool IncludePdf = true,
    bool IncludeXml = true,
    bool IncludeRdTimestamp = true);  // CC csemail@etax.teda.th

public record DocumentEmailLogResponse(
    Guid Id,
    Guid? DocumentId,
    Guid? EtaxInvoiceId,
    string ToEmail,
    string? CcEmail,
    string Subject,
    bool AttachedPdf,
    bool AttachedXml,
    EmailProvider Provider,
    EmailLogStatus Status,
    DateTime? SentAt,
    string? ErrorMessage,
    bool IsEtaxByEmail,
    bool IncludedRdTimestamp,
    DateTime CreatedAt);

// ===== Email configuration =====
public record EmailConfigResponse(
    EmailProvider Provider,
    string? FromAddress,
    bool Configured,
    DateTime? LastTestedAt,
    string? LastTestStatus,
    SmtpConfigDto? Smtp,
    MicrosoftGraphConfigDto? Microsoft,
    GmailConfigDto? Gmail);

public record SmtpConfigDto(
    string? Host,
    int Port,
    string? Username,
    bool HasPassword,           // never expose actual password
    bool UseSsl);

public record MicrosoftGraphConfigDto(
    string? TenantId,
    string? ClientId,
    bool HasClientSecret,
    string? SenderUpn);

public record GmailConfigDto(
    string? ClientId,
    bool HasClientSecret,
    bool HasRefreshToken);

public record UpdateEmailConfigRequest(
    EmailProvider Provider,
    string? FromAddress,
    SmtpConfigInput? Smtp,
    MicrosoftGraphConfigInput? Microsoft,
    GmailConfigInput? Gmail);

public record SmtpConfigInput(
    string? Host,
    int? Port,
    string? Username,
    string? Password,           // null = keep existing
    bool? UseSsl);

public record MicrosoftGraphConfigInput(
    string? TenantId,
    string? ClientId,
    string? ClientSecret,       // null = keep existing
    string? SenderUpn);

public record GmailConfigInput(
    string? ClientId,
    string? ClientSecret,       // null = keep existing
    string? RefreshToken);      // null = keep existing

public record TestEmailRequest(string ToAddress);

// ===== e-Tax mode configuration =====
public record EtaxConfigResponse(
    EtaxMode Mode,
    bool Enabled,
    bool TestMode,
    bool AutoSign,
    bool AutoSubmit,
    // ByEmail mode
    bool ByEmailRdRegistered,
    DateTime? ByEmailRegistrationDate,
    string? ByEmailRegistrationNumber,
    string? ByEmailSenderEmail,
    string ByEmailRdTimestampAddress,
    bool ByEmailEmbedXml,
    bool ByEmailAutoSendOnApprove,
    // Direct mode
    bool DirectCertificateInstalled,
    bool DirectApiCredentialsSet,
    string? ServiceProvider);

public record UpdateEtaxConfigRequest(
    EtaxMode? Mode,
    bool? Enabled,
    bool? TestMode,
    bool? AutoSign,
    bool? AutoSubmit,
    // ByEmail
    bool? ByEmailRdRegistered,
    DateTime? ByEmailRegistrationDate,
    string? ByEmailRegistrationNumber,
    string? ByEmailSenderEmail,
    string? ByEmailRdTimestampAddress,
    bool? ByEmailEmbedXml,
    bool? ByEmailAutoSendOnApprove,
    // Direct
    string? ServiceProvider);
