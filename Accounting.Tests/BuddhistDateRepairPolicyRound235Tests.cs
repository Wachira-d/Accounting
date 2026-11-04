using Accounting.Helpers;
using Xunit;
using Skip = Accounting.Helpers.BuddhistDateRepairPolicy.Skip;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 (ฝ่ายค้าน S2) — ตัวตัดสิน "แถวใดห้ามซ่อมวันที่ตรง" ของงานซ่อมศักราช (pure) · สองทิศ: งวดเปิด/ร่าง/แบบภาษียังไม่ยื่น ⇒ แก้ได้ ·
/// งวดปิด/ออกเลขแล้ว/แบบภาษียื่นแล้ว ⇒ ข้ามพร้อมเหตุ
/// </summary>
public class BuddhistDateRepairPolicyRound235Tests
{
    private static readonly (DateTime, DateTime)[] ClosedMar2024 = { (new DateTime(2024, 3, 1), new DateTime(2024, 3, 31)) };

    [Fact]
    public void ใบที่แก้ได้_ไม่ถูกข้าม()
    {
        Assert.Equal(Skip.None, BuddhistDateRepairPolicy.Decide(false, false, false));
        Assert.False(BuddhistDateRepairPolicy.InClosedPeriod(new DateTime(2024, 4, 1), ClosedMar2024));
        Assert.False(BuddhistDateRepairPolicy.IsNumberIssued("DRAFT-" + Guid.NewGuid()));
        Assert.False(BuddhistDateRepairPolicy.IsNumberIssued(null));
    }

    [Fact]
    public void งวดปิด_ออกเลขแล้ว_แบบภาษียื่นแล้ว_ถูกข้ามพร้อมเหตุ()
    {
        Assert.True(BuddhistDateRepairPolicy.InClosedPeriod(new DateTime(2024, 3, 31, 23, 0, 0), ClosedMar2024));   // ขอบท้ายงวด (เทียบวัน)
        Assert.True(BuddhistDateRepairPolicy.IsNumberIssued("INV-2024-0001"));
        Assert.Equal(Skip.ClosedPeriod, BuddhistDateRepairPolicy.Decide(true, false, false));
        Assert.Equal(Skip.NumberIssued, BuddhistDateRepairPolicy.Decide(true, true, false));
        Assert.Equal(Skip.TaxReportFiled, BuddhistDateRepairPolicy.Decide(true, true, true));
        foreach (var s in new[] { Skip.ClosedPeriod, Skip.NumberIssued, Skip.TaxReportFiled })
            Assert.Contains("ต้อง", BuddhistDateRepairPolicy.Message(s));   // บอกทางไปต่อ
    }
}
