using Accounting.Models.Enums;

namespace Accounting.Helpers;

public enum SubscriptionCancelMode
{
    /// <summary>ยกเลิกทันที — ทดลองใช้/ฟรีถาวร/หมดอายุ/เกินกำหนด (ไม่มีรอบที่ชำระแล้วเหลือ)</summary>
    Immediate = 1,
    /// <summary>ยกเลิกเมื่อสิ้นรอบ — ใช้ได้ต่อจนวันหมดอายุของรอบที่ชำระแล้ว ไม่ออกใบต่ออายุ ไม่ prorate (คำตัดสินเจ้าของข้อ 178 ⑥)</summary>
    AtPeriodEnd = 2,
}

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 3 (คำตัดสินเจ้าของข้อ 178 ⑥ · ข้อ 180) — <b>ตัวตัดสินการยกเลิกแพ็กเกจตัวเดียว (pure)</b>: ยกเลิกแบบไหน · กลับมาใช้ต่อได้ไหม ·
/// ใบต่ออายุของรอบที่ยกเลิกไว้ห้ามจ่าย/ห้ามทวง · ข้อความที่ผู้เช่าเห็น
/// </summary>
public static class SubscriptionCancelPolicy
{
    public static SubscriptionCancelMode Decide(SubscriptionStatus status, bool permanentFree, DateTime endUtc, DateTime nowUtc) =>
        status == SubscriptionStatus.Active && !permanentFree && endUtc > nowUtc
            ? SubscriptionCancelMode.AtPeriodEnd : SubscriptionCancelMode.Immediate;

    /// <summary>กลับมาใช้ต่อ (ยกเลิกการยกเลิก) ได้เฉพาะก่อนสิ้นรอบ ขณะยัง Active</summary>
    public static string? ReactivateRefusal(bool cancelAtPeriodEnd, SubscriptionStatus status, DateTime endUtc, DateTime nowUtc)
    {
        if (!cancelAtPeriodEnd) return "แพ็กเกจนี้ไม่ได้ตั้งยกเลิกไว้";
        if (status != SubscriptionStatus.Active || endUtc <= nowUtc)
            return "รอบนี้สิ้นสุดแล้ว — กรุณาเลือกแพ็กเกจใหม่ที่หน้าแพ็กเกจ";
        return null;
    }

    /// <summary>ใบต่ออายุของรอบที่ผู้เช่ายกเลิกไว้ — ห้ามจ่าย (จ่าย = ต่ออายุ ขัดกับการยกเลิก) · ทางไปต่อ = กด "ใช้ต่อ" ก่อน</summary>
    public static string? RenewalNotPayableReason(bool cancelAtPeriodEnd) => cancelAtPeriodEnd
        ? "ยกเลิกการต่ออายุไว้แล้ว — ถ้าต้องการใช้ต่อ กด \"ใช้ต่อ\" ที่ศูนย์บิลก่อนชำระใบต่ออายุ"
        : null;

    /// <summary>ข้อความสถานะที่ผู้เช่าเห็น (ศูนย์บิล · ผลการกดยกเลิก)</summary>
    public static string? Note(bool cancelAtPeriodEnd, DateTime endUtc) => cancelAtPeriodEnd
        ? $"ยกเลิกแล้ว — ใช้งานได้ถึง {PlatformInvoiceRules.ThaiDate(endUtc):dd/MM/yyyy} · ไม่ออกใบแจ้งหนี้ต่ออายุ · ไม่คืนเงินตามสัดส่วน"
        : null;
}
