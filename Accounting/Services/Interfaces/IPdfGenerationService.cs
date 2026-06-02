using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.DTOs.Etax;

namespace Accounting.Services.Interfaces;

public interface IPdfGenerationService
{
    Task<GeneratePdfResponse> GenerateDocumentPdfAsync(Guid companyId, GeneratePdfRequest request);
    Task<GeneratePdfResponse> GenerateWithholdingTaxCertPdfAsync(Guid companyId, Guid certId);
    Task<GeneratePdfResponse> GenerateReceiptPdfAsync(Guid companyId, Guid paymentId);
    Task<byte[]> GeneratePreviewPdfAsync(Guid companyId, PdfPreviewRequest request);

    /// <summary>Template-preview HTML (sample data) for a template or a
    /// document type — powers the templates gallery thumbnails.</summary>
    Task<string> GeneratePreviewHtmlAsync(Guid companyId, Guid? templateId, string? documentType, string? language);

    /// <summary>Build the SAME HTML the PDF generator uses — exposed
    /// so the browser print preview can render an identical layout
    /// (no more "downloaded PDF and ctrl-P print look different"
    /// drift). The browser then runs window.print() on this HTML so
    /// the output matches the PDF exactly minus driver-specific
    /// rendering differences.</summary>
    Task<string> GenerateDocumentHtmlAsync(Guid companyId, GeneratePdfRequest request);

    /// <summary>Convert raw HTML string to PDF bytes (for use by other services)</summary>
    byte[] ConvertHtmlToPdfBytes(string html);

    /// <summary>
    /// Build a PDF/A-3 (conformance level U) document with the eTax XML embedded as an Associated File.
    /// Required for Thai e-Tax Invoice by Email compliance per ETDA Recommendation 3-2560 v2.0.
    /// </summary>
    byte[] BuildEtaxPdfA3WithEmbeddedXml(string xmlContent, EtaxPdfMetadata metadata);
}
