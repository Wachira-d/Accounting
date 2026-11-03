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
        string Note, string? NotApplicableReason, decimal OldPricePerCycle = 0m, decimal NewPricePerCycle = 0m);

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
        // รอบ 233 (LICENSE_CASE_MATRIX E-3): แพ็กเกจเดิมไม่มีค่าบริการ (FreeTrial ที่ Active แต่ไม่ติดธงฟรีถาวร) ⇒ ไม่มีอะไรให้ "ส่วนต่าง" —
        // เดิมคิด ราคาใหม่ × D/C แล้ว "คงวันหมดอายุเดิม" ⇒ ลูกค้าจ่ายเศษของราคาแล้วได้แพ็กเกจจ่ายเงินถึงวันหมดทดลองเท่านั้น
        if (oldPricePerCycle <= 0m) return No("แพ็กเกจเดิมไม่มีค่าบริการ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (SubscriptionExpiry.IsOpenEnded(cycleEndUtc, nowUtc)) return No("แพ็กเกจเดิมไม่มีวันหมดอายุ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
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
        return new Result(true, amount, d, c, start, cycleEndUtc, note, null, R2(oldPricePerCycle), R2(newPricePerCycle));
    }

    /// <summary>ข้อความบนใบกำกับ/ใบเสร็จของรายการส่วนต่าง — ช่วงที่คิดเงินจริง (วันที่ไทย พ.ศ.): วันเริ่ม = วันหมดอายุ − D วัน (ฝ่ายค้าน N4: ไม่ใช่วันที่จ่าย ซึ่งอาจช้ากว่าวันที่คิด)</summary>
    public static string ReceiptDescription(string fromPlan, string toPlan, DateTime cycleEndUtc, int daysRemaining, int cycleDays)
    {
        var end = ThaiDate(cycleEndUtc);
        return $"ค่าส่วนต่างอัปเกรดแพ็กเกจ {fromPlan} → {toPlan} · {QuotaAddOnPricing.ThaiDateText(end.AddDays(-daysRemaining))}–"
               + $"{QuotaAddOnPricing.ThaiDateText(end)} ({daysRemaining}/{cycleDays} วัน)";
    }

    // ═════════ รอบ 231 (คำตัดสินข้อ 231-9 · LICENSE_USAGE_OVERAGE_AUDIT §B.8) — เปลี่ยนรอบบิลกลางรอบ (รายเดือน → รายปี) ═════════

    /// <summary>ผลคิดเปลี่ยนรอบบิล — <see cref="Amount"/> = ราคารอบใหม่เต็ม − <see cref="Credit"/> (มูลค่าคงเหลือรอบเดิม) · ไม่ติดลบ ไม่คืน ·
    /// <see cref="NewEndUtc"/> = <paramref name="nowUtc"/> + เดือนของรอบใหม่ (รอบใหม่ "เริ่มใหม่" — ต่างจากอัปเกรดแพ็กเกจที่คงวันหมดอายุ)</summary>
    public sealed record CycleChangeResult(bool Applies, decimal Amount, decimal Credit, int DaysRemaining, int CycleDays, int NewCycleMonths,
        DateTime CycleEndUtc, DateTime NewStartUtc, DateTime NewEndUtc, string Note, string? NotApplicableReason);

    /// <summary>เปลี่ยนรอบบิลกลางรอบ (ยาวขึ้นเท่านั้น · พร้อมอัปเกรดแพ็กเกจได้): <c>credit = round(ราคาเดิมต่อรอบ × D/C, 2)</c> · <c>amount = max(0, round(ราคาใหม่ต่อรอบ − credit, 2))</c> ·
    /// ราคาทั้งสองฝั่งผ่านตัวตัดสินราคาพิเศษ (<c>PlatformPlanPricing.EffectiveCyclePrice</c>) ก่อนส่งเข้ามา · สั้นลง = ไม่ใช่เส้นนี้ (ตั้งมีผลวันครบรอบ ไม่คืนเงิน — ข้อ 178) ·
    /// ทดลอง/ฟรีถาวร/ไม่ active = เส้น "เริ่มรอบใหม่" เดิม · เหลือไม่ถึง 1 วัน = ต่ออายุรอบใหม่ได้เลย (credit 0 ไม่มีความหมาย) · credit ≥ ราคาใหม่ ⇒ amount 0 (ห้ามติดลบ ห้ามคืน)</summary>
    public static CycleChangeResult CycleChange(decimal oldPricePerCycle, int oldCycleMonths, decimal newPricePerCycle, int newCycleMonths,
        DateTime cycleEndUtc, DateTime nowUtc, bool planActive = true, bool isTrial = false, bool isPermanentFree = false)
    {
        var start = CycleStartUtc(cycleEndUtc, oldCycleMonths);
        var newEnd = newCycleMonths > 0 ? nowUtc.AddMonths(newCycleMonths) : nowUtc;
        CycleChangeResult No(string why) => new(false, 0m, 0m, 0, 0, newCycleMonths, cycleEndUtc, nowUtc, newEnd, "", why);
        if (newCycleMonths <= 0 || oldCycleMonths <= 0) return No("รอบบิลไม่ถูกต้อง");
        if (newCycleMonths <= oldCycleMonths) return No("รอบบิลใหม่ไม่ยาวกว่ารอบเดิม — เปลี่ยนรอบมีผลวันครบรอบ (ไม่คืนเงิน)");
        if (isPermanentFree) return No("แพ็กเกจฟรีถาวร — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (isTrial) return No("ระหว่างทดลองใช้ — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (!planActive) return No("แพ็กเกจไม่ได้ใช้งานอยู่ — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        var today = ThaiDate(nowUtc);
        var end = ThaiDate(cycleEndUtc);
        var c = (end - ThaiDate(start)).Days;
        if (c < 1) c = 1;
        var d = (end - today).Days;
        if (d < 1) return No("รอบปัจจุบันเหลือไม่ถึง 1 วัน — ต่ออายุเป็นรอบใหม่ได้เลย (ไม่มีมูลค่าคงเหลือให้หัก)");
        if (d > c) d = c;
        var credit = R2(Math.Max(0m, oldPricePerCycle) * d / c);
        var amount = R2(Math.Max(0m, newPricePerCycle - credit));
        if (newPricePerCycle <= 0m) return No("รอบบิลใหม่ไม่มีค่าบริการตามตารางราคา");
        var note = $"ค่ารอบใหม่ {R2(newPricePerCycle):N2} บาท หักมูลค่าคงเหลือรอบเดิม {credit:N2} บาท ({d}/{c} วัน) · รอบใหม่เริ่มวันที่ชำระ ถึง {QuotaAddOnPricing.ThaiDateText(ThaiDate(newEnd))}";
        return new CycleChangeResult(true, amount, credit, d, c, newCycleMonths, cycleEndUtc, nowUtc, newEnd, note, null);
    }

    /// <summary>ฝ่ายค้าน S2: ฐาน "ราคาเดิมต่อรอบ" ของส่วนต่าง/เครดิตเปลี่ยนรอบ = <b>ยอดที่จ่ายจริง</b>ของรอบนั้น (รายการชำระ Approved ล่าสุดของแพ็กเกจ+รอบเดียวกัน ·
    /// หลังคูปอง/แก้ยอด) — ไม่มี (เปิดให้โดยไม่ผ่านรายการชำระ) ⇒ ราคาพิเศษ/ราคาแม่แบบที่ resolver คืน · ยอดจ่าย ≤ 0 ไม่นับ</summary>
    /// <summary>ฝ่ายค้าน S3: RuleCode เมื่ออนุมัติเปลี่ยนรอบไม่ได้เพราะใบต่ออายุเดิมถูกจ่าย/แจ้งชำระแล้ว (ระบบไม่ทับ EndDate เอง)</summary>
    public const string RenewalClaimRuleCode = "PLATFORM-CYCLE-CHANGE-RENEWAL-CLAIM";

    /// <remarks>รอบ 233 ฝ่ายค้าน S3: มีรายการ Approved (แม้ยอด 0 — คูปอง 100%/ยกเว้น) ⇒ ฐาน = ยอดนั้น (0 ⇒ เครดิต 0) · ราคาแม่แบบ/พิเศษเฉพาะเมื่อ<b>ไม่มี</b>รายการ Approved เลย (null)</remarks>
    public static decimal OldPriceBasis(decimal? lastApprovedAmount, decimal resolvedPrice)
        => lastApprovedAmount is decimal paid ? R2(Math.Max(0m, paid)) : R2(Math.Max(0m, resolvedPrice));

    /// <summary>ข้อความบรรทัด "หักมูลค่าคงเหลือรอบเดิม" บนใบเสร็จของรายการเปลี่ยนรอบ (เป็น <c>DiscountAmount</c> ของบรรทัดค่าแพ็กเกจ — ฐานหลังลด + VAT = เงินรับ)</summary>
    public static string CycleChangeCreditDescription(decimal credit, int daysRemaining, int cycleDays, DateTime cycleEndUtc)
        => $"หักมูลค่าคงเหลือรอบเดิม {R2(credit):N2} บาท ({daysRemaining}/{cycleDays} วัน ถึง {QuotaAddOnPricing.ThaiDateText(ThaiDate(cycleEndUtc))})";

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
