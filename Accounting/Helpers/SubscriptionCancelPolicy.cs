using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179 — เจ้าของ: "ยกเลิก = ใช้ได้ถึงสิ้นรอบ") — <b>ตัวตัดสินเดียว</b>ของการยกเลิกแพ็กเกจ · pure
/// <para><b>ที่มา</b>: <c>CancelSubscriptionAsync</c> เดิมตั้ง Cancelled ทันที ⇒ ผู้ที่จ่ายรายปีแล้วกดยกเลิกเสียสิทธิ์ที่เหลือทันที
/// ขัดกับนโยบายคืนเงินฉบับตั้งต้น ("ไม่คืนเงิน · ยกเลิกมีผลสิ้นรอบ") — นโยบายต้องตรงกับพฤติกรรมระบบจริง</para>
/// </summary>
public static class SubscriptionCancelPolicy
{
    public enum Outcome { Immediate, AtPeriodEnd }

    /// <summary>รอบบิลยาวสุดที่ขาย (เดือน) + ส่วนเผื่อ — วันสิ้นรอบไกลกว่านี้ไม่ใช่ "รอบที่จ่ายแล้ว" (เช่น ฟรีถาวร/แอดมินให้ +100 ปี) ⇒ ยกเลิกทันที</summary>
    public const int MaxPaidHorizonMonths = 13;

    /// <summary>แพ็กเกจที่ใช้งานอยู่และยังมีรอบที่ชำระแล้วเหลือ ⇒ มีผลสิ้นรอบ · ทดลองใช้/หมดอายุ/ค้างชำระ/ฟรีถาวร/สิ้นรอบแล้ว/
    /// วันสิ้นรอบไกลเกินรอบที่ขาย (ฝ่ายค้าน S5: +100 ปี) ⇒ ทันที</summary>
    public static Outcome Decide(SubscriptionStatus status, DateTime endDateUtc, DateTime nowUtc, bool isPermanentFree)
        => status == SubscriptionStatus.Active && !isPermanentFree && endDateUtc > nowUtc
           && endDateUtc <= nowUtc.AddMonths(MaxPaidHorizonMonths) ? Outcome.AtPeriodEnd : Outcome.Immediate;

    /// <summary>ถึงเวลาเปลี่ยนเป็น Cancelled แล้วไหม (งานรายวัน) — ต้องเป็นแพ็กเกจที่ขอยกเลิกไว้และพ้นสิ้นรอบ</summary>
    public static bool DueForCancellation(bool cancelAtPeriodEnd, SubscriptionStatus status, DateTime endDateUtc, DateTime nowUtc)
        => cancelAtPeriodEnd && status == SubscriptionStatus.Active && endDateUtc <= nowUtc;

    /// <summary>กลับมาใช้ต่อ (ยกเลิกการยกเลิก) ได้ไหม — null = ได้</summary>
    public static string? ResumeRefusal(bool cancelAtPeriodEnd, SubscriptionStatus status, DateTime endDateUtc, DateTime nowUtc)
        => !cancelAtPeriodEnd ? "แพ็กเกจนี้ไม่ได้อยู่ระหว่างรอยกเลิก"
            : status != SubscriptionStatus.Active || endDateUtc <= nowUtc ? "พ้นวันสิ้นรอบแล้ว — สมัครแพ็กเกจใหม่จากหน้าแพ็กเกจ"
            : null;

    /// <summary>ข้อความถึงผู้ใช้หลังกดยกเลิก (หน้าเว็บแสดงตรง ๆ)</summary>
    public static string Message(Outcome o, DateTime endDateUtc)
        => o == Outcome.AtPeriodEnd
            ? $"รับคำขอยกเลิกแล้ว — ใช้งานได้ตามสิทธิ์จนถึง {ThaiDateText(endDateUtc)} แล้วแพ็กเกจจะสิ้นสุด ไม่มีการเรียกเก็บรอบถัดไป (กลับมาใช้ต่อได้ก่อนวันดังกล่าว)"
            : "ยกเลิกแพ็กเกจแล้ว";

    /// <summary>ฝ่ายค้าน (หมายเหตุ): ข้อความยืนยันก่อนกดยกเลิก — เซิร์ฟเวอร์ประกอบจากผลตัดสินจริง (หน้าเว็บแสดงอย่างเดียว · ไม่สรุปนโยบายเอง)</summary>
    public static string ConfirmText(SubscriptionStatus status, DateTime endDateUtc, DateTime nowUtc, bool isPermanentFree)
        => Decide(status, endDateUtc, nowUtc, isPermanentFree) == Outcome.AtPeriodEnd
            ? $"ยกเลิกแพ็กเกจ? ใช้งานได้ถึง {ThaiDateText(endDateUtc)} (สิ้นรอบที่ชำระแล้ว) แล้วแพ็กเกจจะสิ้นสุด — การคืนเงินเป็นไปตามนโยบายการยกเลิกและคืนเงิน"
            : "ยกเลิกแพ็กเกจทันที? — การคืนเงินเป็นไปตามนโยบายการยกเลิกและคืนเงิน";

    /// <summary>วันที่แบบไทย (พ.ศ. · เวลาไทย) สำหรับข้อความ</summary>
    private static string ThaiDateText(DateTime utc)
    {
        var bkk = utc.AddHours(7);
        return $"{bkk.Day}/{bkk.Month}/{bkk.Year + 543}";
    }
}
