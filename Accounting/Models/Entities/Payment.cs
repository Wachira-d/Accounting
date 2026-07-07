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

    // Proper FK to bank account (replaces text BankAccount field).
    // When OverrideBankAccountId is set, the GL hit + bank-balance update
    // follow this value; BankAccountId mirrors it for convenience. When
    // OverrideBankAccountId is null, BankAccountId inherits from doc.
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccountEntity { get; set; }

    /// <summary>Per-payment override of the source document's BankAccountId.
    /// Lets operators record "the cheque actually cleared at Kasikorn even
    /// though the invoice was targeted at Bangkok Bank". Null = use doc's
    /// channel as-is. Audit-visible so we can trace overrides explicitly.</summary>
    public Guid? OverrideBankAccountId { get; set; }

    /// <summary>Per-payment override of the CASH/BANK GL account — lets the
    /// operator fund a payment from a chart-of-accounts line that isn't a bank
    /// account, e.g. เงินทดรองกรรมการ (director advance/loan), เงินสดย่อย
    /// (petty cash), or a clearing account. When set, the auto-posted JE's
    /// cash side hits THIS account instead of the default cash (111) / bank's
    /// linked GL. Null = use the normal resolution (bank's linked GL → 111).</summary>
    public Guid? OverridePaymentAccountId { get; set; }

    /// <summary>Per-payment override of the "ผู้จ่ายเงิน" signature image
    /// rendered on the PV PDF. Format: data-url ("data:image/png;base64,...")
    /// or bare base64. When set, takes priority over the CreatedBy User's
    /// stored SignatureImageBase64 — useful for API integrations where the
    /// caller signs on behalf of a service account that has no signature on
    /// file. Null = fall back to CreatedBy user's signature → empty space.
    /// Image is rendered at the bottom-left signature slot of the
    /// PaymentVoucher template.</summary>
    public string? PayerSignatureBase64 { get; set; }
    /// <summary>Optional display name for the payer slot — printed under the
    /// signature image. Falls back to CreatedBy user's FullName when null.</summary>
    public string? PayerSignatureName { get; set; }

    /// <summary>WHT withheld by the customer (revenue side) / by us (purchase
    /// side) on THIS installment. Per Thai practice + ประมวลรัษฎากร §50, when
    /// the source invoice is paid in installments the customer withholds
    /// proportionally per installment. Default at CreatePaymentAsync time is
    /// proportional (Amount / Document.TotalAmount × Document.Withholding
    /// TaxAmount); operator can override on the modal. Sum of WHT across
    /// all payments of a document must not exceed Document.WithholdingTax
    /// Amount — enforced inline. Drives the per-receipt Dr WHT-Asset / Cr
    /// WHT-Payable line on the cash-basis GL.</summary>
    public decimal WithholdingTaxAmount { get; set; }

    /// <summary>Project allocation — when set, overrides the parent
    /// document's project for THIS payment. Useful when one document
    /// is split across multiple project payments (advance on Project A
    /// followed by final on Project B). Null = inherit from
    /// Document.ProjectId.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>อัตราแลกเปลี่ยน ณ วันชำระจริง (settlement-day rate) — เฉพาะ
    /// เอกสารสกุลต่างประเทศ. เมื่อต่างจาก rate ของเอกสาร (วันแจ้งหนี้) ระบบ
    /// post กำไร/ขาดทุนจากอัตราแลกเปลี่ยนที่เกิดขึ้นจริง (realized FX G/L)
    /// อัตโนมัติ: เงินสดเข้า-ออกที่ rate นี้, AR/AP ตัดที่ rate เอกสาร,
    /// ผลต่าง → 42600 (กำไร) / 54950 (ขาดทุน). Null = ใช้ rate เอกสาร
    /// (พฤติกรรมเดิม — ไม่มี FX diff). เก็บไว้บน row เพื่อให้การ void
    /// กลับยอดธนาคารด้วย rate เดียวกับตอนบันทึก.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>ค่าธรรมเนียมที่ถูกหักจากยอดโอน (marketplace Shopee/Lazada,
    /// payment gateway, ค่าธรรมเนียมธนาคาร) — ลูกค้าชำระเต็มแต่เงินเข้าสุทธิ.
    /// JE: Dr เงินสด (Amount) + Dr ค่าธรรมเนียม (FeeAmount) / Cr AR
    /// (Amount+FeeAmount) → เอกสารถูกล้างที่ยอดเต็ม, ค่าธรรมเนียมเข้า P&L.</summary>
    public decimal FeeAmount { get; set; }
    /// <summary>ผังบัญชีค่าธรรมเนียม — null = default 53xxx/ค้นชื่อ "ค่าธรรมเนียม".</summary>
    public Guid? FeeAccountId { get; set; }

    /// <summary>ใบเสร็จรับเงิน (Document) ที่ออกคู่กับการชำระนี้ (ตอนติ๊ก "ออก
    /// ใบเสร็จรับเงิน"). ตอน void payment → ใบเสร็จนี้ถูก void ตามด้วย.</summary>
    public Guid? ReceiptDocumentId { get; set; }

    /// <summary>Per-document allocation lines — populated when ONE
    /// payment settles MULTIPLE documents (e.g. a single ฿15,000
    /// cheque that pays invoice A 5K + B 6K + C 4K). When this list
    /// is empty, the legacy single-doc path applies via DocumentId +
    /// Amount (the original 1:1 model is preserved for back-compat).
    /// Sum of AllocatedAmount must be ≤ Amount; the remainder is
    /// surfaced as UnappliedCredit on the response.</summary>
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

/// <summary>One row per (Payment, Document) — splits a payment
/// across multiple invoices. WHT is allocated proportionally so per-
/// document GL stays balanced. When a Payment has zero PaymentAllocation
/// rows the legacy Payment.DocumentId path drives settlement (existing
/// data is unchanged on migration).</summary>
public class PaymentAllocation : TenantEntity
{
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public decimal AllocatedAmount { get; set; }
    /// <summary>WHT carved out of this allocation row — sums across
    /// all allocations on the parent Payment should equal
    /// Payment.WithholdingTaxAmount.</summary>
    public decimal WithholdingTaxAmount { get; set; }
    public string? Note { get; set; }
}
