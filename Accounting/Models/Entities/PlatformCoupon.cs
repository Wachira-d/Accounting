namespace Accounting.Models.Entities;

/// <summary>รอบ 230: ชนิดส่วนลดของคูปอง</summary>
public enum PlatformCouponKind { Percent = 1, Fixed = 2 }

/// <summary>รอบ 230: คูปองใช้กับอะไรได้ (flags) — ค่าแพ็กเกจ (รายการชำระค่าแพ็กเกจ/ส่วนต่างอัปเกรด) · โควตาเสริม</summary>
[Flags]
public enum PlatformCouponScope { None = 0, Subscription = 1, QuotaAddOn = 2, Both = 3 }

/// <summary>รอบ 235 (K-2 · คำตัดสิน 235-1): คูปองค่าแพ็กเกจใช้กับการซื้อแบบไหน — <see cref="New"/> = ซื้อใหม่/อัปเกรด/เปลี่ยนรอบ ·
/// <see cref="Renewal"/> = ต่ออายุแพ็กเดิม+รอบเดิม (<c>PlatformCouponRule.IsRenewalTarget</c>) · <see cref="Both"/> = ทั้งสอง ·
/// ค่าตั้งต้น = <see cref="New"/> (ฝ่ายค้าน S-1: คูปองเดิม/ใหม่ไม่ลดต่ออายุจนแอดมินเลือกเปิด)</summary>
public enum PlatformCouponPurchaseKinds { None = 0, New = 1, Renewal = 2, Both = 3 }

/// <summary>รอบ 230: สถานะการใช้คูปองหนึ่งครั้ง — Reserved = ผูกกับรายการที่ยังไม่จ่าย (นับเข้าเพดานจนหมดอายุการจอง) · Redeemed = เงินเข้าแล้ว ·
/// Released = รายการถูกยกเลิก/ปฏิเสธ (ไม่นับ)</summary>
public enum PlatformCouponRedemptionStatus { Reserved = 1, Redeemed = 2, Released = 3 }

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §7.1 "คูปอง/โค้ดส่วนลด" · คำตัดสินข้อ 230-Q2) — <b>คูปองของแพลตฟอร์ม</b> (NextAcc เก็บเงินผู้เช่า · ไม่ใช่คูปองของร้าน) ·
/// แอดมินแพลตฟอร์มสร้าง/แก้/ปิดที่ <c>/pages/admin-coupons.html</c> (audit chain ทุกครั้ง) · ตัวตัดสินเดียว <c>Helpers/PlatformCouponRule</c> ·
/// ตัวเก็บ/นับ <c>Services/Implementations/PlatformCouponStore</c> (ใต้ <c>pg_advisory_xact_lock</c> ต่อคูปอง) · ตาราง/index ตรงกับ <c>Helpers/PlatformCouponMigration</c>
/// </summary>
public class PlatformCoupon : BaseEntity
{
    /// <summary>รหัส (ตัวพิมพ์ใหญ่ · ไม่สนตัวพิมพ์ตอนกรอก · unique)</summary>
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public PlatformCouponKind Kind { get; set; } = PlatformCouponKind.Percent;
    /// <summary>Percent = 0–100 · Fixed = บาท (รวม VAT ตามค่าตั้งราคาแพลตฟอร์ม — หักจากยอดที่ต้องจ่าย)</summary>
    public decimal Value { get; set; }
    public DateTime? ValidFromUtc { get; set; }
    /// <summary>ใช้ได้ถึง (ไม่รวมเวลานี้) · null = ไม่หมดอายุ</summary>
    public DateTime? ValidToUtc { get; set; }
    /// <summary>ใช้ได้ทั้งหมดกี่ครั้ง (ทุกบริษัทรวม) · null = ไม่จำกัด</summary>
    public int? MaxUsesTotal { get; set; }
    /// <summary>ใช้ได้กี่ครั้งต่อบริษัท · null = ไม่จำกัด (ค่าตั้งต้น 1)</summary>
    public int? MaxUsesPerCompany { get; set; } = 1;
    /// <summary>จำนวนครั้งที่เงินเข้าแล้ว (นับตอน Redeemed ใต้ล็อก — ตัวแสดงผล · ตัวตัดสินเพดานนับจากตาราง redemption)</summary>
    public int UsedCount { get; set; }
    public PlatformCouponScope AppliesTo { get; set; } = PlatformCouponScope.Both;
    /// <summary>รอบ 235 (K-2): ใช้กับซื้อใหม่/ต่ออายุ/ทั้งสอง (เฉพาะขอบเขตค่าแพ็กเกจ · โควตาเสริมไม่ดูช่องนี้) — ค่าตั้งต้น New (ฝ่ายค้าน S-1 · migration DEFAULT 1)</summary>
    public PlatformCouponPurchaseKinds PurchaseKinds { get; set; } = PlatformCouponPurchaseKinds.New;
    /// <summary>แพ็กเกจที่ใช้ได้ (ชื่อ enum คั่นด้วย ",") · null/ว่าง = ทุกแพ็กเกจ</summary>
    public string? PlansCsv { get; set; }
    /// <summary>รอบบิลที่ใช้ได้ (Monthly,Quarterly,SemiAnnual,Annual) · null/ว่าง = ทุกรอบ</summary>
    public string? CyclesCsv { get; set; }
    /// <summary>ทรัพยากรโควตาเสริมที่ใช้ได้ (รหัสใน QuotaResources) · null/ว่าง = ทุกรายการ</summary>
    public string? AddOnResourcesCsv { get; set; }
    /// <summary>ใช้ได้เฉพาะบริษัทที่ยังไม่เคยจ่ายค่าบริการ NextAcc (การชำระครั้งแรก)</summary>
    public bool FirstPaymentOnly { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>รอบ 230: การใช้คูปอง 1 ครั้งต่อรายการต้นทาง (unique CouponId + SourceKind + SourceId) — จองตอนสร้างรายการชำระ · นับเมื่อเงินเข้า · ปลดเมื่อยกเลิก/ปฏิเสธ</summary>
public class PlatformCouponRedemption : BaseEntity
{
    public Guid CouponId { get; set; }
    /// <summary>บริษัทผู้ใช้คูปอง (ผู้จ่าย)</summary>
    public Guid CompanyId { get; set; }
    /// <summary>"SubscriptionPayment" | "QuotaAddOn"</summary>
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    /// <summary>ราคาก่อนส่วนลด / ส่วนลด / ยอดหลังส่วนลด (รวม VAT ตามค่าตั้ง)</summary>
    public decimal ListAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal AmountAfter { get; set; }
    public PlatformCouponRedemptionStatus Status { get; set; } = PlatformCouponRedemptionStatus.Reserved;
    /// <summary>การจองนับเข้าเพดานถึงเวลานี้ (รายการค้างไม่จ่ายไม่กินสิทธิ์คนอื่นตลอดไป) · เงินเข้าหลังหมดอายุการจอง = ยังให้ส่วนลด (ลูกค้าจ่ายตามยอดที่เห็นแล้ว)</summary>
    public DateTime ReservedUntilUtc { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
