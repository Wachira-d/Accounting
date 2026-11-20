using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 276 (ฝ่ายค้าน MED-4) — ด่านอนุมัติแบบ <b>บล็อก</b> ของใบที่ผู้ใช้ถูกจำกัดเอกสารสร้าง: ยอดรายได้สะสมของใบเสนอราคาต้นทางเกิน (แปลงซ้ำที่คนอื่นแปลงไปแล้ว) ·
/// ใบทั่วไปยังเป็น "คำเตือนก่อนอนุมัติ" ตามคำตัดสินข้อ 138 (ตัวนับเดียวกัน <c>LoadRootRevenueAsync</c> · <c>PartialConvertPolicy.IsOverAmount</c>) —
/// ผู้สร้างที่ถูกจำกัดมองไม่เห็นใบของคนอื่นตอนทำร่าง ⇒ ผู้อนุมัติต้องไม่ "รับทราบผ่าน" ยอดซ้ำโดยไม่ตั้งใจ
/// </summary>
public partial class DocumentService
{
    public const string RestrictedOverConvertRuleCode = "R276-OVER-CONVERT";

    /// <summary>null = ผ่าน · ข้อความ = บล็อกพร้อมทางไปต่อ (ตีกลับให้ผู้สร้างแก้)</summary>
    internal async Task<string?> RestrictedCreatorOverConvertAsync(Guid companyId, Document doc)
    {
        if (doc.CreatedByUserId is not Guid creator
            || !(await DocumentRestrictionGuard.ForUserAsync(_db, companyId, creator)).Restriction.IsRestricted)
            return null;
        if (doc.DocumentType is not (DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.Receipt or DocumentType.ReceiptVoucher)
            || doc.IsDeposit
            || await ResolveRootQuotationAsync(companyId, doc.RelatedDocumentId) is not { } rootRef
            || !RootRevenueLedger.CountsAsRevenue(doc.DocumentType, rootRef.ParentType, doc.IsDeposit)
            || await LoadRootRevenueAsync(companyId, rootRef.RootId, doc.Id) is not { } ledger
            || !string.Equals(ledger.Currency, doc.Currency ?? "THB", StringComparison.OrdinalIgnoreCase))
            return null;
        var billingNow = doc.Lines.Where(l => !l.IsDeleted).Sum(l => l.Amount) + doc.DepositBaseDeducted;
        return PartialConvertPolicy.IsOverAmount(ledger.Billed, billingNow, ledger.RootBase,
                roundingLines: ledger.LineCount + doc.Lines.Count(l => !l.IsDeleted))
            ? RootRevenueLedger.OverMessage(ledger.RootNumber, ledger.Billed, billingNow, ledger.RootBase)
              + " — ใบนี้สร้างโดยผู้ใช้ที่ถูกจำกัดเอกสาร (มองไม่เห็นใบของคนอื่น) จึงอนุมัติข้ามคำเตือนไม่ได้ · กด \"ตีกลับ\" ให้ผู้สร้างแก้ยอด"
            : null;
    }
}
