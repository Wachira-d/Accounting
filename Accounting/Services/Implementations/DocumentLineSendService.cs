using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 259 แพ็ก B (E-H3 · คำตัดสิน 259-2-2) — ปุ่ม "💬 ส่งทาง LINE" บนหน้าเอกสาร (ผู้เรียก: <c>DocumentController</c> หลังด่านสิทธิ์ส่งเอกสาร + ชั้นความลับ)
/// <list type="number">
/// <item>ใบส่งได้ไหม (<see cref="DocumentLineSendRule.DocumentProblem"/>) → ปลายทาง LINE ของผู้ติดต่อจากตัวหาผู้รับเดียว (เรื่อง "เอกสาร")</item>
/// <item>ลิงก์ชำระเมื่อผู้ใช้ติ๊ก — ตัวออกลิงก์เดียว (<see cref="IDocumentPayLinkService.EnsureLinkAsync"/>) · ออกไม่ได้ ⇒ ปฏิเสธทั้งคำขอพร้อมเหตุ (ไม่ส่งการ์ดที่ไม่มีปุ่มเงียบ ๆ)</item>
/// <item>เข้าคิว LINE ตัวเดิม (trigger <see cref="ContactLineChannel.ManualTrigger"/>) — ผู้ส่งคิวตรวจใบ/ปลายทางซ้ำตอนส่ง · การ์ดเดียวกับตารางส่ง (ภาษา/หัว/ปุ่มดู PDF)</item>
/// </list>
/// ทุก query ผูก CompanyId · ไม่ส่งในคำขอ HTTP (คิว + ผู้ส่งคิวใต้ JobLock เดิม)
/// </summary>
public class DocumentLineSendService : IDocumentLineSendService
{
    private readonly AccountingDbContext _db;
    private readonly IContactRecipientResolver _recipients;
    private readonly IDocumentPayLinkService? _payLinks;

    public DocumentLineSendService(AccountingDbContext db, IContactRecipientResolver recipients, IDocumentPayLinkService? payLinks = null)
    {
        _db = db; _recipients = recipients; _payLinks = payLinks;
    }

    public async Task<DocumentLineSendOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.ContactId, d.DocumentType, d.Status, d.BalanceDue }).FirstOrDefaultAsync(ct);
        var problem = DocumentLineSendRule.DocumentProblem(doc != null, doc?.DocumentType ?? default, doc?.Status ?? default);
        if (doc == null || problem != null)
            return new DocumentLineSendOptions(false, problem, Array.Empty<DocumentLineSendRule.Target>(), false, null);
        var set = await _recipients.ResolveAsync(companyId, doc.ContactId, DocumentLineSendRule.Topic, ct);
        var targets = DocumentLineSendRule.Targets(set.Options);
        var none = targets.Count == 0
            ? "ผู้ติดต่อนี้ยังไม่มีปลายทาง LINE — ผูก LINE ผู้ใช้ (ลิงก์/QR) หรือกลุ่ม LINE ได้ที่หน้าผู้ติดต่อ แท็บช่องทางแจ้งเตือน"
            : targets.All(t => !t.Available) ? "ไม่มีปลายทาง LINE ที่ส่งได้ตอนนี้ (ดูเหตุของแต่ละปลายทาง)" : null;
        // ลิงก์ชำระ: เฉพาะใบที่มียอดค้าง (ตัวออกลิงก์ตัดสินเงื่อนไขเต็มตอนกดส่ง)
        var payOffered = doc.BalanceDue > 0 && _payLinks != null;
        return new DocumentLineSendOptions(none == null, none, targets, payOffered,
            payOffered ? null : "ใบนี้ไม่มียอดค้างชำระ — ไม่มีลิงก์ชำระเงิน");
    }

    public async Task<DocumentLineSendResult> SendAsync(Guid companyId, Guid documentId, IReadOnlyCollection<Guid>? endpointIds, bool includePayLink,
        Guid? userId, string? actor, CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.ContactId, d.DocumentType, d.Status }).FirstOrDefaultAsync(ct);
        var docProblem = DocumentLineSendRule.DocumentProblem(doc != null, doc?.DocumentType ?? default, doc?.Status ?? default);
        if (doc == null || docProblem != null)
            throw new BusinessRuleException(docProblem ?? "ไม่พบเอกสาร", DocumentLineSendRule.RuleCode, doc == null ? 404 : 409);
        var set = await _recipients.ResolveAsync(companyId, doc.ContactId, DocumentLineSendRule.Topic, ct);
        var (chosen, pickProblem) = DocumentLineSendRule.Pick(set.Options, endpointIds);
        if (pickProblem != null) throw new BusinessRuleException(pickProblem, DocumentLineSendRule.RuleCode, 400);
        if (includePayLink)
        {
            if (_payLinks == null) throw new BusinessRuleException("บริการลิงก์ชำระเงินยังไม่พร้อม — ส่งโดยไม่ติ๊กลิงก์ชำระ", DocumentLineSendRule.RuleCode, 503);
            var link = await _payLinks.EnsureLinkAsync(companyId, documentId, null, ct);
            if (link.Url == null)
                throw new BusinessRuleException("ใส่ลิงก์ชำระเงินไม่ได้ — " + (link.Refusal ?? "ไม่ทราบเหตุ") + " (เอาติ๊กลิงก์ชำระออกแล้วส่งได้)",
                    DocumentLineSendRule.RuleCode, 409);
        }
        var now = DateTime.UtcNow;
        var baseKey = DocumentLineSendRule.BaseKey(documentId, now);
        int queued = 0, already = 0;
        foreach (var o in chosen)
        {
            var key = ContactLineChannel.QueueKey(baseKey, o.EndpointId);
            if (await _db.LineQueues.AnyAsync(q => q.CompanyId == companyId && q.IdempotencyKey == key, ct)) { already++; continue; }
            _db.LineQueues.Add(new LineQueue
            {
                CompanyId = companyId, RuleId = null, EntityType = "Document", EntityId = documentId, ContactId = doc.ContactId,
                EndpointId = o.EndpointId, EndpointKind = o.Kind, Topic = DocumentLineSendRule.Topic.ToString(), Trigger = ContactLineChannel.ManualTrigger,
                ScheduledFor = now, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(companyId, key),
                IdempotencyKey = key, BaseKey = baseKey, FallbackToEmail = false, IncludePayLink = includePayLink,
                RequestedByUserId = userId, CreatedBy = actor ?? "document-line-send",
            });
            queued++;
        }
        if (queued > 0)
        {
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // กดพร้อมกันสองครั้งในนาทีเดียว — อีกคำขอเข้าคิวไปแล้ว (ไม่ส่งซ้ำ)
                _db.ChangeTracker.Clear();
                already += queued;
                queued = 0;
            }
        }
        return new DocumentLineSendResult(queued, already, DocumentLineSendRule.QueuedMessage(queued, already));
    }
}
