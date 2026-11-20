using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 278 (ฝ่ายค้าน HIGH-1/2/3) — กุญแจผูกช่องลายเซ็น "ผู้จัดทำ ↔ ผู้มีอำนาจลงนาม" ฝั่งผู้ใช้/เจ้าของบริษัท:
/// ชื่อเล่นต่อบริษัท (ยื่น → <b>เจ้าของอนุมัติ</b> → มีผลกับเอกสารที่ออกหลังจากนั้น) · สิทธิ์ "ให้ระบบภายนอกระบุฉันเป็นผู้จัดทำ" ·
/// ปุ่ม "ปรับผู้ลงนามตามข้อมูลปัจจุบัน" (เจ้าของ · เหตุผล · audit · เฉพาะชนิดที่อนุญาตและลูกค้ายังไม่เซ็น) · ตัวตัดสินกติกาอยู่ที่ <see cref="SignatureSlotIdentity"/>
/// </summary>
public class SignerIdentityService
{
    private readonly AccountingDbContext _db;
    public SignerIdentityService(AccountingDbContext db) { _db = db; }

    public sealed record AliasItem(Guid Id, string Alias, string Status);
    public sealed record PendingAliasItem(Guid Id, Guid UserId, string UserName, string Alias, DateTime RequestedAt);
    public sealed record MySignerIdentity(List<AliasItem> Aliases, bool AllowExternalPreparerBinding, bool IsOwner, List<PendingAliasItem> Pending);
    public sealed record SaveMyRequest(string? Aliases, bool? AllowExternalPreparerBinding);
    public sealed record RefreshResult(Guid DocumentId, string Rule, int Candidates);

    public const string StatusApproved = "approved";
    public const string StatusPending = "pending";

