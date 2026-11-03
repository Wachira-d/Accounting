using Accounting.Models.Enums;
using Accounting.Services.Payments;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 230 ฝ่ายค้าน (B1 · B2) — <b>ตัวตัดสินเดียว (pure)</b> ของการตัดบัตรที่บันทึกไว้ใน <c>PaymentIntentService</c>:
/// <list type="bullet">
/// <item><b>ผลไม่แน่ชัด</b> (<see cref="IsUnknown"/>): ผู้ให้บริการตอบ 5xx/408 · เครือข่ายขาด · หมดเวลาที่ไม่ใช่ผู้เรียกยกเลิก ⇒ อาจตัดเงินแล้ว ⇒
///   รายการคง "รอผล" (<see cref="UnknownCode"/>) แล้วหา charge จาก metadata ทีหลัง — <b>ห้ามตีเป็นล้มเหลว</b> (ลองใหม่ = ตัดซ้ำ) ·
///   หาไม่พบเกิน <see cref="GiveUpAfter"/> ⇒ ปิดเป็นล้มเหลว "ไม่พบรายการที่ผู้ให้บริการ" (ยังเปลี่ยนเป็นสำเร็จได้ถ้าเงินเข้าทีหลัง — นโยบายสถานะเดิม)</item>
/// <item><b>ใช้รายการเปิดซ้ำ</b> (<see cref="ReuseOpenIntent"/>): บัตร — คำขอที่มีโทเคนใหม่ (ใช้ครั้งเดียว) ห้ามได้รายการเดิม · บัตรที่บันทึกต้องเป็นใบเดียวกัน</item>
/// </list>
/// </summary>
public static class SavedCardChargeOutcome
{
    public const string UnknownCode = "outcome_unknown", ActionRequiredCode = "action_required", NotFoundCode = "charge_not_found";
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(2);

    public static bool IsUnknown(Exception ex, bool callerCancelled) => ex switch
    {
        ProviderOutcomeUnknownException => true,
        HttpRequestException => true,
        OperationCanceledException => !callerCancelled,
        _ => false,
    };

    public static bool GiveUp(DateTime createdAtUtc, DateTime nowUtc) => nowUtc - createdAtUtc > GiveUpAfter;

    public static bool ReuseOpenIntent(PaymentMethodKind method, bool requestHasToken, string? requestSavedCardRef, string? existingSavedCardRef)
        => method != PaymentMethodKind.Card
           || (!requestHasToken && !string.IsNullOrEmpty(requestSavedCardRef) && requestSavedCardRef == existingSavedCardRef);
}
