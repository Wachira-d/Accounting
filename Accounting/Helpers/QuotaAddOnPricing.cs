using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.5 · คำตัดสินข้อ 229-4/229-5) — <b>ตัวคิดยอดเดียวของการซื้อโควตาเสริม</b> · pure · decimal · <c>MidpointRounding.AwayFromZero</c>
///
/// <para>ให้ <c>P</c> = ราคาต่อหน่วยต่อเดือน · <c>Pa</c> = ราคาต่อหน่วยต่อปี (<c>AnnualUnitPrice ?? P × 12</c>) · <c>u</c> = จำนวนหน่วย ·
/// วันนับตามปฏิทินไทย (UTC+7) แบบ [วันเริ่ม, วันสิ้นสุด)</para>
/// <list type="bullet">
/// <item><b>OneOffMonth</b> — ถึงสิ้นเดือนไทยนี้ (หรือ N วันตาม <c>ValidDays</c> · Consumable ไม่ตั้ง = 60 วัน) · <c>perUnit = P</c> (ไม่คิดตามวัน — บอกบนหน้าซื้อ)</item>
/// <item><b>RecurringMonthly</b> — เดือนแรก <c>perUnit = round(P × D / C, 2)</c> (D = วันที่เหลือในเดือนรวมวันนี้ · C = วันในเดือน) · เดือนถัดไป <c>round(u × P, 2)</c></item>
/// <item><b>Coterm</b> — ถึงวันหมดอายุแพ็กเกจ · <c>R</c> = <c>Pa</c> ถ้ารอบ 12 เดือน ไม่งั้น <c>P × เดือนของรอบ</c> · <c>perUnit = round(R × D / C, 2)</c>
/// (D = วันที่เหลือ · C = วันของรอบปัจจุบัน) · ต่ำกว่า <c>P</c> และเปิดขั้นต่ำ ⇒ <c>perUnit = P</c></item>
/// </list>
/// <para><b>คิดราคาต่อหน่วยก่อนแล้วคูณจำนวน</b> ⇒ บรรทัดบนใบกำกับได้ <c>LineAmount = round(Qty × UnitPrice, 2)</c> พอดี (CLAUDE.md กฎ A §86/4)</para>
/// </summary>
public static class QuotaAddOnPricing
{
    /// <summary>ข้อเสนอที่ใช้คิด (snapshot จาก <c>QuotaAddOnOffer</c>)</summary>
    public sealed record Offer(string ResourceKey, decimal MonthlyUnitPrice, decimal? AnnualUnitPrice, QuotaAddOnMode AllowedModes,
        bool MinChargeOneMonth, int? ValidDays, int UnitSize = 1, int? MaxUnitsPerCompany = null);

    /// <summary>บริบทแพ็กเกจของผู้ซื้อ ณ ตอนคิด</summary>
    public sealed record PlanContext(bool PlanActive, bool IsTrial, bool IsPermanentFree, DateTime PlanEndUtc, int CycleMonths);

    /// <summary>ผลคิดยอด — <see cref="Allowed"/>=false ⇒ <see cref="DenyReason"/> (ไทย) · เงินทุกช่องคิดแล้ว (หน้าเว็บแสดงอย่างเดียว)</summary>
    public sealed record Quote(QuotaAddOnMode Mode, bool Allowed, string? DenyReason, int Units, decimal PerUnit, decimal Amount,
        DateTime PeriodStartUtc, DateTime? PeriodEndUtc, string Note, decimal? NextMonthlyAmount, bool MinChargeApplied = false);

    public const int ConsumableDefaultValidDays = 60;
    public const int MaxUnitsPerPurchase = 100;

    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static DateTime ThaiDate(DateTime utc) => utc.AddHours(7).Date;
    /// <summary>วันที่ไทย (พ.ศ.) dd/MM/yyyy — InvariantCulture เสมอ (เครื่อง th-TH จะบวก 543 ซ้ำ)</summary>
    public static string ThaiDateText(DateTime thaiDate)
        => thaiDate.AddYears(543).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTime ThaiMidnightToUtc(DateTime thaiDate) => DateTime.SpecifyKind(thaiDate.AddHours(-7), DateTimeKind.Utc);

    /// <summary>ต้นเดือนไทยถัดไป (เวลา UTC) — วันสิ้นสุดของสิทธิ์รายเดือน/เดือนเดียว</summary>
    public static DateTime NextThaiMonthStartUtc(DateTime nowUtc)
    {
        var d = ThaiDate(nowUtc);
        return ThaiMidnightToUtc(new DateTime(d.Year, d.Month, 1).AddMonths(1));
    }

