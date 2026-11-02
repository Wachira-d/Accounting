using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องที่เจ้าของเพิ่มเองบนฟอร์มจอง (เช่น "ความยาวผม" dropdown · "แพ้ยา/อาหาร" ข้อความ) — กติกากลาง <see cref="CustomFormFields"/></summary>
/// <param name="Key">a–z 0–9 _ (≤ 30) — ไม่ซ้ำในฟอร์ม</param>
/// <param name="Type">text · textarea · number · date · select · checkbox (<see cref="CustomFormFields.FieldTypes"/>)</param>
/// <param name="Requirement">Hidden = ปิดช่องไว้ชั่วคราว (ไม่แสดง · ไม่เก็บ — ความหมายเดียวกับช่องมาตรฐาน)</param>
/// <param name="ServiceIds">ใช้กับบริการไหน · null/ว่าง = ทุกบริการของเว็บ</param>
public sealed record BookingCustomField(
    string Key, string Label, string? LabelEn = null, string Type = "text",
    IReadOnlyList<string>? Options = null,
    BookingFieldRequirement Requirement = BookingFieldRequirement.Optional,
    int? MaxLength = null, IReadOnlyList<Guid>? ServiceIds = null) : ICustomFormField;

/// <summary>ค่าตั้งช่องฟอร์มจองของลูกค้า — ชื่อบังคับเสมอ (ใช้ออกเอกสาร/เรียกคิว) · ช่องอื่น บังคับ/ไม่บังคับ/ซ่อน ·
/// รอบ 222: <paramref name="CustomFields"/> ช่องที่เจ้าของเพิ่มเอง (ระดับเว็บ · เลือกบริการที่ใช้ได้ต่อช่อง)</summary>
public sealed record BookingFormFieldsConfig(
    BookingFieldRequirement Phone = BookingFieldRequirement.Required,
    BookingFieldRequirement Email = BookingFieldRequirement.Optional,
    BookingFieldRequirement Notes = BookingFieldRequirement.Optional,
    IReadOnlyList<BookingCustomField>? CustomFields = null);

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

    public static string Serialize(BookingFormFieldsConfig c) => JsonSerializer.Serialize(Normalize(c), Json);

    /// <summary>รูปมาตรฐานของช่องเพิ่มเอง (ตัวกลาง <see cref="CustomFormFields"/>) · รายการบริการว่าง = null (ทุกบริการ) · ไม่มีช่อง = null</summary>
    public static BookingFormFieldsConfig Normalize(BookingFormFieldsConfig c) => c with
    {
        CustomFields = c.CustomFields is not { Count: > 0 } ? null : c.CustomFields.Select(f => f with
        {
            Key = CustomFormFields.NormKey(f.Key),
            Label = CustomFormFields.NormLabel(f.Label),
            LabelEn = CustomFormFields.NormLabelEn(f.LabelEn),
            Type = CustomFormFields.NormType(f.Type),
            Options = CustomFormFields.NormOptions(f.Options),
            ServiceIds = f.ServiceIds is { Count: > 0 } ids ? ids.Distinct().ToList() : null,
        }).ToList(),
    };

    /// <summary>ตรวจค่าตั้งก่อนบันทึก · null = ผ่าน · <paramref name="knownServiceIds"/> = บริการของเว็บนี้ (null = ไม่ตรวจขอบเขตบริการ)</summary>
    public static string? ValidateConfig(BookingFormFieldsConfig raw, IReadOnlyCollection<Guid>? knownServiceIds = null)
    {
        var c = Normalize(raw);
        if (!Enum.IsDefined(c.Phone) || !Enum.IsDefined(c.Email) || !Enum.IsDefined(c.Notes)) return "ค่าตั้งช่องฟอร์มจองไม่ถูกต้อง";
        if (c.Phone == BookingFieldRequirement.Hidden && c.Email == BookingFieldRequirement.Hidden)
            return "ซ่อนทั้งเบอร์โทรและอีเมลไม่ได้ — ร้านจะติดต่อ/ส่งเตือนลูกค้าไม่ได้ (เปิดอย่างน้อย 1 ช่อง)";
        var fields = c.CustomFields ?? Array.Empty<BookingCustomField>();
        if (CustomFormFields.ValidateFields(fields.Cast<ICustomFormField>().ToList()) is string fieldErr) return fieldErr;
        if (knownServiceIds != null)
            foreach (var f in fields)
                if (f.ServiceIds?.Any(id => !knownServiceIds.Contains(id)) == true)
                    return $"ช่อง \"{f.Label}\": บริการที่เลือกไม่มีในเว็บนี้แล้ว (อาจถูกลบ) — เลือกบริการใหม่ หรือเลือก \"ทุกบริการ\"";
        return null;
    }

    /// <summary>ช่องเพิ่มเองข้อนี้ใช้กับบริการนี้ไหม (ไม่ระบุบริการ = ทุกบริการ)</summary>
    public static bool AppliesTo(BookingCustomField f, Guid serviceId) => f.ServiceIds is not { Count: > 0 } || f.ServiceIds.Contains(serviceId);

    /// <summary>ช่องเพิ่มเองที่แสดง/เก็บบนฟอร์มจองบริการนี้ (ใช้กับบริการ + ไม่ได้ตั้ง "ไม่แสดง") — ตัวเดียวที่ด่านและหน้าเว็บ (ผ่าน DTO บริการ) ใช้</summary>
    public static IReadOnlyList<BookingCustomField> FieldsForService(BookingFormFieldsConfig c, Guid serviceId)
        => (Normalize(c).CustomFields ?? Array.Empty<BookingCustomField>())
            .Where(f => AppliesTo(f, serviceId) && f.Requirement != BookingFieldRequirement.Hidden).ToList();

    /// <summary>ตรวจคำตอบช่องเพิ่มเองตอนสร้างการจอง — ทุกทางเข้า (ลูกค้า · สมาชิก · พนักงานจองแทน) เดินที่นี่ ·
    /// <paramref name="byStaff"/> = พนักงานจองแทน: ไม่บังคับช่อง "บังคับ" (walk-in ไม่ต้องถามครบ) แต่ค่าที่ส่งต้องถูกรูป (ตัวเลือกในรายการ/ความยาว/ตัวเลข/วันที่) ·
    /// คืนข้อความผิด (ว่าง = ผ่าน) + JSON คำตอบที่ตรึงบนการจอง (null = ไม่มี) · ลูกค้าส่งคีย์ของช่องซ่อน/บริการอื่นมา = ไม่เก็บ</summary>
    public static (IReadOnlyList<string> Errors, string? AnswersJson) CheckCustom(BookingFormFieldsConfig c, Guid serviceId,
        IReadOnlyDictionary<string, string?>? input, bool byStaff)
    {
        var errs = new List<string>();
        var answers = CustomFormFields.CheckAnswers(FieldsForService(c, serviceId), input, errs, enforceRequired: !byStaff);
        return (errs, CustomFormFields.SerializeAnswers(answers));
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

    /// <summary>schema รอบ 222 — คำตอบช่องเพิ่มเองบนการจอง (NULL = การจองเดิม · ไม่ backfill · รันทุกบูตได้) · ค่าตั้งช่องอยู่ใน <c>FormFieldsJson</c> เดิม (ไม่ต้องมีคอลัมน์ใหม่)</summary>
    public static IReadOnlyList<string> MigrationStatements() => new[]
    {
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "CustomFieldsJson" text NULL;""",
    };

    public sealed record FieldOption(string Value, string Label);
    public static IReadOnlyList<FieldOption> RequirementOptions() => new[]
    {
        new FieldOption(nameof(BookingFieldRequirement.Required), "บังคับกรอก"),
        new FieldOption(nameof(BookingFieldRequirement.Optional), "ไม่บังคับ"),
        new FieldOption(nameof(BookingFieldRequirement.Hidden), "ไม่แสดง"),
    };
}
