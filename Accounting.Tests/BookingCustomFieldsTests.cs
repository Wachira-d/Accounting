using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 222 — ช่องที่เจ้าของเพิ่มเองบนฟอร์มจอง (คำขอเจ้าของ "เพิ่มฟิลด์ให้ลูกค้ากรอก หรือเลือก dropdown ตามที่ตั้งค่า") ·
/// กติกากลาง <see cref="CustomFormFields"/> ใช้ร่วมกับฟอร์มสั่งซื้อ · ล็อกทั้ง "ของใหม่ทำงาน" และ "ค่าเดิม/ฟอร์มสั่งซื้อไม่ถูกแตะ"
/// </summary>
public class BookingCustomFieldsTests
{
    private static readonly Guid Hair = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid Nail = Guid.Parse("40000000-0000-0000-0000-000000000002");

    private static BookingFormFieldsConfig Cfg() => new(CustomFields: new[]
    {
        new BookingCustomField("hair_len", "ความยาวผม", Type: "select", Options: new[] { "สั้น", "กลาง", "ยาว" },
            Requirement: BookingFieldRequirement.Required, ServiceIds: new[] { Hair }),
        new BookingCustomField("allergy", "แพ้ยา/สารเคมี", Type: "textarea", MaxLength: 20),
        new BookingCustomField("kids", "จำนวนเด็ก", Type: "number"),
        new BookingCustomField("agree", "ยอมรับว่ามาก่อนเวลา 10 นาที", Type: "checkbox", Requirement: BookingFieldRequirement.Required),
        new BookingCustomField("internal", "ช่องปิดไว้", Requirement: BookingFieldRequirement.Hidden),
    });

    [Fact]
    public void Default_config_has_no_custom_fields_and_round_trips()
    {
        // ค่าเดิม (JSON null / ค่าตั้งก่อนรอบ 222) = ไม่มีช่องเพิ่ม · ไม่มีคำตอบ · JSON เดิมอ่านได้ทุกตัว
        Assert.Empty(BookingFormFields.FieldsForService(BookingFormFields.Parse(null), Hair));
        Assert.Empty(BookingFormFields.CheckCustom(BookingFormFields.Parse(null), Hair, null, byStaff: false).Errors);
        Assert.Null(BookingFormFields.CheckCustom(BookingFormFields.Parse(null), Hair, null, byStaff: false).AnswersJson);
        var old = BookingFormFields.Parse("{\"phone\":\"Required\",\"email\":\"Hidden\",\"notes\":\"Optional\"}");
        Assert.Equal(BookingFieldRequirement.Hidden, old.Email);
        Assert.Null(old.CustomFields);

        var rt = BookingFormFields.Parse(BookingFormFields.Serialize(Cfg()));
        Assert.Equal(5, rt.CustomFields!.Count);
        Assert.Equal(new[] { Hair }, rt.CustomFields[0].ServiceIds);
        Assert.Equal(new[] { "สั้น", "กลาง", "ยาว" }, rt.CustomFields[0].Options);
    }

    [Fact]
    public void Fields_scoped_per_service_and_hidden_not_shown()
    {
        Assert.Equal(new[] { "hair_len", "allergy", "kids", "agree" }, BookingFormFields.FieldsForService(Cfg(), Hair).Select(f => f.Key));
        // บริการอื่น ⇒ ไม่เห็นช่องเฉพาะบริการทำผม · ช่อง "ไม่แสดง" ไม่ส่งถึงหน้าเว็บ
        Assert.Equal(new[] { "allergy", "kids", "agree" }, BookingFormFields.FieldsForService(Cfg(), Nail).Select(f => f.Key));
    }

