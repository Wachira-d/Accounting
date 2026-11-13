using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P3 — <b>ไทม์ไลน์ลูกค้าของใบ</b> (read model · อ่านอย่างเดียว · คำตัดสิน 267-1-64 · DOCUMENT_FLOW §2.12n-P3):
/// ออก/ยกเลิกลิงก์ · เปิดช่วงตอบรับ · ลูกค้าเปิดลิงก์ · อีเมล (<c>DocumentEmailLogs</c>) · การ์ด LINE (<c>LineQueues</c>) · คำตอบของลูกค้า (<c>CustomerDocumentActions</c>) ·
/// คำขอลิงก์ใหม่ (audit) · รายการชำระ/สลิป (<c>PaymentIntents</c>) · รับชำระ (<c>Payments</c>) · ใบเสร็จอัตโนมัติ (<c>PostPaymentAutomationRuns</c>) · e-Tax by Email ·
/// <b>ข้อเท็จจริงจากแถวจริงเท่านั้น</b> — ไม่ประทับสถานะเอง (F2 ข้อ 3) · ป้าย/ทางไปต่อจาก <see cref="CustomerShareRule"/> · ทุก query ผูก CompanyId
/// </summary>
public class CustomerTimelineService : ICustomerTimelineService
{
    /// <summary>จำนวนแถวสูงสุดต่อแหล่ง (ใบที่ส่งซ้ำมาก ⇒ ชุดล่าสุด + ธง Truncated)</summary>
    public const int PerSourceLimit = 50;
    public const int MaxItems = 200;

    private readonly AccountingDbContext _db;
    public CustomerTimelineService(AccountingDbContext db) => _db = db;

    public async Task<CustomerTimelineView> TimelineAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await _db.Documents.AsNoTracking()
            .Where(x => x.Id == documentId && x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => new { x.Id, x.PayLinkIssuedAt, x.PayLinkExpiresAt, x.PayLinkRevokedAt, x.SignLinkIssuedAt, x.SignLinkOpenedAt })
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var items = new List<CustomerShareRule.TimelineItem>();
        var truncated = false;

        if (d.PayLinkIssuedAt is DateTime issued)
            items.Add(new(issued, CustomerShareRule.KindLinkIssued, CustomerShareRule.KindLabel(CustomerShareRule.KindLinkIssued),
                d.PayLinkExpiresAt is DateTime exp ? "ใช้ได้ถึง " + ThaiDate.ToThaiDisplayString(exp) : null, CustomerShareRule.ToneInfo));
        if (d.PayLinkRevokedAt is DateTime revoked)
            items.Add(new(revoked, CustomerShareRule.KindLinkRevoked, CustomerShareRule.KindLabel(CustomerShareRule.KindLinkRevoked),
                "ลิงก์ที่ส่งไปแล้วใช้ไม่ได้", CustomerShareRule.ToneWarn, CustomerShareRule.ActionReshare, "ออกลิงก์ใหม่และส่ง"));
        if (d.SignLinkIssuedAt is DateTime respond)
            items.Add(new(respond, CustomerShareRule.KindRespondOpened, CustomerShareRule.KindLabel(CustomerShareRule.KindRespondOpened), null, CustomerShareRule.ToneInfo));
        if (d.SignLinkOpenedAt is DateTime opened)
            items.Add(new(opened, CustomerShareRule.KindCustomerOpened, "ลูกค้าเปิดลิงก์ครั้งแรก", "นับจากการโหลดหน้าเอกสารจริง (ไม่มี tracking ในอีเมล)", CustomerShareRule.ToneInfo));

