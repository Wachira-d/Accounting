using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 206 (ฝ่ายค้านรอบ 2 P2(b) ส่วนที่ค้าง · PAYMENT_CHANNEL_AUDIT ADV2-P2-b) — ตัวเลือก "ผังลูกหนี้/เจ้าหนี้ที่ใบเดิมตั้งไว้จริง"
/// (<see cref="OriginalArApAccount.Pick"/>) สองทิศ: ผังที่ JE ตอนออกใบลงไว้ชนะ · ไม่มีผู้สมัคร = null (ผู้เรียกใช้ตัวหาเดิม)
/// </summary>
public class OriginalArApAccountTests
{
    private static readonly Guid Ar = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Ar2 = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Revenue = Guid.Parse("41000000-0000-0000-0000-000000000001");
    private static readonly Guid Vat = Guid.Parse("21911000-0000-0000-0000-000000000001");
    private static readonly Guid Ap = Guid.Parse("21210000-0000-0000-0000-000000000001");
    private static readonly Guid Grn = Guid.Parse("21240000-0000-0000-0000-000000000001");
    private static readonly Guid Clearing = Guid.Parse("11340000-0000-0000-0000-000000000001");
    private static readonly Guid CustomAr = Guid.Parse("13100000-0000-0000-0000-000000000001");

    [Fact]
    public void ใบขาย_ได้ผังลูกหนี้ที่ลงเดบิต_แม้ผู้ติดต่อปักผังอื่นอยู่ตอนนี้()
    {
        var lines = new[]
        {
            new ArApJeLine(Ar, "11310-X", 1_070m, 0m),
            new ArApJeLine(Revenue, "41000", 0m, 1_000m),
            new ArApJeLine(Vat, "21911", 0m, 70m),
        };
        Assert.Equal(Ar, OriginalArApAccount.Pick(lines, ArApSide.Receivable, pinnedAccountId: Ar2));
        Assert.Equal(Ar, OriginalArApAccount.Pick(lines, ArApSide.Receivable, pinnedAccountId: null));
    }

    [Fact]
    public void ใบซื้อ_ได้ผังเจ้าหนี้ที่ลงเครดิต_ไม่ใช่ขาเดบิตพักรับสินค้า()
    {
        var lines = new[]
        {
            new ArApJeLine(Grn, "21240", 1_000m, 0m),      // ล้างพักรับสินค้า (เดบิต — ไม่ใช่หนี้ที่ใบตั้ง)
            new ArApJeLine(Vat, "11610", 70m, 0m),
            new ArApJeLine(Ap, "21210", 0m, 1_070m),
        };
        Assert.Equal(Ap, OriginalArApAccount.Pick(lines, ArApSide.Payable, null));
        // ทิศตรงข้าม: ถามฝั่งลูกหนี้จาก JE ซื้อ ⇒ ไม่มี (ผู้เรียกใช้ตัวหาเดิม)
        Assert.Null(OriginalArApAccount.Pick(lines, ArApSide.Receivable, null));
    }

    [Fact]
    public void ผังลูกหนี้ของบริษัทเองที่รหัสไม่ใช่113_นับเมื่อเป็นผังที่ผู้ติดต่อปัก()
    {
        var lines = new[] { new ArApJeLine(CustomAr, "1310", 500m, 0m), new ArApJeLine(Revenue, "4100", 0m, 500m) };
        Assert.Equal(CustomAr, OriginalArApAccount.Pick(lines, ArApSide.Receivable, CustomAr));
        // ไม่ได้ปักไว้ + รหัสไม่อยู่กลุ่ม 113 ⇒ ไม่เดา
        Assert.Null(OriginalArApAccount.Pick(lines, ArApSide.Receivable, null));
    }

    [Fact]
    public void บัญชีพักผู้ให้บริการรับชำระเงิน_ไม่ใช่ลูกหนี้ของใบ()
    {
        var lines = new[] { new ArApJeLine(Clearing, "11340", 2_000m, 0m), new ArApJeLine(Ar, "11310", 1_000m, 0m) };
        Assert.Equal(Ar, OriginalArApAccount.Pick(lines, ArApSide.Receivable, null));
        var walletOnly = new[] { new ArApJeLine(Clearing, "11341", 2_000m, 0m) };
        Assert.Null(OriginalArApAccount.Pick(walletOnly, ArApSide.Receivable, null));
    }

    [Fact]
    public void ไม่มีบรรทัด_หรือยอดเป็นศูนย์_คืนnull_และเท่ากันเลือกรหัสน้อยสุด()
    {
        Assert.Null(OriginalArApAccount.Pick(Array.Empty<ArApJeLine>(), ArApSide.Receivable, Ar));
        Assert.Null(OriginalArApAccount.Pick(new[] { new ArApJeLine(Ar, "11310", 0m, 0m) }, ArApSide.Receivable, null));
        var tie = new[] { new ArApJeLine(Ar2, "11320", 500m, 0m), new ArApJeLine(Ar, "11310", 500m, 0m) };
        Assert.Equal(Ar, OriginalArApAccount.Pick(tie, ArApSide.Receivable, null));
    }
}
