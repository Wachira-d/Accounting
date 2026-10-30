namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินโควตาข้อความผ่านช่องกลาง — ตัวเดียว** (รอบ 212 แพ็ก A · คำตัดสินข้อ 171 · design §2.3/§3.1)
/// <list type="bullet">
/// <item><b>นับอะไร</b>: push ผ่าน LINE OA ของ NextAcc + อีเมลผ่าน SMTP ของระบบในนามร้าน (<see cref="IsMetered"/>) ·
///   <b>ไม่นับ</b>: OA/SMTP ของร้านเอง · reply API ของ LINE · ข้อความแพลตฟอร์ม→ร้าน (บิล/แจ้งเตือนโควตา/ความปลอดภัย) · แจ้งเตือนงานภายในของ NotificationEngine ·
///   ข้อความถึงร้าน `shop.*` (ฝ่ายค้านรอบ 212 B3 · <see cref="CountsEvent"/>)</item>
/// <item>1 หน่วย = 1 คำขอ push ต่อผู้รับ หรือ 1 อีเมลต่อผู้รับ</item>
/// <item>งวด = เดือนตามเวลาไทย (<see cref="AddOnBilling.PeriodOf"/> ตัวเดียวกับบิล add-on) — งวดใหม่ = แถวตัวนับใหม่ (ไม่มีงาน reset)</item>
/// <item>เพดาน: ค่าเฉพาะบริษัท (<c>Subscription.MaxSharedMessagesPerMonth</c>) ชนะ ⇒ ค่าของแพ็กเกจ (<c>PlanTemplate.MaxSharedMessagesPerMonth</c> ·
///   ทดลองใช้ <c>TrialMaxSharedMessagesPerMonth</c>) ⇒ ไม่พบแพ็กเกจ = ไม่จำกัด (พฤติกรรมเดิม) · <c>-1</c> = ไม่จำกัด · นับต่อบริษัท (ไม่ pool ข้ามกลุ่มบัญชี)</item>
/// <item>เต็มแล้ว ⇒ ใช้โบนัสที่ซื้อ (<c>notify.topup</c>) ที่ยังไม่หมดอายุ ⇒ หมด = ไม่ส่งทางช่องกลาง (ลองช่องของร้านก่อน) · ข้อความจำเป็น (<see cref="CustomerNotifyEvents.IsEssential"/>) นับแต่ไม่ระงับ</item>
/// <item>เตือนเจ้าของ 80% / 100% ครั้งเดียวต่อระดับต่องวด (ระดับเดียวกับ <see cref="DocumentQuotaPolicy.WarnLevel"/>)</item>
/// </list>
/// โควตาเต็ม<b>ไม่บล็อกงานธุรกิจ</b>ใด ๆ — บล็อกแค่การส่งข้อความผ่านช่องกลาง
/// </summary>
public static class NotifyQuotaPolicy
{
    public const int Unlimited = -1;

    /// <summary>ช่องนี้นับโควตาไหม — เฉพาะช่องกลางของ NextAcc</summary>
    public static bool IsMetered(string? source)
        => source is CustomerNotifyPolicy.SrcNextAccOa or CustomerNotifyPolicy.SrcSystemSmtp;

    /// <summary>ผู้ประสานงานตัดสินหลังฝ่ายค้านรอบ 212 (c): ตัวเลข<b>แนะนำ</b>ต่อระดับแพ็กเกจ — ไม่ถูกตั้งให้อัตโนมัติ (ค่าเริ่มต้น = ไม่จำกัด = พฤติกรรมเดิม) ·
    /// หน้าแอดมิน plans แสดงเป็นคำใบ้ให้เจ้าของตั้งเอง · null = ไม่มีคำแนะนำ</summary>
    public static int? SuggestedLimit(Accounting.Models.Enums.SubscriptionPlan plan) => plan switch
    {
        Accounting.Models.Enums.SubscriptionPlan.FreeTrial => 100,
        Accounting.Models.Enums.SubscriptionPlan.Basic => 300,
        Accounting.Models.Enums.SubscriptionPlan.Pro => 1000,
        Accounting.Models.Enums.SubscriptionPlan.Enterprise => 5000,
        _ => null,
    };

    /// <summary>ตัวเลขแนะนำสำหรับช่วงทดลองใช้ของทุกแม่แบบ</summary>
    public const int SuggestedTrialLimit = 100;

