namespace Accounting.Helpers;

/// <summary>ทำไมภารกิจนี้กดไม่ได้ — ต้องเป็น "เหตุผลที่เอาไปโชว์ได้"
/// ไม่ใช่ bool แล้วให้แต่ละหน้าจอไปแต่งคำเอง (จะกลายเป็นสำเนามือชุดที่สอง)</summary>
public enum QuotaRewardBlockReason
{
    None = 0,
    /// <summary>แพลตฟอร์มปิดทั้งระบบ (ไม่มี option ที่ IsActive) — ชั้นที่ 1</summary>
    PlatformOff = 1,
    /// <summary>แพ็กเกจนี้ไม่เปิดให้แลกโควตา (`PlanTemplate.AllowQuotaReward`) — ชั้นที่ 2</summary>
    PlanOff = 2,
    /// <summary>บริษัทนี้ถูกระงับสิทธิ์ (`Subscription.QuotaRewardBlocked`) — ชั้นที่ 3</summary>
    CompanyBlocked = 3,
    /// <summary>ครบเพดานต่อวันแล้ว</summary>
    DailyCap = 4,
    /// <summary>ครบเพดานต่อเดือนแล้ว</summary>
    MonthlyCap = 5,
    /// <summary>ยังอยู่กับหน้าไม่ครบเวลา</summary>
    TooShort = 6,
}

/// <summary>
/// **กติกา "ทำภารกิจสั้น ๆ แลกโควตาเอกสาร"** — pure ตัวเดียวของระบบ
/// (LODGING_LICENSING_PLAN.md §12)
///
/// แยกออกมาเป็น pure เพราะเป็นตรรกะที่ต้องเหมือนกันเป๊ะสองฝั่ง: ฝั่ง **แสดงรายการ**
/// (บอกล่วงหน้าว่ากดได้/ไม่ได้ เพราะอะไร) กับฝั่ง **รับคำขอ** (ตัวบังคับจริง) —
/// ถ้าเขียนสองที่ วันหนึ่งหน้าจอจะบอกว่ากดได้แล้วเซิร์ฟเวอร์ปฏิเสธ
/// </summary>
public static class QuotaRewardPolicy
{
    /// <summary>ผ่อนเวลาให้กี่วินาที — นาฬิกาเบราว์เซอร์กับเซิร์ฟเวอร์ไม่ตรงกันเป๊ะ
    /// และการนับเริ่มหลังวิดีโอโหลดจริง ⇒ ถ้าบังคับ 60 พอดีจะมีคนดูครบแล้วโดนปฏิเสธ</summary>
    public const int ClockToleranceSeconds = 2;

    /// <summary>ตรวจสิทธิ์ก่อนให้รางวัล
    ///
    /// <paramref name="watchedSeconds"/> = -1 แปลว่า "ยังไม่ได้ดู กำลังถามว่ากดได้ไหม"
    /// (ฝั่งแสดงรายการ) จึงข้ามด่านเวลา — ห้ามใช้ 0 แทน เพราะ 0 เป็นค่าจริงที่
    /// แปลว่า "กดส่งทันทีโดยไม่ดูเลย" ซึ่งต้องถูกปฏิเสธ</summary>
    public static QuotaRewardBlockReason Evaluate(
        bool anyActiveOption, bool planAllows, bool companyBlocked,
        int usedToday, int maxPerDay, int usedThisMonth, int maxPerMonth,
        int watchedSeconds, int requiredSeconds)
    {
        if (!anyActiveOption) return QuotaRewardBlockReason.PlatformOff;
        if (!planAllows) return QuotaRewardBlockReason.PlanOff;
        if (companyBlocked) return QuotaRewardBlockReason.CompanyBlocked;
        if (maxPerDay > 0 && usedToday >= maxPerDay) return QuotaRewardBlockReason.DailyCap;
        if (maxPerMonth > 0 && usedThisMonth >= maxPerMonth) return QuotaRewardBlockReason.MonthlyCap;
        if (watchedSeconds >= 0 && watchedSeconds + ClockToleranceSeconds < requiredSeconds)
            return QuotaRewardBlockReason.TooShort;
        return QuotaRewardBlockReason.None;
    }

    /// <summary>ข้อความไทยที่เอาไปโชว์ได้ตรง ๆ พร้อม "ทำอะไรต่อได้"</summary>
    public static string Message(QuotaRewardBlockReason r, int maxPerDay, int maxPerMonth, int requiredSeconds) => r switch
    {
        QuotaRewardBlockReason.None => "",
        QuotaRewardBlockReason.PlatformOff => "ขณะนี้ไม่มีภารกิจแลกโควตา — ซื้อโควตาเพิ่มหรืออัปเกรดแพ็กเกจได้ที่หน้าส่วนเสริม",
        QuotaRewardBlockReason.PlanOff => "แพ็กเกจของคุณไม่รองรับการแลกโควตา — ซื้อโควตาเพิ่มหรืออัปเกรดแพ็กเกจแทน",
        QuotaRewardBlockReason.CompanyBlocked => "บริษัทนี้ถูกระงับสิทธิ์แลกโควตา — กรุณาติดต่อฝ่ายสนับสนุน",
        QuotaRewardBlockReason.DailyCap => $"วันนี้แลกครบ {maxPerDay} ครั้งแล้ว — พรุ่งนี้แลกได้อีก หรือซื้อโควตาเพิ่มได้ทันที",
        QuotaRewardBlockReason.MonthlyCap => $"เดือนนี้แลกครบ {maxPerMonth} ครั้งแล้ว — ซื้อโควตาเพิ่มหรืออัปเกรดแพ็กเกจ",
        QuotaRewardBlockReason.TooShort => $"ต้องอยู่กับหน้านี้ครบ {requiredSeconds} วินาทีจึงจะได้รับโควตา",
        _ => "ไม่สามารถแลกโควตาได้ในขณะนี้",
    };

    /// <summary>โควตาที่ได้หมดอายุเมื่อไร — 0 วัน = สิ้นเดือนไทยของเดือนนี้
    /// (โบนัสมีไว้แก้ปัญหา "ตอนนี้ทำงานไม่ได้" ไม่ใช่สะสมข้ามปี)</summary>
    public static DateTime ExpiryOf(int validDays, DateTime nowUtc)
    {
        if (validDays > 0) return nowUtc.AddDays(validDays);
        var th = nowUtc.AddHours(7);
        return new DateTime(th.Year, th.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(1).AddHours(-7);   // ต้นเดือนถัดไปตามเวลาไทย → กลับเป็น UTC
    }
}
