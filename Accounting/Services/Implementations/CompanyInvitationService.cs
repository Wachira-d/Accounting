using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>ข้อมูลหน้ารับคำเชิญ (ไม่ต้องล็อกอิน · ผู้ถือลิงก์เท่านั้นที่เห็น) — ป้าย/ข้อความจากเซิร์ฟเวอร์</summary>
public sealed record InvitationPreviewResponse(
    string CompanyName,
    string InviterName,
    string Email,
    UserRole Role,
    bool RoleChosen,
    string RoleLabel,
    InvitationStatus Status,
    string? StatusMessage,
    DateTime ExpiresAt);
// รอบ 277 ฝ่ายค้าน LOW: ไม่บอกว่าอีเมลนี้มีบัญชีอยู่แล้วหรือไม่ (ผู้ถือลิงก์ไล่ตรวจการมีบัญชีของอีเมลได้) — หน้ารับคำเชิญแสดงทั้งสองทางเสมอ

/// <summary>ผลตอบรับคำเชิญ</summary>
public sealed record AcceptInvitationResult(Guid CompanyId, string CompanyName, bool AccessPending, string Message);

public interface ICompanyInvitationService
{
    Task<InvitationPreviewResponse> PreviewAsync(string? rawToken);
    /// <summary>ผู้ที่ล็อกอินแล้วกดตอบรับ — ไม่ผ่านกติกา ⇒ <see cref="BusinessRuleException"/> พร้อมเหตุ/ทางไปต่อ</summary>
    Task<AcceptInvitationResult> AcceptAsync(Guid userId, string? rawToken);
    /// <summary>สมัคร/ล็อกอินครั้งแรกผ่านลิงก์คำเชิญ (ฟอร์ม · Google · LINE · Facebook) — ใช้ไม่ได้ ⇒ false (บัญชียังถูกสร้าง · ไม่สร้างบริษัทให้)</summary>
    Task<bool> TryAcceptOnSignupAsync(User user, string? rawToken);
    /// <summary>คำเชิญนี้ใช้ได้กับอีเมลนี้ไหม (ด่านเปิดรับสมัคร)</summary>
    Task<bool> IsUsableForEmailAsync(string? rawToken, string? email);
}

/// <summary>
/// รอบ 277 (งาน B) — ฝั่ง "ตอบรับคำเชิญ" ตัวเดียวของทุกทางเข้า: <c>InvitationController</c> (ลิงก์ในอีเมล · ล็อกอินแล้วกดรับ) +
/// <c>AuthService.RegisterAsync</c>/<c>SsoLoginAsync</c> (สมัครผ่านลิงก์ — ไม่สร้างบริษัท) · ฝั่งเจ้าของ (เชิญ/ส่งซ้ำ/ยกเลิก/รายการ) อยู่ที่
/// <c>CompanyService</c> · กติกาทั้งหมดใน <see cref="CompanyInvitationPolicy"/>
///
/// <para>ใช้ครั้งเดียว: ตรวจ + เขียนใต้ล็อกที่นั่งของบริษัท (<c>pg_advisory_xact_lock</c> เดียวกับการเชิญ) — กดรับสองแท็บพร้อมกันได้สมาชิกแถวเดียว ·
/// ที่นั่งตรวจซ้ำตอนรับ (แพ็กเกจอาจถูกลดหลังเชิญ) · ไม่เลือกบทบาท ⇒ <c>AccessPending</c> + แจ้งเจ้าของ · audit ผ่าน hash chain</para>
/// </summary>
public class CompanyInvitationService : ICompanyInvitationService
{
    private readonly AccountingDbContext _db;
    private readonly IQuotaGate? _quotaGate;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<CompanyInvitationService>? _logger;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _http;

    public CompanyInvitationService(AccountingDbContext db, IQuotaGate? quotaGate = null, INotificationEngine? notify = null,
        ILogger<CompanyInvitationService>? logger = null, Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null)
    {
        _db = db;
        _quotaGate = quotaGate;
        _notify = notify;
        _logger = logger;
        _http = http;
    }

    private Task<CompanyInvitation?> FindAsync(string? rawToken, bool track)
    {
        var hash = CompanyInvitationPolicy.HashToken(rawToken);
        if (hash == null) return Task.FromResult<CompanyInvitation?>(null);
        var q = _db.CompanyInvitations.Where(i => i.TokenHash == hash && !i.IsDeleted);
        return (track ? q : q.AsNoTracking()).FirstOrDefaultAsync();
    }

