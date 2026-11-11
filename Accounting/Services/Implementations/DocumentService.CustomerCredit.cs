using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" — <b>จุดต่อ</b>ระหว่าง <c>CustomerCreditService</c> (ผู้จัดสรร/ยกเลิกการจัดสรร) กับขั้นภาษีของเอกสารที่มีอยู่แล้วในคลาสนี้
/// (ย้ายภาษีขายถึงกำหนด · ถอยภาษีขาย · ภาษีถูกหัก ณ ที่จ่ายตอนปิดยอด · ทะเบียนเครดิตภาษี) — <b>ไม่มีกติกาใหม่ที่นี่</b> เรียกเมธอดเดิมตามลำดับเดิม
/// (ไม่คัดลอกลำดับไปไว้ใน service ใหม่ · ไม่เพิ่มเมธอดธุรกิจลง DocumentService.cs — CLAUDE.md F4 ข้อ 7)
/// </summary>
public partial class DocumentService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AfterCreditAllocatedAsync(Guid companyId, Guid documentId, DateTime receiptDate,
        DateTime applyDate, string journalReference, string actor)
    {
        var notices = new List<string>();
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารที่จัดสรร");

        // ── ภาษีขาย (ม.78/1): เงินเข้าตั้งแต่ receiptDate ⇒ ใบที่พักภาษีไว้ 21913 ย้ายเข้างวดของวันรับเงินถ้ายังยื่นทัน (258-1-5) ──
        // ตัวตัดสินงวดเดียวกับการริบมัดจำ (LateReceiptTaxPoint → DepositPolicyResolver.ForfeitTaxPointDecision) · ตัวย้ายเดิม TryReclassifyUndueOutputVatAsync
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var taxPoint = LateReceiptTaxPoint.Decide(receiptDate, applyDate,
            await DepositReceiptPeriodLockedAsync(companyId, receiptDate), today,
            await PlatformHolidayStore.LoadSetAsync(_db, receiptDate.Year, receiptDate.Year));
        var dueBefore = doc.OutputVatDueAt;
        await TryReclassifyUndueOutputVatAsync(companyId, documentId, taxPoint.TaxPointDate, actor);
        string? vatNote = null;
        if (dueBefore == null && doc.OutputVatDueAt != null)
            vatNote = taxPoint.Note;                                     // ย้ายภาษีขายจริงในครั้งนี้
        else if (dueBefore == null)                                      // ภาษีของใบไม่ได้พัก (ลงตามวันที่ใบแล้ว) — ระบบไม่ย้ายงวดของใบที่ออกแล้ว แต่ต้องบอก
            vatNote = LateReceiptTaxPoint.IssuedAfterReceiptNote(receiptDate, doc.TaxPointDate ?? doc.DocumentDate, doc.VatAmount);
        if (vatNote != null)
        {
            notices.Add(vatNote);
            if (!(doc.InternalNotes ?? "").Contains(vatNote, StringComparison.Ordinal))
            {
                AppendInternalNote(doc, vatNote);
                doc.UpdatedAt = DateTime.UtcNow;
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(actor, out var actorId) ? actorId : null,
                Action = AuditAction.Update,
                EntityType = "Document",
                EntityId = doc.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    RuleCode = LateReceiptTaxPoint.RuleCode,
                    LegalReference = LateReceiptTaxPoint.LegalReference,
                    doc.DocumentNumber,
                    ReceiptDate = receiptDate.Date,
                    ApplyDate = applyDate.Date,
                    TaxPointDate = taxPoint.TaxPointDate,
                    taxPoint.LateFlag,
                    Message = vatNote,
                }),
            });
        }
        await _db.SaveChangesAsync();

        // ── ภาษีถูกหัก ณ ที่จ่าย: ปิดยอดครบโดยไม่ผ่านการรับเงินสด ⇒ Dr 11910 / Cr ลูกหนี้ ส่วนที่ยังไม่เคยลง (ตัวเดียวกับเส้นหักมัดจำ) ──
        // อ้างเลขของการจัดสรร (journalReference) ⇒ ยกเลิกการจัดสรร/ยกเลิกการชำระกลับ JE นี้ไปด้วย
        if (doc.BalanceDue <= 0.005m && doc.WithholdingTaxAmount > 0.005m)
        {
            await TryRecognizeSalesWhtOnSettlementAsync(companyId, doc, applyDate, actor, journalReference);
            await _db.SaveChangesAsync();
        }
        await SyncWhtCreditReceivedAsync(companyId, doc);

        // เส้นนี้ไม่ออกใบกำกับภาษี/ใบเสร็จรับเงินอัตโนมัติ ⇒ ใบแจ้งหนี้ที่มี VAT ต้องบอกผู้ใช้ทุกครั้ง (ตัวเดียวกับเส้นรับชำระหลายใบ)
        if (PaymentExcessPlan.NoAutoTaxInvoiceNotice(doc.DocumentType, doc.VatAmount, doc.DocumentNumber) is string noAutoDoc)
            notices.Add(noAutoDoc);

        // ธงสลิปพอร์ทัลที่ถูกแทน (ลูกค้าแนบสลิปของใบนี้ไว้ แต่ใบถูกปิดด้วยเงินรอจัดสรรแล้ว) — แยก try ของตัวเอง (ตัวเดียวกับเส้นรับชำระหลายใบ)
        try { await DocumentSlipIntents.FlagSupersededAsync(_db, companyId, documentId); }
        catch (Exception flagEx) when (flagEx is not OperationCanceledException)
        { _logger.LogError(flagEx, "ติดธงสลิปพอร์ทัลที่ถูกแทนของเอกสาร {Doc} ไม่สำเร็จ (จัดสรรเงินรอจัดสรร)", documentId); }
        return notices;
    }

    /// <inheritdoc />
    public async Task<string?> UndoCreditAllocationTaxEffectsAsync(Guid companyId, Guid documentId, string paymentNumber)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารที่ยกเลิกการจัดสรร");
        // ตัวเดียวกับการยกเลิกการรับชำระ: ไม่เหลือเงินรับแล้ว ⇒ ถอยภาษีขายถึงกำหนด · งวดปิด/ยื่นแล้ว ⇒ ปฏิเสธ (ผู้ใช้กดเอง) · ยังมีใบเสร็จถือ VAT ⇒ ไม่ถอย
        return await UndoOutputVatOnPaymentVoidAsync(companyId, doc,
            new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null), null,
            PaymentVoidCause.User, "ยกเลิกการจัดสรรเงินรอจัดสรร", paymentNumber);
    }

    /// <inheritdoc />
    public Task<string?> CreditAllocationVatLockReasonAsync(Guid companyId, DateTime date)
        => OutputVatPeriodLockReasonAsync(companyId, date);

    /// <inheritdoc />
    public async Task SyncWhtCreditAfterCreditUndoAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc != null) await SyncWhtCreditReceivedAsync(companyId, doc);
    }
}
