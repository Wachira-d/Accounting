namespace Accounting.Helpers;

/// <summary>
/// **เพดาน/ตัวกันส่งถี่ของอีเมลขอเซ็นเอกสาร — ตัวตัดสินเดียว** (รอบ 240 · คำตัดสิน 240-6 "กันใช้ในทางที่ผิด")
///
/// <para>(1) ส่งผ่าน SMTP ของ NextAcc ⇒ เพดานต่อบริษัทต่อวัน (เวลาไทย) <see cref="DefaultPlatformDailyCap"/> ฉบับ — ผู้ดูแลแพลตฟอร์มปรับได้ที่
/// <c>SignRequestEmail:PlatformDailyCapPerCompany</c> (บริษัทปรับเองไม่ได้ — ชื่อเสียงของโดเมนแพลตฟอร์มเป็นของทุกบริษัทร่วมกัน) ·
/// (2) ทุกโหมด ⇒ ผู้รับคนเดียวกัน: เอกสารเดียวกันไม่เกิน <see cref="PerRecipientPerDocPerDay"/> ฉบับ/วัน และรวมทุกเอกสารของบริษัทไม่เกิน
/// <see cref="PerRecipientPerDay"/> ฉบับ/วัน (กันกดซ้ำ/ก่อกวนผู้รับ) · (3) ผู้รับต่อครั้งไม่เกิน <see cref="MaxRecipients"/> คน (ส่งแยกฉบับละคน —
/// ผู้รับไม่เห็นอีเมลกันและกัน)</para>
/// </summary>
public static class SignEmailThrottle
{
    public const int DefaultPlatformDailyCap = 200;
    public const int PerRecipientPerDocPerDay = 3;
    public const int PerRecipientPerDay = 20;
    public const int MaxRecipients = 5;
    public const string ConfigKey = "SignRequestEmail:PlatformDailyCapPerCompany";

    /// <summary>คำตัดสิน 240-9 (ฝ่ายค้าน B1): บริษัทที่ยังไม่ยืนยันตัวตนพื้นฐาน ⇒ เพดานอีเมลของแพลตฟอร์มต่ำกว่านี้</summary>
    public const int UnverifiedPlatformDailyCap = 20;

    /// <summary>ยืนยันตัวตนพื้นฐาน = เลขผู้เสียภาษีของบริษัทถูกต้อง (13 หลัก + checksum · <c>ThaiTaxId.IsValid</c>) <b>และ</b> เคยชำระค่าบริการ NextAcc ที่อนุมัติแล้ว
    /// (<c>SubscriptionPayments.Status = Approved</c>) — บริษัททดลองที่ไม่เคยจ่าย/ไม่มีเลขภาษีจริง = ยังไม่ยืนยัน</summary>
    public static bool IdentityVerified(bool taxIdValid, bool everPaid) => taxIdValid && everPaid;

    /// <summary>เพดานรายวันของบริษัทนี้ — ยืนยันแล้ว = ค่าที่มีผล · ยังไม่ยืนยัน = ไม่เกิน <see cref="UnverifiedPlatformDailyCap"/></summary>
    public static int PlatformCapFor(int effectiveCap, bool identityVerified)
        => identityVerified ? effectiveCap : Math.Min(effectiveCap, UnverifiedPlatformDailyCap);

    /// <summary>เหตุที่ส่งไม่ได้เพราะจำนวนผู้รับรวม (To + Cc + Bcc) เกิน <see cref="MaxRecipients"/> — null = ได้</summary>
    public static string? RecipientCountRefusal(int totalRecipients)
        => totalRecipients > MaxRecipients
            ? $"ส่งผ่านอีเมลของระบบ NextAcc ได้ครั้งละไม่เกิน {MaxRecipients} ผู้รับ (รวมสำเนา/สำเนาลับ) — ตั้งค่าอีเมลของบริษัทเพื่อส่งถึงผู้รับมากกว่านี้"
            : null;

    /// <summary>เพดานที่มีผล — ค่าตั้งของผู้ดูแลที่เป็นบวก ไม่งั้นค่าเริ่มต้น</summary>
    public static int EffectiveCap(string? configured)
        => int.TryParse(configured, out var v) && v > 0 ? v : DefaultPlatformDailyCap;

    /// <summary>ต้นวันตามเวลาไทย (UTC+7) เป็น UTC — ฐานของการนับรายวัน</summary>
    public static DateTime BangkokDayStartUtc(DateTime nowUtc)
    {
        var local = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddHours(7);
        return DateTime.SpecifyKind(local.Date.AddHours(-7), DateTimeKind.Utc);
    }

    /// <summary>เหตุที่ส่งไม่ได้ (null = ส่งได้) — ตัวเลขนับจากบันทึกการส่งที่สำเร็จของวันนี้</summary>
    public static string? Refusal(bool platformMode, int platformSentToday, int platformCap,
        int sameRecipientSameDocToday, int sameRecipientToday)
    {
        if (sameRecipientSameDocToday >= PerRecipientPerDocPerDay)
            return $"ส่งลิงก์เอกสารนี้ถึงผู้รับรายนี้ครบ {PerRecipientPerDocPerDay} ครั้งของวันนี้แล้ว — ลองใหม่พรุ่งนี้ หรือคัดลอกลิงก์ส่งทางช่องทางอื่น";
        if (sameRecipientToday >= PerRecipientPerDay)
            return $"ส่งอีเมลถึงผู้รับรายนี้ครบ {PerRecipientPerDay} ฉบับของวันนี้แล้ว (กันส่งถี่จนรบกวนผู้รับ)";
        if (platformMode && platformSentToday >= platformCap)
            return $"ส่งผ่านอีเมลของระบบ NextAcc ครบเพดาน {platformCap} ฉบับของวันนี้แล้ว — ตั้งค่าอีเมลของบริษัท (ตั้งค่า → อีเมล) เพื่อส่งต่อได้ไม่จำกัด หรือรอพรุ่งนี้"
                + (platformCap <= UnverifiedPlatformDailyCap
                    ? " (บริษัทที่ยังไม่ยืนยันตัวตน — เลขผู้เสียภาษีถูกต้อง + เคยชำระค่าบริการ — มีเพดานต่ำกว่า)" : "");
        return null;
    }

    /// <summary>แยก/ตรวจรายชื่อผู้รับ — คั่นด้วย , ; ช่องว่าง ขึ้นบรรทัด · ตัดซ้ำ (ไม่สนตัวพิมพ์) · ตรวจรูปแบบด้วย <see cref="OnBehalfEmailSender.IsPlausibleEmail"/></summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) ParseRecipients(IEnumerable<string?>? raw)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        if (raw == null) return (valid, invalid);
        foreach (var chunk in raw)
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            foreach (var part in chunk.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!OnBehalfEmailSender.IsPlausibleEmail(part)) { invalid.Add(part); continue; }
                var norm = part.ToLowerInvariant();
                if (!valid.Contains(norm)) valid.Add(norm);
            }
        }
        return (valid, invalid);
    }
}