    /// <summary>ฝ่ายค้านรอบ 212 B3: ประเภทข้อความนี้นับโควตาไหม — ข้อความถึงร้าน (<c>shop.*</c>: ออเดอร์/การจองใหม่ · สลิป · lead · ที่พัก · เงินค้าง)
    /// เป็นแจ้งเตือนงาน ⇒ <b>ไม่นับและไม่ถูกระงับ</b> (คำตัดสิน §10 "แจ้งเตือนเหตุการณ์ไม่นับ") · ข้อความถึงลูกค้า = นับ</summary>
    public static bool CountsEvent(string? eventKey) => !CustomerNotifyEvents.IsShopEvent(eventKey);

    /// <summary>งวด "yyyy-MM" ตามเวลาไทย — ตัวเดียวกับงวดบิล add-on</summary>
    public static string PeriodOf(DateTime utc) => AddOnBilling.PeriodOf(utc);

    /// <summary>เพดานของแพ็กเกจที่มีผลกับบริษัท · null ทุกชั้น = ไม่จำกัด (ไม่มีแม่แบบแพ็กเกจ — ไม่เปลี่ยนพฤติกรรมเดิม)</summary>
    public static int PlanLimit(int? subscriptionOverride, int? templateLimit, int? templateTrialLimit, bool isTrial)
    {
        if (subscriptionOverride is int o) return o < 0 ? Unlimited : o;
        var t = isTrial ? (templateTrialLimit ?? templateLimit) : templateLimit;
        return t is int v ? (v < 0 ? Unlimited : v) : Unlimited;
    }

    /// <summary>โบนัสยังใช้ได้ไหม (มีเหลือ + ยังไม่หมดอายุ)</summary>
    public static bool BonusAlive(int bonus, DateTime? expiresAtUtc, DateTime nowUtc)
        => bonus > 0 && (expiresAtUtc == null || expiresAtUtc.Value > nowUtc);

    /// <summary>เพดานรวมที่แสดง = แพ็กเกจ + โบนัสที่ใช้ไปแล้วงวดนี้ + โบนัสที่ยังเหลือ (หมดอายุ = ไม่นับ) · ไม่จำกัด ⇒ <see cref="Unlimited"/></summary>
    public static int EffectiveLimit(int planLimit, int bonusUsedThisPeriod, int bonusRemaining, DateTime? bonusExpiresAtUtc, DateTime nowUtc)
        => planLimit < 0 ? Unlimited
            : planLimit + Math.Max(0, bonusUsedThisPeriod) + (BonusAlive(bonusRemaining, bonusExpiresAtUtc, nowUtc) ? bonusRemaining : 0);

    /// <summary>0 = ปกติ · 1 = ≥ 80% · 2 = เต็ม · ไม่จำกัด = 0</summary>
    public static int WarnLevel(int used, int limit) => limit < 0 ? 0 : DocumentQuotaPolicy.WarnLevel(used, limit);

    /// <summary>ระดับที่<b>เพิ่งข้าม</b>จากข้อความนี้ (0 = ไม่ข้าม)</summary>
    public static int CrossedLevel(int usedBefore, int usedAfter, int limit)
    {
        var after = WarnLevel(usedAfter, limit);
        return after > WarnLevel(usedBefore, limit) ? after : 0;
    }

    /// <summary>ลองช่องกลางได้ไหม: ข้อความจำเป็น ⇒ ได้เสมอ (นับเกินได้) · ไม่จำกัด ⇒ ได้ · อยู่ในเพดานแพ็กเกจ ⇒ ใช้แพ็กเกจ · ไม่งั้นใช้โบนัสถ้ายังมี</summary>
    public static NotifyQuotaDecision Decide(int planUsed, int planLimit, bool essential, int bonusRemaining, DateTime? bonusExpiresAtUtc, DateTime nowUtc)
    {
        if (planLimit < 0) return NotifyQuotaDecision.Plan;
        if (planUsed < planLimit) return NotifyQuotaDecision.Plan;
        if (BonusAlive(bonusRemaining, bonusExpiresAtUtc, nowUtc)) return NotifyQuotaDecision.Bonus;
        return essential ? NotifyQuotaDecision.Essential : NotifyQuotaDecision.Full;
    }

    /// <summary>ป้ายนำหน้าคำเตือนเมื่อส่งทางช่องของร้านแทนเพราะโควตาเต็ม (ประเภท "ส่งถึงทางถอย" — <see cref="CmsNotificationText.FallbackTag"/>)</summary>
    public static string RoutedToTenantWarning(int limit)
        => $"{CmsNotificationText.FallbackTag} โควตาข้อความผ่านช่องกลางของ NextAcc เดือนนี้ครบแล้ว ({limit} ข้อความ) — ส่งผ่านช่องของร้านแทน";

