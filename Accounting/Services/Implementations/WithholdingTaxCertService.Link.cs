using System.Linq.Expressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 271 — ผูกใบสำคัญจ่าย (PV) กับ 50 ทวิ ที่ออกไว้แล้ว · เปลี่ยนการผูก · หน้า 50 ทวิ (ตัวกรองฝั่งเซิร์ฟเวอร์ · คอลัมน์ "ผูกกับ" · อาจนับซ้ำ)
///
/// <para>ตัวตัดสินเดียว <see cref="WhtCertLinkPlan"/> · "ยื่นแล้ว" จาก <see cref="WhtCertVoidGuard.CheckPeriodAsync"/> (TaxFilingLockPolicy + บันทึกนำส่ง) ตัวเดียว ·
/// ความหมาย "ใบ X ครอบเอกสาร Y = X.DocumentId == Y" คงเดิม ⇒ ผู้อ่าน 41 จุด (รายงาน/ไฟล์ยื่น/นำส่ง/กระทบยอด) ไม่ต้องแตะ ·
/// audit ผ่าน <c>AddChainedAuditLog</c> เท่านั้น · ไม่มีจุดเรียก AI (กติกาตายตัว ไม่มี fuzzy)</para>
/// </summary>
public partial class WithholdingTaxCertService
{
    // ───────────────────────────── ข้อเท็จจริง ─────────────────────────────

    private sealed record PvLinkContext(WhtCertLinkPvFacts Facts, string PayeeName, string? PayeeTaxIdRaw);

    /// <summary>ฝั่งการจ่าย — ค่าทั้งหมดมาจาก "ใบที่ระบบจะออกให้การจ่ายนี้" (<see cref="PreviewCertFromDocumentAsync"/>) ไม่ใช่สูตรสำเนา</summary>
    private async Task<PvLinkContext> BuildPvLinkFactsAsync(Guid companyId, Document doc, DateTime paymentDate, Guid? sourcePaymentId,
        decimal? paymentWhtAmount)
    {
        var portions = await LoadPayeePortionsAsync(companyId, doc.Id);
        var multi = WhtPayeeSplit.IsMultiPayee(portions);
        var viaE = await EWithholdingScope.IsViaAsync(_db, companyId, doc);
        WithholdingTaxCert? preview = null;
        string? problem = null;
        if (!multi && doc.WithholdingTaxAmount > 0m && WhtCertIssuePlan.AppliesTo(doc.DocumentType))
        {
            try { preview = await PreviewCertFromDocumentAsync(companyId, doc.Id, paymentDate, sourcePaymentId, paymentWhtAmount); }
            catch (BusinessRuleException ex) { problem = ex.Message; }
            catch (KeyNotFoundException ex) { problem = ex.Message; }
        }
        var payeeId = preview?.PayeeContactId ?? WhtCertIssuePlan.PayeeOf(doc.ContactId, doc.WhtPayeeContactId);
        var payee = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == payeeId && c.CompanyId == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync();
        var facts = new WhtCertLinkPvFacts(
            doc.Id, doc.DocumentType, doc.Status, doc.DocumentNumber,
            payeeId, payee?.TaxId, payee?.BranchCode, multi, viaE,
            preview?.TotalTaxAmount ?? (paymentWhtAmount ?? doc.WithholdingTaxAmount),
            preview?.TotalIncomeAmount ?? doc.SubTotal,
            preview?.TaxFormType ?? TaxType.WithholdingTax53,
            preview?.CertificateType ?? WhtGrossUp.Normalize(doc.WhtCertType),
            preview?.Lines.Select(l => l.IncomeTypeCode).ToList() ?? new List<string>(),
            preview?.TaxYear ?? paymentDate.Year, preview?.TaxMonth ?? paymentDate.Month,
            problem);
        return new PvLinkContext(facts, payee?.Name ?? "-", payee?.TaxId);
    }

    private sealed record CandidateRow(WithholdingTaxCert Cert, WhtCertLinkCandidateFacts Facts, string PayeeName);

