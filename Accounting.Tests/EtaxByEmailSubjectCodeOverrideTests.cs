using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 250 WP-03 (คำตัดสิน AUDIT_249 §3.3 N1) — รหัสหัวเรื่อง e-Tax by Email เป็นค่าตั้งแพลตฟอร์มที่แอดมินทับได้
/// ล็อกทั้งสองทิศ: ค่าทับมีผล (CDN → CRN) · ไม่มีค่าทับ/ค่าเสีย = หัวเรื่องเดิมทุกใบ (ไม่ถูกแตะ) · ใบลดหนี้ที่ทับรหัสแล้วยังต้องมีเลขใบเดิม (§86/9-10)</summary>
public class EtaxByEmailSubjectCodeOverrideTests
{
    private static readonly DateTime Jan18_2017 = new(2017, 1, 18, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ทับรหัสใบลดหนี้เป็น_CRN_หัวเรื่องเปลี่ยนตาม()
    {
        var codes = EtaxByEmailFormat.ParseSubjectCodes("{\"CreditNote\":\"crn\"}");
        Assert.Equal("[18012560][CRN][CN-1][IV-9]", EtaxByEmailFormat.Subject("81", Jan18_2017, "CN-1", "IV-9", null, codes).Subject);
        // ช่องอื่นไม่ถูกแตะ
        Assert.Equal("[18012560][INV][IV-1]", EtaxByEmailFormat.Subject("388", Jan18_2017, "IV-1", null, null, codes).Subject);
        Assert.Equal("[18012560][DBN][DN-1][IV-9]", EtaxByEmailFormat.Subject("80", Jan18_2017, "DN-1", "IV-9", null, codes).Subject);
    }

    [Fact]
    public void ไม่มีค่าทับ_หรือค่าเสีย_ใช้รหัสตั้งต้นเดิม()
    {
        foreach (var json in new[] { null, "", "not-json", "{\"CreditNote\":\"CR\"}", "{\"Unknown\":\"ABC\"}", "{\"Receipt\":\"R1T\"}" })
        {
            var codes = EtaxByEmailFormat.ParseSubjectCodes(json);
            Assert.Equal("[18012560][CDN][CN-1][IV-9]", EtaxByEmailFormat.Subject("81", Jan18_2017, "CN-1", "IV-9", null, codes).Subject);
            Assert.Equal("[18012560][RCT][RE-1]", EtaxByEmailFormat.Subject("T03", Jan18_2017, "RE-1", null, null, codes).Subject);
        }
    }

    [Fact]
    public void ใบลดหนี้ที่ทับรหัสแล้ว_ยังต้องอ้างเลขใบเดิม()
    {
        var codes = EtaxByEmailFormat.ParseSubjectCodes("{\"CreditNote\":\"CRN\"}");
        var r = EtaxByEmailFormat.Subject("81", Jan18_2017, "CN-1", null, null, codes);
        Assert.Null(r.Subject);
        Assert.Contains("ใบลดหนี้", r.Problem);
    }

    [Fact]
    public void เก็บเฉพาะช่องที่ต่างจากค่าตั้งต้น_กลับค่าตั้งต้นทั้งหมด_เป็น_null()
    {
        Assert.Null(EtaxByEmailFormat.SerializeSubjectCodes(new Dictionary<string, string> { ["Invoice"] = "INV", ["CreditNote"] = "CDN" }));
        var json = EtaxByEmailFormat.SerializeSubjectCodes(new Dictionary<string, string> { ["Invoice"] = "INV", ["CreditNote"] = "CRN" });
        Assert.Equal("CRN", EtaxByEmailFormat.ParseSubjectCodes(json)["CreditNote"]);
        Assert.False(EtaxByEmailFormat.ParseSubjectCodes(json).ContainsKey("Invoice"));
        var eff = EtaxByEmailFormat.EffectiveSubjectCodes(json);
        Assert.Equal(4, eff.Count);
        Assert.Contains(eff, e => e.Key == "CreditNote" && e.Code == "CRN" && e.Default == "CDN");
    }

    [Theory]
    [InlineData("INV", true)]
    [InlineData("CRN", true)]
    [InlineData("inv", false)]
    [InlineData("IN", false)]
    [InlineData("INVX", false)]
    [InlineData("I1V", false)]
    [InlineData(null, false)]
    public void รหัสต้องเป็นอักษรใหญ่สามตัว(string? code, bool ok) => Assert.Equal(ok, EtaxByEmailFormat.IsValidSubjectCode(code));
}
