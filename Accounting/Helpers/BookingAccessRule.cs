using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว "ต้องเป็นสมาชิกก่อนจองไหม"</b> (รอบ 210 · คำตัดสินข้อ 169) — ค่าเริ่มต้น = พฤติกรรมเดิม (แขกจองได้)
/// <para>ลำดับชั้น: ค่าของบริการ (<c>SiteBookingService.MemberOnly</c> true/false) ชนะ · null = ตามค่าของเว็บ (<c>SiteBookingSettings.RequireMemberToBook</c>) ·
/// เว็บไม่มีแถวค่าตั้ง = false</para>
/// <para>ด่านจริงอยู่ที่ <c>CmsBookingService.CreateBookingAsync</c> <b>ก่อน</b> ตัดสินรอบ (<c>BookingSlotEngine.Decide</c>) และก่อนบันทึก ·
/// "สมาชิกที่ยืนยันแล้ว" = ลูกค้าจากโทเคนหน้าร้านของเว็บนี้ (controller แปลงผ่าน <c>StorefrontCustomerAuth.OrderCustomerId</c>) ที่ยังใช้งานอยู่ ·
/// พนักงานที่มีสิทธิ์จัดการคำสั่งซื้อ/การจอง (จองแทนลูกค้า) ผ่านได้ · หน้าเว็บอ่าน <c>MemberOnly</c> ที่เซิร์ฟเวอร์ตัดสินแล้ว (ไม่คิดเอง)</para>
/// </summary>
public static class BookingAccessRule
{
    public const string RuleCode = "CMS-BOOKING-MEMBER-ONLY";

    /// <summary>ข้อความเมื่อถูกกัน — บอกทางไปต่อ (เข้าสู่ระบบ/สมัครสมาชิก) · HTTP 401</summary>
    public const string MemberRequiredMessage =
        "บริการนี้ต้องเข้าสู่ระบบสมาชิกก่อนจอง — เข้าสู่ระบบหรือสมัครสมาชิกที่หน้า \"บัญชีของฉัน\" ของเว็บนี้ แล้วจองอีกครั้ง";

    public const int MemberRequiredStatus = 401;

    /// <summary>บริการนี้ต้องเป็นสมาชิกไหม — <paramref name="serviceOverride"/> ไม่ null ชนะ · null = ค่าของเว็บ</summary>
    public static bool RequiresMember(bool siteRequiresMember, bool? serviceOverride) => serviceOverride ?? siteRequiresMember;

    /// <summary>ด่าน: ไม่ต้องเป็นสมาชิก / มีสมาชิกที่ยืนยันแล้ว / พนักงานจองแทน ⇒ null · ไม่งั้น = ข้อความไทยพร้อมทางไปต่อ</summary>
    public static string? Problem(bool requiresMember, bool hasVerifiedCustomer, bool isStaff)
        => !requiresMember || hasVerifiedCustomer || isStaff ? null : MemberRequiredMessage;

    /// <summary>ค่าที่ผู้ใช้เลือกบนฟอร์ม → ค่าที่เก็บ (Inherit = null)</summary>
    public static bool? ToOverride(BookingMemberAccess access) => access switch
    {
        BookingMemberAccess.Required => true,
        BookingMemberAccess.NotRequired => false,
        _ => null,
    };

    /// <summary>ค่าที่เก็บ → ค่าบนฟอร์ม (echo กลับ — กฎ #4 B)</summary>
    public static BookingMemberAccess FromOverride(bool? serviceOverride) => serviceOverride switch
    {
        true => BookingMemberAccess.Required,
        false => BookingMemberAccess.NotRequired,
        null => BookingMemberAccess.Inherit,
    };

    public sealed record Option(string Value, string Label);

    /// <summary>ตัวเลือกบนฟอร์มบริการ (server ประกาศป้าย — หน้า CMS แสดงตรง ไม่มีสำเนาป้าย)</summary>
    public static IReadOnlyList<Option> Options(bool siteRequiresMember) => new[]
    {
        new Option(nameof(BookingMemberAccess.Inherit),
            "ตามค่าของเว็บไซต์ (ตอนนี้: " + (siteRequiresMember ? "ต้องเป็นสมาชิก" : "แขกจองได้") + ")"),
        new Option(nameof(BookingMemberAccess.Required), "ต้องเข้าสู่ระบบสมาชิกก่อนจอง"),
        new Option(nameof(BookingMemberAccess.NotRequired), "แขกจองได้ (ไม่ต้องเป็นสมาชิก)"),
    };
}