    /// <summary>ราคาต่อหน่วยต่อรอบแพ็กเกจ (<c>R</c>) — รอบ 12 เดือน = ราคาปีที่ตั้ง (ไม่ตั้ง = เดือน × 12) · รอบอื่น = เดือน × จำนวนเดือน</summary>
    private static decimal CycleUnitPrice(Offer o, int cycleMonths)
    {
        var months = cycleMonths <= 0 ? 1 : cycleMonths;
        if (months == 12) return o.AnnualUnitPrice is decimal a && a > 0m ? a : o.MonthlyUnitPrice * 12m;
        return o.MonthlyUnitPrice * months;
    }

    /// <summary>โหมดที่ทรัพยากรชนิดนี้รับได้ (Consumable = เดือนเดียวเท่านั้น — ตัวนับรายงวดไม่มีความหมายแบบต่อเนื่อง)</summary>
    public static QuotaAddOnMode ModesFor(string resourceKey, QuotaAddOnMode allowed)
    {
        var res = QuotaResources.Find(resourceKey);
        if (res == null || !res.Sellable) return QuotaAddOnMode.None;
        return res.Kind == QuotaResources.ResourceKind.Consumable ? allowed & QuotaAddOnMode.OneOffMonth : allowed;
    }

    public static Quote Compute(Offer o, QuotaAddOnMode mode, int units, PlanContext plan, DateTime nowUtc)
    {
        Quote Deny(string why) => new(mode, false, why, units, 0m, 0m, nowUtc, null, "", null);
        if (units < 1) return Deny("จำนวนต้องอย่างน้อย 1");
        if (units > MaxUnitsPerPurchase) return Deny($"ซื้อได้ครั้งละไม่เกิน {MaxUnitsPerPurchase} หน่วย");
        if (o.MonthlyUnitPrice <= 0m) return Deny("ข้อเสนอนี้ยังไม่ได้ตั้งราคา — ยังซื้อไม่ได้ (แจ้งผู้ดูแลระบบ)");
        if ((ModesFor(o.ResourceKey, o.AllowedModes) & mode) == 0 || mode is not (QuotaAddOnMode.OneOffMonth or QuotaAddOnMode.RecurringMonthly or QuotaAddOnMode.Coterm))
            return Deny("วิธีซื้อนี้ไม่เปิดสำหรับรายการนี้");
        if (!plan.PlanActive) return Deny("แพ็กเกจหมดอายุ/ถูกระงับ — ต่ออายุแพ็กเกจก่อนจึงจะซื้อโควตาเสริมได้");

        var P = o.MonthlyUnitPrice;
        switch (mode)
        {
            case QuotaAddOnMode.OneOffMonth:
            {
                var res = QuotaResources.Find(o.ResourceKey);
                var days = o.ValidDays ?? (res?.Kind == QuotaResources.ResourceKind.Consumable ? ConsumableDefaultValidDays : (int?)null);
                var end = days is int vd && vd > 0 ? nowUtc.AddDays(vd) : NextThaiMonthStartUtc(nowUtc);
                var per = R2(P);
                var note = days is int v2 && v2 > 0 ? $"ใช้ได้ {v2} วัน" : "ถึงสิ้นเดือนนี้ (ราคาเต็มเดือน ไม่คิดตามวัน)";
                return new Quote(mode, true, null, units, per, R2(units * per), nowUtc, end, note, null);
            }
            case QuotaAddOnMode.RecurringMonthly:
            {
                var d = ThaiDate(nowUtc);
                var C = DateTime.DaysInMonth(d.Year, d.Month);
                var D = C - d.Day + 1;
                var per = R2(P * D / C);
                return new Quote(mode, true, null, units, per, R2(units * per), nowUtc, null,
                    $"เดือนนี้คิด {D}/{C} วัน · จากนั้นเดือนละ {R2(units * P):N2} บาท (ยกเลิกได้ มีผลสิ้นเดือน)", R2(units * P));
            }
            default: // Coterm
            {
                if (plan.IsPermanentFree) return Deny("แพ็กเกจฟรีถาวรไม่มีวันหมดอายุ — เลือก \"เดือนเดียว\" หรือ \"ทุกเดือน\" แทน");
                if (plan.IsTrial) return Deny("ระหว่างทดลองใช้ซื้อแบบ \"ถึงวันหมดอายุแพ็กเกจ\" ไม่ได้ — เลือก \"เดือนเดียว\" หรือ \"ทุกเดือน\" แทน");
                var today = ThaiDate(nowUtc);
                var endDate = ThaiDate(plan.PlanEndUtc);
                var D = (endDate - today).Days;
                if (D < 1) return Deny("แพ็กเกจใกล้หมดอายุแล้ว — ซื้อพร้อมการต่ออายุแทน (เลือกที่หน้าต่ออายุ)");
                var months = plan.CycleMonths <= 0 ? 1 : plan.CycleMonths;
                var C = (endDate - endDate.AddMonths(-months)).Days;
                if (C < 1) C = 1;
                var R = CycleUnitPrice(o, months);
                var per = R2(R * D / C);
                var min = false;
                if (o.MinChargeOneMonth && per < R2(P)) { per = R2(P); min = true; }
                var note = $"ถึง {ThaiDateText(endDate)} · คิด {D}/{C} วัน" + (min ? " · ขั้นต่ำ 1 เดือน" : "");
                return new Quote(mode, true, null, units, per, R2(units * per), nowUtc, plan.PlanEndUtc, note, null, min);
            }
        }
    }

