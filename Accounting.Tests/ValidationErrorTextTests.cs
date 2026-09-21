using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ไม่มีบอกว่า Require อันไหน หรือ ขาดอะไร อันไหน" — คำร้องของผู้ใช้ 2026-09-21
/// หลังกด "💾 บันทึกการตั้งค่า" ที่หน้าตั้งค่าที่พักแล้วได้ toast แดง:
///   <c>One or more validation errors occurred. — Code: The Code field is required.</c>
///
/// เทสต์สองครึ่ง (F2 ข้อ 8):
///   • ครึ่งแรก — เคสที่ผู้ใช้เจอต้องกลายเป็นข้อความไทยที่ระบุช่องได้
///   • ครึ่งหลัง — ข้อความที่ถูกอยู่แล้ว (ไทยจาก [Required(ErrorMessage=…)] ของเราเอง)
///     ต้อง **ไม่ถูกแตะ** และข้อความอังกฤษที่แปลไม่ได้ต้อง **ไม่ถูกกลืน**
/// </summary>
public class ValidationErrorTextTests
{
    private static ValidationErrorSummary Describe(params (string, string)[] pairs)
        => ValidationErrorText.Describe(
            pairs.Select(p => (p.Item1, (IEnumerable<string>)new[] { p.Item2 })));

    // ═══════════ ครึ่งที่ 1 — เคสที่ผู้ใช้รายงาน ═══════════

    [Fact]
    public void เคสจริงของผู้ใช้_The_Code_field_is_required_ต้องกลายเป็นไทยและระบุช่อง()
    {
        var s = Describe(("Code", "The Code field is required."));

        // ชื่อช่องต้องเป็นรูปที่ฟอร์มใช้ (name="code") ไม่ใช่ชื่อ property C#
        Assert.Equal(new[] { "code" }, s.Fields);
        // ข้อความต้องไม่เหลือภาษาอังกฤษของ framework
        Assert.DoesNotContain("field is required", s.Message);
        Assert.DoesNotContain("One or more validation errors", s.Message);
        // ต้องมีอักษรไทย (ทั้งระบบเป็นไทยตาม CLAUDE.md "ภาษา")
        Assert.True(s.Message.Any(c => c is >= '฀' and <= '๿'));
        // ต้องบอกชื่อช่อง — ไม่ใช่ "มีข้อผิดพลาด" ลอย ๆ
        Assert.Contains("code", s.Message);
        Assert.Contains("เว้นว่างไม่ได้", s.Errors[0]);
    }

    [Fact]
    public void ข้อความต้องบอกทางไปต่อเมื่อหาช่องบนหน้าจอไม่เจอ()
    {
        // G3 "ไม่รู้ต้องบอกว่าไม่รู้" — ถ้าช่องนั้นไม่มีบนหน้า ผู้ใช้ต้องรู้ว่า
        // ไม่ต้องตามหา ไม่ใช่นั่งไล่ฟอร์มทั้งหน้า
        var s = Describe(("SomethingInvisible", "The SomethingInvisible field is required."));
        Assert.Contains("ถ้าไม่พบช่องนั้นบนหน้าจอ", s.Message);
        Assert.Contains("แจ้งผู้ดูแลระบบ", s.Message);
    }

    [Theory]
    [InlineData("Code", "code")]
    [InlineData("$.code", "code")]
    [InlineData("$.lines[0].unitPrice", "lines[0].unitPrice")]
    [InlineData("BuyerTaxId", "buyerTaxId")]
    [InlineData("URL", "url")]
    [InlineData("URLValue", "urlValue")]
    [InlineData("code", "code")]
    public void คีย์ทุกทรงต้องถูกทำให้ตรงกับ_name_บนฟอร์ม(string key, string expected)
    {
        // ต้องเป็นอัลกอริทึมเดียวกับ JsonNamingPolicy.CamelCase ที่ Program.cs ตั้งไว้
        // มิฉะนั้นหน้าเว็บ querySelector('[name=...]') ไม่เจอ แล้วข้อความจะกลาย
        // เป็น "ไม่มีช่องนี้บนหน้านี้" ทั้งที่มีอยู่จริง
        // ยิงผ่าน Describe (เส้นที่ระบบเดินจริง) ไม่ใช่ยิงตรงเข้าขั้นตอนภายใน —
        // เทสต์ที่ยิงตรงจะผ่านได้แม้ Describe ไม่เคยเรียกขั้นตอนนั้นเลย
        Assert.Equal(new[] { expected }, Describe((key, "The field is required.")).Fields);
    }

    [Fact]
    public void หลายช่องพร้อมกันต้องขึ้นครบทุกช่องไม่ใช่ช่องแรกช่องเดียว()
    {
        var s = Describe(
            ("Name", "The Name field is required."),
            ("Code", "The Code field is required."),
            ("StarRating", "The value 'abc' is not valid for StarRating."));
        Assert.Equal(new[] { "name", "code", "starRating" }, s.Fields);
        Assert.Equal(3, s.Errors.Count);
        Assert.Contains("3 ช่อง", s.Message);
        Assert.Contains("ผิดชนิด", s.Errors[2]);
    }

