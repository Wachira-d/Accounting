using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกตัวจำแนกบรรทัดรายงานภาษีตัวเดียวของระบบ — เคสตั้งต้นจากผู้ใช้ (2026-09-10):
/// รายงานภาษีขายเดือน 08/2569 มีแถวแรกเป็น "ยอดซื้อที่ได้รับยกเว้นภาษี (§81)"
/// ลงวันที่ 01/01/0544 ยอด 7,151 และถูกบวกเข้ายอดรวม 3,234,571.84
///
/// <para>เทสต์ล็อก<b>สองทิศ</b>: แถวยอดรวมฝั่งซื้อ/เครดิตยกมาต้องหลุดจากรายงานขาย
/// **และ** แถวใบกำกับขายจริงต้องยังอยู่ครบ (แก้แล้วต้องไม่กลายเป็นตัดทิ้งทั้งกอง)</para>
/// </summary>
public class VatReportLineKindTests
{
    [Theory]
    [InlineData("OUTPUT", VatReportSide.Output)]
    [InlineData("JE_OUTPUT", VatReportSide.Output)]
    [InlineData(null, VatReportSide.Output)]          // บรรทัดรุ่นเก่าไม่มีรหัส = ภาษีขาย
    [InlineData("", VatReportSide.Output)]
    [InlineData("INPUT", VatReportSide.Input)]
    [InlineData("JE_INPUT", VatReportSide.Input)]
    [InlineData("EXEMPT", VatReportSide.Summary)]     // ← ยอด "ซื้อ" ยกเว้น (ตัวที่หลุดเข้ารายงานขาย)
    [InlineData("EXEMPT_SALES", VatReportSide.Summary)]
    [InlineData("ZERO_RATED_SALES", VatReportSide.Summary)]
    [InlineData("VAT_CREDIT_CF", VatReportSide.Summary)]
    [InlineData("SUMMARY", VatReportSide.Summary)]
    [InlineData("TAX_CREDIT", VatReportSide.Summary)]
    public void จำแนกฝั่งของทุกรหัสที่ระบบสร้างจริง(string? code, VatReportSide expected)
        => Assert.Equal(expected, VatReportLineKind.SideOf(code));

    [Fact]
    public void แถวยอดรวมห้ามเป็นรายการเอกสารในตาราง87()
    {
        Assert.False(VatReportLineKind.IsDocumentRow("EXEMPT"));
        Assert.False(VatReportLineKind.IsDocumentRow("EXEMPT_SALES"));
        Assert.False(VatReportLineKind.IsDocumentRow("ZERO_RATED_SALES"));
        Assert.False(VatReportLineKind.IsDocumentRow("VAT_CREDIT_CF"));
        Assert.True(VatReportLineKind.IsDocumentRow("OUTPUT"));
        Assert.True(VatReportLineKind.IsDocumentRow("INPUT"));
        Assert.True(VatReportLineKind.IsDocumentRow(null));
    }

    [Fact]
    public void รายงานภาษีขายรับเฉพาะแถวฝั่งขาย_ยอดซื้อยกเว้นและเครดิตยกมาต้องหลุด()
    {
        // เคสจริงที่ผู้ใช้เจอ
        Assert.False(VatReportLineKind.BelongsToDetailReport("EXEMPT", wantSales: true));
        Assert.False(VatReportLineKind.BelongsToDetailReport("VAT_CREDIT_CF", wantSales: true));
        // ทิศตรงข้าม: ใบกำกับขายต้องยังอยู่
        Assert.True(VatReportLineKind.BelongsToDetailReport("OUTPUT", wantSales: true));
        Assert.True(VatReportLineKind.BelongsToDetailReport(null, wantSales: true));
        Assert.True(VatReportLineKind.BelongsToDetailReport("JE_OUTPUT", wantSales: true));
        // และรายงานภาษีซื้อยังได้แถวฝั่งซื้อครบ (รวม JE_INPUT ที่เคยตกหล่น)
        Assert.True(VatReportLineKind.BelongsToDetailReport("INPUT", wantSales: false));
        Assert.True(VatReportLineKind.BelongsToDetailReport("JE_INPUT", wantSales: false));
        Assert.False(VatReportLineKind.BelongsToDetailReport("OUTPUT", wantSales: false));
        Assert.False(VatReportLineKind.BelongsToDetailReport("EXEMPT", wantSales: false));
    }