    /// <summary>รอบ 229 ฝ่ายค้าน B2: ข้อเสนอที่คิดไว้ตอนสร้างรายการยังใช้ได้ไหม ณ ตอนจ่าย/อนุมัติ</summary>
    public enum Staleness { Fresh = 0, PeriodEnded = 1, MonthChanged = 2, PlanEndChanged = 3 }

    /// <summary>ตัวตัดสินเดียวของ "รายการรอชำระล้าสมัย" (pure) — ใช้ทั้งด่านเริ่มชำระ/แนบสลิป/อนุมัติ (409) และตอนเงินเข้าช้า
    /// <list type="bullet">
    /// <item>วันสิ้นสุดผ่านไปแล้ว ⇒ <see cref="Staleness.PeriodEnded"/> (เดือนเดียวถึงสิ้นเดือนที่ข้ามเดือน · ถึงวันหมดอายุที่เลยวัน)</item>
    /// <item>รายเดือน: เดือนไทยของตอนคิด ≠ เดือนนี้ ⇒ <see cref="Staleness.MonthChanged"/> (เดือนแรกคิดตามวันของอีกเดือน)</item>
    /// <item>ถึงวันหมดอายุ: วันหมดอายุแพ็กเกจตอนนี้ ≠ ที่คิดไว้ ⇒ <see cref="Staleness.PlanEndChanged"/> (ต่ออายุ/อัปเกรดระหว่างนั้น — วันที่/ยอดคิดผิดฐาน)</item>
    /// </list></summary>
    public static Staleness StaleCheck(QuotaAddOnMode mode, DateTime quotedAtUtc, DateTime? periodEndUtc, DateTime? currentPlanEndUtc, DateTime nowUtc)
    {
        if (periodEndUtc is DateTime end && end <= nowUtc) return Staleness.PeriodEnded;
        if (mode == QuotaAddOnMode.RecurringMonthly && AddOnBilling.PeriodOf(quotedAtUtc) != AddOnBilling.PeriodOf(nowUtc)) return Staleness.MonthChanged;
        if (mode == QuotaAddOnMode.Coterm && (currentPlanEndUtc == null || periodEndUtc == null
                || Math.Abs((currentPlanEndUtc.Value - periodEndUtc.Value).TotalMinutes) > 1))
            return Staleness.PlanEndChanged;
        return Staleness.Fresh;
    }

    /// <summary>ข้อความ 409 ของรายการล้าสมัย (ไทย · ทางไปต่อ = สร้างรายการใหม่)</summary>
    public static string StaleMessage(Staleness s) => s switch
    {
        Staleness.PeriodEnded => "รายการนี้หมดช่วงที่คิดราคาไว้แล้ว — กรุณาสร้างรายการซื้อใหม่ (ยังไม่ได้คิดเงิน)",
        Staleness.MonthChanged => "ข้ามเดือนแล้ว ยอดเดือนแรกที่คิดไว้ไม่ตรง — กรุณาสร้างรายการซื้อใหม่ (ยังไม่ได้คิดเงิน)",
        Staleness.PlanEndChanged => "วันหมดอายุแพ็กเกจเปลี่ยนหลังคิดราคา — กรุณาสร้างรายการซื้อใหม่ (ยังไม่ได้คิดเงิน)",
        _ => "",
    };

    /// <summary>เดือนเดียวแบบกำหนดจำนวนวัน (ไม่ใช่ "ถึงสิ้นเดือนไทย") — ดูจากช่วงที่คิดไว้ (ไม่ต้องมีคอลัมน์เพิ่ม)</summary>
    public static bool IsFixedDays(QuotaAddOnMode mode, DateTime quotedStartUtc, DateTime? periodEndUtc)
        => mode == QuotaAddOnMode.OneOffMonth && periodEndUtc is DateTime e && e != NextThaiMonthStartUtc(quotedStartUtc);

