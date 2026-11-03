using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 233 (คำตัดสิน 233-1…233-8 · erp-review/2026-11-03/LICENSE_CASE_MATRIX.md) — <b>ตัวตัดสินเดียวของ "สิ่งที่ลูกค้าจะได้ถ้าจ่ายเงินนี้"</b> · pure
/// <para>เจ้าของ: "ควรจะแสดงสิ่งที่ลูกค้าจะได้ถ้าจ่ายเงินนี้ เช่น ต่ออายุไปอีก 1 ปีจากวันหมดอายุเดิม จะเปลี่ยนวันหมดอายุเป็นวันที่เท่าไหร่ ·
/// ถ้าเลือกแพ็คต่ำกว่าต้องขึ้นแจ้งว่าชำระไม่ได้เพราะอะไร" — เดิมหน้าชำระบอกแค่ "เริ่มรอบใหม่จากวันที่ชำระ" ไม่มีวันที่ และดาวน์เกรดได้ข้อความ error ลอย ๆ</para>
/// <list type="bullet">
/// <item><see cref="PaidPeriod"/> = <b>ตัวคำนวณวันที่ตัวเดียว</b> ของทั้ง preview (วันชำระ = วันนี้) และตอนอนุมัติชำระ (<c>SubscriptionService.ReviewPaymentLockedAsync</c>
/// ทั้ง 3 เส้น: ซื้อ/ต่ออายุ · ส่วนต่างอัปเกรด · เปลี่ยนรอบบิล — วันชำระ = <c>PlatformPlanPricing.ApprovedPaymentDate</c>) ⇒ วันที่ที่ preview บอก = วันที่ที่ประทับจริงเมื่อจ่ายวันนี้
/// (ล็อกด้วย <c>tools/required_call_site_check.py</c>)</item>
/// <item><see cref="ForPurchase"/>/<see cref="ForScheduled"/>/<see cref="ForSame"/>/<see cref="Refused"/>/<see cref="ForAnonymous"/> = ผลที่หน้าเว็บแสดงอย่างเดียว
/// (server computes · page displays) — ทางเข้า: <c>SubscriptionService.QuotePlanChangeAsync</c> (hub ผู้เช่า) · <c>PublicCheckoutService.QuoteAsync/CreateOrderAsync</c></item>
/// <item>ยอดเงินไม่คิดที่นี่ — รับจากตัวคิดราคาเดิม (<c>PlatformProration</c> · <c>PlatformPriceOverrideStore</c> · <c>PlatformCouponStore</c>) แล้วแตกเป็นบรรทัดอธิบาย ·
/// เครดิตส่วนต่างอัปเกรด (คำตัดสิน 233-2 = แบบ ก คงวันหมดอายุ) แสดงเป็น "ค่าแพ็กใหม่ส่วนที่เหลือ − เครดิตแพ็กเดิม" โดยผลรวม = ยอดที่คิดจริงพอดี</item>
/// </list>
/// </summary>
public static class PlanPurchasePreview
{
    public const string PlanTabUrl = "/pages/billing.html#tab=plan";
    public const string DocsTabUrl = "/pages/billing.html#tab=docs";

    /// <summary>ชนิดสิ่งที่จะเกิด — NewPurchase (ทดลอง/หมดอายุ/ฟรี → ซื้อ) · Renewal (แพ็กเดิมรอบเดิม) · Upgrade (ส่วนต่าง คงวันหมดอายุ) ·
    /// UpgradeRestart (อัปเกรดเต็มรอบ เริ่มวันที่ชำระ) · CycleChange (รอบยาวขึ้น หักมูลค่าคงเหลือ) · Scheduled (ดาวน์เกรด/รอบสั้นลง มีผลวันครบรอบ ไม่ต้องจ่าย) ·
    /// Same (แพ็กเดิมรอบเดิมในหน้าเปลี่ยนแพ็กเกจ) · Refused (ชำระไม่ได้ — เหตุ + ทางไปต่อ)</summary>
    public enum PreviewKind { NewPurchase, Renewal, Upgrade, UpgradeRestart, CycleChange, Scheduled, Same, Refused }

    /// <summary>ผลที่หน้าเว็บแสดง — วันที่เป็น UTC (หน้าเว็บแปลงผ่าน <c>Layout.date</c>) และมีข้อความไทย พ.ศ. สำเร็จรูปใน <see cref="Lines"/>/<see cref="DateLine"/></summary>
    public sealed record Result(
        string Kind, bool Allowed, string? RefusalReason, string? NextStep, string? NextStepUrl,
        string CurrentPlan, string CurrentPlanName, string CurrentCycle, DateTime? CurrentEndUtc,
        string NewPlan, string NewPlanName, string NewCycle,
        DateTime? NewStartUtc, DateTime? NewEndUtc, bool EndUnchanged, DateTime? EffectiveAtUtc,
        decimal ListPrice, decimal Credit, string? CreditFormula, int? DaysRemaining, int? CycleDays,
        decimal CouponDiscount, decimal AmountDue, bool VatIncluded,
        string Headline, string DateLine, IReadOnlyList<string> Lines, IReadOnlyList<string> Notes);