    private async Task<List<CandidateRow>> CandidateFactsAsync(Guid companyId, IReadOnlyList<WithholdingTaxCert> certs)
    {
        var payeeIds = certs.Select(c => c.PayeeContactId).Distinct().ToList();
        var payees = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && payeeIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId, c.BranchCode }).ToDictionaryAsync(c => c.Id);
        return certs.Select(w =>
        {
            payees.TryGetValue(w.PayeeContactId, out var p);
            return new CandidateRow(w, new WhtCertLinkCandidateFacts(
                w.Id, w.CertificateNumber, w.Status, w.IsDeleted, w.DocumentId, w.SourcePayrollRunId,
                w.PayeeContactId, p?.TaxId, p?.BranchCode, w.TotalTaxAmount, w.TotalIncomeAmount, w.TaxFormType, w.CertificateType,
                w.Lines.Select(l => l.IncomeTypeCode).ToList(), w.TaxYear, w.TaxMonth), p?.Name ?? "-");
        }).ToList();
    }

    /// <summary>ผลตรวจงวดจากตัวกลาง <see cref="WhtCertVoidGuard.CheckPeriodAsync"/> (cache ต่อคำขอ)</summary>
    private async Task<WhtCertLinkPeriodFacts> PeriodFactsAsync(Guid companyId, WithholdingTaxCert? existing, WithholdingTaxCert candidate,
        WhtCertLinkPvFacts pv, Dictionary<(TaxType, int, int), string?> cache)
    {
        async Task<string?> P(TaxType form, int y, int m)
        {
            if (cache.TryGetValue((form, y, m), out var hit)) return hit;
            var r = await WhtCertVoidGuard.CheckPeriodAsync(_db, companyId, form, y, m);
            cache[(form, y, m)] = r;
            return r;
        }
        // รอบ 271 ฝ่ายค้าน M2: "ผูกครั้งแรก" นับว่าไม่ยกเลิกใบใด (271-1-2) ได้ก็ต่อเมื่อใบสำคัญจ่ายนี้ไม่เคยมีใบถูกยกเลิกในเดือนที่ยื่นแล้ว —
        // ไม่งั้น "ยกเลิก A (รับทราบงวดยื่นแล้ว) แล้วผูก B" = เปลี่ยนการผูกของเดือนที่ยื่นแล้วสองขั้น (ห้ามตาม 271-0-2)
        string? voidedFiled = null;
        if (existing == null && pv.DocumentId != Guid.Empty)
        {
            var voided = await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(w => w.CompanyId == companyId && w.DocumentId == pv.DocumentId && w.Status == WithholdingTaxCertStatus.Voided)
                .Select(w => new { w.CertificateNumber, w.TaxFormType, w.TaxYear, w.TaxMonth }).ToListAsync();
            foreach (var v in voided)
                if (await P(v.TaxFormType, v.TaxYear, v.TaxMonth) is string vr)
                {
                    voidedFiled = $"{vr} — ใบเดิม {v.CertificateNumber} ของใบสำคัญจ่ายนี้ถูกยกเลิกในเดือนที่ยื่นแล้ว ⇒ การผูกใบใหม่คือการเปลี่ยนการผูกของเดือนที่ยื่นแล้ว";
                    break;
                }
        }
        return new WhtCertLinkPeriodFacts(
            existing != null || voidedFiled != null,
            existing != null ? await P(existing.TaxFormType, existing.TaxYear, existing.TaxMonth) : voidedFiled,
            await P(candidate.TaxFormType, candidate.TaxYear, candidate.TaxMonth),
            await P(pv.FormType, pv.PaymentYear, pv.PaymentMonth));
    }

    private static WhtLinkCandidateDto ToCandidateDto(CandidateRow r, WhtCertLinkDecision d) => new(
        r.Cert.Id, r.Cert.CertificateNumber, r.Cert.Status.ToString(), WhtCertLinkLabels.Status(r.Cert.Status),
        WhtUnissuedCertGate.FormLabel(r.Cert.TaxFormType), r.Cert.TaxYear, r.Cert.TaxMonth, r.PayeeName,
        r.Cert.TotalIncomeAmount, r.Cert.TotalTaxAmount, d.Verdict.ToString(),
        d.Blocks.Select(b => new WhtLinkIssueDto(b.Code, b.Message)).ToList(),
        d.Warnings.Select(w => new WhtLinkIssueDto(w.Code, w.Message)).ToList(),
        WhtCertLinkLabels.Result(d));

    private static WhtLinkPvSideDto ToPvDto(PvLinkContext ctx) => new(
        ctx.Facts.DocumentId == Guid.Empty ? null : ctx.Facts.DocumentId, ctx.Facts.DocumentNumber, ctx.PayeeName,
        ctx.PayeeTaxIdRaw, TaxBranchCode.Normalize(ctx.Facts.PayeeBranch), WhtUnissuedCertGate.FormLabel(ctx.Facts.FormType),
        WhtCertLinkLabels.CertType(ctx.Facts.CertType), ctx.Facts.IncomeThb, ctx.Facts.WhtThb, ctx.Facts.PaymentYear, ctx.Facts.PaymentMonth,
        ctx.Facts.IncomeCodes.ToList());

    /// <summary>ขอบเขตการจ่าย: งวด (paymentId) ⇒ วันจ่าย + ภาษีของงวดนั้น · ไม่ระบุ ⇒ ทั้งเอกสาร</summary>
    private async Task<(DateTime PaymentDate, decimal? PaymentWht)> PaymentScopeAsync(Guid companyId, Document doc, Guid? paymentId)
    {
        if (paymentId is not Guid pid) return (doc.PaymentDate ?? doc.DocumentDate, null);
        var pay = await _db.Payments.AsNoTracking().Where(p => p.Id == pid && p.CompanyId == companyId)
            .Select(p => new { p.DocumentId, p.PaymentDate, p.WithholdingTaxAmount }).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบงวดการจ่ายที่เลือก");
        var alloc = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.PaymentId == pid && a.DocumentId == doc.Id)
            .Select(a => (decimal?)a.WithholdingTaxAmount).FirstOrDefaultAsync();
        if (alloc == null && pay.DocumentId != doc.Id)
            throw new BusinessRuleException("งวดการจ่ายที่เลือกไม่ใช่ของใบสำคัญจ่ายนี้", WhtCertLinkPlan.RuleCode);
        var wht = alloc ?? pay.WithholdingTaxAmount;
        return (pay.PaymentDate, wht > 0m ? wht : null);
    }

    private Task<List<WithholdingTaxCert>> ActiveCertsOfDocumentAsync(Guid companyId, Guid documentId, Guid? paymentId)
        => _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .Where(w => w.CompanyId == companyId && w.DocumentId == documentId && w.Status != WithholdingTaxCertStatus.Voided
                && (paymentId == null || w.SourcePaymentId == paymentId))
            .ToListAsync();

    private static string UnlinkedNotice(WithholdingTaxCert a, string newNumber)
        => $"ใบเดิม {a.CertificateNumber} จะถูกยกเลิก — เลขที่ {a.CertificateNumber} คงอยู่และประทับ \"ยกเลิก\" (เลขไม่ขาดช่วง) · แทนด้วย {newNumber}"
           + (a.PartnerDeliveredAt != null ? " · แจ้งผู้ขายในกล่องรับ 50 ทวิ อัตโนมัติ" : "")
           + " · ไม่มีทางเลือกลบ (271-0-1)";

    /// <summary>ฝ่ายค้าน M4: ล็อกแถวใบสำคัญจ่าย (ต้องอยู่ในธุรกรรม) — ทุกทางที่ตั้ง <c>DocumentId</c> ของ 50 ทวิ (ผูก · เปลี่ยนการผูก · ออกใบจากเอกสาร)
    /// ถือล็อกเดียวกัน ⇒ "ผูก B" กับ "สร้างใบ" พร้อมกันไม่ได้สองใบต่อการจ่าย (ดัชนี unique กันชั้นฐานข้อมูลอีกชั้น — Round271Migration)</summary>
    private async Task LockDocumentRowAsync(Guid companyId, Guid documentId)
    {
        await _db.Database.ExecuteSqlRawAsync(
            @"SELECT 1 FROM ""Documents"" WHERE ""Id"" = {0} AND ""CompanyId"" = {1} FOR UPDATE", documentId, companyId);
    }

    // ───────────────────────────── ทางอัตโนมัติ (อนุมัติ/จ่าย) ─────────────────────────────

    /// <summary>ผูกใบที่ผู้ใช้เลือกไว้บนร่าง (<c>Document.WhtLinkCertId</c>) — เรียกจาก <see cref="TryAutoIssueCoreAsync"/> เมื่อ
    /// <see cref="WhtCertIssuePlan.Decide"/> ตอบ <see cref="WhtCertIssueAction.LinkExisting"/> เท่านั้น ·
    /// ผูกไม่ผ่าน (271-1-4 = D7) ⇒ การอนุมัติ/การจ่ายยังสำเร็จ · <b>ไม่ผูก · ไม่ออกใบใหม่</b> · ล้มดัง 3 ที่: ป้ายบนเอกสาร + ผลลัพธ์ถึงผู้เรียก + audit</summary>
    private async Task<WhtAutoIssueOutcome> LinkRequestedCertAsync(Guid companyId, Document doc, Guid certId, WhtCertIssueTrigger trigger,
        string createdBy, DateTime? paymentDate, Guid? sourcePaymentId, decimal? paymentWhtAmount, bool writeFailureNote)
    {
        var cert = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId);
        string? failure = null;
        WhtCertLinkDecision? decision = null;
        if (cert == null)
            failure = "ไม่พบ 50 ทวิ ที่เลือกไว้บนใบนี้ (อาจถูกลบ)";
        else
        {
            var ctx = await BuildPvLinkFactsAsync(companyId, doc, paymentDate ?? doc.PaymentDate ?? doc.DocumentDate, sourcePaymentId, paymentWhtAmount);
            var row = (await CandidateFactsAsync(companyId, new[] { cert }))[0];
            var period = await PeriodFactsAsync(companyId, null, cert, ctx.Facts, new());
            decision = WhtCertLinkPlan.Decide(ctx.Facts, row.Facts, period);
            var unacked = decision.Unacknowledged(WhtCertLinkPlan.ParseAckCodes(doc.WhtLinkAckCodes));
            if (decision.IsBlocked)
                failure = string.Join(" · ", decision.Blocks.Select(b => b.Message));
            else if (unacked.Count > 0)
                failure = "มีคำเตือนใหม่ที่ยังไม่ได้รับทราบ: " + string.Join(" · ", unacked.Select(w => w.Message));
        }

        var userId = Guid.TryParse(createdBy, out var uid) ? uid : (Guid?)null;
        if (failure == null)
        {
            var now = DateTime.UtcNow;
            var rows = await _db.WithholdingTaxCerts
                .Where(w => w.Id == certId && w.CompanyId == companyId && (w.DocumentId == null || w.DocumentId == doc.Id)
                    && w.SourcePayrollRunId == null && w.Status != WithholdingTaxCertStatus.Voided
                    && w.UpdatedAt == cert!.UpdatedAt)   // ฝ่ายค้าน L1: ใบไม่ถูกแก้ระหว่างตรวจกับผูก
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.DocumentId, doc.Id)
                    .SetProperty(w => w.SourcePaymentId, sourcePaymentId)
                    .SetProperty(w => w.LinkedAt, now)
                    .SetProperty(w => w.LinkedByUserId, userId)
                    .SetProperty(w => w.UpdatedAt, now));
            if (rows == 1)
            {
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserId = userId, Action = AuditAction.Update,
                    EntityType = "WithholdingTaxCert", EntityId = certId.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { cert!.CertificateNumber, cert.DocumentId, cert.SourcePaymentId }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        DocumentId = doc.Id, doc.DocumentNumber, SourcePaymentId = sourcePaymentId, Trigger = trigger.ToString(),
                        Warnings = decision!.Warnings.Select(w => w.Code), AcknowledgedOnDraft = doc.WhtLinkAckCodes,
                        RuleCode = WhtCertLinkPlan.RuleCode, LegalReference = WhtCertLinkPlan.LegalReference,
                    }),
                    Timestamp = now,
                });
                doc.InternalNotes = WhtCertAutoFailedNote.Clear(doc.InternalNotes);
                await _db.SaveChangesAsync();
                if (cert.Status is WithholdingTaxCertStatus.Issued or WithholdingTaxCertStatus.Printed)
                    await AttachCertPdfToDocumentAsync(companyId, certId);
                // ไม่ส่งอีเมลซ้ำ — ใบนี้ส่งไปแล้วตอนออก (ถ้ามีกฎ)
                return new WhtAutoIssueOutcome(WhtCertIssueAction.LinkExisting, certId, cert.CertificateNumber, null,
                    $"ผูก 50 ทวิ {cert.CertificateNumber} ที่ออกไว้แล้วกับ {doc.DocumentNumber} (ไม่ออกใบใหม่)");
            }
            failure = "ใบนี้ถูกผูกกับเอกสารอื่น/ถูกยกเลิกระหว่างทำรายการ";
        }

        var reason = $"ผูก 50 ทวิ {cert?.CertificateNumber} ไม่ได้: {failure} — ระบบไม่ได้ออกใบใหม่และไม่ได้ผูกใบ (กันนับซ้ำ) · "
                     + "ผูกใบอื่นหรือสร้างใบได้ที่หน้ารายละเอียดใบสำคัญจ่าย (กล่อง \"50 ทวิ ของใบนี้\")";
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update,
            EntityType = "Document", EntityId = doc.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                doc.DocumentNumber, WhtLinkCertId = certId, cert?.CertificateNumber, Trigger = trigger.ToString(), Failure = failure,
                Blocks = decision?.Blocks.Select(b => b.Code), RuleCode = WhtCertLinkPlan.FailedRuleCode, LegalReference = WhtCertLinkPlan.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        if (writeFailureNote)
            doc.InternalNotes = WhtCertAutoFailedNote.Append(doc.InternalNotes, reason);
        await _db.SaveChangesAsync();
        return new WhtAutoIssueOutcome(WhtCertIssueAction.LinkExisting, null, null, reason, reason);
    }

    // ───────────────────────────── ผู้สมัคร + ตรวจ ─────────────────────────────

    /// <summary>ผู้สมัครของใบสำคัญจ่ายที่บันทึกแล้ว (ฟอร์มแก้ร่าง · โมดัลเปลี่ยนการผูก)</summary>
    public async Task<WhtLinkCandidatesResponse> GetLinkCandidatesForDocumentAsync(Guid companyId, Guid documentId, Guid? paymentId)
    {
        var doc = await _db.Documents.Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var (payDate, payWht) = await PaymentScopeAsync(companyId, doc, paymentId);
        var ctx = await BuildPvLinkFactsAsync(companyId, doc, payDate, paymentId, payWht);
        var current = await ActiveCertsOfDocumentAsync(companyId, doc.Id, paymentId);
        var a = current.Count == 1 ? current[0] : null;
        if (current.Count > 1)
            return new WhtLinkCandidatesResponse(ToPvDto(ctx), Array.Empty<WhtLinkCandidateDto>(), null,
                "ใบนี้มี 50 ทวิ หลายใบ (รายงวด) — เลือกงวดที่จะเปลี่ยนการผูกก่อน", null, null, null);
        return await CandidatesForAsync(companyId, ctx, a, doc.Id);
    }

    /// <summary>ผู้สมัครจากฟอร์มที่ยังไม่บันทึก (ค่าที่ฟอร์มแสดง) — ใบที่บันทึกแล้ว + ฟอร์มไม่เปลี่ยนยอด ใช้ <see cref="GetLinkCandidatesForDocumentAsync"/> ดีกว่า</summary>
    public async Task<WhtLinkCandidatesResponse> GetLinkCandidatesForFormAsync(Guid companyId, WhtLinkFormQuery q)
    {
        var payee = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == q.PayeeContactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ถูกหักภาษี");
        var codes = (q.IncomeCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var (form, _, _) = ResolveWhtFormType(payee, null, q.IsForeignService, codes.Cast<string?>(),
            royaltyAsPnd2: (await WhtRoyaltyPolicy.LoadAsync(_db, companyId)).AppliesTo(q.Date));
        var facts = new WhtCertLinkPvFacts(q.DocumentId ?? Guid.Empty, DocumentType.PaymentVoucher, DocumentStatus.Draft, null,
            payee.Id, payee.TaxId, payee.BranchCode, false, false, q.Wht, q.Income, form,
            WhtGrossUp.Normalize(q.CertType ?? WithholdingTaxCertType.Withhold), codes, q.Date.Year, q.Date.Month);
        var ctx = new PvLinkContext(facts, payee.Name, payee.TaxId);
        WithholdingTaxCert? a = null;
        if (q.DocumentId is Guid did)
        {
            var current = await ActiveCertsOfDocumentAsync(companyId, did, null);
            a = current.Count == 1 ? current[0] : null;
        }
        return await CandidatesForAsync(companyId, ctx, a, q.DocumentId);
    }

    private async Task<WhtLinkCandidatesResponse> CandidatesForAsync(Guid companyId, PvLinkContext ctx, WithholdingTaxCert? a, Guid? documentId)
    {
        var pv = ctx.Facts;
        var pvDto = ToPvDto(ctx);
        if (pv.IsMultiPayee)
            return new(pvDto, Array.Empty<WhtLinkCandidateDto>(), null,
                "ใบนี้มีผู้ถูกหักหลายราย — ยังผูก 50 ทวิ ที่ออกไว้แล้วไม่ได้ (ให้ระบบออกใบแยกต่อผู้ถูกหัก)", a?.Id, a?.CertificateNumber, null);
        if (pv.ViaEWithholding)
            return new(pvDto, Array.Empty<WhtLinkCandidateDto>(), null, WhtCertIssuePlan.EWithholdingLabel, a?.Id, a?.CertificateNumber, null);

        var cache = new Dictionary<(TaxType, int, int), string?>();
        // 271-0-2: ใบเดิม A / เดือนจ่ายอยู่ในเดือนที่ยื่นแล้ว ⇒ ไม่แสดงตารางผู้สมัครเลย (กล่องแดง)
        string? filedBlock = null;
        if (a != null)
        {
            var aFiled = await WhtCertVoidGuard.CheckPeriodAsync(_db, companyId, a.TaxFormType, a.TaxYear, a.TaxMonth);
            var payFiled = await WhtCertVoidGuard.CheckPeriodAsync(_db, companyId, pv.FormType, pv.PaymentYear, pv.PaymentMonth);
            var why = aFiled ?? payFiled;
            if (why != null)
                filedBlock = $"เปลี่ยน 50 ทวิ ที่ผูกไม่ได้ — {why} · ใบ {a.CertificateNumber} อยู่ในแบบที่ยื่นแล้ว (ผู้ถูกหักถือฉบับจริง)" + WhtCertLinkPlan.FiledWayForward;
        }
        if (filedBlock != null)
            return new(pvDto, Array.Empty<WhtLinkCandidateDto>(), filedBlock, null, a?.Id, a?.CertificateNumber, null);

        var payeeIds = new List<Guid> { pv.EffectivePayeeId };
        // ผู้ติดต่อซ้ำ (เลขภาษีเดียวกัน — ทุกสาขา โดยตั้งใจ: ใบคนละสาขาโผล่เป็น ⛔ R4 พร้อมเหตุผล ไม่ซ่อนเงียบ · สาขาเดียวกัน = ⚠️ R4-DUP)
        if (ThaiTaxId.IsWellFormed(pv.PayeeTaxId))
        {
            payeeIds.AddRange(await ContactTaxBranchKey.AllBranchIdsAsync(
                _db.Contacts.AsNoTracking().IgnoreQueryFilters().Where(c => c.CompanyId == companyId), companyId, pv.PayeeTaxId));
            payeeIds = payeeIds.Distinct().ToList();
        }
        var key = pv.PaymentYear * 12 + pv.PaymentMonth;
        var lo = key - WhtCertLinkPlan.CandidateMonthWindow;
        var hi = key + WhtCertLinkPlan.CandidateMonthWindow;
        var certs = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .Where(w => w.CompanyId == companyId && payeeIds.Contains(w.PayeeContactId)
                && w.Status != WithholdingTaxCertStatus.Voided && w.SourcePayrollRunId == null
                && w.DocumentId == null   // 271-0-3: ใบที่ผูกเอกสารใดอยู่ (รวมใบนี้) ไม่ใช่ผู้สมัคร
                && w.TaxYear * 12 + w.TaxMonth >= lo && w.TaxYear * 12 + w.TaxMonth <= hi)
            .OrderByDescending(w => w.CreatedAt).Take(50).ToListAsync();
        var rows = await CandidateFactsAsync(companyId, certs);
        var list = new List<(int Score, WhtLinkCandidateDto Dto)>();
        foreach (var r in rows)
        {
            var d = WhtCertLinkPlan.Decide(pv, r.Facts, await PeriodFactsAsync(companyId, a, r.Cert, pv, cache));
            list.Add((WhtCertLinkPlan.CandidateScore(pv, r.Facts, d), ToCandidateDto(r, d)));
        }
        var ordered = list.OrderByDescending(x => x.Score).ThenBy(x => x.Dto.CertificateNumber, StringComparer.Ordinal).Select(x => x.Dto).ToList();
        var hidden = ordered.Count == 0 ? $"ไม่พบ 50 ทวิ ที่ออกไว้ของ \"{ctx.PayeeName}\" ที่ยังไม่ผูกเอกสารใด (ช่วง ±{WhtCertLinkPlan.CandidateMonthWindow} เดือนจากเดือนจ่าย)" : null;
        return new(pvDto, ordered, null, hidden, a?.Id, a?.CertificateNumber, null);
    }

    /// <summary>ตรวจเต็มก่อนกดยืนยัน (ไม่เขียน) — ตารางเทียบ + ใบเดิมที่จะถูกยกเลิก</summary>
    public async Task<WhtLinkCheckResponse> CheckLinkAsync(Guid companyId, Guid documentId, Guid certId, Guid? paymentId)
    {
        var doc = await _db.Documents.Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var cert = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        var (payDate, payWht) = await PaymentScopeAsync(companyId, doc, paymentId);
        var ctx = await BuildPvLinkFactsAsync(companyId, doc, payDate, paymentId, payWht);
        var current = (await ActiveCertsOfDocumentAsync(companyId, doc.Id, paymentId)).Where(c => c.Id != certId).ToList();
        var a = current.Count == 1 ? current[0] : null;
        var row = (await CandidateFactsAsync(companyId, new[] { cert }))[0];
        var d = WhtCertLinkPlan.Decide(ctx.Facts, row.Facts, await PeriodFactsAsync(companyId, a, cert, ctx.Facts, new()));
        string Mark(bool blocked, bool warn) => blocked ? "Blocked" : warn ? "Warning" : "Ok";
        bool Has(string code) => d.Blocks.Any(b => b.Code == code);
        bool Warn(string code) => d.Warnings.Any(w => w.Code == code);
        var pv = ctx.Facts;
        var rows = new List<WhtLinkCompareRow>
        {
            new("ผู้ถูกหัก", $"{ctx.PayeeName} ({ctx.PayeeTaxIdRaw ?? "-"}/{TaxBranchCode.Normalize(pv.PayeeBranch)})",
                $"{row.PayeeName} ({row.Facts.PayeeTaxId ?? "-"}/{TaxBranchCode.Normalize(row.Facts.PayeeBranch)})",
                Mark(Has(WhtCertLinkPlan.CodePayee), Warn(WhtCertLinkPlan.CodeDuplicateContact))),
            new("แบบ", WhtUnissuedCertGate.FormLabel(pv.FormType), WhtUnissuedCertGate.FormLabel(cert.TaxFormType),
                Mark(pv.FormType != cert.TaxFormType, false)),
            new("เงื่อนไข", WhtCertLinkLabels.CertType(pv.CertType), WhtCertLinkLabels.CertType(cert.CertificateType),
                Mark(WhtGrossUp.Normalize(pv.CertType) != WhtGrossUp.Normalize(cert.CertificateType), false)),
            new("เงินได้", pv.IncomeThb.ToString("N2"), cert.TotalIncomeAmount.ToString("N2"),
                Mark(pv.IncomeThb != cert.TotalIncomeAmount, false)),
            new("ภาษี", pv.WhtThb.ToString("N2"), cert.TotalTaxAmount.ToString("N2"),
                Mark(pv.WhtThb != cert.TotalTaxAmount, false)),
            new("เดือนภาษี", $"{pv.PaymentMonth:00}/{pv.PaymentYear + 543} (วันจ่าย {payDate.Day:00}/{payDate.Month:00}/{payDate.Year + 543})",
                $"{cert.TaxMonth:00}/{cert.TaxYear + 543}", Mark(false, Warn(WhtCertLinkPlan.CodeMonth))),
            new("ประเภทเงินได้", string.Join(", ", pv.IncomeCodes.Distinct()), string.Join(", ", row.Facts.IncomeCodes.Distinct()),
                Mark(false, Warn(WhtCertLinkPlan.CodeIncomeType))),
        };
        return new WhtLinkCheckResponse(ToCandidateDto(row, d), ToPvDto(ctx), rows, a?.Id, a?.CertificateNumber,
            a != null ? UnlinkedNotice(a, cert.CertificateNumber) : null, !d.IsBlocked);
    }

    // ───────────────────────────── ผูก / เปลี่ยนการผูก (ทางเดียว) ─────────────────────────────

    /// <summary>ผูก (ครั้งแรก) หรือเปลี่ยนการผูกหลังอนุมัติ — ธุรกรรมเดียว + ล็อกแถวใบสำคัญจ่าย · ด่าน R1–R11 · เดือนที่ยื่นแล้ว + ยกเลิกใบเดิม = 409 เด็ดขาด
    /// (271-0-2 — ไม่มีทางรับทราบ ไม่มีสิทธิ์ใดผ่าน) · ใบเดิมยกเลิกเสมอ ไม่ลบ (271-0-1) · ล้ม = ย้อนทั้งหมด</summary>
    public async Task<WhtLinkResult> LinkCertToDocumentAsync(Guid companyId, Guid documentId, WhtLinkRequest request, Guid? userId)
    {
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            // ล็อกแถวใบสำคัญจ่าย (สองคำขอเปลี่ยนการผูกใบเดียวกันพร้อมกัน ⇒ ทีละคำขอ) — แบบเดียวกับเปลี่ยนผู้ถูกหัก
            await LockDocumentRowAsync(companyId, documentId);
            var doc = await _db.Documents.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
            if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                throw new BusinessRuleException(
                    "ใบนี้ยังไม่อนุมัติ — เลือก \"ออกใบไว้แล้ว — ผูกกับ 50 ทวิ ที่มีอยู่\" ในฟอร์มใบสำคัญจ่ายแทน (ระบบผูกให้ตอนอนุมัติ/จ่าย)",
                    WhtCertLinkPlan.RuleCode);
            var cert = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
                .FirstOrDefaultAsync(w => w.Id == request.CertId && w.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
            var (payDate, payWht) = await PaymentScopeAsync(companyId, doc, request.PaymentId);
            var current = await ActiveCertsOfDocumentAsync(companyId, doc.Id, request.PaymentId);
            if (current.Any(c => c.Id == cert.Id))
                throw new BusinessRuleException($"{cert.CertificateNumber} ผูกกับใบนี้อยู่แล้ว", WhtCertLinkPlan.RuleCode);
            if (current.Count > 1)
                throw new BusinessRuleException("ใบนี้มี 50 ทวิ หลายใบ (รายงวด) — เลือกงวดที่จะเปลี่ยนการผูก", WhtCertLinkPlan.RuleCode);
            var a = current.FirstOrDefault();
            // ฝ่ายค้าน M6: ใบปัจจุบันต้องเป็นใบที่ผู้ใช้เห็นตอนเปิดโมดัล (optimistic) — มีคนเปลี่ยนก่อน ⇒ 409 (ไม่ยกเลิกใบที่ผู้ใช้ไม่เคยเห็น)
            if (request.ExpectedCurrentCertId != a?.Id)
                throw new BusinessRuleException(
                    $"50 ทวิ ที่ผูกกับใบนี้เปลี่ยนไปแล้ว (ตอนนี้: {a?.CertificateNumber ?? "ไม่มีใบ"}) — มีคนเปลี่ยนการผูกก่อน · เปิดโมดัลใหม่แล้วตรวจอีกครั้ง",
                    WhtCertLinkPlan.StaleRuleCode, 409);

            var ctx = await BuildPvLinkFactsAsync(companyId, doc, payDate, request.PaymentId, payWht);
            var row = (await CandidateFactsAsync(companyId, new[] { cert }))[0];
            var period = await PeriodFactsAsync(companyId, a, cert, ctx.Facts, new());
            var decision = WhtCertLinkPlan.Decide(ctx.Facts, row.Facts, period);
            if (decision.IsBlocked)
                throw new BusinessRuleException(string.Join(" · ", decision.Blocks.Select(b => b.Message)),
                    decision.BlockedByFiledPeriod ? WhtCertLinkPlan.FiledRuleCode : WhtCertLinkPlan.RuleCode, 409);
            var unacked = decision.Unacknowledged(request.AcknowledgeWarnings);
            if (unacked.Count > 0)
                throw new BusinessRuleException("ต้องรับทราบก่อนผูก: " + string.Join(" · ", unacked.Select(w => w.Message)),
                    WhtCertLinkPlan.NeedsAckRuleCode, 409);
            var reason = request.Reason?.Trim();
            if (a != null && string.IsNullOrWhiteSpace(reason))
                throw new BusinessRuleException($"ระบุเหตุผลที่เปลี่ยนการผูก (ใบเดิม {a.CertificateNumber} จะถูกยกเลิก)", WhtCertLinkPlan.RelinkRuleCode, 400);
            if (a != null && WhtCertLinkPlan.UnlinkedOutcome(a.CertificateNumber, period.ExistingCertFiled != null) == WhtCertUnlinkedOutcome.Blocked)
                throw new BusinessRuleException(period.ExistingCertFiled + WhtCertLinkPlan.FiledWayForward, WhtCertLinkPlan.FiledRuleCode, 409);

            // ฝ่ายค้าน M4: ยกเลิกใบเดิมก่อนผูกใบใหม่ — ดัชนี unique "ใบที่ไม่ยกเลิก 1 ใบต่อ (เอกสาร, ผู้ถูกหัก, งวดจ่าย)" (ล้ม = ย้อนทั้งธุรกรรม)
            if (a != null)
            {
                // ทางยกเลิกปกติ (ด่าน WhtCertVoidGuard ซ้ำในตัว — ไม่ส่งการรับทราบ ⇒ งวดที่ยื่นแล้ว = 409 เหมือนเดิม)
                await VoidAsync(companyId, a.Id, null, userId);
                var tracked = await _db.WithholdingTaxCerts.FirstAsync(w => w.Id == a.Id && w.CompanyId == companyId);
                tracked.SupersededByCertId = cert.Id;
                tracked.UpdatedAt = DateTime.UtcNow;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserId = userId, Action = AuditAction.Update,
                    EntityType = "WithholdingTaxCert", EntityId = a.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { a.CertificateNumber, Status = a.Status.ToString(), a.DocumentId }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Status = "Voided", SupersededByCertId = cert.Id, SupersededBy = cert.CertificateNumber, Reason = reason,
                        RuleCode = WhtCertLinkPlan.RelinkRuleCode, LegalReference = WhtCertLinkPlan.LegalReference,
                    }),
                    Timestamp = DateTime.UtcNow,
                });
            }
            var now = DateTime.UtcNow;
            var rows = await _db.WithholdingTaxCerts
                .Where(w => w.Id == cert.Id && w.CompanyId == companyId && w.DocumentId == null
                    && w.SourcePayrollRunId == null && w.Status != WithholdingTaxCertStatus.Voided
                    && w.UpdatedAt == cert.UpdatedAt)   // ฝ่ายค้าน L1: ใบไม่ถูกแก้ (ยอด/สถานะ) ระหว่างตรวจกับผูก
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.DocumentId, doc.Id)
                    .SetProperty(w => w.SourcePaymentId, request.PaymentId)
                    .SetProperty(w => w.LinkedAt, now)
                    .SetProperty(w => w.LinkedByUserId, userId)
                    .SetProperty(w => w.UpdatedAt, now));
            if (rows != 1)
                throw new BusinessRuleException($"{cert.CertificateNumber} เพิ่งถูกผูกกับเอกสารอื่นหรือถูกยกเลิก — รีเฟรชแล้วเลือกใหม่", WhtCertLinkPlan.RuleCode, 409);

            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Update,
                EntityType = "WithholdingTaxCert", EntityId = cert.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { cert.CertificateNumber, cert.DocumentId, cert.SourcePaymentId }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    DocumentId = doc.Id, doc.DocumentNumber, SourcePaymentId = request.PaymentId, Replaces = a?.CertificateNumber, Reason = reason,
                    Acknowledged = decision.Warnings.Select(w => w.Code), RuleCode = WhtCertLinkPlan.RuleCode, LegalReference = WhtCertLinkPlan.LegalReference,
                }),
                Timestamp = now,
            });

            doc.WhtLinkCertId = cert.Id;
            doc.WhtLinkAckCodes = WhtCertLinkPlan.JoinAckCodes(decision.Warnings.Select(w => w.Code));
            doc.InternalNotes = WhtCertAutoFailedNote.Clear(doc.InternalNotes);
            await _db.SaveChangesAsync();
            if (ownTx != null) await ownTx.CommitAsync();
            if (cert.Status is WithholdingTaxCertStatus.Issued or WithholdingTaxCertStatus.Printed)
                await AttachCertPdfToDocumentAsync(companyId, cert.Id);
            return new WhtLinkResult(cert.Id, cert.CertificateNumber, a?.Id, a?.CertificateNumber,
                a == null ? $"ผูก {cert.CertificateNumber} กับ {doc.DocumentNumber} แล้ว"
                          : $"ผูก {cert.CertificateNumber} แล้ว · ยกเลิก {a.CertificateNumber}");
        }
        catch
        {
            if (ownTx != null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    // ───────────────────────────── ฝ่ายค้าน H3: สร้างใบพร้อมระบุเอกสาร (POST withholding-tax-certs + DocumentId) ─────────────────────────────

    /// <summary>ทางสร้างใบจากภายนอกที่ระบุ <c>DocumentId</c> — สร้างใบเดี่ยว (ไม่ผูก) แล้ว <b>ผูกผ่านทางเข้าเดียว</b> <see cref="LinkCertToDocumentAsync"/>
    /// (R1–R11 + เดือนที่ยื่นแล้ว + ล็อกใบสำคัญจ่าย) ในธุรกรรมเดียว · ผูกไม่ผ่าน ⇒ ย้อนทั้งหมด (ไม่มีใบค้าง) · เอกสารมีใบอยู่แล้ว ⇒ 409 (ใช้ "เปลี่ยน 50 ทวิ ที่ผูก") ·
    /// ใบใหม่เป็นร่างเสมอ ⇒ รับทราบ R10 ให้ (คำเตือนอื่นต้องผูกที่หน้ารายละเอียดใบสำคัญจ่าย) · ผู้เรียกภายใน (settlement) ใช้ <see cref="CreateAsync"/> ตรงตามเดิม</summary>
    public async Task<WithholdingTaxCertResponse> CreateLinkedAsync(Guid companyId, CreateWithholdingTaxCertRequest request, string createdBy, Guid? userId)
    {
        if (request.DocumentId is not Guid docId) return await CreateAsync(companyId, request, createdBy);
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            if (await _db.WithholdingTaxCerts.AsNoTracking().AnyAsync(w => w.CompanyId == companyId && w.DocumentId == docId
                    && w.Status != WithholdingTaxCertStatus.Voided))
                throw new BusinessRuleException("เอกสารนี้มี 50 ทวิ อยู่แล้ว — เปลี่ยนใบที่ผูกที่หน้ารายละเอียดใบสำคัญจ่าย (\"เปลี่ยน 50 ทวิ ที่ผูก\")",
                    WhtCertLinkPlan.RuleCode, 409);
            var created = await CreateAsync(companyId, request with { DocumentId = null }, createdBy);
            await LinkCertToDocumentAsync(companyId, docId,
                new WhtLinkRequest(created.Id, null, "สร้างหนังสือรับรองพร้อมระบุเอกสาร", new List<string> { WhtCertLinkPlan.CodeDraft }), userId);
            if (ownTx != null) await ownTx.CommitAsync();
            // ผูกด้วย UPDATE ตรง (ไม่ผ่าน ChangeTracker) — ใบที่ CreateAsync ถือไว้ยังเป็นค่าเก่า ⇒ อ่านใหม่จากฐาน
            foreach (var e in _db.ChangeTracker.Entries<WithholdingTaxCert>().Where(e => e.Entity.Id == created.Id).ToList())
                await e.ReloadAsync();
            return await GetByIdAsync(companyId, created.Id);
        }
        catch
        {
            if (ownTx != null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    // ───────────────────────────── กล่อง 50 ทวิ ในรายละเอียดใบสำคัญจ่าย ─────────────────────────────

    public async Task<WhtDocumentLinkStatus> GetDocumentLinkStatusAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (!WhtCertIssuePlan.AppliesTo(doc.DocumentType) || doc.WithholdingTaxAmount <= 0m)
            return new WhtDocumentLinkStatus(false, Array.Empty<WhtDocumentCertRow>(), false, null, null, Array.Empty<WhtLinkPaymentOption>());
        var certs = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DocumentId == documentId)
            .OrderBy(w => w.CreatedAt).ToListAsync();
        var supersededIds = certs.Where(c => c.SupersededByCertId != null).Select(c => c.SupersededByCertId!.Value).ToList();
        var cache = new Dictionary<(TaxType, int, int), string?>();
        async Task<string?> P(TaxType f, int y, int m)
        {
            if (cache.TryGetValue((f, y, m), out var hit)) return hit;
            return cache[(f, y, m)] = await WhtCertVoidGuard.CheckPeriodAsync(_db, companyId, f, y, m);
        }
        var open = doc.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected);
        var rows = new List<WhtDocumentCertRow>();
        foreach (var c in certs)
        {
            string? blocked = null;
            if (c.Status == WithholdingTaxCertStatus.Voided) blocked = "ใบนี้ยกเลิกแล้ว";
            else if (!open) blocked = "ใบสำคัญจ่ายยังไม่อนุมัติ/ถูกยกเลิก";
            else if (await P(c.TaxFormType, c.TaxYear, c.TaxMonth) is string filed)
                blocked = $"{filed} — ระบบไม่เปลี่ยนการผูกของเดือนที่ยื่นแล้ว (271-0-2)";
            rows.Add(new WhtDocumentCertRow(c.Id, c.CertificateNumber, c.Status.ToString(), WhtCertLinkLabels.Status(c.Status), WhtUnissuedCertGate.FormLabel(c.TaxFormType),
                c.TaxYear, c.TaxMonth, c.TotalTaxAmount, c.SourcePaymentId,
                c.LinkedAt != null ? "ผูกใบที่ออกไว้แล้ว" : "ระบบออกจากใบนี้",
                blocked == null, blocked));
        }
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == documentId && p.WithholdingTaxAmount > 0m)
            .OrderBy(p => p.PaymentDate)
            .Select(p => new { p.Id, p.PaymentNumber, p.PaymentDate, p.WithholdingTaxAmount }).ToListAsync();
        var payOpts = payments.Select(p => new WhtLinkPaymentOption(p.Id, p.PaymentNumber, p.PaymentDate, p.WithholdingTaxAmount,
            certs.FirstOrDefault(c => c.SourcePaymentId == p.Id && c.Status != WithholdingTaxCertStatus.Voided)?.CertificateNumber)).ToList();
        var hasActive = certs.Any(c => c.Status != WithholdingTaxCertStatus.Voided);
        var portions = await LoadPayeePortionsAsync(companyId, documentId);
        string? firstBlocked = null;
        if (!open) firstBlocked = "ใบสำคัญจ่ายยังไม่อนุมัติ — เลือก \"ออกใบไว้แล้ว\" ในฟอร์มแทน";
        else if (WhtPayeeSplit.IsMultiPayee(portions)) firstBlocked = "ใบนี้มีผู้ถูกหักหลายราย — ผูกใบที่ออกไว้แล้วไม่ได้";
        else if (doc.WhtViaEWithholding) firstBlocked = WhtCertIssuePlan.EWithholdingLabel;
        string? intentNo = doc.WhtLinkCertId is Guid lid
            ? await _db.WithholdingTaxCerts.AsNoTracking().Where(w => w.Id == lid && w.CompanyId == companyId).Select(w => w.CertificateNumber).FirstOrDefaultAsync()
            : null;
        return new WhtDocumentLinkStatus(true, rows, !hasActive && firstBlocked == null, hasActive ? null : firstBlocked, intentNo, payOpts);
    }

    // ───────────────────────────── โมดัลกลับทิศ (จากหน้า 50 ทวิ) ─────────────────────────────

    /// <summary>ใบสำคัญจ่ายที่น่าจะเป็นของใบนี้: ผู้ถูกหักเดียวกัน · ช่วง ±3 เดือน · (ก) ยังไม่มีใบ หรือ (ข) มีใบที่ระบบออกเอง (ผูกแล้วใบนั้นถูกยกเลิก)</summary>
    public async Task<IReadOnlyList<WhtCertPvCandidateDto>> GetPvCandidatesForCertAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        var payeeId = cert.PayeeContactId;
        var mid = new DateTime(cert.TaxYear, cert.TaxMonth, 1);
        var from = mid.AddMonths(-WhtCertLinkPlan.CandidateMonthWindow);
        var to = mid.AddMonths(WhtCertLinkPlan.CandidateMonthWindow + 1);
        var payerTypes = WhtRemitScope.PayerSideTypes.ToList();
        var docs = await _db.Documents.Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && payerTypes.Contains(d.DocumentType) && d.WithholdingTaxAmount > 0m
                && (d.ContactId == payeeId || d.WhtPayeeContactId == payeeId)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && (d.PaymentDate ?? d.DocumentDate) >= from && (d.PaymentDate ?? d.DocumentDate) < to)
            .OrderByDescending(d => d.DocumentDate).Take(20).ToListAsync();
        var docIds = docs.Select(d => d.Id).ToList();
        var actives = await _db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .Where(w => w.CompanyId == companyId && w.DocumentId != null && docIds.Contains(w.DocumentId!.Value)
                && w.Status != WithholdingTaxCertStatus.Voided)
            .ToListAsync();
        var row = (await CandidateFactsAsync(companyId, new[] { cert }))[0];
        var cache = new Dictionary<(TaxType, int, int), string?>();
        var result = new List<(int, WhtCertPvCandidateDto)>();
        foreach (var d in docs)
        {
            var mine = actives.Where(w => w.DocumentId == d.Id).ToList();
            if (mine.Count > 1 || mine.Any(w => w.LinkedAt != null)) continue;   // หลายใบ (รายงวด) / ผูกใบอื่นด้วยมือแล้ว ⇒ ไม่ใช่ผู้สมัคร
            var a = mine.FirstOrDefault();
            var (scopeDate, scopeWht) = await PaymentScopeAsync(companyId, d, a?.SourcePaymentId);
            var ctx = await BuildPvLinkFactsAsync(companyId, d, scopeDate, a?.SourcePaymentId, scopeWht);
            var dec = WhtCertLinkPlan.Decide(ctx.Facts, row.Facts, await PeriodFactsAsync(companyId, a, cert, ctx.Facts, cache));
            result.Add((WhtCertLinkPlan.CandidateScore(ctx.Facts, row.Facts, dec), new WhtCertPvCandidateDto(
                d.Id, d.DocumentNumber, DocumentTypeNames.Title(d.DocumentType, "th"), d.PaymentDate ?? d.DocumentDate, d.WithholdingTaxAmount,
                a?.CertificateNumber, dec.Verdict.ToString(), WhtCertLinkLabels.Result(dec),
                dec.Blocks.Select(b => new WhtLinkIssueDto(b.Code, b.Message)).ToList(),
                dec.Warnings.Select(w => new WhtLinkIssueDto(w.Code, w.Message)).ToList())));
        }
        return result.OrderByDescending(x => x.Item1).Select(x => x.Item2).ToList();
    }

    // ───────────────────────────── D5 ยืนยันว่าไม่มีใบสำคัญจ่าย ─────────────────────────────

    public async Task SetNoSourceDocumentAsync(Guid companyId, Guid certId, WhtCertNoSourceRequest request, Guid? userId)
    {
        var cert = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        if (!request.Clear)
        {
            if (cert.DocumentId != null || cert.SourcePayrollRunId != null)
                throw new BusinessRuleException($"{cert.CertificateNumber} ผูกเอกสาร/รอบเงินเดือนอยู่แล้ว — ไม่ต้องยืนยันว่าไม่มีใบสำคัญจ่าย", WhtCertLinkPlan.NoSourceRuleCode);
            if (cert.Status == WithholdingTaxCertStatus.Voided)
                throw new BusinessRuleException($"{cert.CertificateNumber} ยกเลิกแล้ว", WhtCertLinkPlan.NoSourceRuleCode);
            if (string.IsNullOrWhiteSpace(request.Reason))
                throw new BusinessRuleException("ระบุเหตุผลที่ใบนี้ไม่มีใบสำคัญจ่ายในระบบ (เช่น จ่ายนอกระบบ ลงบัญชีด้วยสมุดรายวันมือ)", WhtCertLinkPlan.NoSourceRuleCode, 400);
        }
        var old = cert.NoSourceDocumentReason;
        cert.NoSourceDocumentReason = request.Clear ? null : request.Reason!.Trim()[..Math.Min(request.Reason!.Trim().Length, 500)];
        cert.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update,
            EntityType = "WithholdingTaxCert", EntityId = cert.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { cert.CertificateNumber, NoSourceDocumentReason = old }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                cert.NoSourceDocumentReason, RuleCode = WhtCertLinkPlan.NoSourceRuleCode, LegalReference = WhtCertLinkPlan.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    // ───────────────────────────── หน้า 50 ทวิ: ตัวกรองฝั่งเซิร์ฟเวอร์ (F2) ─────────────────────────────

    /// <summary>"อาจนับซ้ำ" — เจ้าของเงื่อนไขตัวเดียว <see cref="WhtCertDuplicateSuspect"/> (ฝ่ายค้าน M5 · ขอบเขตบริษัทนี้)</summary>
    private Expression<Func<WithholdingTaxCert, bool>> DuplicateSuspect(Guid companyId)
    {
        return WhtCertDuplicateSuspect.Predicate(_db.WithholdingTaxCerts.Where(y => y.CompanyId == companyId),
            _db.Contacts.IgnoreQueryFilters().Where(c => c.CompanyId == companyId));
    }

    private IQueryable<WithholdingTaxCert> FilteredCerts(Guid companyId, WhtCertListFilter f, bool applyStatusAndLink)
    {
        var query = _db.WithholdingTaxCerts.Where(w => w.CompanyId == companyId);
        if (f.TaxFormType.HasValue) query = query.Where(w => w.TaxFormType == f.TaxFormType.Value);
        if (f.Year.HasValue) query = query.Where(w => w.TaxYear == f.Year.Value);
        if (f.Month.HasValue) query = query.Where(w => w.TaxMonth == f.Month.Value);
        if (f.DocumentId.HasValue) query = query.Where(w => w.DocumentId == f.DocumentId.Value);
        var q = f.Q?.Trim();
        if (!string.IsNullOrEmpty(q))
            query = query.Where(w => w.CertificateNumber.Contains(q)
                || (w.Document != null && w.Document.DocumentNumber.Contains(q))
                || _db.Contacts.IgnoreQueryFilters().Any(c => c.Id == w.PayeeContactId && c.CompanyId == companyId
                    && (c.Name.Contains(q) || (c.TaxId != null && c.TaxId.Contains(q)))));
        if (!applyStatusAndLink) return query;
        if (f.Status.HasValue)
            query = f.Status.Value == WithholdingTaxCertStatus.Issued
                ? query.Where(w => w.Status == WithholdingTaxCertStatus.Issued || w.Status == WithholdingTaxCertStatus.Printed)
                : query.Where(w => w.Status == f.Status.Value);
        query = (f.Link ?? "").Trim().ToLowerInvariant() switch
        {
            "unlinked" => query.Where(WhtCertLinkPlan.IsUnlinked),
            "linked" => query.Where(WhtCertLinkPlan.IsLinked),
            "duplicate" => query.Where(DuplicateSuspect(companyId)),
            _ => query,
        };
        return query;
    }

    public async Task<PagedResponse<WithholdingTaxCertResponse>> GetListAsync(Guid companyId, WhtCertListFilter filter, PagedRequest request)
    {
        var query = FilteredCerts(companyId, filter, applyStatusAndLink: true);
        var total = await query.CountAsync();
        var items = await query.Include(w => w.Lines).Include(w => w.Document)
            .OrderByDescending(w => w.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();
        await _db.HydratePayeeContactsAsync(companyId, items);
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
        var mapped = await DecorateLinkAsync(companyId, items, items.Select(w => MapToResponse(w, company)).ToList());
        return new PagedResponse<WithholdingTaxCertResponse>(mapped, total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<WhtCertListSummary> GetListSummaryAsync(Guid companyId, WhtCertListFilter filter)
    {
        var baseQ = FilteredCerts(companyId, filter, applyStatusAndLink: false);
        var byStatus = await baseQ.GroupBy(w => w.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        int N(params WithholdingTaxCertStatus[] s) => byStatus.Where(x => s.Contains(x.Key)).Sum(x => x.N);
        var unlinked = await baseQ.Where(WhtCertLinkPlan.IsUnlinked).CountAsync();
        var dup = await baseQ.Where(DuplicateSuspect(companyId)).CountAsync();
        var tax = await baseQ.Where(w => WhtCertFilingScope.Filed.Contains(w.Status)).SumAsync(w => (decimal?)w.TotalTaxAmount) ?? 0m;
        return new WhtCertListSummary(byStatus.Sum(x => x.N), N(WithholdingTaxCertStatus.Draft),
            N(WithholdingTaxCertStatus.Issued, WithholdingTaxCertStatus.Printed), N(WithholdingTaxCertStatus.Voided), unlinked, dup, tax);
    }

    /// <summary>เติมช่องการผูก (server computes · page displays): สถานะ · ป้าย "ผูกกับ" · แทนด้วย · ลบ/ยกเลิกได้ไหม · อาจนับซ้ำ</summary>
    private async Task<List<WithholdingTaxCertResponse>> DecorateLinkAsync(Guid companyId, List<WithholdingTaxCert> certs,
        List<WithholdingTaxCertResponse> mapped)
    {
        var supIds = certs.Where(c => c.SupersededByCertId != null).Select(c => c.SupersededByCertId!.Value).Distinct().ToList();
        var supNo = supIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.WithholdingTaxCerts.AsNoTracking().Where(w => w.CompanyId == companyId && supIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => w.CertificateNumber);
        var payIds = certs.Where(c => c.SourcePaymentId != null).Select(c => c.SourcePaymentId!.Value).Distinct().ToList();
        var payDates = payIds.Count == 0 ? new Dictionary<Guid, DateTime>()
            : await _db.Payments.AsNoTracking().Where(p => p.CompanyId == companyId && payIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.PaymentDate);
        // อาจนับซ้ำ (ฝ่ายค้าน M5): ใบของหน้านี้ที่ผ่านเงื่อนไขเดียวกับตัวกรอง/การ์ด แล้วหาคู่ด้วย PeersOf (เจ้าของตัวเดียว WhtCertDuplicateSuspect)
        var pageIds = certs.Select(c => c.Id).ToList();
        var flagged = (await _db.WithholdingTaxCerts.AsNoTracking().Where(w => w.CompanyId == companyId && pageIds.Contains(w.Id))
            .Where(DuplicateSuspect(companyId)).Select(w => w.Id).ToListAsync()).ToHashSet();
        var peerOf = new Dictionary<Guid, (string No, Guid DocId, string? DocNo, bool SamePayment)>();
        foreach (var c in certs.Where(c => flagged.Contains(c.Id)))
        {
            var p = await WhtCertDuplicateSuspect.PeersOf(_db.WithholdingTaxCerts.AsNoTracking().Where(y => y.CompanyId == companyId),
                    _db.Contacts.IgnoreQueryFilters().Where(x => x.CompanyId == companyId), c)
                .Where(y => y.DocumentId != null)
                .OrderByDescending(y => y.CreatedAt)
                .Select(y => new { y.CertificateNumber, DocId = y.DocumentId!.Value, DocNo = y.Document != null ? y.Document.DocumentNumber : null })
                .FirstOrDefaultAsync();
            if (p != null) peerOf[c.Id] = (p.CertificateNumber, p.DocId, p.DocNo, c.DocumentId != null && c.DocumentId == p.DocId);
        }
        var result = new List<WithholdingTaxCertResponse>(mapped.Count);
        for (var i = 0; i < certs.Count; i++)
        {
            var c = certs[i];
            var state = WhtCertLinkPlan.StateOf(c.Status, c.DocumentId, c.SourcePayrollRunId, c.NoSourceDocumentReason);
            DateTime? payDate = c.SourcePaymentId is Guid sp && payDates.TryGetValue(sp, out var pd) ? pd : null;
            string? supersededBy = c.SupersededByCertId is Guid s && supNo.TryGetValue(s, out var n) ? n : null;
            string? dupLabel = null;
            Guid? dupDocId = null;
            if (peerOf.TryGetValue(c.Id, out var peer))
            {
                dupLabel = WhtCertDuplicateSuspect.Label(peer.No, peer.DocNo, peer.SamePayment);
                dupDocId = peer.DocId;
            }
            result.Add(mapped[i] with
            {
                LinkState = state.ToString(),
                LinkLabel = WhtCertLinkPlan.LinkLabel(state, c.Document?.DocumentType, c.Document?.DocumentNumber, payDate,
                    c.TaxYear, c.TaxMonth, supersededBy, c.NoSourceDocumentReason),
                LinkedDocumentTypeLabel = c.Document != null ? DocumentTypeNames.Title(c.Document.DocumentType, "th") : null,
                SupersededByCertNumber = supersededBy,
                DeleteAllowed = WhtCertLinkPlan.DeleteAllowed(c.CertificateNumber),
                VoidAllowed = WhtCertLinkPlan.VoidAllowed(c.Status),
                CanLinkToPv = state is WhtCertLinkState.Unlinked or WhtCertLinkState.NoSourceConfirmed,
                LinkedManually = c.LinkedAt != null,
                NoSourceDocumentReason = c.NoSourceDocumentReason,
                DuplicateSuspectLabel = dupLabel,
                DuplicateSuspectDocumentId = dupDocId,
            });
        }
        return result;
    }
}
