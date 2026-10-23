using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>
/// <b>คำตัดสินเจ้าของข้อ 157 (2026-10-23 · ทีม P) — จังหวะออกเอกสารภาษีของที่พัก</b> · ตัวตัดสินเดียว <see cref="LodgingTaxInvoiceTimingPolicy"/>
///
/// <para>โหมด <see cref="BookingInvoiceTiming.TaxInvoiceAtBooking"/>: ใบกำกับภาษีเต็มยอดการเข้าพัก ("ใบกำกับล่วงหน้า") ออกเมื่อพนักงานยืนยัน หรือรับเงินครั้งแรก
/// ด้วย <b>เส้นเดิมทั้งหมด</b> — ตัวสร้างรายการใบสุดท้าย (<c>BuildFinalInvoiceLinesAsync</c>) · ใบร่างกันกดซ้ำ (<c>UpsertFinalDraftAsync</c>) ·
/// <c>ApproveDocumentAsync</c> · ใบลดหนี้ = <c>CreateDocumentAsync(CreditNote)</c> อ้างใบเดิม · ตัดชำระ = <c>ApplyDepositToInvoiceAsync</c> ·
/// เงินที่รับหลังออกใบ = ใบรับเงินมัดจำไม่มี VAT (<c>CreateDepositReceiptAsync</c> เส้นเดิม รูป "เต็มยอด") ⇒ ยกเลิก/คืนเงินใช้กลไกมัดจำเดิม</para>
/// <para>ทุกทางเข้าตัดสินจาก "ใบที่ผูกจริง" (<c>AdvanceTaxDocumentId</c> + สถานะเอกสาร) ไม่ใช่ค่าตั้งปัจจุบัน ⇒ สลับค่าตั้งกลางทาง tax point ไม่หลุด ·
/// ใบล่วงหน้าถูกยกเลิก = การจองกลับไปใช้จังหวะปกติ</para>
/// </summary>
public partial class LodgingService
{
    /// <summary>ใบกำกับล่วงหน้าที่ผูกกับการจอง + ยอดคงเหลือหลังหักใบลดหนี้/บวกใบเพิ่มหนี้ที่อ้างใบนั้น</summary>
    private sealed record LodgingAdvanceDoc(Guid Id, string Number, DocumentType Type, Guid ContactId,
        decimal NetRemaining, decimal GrossRemaining, LodgingAdvanceState State);

