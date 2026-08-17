using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กันบั๊กคลาส "เลขผังชนความหมาย" (ที่มา: พรีวิว GL hardcode 21510 เป็นบัญชี
/// WHT ค้างจ่าย แต่ผังมาตรฐาน 21510 คือ "เงินมัดจำรับล่วงหน้าค่าห้องพัก" ⇒
/// พรีวิวโชว์ภาษีหัก ณ ที่จ่าย เข้าบัญชีเงินมัดจำ ผู้ใช้เข้าใจว่าลงบัญชีผิด).
/// เทสต์ pin ความหมายของ code ที่ JE engine + พรีวิวพึ่งพา — ถ้าใครย้าย/
/// เปลี่ยนชื่อบัญชีเหล่านี้ในผังมาตรฐาน เทสต์ล้มก่อนถึงมือผู้ใช้.
/// คู่กับ tools/gl_code_check.py (ตรวจฝั่ง call site).
/// </summary>
public class GlAccountTemplateSanityTests
{
    private static Dictionary<string, string> AllNames()
    {
        // common + ทุกชุด equity/industry — ชื่อแรกของ code ชนะ (ตรงกับ seeding)
        var lists = new[]
        {
            ChartOfAccountTemplates.GetCommonAccounts(),
            ChartOfAccountTemplates.GetEquityJuristicPerson(),
            ChartOfAccountTemplates.GetEquityPublicCompany(),
            ChartOfAccountTemplates.GetEquityPartnership(),
            ChartOfAccountTemplates.GetEquityIndividual(),
            ChartOfAccountTemplates.GetEquityFoundation(),
        };
        var map = new Dictionary<string, string>();
        foreach (var list in lists)
            foreach (var a in list)
                if (!map.ContainsKey(a.Code)) map[a.Code] = a.NameTh;
        return map;
    }

    [Theory]
    // ── บัญชีที่ JE engine (DocumentService) + พรีวิว (PdfGenerationService) อ้างด้วย code ──
    [InlineData("21916", "หัก ณ ที่จ่าย")]   // WHT payable ภ.ง.ด.3 (บุคคลธรรมดา)
    [InlineData("21917", "หัก ณ ที่จ่าย")]   // WHT payable ภ.ง.ด.53 (นิติบุคคล)
    [InlineData("11910", "ถูกหัก")]          // WHT ฝั่งถูกหัก (สินทรัพย์ — เครดิตภาษี)
    [InlineData("21911", "ภาษีขาย")]         // output VAT ภ.พ.30
    [InlineData("21913", "ภาษีขาย")]         // output VAT รอเรียกเก็บ (undue)
    [InlineData("11610", "ภาษีซื้อ")]        // input VAT ภ.พ.30
    [InlineData("11640", "ภาษีซื้อ")]        // input VAT ยังไม่ถึงกำหนด
    [InlineData("21210", "เจ้าหนี้")]        // AP การค้า
    [InlineData("21220", "เจ้าหนี้")]        // AP อื่น (Expense)
    [InlineData("11111", "เงินสด")]  // (11110 ไม่มีในผังมาตรฐาน — พรีวิวเคย fallback ผิดตัว)
    public void Critical_gl_codes_keep_their_meaning(string code, string mustContain)
    {
        var names = AllNames();
        Assert.True(names.ContainsKey(code), $"ผังมาตรฐานไม่มีบัญชี {code} — JE engine อ้าง code นี้ตรง ๆ");
        Assert.Contains(mustContain, names[code]);
    }

    [Fact]
    public void Code_21510_is_NOT_a_wht_account()
    {
        // pin การชนที่เคยเกิด: 21510 = เงินมัดจำรับล่วงหน้า (หนี้สินมัดจำ) —
        // ห้ามมีใครเข้าใจ/เปลี่ยนเป็นบัญชี WHT (พรีวิวเคย hardcode ผิดตัวนี้)
        var names = AllNames();
        Assert.True(names.ContainsKey("21510"));
        Assert.DoesNotContain("หัก ณ ที่จ่าย", names["21510"]);
        Assert.Contains("มัดจำ", names["21510"]);
    }

    [Fact]
    public void Common_chart_has_no_duplicate_codes()
    {
        var dup = ChartOfAccountTemplates.GetCommonAccounts()
            .GroupBy(a => a.Code).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "เลขผังซ้ำใน common template: " + string.Join(", ", dup));
    }
}