    [Fact]
    public void ยอดที่ตัดออกจากตารางต้องไปโผล่เป็นหมายเหตุของฝั่งที่ถูก()
    {
        // ยอดซื้อยกเว้น = หมายเหตุของรายงานภาษีซื้อ ไม่ใช่ของขาย (ห้ามหายเงียบ)
        Assert.True(VatReportLineKind.IsNoteFor("EXEMPT", salesReport: false));
        Assert.False(VatReportLineKind.IsNoteFor("EXEMPT", salesReport: true));
        // ยอดขาย 0%/ยกเว้น = หมายเหตุของรายงานภาษีขาย
        Assert.True(VatReportLineKind.IsNoteFor("EXEMPT_SALES", salesReport: true));
        Assert.True(VatReportLineKind.IsNoteFor("ZERO_RATED_SALES", salesReport: true));
        Assert.False(VatReportLineKind.IsNoteFor("ZERO_RATED_SALES", salesReport: false));
        // เครดิตยกมาเป็นเรื่องของแบบ ภ.พ.30 เท่านั้น
        Assert.False(VatReportLineKind.IsNoteFor("VAT_CREDIT_CF", salesReport: true));
        Assert.False(VatReportLineKind.IsNoteFor("VAT_CREDIT_CF", salesReport: false));
        // แถวรายการเอกสารไม่ใช่หมายเหตุ
        Assert.False(VatReportLineKind.IsNoteFor("OUTPUT", salesReport: true));
    }

    [Theory]
    [InlineData("ZERO_RATED_SALES", Pp30SalesBox.ZeroRated)]   // ช่อง 7
    [InlineData("EXEMPT_SALES", Pp30SalesBox.Exempt)]          // ช่อง 8
    [InlineData("EXEMPT", Pp30SalesBox.None)]                  // ยอด "ซื้อ" — ไม่เข้าช่องใด
    [InlineData("VAT_CREDIT_CF", Pp30SalesBox.None)]
    [InlineData("INPUT", Pp30SalesBox.None)]
    [InlineData("OUTPUT", Pp30SalesBox.Standard7)]
    [InlineData(null, Pp30SalesBox.Standard7)]
    public void ช่องของแบบภพ30ตัดสินจากรหัส_ไม่ใช่จากอัตราภาษี(string? code, Pp30SalesBox expected)
        => Assert.Equal(expected, VatReportLineKind.SalesBoxOf(code));

    [Fact]
    public void ช่อง5คิดแบบลบ_รวมสามช่องได้ยอดขายทั้งหมดพอดี()
    {
        // ใบผสม: ฐาน 7% = 100,000 · ฐาน 0% (ส่งออก) = 40,000 · ยกเว้น §81 = 7,000
        // แถวรายการเก็บ SubTotal − ยกเว้น = 140,000 (ยังรวมฐาน 0% ไว้)
        const decimal documentRows = 140_000m, zero = 40_000m, exempt = 7_000m;
        var std = VatReportLineKind.StandardBase(documentRows, zero);
        Assert.Equal(100_000m, std);
        Assert.Equal(147_000m, std + zero + exempt);   // = SubTotal ทั้งหมด
    }

    [Fact]
    public void รหัสตัวพิมพ์เล็ก_เว้นวรรค_ต้องจำแนกได้เหมือนกัน()
    {
        Assert.Equal(VatReportSide.Input, VatReportLineKind.SideOf(" je_input "));
        Assert.Equal(VatReportSide.Summary, VatReportLineKind.SideOf("exempt"));
    }
}
