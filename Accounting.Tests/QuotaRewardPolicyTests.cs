using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ภารกิจแลกโควตา — สวิตช์ 3 ชั้น + เพดานกัน abuse (LODGING_LICENSING_PLAN §12)
///
/// ลำดับการตรวจสำคัญพอ ๆ กับผลลัพธ์: เหตุผลที่คืนต้องเป็น "เหตุที่ใหญ่ที่สุด"
/// ก่อนเสมอ — บอกผู้ใช้ว่า "วันนี้ครบโควตาแล้ว" ทั้งที่จริงแพ็กเกจไม่รองรับเลย
/// = พาไปรอพรุ่งนี้ฟรี ๆ
/// </summary>
public class QuotaRewardPolicyTests
{
    private static QuotaRewardBlockReason Eval(
        bool anyActive = true, bool planAllows = true, bool blocked = false,
        int usedToday = 0, int maxDay = 2, int usedMonth = 0, int maxMonth = 10,
        int watched = 60, int required = 60)
        => QuotaRewardPolicy.Evaluate(anyActive, planAllows, blocked,
            usedToday, maxDay, usedMonth, maxMonth, watched, required);

    [Fact]
    public void ครบทุกเงื่อนไข_ผ่าน() => Assert.Equal(QuotaRewardBlockReason.None, Eval());

    [Fact]
    public void แพลตฟอร์มปิด_ชนะทุกเหตุผลอื่น()
        => Assert.Equal(QuotaRewardBlockReason.PlatformOff,
            Eval(anyActive: false, planAllows: false, blocked: true, usedToday: 99));

    [Fact]
    public void แพ็กเกจไม่รองรับ_ชนะเพดานและเวลา()
        => Assert.Equal(QuotaRewardBlockReason.PlanOff, Eval(planAllows: false, usedToday: 99, watched: 0));

    [Fact]
    public void บริษัทถูกระงับสิทธิ์()
        => Assert.Equal(QuotaRewardBlockReason.CompanyBlocked, Eval(blocked: true));

    [Fact]
    public void ครบเพดานต่อวัน()
        => Assert.Equal(QuotaRewardBlockReason.DailyCap, Eval(usedToday: 2, maxDay: 2));

    [Fact]
    public void ครบเพดานต่อเดือน()
        => Assert.Equal(QuotaRewardBlockReason.MonthlyCap, Eval(usedMonth: 10, maxMonth: 10));

    [Fact]
    public void เพดานเป็นศูนย์แปลว่าไม่จำกัด_ไม่ใช่ห้ามทั้งหมด()
        => Assert.Equal(QuotaRewardBlockReason.None, Eval(usedToday: 500, maxDay: 0, usedMonth: 900, maxMonth: 0));

    [Fact]
    public void ดูไม่ครบเวลา_ไม่ให้รางวัล()
        => Assert.Equal(QuotaRewardBlockReason.TooShort, Eval(watched: 10, required: 60));

    [Fact]
    public void กดส่งทันทีโดยไม่ดูเลย_ต้องถูกปฏิเสธ()
        => Assert.Equal(QuotaRewardBlockReason.TooShort, Eval(watched: 0, required: 60));

    /// <summary>นาฬิกาเบราว์เซอร์กับเซิร์ฟเวอร์ไม่ตรงกันเป๊ะ — ขาดไม่เกิน 2 วินาที
    /// ต้องผ่าน ไม่งั้นคนที่ดูครบจริงจะโดนปฏิเสธเป็นครั้งคราวโดยไม่มีทางแก้</summary>
    [Fact]
    public void ขาดไม่เกินสองวินาที_ยังผ่าน_เพราะนาฬิกาสองฝั่งไม่ตรงกัน()
    {
        Assert.Equal(QuotaRewardBlockReason.None, Eval(watched: 58, required: 60));
        Assert.Equal(QuotaRewardBlockReason.TooShort, Eval(watched: 57, required: 60));
    }

    /// <summary>-1 = "ยังไม่ได้ดู กำลังถามว่ากดได้ไหม" (ฝั่งแสดงรายการ) ต้องข้ามด่านเวลา
    /// — ห้ามใช้ 0 แทน เพราะ 0 เป็นค่าจริงที่แปลว่ากดส่งโดยไม่ดู</summary>
    [Fact]
    public void ฝั่งแสดงรายการส่งลบหนึ่ง_ข้ามด่านเวลาแต่ยังเช็คเพดาน()
    {
        Assert.Equal(QuotaRewardBlockReason.None, Eval(watched: -1, required: 60));
        Assert.Equal(QuotaRewardBlockReason.DailyCap, Eval(watched: -1, usedToday: 2, maxDay: 2));
    }

    [Fact]
    public void ทุกเหตุผลที่ปฏิเสธต้องมีข้อความไทยให้โชว์()
    {
        foreach (QuotaRewardBlockReason r in Enum.GetValues<QuotaRewardBlockReason>())
        {
            var msg = QuotaRewardPolicy.Message(r, 2, 10, 60);
            if (r == QuotaRewardBlockReason.None) Assert.Equal("", msg);
            else Assert.False(string.IsNullOrWhiteSpace(msg), $"{r} ไม่มีข้อความ");
        }
    }

    [Fact]
    public void อายุโบนัสแบบระบุจำนวนวัน()
    {
        var now = new DateTime(2026, 9, 3, 5, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddDays(30), QuotaRewardPolicy.ExpiryOf(30, now));
    }

    /// <summary>0 วัน = สิ้นเดือนไทย — 3 ก.ย. 2026 (ไทย) ⇒ หมดอายุต้นเดือน ต.ค.
    /// ตามเวลาไทย = 30 ก.ย. 17:00Z</summary>
    [Fact]
    public void อายุโบนัสแบบสิ้นเดือน_คิดตามปฏิทินไทย()
    {
        var now = new DateTime(2026, 9, 3, 5, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc), QuotaRewardPolicy.ExpiryOf(0, now));
    }

    /// <summary>ปลายเดือนแบบ UTC ที่ข้ามวันไปแล้วในไทย: 30 ก.ย. 18:00Z = 1 ต.ค. ไทย
    /// ⇒ ต้องได้สิ้นเดือน**ตุลาคม** ไม่ใช่กันยายน (ไม่งั้นโบนัสหมดอายุทันทีที่ได้รับ)</summary>
    [Fact]
    public void ปลายเดือน_UTC_ที่ข้ามวันในไทยแล้ว_ต้องนับเป็นเดือนถัดไป()
    {
        var now = new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc);
        var exp = QuotaRewardPolicy.ExpiryOf(0, now);
        Assert.True(exp > now, "โบนัสต้องไม่หมดอายุก่อนเวลาที่ได้รับ");
        Assert.Equal(new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc), exp);
    }
}
