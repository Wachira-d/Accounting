using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 200 ทีม V1 — **"ยกเลิกและออกใบแทน" ใบขายที่รอบโอน settlement ที่ลงบัญชีแล้วรับชำระ** (คำตัดสินข้อ 9 · review198-S3 S3-5)
/// และรายการงานค้าง "ต้องยกเลิกทาง e-Tax" (คำตัดสินข้อ 11)
///
/// <para>แยกไฟล์ (partial) ตาม F4 ข้อ 7 — ไม่เพิ่มฟีเจอร์ทับ DocumentService.cs · ตัวตัดสินทั้งหมดอยู่ที่ <see cref="SettlementPaidReissue"/> (pure + เทสต์) ·
/// จุดเรียกล็อกด้วย tools/required_call_site_check.py</para>
///
/// <para><b>ทำไม "ย้าย" ไม่ใช่ "กลับรายการแล้วลงใหม่"</b>: เงินจริงไม่เปลี่ยน (รอบโอนรับเงินเข้าผังพักแล้ว) · ยอด/บรรทัด/อัตรา VAT/tax point เท่าเดิมทุกตัว ⇒
/// รายการบัญชีทุกขาเท่าเดิม — กลับรายการ + ลงใหม่จะทำให้ต้นทุน FIFO ขยับ · ภาษีขายที่ถึงกำหนดตอนรับเงิน (§78/1) ถูกกลับ · งวดปิดล้ม ·
/// ผังพักของรอบโอนคลาดถ้าการรับชำระถูกกลับ ⇒ ใบเดิม Voided โดย<b>ไม่มีรายการกลับ</b> และรายการบัญชี/การรับชำระ/สต็อก/คู่จับของรอบโอน
/// ถูกชี้ไปใบใหม่ (<c>ReplacementCarriesPostings = true</c> — ใบใหม่เป็นเจ้าของผลทางบัญชี: ยกเลิกใบใหม่ภายหลัง = กลับรายการ/คืนสต็อกตามปกติ)</para>
///
/// <para>รอบ 200 ทีม V1F (แก้ผลฝ่ายค้าน review200-V1): ด่านรายงานภาษีล็อก/งวดปิดดูใบเสร็จอัตโนมัติด้วย (R1) · e-Tax ที่ส่งแล้วยังไม่รู้ผลของใบขาย
/// = บล็อก ไม่พลิกเป็น Voided (R4) · ผู้ขอคนเดียวไม่พอเมื่อ SoD/วงเงินเซ็นหลายขั้น (R6 — บันทึกคำขอรอคนที่สอง) · คัดลอกแบบ allowlist (R7) ·
/// ด่านของคำขอผู้ใช้ (R8) · ย้ายลิงก์ PaymentIntent/บรรทัดโครงการ/คำบรรยาย JE (R9) · ผู้ซื้อ §86/4 ตัวตัดสินเดียวกับเส้นอนุมัติ (R10) ·
/// ผลข้างเคียงหลัง commit อยู่นอก execution strategy (P4) · ปิดธง "ต้องยกเลิกทาง e-Tax" (R3)</para>
/// </summary>
public partial class DocumentService
{
    /// <summary>ข้อเท็จจริง + คำตัดสินของ "ยกเลิกและออกใบแทน" — <b>ตัวเดียว</b>ของปุ่มบนหน้าเอกสาร (<c>GetDocumentAsync</c>) และการกดจริง</summary>
    /// <param name="lockRows">true = อยู่ในธุรกรรมของการกดจริง: ล็อกแถวรอบโอน <c>FOR SHARE</c> + แถวการรับชำระ <c>FOR UPDATE</c></param>
    private async Task<(SettlementPaidReissueVerdict Verdict, List<Payment> Payments, List<Document> Receipts)> EvaluateSettlementPaidReissueAsync(
        Guid companyId, Document doc, bool lockRows)
    {
        var none = (new List<Payment>(), new List<Document>());
        var quick = SettlementPaidReissue.QuickRelevance(doc.DocumentType, doc.Status, doc.IsSettlementReceipt,
            doc.ReplacedByDocumentId.HasValue, SettlementArtifactGuard.BatchIdFromCreator(doc.CreatedBy).HasValue);
        if (quick != null) return (quick, none.Item1, none.Item2);

        var postedBlock = await SettlementArtifactGuard.CheckDocumentPaymentsAsync(_db, companyId, doc.Id, lockBatchRows: lockRows);
        if (postedBlock == null)
            return (SettlementPaidReissue.Decide(Facts(doc, null)), none.Item1, none.Item2);

        var payments = lockRows
            ? await _db.Payments
                .FromSqlRaw(
                    """SELECT * FROM "Payments" WHERE "DocumentId" = {0} AND "CompanyId" = {1} AND "IsDeleted" = false FOR UPDATE""",
                    doc.Id, companyId)
                .ToListAsync()
            : await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.DocumentId == doc.Id && !p.IsDeleted)
                .ToListAsync();
        var paymentIds = payments.Select(p => p.Id).ToList();

