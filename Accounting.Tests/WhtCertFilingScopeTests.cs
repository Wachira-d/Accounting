using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>ล็อกว่า "สถานะไหนของ 50 ทวิ นับเข้าแบบยื่น" ตัดสินที่เดียว — ไล่ **ทุกค่า** ของ enum
/// ไม่ใช่เฉพาะสองค่าที่ตั้งใจ (บทเรียน: เทสต์ที่เขียนจากเคสที่เจอพลาดทิศที่ไม่เคยเจอ)</summary>
public class WhtCertFilingScopeTests
{
    [Fact]
    public void ทุกสถานะถูกตัดสิน_เฉพาะIssuedและPrintedที่นับเข้าแบบยื่น()
    {
        foreach (WithholdingTaxCertStatus s in Enum.GetValues(typeof(WithholdingTaxCertStatus)))
        {
            var expected = s is WithholdingTaxCertStatus.Issued or WithholdingTaxCertStatus.Printed;
            Assert.Equal(expected, WhtCertFilingScope.Filed.Contains(s));
        }
    }

    [Fact]
    public void Filed_ไม่มีค่าซ้ำและไม่ว่าง()
    {
        Assert.Equal(WhtCertFilingScope.Filed.Length, WhtCertFilingScope.Filed.Distinct().Count());
        Assert.NotEmpty(WhtCertFilingScope.Filed);
    }
}
