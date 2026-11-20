using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 275 แพ็ก C — ใบกำกับภาษี ณ วันรับเงิน (ม.78/1) ของการรับชำระใบแจ้งหนี้: คิว "รับเงินแล้วยังไม่มีใบกำกับภาษี" + ปุ่มออกใบกำกับเพิ่ม
/// (คำตัดสิน 4.1 / 275-C-1 · ใบเก่าที่ออกไปแล้วไม่แก้ย้อนหลัง — ผู้ทำบัญชีกดทีละรายการ) · ตัวตัดสินเดียว <see cref="SettlementTaxInvoiceRole"/>
/// </summary>
public partial class DocumentService
{
    /// <summary>คิว (อ่านอย่างเดียว · tenant) — ตัวอ่านเดียวกับ Autopilot (<see cref="SettlementTaxInvoiceLoader.QueueAsync"/>)</summary>
    public async Task<IReadOnlyList<ReceivedWithoutTaxInvoiceRow>> ReceivedWithoutTaxInvoiceAsync(Guid companyId, CancellationToken ct = default)
        => (await SettlementTaxInvoiceLoader.QueueAsync(_db, companyId, 200, ct)).Select(ToQueueDto).ToList();

    internal static ReceivedWithoutTaxInvoiceRow ToQueueDto(SettlementTaxInvoiceLoader.QueueRow r) => new(
        r.DocumentId, r.DocumentNumber, r.ContactName, r.PaymentId, r.PaymentNumber, r.PaymentDate, r.Amount,
        r.PlainReceiptId, r.PlainReceiptNumber, r.Decision.Mode.ToString(), r.Decision.Reason.ToString(),
        r.Decision.CarriesRole ? r.Decision.VatAmount : null,
        r.Decision.CarriesRole ? null : SettlementTaxInvoiceRole.ReasonText(r.Decision.Reason),
        r.Decision.CarriesRole);

    /// <summary>ปุ่ม "ออกใบกำกับภาษี ณ วันรับเงิน" ของคิว — ยกเลิกใบรับเปล่าของการรับนี้ (ถ้ามี) แล้วออกใบเสร็จที่ถือ VAT ตามตัวตัดสินเดียว ในธุรกรรมเดียว ·
    /// ตัวตัดสินไม่ออก (ใบลดหนี้ก่อนรับ ฯลฯ) ⇒ 409 พร้อมเหตุ ไม่แตะอะไร · ใบรับเปล่าที่ถึงกรมสรรพากรแล้ว ⇒ 409</summary>
    /// <para>ใบเก่าที่ภาษีขายทั้งใบรายงานแล้ว (คำตัดสิน 275-C-10): ใบกำกับที่ออกเป็นหลักฐานกระดาษ (<c>Document.VatReportedOnSource</c>) — แถว ภ.พ.30/§87 คงอยู่ที่ใบแจ้งหนี้
    /// ตามที่ยื่นไปแล้ว ไม่มีแถวใหม่ลงวันที่ย้อนหลังในรายงาน · ออกทีละรายการได้ (ไม่กระทบตัวเลขที่ยื่น)</para></summary>
    public Task<IssuedReceiptResult> IssueTaxInvoiceForPaymentAsync(Guid companyId, Guid paymentId, Guid documentId, string createdBy)
        => IssueReceiptForPaymentCoreAsync(companyId, paymentId, createdBy, documentId, replacePlainWithTaxInvoice: true);

    /// <summary>งวดรายงาน (TaxPointDate) ของใบกำกับรายงวดที่ออก<b>หลัง</b>วันรับเงิน: วันที่ของ JE ย้ายภาษีตามส่วนที่ผูกการรับนี้ (เส้นใหม่) ·
    /// ไม่มี (ใบเก่าที่ภาษีย้ายทั้งก้อนตอนรับงวดแรก) ⇒ <c>OutputVatDueAt</c> ของใบแจ้งหนี้ (ภ.พ.30 ที่ยื่นไปแล้วไม่เปลี่ยน — ไม่แก้ย้อนหลัง) · ยังไม่ย้าย ⇒ วันรับเงิน</summary>
    private async Task<DateTime> InstallmentTaxPointAsync(Guid companyId, Document invoice, Payment payment)
    {
        var tag = SettlementTaxInvoiceLoader.InstallmentReclassTag(payment.Id);
        var tagged = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId == invoice.Id && j.Note == tag && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted && j.OriginalEntryId == null && j.ReversedByEntryId == null)
            .Select(j => (DateTime?)j.EntryDate).FirstOrDefaultAsync();
        return (tagged ?? invoice.OutputVatDueAt ?? payment.PaymentDate).Date;
    }

    /// <summary>ยกเลิกใบรับเปล่า (VAT 0) ที่ถูกแทนด้วยใบกำกับ ณ วันรับเงิน — ใบที่ถึงกรมสรรพากรแล้ว (e-Tax) ⇒ ปฏิเสธ (ยกเลิกเงียบไม่ได้) · tenant</summary>
    private async Task VoidPlainReceiptForTaxInvoiceAsync(Guid companyId, Document plain, Document invoice)
    {
        var strongest = (await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, new[] { plain.Id })).GetValueOrDefault(plain.Id);
        var etax = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(strongest, plain.DocumentNumber, PaymentVoidCause.User);
        if (etax.Action == AutoReceiptEtaxAction.Refuse)
            throw new BusinessRuleException(
                $"ใบเสร็จ {plain.DocumentNumber} ของการรับนี้ส่ง e-Tax ถึงกรมสรรพากรแล้ว — ยกเลิกเพื่อออกใบกำกับภาษีแทนเงียบ ๆ ไม่ได้ · "
                + "ยกเลิกทาง e-Tax ก่อน แล้วกดอีกครั้ง · ระบบยังไม่ได้แตะอะไร", SettlementTaxInvoiceRole.RuleCode, 409);
        var now = DateTime.UtcNow;
        plain.Status = DocumentStatus.Voided;
        plain.UpdatedAt = now;
        AppendInternalNote(plain, $"[{SettlementTaxInvoiceRole.RuleCode}] ยกเลิกเพื่อออกใบกำกับภาษี ณ วันรับเงินแทน (ใบแจ้งหนี้ {invoice.DocumentNumber} · "
            + $"{SettlementTaxInvoiceRole.LegalReference})");
        var pending = await _db.EtaxInvoices
            .Where(e => e.CompanyId == companyId && e.DocumentId == plain.Id && e.Status != EtaxStatus.Voided
                && !DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(e.Status))
            .ToListAsync();
        foreach (var e in pending)
        {
            e.Status = EtaxStatus.Voided;
            e.VoidedAt = now;
            e.VoidReason = "ยกเลิกเพื่อออกใบกำกับภาษี ณ วันรับเงินแทน";
            e.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
    }
}
