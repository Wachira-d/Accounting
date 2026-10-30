using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 215 เฟส 2 (คำตัดสินข้อ 176 · erp-review/2026-10-30/CMS_UX_REDESIGN.md §5.4) — "ลูกค้าเลือกผู้ให้บริการได้ไหม" ระดับเว็บ:
/// บริการ <c>ResourceMode = null</c> = ตามค่าเว็บ · ตัวตัดสินเดียว <see cref="BookingProviderAllocation.EffectiveMode"/> ·
/// บริการเดิมทุกแถวมีค่าตั้งเอง (migration ไม่แปลงเป็น null) ⇒ พฤติกรรมเดิมไม่เปลี่ยน — ล็อกทั้งสองทิศ
/// </summary>
public class BookingRound215ProviderDefaultTests
{
    [Fact]
    public void บริการตามค่าเว็บ_ได้ค่าเว็บ()
    {
        Assert.Equal(BookingResourceMode.Auto, BookingProviderAllocation.EffectiveMode(null, siteEnabled: true, BookingResourceMode.Auto));
        Assert.Equal(BookingResourceMode.CustomerChoice, BookingProviderAllocation.EffectiveMode(null, true, BookingResourceMode.CustomerChoice));
    }

    [Fact]
    public void ทิศตรงข้าม_บริการกำหนดเอง_ชนะค่าเว็บ_บริการเดิมไม่เปลี่ยนพฤติกรรม()
    {
        Assert.Equal(BookingResourceMode.Required, BookingProviderAllocation.EffectiveMode(BookingResourceMode.Required, true, BookingResourceMode.Auto));
        Assert.Equal(BookingResourceMode.None, BookingProviderAllocation.EffectiveMode(BookingResourceMode.None, true, BookingResourceMode.CustomerChoice));
    }

    [Fact]
    public void เว็บปิดจัดสรร_None_เสมอ_ไม่ว่าจะตามค่าเว็บหรือกำหนดเอง()
    {
        Assert.Equal(BookingResourceMode.None, BookingProviderAllocation.EffectiveMode(null, false, BookingResourceMode.Auto));
        Assert.Equal(BookingResourceMode.None, BookingProviderAllocation.EffectiveMode(BookingResourceMode.Required, false, BookingResourceMode.Auto));
    }

    [Fact]
    public void เว็บที่ไม่ตั้งค่าเว็บ_ตามค่าเว็บ_คือ_None_พฤติกรรมเดิม()
    {
        Assert.Equal(BookingResourceMode.None, BookingProviderAllocation.EffectiveMode(null, true));
        Assert.Equal(BookingResourceMode.None, BookingSlotEngine.DefaultsOf(null).DefaultResourceMode);
        Assert.Equal(BookingResourceMode.None, BookingSlotEngine.DefaultsOf(new SiteBookingSettings()).DefaultResourceMode);
    }

    [Theory]
    [InlineData(IndustryType.Healthcare, BookingResourceMode.Auto)]
    [InlineData(IndustryType.Service, BookingResourceMode.Auto)]
    [InlineData(IndustryType.Freelance, BookingResourceMode.Auto)]
    [InlineData(IndustryType.Beauty, BookingResourceMode.CustomerChoice)]
    [InlineData(IndustryType.Hotel, BookingResourceMode.CustomerChoice)]
    [InlineData(IndustryType.General, BookingResourceMode.CustomerChoice)]
    public void เว็บใหม่_ค่าเริ่มต้นตามประเภทธุรกิจ_คำตัดสิน176(IndustryType industry, BookingResourceMode expected)
    {
        Assert.Equal(expected, BookingProviderAllocation.NewSiteDefaultMode(industry));
    }

    [Fact]
    public void RulesOf_บริการตามค่าเว็บ_รับค่าเว็บ_บริการกำหนดเอง_คงค่าตัวเอง()
    {
        var site = BookingSlotEngine.DefaultsOf(new SiteBookingSettings { DefaultResourceMode = BookingResourceMode.Auto });
        Assert.Equal(BookingResourceMode.Auto, BookingSlotEngine.RulesOf(new SiteBookingService { ResourceMode = null }, site).ResourceMode);
        Assert.Equal(BookingResourceMode.CustomerChoice,
            BookingSlotEngine.RulesOf(new SiteBookingService { ResourceMode = BookingResourceMode.CustomerChoice }, site).ResourceMode);
        // ไม่มีค่าตั้งเว็บ ⇒ None (ค่าเริ่มต้นเดิมของบริการ)
        Assert.Equal(BookingResourceMode.None, BookingSlotEngine.RulesOf(new SiteBookingService { ResourceMode = null }).ResourceMode);
    }

