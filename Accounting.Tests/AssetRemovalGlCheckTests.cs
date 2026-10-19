using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>ตัวตัดสิน "ลบทะเบียนสินทรัพย์ที่ขึ้นจากเอกสารแล้ว GL ไม่ค้าง" — เคสจริง 2026-10-19:
/// PV-20260903-0001 อุปกรณ์สำนักงาน 138.32 ลงผัง 12210 ⇒ ขึ้นทะเบียนอัตโนมัติ ทั้งที่ไม่ใช่สินทรัพย์ · ลบไม่ได้</summary>
public class AssetRemovalGlCheckTests
{
    [Fact]
    public void Document_still_posts_to_asset_account_blocks_with_the_exact_amount()
        // ใบยังลง Dr 12210 138.32 · ไม่มีสินทรัพย์ตัวอื่น ⇒ ลบแล้วค้าง 138.32 (ข้อความบอกยอดนี้)
        => Assert.Equal(138.32m, AssetRemovalGlCheck.OrphanedIfRemoved(138.32m, 0m));

    [Fact]
    public void Line_already_moved_to_expense_allows_removal()
        // ใบปรับปรุงย้ายผัง Cr 12210 138.32 ⇒ สุทธิ 0 ⇒ ลบได้แม้ใบยังจ่ายเงินอยู่ (ด่านเดิมบล็อกเพราะดูสถานะใบ)
        => Assert.Equal(0m, AssetRemovalGlCheck.OrphanedIfRemoved(0m, 0m));

    [Fact]
    public void Duplicate_register_on_the_same_line_amount_can_be_removed_while_the_twin_stays()
        // ใบเดียวขึ้นทะเบียนซ้ำ 2 แถว (สแกน + อนุมัติ) — GL 26,944.86 มีแถวอื่นรองรับครบ ⇒ ลบตัวซ้ำได้
        => Assert.Equal(0m, AssetRemovalGlCheck.OrphanedIfRemoved(26_944.86m, 26_944.86m));

    [Fact]
    public void Removing_one_of_three_air_conditioners_still_on_the_books_blocks()
        // ซื้อแอร์ 3 เครื่องบน 12210 = 90,000 · ลบ 1 ตัวที่ยังใช้งานจริง ⇒ ค้าง 30,000 ⇒ บล็อก (ทิศตรงข้าม: ห้ามหลวมเกิน)
        => Assert.Equal(30_000m, AssetRemovalGlCheck.OrphanedIfRemoved(90_000m, 60_000m));

    [Theory]
    [InlineData(100.005, 100.00, 0)]      // เศษปัดต้นทุนเฉลี่ยค่าประกอบ ≤ 1 สตางค์ ⇒ ไม่นับว่าค้าง
    [InlineData(100.02, 100.00, 0.02)]    // เกิน 1 สตางค์ ⇒ ค้างจริง
    [InlineData(-5, 0, 0)]                // GL ติดลบ (ย้ายออกเกิน) ไม่ใช่ยอดสินทรัพย์ที่ค้าง
    public void Rounding_tolerance_is_one_satang(double gl, double others, double expected)
        => Assert.Equal((decimal)expected, AssetRemovalGlCheck.OrphanedIfRemoved((decimal)gl, (decimal)others));

    [Theory]
    [InlineData("12210", "51800", true)]   // อุปกรณ์สำนักงาน → ค่าวัสดุสิ้นเปลือง: ทะเบียนต้องออกตาม
    [InlineData("122101", "51800", true)]  // ผัง custom 6 หลักใต้ 12210
    [InlineData("12210", "12220", false)]  // ย้ายระหว่างผังสินทรัพย์ = ยังเป็นสินทรัพย์ ห้ามลบทะเบียน
    [InlineData("51800", "12210", false)]  // ค่าใช้จ่าย → สินทรัพย์ ไม่ใช่ทิศนี้
    [InlineData("51800", "51100", false)]
    [InlineData(null, "51800", false)]
    public void Moves_off_asset_account_only_when_leaving_ppe(string? from, string? to, bool expected)
        => Assert.Equal(expected, AssetRemovalGlCheck.MovesOffAssetAccount(from, to));
}