    private async Task<CompanyUser> MemberAsync(Guid companyId, Guid userId, bool tracked = false)
    {
        var q = _db.CompanyUsers.Where(cu => cu.CompanyId == companyId && cu.UserId == userId);
        return await (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync()
            ?? throw new BusinessRuleException("คุณไม่ได้เป็นสมาชิกของบริษัทนี้", SignatureSlotIdentity.RuleCode, 403);
    }

    public async Task<MySignerIdentity> GetMineAsync(Guid companyId, Guid userId)
    {
        var me = await MemberAsync(companyId, userId);
        var mine = await _db.SignerAliases.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.UserId == userId && a.RevokedAt == null)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AliasItem(a.Id, a.Alias, a.ApprovedAt != null ? StatusApproved : StatusPending)).ToListAsync();
        var isOwner = me.Role == UserRole.Owner;
        var pending = isOwner ? await PendingAsync(companyId) : new List<PendingAliasItem>();
        return new MySignerIdentity(mine, me.AllowExternalPreparerBinding, isOwner, pending);
    }

    public async Task<List<PendingAliasItem>> PendingAsync(Guid companyId)
        => await (from a in _db.SignerAliases.AsNoTracking()
                  join u in _db.Users.AsNoTracking() on a.UserId equals u.Id
                  where a.CompanyId == companyId && a.ApprovedAt == null && a.RevokedAt == null
                  orderby a.CreatedAt
                  select new PendingAliasItem(a.Id, a.UserId, u.FullName, a.Alias, a.CreatedAt)).ToListAsync();

    /// <summary>ชื่อ/ชื่อเล่นของ<b>สมาชิกคนอื่นในทุกบริษัทที่ผู้ใช้เป็นสมาชิก</b> (ฝ่ายค้าน HIGH-2 — กันตั้งชื่อเล่นเป็นชื่อของคนอื่นแล้วยืมลายเซ็น)</summary>
    private async Task<List<string?>> OtherMembersNamesAsync(Guid userId)
    {
        var myCompanies = _db.CompanyUsers.AsNoTracking().Where(cu => cu.UserId == userId).Select(cu => cu.CompanyId);
        var names = await (from cu in _db.CompanyUsers.AsNoTracking()
                           join u in _db.Users.AsNoTracking() on cu.UserId equals u.Id
                           where myCompanies.Contains(cu.CompanyId) && cu.UserId != userId
                           select new { u.FullName, u.SignatureName }).ToListAsync();
        var aliases = await _db.SignerAliases.AsNoTracking()
            .Where(a => myCompanies.Contains(a.CompanyId) && a.UserId != userId && a.RevokedAt == null)
            .Select(a => a.Alias).ToListAsync();
        return names.SelectMany(n => new[] { n.FullName, n.SignatureName }).Concat(aliases).ToList();
    }

    /// <summary>บันทึกชื่อเล่น (ชุดใหม่ทั้งชุด — ที่หายไป = ยกเลิกทันที · ที่เพิ่ม = รออนุมัติ ยกเว้นเจ้าของบริษัทยื่นเอง) + สิทธิ์ให้ระบบภายนอกระบุ ·
    /// <paramref name="req"/>.Aliases null = ไม่แตะรายชื่อ</summary>
    public async Task<MySignerIdentity> SaveMineAsync(Guid companyId, Guid userId, SaveMyRequest req)
    {
        var me = await MemberAsync(companyId, userId, tracked: true);
        var now = DateTime.UtcNow;
        if (req.Aliases != null)
        {
            var wanted = SignatureSlotIdentity.ParseRequested(req.Aliases);
            var wantedNorm = wanted.Select(w => SignatureSlotIdentity.NormalizeName(w)!).ToHashSet();
            var active = await _db.SignerAliases.Where(a => a.CompanyId == companyId && a.UserId == userId && a.RevokedAt == null).ToListAsync();
            foreach (var a in active.Where(a => !wantedNorm.Contains(a.NormalizedAlias)))
            {
                a.RevokedAt = now; a.RevokedByUserId = userId;   // ลดสิทธิ์ ⇒ มีผลทันที (เอกสารที่ออกแล้วแช่แข็งไว้ ไม่เปลี่ยน)
            }
            var activeNorm = active.Select(a => a.NormalizedAlias).ToHashSet();
            var toAdd = wanted.Where(w => !activeNorm.Contains(SignatureSlotIdentity.NormalizeName(w)!)).ToList();
            if (toAdd.Count > 0)
            {
                var others = await OtherMembersNamesAsync(userId);
                foreach (var w in toAdd)
                    if (SignatureSlotIdentity.AliasProblem(w, others) is { } why)
                        throw new BusinessRuleException(why, SignatureSlotIdentity.RuleCode);
                var isOwner = me.Role == UserRole.Owner;
                foreach (var w in toAdd)
                    _db.SignerAliases.Add(new SignerAlias
                    {
                        CompanyId = companyId, UserId = userId, Alias = w, NormalizedAlias = SignatureSlotIdentity.NormalizeName(w)!,
                        CreatedBy = userId.ToString(),
                        // เจ้าของบริษัทยื่นของตัวเอง = ผู้อนุมัติคนเดียวกัน (บันทึกใน audit ของแถว)
                        ApprovedAt = isOwner ? now : null, ApprovedByUserId = isOwner ? userId : null,
                    });
            }
        }
        if (req.AllowExternalPreparerBinding is { } allow && allow != me.AllowExternalPreparerBinding)
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(CompanyUser), EntityId = userId.ToString(),
                OldValues = JsonSerializer.Serialize(new { me.AllowExternalPreparerBinding }),
                NewValues = JsonSerializer.Serialize(new { AllowExternalPreparerBinding = allow, ruleCode = SignatureSlotIdentity.RuleCode }),
                Timestamp = now,
            });
            me.AllowExternalPreparerBinding = allow;
        }
        await _db.SaveChangesAsync();
        return await GetMineAsync(companyId, userId);
    }

    /// <summary>เจ้าของอนุมัติ/ปฏิเสธชื่อเล่นที่รอ — ตรวจซ้ำตอนอนุมัติ (ระหว่างรอ คนอื่นอาจได้ชื่อนี้ไปแล้ว) · ปฏิเสธ = ประทับ RevokedAt</summary>
    public async Task<List<PendingAliasItem>> DecideAsync(Guid companyId, Guid ownerId, Guid aliasId, bool approve)
    {
        var owner = await MemberAsync(companyId, ownerId);
        if (owner.Role != UserRole.Owner)
            throw new BusinessRuleException("เฉพาะเจ้าของบริษัทอนุมัติชื่อเล่นได้", SignatureSlotIdentity.RuleCode, 403);
        var a = await _db.SignerAliases.FirstOrDefaultAsync(x => x.Id == aliasId && x.CompanyId == companyId && x.ApprovedAt == null && x.RevokedAt == null)
            ?? throw new BusinessRuleException("ไม่พบชื่อเล่นที่รออนุมัติ (อาจถูกยกเลิก/ตัดสินไปแล้ว)", SignatureSlotIdentity.RuleCode, 404);
        var now = DateTime.UtcNow;
        if (approve)
        {
            if (SignatureSlotIdentity.AliasProblem(a.Alias, await OtherMembersNamesAsync(a.UserId)) is { } why)
                throw new BusinessRuleException(why, SignatureSlotIdentity.RuleCode);
            a.ApprovedAt = now; a.ApprovedByUserId = ownerId;
        }
        else { a.RevokedAt = now; a.RevokedByUserId = ownerId; }
        await _db.SaveChangesAsync();
        return await PendingAsync(companyId);
    }

    /// <summary>ปุ่ม "ปรับผู้ลงนามตามข้อมูลปัจจุบัน" (ฝ่ายค้าน HIGH-3): เจ้าของเท่านั้น · ต้องมีเหตุผล · ชนิดที่ไม่ใช่หลักฐานภาษี/รับ-จ่ายเงิน · ลูกค้ายังไม่เซ็น ·
    /// แช่แข็งใหม่ด้วยกุญแจ ณ ตอนนี้ + audit (ก่อน/หลัง · เหตุผล · RuleCode)</summary>
    public async Task<RefreshResult> RefreshDocumentSignersAsync(Guid companyId, Guid ownerId, Guid documentId, string? reason)
    {
        var owner = await MemberAsync(companyId, ownerId);
        if (owner.Role != UserRole.Owner)
            throw new BusinessRuleException("เฉพาะเจ้าของบริษัทปรับผู้ลงนามได้", SignatureSlotIdentity.RuleCodeRefresh, 403);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new BusinessRuleException("กรุณาระบุเหตุผลที่ปรับผู้ลงนาม (อย่างน้อย 5 ตัวอักษร)", SignatureSlotIdentity.RuleCodeRefresh);
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new BusinessRuleException("ไม่พบเอกสาร", SignatureSlotIdentity.RuleCodeRefresh, 404);
        if (SignatureSlotIdentity.RefreshBlockedReason(doc) is { } blocked)
            throw new BusinessRuleException(blocked, SignatureSlotIdentity.RuleCodeRefresh);
        var customerSigned = await _db.DocumentApprovals.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.DocumentId == documentId
            && a.SignatureData != null && (a.ApprovalType == "Customer" || a.ApprovalType == "External"));
        if (customerSigned)
            throw new BusinessRuleException("ลูกค้าเซ็นใบนี้แล้ว — คงผู้ลงนามตามที่ลูกค้าเห็นตอนเซ็น", SignatureSlotIdentity.RuleCodeRefresh);
        var before = doc.SignerSnapshotJson;
        var snap = _db.ComputeSignerSnapshot(companyId, doc.PreparerUserId, doc.PreparerName, doc.PreparerSignatureBase64);
        doc.SignerSnapshotJson = SignatureSlotIdentity.Serialize(snap);
        doc.SignersFrozenAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = ownerId, Action = AuditAction.Update, EntityType = nameof(Document), EntityId = documentId.ToString(),
            OldValues = JsonSerializer.Serialize(new { SignerSnapshotJson = before }),
            NewValues = JsonSerializer.Serialize(new { SignerSnapshotJson = doc.SignerSnapshotJson, reason = reason.Trim(), ruleCode = SignatureSlotIdentity.RuleCodeRefresh }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return new RefreshResult(documentId, snap.Rule, snap.C.Count);
    }
}