    /// <summary>สถานะปัจจุบันของบริษัท (pure — <see cref="CurrentOf"/> แปลงจาก entity)</summary>
    public sealed record Current(SubscriptionPlan Plan, string PlanName, BillingCycle Cycle, SubscriptionStatus Status,
        bool IsPermanentFree, DateTime EndUtc, decimal MonthlyPrice, bool CancelAtPeriodEnd,
        SubscriptionPlan? ScheduledPlan, DateTime? ScheduledEffectiveAtUtc)
    {
        public bool IsTrial => Status == SubscriptionStatus.Trial;
        public bool ActivePaid => Status == SubscriptionStatus.Active && !IsPermanentFree;
    }

    public sealed record Target(SubscriptionPlan Plan, string PlanName, BillingCycle Cycle, decimal MonthlyPrice);

    /// <summary>ตัวเลขเงินจากตัวคิดราคาเดิม — <see cref="ListPrice"/> = ยอดก่อนคูปอง (ส่วนต่าง/ค่ารอบใหม่หลังหักเครดิต/ราคาเต็ม) ·
    /// <see cref="OldPriceBasis"/> = ฐานราคาเดิมต่อรอบ (ยอดที่จ่ายจริง · <c>PlatformProration.OldPriceBasis</c>) ใช้แตกบรรทัดเครดิตของส่วนต่าง</summary>
    public sealed record Money(decimal ListPrice, decimal CouponDiscount, decimal AmountDue,
        bool Prorated = false, bool CycleChange = false, decimal? OldPriceBasis = null,
        int? DaysRemaining = null, int? CycleDays = null, decimal? CycleChangeCredit = null,
        string? CouponLabel = null, string? CouponProblem = null, string? SpecialPriceNote = null, decimal? NewCyclePrice = null);

    /// <summary>ข้อความเมื่อมูลค่าคงเหลือครอบค่ารอบใหม่ทั้งหมด — ตัวเดียวกับที่ <c>RequestPlanPaymentAsync</c> ปฏิเสธ (preview บอกก่อนกด)</summary>
    public const string CreditCoversAllRefusal = "มูลค่าคงเหลือของรอบเดิมครอบคลุมค่ารอบใหม่ทั้งหมด — ติดต่อผู้ดูแลระบบ NextAcc ให้บันทึกเปลี่ยนรอบแบบยกเว้นค่าบริการ (ยอด 0)";

    /// <summary>ฝ่ายค้าน N3: ออก "ใบกำกับภาษี" (ยอดรวม VAT) ไหม — ตัวตัดสินเดียวของตัวออกใบ (<c>PlatformBillingDocumentIssuer</c>) และ preview:
    /// จด VAT <b>และ</b> เลขผู้เสียภาษีของผู้ให้บริการถูกต้อง (13 หลัก + checksum)</summary>
    public static bool IssuesTaxInvoice(bool platformVatRegistered, string? platformSellerTaxId)
        => platformVatRegistered && ThaiTaxId.IsValid(platformSellerTaxId);

    public static Current CurrentOf(Subscription sub, PlanTemplate? currentTemplate)
        => new(sub.Plan, currentTemplate?.Name ?? sub.Plan.ToString(), sub.BillingCycle, sub.Status, sub.IsPermanentFree, sub.EndDate,
            currentTemplate?.MonthlyPrice ?? 0m, sub.CancelAtPeriodEnd, sub.ScheduledPlan, sub.ScheduledPlanEffectiveAt);

    public static Target TargetOf(PlanTemplate t, BillingCycle cycle) => new(t.Plan, t.Name, cycle, t.MonthlyPrice);

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>วันที่ไทย พ.ศ. ของเวลา UTC (dd/MM/yyyy) — ตัวแปลงเดียวกับใบเสร็จ (<see cref="QuotaAddOnPricing.ThaiDateText"/>)</summary>
    private static string BeDate(DateTime utc) => QuotaAddOnPricing.ThaiDateText(utc.AddHours(7).Date);

    // ═════════ ตัวคำนวณวันที่ตัวเดียว (preview + อนุมัติชำระ) ═════════

