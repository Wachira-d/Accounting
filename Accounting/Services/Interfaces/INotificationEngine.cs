namespace Accounting.Services.Interfaces;

/// <summary>
/// Centralised omnichannel notification dispatcher. Services raise an
/// event by calling <see cref="DispatchAsync"/> with an event key from
/// <see cref="Models.Constants.NotificationEvents"/>; the engine looks
/// up the per-company NotificationSettings matrix, resolves the
/// logical recipient roles (Requester / DirectManager / HrAdmin / etc.)
/// to concrete users, applies each user's NotificationPreference
/// suppressions, and dispatches to System (bell), Email (SMTP) and
/// LINE Messaging API channels. Each channel degrades gracefully:
/// missing LINE binding falls back silently, unconfigured SMTP logs a
/// warning and skips. Fire-and-forget — never throws back to the
/// caller so a notification failure can't roll back a business
/// transaction.
/// </summary>
public interface INotificationEngine
{
    Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context);
}

/// <summary>
/// Carries everything the engine needs to assemble a notification:
/// title / body for the user, action URL for the bell, recipient
/// resolution hints (the requester's EmployeeId and / or UserId for
/// Requester / DirectManager resolution). All fields optional except
/// Title — the engine renders sensible defaults.
/// </summary>
public class NotificationContext
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ActionUrl { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    /// <summary>Employee at the centre of the event (the leave / advance /
    /// claim requester). Used to resolve Requester / DirectManager /
    /// DepartmentHead recipients.</summary>
    public Guid? RequesterEmployeeId { get; set; }

    /// <summary>The user who triggered the event (e.g. the approver
    /// approving a leave). Used so the engine can avoid notifying the
    /// actor about their own action.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>NotificationType used for the in-app bell row (legacy
    /// enum). Defaults to System when omitted.</summary>
    public Models.Enums.NotificationType BellType { get; set; } = Models.Enums.NotificationType.System;

    /// <summary>Override recipient resolver — เมื่อระบุ จะส่งหา user คนนี้
    /// คนเดียว (skip role resolution). ใช้กับ approval / subscription event
    /// ที่ผู้รับเป็น user ตัวเฉพาะที่ระบบรู้ตัวอยู่แล้ว (ApprovalAction.ApproverUserId).
    /// Engine ยัง apply per-user channel preferences ตามปกติ.</summary>
    public Guid? RecipientUserId { get; set; }
}
