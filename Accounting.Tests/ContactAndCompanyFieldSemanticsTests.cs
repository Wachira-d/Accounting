using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ความหมายของ "ค่าว่าง" ในช่องที่ตัดสินภาษี** — ล็อกว่า `0`/`null`/`Unknown`
/// แปลว่า "ยังไม่กรอก" และ<b>ไม่ทำให้ยอดของบริษัทที่กรอกครบขยับแม้แต่สตางค์เดียว</b>
///
/// <para>ที่มา: คำถามค้างของเจ้าของ (DECISION_AUDIT_2026-09-18 §10.5 ข้อ 6) —
/// <c>Company.PaidUpCapital</c> ควรเป็น <c>decimal?</c> จริงไหม ในเมื่อวันนี้เป็น
/// <c>NOT NULL DEFAULT 0</c> และ <c>CitRateTable.IsSme</c> ถือ <c>null</c>/<c>≤0</c>
/// เป็น "ยังไม่กรอก" เหมือนกัน. <b>คำตัดสินของทีม: ยังไม่เปลี่ยนชนิด</b>
/// (เหตุผลเต็มในรายงานรอบนี้) ⇒ เทสต์ชุดนี้คือสิ่งที่ทำให้การ "ไม่เปลี่ยน"
/// ปลอดภัย: ถ้าวันหนึ่งมีใครแปลง 0 เป็น "ทุนศูนย์จริง" เทสต์จะแดงทันที</para>
/// </summary>
public class ContactAndCompanyFieldSemanticsTests
{
    // ═══ ทุนที่ชำระแล้ว: 0 กับ null ต้องให้คำตอบเดียวกันทุกทาง ═══

    [Fact]
    public void ทุนศูนย์กับทุน_null_ต้องได้คำตอบ_SME_เหมือนกัน()
    {
        const decimal revenue = 10_000_000m;
        Assert.Equal(CitRateTable.IsSme(null, revenue), CitRateTable.IsSme(0m, revenue));
        Assert.False(CitRateTable.IsSme(0m, revenue));      // ไม่รู้ = ไม่ใช่ SME (G5)
        Assert.Equal(CitRateTable.SmeReason(null, revenue), CitRateTable.SmeReason(0m, revenue));
    }

    [Fact]
    public void ทุนติดลบ_ซึ่งเป็นไปไม่ได้จริง_ก็ต้องถือว่าไม่รู้()
        => Assert.False(CitRateTable.IsSme(-1m, 1_000_000m));

    /// <summary>
    /// **ทิศตรงข้าม (บังคับตาม G7)**: บริษัทที่กรอกทุนครบแล้ว ยอดภาษี
    /// <b>ต้องไม่ขยับแม้แต่สตางค์เดียว</b> ไม่ว่าชนิดของคอลัมน์จะเป็นอะไร
    /// — นี่คือเทสต์ที่กัน "การเปลี่ยนชนิดคอลัมน์" ไม่ให้เปลี่ยนตัวเลขของลูกค้า
    /// </summary>
    // รับเป็น int แล้วแปลงเอง — xUnit InlineData ส่ง decimal ตรง ๆ ไม่ได้
    [Theory]
    // (ทุนที่ชำระแล้ว, รายได้ทั้งรอบ, กำไรสุทธิ, ภาษีที่ต้องได้)
    [InlineData(1_000_000, 10_000_000, 1_000_000, 105_000)]   // SME: 0 + 15% ของ 700,000
    [InlineData(5_000_000, 30_000_000, 1_000_000, 105_000)]   // ขอบพอดีทั้งสองข้อ = ยัง SME
    [InlineData(5_000_001, 10_000_000, 1_000_000, 200_000)]   // ทุนเกิน 1 บาท = 20% เต็ม
    [InlineData(1_000_000, 30_000_001, 1_000_000, 200_000)]   // รายได้เกิน 1 บาท = 20% เต็ม
    [InlineData(1_000_000, 10_000_000, 300_000, 0)]           // ขั้นแรก 0%
    [InlineData(1_000_000, 10_000_000, 3_000_000, 405_000)]   // 0 + 15% ของ 2,700,000
    [InlineData(1_000_000, 10_000_000, 0, 0)]                 // ไม่มีกำไร = ไม่มีภาษี
    [InlineData(1_000_000, 10_000_000, -500_000, 0)]          // ขาดทุน = ไม่มีภาษี
    public void บริษัทที่กรอกทุนครบ_ยอดภาษีต้องไม่ขยับ(
        int paidUpCapital, int revenue, int netProfit, int expectedTax)
    {
        decimal capital = paidUpCapital, rev = revenue, profit = netProfit;
        var isSme = CitRateTable.IsSme(capital, rev);
        Assert.Equal((decimal)expectedTax, CitRateTable.Compute(profit, isSme));

        // และต้องได้เลขเดียวกันเมื่อส่งค่าเดียวกันผ่านชนิด nullable
        // (ถ้าวันหนึ่งคอลัมน์ถูกเปลี่ยนเป็น decimal? ยอดต้องเท่าเดิมเป๊ะ)
        decimal? nullableCapital = capital;
        Assert.Equal((decimal)expectedTax,
            CitRateTable.Compute(profit, CitRateTable.IsSme(nullableCapital, rev)));
    }

    [Fact]
    public void บริษัทที่ยังไม่กรอกทุน_ต้องเห็นเหตุผลบนรายงาน_ไม่ใช่เงียบ()
    {
        var reason = CitRateTable.SmeReason(0m, 10_000_000m);
        Assert.Contains("ยังไม่ได้กรอก", reason);
        Assert.Contains("ทุนที่ชำระแล้ว", reason);   // บอกว่าต้องไปทำอะไร
    }

    // ═══ ค่าตั้งต้นของ Contact: "ยังไม่รู้" ต้องมองเห็นได้ ═══

    [Fact]
    public void ผู้ติดต่อที่สร้างโดยไม่ตั้งชนิด_ต้องเป็น_Unknown_ไม่ใช่บุคคลธรรมดา()
    {
        // นี่คือค่าที่ทุกทางเข้าที่ "ลืมตั้ง ContactType" จะได้ — ต้องเป็น
        // "ยังไม่รู้" ที่เห็นได้บนหน้าจอ ไม่ใช่คำตอบที่ระบบแต่งขึ้น
        var c = new Contact { Name = "ทดสอบ" };
        Assert.Equal(ContactType.Unknown, c.ContactType);
    }

    [Fact]
    public void ผู้ติดต่อที่ตั้งชนิดไว้แล้ว_ต้องไม่ถูกค่าตั้งต้นใหม่แตะ()
    {
        Assert.Equal(ContactType.JuristicPerson,
            new Contact { Name = "บริษัท ก จำกัด", ContactType = ContactType.JuristicPerson }.ContactType);
        Assert.Equal(ContactType.Individual,
            new Contact { Name = "สมชาย", ContactType = ContactType.Individual }.ContactType);
    }
}
