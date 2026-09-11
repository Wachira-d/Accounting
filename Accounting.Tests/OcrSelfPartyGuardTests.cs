using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **“ชื่อเราโผล่ในช่องคู่ค้า” ต้องตัดสินจากป้ายบนกระดาษ ไม่ใช่จากช่องที่ engine เลือก**
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-11 · scan c0f55862) ═══
/// บิลเงินสดเขียนมือ 3,500 บาทที่<b>ร้านออกให้เรา</b> — Azure DI หยิบชื่อในช่อง
/// “นาม” (= ลูกค้า) ไปใส่ <c>VendorName</c> ⇒ ระบบสรุป “เราเป็นผู้ขาย” (conf 0.75)
/// ⇒ เสนอสร้าง<b>ใบแจ้งหนี้ขาย</b>จากเงินที่<b>จ่ายออก</b> และจับคู่ Contact ได้เป็น
/// ตัวบริษัทเราเอง
/// </summary>
public class OcrSelfPartyGuardTests
{
    /// <summary>ข้อความจาก Azure DI ของใบจริง (ลำดับบรรทัดตามที่ engine คืนมาเป๊ะ —
    /// สังเกตว่า<b>ค่าอยู่ก่อนป้าย</b> “บจก. …” แล้วค่อย “นาม”)</summary>
    private const string RealPaper =
        "อ๊อฟ พิการ\n"
        + "เล่มที่\n"
        + "เลขที่\n"
        + "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี\n"
        + "บิลเงินสด\n"
        + "CASHSALE\n"
        + "บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)\n"
        + "นาม\n"
        + "วันที่\n"
        + "6/9 /69\n"
        + "NAME\n"
        + "DATE\n"
        + "ที่อยู่\n"
        + "202/24 ม.5 ซ. บ้านห้วยกุ่ม 4\n"
        + "0203562025871\n"
        + "ADDRESS\n"
        + "ต. บางพระ อ. ศรีราชา จ. ชลบุรี\n";

    private const string OurCompany = "หจก. แอม แฮปปี้เนส";

    [Fact]
    public void เคสจริง_ชื่อเราอยู่ใต้ป้ายนาม_ต้องสรุปว่าเราเป็นผู้ซื้อ()
    {
        var v = OcrSelfPartyGuard.FromPaperLabels(RealPaper, OurCompany);
        Assert.Equal(OcrSelfSide.Buyer, v.Side);
        Assert.Contains("ผู้ซื้อ", v.Reason);
    }

    [Fact]
    public void ป้ายอยู่หลังค่า_ก็ต้องนับ()
    {
        // แบบฟอร์มพิมพ์สำเร็จวางป้ายใต้เส้นประ ⇒ engine คืน “ค่า” ก่อน “ป้าย”
        // (บทเรียนเดิมของเรพ: “ป้ายกำกับอยู่ก่อนค่าเสมอ” เป็นสมมติฐานที่ผิด)
        var idxName = RealPaper.IndexOf("บจก.", System.StringComparison.Ordinal);
        var idxLabel = RealPaper.IndexOf("นาม", System.StringComparison.Ordinal);
        Assert.True(idxLabel > idxName, "ใบจริงใบนี้ป้ายอยู่หลังค่า — ถ้าไม่จริงแปลว่าแก้ข้อมูลเทสต์");
        Assert.Equal(OcrSelfSide.Buyer, OcrSelfPartyGuard.FromPaperLabels(RealPaper, OurCompany).Side);
    }

    [Fact]
    public void ใบที่เราออกเอง_ชื่อเราอยู่ใต้ป้ายผู้ขาย_ต้องสรุปว่าเราเป็นผู้ขาย()
        // ทิศตรงข้าม — ด่านใหม่ต้องไม่กลับทิศของใบที่เดิมถูกอยู่แล้ว
        => Assert.Equal(OcrSelfSide.Seller, OcrSelfPartyGuard.FromPaperLabels(
            "ใบกำกับภาษี\nผู้ขาย\nหจก. แอม แฮปปี้เนส\nผู้ซื้อ\nบริษัท ลูกค้า จำกัด\n",
            OurCompany).Side);

    [Fact]
    public void ไม่มีป้ายใกล้ชื่อเราเลย_ต้องบอกว่าไม่รู้_ห้ามเดา()
        // เดาผิดทิศเดียว = รายจ่ายกลายเป็นรายได้ — ไม่มีหลักฐาน = ไม่ตอบ
        => Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels(
            "หจก. แอม แฮปปี้เนส\nรายการสินค้า\nรวมเงิน 3500\n", OurCompany).Side);

    [Fact]
    public void ป้ายอยู่ไกลเกินหนึ่งบล็อก_ไม่นับ()
    {
        // “ด่านที่อิงความใกล้” ต้องใกล้จริง ไม่งั้นป้ายคนละบล็อกติดธงโดยบังเอิญ
        var far = "ผู้ซื้อ\n" + new string('x', OcrSelfPartyGuard.MaxLabelDistance + 40)
            + "\nหจก. แอม แฮปปี้เนส\n";
        Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels(far, OurCompany).Side);
    }

    [Fact]
    public void หาชื่อเราบนกระดาษไม่เจอ_ต้องไม่ตอบ()
        => Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels(
            "ผู้ซื้อ\nบริษัท คนอื่น จำกัด\n", OurCompany).Side);

    [Fact]
    public void ค่าว่าง_ต้องไม่พัง()
    {
        Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels(null, OurCompany).Side);
        Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels(RealPaper, null).Side);
        Assert.Equal(OcrSelfSide.Unknown, OcrSelfPartyGuard.FromPaperLabels("", "").Side);
    }

    // ── IsSelf / NameOverlaps ────────────────────────────────────────────
    [Fact]
    public void บจก_กับ_หจก_ของชื่อเดียวกัน_ต้องเทียบติด()
        // ตัว normalize เดิมตกคำว่า “บจก.” ⇒ ชื่อเดียวกันสองรูปเทียบไม่ติด
        => Assert.True(OcrSelfPartyGuard.IsSelf("บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)", OurCompany));

    [Fact]
    public void คนละบริษัท_ต้องไม่ถือว่าเป็นเรา()
        => Assert.False(OcrSelfPartyGuard.IsSelf("บริษัท ลักกี้ เวย์ จำกัด", OurCompany));

    [Fact]
    public void ชื่อสั้นเกินไป_ไม่นับเป็นหลักฐาน()
        => Assert.False(OcrSelfPartyGuard.IsSelf("บจก.", OurCompany));
}
