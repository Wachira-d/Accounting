using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public interface IDocumentApprovalRequestService
{
    Task<DocumentApprovalStateResponse> GetStateAsync(Guid companyId, Guid documentId, Guid userId);
    Task<DocumentApprovalSubmitResult> RequestAsync(Guid companyId, Guid documentId, Guid userId, string? note);
    Task<DocumentApprovalStateResponse> WithdrawAsync(Guid companyId, Guid documentId, Guid userId);
    Task<DocumentApprovalStateResponse> ReturnAsync(Guid companyId, Guid documentId, Guid userId, string? reason);
    Task<List<DocumentApprovalQueueItem>> QueueAsync(Guid companyId, Guid userId);
}

/// <summary>
/// รอบ 276 — "ส่งขออนุมัติ" ของเอกสาร (ผู้เขียนเดียวของ <see cref="DocumentApprovalRequest"/>) ·
/// กฎอนุมัติที่ตรง ⇒ ส่งต่อ workflow เดิม (<see cref="IApprovalService.SubmitForApprovalAsync"/>) · ไม่มีกฎ + ผู้ขออนุมัติเองไม่ได้ ⇒ เส้นพื้นฐาน:
/// ใบ → WaitingApproval · แจ้ง <see cref="NotificationEvents.DocumentApprovalRequested"/> ถึงผู้อนุมัติเอกสาร (ช่องตามตารางแจ้งเตือนของบริษัท) ·
/// ผู้อนุมัติกด "อนุมัติ" (endpoint เดิม → <c>ApproveDocumentAsync</c> → <see cref="OnDocumentApprovedAsync"/>) หรือ "ตีกลับ" พร้อมเหตุผล ·
/// ผู้ขอถอนคำขอได้ (แก้ระหว่างรออนุมัติไม่ได้ — คำตัดสิน 276-5) · audit ทุกขั้นผ่าน <c>AddChainedAuditLog</c>
/// </summary>
public class DocumentApprovalRequestService : IDocumentApprovalRequestService
{
    public const string NoRuleSelfApproveCode = "R276-NO-RULE-SELF";
    public const string NoApproverCode = "R276-NO-APPROVER";
    public const string SelfApprovalCode = "R276-SOD-SELF";
    public const string StateCode = "R276-APPROVAL-STATE";
    private const int MaxText = 500;

    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly IApprovalService _approval;
    private readonly INotificationEngine? _notify;

    public DocumentApprovalRequestService(AccountingDbContext db, IPermissionService perms, IApprovalService approval,
        INotificationEngine? notify = null)
    {
        _db = db; _perms = perms; _approval = approval; _notify = notify;
    }

    private async Task<Document> LoadAsync(Guid companyId, Guid documentId, bool track = false)
    {
        // ตัวกรองแถว "ของตัวเอง" ของคำขอนี้มีผลที่นี่ด้วย (ผู้ถูกจำกัดขอใบของคนอื่น = ไม่พบ)
        var q = _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException(DocumentRestrictionPolicy.NotFoundMessage);
    }

