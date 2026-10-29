using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องที่เจ้าของร้านเพิ่มเองบนฟอร์มสั่งซื้อ (เช่น "ข้อความบนเค้ก" · "วันจัดงาน") — ตรึงป้าย+ค่าลงออเดอร์ตอนสั่ง</summary>
/// <param name="Key">a–z 0–9 _ (≤ 30) — ไม่ซ้ำในฟอร์ม</param>
/// <param name="Type">text · textarea · date · select · checkbox</param>
/// <param name="AppliesTo">all · delivery · pickup</param>
public sealed record OrderCustomField(
    string Key, string Label, string? LabelEn = null, string Type = "text",
    IReadOnlyList<string>? Options = null,
    BookingFieldRequirement Requirement = BookingFieldRequirement.Optional,
    int? MaxLength = null, string AppliesTo = "all");

/// <summary>ค่าตั้งฟอร์มสั่งซื้อของเว็บ — ชื่อบังคับเสมอ · ที่อยู่บังคับเสมอเมื่อจัดส่ง · ผู้ซื้อใบกำกับบังคับเสมอเมื่อขอใบกำกับ (§86/4 ตั้งปิดไม่ได้)</summary>
/// <param name="RequestedDate">ช่อง "วันที่ต้องการรับสินค้า" · ค่าเริ่มต้นไม่แสดง (ฟอร์มเดิม)</param>
public sealed record OrderFormFieldsConfig(
    BookingFieldRequirement Phone = BookingFieldRequirement.Required,
    BookingFieldRequirement Email = BookingFieldRequirement.Required,
    BookingFieldRequirement Notes = BookingFieldRequirement.Optional,
    BookingFieldRequirement RequestedDate = BookingFieldRequirement.Hidden,
    IReadOnlyList<OrderCustomField>? CustomFields = null);

/// <summary>สิ่งที่ลูกค้ากรอกบนฟอร์มสั่งซื้อ (ดิบ)</summary>
public sealed record OrderFormInput(
    string? Name, string? Phone, string? Email, string? Address, string? Notes,
    bool RequestTaxInvoice, string? BillingName, string? BillingAddress, string? BillingTaxId, string? BillingBranchCode,
    IReadOnlyDictionary<string, string?>? Custom);

/// <summary>ค่าหลังตรวจ — ช่องที่ซ่อนเป็น null แม้ส่งมา · เลขผู้เสียภาษีเป็นตัวเลขล้วน · ช่องเพิ่มเองตรึงเป็น JSON (ป้าย+ค่า)</summary>
public sealed record OrderFormCleaned(
    string Name, string? Phone, string? Email, string? Address, string? Notes,
    string? BillingName, string? BillingAddress, string? BillingTaxId, string? BillingBranchCode,
    string? CustomFieldsJson);

