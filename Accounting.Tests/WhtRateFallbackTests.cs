using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ห้ามเติมอัตราของผู้รับ "ชนิดตรงข้าม" ให้ช่องไม่ว่าง** (ผลตรวจรอบ 181 · D2-B3b)
///
/// ═══ กับดักที่ถอดออก ═══
/// <para><c>TaxService.GetWhtRate</c> เคยเขียนขั้นแรกว่า
/// <c>RateFor(code, payeeIsJuristic) ?? RateFor(code, !payeeIsJuristic)</c>
/// = "ถ้าหาอัตราของผู้รับชนิดนี้ไม่เจอ ให้ใช้อัตราของ<b>ชนิดตรงข้าม</b>"
/// วันนี้ยังไม่เกิดผลเพราะทุกแถวใน <see cref="ThaiWhtRateTable"/> มีอัตราครบ
/// ทั้งสองข้างหรือว่างทั้งสองข้าง — แต่เป็นระเบิดเวลาทันทีที่เพิ่มรหัสที่
/// กฎหมายกำหนดอัตราไว้ฝั่งเดียว และขัดกับงานรอบ 180 ที่เพิ่งแยกอัตราตามชนิด
/// ผู้รับ (ดอกเบี้ย 40(4)(ก): บุคคล 15% · นิติบุคคล 1% — ต่างกัน 15 เท่า)</para>
///
/// <para><b>เทสต์นี้เป็นด่านเชิงรุก</b>: มันเดินทุกแถวของตารางกลาง เทียบคำตอบของ
/// <c>GetWhtRate</c> กับอัตรา<b>ของชนิดผู้รับที่ถาม</b> โดยตรง ⇒ วันไหนมีคนเพิ่มแถว
/// ที่มีอัตราฝั่งเดียว แล้วโค้ดกลับไปยืมอัตราฝั่งตรงข้าม เทสต์นี้จะแดงทันที
/// (ใส่ <c>?? RateFor(code, !payeeIsJuristic)</c> กลับเข้าไปพร้อมแถวแบบนั้น = จับได้)</para>
/// </summary>
public class WhtRateFallbackTests
{
    public static TheoryData<string, bool> ทุกรหัสคูณสองชนิดผู้รับ()
    {
        var data = new TheoryData<string, bool>();
        foreach (var t in ThaiWhtRateTable.All)
        {
            data.Add(t.Code, false);   // ผู้รับเป็นบุคคลธรรมดา
            data.Add(t.Code, true);    // ผู้รับเป็นนิติบุคคล
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ทุกรหัสคูณสองชนิดผู้รับ))]
    public void อัตราที่คืนต้องเป็นของชนิดผู้รับที่ถามเท่านั้น(string code, bool payeeIsJuristic)
    {
        // taxAmount = 0 ⇒ ขั้น (2) "คิดกลับจากยอดที่หักจริง" ทำงานไม่ได้
        // ⇒ เหลือแต่ขั้น (1) ตารางกฎหมาย และขั้น (3) คืน 0 = "ไม่รู้"
        var actual = TaxService.GetWhtRate(code, incomeAmount: 100_000m, taxAmount: 0m, payeeIsJuristic);

        var lawful = ThaiWhtRateTable.RateFor(code, payeeIsJuristic);
        if (lawful is decimal expected)
            Assert.Equal(expected, actual);
        else
            // กฎหมายไม่ได้กำหนดอัตราคงที่ให้ผู้รับชนิดนี้ → ต้องตอบ "ไม่รู้" (0)
            // **ห้าม**ยืมอัตราของชนิดตรงข้ามมาเติม
            Assert.Equal(0m, actual);
    }

    [Fact]
    public void เงินเดือน40_1_ไม่มีอัตราคงที่ทั้งสองชนิด_ต้องคืนศูนย์ไม่ใช่เดา()
    {
        Assert.Equal(0m, TaxService.GetWhtRate("1", 500_000m, 0m, payeeIsJuristic: false));
        Assert.Equal(0m, TaxService.GetWhtRate("1", 500_000m, 0m, payeeIsJuristic: true));
    }

    [Fact]
    public void รหัสที่ไม่รู้จักและคิดกลับไม่ได้_ต้องคืนศูนย์()
        => Assert.Equal(0m, TaxService.GetWhtRate("ไม่มีรหัสนี้", 0m, 0m, payeeIsJuristic: true));

    // ───────── ทิศตรงข้าม: ขั้นที่เหลือต้องยังทำงานเหมือนเดิม ─────────

    [Fact]
    public void ขั้น2_ยังคิดกลับจากยอดที่หักจริงเมื่อกฎหมายไม่มีอัตราคงที่()
        // เงินเดือนขั้นบันได: หัก 25,000 จาก 500,000 = 5% — ข้อมูลจริงบนบรรทัด
        // ไม่ใช่ค่าที่แต่งขึ้น จึงยังใช้ได้ (การถอดขั้นยืมอัตราต้องไม่ไปปิดขั้นนี้)
        => Assert.Equal(5m, TaxService.GetWhtRate("1", 500_000m, 25_000m, payeeIsJuristic: false));

    [Fact]
    public void ดอกเบี้ยยังแยกอัตราตามชนิดผู้รับถูกต้อง()
    {
        // งานรอบ 180 ต้องไม่ถูกแตะ: บุคคล 15% · นิติบุคคล 1%
        Assert.Equal(15m, TaxService.GetWhtRate("4a", 100_000m, 0m, payeeIsJuristic: false));
        Assert.Equal(1m, TaxService.GetWhtRate("4a", 100_000m, 0m, payeeIsJuristic: true));
    }

    [Fact]
    public void อัตราตามกฎหมายชนะการคิดกลับ_เมื่อยอดบนบรรทัดหักมาผิด()
        // ค่าบริการ 40(2) = 3% ตามกฎหมาย แม้บรรทัดจะหักมา 10%
        // (ขั้น (1) ต้องมาก่อนขั้น (2) เสมอ)
        => Assert.Equal(3m, TaxService.GetWhtRate("2", 100_000m, 10_000m, payeeIsJuristic: true));
}