    /// <summary>เงินเข้าช้าสำหรับ "เดือนเดียวแบบกำหนดวัน" — ช่วงสิทธิ์เริ่มวันจ่ายด้วยความยาวเดิม (ราคาเดียวกัน · ลูกค้าไม่เสียวัน) · แบบอื่นคืน null (ตัดสินด้วย <see cref="StaleCheck"/>)</summary>
    public static DateTime? LatePaymentEnd(QuotaAddOnMode mode, bool fixedDays, DateTime quotedStartUtc, DateTime? periodEndUtc, DateTime paidAtUtc)
        => mode == QuotaAddOnMode.OneOffMonth && fixedDays && periodEndUtc is DateTime e && paidAtUtc > quotedStartUtc
            ? paidAtUtc + (e - quotedStartUtc) : null;

    /// <summary>ยอดเดือนถัดไปของรายเดือน (เข้าใบแจ้งหนี้ค่าใช้งาน) — ราคาเดือน ณ วันซื้อ × จำนวน</summary>
    public static decimal RecurringMonthAmount(int units, decimal monthlyUnitPriceSnapshot) => R2(units * R2(monthlyUnitPriceSnapshot));

    /// <summary>บรรทัดต่ออายุ (เต็มรอบ) — ราคาต่อหน่วย <c>R</c> ปัดแล้ว และยอด <c>round(u × R, 2)</c></summary>
    public static (decimal PerUnit, decimal Amount) RenewalLine(Offer o, int units, int cycleMonths)
    {
        var per = R2(CycleUnitPrice(o, cycleMonths));
        return (per, R2(units * per));
    }

    /// <summary>ข้อเสนอที่ผู้ซื้อเห็นยังตรงของจริงไหม (ลอก <c>notify.topup</c>) · null = ตรง · ข้อความ = เปลี่ยนแล้ว (409 · ยังไม่คิดเงิน)</summary>
    public static string? OfferChanged(decimal? expectedPerUnit, decimal? expectedAmount, Quote q)
    {
        if (expectedAmount is decimal a && a != q.Amount)
            return $"ยอดเปลี่ยนเป็น {q.Amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} บาทแล้ว — กรุณาตรวจรายละเอียดแล้วยืนยันอีกครั้ง (ยังไม่ได้คิดเงิน)";
        if (expectedPerUnit is decimal p && p != q.PerUnit)
            return $"ราคาต่อหน่วยเปลี่ยนเป็น {q.PerUnit.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} บาทแล้ว — กรุณาตรวจรายละเอียดแล้วยืนยันอีกครั้ง (ยังไม่ได้คิดเงิน)";
        return null;
    }

    /// <summary>ตรวจค่าที่แอดมินตั้ง · null = ผ่าน</summary>
    public static string? ValidateOffer(string? resourceKey, int unitSize, decimal monthly, decimal? annual, QuotaAddOnMode modes, int? maxUnits, int? validDays)
    {
        var res = QuotaResources.Find(resourceKey);
        if (res == null) return "ทรัพยากรต้องเลือกจากรายการของระบบเท่านั้น";
        if (!res.Sellable) return $"\"{res.Label}\" ยังไม่เปิดให้ขายเป็นโควตาเสริม (ตั้งได้เฉพาะเพดานต่อแพ็กเกจ/รายบริษัท)";
        if (unitSize < 1 || unitSize > 1_000_000) return "ขนาดต่อหน่วยต้องอยู่ระหว่าง 1–1,000,000";
        if (monthly < 0m || (annual is decimal a && a < 0m)) return "ราคาต้องไม่ติดลบ";
        if ((modes & ~(QuotaAddOnMode.OneOffMonth | QuotaAddOnMode.RecurringMonthly | QuotaAddOnMode.Coterm)) != 0) return "วิธีซื้อไม่ถูกต้อง";
        if (ModesFor(res.Key, modes) == QuotaAddOnMode.None)
            return res.Kind == QuotaResources.ResourceKind.Consumable
                ? "ทรัพยากรแบบใช้แล้วหมด (เช่น หน้า OCR) ขายได้เฉพาะแบบ \"เดือนเดียว\""
                : "ต้องเปิดอย่างน้อย 1 วิธีซื้อ";
        if (maxUnits is int m && m < 1) return "ซื้อได้สูงสุดต้องอย่างน้อย 1 (เว้นว่าง = ไม่จำกัด)";
        if (validDays is int v && (v < 1 || v > 366)) return "อายุของการซื้อแบบเดือนเดียวต้องอยู่ระหว่าง 1–366 วัน";
        return null;
    }
}
