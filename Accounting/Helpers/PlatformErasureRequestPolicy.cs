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

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B2-c: แพ็กเกจบัญชี "โอน" ไม่ได้ (ผู้ใช้ไม่มีปุ่มเอง) — บอกทางไปต่อที่ทำได้จริง: แอดมินยกเลิกให้</summary>
    public static string AccountPlanBlockerLabel(string? planName)
        => $"{AccountPlanBlockerPrefix}\u201C{planName}\u201D (เจ้าของแพ็กเกจ — ติดต่อแอดมินให้ยกเลิกแพ็กเกจ)";

    private const string AccountPlanBlockerPrefix = "แพ็กเกจบัญชี ";

    /// <summary>รายการติดค้างนี้คือ "เจ้าของแพ็กเกจบัญชี" (ป้ายที่ <see cref="AccountPlanBlockerLabel"/> สร้าง) — หน้าแอดมินใช้แสดงปุ่มยกเลิกแพ็กเกจ</summary>
    public static bool IsAccountPlanBlocker(string? blocker) => blocker != null && blocker.StartsWith(AccountPlanBlockerPrefix, StringComparison.Ordinal);

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B2-c: แอดมินยกเลิกแพ็กเกจของผู้ขอลบได้เฉพาะแพ็กเกจที่<b>ไม่มีบริษัทผูกอยู่</b> — ยกเลิกแพ็กเกจที่ยังมีบริษัท
    /// ใช้โควตา = ตัดบริการของบริษัทนั้นทั้งบริษัท (ต้องย้ายบริษัทออกจากแพ็กเกจก่อน) · null = ยกเลิกได้ทั้งหมด · ไม่มีแพ็กเกจ = ข้อความบอกตรง ๆ</summary>
    public static string? AccountPlanCancelBlock(IReadOnlyCollection<(string? PlanName, int AttachedCompanies)> plans)
    {
        if (plans.Count == 0) return "ผู้ใช้นี้ไม่มีแพ็กเกจบัญชีที่ยังใช้งานอยู่";
        var busy = plans.Where(p => p.AttachedCompanies > 0).ToList();
        if (busy.Count == 0) return null;
        return "แพ็กเกจยังมีบริษัทผูกอยู่ — ย้ายบริษัทออกจากแพ็กเกจก่อน (หน้าแอดมิน › แพ็กเกจบัญชี): "
            + string.Join(" · ", busy.Select(p => $"\u201C{p.PlanName}\u201D {p.AttachedCompanies} บริษัท"));
    }

    /// <summary>ฝ่ายค้านรอบ 214 S5: อีเมลแจ้งผล — อีเมลปัจจุบันของบัญชี (ยังไม่เป็นอีเมลนิรนาม) ก่อน · ไม่งั้นอีเมล ณ ตอนยื่น · ไม่มีทั้งคู่ = null</summary>
    public static string? NotifyAddress(string? currentEmail, string? requestedEmail)
    {
        static bool Real(string? e) => !string.IsNullOrWhiteSpace(e) && e.Contains('@') && !e.EndsWith("@deleted.local", StringComparison.OrdinalIgnoreCase);
        return Real(currentEmail) ? currentEmail!.Trim() : Real(requestedEmail) ? requestedEmail!.Trim() : null;
    }
}