    [Fact]
    public void ตัวอย่างที่ลูกค้าเห็น_ตามโหมด()
    {
        Assert.False(BookingProviderAllocation.CustomerPreview(BookingResourceMode.Auto).ShowPicker);
        var choice = BookingProviderAllocation.CustomerPreview(BookingResourceMode.CustomerChoice);
        Assert.True(choice.ShowPicker); Assert.False(choice.Required);
        Assert.True(BookingProviderAllocation.CustomerPreview(BookingResourceMode.Required).Required);
        Assert.False(BookingProviderAllocation.CustomerPreview(BookingResourceMode.None).ShowPicker);
    }

    [Fact]
    public void ที่มาของโหมด_ตามค่าเว็บ_หรือ_กำหนดเอง()
    {
        Assert.Equal("site", BookingProviderAllocation.ModeSource(null));
        Assert.Equal("service", BookingProviderAllocation.ModeSource(BookingResourceMode.None));
    }

    [Fact]
    public void migration_ไม่แตะค่าบริการเดิม_เพิ่มค่าเว็บเป็น_null()
    {
        var sql = string.Join("\n", BookingEngineMigration.Statements());
        Assert.Contains("ALTER COLUMN \"ResourceMode\" DROP NOT NULL", sql);
        Assert.Contains("\"DefaultResourceMode\" integer NULL", sql);
        Assert.DoesNotContain("SET \"ResourceMode\"", sql);
    }

    // ── ฝ่ายค้านรอบ 215 เฟส 2 ──

    [Fact]
    public void S1_เว็บใหม่ที่ตั้งค่าเว็บไว้_อนุมานว่าเปิดจัดสรร_แม้ยังไม่มีผู้ให้บริการ()
    {
        Assert.True(BookingProviderAllocation.Enabled(null, false, false, BookingResourceMode.CustomerChoice));
        Assert.True(BookingProviderAllocation.Enabled(null, false, false, BookingResourceMode.Auto));
    }

    [Fact]
    public void S1_ทิศตรงข้าม_ค่าเว็บไม่ตั้งหรือNone_อนุมานแบบเดิม_และเจ้าของปิดเองชนะ()
    {
        Assert.False(BookingProviderAllocation.Enabled(null, false, false, null));
        Assert.False(BookingProviderAllocation.Enabled(null, false, false, BookingResourceMode.None));
        Assert.False(BookingProviderAllocation.Enabled(false, true, true, BookingResourceMode.CustomerChoice));
        Assert.True(BookingProviderAllocation.Enabled(null, true, false));   // เดิม: มีผู้ให้บริการ ⇒ เปิด
    }

    [Fact]
    public void S2_ตัวอย่างบริการที่ไม่ตั้งโหมดเอง_สร้างเป็นตามค่าเว็บ()
    {
        var all = Accounting.Services.Implementations.Cms.CmsBookingServiceSeeder.PresetResponses().SelectMany(p => p.Services).ToList();
        var aroma = all.First(s => s.Name.StartsWith("นวดน้ำมันอโรมา"));
        Assert.Null(aroma.ResourceMode);
    }

    [Fact]
    public void S2_ทิศตรงข้าม_ตัวอย่างที่ตั้งโหมดเฉพาะ_คงค่าเดิม()
    {
        var all = Accounting.Services.Implementations.Cms.CmsBookingServiceSeeder.PresetResponses().SelectMany(p => p.Services).ToList();
        Assert.Equal(BookingResourceMode.CustomerChoice, all.First(s => s.Name.StartsWith("นวดไทย 60")).ResourceMode);
        Assert.Equal(BookingResourceMode.Auto, all.First(s => s.Name.StartsWith("ทำเล็บเจล")).ResourceMode);
    }
}

