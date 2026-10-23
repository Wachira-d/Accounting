using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวเลือกสินค้าตามสั่ง (dynamic option builder) — server ตรวจ + คิดราคาเอง ห้ามเชื่อราคาจาก client
/// ครึ่งแรก: กลุ่มเดิม (ก่อนมีชนิดช่อง) ต้องทำงานเหมือนเดิม · ครึ่งหลัง: ชนิดใหม่ + จำนวนเลือก min–max + ราคา/วันเพิ่ม
/// </summary>
public class ModifierSelectionRulesTests
{
    private static readonly Guid GSize = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid GTop = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid GText = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid GImg = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid S1 = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid S2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid T1 = Guid.Parse("20000000-0000-0000-0000-000000000011");
    private static readonly Guid T2 = Guid.Parse("20000000-0000-0000-0000-000000000012");
    private static readonly Guid T3 = Guid.Parse("20000000-0000-0000-0000-000000000013");
    private static readonly Guid TOff = Guid.Parse("20000000-0000-0000-0000-000000000019");

    private static ModifierGroupSpec Size(bool required = true) => new(GSize, "ขนาด", ModifierInputType.Radio, required,
        null, null, null, 0m, 0, new[]
        {
            new ModifierOptionSpec(S1, "1 ปอนด์", 0m, 0, true),
            new ModifierOptionSpec(S2, "2 ปอนด์", 350m, 1, true),
        });

    private static ModifierGroupSpec Toppings(int? min = null, int? max = 2) => new(GTop, "ท็อปปิ้ง", ModifierInputType.Checkbox, false,
        min, max, null, 0m, 0, new[]
        {
            new ModifierOptionSpec(T1, "สตรอว์เบอร์รี", 60m, 0, true),
            new ModifierOptionSpec(T2, "ช็อกโกแลต", 40m, 0, true),
            new ModifierOptionSpec(T3, "ทองคำเปลว", 200m, 2, true),
            new ModifierOptionSpec(TOff, "ผลไม้ตามฤดู", 30m, 0, false),
        });

    private static ModifierGroupSpec Message(bool required = false) => new(GText, "ข้อความบนเค้ก", ModifierInputType.Text, required,
        null, null, 20, 50m, 0, Array.Empty<ModifierOptionSpec>());

    private static ModifierGroupSpec Photo(bool required = false) => new(GImg, "รูปตัวอย่าง", ModifierInputType.Image, required,
        null, null, null, 120m, 1, Array.Empty<ModifierOptionSpec>());

    private static ModifierChoice Pick(Guid g, params Guid[] ids) => new(g, ids, null, null);

    [Fact]
    public void ชนิดช่องเดิมว่าง_อนุมานจากธงAllowMultiple_กลุ่มเดิมไม่เปลี่ยนพฤติกรรม()
    {
        Assert.Equal(ModifierInputType.Radio, ModifierInputType.Resolve(null, allowMultiple: false));
        Assert.Equal(ModifierInputType.Checkbox, ModifierInputType.Resolve("", allowMultiple: true));
        Assert.Equal(ModifierInputType.Dropdown, ModifierInputType.Resolve("Dropdown", allowMultiple: true));
        Assert.Null(ModifierInputType.Validate("  "));
        Assert.Throws<BusinessRuleException>(() => ModifierInputType.Validate("slider"));
    }

    [Fact]
    public void ราคาและวันผลิตเพิ่มมาจากฐานข้อมูล_ไม่ใช่จากclient()
    {
        var r = ModifierSelectionRules.Evaluate(new[] { Size(), Toppings() },
            new[] { Pick(GSize, S2), Pick(GTop, T1, T3) });
        Assert.True(r.IsValid);
        Assert.Equal(350m + 60m + 200m, r.PriceAdjustmentTotal);
        Assert.Equal(1 + 2, r.ExtraLeadDays);                       // วันผลิตเพิ่ม = ผลรวม
        Assert.Equal(new[] { "2 ปอนด์", "สตรอว์เบอร์รี", "ทองคำเปลว" }, r.Lines.Select(l => l.Label));
    }

    [Fact]
    public void บังคับเลือกแต่ไม่เลือก_ผิด()
    {
        var r = ModifierSelectionRules.Evaluate(new[] { Size() }, null);
        Assert.False(r.IsValid);
        Assert.Contains("ขนาด", r.Errors[0]);
    }

    [Fact]
    public void radio_เลือกเกิน1_ผิด()
    {
        var r = ModifierSelectionRules.Evaluate(new[] { Size() }, new[] { Pick(GSize, S1, S2) });
        Assert.False(r.IsValid);
    }

