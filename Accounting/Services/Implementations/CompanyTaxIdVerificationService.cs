using System.Security.Cryptography;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 285 — ยืนยันเลขผู้เสียภาษีของบริษัท (คำตัดสิน 285-1…285-6 · ตัวตัดสินเดียว <see cref="CompanyTaxIdVerification"/>):
/// บริษัทยื่นหลักฐาน → เทียบชื่อกับทะเบียน (ตรงตัว · ไม่ fuzzy) → ผู้ดูแลแพลตฟอร์มอนุมัติ/ปฏิเสธ/ถอน · ทุกการเปลี่ยนสถานะ audit ผ่าน
/// <c>AddChainedAuditLog</c> · ทางเดียวที่ตั้ง <c>Company.TaxIdVerifiedAt</c> = <see cref="ApproveAsync"/>
/// </summary>
public class CompanyTaxIdVerificationService
{
    private readonly AccountingDbContext _db;
    private readonly IFileAttachmentService _files;
    private readonly IDbdLookupService? _registry;

    public CompanyTaxIdVerificationService(AccountingDbContext db, IFileAttachmentService files, IDbdLookupService? registry = null)
    {
        _db = db;
        _files = files;
        _registry = registry;
    }

    /// <summary>ข้อผิดพลาดที่ผู้ใช้แก้ได้ — ข้อความไทยตรงตัว</summary>
    public sealed class VerificationException : BusinessRuleException
    {
        public VerificationException(string message, int status = 400) : base(message, CompanyTaxIdVerification.RuleCode, status) { }
    }

    public sealed record SubmitResult(Guid RequestId, string Status, string RegistryResult);

