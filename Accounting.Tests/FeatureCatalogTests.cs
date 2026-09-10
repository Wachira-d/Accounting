using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกแคตตาล็อกฟีเจอร์ตัวเดียวของระบบ — ทุกหน้า (หน้าแรก · สมัคร · ตั้งค่าฟีเจอร์ ·
/// แพ็กเกจแอดมิน) แสดงจากตัวนี้ จึงต้องพิสูจน์ว่า (1) ทุกบิตใน <see cref="FeatureFlags"/>
/// มี metadata จริง ไม่ตกเป็นชื่อตัวแปร (2) ชุดสำเร็จรูปตรงกับ enum combo (ปุ่ม preset ของ
/// แอดมินเคยเป็นสำเนามือที่ตก <c>EtaxByEmail</c>) (3) bitmask ↔ ชื่อ ไป-กลับได้ครบ
/// </summary>
public class FeatureCatalogTests
{
    [Fact]
    public void ทุกบิตเดี่ยวต้องมีป้ายไทยและหมวดที่รู้จัก()
    {
        var categoryKeys = FeatureCatalog.Categories.Select(c => c.Key).ToHashSet();
        var missing = new List<string>();
        foreach (FeatureFlags v in Enum.GetValues(typeof(FeatureFlags)))
        {
            var name = v.ToString();
            if (FeatureCatalog.PresetNames.Contains(name) || !FeatureCatalog.IsSingleBit(v)) continue;
            var e = FeatureCatalog.Find(name);
            if (e == null || e.LabelTh == name || e.Category == "other" || string.IsNullOrWhiteSpace(e.LabelEn)
                || !categoryKeys.Contains(e.Category))
                missing.Add(name);
        }
        Assert.True(missing.Count == 0, "ธงที่ยังไม่มี metadata ใน FeatureCatalog.Meta: " + string.Join(", ", missing));
    }

    [Fact]
    public void ธงCmsสามตัวต้องอยู่ในแคตตาล็อก()
    {
        // เดิมสำเนาป้ายใน SubscriptionService/settings-features.html ตกสามตัวนี้
        Assert.Equal("cms", FeatureCatalog.Find("CmsWebsiteBuilder")!.Category);
        Assert.Equal("cms", FeatureCatalog.Find("CmsEcommerce")!.Category);
        Assert.Equal("cms", FeatureCatalog.Find("CmsBooking")!.Category);
    }

    [Fact]
    public void ไม่มีชื่อซ้ำ_และไม่มีpresetปนในรายการฟีเจอร์เดี่ยว()
    {
        var names = FeatureCatalog.All.Select(e => e.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain(names, n => FeatureCatalog.PresetNames.Contains(n));
    }

    [Theory]
    [InlineData("trial", FeatureFlags.TrialFeatures)]
    [InlineData("basic", FeatureFlags.BasicFeatures)]
    [InlineData("pro", FeatureFlags.ProFeatures)]
    [InlineData("enterprise", FeatureFlags.EnterpriseFeatures)]
    public void ชุดสำเร็จรูปต้องตรงกับenum_combo(string key, FeatureFlags combo)
    {
        var fromCatalog = FeatureCatalog.Presets[key];
        Assert.Equal(FeatureCatalog.NamesOf(combo), fromCatalog);
        // ทิศกลับ: ชื่อทั้งหมดรวมกันได้ combo เดิมพอดี
        Assert.Equal(combo, FeatureCatalog.FlagsOf(fromCatalog));
    }

    [Fact]
    public void Proต้องมีEtaxByEmail_ที่presetมือของแอดมินเคยตก()
    {
        Assert.Contains("EtaxByEmail", FeatureCatalog.Presets["pro"]);
    }

    [Fact]
    public void bitmaskไปกลับได้ครบ_และชื่อที่ไม่รู้จักถูกข้าม()
    {
        var flags = FeatureFlags.Inventory | FeatureFlags.DocumentOCR | FeatureFlags.CmsBooking;
        var names = FeatureCatalog.NamesOf(flags);
        Assert.Equal(new[] { "Inventory", "DocumentOCR", "CmsBooking" }, names);
        Assert.Equal(flags, FeatureCatalog.FlagsOf(names.Concat(new[] { "NotAFeature", "ProFeatures", "" })));
    }
}
