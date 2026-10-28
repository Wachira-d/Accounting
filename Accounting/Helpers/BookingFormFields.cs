using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ค่าตั้งช่องฟอร์มจองของลูกค้า — ชื่อบังคับเสมอ (ใช้ออกเอกสาร/เรียกคิว) · ช่องอื่น บังคับ/ไม่บังคับ/ซ่อน</summary>
public sealed record BookingFormFieldsConfig(
    BookingFieldRequirement Phone = BookingFieldRequirement.Required,
    BookingFieldRequirement Email = BookingFieldRequirement.Optional,
    BookingFieldRequirement Notes = BookingFieldRequirement.Optional);

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ช่องไหนต้องกรอกถึงจะจองได้" (รอบ 207 · คำขอเจ้าของข้อ 7)
/// <para>ผู้อ่าน: <c>CmsBookingService.CreateBookingAsync</c> (เส้นลูกค้า — ด่านจริง) · หน้าจองของ storefront (แสดงดอกจัน/ซ่อนช่องจากค่าที่เซิร์ฟเวอร์ส่ง ·
/// ไม่มีสำเนากติกาใน JS) · หน้าตั้งค่า CMS</para>
/// <para><b>ค่าเดิม</b> (JSON null) = หน้าเว็บแสดงฟอร์มเดิม (ชื่อ+เบอร์บังคับฝั่งฟอร์ม) และ<b>เซิร์ฟเวอร์ไม่บังคับ</b>เหมือนก่อนรอบ 207 (ผู้เรียก API เดิมไม่พัง) · ตั้งค่าแล้วจึงบังคับที่เซิร์ฟเวอร์ · เบอร์และอีเมลซ่อนพร้อมกันไม่ได้ (ร้านติดต่อลูกค้า/ส่งเตือนไม่ได้)</para>
/// <para>พนักงานสร้างการจองเอง (walk-in) ไม่ผ่านด่านนี้ — ลูกค้าหน้าร้านอาจไม่ให้เบอร์</para>
/// </summary>
public static class BookingFormFields
{
    public const string RuleCode = "CMS-BOOKING-FORM";
    public static readonly BookingFormFieldsConfig Default = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static BookingFormFieldsConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return JsonSerializer.Deserialize<BookingFormFieldsConfig>(json, Json) ?? Default; }
        catch (JsonException) { return Default; }
    }

    public static string Serialize(BookingFormFieldsConfig c) => JsonSerializer.Serialize(c, Json);

    /// <summary>ตรวจค่าตั้งก่อนบันทึก · null = ผ่าน</summary>
    public static string? ValidateConfig(BookingFormFieldsConfig c)
    {
        if (!Enum.IsDefined(c.Phone) || !Enum.IsDefined(c.Email) || !Enum.IsDefined(c.Notes)) return "ค่าตั้งช่องฟอร์มจองไม่ถูกต้อง";
        if (c.Phone == BookingFieldRequirement.Hidden && c.Email == BookingFieldRequirement.Hidden)
            return "ซ่อนทั้งเบอร์โทรและอีเมลไม่ได้ — ร้านจะติดต่อ/ส่งเตือนลูกค้าไม่ได้ (เปิดอย่างน้อย 1 ช่อง)";
        return null;
    }

    /// <summary>ค่าที่ลูกค้ากรอกหลังตัดช่องที่ซ่อน (ช่องซ่อน ⇒ null แม้ส่งมา)</summary>
    public sealed record Cleaned(string Name, string? Phone, string? Email, string? Notes);

    /// <summary>ตรวจฟอร์มของลูกค้า — คืนข้อความผิดทั้งหมด (ว่าง = ผ่าน) + ค่าที่ทำความสะอาดแล้ว</summary>
    public static (IReadOnlyList<string> Errors, Cleaned Values) Check(BookingFormFieldsConfig c, string? name, string? phone, string? email, string? notes)
    {
        var errs = new List<string>();
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var n = T(name);
        var p = c.Phone == BookingFieldRequirement.Hidden ? null : T(phone);
        var e = c.Email == BookingFieldRequirement.Hidden ? null : T(email);
        var no = c.Notes == BookingFieldRequirement.Hidden ? null : T(notes);
        if (n == null) errs.Add("กรุณากรอกชื่อ-นามสกุล");
        if (c.Phone == BookingFieldRequirement.Required && p == null) errs.Add("กรุณากรอกเบอร์โทร");
        if (c.Email == BookingFieldRequirement.Required && e == null) errs.Add("กรุณากรอกอีเมล");
        if (c.Notes == BookingFieldRequirement.Required && no == null) errs.Add("กรุณากรอกหมายเหตุ/รายละเอียดเพิ่มเติม");
        if (p != null && p.Count(char.IsDigit) < 6) errs.Add("เบอร์โทรไม่ถูกต้อง");
        if (e != null && !LooksLikeEmail(e)) errs.Add("อีเมลไม่ถูกต้อง");
        return (errs, new Cleaned(n ?? "", p, e, no));
    }

    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at == s.LastIndexOf('@') && s.IndexOf('.', at) > at + 1 && !s.EndsWith('.') && !s.Any(char.IsWhiteSpace);
    }

    public sealed record FieldOption(string Value, string Label);
    public static IReadOnlyList<FieldOption> RequirementOptions() => new[]
    {
        new FieldOption(nameof(BookingFieldRequirement.Required), "บังคับกรอก"),
        new FieldOption(nameof(BookingFieldRequirement.Optional), "ไม่บังคับ"),
        new FieldOption(nameof(BookingFieldRequirement.Hidden), "ไม่แสดง"),
    };
}