    [Fact]
    public void Customer_answers_validated_required_option_length_number()
    {
        var bad = BookingFormFields.CheckCustom(Cfg(), Hair, new Dictionary<string, string?>
        {
            ["hair_len"] = "ยาวมาก", ["allergy"] = new string('ก', 21), ["kids"] = "สอง",
        }, byStaff: false);
        Assert.Contains(bad.Errors, e => e.Contains("ความยาวผม") && e.Contains("ตัวเลือก"));
        Assert.Contains(bad.Errors, e => e.Contains("แพ้ยา") && e.Contains("ยาวเกิน 20"));
        Assert.Contains(bad.Errors, e => e.Contains("จำนวนเด็ก") && e.Contains("ตัวเลข"));
        Assert.Contains(bad.Errors, e => e.Contains("กรุณาติ๊ก"));

        var missing = BookingFormFields.CheckCustom(Cfg(), Hair, null, byStaff: false);
        Assert.Contains(missing.Errors, e => e.Contains("กรุณากรอก \"ความยาวผม\""));

        var ok = BookingFormFields.CheckCustom(Cfg(), Hair, new Dictionary<string, string?>
        {
            ["hair_len"] = "ยาว", ["allergy"] = "  ไม่มี ", ["kids"] = "1,000", ["agree"] = "true",
            ["internal"] = "ลูกค้าส่งช่องที่ปิดมาเอง", ["unknown"] = "x",
        }, byStaff: false);
        Assert.Empty(ok.Errors);
        var answers = CustomFormFields.ParseAnswers(ok.AnswersJson);
        Assert.Equal(new[] { "hair_len", "allergy", "kids", "agree" }, answers.Select(a => a.Key));   // ช่องปิด/คีย์แปลกไม่ถูกเก็บ
        Assert.Contains(answers, a => a.Label == "แพ้ยา/สารเคมี" && a.Value == "ไม่มี");
        Assert.Contains(answers, a => a.Key == "kids" && a.Value == "1000");
        Assert.Contains(answers, a => a.Key == "agree" && a.Value == "ใช่");
        Assert.Equal("ความยาวผม: ยาว · แพ้ยา/สารเคมี: ไม่มี · จำนวนเด็ก: 1000 · ยอมรับว่ามาก่อนเวลา 10 นาที: ใช่", CustomFormFields.SummaryLine(ok.AnswersJson));
    }

    [Fact]
    public void Staff_not_blocked_by_required_but_values_still_checked()
    {
        // ทิศตรงข้าม: พนักงานจองแทน (walk-in) ไม่ต้องตอบช่องบังคับ — แต่ค่าที่ส่งต้องถูกรูป
        Assert.Empty(BookingFormFields.CheckCustom(Cfg(), Hair, null, byStaff: true).Errors);
        Assert.Null(BookingFormFields.CheckCustom(Cfg(), Hair, null, byStaff: true).AnswersJson);
        var wrong = BookingFormFields.CheckCustom(Cfg(), Hair, new Dictionary<string, string?> { ["hair_len"] = "ไม่มีในรายการ" }, byStaff: true);
        Assert.Single(wrong.Errors);
    }

    [Fact]
    public void Config_validation_shared_with_order_form()
    {
        Assert.Null(BookingFormFields.ValidateConfig(Cfg(), new[] { Hair, Nail }));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: new[] { new BookingCustomField("bad key", "x") })));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: new[] { new BookingCustomField("a", "x"), new BookingCustomField("A", "y") })));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: new[] { new BookingCustomField("s", "ขนาด", Type: "select") })));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: new[] { new BookingCustomField("s", "") })));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: new[] { new BookingCustomField("s", "x", Type: "color") })));
        Assert.NotNull(BookingFormFields.ValidateConfig(new(CustomFields: Enumerable.Range(1, 11).Select(i => new BookingCustomField("f" + i, "x")).ToList())));
        // บริการที่ไม่มีในเว็บ (ถูกลบ/ของเว็บอื่น) ⇒ ข้อความบอกให้เลือกใหม่ · ไม่ส่งรายการบริการ (คงรายการเดิม) ⇒ ไม่ตรวจขอบเขต
        Assert.Contains("บริการที่เลือก", BookingFormFields.ValidateConfig(Cfg(), new[] { Nail })!);
        Assert.Null(BookingFormFields.ValidateConfig(Cfg()));
        // ซ่อนทั้งเบอร์และอีเมล ยังห้ามเหมือนเดิม
        Assert.NotNull(BookingFormFields.ValidateConfig(new(BookingFieldRequirement.Hidden, BookingFieldRequirement.Hidden)));
        // ชนิด "ตัวเลข" ใช้ได้กับฟอร์มสั่งซื้อด้วย (ตัวตั้งเดียว)
        Assert.Contains("number", OrderFormFields.FieldTypes);
        Assert.Null(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("qty", "จำนวนชิ้น", Type: "number") })));
        Assert.Equal(CustomFormFields.FieldTypes, CustomFormFields.TypeOptions().Select(o => o.Value));
    }
}
