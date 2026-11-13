using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 259 แพ็ก B (E-H3 · คำตัดสิน 259-2-2) — ปุ่ม "💬 ส่งทาง LINE" บนหน้าเอกสาร (ผู้เรียก: <c>DocumentController</c> หลังด่านสิทธิ์ส่งเอกสาร + ชั้นความลับ ·
/// รอบ 267 P3: <c>CustomerShareService</c> — หน้าต่าง "📤 ส่งให้ลูกค้า")
/// <list type="number">
/// <item>ใบส่งได้ไหม (<see cref="DocumentLineSendRule.DocumentProblem"/>) → ปลายทาง LINE ของผู้ติดต่อจากตัวหาผู้รับเดียว (เรื่อง "เอกสาร" · ลิงก์หน้าเอกสารของใบที่ตอบรับได้
/// รวม 1:1 ที่ติ๊ก "ลิงก์เซ็น" — <see cref="DocumentLineSendRule.MergeSignTopic"/>)</item>
/// <item>ลิงก์ชำระเมื่อผู้ใช้ติ๊ก — ตัวออกลิงก์เดียว (<see cref="IDocumentPayLinkService.EnsureLinkAsync"/>) · ออกไม่ได้ ⇒ ปฏิเสธทั้งคำขอพร้อมเหตุ (ไม่ส่งการ์ดที่ไม่มีปุ่มเงียบ ๆ)</item>
/// <item>รอบ 267 P3: ลิงก์หน้าเอกสาร — ตัวออกเดียว (<see cref="IDocumentPayLinkService.EnsureCustomerLinkAsync"/>) · ต่อปลายทางตาม <see cref="DocumentLineSendRule.PageLinkAllowed"/></item>
/// <item>เข้าคิว LINE ตัวเดิม (trigger <see cref="ContactLineChannel.ManualTrigger"/>) — ผู้ส่งคิวตรวจใบ/ปลายทางซ้ำตอนส่ง · การ์ดเดียวกับตารางส่ง (ภาษา/หัว/ปุ่ม)</item>
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

    /// <summary>รอบ 267 P3 (267-1-61): ปลายทางของผู้ติดต่อ + เรื่องที่ทำให้ส่งได้ — ส่งลิงก์หน้าเอกสารของใบที่มีปุ่มตอบรับ ⇒ รวมปลายทาง 1:1 ที่ติ๊ก "ลิงก์เซ็น"</summary>
    private async Task<IReadOnlyList<(ContactRecipientRule.RecipientOption Option, NotifyTopic Topic)>> OptionsWithTopicAsync(
        Guid companyId, Guid contactId, DocumentType type, bool pageLink, CancellationToken ct)
    {
        var set = await _recipients.ResolveAsync(companyId, contactId, DocumentLineSendRule.Topic, ct);
        var sign = pageLink && CustomerDocPage.HasRespondAction(type)
            ? (await _recipients.ResolveAsync(companyId, contactId, NotifyTopic.Sign, ct)).Options : null;
        return DocumentLineSendRule.MergeSignTopic(set.Options, sign);
    }

    public async Task<DocumentLineSendOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default, bool forShare = false)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.ContactId, d.DocumentType, d.Status, d.BalanceDue, d.PayLinkRevokedAt }).FirstOrDefaultAsync(ct);
        var problem = DocumentLineSendRule.DocumentProblem(doc != null, doc?.DocumentType ?? default, doc?.Status ?? default);
        if (doc == null || problem != null)
            return new DocumentLineSendOptions(false, problem, Array.Empty<DocumentLineSendRule.Target>(), false, null);
        var merged = await OptionsWithTopicAsync(companyId, doc.ContactId, doc.DocumentType, forShare, ct);
        var targets = DocumentLineSendRule.Targets(merged.Select(m => m.Option).ToList(), doc.DocumentType);
        var none = targets.Count == 0
            ? "ผู้ติดต่อนี้ยังไม่มีปลายทาง LINE — ผูก LINE ผู้ใช้ (ลิงก์/QR) หรือกลุ่ม LINE ได้ที่หน้าผู้ติดต่อ แท็บช่องทางแจ้งเตือน"
            : targets.All(t => !t.Available) ? "ไม่มีปลายทาง LINE ที่ส่งได้ตอนนี้ (ดูเหตุของแต่ละปลายทาง)" : null;
        // ฝ่ายค้าน 259 L3: ช่องลิงก์ชำระตัดสินตอนเปิดหน้าต่างด้วยเหตุปฏิเสธตัวเดียวของตัวออกลิงก์ (ชนิด/สกุลเงิน/ใบกำกับแทน) + การเพิกถอน (L5)
        var refusal = _payLinks != null && doc.BalanceDue > 0 ? (await _payLinks.PayQrFactsAsync(companyId, documentId, ct))?.PayLinkRefusal : null;
        var (payOffered, payNote) = DocumentLineSendRule.PayLinkOffer(_payLinks != null, doc.BalanceDue, refusal, doc.PayLinkRevokedAt);
        return new DocumentLineSendOptions(none == null, none, targets, payOffered, payNote);
    }

    public async Task<DocumentLineSendResult> SendAsync(Guid companyId, Guid documentId, IReadOnlyCollection<Guid>? endpointIds, bool includePayLink,
        Guid? userId, string? actor, CancellationToken ct = default, bool includePageLink = false)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.ContactId, d.DocumentType, d.Status }).FirstOrDefaultAsync(ct);
        var docProblem = DocumentLineSendRule.DocumentProblem(doc != null, doc?.DocumentType ?? default, doc?.Status ?? default);
        if (doc == null || docProblem != null)
            throw new BusinessRuleException(docProblem ?? "ไม่พบเอกสาร", DocumentLineSendRule.RuleCode, doc == null ? 404 : 409);
        // ตัวหาผู้รับเดียว (เรื่อง "เอกสาร") · รอบ 267 P3: ลิงก์หน้าเอกสารของใบที่ตอบรับได้ ⇒ รวม 1:1 ที่ติ๊ก "ลิงก์เซ็น" (267-1-61)
        var docSet = await _recipients.ResolveAsync(companyId, doc.ContactId, DocumentLineSendRule.Topic, ct);
        var signSet = includePageLink && CustomerDocPage.HasRespondAction(doc.DocumentType)
            ? (await _recipients.ResolveAsync(companyId, doc.ContactId, NotifyTopic.Sign, ct)).Options : null;
        var merged = DocumentLineSendRule.MergeSignTopic(docSet.Options, signSet);
        var (chosen, pickProblem) = DocumentLineSendRule.Pick(merged.Select(m => m.Option).ToList(), endpointIds);
        if (pickProblem != null) throw new BusinessRuleException(pickProblem, DocumentLineSendRule.RuleCode, 400);
        // ฝ่ายค้าน 259 M1: ปุ่มชำระไม่เข้ากลุ่มซ่อนยอด · L5: ไม่ออกลิงก์ใหม่ทับลิงก์ที่ถูกเพิกถอน
        if (DocumentLineSendRule.PayLinkProblem(includePayLink, chosen) is string hiddenGroup)
            throw new BusinessRuleException(hiddenGroup, DocumentLineSendRule.RuleCode, 400);
        if (includePayLink)
        {
            if (_payLinks == null) throw new BusinessRuleException("บริการลิงก์ชำระเงินยังไม่พร้อม — ส่งโดยไม่ติ๊กลิงก์ชำระ", DocumentLineSendRule.RuleCode, 503);
            // ลิงก์ที่ถูกเพิกถอน ⇒ ตัวออกลิงก์โยน PayLinkRevokedException (409 · 259-2-8) — ไม่ออกใหม่ทับ
            var link = await _payLinks.EnsureLinkAsync(companyId, documentId, null, ct, allowReissueRevoked: false, actor: actor);
            if (link.Url == null)
                throw new BusinessRuleException("ใส่ลิงก์ชำระเงินไม่ได้ — " + (link.Refusal ?? "ไม่ทราบเหตุ") + " (เอาติ๊กลิงก์ชำระออกแล้วส่งได้)",
                    DocumentLineSendRule.RuleCode, 409);
        }
        // รอบ 267 P3 (267-1-61): ลิงก์หน้าเอกสาร — ออกผ่านตัวออกเดียวก่อนเขียนแถวคิว (ออกไม่ได้ ⇒ ปฏิเสธทั้งคำขอพร้อมเหตุ · ไม่ส่งการ์ดไม่มีปุ่มเงียบ ๆ) ·
        // ปลายทางที่กติกาไม่ให้ลิงก์ (กลุ่มซ่อนยอด · กลุ่มของใบที่ตอบรับได้) ⇒ การ์ดดูอย่างเดียว/ไม่มีปุ่ม + เหตุในผลต่อปลายทาง (S21 · S23)
        if (includePageLink && chosen.Any(o => DocumentLineSendRule.PageLinkAllowed(o.Kind, o.ShowAmounts, doc.DocumentType)))
        {
            if (_payLinks == null) throw new BusinessRuleException("บริการลิงก์หน้าเอกสารยังไม่พร้อม", DocumentLineSendRule.RuleCode, 503);
            // ลิงก์ที่ถูกเพิกถอน ⇒ ตัวออกลิงก์โยน PayLinkRevokedException (409 · 259-2-8) — หน้าต่างส่งไม่ออกใหม่ทับ
            var page = await _payLinks.EnsureCustomerLinkAsync(companyId, documentId, "share-line", ct, allowReissueRevoked: false, actor: actor);
            if (!page.Ok)
                throw new BusinessRuleException("ใส่ลิงก์หน้าเอกสารไม่ได้ — " + (page.Refusal ?? "ไม่ทราบเหตุ"), DocumentLineSendRule.RuleCode, 409);
        }
        var now = DateTime.UtcNow;
        var baseKey = DocumentLineSendRule.BaseKey(documentId, now);
        // ฝ่ายค้าน 259 L4: ทีละปลายทาง (บันทึกต่อแถว) — ชนกุญแจของปลายทางหนึ่ง (คำขอพร้อมกัน) ไม่ทิ้งปลายทางอื่น · ผลต่อปลายทางตามจริง
        var outcomes = new List<DocumentLineSendRule.EndpointOutcome>();
        foreach (var o in chosen)
        {
            var key = ContactLineChannel.QueueKey(baseKey, o.EndpointId);
            var pageProblem = includePageLink ? DocumentLineSendRule.PageLinkProblem(o.Kind, o.ShowAmounts, doc.DocumentType) : null;
            var pageLink = includePageLink && pageProblem == null;
            if (await _db.LineQueues.AnyAsync(q => q.CompanyId == companyId && q.IdempotencyKey == key, ct))
            {
                outcomes.Add(new(o.EndpointId!.Value, o.Display, false, pageLink, pageProblem));
                continue;
            }
            var topic = merged.First(m => m.Option.EndpointId == o.EndpointId).Topic;
            var row = new LineQueue
            {
                CompanyId = companyId, RuleId = null, EntityType = "Document", EntityId = documentId, ContactId = doc.ContactId,
                EndpointId = o.EndpointId, EndpointKind = o.Kind, Topic = topic.ToString(), Trigger = ContactLineChannel.ManualTrigger,
                ScheduledFor = now, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(companyId, key),
                IdempotencyKey = key, BaseKey = baseKey, FallbackToEmail = false, IncludePayLink = includePayLink,
                IncludePageLink = includePageLink ? pageLink : null,
                RequestedByUserId = userId, CreatedBy = actor ?? "document-line-send",
            };
            _db.LineQueues.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
                outcomes.Add(new(o.EndpointId!.Value, o.Display, true, pageLink, pageProblem));
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // คำขอพร้อมกันเข้าคิวปลายทางนี้ไปก่อน — ถอดเฉพาะแถวนี้ (ไม่ส่งซ้ำ) แล้วไปต่อปลายทางถัดไป
                _db.Entry(row).State = EntityState.Detached;
                outcomes.Add(new(o.EndpointId!.Value, o.Display, false, pageLink, pageProblem));
            }
        }
        var queued = outcomes.Count(x => x.Queued);
        var already = outcomes.Count - queued;
        return new DocumentLineSendResult(queued, already, DocumentLineSendRule.QueuedMessage(queued, already), outcomes);
    }

    /// <summary>
    /// รอบ 267 P3 (267-1-36 ส่วน LINE · 267-1-63) — <b>ผู้เข้าคิวเดียวของการ์ด "ฉบับเซ็น"</b> กลับไป LINE 1:1 เดิมที่ลูกค้าเปิดลิงก์มาลงนาม ·
    /// ผู้เรียก: งาน outbox (<c>SignEventDispatcher</c>) หลังสร้างฉบับที่ลงนามแล้ว · แถวต้นทางต้องผ่าน <see cref="CustomerShareRule.LineSourceValid"/> (ใบเดียวกัน ·
    /// 1:1 · การ์ดลิงก์หน้าเอกสารที่ LINE รับแล้ว) · กุญแจกันซ้ำต่อแถวต้นทาง (ลองใหม่ = แถวเดิม) · ผู้ส่งคิวตรวจปลายทาง/ใบซ้ำตอนส่ง (ยกเลิกผูก/บล็อก = ข้ามพร้อมเหตุ) ·
    /// คืน (เข้าคิวแล้วหรือมีอยู่แล้ว, หมายเหตุเมื่อไม่เข้าคิว)
    /// </summary>
    public static async Task<(bool Queued, string? Note)> EnqueueSignedCopyAsync(AccountingDbContext db, Guid companyId, Guid documentId, Guid sourceQueueId,
        CancellationToken ct = default)
    {
        var src = await db.LineQueues.AsNoTracking()
            .Where(q => q.Id == sourceQueueId && q.CompanyId == companyId && !q.IsDeleted)
            .Select(q => new { q.EntityType, q.EntityId, q.ContactId, q.EndpointId, q.EndpointKind, q.IncludePageLink, q.Status, q.Topic })
            .FirstOrDefaultAsync(ct);
        if (src?.EndpointId is not Guid endpointId
            || !CustomerShareRule.LineSourceValid(true, src.EntityType, src.EntityId, documentId, src.EndpointKind, src.IncludePageLink, src.Status))
            return (false, "แหล่งที่มาไม่ใช่การ์ดลิงก์หน้าเอกสารของใบนี้ทาง LINE 1:1 — ไม่ส่งฉบับเซ็นทาง LINE");
        var key = $"doc:{documentId}:{ContactLineChannel.SignedCopyTrigger}:{sourceQueueId:N}";
        if (await db.LineQueues.AnyAsync(q => q.CompanyId == companyId && q.IdempotencyKey == key, ct)) return (true, null);
        var now = DateTime.UtcNow;
        var row = new LineQueue
        {
            CompanyId = companyId, RuleId = null, EntityType = "Document", EntityId = documentId, ContactId = src.ContactId,
            EndpointId = endpointId, EndpointKind = src.EndpointKind, Topic = src.Topic, Trigger = ContactLineChannel.SignedCopyTrigger,
            ScheduledFor = now, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(companyId, key),
            IdempotencyKey = key, BaseKey = key, FallbackToEmail = false, IncludePayLink = false, IncludePageLink = true,
            CustomerInitiated = true, CreatedBy = "ระบบ (ฉบับที่ลูกค้าเซ็น)",
        };
        db.LineQueues.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            db.Entry(row).State = EntityState.Detached;   // อีกทางเข้าคิวไปก่อน (กุญแจเดียวกัน) — ไม่ส่งซ้ำ
        }
        return (true, null);
    }
}
