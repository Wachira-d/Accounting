namespace Accounting.Models.Entities;

/// <summary>
/// ภ.พ.30 ประวัติย้อนหลัง — สำหรับบริษัทที่เริ่มใช้ระบบกลางปี
/// และต้องบันทึกยอด VAT ที่เคยยื่นไว้แล้วเพื่อให้รายงาน YTD ถูกต้อง.
///
/// One row per (Year, Month) per company. The Tax-report aggregator
/// adds these to any in-system data for the same period so users get
/// a continuous YTD view even when ledger detail only goes back N months.
/// </summary>
public class VatFilingHistory : TenantEntity
{
    public int Year { get; set; }
    public int Month { get; set; }       // 1..12

    /// <summary>ยอดขาย (ก่อน VAT) ตามแบบ ภ.พ.30 ที่ยื่นไปแล้ว</summary>
    public decimal SalesTotal { get; set; }
    /// <summary>VAT ขาย (Output VAT)</summary>
    public decimal OutputVat { get; set; }
    /// <summary>ยอดซื้อ (ก่อน VAT) ตามแบบ ภ.พ.30 ที่ยื่นไปแล้ว</summary>
    public decimal PurchaseTotal { get; set; }
    /// <summary>VAT ซื้อ (Input VAT)</summary>
    public decimal InputVat { get; set; }
    /// <summary>NetPayable = OutputVat - InputVat. คำนวณตอนบันทึก.</summary>
    public decimal NetPayable { get; set; }

    /// <summary>True เมื่อยื่นและจ่ายไปแล้วในระบบเดิม.</summary>
    public bool IsFiled { get; set; } = true;
    public DateTime? FiledAt { get; set; }
    public string? FilingReference { get; set; }      // เลขที่ยื่น/รับ
    public string? Notes { get; set; }
}