    public async Task<InvitationPreviewResponse> PreviewAsync(string? rawToken)
    {
        var inv = await FindAsync(rawToken, track: false);
        if (inv == null)
            throw new BusinessRuleException(CompanyInvitationPolicy.Message(CompanyInvitationPolicy.Verdict.NotFound)!, CompanyInvitationPolicy.RuleCode, 404);
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == inv.CompanyId).Select(c => c.Name).FirstOrDefaultAsync() ?? "บริษัท";
        var inviterName = await _db.Users.AsNoTracking().Where(u => u.Id == inv.InvitedByUserId).Select(u => u.FullName).FirstOrDefaultAsync() ?? "เจ้าของบริษัท";
        var customRole = inv.CompanyRoleId is Guid crid
            ? await _db.CompanyRoles.AsNoTracking().Where(r => r.Id == crid && r.CompanyId == inv.CompanyId).Select(r => r.Name).FirstOrDefaultAsync() : null;
        var now = DateTime.UtcNow;
        var status = CompanyInvitationPolicy.DisplayStatus(inv.Status, inv.ExpiresAt, now);
        var verdict = CompanyInvitationPolicy.Check(inv.Status, inv.ExpiresAt, inv.Email, inv.Email, now);
        return new InvitationPreviewResponse(companyName, inviterName, inv.Email, inv.Role, inv.RoleChosen,
            CompanyInvitationPolicy.RoleLabel(inv.RoleChosen, inv.Role, customRole), status,
            CompanyInvitationPolicy.Message(verdict), inv.ExpiresAt);
    }

    public async Task<bool> IsUsableForEmailAsync(string? rawToken, string? email)
    {
        var inv = await FindAsync(rawToken, track: false);
        return inv != null && CompanyInvitationPolicy.Check(inv.Status, inv.ExpiresAt, inv.Email, email, DateTime.UtcNow) == CompanyInvitationPolicy.Verdict.Ok;
    }

    public async Task<bool> TryAcceptOnSignupAsync(User user, string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return false;
        try
        {
            await AcceptAsync(user.Id, rawToken);
            return true;
        }
        catch (BusinessRuleException ex)
        {
            // บัญชียังถูกสร้าง (ไม่สร้างบริษัทให้) — ผู้ใช้เปิดลิงก์คำเชิญอีกครั้งแล้วเห็นเหตุผลจาก AcceptAsync · log ไม่ใช่การเงียบ (หน้าแรกมีทางไปต่อ)
            _logger?.LogWarning("Invitation not consumed at signup for user {UserId}: {Reason}", user.Id, ex.Message);
            return false;
        }
    }

    public async Task<AcceptInvitationResult> AcceptAsync(Guid userId, string? rawToken)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new BusinessRuleException("ไม่พบผู้ใช้ — เข้าสู่ระบบใหม่แล้วเปิดลิงก์คำเชิญอีกครั้ง", CompanyInvitationPolicy.RuleCode, 401);
        var probe = await FindAsync(rawToken, track: false);
        if (probe == null)
            throw new BusinessRuleException(CompanyInvitationPolicy.Message(CompanyInvitationPolicy.Verdict.NotFound)!, CompanyInvitationPolicy.RuleCode, 404);

        var companyId = probe.CompanyId;
        var now = DateTime.UtcNow;
        bool accessPending;
        await using (var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null)
        {
            // ล็อกที่นั่งของบริษัท (ตัวเดียวกับตอนเชิญ) แล้วอ่านแถวใหม่ใต้ล็อก ⇒ ใช้ครั้งเดียวแม้กดพร้อมกัน
            if (_db.Database.CurrentTransaction != null)
                await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaCapacity, QuotaResources.Users));
            var inv = await FindAsync(rawToken, track: true)
                ?? throw new BusinessRuleException(CompanyInvitationPolicy.Message(CompanyInvitationPolicy.Verdict.NotFound)!, CompanyInvitationPolicy.RuleCode, 404);
            await _db.Entry(inv).ReloadAsync();

            var verdict = CompanyInvitationPolicy.Check(inv.Status, inv.ExpiresAt, inv.Email, user.Email, now);
            if (verdict == CompanyInvitationPolicy.Verdict.Expired && inv.Status == InvitationStatus.Pending)
            {
                inv.Status = InvitationStatus.Expired;
                inv.UpdatedAt = now;
                await _db.SaveChangesAsync();
                if (tx != null) await tx.CommitAsync();
            }
            if (verdict != CompanyInvitationPolicy.Verdict.Ok)
                throw new BusinessRuleException(CompanyInvitationPolicy.Message(verdict, inv.Email, user.Email)!, CompanyInvitationPolicy.RuleCode,
                    CompanyInvitationPolicy.StatusCode(verdict));

            // ฝ่ายค้านรอบสาม P2-2 (รอบ 201 ทีม PL): ตรวจบทบาทซ้ำตอนรับ — คำเชิญ SystemAdmin/PlatformSupport ที่ออกก่อนแก้ยังค้างได้
            if (inv.RoleChosen)
            {
                var inviterIsPlatformAdmin = await _db.Users.AsNoTracking()
                    .Where(u => u.Id == inv.InvitedByUserId).Select(u => u.IsSystemAdmin).FirstOrDefaultAsync();
                var roleBlocked = OwnershipTransferPolicy.InvitationRoleBlock(inv.Role, inviterIsPlatformAdmin);
                if (roleBlocked != null)
                    throw new BusinessRuleException(roleBlocked, OwnershipTransferPolicy.RuleCode, 403);
            }

            var alreadyMember = await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
            if (!alreadyMember)
            {
                // ที่นั่ง: คำเชิญใบนี้ถือที่นั่งอยู่แล้ว (adding 0) — ตรวจซ้ำเผื่อแพ็กเกจถูกลดหลังเชิญ
                if (_quotaGate != null)
                {
                    var seat = await _quotaGate.CheckAsync(companyId, QuotaResources.Users, 0);
                    if (!seat.Allowed)
                        throw new BusinessRuleException("ตอบรับไม่ได้ตอนนี้ — ที่นั่งผู้ใช้ของบริษัทเต็ม (เจ้าของลดแพ็กเกจหลังส่งคำเชิญ) · แจ้งเจ้าของให้เพิ่มที่นั่งหรือนำผู้ใช้ออกก่อน",
                            seat.RuleCode, 409);
                }
                // บทบาทกำหนดเองต้องยังอยู่ในบริษัทนี้ — ถูกลบไปแล้ว ⇒ เข้าแบบ "ยังไม่มีสิทธิ์" (ปลอดภัยกว่าให้บทบาทมาตรฐานกว้างกว่าที่ตั้งใจ)
                var customRoleOk = inv.CompanyRoleId is not Guid crid
                    || await _db.CompanyRoles.AnyAsync(r => r.Id == crid && r.CompanyId == companyId);
                var (role, pending) = CompanyInvitationPolicy.MembershipFor(inv.RoleChosen && customRoleOk, inv.Role);
                _db.CompanyUsers.Add(new CompanyUser
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Role = role,
                    CompanyRoleId = pending ? null : inv.CompanyRoleId,
                    OwnDocumentsOnly = inv.OwnDocumentsOnly,
                    DraftDocumentsOnly = inv.DraftDocumentsOnly || inv.OwnDocumentsOnly,
                    AccessPending = pending,
                    JoinedAt = now,
                });
                accessPending = pending;
            }
            else
            {
                accessPending = await _db.CompanyUsers.Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
                    .Select(cu => cu.AccessPending).FirstAsync();
            }

            // รอบ 277 ฝ่ายค้าน MED-3 (คำตัดสิน 277-8): token ส่งถึงกล่องจดหมายของอีเมลที่เชิญเท่านั้น + อีเมลของบัญชีต้องตรงอีเมลที่เชิญ (ตรวจแล้วข้างบน)
            // ⇒ การตอบรับด้วย token = หลักฐานถือกล่องจดหมาย ⇒ ประทับ "ยืนยันอีเมลแล้ว" (CompanyInvitationPolicy.ConfirmsEmail) · ระบบไม่มีทางเปลี่ยนอีเมลบัญชีภายหลัง
            var emailNewlyVerified = false;
            if (CompanyInvitationPolicy.ConfirmsEmail(verdict) && !user.EmailVerified)
            {
                var trackedUser = await _db.Users.FirstAsync(u => u.Id == userId);
                trackedUser.EmailVerified = true;
                emailNewlyVerified = true;
            }

            inv.Status = InvitationStatus.Accepted;
            inv.AcceptedAt = now;
            inv.AcceptedByUserId = userId;
            inv.UpdatedAt = now;
            inv.UpdatedBy = userId.ToString();
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                Action = AuditAction.Approve,
                EntityType = "CompanyInvitation",
                EntityId = inv.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { status = "Pending" }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "accept", status = "Accepted", alreadyMember, accessPending, role = inv.Role.ToString(), inv.RoleChosen, inv.CompanyRoleId,
                    emailVerifiedByInvitation = emailNewlyVerified,
                    ruleCode = CompanyInvitationPolicy.RuleCode,
                }),
                IpAddress = _http?.HttpContext?.Connection.RemoteIpAddress?.ToString(),
                Timestamp = now,
            });
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
        }

        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync() ?? "บริษัท";
        if (_notify != null)
            await _notify.DispatchAsync(companyId, NotificationEvents.TeamInvitationAccepted, new NotificationContext
            {
                Title = accessPending ? "มีผู้ตอบรับคำเชิญ — รอคุณตั้งสิทธิ์" : "มีผู้ตอบรับคำเชิญเข้าทีมแล้ว",
                Message = accessPending
                    ? $"{user.FullName} ({user.Email}) เข้าร่วม {companyName} แล้ว แต่ยังใช้งานอะไรไม่ได้จนกว่าคุณจะตั้งบทบาท/สิทธิ์ที่หน้าจัดการทีม"
                    : $"{user.FullName} ({user.Email}) เข้าร่วม {companyName} แล้วตามบทบาทที่ตั้งไว้ในคำเชิญ",
                ActionUrl = "/pages/team.html",
                EntityType = "CompanyInvitation",
                BellType = accessPending ? NotificationType.ApprovalRequired : NotificationType.System,
                ActorUserId = userId,
            });

        var msg = accessPending
            ? $"เข้าร่วม {companyName} แล้ว — รอเจ้าของกำหนดบทบาท/สิทธิ์ (แจ้งเจ้าของแล้ว)"
            : $"เข้าร่วม {companyName} แล้ว";
        return new AcceptInvitationResult(companyId, companyName, accessPending, msg);
    }
}