    /// <summary>ผลของ <see cref="PaidPeriod"/> — <see cref="StartedAtApproval"/> = วันรับเงินย้อนหลังจนรอบที่คิดจบก่อนเวลาอนุมัติ ⇒ นับจากวันอนุมัติแทน (ฝ่ายค้าน B2) ·
    /// deconstruct 2 ค่า (start, end) ได้เหมือนเดิม</summary>
    public sealed record PaidPeriodResult(DateTime StartUtc, DateTime EndUtc, bool StartedAtApproval)
    {
        public void Deconstruct(out DateTime startUtc, out DateTime endUtc) { startUtc = StartUtc; endUtc = EndUtc; }
    }

    /// <summary>ข้อความประวัติ/audit เมื่อ <see cref="PaidPeriodResult.StartedAtApproval"/> (ตัวเดียว)</summary>
    public const string BackdatedNote = "วันรับเงินย้อนหลังเกินรอบ — นับจากวันอนุมัติ";

    /// <summary><b>ช่วงที่ได้เมื่อจ่ายเงินนี้</b> — ตัวเดียวของ preview และตอนอนุมัติ (คำตัดสิน 233-1/233-1b/233-7):
    /// <list type="bullet">
    /// <item>ส่วนต่างอัปเกรด (<paramref name="isProration"/>) ⇒ [วันชำระ, วันหมดอายุเดิม] — วันหมดอายุคงเดิม (คำตัดสินเจ้าของข้อ 178 · 233-2)</item>
    /// <item>เปลี่ยนรอบบิล (<paramref name="isCycleChange"/>) ⇒ [วันชำระ, วันชำระ + เดือนของรอบใหม่] (231-9)</item>
    /// <item>อื่น ๆ ⇒ เริ่ม = <see cref="PlatformPlanPricing.PeriodStart"/> (ต่ออายุแพ็กเดิม = max(วันหมดอายุเดิม, วันชำระ) · อัปเกรด/ทดลองใช้/ฟรีถาวร/วันหมดอายุค่าแทน = วันชำระ ·
    /// ดาวน์เกรดระหว่างรอบ = วันหมดอายุเดิม) · สิ้นสุด = เริ่ม + <paramref name="months"/> (AddMonths: 31 ม.ค. + 1 เดือน = 28/29 ก.พ.)</item>
    /// <item><b>ฝ่ายค้าน B2</b> (<paramref name="approvedAtUtc"/> = เวลาอนุมัติ): วันรับเงินย้อนหลังจนวันหมดอายุที่คิดได้ ≤ เวลาอนุมัติ ⇒ ลูกค้าต้องได้รอบที่ใช้ได้จริง —
    /// นับจากเวลาอนุมัติแทน (<see cref="PaidPeriodResult.StartedAtApproval"/> + <see cref="BackdatedNote"/>) · ทิศตรงข้าม: โอนก่อนหมดอายุแล้วอนุมัติหลังหมด ⇒ ยังต่อจากวันหมดอายุเดิม
    /// (ผลยังเลยเวลาอนุมัติ — คำตัดสิน 233-1b) · ส่วนต่างอัปเกรดไม่ใช้กติกานี้ (รอบที่คิดจบแล้ว = <c>PlatformProration.StaleReason</c> พักให้แอดมิน)</item>
    /// </list></summary>
    public static PaidPeriodResult PaidPeriod(PlatformPlanPricing.ChangeKind kind, bool samePlan, bool isPermanentFree, bool isTrial,
        bool isProration, bool isCycleChange, DateTime currentEndUtc, DateTime paidAtUtc, int months, DateTime? approvedAtUtc = null)
    {
        if (isProration) return new PaidPeriodResult(paidAtUtc, currentEndUtc, false);
        var m = months > 0 ? months : 0;
        var start = isCycleChange ? paidAtUtc : PlatformPlanPricing.PeriodStart(kind, samePlan, isPermanentFree, currentEndUtc, paidAtUtc, isTrial);
        var end = start.AddMonths(m);
        if (m > 0 && approvedAtUtc is DateTime appr && end <= appr)
            return new PaidPeriodResult(appr, appr.AddMonths(m), true);
        return new PaidPeriodResult(start, end, false);
    }

    /// <summary>วันที่เหลือของรอบปัจจุบัน (ปฏิทินไทย · 0 เมื่อหมดแล้ว/ไม่หมดอายุ)</summary>
    public static int DaysLeft(DateTime endUtc, DateTime nowUtc, bool openEnded)
        => openEnded ? 0 : Math.Max(0, (endUtc.AddHours(7).Date - nowUtc.AddHours(7).Date).Days);

    // ═════════ ผล preview ═════════

