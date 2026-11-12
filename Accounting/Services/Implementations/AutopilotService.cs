using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Autopilot;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **หน้า Autopilot "ใบนี้อยู่ขั้นไหน · ติดอะไร" + ค่าตั้ง + ตัวเลขสรุปประจำวัน** (รอบ 252 WP-12A · AUTOPILOT §11.4 · A-5/A-6)
/// <para>read model จากแถวจริงเท่านั้น (Documents · DocumentEmailLogs · Payments · PostPaymentAutomationRuns · EmailQueues · RecurringTransactions ·
/// BankTransactions) — ไม่มีตารางสถานะซ้ำ · ขั้นของใบตัดสินที่ <see cref="AutopilotStage.Of"/> ตัวเดียว · ทุก query มี <c>CompanyId</c> · query แบบชุด (ไม่ N+1)</para>
/// </summary>
public class AutopilotService : IAutopilotService
{
    public const int MaxRows = 200;
    /// <summary>ข้อเสนอเงินโอนที่ค้างเกินนี้ขึ้นเป็น "เรื่องที่ระบบทำต่อไม่ได้" (AUTOPILOT §11.4)</summary>
    public const int BankSuggestStaleDays = 3;

    private readonly AccountingDbContext _db;
    private readonly IBankReceiptSettleService _bank;
    /// <summary>รอบ 259-3 (E-M2): ข้อเท็จจริงช่องทางรับเงินของบริษัทสำหรับการ์ดศูนย์รวมค่าตั้ง (ตัวเดียวกับลิงก์ชำระ) · null (สร้างนอก DI) = การ์ดบอก "ตรวจไม่ได้"</summary>
    private readonly IDocumentPayLinkService? _payLinks;

    public AutopilotService(AccountingDbContext db, IBankReceiptSettleService bank, IDocumentPayLinkService? payLinks = null)
    {
        _db = db;
        _bank = bank;
        _payLinks = payLinks;
    }

    private static readonly DocumentType[] SaleTypes = { DocumentType.Invoice, DocumentType.TaxInvoice };

    private static string TypeLabel(DocumentType t) => t switch
    {
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.Quotation => "ใบเสนอราคา",
        _ => t.ToString(),
    };

