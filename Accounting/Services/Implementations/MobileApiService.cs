using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Mobile;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class MobileApiService : IMobileApiService
{
    private readonly AccountingDbContext _db;

    private readonly Accounting.Services.Implementations.Ocr.VendorIntelligenceService _vendorIntel;
    // IDocumentService resolve ตอนใช้ (ไม่ inject ตรง) — กัน DI cycle แบบเดียวกับ
    // ApprovalService: DocumentService ↔ approval workflow อ้างถึงกันได้
    private readonly IServiceProvider _services;
    public MobileApiService(AccountingDbContext db, Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        IServiceProvider services)
    {
        _db = db;
        _vendorIntel = vendorIntel;
        _services = services;
    }

    // ==================== Device Management ====================

    public async Task<DeviceRegistrationResponse> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request)
    {
        // Check if this device token is already registered for this user
        var existing = await _db.Set<UserDevice>()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceToken == request.DeviceToken && !d.IsDeleted);

        if (existing is not null)
        {
            // Update the existing registration
            existing.Platform = request.Platform;
            existing.DeviceName = request.DeviceName;
            existing.AppVersion = request.AppVersion;
            existing.IsActive = true;
            existing.LastActiveAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return new DeviceRegistrationResponse(existing.Id, existing.DeviceToken, existing.Platform, existing.IsActive);
        }

        var device = new UserDevice
        {
            UserId = userId,
            DeviceToken = request.DeviceToken,
            Platform = request.Platform,
            DeviceName = request.DeviceName,
            AppVersion = request.AppVersion,
            IsActive = true,
            LastActiveAt = DateTime.UtcNow
        };

        _db.Set<UserDevice>().Add(device);
        await _db.SaveChangesAsync();

        return new DeviceRegistrationResponse(device.Id, device.DeviceToken, device.Platform, device.IsActive);
    }

    public async Task UnregisterDeviceAsync(Guid userId, string deviceToken)
    {
        var device = await _db.Set<UserDevice>()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceToken == deviceToken && !d.IsDeleted)
            ?? throw new InvalidOperationException($"Device with token '{deviceToken}' not found for user {userId}.");

        device.IsActive = false;
        device.IsDeleted = true;
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task SendPushNotificationAsync(Guid userId, string title, string body, string? actionUrl = null)
    {
        // Persist as an in-app notification
        var notification = new Notification
        {
            UserId = userId,
            Type = NotificationType.System,
            Channel = NotificationChannel.InApp,
            Title = title,
            Message = body,
            ActionUrl = actionUrl,
            IsRead = false
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();

        // In a production system this would also fan out to push notification providers
        // (APNs for iOS, FCM for Android) via the active device registrations.
        // Retrieve active devices for reference:
        var activeDevices = await _db.Set<UserDevice>()
            .Where(d => d.UserId == userId && d.IsActive && !d.IsDeleted)
            .ToListAsync();

        // Each device would receive a platform-specific push payload.
        // This is left as a hook for the actual push provider integration.
        foreach (var device in activeDevices)
        {
            device.LastActiveAt = DateTime.UtcNow;
        }

        if (activeDevices.Count > 0)
        {
            await _db.SaveChangesAsync();
        }
    }

    // ==================== Offline Sync ====================

    public async Task<SyncResponse> SyncAsync(Guid companyId, Guid userId, SyncRequest request)
    {
        var syncTimestamp = DateTime.UtcNow;
        var conflicts = new List<SyncConflict>();

        // 1. Process local changes sent from the mobile client
        if (request.LocalChanges is not null && request.LocalChanges.Count > 0)
        {
            foreach (var change in request.LocalChanges)
            {
                // Check if a server-side change already exists for this entity since last sync
                var serverChange = await _db.Set<SyncQueue>()
                    .Where(sq => sq.CompanyId == companyId
                        && sq.EntityType == change.EntityType
                        && sq.EntityId == change.EntityId
                        && !sq.IsProcessed
                        && !sq.IsDeleted)
                    .OrderByDescending(sq => sq.QueuedAt)
                    .FirstOrDefaultAsync();

                if (serverChange is not null && request.LastSyncAt.HasValue && serverChange.QueuedAt > request.LastSyncAt.Value)
                {
                    // Conflict detected: server has a newer change for the same entity
                    conflicts.Add(new SyncConflict(
                        change.EntityType,
                        change.EntityId,
                        serverChange.PayloadJson,
                        change.PayloadJson,
                        "ServerWins"
                    ));

                    // Mark server change as processed via conflict resolution
                    serverChange.ConflictResolution = "ServerWins";
                    serverChange.ProcessedAt = syncTimestamp;
                    serverChange.IsProcessed = true;
                }
                else
                {
                    // No conflict: enqueue the client change for server-side processing
                    var queueEntry = new SyncQueue
                    {
                        CompanyId = companyId,
                        UserId = userId,
                        EntityType = change.EntityType,
                        EntityId = change.EntityId,
                        OperationType = change.OperationType,
                        PayloadJson = change.PayloadJson,
                        QueuedAt = syncTimestamp,
                        IsProcessed = true,
                        ProcessedAt = syncTimestamp
                    };

                    _db.Set<SyncQueue>().Add(queueEntry);
                }
            }

            await _db.SaveChangesAsync();
        }

        // 2. Retrieve server changes since the client's last sync
        var serverChanges = await GetPendingChangesAsync(companyId, userId, request.LastSyncAt);

        return new SyncResponse(syncTimestamp, serverChanges, conflicts);
    }

    public async Task<List<SyncQueueResponse>> GetPendingChangesAsync(Guid companyId, Guid userId, DateTime? since = null)
    {
        var query = _db.Set<SyncQueue>()
            .Where(sq => sq.CompanyId == companyId && !sq.IsDeleted);

        if (since.HasValue)
        {
            query = query.Where(sq => sq.QueuedAt > since.Value);
        }

        // Exclude changes that were made by the requesting user (they already have those locally)
        query = query.Where(sq => sq.UserId != userId || sq.UserId == null);

        var changes = await query
            .OrderBy(sq => sq.QueuedAt)
            .Take(500) // Limit for mobile bandwidth
            .ToListAsync();

        return changes.Select(sq => new SyncQueueResponse(
            sq.EntityType,
            sq.EntityId,
            sq.OperationType,
            sq.PayloadJson,
            sq.QueuedAt
        )).ToList();
    }

    // ==================== Mobile-Optimized Endpoints ====================

    public async Task<MobileDashboardResponse> GetMobileDashboardAsync(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Cash balance: sum of all active bank accounts
        var cashBalance = await _db.BankAccounts
            .Where(ba => ba.CompanyId == companyId && ba.IsActive && !ba.IsDeleted)
            .SumAsync(ba => ba.CurrentBalance);

        // Total receivables: unpaid or partially paid invoices
        var receivableStatuses = new[]
        {
            DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue
        };

        var totalReceivables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && receivableStatuses.Contains(d.Status)
                && !d.IsDeleted)
            .SumAsync(d => d.BalanceDue);

        // Total payables: unpaid purchase invoices
        var totalPayables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.PurchaseInvoice
                && receivableStatuses.Contains(d.Status)
                && !d.IsDeleted)
            .SumAsync(d => d.BalanceDue);

        // Pending approvals
        var pendingApprovals = await _db.ApprovalRequests
            .CountAsync(ar => ar.CompanyId == companyId
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted);

        // Overdue invoices
        var overdueInvoices = await _db.Documents
            .CountAsync(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status == DocumentStatus.Overdue
                && !d.IsDeleted);

        // Today's revenue: payments received today
        var todayRevenue = await _db.Payments
            .Where(p => p.CompanyId == companyId
                && p.PaymentDate >= todayStart
                && !p.IsDeleted)
            .SumAsync(p => p.Amount);

        // Month revenue: payments received this month
        var monthRevenue = await _db.Payments
            .Where(p => p.CompanyId == companyId
                && p.PaymentDate >= monthStart
                && !p.IsDeleted)
            .SumAsync(p => p.Amount);

        // Alerts: recent important notifications for the company
        var alerts = await _db.Notifications
            .Where(n => n.CompanyId == companyId && !n.IsRead && !n.IsDeleted)
            .OrderByDescending(n => n.CreatedAt)
            .Take(10)
            .Select(n => new MobileAlertResponse(
                n.Type.ToString(),
                n.Title,
                n.Message,
                n.ActionUrl,
                n.CreatedAt
            ))
            .ToListAsync();

        return new MobileDashboardResponse(
            cashBalance,
            totalReceivables,
            totalPayables,
            pendingApprovals,
            overdueInvoices,
            todayRevenue,
            monthRevenue,
            alerts
        );
    }

    public async Task<MobileQuickActionsResponse> GetQuickActionsAsync(Guid companyId, Guid userId)
    {
        // ฝ่ายค้าน F4 (ID#9): นับเฉพาะคำขอที่ "รอคุณ" (ผู้อนุมัติของขั้นปัจจุบัน) — เกณฑ์เดียวกับ ApprovalService.GetPendingApprovalsAsync
        // และด่านใน SubmitActionAsync (เดิมนับทั้งบริษัท ⇒ แอปโชว์ว่ามีให้อนุมัติ แล้วกดได้ 403)
        var pendingDocuments = await _db.ApprovalRequests
            .CountAsync(ar => ar.CompanyId == companyId
                && ar.EntityType == "Document"
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted
                && ar.Actions.Any(a => a.ApproverUserId == userId && a.Status == ApprovalStatus.Pending && a.StepOrder == ar.CurrentStep));

        // Pending expense claims awaiting approval
        var pendingExpenses = await _db.ExpenseClaims
            .CountAsync(ec => ec.CompanyId == companyId
                && ec.Status == ExpenseClaimStatus.Submitted
                && !ec.IsDeleted);

        // Pending payroll approvals
        var pendingPayroll = await _db.ApprovalRequests
            .CountAsync(ar => ar.CompanyId == companyId
                && ar.EntityType == "Payroll"
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted
                && ar.Actions.Any(a => a.ApproverUserId == userId && a.Status == ApprovalStatus.Pending && a.StepOrder == ar.CurrentStep));

        return new MobileQuickActionsResponse(
            CanApproveDocuments: pendingDocuments > 0,
            CanApproveExpenses: pendingExpenses > 0,
            CanApprovePayroll: pendingPayroll > 0,
            PendingDocuments: pendingDocuments,
            PendingExpenses: pendingExpenses,
            PendingPayroll: pendingPayroll
        );
    }

    public async Task<MobileApprovalResponse> QuickApproveAsync(Guid companyId, Guid entityId, string entityType, string action, Guid userId,
        bool acknowledgeWarnings = false)
    {
        // รอบ 2026-10-14 ทีม F4 (ID#9 · ID#18): ข้อผิดพลาดเป็นสถานะ HTTP + ข้อความไทยพร้อมทางไปต่อ (เดิมตอบ 200 + อังกฤษ + GUID)
        var newStatus = Accounting.Helpers.ApprovalApproverCheck.ParseMobileAction(action)
            ?? throw new BusinessRuleException("คำสั่งต้องเป็น approve (อนุมัติ) หรือ reject (ปฏิเสธ) — อัปเดตแอปแล้วลองใหม่", "MOBILE-APPROVE-ACTION", 400);
        var isApprove = newStatus == ApprovalStatus.Approved;

        // Handle expense claims directly (they have their own status field)
        if (entityType.Equals("ExpenseClaim", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleExpenseClaimApprovalAsync(companyId, entityId, isApprove, userId);
        }

        // Handle document and other entity types via the approval workflow
        var approvalRequest = await _db.ApprovalRequests.AsNoTracking()
            .Where(ar => ar.CompanyId == companyId
                && ar.EntityId == entityId
                && ar.EntityType == entityType
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted)
            .Select(ar => new { ar.Id })
            .FirstOrDefaultAsync();
        if (approvalRequest is null)
            throw await NoPendingRequestErrorAsync(companyId, entityType, entityId);

        // ── ID#9: ช่องมือถือเดินกติกาเดียวกับเว็บ — ApprovalService.SubmitActionAsync เป็นเจ้าของตัวเดียว ──
        // เดิมเส้นนี้เขียนกติกาเองชุดที่สอง: ไม่ตรวจว่าผู้กดเป็นผู้อนุมัติของขั้นนี้ (ใครก็เลื่อนขั้น/ตีกลับได้) · ตรวจสิทธิ์เฉพาะขั้นสุดท้าย ·
        // เพิ่มแถว ApprovalAction ใหม่แทนการแก้แถวที่รออยู่ (ขั้นเดียวมีสองแถว แถวของผู้อนุมัติตัวจริงค้าง Pending) ·
        // ตอนนี้ทุกด่าน (ผู้อนุมัติของขั้น · ผู้ขอห้ามอนุมัติเอง · สิทธิ์อนุมัติเอกสารขั้นสุดท้าย · คำเตือนต้องกดรับทราบ ·
        // ตีกลับเป็นร่าง · อนุมัติเอกสารผ่าน ApproveDocumentAsync เท่านั้น) อยู่ที่เดียว
        var approvals = _services.GetService(typeof(IApprovalService)) as IApprovalService
            ?? throw new InvalidOperationException("ไม่พบบริการอนุมัติ (IApprovalService) — อนุมัติผ่านมือถือไม่ได้");
        Accounting.Models.DTOs.Approval.ApprovalRequestResponse result;
        try
        {
            result = await approvals.SubmitActionAsync(companyId, approvalRequest.Id, userId,
                new Accounting.Models.DTOs.Approval.SubmitApprovalActionRequest(
                    newStatus, isApprove ? "อนุมัติผ่านมือถือ" : "ปฏิเสธผ่านมือถือ", acknowledgeWarnings));
        }
        catch (DocumentApprovalWarningsException ex)
        {
            // รอบ 193 (คำตัดสินเจ้าของข้อ 12 · ฝ่ายค้าน C5): ขั้นสุดท้ายมีคำเตือน ⇒ ยังไม่บันทึกอะไร — แอปแสดงรายการแล้วเรียกซ้ำด้วย acknowledgeWarnings=true
            return new MobileApprovalResponse(false,
                "มีคำเตือนที่ต้องกด \"รับทราบ\" ก่อนอนุมัติ: " + string.Join(" · ", ex.Warnings),
                entityType, entityId, Warnings: ex.Warnings, RequiresAcknowledgement: true);
        }

        // ลำดับอนุมัติครบแต่อนุมัติเอกสารจริงไม่สำเร็จ (ด่านบัญชี/ภาษี) ⇒ ไม่ใช่ "สำเร็จ" — ข้อความไทยพร้อมทางไปต่อตัวเดียวกับหมายเหตุบนเอกสาร
        if (result.FinalizeError != null)
            return new MobileApprovalResponse(false, result.FinalizeError, entityType, entityId);

        // Train vendor intelligence if a document was approved this round
        if (result.OverallStatus == ApprovalStatus.Approved && entityType.Equals("Document", StringComparison.OrdinalIgnoreCase))
            await _vendorIntel.TryTrainAsync(companyId, entityId);

        // Enqueue the change to the sync queue so other mobile users receive it
        var syncEntry = new SyncQueue
        {
            CompanyId = companyId,
            UserId = userId,
            EntityType = entityType,
            EntityId = entityId,
            OperationType = "Update",
            PayloadJson = JsonSerializer.Serialize(new { status = result.OverallStatus.ToString(), approvedBy = userId }),
            QueuedAt = DateTime.UtcNow,
            IsProcessed = true,
            ProcessedAt = DateTime.UtcNow
        };

        _db.Set<SyncQueue>().Add(syncEntry);
        await _db.SaveChangesAsync();

        var resultMessage = !isApprove
            ? "ปฏิเสธแล้ว — รายการกลับไปให้ผู้ขอแก้ไขแล้วส่งใหม่"
            : result.OverallStatus == ApprovalStatus.Approved
                ? "อนุมัติแล้ว (ครบทุกขั้น)"
                : $"อนุมัติขั้นของคุณแล้ว — ส่งต่อผู้อนุมัติขั้นที่ {result.CurrentStep}";

        return new MobileApprovalResponse(true, resultMessage, entityType, entityId);
    }

    /// <summary>ไม่มีคำขอที่รออยู่ — ฝ่ายค้าน F4: ถ้าลำดับอนุมัติครบแล้วแต่ระบบอนุมัติเอกสารไม่สำเร็จ (FinalizeError ของรอบก่อน) บอกทางไปต่อ
    /// (อนุมัติเอกสารตรงที่หน้าเอกสาร — gate ปล่อยเพราะคำขออนุมัติครบแล้ว) แทน "ไม่พบ" ที่ทำให้ผู้ใช้กดซ้ำไม่จบ</summary>
    private async Task<Exception> NoPendingRequestErrorAsync(Guid companyId, string entityType, Guid entityId)
    {
        var latest = await _db.ApprovalRequests.AsNoTracking()
            .Where(ar => ar.CompanyId == companyId && ar.EntityId == entityId && ar.EntityType == entityType && !ar.IsDeleted)
            .OrderByDescending(ar => ar.RequestedAt)
            .Select(ar => (ApprovalStatus?)ar.OverallStatus)
            .FirstOrDefaultAsync();
        if (latest == ApprovalStatus.Approved && entityType.Equals("Document", StringComparison.OrdinalIgnoreCase))
        {
            var docStatus = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == entityId && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => (DocumentStatus?)d.Status)
                .FirstOrDefaultAsync();
            if (docStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                return new BusinessRuleException(
                    "ลำดับอนุมัติครบทุกขั้นแล้ว แต่ระบบอนุมัติเอกสารยังไม่สำเร็จ — เปิดเอกสารบนเว็บดูเหตุผลในหมายเหตุภายใน แก้ตามนั้นแล้วกด “อนุมัติ” ที่หน้าเอกสาร",
                    "MOBILE-APPROVE-FINALIZE-PENDING", 409);
        }
        return new KeyNotFoundException(
            "ไม่พบคำขออนุมัติที่รออยู่ของรายการนี้ (อาจถูกอนุมัติ/ปฏิเสธไปแล้ว) — ดึงรายการรออนุมัติใหม่ในแอป");
    }

    // ==================== Private Helpers ====================

    private async Task<MobileApprovalResponse> HandleExpenseClaimApprovalAsync(Guid companyId, Guid entityId, bool isApprove, Guid userId)
    {
        var claim = await _db.ExpenseClaims.AsNoTracking()
            .FirstOrDefaultAsync(ec => ec.Id == entityId
                && ec.CompanyId == companyId
                && ec.Status == ExpenseClaimStatus.Submitted
                && !ec.IsDeleted);

        if (claim is null)
            // ID#18: เดิมตอบ 200 + อังกฤษ + GUID ⇒ 404 + ไทยพร้อมทางไปต่อ
            throw new KeyNotFoundException(
                "ไม่พบใบเบิกที่รออนุมัติรายการนี้ (อาจถูกอนุมัติ/ปฏิเสธไปแล้ว) — ดึงรายการรออนุมัติใหม่ในแอป");

        // ฝ่ายค้านรอบสอง 193 (R2-C2 · Q7): เดิมตั้งสถานะเองตรง ๆ — ไม่ตรวจสิทธิ์ (ผู้ยื่นอนุมัติใบตัวเองทางมือถือได้) และไม่สร้าง
        // ใบรับรองแทนใบเสร็จ (§65 ทวิ) ของใบ no-receipt · ตอนนี้เดินเมธอดเดียวกับเว็บ (ExpenseClaimService.ApproveAsync/RejectAsync)
        // ⇒ ด่านสิทธิ์ + SoD (Helpers/ExpenseClaimActionPolicy) · ตรวจหลักฐาน §65 ทวิ ซ้ำ · CertificateInLieu · แจ้งเตือน ครบเท่าเว็บ
        // ข้อผิดพลาดทางธุรกิจ (ไม่มีสิทธิ์ · SoD · หลักฐานไม่ครบ) โยนขึ้นไปดัง ๆ เหมือนเส้นอนุมัติเอกสารของมือถือ
        var claimSvc = _services.GetService(typeof(IExpenseClaimService)) as IExpenseClaimService
            ?? throw new InvalidOperationException("ไม่พบบริการใบเบิก (IExpenseClaimService) — อนุมัติใบเบิกผ่านมือถือไม่ได้");
        if (isApprove)
            await claimSvc.ApproveAsync(companyId, claim.Id, userId, new Accounting.Models.DTOs.Expense.ApproveExpenseClaimRequest("อนุมัติผ่านมือถือ"));
        else
            await claimSvc.RejectAsync(companyId, claim.Id, userId, new Accounting.Models.DTOs.Expense.RejectExpenseClaimRequest("ปฏิเสธผ่านมือถือ"));
        var newStatus = await _db.ExpenseClaims.AsNoTracking()
            .Where(ec => ec.Id == claim.Id && ec.CompanyId == companyId)
            .Select(ec => ec.Status)
            .FirstAsync();

        // Enqueue sync entry
        var syncEntry = new SyncQueue
        {
            CompanyId = companyId,
            UserId = userId,
            EntityType = "ExpenseClaim",
            EntityId = entityId,
            OperationType = "Update",
            PayloadJson = JsonSerializer.Serialize(new { status = newStatus.ToString(), approvedBy = userId }),
            QueuedAt = DateTime.UtcNow,
            IsProcessed = true,
            ProcessedAt = DateTime.UtcNow
        };

        _db.Set<SyncQueue>().Add(syncEntry);
        await _db.SaveChangesAsync();

        var message = isApprove
            ? $"อนุมัติใบเบิก {claim.ClaimNumber} แล้ว"
            : $"ปฏิเสธใบเบิก {claim.ClaimNumber} แล้ว";

        return new MobileApprovalResponse(true, message, "ExpenseClaim", entityId);
    }
}
