namespace Accounting.Helpers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.8/§7.1 · QUOTA_ADDON_SUBSCRIPTION_AUDIT D-8 · คำตัดสินข้อ 230-Q1) — <b>ตัวคิดส่วนต่างอัปเกรดกลางรอบตัวเดียว</b> · pure · decimal ·
/// <c>MidpointRounding.AwayFromZero</c>
///
/// <para><b>สูตร:</b> <c>amount = round((ราคาใหม่ต่อรอบ − ราคาเดิมต่อรอบ) × D ÷ C, 2)</c> โดย <c>D</c> = วันที่เหลือของรอบ (ปฏิทินไทย · วันนี้ถึงวันหมดอายุ
/// แบบ [วันนี้, วันหมดอายุ) — วันเดียวกับ <see cref="QuotaAddOnPricing"/> แบบ Coterm) · <c>C</c> = จำนวนวันของรอบ (วันหมดอายุ − เดือนของรอบ)</para>
///
/// <para><b>นโยบาย (คำตัดสินเจ้าของข้อ 178 ②⑥⑦):</b> อัปเกรด = จ่ายส่วนต่างของวันที่เหลือ แล้วเปลี่ยนแพ็กเกจทันทีเมื่อเงินเข้า โดย<b>วันหมดอายุเดิมคงที่</b>
/// (แทนนโยบายชั่วคราว "เริ่มรอบใหม่จากวันอนุมัติ — ส่วนที่เหลือสละ") · ดาวน์เกรด = มีผลวันครบรอบ (<c>Scheduled*</c> เดิม) ไม่คืนส่วนต่าง ·
/// ไม่เก็บย้อนหลัง · แอดมินแก้ยอดได้ (<c>SubscriptionService.OverridePaymentAmountAsync</c> + audit)</para>
///
/// <para><b>ไม่ใช้ได้</b> (คืน <see cref="Result.Applies"/>=false + เหตุ — ผู้เรียกใช้เส้นเดิม "เริ่มรอบใหม่"): ทดลองใช้/ฟรีถาวร/หมดอายุแล้ว ·
/// เหลือ &lt; 1 วัน · ส่วนต่าง ≤ 0 (ราคาต่อรอบของแพ็กเกจใหม่ไม่สูงกว่า)</para>
/// </summary>
public static class PlatformProration
{
    /// <summary>ผลคิด — <see cref="Amount"/> = ยอดส่วนต่างที่ต้องจ่าย (รวม VAT ตามค่าตั้งราคาแพ็กเกจ) · <see cref="Note"/> = ข้อความบนหน้าเว็บ/ใบกำกับ</summary>
    public sealed record Result(bool Applies, decimal Amount, int DaysRemaining, int CycleDays, DateTime CycleStartUtc, DateTime CycleEndUtc,
        string Note, string? NotApplicableReason);

    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static DateTime ThaiDate(DateTime utc) => utc.AddHours(7).Date;

    /// <summary>วันเริ่มรอบปัจจุบัน = วันหมดอายุ − เดือนของรอบ (รอบถูกสร้างด้วย <c>baseDate.AddMonths(months)</c> เสมอ)</summary>
    private static DateTime CycleStartUtc(DateTime cycleEndUtc, int cycleMonths)
        => cycleEndUtc.AddMonths(-(cycleMonths <= 0 ? 1 : cycleMonths));

