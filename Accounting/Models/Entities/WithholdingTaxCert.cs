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