    private async Task<Dictionary<Guid, string>> ContactNamesAsync(Guid companyId, IEnumerable<Guid> contactIds, CancellationToken ct)
    {
        var ids = contactIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    private static string DocHref(Guid id) => "/pages/documents.html?openDoc=" + id.ToString();

    // ═══════════════ ภาพรวม ═══════════════

    public async Task<AutopilotOverviewResponse> OverviewAsync(Guid companyId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var now = DateTime.UtcNow;
        var since = now.Date.AddDays(-days);

        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && SaleTypes.Contains(d.DocumentType) && d.DocumentDate >= since)
            .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreatedAt).Take(MaxRows)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.TotalAmount, d.BalanceDue, d.RecurringTransactionId,
                d.RelatedDocumentId, d.ContactId })
            .ToListAsync(ct);
        var ids = docs.Select(d => d.Id).ToList();
        // ชื่อผู้ติดต่อแยก query (รวมที่ถูกลบ) — นำทาง d.Contact ใน projection = INNER JOIN ใต้ query filter ⇒ ใบของผู้ติดต่อที่ถูกลบหายจากหน้าเงียบ ๆ
        var names = await ContactNamesAsync(companyId, docs.Select(d => d.ContactId), ct);

        var emails = await _db.DocumentEmailLogs.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.DocumentId != null && ids.Contains(l.DocumentId.Value))
            .Select(l => new { DocumentId = l.DocumentId!.Value, l.Status }).ToListAsync(ct);
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && ids.Contains(p.DocumentId))
            .Select(p => new { p.DocumentId, p.PaymentIntentId, p.Reference, p.CreatedAt }).ToListAsync(ct);
        var runs = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId && ids.Contains(r.DocumentId))
            .Select(r => new { r.DocumentId, r.Status, r.Message, r.ReceiptDocumentId, r.CreatedAt }).ToListAsync(ct);
        var receiptIds = runs.Where(r => r.ReceiptDocumentId != null).Select(r => r.ReceiptDocumentId!.Value).Distinct().ToList();
        var receiptNumbers = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && receiptIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber }).ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        var bankWaiting = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.AutoSettleDocumentId != null && ids.Contains(t.AutoSettleDocumentId.Value)
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed
                    || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Held))
            .Select(t => t.AutoSettleDocumentId!.Value).Distinct().ToListAsync(ct);
        var autopilotChildren = await _db.Documents.AsNoTracking()
            .Where(q => q.CompanyId == companyId && q.QuotationAutopilotChildId != null && ids.Contains(q.QuotationAutopilotChildId.Value))
            .Select(q => q.QuotationAutopilotChildId!.Value).ToListAsync(ct);

        var rows = new List<AutopilotRowDto>();
        foreach (var d in docs)
        {
            var mineEmails = emails.Where(e => e.DocumentId == d.Id).ToList();
            var minePays = payments.Where(p => p.DocumentId == d.Id).OrderByDescending(p => p.CreatedAt).ToList();
            var lastRun = runs.Where(r => r.DocumentId == d.Id).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            var last = minePays.FirstOrDefault();
            var via = last == null ? null
                : last.PaymentIntentId != null ? "Gateway"
                : (last.Reference ?? "").StartsWith("BANK:", StringComparison.Ordinal) ? "Bank"
                : "Manual";
            var stage = AutopilotStage.Of(new AutopilotStage.DocFacts(d.Status,
                Emailed: mineEmails.Any(e => e.Status == EmailLogStatus.Sent),
                EmailFailed: mineEmails.Count > 0 && mineEmails.All(e => e.Status == EmailLogStatus.Failed),
                d.BalanceDue, AnyPayment: minePays.Count > 0, via,
                lastRun?.Status, lastRun?.Message,
                lastRun?.ReceiptDocumentId is Guid rid && receiptNumbers.TryGetValue(rid, out var rn) ? rn : null,
                AwaitingBankConfirm: bankWaiting.Contains(d.Id)));
            var origin = d.RecurringTransactionId != null ? "รายการประจำ" : autopilotChildren.Contains(d.Id) ? "ใบเสนอราคา (แปลงอัตโนมัติ)" : null;
            rows.Add(new AutopilotRowDto(d.Id, d.DocumentNumber, TypeLabel(d.DocumentType), names.GetValueOrDefault(d.ContactId), d.DocumentDate, d.TotalAmount, d.BalanceDue,
                origin, stage.Overall, stage.OverallLabel,
                stage.Steps.Select(s => new AutopilotStepDto(s.Key, s.Label, s.State.ToString(), s.Note)).ToList(), stage.Problem));
        }

        var issues = await IssuesAsync(companyId, now, ct);
        var queue = await _bank.QueueAsync(companyId, ct);
        var summary = new AutopilotSummaryDto(days,
            rows.Count(r => r.Overall == AutopilotStage.OverallDone),
            rows.Count(r => r.Overall == AutopilotStage.OverallRunning),
            rows.Count(r => r.Overall == AutopilotStage.OverallStuck),
            issues.Count, queue.Count,
            // รอบ 259-3 (E-M4): KPI สลิปรอยืนยัน (ทั้งหมด ไม่ตัดที่ 50) + ลิงก์คิวเจ้าของเดียว
            SlipAwaitingConfirm: await SlipPendingQuery(companyId).CountAsync(ct), SlipQueueHref: SlipQueueHref);
        return new AutopilotOverviewResponse(summary, rows, issues, await QuotationsAsync(companyId, ct), queue, await SettingsAsync(companyId, ct));
    }

    // ═══════════════ เรื่องที่ระบบทำต่อไม่ได้ ═══════════════

    public async Task<IReadOnlyList<AutopilotIssueDto>> IssuesAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var list = new List<AutopilotIssueDto>();
        var window = nowUtc.AddDays(-30);

        // (1) รอบรายการประจำล้ม
        var recurring = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.LastRunError != null && r.Status == RecurringStatus.Active)
            .Select(r => new { r.Id, r.Name, r.LastRunError, r.LastRunErrorAt, r.UpdatedAt }).ToListAsync(ct);
        list.AddRange(recurring.Select(r => new AutopilotIssueDto("RecurringFailed", $"รายการประจำ \"{r.Name}\" สร้างเอกสารไม่สำเร็จ", r.LastRunError ?? "",
            r.LastRunErrorAt ?? r.UpdatedAt ?? nowUtc, null, "แก้ที่หน้ารายการประจำ", "/pages/recurring.html", null, null, null)));

        // (2) ใบจากรายการประจำค้างร่าง (อนุมัติอัตโนมัติไม่ผ่าน/ไม่ได้ตั้ง)
        var drafts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.RecurringTransactionId != null && d.CreatedAt >= window
                && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval))
            .OrderByDescending(d => d.CreatedAt).Take(50)
            .Select(d => new { d.Id, d.DocumentNumber, d.CreatedAt }).ToListAsync(ct);
        list.AddRange(drafts.Select(d => new AutopilotIssueDto("RecurringDraft", $"{d.DocumentNumber} จากรายการประจำยังเป็นร่าง",
            "อนุมัติอัตโนมัติไม่ผ่าน (คำเตือนที่นโยบายไม่ปล่อย) หรือรายการไม่ได้ตั้งอนุมัติอัตโนมัติ — ตรวจแล้วอนุมัติเอง หรือตั้งนโยบายที่รายการประจำ",
            d.CreatedAt, d.Id, "เปิดใบร่าง", DocHref(d.Id), null, null, null)));

        // (3) งานหลังรับชำระหยุด/ล้ม
        var runs = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.CreatedAt >= window
                && (r.Status == PostPaymentAutomationPolicy.StatusBlocked || r.Status == PostPaymentAutomationPolicy.StatusFailed))
            .OrderByDescending(r => r.CreatedAt).Take(50)
            .Select(r => new { r.Id, r.DocumentId, r.Message, r.CreatedAt, Number = _db.Documents.Where(d => d.Id == r.DocumentId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefault() })
            .ToListAsync(ct);
        list.AddRange(runs.Select(r => new AutopilotIssueDto("PostPayment", $"{r.Number ?? "เอกสาร"}: ส่งใบเสร็จหลังรับชำระไม่สำเร็จ",
            r.Message ?? "งานหลังรับชำระหยุด", r.CreatedAt, r.DocumentId, "ลองใหม่ (หลังแก้สาเหตุ)", null,
            $"document-auto-send/post-payment-runs/{r.Id}/retry", "เปิดเอกสาร", DocHref(r.DocumentId))));

        // (4) อีเมลเอกสารส่งไม่สำเร็จ (คิว)
        var staleSendingCutoff = EmailQueueStaleSending.Cutoff(DateTime.UtcNow);
        var failedMail = await _db.EmailQueues.AsNoTracking()
            // รอบ 258 (B-H2): รวมแถวที่ค้าง "กำลังส่ง" เกินเส้นแบ่ง (ตัวกู้จะประทับล้มในรอบถัดไป — ระหว่างนั้นก็ต้องเห็น)
            .Where(q => q.CompanyId == companyId && q.EntityType == "Document" && q.CreatedAt >= window
                && (q.Status == EmailQueueStaleSending.StatusFailed
                    || (q.Status == EmailQueueStaleSending.StatusSending && (q.UpdatedAt ?? q.CreatedAt) < staleSendingCutoff)))
            .OrderByDescending(q => q.CreatedAt).Take(50)
            .Select(q => new { q.EntityId, q.ToEmail, q.ErrorMessage, q.CreatedAt, q.Status,
                Number = _db.Documents.Where(d => d.Id == q.EntityId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefault() })
            .ToListAsync(ct);
        list.AddRange(failedMail.Select(m => new AutopilotIssueDto("EmailFailed", $"{m.Number ?? "เอกสาร"}: ส่งอีเมลถึง {m.ToEmail} ไม่สำเร็จ",
            m.ErrorMessage ?? (m.Status == EmailQueueStaleSending.StatusSending ? EmailQueueStaleSending.StaleReason : "ส่งไม่สำเร็จครบรอบ"), m.CreatedAt, m.EntityId, "เปิดเอกสารแล้วส่งใหม่", DocHref(m.EntityId), null, null, null)));

        // (5) ใบเสนอราคาที่ลูกค้ายอมรับแต่แปลงอัตโนมัติไม่ได้
        var held = new[] { nameof(QuotationAcceptAutopilot.Outcome.Held), nameof(QuotationAcceptAutopilot.Outcome.Failed) };
        var qts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.Quotation && d.QuotationAutopilotAt != null
                && held.Contains(d.QuotationAutopilotOutcome!))
            .OrderByDescending(d => d.QuotationAutopilotAt).Take(50)
            .Select(d => new { d.Id, d.DocumentNumber, d.QuotationAutopilotMessage, d.QuotationAutopilotAt }).ToListAsync(ct);
        list.AddRange(qts.Select(q => new AutopilotIssueDto("QuotationBlocked", $"ใบเสนอราคา {q.DocumentNumber}: ลูกค้ายอมรับแล้วแต่แปลงอัตโนมัติไม่ได้",
            q.QuotationAutopilotMessage ?? "", q.QuotationAutopilotAt ?? nowUtc, q.Id, "ลองแปลงอีกครั้ง", null,
            $"autopilot/quotations/{q.Id}/retry", "เปิดใบเสนอราคา", DocHref(q.Id))));

        // (6) เงินโอนเข้าบันทึกรับชำระไม่สำเร็จ + ข้อเสนอค้างเกิน 3 วัน
        var staleCut = nowUtc.Date.AddDays(-BankSuggestStaleDays);
        var bank = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Held
                    || (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest && t.AutoSettleDocumentId != null && t.TransactionDate < staleCut)))
            .OrderByDescending(t => t.TransactionDate).Take(50)
            .Select(t => new { t.Id, t.Amount, t.TransactionDate, t.AutoSettleVerdict, t.AutoSettleReason, t.AutoSettleDocumentId }).ToListAsync(ct);
        list.AddRange(bank.Select(t => new AutopilotIssueDto(
            t.AutoSettleVerdict != BankReceiptAutoSettle.Stamps.Suggest ? "BankSettleFailed" : "BankSuggestStale",
            t.AutoSettleVerdict != BankReceiptAutoSettle.Stamps.Suggest
                ? $"เงินโอน {Math.Abs(t.Amount):N2} บาท บันทึกรับชำระอัตโนมัติไม่สำเร็จ"
                : $"เงินโอน {Math.Abs(t.Amount):N2} บาท รอยืนยันเกิน {BankSuggestStaleDays} วัน",
            t.AutoSettleReason ?? "", t.TransactionDate, t.AutoSettleDocumentId,
            "ไปที่คิวเงินเข้าที่รอยืนยัน (ด้านล่าง)", null, null, "เปิดหน้ากระทบยอดธนาคาร", "/pages/bank.html")));

        // รอบ 258-3 (F-M2 · F-M3): ช่วงที่เส้นบิลอัตโนมัติขาดแล้วเดิมไม่มีใครเห็นนอกหน้าย่อย
        list.AddRange(await BillingChainIssuesAsync(companyId, window, nowUtc, ct));

        // รอบ 259-3 (ทีม E-M4 · คำตัดสิน 259-3-3): (11) สลิปลูกค้ารอพนักงานยืนยัน — คิวเจ้าของเดียวคือหน้ารายการรับชำระออนไลน์ (payment-intents) ·
        // ที่นี่ชี้ไปที่นั่น ไม่มีปุ่มยืนยันซ้ำ (ยืนยันต้องเห็นรูปสลิป + วันที่เงินเข้า)
        list.AddRange(await SlipPendingIssuesAsync(companyId, ct));
        // รอบ 260: สลิปจากแชต LINE ที่ยังไม่ผูกเอกสาร (ไม่ได้ผูก LINE · อ่านยอดไม่ได้ · ยอดไม่ตรง · ลูกค้าไม่ได้เลือก) — คิวเดียวกัน (หน้ารายการรับชำระออนไลน์)
        list.AddRange(await LineChatSlipIssuesAsync(companyId, nowUtc, ct));
        // รอบ 259 P6: ใบที่ต้องออกใบเสร็จ ([RECEIPT-PENDING]) · เงินรอจัดสรรค้างเกิน 30 วัน — ตัวอ่านเดียวกับหน้า "เงินรอจัดสรร"
        list.AddRange(await CustomerCreditWorkQueue.AutopilotIssuesAsync(_db, companyId, nowUtc, ct));

        return list.OrderByDescending(i => i.At).ToList();
    }

    /// <summary>รอบ 258-3 (F-M2 · F-M3) — (7) ออกใบเสร็จตอนรับชำระล้ม (ป้าย <see cref="SettlementReceiptFailedNote"/>) · (8) การ์ด LINE ถึงผู้ติดต่อส่งไม่ออก ·
    /// (9) เงินออนไลน์เข้าแล้วแต่ยังไม่บันทึกรับชำระ (รายการชำระของเอกสารที่สำเร็จแต่ไม่มีการชำระอ้าง intent) · (10) ส่งเอกสารเข้าระบบคู่ค้าล้ม (ป้าย
    /// <see cref="PartnerRoutingFailedNote"/>) — อ่านอย่างเดียว · ทุก query ผูก CompanyId</summary>
    private async Task<List<AutopilotIssueDto>> BillingChainIssuesAsync(Guid companyId, DateTime window, DateTime nowUtc, CancellationToken ct)
    {
        var list = new List<AutopilotIssueDto>();
        var receiptMarker = SettlementReceiptFailedNote.Marker;
        var routeMarker = PartnerRoutingFailedNote.Marker;
        var flagged = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.InternalNotes != null
                && (d.InternalNotes.Contains(receiptMarker) || d.InternalNotes.Contains(routeMarker)))
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt).Take(50)
            .Select(d => new { d.Id, d.DocumentNumber, d.InternalNotes, At = d.UpdatedAt ?? d.CreatedAt }).ToListAsync(ct);
        foreach (var d in flagged)
        {
            if (SettlementReceiptFailedNote.Has(d.InternalNotes))
                list.Add(new AutopilotIssueDto("ReceiptIssueFailed", $"{d.DocumentNumber}: รับชำระแล้วแต่ออกใบเสร็จ/ใบกำกับไม่สำเร็จ",
                    NoteLine(d.InternalNotes, receiptMarker), d.At, d.Id, "เปิดเอกสารแล้วกดออกใบเสร็จ", DocHref(d.Id), null, null, null));
            if (PartnerRoutingFailedNote.Has(d.InternalNotes))
                list.Add(new AutopilotIssueDto("PartnerRouteFailed", $"{d.DocumentNumber}: ส่งเข้าระบบคู่ค้าไม่สำเร็จ",
                    NoteLine(d.InternalNotes, routeMarker), d.At, d.Id, "เปิดเอกสารแล้วกดส่งให้คู่ค้าอีกครั้ง", DocHref(d.Id), null, null, null));
        }

        var lineFailed = await _db.LineQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted && q.CreatedAt >= window && q.Status == ContactLineChannel.StatusFailed)
            .OrderByDescending(q => q.CreatedAt).Take(50)
            .Select(q => new { q.EntityId, q.EntityType, q.ErrorMessage, q.CreatedAt, q.EndpointKind,
                Number = _db.Documents.Where(d => d.Id == q.EntityId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefault() })
            .ToListAsync(ct);
        // รอบ 259 (258-3-6): แถวการ์ดทวง 50 ทวิ (EntityId = ผู้ติดต่อ — ไม่ใช่เอกสาร) ⇒ ป้ายของตัวเอง ไม่มีปุ่ม "เปิดเอกสาร"
        list.AddRange(lineFailed.Select(q => q.EntityType == LineWhtCertCard.QueueEntityType
            ? new AutopilotIssueDto("LineFailed", $"ทวง 50 ทวิ: ส่งทาง {NotifyEndpointKind.Label(q.EndpointKind)} ไม่สำเร็จ",
                q.ErrorMessage ?? "LINE ไม่รับข้อความ", q.CreatedAt, null, "ดูคิว LINE (ส่งใหม่ได้)", "/pages/email-schedule.html", null, null, null)
            : new AutopilotIssueDto("LineFailed",
            $"{q.Number ?? "เอกสาร"}: ส่งทาง {NotifyEndpointKind.Label(q.EndpointKind)} ไม่สำเร็จ",
            q.ErrorMessage ?? "LINE ไม่รับข้อความ", q.CreatedAt, q.EntityId, "ดูคิว LINE (ส่งใหม่ได้)", "/pages/email-schedule.html", null, "เปิดเอกสาร", DocHref(q.EntityId))));

        // รอบ 260-4 (team-A L6/L8 · คำตัดสิน 260-4-4/260-4-6): กระดิ่ง "LINE ล้มซ้ำ/ช่องเสีย" และ "อนุมัติแล้วเข้าคิวส่งอัตโนมัติไม่สำเร็จ" — แถวแจ้งเตือนเดียวกับที่ส่งถึงเจ้าของ
        // (ทุกเจ้าของได้แถวของตัวเอง ⇒ รวมเป็นเรื่องเดียวต่อชนิด/เอกสาร · ล่าสุดก่อน)
        var alertTypes = new[] { LineFailureAlert.EntityType, LineFailureAlert.ApproveEnqueueEntityType };
        var alerts = await _db.Notifications.AsNoTracking()
            .Where(n => n.CompanyId == companyId && n.EntityType != null && alertTypes.Contains(n.EntityType) && n.CreatedAt >= window)
            .OrderByDescending(n => n.CreatedAt).Take(200)
            .Select(n => new { n.EntityType, n.EntityId, n.Title, n.Message, n.CreatedAt }).ToListAsync(ct);
        // ฝ่ายค้าน 260-4 L6: เรื่องที่แก้แล้วไม่ค้าง — LINE ส่งสำเร็จหลังกระดิ่ง (OA บริษัท) · รายการมีแถวคิว/การส่งหลังกระดิ่ง (ส่งเองแล้ว)
        DateTime? lastLineSent = null;
        if (alerts.Any(a => a.EntityType == LineFailureAlert.EntityType))
            lastLineSent = await _db.LineQueues.AsNoTracking()
                .Where(q => q.CompanyId == companyId && q.Status == ContactLineChannel.StatusSent && q.SentAt != null
                    && (q.OaScope == null || q.OaScope != NotifyEndpointKind.ScopeNextAcc))
                .MaxAsync(q => q.SentAt, ct);
        var enqueueIds = alerts.Where(a => a.EntityType == LineFailureAlert.ApproveEnqueueEntityType && a.EntityId != null).Select(a => a.EntityId!.Value).Distinct().ToList();
        var lastSendByEntity = new Dictionary<Guid, DateTime>();
        if (enqueueIds.Count > 0)
        {
            foreach (var r in await _db.EmailQueues.AsNoTracking()
                         .Where(q => q.CompanyId == companyId && !q.IsDeleted && enqueueIds.Contains(q.EntityId) && q.Status != ScheduledEmailSendCheck.SkippedStatus)
                         .GroupBy(q => q.EntityId).Select(g => new { g.Key, At = g.Max(q => q.CreatedAt) }).ToListAsync(ct))
                lastSendByEntity[r.Key] = r.At;
            foreach (var r in await _db.LineQueues.AsNoTracking()
                         .Where(q => q.CompanyId == companyId && !q.IsDeleted && enqueueIds.Contains(q.EntityId) && q.Status != ContactLineChannel.StatusSkipped)
                         .GroupBy(q => q.EntityId).Select(g => new { g.Key, At = g.Max(q => q.CreatedAt) }).ToListAsync(ct))
                if (!lastSendByEntity.TryGetValue(r.Key, out var at) || r.At > at) lastSendByEntity[r.Key] = r.At;
            foreach (var r in await _db.DocumentEmailLogs.AsNoTracking()
                         .Where(l => l.CompanyId == companyId && l.DocumentId != null && enqueueIds.Contains(l.DocumentId.Value))
                         .GroupBy(l => l.DocumentId!.Value).Select(g => new { g.Key, At = g.Max(l => l.CreatedAt) }).ToListAsync(ct))
                if (!lastSendByEntity.TryGetValue(r.Key, out var at) || r.At > at) lastSendByEntity[r.Key] = r.At;
        }
        foreach (var a in alerts.GroupBy(n => (n.EntityType, n.EntityId)).Select(g => g.First())
                     .Where(a => a.EntityType == LineFailureAlert.EntityType
                         ? !(lastLineSent is DateTime sent && sent > a.CreatedAt)
                         : !(a.EntityId is Guid eid && lastSendByEntity.TryGetValue(eid, out var at) && at > a.CreatedAt)))
            list.Add(a.EntityType == LineFailureAlert.EntityType
                ? new AutopilotIssueDto("LineRepeatedFailure", a.Title, a.Message, a.CreatedAt, null, "ดูคิว LINE (ส่งใหม่ได้)", "/pages/email-schedule.html",
                    null, "ตั้งค่า LINE", "/pages/settings.html?tab=line")
                : new AutopilotIssueDto("AutoSendEnqueueFailed", a.Title, a.Message, a.CreatedAt, a.EntityId, "เปิดเอกสารแล้วส่งเอง (📧/💬)",
                    a.EntityId is Guid d ? DocHref(d) : null, null, null, null));

        // รอบ 259 (B-M6): e-Tax โหมดส่งตรง — นำส่งกรมล้ม/กรมปฏิเสธ · ลงนามแล้วแต่ใบเดือนก่อนยังไม่ส่ง (กำหนดวันที่ 15) — ตัวตัดสิน Helpers/EtaxRdSubmission
        var etaxDirect = await _db.CompanySettings.AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && s.EtaxEnabled && s.EtaxMode == EtaxMode.Direct, ct);
        if (etaxDirect)
        {
            var monthStart = EtaxRdSubmission.CurrentMonthStart(nowUtc);
            // ฝ่ายค้าน 259-D M3: เดือนภาษี/กำหนดส่ง = วันที่เอกสาร (ตัวเดียว EtaxRdSubmission.FilingMonthOf/DeadlineLabel) · M4: ป้าย "เลยกำหนดส่งแล้ว"
            var etaxRows = await (from e in _db.EtaxInvoices.AsNoTracking()
                                  join d in _db.Documents.AsNoTracking() on e.DocumentId equals d.Id
                                  where e.CompanyId == companyId && d.CompanyId == companyId && e.SubmittedAt == null
                                        && (e.Status == EtaxStatus.Rejected
                                            || (e.Status == EtaxStatus.Error && e.SignedAt != null)
                                            || (e.Status == EtaxStatus.Signed && d.DocumentDate < monthStart))
                                  orderby d.DocumentDate
                                  select new { e.DocumentId, e.Status, e.ErrorMessage, e.CreatedAt, e.RdLastAttemptAt, d.DocumentDate, Number = d.DocumentNumber })
                .Take(50).ToListAsync(ct);
            list.AddRange(etaxRows.Select(e => new AutopilotIssueDto("EtaxRdFailed",
                $"{e.Number ?? "เอกสาร"}: e-Tax " + (e.Status == EtaxStatus.Signed ? "ลงนามแล้วยังไม่ได้นำส่งกรมสรรพากร" : "นำส่งกรมสรรพากรไม่สำเร็จ")
                    + (EtaxRdSubmission.IsOverdue(e.DocumentDate, nowUtc) ? " (เลยกำหนดส่ง)" : ""),
                (e.ErrorMessage ?? "ยังไม่ได้นำส่ง") + $" — เดือนภาษี {EtaxRdSubmission.FilingMonthOf(e.DocumentDate):MM}/{EtaxRdSubmission.FilingMonthOf(e.DocumentDate).Year + 543} · "
                    + EtaxRdSubmission.DeadlineLabel(e.DocumentDate, nowUtc)
                    + (e.Status == EtaxStatus.Rejected ? " · กรมปฏิเสธ: แก้ข้อมูล/ออกใบใหม่" : " · ระบบลองส่งให้อัตโนมัติ (วันที่ 1–15 ทุก 3 ชม. · เลยกำหนดแล้ววันละครั้ง) หรือกดส่งเองที่หน้า e-Tax"),
                e.RdLastAttemptAt ?? e.CreatedAt, e.DocumentId, "เปิดหน้า e-Tax", "/pages/etax.html", null, "เปิดเอกสาร", DocHref(e.DocumentId))));
        }

        // เงินเข้าแล้วแต่ยังไม่มีการชำระที่อ้างรายการนี้ (ผู้บันทึกล้ม/ค้าง) — เผื่อเวลา 15 นาทีให้ webhook/งานกระทบยอดทำงาน
        var settledBefore = nowUtc.AddMinutes(-15);
        var intents = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted && i.SourceKind == PaymentSourceKind.Document && i.Status == PaymentIntentStatus.Succeeded
                && i.IsTestMode != true && i.CreatedAt >= window && (i.PaidAt ?? i.CreatedAt) < settledBefore)
            .OrderByDescending(i => i.CreatedAt).Take(100)
            .Select(i => new { i.Id, i.SourceId, i.Amount, i.Description, At = i.PaidAt ?? i.CreatedAt }).ToListAsync(ct);
        if (intents.Count > 0)
        {
            var refs = intents.Select(i => GatewayPaymentRecord.Reference(i.Id)).ToList();
            var posted = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference != null && refs.Contains(p.Reference))
                .Select(p => p.Reference!).ToListAsync(ct);
            list.AddRange(intents.Where(i => !posted.Contains(GatewayPaymentRecord.Reference(i.Id))).Select(i => new AutopilotIssueDto("GatewayUnposted",
                $"เงินออนไลน์ {i.Amount:N2} บาท เข้าแล้วแต่ยังไม่บันทึกรับชำระ",
                (i.Description ?? "") + " — ตรวจที่หน้ารายการรับชำระออนไลน์ แล้วกดดำเนินการต่อ/บันทึกรับเงินด้วยมือ", i.At, i.SourceId,
                "เปิดรายการรับชำระออนไลน์", "/pages/payment-intents.html", null, "เปิดเอกสาร", DocHref(i.SourceId))));
        }
        return list;
    }

    /// <summary>รอบ 259-3 (E-M4): ลิงก์คิวสลิปที่หน้ารายการรับชำระออนไลน์ (ตัวกรองสถานะรอ) — ตัวเดียวของ issue + การ์ด KPI + หน้าอื่นที่ชี้มา</summary>
    public const string SlipQueueHref = "/pages/payment-intents.html?status=Pending";

    private IQueryable<PaymentIntent> SlipPendingQuery(Guid companyId)
        => _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted && i.SourceKind == PaymentSourceKind.Document
                && i.MethodKind == PaymentMethodKind.ManualSlip && PaymentIntentPolicy.OpenStatuses.Contains(i.Status));

    private async Task<List<AutopilotIssueDto>> SlipPendingIssuesAsync(Guid companyId, CancellationToken ct)
    {
        var rows = await SlipPendingQuery(companyId)
            .OrderBy(i => i.CreatedAt).Take(50)
            .Select(i => new { i.SourceId, i.Amount, i.CreatedAt,
                Number = _db.Documents.Where(d => d.Id == i.SourceId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.Select(i => new AutopilotIssueDto("SlipPending",
            $"{i.Number ?? "เอกสาร"}: สลิปลูกค้า {i.Amount:N2} บาท รอยืนยัน",
            "ลูกค้าแนบสลิปแล้ว — ระบบไม่ปิดหนี้จากสลิปเอง · ตรวจรูปสลิปกับรายการเดินบัญชี แล้วกดยืนยัน/ปฏิเสธที่หน้ารายการรับชำระออนไลน์",
            i.CreatedAt, i.SourceId, "ไปยืนยันสลิป", SlipQueueHref, null, "เปิดเอกสาร", DocHref(i.SourceId))).ToList();
    }

    /// <summary>รอบ 260: สลิปจากแชต LINE ที่รอพนักงาน — แถว Unmatched + ตัวเลือกที่หมดอายุแล้ว (อ่านอย่างเดียว · การย้ายสถานะทำที่ <c>LineChatSlipService</c>)</summary>
    private async Task<List<AutopilotIssueDto>> LineChatSlipIssuesAsync(Guid companyId, DateTime nowUtc, CancellationToken ct)
    {
        // ตัวกรองคิวเดียวกับหน้ารายการรับชำระ (LineChatSlipService.QueueOf — ไม่ได้ผูก · ตัวเลือกหมดอายุ · ค้างกำลังแจ้งโอน)
        var rows = await LineChatSlipService.QueueOf(_db.LineChatSlips.AsNoTracking(), companyId, nowUtc)
            .OrderBy(x => x.CreatedAt).Take(50)
            .ToListAsync(ct);
        return rows.Select(x => new AutopilotIssueDto("LineChatSlipUnmatched",
            "สลิปจากแชต LINE ยังไม่ผูกเอกสาร" + (x.OcrAmount is decimal a ? $" ยอด {a:N2} บาท" : ""),
            LineChatSlipRule.Reasons.Label(LineChatSlipService.EffectiveReason(x))
                + " — ดูรูปสลิปแล้วผูกกับเอกสาร หรือปิดรายการ (ระบบไม่ปิดหนี้จากสลิปเอง)",
            x.CreatedAt, null, "ไปผูกสลิป", SlipQueueHref, null, null, null)).ToList();
    }

    /// <summary>ย่อหน้าของป้ายบนหมายเหตุภายใน (ข้อความถึงผู้ใช้ — ไม่ต้องถอดป้าย)</summary>
    private static string NoteLine(string? notes, string marker)
        => (notes ?? "").Replace("\r\n", "\n").Split("\n\n").FirstOrDefault(p => p.TrimStart().StartsWith(marker, StringComparison.Ordinal))?.Trim() ?? "";

    // ═══════════════ ใบเสนอราคา ═══════════════

    private async Task<IReadOnlyList<QuotationAutopilotRowDto>> QuotationsAsync(Guid companyId, CancellationToken ct)
    {
        var companyDefault = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.QuotationAcceptAutopilotDefault).FirstOrDefaultAsync(ct);
        var since = DateTime.UtcNow.AddDays(-60);
        var qts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.Quotation
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && (d.QuotationAcceptedAt != null || d.DocumentDate >= since))
            .OrderByDescending(d => d.QuotationAcceptedAt ?? d.DocumentDate).Take(100)
            .Select(d => new { d.Id, d.DocumentNumber, d.ContactId, d.QuotationAcceptedAt, d.QuotationAcceptAutopilot,
                d.QuotationAutopilotOutcome, d.QuotationAutopilotMessage, d.QuotationAutopilotChildId })
            .ToListAsync(ct);
        var names = await ContactNamesAsync(companyId, qts.Select(q => q.ContactId), ct);
        var childIds = qts.Where(q => q.QuotationAutopilotChildId != null).Select(q => q.QuotationAutopilotChildId!.Value).ToList();
        var childNumbers = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && childIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber }).ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        return qts.Select(q => ToRow(q.Id, q.DocumentNumber, names.GetValueOrDefault(q.ContactId), q.QuotationAcceptedAt, q.QuotationAcceptAutopilot, companyDefault,
            q.QuotationAutopilotOutcome, q.QuotationAutopilotMessage, q.QuotationAutopilotChildId,
            q.QuotationAutopilotChildId is Guid c && childNumbers.TryGetValue(c, out var n) ? n : null)).ToList();
    }

    private static QuotationAutopilotRowDto ToRow(Guid id, string number, string? contactName, DateTime? acceptedAt, string? mode, string? companyDefault,
        string? outcome, string? message, Guid? childId, string? childNumber)
    {
        var eff = QuotationAcceptAutopilot.Effective(mode, companyDefault);
        var label = QuotationAcceptAutopilot.Modes.First(m => m.Code == eff.ToString()).Label
            + (QuotationAcceptAutopilot.Parse(mode) == null ? " (ตามค่าบริษัท)" : "");
        var canRetry = outcome is nameof(QuotationAcceptAutopilot.Outcome.Held) or nameof(QuotationAcceptAutopilot.Outcome.Failed);
        return new QuotationAutopilotRowDto(id, number, contactName, acceptedAt, QuotationAcceptAutopilot.Parse(mode)?.ToString(), eff.ToString(), label,
            outcome, message, childId, childNumber, canRetry);
    }

    public async Task<QuotationAutopilotRowDto> SetQuotationModeAsync(Guid companyId, Guid quotationId, string? mode, Guid userId, CancellationToken ct = default)
    {
        var (stored, problem) = QuotationAcceptAutopilot.Normalize(mode);
        if (problem != null) throw new BusinessRuleException(problem, QuotationAcceptAutopilot.RuleCode, 400);
        var qt = await _db.Documents.FirstOrDefaultAsync(d => d.Id == quotationId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบใบเสนอราคา");
        if (qt.DocumentType != DocumentType.Quotation)
            throw new BusinessRuleException("ตั้งการแปลงอัตโนมัติได้เฉพาะใบเสนอราคา", QuotationAcceptAutopilot.RuleCode, 400);
        if (qt.QuotationAutopilotOutcome is nameof(QuotationAcceptAutopilot.Outcome.Converted))
            throw new BusinessRuleException("ใบเสนอราคานี้ระบบแปลงไปแล้ว — เปลี่ยนโหมดไม่มีผล (แก้ที่ใบที่แปลงได้)", QuotationAcceptAutopilot.RuleCode, 409);
        var before = qt.QuotationAcceptAutopilot;
        qt.QuotationAcceptAutopilot = stored;
        // ผลที่หยุด/ล้ม/ไม่ต้องทำไว้ ⇒ ล้าง ให้รอบถัดไปตัดสินใหม่ตามโหมดใหม่ (ไม่ใช่ปุ่มที่กดแล้วไม่มีผล)
        if (qt.QuotationAutopilotOutcome is nameof(QuotationAcceptAutopilot.Outcome.Held) or nameof(QuotationAcceptAutopilot.Outcome.Failed)
            or nameof(QuotationAcceptAutopilot.Outcome.AlreadyConverted))
        {
            qt.QuotationAutopilotAt = null;
            qt.QuotationAutopilotOutcome = null;
            qt.QuotationAutopilotMessage = null;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = qt.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { quotationAcceptAutopilot = before }),
            NewValues = JsonSerializer.Serialize(new { quotationAcceptAutopilot = stored, ruleCode = QuotationAcceptAutopilot.RuleCode }),
        });
        await _db.SaveChangesAsync(ct);
        var companyDefault = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.QuotationAcceptAutopilotDefault).FirstOrDefaultAsync(ct);
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters().Where(c => c.Id == qt.ContactId && c.CompanyId == companyId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        return ToRow(qt.Id, qt.DocumentNumber, contactName, qt.QuotationAcceptedAt, qt.QuotationAcceptAutopilot, companyDefault,
            qt.QuotationAutopilotOutcome, qt.QuotationAutopilotMessage, qt.QuotationAutopilotChildId, null);
    }

    public async Task RetryQuotationAsync(Guid companyId, Guid quotationId, Guid userId, CancellationToken ct = default)
    {
        var held = new[] { nameof(QuotationAcceptAutopilot.Outcome.Held), nameof(QuotationAcceptAutopilot.Outcome.Failed) };
        var n = await _db.Documents
            .Where(d => d.Id == quotationId && d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.Quotation
                && held.Contains(d.QuotationAutopilotOutcome!))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.QuotationAutopilotAt, (DateTime?)null)
                .SetProperty(d => d.QuotationAutopilotOutcome, (string?)null)
                .SetProperty(d => d.QuotationAutopilotMessage, (string?)null), ct);
        if (n == 0) throw new KeyNotFoundException("ไม่พบใบเสนอราคาที่หยุด/ล้มให้ลองใหม่ (อาจแปลงไปแล้ว)");
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = quotationId.ToString(),
            NewValues = JsonSerializer.Serialize(new { action = "quotation-autopilot-retry", ruleCode = QuotationAcceptAutopilot.RuleCode }),
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetContactBankAutoSettleAsync(Guid companyId, Guid contactId, bool disabled, Guid userId, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var before = contact.BankAutoSettleDisabled;
        contact.BankAutoSettleDisabled = disabled;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Contact", EntityId = contactId.ToString(),
            OldValues = JsonSerializer.Serialize(new { bankAutoSettleDisabled = before }),
            NewValues = JsonSerializer.Serialize(new { bankAutoSettleDisabled = disabled, ruleCode = BankReceiptAutoSettle.RuleAuto }),
        });
        await _db.SaveChangesAsync(ct);
    }

    // ═══════════════ ค่าตั้ง ═══════════════

    public async Task<AutopilotSettingsDto> SettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        var s = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.BankAutoSettleEnabled, x.QuotationAcceptAutopilotDefault, x.AutopilotDigestLocalTime,
                x.PayLinkDefaultEnabled, x.PostPayEmailReceipt, x.WhtCertReminderEnabled })
            .FirstOrDefaultAsync(ct);
        // รอบ 259-3 (E-M2 · คำตัดสิน 259-3-4): ศูนย์รวมค่าตั้งที่อยู่หน้าอื่น — สถานะอ่านอย่างเดียว + ลิงก์บ้านของค่า (AutopilotSettingsHub ตัวเดียว)
        var payFacts = _payLinks == null ? null : await _payLinks.CompanyFactsAsync(companyId, ct);
        if (payFacts is { BankTransfer: false } && await GettingStartedChecklist.TransferAccounts(_db.BankAccounts.AsNoTracking(), companyId).AnyAsync(ct))
            payFacts = payFacts with { BankTransfer = true };   // บัญชีของใบ (ธนาคาร) ก็รับโอน/สลิปได้ — ตัวนับเดียวกับเช็กลิสต์เริ่มต้น
        var activeRules = await _db.EmailScheduleRules.AsNoTracking().CountAsync(r => r.CompanyId == companyId && !r.IsDeleted && r.IsActive, ct);
        var hub = AutopilotSettingsHub.Build(new AutopilotSettingsHub.Facts(s?.PayLinkDefaultEnabled ?? false, s?.PostPayEmailReceipt ?? false,
            s?.WhtCertReminderEnabled ?? false, payFacts, activeRules, await SlipPendingQuery(companyId).CountAsync(ct), SlipQueueHref));
        return new AutopilotSettingsDto(
            s?.BankAutoSettleEnabled ?? false,
            (QuotationAcceptAutopilot.Parse(s?.QuotationAcceptAutopilotDefault) ?? QuotationAcceptMode.Off).ToString(),
            AutopilotNotice.DigestTime(s?.AutopilotDigestLocalTime).ToString(@"hh\:mm"),
            QuotationAcceptAutopilot.Modes.Select(m => new AutopilotOptionDto(m.Code, m.Label)).ToList(),
            AutopilotNotice.Catalogue.Select(e => new AutopilotCatalogueDto(e.Code, e.Label, e.Delivery.ToString(),
                e.Delivery switch
                {
                    AutopilotDelivery.Immediate => "แจ้งทันที",
                    AutopilotDelivery.Digest => "รวมในสรุปประจำวัน",
                    _ => "ไม่แจ้ง",
                }, e.Wired)).ToList(),
            BankAutoSettleEnableNote: BankReceiptAutoSettle.EnableNote(BankReceiptSettleService.LookbackDays),
            Hub: hub);
    }

    public async Task<AutopilotSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateAutopilotSettingsRequest request, Guid userId, CancellationToken ct = default)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId, ct)
            ?? await CompanySettingsFactory.AddNewAsync(_db, companyId, ct);
        var before = new { s.BankAutoSettleEnabled, s.QuotationAcceptAutopilotDefault, s.AutopilotDigestLocalTime };
        var now = DateTime.UtcNow;
        // เปิด/ปิดเมื่อไร/โดยใคร = แถว audit ด้านล่าง (hash chain) — ไม่เก็บสำเนาที่สองบนค่าตั้ง
        if (request.BankAutoSettleEnabled is bool on) s.BankAutoSettleEnabled = on;
        if (request.QuotationAcceptAutopilotDefault != null)
        {
            var (stored, problem) = QuotationAcceptAutopilot.Normalize(request.QuotationAcceptAutopilotDefault);
            if (problem != null) throw new BusinessRuleException(problem, QuotationAcceptAutopilot.RuleCode, 400);
            var wasOn = QuotationAcceptAutopilot.Effective(null, s.QuotationAcceptAutopilotDefault) != QuotationAcceptMode.Off;
            var nowOn = QuotationAcceptAutopilot.Effective(null, stored) != QuotationAcceptMode.Off;
            s.QuotationAcceptAutopilotDefault = stored;
            // watermark: เปิดใหม่ ⇒ ใบที่ลูกค้ายอมรับก่อนเวลานี้ไม่ถูกแปลงย้อนหลัง · ปิด ⇒ ล้าง
            if (nowOn && !wasOn) s.QuotationAcceptAutopilotSince = now;
            if (!nowOn) s.QuotationAcceptAutopilotSince = null;
        }
        if (request.DigestLocalTime != null)
        {
            var (value, problem) = AutopilotNotice.NormalizeDigestTime(request.DigestLocalTime);
            if (problem != null) throw new BusinessRuleException(problem, "AUTOPILOT-DIGEST", 400);
            s.AutopilotDigestLocalTime = value;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "CompanySettings", EntityId = companyId.ToString(),
            OldValues = JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new { s.BankAutoSettleEnabled, s.QuotationAcceptAutopilotDefault, s.AutopilotDigestLocalTime, area = "autopilot" }),
        });
        await _db.SaveChangesAsync(ct);
        return await SettingsAsync(companyId, ct);
    }

    // ═══════════════ สรุปประจำวัน ═══════════════

    public async Task<AutopilotNotice.DigestFacts> DigestFactsAsync(Guid companyId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var emailed = await _db.DocumentEmailLogs.AsNoTracking()
            .CountAsync(l => l.CompanyId == companyId && l.DocumentId != null && l.Status == EmailLogStatus.Sent
                && l.SentAt >= fromUtc && l.SentAt < toUtc, ct);
        var pays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.CreatedAt >= fromUtc && p.CreatedAt < toUtc
                && _db.Documents.Any(d => d.Id == p.DocumentId && d.CompanyId == companyId && SaleTypes.Contains(d.DocumentType)))
            .Select(p => p.Amount).ToListAsync(ct);
        var receipts = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .CountAsync(r => r.CompanyId == companyId && r.Status == PostPaymentAutomationPolicy.StatusDone
                && r.CompletedAt >= fromUtc && r.CompletedAt < toUtc, ct);
        var converted = nameof(QuotationAcceptAutopilot.Outcome.Converted);
        var quotations = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && d.QuotationAutopilotOutcome == converted
                && d.QuotationAutopilotAt >= fromUtc && d.QuotationAutopilotAt < toUtc, ct);
        var bankSettled = await _db.BankTransactions.AsNoTracking()
            .CountAsync(t => t.CompanyId == companyId && t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Settled
                && t.ReconciledBy == BankMatchAttribution.BankAutoSettle && t.ReconciledAt >= fromUtc && t.ReconciledAt < toUtc, ct);
        // รอบ 257 ชุด 4 (A L11): นับเกณฑ์เดียวกับคิว "เงินเข้าที่รอยืนยัน" (BankReceiptSettleService.QueueAsync) — รวมคำแนะนำที่มีหลายใบยอดเท่ากัน
        // (ไม่มีใบเดียวให้ชี้ ⇒ AutoSettleDocumentId = null · เดิมถูกตัดทิ้ง ⇒ สรุปบอก 0 ทั้งที่คิวมีรายการรอ)
        var bankWaiting = await _db.BankTransactions.AsNoTracking()
            .CountAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed
                    || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Held), ct);
        var issues = await IssuesAsync(companyId, toUtc, ct);
        return new AutopilotNotice.DigestFacts(emailed, pays.Count, pays.Sum(), receipts, quotations, bankSettled, bankWaiting,
            issues.Count, issues.Take(5).Select(i => i.Title).ToList());
    }
}
