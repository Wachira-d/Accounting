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
        var receiptEtax = liveReceiptIds.Count == 0 ? new List<(Guid DocumentId, EtaxStatus Status)>()
            : (await _db.EtaxInvoices.AsNoTracking()
                .Where(e => e.CompanyId == companyId && liveReceiptIds.Contains(e.DocumentId))
                .Select(e => new { e.DocumentId, e.Status }).ToListAsync())
                .Select(e => (e.DocumentId, e.Status)).ToList();
        var receiptFacts = receipts.Select(r => new ReissueReceiptFact(r.DocumentNumber,
            DocumentVoidPreconditions.StrongestEtax(receiptEtax.Where(e => e.DocumentId == r.Id).Select(e => e.Status)))).ToList();

        // ด่านเดิมของ "ยกเลิกเอกสาร" ทุกตัว (e-Tax ตอบรับ · รายงานล็อก · 50 ทวิ ยื่นแล้ว · เอกสารลูก/ใบลดหนี้อ้าง)
        var etaxAccepted = await _db.EtaxInvoices.AsNoTracking()
            .AnyAsync(e => e.CompanyId == companyId && e.DocumentId == doc.Id && e.Status == EtaxStatus.Accepted);
        var filingLocked = _taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, doc.Id);
        var whtFiled = await WhtCertVoidGuard.CheckDocumentAsync(_db, companyId, doc.Id);
        var childBlock = (await DocumentVoidPreconditions.ChildBlocksAsync(_db, companyId, new[] { doc.Id },
                ignoreChildIds: liveReceiptIds))
            .TryGetValue(doc.Id, out var cb) ? cb : null;
        var docDay = doc.DocumentDate.Date;
        var closedPeriod = await _db.FiscalPeriods.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.StartDate <= docDay && f.EndDate >= docDay && f.Status != FiscalPeriodStatus.Open)
            .Select(f => f.Name).FirstOrDefaultAsync();
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
            EtaxAccepted = etaxAccepted,
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

    private static SettlementPaidReissueFacts Facts(Document doc, string? postedBlock) => new(
        doc.DocumentType, doc.Status, doc.IsSettlementReceipt, doc.ReplacedByDocumentId.HasValue,
        SettlementArtifactGuard.BatchIdFromCreator(doc.CreatedBy).HasValue, postedBlock, doc.IsDeposit,
        HasDepositApplied: false, EtaxAccepted: false, FilingLocked: false, WhtFiledBlock: null, ChildBlock: null,
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
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ยกเลิกและออกใบแทน (พิมพ์บนใบใหม่และเก็บไว้ให้ผู้สอบบัญชี)", "REISSUE-REASON");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            Document neo;
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

                // ด่านเดียวของปุ่มและการกดจริง — ใต้ล็อก (แถวรอบโอน FOR SHARE: ยกเลิกการลงบัญชีแทรกไม่ได้ · การรับชำระ FOR UPDATE)
                var eval = await EvaluateSettlementPaidReissueAsync(companyId, old, lockRows: true);
                if (!eval.Verdict.Allowed)
                    throw new BusinessRuleException(eval.Verdict.Reason ?? "ยกเลิกและออกใบแทนไม่ได้", eval.Verdict.RuleCode ?? "REISSUE-BLOCKED", 409);

                // ── ผู้ซื้อของใบใหม่ (ของบริษัทนี้เท่านั้น) + §86/4 ผู้ซื้อต้องครบเมื่อเป็นใบกำกับภาษี ──
                var contactId = request.ContactId ?? old.ContactId;
                var contact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
                    ?? throw new BusinessRuleException("ไม่พบผู้ซื้อที่เลือกในบริษัทนี้ — เลือกผู้ติดต่อใหม่แล้วกดอีกครั้ง · ระบบยังไม่ได้แตะอะไร",
                        "REISSUE-CONTACT", 400);
                if ((old.DocumentType == DocumentType.TaxInvoice || old.IsTaxInvoiceByLaw == true)
                    && !contact.IsWalkInCustomer && !old.BuyerDeclinedTaxInvoice)
                {
                    var missing = Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(contact);
                    if (missing.Count > 0)
                        throw new BusinessRuleException(
                            $"ข้อมูลผู้ซื้อยังไม่ครบตาม §86/4 ({string.Join(", ", missing)}) — แก้ที่ผู้ติดต่อ “{contact.Name}” ก่อน แล้วกดอีกครั้ง "
                            + "(ใบแทนต้องถูกต้องครบ ไม่งั้นต้องออกซ้ำอีก) · ระบบยังไม่ได้แตะอะไร", "RD-86/4-REISSUE-BUYER", 409);
                }

                // ── คำบรรยายที่แก้ (อ้าง Id บรรทัดของใบเดิมเท่านั้น) ──
                var oldLines = old.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();
                var descById = (request.Lines ?? new List<ReissueLineDescription>())
                    .Where(l => l.Description != null)
                    .GroupBy(l => l.LineId)
                    .ToDictionary(g => g.Key, g => g.Last().Description!.Trim());
                if (descById.Keys.Any(id => oldLines.All(l => l.Id != id)))
                    throw new BusinessRuleException("มีบรรทัดที่อ้างถึงแต่ไม่อยู่ในเอกสารนี้ — โหลดหน้าเอกสารใหม่แล้วลองอีกครั้ง", "REISSUE-LINE-UNKNOWN", 400);

                // ── ใบใหม่ = สำเนาทุกช่องของใบเดิม แล้วตั้งเฉพาะช่องที่ต้องต่าง ──
                var now = DateTime.UtcNow;
                var before = ReissueDocumentSnapshot.Of(old, oldLines);
                neo = new Document();
                SettlementPaidReissue.CopyScalars(old, neo);
                neo.Id = Guid.NewGuid();
                neo.CreatedAt = now;
                neo.CreatedBy = actor;
                neo.UpdatedAt = null;
                neo.UpdatedBy = actor;
                neo.IsDeleted = false;
                neo.ContactId = contact.Id;
                neo.Notes = SettlementPaidReissue.ComposeNotes(request.Notes ?? old.Notes, old.DocumentNumber, old.DocumentDate, reason);
                neo.ReplacesDocumentId = old.Id;
                neo.ReplacedByDocumentId = null;
                neo.ReplacedAt = null;
                neo.ReplacementReason = reason;
                neo.ReplacementCarriesPostings = true;
                neo.EtaxCancelRequiredAt = null;
                neo.EtaxCancelRequiredReason = null;
                neo.QuotationAcceptToken = null;
                neo.QuotationAcceptTokenExpiresAt = null;
                neo.DeliverySignToken = null;
                neo.DeliverySignTokenExpiresAt = null;
                neo.RevisionNumber = 0;
                neo.AgingDays = null;
                neo.AgingLastEvaluatedAt = null;
                neo.LastDunningSentAt = null;
                neo.LastDunningLevel = null;
                // ใบใหม่ยังไม่ถูกส่งให้ลูกค้า — "ส่งแล้ว" ไม่ตามมา · สถานะการชำระตามเดิม (การรับชำระย้ายมาทั้งหมด)
                neo.Status = old.Status == DocumentStatus.Sent ? DocumentStatus.Approved : old.Status;

                var newLines = new List<DocumentLine>();
                foreach (var ol in oldLines)
                {
                    var nl = new DocumentLine();
                    SettlementPaidReissue.CopyScalars(ol, nl);
                    nl.Id = Guid.NewGuid();
                    nl.DocumentId = neo.Id;
                    nl.CreatedAt = now;
                    nl.CreatedBy = actor;
                    nl.UpdatedAt = null;
                    nl.UpdatedBy = null;
                    nl.IsDeleted = false;
                    if (descById.TryGetValue(ol.Id, out var desc)) nl.Description = desc;
                    newLines.Add(nl);
                }

                // ── ด่าน "ใบใหม่เท่าใบเดิม" (คำตัดสินข้อ 9) — ต่างได้เฉพาะผู้ซื้อ/หมายเหตุ/คำบรรยาย ──
                var after = ReissueDocumentSnapshot.Of(neo, newLines);
                var forbidden = SettlementPaidReissue.ForbiddenChanges(before, after);
                if (forbidden.Count > 0)
                    throw new BusinessRuleException(
                        "ยกเลิกและออกใบแทนเปลี่ยนได้เฉพาะผู้ซื้อ หมายเหตุ และคำบรรยายรายการ — " + string.Join(" · ", forbidden)
                        + " · ยอด/รายการ/อัตรา VAT ที่ต่าง ⇒ ออกใบลดหนี้/ใบเพิ่มหนี้อ้างใบนี้ (§86/9-10) · ระบบยังไม่ได้แตะอะไร",
                        "REISSUE-CONTENT-CHANGED", 409);
                var changed = SettlementPaidReissue.AllowedChanges(before, after);

                // เลขที่ใหม่ — วันที่เดิม (จุดความรับผิดเกิดไปแล้ว) · ชุดเลขตามกติกาเดียวกับตอนอนุมัติ · ใต้ advisory lock ของตัวออกเลข
                neo.DocumentNumber = await DocumentNumberGenerator.NextAsync(
                    _db, companyId, await ResolveNumberSeriesTypeAsync(companyId, neo), neo.DocumentDate);
                neo.InternalNotes = SettlementPaidReissue.ReplacementInternalNote(old.DocumentNumber, reason, changed);
                _db.Documents.Add(neo);
                _db.DocumentLines.AddRange(newLines);

                // ── ใบเดิม: Voided + ตราประทับใบแทน · ไม่มีรายการกลับบัญชี (ผลทางบัญชีย้ายไปใบใหม่ข้างล่าง) ──
                var oldStatus = old.Status;
                var movedPaymentCount = eval.Payments.Count;
                old.Status = DocumentStatus.Voided;
                old.ReplacedByDocumentId = neo.Id;
                old.ReplacedAt = now;
                old.PaidAmount = 0m;
                old.BalanceDue = old.TotalAmount;
                old.AgingDays = null;
                old.AgingLastEvaluatedAt = now;
                old.UpdatedAt = now;
                old.UpdatedBy = actor;
                AppendInternalNote(old, SettlementPaidReissue.OriginalNote(neo.DocumentNumber, reason, movedPaymentCount));
                // e-Tax ของใบเดิมที่ยังไม่ตอบรับ (ตอบรับแล้วถูกด่านปฏิเสธ) — ยกเลิกตามใบ (เหมือน VoidDocumentAsync ขั้น 3)
                var oldEtax = await _db.EtaxInvoices
                    .Where(e => e.CompanyId == companyId && e.DocumentId == old.Id
                        && e.Status != EtaxStatus.Voided && e.Status != EtaxStatus.Accepted)
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
                var moved = await RepointDocumentLinksAsync(companyId, old.Id, neo.Id);

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
                        movedPayments = movedPaymentCount,
                        moved,
                        reissuedReceipts = newReceipts.Select(x => x.DocumentNumber).ToList(),
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

            // ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ) — หลัง commit เท่านั้น · ล้ม = ป้ายบนเอกสาร ไม่ย้อนการออกใบ
            await _issuedHooks.RunAsync(companyId, neo);
            foreach (var nr in newReceipts)
                await _issuedHooks.RunAsync(companyId, nr);
            return await GetDocumentAsync(companyId, neo.Id);
        });
    }

    /// <summary>ชี้ทุกอย่างที่อ้างใบเดิม "ในฐานะเจ้าของผลทางบัญชี/ใบขายของรายการนั้น" ไปใบใหม่ (บริษัทนี้เท่านั้น) — คืนจำนวนแถวต่อชนิด (ลง audit)
    /// <para>ไม่ย้าย (ประวัติของกระดาษใบเดิม): e-Tax · ประวัติแก้ไข/อนุมัติ/ลายเซ็น/อีเมล · แถวรายงานภาษี · ใบทวงหนี้ · log การเชื่อมต่อ/OCR ·
    /// ส่วนที่ด่านปฏิเสธไปแล้ว (มัดจำ · 50 ทวิ · เลื่อนภาษี) ไม่มีให้ย้าย</para></summary>
    private async Task<Dictionary<string, int>> RepointDocumentLinksAsync(Guid companyId, Guid oldId, Guid newId)
    {
        var r = new Dictionary<string, int>();
        r["paymentAllocations"] = await _db.PaymentAllocations
            .Where(a => a.CompanyId == companyId && a.DocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.DocumentId, newId));
        r["journalEntries"] = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.SourceDocumentId, (Guid?)newId));
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
        r["reconciliationItems"] = await _db.ReconciliationGroupItems
            .Where(i => i.ItemType == ReconciliationItemType.Document && i.ItemId == oldId
                && _db.ReconciliationGroups.Any(g => g.Id == i.GroupId && g.CompanyId == companyId))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ItemId, newId));
        r["bankSuggestions"] = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && t.SuggestedDocumentId == oldId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.SuggestedDocumentId, (Guid?)newId));
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
}
