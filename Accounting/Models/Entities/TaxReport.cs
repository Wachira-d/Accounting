using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รายงานภาษี (ภพ.30, ภงด.3, ภงด.53)
/// </summary>
public class TaxReport : TenantEntity
{
    public TaxType TaxType { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public TaxReportStatus Status { get; set; } = TaxReportStatus.Draft;
    public DateTime? FiledDate { get; set; }

    // VAT (ภพ.30)
    public decimal OutputVat { get; set; }       // ภาษีขาย
    public decimal InputVat { get; set; }        // ภาษีซื้อ
    public decimal NetVat { get; set; }          // ภาษีที่ต้องชำระ/ขอคืน

    // WHT (ภงด.3/53)
    public decimal TotalIncome { get; set; }
    public decimal TotalTaxWithheld { get; set; }

    public string? Notes { get; set; }

    public ICollection<TaxReportLine> Lines { get; set; } = new List<TaxReportLine>();
}

public class TaxReportLine : BaseEntity
{
    public Guid TaxReportId { get; set; }
    public TaxReport TaxReport { get; set; } = null!;

    public int LineOrder { get; set; }
    public string? TaxPayerId { get; set; }      // เลขผู้เสียภาษี
    public string? TaxPayerName { get; set; }
    public DateTime TransactionDate { get; set; }
    public string? Description { get; set; }
    public decimal IncomeAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public string? IncomeTypeCode { get; set; }  // รหัสประเภทเงินได้
    public Guid? DocumentId { get; set; }
}
