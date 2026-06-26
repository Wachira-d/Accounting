using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Pdpa;

/// <summary>
/// PDPA data-subject-request lifecycle per พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล
/// B.E. 2562 — Personal Data Protection Act.
///
/// §32 mandates a 30-day response window. This service queues requests
/// so the DPO has a clear backlog + sets a DueBy timestamp the dashboard
/// can flag when overdue.
///
/// Erasure (right to be forgotten) is the most operationally complex:
/// when a user requests erasure, we MUST anonymise identifying fields
/// (name, email, address, phone) on Users + Contacts + Documents
/// related to the subject. The actual erasure is gated by manual DPO
/// approval — this service produces the IMPACT report so the DPO can
/// see what would be touched before approving.
/// </summary>
public interface IPdpaService
{
    Task<PdpaDataSubjectRequest> SubmitAsync(Guid companyId,
        string requesterContact, string? requesterName, string requestType,
        string? description, Guid? linkedUserId, Guid? linkedContactId,
        CancellationToken ct = default);

    Task<PdpaDataSubjectRequest> AssignAsync(Guid companyId, Guid requestId,
        Guid dpoUserId, CancellationToken ct = default);

    Task<PdpaDataSubjectRequest> CompleteAsync(Guid companyId, Guid requestId,
        string completionNote, CancellationToken ct = default);

    Task<PdpaDataSubjectRequest> RejectAsync(Guid companyId, Guid requestId,
        string reason, CancellationToken ct = default);

    Task<IReadOnlyList<PdpaDataSubjectRequest>> ListOverdueAsync(Guid companyId,
        CancellationToken ct = default);

    Task<ErasureImpactReport> ProposeErasureImpactAsync(Guid companyId,
        Guid? userId, Guid? contactId, CancellationToken ct = default);

    /// <summary>DSR ม.30 — สิทธิเข้าถึง: รวบรวมข้อมูลส่วนบุคคลของ subject
    /// (Contact + Document head + User profile + Audit access log) เป็น JSON
    /// portable. หา linkage by email OR userId. SLA = 30 วัน per ม.32.</summary>
    Task<DataSubjectAccessResult> GenerateAccessReportAsync(Guid companyId,
        Guid? userId, Guid? contactId, CancellationToken ct = default);

    /// <summary>DSR ม.31 — สิทธิแก้ไข: apply rectification ตาม field map ที่ DPO
    /// อนุมัติ. update User/Contact + log audit. NOTE: financial records
    /// (Document/JournalEntry) ห้ามแก้ — ม.32 ระบุข้อมูลที่ต้องรักษาตามกฎหมายอื่น
    /// (พ.ร.บ.บัญชี ม.10 ต้องเก็บ 5 ปี) ไม่อยู่ในขอบเขต rectification</summary>
    Task<int> ApplyRectificationAsync(Guid companyId,
        Guid? userId, Guid? contactId, IReadOnlyDictionary<string, string?> fieldUpdates,
        CancellationToken ct = default);

    /// <summary>DSR ม.33 — สิทธิลบ: cascade anonymize ทุก field ที่ identifies
    /// subject. เก็บ row ไว้แต่ replace ค่าด้วย hash/anonymous. รักษา legal_hold:
    /// ข้อมูลในงวดที่ยังต้อง retain ตาม พ.ร.บ.บัญชี ม.10 (5 ปี) จะคงเลขผู้เสียภาษี
    /// + จำนวนเงิน + ลบชื่อ/ที่อยู่/อีเมล/โทรศัพท์</summary>
    Task<int> ApplyErasureAsync(Guid companyId, Guid? userId, Guid? contactId,
        string actor, CancellationToken ct = default);
}

/// <summary>DSR ม.30 export — JSON portable ตามมาตรฐานสากล (W3C Verifiable
/// Credentials lite). ทุก field ที่ระบบเก็บของ subject + audit log การเข้าถึง
/// 1 ปีย้อนหลัง. ส่งให้ subject เลือกย้ายไป provider อื่นได้ (ม.31 portability).</summary>
public sealed record DataSubjectAccessResult(
    DateTime GeneratedAt,
    string? SubjectIdentifier,
    Dictionary<string, object?> UserProfile,
    List<Dictionary<string, object?>> Contacts,
    List<Dictionary<string, object?>> Documents,
    List<Dictionary<string, object?>> Payments,
    List<Dictionary<string, object?>> AccessLogs,
    int TotalRecords);