    /// <summary>ซื้อ/ต่ออายุ/อัปเกรด/เปลี่ยนรอบที่ต้องจ่ายตอนนี้ — <paramref name="m"/> = ตัวเลขจากตัวคิดราคาเดิม</summary>
    public static Result ForPurchase(Current cur, Target tgt, Money m, DateTime nowUtc, bool vatIncluded)
    {
        var samePlan = PlatformPlanPricing.IsRenewalPurchase(cur.Plan, tgt.Plan);
        var changeKind = PlatformPlanPricing.Classify(cur.MonthlyPrice, tgt.MonthlyPrice);
        var curOpen = SubscriptionExpiry.IsOpenEnded(cur.IsPermanentFree, cur.EndUtc, nowUtc);
        var curExpired = !curOpen && cur.EndUtc <= nowUtc;
        var kind = m.Prorated ? PreviewKind.Upgrade
            : m.CycleChange ? PreviewKind.CycleChange
            : !cur.ActivePaid ? (samePlan && !cur.IsTrial && !cur.IsPermanentFree ? PreviewKind.Renewal : PreviewKind.NewPurchase)
            : samePlan && tgt.Cycle == cur.Cycle ? PreviewKind.Renewal
            : PreviewKind.UpgradeRestart;
        var months = PlatformPlanPricing.MonthsOf(tgt.Cycle);
        var (start, end) = PaidPeriod(changeKind, samePlan, cur.IsPermanentFree, cur.IsTrial, m.Prorated, m.CycleChange, cur.EndUtc, nowUtc, months);
        var daysLeft = DaysLeft(cur.EndUtc, nowUtc, curOpen);
        var notes = new List<string>();   // เหตุผลของวันที่ + ผลข้างเคียง (หน้าเว็บแสดงเป็นหัวข้อย่อย — ไม่กรองข้อความเอง)
        var money = new List<string>();   // บรรทัดเงิน

        var headline = kind switch
        {
            PreviewKind.Renewal => $"ต่ออายุ {tgt.PlanName} {PublicCycle(tgt.Cycle)}",
            PreviewKind.Upgrade => $"อัปเกรด {cur.PlanName} → {tgt.PlanName} (จ่ายส่วนต่างเฉพาะวันที่เหลือ)",
            PreviewKind.UpgradeRestart => samePlan ? $"เปลี่ยนเป็น {tgt.PlanName} {PublicCycle(tgt.Cycle)}" : $"อัปเกรด {cur.PlanName} → {tgt.PlanName} {PublicCycle(tgt.Cycle)}",
            PreviewKind.CycleChange => $"เปลี่ยนเป็น {tgt.PlanName} {PublicCycle(tgt.Cycle)} (หักมูลค่าคงเหลือรอบเดิม)",
            _ => $"ซื้อ {tgt.PlanName} {PublicCycle(tgt.Cycle)}",
        };
        var endUnchanged = kind == PreviewKind.Upgrade;
        var dateLine = endUnchanged
            ? $"ใช้ได้ถึง {BeDate(cur.EndUtc)} (วันหมดอายุคงเดิม) — ได้ {tgt.PlanName} ทันทีเมื่อชำระ"
            : curOpen || cur.IsTrial || cur.IsPermanentFree || curExpired
                ? $"ใช้ได้ตั้งแต่วันที่ชำระ {BeDate(start)} ถึง {BeDate(end)}"
                : $"ใช้ได้ถึง {BeDate(cur.EndUtc)} → {BeDate(end)}";

        // เหตุผลของวันที่ — ทุกเคสต้องบอกว่าวันที่เหลือไปไหน (เจ้าของ: "จะเปลี่ยนวันหมดอายุเป็นวันที่เท่าไหร่")
        if (kind == PreviewKind.Renewal && !curExpired && !curOpen && start > nowUtc)
            notes.Add($"ต่อจากวันหมดอายุเดิม {BeDate(cur.EndUtc)} อีก {months} เดือน — วันที่เหลือ {daysLeft} วันไม่หาย");
        else if (cur.IsTrial && !curExpired)
            notes.Add($"การทดลองใช้สิ้นสุดเมื่อชำระ — วันทดลองที่เหลือ {daysLeft} วันไม่ถูกนำไปต่อท้าย · รอบที่จ่ายเริ่มวันที่ชำระ ได้สิทธิ์เต็มทันที");
        else if (curExpired && !cur.IsPermanentFree)
            notes.Add($"แพ็กเกจเดิมหมดอายุแล้ว ({BeDate(cur.EndUtc)}) — รอบใหม่นับจากวันที่ชำระ");
        else if (curOpen && !cur.IsTrial)
            notes.Add("แพ็กเกจเดิมไม่มีวันหมดอายุ — รอบที่จ่ายนับจากวันที่ชำระ");
        else if (kind == PreviewKind.UpgradeRestart && daysLeft > 0)
            notes.Add($"เริ่มรอบใหม่จากวันที่ชำระ — วันที่เหลือของ {cur.PlanName} {daysLeft} วันไม่ถูกนำมาหัก (ส่วนต่างคิดตามวันที่เหลือใช้ไม่ได้กับรายการนี้)");
        else if (kind == PreviewKind.CycleChange)
            notes.Add("รอบใหม่เริ่มวันที่ชำระ — วันหมดอายุเดิมไม่ใช้แล้ว (มูลค่าคงเหลือถูกหักจากยอดแล้ว)");

        // เงิน
        decimal credit = 0m; string? creditFormula = null; decimal listPrice = m.ListPrice;
        if (kind == PreviewKind.Upgrade && m.OldPriceBasis is decimal oldBasis && m.DaysRemaining is int d && m.CycleDays is int c && c > 0)
        {
            credit = R2(Math.Max(0m, oldBasis) * d / c);
            listPrice = R2(m.ListPrice + credit);   // ค่าแพ็กใหม่ส่วนที่เหลือ — ผลรวมกับเครดิต = ส่วนต่างที่คิดจริงพอดี
            creditFormula = $"{cur.PlanName} ที่จ่าย ฿{oldBasis:N2}/รอบ × เหลือ {d}/{c} วัน";
            money.Add($"ค่า {tgt.PlanName} ส่วนที่เหลือ {d}/{c} วัน ฿{listPrice:N2}");
            money.Add($"หักเครดิตจาก {creditFormula} −฿{credit:N2}");
        }
        else if (kind == PreviewKind.CycleChange && m.CycleChangeCredit is decimal cc)
        {
            // ฝ่ายค้าน N2: ราคาจริงของรอบใหม่มาจากตัวคิดราคา (NewCyclePrice) — เครดิตมากกว่าค่ารอบใหม่ ⇒ ใช้ได้เท่าค่ารอบ (ไม่แสดง "ค่ารอบใหม่ = ฿เครดิต")
            listPrice = m.NewCyclePrice is decimal np ? R2(np) : R2(m.ListPrice + cc);
            credit = R2(Math.Min(cc, listPrice));
            if (cc > listPrice) notes.Add($"มูลค่าคงเหลือรอบเดิม ฿{R2(cc):N2} มากกว่าค่ารอบใหม่ — ส่วนเกินไม่คืน");
            creditFormula = m.DaysRemaining is int d2 && m.CycleDays is int c2 ? $"มูลค่าคงเหลือรอบเดิม {d2}/{c2} วัน" : "มูลค่าคงเหลือรอบเดิม";
            money.Add($"ค่า {tgt.PlanName} {PublicCycle(tgt.Cycle)} ฿{listPrice:N2}");
            money.Add($"หักเครดิต{creditFormula} −฿{credit:N2}");
        }
        else
            money.Add($"ค่า {tgt.PlanName} {PublicCycle(tgt.Cycle)} ฿{listPrice:N2}");
        if (m.SpecialPriceNote != null) notes.Add(m.SpecialPriceNote);
        if (m.CouponDiscount > 0m) money.Add($"{m.CouponLabel ?? "ส่วนลดคูปอง"} −฿{m.CouponDiscount:N2}");
        if (m.CouponProblem != null) notes.Add($"คูปองใช้ไม่ได้: {m.CouponProblem}");
        money.Add(vatIncluded ? $"ยอดชำระ ฿{m.AmountDue:N2} (รวม VAT 7% แล้ว · ใบกำกับภาษีออกเมื่อชำระ)" : $"ยอดชำระ ฿{m.AmountDue:N2} (ใบเสร็จรับเงินออกเมื่อชำระ)");

        // ผลข้างเคียงที่ลูกค้าต้องรู้ (อนุมัติชำระล้างให้จริง — ReviewPaymentLockedAsync)
        if (cur.CancelAtPeriodEnd) notes.Add("การยกเลิกเมื่อสิ้นรอบที่ตั้งไว้จะถูกยกเลิก (ชำระ = ใช้ต่อ)");
        if (cur.ScheduledPlan != null)
            notes.Add($"การเปลี่ยนเป็น {cur.ScheduledPlan} ที่รอมีผล{(cur.ScheduledEffectiveAtUtc is DateTime se ? " " + BeDate(se) : "")} จะถูกยกเลิก");

        var covered = kind == PreviewKind.CycleChange && m.AmountDue <= 0m;   // N2: RequestPlanPaymentAsync ปฏิเสธยอด 0 — บอกก่อนกด
        return new Result(kind.ToString(), !covered, covered ? CreditCoversAllRefusal : null, covered ? "ติดต่อผู้ดูแลระบบ NextAcc" : null, null,
            cur.Plan.ToString(), cur.PlanName, cur.Cycle.ToString(), curOpen ? null : cur.EndUtc,
            tgt.Plan.ToString(), tgt.PlanName, tgt.Cycle.ToString(),
            start, end, endUnchanged, null,
            listPrice, credit, creditFormula, m.DaysRemaining, m.CycleDays,
            R2(m.CouponDiscount), R2(m.AmountDue), vatIncluded, headline, dateLine,
            new List<string> { dateLine }.Concat(notes).Concat(money).ToList(), notes);
    }