    /// <summary>เหตุที่ไม่ส่ง (ระงับ) เมื่อเหลือแต่ช่องกลางและโควตาเต็ม — บอกทางไปต่อ (F2 #8)</summary>
    public static string FullReason(int limit)
        => $"โควตาข้อความผ่านช่องกลางของ NextAcc เดือนนี้ครบแล้ว ({limit} ข้อความ) — ไม่ได้ส่ง · ซื้อแพ็กข้อความเพิ่มที่ \"ส่วนเสริม\" "
           + "หรือตั้ง LINE OA / ผู้ส่งอีเมลของร้านเอง (ไม่นับโควตา)";

    public static string CrossedTitle(int level) => level >= 2 ? "โควตาข้อความแจ้งเตือนเดือนนี้เต็มแล้ว" : "โควตาข้อความแจ้งเตือนใกล้เต็ม";

    public static string CrossedMessage(int level, int used, int limit)
        => level >= 2
            ? $"ส่งข้อความผ่าน LINE OA/อีเมลของ NextAcc ครบ {limit} ข้อความแล้ว — ข้อความถัดไปจะส่งทางช่องของร้าน (ถ้าตั้งไว้) หรือไม่ถูกส่ง · ซื้อแพ็กข้อความเพิ่มได้ที่หน้า ส่วนเสริม"
            : $"ส่งข้อความผ่าน LINE OA/อีเมลของ NextAcc ไปแล้ว {used}/{limit} ข้อความ (≥ {DocumentQuotaPolicy.WarnPercent}%) — ตั้ง LINE OA/อีเมลของร้านเอง (ไม่นับโควตา) หรือซื้อแพ็กข้อความเพิ่ม";

    /// <summary>ข้อความแบนเนอร์ของหน้าเว็บ (เซิร์ฟเวอร์ประกอบ · null = ปกติ)</summary>
    public static string? StatusMessage(int used, int limit)
        => WarnLevel(used, limit) switch
        {
            2 => $"โควตาข้อความผ่านช่องกลางเดือนนี้เต็มแล้ว ({used}/{limit}) — ข้อความถึงลูกค้าจะส่งทางช่องของร้าน หรือไม่ถูกส่ง จนกว่าจะซื้อแพ็กเพิ่ม/ขึ้นเดือนใหม่",
            1 => $"ใช้โควตาข้อความผ่านช่องกลางไปแล้ว {used}/{limit}",
            _ => null,
        };

    public const string MeteredChannelsNote = "นับเฉพาะข้อความที่ส่งผ่าน LINE OA ของ NextAcc และอีเมลของระบบ NextAcc — LINE OA / ผู้ส่งอีเมลของร้านเองไม่นับ";

    // ───────── รอบ 213 (คำตัดสินข้อ 172): แพ็ก notify.topup ตั้งได้ที่หน้าแอดมิน + เพดานเฉพาะบริษัท ─────────

    public const int MinPackSize = 1;
    public const int MaxPackSize = 100_000;
    public const int MinPackValidDays = 1;
    public const int MaxPackValidDays = 730;

    /// <summary>ขนาด/อายุแพ็ก <c>notify.topup</c> ที่มีผลกับการซื้อครั้งนี้ — ค่าที่แอดมินตั้ง (<c>ApiFeature.PackSize/PackValidDays</c> ของ
    /// <c>notify.topup</c>) ชนะ · null/นอกช่วง ⇒ ค่าตั้งต้น <see cref="Models.Constants.AddOnCodes.MessagesPerNotifyTopUpPack"/> / <see cref="Models.Constants.AddOnCodes.TopUpValidDays"/>
    /// (<c>documents.topup</c> ไม่อ่านค่านี้ — คงพฤติกรรมเดิม) · มีผลกับการซื้อครั้งถัดไปเท่านั้น · อายุนับจากวันซื้อ และโบนัสคงเหลือทั้งก้อนใช้ได้ถึง
    /// <b>วันหมดอายุที่ช้ากว่า</b> (GREATEST ของเดิมกับใหม่ — ไม่หดอายุของที่ซื้อไว้แล้ว · ฝ่ายค้านรอบ 213 (e))</summary>
    public static NotifyTopUpPack TopUpPack(int? packSize, int? validDays)
        => new(packSize is int s && s >= MinPackSize && s <= MaxPackSize ? s : Models.Constants.AddOnCodes.MessagesPerNotifyTopUpPack,
               validDays is int d && d >= MinPackValidDays && d <= MaxPackValidDays ? d : Models.Constants.AddOnCodes.TopUpValidDays);

