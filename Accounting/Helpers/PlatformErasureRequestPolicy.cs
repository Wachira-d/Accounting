namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฐ) — กติกาคำขอลบข้อมูลระดับแพลตฟอร์มที่ผู้ใช้ยื่นเอง (pure · ตัวตัดสินเดียว):
/// ค้างอยู่แล้ว = คืนคำขอเดิม (idempotent — กดซ้ำไม่สร้างแถวใหม่) · ยื่นล่าสุดไม่ถึง <see cref="MinIntervalHours"/> ชม. = ปฏิเสธ (กันยิงรัว) ·
/// กำหนดเสร็จ = ยื่น + 30 วัน (PDPA ม.30 วรรคสาม / กฎ J) · ดำเนินการได้เฉพาะคำขอที่ค้าง
/// </summary>
public static class PlatformErasureRequestPolicy
{
    public const string StatusPending = "Pending";
    public const string StatusCompleted = "Completed";
    public const string StatusRejected = "Rejected";
    public const int SlaDays = 30;
    public const int MinIntervalHours = 24;
    public const string RuleCode = "PDPA-33-PLATFORM-ERASURE";

    public enum Outcome { Create, ReturnExisting, TooSoon }

    public static Outcome Decide(bool hasPending, DateTime? lastRequestedAt, DateTime nowUtc)
    {
        if (hasPending) return Outcome.ReturnExisting;
        if (lastRequestedAt is DateTime last && nowUtc - last < TimeSpan.FromHours(MinIntervalHours)) return Outcome.TooSoon;
        return Outcome.Create;
    }

    public static DateTime DueAt(DateTime requestedAtUtc) => requestedAtUtc.AddDays(SlaDays);

    public static bool CanResolve(string status) => status == StatusPending;
}
