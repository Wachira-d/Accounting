using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Log การส่งอีเมลเอกสาร (รวม e-Tax by Email และอีเมลส่งเอกสารทั่วไป)
/// </summary>
public class DocumentEmailLog : TenantEntity
{
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }

    public Guid? EtaxInvoiceId { get; set; }
    public EtaxInvoice? EtaxInvoice { get; set; }

    public string ToEmail { get; set; } = "";
    public string? CcEmail { get; set; }
    public string? BccEmail { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    public bool AttachedPdf { get; set; }
    public bool AttachedXml { get; set; }
    public string? PdfFilePath { get; set; }
    public string? XmlFilePath { get; set; }

    public EmailProvider Provider { get; set; } = EmailProvider.Smtp;
    public EmailLogStatus Status { get; set; } = EmailLogStatus.Pending;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ProviderMessageId { get; set; }   // returned by provider for tracking

    public bool IsEtaxByEmail { get; set; }          // true if part of eTax by email flow
    public bool IncludedRdTimestamp { get; set; }    // true if csemail@etax.teda.th was CC'd
}