/// <summary>คำตอบช่องเพิ่มเองหนึ่งข้อที่ตรึงบนออเดอร์</summary>
public sealed record OrderCustomAnswer(string Key, string Label, string Value);

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ฟอร์มสั่งซื้อหน้าร้านต้องกรอกอะไร" (รอบ 209 · คำตัดสินข้อ 168 ง · R-209-A2 / A10) — แบบแผนเดียวกับ <see cref="BookingFormFields"/>
///
/// <para>═══ ที่มา ═══ หน้าร้านบังคับชื่อ/เบอร์/อีเมลด้วย <c>required</c> ของ HTML เท่านั้น — <c>CreateOrderAsync</c> ไม่ตรวจอะไรเลย (ทีม A §3) ·
/// ผู้ซื้อที่ขอใบกำกับส่งเลขผู้เสียภาษีผิด checksum/สาขาว่างได้ ⇒ ใบกำกับขาดรายการ §86/4 · ร้านเพิ่มช่องของตัวเองไม่ได้</para>
///
/// <para><b>กติกา</b>:
/// <list type="bullet">
/// <item>ขั้นต่ำที่เซิร์ฟเวอร์บังคับ<b>เสมอ</b> (แม้ร้านไม่ตั้งค่า): ชื่อ + (เบอร์ หรือ อีเมล) · จัดส่ง ⇒ ที่อยู่ · ขอใบกำกับ ⇒ ชื่อ/ที่อยู่ผู้ซื้อ +
///   เลขผู้เสียภาษี 13 หลักผ่าน checksum (<see cref="ThaiTaxId.IsValid"/> ตัวกลาง) + รหัสสาขา 5 หลัก (ประกาศอธิบดีฯ 199)</item>
/// <item>ไม่ตั้งค่า (JSON null) = หน้าเว็บวาดฟอร์มเดิม (เบอร์+อีเมลมีดอกจัน) แต่เซิร์ฟเวอร์บังคับแค่ขั้นต่ำ (ผู้เรียก API เดิมไม่พัง)</item>
/// <item>ตั้งค่าแล้ว ⇒ บังคับ/ไม่บังคับ/ซ่อน ตามที่ตั้ง · ซ่อนทั้งเบอร์และอีเมลไม่ได้</item>
/// <item>วันที่ต้องการรับ: ตรวจกับวันพร้อมเร็วสุด + ปฏิทินร้านผ่าน <see cref="MtoLeadTime.RequestedDateProblem"/> (ตัวตัดสินปฏิทินตัวเดียว)</item>
/// </list></para>
/// <para>ผู้อ่าน: <c>CmsCommerceService.CreateOrderAsync</c> (ด่านจริง ก่อนบันทึกอะไร) · checkout-quote (ส่งค่าตั้งให้หน้าเว็บวาด) · หน้าตั้งค่าร้าน</para>
/// </summary>
public static class OrderFormFields
{
    public const string RuleCode = "CMS-ORDER-FORM";
    public const string LegalReference = "RD-86/4 · ประกาศอธิบดีฯ 199";
    public const int MaxCustomFields = 10;
    public const int MaxTextLength = 500;
    public const int MaxNotesLength = 1000;
    public const int MaxOptions = 20;

