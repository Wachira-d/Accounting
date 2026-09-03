namespace Accounting.Services.Interfaces;

/// <summary>สถานะโควตาเอกสารของบริษัท — ตัวเลขชุดเดียวที่ทุกหน้าจอต้องใช้
///
/// **ทำไมต้องมี DTO นี้**: ก่อนหน้านี้แต่ละหน้าคำนวณ "ใกล้เต็มหรือยัง" เอง
/// (บางหน้าเทียบ 80% บางหน้าเทียบ 90% บางหน้าไม่เทียบเลย) — defect class
/// "สำเนามือฝั่ง JS" ที่ไฟล์ CLAUDE.md ห้ามไว้ ⇒ เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดง</summary>
/// <param name="WarnLevel">0 ปกติ · 1 ใกล้เต็ม (≥80%) · 2 เต็มแล้ว</param>
/// <param name="ForecastExhaustionDay">คาดว่าจะเต็มวันที่เท่าไรของเดือน (null = ไม่เต็ม)</param>
/// <param name="OverageEnabled">เกินแล้วยังออกเอกสารต่อได้โดยคิดค่าส่วนเกินไหม</param>
public record DocumentQuotaStatus(
    int Used,
    int PlanLimit,
    int BonusQuota,
    DateTime? BonusExpiresAt,
    int EffectiveLimit,
    int Remaining,
    int WarnLevel,
    int? ForecastExhaustionDay,
    bool OverageEnabled,
    decimal OverageUnitPrice,
    int OverageThisMonth,
    decimal OverageChargedThisMonth,
    string? Message);

/// <summary>ภารกิจแลกโควตา 1 รายการที่ผู้ใช้เห็น</summary>
public record QuotaRewardOptionDto(
    Guid Id, string Kind, string Title, string? Description,
    string? ImageUrl, string? MediaUrl, string? PartnerUrl,
    int DurationSeconds, int RewardDocuments,
    int UsedToday, int MaxPerDay, int UsedThisMonth, int MaxPerMonth,
    bool CanClaim, string? BlockedReason);

public record QuotaRewardClaimResult(
    bool Granted, int GrantedDocuments, DateTime? ExpiresAt,
    int BonusQuotaAfter, string Message);

/// <summary>
/// โควตาเอกสาร: สถานะ · ซื้อเพิ่ม · แลกจากภารกิจ (LODGING_LICENSING_PLAN §11-§12)
///
/// **หลักที่ห้ามละเมิด**: ทุกเส้นทางเพิ่มโควตาต้องผ่านที่นี่ที่เดียว เพื่อให้
/// `Subscription.DocumentBonusQuota` มีที่มาที่ตรวจสอบได้เสมอ (UsageEvent สำหรับ
/// ที่ซื้อ · QuotaRewardGrant สำหรับที่แลก) — ห้ามมีโค้ดที่ `+=` ตรง ๆ
/// </summary>
public interface IQuotaService
{
    Task<DocumentQuotaStatus> GetDocumentQuotaAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>ซื้อโควตาเพิ่ม (top-up) — 1 แพ็ก = `documents.topup` 1 หน่วย
    /// คิดเงินผ่าน UsageEvent ปกติ (เข้าบิลรอบเดือน/ตัดเครดิต Prepaid)</summary>
    Task<DocumentQuotaStatus> PurchaseTopUpAsync(Guid companyId, int packs, string actor, CancellationToken ct = default);

    Task<List<QuotaRewardOptionDto>> ListRewardOptionsAsync(Guid companyId, CancellationToken ct = default);

    Task<QuotaRewardClaimResult> ClaimRewardAsync(Guid companyId, Guid optionId, int watchedSeconds,
        bool clickedThrough, Guid? userId, CancellationToken ct = default);
}
