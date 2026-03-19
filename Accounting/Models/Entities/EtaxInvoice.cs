using Accounting.Models.DTOs.DocumentTemplate;

namespace Accounting.Models.Entities;

/// <summary>
/// e-Tax Invoice record - เก็บ XML และสถานะการส่ง
/// ตาม มาตรฐาน กรมสรรพากร e-Tax Invoice & e-Receipt
/// </summary>
public class EtaxInvoice : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public string EtaxRefNumber { get; set; } = "";           // เลขอ้างอิง e-Tax
    public string XmlContent { get; set; } = "";              // XML ตามมาตรฐาน
    public string? XmlFilePath { get; set; }                  // path เก็บไฟล์ XML
    public string? PdfFilePath { get; set; }                  // path เก็บ PDF ที่สร้างจาก XML

    public string? DigitalSignature { get; set; }             // ลายเซ็นดิจิทัล
    public string? CertificateSerialNumber { get; set; }      // เลข Serial ใบรับรอง
    public DateTime? SignedAt { get; set; }

    public EtaxStatus Status { get; set; } = EtaxStatus.Generated;

    // สถานะการส่งกรมสรรพากร
    public string? SubmissionId { get; set; }                 // รหัสการส่ง
    public DateTime? SubmittedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptanceNumber { get; set; }             // เลขที่ตอบรับ
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }

    // ข้อมูลผู้ออก/ผู้รับ (snapshot ณ เวลาสร้าง)
    public string SellerName { get; set; } = "";
    public string SellerTaxId { get; set; } = "";
    public string? SellerBranch { get; set; }
    public string? SellerAddress { get; set; }
    public string BuyerName { get; set; } = "";
    public string? BuyerTaxId { get; set; }
    public string? BuyerBranch { get; set; }
    public string? BuyerAddress { get; set; }
}
