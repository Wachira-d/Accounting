using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IEtaxInvoiceService
{
    Task<EtaxInvoiceResponse> GenerateAsync(Guid companyId, GenerateEtaxRequest request);
    Task<EtaxInvoiceResponse> GetByIdAsync(Guid companyId, Guid etaxId);
    Task<EtaxInvoiceResponse> GetByDocumentIdAsync(Guid companyId, Guid documentId);
    Task<PagedResponse<EtaxInvoiceResponse>> GetAllAsync(Guid companyId, EtaxStatus? status, PagedRequest request);
    Task<EtaxInvoiceResponse> SignAsync(Guid companyId, Guid etaxId);
    Task<EtaxInvoiceResponse> SubmitToRevenueAsync(Guid companyId, Guid etaxId);
    Task<string> GetXmlAsync(Guid companyId, Guid etaxId);
    /// <summary>ยกเลิกแถว e-Tax <b>ในระบบนี้</b> (ไม่ได้ส่งคำยกเลิกถึงกรมสรรพากร) — ตัวตัดสิน <c>Helpers/EtaxVoidPolicy</c> · แถว Submitted ต้องมีไฟล์หลักฐาน
    /// การยกเลิกที่แนบเข้าเอกสารของแถวนี้ (รอบ 200 ทีม V1H · คำตัดสินข้อ 51 — controller เดินด่านไฟล์แนบตัวเดียวก่อนถึงที่นี่)</summary>
    Task VoidAsync(Guid companyId, Guid etaxId, EtaxVoidRequest? request, string actor);

    /// <summary>
    /// Build PDF/A-3 with embedded ETDA XML for e-Tax by Email compliance.
    /// Persists the PDF and XML files to disk and updates the EtaxInvoice record.
    /// </summary>
    Task<(byte[] pdfBytes, string fileName)> GeneratePdfA3Async(Guid companyId, Guid etaxId);
}