    /// <summary>ดาวน์เกรด/รอบสั้นลง/ราคาเท่ากันต่างแพ็ก (Active) — ไม่ต้องจ่าย มีผลวันครบรอบ (<paramref name="blocked"/> = ใบต่ออายุออกแล้ว ⇒ ทำไม่ได้ตอนนี้)</summary>
    public static Result ForScheduled(Current cur, Target tgt, DateTime effectiveAtUtc, string? blocked, DateTime nowUtc)
    {
        var curOpen = SubscriptionExpiry.IsOpenEnded(cur.IsPermanentFree, cur.EndUtc, nowUtc);
        var dateLine = $"ใช้ {cur.PlanName} ได้ถึง {BeDate(effectiveAtUtc)} แล้วเปลี่ยนเป็น {tgt.PlanName} {PublicCycle(tgt.Cycle)} (ไม่ต้องชำระตอนนี้ · ไม่คืนเงินส่วนต่าง)";
        var notes = new List<string> { $"รอบถัดไปคิดราคา {tgt.PlanName} — ใบแจ้งหนี้ต่ออายุออกก่อนวันมีผล" };
        if (blocked != null) notes.Insert(0, blocked);
        return new Result(PreviewKind.Scheduled.ToString(), blocked == null, blocked, blocked != null ? "ไปที่ใบแจ้งหนี้ & ใบเสร็จ" : null, blocked != null ? DocsTabUrl : null,
            cur.Plan.ToString(), cur.PlanName, cur.Cycle.ToString(), curOpen ? null : cur.EndUtc,
            tgt.Plan.ToString(), tgt.PlanName, tgt.Cycle.ToString(),
            effectiveAtUtc, null, false, effectiveAtUtc, 0m, 0m, null, null, null, 0m, 0m, false,
            $"เปลี่ยนเป็น {tgt.PlanName} {PublicCycle(tgt.Cycle)} มีผล {BeDate(effectiveAtUtc)}", dateLine,
            new List<string> { dateLine }.Concat(notes).ToList(), notes);
    }

