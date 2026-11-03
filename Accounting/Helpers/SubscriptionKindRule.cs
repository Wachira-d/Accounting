using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 231 (คำตัดสินข้อ 231-10 · LICENSE_USAGE_OVERAGE_AUDIT §A.8/E7 · ฝ่ายค้าน B1) — <b>ตัวตัดสินเดียวของ <c>Subscription.Kind</c></b> (ทดลอง / Free Edition / จ่ายเงิน)
/// <para>เดิมทั้ง "ทดลองใช้แพ็กเกจ X" และ "Free Edition" ใช้ <c>SubscriptionPlan.FreeTrial</c> ร่วมกัน ⇒ รายงาน/ตัวกรอง/ด่านต้องอ่าน 3 ช่องเสมอ ·
/// ตอนนี้ผู้เขียนทุกจุด (เริ่มทดลอง/ฟรี · อนุมัติชำระ · แอดมินเปลี่ยนแพ็กเกจ · หมดทดลอง · ดาวน์เกรดถึงวันมีผล) เรียกที่นี่ ·
/// SQL backfill ใน <c>PlatformPriceOverrideMigration</c> เป็นสูตรเดียวกัน — แก้ที่หนึ่งต้องแก้อีกที่</para>
/// <para><b>นิยาม "ไม่เคยจ่าย"</b> (ฝ่ายค้าน B1 ข): <c>LastPaymentDate == null</c> <b>และ</b> ไม่มี <c>SubscriptionPayment</c> สถานะ Approved — ผู้เรียกส่ง <paramref name="neverPaid"/> ที่ตรวจทั้งสองมาแล้ว ·
/// <b>สูตร:</b> ฟรีถาวร ⇒ Free · สถานะ Trial ⇒ Trial · ไม่เคยจ่าย + (แพ็กเกจ FreeTrial <b>หรือ</b> มีประวัติทดลองที่หมดอายุ <c>TrialConfig.TrialStatus = Expired</c>) ⇒ Trial ·
/// <b>อื่น ๆ ⇒ Paid</b> — แพ็กเกจจ่ายเงินที่หมดอายุโดยไม่มีประวัติทดลอง = ลูกค้าหมดอายุ (เช่น แอดมินเปิดให้) ไม่ใช่ทดลอง แม้ <c>LastPaymentDate</c> ว่าง ·
/// ราคาต่อรอบ <b>ไม่</b>ใช่ปัจจัย (ราคา 0 ที่แอดมินเปิดให้ = Paid)</para></summary>
public static class SubscriptionKindRule
{
    public static SubscriptionKind Derive(bool isPermanentFree, SubscriptionStatus status, bool neverPaid, bool isFreeTrialPlan, bool trialRecordExpired = false)
    {
        if (isPermanentFree) return SubscriptionKind.Free;
        if (status == SubscriptionStatus.Trial) return SubscriptionKind.Trial;
        if (neverPaid && (isFreeTrialPlan || trialRecordExpired)) return SubscriptionKind.Trial;
        return SubscriptionKind.Paid;
    }

    /// <summary>"ไม่เคยจ่าย" = ไม่มีวันชำระล่าสุด <b>และ</b> ไม่มีรายการชำระที่อนุมัติ (ผู้เรียกตรวจฐานมาให้)</summary>
    public static bool NeverPaid(DateTime? lastPaymentDate, bool hasApprovedPayment) => lastPaymentDate == null && !hasApprovedPayment;

    /// <summary>หลังอนุมัติการชำระ/เปิดแพ็กเกจจากแม่แบบ (รวมแอดมินเปลี่ยนแพ็กเกจให้บริษัทที่ Active) — ฟรีถาวร = Free · ไม่งั้น Paid</summary>
    public static SubscriptionKind AfterTemplateApplied(bool templateIsPermanentFree)
        => templateIsPermanentFree ? SubscriptionKind.Free : SubscriptionKind.Paid;

    /// <summary>หมดทดลอง (สถานะ Trial → Expired) — ยังเป็นทดลอง (ฟรีถาวรไม่มีวันหมดอายุ แต่กันไว้)</summary>
    public static SubscriptionKind AfterTrialExpired(bool isPermanentFree)
        => isPermanentFree ? SubscriptionKind.Free : SubscriptionKind.Trial;

    public static string Label(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Trial => "ทดลองใช้",
        SubscriptionKind.Free => "Free Edition",
        SubscriptionKind.Paid => "จ่ายเงิน",
        _ => "ไม่ทราบ",
    };
}