    private Task<DocumentApprovalRequest?> PendingAsync(Guid companyId, Guid documentId) =>
        _db.DocumentApprovalRequests
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId && r.Status == DocumentApprovalRequestStatus.Pending)
            .OrderByDescending(r => r.RequestedAt).FirstOrDefaultAsync();

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return await _db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName);
    }

    private static string? Clip(string? s)
    {
        var t = (s ?? "").Trim();
        return t.Length == 0 ? null : t.Length > MaxText ? t[..MaxText] : t;
    }

    public async Task<DocumentApprovalStateResponse> GetStateAsync(Guid companyId, Guid documentId, Guid userId)
    {
        var doc = await LoadAsync(companyId, documentId);
        var scope = await DocumentRestrictionGuard.ForUserAsync(_db, companyId, userId);
        var latest = await _db.DocumentApprovalRequests.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId)
            .OrderByDescending(r => r.RequestedAt).FirstOrDefaultAsync();
        var workflowPending = await _db.ApprovalRequests.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.EntityType == "Document" && r.EntityId == documentId && r.OverallStatus == ApprovalStatus.Pending);
        var basicPending = latest is { Status: DocumentApprovalRequestStatus.Pending } && doc.Status == DocumentStatus.WaitingApproval;

        var mayFinalize = !scope.Restriction.DraftOnly && await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, doc.DocumentType);
        var canCreate = await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, userId, doc.DocumentType);
        var isRequester = latest != null && latest.RequestedByUserId == userId;
        var names = await NamesAsync(new[] { latest?.RequestedByUserId, latest?.DecidedByUserId }.Where(x => x.HasValue).Select(x => x!.Value));

        var route = workflowPending ? "Workflow" : basicPending ? "Basic" : "None";
        string? label = route switch
        {
            "Workflow" => "รออนุมัติตามกฎอนุมัติ (ดูลำดับผู้อนุมัติด้านบน)",
            "Basic" => $"รออนุมัติจากผู้ดูแล/บัญชี — ส่งโดย {names.GetValueOrDefault(latest!.RequestedByUserId, "-")}",
            _ => latest?.Status switch
            {
                DocumentApprovalRequestStatus.Returned when doc.Status == DocumentStatus.Rejected =>
                    $"ถูกตีกลับโดย {names.GetValueOrDefault(latest.DecidedByUserId ?? Guid.Empty, "-")}: {latest.DecisionReason}",
                DocumentApprovalRequestStatus.Withdrawn when doc.Status == DocumentStatus.Draft => "ถอนคำขออนุมัติแล้ว — แก้ไขแล้วส่งขออนุมัติใหม่ได้",
                _ => null,
            },
        };

        return new DocumentApprovalStateResponse(
            doc.Id, doc.Status.ToString(), route, latest?.Status.ToString(),
            latest == null ? null : names.GetValueOrDefault(latest.RequestedByUserId),
            latest?.RequestedAt, latest?.Note,
            latest?.DecidedByUserId is Guid dby ? names.GetValueOrDefault(dby) : null,
            latest?.DecidedAt, latest?.DecisionReason, label,
            CanRequest: canCreate && DocumentRestrictionPolicy.IsEditableDraftStatus(doc.Status),
            CanWithdraw: basicPending && isRequester,
            CanApprove: basicPending && mayFinalize && !isRequester,
            CanReturn: basicPending && mayFinalize && !isRequester,
            MayFinalize: mayFinalize,
            RestrictionNotice: DocumentRestrictionPolicy.Notice(scope.Restriction));
    }

    public async Task<DocumentApprovalSubmitResult> RequestAsync(Guid companyId, Guid documentId, Guid userId, string? note)
    {
        var doc = await LoadAsync(companyId, documentId, track: true);
        if (!await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, userId, doc.DocumentType))
            throw new BusinessRuleException($"ไม่มีสิทธิ์ส่งขออนุมัติเอกสาร {DocumentTypeNames.Title(doc.DocumentType, "th")} (ต้องมีสิทธิ์สร้างเอกสารชนิดนี้)", StateCode, 403);
        if (doc.Status == DocumentStatus.WaitingApproval)
            throw new BusinessRuleException("ใบนี้ส่งขออนุมัติไว้แล้ว รอผู้อนุมัติอยู่ — ถ้าต้องแก้ ให้กด \"ถอนคำขอ\" ก่อน", StateCode, 409);
        if (!DocumentRestrictionPolicy.IsEditableDraftStatus(doc.Status))
            throw new BusinessRuleException("ส่งขออนุมัติได้เฉพาะใบร่างหรือใบที่ถูกตีกลับ", StateCode, 409);

        // กฎอนุมัติที่ตรง ⇒ workflow เดิม (ผู้อนุมัติระบุตัว · แจ้ง approval.required เอง)
        if (await _approval.HasMatchingRuleAsync(companyId, "Document", documentId))
        {
            if (doc.Status == DocumentStatus.Rejected) { doc.Status = DocumentStatus.Draft; doc.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(); }
            var wf = await _approval.SubmitForApprovalAsync(companyId, "Document", documentId, userId);
            var waiting = wf.Actions.Where(a => a.StepOrder == wf.CurrentStep && a.Status == ApprovalStatus.Pending)
                .Select(a => a.ApproverName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            return new DocumentApprovalSubmitResult("Workflow",
                waiting.Count > 0 ? $"ส่งขออนุมัติตามกฎอนุมัติแล้ว — รออนุมัติจาก {string.Join(", ", waiting)}" : "ส่งขออนุมัติตามกฎอนุมัติแล้ว",
                waiting.Count, waiting);
        }

        var scope = await DocumentRestrictionGuard.ForUserAsync(_db, companyId, userId);
        if (!scope.Restriction.DraftOnly && await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, doc.DocumentType))
            throw new BusinessRuleException("เอกสารนี้ไม่เข้ากฎอนุมัติใด และคุณอนุมัติเองได้ — กด \"อนุมัติ\" ได้เลย", NoRuleSelfApproveCode, 409);

        var approvers = (await DocumentApproverDirectory.ResolveAsync(_db, _perms, companyId, doc.DocumentType)).Where(id => id != userId).ToList();
        if (approvers.Count == 0)
            throw new BusinessRuleException("บริษัทยังไม่มีผู้อนุมัติเอกสารชนิดนี้ (เจ้าของ/บัญชี/ผู้มีสิทธิ์อนุมัติ) — ให้เจ้าของเพิ่มสิทธิ์ที่หน้า \"ทีม\" หรือตั้งกฎอนุมัติ", NoApproverCode, 409);

        var now = DateTime.UtcNow;
        var row = new DocumentApprovalRequest
        {
            CompanyId = companyId, DocumentId = documentId, RequestedByUserId = userId, RequestedAt = now,
            Note = Clip(note), Status = DocumentApprovalRequestStatus.Pending, CreatedBy = userId.ToString(),
        };
        _db.DocumentApprovalRequests.Add(row);
        var fromStatus = doc.Status;
        doc.Status = DocumentStatus.WaitingApproval;
        doc.UpdatedAt = now;
        doc.UpdatedBy = userId.ToString();
        Audit(companyId, userId, AuditAction.Update, doc.Id, new { status = fromStatus.ToString() },
            new { action = "approval-request", status = doc.Status.ToString(), requestId = row.Id, note = row.Note, approverCount = approvers.Count });
        await _db.SaveChangesAsync();

        var names = await NamesAsync(approvers.Append(userId));
        if (_notify != null)
            await _notify.DispatchAsync(companyId, NotificationEvents.DocumentApprovalRequested, new NotificationContext
            {
                Title = $"ขออนุมัติ{DocumentTypeNames.Title(doc.DocumentType, "th")}",
                // คำตัดสิน 276-6: ไม่มีชื่อลูกค้า/เลขผู้เสียภาษี (ข้อความเดียวกันถึงกลุ่ม LINE) — ชนิด · ยอด · ผู้ขอ · หมายเหตุ
                Message = $"{names.GetValueOrDefault(userId, "พนักงาน")} ส่งเอกสารร่างขออนุมัติ · ยอดรวม {doc.TotalAmount:N2} บาท"
                    + (row.Note == null ? "" : $"\nหมายเหตุ: {row.Note}"),
                ActionUrl = $"/pages/documents.html?id={doc.Id}",
                EntityType = "Document", EntityId = doc.Id,
                BellType = NotificationType.ApprovalRequired,
                ActorUserId = userId,
            });

        var approverNames = approvers.Select(a => names.GetValueOrDefault(a, "-")).ToList();
        return new DocumentApprovalSubmitResult("Basic",
            $"ส่งขออนุมัติแล้ว — แจ้งผู้อนุมัติ {approvers.Count} คน ({string.Join(", ", approverNames.Take(5))}{(approverNames.Count > 5 ? " …" : "")}) " +
            "ทางกระดิ่งในระบบ และ LINE/อีเมล/กลุ่ม LINE ตามที่บริษัทตั้งไว้ในหน้าแจ้งเตือน · ระหว่างรออนุมัติแก้ใบไม่ได้ (กด \"ถอนคำขอ\" ถ้าต้องแก้)",
            approvers.Count, approverNames);
    }

    public async Task<DocumentApprovalStateResponse> WithdrawAsync(Guid companyId, Guid documentId, Guid userId)
    {
        var doc = await LoadAsync(companyId, documentId, track: true);
        var pending = await PendingAsync(companyId, documentId);
        if (pending == null || doc.Status != DocumentStatus.WaitingApproval)
            throw new BusinessRuleException("ใบนี้ไม่มีคำขออนุมัติที่รออยู่ (อาจถูกอนุมัติ/ตีกลับไปแล้ว) — รีเฟรชหน้า", StateCode, 409);
        if (pending.RequestedByUserId != userId)
            throw new BusinessRuleException("ถอนคำขอได้เฉพาะผู้ที่ส่งขออนุมัติ — ผู้อนุมัติให้ใช้ \"ตีกลับ\" พร้อมเหตุผล", StateCode, 403);
        var now = DateTime.UtcNow;
        pending.Status = DocumentApprovalRequestStatus.Withdrawn;
        pending.DecidedByUserId = userId; pending.DecidedAt = now; pending.UpdatedAt = now;
        doc.Status = DocumentStatus.Draft; doc.UpdatedAt = now; doc.UpdatedBy = userId.ToString();
        Audit(companyId, userId, AuditAction.Update, doc.Id, new { status = DocumentStatus.WaitingApproval.ToString() },
            new { action = "approval-withdraw", status = doc.Status.ToString(), requestId = pending.Id });
        await _db.SaveChangesAsync();
        return await GetStateAsync(companyId, documentId, userId);
    }

    public async Task<DocumentApprovalStateResponse> ReturnAsync(Guid companyId, Guid documentId, Guid userId, string? reason)
    {
        var text = Clip(reason);
        if (text == null || text.Length < 3)
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ตีกลับ (อย่างน้อย 3 ตัวอักษร) — ผู้ขอจะเห็นเหตุผลนี้", StateCode, 400);
        var doc = await LoadAsync(companyId, documentId, track: true);
        var pending = await PendingAsync(companyId, documentId);
        if (pending == null || doc.Status != DocumentStatus.WaitingApproval)
            throw new BusinessRuleException("ใบนี้ไม่มีคำขออนุมัติที่รออยู่ (อาจถูกอนุมัติ/ถอนไปแล้ว) — รีเฟรชหน้า", StateCode, 409);
        if (pending.RequestedByUserId == userId)
            throw new BusinessRuleException("ผู้ขอตีกลับคำขอของตัวเองไม่ได้ — ใช้ \"ถอนคำขอ\"", SelfApprovalCode, 403);
        if (!await DocumentApproverDirectory.MayApproveAsync(_db, _perms, companyId, userId, doc.DocumentType))
            throw new BusinessRuleException($"ไม่มีสิทธิ์พิจารณาเอกสาร {DocumentTypeNames.Title(doc.DocumentType, "th")} (ต้องมีสิทธิ์อนุมัติเอกสารชนิดนี้)", "PERM-DOC-APPROVE", 403);
        var now = DateTime.UtcNow;
        pending.Status = DocumentApprovalRequestStatus.Returned;
        pending.DecidedByUserId = userId; pending.DecidedAt = now; pending.DecisionReason = text; pending.UpdatedAt = now;
        doc.Status = DocumentStatus.Rejected; doc.UpdatedAt = now; doc.UpdatedBy = userId.ToString();
        Audit(companyId, userId, AuditAction.Reject, doc.Id, new { status = DocumentStatus.WaitingApproval.ToString() },
            new { action = "approval-return", status = doc.Status.ToString(), requestId = pending.Id, reason = text });
        await _db.SaveChangesAsync();
        if (_notify != null)
            await _notify.DispatchAsync(companyId, NotificationEvents.DocumentApprovalReturned, new NotificationContext
            {
                Title = $"{DocumentTypeNames.Title(doc.DocumentType, "th")}ที่ขออนุมัติถูกตีกลับ",
                Message = $"เหตุผล: {text}\nแก้ไขใบแล้วกด \"ส่งขออนุมัติ\" อีกครั้ง",
                ActionUrl = $"/pages/documents.html?id={doc.Id}",
                EntityType = "Document", EntityId = doc.Id,
                BellType = NotificationType.ApprovalRequired,
                RecipientUserId = pending.RequestedByUserId, ActorUserId = userId,
            });
        return await GetStateAsync(companyId, documentId, userId);
    }

    public async Task<List<DocumentApprovalQueueItem>> QueueAsync(Guid companyId, Guid userId)
    {
        var rows = await (from r in _db.DocumentApprovalRequests.AsNoTracking()
                          join d in _db.Documents.AsNoTracking() on r.DocumentId equals d.Id
                          where r.CompanyId == companyId && d.CompanyId == companyId && !d.IsDeleted
                                && r.Status == DocumentApprovalRequestStatus.Pending && d.Status == DocumentStatus.WaitingApproval
                                && r.RequestedByUserId != userId
                          orderby r.RequestedAt
                          select new { r, d.Id, d.DocumentType, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.ContactId })
            .ToListAsync();
        if (rows.Count == 0) return new();
        var mayByType = new Dictionary<DocumentType, bool>();
        foreach (var t in rows.Select(x => x.DocumentType).Distinct())
            mayByType[t] = await DocumentApproverDirectory.MayApproveAsync(_db, _perms, companyId, userId, t);
        var visible = rows.Where(x => mayByType[x.DocumentType]).ToList();
        var names = await NamesAsync(visible.Select(x => x.r.RequestedByUserId));
        var contactIds = visible.Select(x => x.ContactId).Distinct().ToList();
        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        return visible.Select(x => new DocumentApprovalQueueItem(
            x.Id, x.DocumentType.ToString(), DocumentTypeNames.Title(x.DocumentType, "th"), x.DocumentNumber, x.DocumentDate, x.TotalAmount,
            contacts.GetValueOrDefault(x.ContactId), names.GetValueOrDefault(x.r.RequestedByUserId, "-"), x.r.RequestedAt, x.r.Note)).ToList();
    }

    // ===== hook จาก ApproveDocumentAsync (ทุกทางอนุมัติ) =====

    /// <summary>ด่าน SoD ของเส้นพื้นฐาน: ผู้ส่งขออนุมัติห้ามอนุมัติใบที่ตัวเองขอ (ทุกทางเข้า) · ผู้กระทำที่ไม่ใช่ id ผู้ใช้ (ป้ายระบบ/อีเมล) ไม่ใช่ด่านนี้</summary>
    public static async Task EnsureNotSelfApprovalAsync(AccountingDbContext db, Guid companyId, Guid documentId, string? approvedBy)
    {
        if (!Guid.TryParse(approvedBy, out var uid)) return;
        var selfPending = await db.DocumentApprovalRequests.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.DocumentId == documentId
                && r.Status == DocumentApprovalRequestStatus.Pending && r.RequestedByUserId == uid);
        if (selfPending)
            throw new BusinessRuleException("ผู้ส่งขออนุมัติอนุมัติใบที่ตัวเองขอไม่ได้ — ให้ผู้ดูแล/บัญชีเป็นผู้อนุมัติ", SelfApprovalCode, 403);
    }

    /// <summary>ใบที่มีคำขอพื้นฐานค้างอยู่ถูกอนุมัติแล้ว ⇒ ปิดคำขอ (Approved) + แจ้งผู้ขอ · เรียกหลังการอนุมัติบันทึกครบแล้ว (ท้าย <c>ApproveDocumentAsync</c>)</summary>
    public static async Task OnDocumentApprovedAsync(AccountingDbContext db, INotificationEngine? notify, Guid companyId, Guid documentId,
        string? approvedBy, string? documentNumber, DocumentType type)
    {
        var pending = await db.DocumentApprovalRequests
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId && r.Status == DocumentApprovalRequestStatus.Pending)
            .ToListAsync();
        if (pending.Count == 0) return;
        var now = DateTime.UtcNow;
        Guid? approver = Guid.TryParse(approvedBy, out var a) ? a : null;
        foreach (var p in pending)
        {
            p.Status = DocumentApprovalRequestStatus.Approved;
            p.DecidedByUserId = approver; p.DecidedAt = now; p.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
        if (notify == null) return;
        foreach (var requester in pending.Select(p => p.RequestedByUserId).Distinct().Where(r => r != approver))
            await notify.DispatchAsync(companyId, NotificationEvents.DocumentApprovalGranted, new NotificationContext
            {
                Title = $"อนุมัติ{DocumentTypeNames.Title(type, "th")}แล้ว",
                Message = $"เอกสารที่คุณขออนุมัติได้รับการอนุมัติ — เลขที่ {documentNumber}",
                ActionUrl = $"/pages/documents.html?id={documentId}",
                EntityType = "Document", EntityId = documentId,
                BellType = NotificationType.ApprovalRequired,
                RecipientUserId = requester, ActorUserId = approver,
            });
    }

    private void Audit(Guid companyId, Guid userId, AuditAction action, Guid docId, object before, object after)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = action,
            EntityType = "Document", EntityId = docId.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(after),
            Timestamp = DateTime.UtcNow,
        });
    }
}
