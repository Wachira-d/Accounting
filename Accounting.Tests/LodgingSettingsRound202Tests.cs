using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 202 ทีม LS — หน้าตั้งค่าที่พัก: ด่านที่เดิม "แก้ค่าให้เงียบ ๆ" ต้องปฏิเสธพร้อมป้ายบนจอ และของที่ถูกอยู่แล้วต้องไม่ถูกแตะ (สองทิศ · F2 ข้อ 8)
/// <para>เวลาเข้า-ออก (S-P1-2 · ข้อ 116) · ช่วงคืน/วันที่ (S-P2-7) · เตียงเสริม (ข้อ 123) · สถานะเปิดจองออนไลน์ (W-01) · เงื่อนไขนอกเวลา (ข้อ 121)</para>
/// </summary>
public class LodgingSettingsRound202Tests
{
    // ───────── เวลาเช็คอิน/เช็คเอาต์ ─────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void เวลาว่าง_ใช้ค่าเริ่มต้น_ไม่ปฏิเสธ(string? text)
    {
        Assert.Equal(new TimeOnly(14, 0), LodgingTimeOfDay.Parse(text, LodgingTimeOfDay.DefaultCheckIn, LodgingTimeOfDay.CheckInLabel));
        Assert.Equal(new TimeOnly(12, 0), LodgingTimeOfDay.Parse(text, LodgingTimeOfDay.DefaultCheckOut, LodgingTimeOfDay.CheckOutLabel));
    }

    [Theory]
    [InlineData("14:00", 14, 0)]
    [InlineData("9:30", 9, 30)]
    [InlineData("09:30", 9, 30)]
    [InlineData(" 15:45 ", 15, 45)]
    [InlineData("13:00:00", 13, 0)]   // <input type="time"> บางเบราว์เซอร์ส่งวินาทีมาด้วย
    public void เวลาที่อ่านได้_ได้ค่าที่ผู้ใช้กรอก_ไม่ถูกแทนด้วยค่าเริ่มต้น(string text, int h, int m)
        => Assert.Equal(new TimeOnly(h, m), LodgingTimeOfDay.Parse(text, LodgingTimeOfDay.DefaultCheckIn, LodgingTimeOfDay.CheckInLabel));

