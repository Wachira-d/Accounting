using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// **ทางเลือก "ทำภารกิจสั้น ๆ แลกโควตาเอกสาร"** (LODGING_LICENSING_PLAN.md §12)
///
/// ที่มาของโจทย์: เจ้าของระบบอยากให้ผู้ใช้ที่เกินโควตา "ดูโฆษณา 1 นาทีแล้วทำงานต่อได้"
/// โดยเราได้เงิน. ผลวิเคราะห์: โฆษณาเครือข่ายบนเว็บได้ ~฿0.03–0.15 ต่อครั้ง ซึ่ง
/// **กินรายได้ overage (฿5/ฉบับ) ของตัวเอง** และคุมเนื้อหาไม่ได้ (อาจขึ้นโฆษณา
/// โปรแกรมบัญชีคู่แข่งกลางหน้าเช็คเอาต์) ⇒ ออกแบบเป็น **ช่องเสียบ (provider)**
/// แทนการผูกกับเครือข่ายโฆษณาเจ้าใดเจ้าหนึ่ง:
///   • `PartnerOffer` — ข้อเสนอ B2B ที่เกี่ยวกับที่พัก (สินเชื่อ SME/ประกัน/
///     payment gateway) กด "สนใจ" = lead มูลค่า ฿100–1,000+ ต่อครั้ง
///   • `HouseVideo` — วิดีโอสอนฟีเจอร์ของเราเอง (ไม่ได้เงินสด แต่เพิ่ม activation)
///   • `Referral` / `Survey` — แนะนำเพื่อน / ให้ข้อมูลธุรกิจ
///   • `AdNetwork` — โฆษณาเครือข่ายจริง (ปิดไว้เป็นค่าเริ่มต้น)
///
/// **เปิด/ปิดได้ 3 ชั้น**: ปิดทุก option (แพลตฟอร์ม) · `PlanTemplate.AllowQuotaReward`
/// (ต่อแพ็กเกจ) · `Subscription.QuotaRewardBlocked` (ต่อบริษัทที่ใช้ในทางที่ผิด)
/// </summary>
public class QuotaRewardOption : BaseEntity
{
    public QuotaRewardKind Kind { get; set; } = QuotaRewardKind.PartnerOffer;

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>โลโก้/ภาพประกอบของข้อเสนอ (URL ในระบบเรา — ไม่ดึงจากภายนอกเพื่อไม่ชน CSP)</summary>
    public string? ImageUrl { get; set; }
    /// <summary>วิดีโอ/หน้า landing ที่ผู้ใช้ต้องดูให้จบ — ต้องเป็นโดเมนที่ CSP อนุญาต</summary>
    public string? MediaUrl { get; set; }
    /// <summary>ลิงก์ที่เปิดเมื่อผู้ใช้กด "สนใจ" (partner) — นับเป็น lead</summary>
    public string? PartnerUrl { get; set; }

    /// <summary>ต้องอยู่กับหน้านี้กี่วินาทีจึงจะได้รางวัล (เจ้าของระบบขอ 60 วินาที)</summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>ได้โควตาเอกสารเพิ่มกี่ฉบับต่อครั้ง</summary>
    public int RewardDocuments { get; set; } = 5;
    /// <summary>โควตาที่ได้มีอายุกี่วัน (0 = ถึงสิ้นเดือน)</summary>
    public int RewardValidDays { get; set; } = 30;

    /// <summary>เพดานต่อบริษัท — กัน abuse (ดูรัว ๆ จนไม่ต้องจ่ายเลย)</summary>
    public int MaxPerDay { get; set; } = 2;
    public int MaxPerMonth { get; set; } = 10;

    /// <summary>รายได้ที่เราคาดว่าจะได้ต่อครั้ง (บาท) — ใช้ในรายงานฝั่งแอดมินเพื่อ
    /// เทียบกับ overage ที่เสียไป **ไม่ใช่ยอดที่ลงบัญชี** (lead ยังไม่ใช่รายได้)</summary>
    public decimal EstimatedRevenuePerView { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>1 ครั้งที่ผู้ใช้ทำภารกิจจนจบและได้โควตา — append-only เหมือน UsageEvent
/// (เป็นหลักฐานว่าโควตาพิเศษมาจากไหน เวลาลูกค้าถามว่าทำไมตัวเลขไม่ตรงแพ็กเกจ)</summary>
public class QuotaRewardGrant : TenantEntity
{
    public Guid OptionId { get; set; }
    public QuotaRewardOption Option { get; set; } = null!;

    public Guid? UserId { get; set; }
    public QuotaRewardKind Kind { get; set; }
    public int GrantedDocuments { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    /// <summary>ผู้ใช้กด "สนใจ" ต่อจากการดู (lead จริง) — ตัวชี้วัดว่ารายได้เกิดไหม</summary>
    public bool ClickedThrough { get; set; }
    /// <summary>เวลาที่อยู่กับหน้าจริง (วินาที) — ต่ำกว่า DurationSeconds = ไม่ให้รางวัล</summary>
    public int WatchedSeconds { get; set; }
}