    [Fact]
    public void คีย์ซ้ำต้องนับเป็นช่องเดียว()
    {
        // ModelState ใส่ได้หลาย error ต่อคีย์ — ถ้าไม่ยุบ ผู้ใช้จะเห็น "ต้องแก้ 3 ช่อง"
        // ทั้งที่มีช่องเดียว (ตัวเลขที่ผิด = ข้อความที่เชื่อไม่ได้)
        var s = ValidationErrorText.Describe(new (string, IEnumerable<string>)[]
        {
            ("Code", new[] { "The Code field is required." }),
            ("Code", new[] { "The Code field is required." }),
        });
        Assert.Single(s.Fields);
        Assert.Contains("1 ช่อง", s.Message);
    }

    [Fact]
    public void เคสจริงที่สอง_body_แปลงไม่ผ่าน_ต้องรายงานเฉพาะช่องที่ผิดจริง_ไม่ใช่ผลพวง()
    {
        // ผู้ใช้ส่งภาพ 2026-09-21 (สร้างประเภทห้อง "Nordic Tent") — ช่อง "เตียงเสริมสูงสุด"
        // ว่าง ⇒ หน้าเว็บส่ง `maxExtraBeds: null` ⇒ System.Text.Json แปลงเข้า `int`
        // ไม่ได้ ⇒ โยน body ทิ้ง ⇒ พารามิเตอร์ของ action เป็น null ⇒ MVC เติม error
        // ตัวที่สอง "The dto field is required." ซึ่ง **ไม่มีช่องชื่อ dto บนหน้าจอ**
        // ให้ผู้ใช้แก้เลย — ตัวจริงมีตัวเดียวคือ maxExtraBeds
        var s = Describe(
            ("dto", "The dto field is required."),
            ("$.maxExtraBeds", "The JSON value could not be converted to System.Int32. "
                + "Path: $.maxExtraBeds | LineNumber: 0 | BytePositionInLine: 248."));

        Assert.Equal(new[] { "maxExtraBeds" }, s.Fields);
        Assert.DoesNotContain("dto", s.Message);
        Assert.Contains("1 ช่อง", s.Message);
        Assert.Contains("ผิดชนิด", s.Errors[0]);
    }

    [Fact]
    public void ผลพวงถูกตัดเฉพาะตอนมี_error_ระดับ_JSON_path_เท่านั้น()
    {
        // ไม่มี `$.` = body แปลงผ่าน ⇒ "required" ที่เหลือเป็นของจริง ห้ามตัด
        var s = Describe(("Name", "The Name field is required."));
        Assert.Equal(new[] { "name" }, s.Fields);
    }

    [Fact]
    public void มี_JSON_path_แล้ว_error_อื่นที่ไม่ใช่_required_ต้องยังอยู่()
    {
        // ตัดเฉพาะ "required" ที่เป็นผลพวง — ข้อความชนิดอื่นไม่ใช่ผลพวง ห้ามกลืน
        var s = Describe(
            ("$.maxExtraBeds", "The JSON value could not be converted to System.Int32."),
            ("SomeOther", "The field SomeOther must be between 1 and 5."));
        Assert.Equal(new[] { "maxExtraBeds", "someOther" }, s.Fields);
    }

    // ═══════════ ครึ่งที่ 2 — ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ ═══════════

    [Fact]
    public void ข้อความไทยของเราเองต้องผ่านมาเหมือนเดิมทุกตัวอักษร()
    {
        const string ours = "กรุณาระบุเลขประจำตัวผู้เสียภาษี 13 หลัก";
        var s = Describe(("BuyerTaxId", ours));
        Assert.Equal(ours, s.Errors[0]);      // ต้องเท่ากันเป๊ะ ไม่ใช่แค่ "มีคำนี้อยู่"
        Assert.Contains(ours, s.Message);
    }

    [Fact]
    public void ข้อความอังกฤษที่แปลไม่ได้ต้องไม่ถูกกลืน_ต้องติดไปให้เห็น()
    {
        // F2 ข้อ 3 "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ" — แปลไม่ได้ให้ส่งต้นฉบับไปด้วย
        var s = Describe(("Foo", "The field Foo must be a string with a maximum length of 8."));
        Assert.Contains("maximum length of 8", s.Errors[0]);
        Assert.Contains("ระบบแจ้งว่า", s.Errors[0]);
    }

    [Fact]
    public void ModelState_ที่ไม่มีคีย์เลย_ต้องไม่โกหกว่ารู้ว่าช่องไหน()
    {
        // body ทั้งก้อน parse ไม่ผ่าน (คีย์เป็น "$") — ตอบว่าไม่รู้ช่อง ดีกว่าเดาช่อง
        var s = Describe(("$", "The JSON value could not be converted."));
        Assert.Empty(s.Fields);
        Assert.Contains("ไม่ได้ระบุว่าเป็นช่องไหน", s.Message);
    }

    [Fact]
    public void ไม่มี_error_เลย_ต้องไม่พังและไม่แต่งชื่อช่อง()
    {
        var s = ValidationErrorText.Describe(Array.Empty<(string, IEnumerable<string>)>());
        Assert.Empty(s.Fields);
        Assert.False(string.IsNullOrWhiteSpace(s.Message));
    }
}
