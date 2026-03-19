using Accounting.Models.DTOs.DocumentTemplate;

namespace Accounting.Services.Interfaces;

public interface IPdfGenerationService
{
    Task<GeneratePdfResponse> GenerateDocumentPdfAsync(Guid companyId, GeneratePdfRequest request);
    Task<GeneratePdfResponse> GenerateWithholdingTaxCertPdfAsync(Guid companyId, Guid certId);
    Task<GeneratePdfResponse> GenerateReceiptPdfAsync(Guid companyId, Guid paymentId);
    Task<byte[]> GeneratePreviewPdfAsync(Guid companyId, PdfPreviewRequest request);
}
