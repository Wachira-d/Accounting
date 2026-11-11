namespace Accounting.Services.Interfaces;

/// <summary>แถวคิว LINE บนหน้า "ตารางส่ง" (เซิร์ฟเวอร์ประกอบป้าย · ไม่มี LINE id)</summary>
public sealed record LineQueueItemDto(Guid Id, Guid EntityId, string? DocumentNumber, string Trigger, string EndpointKind, string EndpointKindLabel,
    string Status, string StatusLabel, DateTime ScheduledFor, DateTime? SentAt, int RetryCount, string? ErrorMessage, string? RuleCode,
    bool CanRetry, bool CanCancel,
    // รอบ 259: สิ่งที่ส่ง (เซิร์ฟเวอร์ประกอบ — การ์ดเอกสาร = เลขที่ · ทวง 50 ทวิ = ชื่อผู้ติดต่อ · ส่งเองจากหน้าเอกสาร = ป้าย "ส่งเอง") · หน้าแสดงตรงตัว
    string? SubjectLabel = null,
    // รอบ 259 (A-L5): ข้อความยืนยันก่อนกด "ส่งอีกครั้ง" (บอกเมื่ออีเมลถอยไปแล้ว — ลูกค้าจะได้ทั้งสองทาง) · null = ไม่มีหมายเหตุ
    string? RetryNote = null);

/// <summary>รอบ 254 WP-11 L2 — ผู้ส่งคิว LINE ถึงผู้ติดต่อ (LINE_MULTICHANNEL_DESIGN §3.e) · ผู้เรียก: <c>EmailScheduleWorker</c> (ทุก 1 นาที) + หน้าตารางส่ง</summary>
public interface IContactLineQueueService
{
    /// <summary>จองแถวที่ถึงเวลา (SKIP LOCKED) แล้วส่ง — คืนจำนวนที่ LINE รับแล้ว</summary>
    Task<int> ProcessPendingAsync(CancellationToken ct = default);

    Task<List<LineQueueItemDto>> GetRecentAsync(Guid companyId, int take = 50, CancellationToken ct = default);

    /// <summary>ส่งอีกครั้ง (เฉพาะแถวที่ล้ม) — retry key ใหม่ (ผู้ใช้ตั้งใจส่งซ้ำ)</summary>
    Task RetryFailedAsync(Guid companyId, Guid id, CancellationToken ct = default);

    Task CancelPendingAsync(Guid companyId, Guid id, CancellationToken ct = default);
}