    /// <summary>สถานะของบริษัท (หน้าตั้งค่าบริษัท) — เซิร์ฟเวอร์ตัดสินทุกป้าย</summary>
    public async Task<object?> GetStatusAsync(Guid companyId, CancellationToken ct = default)
    {
        var c = await _db.Companies.AsNoTracking().Where(x => x.Id == companyId && !x.IsDeleted)
            .Select(x => new { x.TaxId, x.TaxIdVerifiedAt, x.TaxIdVerifiedFor }).FirstOrDefaultAsync(ct);
        if (c == null) return null;
        var verified = CompanyTaxIdVerification.IsVerified(c.TaxId, c.TaxIdVerifiedAt, c.TaxIdVerifiedFor);
        var last = await _db.CompanyTaxIdVerificationRequests.AsNoTracking().Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.SubmittedAt).FirstOrDefaultAsync(ct);
        var lastForCurrent = last != null && last.TaxId == ThaiTaxId.Normalize(c.TaxId);
        var status = verified ? CompanyTaxIdVerification.StatusApproved : lastForCurrent ? last!.Status : null;
        var canSubmit = !verified && status != CompanyTaxIdVerification.StatusPending && ThaiTaxId.IsValid(c.TaxId);
        return new
        {
            Verified = verified,
            VerifiedAt = verified ? c.TaxIdVerifiedAt : null,
            TaxId = c.TaxId,
            Status = status,
            StatusLabel = CompanyTaxIdVerification.StatusLabel(status),
            CanSubmit = canSubmit,
            CannotSubmitReason = canSubmit || verified || status == CompanyTaxIdVerification.StatusPending ? null
                : CompanyTaxIdVerification.SubmitProblem(c.TaxId, CompanyTaxIdVerification.EvidencePp20, 1, false, false, CompanyTaxIdVerification.RegistryUnknown, null),
            LastRequest = last == null ? null : new
            {
                last.Id, last.TaxId, last.SubmittedAt, last.Status,
                StatusLabel = CompanyTaxIdVerification.StatusLabel(last.Status),
                EvidenceLabel = CompanyTaxIdVerification.EvidenceLabel(last.EvidenceKind),
                RegistryLabel = CompanyTaxIdVerification.RegistryLabel(last.RegistryResult, last.RegistryName),
                last.ReviewedAt, last.ReviewNote,
                ForCurrentTaxId = lastForCurrent,
            },
            EvidenceKinds = CompanyTaxIdVerification.EvidenceKinds.Select(k => new { Value = k, Label = CompanyTaxIdVerification.EvidenceLabel(k) }),
            Effect = "ยืนยันแล้ว ⇒ คำขอเชื่อมคู่ค้าที่ส่งถึงเลขนี้ก่อนบริษัทสมัคร/ตั้งเลขจะเปิดให้ตอบได้ · คู่ค้าเห็นว่าเลขนี้ผ่านการยืนยัน · เปลี่ยนเลขผู้เสียภาษี = ต้องยืนยันใหม่",
        };
    }

    /// <summary>บริษัทยื่นหลักฐาน — ตรวจชนิดไฟล์จากไบต์ (PDF/รูป) · เทียบชื่อกับทะเบียนเมื่อเป็นนิติบุคคล · ชื่อไม่ตรง = ยื่นไม่ได้ · audit</summary>
    public async Task<SubmitResult> SubmitAsync(Guid companyId, Guid userId, string? evidenceKind, byte[] content, string? clientFileName, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == companyId && !x.IsDeleted, ct)
            ?? throw new VerificationException("ไม่พบบริษัท", 404);
        var verified = CompanyTaxIdVerification.IsVerified(company);
        var tax = ThaiTaxId.Normalize(company.TaxId);
        var hasPending = await _db.CompanyTaxIdVerificationRequests.AnyAsync(r => r.CompanyId == companyId && r.TaxId == tax
            && r.Status == CompanyTaxIdVerification.StatusPending, ct);
        // ด่านที่ไม่ต้องถามทะเบียนก่อน (ไม่ยิงทะเบียนเมื่อคำขอผิดชัด)
        var pre = CompanyTaxIdVerification.SubmitProblem(company.TaxId, evidenceKind, content.Length, verified, hasPending, CompanyTaxIdVerification.RegistryUnknown, null);
        if (pre != null) throw new VerificationException(pre, verified || hasPending ? 409 : 400);
        var kind = UploadFileType.SniffAttachment(content.AsSpan(0, Math.Min(content.Length, UploadFileType.HeaderBytes)), clientFileName);
        if (kind is not { } k || !(k.IsImage || k.Extension == ".pdf"))
            throw new VerificationException("ไฟล์หลักฐานต้องเป็น PDF หรือรูปภาพ (JPG/PNG/WEBP) — ตัดสินจากเนื้อไฟล์");

        string? registryName = null;
        if (_registry != null && ThaiTaxId.IsJuristic(tax))
        {
            try { registryName = (await _registry.GetByJuristicIdAsync(tax))?.NameTh; }
            catch (Exception) { registryName = null; }   // ทะเบียนไม่ตอบ = "ไม่ทราบ" ⇒ ผู้ดูแลตัดสินจากเอกสาร (ป้ายบอกชัดบนคำขอ) — ไม่ใช่ผ่าน
        }
        var registry = CompanyTaxIdVerification.CompareRegistryName(company.Name, registryName);
        var problem = CompanyTaxIdVerification.SubmitProblem(company.TaxId, evidenceKind, content.Length, verified, hasPending, registry, registryName);
        if (problem != null) throw new VerificationException(problem);

        var req = new CompanyTaxIdVerificationRequest
        {
            CompanyId = companyId, TaxId = tax, BranchCode = company.BranchCode, CompanyName = company.Name, EvidenceKind = evidenceKind!,
            EvidenceSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            RegistryName = registryName, RegistryResult = registry, Status = CompanyTaxIdVerification.StatusPending,
            SubmittedByUserId = userId, SubmittedAt = nowUtc, CreatedBy = userId.ToString(),
        };
        var baseName = Path.GetFileNameWithoutExtension(clientFileName);
        var att = await _files.UploadBytesAsync(companyId, CompanyTaxIdVerification.AttachmentEntityType, req.Id,
            (string.IsNullOrWhiteSpace(baseName) ? "taxid-evidence" : baseName) + k.Extension, k.ContentType, content, userId);
        req.AttachmentId = att.Id;
        _db.CompanyTaxIdVerificationRequests.Add(req);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Create, EntityType = nameof(CompanyTaxIdVerificationRequest), EntityId = req.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                req.TaxId, req.EvidenceKind, req.EvidenceSha256, req.RegistryResult, req.RegistryName,
                ruleCode = CompanyTaxIdVerification.RuleCode, legalReference = CompanyTaxIdVerification.LegalReference,
            }),
            Timestamp = nowUtc,
        });
        await _db.SaveChangesAsync(ct);
        return new SubmitResult(req.Id, req.Status, registry);
    }

    /// <summary>คิวของผู้ดูแลแพลตฟอร์ม — ข้ามบริษัท (SystemAdmin เท่านั้น · ผู้เรียกตรวจบทบาท) · ป้ายจากเซิร์ฟเวอร์</summary>
    public async Task<List<object>> ListForAdminAsync(string? status, CancellationToken ct = default)
    {
        var st = string.IsNullOrWhiteSpace(status) ? CompanyTaxIdVerification.StatusPending : status;
        var rows = await _db.CompanyTaxIdVerificationRequests.AsNoTracking()
            .Where(r => st == "All" || r.Status == st)
            .OrderBy(r => r.SubmittedAt).Take(200)
            .Join(_db.Companies.AsNoTracking(), r => r.CompanyId, c => c.Id, (r, c) => new { r, c.Name, c.TaxId, c.IsDeleted })
            .ToListAsync(ct);
        return rows.Select(x => (object)new
        {
            x.r.Id, x.r.CompanyId, CompanyName = x.Name, CurrentTaxId = x.TaxId, RequestTaxId = x.r.TaxId,
            NameAtSubmit = x.r.CompanyName, x.r.BranchCode, BranchLabel = TaxBranchCode.Label(x.r.BranchCode),
            x.r.EvidenceKind, EvidenceLabel = CompanyTaxIdVerification.EvidenceLabel(x.r.EvidenceKind), x.r.EvidenceSha256,
            x.r.RegistryResult, x.r.RegistryName, RegistryLabel = CompanyTaxIdVerification.RegistryLabel(x.r.RegistryResult, x.r.RegistryName),
            x.r.Status, StatusLabel = CompanyTaxIdVerification.StatusLabel(x.r.Status), x.r.SubmittedAt, x.r.ReviewedAt, x.r.ReviewNote,
            ApproveProblem = x.IsDeleted ? "บริษัทถูกลบแล้ว"
                : CompanyTaxIdVerification.ApproveProblem(x.r.Status, x.r.TaxId, x.TaxId, x.r.RegistryResult, x.r.AttachmentId != null),
        }).ToList();
    }

    /// <summary>ไฟล์หลักฐานของคำขอ (ผู้ดูแลแพลตฟอร์มเปิดดู) — อ่านด้วย tenant ของคำขอ · ตรวจ SHA-256 ตรงกับตอนยื่น (ไฟล์ถูกสลับ = ล้มดัง)</summary>
    public async Task<(byte[] Bytes, string ContentType, string FileName)?> ReadEvidenceAsync(Guid requestId, CancellationToken ct = default)
    {
        var r = await _db.CompanyTaxIdVerificationRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId, ct);
        if (r?.AttachmentId is not Guid attId) return null;
        var meta = await _files.GetByIdAsync(r.CompanyId, attId);
        var bytes = await _files.ReadBytesAsync(r.CompanyId, attId, ct);
        if (meta == null || bytes == null) return null;
        if (r.EvidenceSha256 != null && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != r.EvidenceSha256)
            throw new VerificationException("ไฟล์หลักฐานไม่ตรงกับที่บริษัทยื่น (SHA-256 ไม่ตรง) — อย่าอนุมัติจากไฟล์นี้", 409);
        return (bytes, meta.ContentType, meta.OriginalFileName);
    }

    /// <summary>อนุมัติ — ใต้ล็อกแถวบริษัท · ด่าน <see cref="CompanyTaxIdVerification.ApproveProblem"/> · ตั้งสามช่องของบริษัท + audit · คำขอ Pending อื่นของบริษัทยังอยู่ (ไม่แตะ)</summary>
    public async Task ApproveAsync(Guid requestId, Guid adminUserId, string? note, DateTime nowUtc, CancellationToken ct = default)
    {
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var r = await _db.CompanyTaxIdVerificationRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct)
                ?? throw new VerificationException("ไม่พบคำขอ", 404);
            // ล็อกแถวบริษัท: กันอนุมัติพร้อมกับบริษัทเปลี่ยนเลข (ตรวจเลขตรงกันใต้ล็อก)
            await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"Companies\" WHERE \"Id\" = {0} FOR UPDATE", new object[] { r.CompanyId }, ct);
            var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == r.CompanyId && !x.IsDeleted, ct)
                ?? throw new VerificationException("ไม่พบบริษัท", 404);
            await _db.Entry(company).ReloadAsync(ct);
            await _db.Entry(r).ReloadAsync(ct);
            var problem = CompanyTaxIdVerification.ApproveProblem(r.Status, r.TaxId, company.TaxId, r.RegistryResult, r.AttachmentId != null);
            if (problem != null) throw new VerificationException(problem, 409);
            if (note != null && note.Trim().Length > CompanyTaxIdVerification.MaxNoteLength) throw new VerificationException("หมายเหตุยาวเกิน 1,000 ตัวอักษร");
            r.Status = CompanyTaxIdVerification.StatusApproved;
            r.ReviewedByUserId = adminUserId;
            r.ReviewedAt = nowUtc;
            r.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            r.UpdatedAt = nowUtc;
            r.UpdatedBy = adminUserId.ToString();
            company.TaxIdVerifiedAt = nowUtc;
            company.TaxIdVerifiedFor = r.TaxId;
            company.TaxIdVerificationRequestId = r.Id;
            Audit(r.CompanyId, adminUserId, r.Id, CompanyTaxIdVerification.StatusApproved, r.ReviewNote, r.TaxId, nowUtc);
            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    /// <summary>ปฏิเสธคำขอที่รอตรวจ — ต้องมีเหตุ (บริษัทเห็น) · audit</summary>
    public async Task RejectAsync(Guid requestId, Guid adminUserId, string? reason, DateTime nowUtc, CancellationToken ct = default)
    {
        var rp = CompanyTaxIdVerification.ReasonProblem(reason);
        if (rp != null) throw new VerificationException(rp);
        var r = await _db.CompanyTaxIdVerificationRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw new VerificationException("ไม่พบคำขอ", 404);
        if (r.Status != CompanyTaxIdVerification.StatusPending) throw new VerificationException("คำขอนี้ไม่ได้อยู่ในสถานะรอตรวจ", 409);
        r.Status = CompanyTaxIdVerification.StatusRejected;
        r.ReviewedByUserId = adminUserId;
        r.ReviewedAt = nowUtc;
        r.ReviewNote = reason!.Trim();
        r.UpdatedAt = nowUtc;
        r.UpdatedBy = adminUserId.ToString();
        Audit(r.CompanyId, adminUserId, r.Id, CompanyTaxIdVerification.StatusRejected, r.ReviewNote, r.TaxId, nowUtc);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>ถอนการยืนยันที่อนุมัติไปแล้ว (เช่น พบภายหลังว่าเอกสารปลอม) — ต้องมีเหตุ · ล้างสามช่องของบริษัท · audit ·
    /// คู่ค้าที่เชื่อมแล้วไม่ถูกแตะ (เชื่อมด้วยความยินยอมของทั้งสองฝั่ง — ระงับได้ที่หน้าคู่ค้า)</summary>
    public async Task RevokeAsync(Guid requestId, Guid adminUserId, string? reason, DateTime nowUtc, CancellationToken ct = default)
    {
        var rp = CompanyTaxIdVerification.ReasonProblem(reason);
        if (rp != null) throw new VerificationException(rp);
        var r = await _db.CompanyTaxIdVerificationRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw new VerificationException("ไม่พบคำขอ", 404);
        if (r.Status != CompanyTaxIdVerification.StatusApproved) throw new VerificationException("ถอนได้เฉพาะคำขอที่อนุมัติแล้ว", 409);
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == r.CompanyId, ct);
        r.Status = CompanyTaxIdVerification.StatusRevoked;
        r.ReviewedByUserId = adminUserId;
        r.ReviewedAt = nowUtc;
        r.ReviewNote = reason!.Trim();
        r.UpdatedAt = nowUtc;
        r.UpdatedBy = adminUserId.ToString();
        if (company != null && company.TaxIdVerificationRequestId == r.Id) CompanyTaxIdVerification.ClearOnTaxIdChange(company);
        Audit(r.CompanyId, adminUserId, r.Id, CompanyTaxIdVerification.StatusRevoked, r.ReviewNote, r.TaxId, nowUtc);
        await _db.SaveChangesAsync(ct);
    }

    private void Audit(Guid companyId, Guid userId, Guid requestId, string status, string? note, string taxId, DateTime nowUtc)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(CompanyTaxIdVerificationRequest), EntityId = requestId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                status, note, taxId, ruleCode = CompanyTaxIdVerification.RuleCode, legalReference = CompanyTaxIdVerification.LegalReference,
            }),
            Timestamp = nowUtc,
        });
}
