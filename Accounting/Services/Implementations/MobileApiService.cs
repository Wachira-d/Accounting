using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class MobileApiService : IMobileApiService
{
    private readonly AccountingDbContext _db;

    public MobileApiService(AccountingDbContext db)
    {
        _db = db;
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

    public async Task<MobileQuickActionsResponse> GetQuickActionsAsync(Guid companyId)
    {
        // Pending documents awaiting approval
        var pendingDocuments = await _db.ApprovalRequests
            .CountAsync(ar => ar.CompanyId == companyId
                && ar.EntityType == "Document"
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted);

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
                && !ar.IsDeleted);

        return new MobileQuickActionsResponse(
            CanApproveDocuments: pendingDocuments > 0,
            CanApproveExpenses: pendingExpenses > 0,
            CanApprovePayroll: pendingPayroll > 0,
            PendingDocuments: pendingDocuments,
            PendingExpenses: pendingExpenses,
            PendingPayroll: pendingPayroll
        );
    }

    public async Task<MobileApprovalResponse> QuickApproveAsync(Guid companyId, Guid entityId, string entityType, string action, Guid userId)
    {
        var isApprove = action.Equals("approve", StringComparison.OrdinalIgnoreCase);
        var isReject = action.Equals("reject", StringComparison.OrdinalIgnoreCase);

        if (!isApprove && !isReject)
        {
            return new MobileApprovalResponse(false, $"Unknown action '{action}'. Use 'approve' or 'reject'.", entityType, entityId);
        }

        // Handle expense claims directly (they have their own status field)
        if (entityType.Equals("ExpenseClaim", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleExpenseClaimApprovalAsync(companyId, entityId, isApprove, userId);
        }

        // Handle document and other entity types via the approval workflow
        var approvalRequest = await _db.ApprovalRequests
            .Include(ar => ar.Actions)
            .FirstOrDefaultAsync(ar => ar.CompanyId == companyId
                && ar.EntityId == entityId
                && ar.EntityType == entityType
                && ar.OverallStatus == ApprovalStatus.Pending
                && !ar.IsDeleted);

        if (approvalRequest is null)
        {
            return new MobileApprovalResponse(false, $"No pending approval request found for {entityType} {entityId}.", entityType, entityId);
        }

        var newStatus = isApprove ? ApprovalStatus.Approved : ApprovalStatus.Rejected;

        // Record the approval action
        var approvalAction = new ApprovalAction
        {
            ApprovalRequestId = approvalRequest.Id,
            StepOrder = approvalRequest.CurrentStep,
            ApproverUserId = userId,
            Status = newStatus,
            ActionAt = DateTime.UtcNow,
            Comments = isApprove ? "Approved via mobile" : "Rejected via mobile"
        };

        _db.ApprovalActions.Add(approvalAction);

        if (isApprove)
        {
            // Check if there are more steps
            var totalSteps = await _db.Set<ApprovalStep>()
                .CountAsync(s => s.ApprovalRuleId == approvalRequest.ApprovalRuleId);

            if (approvalRequest.CurrentStep >= totalSteps)
            {
                // Final step: mark as fully approved
                approvalRequest.OverallStatus = ApprovalStatus.Approved;

                // Update the underlying entity status if it is a Document
                if (entityType.Equals("Document", StringComparison.OrdinalIgnoreCase))
                {
                    var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == entityId && !d.IsDeleted);
                    if (document is not null)
                    {
                        document.Status = DocumentStatus.Approved;
                        document.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }
            else
            {
                // Advance to next step
                approvalRequest.CurrentStep++;
            }
        }
        else
        {
            approvalRequest.OverallStatus = ApprovalStatus.Rejected;

            // Update the underlying entity status if it is a Document
            if (entityType.Equals("Document", StringComparison.OrdinalIgnoreCase))
            {
                var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == entityId && !d.IsDeleted);
                if (document is not null)
                {
                    document.Status = DocumentStatus.Rejected;
                    document.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        approvalRequest.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Enqueue the change to the sync queue so other mobile users receive it
        var syncEntry = new SyncQueue
        {
            CompanyId = companyId,
            UserId = userId,
            EntityType = entityType,
            EntityId = entityId,
            OperationType = "Update",
            PayloadJson = JsonSerializer.Serialize(new { status = newStatus.ToString(), approvedBy = userId }),
            QueuedAt = DateTime.UtcNow,
            IsProcessed = true,
            ProcessedAt = DateTime.UtcNow
        };

        _db.Set<SyncQueue>().Add(syncEntry);
        await _db.SaveChangesAsync();

        var resultMessage = isApprove
            ? $"{entityType} {entityId} has been approved."
            : $"{entityType} {entityId} has been rejected.";

        return new MobileApprovalResponse(true, resultMessage, entityType, entityId);
    }

    // ==================== Private Helpers ====================

    private async Task<MobileApprovalResponse> HandleExpenseClaimApprovalAsync(Guid companyId, Guid entityId, bool isApprove, Guid userId)
    {
        var claim = await _db.ExpenseClaims
            .FirstOrDefaultAsync(ec => ec.Id == entityId
                && ec.CompanyId == companyId
                && ec.Status == ExpenseClaimStatus.Submitted
                && !ec.IsDeleted);

        if (claim is null)
        {
            return new MobileApprovalResponse(false, $"No pending expense claim found with ID {entityId}.", "ExpenseClaim", entityId);
        }

        if (isApprove)
        {
            claim.Status = ExpenseClaimStatus.Approved;
            claim.ApprovedByUserId = userId;
            claim.ApprovedAt = DateTime.UtcNow;
            claim.ApprovalNotes = "Approved via mobile";
        }
        else
        {
            claim.Status = ExpenseClaimStatus.Rejected;
            claim.ApprovedByUserId = userId;
            claim.ApprovedAt = DateTime.UtcNow;
            claim.RejectionReason = "Rejected via mobile";
        }

        claim.UpdatedAt = DateTime.UtcNow;

        // Enqueue sync entry
        var syncEntry = new SyncQueue
        {
            CompanyId = companyId,
            UserId = userId,
            EntityType = "ExpenseClaim",
            EntityId = entityId,
            OperationType = "Update",
            PayloadJson = JsonSerializer.Serialize(new { status = claim.Status.ToString(), approvedBy = userId }),
            QueuedAt = DateTime.UtcNow,
            IsProcessed = true,
            ProcessedAt = DateTime.UtcNow
        };

        _db.Set<SyncQueue>().Add(syncEntry);
        await _db.SaveChangesAsync();

        var message = isApprove
            ? $"Expense claim {claim.ClaimNumber} has been approved."
            : $"Expense claim {claim.ClaimNumber} has been rejected.";

        return new MobileApprovalResponse(true, message, "ExpenseClaim", entityId);
    }
}