    /// <summary>หน้าเปลี่ยนแพ็กเกจเลือกแพ็กเดิมรอบเดิม — ไม่ใช่การเปลี่ยน (ต่ออายุผ่านใบแจ้งหนี้ต่ออายุ/หน้าซื้อแพ็กเกจ)</summary>
    public static Result ForSame(Current cur, Target tgt, DateTime nowUtc)
    {
        var curOpen = SubscriptionExpiry.IsOpenEnded(cur.IsPermanentFree, cur.EndUtc, nowUtc);
        var dateLine = curOpen ? $"{cur.PlanName} ไม่มีวันหมดอายุ" : $"ใช้ {cur.PlanName} ได้ถึง {BeDate(cur.EndUtc)}";
        return new Result(PreviewKind.Same.ToString(), false, "นี่คือแพ็กเกจและรอบบิลปัจจุบันอยู่แล้ว",
            "ต่ออายุได้จากใบแจ้งหนี้ต่ออายุ (ออกก่อนหมดอายุ) ที่ใบแจ้งหนี้ & ใบเสร็จ", DocsTabUrl,
            cur.Plan.ToString(), cur.PlanName, cur.Cycle.ToString(), curOpen ? null : cur.EndUtc,
            tgt.Plan.ToString(), tgt.PlanName, tgt.Cycle.ToString(),
            null, curOpen ? null : cur.EndUtc, true, null, 0m, 0m, null, null, null, 0m, 0m, false,
            $"{tgt.PlanName} {PublicCycle(tgt.Cycle)} คือแพ็กเกจปัจจุบัน", dateLine, new List<string> { dateLine }, new List<string>());
    }

