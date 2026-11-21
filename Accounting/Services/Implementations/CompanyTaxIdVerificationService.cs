using System.Security.Cryptography;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 285 — ยืนยันเลขผู้เสียภาษีของบริษัท (คำตัดสิน 285-1…285-6 · ตัวตัดสินเดียว <see cref="CompanyTaxIdVerification"/>):
/// บริษัทยื่นหลักฐาน → เทียบชื่อกับทะเบียน (ตรงตัว · ไม่ fuzzy) → ผู้ดูแลแพลตฟอร์มอนุมัติ/ปฏิเสธ/ถอน · ทุกการเปลี่ยนสถานะ audit ผ่าน
/// <c>AddChainedAuditLog</c> · ทางเดียวที่ตั้ง <c>Company.TaxIdVerifiedAt</c> = <see cref="ApproveAsync"/>
/// </summary>
public class CompanyTaxIdVerificationService
{
    private readonly AccountingDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IDbdLookupService? _registry;
    private readonly INotificationService? _notifications;
    private readonly ILogger<CompanyTaxIdVerificationService>? _logger;

    /// <param name="storage">ที่เก็บไฟล์กลางตัวเดียว (กฎเหล็ก #4 D) — หลักฐานไม่อยู่ใน <c>FileAttachments</c> (ข้อเพิ่มรอบ 285)</param>
    public CompanyTaxIdVerificationService(AccountingDbContext db, IFileStorage storage, IDbdLookupService? registry = null,
        INotificationService? notifications = null, ILogger<CompanyTaxIdVerificationService>? logger = null)
    {
        _db = db;
        _storage = storage;
        _registry = registry;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>ผู้เปิดไฟล์หลักฐาน (บันทึกลง <c>PdpaPiiAccessLogs</c>)</summary>
    public sealed record EvidenceViewer(Guid UserId, string? Ip, string? UserAgent, string Purpose);

    /// <summary>ข้อผิดพลาดที่ผู้ใช้แก้ได้ — ข้อความไทยตรงตัว</summary>
    public sealed class VerificationException : BusinessRuleException
    {
        public VerificationException(string message, int status = 400) : base(message, CompanyTaxIdVerification.RuleCode, status) { }
    }

    public sealed record SubmitResult(Guid RequestId, string Status, string RegistryResult);

    /// <summary>สถานะของบริษัท (หน้าตั้งค่าบริษัท) — เซิร์ฟเวอร์ตัดสินทุกป้าย</summary>
    /// <param name="viewerUserId">ผู้เปิดหน้า — ใช้ตัดสินว่าแสดงปุ่ม "เปิดไฟล์หลักฐาน" ไหม (เฉพาะเจ้าของ · ด่านจริงอยู่ที่ <see cref="ReadEvidenceAsync"/>)</param>
    public async Task<object?> GetStatusAsync(Guid companyId, CancellationToken ct = default, Guid? viewerUserId = null)
    {
        var viewerIsOwner = viewerUserId is Guid vu && await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == vu && cu.Role == UserRole.Owner, ct);
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
                : CompanyTaxIdVerification.SubmitProblem(c.TaxId, CompanyTaxIdVerification.EvidencePp20, 1, false, false),
            LastRequest = last == null ? null : new
            {
                last.Id, last.TaxId, last.SubmittedAt, last.Status,
                StatusLabel = CompanyTaxIdVerification.StatusLabel(last.Status),
                EvidenceLabel = CompanyTaxIdVerification.EvidenceLabel(last.EvidenceKind),
                RegistryLabel = CompanyTaxIdVerification.RegistryLabel(last.RegistryResult, last.RegistryName),
                last.ReviewedAt, last.ReviewNote,
                ForCurrentTaxId = lastForCurrent,
                CanViewEvidence = last.EvidenceLocation != null && CompanyTaxIdVerification.CanViewEvidence(false, viewerIsOwner),
                last.EvidenceRetainUntil,
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
        var pre = CompanyTaxIdVerification.SubmitProblem(company.TaxId, evidenceKind, content.Length, verified, hasPending);
        if (pre != null) throw new VerificationException(pre, verified || hasPending ? 409 : 400);
        var kind = UploadFileType.SniffAttachment(content.AsSpan(0, Math.Min(content.Length, UploadFileType.HeaderBytes)), clientFileName);
        if (kind is not { } k || !(k.IsImage || k.Extension == ".pdf"))
            throw new VerificationException("ไฟล์หลักฐานต้องเป็น PDF หรือรูปภาพ (JPG/PNG/WEBP) — ตัดสินจากเนื้อไฟล์");

        string? registryName = null;
        if (_registry != null && ThaiTaxId.IsJuristic(tax))
        {
            try { registryName = (await _registry.GetByJuristicIdAsync(tax))?.NameTh; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ฝ่ายค้าน LOW-B3: ทะเบียนไม่ตอบ = "ไม่ทราบ" ⇒ ผู้ดูแลตัดสินจากเอกสาร (ป้ายบอกชัดบนคำขอ) — ไม่ใช่ผ่าน · บันทึกเหตุ (ไม่กลืนเงียบ)
                _logger?.LogWarning(ex, "ถามทะเบียนของเลข {Tax} ไม่สำเร็จตอนยื่นยืนยันเลขภาษี (บริษัท {Company}) — คำขอติดป้าย ตรวจกับทะเบียนไม่ได้", tax, companyId);
                registryName = null;
            }
        }
        // ฝ่ายค้าน MED-B2: ชื่อไม่ตรงทะเบียน (ชื่อการค้า/ชื่ออังกฤษ) ยื่นได้ — ติดธงให้ผู้ดูแล · อนุมัติต้องระบุเหตุผล (ไม่ fuzzy)
        var registry = CompanyTaxIdVerification.CompareRegistryName(company.Name, registryName);

        var req = new CompanyTaxIdVerificationRequest
        {
            CompanyId = companyId, TaxId = tax, BranchCode = company.BranchCode, CompanyName = company.Name, EvidenceKind = evidenceKind!,
            EvidenceSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            RegistryName = registryName, RegistryResult = registry, Status = CompanyTaxIdVerification.StatusPending,
            SubmittedByUserId = userId, SubmittedAt = nowUtc, CreatedBy = userId.ToString(),
        };
        var baseName = Path.GetFileNameWithoutExtension(clientFileName);
        // ชื่อไฟล์บนที่เก็บ = id ของคำขอ + นามสกุลจากไบต์ (ไม่ใช้ชื่อ/นามสกุลของ client) · ชื่อเดิมเก็บไว้แสดงเท่านั้น
        req.EvidenceLocation = Path.Combine(_storage.Locator.AttachmentsRoot, companyId.ToString(), CompanyTaxIdVerification.EvidenceFolder, req.Id.ToString("N") + k.Extension);
        req.EvidenceContentType = k.ContentType;
        req.EvidenceFileName = (string.IsNullOrWhiteSpace(baseName) ? "taxid-evidence" : baseName[..Math.Min(baseName.Length, 200)]) + k.Extension;
        req.EvidenceRetainUntil = CompanyTaxIdVerification.EvidenceRetainUntil(nowUtc);
        await _storage.WriteAsync(req.EvidenceLocation, content, k.ContentType, ct);   // เขียนล้ม = โยน (ไม่มีแถวค้างที่ไม่มีไฟล์)
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
        // แจ้งผู้ดูแลแพลตฟอร์ม (กระดิ่ง + อีเมลตามค่าตั้งของผู้รับ) — คำขออยู่ในคิวแล้ว การแจ้งล้มไม่ย้อนการยื่น แต่ล้มดัง (LogError)
        var (title, message) = CompanyTaxIdVerification.AdminNewRequestNotice(company.Name, tax, registry, registryName);
        try
        {
            var sent = await PlatformPaymentAttention.NotifyAdminsAboutAsync(_db, _notifications, title, message, CompanyTaxIdVerification.AdminPageUrl,
                nameof(CompanyTaxIdVerificationRequest), req.Id, ct);
            if (sent == 0) _logger?.LogError("คำขอยืนยันเลขภาษี {Request} ของบริษัท {Company} ไม่มีผู้ดูแลแพลตฟอร์มให้แจ้ง (ไม่มีบริษัทผู้ให้บริการ/ผู้ดูแล) — ดูคิวที่ {Url}",
                req.Id, companyId, CompanyTaxIdVerification.AdminPageUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "แจ้งผู้ดูแลแพลตฟอร์มเรื่องคำขอยืนยันเลขภาษี {Request} ไม่สำเร็จ — คำขออยู่ในคิวแล้ว", req.Id);
        }
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
        // ฝ่ายค้าน MED-B1: ธงเด่น "เลขนี้ยืนยันแล้วโดยบริษัท X" (ตัวหาเดียวกับด่านอนุมัติ)
        var conflicts = new Dictionary<Guid, (string Name, Guid? RequestId)?>();
        foreach (var x in rows) conflicts[x.r.Id] = await VerifiedByOtherAsync(x.r.TaxId, x.r.BranchCode, x.r.CompanyId, ct);
        return rows.Select(x => (object)new
        {
            ConflictLabel = conflicts[x.r.Id] is { } cf ? CompanyTaxIdVerification.ConflictLabel(cf.Name) : null,
            ConflictRequestId = conflicts[x.r.Id]?.RequestId,
            x.r.Id, x.r.CompanyId, CompanyName = x.Name, CurrentTaxId = x.TaxId, RequestTaxId = x.r.TaxId,
            NameAtSubmit = x.r.CompanyName, x.r.BranchCode, BranchLabel = TaxBranchCode.Label(x.r.BranchCode),
            x.r.EvidenceKind, EvidenceLabel = CompanyTaxIdVerification.EvidenceLabel(x.r.EvidenceKind), x.r.EvidenceSha256,
            x.r.RegistryResult, x.r.RegistryName, RegistryLabel = CompanyTaxIdVerification.RegistryLabel(x.r.RegistryResult, x.r.RegistryName),
            x.r.Status, StatusLabel = CompanyTaxIdVerification.StatusLabel(x.r.Status), x.r.SubmittedAt, x.r.ReviewedAt, x.r.ReviewNote,
            x.r.EvidenceRetainUntil,
            ApproveProblem = x.IsDeleted ? "บริษัทถูกลบแล้ว"
                : CompanyTaxIdVerification.ApproveProblem(x.r.Status, x.r.TaxId, x.TaxId, x.r.RegistryResult, x.r.EvidenceLocation != null,
                    note: "(ตรวจตอนกด)", verifiedByOtherCompany: conflicts[x.r.Id]?.Name),
            NeedsApproveReason = x.r.RegistryResult == CompanyTaxIdVerification.RegistryMismatch,
        }).ToList();
    }

    /// <summary>ไฟล์หลักฐานของคำขอ — ผู้ดูแลแพลตฟอร์ม (<paramref name="companyId"/> = null) หรือ<b>เจ้าของ</b>บริษัทของคำขอ
    /// (<see cref="CompanyTaxIdVerification.CanViewEvidence"/>) · บันทึก <c>PdpaPiiAccessLogs</c> ก่อนคืนไบต์ · ตรวจ SHA-256 ตรงกับตอนยื่น (ไฟล์ถูกสลับ = ล้มดัง) ·
    /// ไม่พบ/ไม่ใช่ของบริษัทนี้ = null · ไม่มีสิทธิ์ = 403</summary>
    public async Task<(byte[] Bytes, string ContentType, string FileName)?> ReadEvidenceAsync(Guid requestId, Guid? companyId, EvidenceViewer viewer,
        CancellationToken ct = default)
    {
        var r = await _db.CompanyTaxIdVerificationRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == requestId && (companyId == null || x.CompanyId == companyId), ct);
        if (r?.EvidenceLocation is not string loc) return null;
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == viewer.UserId && !u.IsDeleted)
            .Select(u => new { u.Email, u.IsSystemAdmin }).FirstOrDefaultAsync(ct);
        var isOwner = companyId != null && await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == r.CompanyId && cu.UserId == viewer.UserId && cu.Role == UserRole.Owner, ct);
        var isAdmin = companyId == null && user?.IsSystemAdmin == true;
        if (user == null || !CompanyTaxIdVerification.CanViewEvidence(isAdmin, isOwner))
            throw new VerificationException(CompanyTaxIdVerification.EvidenceDeniedMessage, 403);
        var bytes = await _storage.ReadAllBytesAsync(loc, ct)
            ?? throw new FileNotFoundException("ไฟล์หลักฐานหายจากที่เก็บไฟล์ (แถวคำขอยังอยู่) — ล้มดัง ไม่คืนไฟล์ว่าง", loc);
        if (r.EvidenceSha256 != null && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != r.EvidenceSha256)
            throw new VerificationException("ไฟล์หลักฐานไม่ตรงกับที่บริษัทยื่น (SHA-256 ไม่ตรง) — อย่าอนุมัติจากไฟล์นี้", 409);
        _db.PdpaPiiAccessLogs.Add(new PdpaPiiAccessLog
        {
            CompanyId = r.CompanyId, ActorUserId = viewer.UserId, ActorEmail = user.Email, SubjectType = CompanyTaxIdVerification.PiiSubjectType,
            SubjectId = r.Id, FieldName = "Evidence", Operation = "Read", Purpose = viewer.Purpose, IpAddress = viewer.Ip, UserAgent = viewer.UserAgent,
        });
        await _db.SaveChangesAsync(ct);
        return (bytes, r.EvidenceContentType ?? "application/octet-stream", r.EvidenceFileName ?? "taxid-evidence");
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
            // ฝ่ายค้าน MED-B1: เลข+สาขาหนึ่งยืนยันได้บริษัทเดียว — ล็อกต่อ "ช่อง" (กันสองคำขอของเลขเดียวกันอนุมัติพร้อมกัน) แล้วหาเจ้าของเดิมใต้ล็อก
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(CompanyTaxIdVerification.RuleCode, CompanyTaxIdVerification.SlotKey(r.TaxId, r.BranchCode)) }, ct);
            var other = await VerifiedByOtherAsync(r.TaxId, r.BranchCode, r.CompanyId, ct);
            var problem = CompanyTaxIdVerification.ApproveProblem(r.Status, r.TaxId, company.TaxId, r.RegistryResult, r.EvidenceLocation != null,
                note, other?.Name);
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
        await NotifyCompanyAsync(requestId, ct);
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
        await NotifyCompanyAsync(r.Id, ct);
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
        await NotifyCompanyAsync(r.Id, ct);
    }

    /// <summary>ฝ่ายค้าน MED-B1: บริษัทอื่นที่ยืนยันเลข+สาขาเดียวกันอยู่ (ตัวตัดสิน <see cref="CompanyTaxIdVerification.IsVerified(Company)"/> + <see cref="CompanyTaxIdVerification.SlotKey"/>) ·
    /// คืนชื่อ + คำขอที่อนุมัติไว้ (ให้ผู้ดูแลถอนได้) · ไม่มี = null</summary>
    private async Task<(string Name, Guid? RequestId)?> VerifiedByOtherAsync(string taxId, string? branchCode, Guid companyId, CancellationToken ct)
    {
        var tax = ThaiTaxId.Normalize(taxId);
        var slot = CompanyTaxIdVerification.SlotKey(tax, branchCode);
        var others = await _db.Companies.AsNoTracking()
            .Where(c => c.Id != companyId && !c.IsDeleted && c.TaxIdVerifiedFor == tax && c.TaxIdVerifiedAt != null)
            .ToListAsync(ct);
        var hit = others.FirstOrDefault(c => CompanyTaxIdVerification.IsVerified(c) && CompanyTaxIdVerification.SlotKey(c.TaxId, c.BranchCode) == slot);
        return hit == null ? null : (hit.Name, hit.TaxIdVerificationRequestId);
    }

    /// <summary>แจ้งบริษัทเมื่อผู้ดูแลตัดสิน (พร้อมเหตุ) — ผู้ยื่น + เจ้าของบริษัท · กระดิ่ง + อีเมลตามค่าตั้งของผู้รับ · ผลตัดสินบันทึกแล้ว การแจ้งล้มไม่ย้อน แต่ล้มดัง</summary>
    private async Task NotifyCompanyAsync(Guid requestId, CancellationToken ct)
    {
        if (_notifications == null) return;
        try
        {
            var r = await _db.CompanyTaxIdVerificationRequests.AsNoTracking().FirstAsync(x => x.Id == requestId, ct);
            var (title, message) = CompanyTaxIdVerification.CompanyReviewedNotice(r.Status, r.TaxId, r.ReviewNote);
            var owners = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == r.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
            foreach (var uid in owners.Append(r.SubmittedByUserId).Distinct())
                await _notifications.SendAsync(uid, r.CompanyId, NotificationType.System, title, message, CompanyTaxIdVerification.CompanyPageUrl,
                    nameof(CompanyTaxIdVerificationRequest), r.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "แจ้งบริษัทเรื่องผลยืนยันเลขภาษี (คำขอ {Request}) ไม่สำเร็จ — ผลบันทึกแล้ว บริษัทเห็นที่หน้าตั้งค่า", requestId);
        }
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