    [Theory]
    [InlineData("15.00")]
    [InlineData("25:00")]
    [InlineData("บ่ายสอง")]
    [InlineData("14")]
    public void เวลาไม่ว่างแต่อ่านไม่ได้_ปฏิเสธพร้อมชื่อช่อง_ไม่แทนเงียบ(string text)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => LodgingTimeOfDay.Parse(text, LodgingTimeOfDay.DefaultCheckOut, LodgingTimeOfDay.CheckOutLabel));
        Assert.Contains("เวลาเช็คเอาต์", ex.Message);
        Assert.Contains(text.Trim(), ex.Message);
        Assert.Equal("LODGING-TIME", ex.RuleCode);
    }

    [Fact]
    public void ค่าเริ่มต้นของเวลาเป็นชุดเดียวกับค่า_seed()
    {
        Assert.Equal(LodgingSeedDefaults.CheckIn, LodgingTimeOfDay.Format(LodgingTimeOfDay.DefaultCheckIn));
        Assert.Equal(LodgingSeedDefaults.CheckOut, LodgingTimeOfDay.Format(LodgingTimeOfDay.DefaultCheckOut));
    }

    // ───────── ช่วงคืน / ช่วงวันที่ ─────────

    [Fact]
    public void พักสูงสุดน้อยกว่าขั้นต่ำ_ปฏิเสธ_ไม่ยกค่าให้เอง()
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => LodgingSettingsRules.EnsureNightsRange(5, 3, "พักขั้นต่ำ (คืน)", "พักสูงสุด (คืน)"));
        Assert.Contains("«พักสูงสุด (คืน)» (3)", ex.Message);
        Assert.Contains("«พักขั้นต่ำ (คืน)» (5)", ex.Message);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(3, 3)]
    [InlineData(null, 2)]
    [InlineData(4, null)]
    [InlineData(null, null)]
    public void ช่วงคืนที่ถูกหรือว่าง_ผ่าน(int? min, int? max)
        => LodgingSettingsRules.EnsureNightsRange(min, max, "a", "b");

    [Fact]
    public void ช่วงวันที่ของแผนราคากลับหัว_ปฏิเสธ_ทิศถูกและวันเดียวกันผ่าน()
    {
        var a = new DateTime(2026, 12, 1); var b = new DateTime(2026, 11, 30);
        var ex = Assert.Throws<BusinessRuleException>(() => LodgingSettingsRules.EnsureDateRange(a, b, "ใช้ได้ตั้งแต่", "ถึง"));
        Assert.Contains("30/11/2569", ex.Message);
        LodgingSettingsRules.EnsureDateRange(b, a, "ใช้ได้ตั้งแต่", "ถึง");
        LodgingSettingsRules.EnsureDateRange(a, a, "ใช้ได้ตั้งแต่", "ถึง");
        LodgingSettingsRules.EnsureDateRange(null, b, "ใช้ได้ตั้งแต่", "ถึง");
    }

    // ───────── เตียงเสริม (คำตัดสินเจ้าของข้อ 123) ─────────

    [Fact]
    public void ติ๊กเตียงเสริมแต่จำนวนศูนย์_ปฏิเสธพร้อมป้ายช่อง()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => LodgingSettingsRules.NormalizeExtraBed(true, 0, 500m));
        Assert.Contains("เพิ่มได้สูงสุดกี่คน", ex.Message);
    }

    [Fact]
    public void ติ๊กเตียงเสริมแต่ราคาว่าง_ปฏิเสธ_ไม่ปล่อยให้ฟรีเงียบ()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => LodgingSettingsRules.NormalizeExtraBed(true, 2, null));
        Assert.Contains("ราคาต่อคน/คืน (บาท)", ex.Message);
    }

    [Fact]
    public void ราคาติดลบ_ปฏิเสธทั้งติ๊กและไม่ติ๊ก()
    {
        Assert.Throws<BusinessRuleException>(() => LodgingSettingsRules.NormalizeExtraBed(true, 1, -1m));
        Assert.Throws<BusinessRuleException>(() => LodgingSettingsRules.NormalizeExtraBed(false, 0, -1m));
    }

    [Fact]
    public void ตั้งครบ_ได้ค่าที่กรอก_ราคาศูนย์คือไม่คิดเงินได้()
    {
        Assert.Equal(new ExtraBedSetting(true, 2, 650m), LodgingSettingsRules.NormalizeExtraBed(true, 2, 650m));
        Assert.Equal(new ExtraBedSetting(true, 1, 0m), LodgingSettingsRules.NormalizeExtraBed(true, 1, 0m));
    }

    [Fact]
    public void ไม่ติ๊ก_จำนวนเป็นศูนย์แต่ราคาเดิมคงไว้_ไม่ปฏิเสธแม้ช่องว่าง()
    {
        Assert.Equal(new ExtraBedSetting(false, 0, 800m), LodgingSettingsRules.NormalizeExtraBed(false, 3, 800m));
        Assert.Equal(new ExtraBedSetting(false, 0, null), LodgingSettingsRules.NormalizeExtraBed(false, 0, null));
    }

    [Fact]
    public void ป้ายสรุปเตียงเสริมบนการ์ดห้อง()
    {
        Assert.Equal("เตียงเสริมสูงสุด 2 คน · ฿800.00/คน/คืน", LodgingSettingsRules.ExtraBedSummary(true, 2, 800m));
        Assert.Equal("เตียงเสริมสูงสุด 1 คน · ไม่คิดเงิน", LodgingSettingsRules.ExtraBedSummary(true, 1, 0m));
        Assert.Equal("ไม่รับเตียงเสริม/คนเสริม", LodgingSettingsRules.ExtraBedSummary(false, 0, 800m));
        // ค่าเดิมที่ตั้งไม่ครบก่อนมีด่าน — บอกตรง ๆ ไม่แต่งตัวเลข
        Assert.Contains("ตั้งค่าไม่ครบ", LodgingSettingsRules.ExtraBedSummary(true, 0, 800m));
        Assert.Contains("ตั้งค่าไม่ครบ", LodgingSettingsRules.ExtraBedSummary(true, 2, null));
    }

    // ───────── สถานะเปิดจองออนไลน์ (W-01) ─────────

    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public void ครบทุกเงื่อนไข_เท่านั้นจึงเป็น_Live()
    {
        var r = LodgingPublicReadiness.Evaluate(Site, true, "B4 Resort", isActive: true, onlineBookingEnabled: true, sellableUnitCount: 11);
        Assert.Equal(LodgingPublicBookingStatus.Live, r.Status);
        Assert.True(r.IsLive);
        Assert.Null(r.FixHint);
        Assert.Contains("B4 Resort", r.Message);
    }

    [Fact]
    public void ไม่ผูกเว็บ_บอกเหตุผลและทางแก้_แม้ส่วนอื่นพร้อม()
    {
        var r = LodgingPublicReadiness.Evaluate(null, false, null, true, true, 11);
        Assert.Equal(LodgingPublicBookingStatus.NotLinked, r.Status);
        Assert.Contains("เว็บไซต์ที่ผูก", r.FixHint);
        Assert.False(r.IsLive);
    }

    [Theory]
    [InlineData(false, true, true, 5, LodgingPublicBookingStatus.SiteMissing)]
    [InlineData(true, false, true, 5, LodgingPublicBookingStatus.Inactive)]
    [InlineData(true, true, false, 5, LodgingPublicBookingStatus.OnlineOff)]
    [InlineData(true, true, true, 0, LodgingPublicBookingStatus.NoRooms)]
    [InlineData(true, false, false, 0, LodgingPublicBookingStatus.Inactive)]   // ข้อแรกที่ไม่ผ่านคือสิ่งที่ต้องแก้ก่อน
    public void เงื่อนไขที่ไม่ผ่าน_ไม่ตกเป็น_Live(bool siteExists, bool active, bool online, int units, LodgingPublicBookingStatus want)
    {
        var r = LodgingPublicReadiness.Evaluate(Site, siteExists, "เว็บ", active, online, units);
        Assert.Equal(want, r.Status);
        Assert.False(r.IsLive);
        Assert.False(string.IsNullOrWhiteSpace(r.FixHint));
    }

    [Fact]
    public void สถานะออกเป็นชื่อ_ไม่ใช่ตัวเลข()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { s = LodgingPublicBookingStatus.NotLinked },
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Contains("\"NotLinked\"", json);
    }

    // ───────── เงื่อนไขเช็คอินก่อน/เช็คเอาต์หลังเวลา (ข้อ 121) ─────────

    [Fact]
    public void ชั่วโมงศูนย์_ไม่มีบรรทัด_มีชั่วโมง_มีบรรทัดพร้อมค่าธรรมเนียม()
    {
        Assert.Empty(LodgingStayConditions.Lines("14:00", "12:00", 0, 500m, 0, 500m));
        var lines = LodgingStayConditions.Lines("14:00", "12:00", 3, 500m, 0, 0m);
        Assert.Single(lines);
        Assert.Contains("ได้สูงสุด 3 ชม.", lines[0]);
        Assert.Contains("500.00 บาท", lines[0]);
    }

    // ───────── ผูกด่วนจากป้ายสถานะ (ต่อจาก LW ข้อ 118) ─────────

    [Fact]
    public void ผู้สมัครผูกด่วน_เฉพาะเว็บที่พักที่ยังว่าง()
    {
        var me = Guid.NewGuid(); var other = Guid.NewGuid();
        var free = new LodgingSiteBindSource(Guid.NewGuid(), "B4 Resort", true, null);
        var takenByOther = new LodgingSiteBindSource(Guid.NewGuid(), "A Hotel", true, other);
        var shop = new LodgingSiteBindSource(Guid.NewGuid(), "ร้านค้า", false, null);
        var got = LodgingPublicReadiness.BindCandidates(me, new[] { takenByOther, shop, free });
        Assert.Equal(new[] { new LodgingSiteBindOption(free.SiteId, "B4 Resort") }, got);
    }

    [Fact]
    public void ไม่มีเว็บที่พักว่าง_ไม่มีผู้สมัคร_และทางแก้ไม่ชี้ปุ่ม()
    {
        var got = LodgingPublicReadiness.BindCandidates(Guid.NewGuid(),
            new[] { new LodgingSiteBindSource(Guid.NewGuid(), "A", true, Guid.NewGuid()), new LodgingSiteBindSource(Guid.NewGuid(), "B", false, null) });
        Assert.Empty(got);
        var r = LodgingPublicReadiness.Evaluate(null, false, null, true, true, 5, bindableSiteCount: 0);
        Assert.DoesNotContain("ผูกที่พักนี้กับเว็บ", r.FixHint);
    }

    [Fact]
    public void มีเว็บที่พักว่าง_ทางแก้ของไม่ผูกชี้ปุ่มผูกด่วน()
    {
        var r = LodgingPublicReadiness.Evaluate(null, false, null, true, true, 5, bindableSiteCount: 1);
        Assert.Equal(LodgingPublicBookingStatus.NotLinked, r.Status);
        Assert.Contains("ผูกที่พักนี้กับเว็บ", r.FixHint);
    }

    [Fact]
    public void ผูกด่วนได้เฉพาะไม่ผูกหรือเว็บหาย_และเว็บเป้าหมายต้องเป็นเว็บที่พัก()
    {
        Assert.Null(LodgingPublicReadiness.QuickBindRefusal(LodgingPublicBookingStatus.NotLinked, true));
        Assert.Null(LodgingPublicReadiness.QuickBindRefusal(LodgingPublicBookingStatus.SiteMissing, true));
        Assert.Contains("ไม่ใช่เว็บประเภทที่พัก", LodgingPublicReadiness.QuickBindRefusal(LodgingPublicBookingStatus.NotLinked, false));
        foreach (var st in new[] { LodgingPublicBookingStatus.Live, LodgingPublicBookingStatus.Inactive,
                                   LodgingPublicBookingStatus.OnlineOff, LodgingPublicBookingStatus.NoRooms })
            Assert.Contains("ผูกกับเว็บอยู่แล้ว", LodgingPublicReadiness.QuickBindRefusal(st, true));
    }
}