    /// <summary>ยังไม่ล็อกอิน/ยังไม่มีบริษัท — ซื้อใหม่ นับจากวันที่ชำระ (บริษัทที่มีแพ็กเกจอยู่แล้วคำนวณใหม่หลังเลือกบริษัท)</summary>
    public static Result ForAnonymous(Target tgt, decimal listPrice, DateTime nowUtc)
    {
        var end = nowUtc.AddMonths(Math.Max(0, PlatformPlanPricing.MonthsOf(tgt.Cycle)));
        var dateLine = $"ถ้าชำระวันนี้: ใช้ได้ {BeDate(nowUtc)} ถึง {BeDate(end)}";
        var notes = new List<string> { "บริษัทที่มีแพ็กเกจอยู่แล้ว — ระบบคำนวณวันหมดอายุใหม่/เครดิตแพ็กเดิมให้หลังเข้าสู่ระบบและเลือกบริษัท" };
        var lines = new List<string> { dateLine, $"ค่า {tgt.PlanName} {PublicCycle(tgt.Cycle)} ฿{R2(listPrice):N2}" }.Concat(notes).ToList();
        return new Result(PreviewKind.NewPurchase.ToString(), true, null, null, null,
            "", "", "", null, tgt.Plan.ToString(), tgt.PlanName, tgt.Cycle.ToString(),
            nowUtc, end, false, null, R2(listPrice), 0m, null, null, null, 0m, R2(listPrice), false,
            $"ซื้อ {tgt.PlanName} {PublicCycle(tgt.Cycle)}", dateLine, lines, notes);
    }

    /// <summary>เหตุที่หน้าซื้อแพ็กเกจสาธารณะชำระไม่ได้ (ตัวจำแนกจาก <see cref="PublicCheckoutPolicy.DecideOrder"/>)</summary>
    private enum RefusalCause { Downgrade, ShorterCycle, RenewalInvoiceIssued, RenewalCoupon, NoPaymentChange, Other }

    private static RefusalCause CauseOf(string? refusal) => refusal switch
    {
        PublicCheckoutPolicy.DowngradeRefusal => RefusalCause.Downgrade,
        PublicCheckoutPolicy.ShorterCycleRefusal => RefusalCause.ShorterCycle,
        PublicCheckoutPolicy.RenewalInvoiceIssuedRefusal => RefusalCause.RenewalInvoiceIssued,
        PublicCheckoutPolicy.RenewalCouponRefusal => RefusalCause.RenewalCoupon,
        PublicCheckoutPolicy.NoPaymentChangeRefusal => RefusalCause.NoPaymentChange,
        _ => RefusalCause.Other,
    };

