using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 T0 — ตัวแปลงค่าไฟล์นำเข้า <see cref="ImportValueNormalizer"/> + ตัวเลือกตัวแปลงตามชนิด field <see cref="ImportCellNormalizer"/>
/// · แทน <c>DateTime.TryParse</c>/<c>decimal.TryParse</c>/<c>bool.TryParse</c> ที่ขึ้นกับ culture ("ใช่" = false · dd/MM↔MM/dd สลับเงียบ) ·
/// ทุกกลุ่มล็อกสองทิศ (ค่าที่ต้องแปลง/ต้องจับ กับค่าที่ถูกอยู่แล้วต้องไม่ถูกแตะ) · ค่าเพี้ยน = issue ไม่ใช่ throw
/// </summary>
public class ImportValueNormalizerTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // ═══ วันที่ ═══

    [Fact]
    public void วันที่ไทย_พศ_อ่านเป็น_วัน_เดือน_และติดคำเตือนเมื่อสลับได้จริง()
    {
        var v = ImportValueNormalizer.Date("05/08/2569");
        Assert.True(v.Ok);
        Assert.Equal(D(2026, 8, 5), v.Value);
        Assert.True(v.IsWarning);
        Assert.Equal("date-order-assumed", v.IssueCode);
    }

    [Fact]
    public void วันที่ที่ไม่กำกวม_ไม่ติดคำเตือน()
    {
        var v = ImportValueNormalizer.Date("25/08/2569");
        Assert.Equal(D(2026, 8, 25), v.Value);
        Assert.False(v.IsWarning);
    }

    [Fact]
    public void ISO_ไม่สลับวันเดือน()
    {
        var v = ImportValueNormalizer.Date("2026-08-05");
        Assert.Equal(D(2026, 8, 5), v.Value);
        Assert.Null(v.IssueCode);
    }

    [Fact]
    public void ระบุ_เดือน_วัน_อ่านตามที่ระบุ_ไม่ติดคำเตือน()
    {
        var v = ImportValueNormalizer.Date("08/05/2026", ImportDateOrder.MonthDayYear);
        Assert.Equal(D(2026, 8, 5), v.Value);
        Assert.Null(v.IssueCode);
        // ไทยระบุชัด (DayMonthYear) = ไม่เตือน
        Assert.Null(ImportValueNormalizer.Date("05/08/2026", ImportDateOrder.DayMonthYear).IssueCode);
    }

    [Fact]
    public void Excel_serial_อ่านเป็นวันที่()
    {
        Assert.Equal(D(2026, 8, 5), ImportValueNormalizer.Date("46239").Value);
        Assert.Equal(D(2026, 8, 5), ImportValueNormalizer.Date("46239.75").Value);   // มีเวลา ⇒ ตัดเป็นวัน
    }

    [Theory]
    [InlineData("๐๕/๐๘/๒๕๖๙")]                // เลขไทย
    [InlineData("05-Aug-2026")]                // ชื่อเดือนอังกฤษ
    [InlineData("5 สิงหาคม 2569")]             // ชื่อเดือนไทย
    [InlineData("05/08/2569 10:30")]           // มีเวลาท้าย
    public void วันที่รูปแบบอื่นที่พบจริง_อ่านได้(string raw)
        => Assert.Equal(D(2026, 8, 5), ImportValueNormalizer.Date(raw).Value);

    [Theory]
    [InlineData("32/13/2569")]
    [InlineData("พรุ่งนี้")]
    [InlineData("2569")]
    public void วันที่เพี้ยน_เป็น_issue_ไม่_throw(string raw)
    {
        var v = ImportValueNormalizer.Date(raw);
        Assert.False(v.Ok);
        Assert.Null(v.Value);
        Assert.Equal("date-invalid", v.IssueCode);
        Assert.False(string.IsNullOrWhiteSpace(v.Message));
    }

    [Fact]
    public void วันที่ว่าง_คือไม่มีค่า_ไม่ใช่ข้อผิดพลาด()
    {
        var v = ImportValueNormalizer.Date("   ");
        Assert.True(v.Ok);
        Assert.Null(v.Value);
        Assert.Null(v.IssueCode);
    }

    [Fact]
    public void วันที่รูปมาตรฐาน_ISO_ไม่ขึ้นกับ_culture()
        => Assert.Equal("2026-08-05", ImportValueNormalizer.CanonicalDate(D(2026, 8, 5)));

    // ═══ จำนวนเงิน / ตัวเลข ═══

    [Theory]
    [InlineData("(1,000.00)", -1000)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("๑,๒๐๐", 1200)]          // เลขไทย
    [InlineData("฿ 99.995", 100.00)]       // ปัด AwayFromZero
    [InlineData("1,000.00-", -1000)]
    public void จำนวนเงิน_อ่านได้ทุกรูปที่พบจริง(string raw, double expected)
    {
        var v = ImportValueNormalizer.Amount(raw);
        Assert.True(v.Ok);
        Assert.Equal((decimal)expected, v.Value);
    }

    [Fact]
    public void จำนวนเงินเพี้ยน_เป็น_issue()
    {
        var v = ImportValueNormalizer.Amount("หนึ่งพัน");
        Assert.False(v.Ok);
        Assert.Equal("amount-invalid", v.IssueCode);
    }

    [Fact]
    public void จำนวนเงินรูปมาตรฐาน_invariant()
        => Assert.Equal("-1234.5", ImportValueNormalizer.CanonicalAmount(-1234.5m));

    [Fact]
    public void ตัวเลขไม่ใช่เงิน_ไม่ปัดเศษ()
    {
        Assert.Equal(1.125m, ImportValueNormalizer.Number("1.125").Value);
        Assert.Equal(-2500m, ImportValueNormalizer.Number("-2,500").Value);
        Assert.False(ImportValueNormalizer.Number("1,2,3").Ok);
    }

    // ═══ ใช่/ไม่ใช่ ═══

    [Theory]
    [InlineData("ใช่", true)]
    [InlineData("Y", true)]
    [InlineData("y", true)]
    [InlineData("1", true)]
    [InlineData("✓", true)]
    [InlineData("มี", true)]
    [InlineData("TRUE", true)]
    [InlineData("ไม่ใช่", false)]
    [InlineData("N", false)]
    [InlineData("0", false)]
    [InlineData("ไม่มี", false)]
    [InlineData("false", false)]
    public void ใช่_ไม่ใช่_ทุกรูป(string raw, bool expected)
    {
        var v = ImportValueNormalizer.Flag(raw);
        Assert.True(v.Ok);
        Assert.Equal(expected, v.Value);
    }

    [Fact]
    public void คำที่ไม่ใช่ใช่หรือไม่ใช่_ไม่เดา()
    {
        var v = ImportValueNormalizer.Flag("อาจจะ");
        Assert.False(v.Ok);
        Assert.Null(v.Value);
        Assert.Equal("flag-invalid", v.IssueCode);
    }

    // ═══ เลขผู้เสียภาษี ═══

    [Fact]
    public void เลขผู้เสียภาษีถูกต้อง_ตัดขีดออก_ผ่าน()
    {
        var good = TaxIdOf("010555000000");
        var v = ImportValueNormalizer.TaxId(good[..1] + "-" + good[1..5] + "-" + good[5..10] + "-" + good[10..12] + "-" + good[12..]);
        Assert.True(v.Ok);
        Assert.Equal(good, v.Value);
        Assert.Null(v.IssueCode);
    }

    [Fact]
    public void เลขผู้เสียภาษีจาก_Excel_รูป_scientific_เสียหลักแล้ว_ไม่มีค่าแนะนำ()
    {
        var v = ImportValueNormalizer.TaxId("1.23457E+12");
        Assert.False(v.Ok);
        Assert.Equal("taxid-lost-precision", v.IssueCode);
        Assert.Null(v.Suggested);   // กู้ไม่ได้ — ห้ามแต่งเลข
    }

    [Fact]
    public void เลข_12_หลักที่_Excel_ตัดศูนย์นำหน้า_แนะนำ_ไม่ใส่เอง()
    {
        var good = TaxIdOf("010555000000");
        var v = ImportValueNormalizer.TaxId(good[1..]);
        Assert.False(v.Ok);
        Assert.Null(v.Value);
        Assert.Equal("taxid-leading-zero", v.IssueCode);
        Assert.Equal(good, v.Suggested);
    }

    [Fact]
    public void เลขผู้เสียภาษี_checksum_ผิด_เป็น_issue()
    {
        var good = TaxIdOf("010555000000");
        var bad = good[..12] + (char)('0' + (good[12] - '0' + 1) % 10);
        var v = ImportValueNormalizer.TaxId(bad);
        Assert.False(v.Ok);
        Assert.Equal("taxid-invalid", v.IssueCode);
    }

    /// <summary>เลข 13 หลักที่ checksum ถูก (คิดจากตัวกลาง ThaiTaxId — ไม่เขียนสูตรเอง)</summary>
    private static string TaxIdOf(string first12)
    {
        for (var c = 0; c <= 9; c++)
        {
            var id = first12 + c;
            if (ThaiTaxIdValidator.Check(id).IsValid) return id;
        }
        throw new InvalidOperationException("ไม่มีหลักตรวจที่ผ่าน");
    }

    // ═══ สาขา ═══

    [Theory]
    [InlineData("สำนักงานใหญ่", "00000")]
    [InlineData("สนญ.", "00000")]
    [InlineData("HQ", "00000")]
    [InlineData("head office", "00000")]
    [InlineData("0", "00000")]
    [InlineData("3", "00003")]
    [InlineData("สาขาที่ 3", "00003")]
    [InlineData("สาขา 00012", "00012")]
    [InlineData("Branch 7", "00007")]
    [InlineData("00001", "00001")]
    public void รหัสสาขา_ทุกรูป(string raw, string expected)
    {
        var v = ImportValueNormalizer.BranchCode(raw);
        Assert.True(v.Ok);
        Assert.Equal(expected, v.Value);
    }

    [Fact]
    public void รหัสสาขาเพี้ยน_เป็น_issue()
        => Assert.Equal("branch-invalid", ImportValueNormalizer.BranchCode("สาขาลาดพร้าว").IssueCode);

    // ═══ VAT ═══

    [Theory]
    [InlineData("7%", 7)]
    [InlineData("7", 7)]
    [InlineData("0.07", 7)]     // เซลล์เปอร์เซ็นต์ของ Excel
    [InlineData("0", 0)]
    public void อัตรา_VAT(string raw, double expected)
    {
        var v = ImportValueNormalizer.VatRate(raw);
        Assert.True(v.Ok);
        Assert.Equal((decimal)expected, v.Value);
        Assert.Null(v.IssueCode);
    }

    [Fact]
    public void ยกเว้น_VAT_ไม่ใช่อัตรา_0()
    {
        var v = ImportValueNormalizer.VatRate("ยกเว้น");
        Assert.True(v.Ok);
        Assert.Null(v.Value);
        Assert.Equal("vat-exempt", v.IssueCode);
    }

    [Fact]
    public void อัตรา_VAT_แปลก_ติดคำเตือน_และอ่านไม่ได้เป็น_issue()
    {
        Assert.Equal("vat-rate-unusual", ImportValueNormalizer.VatRate("10%").IssueCode);
        Assert.Equal("vat-rate-invalid", ImportValueNormalizer.VatRate("เจ็ด").IssueCode);
    }

    // ═══ ติดต่อ / ข้อความ / enum / เลขอ้างอิง ═══

    [Fact]
    public void เบอร์โทร_อีเมล()
    {
        Assert.Equal("0812345678", ImportValueNormalizer.Phone("+66 81-234-5678").Value);
        Assert.Equal("021234567", ImportValueNormalizer.Phone("02-123-4567").Value);
        Assert.Equal("phone-unusual", ImportValueNormalizer.Phone("123").IssueCode);   // ไม่ทิ้งข้อมูล — เก็บตามไฟล์
        Assert.Equal("123", ImportValueNormalizer.Phone("123").Value);
        Assert.Equal("a@b.co.th", ImportValueNormalizer.Email(" A@B.co.th ").Value);
        Assert.Equal("email-invalid", ImportValueNormalizer.Email("a@b").IssueCode);
    }

    [Fact]
    public void ข้อความ_ยุบช่องว่าง_และตัดความยาวพร้อมคำเตือน()
    {
        Assert.Equal("ร้าน ทดสอบ", ImportValueNormalizer.Text("  ร้าน   ทดสอบ ").Value);
        var t = ImportValueNormalizer.Text("abcdef", 3);
        Assert.Equal("abc", t.Value);
        Assert.Equal("text-truncated", t.IssueCode);
    }

    [Fact]
    public void enum_ชื่อ_คำพ้อง_เลข_และค่าที่ไม่รู้จัก()
    {
        var aliases = new Dictionary<string, ImportRowAction> { ["สร้างใหม่"] = ImportRowAction.Create };
        Assert.Equal(ImportRowAction.Update, ImportValueNormalizer.Enum<ImportRowAction>("update").Value);
        Assert.Equal(ImportRowAction.Create, ImportValueNormalizer.Enum("สร้างใหม่", aliases).Value);
        Assert.Equal(ImportRowAction.Skip, ImportValueNormalizer.Enum<ImportRowAction>("3").Value);
        var bad = ImportValueNormalizer.Enum<ImportRowAction>("99");
        Assert.False(bad.Ok);
        Assert.Equal("enum-invalid", bad.IssueCode);
    }

    [Fact]
    public void เลขอ้างอิงจาก_Excel_เสียหลัก_จับได้_เลขปกติไม่ถูกแตะ()
    {
        Assert.Equal("reference-lost-precision", ImportValueNormalizer.Reference("1.23457E+17").IssueCode);
        Assert.Equal("INV-2026-0001", ImportValueNormalizer.Reference("INV-2026-0001").Value);
        Assert.Null(ImportValueNormalizer.Reference("5E10").IssueCode);   // ตัวอักษรปนตัวเลขไม่ใช่ scientific ของ Excel
    }

    // ═══ ตัวเลือกตัวแปลงตามชนิด field (ImportCellNormalizer) ═══

    private static ImportFieldDto F(ImportFieldType type, bool required = false, params string[] allowed)
        => new("f", "ช่องทดสอบ", type, required, AllowedValues: allowed.Length == 0 ? null : allowed);

    [Fact]
    public void เซลล์_แปลงรูปแล้ว_เป็นสีเขียว_ค่าเดิมเก็บไว้()
    {
        var r = ImportCellNormalizer.Normalize(F(ImportFieldType.Amount), "1,234.50");
        Assert.Equal("1234.50", r.Cell.Value);
        Assert.Equal("1,234.50", r.Cell.Raw);
        Assert.Equal(ImportCellState.Normalized, r.Cell.State);
        Assert.Null(r.Issue);
        Assert.False(r.Cell.UsedAi);
    }

    [Fact]
    public void เซลล์_ค่าตรงรูปอยู่แล้ว_ไม่ถูกแตะ()
    {
        var r = ImportCellNormalizer.Normalize(F(ImportFieldType.Date), "2026-08-05");
        Assert.Equal("2026-08-05", r.Cell.Value);
        Assert.Equal(ImportCellState.Original, r.Cell.State);
        Assert.Null(r.Issue);
    }

    [Fact]
    public void เซลล์_ช่องบังคับเพี้ยน_Error_ช่องไม่บังคับ_Warning()
    {
        var req = ImportCellNormalizer.Normalize(F(ImportFieldType.Date, required: true), "พรุ่งนี้");
        Assert.Equal(ImportIssueSeverity.Error, req.Issue!.Severity);
        Assert.Equal(ImportCellState.Error, req.Cell.State);
        Assert.Null(req.Cell.Value);
        var opt = ImportCellNormalizer.Normalize(F(ImportFieldType.Date), "พรุ่งนี้");
        Assert.Equal(ImportIssueSeverity.Warning, opt.Issue!.Severity);
        Assert.Equal(ImportIssueSource.Normalizer, opt.Issue.Source);
    }

    [Fact]
    public void เซลล์_ช่องบังคับว่าง_Error_ช่องไม่บังคับว่าง_ไม่มี_issue()
    {
        Assert.Equal("required-missing", ImportCellNormalizer.Normalize(F(ImportFieldType.Text, required: true), " ").Issue!.Code);
        Assert.Null(ImportCellNormalizer.Normalize(F(ImportFieldType.Text), "").Issue);
    }

    [Fact]
    public void เซลล์_คำแนะนำส่งต่อถึงสัญญา_issue()
    {
        var good = TaxIdOf("010555000000");
        var r = ImportCellNormalizer.Normalize(F(ImportFieldType.TaxId), good[1..]);
        Assert.Equal("taxid-leading-zero", r.Issue!.Code);
        Assert.Equal(good, r.Issue.Suggested);
    }

    [Fact]
    public void เซลล์_flag_และ_enum_จากชุดที่รับได้_และแผนที่ค่า()
    {
        Assert.Equal("true", ImportCellNormalizer.Normalize(F(ImportFieldType.Flag), "ใช่").Cell.Value);
        Assert.Equal("Customer", ImportCellNormalizer.Normalize(F(ImportFieldType.Enum, false, "Customer", "Supplier"), "customer").Cell.Value);
        Assert.Equal("enum-invalid", ImportCellNormalizer.Normalize(F(ImportFieldType.Enum, false, "Customer", "Supplier"), "ลูกค้า").Issue!.Code);
        var map = new Dictionary<string, string> { ["ลูกค้า"] = "Customer" };
        Assert.Equal("Customer",
            ImportCellNormalizer.Normalize(F(ImportFieldType.Enum, false, "Customer", "Supplier"), "ลูกค้า", valueMap: map).Cell.Value);
    }

    [Fact]
    public void เซลล์_ยกเว้นภาษี_เป็นข้อมูลประกอบ_ไม่ใช่คำเตือนที่ต้องรับทราบ()
        => Assert.Equal(ImportIssueSeverity.Info, ImportCellNormalizer.Normalize(F(ImportFieldType.VatRate), "ยกเว้น").Issue!.Severity);
}
