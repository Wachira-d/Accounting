using Accounting.Models.Enums;

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

    /// <summary>วันเริ่มรอบปัจจุบัน = วันหมดอายุ − เดือนของรอบ (รอบถูกสร้างด้วย <c>PlatformPlanPricing.AddCycleMonths</c> บนปฏิทินไทย — รอบ 235 K-4 · แถวเก่าที่สร้างด้วย AddMonths บน UTC อาจต่าง ≤ 1 วันที่สิ้นเดือน)</summary>
    private static DateTime CycleStartUtc(DateTime cycleEndUtc, int cycleMonths)
        => PlatformPlanPricing.AddCycleMonths(cycleEndUtc, -(cycleMonths <= 0 ? 1 : cycleMonths));   // รอบ 235 K-4: ปฏิทินไทย (คู่กับ PaidPeriod)

    /// <summary>รอบ 237 (237-8): <b>ตัวนับวันคงเหลือตัวเดียว</b> ของรอบปัจจุบัน ณ <paramref name="atUtc"/> (ปฏิทินไทย) — D = วันหมดอายุ − วันนั้น (ยังไม่บีบ · &lt; 1 = หมดแล้ว) ·
    /// C = วันของรอบ (≥ 1) · ผู้เรียก: <see cref="Upgrade"/> · <see cref="CycleChange"/> (ตอนคิดยอด) และ <see cref="RestartCreditDrift"/> (ตอนเงินเข้า ผ่าน <see cref="UpgradeByMode"/>)</summary>
    public static (int Days, int CycleDays) RemainingDays(DateTime cycleEndUtc, int cycleMonths, DateTime atUtc)
    {
        var end = ThaiDate(cycleEndUtc);
        var c = (end - ThaiDate(CycleStartUtc(cycleEndUtc, cycleMonths))).Days;
        if (c < 1) c = 1;
        return ((end - ThaiDate(atUtc)).Days, c);
    }

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
        var end = ThaiDate(cycleEndUtc);
        var (d, c) = RemainingDays(cycleEndUtc, cycleMonths, nowUtc);   // รอบ 237 (237-8): ตัวนับวันคงเหลือตัวเดียว
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
    /// <remarks>รอบ 237 (K-1 · คำตัดสิน 237-2): <paramref name="sameCycleUpgrade"/> = อัปเกรดแพ็กเกจแบบ "เริ่มรอบใหม่" (<see cref="PlatformUpgradeMode.RestartCycle"/>) —
    /// ยอมรอบเท่าเดิม (<c>newCycleMonths == oldCycleMonths</c>) · สูตรเครดิต/ยอด/วันหมดอายุใหม่ตัวเดียวกับเปลี่ยนรอบ (ผู้เรียกผ่าน <see cref="UpgradeByMode"/> เท่านั้น)</remarks>
    public static CycleChangeResult CycleChange(decimal oldPricePerCycle, int oldCycleMonths, decimal newPricePerCycle, int newCycleMonths,
        DateTime cycleEndUtc, DateTime nowUtc, bool planActive = true, bool isTrial = false, bool isPermanentFree = false, bool sameCycleUpgrade = false)
    {
        var newEnd = newCycleMonths > 0 ? PlatformPlanPricing.AddCycleMonths(nowUtc, newCycleMonths) : nowUtc;   // รอบ 235 K-4
        CycleChangeResult No(string why) => new(false, 0m, 0m, 0, 0, newCycleMonths, cycleEndUtc, nowUtc, newEnd, "", why);
        if (newCycleMonths <= 0 || oldCycleMonths <= 0) return No("รอบบิลไม่ถูกต้อง");
        if (sameCycleUpgrade ? newCycleMonths < oldCycleMonths : newCycleMonths <= oldCycleMonths)
            return No("รอบบิลใหม่ไม่ยาวกว่ารอบเดิม — เปลี่ยนรอบมีผลวันครบรอบ (ไม่คืนเงิน)");
        if (isPermanentFree) return No("แพ็กเกจฟรีถาวร — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (isTrial) return No("ระหว่างทดลองใช้ — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (!planActive) return No("แพ็กเกจไม่ได้ใช้งานอยู่ — ชำระรอบใหม่เต็ม เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        var (d, c) = RemainingDays(cycleEndUtc, oldCycleMonths, nowUtc);   // รอบ 237 (237-8): ตัวนับวันคงเหลือตัวเดียว
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

    // ═════════ รอบ 237 (K-1 · คำตัดสินเจ้าของ 2026-11-04 "ทำให้ระบบรองรับทั้ง 2 ทาง สามารถเลือกได้" · DECISIONS 237-1…237-7) ═════════
    // อัปเกรดแพ็กเกจกลางรอบมี 2 แบบ — ตัวตัดสินเดียวของ ยอด/เครดิต/วันหมดอายุใหม่ ของทั้งสองแบบ (ใบเสนอ · สร้างรายการ · อนุมัติ · preview แอดมิน · ใบเสร็จ)
    //   ก KeepExpiry   = วันหมดอายุคงเดิม · จ่าย round((ใหม่ − เดิมจ่ายจริง) × D/C, 2)                      (Upgrade — เส้นเดิมรอบ 230)
    //   ข RestartCycle = รอบใหม่เต็มรอบเริ่มวันชำระ · จ่าย ราคาเต็มรอบ − round(เดิมจ่ายจริง × D/C, 2)       (CycleChange แบบรอบเท่าเดิม)
    // เครดิต ≥ ราคาเต็มรอบใหม่ (แบบ ข) ⇒ ปฏิเสธแบบ ข (ไม่ตัดเครดิตทิ้งเงียบ · ไม่สร้างรายการยอด 0 · ไม่คืนเงิน) — คำตัดสิน 237-3

    /// <summary>RuleCode ของด่านแบบอัปเกรด (ไม่เปิดให้เลือก · แบบ ข เครดิตครอบยอด)</summary>
    public const string UpgradeModeRuleCode = "PLATFORM-UPGRADE-MODE";

    /// <summary>237-3: แบบ ข ที่มูลค่าคงเหลือ ≥ ราคาเต็มรอบใหม่ — ข้อความเดียวของ preview / ใบเสนอ / ด่านสร้างรายการ</summary>
    public const string RestartCreditCoversAllRefusal =
        "มูลค่าคงเหลือของแพ็กเกจเดิมเท่ากับหรือมากกว่าราคาเต็มรอบของแพ็กเกจใหม่ — อัปเกรดแบบเริ่มรอบใหม่ไม่ได้ (ระบบไม่สร้างรายการยอด 0 และไม่คืนส่วนเกิน) · เลือกแบบคงวันหมดอายุเดิม หรือติดต่อผู้ดูแลระบบ NextAcc";

    /// <summary>แบบที่เปิดให้เลือกตามค่าตั้งแพลตฟอร์ม (<c>SiteSettings.PlatformUpgradeModeOffer</c>) — ตัวแรก = ค่าที่เลือกไว้ให้ (ค่าตั้งต้น CustomerChooses ⇒ คงวันหมดอายุ = พฤติกรรมเดิม)</summary>
    public static IReadOnlyList<PlatformUpgradeMode> OfferedModes(PlatformUpgradeModeOffer offer) => offer switch
    {
        PlatformUpgradeModeOffer.KeepExpiryOnly => new[] { PlatformUpgradeMode.KeepExpiry },
        PlatformUpgradeModeOffer.RestartCycleOnly => new[] { PlatformUpgradeMode.RestartCycle },
        _ => new[] { PlatformUpgradeMode.KeepExpiry, PlatformUpgradeMode.RestartCycle },
    };

    /// <summary>ป้ายค่าตั้งบนหน้าแอดมิน (ตัวเดียว — หน้าเว็บไม่มีตารางป้ายเอง)</summary>
    public static string OfferLabel(PlatformUpgradeModeOffer offer) => offer switch
    {
        PlatformUpgradeModeOffer.KeepExpiryOnly => "คงวันหมดอายุเดิมเท่านั้น (จ่ายส่วนต่างเฉพาะวันที่เหลือ)",
        PlatformUpgradeModeOffer.RestartCycleOnly => "เริ่มรอบใหม่เท่านั้น (จ่ายเต็มรอบ หักมูลค่าคงเหลือ)",
        _ => "ให้ลูกค้าเลือก (แสดงทั้ง 2 แบบเทียบกัน · เลือกแบบคงวันหมดอายุไว้ให้)",
    };

    /// <summary>ชื่อแบบที่ลูกค้า/แอดมินเห็น (ตัวเดียว)</summary>
    public static string ModeLabel(PlatformUpgradeMode mode) => mode == PlatformUpgradeMode.RestartCycle
        ? "เริ่มรอบใหม่วันที่ชำระ — จ่ายเต็มรอบ หักมูลค่าคงเหลือแพ็กเดิม"
        : "คงวันหมดอายุเดิม — จ่ายส่วนต่างเฉพาะวันที่เหลือ";

    /// <summary>คำขอของลูกค้า → แบบที่ใช้ · <paramref name="requested"/> null = ตัวแรกของชุดที่เปิด · ขอแบบที่ไม่เปิด ⇒ (null, เหตุ) — เซิร์ฟเวอร์ตรวจซ้ำทุกทางเข้า (ใบเสนอ · สร้างรายการ)</summary>
    public static (PlatformUpgradeMode? Mode, string? Refusal) ResolveMode(PlatformUpgradeModeOffer offer, PlatformUpgradeMode? requested)
    {
        var offered = OfferedModes(offer);
        if (requested is not PlatformUpgradeMode r) return (offered[0], null);
        return offered.Contains(r) ? (r, null)
            : (null, $"ระบบไม่ได้เปิดให้อัปเกรดแบบ \"{ModeLabel(r)}\" — ใช้ได้เฉพาะแบบ \"{ModeLabel(offered[0])}\"");
    }

    /// <summary>เส้นที่ผลของ <see cref="UpgradeByMode"/> พาไป — KeepExpiryProration = รายการส่วนต่าง (IsProration) · RestartWithCredit = รายการเริ่มรอบใหม่หักเครดิต
    /// (IsCycleChange + UpgradeMode=RestartCycle) · FullRestart = ราคาเต็มรอบ เริ่มวันชำระ ไม่มีเครดิต (เส้นสำรองเดิม — เหตุใน Note) · Refused = ชำระแบบนี้ไม่ได้</summary>
    public enum UpgradeRoute { KeepExpiryProration, RestartWithCredit, FullRestart, Refused }

    /// <summary>ผลตัดสินอัปเกรดตามแบบ — <see cref="Amount"/> = ยอดก่อนคูปอง (คูปองคิดบนยอดนี้ = <b>หลังหักเครดิต</b> · 237-4) · <see cref="Credit"/> = มูลค่าคงเหลือแพ็กเดิม
    /// (แบบ ก แสดงในบรรทัดอธิบาย · แบบ ข หักจริง) · <see cref="NewEndUtc"/> = วันหมดอายุหลังชำระวันนี้</summary>
    public sealed record UpgradeDecision(PlatformUpgradeMode Mode, UpgradeRoute Route, string? RefusalReason, decimal Amount, decimal Credit,
        decimal NewCyclePrice, int DaysRemaining, int CycleDays, DateTime CycleEndUtc, DateTime NewEndUtc, string Note, Result Proration, CycleChangeResult Restart)
    {
        public bool Allowed => Route != UpgradeRoute.Refused;
    }

    /// <summary><b>ตัวตัดสินเดียวของอัปเกรดกลางรอบทั้ง 2 แบบ</b> (รอบ 237 · K-1) — ราคาทั้งสองฝั่ง = ราคาต่อรอบบิล<b>ปัจจุบัน</b> หลังราคาพิเศษ (ฝั่งเดิม = ยอดที่จ่ายจริง <see cref="OldPriceBasis"/>)
    /// <list type="bullet">
    /// <item>แบบ ก <see cref="PlatformUpgradeMode.KeepExpiry"/>: <see cref="Upgrade"/> ใช้ได้ ⇒ ส่วนต่าง · วันหมดอายุคงเดิม — ใช้ไม่ได้ ⇒ เส้นสำรองเดิม FullRestart (ผลเหมือนก่อนรอบ 237 ทุกตัวเลข)</item>
    /// <item>แบบ ข <see cref="PlatformUpgradeMode.RestartCycle"/>: <see cref="CycleChange"/> รอบเท่าเดิม ⇒ ราคาเต็มรอบ − เครดิต · วันหมดอายุใหม่ = วันชำระ + รอบ ·
    /// แพ็กเดิมไม่มีค่าบริการ/ไม่มีวันหมดอายุ/เหลือไม่ถึง 1 วัน ⇒ FullRestart (ไม่มีเครดิตให้หัก) · เครดิต ≥ ราคาเต็ม ⇒ Refused (237-3)</item>
    /// </list></summary>
    public static UpgradeDecision UpgradeByMode(PlatformUpgradeMode mode, decimal oldPricePerCycle, decimal newPricePerCycle, int cycleMonths,
        DateTime cycleEndUtc, DateTime nowUtc, bool planActive = true, bool isTrial = false, bool isPermanentFree = false)
    {
        var months = cycleMonths <= 0 ? 1 : cycleMonths;
        var keep = Upgrade(oldPricePerCycle, newPricePerCycle, months, cycleEndUtc, nowUtc, planActive, isTrial, isPermanentFree);
        var restart = CycleChange(oldPricePerCycle, months, newPricePerCycle, months, cycleEndUtc, nowUtc, planActive, isTrial, isPermanentFree, sameCycleUpgrade: true);
        var fullEnd = PlatformPlanPricing.AddCycleMonths(nowUtc, months);
        var newPrice = R2(Math.Max(0m, newPricePerCycle));
        UpgradeDecision Full(string why) => new(mode, UpgradeRoute.FullRestart, null, newPrice, 0m, newPrice, 0, 0, cycleEndUtc, fullEnd, why, keep, restart);
        if (mode == PlatformUpgradeMode.KeepExpiry)
            return keep.Applies
                ? new(mode, UpgradeRoute.KeepExpiryProration, null, keep.Amount, R2(Math.Max(0m, oldPricePerCycle) * keep.DaysRemaining / keep.CycleDays), newPrice,
                    keep.DaysRemaining, keep.CycleDays, cycleEndUtc, cycleEndUtc, keep.Note, keep, restart)
                : Full(keep.NotApplicableReason ?? "ชำระเต็มรอบ เริ่มรอบใหม่จากวันที่ชำระ");
        // แบบ ข — ไม่มีมูลค่าคงเหลือให้หัก ⇒ เต็มรอบ (เหตุเดียวกับแบบ ก) · วันหมดอายุค่าแทน (ไม่มีวันหมดอายุ) ห้ามนับเป็นเครดิตเต็มรอบ
        if (oldPricePerCycle <= 0m) return Full("แพ็กเกจเดิมไม่มีค่าบริการ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (SubscriptionExpiry.IsOpenEnded(cycleEndUtc, nowUtc)) return Full("แพ็กเกจเดิมไม่มีวันหมดอายุ — ชำระแพ็กเกจเต็มรอบ เริ่มรอบใหม่ตั้งแต่วันที่ชำระ");
        if (!restart.Applies) return Full(restart.NotApplicableReason ?? "ชำระเต็มรอบ เริ่มรอบใหม่จากวันที่ชำระ");
        if (restart.Credit >= newPrice)
            return new(mode, UpgradeRoute.Refused, RestartCreditCoversAllRefusal, 0m, restart.Credit, newPrice, restart.DaysRemaining, restart.CycleDays,
                cycleEndUtc, restart.NewEndUtc, RestartCreditCoversAllRefusal, keep, restart);
        return new(mode, UpgradeRoute.RestartWithCredit, null, restart.Amount, restart.Credit, newPrice, restart.DaysRemaining, restart.CycleDays,
            cycleEndUtc, restart.NewEndUtc, restart.Note, keep, restart);
    }

    /// <summary>เส้นอนุมัติของรายการชำระ — ตัดสินจาก<b>ข้อมูลที่ประทับบนรายการตอนซื้อ</b> (ธง + <c>UpgradeMode</c>) เท่านั้น ไม่อ่านค่าตั้งปัจจุบัน
    /// (237-5: แอดมินเปลี่ยนค่าตั้งระหว่างรอเงินเข้า ⇒ รายการเดิมยังได้แบบที่ลูกค้าเลือก) · ไม่มี UpgradeMode (แถวก่อนรอบ 237) = แบบ ก ตามธงเดิม</summary>
    public enum ApprovalRoute { General, KeepExpiryProration, RestartCycleUpgrade, CycleChange }

    public static ApprovalRoute ApprovalRouteOf(PlatformUpgradeMode? storedMode, bool isProration, bool isCycleChange)
        => isProration ? ApprovalRoute.KeepExpiryProration
            : isCycleChange ? (storedMode == PlatformUpgradeMode.RestartCycle ? ApprovalRoute.RestartCycleUpgrade : ApprovalRoute.CycleChange)
            : ApprovalRoute.General;

    /// <summary>บรรทัดสรุปหนึ่งแบบ (หน้าแอดมิน "บันทึกรับเงินเอง" แสดงเทียบให้แอดมินตัดสินยอด) — ยอด · เครดิต (เฉพาะแบบ ข ที่หักจริง) · วันหมดอายุใหม่ (ไทย พ.ศ.) ·
    /// เหตุที่ใช้ไม่ได้ · <paramref name="offered"/> = false ⇒ บอกว่าค่าตั้งปัจจุบันไม่เปิดแบบนี้ให้ลูกค้า (ฝ่ายค้าน 237 N3)</summary>
    public static string ModeSummaryLine(UpgradeDecision up, bool offered = true)
    {
        var line = up.Route switch
        {
            UpgradeRoute.Refused => $"{ModeLabel(up.Mode)}: ชำระแบบนี้ไม่ได้ — {up.RefusalReason}",
            UpgradeRoute.FullRestart => $"{ModeLabel(up.Mode)}: ราคาเต็มรอบ ฿{up.Amount:N2} ใช้ได้ถึง {QuotaAddOnPricing.ThaiDateText(ThaiDate(up.NewEndUtc))} ({up.Note})",
            // แบบ ก: ยอด = ส่วนต่างของวันที่เหลือ — ไม่มี "มูลค่าคงเหลือที่หัก" (ฝ่ายค้าน N3: เดิมขึ้นเครดิตเหมือนถูกหักจากยอด)
            UpgradeRoute.KeepExpiryProration => $"{ModeLabel(up.Mode)}: ยอด ฿{up.Amount:N2} (ส่วนต่างของวันที่เหลือ {up.DaysRemaining}/{up.CycleDays} วัน) ใช้ได้ถึง {QuotaAddOnPricing.ThaiDateText(ThaiDate(up.NewEndUtc))} (วันหมดอายุคงเดิม)",
            _ => $"{ModeLabel(up.Mode)}: ยอด ฿{up.Amount:N2} (ราคาเต็มรอบ ฿{up.NewCyclePrice:N2} − มูลค่าคงเหลือแพ็กเดิม ฿{up.Credit:N2} · {up.DaysRemaining}/{up.CycleDays} วัน) ใช้ได้ถึง {QuotaAddOnPricing.ThaiDateText(ThaiDate(up.NewEndUtc))}",
        };
        return offered ? line : line + " — ค่าตั้งปัจจุบันไม่เปิดแบบนี้ให้ลูกค้าเลือก";
    }

    /// <summary>รอบ 237 (คำตัดสิน 237-8 · ฝ่ายค้าน S3): เครดิตแบบ ข ต้องตรงกับ<b>วันที่เงินเข้า</b> — <paramref name="atPayment"/> = ผลของ <see cref="UpgradeByMode"/>
    /// (แบบ ข) ที่คิด ณ วันชำระจริง (ตัวนับวันคงเหลือตัวเดียว <see cref="RemainingDays"/>) · วันคงเหลือต่างจากที่ประทับบนรายการ (<paramref name="quotedDays"/>) หรือ
    /// ณ วันชำระแบบ ข ใช้ไม่ได้แล้ว ⇒ คืนเหตุ (ยอดเดิม vs ยอดที่ถูก) ให้ผู้เรียกพักให้แอดมิน — ระบบไม่ประทับผลเอง · เงินเข้าวันเดียวกับวันสร้างรายการ ⇒ null (ผ่านปกติ)</summary>
    public static string? RestartCreditDrift(UpgradeDecision atPayment, int? quotedDays, decimal? quotedCredit, decimal quotedAmount, DateTime paidAtUtc)
    {
        if (atPayment.Route == UpgradeRoute.RestartWithCredit && quotedDays is int qd && atPayment.DaysRemaining == qd) return null;
        var paid = QuotaAddOnPricing.ThaiDateText(ThaiDate(paidAtUtc));
        var was = $"รายการคิดไว้ เหลือ {quotedDays?.ToString() ?? "-"}/{atPayment.CycleDays} วัน เครดิต ฿{quotedCredit ?? 0m:N2} ยอด ฿{quotedAmount:N2}";
        return atPayment.Route == UpgradeRoute.RestartWithCredit
            ? $"เงินเข้าวันที่ {paid} เหลือ {atPayment.DaysRemaining}/{atPayment.CycleDays} วัน — {was} แต่ที่ถูก ณ วันชำระ = เครดิต ฿{atPayment.Credit:N2} ยอด ฿{atPayment.Amount:N2}"
            : $"เงินเข้าวันที่ {paid} — {was} แต่ ณ วันชำระอัปเกรดแบบเริ่มรอบใหม่หักเครดิตไม่ได้แล้ว ({atPayment.RefusalReason ?? atPayment.Note})";
    }

    /// <summary>ทางไปต่อของแอดมินเมื่อเครดิตไม่ตรงวันชำระ (237-8) — ข้อความเดียวของการพัก/ด่านอนุมัติ</summary>
    public const string RestartCreditDriftNextStep = "อนุมัติตามยอดเดิม (ระบุเหตุผลในช่องหมายเหตุ — ลง audit) หรือปฏิเสธ/คืนเงินแล้วให้ลูกค้าสร้างรายการใหม่";
    public const string RestartCreditDriftRuleCode = "PLATFORM-UPGRADE-RESTART-CREDIT-DRIFT";

    /// <summary>ข้อความรายการ (ใบกำกับ/ใบเสร็จ · ศูนย์บิล · หน้าตรวจสลิป) ของอัปเกรดแบบเริ่มรอบใหม่ — บอกแบบ + ช่วงรอบใหม่ + เครดิตที่หัก (วันที่ไทย พ.ศ.) ·
    /// <paramref name="periodStartUtc"/>/<paramref name="periodEndUtc"/> null (ยังไม่อนุมัติ) ⇒ "นับจากวันที่ชำระ"</summary>
    public static string RestartUpgradeDescription(string fromPlan, string toPlan, int months, decimal credit, int daysRemaining, int cycleDays,
        DateTime creditCycleEndUtc, DateTime? periodStartUtc = null, DateTime? periodEndUtc = null)
    {
        var period = periodStartUtc is DateTime s && periodEndUtc is DateTime e
            ? $"{QuotaAddOnPricing.ThaiDateText(ThaiDate(s))}–{QuotaAddOnPricing.ThaiDateText(ThaiDate(e))}"
            : "นับจากวันที่ชำระ";
        return $"ค่าอัปเกรดแพ็กเกจ {fromPlan} → {toPlan} แบบเริ่มรอบใหม่ {months} เดือน ({period})"
               + (credit > 0m ? " · " + CycleChangeCreditDescription(credit, daysRemaining, cycleDays, creditCycleEndUtc) : "");
    }
}
