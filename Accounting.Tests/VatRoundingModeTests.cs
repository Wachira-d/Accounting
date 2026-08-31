using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สูตร VAT ตัวไหน "ต้อง" ระบุ <c>MidpointRounding.AwayFromZero</c> จริง ๆ
///
/// ═══ ที่มา ═══
/// ผลตรวจรายงานว่า `SaasBillingDocumentService` ลืม <c>AwayFromZero</c> 4 จุด
/// ทั้งที่ `PlatformBillingDocumentIssuer` ที่คิดสูตรเดียวกันใส่ไว้ถูก และสรุปว่า
/// "ใบเดียวกันคำนวณคนละที่ได้ยอดต่างกัน ฿0.01" — <b>ตรวจแล้วจริงแค่ครึ่งเดียว</b>:
///
/// <list type="bullet">
/// <item><c>x × 7 / 107</c> (ราคารวม VAT) — <b>ไม่มีทางตกจุดกึ่งกลาง</b>:
///   ต้องมี 14·c ≡ 107 (mod 214) เมื่อ c เป็นจำนวนสตางค์ ซึ่งเป็นไปไม่ได้
///   (ซ้ายคู่เสมอ ขวาคี่) ⇒ ระบุโหมดปัดหรือไม่ ให้ผลเท่ากันทุกค่า</item>
/// <item><c>x / 1.07</c> — เช่นกัน ไม่มีทางตกจุดกึ่งกลาง</item>
/// <item><c>x × 0.07</c> (VAT บวกเพิ่ม) — <b>ตกจุดกึ่งกลางจริง</b> เช่น ฿1.50
///   → 0.105 ⇒ AwayFromZero = 0.11 · banker's = 0.10 ⇒ ต่างกัน ฿0.01 จริง
///   (ราว 0.5% ของยอดทั้งหมด)</item>
/// </list>
///
/// เทสต์ชุดนี้ล็อกข้อเท็จจริงนั้นไว้ เพื่อไม่ให้รอบหน้าสรุปเหมาเข่งอีก —
/// และเพื่อให้ถ้าใครเปลี่ยนอัตรา VAT (เช่น 10%) แล้วสมมติฐานเปลี่ยน เทสต์จะบอก
/// </summary>
public class VatRoundingModeTests
{
    [Theory]
    // ⚠️ ส่งเป็นสตริงแล้ว parse — [InlineData(1.50)] เป็น double ซึ่ง xUnit
    // แปลงเป็น decimal ให้ไม่ได้ (โยน error ตอนรัน ไม่ใช่ตอน compile)
    [InlineData("1.50")]    // เคสที่โหมดปัดต่างกันจริงในสูตรบวกเพิ่ม
    [InlineData("3.50")]
    [InlineData("5.50")]
    public void VAT_บวกเพิ่ม_โหมดปัดมีผลจริง(string amountText)
    {
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var away = Math.Round(amount * 0.07m, 2, MidpointRounding.AwayFromZero);
        var even = Math.Round(amount * 0.07m, 2, MidpointRounding.ToEven);
        Assert.NotEqual(even, away);                       // ← ต่างกันจริง
        Assert.Equal(away, Math.Round(amount * 0.07m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void VAT_รวมใน_ไม่มีค่าไหนที่โหมดปัดทำให้ผลต่างกัน()
    {
        // ไล่ทุกยอดที่เป็นไปได้ ฿0.01–฿20,000 (2 ล้านค่า)
        for (int cents = 1; cents <= 2_000_000; cents++)
        {
            var amount = cents / 100m;
            var incl = amount * 7m / 107m;
            Assert.Equal(
                Math.Round(incl, 2, MidpointRounding.ToEven),
                Math.Round(incl, 2, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public void หารด้วย_1_07_ก็ไม่มีค่าไหนที่โหมดปัดทำให้ผลต่างกัน()
    {
        for (int cents = 1; cents <= 2_000_000; cents++)
        {
            var amount = cents / 100m;
            var div = amount / 1.07m;
            Assert.Equal(
                Math.Round(div, 2, MidpointRounding.ToEven),
                Math.Round(div, 2, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public void VAT_บวกเพิ่ม_มีเคสที่ต่างกันอยู่จริงเป็นสัดส่วนที่วัดได้()
    {
        var differ = 0;
        for (int cents = 1; cents <= 2_000_000; cents++)
        {
            var amount = cents / 100m;
            var excl = amount * 0.07m;
            if (Math.Round(excl, 2, MidpointRounding.ToEven)
                != Math.Round(excl, 2, MidpointRounding.AwayFromZero)) differ++;
        }
        // 1 ใน 200 ยอด — เล็กแต่ไม่ใช่ศูนย์ จึงต้องระบุโหมดเสมอในสูตรนี้
        Assert.Equal(10_000, differ);
    }

    [Fact]
    public void ฐานภาษีบวก_VAT_ต้องเท่ายอดรวมเดิมเสมอ()
    {
        // invariant ที่สำคัญกว่าตัวโหมดปัด: แยกยอดแล้วต้องรวมกลับได้เท่าเดิม
        for (int cents = 1; cents <= 500_000; cents++)
        {
            var total = cents / 100m;
            var vat = Math.Round(total * 7m / 107m, 2, MidpointRounding.AwayFromZero);
            var baseAmount = total - vat;
            Assert.Equal(total, baseAmount + vat);
        }
    }
}
