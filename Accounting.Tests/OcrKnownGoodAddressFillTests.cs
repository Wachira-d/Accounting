using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="OcrKnownGoodAddressFill"/> — ที่อยู่ผู้ขายที่ Azure/ผู้ใช้สอนไว้ ต้องถูก<b>ใช้</b>เมื่อ engine ในเครื่องอ่านไม่ได้
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11 "ยิ่ง Azure ทำงานเยอะ local ยิ่งเก่งขึ้นไหม") ═══ คลัง
/// <c>VendorKnownGoodValues</c> ถูกเขียนทุกใบที่ Azure อ่าน แต่ฝั่งอ่านใช้แค่ "แทนค่าที่อ่านเพี้ยน"
/// ⇒ ใบ POS ที่ Tesseract/python ไม่ได้ที่อยู่ผู้ขายมาเลย ไม่เคยได้ประโยชน์จากคลังนี้</para>
///
/// <para>ครึ่งแรก = เคสที่ต้องเติม · ครึ่งหลัง = ด่านกันคลังเอียง (DECISION_DOCTRINE §3.1) ต้อง<b>ไม่เติม</b>:
/// Azure ใบเดียวที่ไม่มีใครยืนยัน · หลายที่อยู่ · หลายสาขา · สาขาไม่ตรง · ที่อยู่เรา/ผู้ซื้อที่ปนเข้าคลัง</para>
/// </summary>
public class OcrKnownGoodAddressFillTests
{
    private const string WineProHq = "12/861 Moo 15 Bangkaew, Bangplee, Samutprakarn 10540";
    private const string OurAddress = "202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110";

    private static OcrKnownGoodRow Addr(string v, int count, string source = "AzureDI")
        => new(OcrKnownGoodAddressFill.AddressField, v, count, source);

    private static OcrKnownGoodRow Branch(string v)
        => new(OcrKnownGoodAddressFill.BranchField, v, 1, "AzureDI");

    // ═════════ ครึ่งแรก: ต้องเติม ═════════

    [Fact]
    public void Azure_อ่านที่อยู่เดิมได้สองใบ_สาขาตรง_ต้องเติมพร้อมไฮไลต์()
    {
        var r = OcrKnownGoodAddressFill.Decide(null, "00012",
            new[] { Addr(WineProHq, 2), Branch("00012") }, OurAddress);
        Assert.Equal(WineProHq, r.Value);
        Assert.Equal(OcrKnownGoodAddressFill.FillConfidence, r.Confidence);
        Assert.True(r.Confidence < 0.85m);
    }

    [Fact]
    public void ผู้ใช้เคยยืนยัน_ใบเดียวก็พอ_และชนะค่าที่_Azure_อ่านต่าง()
    {
        var r = OcrKnownGoodAddressFill.Decide("", null, new[]
        {
            Addr("12/861 Moo 15 Bangkaew Bangplee", 3),                 // Azure อ่านขาดท้าย 3 ใบ
            Addr(WineProHq, 1, "UserCorrection"),                        // ผู้ใช้แก้ให้ครบ
        }, OurAddress);
        Assert.Equal(WineProHq, r.Value);
    }

    // ═════════ ครึ่งหลัง: ด่านกันคลังเอียง — ห้ามเติม ═════════

    [Fact]
    public void มีที่อยู่จากใบนี้แล้ว_ห้ามทับด้วยประวัติ()
        => Assert.Null(OcrKnownGoodAddressFill.Decide("854/2 ถนนบุรีรัมย์ ต.ชะอำ", null,
            new[] { Addr(WineProHq, 5) }).Value);

    [Fact]
    public void Azure_ใบเดียวที่ยังไม่มีใครยืนยัน_ห้ามกลายเป็นค่าที่เติมเอง()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, null, new[] { Addr(WineProHq, 1) }).Value);

    [Fact]
    public void ผู้ขายมีที่อยู่ที่เชื่อได้สองแห่ง_ไม่รู้ว่าใบนี้ของที่ไหน()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, null, new[]
        {
            Addr(WineProHq, 2),
            Addr("99 ถ.สุขุมวิท แขวงคลองเตย กรุงเทพฯ 10110", 2),
        }).Value);

    [Fact]
    public void ผู้ขายเคยออกใบจากหลายสาขา_ห้ามเติม()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, null, new[]
        {
            Addr(WineProHq, 3), Branch("00000"), Branch("00012"),
        }).Value);

    [Fact]
    public void Radisson_ใบของสาขาที่8_แต่ประวัติเป็นสำนักงานใหญ่_ห้ามเติมที่อยู่สำนักงานใหญ่()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, "00008", new[]
        {
            Addr("200 อาคารจัสมินอินเตอร์เนชั่นแนลทาวเวอร์ ถ.แจ้งวัฒนะ จ.นนทบุรี", 2), Branch("00000"),
        }).Value);

    [Fact]
    public void คลังเก่าปนที่อยู่ของเรา_ห้ามเติมที่อยู่เราเป็นที่อยู่ผู้ขาย()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, null,
            new[] { Addr(OurAddress, 4) }, OurAddress).Value);

    [Fact]
    public void ที่อยู่ในประวัติตรงกับที่อยู่ผู้ซื้อในใบนี้_ห้ามเติม()
        => Assert.Null(OcrKnownGoodAddressFill.Decide(null, null,
            new[] { Addr("202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", 2) },
            ourAddress: null,
            buyerAddress: "202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110").Value);
}
