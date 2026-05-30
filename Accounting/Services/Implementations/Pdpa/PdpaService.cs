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
}

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
}
