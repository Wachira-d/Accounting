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

    /// <summary>ฝ่ายค้านรอบ 214 B2: ข้อความ "ต้องโอนความเป็นเจ้าของก่อน" (หน้าแอดมิน · หน้าของผู้ใช้ · ข้อผิดพลาด 409 ใช้ตัวเดียว)</summary>
    public static string OwnershipBlockMessage(IReadOnlyCollection<string> blockers)
        => "ต้องโอนความเป็นเจ้าของก่อนลบบัญชี: " + string.Join(" · ", blockers);

    /// <summary>ฝ่ายค้านรอบ 214 S5: อีเมลแจ้งผล — อีเมลปัจจุบันของบัญชี (ยังไม่เป็นอีเมลนิรนาม) ก่อน · ไม่งั้นอีเมล ณ ตอนยื่น · ไม่มีทั้งคู่ = null</summary>
    public static string? NotifyAddress(string? currentEmail, string? requestedEmail)
    {
        static bool Real(string? e) => !string.IsNullOrWhiteSpace(e) && e.Contains('@') && !e.EndsWith("@deleted.local", StringComparison.OrdinalIgnoreCase);
        return Real(currentEmail) ? currentEmail!.Trim() : Real(requestedEmail) ? requestedEmail!.Trim() : null;
    }
}