    /// <summary>ฝ่ายค้านรอบ 213 S4: ข้อเสนอที่ผู้ซื้อเห็น (ขนาดแพ็ก/ราคา) ยังตรงกับของจริง ณ ตอนซื้อไหม — ไม่ตรง ⇒ คืนข้อความ (ผู้เรียกตอบ 409 + สถานะใหม่ ให้ยืนยันอีกครั้ง) ·
    /// ผู้เรียกไม่ส่งค่าคาดหวัง (หน้าเก่าที่แคชไว้) ⇒ ไม่ตรวจ (null)</summary>
    public static string? TopUpOfferChanged(int? expectedPackSize, decimal? expectedPrice, NotifyTopUpPack pack, decimal? currentPrice)
    {
        if (expectedPackSize is int s && s != pack.Size)
            return $"ขนาดแพ็กเปลี่ยนเป็น {N(pack.Size)} ข้อความแล้ว — กรุณาตรวจรายละเอียดแล้วยืนยันอีกครั้ง (ยังไม่ได้คิดเงิน)";
        if (expectedPrice is decimal p && p != (currentPrice ?? 0m))
            return $"ราคาแพ็กเปลี่ยนเป็น {(currentPrice ?? 0m).ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} บาทแล้ว — กรุณาตรวจรายละเอียดแล้วยืนยันอีกครั้ง (ยังไม่ได้คิดเงิน)";
        return null;
    }

    /// <summary>ตรวจค่าที่แอดมินส่งมา (null = ไม่เปลี่ยน) · คืนข้อความไทย หรือ null = ผ่าน</summary>
    public static string? ValidatePack(int? packSize, int? validDays)
    {
        if (packSize is int s && (s < MinPackSize || s > MaxPackSize))
            return $"จำนวนข้อความต่อแพ็กต้องอยู่ระหว่าง {MinPackSize:N0}–{MaxPackSize:N0}";
        if (validDays is int d && (d < MinPackValidDays || d > MaxPackValidDays))
            return $"อายุแพ็กต้องอยู่ระหว่าง {MinPackValidDays}–{MaxPackValidDays} วัน";
        return null;
    }

    /// <summary>หน่วยบนใบแจ้งหนี้ (<c>ApiFeature.UnitLabel</c>) — ประกอบจากขนาดแพ็กเสมอ ⇒ "จ่ายราคาแพ็กหนึ่งแต่ได้อีกจำนวน" เกิดไม่ได้</summary>
    public static string PackUnitLabel(int packSize) => $"แพ็ก {packSize.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ข้อความ";

    /// <summary>เพดานเฉพาะบริษัทที่แอดมินตั้ง (<c>Subscription.MaxSharedMessagesPerMonth</c>): null = ตามแพ็กเกจ · <see cref="Unlimited"/> = ไม่จำกัด · ≥ 0 = เพดาน ·
    /// คืนข้อความไทย หรือ null = ผ่าน</summary>
    public static string? ValidateOverride(int? value)
        => value is int v && v < Unlimited ? "เพดานต้องเป็น -1 (ไม่จำกัด), 0 ขึ้นไป หรือเว้นว่าง (ตามแพ็กเกจ)" : null;

    /// <summary>บรรทัดสรุปสำหรับหน้าแอดมิน (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string AdminSummary(int? subscriptionOverride, int used, int planLimit, int bonusRemaining, DateTime? bonusExpiresAtUtc)
    {
        var source = subscriptionOverride is int o
            ? (o < 0 ? "ค่าเฉพาะบริษัท: ไม่จำกัด" : $"ค่าเฉพาะบริษัท {N(o)}")
            : "ตามแพ็กเกจ";
        var limit = planLimit < 0 ? "ไม่จำกัด" : N(planLimit);
        var bonus = bonusRemaining > 0
            ? $" · โบนัสคงเหลือ {N(bonusRemaining)}" + (bonusExpiresAtUtc is DateTime e ? $" (ถึง {e.AddHours(7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)})" : "")
            : "";
        return $"ใช้ {N(used)} / {limit} ข้อความเดือนนี้ ({source}){bonus}";
    }

    private static string N(int v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>ขนาด/อายุแพ็ก <c>notify.topup</c> ที่มีผล (<see cref="NotifyQuotaPolicy.TopUpPack"/>)</summary>
public sealed record NotifyTopUpPack(int Size, int ValidDays);

/// <summary>ผลตัดสินโควตาต่อข้อความ</summary>
public enum NotifyQuotaDecision
{
    /// <summary>อยู่ในเพดานแพ็กเกจ (หรือไม่จำกัด)</summary>
    Plan,
    /// <summary>เกินเพดานแพ็กเกจ — ใช้โบนัสที่ซื้อ</summary>
    Bonus,
    /// <summary>เต็มแต่เป็นข้อความจำเป็น — นับเกินเพดาน</summary>
    Essential,
    /// <summary>เต็ม — ไม่ส่งทางช่องกลาง</summary>
    Full,
}
