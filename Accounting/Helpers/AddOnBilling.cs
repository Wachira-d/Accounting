namespace Accounting.Helpers;

/// <summary>
/// กติกาการคิดค่า add-on เหมารายเดือน — **pure** (ไม่แตะ DB/เวลา) เพื่อให้เทสต์ได้
/// ตามกฎเหล็ก #4 G ("logic เงินใหม่ → extract เป็น pure class + เทสต์")
///
/// สองเรื่องที่ต้องถูกเสมอ:
///   1. **งวด** — ค่าเหมาผูกกับ "เดือนปฏิทินไทย" (UTC+7) ไม่ใช่เดือน UTC
///      ไม่งั้นวันที่ 1 ตี 0-7 โมงเช้าไทยจะถูกคิดเป็นเดือนก่อนหน้า
///   2. **กันคิดซ้ำ** — คีย์ idempotency ต้องเป็นฟังก์ชันของ (ฟีเจอร์, งวด)
///      เท่านั้น ห้ามมีเวลา/สุ่มปน (job รันทุก 6 ชม. · หลาย instance · restart)
/// </summary>
public static class AddOnBilling
{
    /// <summary>งวดของเวลา UTC ที่ให้มา ในรูป yyyy-MM ตามปฏิทินไทย (UTC+7)</summary>
    public static string PeriodOf(DateTime utc) => utc.AddHours(7).ToString("yyyy-MM");

    /// <summary>คีย์กันคิดซ้ำของค่าเหมา 1 ฟีเจอร์ 1 งวด (unique ต่อ CompanyId ในตาราง)
    /// — deterministic ล้วน ไม่มีเวลา/สุ่ม (บทเรียน AdvisoryLockKey: คีย์ที่สุ่มต่อ
    /// process = ไม่กันอะไรเลย)</summary>
    public static string FlatKey(string featureCode, string period) => $"flat:{featureCode}:{period}";

    /// <summary>คีย์ของ top-up ที่ซื้อครั้งที่ n ในงวดนั้น (ซื้อซ้ำได้ ต่างจากค่าเหมา)</summary>
    public static string TopUpKey(string featureCode, string period, int seq) => $"topup:{featureCode}:{period}:{seq}";

    /// <summary>ค่าเหมาของงวดนี้ควรเป็นเท่าไร
    ///
    /// <para><paramref name="trialUntilUtc"/> ที่ยังไม่ถึง = ฿0 (แต่ยัง**บันทึกแถว**
    /// ไว้เพื่อให้ลูกค้าเห็นในบิลว่า "ทดลองใช้ ฿0" — เงียบไปเลยจะกลายเป็นเซอร์ไพรส์
    /// ตอนเดือนถัดไปโดนคิดเงิน)</para>
    /// <para>ของแถมจาก admin/แพ็กเกจ = ฿0 เช่นกัน แต่คนละเหตุผล — แยกไว้ให้รายงาน
    /// ตอบได้ว่าทำไมยอดเป็นศูนย์</para>
    /// </summary>
    public static (decimal Amount, string Reason) FlatAmount(
        decimal standardPrice, decimal? snapshotPrice, DateTime? trialUntilUtc,
        bool bundledOrGranted, DateTime nowUtc)
    {
        if (trialUntilUtc is DateTime t && t > nowUtc) return (0m, "trial");
        if (bundledOrGranted) return (0m, "granted");
        var price = snapshotPrice ?? standardPrice;
        if (price <= 0) return (0m, "no-price");
        return (Math.Round(price, 2, MidpointRounding.AwayFromZero), "charged");
    }

    /// <summary>trial หมดแล้วและตั้งให้ปิดเอง = ต้องปิด (ไม่คิดเงินโดยไม่ถาม)</summary>
    public static bool ShouldAutoDisable(DateTime? trialUntilUtc, bool autoDisableAfterTrial, DateTime nowUtc)
        => autoDisableAfterTrial && trialUntilUtc is DateTime t && t <= nowUtc;

    /// <summary>งวดนี้ถูกออกบิลไปแล้วหรือยัง — เทียบสตริงตรง ๆ (yyyy-MM เรียงตามเวลาอยู่แล้ว)</summary>
    public static bool AlreadyBilled(string? lastBilledPeriod, string period)
        => !string.IsNullOrEmpty(lastBilledPeriod) && string.CompareOrdinal(lastBilledPeriod, period) >= 0;
}
