using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-09 — แถวสรุป/แถวชำระ/เงินทอนที่ engine คืนมาปนในตารางรายการ (สลิป POS · ตาราง layout ของ Azure · python ocr-service)
/// ต้องไม่กลายเป็นบรรทัดสินค้า — และ**สินค้าจริงที่ชื่อขึ้นต้นคล้ายป้าย** ต้องไม่ถูกตัด (ครึ่งทิศตรงข้าม)
/// </summary>
public class OcrNonItemRowTests
{
    // ── ครึ่งที่ 1: แถวที่สลิป POS จริงพิมพ์ต่อท้ายตาราง — ต้องถูกตัด ──────────────────────────────────────

    [Theory]
    [InlineData("เงินสด 1,000.00")]
    [InlineData("เงินทอน 226.00")]
    [InlineData("รวม 3 รายการ")]
    [InlineData("รวม 3 รายการ 774.00")]
    [InlineData("ยอดสุทธิ")]
    [InlineData("ภาษีมูลค่าเพิ่ม 7%")]
    [InlineData("VAT 7%  48.10")]
    [InlineData("TOTAL")]
    [InlineData("Sub Total")]
    [InlineData("Card")]                                   // แถวชำระด้วยบัตรบนใบ Wine Pro
    [InlineData("Cash 1,000.00")]
    [InlineData("CHANGE 226.00")]
    [InlineData("บัตรเครดิต VISA ****1234")]
    [InlineData("Credit Card VISA")]
    [InlineData("พร้อมเพย์")]
    [InlineData("ส่วนลดท้ายบิล")]
    [InlineData("รวมเงินทั้งสิ้น")]
    [InlineData("ยอดรวม 2 ชิ้น")]
    public void แถวสรุปและแถวชำระของสลิปPOS_ไม่ใช่สินค้า(string description)
        => Assert.True(OcrNonItemRow.IsSummaryOrTenderRow(description), description);

    // ── ครึ่งที่ 2: สินค้าจริงที่ชื่อขึ้นต้นด้วยคำเดียวกัน — ต้องคงไว้ ─────────────────────────────────────

    [Theory]
    [InlineData("รวมมิตรทะเล")]                             // อาหาร — ไม่ใช่ "รวม"
    [InlineData("ทอนสเตนคาร์ไบด์ 10 มม.")]                  // วัสดุ — ไม่ใช่ "ทอน"
    [InlineData("ภาษีป้าย (ค่าธรรมเนียม)")]                 // ค่าใช้จ่ายจริง — ไม่ใช่ "ภาษี"
    [InlineData("Cash Drawer Tray")]
    [InlineData("Cashew nuts 500g")]
    [InlineData("Visa application fee")]
    [InlineData("Total Care Package")]
    [InlineData("Card holder leather")]
    [InlineData("น้ำยาล้างจาน 3,600 มล.")]
    [InlineData("ค่าบริการเปลี่ยนถ่ายน้ำมันเครื่อง")]
    [InlineData("เงินมัดจำค่าก่อสร้าง")]                    // บรรทัดมัดจำเป็นรายการจริง (OcrDepositMarker)
    [InlineData("ค่าจัดส่ง / Shipping Fee")]
    [InlineData("Service Charge 10%")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    public void สินค้าจริงและแถวที่ไม่รู้_คงไว้(string description)
        => Assert.False(OcrNonItemRow.IsSummaryOrTenderRow(description), description);

    [Fact]
    public void เหตุผลบอกป้ายที่ตัดและคำที่ทำให้ไม่ตัด()
    {
        var (drop, why) = OcrNonItemRow.Judge("เงินทอน 226.00");
        Assert.True(drop);
        Assert.Contains("เงินทอน", why);
        var (keep, whyKeep) = OcrNonItemRow.Judge("Visa application fee");
        Assert.False(keep);
        Assert.Contains("application", whyKeep);
        Assert.Null(OcrNonItemRow.Judge("น้ำปลา 700 มล.").Reason);
    }

    [Fact]
    public void ข้อความแจ้งตัดแถว_บอกจำนวนและชื่อแถว()
    {
        var note = OcrNonItemRow.DroppedNote(new[] { "เงินสด 1,000.00", "เงินทอน 226.00" });
        Assert.StartsWith("[Items]", note);
        Assert.Contains("2 แถว", note);
        Assert.Contains("เงินทอน 226.00", note);
    }

    // ── ชุดคำเดียวของเส้น OCR: ตัวแตกบรรทัดจากข้อความล้วนถาม MentionsSummaryLabel (หลวม) — ตารางจาก engine ถามตัวเข้ม ──

    [Theory]
    [InlineData("ค่าบริการรวมภาษีมูลค่าเพิ่ม 1,070.00")]       // ป้ายอยู่กลางบรรทัด — ตัวเข้มคงไว้ ตัวหลวมตัด
    [InlineData("หัก ณ ที่จ่าย 3% 300.00")]
    [InlineData("มูลค่าสินค้า 687.20")]
    [InlineData("รวมเงิน 3,000.00")]
    [InlineData("เงินทอน 290.00")]
    public void บรรทัดข้อความล้วนที่พูดถึงแถวสรุป_ตัวแตกบรรทัดต้องข้าม(string line)
    {
        Assert.True(OcrNonItemRow.MentionsSummaryLabel(line), line);
        Assert.Empty(RawTextLineSplitter.Split(line));
    }

    [Theory]
    [InlineData("รวมมิตรทะเล 120.00")]
    [InlineData("ค่าขนส่ง 700.00")]
    [InlineData("ภาษีป้าย (ค่าธรรมเนียม) 500.00")]            // เดิม SummaryMarkers "ภาษี" ตัดทิ้ง — ค่าใช้จ่ายจริง
    public void สินค้า_บริการจริง_ตัวแตกบรรทัดคงไว้(string line)
    {
        Assert.False(OcrNonItemRow.MentionsSummaryLabel(line), line);
        Assert.Single(RawTextLineSplitter.Split("หัวบิล\n" + line));
    }

    [Fact]
    public void รายการจริงของกระดาษในชุดreplay_ไม่มีบรรทัดไหนถูกตัด()
    {
        foreach (var p in OcrReplayHarness.Corpus)
            foreach (var line in p.Lines ?? Array.Empty<ReplayLine>())
                Assert.False(OcrNonItemRow.IsSummaryOrTenderRow(line.Description), $"{p.Name}: {line.Description}");
    }
}