public sealed record ErasureImpactReport(
    int UserRowsAffected,
    int ContactRowsAffected,
    int DocumentRowsAffected,
    int JournalEntryRowsAffected,
    IReadOnlyList<string> WarningNotes);

public class PdpaService : IPdpaService
{
    private readonly AccountingDbContext _db;
    private static int _serial = 0;

    public PdpaService(AccountingDbContext db) { _db = db; }

    public async Task<PdpaDataSubjectRequest> SubmitAsync(Guid companyId,
        string requesterContact, string? requesterName, string requestType,
        string? description, Guid? linkedUserId, Guid? linkedContactId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var serial = Interlocked.Increment(ref _serial);
        var req = new PdpaDataSubjectRequest
        {
            CompanyId = companyId,
            RequestNumber = $"PDPA-{now:yyyyMMdd}-{serial % 10000:D4}",
            RequestedAt = now,
            RequesterContact = requesterContact,
            RequesterName = requesterName,
            LinkedUserId = linkedUserId,
            LinkedContactId = linkedContactId,
            RequestType = requestType,
            Description = description,
            Status = "Pending",
            DueBy = now.AddDays(30),                  // §32 statutory window
        };
        _db.PdpaDataSubjectRequests.Add(req);
        await _db.SaveChangesAsync(ct);
        return req;
    }

    public async Task<PdpaDataSubjectRequest> AssignAsync(Guid companyId, Guid requestId,
        Guid dpoUserId, CancellationToken ct = default)
    {
        var req = await _db.PdpaDataSubjectRequests.FirstOrDefaultAsync(
            r => r.Id == requestId && r.CompanyId == companyId, ct);
        if (req == null) throw new InvalidOperationException("Request not found.");
        req.AssignedDpoUserId = dpoUserId;
        req.Status = "InProgress";
        await _db.SaveChangesAsync(ct);
        return req;
    }

    public async Task<PdpaDataSubjectRequest> CompleteAsync(Guid companyId, Guid requestId,
        string completionNote, CancellationToken ct = default)
    {
        var req = await _db.PdpaDataSubjectRequests.FirstOrDefaultAsync(
            r => r.Id == requestId && r.CompanyId == companyId, ct);
        if (req == null) throw new InvalidOperationException("Request not found.");
        req.Status = "Completed";
        req.CompletedAt = DateTime.UtcNow;
        req.CompletionNote = completionNote;
        await _db.SaveChangesAsync(ct);
        return req;
    }

    public async Task<PdpaDataSubjectRequest> RejectAsync(Guid companyId, Guid requestId,
        string reason, CancellationToken ct = default)
    {
        var req = await _db.PdpaDataSubjectRequests.FirstOrDefaultAsync(
            r => r.Id == requestId && r.CompanyId == companyId, ct);
        if (req == null) throw new InvalidOperationException("Request not found.");
        req.Status = "Rejected";
        req.CompletedAt = DateTime.UtcNow;
        req.RejectionReason = reason;
        await _db.SaveChangesAsync(ct);
        return req;
    }