    /// <summary>อ่านใบกำกับล่วงหน้าที่ผูก (tenant-safe) — ไม่มีลิงก์ = null · ลิงก์ชี้ใบที่หาไม่พบ/ถูกลบ/ยกเลิก = <see cref="LodgingAdvanceState.Gone"/></summary>
    private async Task<LodgingAdvanceDoc?> LoadAdvanceAsync(Guid companyId, LodgingReservation r)
    {
        if (r.AdvanceTaxDocumentId is not Guid id) return null;
        var d = await _db.Documents.AsNoTracking()
            .Where(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => new { x.Id, x.DocumentNumber, x.Status, x.DocumentType, x.ContactId, x.SubTotal, x.TotalAmount })
            .FirstOrDefaultAsync();
        if (d == null)
            return new LodgingAdvanceDoc(id, "", DocumentType.TaxInvoice, Guid.Empty, 0m, 0m, LodgingTaxInvoiceTimingPolicy.StateOf(true, null));
        var adjustments = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.RelatedDocumentId == id
                && (x.DocumentType == DocumentType.CreditNote || x.DocumentType == DocumentType.DebitNote)
                && x.Status != DocumentStatus.Draft && x.Status != DocumentStatus.WaitingApproval
                && x.Status != DocumentStatus.Voided && x.Status != DocumentStatus.Rejected)
            .Select(x => new { x.DocumentType, x.SubTotal, x.TotalAmount })
            .ToListAsync();
        decimal Sign(DocumentType t) => t == DocumentType.CreditNote ? -1m : 1m;
        return new LodgingAdvanceDoc(d.Id, d.DocumentNumber, d.DocumentType, d.ContactId,
            d.SubTotal + adjustments.Sum(a => Sign(a.DocumentType) * a.SubTotal),
            d.TotalAmount + adjustments.Sum(a => Sign(a.DocumentType) * a.TotalAmount),
            LodgingTaxInvoiceTimingPolicy.StateOf(true, d.Status));
    }

    /// <summary>ข้อเท็จจริงของตัวตัดสิน — ค่าตั้งที่พัก (เฉพาะ "ออกใบใหม่ไหม") + เอกสารที่ผูกจริง</summary>
    private async Task<LodgingAdvanceFacts> AdvanceFactsAsync(Guid companyId, LodgingReservation r, LodgingAdvanceDoc? adv)
    {
        var prop = r.Property ?? await RequirePropertyAsync(companyId, r.PropertyId);
        // ฝ่ายค้านทีม P ข้อ 1: สถานะ/ใบเช็คเอาต์อ่านใหม่จากฐาน (ผู้เรียกถือล็อกต่อการจองแล้ว — เส้นยกเลิก/เช็คเอาต์ถือล็อกเดียวกัน) ·
        // แถวที่ติดตามอยู่อาจถูกอ่านไว้ก่อนได้ล็อก ⇒ ห้ามตัดสินจากค่าค้าง (ยกเลิก/เช็คเอาต์/เช็คอินแล้ว ⇒ ไม่ออก)
        var fresh = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.Id == r.Id && x.CompanyId == companyId)
            .Select(x => new { x.Status, x.FinalDocumentId })
            .FirstOrDefaultAsync();
        var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        return new LodgingAdvanceFacts(prop.TaxInvoiceTiming, prop.AccountingMode, fresh?.Status ?? r.Status, r.Source,
            adv?.State ?? LodgingAdvanceState.None,
            HasVatBearingRoomDeposit: deposits.Any(d => d.VatAmount > 0.005m && LodgingDepositSettlement.Remaining(d).Gross > 0.005m),
            HasFinalDocument: (fresh?.FinalDocumentId ?? r.FinalDocumentId) != null);
    }

    /// <summary>
    /// ออกใบกำกับล่วงหน้าถ้าถึงจังหวะ (ตัวตัดสินเดียว) — คืนใบที่มีผล (เดิมหรือที่เพิ่งออก) หรือ null (ไม่ถึงจังหวะ/ค่าเริ่มต้น/ใบเดิมถูกยกเลิก) ·
    /// <see cref="LodgingTaxTrigger.ManualIssue"/> ที่ออกไม่ได้ ⇒ ปฏิเสธพร้อมเหตุผล · ผู้เรียกต้องถือล็อกต่อการจอง (<c>AdvisoryLockKey.LodgingConfirm</c>)
    /// </summary>
    private async Task<LodgingAdvanceDoc?> EnsureAdvanceTaxInvoiceAsync(Guid companyId, LodgingReservation r, LodgingTaxTrigger trigger,
        string userId, bool issueToCompany = false)
    {
        var adv = await LoadAdvanceAsync(companyId, r);
        // ฝ่ายค้านทีม P ข้อ 5: ค่าเริ่มต้นที่ไม่เคยผูกใบล่วงหน้า = ไม่อ่านฐานเพิ่ม (ผลเดิมทุกตัวอักษร) — กติกาข้อแรกของตัวตัดสินเดียว
        var timing = (r.Property ?? await RequirePropertyAsync(companyId, r.PropertyId)).TaxInvoiceTiming;
        if (adv == null && trigger != LodgingTaxTrigger.ManualIssue && !LodgingTaxInvoiceTimingPolicy.TimingAllowsAdvance(timing))
            return null;
        if (adv?.State == LodgingAdvanceState.Live)
        {
            if (trigger == LodgingTaxTrigger.ManualIssue)
                throw new BusinessRuleException($"การจองนี้ออกใบกำกับภาษีล่วงหน้า {adv.Number} แล้ว — ไม่ออกซ้ำ", LodgingTaxInvoiceTimingPolicy.RuleCode);
            return adv;
        }
        var decision = LodgingTaxInvoiceTimingPolicy.Decide(await AdvanceFactsAsync(companyId, r, adv), trigger);
        if (!decision.Issue)
        {
            if (trigger == LodgingTaxTrigger.ManualIssue)
                throw new BusinessRuleException(decision.Notice ?? "ออกใบกำกับภาษีล่วงหน้าไม่ได้ในสถานะนี้", LodgingTaxInvoiceTimingPolicy.RuleCode);
            return null;
        }
        return await IssueAdvanceTaxInvoiceCoreAsync(companyId, r, trigger, userId, issueToCompany);
    }

    /// <summary>เส้นยืนยัน/สร้างแบบยืนยันโดยพนักงาน (ไม่มีเงินเข้า): ล้ม = ล้มดังบนตัวการจอง (หมายเหตุ + audit + ป้าย/ปุ่มบนหน้า) แต่ไม่ย้อนการยืนยัน ·
    /// ทางไปต่อ = ปุ่ม “ออกใบกำกับภาษีล่วงหน้า” (tax point ไม่หลุด: ยังไม่มีใบ ⇒ เงินที่รับ/เช็คเอาต์ออกเอกสารตามจังหวะปกติ)</summary>
    private async Task TryIssueAdvanceOnStaffConfirmAsync(Guid companyId, LodgingReservation r, LodgingTaxTrigger trigger, string userId)
    {
        try
        {
            await EnsureAdvanceTaxInvoiceAsync(companyId, r, trigger, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendInternal(r, $"⚠️ ยืนยันการจองแล้ว แต่ออกใบกำกับภาษีล่วงหน้าไม่สำเร็จ ({ex.Message}) — แก้สาเหตุแล้วกด “ออกใบกำกับภาษีล่วงหน้า” "
                + "(ถ้ายังไม่ได้ออก เงินที่รับจะออกใบมัดจำตามประเภทเงินมัดจำ และใบกำกับออกตอนเช็คเอาต์)");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "AdvanceTaxInvoiceFailed", trigger = trigger.ToString(), error = ex.Message, by = userId }));
            await _db.SaveChangesAsync();
            _logger.LogError(ex, "ออกใบกำกับภาษีล่วงหน้าของการจอง {No} ไม่สำเร็จ ({Trigger})", r.ReservationNumber, trigger);
        }
    }

    /// <summary>ออกใบกำกับภาษีล่วงหน้า — รายการจากตัวสร้างรายการใบสุดท้ายตัวเดียว (ค่าห้อง · บริการเสริม · service charge · ไม่รวม folio) ·
    /// ไม่จด VAT/ที่พักไม่คิด VAT ⇒ ใบแจ้งหนี้ (§90/2 · ชนิดเดียวกับใบเช็คเอาต์) · ไม่ตั้ง ServiceUsedDate (ใบออกก่อนใช้บริการ — tax point = วันออกใบ)</summary>
    private async Task<LodgingAdvanceDoc> IssueAdvanceTaxInvoiceCoreAsync(Guid companyId, LodgingReservation r, LodgingTaxTrigger trigger,
        string userId, bool issueToCompany)
    {
        var prop = r.Property ?? await RequirePropertyAsync(companyId, r.PropertyId);
        var (registered, vatRate) = await VatProfileAsync(companyId, prop);
        var docType = vatRate > 0 ? DocumentType.TaxInvoice : DocumentType.Invoice;
        var contactId = r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact) — แก้ไขข้อมูลแขกก่อน");
        if (issueToCompany)
        {
            if (string.IsNullOrWhiteSpace(r.GuestTaxId) || string.IsNullOrWhiteSpace(r.GuestCompanyName))
                throw new BusinessRuleException("ออกใบกำกับในนามบริษัทต้องมีชื่อบริษัท + เลขผู้เสียภาษีของแขก (§86/4)", "RD-86/4");
            contactId = await FindOrCreateContactAsync(companyId, new LodgingCreateReservationRequest(r.CheckInDate, r.CheckOutDate, new(), r.GuestName, r.GuestEmail, r.GuestPhone,
                GuestAddress: r.GuestAddress, GuestTaxId: r.GuestTaxId, GuestCompanyName: r.GuestCompanyName), userId);
        }
        // มัดจำไม่มี VAT ที่รับไว้ก่อน (ใบล่วงหน้าครอบคลุมและตัดชำระตอนเช็คเอาต์/ยกเลิก) ต้องเป็นผู้ติดต่อเดียวกับใบ
        var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var wrongContact = LodgingDepositSettlement.ApplyCandidates(deposits).FirstOrDefault(a => a.ContactId != contactId);
        if (wrongContact != null)
            throw new BusinessRuleException(
                $"มัดจำ {wrongContact.Number} ออกในนามผู้ติดต่ออื่น — ใบกำกับล่วงหน้าต้องออกในนามเดียวกันจึงตัดชำระได้ · ออกในนามผู้เข้าพัก หรือคืนมัดจำใบเดิมก่อน",
                "LODGING-DEPOSIT-CONTACT");
        var lines = await BuildFinalInvoiceLinesAsync(companyId, r, prop, registered, vatRate, Array.Empty<LodgingFolioCharge>());
        if (lines.Count == 0) throw new BusinessRuleException("การจองไม่มีรายการให้ออกใบกำกับ", LodgingTaxInvoiceTimingPolicy.RuleCode);
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var create = new CreateDocumentRequest(
            DocumentType: docType, DocumentDate: DateTime.UtcNow,
            // ชำระก่อนวันเข้าพัก (เลยวันแล้ว = วันนี้) — ใบเครดิต ไม่ใช่ขายเงินสด
            DueDate: r.CheckInDate > today ? r.CheckInDate : today,
            ContactId: contactId, Reference: r.ReservationNumber,
            Notes: $"ใบกำกับภาษีล่วงหน้า — เข้าพัก {prop.Name} {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน) · จอง {r.ReservationNumber}",
            Lines: lines,
            PricesIncludeVat: prop.PricesIncludeVat,
            BranchId: prop.BranchId,
            BookingNumber: r.ReservationNumber);
        // ฝ่ายค้านทีม P ข้อ 6: ใบร่างเช็คเอาต์ที่ค้าง (มีวันใช้บริการ) ห้ามถูกใช้ต่อเป็นใบล่วงหน้า — UpdateDocumentRequest.ServiceUsedDate null = "คงค่าเดิม"
        // ⇒ ใบล่วงหน้าจะติดวันใช้บริการเดิม (tax point ผิด) · ใบร่างไม่มีเลขจริง ลบแล้วไม่เกิดช่องว่างของเลข (§86/4) — กติกาเดียวกับ LodgingCheckoutDraft.Plan
        var staleCheckoutDrafts = await _db.Documents.AsNoTracking()
            .Where(LodgingCheckoutDraft.LeftoverOf(companyId, r.ReservationNumber, LodgingOrigin))
            .Where(d => d.ServiceUsedDate != null)
            .Select(d => d.Id).ToListAsync();
        foreach (var staleId in staleCheckoutDrafts)
            await _docService.DeleteDocumentAsync(companyId, staleId);
        var draftId = await UpsertFinalDraftAsync(companyId, r, create, docType, userId);
        var approved = await _docService.ApproveDocumentAsync(companyId, draftId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        r.AdvanceTaxDocumentId = approved.Id;
        r.ContactId = contactId;
        AppendInternal(r, $"ออก{(docType == DocumentType.TaxInvoice ? "ใบกำกับภาษี" : "ใบแจ้งหนี้")}ล่วงหน้า {approved.DocumentNumber} ยอด {approved.TotalAmount:N2} "
            + $"({TriggerTh(trigger)} · ค่าตั้ง “ใบกำกับเมื่อยืนยันการจอง”) — เงินที่รับต่อจากนี้ออกใบรับเงินไม่มี VAT และตัดชำระใบนี้ตอนเช็คเอาต์");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Create, r, new
        {
            action = "AdvanceTaxInvoiceIssued", document = approved.DocumentNumber, docType = docType.ToString(), trigger = trigger.ToString(),
            timing = prop.TaxInvoiceTiming.ToString(), total = approved.TotalAmount, vat = approved.VatAmount,
            legalReference = "RD-78/1(1)", by = userId,
        }));
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new LodgingAdvanceDoc(approved.Id, approved.DocumentNumber, docType, contactId, approved.SubTotal, approved.TotalAmount, LodgingAdvanceState.Live);
    }

    private static string TriggerTh(LodgingTaxTrigger t) => t switch
    {
        LodgingTaxTrigger.StaffConfirm => "พนักงานยืนยันการจอง",
        LodgingTaxTrigger.StaffCreateConfirmed => "พนักงานสร้างการจองแบบยืนยันทันที",
        LodgingTaxTrigger.MoneyReceived => "รับเงินครั้งแรก",
        _ => "พนักงานกดออกเอง",
    };

    /// <summary>ปุ่ม “ออกใบกำกับภาษีล่วงหน้า” (สิทธิ์ LodgingManage) — ล็อกเดียวกับเส้นรับเงิน/ยืนยัน (webhook กับคนกดพร้อมกันได้ใบเดียว)</summary>
    public async Task<LodgingReservationResponse> IssueAdvanceTaxInvoiceAsync(Guid companyId, Guid reservationId,
        LodgingIssueAdvanceTaxInvoiceRequest request, string userId)
    {
        await EnsureReservationExistsAsync(companyId, reservationId);   // 404 ก่อนขอล็อก
        return await WithReservationLockAsync(companyId, reservationId, AdvisoryLockKey.LodgingConfirm, async () =>
        {
            var r = await RequireReservationAsync(companyId, reservationId);
            await _db.Entry(r).ReloadAsync();
            await EnsureAdvanceTaxInvoiceAsync(companyId, r, LodgingTaxTrigger.ManualIssue, userId, request.IssueToCompany);
            return await MapAsync(companyId, r, true, true);
        });
    }

    /// <summary>ใบลดหนี้อ้างใบกำกับล่วงหน้า (§86/10 · เหตุ "ปรับยอด") — กระจายยอดลงบรรทัดเดิมตามสัดส่วน (ผัง/อัตรา VAT เดิม · ราคารวม VAT ⇒ ยอดรวมใบ = ยอดที่ลดพอดี) ·
    /// เส้นสร้าง/อนุมัติเอกสารเดิม (ด่าน ลดหนี้ไม่เกินยอดใบเดิม · ผู้ติดต่อเดียวกัน) · คืนใบที่อนุมัติแล้ว หรือ null เมื่อไม่มียอดให้ลด</summary>
    private async Task<DocumentResponse?> IssueAdvanceCreditNoteAsync(Guid companyId, LodgingReservation r, LodgingAdvanceDoc adv,
        decimal creditGross, string reason, string userId)
    {
        var original = await _docService.GetDocumentAsync(companyId, adv.Id);
        var lines = original.Lines.OrderBy(l => l.LineOrder).ToList();
        var alloc = LodgingTaxInvoiceTimingPolicy.AllocateCredit(lines.Select(l => l.Amount + l.VatAmount).ToList(), creditGross);
        var cnLines = new List<DocumentLineRequest>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (alloc[i] <= 0m) continue;
            cnLines.Add(new DocumentLineRequest(Description: $"ลดหนี้: {lines[i].Description}", Quantity: 1, Unit: "รายการ", UnitPrice: alloc[i],
                DiscountPercent: 0, VatRate: lines[i].VatRate, WithholdingTaxRate: 0, AccountId: lines[i].AccountId, ProductCode: lines[i].ProductCode));
        }
        if (cnLines.Count == 0) return null;
        var create = new CreateDocumentRequest(
            DocumentType: DocumentType.CreditNote, DocumentDate: DateTime.UtcNow, DueDate: null,
            ContactId: adv.ContactId, Reference: original.DocumentNumber,
            Notes: $"{reason} · อ้างใบกำกับภาษี {original.DocumentNumber} ลงวันที่ {original.DocumentDate:dd/MM/yyyy} · จอง {r.ReservationNumber}",
            Lines: cnLines,
            RelatedDocumentId: original.Id,
            CnDnPurchaseSideOverride: false,
            CreditNoteReason: CreditNoteReason.Adjustment,
            PricesIncludeVat: true,
            BranchId: r.Property?.BranchId,
            BookingNumber: r.ReservationNumber);
        var cn = await _docService.CreateDocumentAsync(companyId, create, userId, LodgingOrigin);
        var approved = await _docService.ApproveDocumentAsync(companyId, cn.Id, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Create, r, new
        {
            action = "AdvanceTaxInvoiceCredited", advance = adv.Number, creditNote = approved.DocumentNumber, credit = approved.TotalAmount,
            reason, legalReference = "RD-86/10", by = userId,
        }));
        return approved;
    }

    /// <summary>
    /// ยกเลิก/ไม่มา เมื่อมีใบกำกับล่วงหน้าที่ยังมีผล — ภาษีของค่าห้องอยู่ในใบนั้นแล้ว ⇒ ใบลดหนี้ส่วนที่ไม่เรียกเก็บ แล้วตัดชำระใบนั้นด้วยมัดจำที่ริบ
    /// (แทนการริบเป็นรายได้ ซึ่งมัดจำไม่มี VAT จะถูกออกใบกำกับของยอดที่ริบซ้อนกับใบล่วงหน้า) · ส่วนที่ต้องคืน = กลไกเดิม (ยอดค้างคืน → “ยืนยันคืนเงินแล้ว”) ·
    /// มัดจำที่มี VAT (ไม่ควรมีคู่กับใบล่วงหน้า) ริบด้วยเส้นเดิม · ล้มกลางทาง = ล้มดัง 3 ที่ + ทางไปต่อที่หน้าเอกสาร (สถานะยกเลิกคงไว้ — ห้ามลงซ้ำตอนกดใหม่)
    /// </summary>
    private async Task SettleAdvanceOnCancelAsync(Guid companyId, LodgingReservation r, LodgingAdvanceDoc adv, LodgingCancelPlan plan,
        IReadOnlyList<LodgingDepositSnapshot> deposits, bool noShow, string actor)
    {
        var vatBearing = deposits.Where(d => d.VatAmount > 0.005m).Select(d => d.Id).ToHashSet();
        // มัดจำไม่มี VAT: ฐาน = ยอดรวม (ForfeitBase ใช้เป็นยอดตัดชำระได้ตรง)
        var toAdvance = plan.Lines.Where(l => l.ForfeitBase > 0.005m && !vatBearing.Contains(l.Id)).ToList();
        var toRealize = plan.Lines.Where(l => l.ForfeitBase > 0.005m && vatBearing.Contains(l.Id)).ToList();
        var creditGross = LodgingTaxInvoiceTimingPolicy.CancelCreditGross(adv.GrossRemaining, toAdvance.Sum(l => l.ForfeitBase));
        var done = new List<string>();
        try
        {
            if (creditGross > 0.005m)
            {
                var cn = await IssueAdvanceCreditNoteAsync(companyId, r, adv, creditGross,
                    noShow ? "ลูกค้าไม่มาเข้าพัก — ลดหนี้ส่วนที่ไม่เรียกเก็บ" : "ยกเลิกการจอง — ลดหนี้ส่วนที่ไม่เรียกเก็บ", actor);
                if (cn != null) done.Add($"ใบลดหนี้ {cn.DocumentNumber} ยอด {cn.TotalAmount:N2}");
            }
            var advDoc = await _docService.GetDocumentAsync(companyId, adv.Id);
            foreach (var l in toAdvance)
            {
                var amount = Math.Min(l.ForfeitBase, advDoc.BalanceDue);
                if (amount <= 0.005m) continue;
                advDoc = await _docService.ApplyDepositToInvoiceAsync(companyId, adv.Id, new ApplyDepositRequest(l.Id, amount, DateTime.UtcNow), actor);
                done.Add($"ตัดชำระ {adv.Number} ด้วยมัดจำ {l.Number} {amount:N2}");
            }
            if (toRealize.Count > 0)
            {
                var channelVatRate = await EffectiveVatRateAsync(companyId, r.Property!);
                foreach (var l in toRealize)
                {
                    await _docService.RealizeDepositAsync(companyId, l.Id,
                        new RealizeDepositRequest(l.ForfeitBase, DateTime.UtcNow, r.Property!.CancellationFeeAccountCode ?? r.Property.RoomRevenueAccountCode,
                            ForfeitAs: DepositForfeitAs.PriceOrFee), actor, channelVatRate: channelVatRate);
                    done.Add($"ริบมัดจำ {l.Number} ฐาน {l.ForfeitBase:N2}");
                }
            }
            AppendInternal(r, $"{(noShow ? "ไม่มาเข้าพัก" : "ยกเลิก")}หลังออกใบกำกับภาษีล่วงหน้า {adv.Number}: "
                + (done.Count == 0 ? "ไม่มีรายการต้องปรับ" : string.Join(" · ", done))
                + (advDoc.BalanceDue > 0.005m ? $" · ใบ {adv.Number} ยังค้าง {advDoc.BalanceDue:N2}" : ""));
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
            {
                action = "CancelAdvanceSettled", advance = adv.Number, credit = creditGross, steps = done, balanceLeft = advDoc.BalanceDue, by = actor,
            }));
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fix = $"ออกใบลดหนี้อ้างใบ {adv.Number} ยอด {creditGross:N2} (ถ้ายังไม่มี) แล้วกด “หักมัดจำ” เข้าใบนั้นที่หน้าเอกสาร";
            AppendInternal(r, $"⚠️ {(noShow ? "บันทึกไม่มาเข้าพัก" : "ยกเลิก")}แล้ว แต่ปรับใบกำกับภาษีล่วงหน้า {adv.Number} ไม่ครบ ({ex.Message}) — ทำแล้ว: "
                + (done.Count == 0 ? "ยังไม่มี" : string.Join(" · ", done)) + $" · ที่เหลือ: {fix}");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "CancelAdvanceFailed", advance = adv.Number, error = ex.Message, done, by = actor }));
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"{(noShow ? "บันทึกไม่มาเข้าพัก" : "ยกเลิกการจอง")}แล้ว แต่ปรับใบกำกับภาษีล่วงหน้า {adv.Number} ไม่ครบ ({ex.Message}) — {fix}",
                "LODGING-CANCEL-ADVANCE");
        }
    }

    /// <summary>
    /// เช็คเอาต์เมื่อมีใบกำกับล่วงหน้าที่ยังมีผล — ค่าห้องอยู่ในใบนั้นแล้ว ⇒ (1) ยอดเข้าพักลดลง (เลื่อนวัน) = ใบลดหนี้อ้างใบเดิม · (2) มัดจำไม่มี VAT ตัดชำระใบล่วงหน้าก่อน ·
    /// (3) ใบเช็คเอาต์ = เฉพาะรายการระหว่างพัก + ส่วนที่เพิ่ม (เลื่อนวัน/เช็คอินก่อนวัน) — ไม่มีอะไรเพิ่ม ⇒ ไม่ออกใบใหม่ ใบล่วงหน้าคือใบของการเข้าพัก ·
    /// ใช้ตัวสร้างรายการ/ใบร่าง/แผนมัดจำ/ขั้นปิดการเข้าพักชุดเดียวกับเช็คเอาต์ปกติ · ด่านทั้งหมดก่อนแตะข้อมูล
    /// </summary>
    private async Task<LodgingReservationResponse> CheckOutWithAdvanceAsync(Guid companyId, LodgingReservation r, LodgingAdvanceDoc adv,
        LodgingCheckOutRequest request, string userId)
    {
        var prop = r.Property;
        var (registered, vatRate) = await VatProfileAsync(companyId, prop);
        var docType = vatRate > 0 ? DocumentType.TaxInvoice : DocumentType.Invoice;

        // ── ด่านทั้งหมดก่อนแตะข้อมูล ──
        if (adv.Type != docType)
            throw new BusinessRuleException(
                $"ใบกำกับล่วงหน้า {adv.Number} ออกเป็น{(adv.Type == DocumentType.TaxInvoice ? "ใบกำกับภาษี" : "ใบแจ้งหนี้")} แต่สถานะ VAT ของที่พักตอนนี้ต่างไป — "
                + "คืนค่า “คิด VAT” ของที่พักก่อนเช็คเอาต์ หรือให้นักบัญชียกเลิกใบล่วงหน้าแล้วเช็คเอาต์ตามปกติ", "LODGING-ADVANCE-VAT-MISMATCH");
        var contactId = adv.ContactId;
        if (request.IssueTaxInvoiceToCompany)
        {
            if (string.IsNullOrWhiteSpace(r.GuestTaxId) || string.IsNullOrWhiteSpace(r.GuestCompanyName))
                throw new BusinessRuleException("ออกใบกำกับในนามบริษัทต้องมีชื่อบริษัท + เลขผู้เสียภาษีของแขก (§86/4)", "RD-86/4");
            var companyContact = await FindOrCreateContactAsync(companyId, new LodgingCreateReservationRequest(r.CheckInDate, r.CheckOutDate, new(), r.GuestName, r.GuestEmail, r.GuestPhone,
                GuestAddress: r.GuestAddress, GuestTaxId: r.GuestTaxId, GuestCompanyName: r.GuestCompanyName), userId);
            if (companyContact != adv.ContactId)
                throw new BusinessRuleException(
                    $"ใบกำกับล่วงหน้า {adv.Number} ออกในนามผู้ติดต่ออื่นแล้ว — ใบส่วนเพิ่มต้องออกในนามเดียวกัน (ไม่ต้องติ๊ก “ในนามบริษัท”) "
                    + "หรือให้นักบัญชียกเลิกใบล่วงหน้าแล้วเช็คเอาต์ใหม่", "LODGING-ADVANCE-CONTACT");
        }
        var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var wrongContact = LodgingDepositSettlement.ApplyCandidates(deposits).FirstOrDefault(a => a.ContactId != contactId);
        if (wrongContact != null)
            throw new BusinessRuleException(
                $"มัดจำ {wrongContact.Number} ออกในนามผู้ติดต่ออื่น ไม่ใช่ผู้ซื้อของใบกำกับล่วงหน้า {adv.Number} — ตัดชำระไม่ได้ · คืนมัดจำใบนั้นแล้วรับใหม่",
                "LODGING-DEPOSIT-CONTACT");

        var newCharges = await NewCheckoutChargesAsync(companyId, r, prop, request, userId);
        var pendingCharges = r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending).Concat(newCharges).ToList();
        // ยอดเข้าพักตอนนี้ (ตัวสร้างรายการใบสุดท้ายตัวเดียว · ไม่รวม folio) เทียบใบล่วงหน้าหลังหักใบลดหนี้
        var stayLines = await BuildFinalInvoiceLinesAsync(companyId, r, prop, registered, vatRate, Array.Empty<LodgingFolioCharge>());
        var stayNow = DocumentService.PreviewTotals(stayLines, prop.PricesIncludeVat, 0m);
        var delta = LodgingTaxInvoiceTimingPolicy.StayDelta(prop.PricesIncludeVat, stayNow.Net, stayNow.Total, adv.NetRemaining, adv.GrossRemaining);
        var extraLines = await BuildFinalInvoiceLinesAsync(companyId, r, prop, registered, vatRate, pendingCharges, includeStay: false);
        if (delta.IsCharge)
            extraLines.Insert(0, new DocumentLineRequest(
                Description: $"ส่วนต่างค่าห้องพัก/บริการ (เลื่อนวัน/เพิ่มคืน) จากใบกำกับภาษีล่วงหน้า {adv.Number}",
                Quantity: 1, Unit: "รายการ", UnitPrice: delta.InPriceConvention, DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0,
                AccountId: null, AccountCode: prop.RoomRevenueAccountCode));

        var advDoc = await _docService.GetDocumentAsync(companyId, adv.Id);
        var untaxed = deposits.Where(d => d.VatAmount <= 0.005m).ToList();
        if (request.CollectBalanceNow)
        {
            var extrasTotal = extraLines.Count == 0 ? 0m : DocumentService.PreviewTotals(extraLines, prop.PricesIncludeVat, 0m).Total;
            var estimate = Math.Max(0m, advDoc.BalanceDue + Math.Min(0m, delta.Gross)) + extrasTotal
                - untaxed.Sum(d => LodgingDepositSettlement.Remaining(d).Gross);
            if (LodgingPaymentBank.Problem(request.PaymentMethod, estimate, request.BankAccountId) is { } advBankProblem)
                throw new BusinessRuleException(advBankProblem, LodgingPaymentBank.RuleCode);
        }

        // ── (1) ยอดลด ⇒ ใบลดหนี้ก่อน (ยอดค้างของใบล่วงหน้าต้องเป็นยอดหลังลดหนี้) · กดซ้ำ: ยอดคงเหลือหักใบลดหนี้แล้ว ⇒ ส่วนต่าง 0 ไม่ลดซ้ำ ──
        if (delta.IsCredit)
        {
            var cn = await IssueAdvanceCreditNoteAsync(companyId, r, adv, -delta.Gross, "ยอดเข้าพักลดลงจากใบกำกับภาษีล่วงหน้า (เลื่อนวัน/ปรับราคา)", userId);
            if (cn != null) AppendInternal(r, $"ยอดเข้าพักลดลง {-delta.Gross:N2} จากใบกำกับภาษีล่วงหน้า {adv.Number} — ออกใบลดหนี้ {cn.DocumentNumber}");
            advDoc = await _docService.GetDocumentAsync(companyId, adv.Id);
        }

        if (extraLines.Count == 0)
        {
            // ไม่มีรายการเพิ่ม — ใบล่วงหน้าคือใบของการเข้าพักนี้ · ผูกเป็นใบสุดท้าย (ล้มหลังจากนี้ = กดเช็คเอาต์ซ้ำทำต่อผ่าน ResumeCheckOutAsync)
            r.FinalDocumentId = adv.Id;
            await _db.SaveChangesAsync();
            var planAdvanceOnly = LodgingDepositSettlement.PlanCheckout(untaxed, 0m, _ => advDoc.BalanceDue);
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
            {
                action = "CheckOutOnAdvanceInvoice", advance = adv.Number, credit = delta.IsCredit ? -delta.Gross : 0m,
                depositApplied = planAdvanceOnly.GrossApplied, refundDue = planAdvanceOnly.ExcessGross, by = userId,
            }));
            return await SettleCheckOutAsync(companyId, r, advDoc, planAdvanceOnly, request, userId);
        }

        // ── (2) มัดจำไม่มี VAT ตัดชำระใบล่วงหน้าก่อน · (3) เก็บยอดค้างของใบล่วงหน้าตอนนี้ (ถ้าเลือก) — ทำก่อนออกใบส่วนเพิ่ม ⇒ ล้มหลังจากนี้กดซ้ำได้ไม่ซ้ำ ──
        var planAdvance = LodgingDepositSettlement.PlanCheckout(untaxed, 0m, _ => advDoc.BalanceDue);
        advDoc = await ApplyFinalDepositPlanAsync(companyId, prop, advDoc, new LodgingCheckoutDepositPlan(
            Array.Empty<TaxedDepositDeduction>(), planAdvance.Apply, Array.Empty<LodgingDepositExcess>()), userId);
        if (request.CollectBalanceNow && advDoc.BalanceDue > 0.005m)
        {
            var due = advDoc.BalanceDue;
            await _docService.CreatePaymentAsync(companyId, new CreatePaymentRequest(
                DocumentId: adv.Id, PaymentDate: DateTime.UtcNow, Amount: due, PaymentMethod: request.PaymentMethod,
                Reference: request.PaymentReference, BankAccount: null, Notes: $"ชำระใบกำกับภาษีล่วงหน้าตอนเช็คเอาต์ {r.ReservationNumber}",
                OverrideBankAccountId: request.BankAccountId), userId);
            r.PaidAmount += due;
            await _db.SaveChangesAsync();
        }

        // ── (4) ใบเช็คเอาต์ = รายการระหว่างพัก + ส่วนเพิ่ม · แผนมัดจำจากยอดคงเหลือหลังตัดชำระใบล่วงหน้า (วางแผนก่อนออกเลข §86/4) ──
        var rest = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var full = DocumentService.PreviewTotals(extraLines, prop.PricesIncludeVat, 0m);
        var depositPlan = LodgingDepositSettlement.PlanCheckout(rest, full.Net,
            deducted => DocumentService.PreviewTotals(extraLines, prop.PricesIncludeVat, 0m, deducted).Total);
        var create = new CreateDocumentRequest(
            DocumentType: docType, DocumentDate: DateTime.UtcNow, DueDate: request.CollectBalanceNow ? null : DateTime.UtcNow.AddDays(30),
            ContactId: contactId, Reference: r.ReservationNumber,
            Notes: $"ค่าใช้จ่ายระหว่างเข้าพัก/ส่วนเพิ่ม {prop.Name} {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} · จอง {r.ReservationNumber} · "
                + $"ค่าห้องตามใบกำกับภาษีล่วงหน้า {adv.Number}",
            Lines: extraLines,
            DepositAppliedRef: depositPlan.DeductionRef,
            DepositBaseDeducted: depositPlan.BaseDeducted > 0m ? depositPlan.BaseDeducted : null,
            PricesIncludeVat: prop.PricesIncludeVat,
            BankAccountId: request.BankAccountId,
            BranchId: prop.BranchId,
            BookingNumber: r.ReservationNumber,
            ServiceUsedDate: r.CheckOutDate);
        var draftId = await UpsertFinalDraftAsync(companyId, r, create, docType, userId);
        var approved = await _docService.ApproveDocumentAsync(companyId, draftId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        foreach (var c in newCharges) _db.AddNewChild(r.Charges, c);
        RecalcFolio(r);
        r.ContactId = contactId;
        r.FinalDocumentId = approved.Id;
        await _db.SaveChangesAsync();
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action = "CheckOutInvoiceIssued", finalDocument = approved.DocumentNumber, docType = docType.ToString(), advance = adv.Number,
            stayDelta = delta.Gross, depositAppliedToAdvance = planAdvance.GrossApplied,
            depositApplied = depositPlan.GrossApplied, depositExcessToRefund = depositPlan.ExcessGross, by = userId,
        }));
        return await SettleCheckOutAsync(companyId, r, approved, depositPlan, request, userId);
    }
}
