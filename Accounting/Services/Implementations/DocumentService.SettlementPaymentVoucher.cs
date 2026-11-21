using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลของ "ออกใบสำคัญจ่ายย้อนหลัง" (รอบ 283) — <paramref name="VoucherNumber"/> null = ไม่ได้ออก (เหตุใน <paramref name="Reason"/>)</summary>
public sealed record SettlementVoucherResult(Guid PaymentId, SettlementPaymentVoucher.IssueAction Action, Guid? VoucherId, string? VoucherNumber, string Reason);

/// <summary>แถวรายงาน "การจ่ายที่ระบบบันทึกเองจากคู่ค้าแต่ยังไม่มีใบสำคัญจ่าย" (รอบ 283 · อ่านอย่างเดียว)</summary>
public sealed record PartnerPaymentWithoutVoucherRow(Guid PaymentId, string PaymentNumber, DateTime PaymentDate, decimal Amount, decimal WithholdingTaxAmount,
    Guid DocumentId, string DocumentNumber, string? ContactName);

/// <summary>
/// รอบ 283 (คำสั่งเจ้าของ · คำตัดสิน 283-1…283-6) — ใบสำคัญจ่าย<b>หลักฐานคู่การจ่าย</b>ของการจ่ายใบซื้อ (ตัวตัดสิน <see cref="SettlementPaymentVoucher"/>) ·
/// ตัวสร้างเดียว <see cref="CreateSettlementPaymentVoucherCoreAsync"/> ใช้ทั้งในธุรกรรมของ <see cref="CreatePaymentAsync"/> (คำขอ <c>IssuePaymentVoucherDocument</c> —
/// ท่ออัตโนมัติคู่ค้า) และเส้นออกย้อนหลัง <see cref="IssueSettlementPaymentVoucherAsync"/> · ยกเลิกการจ่าย ⇒ ใบนี้ยกเลิกตาม (คงแถวไว้ — เลขไม่หาย) ·
/// ยกเลิกใบนี้ ⇒ ยกเลิกการจ่าย (<see cref="VoidDocumentAsync"/>) — ถอย JE/ยอดหนี้/50 ทวิ ด้วยเส้นยกเลิกการจ่ายเดิมตัวเดียว
/// </summary>
public partial class DocumentService
{
    /// <summary>สร้างใบสำคัญจ่ายหลักฐาน (ผู้เรียกถือธุรกรรม + SaveChanges) — ไม่ลง JE · ไม่ตัดหนี้ · ไม่ออก 50 ทวิ (ใบของการจ่ายนี้มีอยู่แล้ว)</summary>
    private async Task<Document> CreateSettlementPaymentVoucherCoreAsync(Guid companyId, Document source, Payment payment, string actor, bool backfilled)
    {
        var labels = Accounting.Services.Implementations.Pdf.DocumentLabels.For(source.DocumentLanguage);
        var canApprove = true;
        if (_permissionService != null && Guid.TryParse(actor, out var actorUid))
            canApprove = await DocumentPermissionHelper.CanApproveAsync(_permissionService, companyId, actorUid, DocumentType.PaymentVoucher);
        // 50 ทวิ ของการจ่ายนี้ (ออกตอนบันทึกจ่ายในธุรกรรมเดียวกัน · หรือก่อนหน้าสำหรับใบย้อนหลัง) — แสดงเลขบนใบ ไม่ออกใบใหม่
        var certNumber = payment.WithholdingTaxAmount > 0m
            ? await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(w => w.CompanyId == companyId && w.DocumentId == source.Id && w.SourcePaymentId == payment.Id
                    && w.Status != WithholdingTaxCertStatus.Voided && !w.IsDeleted)
                .OrderBy(w => w.CreatedAt).Select(w => w.CertificateNumber).FirstOrDefaultAsync()
            : null;
        var number = canApprove
            ? await DocumentNumberGenerator.NextAsync(_db, companyId, DocumentType.PaymentVoucher, payment.PaymentDate)
            : $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14);
        var amount = SettlementPaymentVoucher.SubTotal(payment.Amount);
        var pv = new Document
        {
            CompanyId = companyId,
            DocumentNumber = number,
            DocumentType = DocumentType.PaymentVoucher,
            DocumentDate = payment.PaymentDate,
            PaymentDate = payment.PaymentDate,
            ContactId = source.ContactId,
            RelatedDocumentId = source.Id,
            // ธง "หลักฐานคู่การชำระ" ตัวเดียวกับใบเสร็จฝั่งขาย — ทุกเส้นแกน (อนุมัติ · ยกเลิก · ลบพร้อมใบแม่ · ตรวจ JE หาย) ข้าม JE/ตัดหนี้ของใบนี้อยู่แล้ว
            IsSettlementReceipt = true,
            SettlementPaymentId = payment.Id,
            OriginModule = SettlementPaymentVoucher.PartnerOrigin,
            Reference = source.DocumentNumber,
            Status = canApprove ? DocumentStatus.Paid : DocumentStatus.Draft,
            UpdatedBy = canApprove ? actor : null,
            PaymentType = PaymentType.Cash,
            Currency = source.Currency,
            ExchangeRate = payment.ExchangeRate ?? source.ExchangeRate,
            DocumentLanguage = source.DocumentLanguage,
            BrandId = source.BrandId,
            Sensitivity = source.Sensitivity,
            BranchId = source.BranchId,
            DocumentTemplateId = source.DocumentTemplateId,
            ProjectId = source.ProjectId,
            SubTotal = amount,
            DiscountAmount = 0m,
            VatAmount = SettlementPaymentVoucher.VatAmount,
            WithholdingTaxAmount = SettlementPaymentVoucher.WithholdingTaxAmount,
            TotalAmount = amount,
            PaidAmount = amount,
            BalanceDue = 0m,
            BankAccountId = payment.BankAccountId,
            PaymentAccountId = payment.OverridePaymentAccountId,
            // ใบนี้ไม่ใช่ทางเข้าออก 50 ทวิ (ภาษีหักไม่อยู่บนใบ) — ปิดเพื่อไม่ให้ผู้อ่านติ๊กตีความผิด
            WhtCertAutoIssue = false,
            Notes = labels.SettlementPaymentVoucherNote(source.DocumentNumber, payment.PaymentDate),
            CreatedBy = actor,
        };
        AppendInternalNote(pv, SettlementPaymentVoucher.InternalNote(payment.PaymentNumber, backfilled));
        _db.Documents.Add(pv);
        _db.DocumentLines.Add(new DocumentLine
        {
            DocumentId = pv.Id,
            LineOrder = 1,
            Description = labels.SettlementPaymentVoucherLine(source.DocumentNumber, amount, payment.WithholdingTaxAmount, certNumber),
            Quantity = 1m,
            Unit = labels.SettlementUnitItem,
            UnitPrice = amount,
            Amount = amount,
            VatRate = 0m,
            VatAmount = 0m,
            ProjectId = source.ProjectId,
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Create,
            EntityType = "Document",
            EntityId = pv.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = backfilled ? "SettlementPaymentVoucherBackfilled" : "SettlementPaymentVoucherIssued",
                documentNumber = number, paymentId = payment.Id, paymentNumber = payment.PaymentNumber, sourceDocumentId = source.Id,
                sourceNumber = source.DocumentNumber, amount, paymentWht = payment.WithholdingTaxAmount, whtCertNumber = certNumber,
                issuedNumber = canApprove, postsJournal = false, by = actor,
                ruleCode = SettlementPaymentVoucher.RuleCode, legalReference = SettlementPaymentVoucher.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        return pv;
    }

    /// <summary>ข้อเท็จจริงของการจ่ายสำหรับตัวตัดสิน (อ่านจากฐาน — ใบหลักฐานที่ยังมีผล · ใบสำคัญจ่ายแบบแปลงของใบตั้งหนี้)</summary>
    private async Task<SettlementPaymentVoucher.Facts> SettlementVoucherFactsAsync(Guid companyId, Payment? payment, Document? source)
    {
        if (payment == null || source == null)
            return new(false, false, 0m, DocumentType.PurchaseInvoice, DocumentStatus.Draft, true, null, false);
        var existing = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.DocumentType == DocumentType.PaymentVoucher && d.SettlementPaymentId == payment.Id
                && !d.IsDeleted && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => (Guid?)d.Id).FirstOrDefaultAsync();
        var converted = await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.CompanyId == companyId && d.DocumentType == DocumentType.PaymentVoucher && d.RelatedDocumentId == source.Id
                && !d.IsSettlementReceipt && !d.IsDeleted && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval);
        return new(true, payment.IsDeleted, payment.Amount, source.DocumentType, source.Status, source.IsDeleted, existing, converted);
    }

    /// <summary>ในธุรกรรมของ <see cref="CreatePaymentAsync"/>: คำขอให้ออกใบสำคัญจ่ายหลักฐาน ⇒ ตัดสิน + สร้าง · ตัดสินว่าออกไม่ได้ ⇒ โยน (การจ่ายทั้งก้อน rollback —
    /// ผู้เรียกอัตโนมัติไม่ได้ "จ่ายแล้วไม่มีใบ" เงียบ ๆ)</summary>
    private async Task<Document?> IssueSettlementPaymentVoucherInPaymentAsync(Guid companyId, Document source, Payment payment, string actor)
    {
        var decision = SettlementPaymentVoucher.Decide(await SettlementVoucherFactsAsync(companyId, payment, source));
        if (decision.Action == SettlementPaymentVoucher.IssueAction.AlreadyIssued) return null;
        if (decision.Action != SettlementPaymentVoucher.IssueAction.Issue)
            throw new BusinessRuleException(decision.Reason, SettlementPaymentVoucher.RuleCode, 409);
        var pv = await CreateSettlementPaymentVoucherCoreAsync(companyId, source, payment, actor, backfilled: false);
        await _db.SaveChangesAsync();
        return pv;
    }

    /// <summary>
    /// "ออกใบสำคัญจ่ายย้อนหลัง" ให้การจ่ายที่บันทึกไปแล้ว (เฉพาะแถวที่ผู้ใช้เลือก) — <b>ไม่ลง JE ซ้ำ</b> · ไม่แตะยอดหนี้ · ไม่ออก 50 ทวิ ·
    /// ล็อกแถวการจ่ายกันกดซ้ำพร้อมกัน · มีใบอยู่แล้ว = AlreadyIssued (ไม่ใช่ความล้มเหลว)
    /// </summary>
    public async Task<SettlementVoucherResult> IssueSettlementPaymentVoucherAsync(Guid companyId, Guid paymentId, string actor)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            var payment = await _db.Payments
                .FromSqlRaw("SELECT * FROM \"Payments\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", paymentId, companyId)
                .FirstOrDefaultAsync();
            var source = payment == null ? null
                : await _db.Documents.FirstOrDefaultAsync(d => d.Id == payment.DocumentId && d.CompanyId == companyId);
            var decision = SettlementPaymentVoucher.Decide(await SettlementVoucherFactsAsync(companyId, payment, source));
            if (decision.Action != SettlementPaymentVoucher.IssueAction.Issue)
            {
                await tx.RollbackAsync();
                string? existingNo = decision.ExistingVoucherId is Guid eid
                    ? await _db.Documents.AsNoTracking().Where(d => d.Id == eid && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync()
                    : null;
                return new SettlementVoucherResult(paymentId, decision.Action, decision.ExistingVoucherId, existingNo, decision.Reason);
            }
            var pv = await CreateSettlementPaymentVoucherCoreAsync(companyId, source!, payment!, actor, backfilled: true);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return new SettlementVoucherResult(paymentId, decision.Action, pv.Id, pv.DocumentNumber, decision.Reason);
        });
    }

    /// <summary>รายงาน (อ่านอย่างเดียว): การจ่ายที่ท่อคู่ค้าบันทึกเอง (หมายเหตุขึ้นต้น <see cref="SettlementPaymentVoucher.PartnerAutoPaymentNotePrefix"/> + การแจ้งที่บันทึกแล้วอ้างถึง)
    /// ที่ยังไม่มีใบสำคัญจ่ายหลักฐานที่มีผล — ใบที่บันทึกก่อนรอบ 283 · ใบที่การจ่ายถูกยกเลิกไม่ขึ้น</summary>
    public async Task<List<PartnerPaymentWithoutVoucherRow>> ListPartnerPaymentsWithoutVoucherAsync(Guid companyId, int take = 200)
    {
        var recordedIds = _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && n.Status == PartnerPaymentNoticeRule.StatusRecorded && n.RecordedPaymentId != null)
            .Select(n => n.RecordedPaymentId!.Value);
        var rows = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && recordedIds.Contains(p.Id)
                && p.Notes != null && p.Notes.StartsWith(SettlementPaymentVoucher.PartnerAutoPaymentNotePrefix)
                && (p.Document.DocumentType == DocumentType.PurchaseInvoice || p.Document.DocumentType == DocumentType.Expense)
                && !_db.Documents.Any(d => d.CompanyId == companyId && d.DocumentType == DocumentType.PaymentVoucher && d.SettlementPaymentId == p.Id
                    && !d.IsDeleted && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected))
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(p => new PartnerPaymentWithoutVoucherRow(p.Id, p.PaymentNumber, p.PaymentDate, p.Amount, p.WithholdingTaxAmount,
                p.DocumentId, p.Document.DocumentNumber, p.Document.Contact != null ? p.Document.Contact.Name : null))
            .ToListAsync();
        return rows;
    }

    /// <summary>ในเส้นกลับรายการการจ่าย (ใบเดียว · หลายใบ): ใบสำคัญจ่ายหลักฐานของการจ่ายนี้ ⇒ ยกเลิก (คงแถวที่มองเห็น — ใบมีเลขแล้ว ห้ามหาย) + audit ·
    /// ไม่มี JE ให้กลับ (หลักฐานอย่างเดียว) · ผู้เรียกถือธุรกรรม</summary>
    private async Task VoidSettlementPaymentVouchersOfPaymentAsync(Guid companyId, Payment payment)
    {
        var vouchers = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.DocumentType == DocumentType.PaymentVoucher && d.SettlementPaymentId == payment.Id
                && d.IsSettlementReceipt && !d.IsDeleted && d.Status != DocumentStatus.Voided)
            .ToListAsync();
        foreach (var pv in vouchers)
        {
            var before = pv.Status;
            pv.Status = DocumentStatus.Voided;
            pv.UpdatedAt = DateTime.UtcNow;
            AppendInternalNote(pv, $"[{SettlementPaymentVoucher.RuleCode}] {SettlementPaymentVoucher.VoidedWithPaymentNote} {payment.PaymentNumber}");
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = "Document",
                EntityId = pv.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = "SettlementPaymentVoucherVoidedWithPayment", documentNumber = pv.DocumentNumber, from = before.ToString(),
                    paymentId = payment.Id, paymentNumber = payment.PaymentNumber, ruleCode = SettlementPaymentVoucher.RuleCode,
                }),
                Timestamp = DateTime.UtcNow,
            });
        }
    }
}
