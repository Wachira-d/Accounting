using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ปีบนกระดาษ" → ปี ค.ศ. — ตัวแปลงกลางตัวเดียว
///
/// ═══ ที่มา ═══
/// กติกาเดียวกันถูกเขียนซ้ำในเรพด้วยเกณฑ์ที่ต่างกัน 4 แบบ
/// (<c>&gt; 2500</c> · <c>&gt;= 2400</c> · <c>&gt; 2400</c> · <c>&gt; currentYear + 10</c>)
/// ⇒ เอกสารใบเดียวกันที่เข้าคนละเส้นทาง OCR ลงคนละปีได้ — โดยเฉพาะปีย่อ:
/// "69" เส้นทางหนึ่งได้ 2069 อีกเส้นทางได้ 2026
/// </summary>
public class ThaiYearNormalizeTests
{
    [Theory]
    [InlineData(2569, 2026)]   // พ.ศ. 4 หลัก
    [InlineData(2568, 2025)]
    [InlineData(2026, 2026)]   // ค.ศ. 4 หลัก — คงเดิม
    [InlineData(1998, 1998)]
    [InlineData(69, 2026)]     // พ.ศ. ย่อ — เคสที่เส้นทางเก่าได้ 2069
    [InlineData(68, 2025)]
    [InlineData(60, 2017)]     // เส้นแบ่ง
    [InlineData(26, 2026)]     // ค.ศ. ย่อ
    [InlineData(59, 2059)]     // ต่ำกว่าเส้นแบ่ง = ค.ศ. ย่อ
    public void แปลงปีจากกระดาษเป็น_ค_ศ(int input, int expected)
        => Assert.Equal(expected, ThaiDate.NormalizeYear(input));

    [Fact]
    public void ปีย่อ_69_ต้องไม่กลายเป็น_2069()
    {
        // negative test ของบั๊กเดิม: `if (year > 2500) year -= 543; if (year < 100) year += 2000;`
        var legacy = 69;
        if (legacy > 2500) legacy -= 543;
        if (legacy < 100) legacy += 2000;
        Assert.Equal(2069, legacy);                       // ← พฤติกรรมเดิม
        Assert.Equal(2026, ThaiDate.NormalizeYear(69));   // ← ที่ถูก
    }
}