    public static readonly OrderFormFieldsConfig Default = new();
    public static readonly IReadOnlyList<string> FieldTypes = new[] { "text", "textarea", "date", "select", "checkbox" };
    public static readonly IReadOnlyList<string> AppliesToValues = new[] { "all", "delivery", "pickup" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>JSON ที่เก็บ ⇒ ค่าตั้ง · null/ว่าง/อ่านไม่ออก = null (ไม่ได้ตั้ง)</summary>
    public static OrderFormFieldsConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<OrderFormFieldsConfig>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>ค่าที่หน้าเว็บใช้วาด — ไม่ได้ตั้ง = ฟอร์มเดิม</summary>
    public static OrderFormFieldsConfig ForDisplay(string? json) => Parse(json) ?? Default;

    public static string Serialize(OrderFormFieldsConfig c) => JsonSerializer.Serialize(Normalize(c), Json);

    /// <summary>รูปมาตรฐาน: ตัดช่องว่าง · key ตัวเล็ก · type/appliesTo ตัวเล็ก</summary>
    public static OrderFormFieldsConfig Normalize(OrderFormFieldsConfig c) => c with
    {
        CustomFields = c.CustomFields?.Select(f => f with
        {
            Key = (f.Key ?? "").Trim().ToLowerInvariant(),
            Label = (f.Label ?? "").Trim(),
            LabelEn = string.IsNullOrWhiteSpace(f.LabelEn) ? null : f.LabelEn.Trim(),
            Type = string.IsNullOrWhiteSpace(f.Type) ? "text" : f.Type.Trim().ToLowerInvariant(),
            AppliesTo = string.IsNullOrWhiteSpace(f.AppliesTo) ? "all" : f.AppliesTo.Trim().ToLowerInvariant(),
            Options = f.Options?.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList(),
        }).ToList(),
    };

    /// <summary>ตรวจค่าตั้งก่อนบันทึก · null = ผ่าน</summary>
    public static string? ValidateConfig(OrderFormFieldsConfig raw)
    {
        var c = Normalize(raw);
        if (!Enum.IsDefined(c.Phone) || !Enum.IsDefined(c.Email) || !Enum.IsDefined(c.Notes) || !Enum.IsDefined(c.RequestedDate))
            return "ค่าตั้งช่องฟอร์มสั่งซื้อไม่ถูกต้อง";
        if (c.Phone == BookingFieldRequirement.Hidden && c.Email == BookingFieldRequirement.Hidden)
            return "ซ่อนทั้งเบอร์โทรและอีเมลไม่ได้ — ร้านจะติดต่อลูกค้าไม่ได้ (เปิดอย่างน้อย 1 ช่อง)";
        var fields = c.CustomFields ?? Array.Empty<OrderCustomField>();
        if (fields.Count > MaxCustomFields) return $"เพิ่มช่องเองได้ไม่เกิน {MaxCustomFields} ช่อง";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (f.Key.Length is 0 or > 30 || !f.Key.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return $"รหัสช่อง \"{f.Key}\" ใช้ได้เฉพาะ a–z 0–9 และ _ ไม่เกิน 30 ตัว";
            if (!keys.Add(f.Key)) return $"รหัสช่อง \"{f.Key}\" ซ้ำ";
            if (f.Label.Length is 0 or > 100) return $"ช่อง \"{f.Key}\" ต้องมีชื่อที่แสดง (ไม่เกิน 100 ตัวอักษร)";
            if (!FieldTypes.Contains(f.Type)) return $"ชนิดช่อง \"{f.Type}\" ไม่รู้จัก (ได้: {string.Join(" · ", FieldTypes)})";
            if (!AppliesToValues.Contains(f.AppliesTo)) return $"ช่อง \"{f.Label}\": ใช้กับ \"{f.AppliesTo}\" ไม่รู้จัก (ได้: all · delivery · pickup)";
            if (!Enum.IsDefined(f.Requirement)) return $"ช่อง \"{f.Label}\": ค่าบังคับไม่ถูกต้อง";
            if (f.MaxLength is int ml && (ml < 1 || ml > MaxTextLength)) return $"ช่อง \"{f.Label}\": ความยาวสูงสุดต้องอยู่ระหว่าง 1–{MaxTextLength}";
            if (f.Type == "select")
            {
                var opts = f.Options ?? Array.Empty<string>();
                if (opts.Count is 0 or > MaxOptions) return $"ช่อง \"{f.Label}\": ตัวเลือกต้องมี 1–{MaxOptions} ข้อ";
                if (opts.Any(o => o.Length > 100)) return $"ช่อง \"{f.Label}\": ตัวเลือกยาวเกิน 100 ตัวอักษร";
                if (opts.Distinct(StringComparer.Ordinal).Count() != opts.Count) return $"ช่อง \"{f.Label}\": ตัวเลือกซ้ำ";
            }
        }
        return null;
    }

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>ช่องเพิ่มเองข้อนี้ใช้กับวิธีรับนี้ไหม</summary>
    public static bool Applies(OrderCustomField f, string fulfillment)
        => f.AppliesTo switch
        {
            "delivery" => fulfillment == FulfillmentMethod.Delivery,
            "pickup" => fulfillment == FulfillmentMethod.Pickup,
            _ => true,
        };

    /// <summary>ช่องเพิ่มเองที่ลูกค้าเห็น/กรอกได้กับวิธีรับนี้ (ไม่ซ่อน + ใช้กับวิธีรับ) — ตัวเดียวที่ด่านตรวจและหน้าเว็บ (ผ่าน checkout-quote) ใช้</summary>
    public static IReadOnlyList<OrderCustomField> ApplicableFields(OrderFormFieldsConfig? c, string fulfillment)
        => (c == null ? Array.Empty<OrderCustomField>() : Normalize(c).CustomFields ?? Array.Empty<OrderCustomField>())
            .Where(f => f.Requirement != BookingFieldRequirement.Hidden && Applies(f, fulfillment)).ToList();

    /// <summary>ตรวจฟอร์มของลูกค้า — คืนข้อความผิดทั้งหมด (ว่าง = ผ่าน) + ค่าที่ทำความสะอาดแล้ว ·
    /// <paramref name="configured"/> null = ร้านไม่ได้ตั้งค่า (บังคับขั้นต่ำเท่านั้น)</summary>
    public static (IReadOnlyList<string> Errors, OrderFormCleaned Values) Check(OrderFormFieldsConfig? configured, OrderFormInput input, string fulfillment)
    {
        var errs = new List<string>();
        var c = configured == null ? null : Normalize(configured);
        var phoneReq = c?.Phone ?? BookingFieldRequirement.Optional;
        var emailReq = c?.Email ?? BookingFieldRequirement.Optional;
        var notesReq = c?.Notes ?? BookingFieldRequirement.Optional;

        var name = T(input.Name);
        var phone = phoneReq == BookingFieldRequirement.Hidden ? null : T(input.Phone);
        var email = emailReq == BookingFieldRequirement.Hidden ? null : T(input.Email);
        var notes = notesReq == BookingFieldRequirement.Hidden ? null : T(input.Notes);
        var address = T(input.Address);

        if (name == null) errs.Add("กรุณากรอกชื่อ-นามสกุล");
        else if (name.Length > 200) errs.Add("ชื่อยาวเกิน 200 ตัวอักษร");
        if (phoneReq == BookingFieldRequirement.Required && phone == null) errs.Add("กรุณากรอกเบอร์โทร");
        if (emailReq == BookingFieldRequirement.Required && email == null) errs.Add("กรุณากรอกอีเมล");
        if (phone == null && email == null) errs.Add("กรุณากรอกเบอร์โทรหรืออีเมลอย่างน้อย 1 ช่อง — ร้านต้องติดต่อกลับได้");
        if (phone != null && phone.Count(char.IsDigit) < 6) errs.Add("เบอร์โทรไม่ถูกต้อง");
        if (email != null && !LooksLikeEmail(email)) errs.Add("อีเมลไม่ถูกต้อง");
        if (notesReq == BookingFieldRequirement.Required && notes == null) errs.Add("กรุณากรอกหมายเหตุ/รายละเอียดเพิ่มเติม");
        if (notes != null && notes.Length > MaxNotesLength) errs.Add($"หมายเหตุยาวเกิน {MaxNotesLength} ตัวอักษร");
        if (fulfillment == FulfillmentMethod.Delivery && address == null) errs.Add("กรุณากรอกที่อยู่จัดส่ง");
        if (fulfillment == FulfillmentMethod.Pickup) address ??= FulfillmentMethod.Label(FulfillmentMethod.Pickup);

        // §86/4 — ผู้ซื้อที่ขอใบกำกับเต็มรูป: ชื่อ · ที่อยู่ · เลขผู้เสียภาษี (checksum ตัวกลาง) · สาขา 5 หลัก — ตั้งปิดไม่ได้
        string? bName = null, bAddr = null, bTax = null, bBranch = null;
        if (input.RequestTaxInvoice)
        {
            bName = T(input.BillingName);
            bAddr = T(input.BillingAddress);
            var taxDigits = ThaiTaxId.Normalize(input.BillingTaxId);
            var branchRaw = T(input.BillingBranchCode);
            if (bName == null) errs.Add("ขอใบกำกับภาษี: กรุณากรอกชื่อผู้ซื้อ (ชื่อบริษัท/ชื่อ-นามสกุล)");
            if (bAddr == null) errs.Add("ขอใบกำกับภาษี: กรุณากรอกที่อยู่ผู้ซื้อ");
            if (taxDigits.Length == 0) errs.Add("ขอใบกำกับภาษี: กรุณากรอกเลขประจำตัวผู้เสียภาษี 13 หลัก");
            else if (!ThaiTaxId.IsValid(taxDigits)) errs.Add("ขอใบกำกับภาษี: เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง (ต้องเป็นเลข 13 หลักที่ตรวจสอบหลักสุดท้ายผ่าน)");
            if (branchRaw == null) errs.Add("ขอใบกำกับภาษี: กรุณากรอกรหัสสาขา 5 หลัก (สำนักงานใหญ่ = 00000)");
            else if (!IsBranchCode(branchRaw)) errs.Add("ขอใบกำกับภาษี: รหัสสาขาต้องเป็นตัวเลข 5 หลัก (สำนักงานใหญ่ = 00000)");
            bTax = taxDigits.Length == 0 ? null : taxDigits;
            bBranch = branchRaw;
        }

        // ช่องเพิ่มเองของร้าน (เฉพาะที่ใช้กับวิธีรับนี้ · ซ่อน = ไม่เก็บ)
        var answers = new List<OrderCustomAnswer>();
        foreach (var f in ApplicableFields(c, fulfillment))
        {
            string? v = null;
            input.Custom?.TryGetValue(f.Key, out v);
            v = T(v);
            if (f.Type == "checkbox")
            {
                var on = v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase));
                if (f.Requirement == BookingFieldRequirement.Required && !on) errs.Add($"กรุณาติ๊ก \"{f.Label}\"");
                if (on) answers.Add(new OrderCustomAnswer(f.Key, f.Label, "ใช่"));
                continue;
            }
            if (v == null)
            {
                if (f.Requirement == BookingFieldRequirement.Required) errs.Add($"กรุณากรอก \"{f.Label}\"");
                continue;
            }
            var max = f.MaxLength ?? MaxTextLength;
            if (v.Length > max) { errs.Add($"\"{f.Label}\" ยาวเกิน {max} ตัวอักษร"); continue; }
            if (f.Type == "select" && !(f.Options ?? Array.Empty<string>()).Contains(v, StringComparer.Ordinal))
            { errs.Add($"\"{f.Label}\": ตัวเลือกไม่ถูกต้อง"); continue; }
            if (f.Type == "date")
            {
                if (!DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                { errs.Add($"\"{f.Label}\": วันที่ไม่ถูกต้อง"); continue; }
                v = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            answers.Add(new OrderCustomAnswer(f.Key, f.Label, v));
        }

        var customJson = answers.Count == 0 ? null : JsonSerializer.Serialize(answers, Json);
        return (errs, new OrderFormCleaned(name ?? "", phone, email, address, notes, bName, bAddr, bTax, bBranch, customJson));
    }

    /// <summary>รหัสสาขา 5 หลักตามประกาศอธิบดีฯ 199 (regex <c>^[0-9]{5}$</c> · ASCII เท่านั้น)</summary>
    private static bool IsBranchCode(string? s) => s is { Length: 5 } && s.All(ch => ch is >= '0' and <= '9');

    /// <summary>คำตอบช่องเพิ่มเองที่ตรึงบนออเดอร์ — อ่านไม่ออก = ว่าง (ไม่แต่ง)</summary>
    public static IReadOnlyList<OrderCustomAnswer> ParseAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OrderCustomAnswer>();
        try { return JsonSerializer.Deserialize<List<OrderCustomAnswer>>(json, Json) ?? new List<OrderCustomAnswer>(); }
        catch (JsonException) { return Array.Empty<OrderCustomAnswer>(); }
    }

    /// <summary>หมายเหตุบนเอกสารขายของออเดอร์ — เดิม "Online order #เลข" ทุกตัวอักษร · มีวันที่ต้องการรับ/ช่องเพิ่มเอง ⇒ ต่อท้ายบรรทัดเดียว
    /// (ตัวสรุปเดียว — หลังบ้าน/เอกสารเห็นข้อความเดียวกัน · ค่าเป็นข้อความล้วน renderer เป็นผู้ encode)</summary>
    public static string DocumentNotes(string orderNumber, DateTime? requestedReadyDate, string? customFieldsJson)
    {
        var parts = new List<string>();
        if (requestedReadyDate is DateTime rd) parts.Add($"วันที่ต้องการรับ {ThaiDate.CalendarDateUtc(rd):dd/MM/yyyy}");
        parts.AddRange(ParseAnswers(customFieldsJson).Select(a => $"{a.Label}: {a.Value}"));
        return parts.Count == 0 ? $"Online order #{orderNumber}" : $"Online order #{orderNumber} · {string.Join(" · ", parts)}";
    }

    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at == s.LastIndexOf('@') && s.IndexOf('.', at) > at + 1 && !s.EndsWith('.') && !s.Any(char.IsWhiteSpace);
    }
}
