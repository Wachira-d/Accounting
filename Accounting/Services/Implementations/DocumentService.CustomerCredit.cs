using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" — <b>จุดต่อ</b>ระหว่าง <c>CustomerCreditService</c> (ผู้จัดสรร/ยกเลิกการจัดสรร) กับขั้นภาษี/ใบเสร็จของเอกสารที่มีอยู่แล้วในคลาสนี้
/// (ย้ายภาษีขายถึงกำหนด · ถอยภาษีขาย · ภาษีถูกหัก ณ ที่จ่ายตอนปิดยอด · ทะเบียนเครดิตภาษี · ออก/ยกเลิกใบเสร็จของการรับเงิน) — <b>ไม่มีกติกาใหม่ที่นี่</b>
/// เรียกเมธอดเดิมตามลำดับเดิม (ไม่คัดลอกลำดับไปไว้ใน service ใหม่ · ไม่เพิ่มเมธอดธุรกิจลง DocumentService.cs — CLAUDE.md F4 ข้อ 7)
/// </summary>
public partial class DocumentService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ApplyCreditAllocationTaxStepsAsync(Guid companyId, Guid documentId, string? creditReason,
        DateTime receiptDate, DateTime applyDate, string journalReference, string actor)
    {
        var notices = new List<string>();
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารที่จัดสรร");

        // ── ภาษีขาย (ม.78/1) — ฝ่ายค้าน H1: วันรับเงินเป็นจุดความรับผิดเฉพาะเงิน "ค้างออกใบ" · เหตุผลอื่น = วันที่จัดสรร ไม่มีธงล่าช้า
        // (ตัวตัดสินเดียว LateReceiptTaxPoint → DepositPolicyResolver.ForfeitTaxPointDecision) ──
        // ฝ่ายค้าน M6: อยู่ในธุรกรรมของการจัดสรร — ย้ายไม่สำเร็จ = throw ทั้งธุรกรรม rollback (ไม่มีใบที่ "ชำระแล้ว" แต่ภาษีขายไม่เข้า ภ.พ.30 เงียบ ๆ)
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var drivesTaxPoint = LateReceiptTaxPoint.ReceiptDrivesTaxPoint(creditReason);
        var taxPoint = LateReceiptTaxPoint.Decide(creditReason, receiptDate, applyDate,
            drivesTaxPoint && await DepositReceiptPeriodLockedAsync(companyId, receiptDate), today,
            await PlatformHolidayStore.LoadSetAsync(_db, receiptDate.Year, receiptDate.Year));
        var dueBefore = doc.OutputVatDueAt;
        await TryReclassifyUndueOutputVatAsync(companyId, documentId, taxPoint.TaxPointDate, actor, throwOnFailure: true);
        if (doc.DocumentType == DocumentType.Invoice && doc.OutputVatDueAt == null && doc.VatAmount > 0.005m)
        {
            // ยังพักอยู่จริงไหม (ยอดสุทธิ 21913 ของใบนี้) — พักอยู่แต่ไม่ถูกย้าย (เช่น ผังไม่มี 21913/21911) = ล้มดัง
            var stillUndue = await (
                from l in _db.JournalEntryLines.AsNoTracking()
                join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                where j.CompanyId == companyId && j.SourceDocumentId == documentId && !j.IsDeleted && !l.IsDeleted
                      && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                      && a.CompanyId == companyId && a.AccountCode == "21913"
                select (decimal?)(l.CreditAmount - l.DebitAmount)).SumAsync() ?? 0m;
            if (stillUndue > 0.005m)
                throw new BusinessRuleException(
                    $"จัดสรรให้ {doc.DocumentNumber} ไม่ได้ — ภาษีขายของใบนี้ยังพักอยู่ {stillUndue:N2} บาท และระบบย้ายเข้าภาษีขาย (ภ.พ.30) ไม่สำเร็จ "
                    + "(ตรวจว่าผังบัญชีมี 21913 และ 21911) · ยังไม่มีอะไรถูกบันทึก", LateReceiptTaxPoint.RuleCode, 409);
        }
        string? vatNote = null;
        if (dueBefore == null && doc.OutputVatDueAt != null)
            vatNote = taxPoint.Note;                                     // ย้ายภาษีขายจริงในครั้งนี้
        else if (dueBefore == null)                                      // ภาษีของใบไม่ได้พัก (ลงตามวันที่ใบแล้ว) — ระบบไม่ย้ายงวดของใบที่ออกแล้ว แต่ต้องบอก
            vatNote = LateReceiptTaxPoint.IssuedAfterReceiptNote(creditReason, receiptDate, doc.TaxPointDate ?? doc.DocumentDate, doc.VatAmount);
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
                    CreditReason = creditReason,
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
        return notices;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AfterCreditAllocationCommittedAsync(Guid companyId, Guid paymentId, Guid documentId, string actor)
    {
        var notices = new List<string>();
        // ธงสลิปพอร์ทัลที่ถูกแทน (ลูกค้าแนบสลิปของใบนี้ไว้ แต่ใบถูกปิดด้วยเงินรอจัดสรรแล้ว) — แยก try ของตัวเอง (ตัวเดียวกับเส้นรับชำระหลายใบ)
        try { await DocumentSlipIntents.FlagSupersededAsync(_db, companyId, documentId); }
        catch (Exception flagEx) when (flagEx is not OperationCanceledException)
        { _logger.LogError(flagEx, "ติดธงสลิปพอร์ทัลที่ถูกแทนของเอกสาร {Doc} ไม่สำเร็จ (จัดสรรเงินรอจัดสรร)", documentId); }

        // ฝ่ายค้าน H2: ใบเสร็จรับเงิน/ใบกำกับภาษีของการจัดสรร — ออกผ่านเส้นออกใบเสร็จของการชำระ (รายใบ) · ออกไม่ได้ = ป้ายบนใบ + คิว
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId);
        var doc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (payment != null && doc != null
            && await TryAutoIssueAllocationReceiptAsync(companyId, payment, doc, issueRequested: null, actor) is string receiptNotice)
            notices.Add(receiptNotice);
        return notices;
    }

    /// <summary>
    /// ฝ่ายค้านรอบ 258 H2 — ออกใบเสร็จรับเงิน/ใบกำกับภาษี "รายใบ" ของการรับเงินที่จัดสรร (เงินเกิน · หลายใบ · จัดสรรทีหลัง) ตามนโยบายเดียวกับรับชำระใบเดียว
    /// (<see cref="ReceiptIssuePolicy"/>: โหมดแยกใบ = ออกเสมอ · ไม่งั้นตามที่ผู้ใช้เลือก/ค่าตั้งต้นของบริษัท) ผ่าน <see cref="IssueReceiptForPaymentAsync"/> ตัวเดียวกับปุ่ม ·
    /// คืน null = ออกแล้ว/ไม่ต้องออก/ใบต้นทางเป็นใบเสร็จในตัว · มีค่า = ข้อความที่ผู้ใช้ต้องรู้ (ฉบับร่าง หรือ <b>ออกไม่ได้ — เก็บป้าย
    /// <see cref="PaymentExcessPlan.ReceiptPendingMarker"/> บนใบแล้ว</b> เพื่อให้คิวงานตามเก็บ · ไม่ throw: การรับเงิน/การจัดสรร commit ไปแล้ว)
    /// </summary>
    private async Task<string?> TryAutoIssueAllocationReceiptAsync(Guid companyId, Payment payment, Document doc, bool? issueRequested, string actor)
    {
        if (!SettlementReceiptPolicy.IsReceivableSource(doc.DocumentType)) return null;
        var mode = await GetReceiptIssueModeAsync(companyId);
        if (!(ReceiptIssuePolicy.ForcesSeparateReceipt(mode) || (issueRequested ?? ReceiptIssuePolicy.DefaultIssueSeparateReceipt(mode))))
            return null;
        string why;
        try
        {
            var issued = await IssueReceiptForPaymentAsync(companyId, payment.Id, actor, doc.Id);
            return issued.IsDraft
                ? $"{doc.DocumentNumber}: สร้างใบเสร็จ {issued.ReceiptNumber} เป็นฉบับร่าง — รอผู้มีสิทธิ์อนุมัติ"
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or BusinessRuleException)
        {
            if (ex.Message.StartsWith(SettlementReceiptPolicy.ServedBySourcePrefix, StringComparison.Ordinal)) return null;   // ใบต้นทางเป็นใบเสร็จของการรับเงินนี้แล้ว
            why = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ออกใบเสร็จอัตโนมัติของการจัดสรร {Pay} → {Doc} ไม่สำเร็จ", payment.PaymentNumber, doc.DocumentNumber);
            why = "ข้อผิดพลาดของระบบ (บันทึกรายละเอียดให้ผู้ดูแลระบบแล้ว)";
        }
        // ล้มดังบนตัวเอกสาร (ไม่ใช่แค่ข้อความชั่วคราว) — ตัวอ่านคิว: CustomerCreditService.ReceiptPendingAsync
        var note = PaymentExcessPlan.ReceiptPendingNote(doc.DocumentNumber, payment.PaymentNumber, why);
        _db.ChangeTracker.Clear();   // ธุรกรรมออกใบเสร็จ rollback แล้ว — ไม่พาแถวค้างไปบันทึกกับหมายเหตุ
        // ใบเสร็จถูกออกและ commit แล้ว แต่ขั้นหลัง commit (เช่น e-Tax อัตโนมัติ) ล้ม ⇒ ไม่ใช่ "ยังไม่ได้ออกใบเสร็จ" — ไม่ติดป้ายผิดความจริง
        if (await _db.Documents.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.SettlementPaymentId == payment.Id
                && r.RelatedDocumentId == doc.Id && r.IsSettlementReceipt && !r.IsDeleted
                && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected))
            return null;
        var current = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == doc.Id && d.CompanyId == companyId).Select(d => d.InternalNotes).FirstOrDefaultAsync();
        var merged = DepositPolicyResolver.AppendNoteOnce(current, note);
        await _db.Documents.Where(d => d.Id == doc.Id && d.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.InternalNotes, merged));
        return note;
    }

    /// <summary>
    /// ใบเสร็จ "รายใบ" ของการชำระ (ไม่ใช่ใบเสร็จของใบหลักที่ <c>Payment.ReceiptDocumentId</c> ชี้) — ตัดสินว่ายกเลิกได้/ต้องติดธง/ต้องปฏิเสธ ด้วยตัวเดียวกับเส้นยกเลิกการชำระ
    /// (<see cref="DecideAutoReceiptOnPaymentVoidAsync"/>) · คีย์ = ใบต้นทางของใบเสร็จ · <paramref name="onlyDocumentId"/> = จำกัดที่ใบเดียว (ยกเลิกการจัดสรร)
    /// </summary>
    private async Task<Dictionary<Guid, (AutoReceiptEtaxDecision Decision, Document Receipt)>> DecideAllocationReceiptsOnVoidAsync(
        Guid companyId, Guid paymentId, Guid? primaryReceiptId, Guid? onlyDocumentId, PaymentVoidCause cause)
    {
        var rows = await _db.Documents
            .Where(r => r.CompanyId == companyId && r.SettlementPaymentId == paymentId && r.IsSettlementReceipt && !r.IsDeleted
                && r.Status != DocumentStatus.Voided && r.RelatedDocumentId != null
                && (primaryReceiptId == null || r.Id != primaryReceiptId)
                && (onlyDocumentId == null || r.RelatedDocumentId == onlyDocumentId))
            .ToListAsync();
        var result = new Dictionary<Guid, (AutoReceiptEtaxDecision Decision, Document Receipt)>();
        foreach (var row in rows)
        {
            var (decision, receipt) = await DecideAutoReceiptOnPaymentVoidAsync(companyId,
                new Payment { CompanyId = companyId, ReceiptDocumentId = row.Id }, cause);
            if (receipt != null) result[row.RelatedDocumentId!.Value] = (decision, receipt);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<string?> UndoCreditAllocationTaxEffectsAsync(Guid companyId, Guid paymentId, Guid documentId, string paymentNumber)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารที่ยกเลิกการจัดสรร");
        // ใบเสร็จของการจัดสรรนี้ (ถ้าออกแล้ว — รวมใบเสร็จของใบหลัก): ตัวตัดสินเดียวกับยกเลิกการชำระ · ถึงกรมสรรพากรแล้ว = throw (ทั้งธุรกรรม rollback)
        var decision = new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null);
        Document? receipt = null;
        if ((await DecideAllocationReceiptsOnVoidAsync(companyId, paymentId, null, documentId, PaymentVoidCause.User))
                .TryGetValue(documentId, out var own))
        {
            decision = own.Decision;
            receipt = own.Receipt;
        }
        var receiptFlag = await ApplyAutoReceiptOnPaymentVoidAsync(companyId, decision, receipt, paymentNumber);
        // ตัวเดียวกับการยกเลิกการรับชำระ: ไม่เหลือเงินรับแล้ว ⇒ ถอยภาษีขายถึงกำหนด · งวดปิด/ยื่นแล้ว ⇒ ปฏิเสธ (ผู้ใช้กดเอง) · ยังมีใบเสร็จถือ VAT ⇒ ไม่ถอย
        var vatNotice = await UndoOutputVatOnPaymentVoidAsync(companyId, doc, decision, receipt,
            PaymentVoidCause.User, "ยกเลิกการจัดสรรเงินรอจัดสรร", paymentNumber);
        return CombineNotices(receiptFlag, vatNotice);
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
