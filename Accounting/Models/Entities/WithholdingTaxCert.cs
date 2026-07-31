using Accounting.Models.Enums;
using Accounting.Models.DTOs.Tax;

namespace Accounting.Models.Entities;

public class WithholdingTaxCert : TenantEntity
{
    public string CertificateNumber { get; set; } = "";

    public Guid PayeeContactId { get; set; }
    public Contact PayeeContact { get; set; } = null!;

    public TaxType TaxFormType { get; set; }
    public int TaxYear { get; set; }
    public int TaxMonth { get; set; }
    public WithholdingTaxCertType CertificateType { get; set; }
    public WithholdingTaxCertStatus Status { get; set; } = WithholdingTaxCertStatus.Draft;

    public decimal TotalIncomeAmount { get; set; }
    public decimal TotalTaxAmount { get; set; }

    public DateTime? IssuedDate { get; set; }

    // Link to source document (for auto-generated certs)
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }

    /// <summary>Link to source PayrollRun สำหรับ monthly ภ.ง.ด.1 ที่ออก
    /// อัตโนมัติเมื่อ post payroll. ใช้ใน:
    ///   • Idempotency check — re-post payroll → void cert เก่า + ออกใหม่
    ///   • Trace gone-back: ภ.ง.ด.1 ของเดือนนี้มาจาก run ไหน
    ///   • Annual summary aggregation (group by SourcePayrollRunId.Year)</summary>
    public Guid? SourcePayrollRunId { get; set; }

    /// <summary>งวดการจ่ายเงินที่ทำให้เกิดใบนี้ (ภ.ง.ด.3/53 = cash basis) —
    /// เอกสารที่ทยอยจ่ายหลายงวดจะมี 50 ทวิ หลายใบ ใบละงวด ยอดตามที่หักจริง
    /// ของงวดนั้น. null = ใบที่ออกจากเอกสารทั้งใบ (จ่ายครั้งเดียว/legacy).
    /// ใช้เป็น idempotency key แทน DocumentId เมื่อจ่ายเป็นงวด.</summary>
    public Guid? SourcePaymentId { get; set; }

    public ICollection<WithholdingTaxCertLine> Lines { get; set; } = new List<WithholdingTaxCertLine>();
}

public class WithholdingTaxCertLine : BaseEntity
{
    public Guid WithholdingTaxCertId { get; set; }
    public WithholdingTaxCert WithholdingTaxCert { get; set; } = null!;

    public int LineOrder { get; set; }
    public string IncomeTypeCode { get; set; } = "";
    public string IncomeDescription { get; set; } = "";
    public DateTime PaymentDate { get; set; }
    public decimal IncomeAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public string? Condition { get; set; }
}