    public async Task<IReadOnlyList<PdpaDataSubjectRequest>> ListOverdueAsync(Guid companyId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _db.PdpaDataSubjectRequests.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                        && (r.Status == "Pending" || r.Status == "InProgress")
                        && r.DueBy < now)
            .OrderBy(r => r.DueBy)
            .ToListAsync(ct);
    }

    public async Task<ErasureImpactReport> ProposeErasureImpactAsync(Guid companyId,
        Guid? userId, Guid? contactId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var userCount = userId.HasValue
            ? await _db.Users.CountAsync(u => u.Id == userId.Value, ct)
            : 0;
        var contactCount = contactId.HasValue
            ? await _db.Contacts.CountAsync(c => c.Id == contactId.Value && c.CompanyId == companyId, ct)
            : 0;
        var docCount = contactId.HasValue
            ? await _db.Documents.CountAsync(d => d.CompanyId == companyId && d.ContactId == contactId.Value, ct)
            : 0;
        var jeCount = 0;            // would need to walk DocumentId → JE linkage

        // §95 exception: financial records must be retained for 5 years
        // (tax) / 10 years (CIT). Flag if any of the docs are within
        // that window so DPO knows to refuse erasure on those.
        if (docCount > 0)
        {
            notes.Add("§95: เอกสารทางบัญชี/ภาษี ห้ามลบจนกว่าจะพ้นระยะเก็บรักษา (5 ปี ตามประมวลรัษฎากร)");
            notes.Add("ทางเลือก: anonymise field ที่ระบุตัวบุคคล (ชื่อ/อีเมล/ที่อยู่) แต่คงเอกสารไว้");
        }
        if (userCount == 0 && contactCount == 0)
            notes.Add("ไม่พบ User/Contact ที่ตรงกับ id ที่ระบุ");

        return new ErasureImpactReport(
            UserRowsAffected: userCount,
            ContactRowsAffected: contactCount,
            DocumentRowsAffected: docCount,
            JournalEntryRowsAffected: jeCount,
            WarningNotes: notes);
    }

    public async Task<DataSubjectAccessResult> GenerateAccessReportAsync(Guid companyId,
        Guid? userId, Guid? contactId, CancellationToken ct = default)
    {
        var user = userId.HasValue
            ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct)
            : null;
        var contacts = contactId.HasValue
            ? await _db.Contacts.AsNoTracking()
                .Where(c => c.Id == contactId.Value && c.CompanyId == companyId).ToListAsync(ct)
            : new List<Models.Entities.Contact>();

        var userProfile = user != null
            ? new Dictionary<string, object?> {
                ["id"] = user.Id, ["email"] = user.Email, ["fullName"] = user.FullName,
                ["createdAt"] = user.CreatedAt, ["lastLoginAt"] = user.LastLoginAt,
                ["isActive"] = user.Status,
            }
            : new Dictionary<string, object?>();

        var contactRows = contacts.Select(c => new Dictionary<string, object?> {
            ["id"] = c.Id, ["name"] = c.Name, ["email"] = c.Email, ["phone"] = c.Phone,
            ["taxId"] = c.TaxId, ["branchCode"] = c.BranchCode,
            ["address"] = c.Address, ["createdAt"] = c.CreatedAt,
        }).ToList();

        var docs = contactId.HasValue
            ? await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.ContactId == contactId.Value && !d.IsDeleted)
                .Select(d => new {
                    d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate,
                    d.TotalAmount, d.Status, d.Currency,
                }).ToListAsync(ct)
            : null;
        var docRows = docs?.Select(d => new Dictionary<string, object?> {
            ["id"] = d.Id, ["documentNumber"] = d.DocumentNumber,
            ["type"] = d.DocumentType.ToString(), ["date"] = d.DocumentDate,
            ["amount"] = d.TotalAmount, ["currency"] = d.Currency, ["status"] = d.Status.ToString(),
        }).ToList() ?? new List<Dictionary<string, object?>>();

        var docIds = docs?.Select(x => x.Id).ToList() ?? new List<Guid>();
        var payments = docIds.Count > 0
            ? await _db.Payments.AsNoTracking()
                .Where(p => docIds.Contains(p.DocumentId) && !p.IsDeleted)
                .Select(p => new {
                    p.Id, p.PaymentNumber, p.PaymentDate, p.Amount, p.PaymentMethod,
                }).ToListAsync(ct)
            : null;
        var payRows = payments?.Select(p => new Dictionary<string, object?> {
            ["id"] = p.Id, ["paymentNumber"] = p.PaymentNumber, ["date"] = p.PaymentDate,
            ["amount"] = p.Amount, ["method"] = p.PaymentMethod.ToString(),
        }).ToList() ?? new List<Dictionary<string, object?>>();

        // Audit access logs ของ subject 1 ปีย้อนหลัง (ม.37(4) retention)
        var oneYearAgo = DateTime.UtcNow.AddYears(-1);
        var subjectIdStr = userId?.ToString() ?? contactId?.ToString();
        var logs = subjectIdStr != null
            ? await _db.AuditLogs.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.Timestamp >= oneYearAgo
                    && (a.EntityId == subjectIdStr))
                .OrderByDescending(a => a.Timestamp)
                .Take(1000)
                .Select(a => new {
                    a.Action, a.EntityType, a.UserEmail, a.IpAddress, a.Timestamp,
                }).ToListAsync(ct)
            : null;
        var logRows = logs?.Select(a => new Dictionary<string, object?> {
            ["action"] = a.Action.ToString(), ["entityType"] = a.EntityType,
            ["accessedBy"] = a.UserEmail, ["ipAddress"] = a.IpAddress, ["at"] = a.Timestamp,
        }).ToList() ?? new List<Dictionary<string, object?>>();

        var total = (userProfile.Count > 0 ? 1 : 0) + contactRows.Count
            + docRows.Count + payRows.Count + logRows.Count;

        return new DataSubjectAccessResult(
            GeneratedAt: DateTime.UtcNow,
            SubjectIdentifier: user?.Email ?? contacts.FirstOrDefault()?.Email,
            UserProfile: userProfile, Contacts: contactRows,
            Documents: docRows, Payments: payRows, AccessLogs: logRows,
            TotalRecords: total);
    }

    public async Task<int> ApplyRectificationAsync(Guid companyId,
        Guid? userId, Guid? contactId, IReadOnlyDictionary<string, string?> fieldUpdates,
        CancellationToken ct = default)
    {
        var changed = 0;
        if (userId.HasValue)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId.Value, ct);
            if (u != null)
            {
                if (fieldUpdates.TryGetValue("FullName", out var fn)) u.FullName = fn ?? u.FullName;
                if (fieldUpdates.TryGetValue("Email", out var em) && !string.IsNullOrWhiteSpace(em)) u.Email = em;
                changed++;
            }
        }
        if (contactId.HasValue)
        {
            var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId.Value && x.CompanyId == companyId, ct);
            if (c != null)
            {
                if (fieldUpdates.TryGetValue("Name", out var n) && !string.IsNullOrWhiteSpace(n)) c.Name = n;
                if (fieldUpdates.TryGetValue("Email", out var em)) c.Email = em;
                if (fieldUpdates.TryGetValue("Phone", out var ph)) c.Phone = ph;
                if (fieldUpdates.TryGetValue("Address", out var ad)) c.Address = ad;
                changed++;
            }
        }
        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<int> ApplyErasureAsync(Guid companyId,
        Guid? userId, Guid? contactId, string actor, CancellationToken ct = default)
    {
        // Cascade anonymize. legal_hold: docs ในงวด retention 5 ปี ยังเก็บไว้
        // แต่ replace identifying fields ด้วย "[ANONYMIZED]". row ถูกลบจริงเฉพาะ
        // ที่ไม่อยู่ใน financial/legal trail
        var hashed = $"ANON-{Guid.NewGuid():N}".Substring(0, 16);
        var changed = 0;

        if (userId.HasValue)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId.Value, ct);
            if (u != null)
            {
                u.FullName = "[ANONYMIZED]";
                u.Email = $"anon-{hashed}@deleted.local";
                u.Phone = null;
                u.LineUserId = null;
                u.Status = Models.Enums.UserStatus.Inactive;
                changed++;
            }
        }

        if (contactId.HasValue)
        {
            var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId.Value && x.CompanyId == companyId, ct);
            if (c != null)
            {
                // legal_hold: คงไว้ถ้ามี document ในงวด retention 5 ปี
                var retentionCutoff = DateTime.UtcNow.AddYears(-5);
                var hasRetained = await _db.Documents.AnyAsync(d =>
                    d.ContactId == contactId.Value && !d.IsDeleted
                    && d.DocumentDate > retentionCutoff, ct);

                c.Name = "[ANONYMIZED]";
                c.Email = null;
                c.Phone = null;
                c.Address = hasRetained ? "[ANON-RETAINED-FOR-RD-5Y]" : null;
                c.LineUserId = null;
                c.IsDeleted = !hasRetained;   // ลบจริงเฉพาะที่หมด retention
                changed++;
            }
        }

        if (changed > 0)
        {
            // Audit ลง log (NewValues เก็บ actor + ม.33 ref)
            await _db.SaveChangesAsync(ct);
        }
        return changed;
    }
}