        var emails = await _db.DocumentEmailLogs.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.DocumentId == documentId && !e.IsDeleted)
            .OrderByDescending(e => e.CreatedAt).Take(PerSourceLimit + 1)
            .Select(e => new { e.CreatedAt, e.SentAt, e.Status, e.ToEmail, e.ErrorMessage, e.IsEtaxByEmail }).ToListAsync(ct);
        truncated |= emails.Count > PerSourceLimit;
        items.AddRange(emails.Take(PerSourceLimit).Select(e =>
            CustomerShareRule.EmailItem(e.SentAt ?? e.CreatedAt, e.Status == EmailLogStatus.Sent, e.ToEmail, e.ErrorMessage, e.IsEtaxByEmail)));

        var lines = await _db.LineQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && q.EntityType == "Document" && q.EntityId == documentId && !q.IsDeleted)
            .OrderByDescending(q => q.CreatedAt).Take(PerSourceLimit + 1)
            .Select(q => new { q.CreatedAt, q.SentAt, q.Status, q.EndpointKind, q.ErrorMessage, q.Trigger }).ToListAsync(ct);
        truncated |= lines.Count > PerSourceLimit;
        items.AddRange(lines.Take(PerSourceLimit).Select(q =>
            CustomerShareRule.LineItem(q.SentAt ?? q.CreatedAt, q.Status, q.EndpointKind, q.ErrorMessage, q.Trigger == ContactLineChannel.SignedCopyTrigger)));

        // คำตอบของลูกค้า — ชื่อ/ตำแหน่งตามสิทธิ์อ่านเอกสาร (267-1-59) · แถวที่ลบข้อมูลส่วนบุคคลแล้วไม่มีชื่อ · ไม่มี IP/อีเมล
        var answers = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == documentId)
            .OrderByDescending(a => a.CreatedAt).Take(PerSourceLimit)
            .Select(a => new { a.CreatedAt, a.Action, a.SignerFirstName, a.SignerLastName, a.SignerTitle, a.RevisionNumber, a.ErasedAt }).ToListAsync(ct);
        items.AddRange(answers.Select(a => CustomerShareRule.CustomerActionItem(a.CreatedAt, a.Action,
            a.ErasedAt != null ? null : $"{a.SignerFirstName} {a.SignerLastName}".Trim(), a.ErasedAt != null ? null : a.SignerTitle, a.RevisionNumber)));

        var entityId = documentId.ToString();
        var requests = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.EntityType == DocumentPayLinkService.LinkRequestEntity && a.EntityId == entityId)
            .OrderByDescending(a => a.Timestamp).Take(PerSourceLimit).Select(a => a.Timestamp).ToListAsync(ct);
        items.AddRange(requests.Select(at => new CustomerShareRule.TimelineItem(at, CustomerShareRule.KindLinkRequested,
            CustomerShareRule.KindLabel(CustomerShareRule.KindLinkRequested), "ลูกค้ากด \"ขอลิงก์\" บนหน้าเอกสาร", CustomerShareRule.ToneWarn,
            CustomerShareRule.ActionReshare, "ส่งลิงก์ให้ลูกค้า")));

        var intents = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == documentId && !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAt).Take(PerSourceLimit)
            .Select(i => new { i.CreatedAt, i.Status, i.Amount, i.ProviderCode }).ToListAsync(ct);
        foreach (var i in intents)
        {
            var slip = i.ProviderCode == Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code;
            var (label, tone) = i.Status switch
            {
                PaymentIntentStatus.Succeeded => (slip ? "สลิปได้รับการยืนยันแล้ว" : "ลูกค้าชำระออนไลน์สำเร็จ", CustomerShareRule.ToneOk),
                PaymentIntentStatus.Failed => (slip ? "สลิปถูกปฏิเสธ" : "ชำระออนไลน์ไม่สำเร็จ", CustomerShareRule.ToneBad),
                PaymentIntentStatus.Expired => ("รายการชำระหมดอายุ", CustomerShareRule.ToneWarn),
                PaymentIntentStatus.Refunded or PaymentIntentStatus.PartiallyRefunded => ("คืนเงินแล้ว", CustomerShareRule.ToneWarn),
                _ => (slip ? "ลูกค้าแจ้งโอน + แนบสลิป — รอตรวจ" : "ลูกค้าเริ่มชำระออนไลน์", CustomerShareRule.ToneInfo),
            };
            var pendingSlip = slip && i.Status is PaymentIntentStatus.Created or PaymentIntentStatus.Pending;
            items.Add(new(i.CreatedAt, slip ? CustomerShareRule.KindSlip : CustomerShareRule.KindPayment, label, $"{i.Amount:N2} บาท", tone,
                pendingSlip ? CustomerShareRule.ActionReviewSlips : null, pendingSlip ? "ตรวจสลิป" : null));
        }

        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == documentId && !p.IsDeleted)
            .OrderByDescending(p => p.PaymentDate).Take(PerSourceLimit)
            .Select(p => new { p.CreatedAt, p.PaymentNumber, p.Amount }).ToListAsync(ct);
        items.AddRange(payments.Select(p => new CustomerShareRule.TimelineItem(p.CreatedAt, CustomerShareRule.KindPayment,
            "บันทึกรับชำระ " + p.PaymentNumber, $"{p.Amount:N2} บาท", CustomerShareRule.ToneOk)));

        var runs = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId && !r.IsDeleted)
            .OrderByDescending(r => r.CreatedAt).Take(PerSourceLimit)
            .Select(r => new { r.CreatedAt, r.CompletedAt, r.Status, r.Message }).ToListAsync(ct);
        items.AddRange(runs.Select(r => new CustomerShareRule.TimelineItem(r.CompletedAt ?? r.CreatedAt, CustomerShareRule.KindReceipt,
            CustomerShareRule.KindLabel(CustomerShareRule.KindReceipt) + " — " + PostPaymentAutomationPolicy.StatusLabel(r.Status), r.Message,
            r.Status == PostPaymentAutomationPolicy.StatusDone ? CustomerShareRule.ToneOk
                : r.Status is PostPaymentAutomationPolicy.StatusFailed or PostPaymentAutomationPolicy.StatusBlocked ? CustomerShareRule.ToneBad
                : CustomerShareRule.ToneInfo)));

        var etax = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.DocumentId == documentId && e.Status != EtaxStatus.Voided && !e.IsDeleted
                && (e.ByEmailSentAt != null || e.EtdaStampedAt != null))
            .Select(e => new { e.ByEmailSentAt, e.EtdaStampedAt }).ToListAsync(ct);
        foreach (var e in etax)
        {
            if (e.ByEmailSentAt is DateTime sent)
                items.Add(new(sent, CustomerShareRule.KindEtax, "ส่ง e-Tax by Email แล้ว (รอประทับเวลา)", null, CustomerShareRule.ToneInfo));
            if (e.EtdaStampedAt is DateTime stamped)
                items.Add(new(stamped, CustomerShareRule.KindEtax, "ETDA ประทับเวลาแล้ว (บันทึกโดยพนักงาน)", null, CustomerShareRule.ToneOk));
        }

        var ordered = items.OrderByDescending(i => i.At).ToList();
        truncated |= ordered.Count > MaxItems;
        return new CustomerTimelineView(documentId, ordered.Take(MaxItems).ToList(), truncated);
    }
}
