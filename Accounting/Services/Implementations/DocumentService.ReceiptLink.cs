using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก B — <b>ย้ายการรับชำระที่ตัดผิดใบไปใบที่ถูก (ผู้ติดต่อเดียวกัน)</b> + ยกเลิกใบเสร็จที่อ้างใบผิดและออกใบแทน
/// (คำตัดสิน 266-1-23…30 · erp-review/2026-11-12/team-266-manual-doc-link.md §4.3) · ไฟล์แยกตาม F4 ข้อ 7 (ไม่โตใน DocumentService.cs)
///
/// <para><b>ชั้นเงิน:</b> สองขาผ่านผังพัก 21630 อ้างเลขการรับชำระเดิม — <c>Dr ลูกหนี้(ใบเดิม) / Cr 21630</c> (<c>{เลข}/M{n}-OUT</c>) แล้ว
/// <c>Dr 21630 / Cr ลูกหนี้(ใบปลายทาง)</c> (<c>{เลข}/M{n}-IN</c>) ในธุรกรรมเดียว ⇒ <b>ขาเงินสด/ธนาคารของการรับชำระไม่ถูกแตะ</b> (คู่จับคู่ธนาคารคงเดิม) ·
/// <c>Payment.DocumentId</c> ไม่ถูกแก้ — ใบที่มีผลอยู่ที่ <c>Payment.MovedToDocumentId</c> + แถวจัดสรร (แถวเดิมถูก "ปล่อย" ไม่ถูกลบ)</para>
/// <para><b>ชั้นกระดาษ:</b> ใบเสร็จที่ออกเลขแล้วห้ามแก้ (ม.86/4 · ม.105) ⇒ ยกเลิก (คงแถว Voided) + ออกใบแทนผ่าน <see cref="CreateSettlementReceiptAsync"/> ตัวเดียวกับการรับชำระปกติ
/// ลงวันที่รับเงินเดิม · ยกเลิกไม่ได้ (e-Tax ถึงกรมสรรพากร) = ปฏิเสธทั้งรายการ — ไม่มี "ย้ายเงินแล้วทิ้งกระดาษอ้างผิด"</para>
/// <para><b>ไม่มีกติกาใหม่ที่นี่</b>: ด่านทั้งหมดอยู่ที่ <see cref="ReceiptLinkPolicy.Decide"/> (ตัดสินซ้ำใต้ล็อกก่อนเขียน) · ขั้นภาษีใช้เมธอดเดิม
/// (<see cref="UndoOutputVatOnPaymentVoidAsync"/> ฝั่งถอน · <see cref="ApplyCreditAllocationTaxStepsAsync"/> ฝั่งรับ) · ล้ม = throw ทั้งธุรกรรม (ห้าม catch กลืนในเส้นเงิน · F4 E)</para>
/// </summary>
public partial class DocumentService
{
    /// <inheritdoc />
    public async Task<SettlementMoveSnapshot?> LoadSettlementMoveFactsAsync(Guid companyId, Guid paymentId, Guid targetDocumentId, string actor)
    {
        var payment = await _db.Payments.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId);
        return payment == null ? null : await BuildSettlementMoveFactsAsync(companyId, payment, targetDocumentId, actor);
    }

    /// <summary>ข้อเท็จจริงของการรับชำระที่จะย้าย — ตัวเดียวของพรีวิว/ผู้สมัคร (นอกล็อก) และของการย้ายจริง (ใต้ล็อก · อ่านค่าล่าสุดแบบไม่ track) · tenant ทุก query</summary>
    private async Task<SettlementMoveSnapshot?> BuildSettlementMoveFactsAsync(Guid companyId, Payment payment, Guid targetDocumentId, string actor)
    {
        var fromId = PaymentEffectiveDocument.Of(payment);
        var settles = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == fromId && d.CompanyId == companyId);
        if (settles == null) return null;
        var target = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == targetDocumentId && d.CompanyId == companyId && !d.IsDeleted);

        var rows = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == payment.Id && a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new { a.DocumentId, a.AllocatedAmount, a.IsLateAllocation })
            .ToListAsync();
        var liveDocuments = rows.Select(r => r.DocumentId).Distinct().Count();
        var amount = rows.Count > 0 ? rows.Sum(r => r.AllocatedAmount) : payment.Amount;

        // ── ใบเสร็จของการรับชำระนี้ที่ยังมีผลและอ้างใบเดิม ──
        var receipt = await _db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.IsSettlementReceipt
                && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected
                && (r.SettlementPaymentId == payment.Id || r.Id == payment.ReceiptDocumentId)
                && r.RelatedDocumentId == fromId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();
        var etaxReachedRd = false;
        if (receipt != null)
        {
            var strongest = (await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, new[] { receipt.Id })).GetValueOrDefault(receipt.Id);
            etaxReachedRd = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(strongest, receipt.DocumentNumber, PaymentVoidCause.User).Action
                == AutoReceiptEtaxAction.Refuse;
        }
        var keptOriginal = await _db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.RelatedDocumentId == fromId && !r.IsDeleted
                && r.Status != DocumentStatus.Voided && r.EtaxKeptOriginalAt != null)
            .Select(r => r.DocumentNumber).FirstOrDefaultAsync();

        // ── ภาษีขายของใบเดิมที่ต้องถอย (เงื่อนไขเดียวกับ UndoOutputVatOnPaymentVoidAsync): ไม่เหลือเงินรับ + ภาษีถึงกำหนดแล้ว + ไม่มีใบเสร็จถือ VAT อื่นค้ำ ──
        string? sourceVatLock = null;
        if (settles.PaidAmount - amount <= 0.005m && settles.OutputVatDueAt != null
            && !await LiveVatReceiptExistsAsync(companyId, settles.Id, exceptReceiptId: receipt?.Id))
            sourceVatLock = await ReclassLockReasonAsync(companyId, settles.Id);

        // ── คู่ค้า NextAcc: ใบใดส่งเข้าระบบคู่ค้า (มีใบซื้อฝั่งผู้ซื้อ) หรือการรับชำระนี้ถูกแจ้งผู้ซื้อแล้ว ──
        var partnerLinked = await _db.CrossTenantDocumentLinks.AsNoTracking()
                .AnyAsync(l => l.SourceCompanyId == companyId && (l.SourceDocumentId == fromId || l.SourceDocumentId == targetDocumentId)
                    && l.LinkType == CrossTenantLinkType.InvoiceFlow && l.TargetDocumentId != null && !l.IsDeleted)
            || await _db.PartnerPaymentNotices.AsNoTracking()
                .AnyAsync(n => n.SourceCompanyId == companyId && n.SellerPaymentId == payment.Id && n.SellerVoidedAt == null);

        // ── กระดาษ (ฝ่ายค้าน B-1 · คำตัดสิน 266-1-32) — ตัวตัดสินหัวกระดาษเดียว (ComputeServedAsReceipt + ReceiptIssuePolicy.CoversPayment) ทั้งสองใบ:
        //    ใบเดิมเป็นใบกำกับภาษี/ใบเสร็จรับเงินในตัวของเงินก้อนนี้ (ไม่มี REC แยก) ⇒ ปฏิเสธ (หัวที่ลูกค้าถือจะเปลี่ยนย้อนหลังโดยไม่มีใบยกเลิก) ·
        //    ใบปลายทางจะเป็นใบรับในตัวหลังย้าย ⇒ ไม่ออกใบแยก + พรีวิวบอก · ใบปลายทางเป็นใบแจ้งหนี้ที่พักภาษีขายปิดยอดงวดเดียว ⇒ ใบแทน/ใบใหม่ถือ VAT ณ วันรับเงิน ──
        var receiptMode = await GetReceiptIssueModeAsync(companyId);
        var fromServed = false;
        if (receipt == null)
        {
            var fromServes = ComputeServedAsReceipt(settles,
                (await LoadDocIdsWithSeparateReceiptAsync(_db, companyId, new[] { settles.Id })).Contains(settles.Id), receiptMode,
                (await LoadSettlementDatesAsync(_db, companyId, new[] { settles.Id })).TryGetValue(settles.Id, out var fromSettledOn) ? fromSettledOn : null);
            fromServed = ReceiptIssuePolicy.CoversPayment(fromServes, settles.DocumentDate, payment.PaymentDate);
        }
        var targetServesAfter = false;
        var carriesVat = false;
        if (target != null)
        {
            // สภาพของใบปลายทาง "หลังย้าย" (ไม่ track · ตัวคำนวณสถานะเดียวกับเส้นย้าย DocumentSettlementState.Apply)
            var after = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(target), target.PaidAmount + amount, target.Status);
            var targetAfter = new Document
            {
                Id = target.Id, DocumentType = target.DocumentType, Status = after.Status, DocumentDate = target.DocumentDate,
                PaidOnIssue = target.PaidOnIssue, PaidAmount = target.PaidAmount + amount, BalanceDue = after.BalanceDue,
            };
            var targetSettled = (await LoadSettlementDatesAsync(_db, companyId, new[] { target.Id })).TryGetValue(target.Id, out var prior) ? prior : (DateTime?)null;
            var settledAfter = targetSettled is DateTime p0 && p0 > payment.PaymentDate ? p0 : payment.PaymentDate;
            targetServesAfter = ReceiptIssuePolicy.CoversPayment(
                ComputeServedAsReceipt(targetAfter, (await LoadDocIdsWithSeparateReceiptAsync(_db, companyId, new[] { target.Id })).Contains(target.Id),
                    receiptMode, settledAfter),
                target.DocumentDate, payment.PaymentDate);
            carriesVat = SettlementReceiptPolicy.CarriesTaxInvoiceRole(target.DocumentType, target.VatAmount,
                singleShotFull: target.PaidAmount <= 0.05m && target.BalanceDue - amount <= 0.005m,
                liveVatReceiptExists: await LiveVatReceiptExistsAsync(companyId, target.Id, exceptReceiptId: null));
        }
        var issueForTarget = receipt == null && carriesVat && !targetServesAfter;
        var needVoidReceipt = receipt != null;
        var needIssueReceipt = (receipt != null && !targetServesAfter) || issueForTarget;

        // ── สิทธิ์ (ฝ่ายค้าน B-2 · 266-1-27 ปรับ) — ตัดสินที่นี่ (ใต้ล็อกของเส้นย้ายด้วย) จากชนิดของใบจริงและเส้นที่จะเดินจริง ไม่ใช่จากพรีวิวนอกล็อก:
        //    อนุมัติชนิดใบปลายทาง (เงินเข้า) · ยกเลิกชนิดใบที่เงินถูกถอนออก · ยกเลิกใบเสร็จ (มีใบเดิม) · สร้าง+อนุมัติใบเสร็จ (ระบบออกใบใหม่ — ต้องเลขจริงทันที ไม่ใช่ฉบับร่าง) ──
        string? permissionProblem = null;
        string? receiptBlock = null;
        if (_permissionService != null && Guid.TryParse(actor, out var actorId))
        {
            if (target != null && !await DocumentPermissionHelper.CanApproveAsync(_permissionService, companyId, actorId, target.DocumentType))
                permissionProblem = $"ผู้กดไม่มีสิทธิ์อนุมัติ{DocumentTypeNames.Title(target.DocumentType, null)} — การรับเงินเข้าใบ {target.DocumentNumber} ต้องการ Document.Approve หรือ Document.Revenue.Approve";
            else if (!await DocumentPermissionHelper.CanVoidAsync(_permissionService, companyId, actorId, settles.DocumentType))
                permissionProblem = $"ผู้กดไม่มีสิทธิ์ยกเลิก{DocumentTypeNames.Title(settles.DocumentType, null)} — การถอนการรับชำระออกจากใบ {settles.DocumentNumber} ต้องการ Document.Void หรือ Document.Revenue.Void";
            var missing = new List<string>();
            if (needVoidReceipt && !await DocumentPermissionHelper.CanVoidAsync(_permissionService, companyId, actorId, DocumentType.Receipt)) missing.Add("ยกเลิก");
            if (needIssueReceipt && !await DocumentPermissionHelper.CanCreateAsync(_permissionService, companyId, actorId, DocumentType.Receipt)) missing.Add("สร้าง");
            if (needIssueReceipt && !await DocumentPermissionHelper.CanApproveAsync(_permissionService, companyId, actorId, DocumentType.Receipt)) missing.Add("อนุมัติ");
            if (missing.Count > 0)
                receiptBlock = $"ผู้กดไม่มีสิทธิ์{string.Join("/", missing)}ใบเสร็จรับเงิน (Document.Create/Approve/Void ของ Receipt — ใบที่ระบบออกต้องเป็นเลขจริงทันที ไม่ใช่ฉบับร่าง)";
        }

        // ── การควบคุมภายใน (266-1-27): SoD = ผู้บันทึกรับชำระย้ายเองไม่ได้ · วงเงินเซ็นหลายขั้น = ใบที่ระบบออกทันทีผ่านขั้นตอนเซ็นไม่ได้ ──
        string? approvalProblem = null;
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings != null)
        {
            if (needIssueReceipt && ApprovalControlPolicy.NeedsSignatureFlow(settings.RequireApprovalForDocuments, settings.ApprovalThresholdAmount, amount))
                approvalProblem = $"ยอด {amount:N2} บาท ถึงเกณฑ์เซ็นอนุมัติหลายขั้นของบริษัท — ใบเสร็จแทนที่ระบบออกให้ทันทีผ่านขั้นตอนเซ็นหลายขั้นไม่ได้";
            else if (ApprovalControlPolicy.SelfApprovalBlocked(settings.SodBlockSelfApproval, payment.CreatedBy, actor))
                approvalProblem = $"บริษัทเปิดการแบ่งแยกหน้าที่ (SoD) — ผู้บันทึกการรับชำระ {payment.PaymentNumber} ย้ายการรับชำระนั้นเองไม่ได้";
        }

        // ── สมุดรายวัน: ผังลูกหนี้ที่แต่ละใบตั้งไว้จริง · สมุดรายวันของการรับชำระ · วันที่ลง (266-1-24) ──
        string? journalProblem = null;
        if (!PortalSlipPending.WasCountedAsReceived(payment))
            journalProblem = $"การรับชำระ {payment.PaymentNumber} เป็นสลิปที่ยังไม่ตรวจ (ยังไม่นับเป็นเงินรับ ไม่มีสมุดรายวัน) — ไม่มีเงินให้ย้าย";
        else if (rows.Count > 0 && (liveDocuments != 1 || rows[0].DocumentId != fromId))
            journalProblem = liveDocuments > 1 ? null   // หลายใบ = ด่าน RL-COMBINED ของตัวตัดสิน
                : $"แถวจัดสรรของการรับชำระ {payment.PaymentNumber} ไม่ตรงกับใบที่การรับชำระนี้ตัดหนี้อยู่ — ให้ผู้ดูแลระบบตรวจข้อมูลก่อนย้าย";
        else if (await OriginalArApAccount.LoadAsync(_db, companyId, settles.Id, ArApSide.Receivable) == null)
            journalProblem = $"ไม่พบรายการบัญชีตั้งลูกหนี้ของใบ {settles.DocumentNumber} (ใบนี้ไม่มีสมุดรายวันตอนออกใบ) — ย้ายการรับชำระออกจากใบนี้ไม่ได้";
        else if (target != null && await OriginalArApAccount.LoadAsync(_db, companyId, target.Id, ArApSide.Receivable) == null)
            journalProblem = $"ไม่พบรายการบัญชีตั้งลูกหนี้ของใบ {target.DocumentNumber} (ใบนี้ไม่มีสมุดรายวันตอนออกใบ) — ย้ายการรับชำระเข้าใบนี้ไม่ได้";
        else if (!await _db.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == companyId && !j.IsDeleted
                     && j.Status == JournalEntryStatus.Posted && j.OriginalEntryId == null && j.ReversedByEntryId == null
                     && j.Reference != null && (j.Reference == payment.PaymentNumber || j.Reference.StartsWith(payment.PaymentNumber + "/"))))
            journalProblem = $"ไม่พบสมุดรายวันที่ยังมีผลของการรับชำระ {payment.PaymentNumber} — ย้ายผ่านผังพักจะทำให้ลูกหนี้ของใบเดิมเกินจริง";

        // วันที่ลงสองขา: วันรับเงินเดิม (ไม่ก่อนวันที่ของใบปลายทาง — ลูกหนี้ของใบนั้นยังไม่เกิด) ถ้างวดบัญชีเปิด · ปิด = วันที่ทำรายการ
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;
        var wanted = payment.PaymentDate.Date;
        if (target != null && target.DocumentDate.Date > wanted) wanted = target.DocumentDate.Date;
        if (wanted > today) wanted = today;
        var journalDate = wanted;
        string? closedNote = null;
        if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, wanted) is string)
        {
            journalDate = today;
            closedNote = $"งวดบัญชีของวันที่ {wanted:dd/MM/yyyy} ปิดแล้ว — ลงวันที่ทำรายการ {today:dd/MM/yyyy} (ยอดลูกหนี้ ณ สิ้นงวดที่ปิดคงตามที่รายงานไว้ ไม่แก้ย้อนหลัง)";
            if (journalProblem == null && await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, today) is string closedToday)
                journalProblem = closedToday;
        }

        var move = new ReceiptLinkMoveFacts(
            PaymentNumber: payment.PaymentNumber,
            PaymentDate: payment.PaymentDate.Date,
            Amount: Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            FromDocumentId: settles.Id,
            FromDocumentNumber: settles.DocumentNumber,
            FromBalanceDue: settles.BalanceDue,
            PaymentVoided: payment.IsDeleted,
            IsThb: (payment.ExchangeRate == null || payment.ExchangeRate == 1m) && CustomerCreditAllocation.IsThbDocument(settles.Currency, settles.ExchangeRate),
            HasWithholdingTax: payment.WithholdingTaxAmount > 0.005m,
            HasFeeOrAdjustment: payment.FeeAmount != 0m || payment.SettlementAdjustmentAmount != 0m,
            FromSettlementBatch: payment.SettlementBatchId != null,
            FromGateway: payment.PaymentIntentId != null || !string.IsNullOrWhiteSpace(payment.GatewayRef),
            HasUnappliedCredit: payment.UnappliedAccountId != null || UnappliedCreditBalance.HasBalance(payment.UnappliedAmount),
            LiveAllocationDocuments: liveDocuments,
            FromLateAllocation: rows.Any(r => r.IsLateAllocation),
            PartnerLinked: partnerLinked,
            LiveReceiptNumber: receipt?.DocumentNumber,
            ReceiptEtaxReachedRd: etaxReachedRd,
            KeptOriginalReceiptNumber: keptOriginal,
            SourceVatLockReason: sourceVatLock,
            ReplacementReceiptBlock: receiptBlock,
            ApprovalControlProblem: approvalProblem,
            JournalProblem: journalProblem,
            JournalDate: journalDate,
            ClosedPeriodNote: closedNote,
            ReplacementCarriesVat: carriesVat,
            FromServedAsReceipt: fromServed,
            TargetServesAfterMove: targetServesAfter,
            IssueReceiptForTarget: issueForTarget,
            PermissionProblem: permissionProblem);
        var source = receipt != null ? ReceiptLinkFacts.Source(receipt) : ReceiptLinkFacts.SettlementPayment(payment, settles, move.Amount);
        return new SettlementMoveSnapshot(payment.Id, source, move, receipt?.Id);
    }

    /// <summary>
    /// ด่านของ <c>VoidDocumentAsync</c>: ใบนี้มีการรับชำระที่ถูกย้ายออกไปตัดใบอื่นและยังมีผลไหม — สมุดรายวันขาเงินสด (และขาถอนของการย้าย) อ้างใบนี้ ⇒ การยกเลิกใบ
    /// (กลับทุกสมุดรายวันของใบ) จะกลับเงินสดที่รับจริงและทำให้ผังพัก 21630 ไม่เป็นศูนย์ · null = ไม่มี (ยกเลิกได้ตามด่านอื่น)
    /// </summary>
    private async Task<string?> MovedOutPaymentVoidBlockAsync(Guid companyId, Guid documentId, string documentNumber)
    {
        var movedOut = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.DocumentId == documentId
                && p.MovedToDocumentId != null && p.MovedToDocumentId != documentId)
            .Select(p => new { p.PaymentNumber, p.MovedToDocumentId })
            .FirstOrDefaultAsync();
        if (movedOut == null) return null;
        var movedToNumber = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == movedOut.MovedToDocumentId && d.CompanyId == companyId)
            .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
        return ReceiptLinkPolicy.MoveVoidSourceMessage(documentNumber, movedOut.PaymentNumber, movedToNumber);
    }

    /// <inheritdoc />
    public async Task<SettlementMoveResult> MoveSettlementAsync(Guid companyId, SettlementMoveCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = (command.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ย้ายการรับชำระ (เก็บบนใบแทนและร่องรอยตรวจสอบให้ผู้สอบบัญชี)", ReceiptLinkPolicy.MoveReasonCode, 400);

        SettlementMoveResult? result = null;
        Document? replacement = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            result = null;
            replacement = null;
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var peek = await _db.Payments.AsNoTracking()
                    .Where(p => p.Id == command.PaymentId && p.CompanyId == companyId && !p.IsDeleted)
                    .Select(p => new { p.DocumentId, p.MovedToDocumentId })
                    .FirstOrDefaultAsync()
                    ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
                var fromId = PaymentEffectiveDocument.Of(peek.DocumentId, peek.MovedToDocumentId);

                // ล็อกใบทั้งสอง (ORDER BY Id — ลำดับเดียวกับรับชำระ/ยกเลิกการรับชำระ/จัดสรร) แล้วจึงล็อกแถวการรับชำระ · อ่านค่าใหม่ใต้ล็อก
                await LockDocumentsForPaymentVoidAsync(companyId, new[] { fromId, command.TargetDocumentId });
                var payment = await _db.Payments
                    .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", command.PaymentId, companyId)
                    .FirstOrDefaultAsync()
                    ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
                await _db.Entry(payment).ReloadAsync();
                // คำขอซ้อน (กดซ้ำ/สองคน): คนที่สองเห็นว่าเงินไม่ได้อยู่ที่ใบที่พรีวิวเห็นแล้ว ⇒ 409 ไม่ย้ายซ้ำ
                if (payment.IsDeleted || PaymentEffectiveDocument.Of(payment) != fromId || fromId != command.ExpectedFromDocumentId)
                    throw new BusinessRuleException(ReceiptLinkPolicy.StaleMessage, ReceiptLinkPolicy.StaleCode, 409);

                var toDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == command.TargetDocumentId && d.CompanyId == companyId)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสารปลายทาง");
                await _db.Entry(toDoc).ReloadAsync();
                var fromDoc = await _db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == fromId && d.CompanyId == companyId)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสารเดิมของการรับชำระ");
                await _db.Entry(fromDoc).ReloadAsync();

                // ── ด่านเดียว ตัดสินซ้ำใต้ล็อกก่อนเขียน (ตัวเดียวกับพรีวิว) ──
                var snap = await BuildSettlementMoveFactsAsync(companyId, payment, toDoc.Id, actor)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสารเดิมของการรับชำระ");
                var verdict = ReceiptLinkPolicy.Decide(snap.Source, ReceiptLinkFacts.Invoice(toDoc), new ReceiptLinkContext(snap.Move.Amount, Move: snap.Move));
                if (!verdict.Allowed)
                    throw new BusinessRuleException((verdict.Message ?? "ย้ายการรับชำระไม่ได้") + (verdict.WayForward is { } way ? " · ทางไปต่อ: " + way : ""),
                        verdict.RuleCode, verdict.RuleCode == ReceiptLinkPolicy.MovePermissionCode ? 403 : 409);
                // ฝ่ายค้าน B-2: เส้นใต้ล็อกต้องเป็นเส้นที่ผู้กดเห็นในพรีวิว (มีคนออก/ยกเลิกใบเสร็จของการรับชำระนี้ระหว่างนั้น = ผลกระดาษต่างจากที่ยืนยัน) ⇒ 409 พรีวิวใหม่
                if (command.ExpectedRoute is ReceiptLinkRoute expectedRoute && verdict.Route != expectedRoute)
                    throw new BusinessRuleException(ReceiptLinkPolicy.StaleMessage, ReceiptLinkPolicy.StaleCode, 409);

                var amount = snap.Move.Amount;
                var journalDate = snap.Move.JournalDate;
                var now = DateTime.UtcNow;
                var fromBalanceBefore = fromDoc.BalanceDue;
                var toBalanceBefore = toDoc.BalanceDue;
                var arFrom = await OriginalArApAccount.LoadAsync(_db, companyId, fromDoc.Id, ArApSide.Receivable)
                    ?? throw new BusinessRuleException($"ไม่พบรายการบัญชีตั้งลูกหนี้ของใบ {fromDoc.DocumentNumber}", ReceiptLinkPolicy.MoveJournalCode, 409);
                var arTo = await OriginalArApAccount.LoadAsync(_db, companyId, toDoc.Id, ArApSide.Receivable)
                    ?? throw new BusinessRuleException($"ไม่พบรายการบัญชีตั้งลูกหนี้ของใบ {toDoc.DocumentNumber}", ReceiptLinkPolicy.MoveJournalCode, 409);
                var suspense = await UnappliedReceiptAccount.EnsureAsync(_db, companyId);

                var sequence = await _db.PaymentAllocations.IgnoreQueryFilters()
                    .CountAsync(a => a.PaymentId == payment.Id && a.CompanyId == companyId && a.MovedFromAllocationId != null) + 1;
                var outRef = ReceiptLinkPolicy.MoveJournalReference(payment.PaymentNumber, sequence, outLeg: true);
                var inRef = ReceiptLinkPolicy.MoveJournalReference(payment.PaymentNumber, sequence, outLeg: false);

                // ── ขาถอน: Dr ลูกหนี้ใบเดิม / Cr 21630 ──
                var outJe = await JournalEntryBuilder.For(_db, companyId, journalDate)
                    .Description($"ย้ายการรับชำระ {payment.PaymentNumber}: ถอนจาก {fromDoc.DocumentNumber} (ไป {toDoc.DocumentNumber})")
                    .Reference(outRef)
                    .SourceDocument(fromDoc.Id)
                    .Project(fromDoc.ProjectId)
                    .Debit(arFrom, amount, $"ตั้งลูกหนี้กลับ - {fromDoc.DocumentNumber} (เงิน {payment.PaymentNumber} ย้ายไป {toDoc.DocumentNumber})")
                    .Credit(suspense, amount, $"พักเงินรับ {payment.PaymentNumber} ระหว่างย้าย")
                    .PostAsync(actor);
                // ── ขารับ: Dr 21630 / Cr ลูกหนี้ใบปลายทาง ──
                var inJe = await JournalEntryBuilder.For(_db, companyId, journalDate)
                    .Description($"ย้ายการรับชำระ {payment.PaymentNumber}: ตัดลูกหนี้ {toDoc.DocumentNumber} (จาก {fromDoc.DocumentNumber})")
                    .Reference(inRef)
                    .SourceDocument(toDoc.Id)
                    .Project(toDoc.ProjectId)
                    .Debit(suspense, amount, $"ตัดเงินรับ {payment.PaymentNumber} ที่พักไว้ระหว่างย้าย")
                    .Credit(arTo, amount, $"ตัดลูกหนี้ - {toDoc.DocumentNumber}")
                    .PostAsync(actor);

                // ── แถวจัดสรร: แถวเดิมถูก "ปล่อย" (ไม่ลบ) · การรับชำระใบเดียวที่ยังไม่มีแถว = ทำให้เป็นแถวก่อนแล้วปล่อย ──
                var live = await _db.PaymentAllocations
                    .Where(a => a.PaymentId == payment.Id && a.CompanyId == companyId && !a.IsDeleted)
                    .ToListAsync();
                if (live.Count > 1)
                    throw new BusinessRuleException($"การรับชำระ {payment.PaymentNumber} จัดสรรหลายใบ — ย้ายชิ้นเดียวไม่ได้", ReceiptLinkPolicy.CombinedCode, 409);
                PaymentAllocation released;
                if (live.Count == 1) released = live[0];
                else
                {
                    released = new PaymentAllocation
                    {
                        CompanyId = companyId,
                        PaymentId = payment.Id,
                        DocumentId = fromDoc.Id,
                        AllocatedAmount = amount,
                        WithholdingTaxAmount = 0m,
                        Note = "แถวหลักของการรับชำระ (สร้างตอนย้ายการรับชำระ)",
                        CreatedBy = actor,
                    };
                    _db.PaymentAllocations.Add(released);
                }
                released.IsDeleted = true;
                released.ReleasedAt = now;
                released.ReleasedBy = actor;
                released.ReleaseJournalEntryId = outJe.Id;
                released.UpdatedAt = now;
                released.UpdatedBy = actor;
                if (released.StampedDocumentPaymentDate) fromDoc.PaymentDate = null;   // วันที่รับชำระที่ระบบประทับให้ใบเดิมถอยตามเงิน (ค่าที่ผู้ใช้กรอกเองไม่แตะ)

                var moved = new PaymentAllocation
                {
                    CompanyId = companyId,
                    PaymentId = payment.Id,
                    DocumentId = toDoc.Id,
                    AllocatedAmount = amount,
                    WithholdingTaxAmount = 0m,
                    Note = $"ย้ายจาก {fromDoc.DocumentNumber}: {reason}",
                    IsLateAllocation = false,
                    AppliedAt = journalDate,
                    JournalEntryId = inJe.Id,
                    MovedFromAllocationId = released.Id,
                    CreatedBy = actor,
                };
                // ใบที่ออกหลังเงินเข้า: วันที่รับชำระของใบ = วันรับเงินจริง (ม.78/1 — เงินก้อนนี้เป็นค่าของใบนี้ตั้งแต่วันรับ) · ไม่ทับค่าที่ผู้ใช้กรอกไว้
                if (toDoc.PaymentDate == null && payment.PaymentDate.Date <= toDoc.DocumentDate.Date)
                {
                    toDoc.PaymentDate = payment.PaymentDate.Date;
                    moved.StampedDocumentPaymentDate = true;
                }
                _db.PaymentAllocations.Add(moved);

                // ── ยอดของทั้งสองใบ (ตัวตัดสินสถานะเดียว DocumentSettlementState) ──
                fromDoc.PaidAmount = Math.Max(0m, fromDoc.PaidAmount - amount);
                var fromSettle = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(fromDoc), fromDoc.PaidAmount, fromDoc.Status);
                fromDoc.BalanceDue = fromSettle.BalanceDue;
                fromDoc.Status = fromSettle.Status;
                fromDoc.UpdatedAt = now;
                toDoc.PaidAmount += amount;
                var toSettle = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(toDoc), toDoc.PaidAmount, toDoc.Status);
                toDoc.BalanceDue = toSettle.BalanceDue;
                toDoc.Status = toSettle.Status;
                if (toDoc.Status == DocumentStatus.Paid)
                {
                    toDoc.AgingDays = null;
                    toDoc.AgingLastEvaluatedAt = now;
                }
                toDoc.UpdatedAt = now;
                // Payment.DocumentId ไม่ถูกแก้ (266-1-23) — ใบที่มีผลอยู่ที่ MovedToDocumentId (ย้ายกลับใบเดิม = null)
                payment.MovedToDocumentId = PaymentEffectiveDocument.Stamp(payment.DocumentId, toDoc.Id);
                payment.UpdatedAt = now;
                payment.UpdatedBy = actor;
                await _db.SaveChangesAsync();

                // ── กระดาษของใบเดิม: ใบเสร็จที่อ้างใบเดิม ⇒ ยกเลิก (คงแถว) · ถึงกรมสรรพากรแล้ว = throw (ทั้งธุรกรรม rollback — ด่านตรวจไปแล้ว นี่คือตาข่ายใต้ล็อก) ──
                var notices = new List<string>();
                var oldReceipt = snap.LiveReceiptId is Guid oldReceiptId
                    ? await _db.Documents.FirstOrDefaultAsync(d => d.Id == oldReceiptId && d.CompanyId == companyId)
                    : null;
                var receiptDecision = new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null);
                var oldReceiptWasIssued = false;
                if (oldReceipt != null)
                {
                    oldReceiptWasIssued = oldReceipt.Status != DocumentStatus.Draft;
                    (receiptDecision, _) = await DecideAutoReceiptOnPaymentVoidAsync(companyId,
                        new Payment { CompanyId = companyId, ReceiptDocumentId = oldReceipt.Id }, PaymentVoidCause.User);
                    await ApplyAutoReceiptOnPaymentVoidAsync(companyId, receiptDecision, oldReceipt, payment.PaymentNumber, keepVisible: oldReceiptWasIssued);
                    oldReceipt.UpdatedBy = actor;
                    AppendInternalNote(oldReceipt,
                        $"[RCPT-MOVE] ยกเลิกเพราะการรับชำระ {payment.PaymentNumber} ถูกย้ายจาก {fromDoc.DocumentNumber} ไป {toDoc.DocumentNumber} — เหตุผล: {reason}");
                }

                // ── ภาษีขายฝั่งถอน: ใบเดิมไม่เหลือเงินรับ ⇒ ถอยภาษีขายถึงกำหนด (ตัวเดียวกับยกเลิกการรับชำระ) · งวดปิด/ยื่นแล้ว = throw ⇒ ทั้งธุรกรรม rollback ──
                if (await UndoOutputVatOnPaymentVoidAsync(companyId, fromDoc, receiptDecision, oldReceipt, PaymentVoidCause.User,
                        "ย้ายการรับชำระ", payment.PaymentNumber) is string fromVatNotice)
                    notices.Add(fromVatNotice);
                await _db.SaveChangesAsync();

                // ── ภาษีฝั่งรับ: เงินก้อนนี้เป็นค่าของใบปลายทางตั้งแต่วันรับ ⇒ ตัวตัดสินเดียวกับเงิน "ค้างออกใบ" (LateReceiptTaxPoint) + ภาษีถูกหักตอนปิดยอด (เมธอดเดิม) ──
                notices.AddRange(await ApplyCreditAllocationTaxStepsAsync(companyId, toDoc.Id, nameof(UnappliedReason.UninvoicedSupply),
                    payment.PaymentDate, journalDate, inRef, actor));

                // ── ใบรับของใบปลายทาง (ฝ่ายค้าน B-1 · 266-1-32): ตัดสินจากข้อเท็จจริงชุดเดียวกับพรีวิว (ใต้ล็อก) — ใบปลายทางเป็นใบรับในตัว ⇒ ไม่ออกใบแยก ·
                //    มีใบเดิม ⇒ ใบแทน · ไม่มีใบเดิมแต่ใบปลายทางถือ VAT ณ วันรับเงิน ⇒ ออกใบใหม่ (ห้ามย้ายภาษีเข้างวดโดยไม่มีกระดาษ) · ตัวสร้างตัวเดียวกับการรับชำระปกติ ·
                //    วันที่ = วันรับเงินเดิม · อ้างใบปลายทาง · ออกเลขจริงทันที (สิทธิ์สร้าง/อนุมัติใบเสร็จตัดสินในข้อเท็จจริงแล้ว) ──
                if (oldReceipt != null || snap.Move.IssueReceiptForTarget)
                {
                    if (snap.Move.TargetServesAfterMove)
                    {
                        // ใบปลายทางเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ของการรับเงินก้อนนี้เอง (รับครบวันเดียวกับวันที่ใบ) — ใบเสร็จแยกอีกใบ = กระดาษสองใบของเงินเดียว
                        payment.ReceiptDocumentId = null;
                        notices.Add($"{toDoc.DocumentNumber} เป็นใบกำกับภาษี/ใบเสร็จรับเงินของการรับเงินก้อนนี้แล้ว (รับครบในวันเดียวกับวันที่ใบ) — "
                            + $"ยกเลิก {oldReceipt?.DocumentNumber} โดยไม่ออกใบเสร็จแยกใบใหม่ · พิมพ์ {toDoc.DocumentNumber} ให้ลูกค้าแทน");
                    }
                    else
                    {
                        var carryVat = snap.Move.ReplacementCarriesVat;
                        // "มุมมองการรับเงินของใบปลายทาง" (ไม่ track) — ยอด = ยอดที่ย้าย · วันที่ = วันรับเงินเดิม (ม.105)
                        var receiptOf = new Payment
                        {
                            Id = payment.Id, CompanyId = companyId, PaymentNumber = payment.PaymentNumber, DocumentId = toDoc.Id,
                            PaymentDate = payment.PaymentDate, Amount = amount,
                            PaymentMethod = payment.PaymentMethod, Reference = payment.Reference, BankAccount = payment.BankAccount,
                            BankAccountId = payment.BankAccountId, OverrideBankAccountId = payment.OverrideBankAccountId,
                            OverridePaymentAccountId = payment.OverridePaymentAccountId, Notes = payment.Notes, ProjectId = payment.ProjectId,
                            CreatedBy = payment.CreatedBy,
                        };
                        replacement = await CreateSettlementReceiptAsync(companyId, toDoc, receiptOf, actor, issueApproved: true, carryVatFromSource: carryVat);
                        if (oldReceipt != null)
                        {
                            replacement.ReplacesDocumentId = oldReceipt.Id;
                            replacement.ReplacementReason = reason;
                            AppendInternalNote(replacement,
                                $"[RCPT-MOVE] ออกแทน {oldReceipt.DocumentNumber} (อ้าง {fromDoc.DocumentNumber}) เพราะการรับชำระ {payment.PaymentNumber} ถูกย้ายมา {toDoc.DocumentNumber} — เหตุผล: {reason}");
                        }
                        else
                            AppendInternalNote(replacement,
                                $"[RCPT-MOVE] ออกให้ {toDoc.DocumentNumber} (ใบกำกับภาษี ณ วันรับเงิน) เพราะการรับชำระ {payment.PaymentNumber} ซึ่งไม่มีใบเสร็จแยก ถูกย้ายมาจาก {fromDoc.DocumentNumber} — เหตุผล: {reason}");
                        await _db.SaveChangesAsync();   // ใบใหม่ต้องอยู่ในฐานก่อนชี้กลับมาหา
                        if (oldReceipt != null)
                        {
                            oldReceipt.ReplacedByDocumentId = replacement.Id;
                            oldReceipt.ReplacedAt = now;
                        }
                        payment.ReceiptDocumentId = replacement.Id;
                        notices.Add(oldReceipt != null
                            ? $"ยกเลิกใบเสร็จ {oldReceipt.DocumentNumber} และออก {replacement.DocumentNumber} แทน (ลงวันที่รับเงินเดิม {payment.PaymentDate:dd/MM/yyyy} อ้าง {toDoc.DocumentNumber}) — "
                              + "ส่งใบใหม่ให้ลูกค้าจากหน้าเอกสาร และแจ้งว่าใบเดิมถูกยกเลิก (ระบบไม่ส่งอัตโนมัติ)"
                            : $"ออกใบเสร็จรับเงิน/ใบกำกับภาษี {replacement.DocumentNumber} ให้ {toDoc.DocumentNumber} ลงวันที่รับเงินเดิม {payment.PaymentDate:dd/MM/yyyy} (ใบกำกับภาษี ณ วันรับเงิน) — "
                              + "ส่งให้ลูกค้าจากหน้าเอกสาร (ระบบไม่ส่งอัตโนมัติ)");
                    }
                }
                // ฝ่ายค้าน B-8: จำกระดาษที่การย้ายครั้งนี้แตะไว้บนแถวที่เกิดจากการย้าย — ถอดภายหลังต้องรู้ (ย้ายกลับหลังยกเลิกใบเสร็จ = ใบเดิมชำระครบโดยไม่มีใบรับ)
                moved.MoveVoidedReceiptId = oldReceipt?.Id;
                moved.MoveIssuedReceiptId = replacement?.Id;

                // ── งานหลังรับชำระที่ยังค้างของการรับชำระนี้ (ฝ่ายค้าน B-6 · 266-1-30): บริบทคือใบเดิม ⇒ ข้าม (ไม่ส่งขอบคุณ/ใบเสร็จอ้างใบเดิม) — ธุรกรรมเดียวกับการย้าย ──
                var pendingRuns = await _db.PostPaymentAutomationRuns
                    .Where(r => r.CompanyId == companyId && r.PaymentId == payment.Id
                        && (r.Status == PostPaymentAutomationPolicy.StatusPending || r.Status == PostPaymentAutomationPolicy.StatusBlocked))
                    .ToListAsync();
                foreach (var run in pendingRuns)
                {
                    run.Status = PostPaymentAutomationPolicy.StatusSkipped;
                    run.Message = ReceiptLinkPolicy.MoveSkippedAutomationMessage;
                    run.CompletedAt = now;
                    run.UpdatedAt = now;
                }
                if (pendingRuns.Count > 0)
                    notices.Add($"ข้ามงานหลังรับชำระที่ยังค้าง {pendingRuns.Count} รายการของ {payment.PaymentNumber} (บริบทเป็นใบเดิม — ไม่ส่งข้อความ/ใบเสร็จอ้างใบผิด)");

                // ── การทวง (266-1-29): ใบเดิมกลับมาค้าง ⇒ พักหนังสือทวงอัตโนมัติด้วยกลไกเดิม (ประทับเวลา ไม่ขยับระดับทวง) ──
                if (fromDoc.BalanceDue > 0.005m && !DocumentStatusRules.NotIssued.Contains(fromDoc.Status) && fromDoc.Status != DocumentStatus.Voided)
                {
                    fromDoc.LastDunningSentAt = now;
                    AppendInternalNote(fromDoc,
                        $"[RCPT-MOVE] การรับชำระ {payment.PaymentNumber} ({amount:N2}) ถูกย้ายไป {toDoc.DocumentNumber} เมื่อ {ThaiDate.CalendarDateUtc(now):dd/MM/yyyy} — ใบนี้กลับมาค้างชำระ · พักหนังสือทวง {ReceiptLinkPolicy.DunningPauseDays} วัน");
                    notices.Add($"{fromDoc.DocumentNumber} กลับมาค้าง {fromDoc.BalanceDue:N2} บาท — ระบบพักหนังสือทวงอัตโนมัติ {ReceiptLinkPolicy.DunningPauseDays} วัน แจ้งลูกค้าก่อน");
                }
                if (snap.Move.ClosedPeriodNote != null) notices.Add(snap.Move.ClosedPeriodNote);

                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var actorId) ? actorId : null,
                    Action = AuditAction.Update,
                    EntityType = "Payment",
                    EntityId = payment.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        SettlesDocument = fromDoc.DocumentNumber,
                        FromBalanceDue = fromBalanceBefore,
                        TargetBalanceDue = toBalanceBefore,
                        Receipt = oldReceipt?.DocumentNumber,
                    }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "MoveSettlement",
                        RuleCode = ReceiptLinkPolicy.MoveRuleCode,
                        LegalReference = ReceiptLinkPolicy.MoveLegalReference,
                        payment.PaymentNumber,
                        Amount = amount,
                        SettlesDocument = toDoc.DocumentNumber,
                        FromBalanceDue = fromDoc.BalanceDue,
                        TargetBalanceDue = toDoc.BalanceDue,
                        JournalDate = journalDate,
                        JournalReferences = new[] { outRef, inRef },
                        ReleasedAllocationId = released.Id,
                        AllocationId = moved.Id,
                        VoidedReceipt = oldReceipt?.DocumentNumber,
                        ReplacementReceipt = replacement?.DocumentNumber,
                        Reason = reason,
                        by = actor,
                    }),
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                result = new SettlementMoveResult(payment.Id, payment.PaymentNumber, fromDoc.Id, fromDoc.DocumentNumber, fromDoc.BalanceDue,
                    toDoc.Id, toDoc.DocumentNumber, toDoc.BalanceDue, toDoc.Status, amount, moved.Id,
                    oldReceipt?.Id, oldReceipt?.DocumentNumber, replacement?.Id, replacement?.DocumentNumber, notices);
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // ── หลัง commit (ไม่ย้อนการย้าย): ทะเบียนเครดิตภาษีของใบเดิม · e-Tax อัตโนมัติ/ระบบคู่ค้าของใบแทน · ธงสลิปพอร์ทัลของใบปลายทาง ──
        // ไม่เรียกงานหลังรับชำระ (ขอบคุณ/ส่งใบเสร็จอัตโนมัติ) ซ้ำ — เงินก้อนนี้ไม่ใช่การรับเงินใหม่ (266-1-30)
        var done = result!;
        try { await SyncWhtCreditAfterCreditUndoAsync(companyId, done.FromDocumentId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.LogError(ex, "sync ทะเบียนเครดิตภาษีถูกหักหลังย้ายการรับชำระ {Pay} ไม่สำเร็จ", done.PaymentNumber); }
        if (replacement != null)
        {
            await _issuedHooks.RunAsync(companyId, replacement);
            await AfterSettlementReceiptIssuedAsync(companyId, replacement);
        }
        try { await DocumentSlipIntents.FlagSupersededAsync(_db, companyId, done.TargetDocumentId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.LogError(ex, "ติดธงสลิปพอร์ทัลที่ถูกแทนของเอกสาร {Doc} ไม่สำเร็จ (ย้ายการรับชำระ)", done.TargetDocumentId); }
        _logger.LogInformation("ย้ายการรับชำระ {Pay} {Amount:N2}: {From} → {To}", done.PaymentNumber, done.Amount, done.FromDocumentNumber, done.TargetDocumentNumber);
        return done;
    }
}
