using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class Payment : TenantEntity
{
    public string PaymentNumber { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? Reference { get; set; }
    public string? BankAccount { get; set; }
    public string? Notes { get; set; }
}