    /// <summary>คิดส่วนต่างอัปเกรด — <paramref name="oldPricePerCycle"/>/<paramref name="newPricePerCycle"/> = ราคาต่อรอบบิลเดียวกัน (รอบของ subscription ปัจจุบัน)</summary>
    public static Result Upgrade(decimal oldPricePerCycle, decimal newPricePerCycle, int cycleMonths, DateTime cycleEndUtc, DateTime nowUtc,
        bool planActive = true, bool isTrial = false, bool isPermanentFree = false)
    {
        var start = CycleStartUtc(cycleEndUtc, cycleMonths);
        Result No(string why) => new(false, 0m, 0, 0, start, cycleEndUtc, "", why);
        if (isPermanentFree) return No("แพ็กเกจฟรีถาวร — อัปเกรดแล้วเริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (isTrial) return No("ระหว่างทดลองใช้ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (!planActive) return No("แพ็กเกจไม่ได้ใช้งานอยู่ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        var today = ThaiDate(nowUtc);
        var end = ThaiDate(cycleEndUtc);
        var c = (end - ThaiDate(start)).Days;
        if (c < 1) c = 1;
        var d = (end - today).Days;
        if (d < 1) return No("รอบปัจจุบันเหลือไม่ถึง 1 วัน — ต่ออายุเป็นแพ็กเกจใหม่ได้เลย");
        if (d > c) d = c;
        var diff = newPricePerCycle - oldPricePerCycle;
        // ฝ่ายค้าน N4: ข้อความต้องตรงกับสิ่งที่เกิดจริงในเส้นสำรอง (ชำระเต็มรอบ เริ่มรอบใหม่) — ไม่ใช่ "มีผลวันครบรอบ"
        if (diff <= 0m) return No("ราคาต่อรอบบิลนี้ของแพ็กเกจใหม่ไม่สูงกว่าแพ็กเกจปัจจุบัน จึงไม่คิดส่วนต่าง — ชำระเต็มรอบ เริ่มรอบใหม่จากวันที่ชำระ (ส่วนที่เหลือของรอบเดิมไม่ถูกหัก)");
        var amount = R2(diff * d / c);
        if (amount <= 0m) return No("ส่วนต่างน้อยกว่า 1 สตางค์ — ชำระเต็มรอบ เริ่มรอบใหม่จากวันที่ชำระ");
        var note = $"ส่วนต่าง {R2(diff):N2} บาท/รอบ × {d}/{c} วัน (ใช้ได้ถึง {QuotaAddOnPricing.ThaiDateText(end)})";
        return new Result(true, amount, d, c, start, cycleEndUtc, note, null);
    }

    /// <summary>ข้อความบนใบกำกับ/ใบเสร็จของรายการส่วนต่าง — ช่วงที่คิดเงินจริง (วันที่ไทย พ.ศ.): วันเริ่ม = วันหมดอายุ − D วัน (ฝ่ายค้าน N4: ไม่ใช่วันที่จ่าย ซึ่งอาจช้ากว่าวันที่คิด)</summary>
    public static string ReceiptDescription(string fromPlan, string toPlan, DateTime cycleEndUtc, int daysRemaining, int cycleDays)
    {
        var end = ThaiDate(cycleEndUtc);
        return $"ค่าส่วนต่างอัปเกรดแพ็กเกจ {fromPlan} → {toPlan} · {QuotaAddOnPricing.ThaiDateText(end.AddDays(-daysRemaining))}–"
               + $"{QuotaAddOnPricing.ThaiDateText(end)} ({daysRemaining}/{cycleDays} วัน)";
    }

    /// <summary>ส่วนต่างที่สร้างไว้ยังใช้ได้ไหม ณ ตอนเงินเข้า — วันหมดอายุ/แพ็กเกจเปลี่ยนไปแล้ว (ต่ออายุ/แอดมินแก้) ⇒ ล้าสมัย (ให้แอดมินตัดสิน)</summary>
    public static string? StaleReason(DateTime quotedCycleEndUtc, DateTime currentEndUtc, string quotedFromPlan, string currentPlan, DateTime nowUtc)
    {
        // ฝ่ายค้าน S6: เงินเข้าหลังรอบที่คิดส่วนต่างจบแล้ว ⇒ ส่วนต่างไม่มีวันให้ใช้ (ห้ามเปลี่ยนแพ็กเกจของรอบใหม่ด้วยเงินส่วนต่างรอบเก่า)
        if (nowUtc >= quotedCycleEndUtc)
            return $"รอบที่คิดส่วนต่างสิ้นสุดแล้ว ({QuotaAddOnPricing.ThaiDateText(ThaiDate(quotedCycleEndUtc))}) ก่อนเงินเข้า";
        if (Math.Abs((quotedCycleEndUtc - currentEndUtc).TotalSeconds) > 1)
            return $"วันหมดอายุเปลี่ยนจาก {QuotaAddOnPricing.ThaiDateText(ThaiDate(quotedCycleEndUtc))} เป็น {QuotaAddOnPricing.ThaiDateText(ThaiDate(currentEndUtc))} หลังคิดส่วนต่าง";
        if (!string.Equals(quotedFromPlan, currentPlan, StringComparison.Ordinal))
            return $"แพ็กเกจปัจจุบันเปลี่ยนจาก {quotedFromPlan} เป็น {currentPlan} หลังคิดส่วนต่าง";
        return null;
    }
}