    /// <summary><b>ชำระไม่ได้ — บอกเพราะอะไร + ทางไปต่อ + วันที่</b> (เจ้าของ: "ถ้าเลือกแพ็คต่ำกว่าต้องขึ้นแจ้งว่าชำระไม่ได้เพราะอะไร") ·
    /// ข้อความเดียวกันทั้งกล่องสรุป ใต้ปุ่ม และ error ตอนสร้างรายการ</summary>
    public static Result Refused(Current cur, Target tgt, string? refusal, DateTime nowUtc)
    {
        var curOpen = SubscriptionExpiry.IsOpenEnded(cur.IsPermanentFree, cur.EndUtc, nowUtc);
        var effective = PlatformPlanPricing.ScheduledChangeEffectiveAt(cur.IsPermanentFree, cur.EndUtc, nowUtc);
        var endText = curOpen ? "ไม่มีวันหมดอายุ" : $"ถึง {BeDate(cur.EndUtc)}";
        var renewalEnd = PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, cur.IsPermanentFree, cur.IsTrial, false, false, cur.EndUtc, nowUtc,
            PlatformPlanPricing.MonthsOf(cur.Cycle)).EndUtc;
        var cause = CauseOf(refusal);
        var (reason, next, url) = cause switch
        {
            RefusalCause.Downgrade => (
                $"บริษัทนี้ใช้ {cur.PlanName} {endText} — {tgt.PlanName} ต่ำกว่าแพ็กเกจปัจจุบัน จึงชำระที่หน้านี้ไม่ได้ · ลดเป็น {tgt.PlanName} ได้ที่หน้าแพ็กเกจในระบบ มีผล {BeDate(effective)} (ไม่ต้องชำระตอนนี้ · ไม่คืนเงินส่วนต่าง)",
                "ไปหน้าแพ็กเกจในระบบเพื่อตั้งลดแพ็กเกจ", (string?)PlanTabUrl),
            RefusalCause.ShorterCycle => (
                $"บริษัทนี้ใช้ {cur.PlanName} {PublicCycle(cur.Cycle)} {endText} — เปลี่ยนเป็น{PublicCycle(tgt.Cycle)}ได้ที่หน้าแพ็กเกจในระบบ มีผล {BeDate(effective)} (ไม่คืนเงินส่วนต่าง)",
                "ไปหน้าแพ็กเกจในระบบเพื่อตั้งเปลี่ยนรอบบิล", PlanTabUrl),
            RefusalCause.RenewalInvoiceIssued => (
                $"ออกใบแจ้งหนี้ต่ออายุรอบที่หมด {BeDate(cur.EndUtc)} แล้ว — ชำระจากใบแจ้งหนี้นั้น (ไม่สร้างรายการซ้ำ) · ชำระแล้วใช้ได้ถึง {BeDate(renewalEnd)}",
                "ไปที่ใบแจ้งหนี้ & ใบเสร็จเพื่อชำระใบต่ออายุ", DocsTabUrl),
            RefusalCause.RenewalCoupon => (
                $"คูปองใช้กับการต่ออายุไม่ได้ — ลบรหัสคูปองแล้วต่ออายุ {cur.PlanName} ได้ (ใช้ได้{endText} → {BeDate(renewalEnd)}) · ส่วนลดต่ออายุตามข้อตกลงให้ติดต่อผู้ดูแลระบบ (ราคาพิเศษ)",
                "ลบรหัสคูปองในช่องคูปอง", (string?)null),
            RefusalCause.NoPaymentChange => (
                $"การเปลี่ยนเป็น {tgt.PlanName} {PublicCycle(tgt.Cycle)} ไม่มียอดต้องชำระตอนนี้ — ตั้งได้ที่หน้าแพ็กเกจในระบบ มีผล {BeDate(effective)}",
                "ไปหน้าแพ็กเกจในระบบ", PlanTabUrl),
            _ => (refusal ?? "ชำระรายการนี้ไม่ได้", "ติดต่อผู้ดูแลระบบ NextAcc", (string?)null),
        };
        var dateLine = curOpen ? $"ปัจจุบัน: {cur.PlanName} ไม่มีวันหมดอายุ" : $"ปัจจุบัน: {cur.PlanName} {PublicCycle(cur.Cycle)} ใช้ได้ถึง {BeDate(cur.EndUtc)}";
        return new Result(PreviewKind.Refused.ToString(), false, reason, next, url,
            cur.Plan.ToString(), cur.PlanName, cur.Cycle.ToString(), curOpen ? null : cur.EndUtc,
            tgt.Plan.ToString(), tgt.PlanName, tgt.Cycle.ToString(),
            null, null, false, cause is RefusalCause.Downgrade or RefusalCause.ShorterCycle or RefusalCause.NoPaymentChange ? effective : null,
            0m, 0m, null, null, null, 0m, 0m, false,
            "ชำระรายการนี้ไม่ได้", dateLine, new List<string> { dateLine, reason }, new List<string> { reason });
    }

    /// <summary>ฝ่ายค้าน N5: ข้อความป้าย "ต่ำกว่าแพ็กปัจจุบัน" บน <c>/pricing.html</c> (ต่อบริษัทที่ผู้ใช้เป็นเจ้าของ) — เซิร์ฟเวอร์ประกอบ หน้าเว็บแสดงอย่างเดียว ·
    /// วันมีผลตัวเดียวกับการตั้งเวลาดาวน์เกรด (<see cref="PlatformPlanPricing.ScheduledChangeEffectiveAt"/>)</summary>
    public static string LowerPlanNote(string companyName, string currentPlanName, bool isPermanentFree, DateTime endUtc, DateTime nowUtc)
        => $"{companyName} ใช้ {currentPlanName} อยู่ — แพ็กนี้ต่ำกว่าแพ็กปัจจุบัน ซื้อที่หน้านี้ไม่ได้ · ลดแพ็กได้ที่หน้าแพ็กเกจในระบบ มีผล "
           + BeDate(PlatformPlanPricing.ScheduledChangeEffectiveAt(isPermanentFree, endUtc, nowUtc));

    /// <summary>ส่วนลดของบรรทัดบนใบกำกับ/ใบเสร็จ (รวม VAT) — คูปอง + เครดิตรอบเดิมของรายการเปลี่ยนรอบบิล (หักก่อน VAT §86/4 ผ่าน <c>PlatformCouponRule.SplitDiscounted</c>) ·
    /// ส่วนต่างอัปเกรด (แบบ ก) ยอดรายการ = ส่วนต่างอยู่แล้ว — ช่วงวันที่อยู่ในคำอธิบาย (<c>PlatformProration.ReceiptDescription</c>)</summary>
    public static decimal InvoiceDiscountGross(decimal couponDiscount, bool isCycleChange, decimal? cycleChangeCredit)
        => R2(Math.Max(0m, couponDiscount) + (isCycleChange && cycleChangeCredit is decimal c && c > 0m ? c : 0m));

    private static string PublicCycle(BillingCycle c) => $"ราย{PlatformPlanPricing.CycleLabel(c)}";
}
