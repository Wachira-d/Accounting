using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>Read-only payment surface ที่ครอบ Payment/SiteOrderPayment/
/// PosPayment/SubscriptionPayment ให้ขอ aggregate cross-domain ได้โดยไม่ต้อง
/// migrate schema. duplicate audit #1 phase 1: unified read-side.
///
/// **Phase 2** (อนาคต) — รวม persistence layer + DI: ภายในระบบส่วน new code
/// ใหม่ implement IPaymentRecord และ services ที่ต้องการ aggregate
/// (CashFlowForecast, ExecutiveReport, etc.) consume interface แทน entity
/// specific. Old code path คงเดิมไป.</summary>
public interface IPaymentRecord
{
    Guid Id { get; }
    Guid CompanyId { get; }
    DateTime PaymentDate { get; }
    decimal Amount { get; }
    PaymentMethod PaymentMethod { get; }
    string? Reference { get; }

    /// <summary>Discriminator: "AR" (customer paid us) / "AP" (we paid vendor)
    /// / "SUB" (subscription billing) / "POS" (POS cash sale)</summary>
    string Domain { get; }

    /// <summary>Currency code (3-letter ISO 4217). Default THB.</summary>
    string Currency { get; }
}
