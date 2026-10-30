namespace Accounting.Services.Interfaces;

/// <summary>ผล claim คีย์ข้อความ (กันส่งซ้ำ) — <c>Claimed</c> = ได้สิทธิ์ส่ง · ไม่ได้ + <c>AlreadySent</c> = ส่งถึงแล้วก่อนหน้า · ไม่ได้ + ไม่ใช่ส่งแล้ว = คำขออื่นกำลังส่ง</summary>
public sealed record NotifyClaim(bool Claimed, bool AlreadySent, Guid? LogId, string? PreviousChannel)
{
    /// <summary>ไม่กันซ้ำ (ไม่มีคีย์ / ไม่มีฐาน) — ส่งได้เสมอ</summary>
    public static readonly NotifyClaim Untracked = new(true, false, null, null);
}

/// <summary>ผลจองหน่วยโควตา 1 หน่วยก่อนส่งทางช่องกลาง — <c>Ok</c> = ส่งได้ · <c>Kind</c> = plan/bonus/essential (ใช้คืนหน่วยเมื่อส่งล้ม) ·
/// <c>CrossedLevel</c> = ระดับเตือนที่เพิ่งข้าม (0/1/2) · <c>Limit</c> = เพดานรวมที่ใช้ตัดสิน (ข้อความถึงผู้ใช้)</summary>
public sealed record NotifyReservation(bool Ok, string Kind, string Period, int Limit, int CrossedLevel, int UsedAfter);

/// <summary>สถานะโควตาข้อความช่องกลางของบริษัท (เซิร์ฟเวอร์คำนวณ · หน้าเว็บแสดงอย่างเดียว)</summary>
public sealed record NotifyQuotaStatus(
    string Period,
    bool Unlimited,
    int Used,
    int PlanLimit,
    int BonusUsed,
    int BonusRemaining,
    DateTime? BonusExpiresAt,
    int EffectiveLimit,
    int Remaining,
    int WarnLevel,
    string? Message,
    string MeteredNote,
    int PackSize,
    decimal? PackPrice,
    int PackValidDays)
{
    /// <summary>ฝ่ายค้านรอบ 213 S4: จำนวนข้อความที่การซื้อครั้งนี้ให้จริง (ขนาดแพ็ก ณ ตอนซื้อ × จำนวนแพ็ก) · null = ไม่ใช่ผลของการซื้อ/คำขอซ้ำ</summary>
    public int? Granted { get; init; }
}

/// <summary>
/// โควตาข้อความผ่านช่องกลาง (LINE OA/อีเมลของ NextAcc) — รอบ 212 แพ็ก A · คำตัดสินข้อ 171 ·
/// ทุกคำสั่งนับ/claim ใช้ DbContext ของ scope แยกจากธุรกรรมของผู้เรียก (rollback ของผู้เรียกไม่ถอยการนับข้อความที่ส่งไปแล้ว) ·
/// <b>ไม่เรียก IUsageMeteringService ต่อข้อความ</b> (UsageEvent ไม่ใช่ตัวนับรายข้อความ) — ใช้เฉพาะตอนซื้อแพ็ก
/// </summary>
public interface INotifyQuotaService
{
    /// <summary>claim คีย์ข้อความ (INSERT … ON CONFLICT DO NOTHING แล้ว UPDATE … WHERE ยังไม่ส่ง และ lease ว่าง) · <paramref name="messageKey"/> null = ไม่กันซ้ำ</summary>
    Task<NotifyClaim> ClaimAsync(Guid companyId, string? messageKey, string eventKey, string? targetKind, Guid? targetId, CancellationToken ct = default);

    /// <summary>จอง 1 หน่วยก่อนส่งทางช่องกลาง (UPDATE … RETURNING แบบ R-05) · เต็ม ⇒ <c>Ok=false</c> (ข้อความจำเป็นนับเกินได้)</summary>
    Task<NotifyReservation> TryReserveAsync(Guid companyId, bool essential, CancellationToken ct = default);

    /// <summary>คืนหน่วยเมื่อส่งล้ม (ตามชนิดที่จองไว้)</summary>
    Task ReleaseAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default);

    /// <summary>ข้ามระดับ 80%/100% ⇒ ประทับครั้งเดียวต่องวด แล้วแจ้งเจ้าของ (ไม่โยน)</summary>
    Task AlertIfCrossedAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default);

    /// <summary>ปิดแถว claim (Sent/Failed/Suppressed) + ปล่อย lease · <paramref name="logId"/> null = ไม่มีแถว ·
    /// <c>suppressReason</c> (รอบ 213) = รหัสเหตุของ <c>Helpers/NotifySuppressReason</c> เมื่อ <c>status</c> = Suppressed (อื่น ๆ ล้างเป็น null)</summary>
    Task CompleteAsync(Guid companyId, Guid? logId, string status, string? channel, string? source, bool metered, string? note,
        string? suppressReason = null, CancellationToken ct = default);

    Task<NotifyQuotaStatus> GetStatusAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>ซื้อแพ็กข้อความเพิ่ม (<paramref name="idempotencyKey"/> = header Idempotency-Key — ส่งซ้ำได้ผลเดิม) — ลอก <c>QuotaService.PurchaseTopUpAsync</c>: ล็อกต่อบริษัท → UsageEvent <c>notify.topup</c> ก่อนให้ของ → โบนัส + อายุ → audit chain</summary>
    /// ฝ่ายค้านรอบ 213 S4: <paramref name="expectedPackSize"/>/<paramref name="expectedPackPrice"/> = ข้อเสนอที่ผู้ซื้อเห็น — ไม่ตรงของจริงใต้ล็อก ⇒ <see cref="NotifyTopUpOfferChangedException"/> (ไม่คิดเงิน)
    Task<NotifyQuotaStatus> PurchaseTopUpAsync(Guid companyId, int packs, string actor, CancellationToken ct = default, string? idempotencyKey = null,
        int? expectedPackSize = null, decimal? expectedPackPrice = null);
}

/// <summary>ฝ่ายค้านรอบ 213 S4: ขนาด/ราคาแพ็กข้อความเปลี่ยนระหว่างที่ผู้ซื้อดูหน้าจอ — ยังไม่คิดเงิน · คอนโทรลเลอร์ตอบ 409 พร้อมสถานะใหม่</summary>
public sealed class NotifyTopUpOfferChangedException : Accounting.Helpers.BusinessRuleException
{
    public NotifyTopUpOfferChangedException(string message) : base(message, "NOTIFY-TOPUP-CHANGED", 409) { }
}