        // การรับชำระที่จัดสรรเข้าหลายใบ — ของใบนี้ที่แตกไปใบอื่น หรือของใบอื่นที่มีส่วนมาใบนี้ (ย้ายบางส่วนไม่ได้)
        var sharedNumber = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && ((paymentIds.Contains(a.PaymentId) && a.DocumentId != doc.Id)
                    || (a.DocumentId == doc.Id && !paymentIds.Contains(a.PaymentId))))
            .Select(a => a.Payment.PaymentNumber)
            .FirstOrDefaultAsync();

        // ใบเสร็จอัตโนมัติของการรับชำระที่จะย้าย (ยังมีผล) — ยกเลิกแล้วออกใหม่อ้างใบใหม่ ⇒ ไม่นับเป็น "เอกสารลูกที่บล็อก"
        var receiptIds = payments.Where(p => p.ReceiptDocumentId != null).Select(p => p.ReceiptDocumentId!.Value).Distinct().ToList();
        var receiptsQuery = _db.Documents.Where(d => d.CompanyId == companyId && receiptIds.Contains(d.Id)
            && d.IsSettlementReceipt && !d.IsDeleted && d.Status != DocumentStatus.Voided);
        var receipts = receiptIds.Count == 0 ? new List<Document>()
            : lockRows ? await receiptsQuery.ToListAsync() : await receiptsQuery.AsNoTracking().ToListAsync();
        var liveReceiptIds = receipts.Select(r => r.Id).ToList();

        // สถานะ e-Tax ที่ใช้ตัดสิน — ตัวโหลดเดียว (แถว e-Tax + e-Tax by Email ที่ประทับเวลาแล้ว · V1-P1) ทั้งใบขายและใบเสร็จ (V1-R4: ชุดสถานะเดียวกัน)
        var etaxById = await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, liveReceiptIds.Append(doc.Id).ToList());

        // V1-R1: ใบเสร็จถือ VAT เป็นเจ้าของแถว ภ.พ.30 — ด่านรายงานล็อก + งวดบัญชีของวันที่ใบเสร็จ (วันรับเงิน) ต้องดูทุกใบ
        var receiptFacts = new List<ReissueReceiptFact>();
        foreach (var r in receipts)
        {
            var receiptLocked = _taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, r.Id);
            receiptFacts.Add(new ReissueReceiptFact(r.DocumentNumber, etaxById.GetValueOrDefault(r.Id), receiptLocked,
                await ClosedPeriodNameAsync(companyId, r.DocumentDate)));
        }

        // ด่านเดิมของ "ยกเลิกเอกสาร" ทุกตัว (e-Tax ถึงกรมสรรพากร · รายงานล็อก · 50 ทวิ ยื่นแล้ว · เอกสารลูก/ใบลดหนี้อ้าง)
        var filingLocked = _taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, doc.Id);
        var whtFiled = await WhtCertVoidGuard.CheckDocumentAsync(_db, companyId, doc.Id);
        var childBlock = (await DocumentVoidPreconditions.ChildBlocksAsync(_db, companyId, new[] { doc.Id },
                ignoreChildIds: liveReceiptIds))
            .TryGetValue(doc.Id, out var cb) ? cb : null;
        var closedPeriod = await ClosedPeriodNameAsync(companyId, doc.DocumentDate);
        var depositApplied = doc.DepositAppliedAmount > 0.005m
            || await _db.Documents.AsNoTracking().AnyAsync(d => d.CompanyId == companyId && !d.IsDeleted && d.DepositAppliedToDocumentId == doc.Id)
            || await _db.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == companyId && j.DepositAppliedToDocumentId == doc.Id);
        var activeCerts = await _db.WithholdingTaxCerts.AsNoTracking()
            .CountAsync(w => w.CompanyId == companyId && w.DocumentId == doc.Id && !w.IsDeleted && w.Status != WithholdingTaxCertStatus.Voided);
        var vatDeferral = await _db.VatDeferrals.AsNoTracking()
            .AnyAsync(v => v.CompanyId == companyId && v.DocumentId == doc.Id && !v.IsDeleted);

        var facts = Facts(doc, postedBlock) with
        {
            HasDepositApplied = depositApplied,
            DocumentEtax = etaxById.GetValueOrDefault(doc.Id),
            FilingLocked = filingLocked,
            WhtFiledBlock = whtFiled,
            ChildBlock = childBlock,
            ClosedPeriodName = closedPeriod,
            SharedPaymentNumber = sharedNumber,
            ActiveWhtCertificates = activeCerts,
            HasVatDeferral = vatDeferral,
            Receipts = receiptFacts,
        };
        return (SettlementPaidReissue.Decide(facts), payments, receipts);
    }

    /// <summary>ชื่องวดบัญชีที่ไม่เปิด (ปิด/ล็อก) ของวันที่นั้น — null = เปิด/ไม่มีงวด · ตัวเดียวของใบขายและใบเสร็จในด่านออกใบแทน</summary>
    private async Task<string?> ClosedPeriodNameAsync(Guid companyId, DateTime date)
    {
        var day = date.Date;
        return await _db.FiscalPeriods.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.StartDate <= day && f.EndDate >= day && f.Status != FiscalPeriodStatus.Open)
            .Select(f => f.Name).FirstOrDefaultAsync();
    }

    private static SettlementPaidReissueFacts Facts(Document doc, string? postedBlock) => new(
        doc.DocumentType, doc.Status, doc.IsSettlementReceipt, doc.ReplacedByDocumentId.HasValue,
        SettlementArtifactGuard.BatchIdFromCreator(doc.CreatedBy).HasValue, postedBlock, doc.IsDeposit,
        HasDepositApplied: false, DocumentEtax: null, FilingLocked: false, WhtFiledBlock: null, ChildBlock: null,
        ClosedPeriodName: null, SharedPaymentNumber: null, ActiveWhtCertificates: 0, HasVatDeferral: false,
        Receipts: Array.Empty<ReissueReceiptFact>());

    /// <summary>ชนิดที่ใช้เลือก "ตัวย่อเลขที่" ของเอกสาร — กติกาเดียวของการออกเลขตอนอนุมัติและตอนยกเลิกและออกใบแทน (ย้ายมาจาก ApproveDocumentAsync)</summary>
    private async Task<DocumentType> ResolveNumberSeriesTypeAsync(Guid companyId, Document doc)
    {
        var seriesType = doc.DocumentType;
        if (doc.IsTaxInvoiceByLaw.HasValue)
        {
            // null (ยังไม่เคยตั้ง) = เปิด — ดู TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled ห้ามเขียน `?? false` เองที่นี่
            var unify = TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(
                await _db.CompanySettings.AsNoTracking()
                    .Where(cs => cs.CompanyId == companyId)
                    .Select(cs => cs.UnifyTaxInvoiceNumberSeries)
                    .FirstOrDefaultAsync());
            if (unify)
                seriesType = TaxInvoiceSeriesPolicy.SeriesTypeOverride(doc, doc.IsTaxInvoiceByLaw.Value) ?? doc.DocumentType;
        }
        return seriesType;
    }

    public async Task<DocumentResponse> ReissueSettlementPaidDocumentAsync(Guid companyId, Guid documentId,
        ReissueSettlementPaidRequest request, string actor)
    {
        var confirmPending = request.ConfirmPendingRequest == true;
        if (!confirmPending && string.IsNullOrWhiteSpace(request.Reason))
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ยกเลิกและออกใบแทน (พิมพ์บนใบใหม่และเก็บไว้ให้ผู้สอบบัญชี)", "REISSUE-REASON");

        // P4: execution strategy ครอบเฉพาะธุรกรรม — ผลข้างเคียงหลัง commit (e-Tax อัตโนมัติ · อ่านผล) อยู่นอก lambda ⇒ retry ไม่ทำให้รอบสองเห็นใบเดิม
        // Voided แล้วตอบ 409 ทั้งที่รอบแรกออกใบแทนสำเร็จ
        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            Document? neo = null;
            var newReceipts = new List<Document>();
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // ล็อกแถวใบเดิมก่อนอ่าน — กันกดซ้ำ/ยกเลิกซ้อน (เหมือน VoidDocumentAsync)
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", documentId, companyId);
                var old = await _db.Documents.Include(d => d.Lines)
                    .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

                // ── V1-R6: ด่านควบคุมภายในตัวเดียวกับการอนุมัติ (ApprovalControlPolicy) — SoD/วงเงินเซ็นหลายขั้น ⇒ ต้องมีคนที่สอง ──
                var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
                var control = ApprovalControlPolicy.ForReissue(settings?.RequireApprovalForDocuments ?? false, settings?.ApprovalThresholdAmount,
                    old.TotalAmount, settings?.SodBlockSelfApproval ?? false, old.ReissueRequestedBy, actor, confirmPending);
                if (control.Action == ReissueControlAction.Blocked)
                    throw new BusinessRuleException(control.Reason ?? "ยกเลิกและออกใบแทนไม่ได้", control.RuleCode ?? "REISSUE-CONTROL", 409);
                // ผู้ยืนยันอนุมัติ "สิ่งที่ผู้ขอส่ง" (บันทึกไว้) ไม่ใช่ข้อมูลที่ส่งมาใหม่
                var effective = control.Action == ReissueControlAction.ConfirmRequest
                    ? SettlementPaidReissueRequestCodec.Parse(old.ReissueRequestJson)
                        ?? throw new BusinessRuleException("อ่านคำขอที่บันทึกไว้ไม่ได้ — กด “ยกเลิกคำขอ” แล้วส่งคำขอใหม่ · ระบบยังไม่ได้แตะอะไร",
                            "REISSUE-PENDING-UNREADABLE", 409)
                    : request;
                // รอบ 200 ทีม V1G (คำตัดสินข้อ 49 · RV1F-5): การยืนยันผูกกับคำขอฉบับที่ผู้ยืนยัน "เห็น" (ผู้ซื้อ/เลขภาษี/สาขา/ที่อยู่ เดิม→ใหม่ · หมายเหตุ ·
                // คำบรรยาย) — ประกอบใหม่ใต้ล็อกด้วยตัวเดียวกับหน้าจอ แล้วเทียบ hash · คำขอ/ทะเบียนผู้ซื้อถูกแก้หลังเปิดดู ⇒ 409 (ต้องดูใหม่แล้วยืนยันใหม่)
                if (control.Action == ReissueControlAction.ConfirmRequest)
                {
                    var seen = await BuildReissueRequestViewAsync(companyId, old)
                        ?? throw new BusinessRuleException("อ่านคำขอที่บันทึกไว้ไม่ได้ — กด “ยกเลิกคำขอ” แล้วส่งคำขอใหม่ · ระบบยังไม่ได้แตะอะไร",
                            "REISSUE-PENDING-UNREADABLE", 409);
                    if (SettlementPaidReissueRequestView.ConfirmMismatch(request.ConfirmRequestHash, seen) is string mismatch)
                        throw new BusinessRuleException(mismatch, "REISSUE-REQUEST-CHANGED", 409);
                }
                var reason = (effective.Reason ?? "").Trim();
                if (reason.Length == 0)
                    throw new BusinessRuleException("กรุณาระบุเหตุผลที่ยกเลิกและออกใบแทน (พิมพ์บนใบใหม่และเก็บไว้ให้ผู้สอบบัญชี)", "REISSUE-REASON");

                // ด่านเดียวของปุ่มและการกดจริง — ใต้ล็อก (แถวรอบโอน FOR SHARE: ยกเลิกการลงบัญชีแทรกไม่ได้ · การรับชำระ FOR UPDATE)
                var eval = await EvaluateSettlementPaidReissueAsync(companyId, old, lockRows: true);
                if (!eval.Verdict.Allowed)
                    throw new BusinessRuleException(eval.Verdict.Reason ?? "ยกเลิกและออกใบแทนไม่ได้", eval.Verdict.RuleCode ?? "REISSUE-BLOCKED", 409);

                // ── ผู้ซื้อของใบใหม่ (ของบริษัทนี้เท่านั้น) ──
                var contactId = effective.ContactId ?? old.ContactId;
                var contact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
                    ?? throw new BusinessRuleException("ไม่พบผู้ซื้อที่เลือกในบริษัทนี้ — เลือกผู้ติดต่อใหม่แล้วกดอีกครั้ง · ระบบยังไม่ได้แตะอะไร",
                        "REISSUE-CONTACT", 400);
                var buyerChanged = contact.Id != old.ContactId;
                // V1-R10: ผู้ซื้อ §86/4 — ตัวตัดสินเดียวกับเส้นอนุมัติ · "ผู้ซื้อไม่ประสงค์รับใบกำกับ" เป็นคำแจ้งของผู้ซื้อเดิม ⇒ ไม่ตามไปผู้ซื้อคนใหม่
                var buyerDeclined = !buyerChanged && old.BuyerDeclinedTaxInvoice;
                var enforce864 = settings?.EnforceFullTaxInvoiceFields ?? true;
                var mustEnforce864 = Tax.TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(old.DocumentType, old.VatAmount, enforce864);
                var isDeferredVatDeposit = old.IsDeposit && old.DepositOutputVatDeferred
                    && old.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher;
                var blockingBuyerFields = Tax.TaxInvoiceCompletenessChecker.BuyerBlockingFields(
                    mustEnforce864, old.VatAmount, contact, isDeferredVatDeposit, buyerDeclined);
                if (blockingBuyerFields != null)
                    throw new BusinessRuleException(
                        $"ข้อมูลผู้ซื้อยังไม่ครบตาม §86/4 ({string.Join(", ", blockingBuyerFields)} · ผู้ซื้อนิติบุคคล) — แก้ที่ผู้ติดต่อ “{contact.Name}” ก่อน "
                        + "แล้วกดอีกครั้ง (ใบแทนต้องถูกต้องครบ ไม่งั้นต้องออกซ้ำอีก) · ระบบยังไม่ได้แตะอะไร", "RD-86/4-REISSUE-BUYER", 409);

                // ── V1-R8: ด่านของ "สิ่งที่ผู้ใช้ส่งมา" — คำบรรยายอ้าง Id บรรทัดของใบเดิมเท่านั้น และห้ามว่าง ──
                var oldLines = old.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();
                var requestedLines = (effective.Lines ?? new List<ReissueLineDescription>()).ToList();
                var lineIssues = SettlementPaidReissue.RequestLineIssues(oldLines.Select(l => l.Id).ToList(),
                    requestedLines.Select(l => (l.LineId, l.Description)));
                if (lineIssues.Count > 0)
                    throw new BusinessRuleException(string.Join(" · ", lineIssues) + " · ระบบยังไม่ได้แตะอะไร", "REISSUE-LINE-INVALID", 400);
                var descById = requestedLines
                    .Where(l => l.Description != null)
                    .GroupBy(l => l.LineId)
                    .ToDictionary(g => g.Key, g => g.Last().Description!.Trim());

                // ── ใบใหม่ = สำเนาเฉพาะช่องที่ "ตามไป" (allowlist · V1-R7) แล้วตั้งเฉพาะช่องที่ต้องต่าง ──
                var now = DateTime.UtcNow;
                var userNotesBefore = SettlementPaidReissue.StripReplacementNote(old.Notes);
                var userNotesAfter = effective.Notes ?? userNotesBefore;
                var before = ReissueDocumentSnapshot.Of(old, oldLines) with { Notes = userNotesBefore };
                var candidate = new Document();
                SettlementPaidReissue.CopyDocumentForReissue(old, candidate);
                candidate.Id = Guid.NewGuid();
                candidate.CreatedAt = now;
                candidate.CreatedBy = old.ReissueRequestedBy ?? actor;
                candidate.UpdatedAt = null;
                candidate.UpdatedBy = actor;
                candidate.ContactId = contact.Id;
                candidate.BuyerDeclinedTaxInvoice = buyerDeclined;
                candidate.Status = SettlementPaidReissue.ReplacementStatus(old.Status);
                candidate.ReplacesDocumentId = old.Id;
                candidate.ReplacementReason = reason;
                candidate.ReplacementCarriesPostings = true;

                var newLines = new List<DocumentLine>();
                var lineMap = new Dictionary<Guid, Guid>();
                foreach (var ol in oldLines)
                {
                    var nl = new DocumentLine();
                    SettlementPaidReissue.CopyLineForReissue(ol, nl);
                    nl.Id = Guid.NewGuid();
                    nl.DocumentId = candidate.Id;
                    nl.CreatedAt = now;
                    nl.CreatedBy = actor;
                    if (descById.TryGetValue(ol.Id, out var desc)) nl.Description = desc;
                    newLines.Add(nl);
                    lineMap[ol.Id] = nl.Id;
                }

                // ── ตาข่าย "ใบใหม่เท่าใบเดิม" (คำตัดสินข้อ 9) — ตรวจผลของตัวคัดลอก allowlist (ช่องเงิน/ภาษี/วันที่ห้ามหล่น) ──
                var after = ReissueDocumentSnapshot.Of(candidate, newLines) with { Notes = userNotesAfter };
                var forbidden = SettlementPaidReissue.ForbiddenChanges(before, after);
                if (forbidden.Count > 0)
                    throw new BusinessRuleException(
                        "ยกเลิกและออกใบแทนเปลี่ยนได้เฉพาะผู้ซื้อ หมายเหตุ และคำบรรยายรายการ — " + string.Join(" · ", forbidden)
                        + " · ยอด/รายการ/อัตรา VAT ที่ต่าง ⇒ ออกใบลดหนี้/ใบเพิ่มหนี้อ้างใบนี้ (§86/9-10) · ระบบยังไม่ได้แตะอะไร",
                        "REISSUE-CONTENT-CHANGED", 409);
                var changed = SettlementPaidReissue.AllowedChanges(before, after);

                // ── V1-R6: ต้องมีคนที่สอง ⇒ บันทึกคำขอบนใบเดิมเท่านั้น (ไม่แตะเงิน/เลข/สถานะ) แล้วจบ ──
                if (control.Action == ReissueControlAction.RecordRequest)
                {
                    old.ReissueRequestedAt = now;
                    old.ReissueRequestedBy = actor;
                    old.ReissueRequestJson = SettlementPaidReissueRequestCodec.Serialize(
                        request with { Reason = reason, ConfirmPendingRequest = null, ConfirmRequestHash = null });
                    old.UpdatedAt = now;
                    old.UpdatedBy = actor;
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId,
                        UserId = Guid.TryParse(actor, out var requesterId) ? requesterId : (Guid?)null,
                        EntityType = nameof(Document),
                        EntityId = old.Id.ToString(),
                        Action = AuditAction.Update,
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            action = "void-and-reissue-requested",
                            ruleCode = control.RuleCode,
                            legalReference = "คำตัดสินรอบ 200 ข้อ 9 · review200-V1 R6 (SoD/วงเงินเซ็นหลายขั้น)",
                            reason,
                            contactId = contact.Id,
                            changed,
                        }),
                        Timestamp = now,
                    });
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return (Neo: (Document?)null, Receipts: newReceipts, Message: control.Reason);
                }

                neo = candidate;
                neo.Notes = SettlementPaidReissue.ComposeNotes(userNotesAfter, old.DocumentNumber, old.DocumentDate, reason);

                // เลขที่ใหม่ — วันที่เดิม (จุดความรับผิดเกิดไปแล้ว) · ชุดเลขตามกติกาเดียวกับตอนอนุมัติ · ใต้ advisory lock ของตัวออกเลข
                neo.DocumentNumber = await DocumentNumberGenerator.NextAsync(
                    _db, companyId, await ResolveNumberSeriesTypeAsync(companyId, neo), neo.DocumentDate);
                neo.InternalNotes = SettlementPaidReissue.ReplacementInternalNote(old.DocumentNumber, reason, changed);
                _db.Documents.Add(neo);
                _db.DocumentLines.AddRange(newLines);

                // ── ใบเดิม: Voided + ตราประทับใบแทน · ไม่มีรายการกลับบัญชี (ผลทางบัญชีย้ายไปใบใหม่ข้างล่าง) · คำขอที่ค้าง (ถ้ามี) ถูกล้าง ──
                var oldStatus = old.Status;
                var requestedBy = old.ReissueRequestedBy;
                var movedPaymentCount = eval.Payments.Count;
                old.Status = DocumentStatus.Voided;
                old.ReplacedByDocumentId = neo.Id;
                old.ReplacedAt = now;
                old.PaidAmount = 0m;
                old.BalanceDue = old.TotalAmount;
                old.AgingDays = null;
                old.AgingLastEvaluatedAt = now;
                old.ReissueRequestedAt = null;
                old.ReissueRequestedBy = null;
                old.ReissueRequestJson = null;
                old.UpdatedAt = now;
                old.UpdatedBy = actor;
                AppendInternalNote(old, SettlementPaidReissue.OriginalNote(neo.DocumentNumber, reason, movedPaymentCount));
                // e-Tax ของใบเดิมที่ยังไม่ถึงกรมสรรพากร (สร้าง/เซ็น/ผิดพลาด/ถูกปฏิเสธ) — ยกเลิกตามใบ · ถึงแล้ว (ส่ง/ตอบรับ) ถูกด่านปฏิเสธไปแล้ว (V1-R4:
                // ชุดสถานะเดียวกับใบเสร็จ — เดิมพลิก Submitted เป็น Voided แล้วส่ง e-Tax ใบใหม่ ⇒ RD ตอบรับได้ทั้งสองใบ)
                var oldEtax = await _db.EtaxInvoices
                    .Where(e => e.CompanyId == companyId && e.DocumentId == old.Id && e.Status != EtaxStatus.Voided
                        && !DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(e.Status))
                    .ToListAsync();
                foreach (var etax in oldEtax)
                {
                    etax.Status = EtaxStatus.Voided;
                    etax.VoidedAt = now;
                    etax.VoidReason = $"ยกเลิกและออกใบแทน {neo.DocumentNumber}";
                    etax.UpdatedAt = now;
                }
                await _db.SaveChangesAsync();   // ใบใหม่ต้องมีในฐานก่อนชี้ FK มาหา

                // ── ย้ายผลทางบัญชี: การรับชำระ (+ allocation) · JE ที่อ้างใบ · คู่จับของบรรทัดรอบโอน · สต็อก · ลิงก์ของโมดูล ──
                foreach (var p in eval.Payments)
                {
                    p.DocumentId = neo.Id;
                    p.UpdatedAt = now;
                    p.UpdatedBy = actor;
                }
                var moved = await RepointDocumentLinksAsync(companyId, old, neo, lineMap);

                // ── ใบเสร็จอัตโนมัติของการรับชำระที่ย้าย: ยกเลิก (ยังไม่ถึงกรมสรรพากร — ด่านตรวจแล้ว) แล้วออกใหม่อ้างใบใหม่ วันรับเงินเดิม ──
                foreach (var p in eval.Payments.Where(p => p.ReceiptDocumentId != null))
                {
                    var r = eval.Receipts.FirstOrDefault(x => x.Id == p.ReceiptDocumentId);
                    if (r == null) continue;
                    var wasIssued = r.Status != DocumentStatus.Draft;
                    var carriesVat = r.IsTaxInvoiceByLaw == true;
                    r.Status = DocumentStatus.Voided;
                    r.UpdatedAt = now;
                    r.UpdatedBy = actor;
                    AppendInternalNote(r, $"[VOID-REISSUE] ยกเลิกพร้อมใบต้นทาง {old.DocumentNumber} ที่ถูกยกเลิกและออกใบแทน {neo.DocumentNumber} — ออกใบเสร็จใหม่อ้างใบแทนแล้ว");
                    var pendingEtax = await _db.EtaxInvoices
                        .Where(e => e.CompanyId == companyId && e.DocumentId == r.Id && e.Status != EtaxStatus.Voided
                            && !DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(e.Status))
                        .ToListAsync();
                    foreach (var etax in pendingEtax)
                    {
                        etax.Status = EtaxStatus.Voided;
                        etax.VoidedAt = now;
                        etax.VoidReason = $"ยกเลิกพร้อมใบต้นทาง {old.DocumentNumber} (ยกเลิกและออกใบแทน)";
                        etax.UpdatedAt = now;
                    }
                    var nr = await CreateSettlementReceiptAsync(companyId, neo, p, actor, wasIssued, carriesVat);
                    p.ReceiptDocumentId = nr.Id;
                    newReceipts.Add(nr);
                    // V1-R9: PaymentIntent ที่ชี้ใบเสร็จเดิม ⇒ ใบเสร็จใหม่
                    moved["paymentIntentReceipts"] = moved.GetValueOrDefault("paymentIntentReceipts") + await _db.PaymentIntents
                        .Where(i => i.CompanyId == companyId && i.ReceiptDocumentId == r.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReceiptDocumentId, (Guid?)nr.Id));
                }

                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var actorId) ? actorId : (Guid?)null,
                    EntityType = nameof(Document),
                    EntityId = old.Id.ToString(),
                    Action = AuditAction.Update,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        documentNumber = old.DocumentNumber,
                        status = oldStatus.ToString(),
                        contactId = before.ContactId,
                    }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "void-and-reissue",
                        ruleCode = "RD-86/4-VOID-REISSUE",
                        legalReference = "ป.รัษฎากร §86/4 · คำตัดสินรอบ 200 ข้อ 9",
                        replacementId = neo.Id,
                        replacementNumber = neo.DocumentNumber,
                        contactId = neo.ContactId,
                        changed,
                        reason,
                        requestedBy,
                        approvedBy = actor,
                        movedPayments = movedPaymentCount,
                        moved,
                        reissuedReceipts = newReceipts.Select(x => x.DocumentNumber).ToList(),
                    }),
                    Timestamp = now,
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                return (Neo: neo, Receipts: newReceipts, Message: (string?)null);
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // คำขอถูกบันทึกรอคนที่สอง — คืนใบเดิม (หน้าเว็บแสดงแถบ "รอยืนยัน" จาก ReissueRequestedAt)
        if (outcome.Neo == null)
            return await GetDocumentAsync(companyId, documentId);

        // ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ) — หลัง commit และนอก execution strategy · ล้ม = ป้ายบนเอกสาร ไม่ย้อนการออกใบ
        await _issuedHooks.RunAsync(companyId, outcome.Neo);
        foreach (var nr in outcome.Receipts)
            await _issuedHooks.RunAsync(companyId, nr);
        return await GetDocumentAsync(companyId, outcome.Neo.Id);
    }

    /// <summary>รอบ 200 ทีม V1F (V1-R6) — ยกเลิกคำขอ "ยกเลิกและออกใบแทน" ที่ค้างรอคนที่สอง (ผู้ขอเอง หรือผู้มีสิทธิ์ยกเลิก+อนุมัติ — controller ตรวจสิทธิ์) ·
    /// ไม่มีคำขอ = บอกตรง ๆ (ไม่ใช่สำเร็จเงียบ) · audit ใน hash chain
    /// <para>รอบ 200 ทีม V1G (RV1F-7): ธุรกรรม + ล็อกแถว <c>FOR UPDATE</c> แล้วอ่านใหม่ใต้ล็อก — เดิมอ่านนอกธุรกรรม ⇒ ชนกับ "ยืนยัน" ที่กำลัง commit
    /// แล้วตอบ "ใบนี้ไม่ถูกแตะ" + audit ว่ายกเลิกคำขอ ทั้งที่ใบถูกแทนไปแล้ว · ใบที่ถูกยกเลิก/แทนไปแล้ว = บอกเลขใบแทนตรง ๆ</para></summary>
    public async Task<DocumentResponse> CancelReissueRequestAsync(Guid companyId, Guid documentId, string actor)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", documentId, companyId);
                var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
                if (doc.Status == DocumentStatus.Voided || doc.ReplacedByDocumentId != null)
                {
                    var replacementNo = doc.ReplacedByDocumentId is Guid rid
                        ? await _db.Documents.AsNoTracking().Where(d => d.Id == rid && d.CompanyId == companyId)
                            .Select(d => d.DocumentNumber).FirstOrDefaultAsync()
                        : null;
                    throw new BusinessRuleException(
                        replacementNo != null
                            ? $"ใบนี้ถูกยกเลิกและออกใบแทน {replacementNo} ไปแล้ว — คำขอถูกยืนยันแล้ว ยกเลิกคำขอไม่ได้ (แก้ต่อที่ใบแทน) · ระบบยังไม่ได้แตะอะไร"
                            : "ใบนี้ถูกยกเลิกไปแล้ว — ไม่มีคำขอให้ยกเลิก · ระบบยังไม่ได้แตะอะไร",
                        "REISSUE-ALREADY-REPLACED", 409);
                }
                if (doc.ReissueRequestedBy == null)
                    throw new BusinessRuleException("ใบนี้ไม่มีคำขอยกเลิกและออกใบแทนที่ค้างอยู่ (อาจถูกยืนยันหรือยกเลิกไปแล้ว) — โหลดหน้าเอกสารใหม่",
                        "REISSUE-NO-PENDING", 409);
                var now = DateTime.UtcNow;
                var requestedBy = doc.ReissueRequestedBy;
                var requestedAt = doc.ReissueRequestedAt;
                doc.ReissueRequestedAt = null;
                doc.ReissueRequestedBy = null;
                doc.ReissueRequestJson = null;
                doc.UpdatedAt = now;
                doc.UpdatedBy = actor;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var actorId) ? actorId : (Guid?)null,
                    EntityType = nameof(Document),
                    EntityId = doc.Id.ToString(),
                    Action = AuditAction.Update,
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "void-and-reissue-request-cancelled",
                        requestedBy,
                        requestedAt,
                    }),
                    Timestamp = now,
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });
        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>รอบ 200 ทีม V1G (คำตัดสินข้อ 49 · RV1F-5) — ภาพคำขอออกใบแทนฉบับเต็ม (ผู้ซื้อ/เลขภาษี/สาขา/ที่อยู่ เดิม→ใหม่ · หมายเหตุ · คำบรรยาย) + hash ·
    /// <b>ตัวเดียว</b>ของหน้าจอ (<c>GetDocumentAsync</c>) และการยืนยัน (ใต้ล็อก) · ผู้ซื้อของบริษัทนี้เท่านั้น · null = ไม่มีคำขอ/อ่านไม่ได้</summary>
    private async Task<ReissueRequestView?> BuildReissueRequestViewAsync(Guid companyId, Document doc)
    {
        var req = SettlementPaidReissueRequestCodec.Parse(doc.ReissueRequestJson);
        if (req == null) return null;
        var oldContact = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == companyId);
        var newContactId = req.ContactId ?? doc.ContactId;
        var newContact = newContactId == doc.ContactId ? oldContact
            : await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == newContactId && c.CompanyId == companyId);
        var lines = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == doc.Id && !l.IsDeleted)
            .Select(l => new { l.Id, l.LineOrder, l.Description })
            .ToListAsync();
        return SettlementPaidReissueRequestView.Build(doc.ReissueRequestedBy, doc.ReissueRequestedAt, req.Reason,
            PartySnapshot(oldContact), PartySnapshot(newContact), newContactId != doc.ContactId,
            SettlementPaidReissue.StripReplacementNote(doc.Notes), req.Notes,
            lines.Select(l => (l.Id, l.LineOrder, (string?)l.Description)),
            (req.Lines ?? new List<ReissueLineDescription>()).Select(l => (l.LineId, l.Description)));
    }

    /// <summary>ผู้ซื้อ ณ ตอนนี้ (ชื่อ · เลขภาษี · สาขา · ที่อยู่ภาษาไทยผ่าน resolver กลาง) — ไม่พบ = ช่องว่างทั้งหมด (หน้าจอแสดงว่าไม่พบ)</summary>
    private static ReissuePartySnapshot PartySnapshot(Contact? c)
        => c == null
            ? new ReissuePartySnapshot(null, null, null, null)
            : new ReissuePartySnapshot(c.Name, c.TaxId, c.BranchCode,
                ThaiAddressFormatter.ResolvePartyAddress(false, c.AddressEn, c.Address, c.BuildingNumber, c.BuildingName, c.Moo,
                    c.StreetName, c.SubDistrict, c.District, c.Province, c.PostalCode));

    /// <summary>ชี้ทุกอย่างที่อ้างใบเดิม "ในฐานะเจ้าของผลทางบัญชี/ใบขายของรายการนั้น" ไปใบใหม่ (บริษัทนี้เท่านั้น) — คืนจำนวนแถวต่อชนิด (ลง audit)
    /// <para>ไม่ย้าย (ประวัติของกระดาษใบเดิม): e-Tax · ประวัติแก้ไข/อนุมัติ/ลายเซ็น/อีเมล · แถวรายงานภาษี · ใบทวงหนี้ · log การเชื่อมต่อ/OCR ·
    /// ส่วนที่ด่านปฏิเสธไปแล้ว (มัดจำ · 50 ทวิ · เลื่อนภาษี) ไม่มีให้ย้าย · <c>JournalEntry.Reference</c> คงเลขใบเดิม (ตัวเลือก JE อ่านช่องนั้น) —
    /// ต่อท้ายคำบรรยายแทน (V1-R9)</para></summary>
    /// <param name="lineMap">Id บรรทัดเดิม → Id บรรทัดใหม่ (ลิงก์รายบรรทัดของโครงการ)</param>
    private async Task<Dictionary<string, int>> RepointDocumentLinksAsync(Guid companyId, Document old, Document neo,
        IReadOnlyDictionary<Guid, Guid> lineMap)
    {
        var oldId = old.Id;
        var newId = neo.Id;
        var r = new Dictionary<string, int>();
        r["paymentAllocations"] = await _db.PaymentAllocations
            .Where(a => a.CompanyId == companyId && a.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.DocumentId, newId));
        // JE: ชี้ใบใหม่ + ต่อท้ายคำบรรยาย (ผู้สอบบัญชีเห็นว่า JE นี้เป็นของใบแทน ไม่ใช่ JE ของใบที่ยกเลิกโดยไม่มีรายการกลับ)
        var journals = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId == oldId)
            .ToListAsync();
        foreach (var j in journals)
        {
            j.SourceDocumentId = newId;
            j.Description = SettlementPaidReissue.JournalDescriptionAfterMove(j.Description, old.DocumentNumber, neo.DocumentNumber);
        }
        r["journalEntries"] = journals.Count;
        r["settlementLines"] = await _db.SettlementLines
            .Where(l => l.CompanyId == companyId && l.MatchedDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.MatchedDocumentId, (Guid?)newId));
        r["stockMovements"] = await _db.StockMovements
            .Where(m => m.CompanyId == companyId && m.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.DocumentId, (Guid?)newId));
        r["whtCreditsReceived"] = await _db.WhtCreditsReceived
            .Where(w => w.CompanyId == companyId && w.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.DocumentId, (Guid?)newId));
        r["postDatedChecks"] = await _db.PostDatedChecks
            .Where(c => c.CompanyId == companyId && c.SourceDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.SourceDocumentId, (Guid?)newId));
        r["projectCostEntries"] = await _db.ProjectCostEntries
            .Where(p => p.CompanyId == companyId && p.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.DocumentId, (Guid?)newId));
        // V1-R9: ลิงก์รายบรรทัดของโครงการ — ชี้บรรทัดใหม่ (เดิมย้ายแค่ DocumentId แล้ว DocumentLineId ชี้บรรทัดของใบที่ยกเลิก)
        var projectLines = 0;
        foreach (var (oldLineId, newLineId) in lineMap)
            projectLines += await _db.ProjectCostEntries
                .Where(p => p.CompanyId == companyId && p.DocumentLineId == oldLineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DocumentLineId, (Guid?)newLineId));
        r["projectCostEntryLines"] = projectLines;
        r["reconciliationItems"] = await _db.ReconciliationGroupItems
            .Where(i => i.ItemType == ReconciliationItemType.Document && i.ItemId == oldId
                && _db.ReconciliationGroups.Any(g => g.Id == i.GroupId && g.CompanyId == companyId))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ItemId, newId));
        r["bankSuggestions"] = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && t.SuggestedDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.SuggestedDocumentId, (Guid?)newId));
        // V1-R9: การจ่ายผ่าน gateway ของใบนี้ — ใบลดหนี้ของการคืนเงินต้องอ้างใบแทน (GatewayRefundService นับใบลดหนี้ตาม SourceId) ·
        // ผังลูกหนี้ของการคืนเงินอ่านผู้ซื้อจากเอกสารต้นทาง (ใบแทน = ผู้ซื้อใหม่)
        r["paymentIntents"] = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.SourceId, newId));
        r["paymentIntentReceipts"] = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId && i.ReceiptDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReceiptDocumentId, (Guid?)newId));
        // ลิงก์ "ใบขายของรายการนี้" ของโมดูล — ตามใบใหม่ (ไม่งั้นโมดูลชี้ใบที่ยกเลิกแล้ว)
        r["posOrders"] = await _db.PosOrders
            .Where(o => o.CompanyId == companyId && o.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.DocumentId, (Guid?)newId));
        r["lodgingDeposit"] = await _db.LodgingReservations
            .Where(x => x.CompanyId == companyId && x.DepositDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DepositDocumentId, (Guid?)newId));
        r["lodgingSecurityDeposit"] = await _db.LodgingReservations
            .Where(x => x.CompanyId == companyId && x.SecurityDepositDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityDepositDocumentId, (Guid?)newId));
        r["lodgingFinal"] = await _db.LodgingReservations
            .Where(x => x.CompanyId == companyId && x.FinalDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.FinalDocumentId, (Guid?)newId));
        r["siteOrders"] = await _db.SiteOrders
            .Where(x => x.CompanyId == companyId && x.ErpDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ErpDocumentId, (Guid?)newId));
        r["siteBookings"] = await _db.SiteBookings
            .Where(x => x.CompanyId == companyId && x.ErpDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ErpDocumentId, (Guid?)newId));
        r["siteFormSubmissions"] = await _db.SiteFormSubmissions
            .Where(x => x.CompanyId == companyId && x.ErpDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ErpDocumentId, (Guid?)newId));
        r["cmsLeads"] = await _db.CmsLeads
            .Where(x => x.CompanyId == companyId && x.ErpDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ErpDocumentId, (Guid?)newId));
        r["usageEvents"] = await _db.UsageEvents
            .Where(x => x.CompanyId == companyId && x.BilledDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.BilledDocumentId, (Guid?)newId));
        r["timeEntries"] = await _db.TimeEntries
            .Where(x => x.CompanyId == companyId && x.InvoiceDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.InvoiceDocumentId, (Guid?)newId));
        return r;
    }

    public async Task<List<EtaxCancelRequiredItem>> ListEtaxCancelRequiredAsync(Guid companyId)
        => await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.EtaxCancelRequiredAt != null)
            .OrderByDescending(d => d.EtaxCancelRequiredAt)
            .Select(d => new EtaxCancelRequiredItem(d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount,
                d.EtaxCancelRequiredAt!.Value, d.EtaxCancelRequiredReason))
            .ToListAsync();

    /// <summary>
    /// รอบ 200 ทีม V1F (V1-R3) · แก้รอบ V1G (คำตัดสินข้อ 46–48 · RV1F-1/2/3/4/8/9/10) — **"บันทึกการยกเลิกทาง e-Tax"** ของใบเสร็จที่ติดธง
    /// <para>ตัวตัดสิน <see cref="DocumentVoidPreconditions.EtaxCancellationResolution"/> แยกสองทาง (ระบบไม่ประทับสถานะของกรมสรรพากรเอง):</para>
    /// <para>(ก) <b>ยกเลิกทาง e-Tax สำเร็จ</b> ⇒ ยกเลิกใบเสร็จ <b>คงแสดงเป็น Voided</b> (ไม่ซ่อน — RV1F-10) · ใบที่ถึงกรมสรรพากรต้องมีเลขอ้างอิง +
    /// <b>ไฟล์หลักฐานที่แนบที่ใบเสร็จนี้</b> (controller เดินด่านไฟล์แนบตัวเดียวแล้ว — RV1F-4) · ด่านงวดตรวจ<b>วันที่ของรายการที่จะเขียนจริง</b>: เดือนของใบเสร็จ
    /// (แถว ภ.พ.30 ที่หายไป) · เดือนของ JE ย้ายภาษี (ตัวกลับ) · เดือนของวันรับเงินที่เหลือ (ย้ายภาษีใหม่/ใบกำกับใหม่) — ตัวตัดสิน
    /// <see cref="DocumentVoidPreconditions.EtaxCancellationFollowUp"/> · มีการรับชำระที่ยังมีผล ⇒ <b>ออกใบกำกับ ณ วันรับเงินในธุรกรรมเดียวกัน</b> (RV1F-3)</para>
    /// <para>(ข) <b>ออกใบลดหนี้แล้ว</b> ⇒ ผูกใบลดหนี้ในระบบนี้ (ต้องมีจริง · ห้ามรับแค่เลขที่) · ใบเสร็จเดิมคงมีผล · ภาษีขายลดในเดือนของใบลดหนี้ ·
    /// ไม่แตะภาษีขายของเดือนเดิม (RV1F-1)</para>
    /// <para>ล็อกใบต้นทางก่อนใบเสร็จ (ลำดับเดียวกับเส้นรับชำระ — RV1F-8) · audit ใน hash chain พร้อมป้ายหลักฐานที่ตรงความจริง (RV1F-9)</para>
    /// </summary>
    public async Task<EtaxCancellationResult> ResolveEtaxCancellationAsync(Guid companyId, Guid documentId,
        ResolveEtaxCancellationRequest request, string actor)
    {
        var path = request.Path ?? EtaxCancellationPath.CancelledAtRd;
        Guid? sourceId = null;
        Document? issuedReceipt = null;
        var message = "";
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            sourceId = null;
            issuedReceipt = null;
            message = "";
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // RV1F-8: ล็อกใบต้นทางก่อนใบเสร็จ — ลำดับเดียวกับ CreatePaymentAsync (ล็อกใบต้นทาง FOR UPDATE) ⇒ รับชำระใหม่กับปิดธงไม่แทรกกัน
                // (เดิมล็อกแค่ใบเสร็จแล้วอ่านยอดรับของใบต้นทางไม่ล็อก ⇒ ถอยภาษีทั้งที่มีเงินเข้ามาใหม่)
                var srcIdToLock = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == documentId && d.CompanyId == companyId)
                    .Select(d => d.RelatedDocumentId).FirstOrDefaultAsync();
                if (srcIdToLock is Guid lockSrcId)
                    await _db.Database.ExecuteSqlRawAsync(
                        "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", lockSrcId, companyId);
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", documentId, companyId);
                var rcpt = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
                var src = rcpt.RelatedDocumentId is Guid srcId0
                    ? await _db.Documents.FirstOrDefaultAsync(d => d.Id == srcId0 && d.CompanyId == companyId)
                    : null;

                // ── ข้อเท็จจริงของตัวตัดสิน ──
                var etax = (await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, new[] { rcpt.Id })).GetValueOrDefault(rcpt.Id);
                var anyEtaxRowVoided = await _db.EtaxInvoices.AsNoTracking()
                    .AnyAsync(e => e.CompanyId == companyId && e.DocumentId == rcpt.Id && e.Status == EtaxStatus.Voided);
                // RV1F-4 (ข้อ 46): ไฟล์หลักฐานต้องเป็นไฟล์แนบของใบเสร็จนี้จริง (บริษัทนี้ · ยังไม่ถูกลบ) — ด่านสิทธิ์อ่านไฟล์อยู่ที่ controller (IAttachmentAccessGate)
                var evidenceAttached = request.EvidenceAttachmentId is Guid evidenceId
                    && await _db.FileAttachments.AsNoTracking().AnyAsync(a => a.Id == evidenceId && a.CompanyId == companyId && !a.IsDeleted
                        && a.EntityType == "Document" && a.EntityId == rcpt.Id);
                Document? creditNote = null;
                EtaxCreditNoteFact? creditNoteFact = null;
                if (path == EtaxCancellationPath.CreditNote && request.CreditNoteDocumentId is Guid cnId)
                {
                    creditNote = await _db.Documents.FirstOrDefaultAsync(d => d.Id == cnId && d.CompanyId == companyId && !d.IsDeleted);
                    var usedBy = creditNote == null ? null : await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && d.EtaxCancelledByCreditNoteId == creditNote.Id && d.Id != rcpt.Id)
                        .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
                    creditNoteFact = creditNote == null
                        ? new EtaxCreditNoteFact(false, null, DocumentType.CreditNote, DocumentStatus.Draft, false, false, 0m, null)
                        : new EtaxCreditNoteFact(true, creditNote.DocumentNumber, creditNote.DocumentType, creditNote.Status,
                            creditNote.RelatedDocumentId != null
                                && (creditNote.RelatedDocumentId == rcpt.Id || creditNote.RelatedDocumentId == rcpt.RelatedDocumentId),
                            creditNote.ContactId == rcpt.ContactId, creditNote.VatAmount, usedBy);
                }
                var verdict = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(
                    rcpt.EtaxCancelRequiredAt != null, etax, anyEtaxRowVoided, path, request.Reason, request.RdCancellationReference,
                    evidenceAttached, creditNoteFact, rcpt.VatAmount, src?.PaidAmount ?? 0m));
                if (!verdict.Allowed)
                    throw new BusinessRuleException(verdict.Reason ?? "บันทึกการยกเลิกทาง e-Tax ไม่ได้", "ETAX-CANCEL-EVIDENCE", 409);

                var now = DateTime.UtcNow;
                var flaggedAt = rcpt.EtaxCancelRequiredAt;
                var flagReason = rcpt.EtaxCancelRequiredReason;
                var reason = request.Reason!.Trim();
                var reference = request.RdCancellationReference?.Trim();
                var evidenceLabel = DocumentVoidPreconditions.EvidenceLabel(verdict.Evidence);
                EtaxCancelFollowUpPlan? plan = null;
                sourceId = src?.Id;

                if (path == EtaxCancellationPath.CreditNote)
                {
                    // ── (ข) ใบลดหนี้ในระบบ: ใบเสร็จเดิมคงมีผล · ภาษีขายเดือนเดิมไม่ถูกแตะ · ใบลดหนี้ลดภาษีในเดือนของตัวเอง (§86/10) ──
                    rcpt.EtaxCancelledByCreditNoteId = creditNote!.Id;
                    rcpt.EtaxCancelRequiredAt = null;
                    rcpt.EtaxCancelRequiredReason = null;
                    rcpt.UpdatedAt = now;
                    rcpt.UpdatedBy = actor;
                    AppendInternalNote(rcpt, $"{EtaxReissueReview.ResolvedMarker} ปิดธงด้วยใบลดหนี้ {creditNote.DocumentNumber} — {evidenceLabel} — {reason}");
                    message = $"บันทึกแล้ว — ใบเสร็จ {rcpt.DocumentNumber} ยังมีผล (ใบกำกับเดิมที่กรมสรรพากรมี) · ภาษีขายลดด้วยใบลดหนี้ "
                        + $"{creditNote.DocumentNumber} ในเดือนของใบลดหนี้ · ภาษีขายของเดือนเดิมไม่ถูกแตะ";
                }
                else
                {
                    // ── (ก) ยกเลิกทาง e-Tax สำเร็จ: ด่านงวดของทุกวันที่ที่จะเขียนจริง ก่อนแตะอะไร ──
                    if (_taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, rcpt.Id))
                        throw new BusinessRuleException(
                            "ใบเสร็จนี้อยู่ในรายงานภาษีที่ยื่นและล็อกแล้ว — ปลดล็อกรายงาน (ผู้ดูแล) หรือใช้ “Reject & Reverse” ของรายงานก่อน · "
                            + "ถ้าการยกเลิกเกิดหลังยื่นแบบ ให้ออกใบลดหนี้อ้างใบต้นทางในเดือนปัจจุบันแล้วเลือกทาง “ออกใบลดหนี้แล้ว” · ระบบยังไม่ได้แตะอะไร",
                            "ETAX-CANCEL-FILING-LOCKED", 409);
                    // แถว ภ.พ.30 ของใบเสร็จ (เดือนของใบเสร็จ) จะหายไป ⇒ งวดบัญชี/ภ.พ.30 เดือนนั้นต้องเปิด (ตัวเดียวกับด่านถอย/ย้ายภาษีขาย)
                    if (await OutputVatPeriodLockReasonAsync(companyId, rcpt.DocumentDate) is string rcptLock)
                        throw new BusinessRuleException(
                            $"ใบเสร็จนี้อยู่ใน{rcptLock} — ยกเลิกใบเสร็จแล้วแถวภาษีขายของเดือนนั้นจะหายย้อนหลัง · ทางไปต่อ: ออกใบลดหนี้อ้างใบต้นทางในเดือนปัจจุบัน "
                            + "แล้วเลือกทาง “ออกใบลดหนี้แล้ว” หรือเปิดงวด/Reject & Reverse รายงานเดือนนั้นก่อน · ระบบยังไม่ได้แตะอะไร",
                            "ETAX-CANCEL-PERIOD-LOCKED", 409);

                    if (src != null)
                    {
                        // การรับชำระที่ยังมีผลของใบต้นทาง (ตรง + ผ่านการจัดสรรหลายใบ) พร้อมใบเสร็จอัตโนมัติของแต่ละรายการ
                        var allocPaymentIds = await _db.PaymentAllocations.AsNoTracking()
                            .Where(a => a.CompanyId == companyId && a.DocumentId == src.Id && !a.IsDeleted)
                            .Select(a => a.PaymentId).Distinct().ToListAsync();
                        var livePayments = await _db.Payments.AsNoTracking()
                            .Where(p => p.CompanyId == companyId && !p.IsDeleted && (p.DocumentId == src.Id || allocPaymentIds.Contains(p.Id)))
                            .Select(p => new { p.Id, p.PaymentDate, p.ReceiptDocumentId })
                            .ToListAsync();
                        var payIds = livePayments.Select(p => p.Id).ToList();
                        var withAllocations = (await _db.PaymentAllocations.AsNoTracking()
                                .Where(a => a.CompanyId == companyId && !a.IsDeleted && payIds.Contains(a.PaymentId))
                                .Select(a => a.PaymentId).Distinct().ToListAsync())
                            .ToHashSet();
                        var payReceiptIds = livePayments.Where(p => p.ReceiptDocumentId != null)
                            .Select(p => p.ReceiptDocumentId!.Value).Distinct().ToList();
                        var payReceipts = payReceiptIds.Count == 0
                            ? new Dictionary<Guid, (string Number, decimal Vat)>()
                            : (await _db.Documents.AsNoTracking()
                                    .Where(d => d.CompanyId == companyId && payReceiptIds.Contains(d.Id) && !d.IsDeleted
                                        && d.Status != DocumentStatus.Voided)
                                    .Select(d => new { d.Id, d.DocumentNumber, d.VatAmount }).ToListAsync())
                                .ToDictionary(d => d.Id, d => (Number: d.DocumentNumber, Vat: d.VatAmount));
                        var payReceiptEtax = await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, payReceipts.Keys.ToList());
                        var liveFacts = livePayments.Select(p =>
                        {
                            var hasReceipt = p.ReceiptDocumentId is Guid prid && payReceipts.ContainsKey(prid);
                            var info = hasReceipt ? payReceipts[p.ReceiptDocumentId!.Value] : (Number: "", Vat: 0m);
                            return new EtaxCancelLivePayment(p.Id, p.PaymentDate, withAllocations.Contains(p.Id),
                                hasReceipt ? p.ReceiptDocumentId : null, hasReceipt ? info.Number : null, info.Vat,
                                hasReceipt && DocumentVoidPreconditions.EtaxReachedRd(payReceiptEtax.GetValueOrDefault(p.ReceiptDocumentId!.Value)));
                        }).ToList();
                        var firstPaymentDate = liveFacts.Count == 0 ? (DateTime?)null : liveFacts.Min(p => p.PaymentDate);
                        plan = DocumentVoidPreconditions.EtaxCancellationFollowUp(new EtaxCancelFollowUpFacts(
                            src.DocumentType, src.VatAmount, src.BalanceDue <= 0.005m, src.OutputVatDueAt,
                            await LiveVatReceiptExistsAsync(companyId, src.Id, exceptReceiptId: rcpt.Id),
                            liveFacts,
                            src.OutputVatDueAt != null ? await ReclassLockReasonAsync(companyId, src.Id) : null,
                            firstPaymentDate is DateTime fpd ? await OutputVatPeriodLockReasonAsync(companyId, fpd) : null));
                        if (!plan.Allowed)
                            throw new BusinessRuleException(plan.Reason ?? "บันทึกการยกเลิกทาง e-Tax ไม่ได้ — ภาษีขายของใบต้นทางถอย/ย้ายไม่ได้",
                                "ETAX-CANCEL-FOLLOWUP-BLOCKED", 409);
                        // ออกใบกำกับ ณ วันรับเงินในคำสั่งเดียวกัน ⇒ ผู้กดต้องมีสิทธิ์อนุมัติใบเสร็จ (ใบร่าง = ช่วงที่การขายไม่มีใบกำกับ)
                        if (plan.IssueVatReceiptForPaymentId != null && _permissionService != null && Guid.TryParse(actor, out var actorUid)
                            && !await DocumentPermissionHelper.CanApproveAsync(_permissionService, companyId, actorUid, DocumentType.Receipt))
                            throw new BusinessRuleException(
                                "การรับชำระที่ยังมีผลต้องได้ใบกำกับภาษี ณ วันรับเงินในคำสั่งเดียวกัน — ผู้กดต้องมีสิทธิ์อนุมัติใบเสร็จรับเงิน · "
                                + "ให้ผู้มีสิทธิ์เป็นผู้กด · ระบบยังไม่ได้แตะอะไร", "ETAX-CANCEL-RECEIPT-APPROVE", 403);
                    }

                    rcpt.EtaxCancelRequiredAt = null;
                    rcpt.EtaxCancelRequiredReason = null;
                    rcpt.UpdatedBy = actor;
                    AppendInternalNote(rcpt, $"{EtaxReissueReview.ResolvedMarker} ยกเลิกทาง e-Tax แล้ว — {evidenceLabel}"
                        + (string.IsNullOrEmpty(reference) ? "" : $" · อ้างอิง {reference}") + $" — {reason}");
                    // ยกเลิกใบเสร็จ — คงแสดงเป็น Voided (RV1F-10: เลขใบกำกับที่ออกจริงต้องมองเห็นว่าถูกยกเลิก) · แถว e-Tax ที่ถึงกรมสรรพากรแล้วไม่ถูกแตะ
                    await ApplyAutoReceiptOnPaymentVoidAsync(companyId, new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null), rcpt,
                        rcpt.DocumentNumber, keepVisible: true);

                    var parts = new List<string> { $"บันทึกการยกเลิกทาง e-Tax แล้ว — ใบเสร็จ {rcpt.DocumentNumber} ถูกยกเลิก (คงแสดงเป็น “ยกเลิก”)" };
                    if (src != null && plan != null)
                    {
                        if (plan.UndoReclass)
                        {
                            await UndoUndueOutputVatReclassAsync(companyId, src, "ยกเลิกใบเสร็จทาง e-Tax " + rcpt.DocumentNumber);
                            parts.Add($"ถอยภาษีขายของ {src.DocumentNumber} ในเดือนที่ตั้งรายการแล้ว");
                        }
                        if (plan.VoidPlainReceiptId is Guid plainId
                            && await _db.Documents.FirstOrDefaultAsync(d => d.Id == plainId && d.CompanyId == companyId) is Document plain)
                        {
                            AppendInternalNote(plain, $"[ETAX-CANCEL-REISSUE] ยกเลิกเพราะใบกำกับ {rcpt.DocumentNumber} ถูกยกเลิกทาง e-Tax — "
                                + "ออกใบกำกับ ณ วันรับเงินแทนใบรับนี้");
                            await ApplyAutoReceiptOnPaymentVoidAsync(companyId, new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null), plain,
                                plain.DocumentNumber, keepVisible: true);
                        }
                        if (plan.IssueVatReceiptForPaymentId is Guid payId)
                        {
                            var pay = await _db.Payments.FirstAsync(p => p.Id == payId && p.CompanyId == companyId);
                            var nr = await CreateSettlementReceiptAsync(companyId, src, pay, actor, issueApproved: true, carryVatFromSource: true);
                            pay.ReceiptDocumentId = nr.Id;
                            pay.UpdatedAt = now;
                            issuedReceipt = nr;
                            parts.Add($"ออกใบกำกับภาษี/ใบเสร็จ {nr.DocumentNumber} ณ วันรับเงิน {pay.PaymentDate:dd/MM/yyyy} ให้การรับชำระที่ยังมีผลแล้ว");
                        }
                        if (plan.ReclassAt is DateTime reclassAt)
                        {
                            await _db.SaveChangesAsync();   // ตัวกลับ + ใบเสร็จใหม่ต้องอยู่ในฐานก่อนคำนวณยอด 21913 ที่ต้องย้าย
                            await TryReclassifyUndueOutputVatAsync(companyId, src.Id, reclassAt, actor, throwOnFailure: true);
                            parts.Add($"ภาษีขายของ {src.DocumentNumber} ถึงกำหนด ณ วันรับเงินจริง {reclassAt:dd/MM/yyyy}"
                                + (plan.IssueVatReceiptForPaymentId == null ? " (รับชำระหลายงวด/บางส่วน — รายงานที่ใบต้นทางตามกติกาเดิม)" : ""));
                        }
                    }
                    if (src != null) parts.Add($"ใบต้นทาง {src.DocumentNumber} ยกเลิก/แก้ไขต่อได้");
                    message = string.Join(" · ", parts);
                }

                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var actorId) ? actorId : (Guid?)null,
                    EntityType = nameof(Document),
                    EntityId = rcpt.Id.ToString(),
                    Action = AuditAction.Update,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { etaxCancelRequiredAt = flaggedAt, etaxCancelRequiredReason = flagReason }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "etax-cancellation-recorded",
                        ruleCode = path == EtaxCancellationPath.CreditNote ? "RD-86/10-ETAX-CANCEL-CN" : "RD-ETAX-CANCEL-EVIDENCE",
                        legalReference = "คำตัดสินรอบ 200 ข้อ 11 · 46 · 47 · 48 · review200-round2-V1F",
                        path = path.ToString(),
                        evidence = verdict.Evidence.ToString(),
                        evidenceLabel,
                        rdCancellationReference = reference,
                        evidenceAttachmentId = evidenceAttached ? request.EvidenceAttachmentId : null,
                        creditNoteId = creditNote?.Id,
                        creditNoteNumber = creditNote?.DocumentNumber,
                        effectiveEtax = etax?.ToString(),
                        reason,
                        receiptVoided = path == EtaxCancellationPath.CancelledAtRd,
                        undoReclass = plan?.UndoReclass ?? false,
                        reclassAt = plan?.ReclassAt,
                        issuedReceipt = issuedReceipt?.DocumentNumber,
                    }),
                    Timestamp = now,
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });
        // ใบกำกับ ณ วันรับเงินที่เพิ่งออก — e-Tax อัตโนมัติหลัง commit และนอก execution strategy (เหมือนเส้นรับชำระ/ออกใบแทน)
        if (issuedReceipt != null)
            await _issuedHooks.RunAsync(companyId, issuedReceipt);
        return new EtaxCancellationResult(sourceId is Guid sid ? await GetDocumentAsync(companyId, sid) : null, message);
    }

    /// <summary>รอบ 200 ทีม V1G (ข้อ 47) — ใบลดหนี้ในระบบที่เลือกปิดธงทาง (ข) ได้: อ้างใบเสร็จ/ใบต้นทาง · ผู้ซื้อเดียวกัน · ออกแล้ว · ยังไม่เคยใช้ปิดธงใบอื่น ·
    /// ตัวเลือกบนหน้าจอเท่านั้น — ตัวตัดสินจริงคือ <see cref="DocumentVoidPreconditions.EtaxCancellationResolution"/> ตอนกด · tenant</summary>
    public async Task<List<EtaxCancellationCreditNoteOption>> ListEtaxCancellationCreditNotesAsync(Guid companyId, Guid receiptId)
    {
        var rcpt = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == receiptId && d.CompanyId == companyId)
            .Select(d => new { d.Id, d.RelatedDocumentId, d.ContactId })
            .FirstOrDefaultAsync() ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var used = _db.Documents.Where(d => d.CompanyId == companyId && d.EtaxCancelledByCreditNoteId != null)
            .Select(d => d.EtaxCancelledByCreditNoteId!.Value);
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.CreditNote
                && d.RelatedDocumentId != null && (d.RelatedDocumentId == rcpt.Id || d.RelatedDocumentId == rcpt.RelatedDocumentId)
                && d.ContactId == rcpt.ContactId
                && !DocumentStatusRules.NotIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided
                && !used.Contains(d.Id))
            .OrderByDescending(d => d.DocumentDate)
            .Select(d => new EtaxCancellationCreditNoteOption(d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.VatAmount))
            .ToListAsync();
    }

    /// <summary>
    /// รอบ 200 ทีม V1G (คำตัดสินข้อ 44) — <b>รายงานอ่านอย่างเดียวให้นักบัญชีตรวจ</b> (ไม่แก้อัตโนมัติ — งวดที่อาจยื่นแล้วห้ามแก้เงียบ):
    /// (1) ใบเสร็จถือ VAT ที่ติดธงแล้วภาษีขายของใบต้นทางถูกถอยไปแล้ว (ข้อบกพร่อง V1-R2 เดิม) · (2) ใบเสร็จที่ปิดธงด้วยเส้นของรอบ V1F (ยกเลิก + ซ่อน —
    /// หลักฐานอาจเป็น "เลขที่ใบลดหนี้" · RV1F-1) · (3) ใบแทนที่พาช่องของใบเดิมมาเกิน (ออกก่อนตัวคัดลอก allowlist) · ตัวจำแนก <see cref="EtaxReissueReview"/> · tenant
    /// </summary>
    public async Task<EtaxReissueReviewReport> GetEtaxReissueReviewAsync(Guid companyId)
    {
        var flagged = await (
            from r in _db.Documents.AsNoTracking()
            join s in _db.Documents.AsNoTracking() on r.RelatedDocumentId equals s.Id
            where r.CompanyId == companyId && s.CompanyId == companyId && r.EtaxCancelRequiredAt != null
                  && r.Status != DocumentStatus.Voided && r.VatAmount > 0.005m
            select new
            {
                r.Id, r.DocumentNumber, r.DocumentDate, r.VatAmount, r.EtaxCancelRequiredAt,
                SourceId = s.Id, SourceNumber = s.DocumentNumber, SourceType = s.DocumentType, s.OutputVatDueAt,
            }).ToListAsync();
        var undone = flagged
            .Where(x => EtaxReissueReview.FlaggedReceiptVatUndone(true, x.VatAmount, true, x.SourceType, x.OutputVatDueAt))
            .Select(x => new EtaxReviewReceiptRow(x.Id, x.DocumentNumber, x.DocumentDate, x.VatAmount, x.SourceId, x.SourceNumber,
                x.EtaxCancelRequiredAt,
                "ใบกำกับนี้ยังมีผล (ติดธงต้องยกเลิกทาง e-Tax) แต่ภาษีขายของใบต้นทางถูกถอยไปแล้ว — ภ.พ.30 ของเดือนที่รับเงินอาจขาดภาษีขายนี้"))
            .ToList();

        var marker = EtaxReissueReview.ResolvedByV1FMarker;
        var resolvedOld = await (
            from r in _db.Documents.IgnoreQueryFilters().AsNoTracking()
            join s0 in _db.Documents.IgnoreQueryFilters().AsNoTracking() on r.RelatedDocumentId equals s0.Id into sj
            from s in sj.DefaultIfEmpty()
            where r.CompanyId == companyId && r.IsDeleted && r.Status == DocumentStatus.Voided && r.IsSettlementReceipt
                  && r.InternalNotes != null && r.InternalNotes.Contains(marker)
            select new { r.Id, r.DocumentNumber, r.DocumentDate, r.VatAmount, SourceId = (Guid?)s.Id, SourceNumber = s.DocumentNumber })
            .ToListAsync();
        var resolvedRows = resolvedOld
            .Select(x => new EtaxReviewReceiptRow(x.Id, x.DocumentNumber, x.DocumentDate, x.VatAmount, x.SourceId, x.SourceNumber, null,
                "ปิดธงด้วยเส้นของรอบก่อน (ยกเลิกและซ่อนใบเสร็จ) — ตรวจว่าทางกรมสรรพากร “ยกเลิก” จริง หรือ “ออกใบลดหนี้” · ถ้าเป็นใบลดหนี้: "
                + "บันทึกใบลดหนี้ในระบบ และภาษีขายของเดือนเดิมถูกลดย้อนหลังไปแล้ว (ปรึกษาผู้ทำบัญชี)"))
            .ToList();

        var replacements = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ReplacementCarriesPostings && d.ReplacesDocumentId != null)
            .ToListAsync();
        var originalIds = replacements.Select(d => d.ReplacesDocumentId!.Value).Distinct().ToList();
        var originals = await _db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.CompanyId == companyId && originalIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id);
        var excessRows = new List<EtaxReviewReplacementRow>();
        foreach (var neo in replacements)
        {
            if (!originals.TryGetValue(neo.ReplacesDocumentId!.Value, out var orig)) continue;
            var excess = EtaxReissueReview.CarriedExcess(orig, neo);
            if (excess.Count > 0)
                excessRows.Add(new EtaxReviewReplacementRow(neo.Id, neo.DocumentNumber, orig.Id, orig.DocumentNumber, orig.ReplacedAt, excess));
        }
        return new EtaxReissueReviewReport(undone, resolvedRows, excessRows);
    }

    /// <summary>ใบต้นทางยังมีใบเสร็จ "ถือ VAT" ที่มีผลอยู่ไหม (เงื่อนไขเดียวกับตัวเลือกเจ้าของแถว ภ.พ.30 ของ TaxService) — ใช้ตัดสินว่าจุดความรับผิด §78/1
    /// ยังถูกถือไว้ (V1-R2: ใบเสร็จที่ติดธงยังมีผลที่กรมสรรพากร ⇒ ห้ามถอยภาษีขาย · และห้ามออกใบเสร็จถือ VAT ใบที่สองตอนรับชำระใหม่)</summary>
    private async Task<bool> LiveVatReceiptExistsAsync(Guid companyId, Guid sourceDocumentId, Guid? exceptReceiptId)
        => await _db.Documents.AsNoTracking().AnyAsync(r => r.CompanyId == companyId
            && r.RelatedDocumentId == sourceDocumentId
            && (exceptReceiptId == null || r.Id != exceptReceiptId)
            && (r.DocumentType == DocumentType.Receipt || r.DocumentType == DocumentType.ReceiptVoucher)
            && r.VatAmount > 0.005m
            && !DocumentStatusRules.NotIssued.Contains(r.Status)
            && r.Status != DocumentStatus.Voided
            && !r.IsDeleted);
}

/// <summary>ตัวแปลงคำขอ "ยกเลิกและออกใบแทน" ที่บันทึกรอคนที่สอง (V1-R6) — ตัวเดียวของฝั่งเขียนและฝั่งอ่าน</summary>
internal static class SettlementPaidReissueRequestCodec
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Serialize(ReissueSettlementPaidRequest request)
        => System.Text.Json.JsonSerializer.Serialize(request, Options);

    /// <summary>null = ไม่มี/อ่านไม่ได้ (ผู้เรียกต้องบอกผู้ใช้ ไม่ใช่เดา)</summary>
    public static ReissueSettlementPaidRequest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<ReissueSettlementPaidRequest>(json, Options); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>เหตุผลของคำขอที่ค้าง (แสดงบนหน้าเอกสาร) — อ่านไม่ได้ = null</summary>
    public static string? ReasonOf(string? json) => Parse(json)?.Reason;
}
