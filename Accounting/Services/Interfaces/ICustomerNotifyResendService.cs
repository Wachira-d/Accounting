namespace Accounting.Services.Interfaces;

/// <summary>ข้อความค้างส่ง 1 รายการ (เซิร์ฟเวอร์ประกอบป้ายทั้งหมด · หน้าเว็บแสดงอย่างเดียว) — ไม่มีเนื้อข้อความ/ที่อยู่ติดต่อ (PDPA)</summary>
public sealed record CustomerNotifyPendingItem(
    Guid LogId,
    string EventKey,
    string EventLabel,
    string TargetLabel,
    DateTime CreatedAt,
    string ReasonLabel,
    bool Resendable,
    string VerdictCode,
    string VerdictLabel,
    int ResendCount,
    DateTime? ResendRequestedAt);

/// <summary>รายการ + ตัวนับ + สถานะโควตาตอนนี้ (บอกผู้ใช้ว่าส่งได้อีกกี่ข้อความก่อนกด)</summary>
public sealed record CustomerNotifyPendingList(
    IReadOnlyList<CustomerNotifyPendingItem> Items,
    int Total,
    int ResendableCount,
    NotifyQuotaStatus? Quota,
    string? Hint);

/// <summary>ผลส่งซ้ำ 1 รายการ</summary>
public sealed record CustomerNotifyResendItemResult(Guid LogId, string Outcome, string? Detail);

/// <summary>ผลส่งซ้ำทั้งชุด — <c>StoppedForQuota</c> = หยุดเพราะโควตายังเต็ม (ทางไปต่อ: <c>TopUpUrl</c>)</summary>
public sealed record CustomerNotifyResendResult(
    int Requested,
    int Sent,
    int Failed,
    int Skipped,
    int StillPending,
    bool StoppedForQuota,
    string Message,
    string? TopUpUrl,
    IReadOnlyList<CustomerNotifyResendItemResult> Items);

/// <summary>รอบ 213 (คำตัดสินข้อ 172) — หน้า "ข้อความที่ยังไม่ได้ส่ง": รายการที่ค้างเพราะโควตาเต็ม + ส่งซ้ำหลังเติมโควตา ·
/// ตัวตัดสินเดียว <c>Helpers/CustomerNotifyResend.Evaluate</c> · ส่งผ่านเส้นเดิม (<c>ICmsBookingService/ICmsCommerceService.ResendCustomerNotifyAsync</c> → <c>BookingNotifier</c>)</summary>
public interface ICustomerNotifyResendService
{
    Task<CustomerNotifyPendingList> ListAsync(Guid companyId, CancellationToken ct = default);

    /// <summary><paramref name="logIds"/> null/ว่าง = ทุกรายการที่ส่งซ้ำได้ (สูงสุด <c>CustomerNotifyResend.MaxBatch</c>) · เรียงเก่าไปใหม่ · โควตายังเต็ม ⇒ หยุด</summary>
    Task<CustomerNotifyResendResult> ResendAsync(Guid companyId, IReadOnlyCollection<Guid>? logIds, string actor, CancellationToken ct = default);
}
