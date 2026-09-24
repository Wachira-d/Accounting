using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 · คำตัดสินเจ้าของข้อ 26 — ใบที่ไม่มีที่อยู่ผู้ซื้อบนกระดาษ + ผู้ซื้อคือเรา (≥ 0.85) → เติมที่อยู่บริษัทเรา
/// สีเหลือง · ห้ามทับที่อยู่ที่พิมพ์ · บริษัทตัวอย่าง = หจก.แอม แฮปปี้เนส (ผู้ซื้อของใบ A/B)
///
/// <para>สองครึ่ง: ครึ่งแรก = ช่องว่างถูกเติม (ค่ามาจากฐานเรา ความมั่นใจ &lt; 0.85) · ครึ่งหลัง = ที่อยู่ที่พิมพ์/ฝั่งที่ไม่มั่นใจ/
/// สาขาอื่นของเรา/เราเป็นผู้ขาย <b>ไม่ถูกแตะ</b></para>
/// </summary>
public class OcrOurAddressFillTests
{
    private const string OurAddress = "202/24 ม.5 ซ.บ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110";
    private static readonly OcrOurIdentity Us = new(
        Name: "หจก. แอม แฮปปี้เนส", NameEn: null, TaxId: "0203562005871", BranchCode: "00000", Address: OurAddress);

    // ───────── ครึ่งแรก: ช่องว่าง → เติมจากฐานเรา สีเหลือง ─────────

    [Fact]
    public void BuyerIsUs_NoPrintedAddress_FillsOurAddress_Yellow()
    {
        var r = OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 1.0m, null, null, Us);
        Assert.NotNull(r);
        Assert.Equal(OurAddress, r!.Address);
        Assert.True(r.Confidence < 0.85m, "ค่าจากฐานเรา ไม่ใช่จากกระดาษ ⇒ ต้องขึ้นไฮไลต์");
        Assert.Contains("§86/4", r.Reason);
    }

    [Theory]
    [InlineData("00000")]
    [InlineData("0")]
    public void BuyerBranchIsHeadOffice_StillFills(string paperBranch)
        => Assert.NotNull(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 0.85m, "  ", paperBranch, Us));

    [Fact]
    public void BuyerBranchEqualsOurBranch_Fills()
    {
        var branchCo = Us with { BranchCode = "00003" };
        Assert.NotNull(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 0.9m, null, "00003", branchCo));
    }

    // ───────── ครึ่งหลัง: ไม่แตะ ─────────

    [Fact]
    public void PrintedBuyerAddress_NeverOverwritten()
        => Assert.Null(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 1.0m,
            "202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", null, Us));

    [Fact]
    public void SideNotConfident_NoFill()
        => Assert.Null(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 0.84m, null, null, Us));

    [Theory]
    [InlineData(OcrSelfSide.Seller)]
    [InlineData(OcrSelfSide.Unknown)]
    public void WeAreNotTheBuyer_NoFill(OcrSelfSide side)
        => Assert.Null(OcrOurAddressFill.ForBuyer(side, 1.0m, null, null, Us));

    [Fact]
    public void PaperSaysAnotherBranchOfOurs_NoFill_UnknownBeatsInvented()
        => Assert.Null(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 1.0m, null, "00002", Us));

    [Fact]
    public void CompanyHasNoAddress_NoFill()
    {
        Assert.Null(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 1.0m, null, null, Us with { Address = " " }));
        Assert.Null(OcrOurAddressFill.ForBuyer(OcrSelfSide.Buyer, 1.0m, null, null, null));
    }
}
