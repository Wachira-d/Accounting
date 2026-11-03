using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ตัวเลือกคำถาม "ลูกค้าต้องจ่ายก่อนไหม" (ป้าย + คำอธิบาย + ชนิดเอกสารที่ระบบเลือกให้)</summary>
public sealed record BookingPayFirstOption(string Value, string Label, string Description, string DocumentLabel);

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก D · §1 ข้อ 4 · คำตัดสิน Q1) — <b>ตัวตัดสินเดียว</b>ของคำถามธุรกิจบนฟอร์มบริการ
/// "ลูกค้าต้องจ่ายก่อนไหม" ⇒ ชนิดเอกสารบัญชี (<see cref="BookingType"/>) ที่ระบบเลือกให้ — เดิมเจ้าของต้องเลือก "การออกเอกสารบัญชี" เอง
/// แล้วตั้งมัดจำบนบริการชนิดนัดหมาย ⇒ ชนกล่องแดง "เก็บเงินล่วงหน้าไม่ได้" (§78/1 ต้องเป็นใบรับมัดจำ)
/// <para>คำตอบ ⇒ ชนิด: ไม่ต้องจ่ายก่อน = นัดหมาย (ใบเสนอราคา) · ต้องจ่ายก่อน = ชำระมัดจำล่วงหน้า (ใบรับมัดจำ — ลงวันที่รับเงิน) ·
/// ร้านติดต่อกลับ = รับจอง/รอยืนยัน (ยังไม่ออกเอกสาร) · "จองยืนยัน (ใบกำกับภาษีตอนจอง)" เลือกได้เฉพาะ <b>ขั้นสูง (ผู้ทำบัญชี)</b> ⇒ คำตอบ null</para>
/// </summary>
public static class BookingPayFirst
{
    public const string RuleCode = "CMS-BOOKING-PAY-FIRST";
    public const string AtShop = "AtShop";
    public const string PayFirst = "PayFirst";
    public const string ShopConfirms = "ShopConfirms";

    public static IReadOnlyList<BookingPayFirstOption> Options { get; } = new[]
    {
        new BookingPayFirstOption(AtShop, "ไม่ต้องจ่ายก่อน — จ่ายที่ร้านตอนใช้บริการ", "ลูกค้าจองแล้วได้คิว · ระบบออกใบยืนยันการจอง (ใบเสนอราคา) · รับเงินตอนให้บริการ", "นัดหมาย (ใบเสนอราคา)"),
        new BookingPayFirstOption(PayFirst, "ต้องจ่ายมัดจำหรือจ่ายเต็มก่อน", "ลูกค้าจ่ายผ่านเว็บ (โอน/สลิป หรือออนไลน์) · ระบบออกใบรับมัดจำลงวันที่รับเงิน · ตั้งยอดที่ “การชำระล่วงหน้าของบริการนี้”", "ชำระมัดจำล่วงหน้า (ใบรับมัดจำ)"),
        new BookingPayFirstOption(ShopConfirms, "ร้านติดต่อกลับเพื่อยืนยัน (ยังไม่มีราคา)", "เหมาะกับนัดดูทรัพย์/ปรึกษาฟรี/จองโต๊ะ · ยังไม่ออกเอกสาร", "รับจอง/รอยืนยัน"),
    };

    /// <summary>คำตอบของชนิดที่ตั้งไว้ · null = ชนิดที่เลือกได้เฉพาะขั้นสูง (จองยืนยัน · ใบกำกับภาษีตอนจอง)</summary>
    public static string? AnswerFor(BookingType type) => type switch
    {
        BookingType.Appointment => AtShop,
        BookingType.PrePayment => PayFirst,
        BookingType.Lead => ShopConfirms,
        _ => null,
    };

    /// <summary>คำตอบ ⇒ ชนิด · ไม่รู้จัก ⇒ null (ผู้เรียกปฏิเสธพร้อมข้อความ)</summary>
    public static BookingType? TypeFor(string? answer) => answer switch
    {
        AtShop => BookingType.Appointment,
        PayFirst => BookingType.PrePayment,
        ShopConfirms => BookingType.Lead,
        _ => null,
    };

    /// <summary>ตั้งมัดจำบนบริการที่ตอบ "ไม่ต้องจ่ายก่อน"/"ร้านติดต่อกลับ" ⇒ ข้อความ (มัดจำจะไม่ถูกเก็บ) · null = สอดคล้อง</summary>
    public static string? DepositConflict(BookingType type, decimal? depositAmount, decimal? depositPercent)
        => type != BookingType.PrePayment && (depositAmount is > 0m || depositPercent is > 0m)
            ? "ตั้งมัดจำไว้ แต่ตอบว่าลูกค้า “ไม่ต้องจ่ายก่อน” — มัดจำจะไม่ถูกเก็บ · ถ้าต้องการเก็บเงินก่อน เลือก “ต้องจ่ายมัดจำหรือจ่ายเต็มก่อน”"
            : null;
}