    [Fact]
    public void checkbox_เกินจำนวนสูงสุด_และน้อยกว่าขั้นต่ำ_ผิด()
    {
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Toppings(max: 2) }, new[] { Pick(GTop, T1, T2, T3) }).IsValid);
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Toppings(min: 2, max: 3) }, new[] { Pick(GTop, T1) }).IsValid);
        Assert.True(ModifierSelectionRules.Evaluate(new[] { Toppings(min: 2, max: 3) }, new[] { Pick(GTop, T1, T2) }).IsValid);
    }

    [Fact]
    public void ตัวเลือกปิดใช้_หรือไม่อยู่ในกลุ่ม_หรือกลุ่มไม่อยู่ในสินค้า_ผิดไม่ข้ามเงียบ()
    {
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Toppings() }, new[] { Pick(GTop, TOff) }).IsValid);
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Toppings() }, new[] { Pick(GTop, S1) }).IsValid);
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Toppings() }, new[] { Pick(GSize, S1) }).IsValid);
    }

    [Fact]
    public void ข้อความอิสระ_กรอกแล้วบวกราคา_ยาวเกินผิด_ไม่กรอกไม่บวก()
    {
        var ok = ModifierSelectionRules.Evaluate(new[] { Message() }, new[] { new ModifierChoice(GText, null, " HBD แม่ ", null) });
        Assert.True(ok.IsValid);
        Assert.Equal(50m, ok.PriceAdjustmentTotal);
        Assert.Equal("HBD แม่", ok.Lines[0].Text);

        Assert.False(ModifierSelectionRules.Evaluate(new[] { Message() },
            new[] { new ModifierChoice(GText, null, new string('ก', 21), null) }).IsValid);

        var empty = ModifierSelectionRules.Evaluate(new[] { Message() }, null);
        Assert.True(empty.IsValid);
        Assert.Equal(0m, empty.PriceAdjustmentTotal);

        Assert.False(ModifierSelectionRules.Evaluate(new[] { Message(required: true) }, null).IsValid);
    }

    [Fact]
    public void รูปตัวอย่าง_แนบแล้วบวกราคาและวัน_บังคับแต่ไม่แนบผิด()
    {
        var att = Guid.NewGuid();
        var ok = ModifierSelectionRules.Evaluate(new[] { Photo() }, new[] { new ModifierChoice(GImg, null, null, att) });
        Assert.Equal(120m, ok.PriceAdjustmentTotal);
        Assert.Equal(1, ok.ExtraLeadDays);
        Assert.Equal(att, ok.Lines[0].AttachmentId);
        Assert.False(ModifierSelectionRules.Evaluate(new[] { Photo(required: true) }, null).IsValid);
    }

    [Fact]
    public void ส่งกลุ่มเดียวกันซ้ำ_ผิด()
        => Assert.False(ModifierSelectionRules.Evaluate(new[] { Size() }, new[] { Pick(GSize, S1), Pick(GSize, S2) }).IsValid);

    [Fact]
    public void ค่าตั้งกลุ่ม_ขั้นต่ำเกินสูงสุด_หรือradioเลือกเกิน1_ถูกกันตั้งแต่ตอนตั้ง()
    {
        Assert.Throws<BusinessRuleException>(() => ModifierSelectionRules.ValidateGroupConfig(ModifierInputType.Checkbox, 3, 2, null, 0));
        Assert.Throws<BusinessRuleException>(() => ModifierSelectionRules.ValidateGroupConfig(ModifierInputType.Radio, null, 2, null, 0));
        Assert.Throws<BusinessRuleException>(() => ModifierSelectionRules.ValidateGroupConfig(ModifierInputType.Text, null, null, 0, 0));
        Assert.Throws<BusinessRuleException>(() => ModifierSelectionRules.ValidateGroupConfig(ModifierInputType.Text, null, null, 50, -1));
        ModifierSelectionRules.ValidateGroupConfig(ModifierInputType.Checkbox, 1, 3, null, 2);   // ไม่โยน
    }

    [Fact]
    public void คำอธิบายบรรทัด_อ่านได้บนสลิป()
    {
        var r = ModifierSelectionRules.Evaluate(new[] { Size(), Message() },
            new[] { Pick(GSize, S1), new ModifierChoice(GText, null, "สุขสันต์", null) });
        Assert.Equal("ขนาด: 1 ปอนด์ · ข้อความบนเค้ก: สุขสันต์", ModifierSelectionRules.Describe(r.Lines));
    }
}
