using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>ตัวเลือกการออก/ใช้ลิงก์ให้ลูกค้าเซ็น — <see cref="Regenerate"/> = ออก token ใหม่ (ลิงก์เดิมใช้ไม่ได้) ·
/// <see cref="CallbackUrl"/> ไม่ null = ตั้ง/เปลี่ยน webhook ต่อเอกสาร · <see cref="ClearCallback"/> = ลบ webhook ต่อเอกสาร</summary>
/// <param name="Regenerate">รอบ 267 ฝ่ายค้าน H2 (267-1-24): ออก token ใหม่<b>จริง</b> — ลิงก์หน้าเอกสารเดิมของใบใช้ไม่ได้ทันที (ส่งผิดคน = กดออกใหม่)</param>
/// <param name="StaffIntent">ผู้เรียกเป็นคนกด (หน้าเอกสารของพนักงาน) — false (คีย์ API) ⇒ ไม่ออกลิงก์ใหม่แทนลิงก์ที่พนักงานเพิกถอนไว้ (259-2-8 · ฝ่ายค้าน 267 M8)</param>
public sealed record IssueSignLinkOptions(bool Regenerate, string? CallbackUrl = null, string? CallbackRef = null, bool ClearCallback = false,
    bool StaffIntent = true);

public sealed record SignLinkResult(
    Guid DocumentId, string Kind, string EventType, string Url, string Token, DateTime? ExpiresAt,
    DateTime? SignedAt, string? SignedBy, string? CallbackUrl, string? CallbackRef, SignRequestKinds.Chip? Status);

/// <summary>ส่งอีเมลขอเซ็น — ผู้รับว่าง = ใช้ผู้รับล่าสุดของผู้ติดต่อ แล้วอีเมลของผู้ติดต่อ · <see cref="Purpose"/>: sign-request · sign-request-auto · sign-reminder</summary>
public sealed record SendSignEmailOptions(IReadOnlyList<string>? To, string? Message, string Purpose = SignRequestPurposes.Manual);

public static class SignRequestPurposes
{
    public const string Manual = "sign-request";
    public const string Auto = "sign-request-auto";
    public const string Reminder = "sign-reminder";
    public static bool IsKnown(string? p) => p is Manual or Auto or Reminder;
}

public sealed record SignEmailRecipientResult(string To, bool Sent, string? Error);

public sealed record SignEmailResult(
    Guid DocumentId, string Url, string SenderMode, string FromAddress, string FromName, string? ReplyTo,
    IReadOnlyList<SignEmailRecipientResult> Recipients)
{
    public bool AllSent => Recipients.Count > 0 && Recipients.All(r => r.Sent);
    public bool AnySent => Recipients.Any(r => r.Sent);
    /// <summary>ส่งไม่ได้เพราะเพดาน/ตัวกันส่งถี่ทุกฉบับ (ตอบ 429)</summary>
    public bool Throttled { get; init; }
}

public sealed record SignEmailPreview(
    Guid DocumentId, string Url, string Subject, string Html, string SenderMode, string FromAddress, string FromName,
    string? ReplyTo, IReadOnlyList<string> SuggestedRecipients, int PlatformSentToday, int PlatformDailyCap, SignRequestKinds.Chip? Status);

public sealed record SignRequestSettingsDto(
    bool AutoEmailOnApprove, DateTime? AutoEmailSince, int ReminderDays, string? CallbackAllowedHosts,
    bool HasCallbackSecret, string? CallbackSecretHint, bool CompanySenderVerified, string SenderModeNow, int PlatformDailyCap,
    IReadOnlyList<string> EventTypes);

public sealed record UpdateSignRequestSettings(bool? AutoEmailOnApprove = null, int? ReminderDays = null, string? CallbackAllowedHosts = null);

public sealed record SignEventDto(
    Guid Id, Guid EventId, string EventType, Guid DocumentId, string Status, int Attempts, DateTime CreatedAt, DateTime? LastAttemptAt,
    DateTime? NextAttemptAt, int? LastHttpStatus, string? LastError, DateTime? DeliveredAt, string? CallbackHost, string? CallbackRef,
    bool RegistrationsDispatched, bool SellerNotified);

/// <summary><c>Error</c> ไม่ว่าง = รอบนี้ทำงานไม่ได้ทั้งรอบ (เช่นยังไม่ตั้ง URL ของระบบ) — งานบันทึก JobRun เป็นล้มพร้อมเหตุ (ฝ่ายค้าน S4)</summary>
public sealed record SignAutomationRunResult(int AutoSent, int AutoFailed, int Reminded, int RemindFailed, string? Error = null);

public interface ISignRequestService
{
    Task<SignLinkResult> IssueLinkAsync(Guid companyId, Guid documentId, IssueSignLinkOptions options, string origin, CancellationToken ct = default);
    Task<SignEmailPreview> PreviewEmailAsync(Guid companyId, Guid documentId, string origin, string? message, CancellationToken ct = default);
    Task<SignEmailResult> SendEmailAsync(Guid companyId, Guid documentId, SendSignEmailOptions options, string origin, string actor, CancellationToken ct = default);
    Task<Dictionary<Guid, SignRequestKinds.Chip>> GetStatusesAsync(Guid companyId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default);
    Task<SignRequestSettingsDto> GetSettingsAsync(Guid companyId, CancellationToken ct = default);
    Task<SignRequestSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateSignRequestSettings request, CancellationToken ct = default);
    /// <summary>ออกรหัสลับใหม่ — คืนค่าเต็ม<b>ครั้งเดียว</b> (เก็บแบบเข้ารหัส · หน้าจอแสดงแค่ 4 ตัวท้ายหลังจากนี้)</summary>
    Task<string> RotateCallbackSecretAsync(Guid companyId, CancellationToken ct = default);
    Task<List<SignEventDto>> GetEventsAsync(Guid companyId, Guid? documentId, int take = 50, CancellationToken ct = default);
    Task RetryEventAsync(Guid companyId, Guid outboxId, CancellationToken ct = default);
    Task<SignAutomationRunResult> RunAutomationAsync(DateTime nowUtc, CancellationToken ct = default);
}
