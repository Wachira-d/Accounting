using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 2026-10-14 ทีม X1 — ไฟล์รายการ ภ.พ.36 จากตัวตั้งเดียว <see cref="Pp36FileFormat"/> (เดิมสองปุ่มให้คนละหน้าตา: H|D|T + dd/MM/พ.ศ. +
/// "ประเทศ" ที่จริงเป็นจังหวัด กับ บรรทัดหัวสรุป + ddMMyyyy) · ล็อกทั้งรูปแบบใหม่และว่ารูปแบบเก่าไม่กลับมา
/// </summary>
public class Pp36FileFormatTests
{
    private static Pp36FileFormat.Row Booking() => new(
        SupplierName: "Booking.com B.V.", CountryCode: "nl", PayDate: new DateTime(2026, 9, 15),
        DocumentNo: "PV-20260901-0001", Description: "Commission | Sep", BaseAmount: 5908m, VatRate: 7m, VatAmount: 413.56m);

    [Fact]
    public void ไม่มีบรรทัดหัวท้าย_วันที่ddMMyyyy_ประเทศเป็นรหัส()
    {
        var body = Pp36FileFormat.Build(new[] { Booking(), Booking() });
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("H|") || l.StartsWith("T|") || l.StartsWith("D|") || l.StartsWith("PP36"));
        var c = lines[0].Split('|');
        Assert.Equal(Pp36FileFormat.Headers.Count, c.Length);
        Assert.Equal("1", c[0]);
        Assert.Equal("Booking.com B.V.", c[1]);
        Assert.Equal("NL", c[2]);
        Assert.Equal("15092026", c[3]);                // ไม่ใช่ 15/09/2569
        Assert.Equal("PV-20260901-0001", c[4]);
        Assert.Equal("Commission   Sep", c[5]);        // pipe ในข้อความถูกตัด ไม่แตกคอลัมน์
        Assert.Equal("5908.00", c[6]);
        Assert.Equal("7.00", c[7]);
        Assert.Equal("413.56", c[8]);
        Assert.Equal("2", lines[1].Split('|')[0]);
    }

    [Fact]
    public void ไม่รู้ประเทศ_ปล่อยว่าง_ไม่แต่งคำว่าForeign()
    {
        var c = Pp36FileFormat.DetailRow(1, Booking() with { CountryCode = null }).Split('|');
        Assert.Equal("", c[2]);
    }
}
