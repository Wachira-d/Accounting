using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **settlement เฟส 2 — รายการ payment gateway (PaymentIntent) เข้ารอบโอน <c>SettlementBatch</c> ของช่องทางชนิด Gateway**
/// (รอบ 200 ทีม P2 · <c>erp-review/2026-09-29/DECISIONS.md</c> ข้อ 12 · report-S2 §3 "<c>GatewaySettlementService</c> → ตัวสร้าง batch จาก PaymentIntent")
///
/// <para>═══ คำตัดสิน: สองเส้นอยู่ร่วมกันแบบ "หนึ่งรายการ หนึ่งเจ้าของ · สูตรเดียว" ═══
/// <list type="bullet">
/// <item><b>เจ้าของ</b> ของ intent ถูกกำหนดครั้งเดียวตอนเข้ารอบโอนครั้งแรก: เส้นเดิม (<c>GatewaySettlementService.RecordAsync</c>) ประทับ
/// <c>SettlementJournalEntryId</c> · เส้นใหม่ (รอบโอน settlement) ประทับ <c>SettlementBatchId</c> — ทั้งสองเส้นเลือกเฉพาะ intent ที่<b>ทั้งสองช่องว่าง</b>
/// (<see cref="UnclaimedForBatch"/> ฝั่งนี้ · <c>i.SettlementBatchId == null</c> ใน <c>SelectCandidatesAsync</c> ฝั่งเดิม — ล็อกด้วย
/// <c>tools/required_call_site_check.py</c> ทั้งสองทิศ) และถือล็อก <c>AdvisoryLockKey.GatewaySettlement</c> ตัวเดียวกัน</item>
/// <item><b>คืนเงินภายหลัง</b> ตามเจ้าของ: เจ้าของเดิม = เส้นเดิมหักรอบถัดไป (<c>SettlementJournalEntryId != null</c>) · เจ้าของใหม่ = บรรทัดคืนเงินรอบถัดไป
/// (<see cref="LateRefundInBatch"/> — <c>SettlementJournalEntryId == null</c> เสมอ) ⇒ ยอดคืนก้อนเดียวไม่มีวันถูกหักสองเส้น</item>
/// <item><b>สูตรเดียว</b>: ค่าธรรมเนียม/VAT ค่าธรรมเนียม = <c>GatewaySettlementMath.Contribution</c> ตามโหมดของ config · ยอดคืน ณ วันเงินเข้า =
/// <c>GatewaySettlementMath.RefundedAsOf</c> + <c>RefundCutoffUtc</c> · ภาษีของใบค่าธรรมเนียม = <c>SettlementFeeTax.Compute</c> (VAT ที่ระบุต่อรายการ) — จริงเฉพาะคู่โหมดที่ให้ผลภาษีเท่ากัน (ฝ่ายค้าน X-3 · DECISIONS ข้อ 26: คู่ ภ.พ.36 / หักเองแล้วได้คืน ถูกนิยามเป็น "ไม่ตรง" ใน <see cref="ModeMismatch"/>)</item>
/// <item><b>ข้อเท็จจริงเดียว</b>: "ผู้ให้บริการคิด VAT/เราหัก ณ ที่จ่ายไหม" เก็บสองที่ (config ของ gateway · ช่องทาง) ⇒ ต้องตรงกัน
/// (<see cref="ModeMismatch"/>) ไม่งั้นบล็อกพร้อมทางไปต่อ — เดิม config "ไม่แยก VAT" + ช่องทาง "VAT ไทย 7%" ⇒ รอบโอนแต่งภาษีซื้อ 7/107
/// จากค่าธรรมเนียมที่ไม่มี VAT (ภาษีซื้อเกินจริง) · config "บวก VAT เพิ่ม" ⇒ รอบโอนไม่รู้ว่ายอดที่ถูกหักรวม VAT (ยอดไม่ลงตัว)</item>
/// </list></para>
///
/// <para>รายได้ไม่ถูกนับซ้ำ: บรรทัดทุกบรรทัดที่ประกอบจาก intent พก <c>PaymentIntentId</c> ⇒ <c>SettlementBatchMath.Plan</c> นับเป็น
/// "อยู่ในผังพักแล้ว" (ไม่ใช่ขายใหม่ · ไม่มีวันเป็น <c>AutoSummary</c>) — ขาขายลงไว้ตอนรับชำระ · ขาคืนเงินลงไว้ตอนคืน · รอบโอนแค่ล้างผังพัก</para>
/// </summary>
public static class GatewayBatchIntentRules
{
    /// <summary>ช่องทางนี้ประกอบรอบโอนจากรายการรับชำระในระบบได้ไหม — null = ได้ · marketplace/OTA/ช่องทางที่ไม่ผูก config ⇒ ข้อความพร้อมทางไปต่อ
    /// (รายการของ marketplace ไม่ถูกแตะ: เส้นนี้อ่านเฉพาะ PaymentIntent ของ gateway ที่ช่องทางผูก)</summary>
    public static string? ChannelRefusal(SettlementChannelKind kind, Guid? paymentProviderConfigId)
        => kind == SettlementChannelKind.Gateway && paymentProviderConfigId != null
            ? null
            : "ช่องทางนี้ไม่ได้ผูกกับ payment gateway ในระบบ — ประกอบรอบโอนจากรายการรับชำระได้เฉพาะช่องทางชนิด Gateway ที่ผูกการตั้งค่า gateway แล้ว "
              + "(ช่องทางอื่นให้นำเข้าไฟล์ settlement report แทน)";

    /// <summary>
    /// โหมด VAT/หัก ณ ที่จ่ายของค่าธรรมเนียม ระหว่าง config ของ gateway กับช่องทางที่ผูก <b>ขัดกันไหม</b> — null = ตรงกัน
    ///
    /// <para>รอบ 200 ฝ่ายค้าน X-3 → DECISIONS ข้อ 26: <b>"ตรงกัน" = สองเส้น (รอบโอน gateway เดิม · รอบโอน settlement) ให้ผลภาษีเท่ากันทุกตัว</b>
    /// — คู่ใดที่ให้ผลต่าง (แม้เส้นหนึ่ง "ถูกกว่า") = ไม่ตรง เพราะภาษีห้ามขึ้นกับว่าผู้ใช้กดหน้าไหน:
    /// <list type="bullet">
    /// <item>ช่องทาง "ต่างประเทศ ภ.พ.36" ⇒ ไม่ตรง<b>เสมอ</b> — เส้นรอบโอนตั้งหนี้ ภ.พ.36 (§83/6 ทั้งผู้จด/ไม่จด VAT) · config ของ gateway ไม่มีโหมดนี้
    /// (เส้นเดิมลงค่าใช้จ่ายทั้งก้อน)</item>
    /// <item>บริษัท<b>ไม่จด VAT</b>: VAT ที่ผู้ให้บริการเก็บเป็นค่าใช้จ่ายทั้งก้อนทั้งสองเส้น (ภาษีซื้อเคลมไม่ได้) ⇒ โหมด VAT ไทยทุกคู่ให้ผลเท่ากัน (X-10)</item>
    /// <item>บริษัทจด VAT: config "ไม่แยก" ↔ ช่องทาง "ไม่มี VAT" เท่านั้น · config "รวมใน/บวกเพิ่ม" ↔ ช่องทาง "VAT ไทย 7%" เท่านั้น</item>
    /// <item>หัก ณ ที่จ่าย: config "หัก 3%" (ออกภาษีแทน ฐานก่อน VAT) ↔ ช่องทาง "เราออกภาษีแทน" เท่านั้น · config "ไม่หัก" ↔ ช่องทาง "ไม่หัก" หรือ
    /// "แพลตฟอร์มเป็นตัวแทนหัก" (W1 — ไม่มีขา JE/50 ทวิ/ยอดยื่นฝั่งเรา ⇒ ผลเท่าเส้นเดิม) · "หักเองแล้วได้คืน" (W2 ตั้ง 21917 + ลูกหนี้รอคืน + 50 ทวิ)
    /// ⇒ ไม่ตรง</item>
    /// </list></para>
    /// <para>ผู้เรียก (ตัวตัดสินตัวเดียวทุกทางเข้า — ล็อกด้วย <c>tools/required_call_site_check.py</c>): ประกอบรอบโอนจากรายการรับชำระ ·
    /// นำเข้าไฟล์ของช่องทางที่ผูก config · บันทึกช่องทาง · บันทึกค่าตั้ง gateway (<see cref="ConfigChangeRefusal"/>) · <b>ด่านลงบัญชี</b>
    /// (<see cref="PostingIssue"/> — X-1: รอบโอนที่นำเข้าก่อนด่านนี้มี/ก่อนเปลี่ยนโหมด)</para>
    /// </summary>
    public static string? ModeMismatch(GatewayFeeVatMode gatewayVat, GatewayFeeWhtMode gatewayWht,
        SettlementFeeVatMode channelVat, SettlementFeeWhtMode channelWht, bool companyVatRegistered)
    {
        var problems = new List<string>();
        if (VatProblem(gatewayVat, channelVat, companyVatRegistered) is string vat) problems.Add(vat);
        if (WhtProblem(gatewayWht, channelWht) is string wht) problems.Add(wht);
        if (problems.Count == 0) return null;
        return "โหมดภาษีของค่าธรรมเนียมในการตั้งค่า gateway กับช่องทางรับเงินไม่ตรงกัน (ภาษีของรายการชุดเดียวกันจะต่างกันตามหน้าที่กดรอบโอน) — "
               + string.Join(" · ", problems)
               + " · ทางไปต่อ: ตรวจใบกำกับ/สเตทเมนต์ของผู้ให้บริการ แล้วแก้ให้สองที่ตรงกัน (หน้า \"ตั้งค่าการรับชำระเงินออนไลน์\" และหน้าตั้งค่าช่องทาง) "
               + "— ระบบไม่เลือกฝั่งใดฝั่งหนึ่งให้ เพราะทั้งสองที่เป็นคำตอบของคำถามเดียวกัน";
    }

    private static string? VatProblem(GatewayFeeVatMode gatewayVat, SettlementFeeVatMode channelVat, bool companyVatRegistered)
    {
        if (channelVat == SettlementFeeVatMode.ForeignPp36)
            return "ช่องทางตั้งเป็น \"ผู้ให้บริการต่างประเทศ (ภ.พ.36)\" แต่การตั้งค่า gateway ไม่มีโหมดนี้ (รอบโอน gateway เดิมไม่ตั้งหนี้ ภ.พ.36) — "
                   + "ถ้าผู้ให้บริการเป็นต่างประเทศจริง ให้ใช้ช่องทางที่ไม่ผูกการตั้งค่า gateway แล้วนำเข้าไฟล์ settlement report แทน";
        if (!companyVatRegistered) return null;
        var agrees = gatewayVat == GatewayFeeVatMode.None
            ? channelVat == SettlementFeeVatMode.None
            : channelVat == SettlementFeeVatMode.ThaiVat7;
        if (agrees) return null;
        return gatewayVat == GatewayFeeVatMode.None
            ? "การตั้งค่า gateway บอกว่าค่าธรรมเนียม \"ไม่แยก VAT\" แต่ช่องทางตั้งเป็น \"VAT ไทย 7%\" (รอบโอนจะแยกภาษีซื้อจากค่าธรรมเนียมที่ไม่มี VAT)"
            : "การตั้งค่า gateway บอกว่าค่าธรรมเนียมมี VAT 7% แต่ช่องทางไม่ได้ตั้งเป็น \"VAT ไทย 7%\" (VAT ที่ถูกหักจะไม่ถูกแยกเป็นภาษีซื้อ)";
    }

    private static string? WhtProblem(GatewayFeeWhtMode gatewayWht, SettlementFeeWhtMode channelWht)
    {
        if (gatewayWht == GatewayFeeWhtMode.Withhold3Percent)
            return channelWht == SettlementFeeWhtMode.SelfWithholdPayerBorne ? null
                : "การตั้งค่า gateway เปิด \"หัก ณ ที่จ่ายค่าธรรมเนียม 3%\" (ออกภาษีแทน) แต่ช่องทางไม่ได้ตั้งเป็น \"เราออกภาษีแทน\"";
        return channelWht switch
        {
            SettlementFeeWhtMode.SelfWithholdPayerBorne => "ช่องทางตั้ง \"เราออกภาษีแทน\" แต่การตั้งค่า gateway ปิดการหัก ณ ที่จ่ายค่าธรรมเนียม",
            SettlementFeeWhtMode.SelfWithholdReimbursed =>
                "ช่องทางตั้ง \"เราหักเองแล้วแพลตฟอร์มคืนให้\" (รอบโอนตั้งภาษีหัก ณ ที่จ่ายค้างนำส่ง + ลูกหนี้รอคืน + 50 ทวิ) แต่การตั้งค่า gateway ปิดการหัก ณ ที่จ่ายค่าธรรมเนียม",
            _ => null,
        };
    }

    /// <summary>บันทึกช่องทางครั้งนี้แตะ "ข้อเท็จจริงเรื่องโหมดภาษี" ไหม (X-10) — ช่องทางใหม่ (<paramref name="priorVat"/>/<paramref name="priorWht"/> = null) ·
    /// เปลี่ยน config ที่ผูก · เปลี่ยนโหมด VAT/หัก ณ ที่จ่าย ⇒ true (ต้องตรวจ <see cref="ModeMismatch"/>) · แก้ช่องอื่นล้วน ⇒ false
    /// (ช่องทางเดิมที่โหมดขัดยังแก้ชื่อ/ปิดใช้งานได้ — ด่านนำเข้า/ลงบัญชีตรวจซ้ำทุกครั้ง)</summary>
    public static bool ChannelModeTouched(Guid? priorConfigId, SettlementFeeVatMode? priorVat, SettlementFeeWhtMode? priorWht,
        Guid? newConfigId, SettlementFeeVatMode newVat, SettlementFeeWhtMode newWht)
        => priorVat is null || priorWht is null || priorConfigId != newConfigId || priorVat != newVat || priorWht != newWht;

    /// <summary>ด่านลงบัญชีรอบโอนของช่องทางที่ผูก config ของ gateway (X-1 · DECISIONS ข้อ 26) — ผลของ <see cref="ModeMismatch"/> เป็นปัญหาที่บล็อก ·
    /// null = ผ่าน · ไม่ผูก config ⇒ ไม่เกี่ยว (ช่องทางเป็นคำตอบเดียว)</summary>
    public static SettlementPlanIssue? PostingIssue(string? modeMismatch)
        => modeMismatch is null ? null
            : new SettlementPlanIssue(SettlementPlanIssueCode.GatewayModeMismatch, true, modeMismatch,
                "แก้โหมดให้ตรงกันแล้วดูตัวอย่างใหม่ — ถ้ารอบนี้ประกอบจากรายการรับชำระ ให้ยกเลิกรอบโอนแล้วประกอบใหม่หลังแก้ "
                + "(บรรทัดค่าธรรมเนียมถูกคิดด้วยโหมดตอนประกอบ) · ระบบไม่ลงบัญชีด้วยโหมดที่สองที่ตอบต่างกัน",
                Array.Empty<Guid>(), null);

    /// <summary>บันทึกค่าตั้ง gateway ที่เปลี่ยนโหมด VAT/หัก ณ ที่จ่ายค่าธรรมเนียม (X-1/B-3) — ช่องทางที่ผูก config นี้ต้องยังตรงกันทุกช่อง ·
    /// null = บันทึกได้ · ข้อความรวมชื่อช่องทางที่ขัด + ทางไปต่อ</summary>
    public static string? ConfigChangeRefusal(GatewayFeeVatMode gatewayVat, GatewayFeeWhtMode gatewayWht, bool companyVatRegistered,
        IEnumerable<(string ChannelName, SettlementFeeVatMode Vat, SettlementFeeWhtMode Wht)> boundChannels)
    {
        var bad = boundChannels
            .Select(c => (c.ChannelName, Why: ModeMismatch(gatewayVat, gatewayWht, c.Vat, c.Wht, companyVatRegistered)))
            .Where(x => x.Why != null)
            .ToList();
        if (bad.Count == 0) return null;
        return $"เปลี่ยนโหมดภาษีค่าธรรมเนียมแล้วจะไม่ตรงกับช่องทางรับเงินที่ผูกการตั้งค่านี้ ({string.Join(", ", bad.Select(b => b.ChannelName))}) — "
               + bad[0].Why;
    }

    /// <summary>
    /// intent ที่<b>ยังไม่มีเจ้าของรอบโอน</b>และเข้ารอบโอนใหม่ของ gateway นี้ได้ — เงื่อนไขชุดเดียวกับเส้นเดิม (<c>GatewaySettlementService.SelectCandidatesAsync</c>
    /// ส่วน "ยังไม่เคยบันทึกรอบโอน"): สำเร็จ · หรือคืนแล้ว (บางส่วน/เต็ม) ที่ระบบรู้ยอดคืน · <c>ConfirmedAt</c> อยู่ในช่วง [from, to) (null = ไม่จำกัดฝั่งนั้น)
    /// <para>เป็น expression ให้ EF แปลเป็น SQL ได้ และเทสต์ compile ไปรันกับวัตถุในหน่วยความจำได้ (ตัวตัดสินตัวเดียวของทั้งสองที่)</para>
    /// </summary>
    public static Expression<Func<PaymentIntent, bool>> UnclaimedForBatch(Guid companyId, string providerCode, DateTime? fromUtc,
        DateTime? toUtc)
        => i => i.CompanyId == companyId && i.ProviderCode == providerCode
                && i.SettlementJournalEntryId == null && i.SettlementBatchId == null && i.ConfirmedAt != null
                && (i.Status == PaymentIntentStatus.Succeeded
                    || ((i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded)
                        && i.RefundedAmount > 0m))
                && (fromUtc == null || i.ConfirmedAt >= fromUtc)
                && (toUtc == null || i.ConfirmedAt < toUtc);

    /// <summary>intent ที่รอบโอน settlement เป็นเจ้าของแล้ว (<c>SettlementBatchId</c>) แต่มีการคืนเงิน — คืนภายหลังถูกหักจากรอบโอนถัดไปของเส้นนี้ ·
    /// intent ที่เส้นเดิมเป็นเจ้าของ (<c>SettlementJournalEntryId</c>) <b>ไม่เข้า</b> — เส้นเดิมหักเองในรอบถัดไปของมัน (ยอดคืนก้อนเดียวไม่ถูกหักสองเส้น)</summary>
    public static Expression<Func<PaymentIntent, bool>> LateRefundInBatch(Guid companyId, string providerCode)
        => i => i.CompanyId == companyId && i.ProviderCode == providerCode
                && i.SettlementJournalEntryId == null && i.SettlementBatchId != null && i.RefundedAmount > 0m;

    /// <summary>
    /// ตาข่ายใต้ล็อก: บรรทัดคืนเงินที่อ้าง intent (ทุกรอบโอน · ทุกช่องทาง) + บรรทัดคืนเงินที่กำลังจะเพิ่ม ต้อง<b>ไม่เกินยอดคืนผ่านระบบ</b>ของ intent นั้น —
    /// คืนรายการของ intent ที่เกิน (±0.01) · ว่าง = ผ่าน
    /// <para>ยอดคืนก้อนเดียวถูกนับในรอบโอนเดียว: ตัวประกอบบรรทัดอ่าน "ยอดที่อยู่ในบรรทัดแล้ว" นอกล็อก ⇒ สองคำขอพร้อมกัน (สองช่องทางที่ผูก config เดียวกัน)
    /// ได้บรรทัดคืนเงินซ้ำ ⇒ ผังพักถูกหักสองครั้ง · ตรวจซ้ำใต้ล็อก <c>GatewaySettlement</c> ด้วยตัวนี้</para>
    /// </summary>
    /// <param name="refunded">ยอดคืนผ่านระบบของ intent (<c>PaymentIntent.RefundedAmount</c> · บวก)</param>
    /// <param name="alreadyInLines">Σ |ยอด| ของบรรทัดคืนเงินที่อ้าง intent อยู่แล้วในฐาน</param>
    /// <param name="newRefunds">บรรทัดคืนเงินที่กำลังจะเพิ่ม (intent, |ยอด|)</param>
    public static IReadOnlyList<Guid> RefundLinesOverRefunded(IReadOnlyDictionary<Guid, decimal> refunded,
        IReadOnlyDictionary<Guid, decimal> alreadyInLines, IEnumerable<(Guid IntentId, decimal Amount)> newRefunds)
        => newRefunds.GroupBy(r => r.IntentId)
            .Where(g => (alreadyInLines.TryGetValue(g.Key, out var had) ? had : 0m) + g.Sum(r => Math.Abs(r.Amount))
                        > (refunded.TryGetValue(g.Key, out var total) ? total : 0m) + RefundTolerance)
            .Select(g => g.Key)
            .ToList();

    /// <summary>เศษที่ยอมรับได้ของการเทียบยอดคืน (ตัวเดียวกับ <c>GatewaySettlementMath.ToleranceBaht</c>)</summary>
    private const decimal RefundTolerance = GatewaySettlementMath.ToleranceBaht;

    /// <summary>ข้อความเมื่อแยกไม่ได้ว่ายอดคืนส่วนไหนเกิดก่อนวันเงินเข้า (<c>GatewaySettlementMath.RefundedAsOf</c> คืน <c>Known = false</c>) —
    /// ห้ามเดา (เดาผิด = รอบโอนไม่ลงตัว หรือยอดคืนถูกหักผิดรอบ) · ทางไปต่อเดียวกับเส้นเดิม</summary>
    public static string RefundTimingRefusal(int count)
        => $"มี {count} รายการรับชำระที่คืนเงินครั้งล่าสุดตั้งแต่วันเงินเข้าของรอบนี้ แต่คืนบางครั้งก่อนระบบเริ่มเก็บยอดคืนรายครั้ง — "
           + "ระบบแยกไม่ได้ว่าผู้ให้บริการหักยอดคืนส่วนไหนในรอบโอนนี้ · ทางไปต่อ: ถ้าวันที่เงินเข้าที่กรอกไม่ตรงสเตทเมนต์ให้แก้วันที่ · "
           + "ถ้าตรงแล้ว ต้องให้ฝ่ายสนับสนุนเติมยอดคืนรายครั้งตามแดชบอร์ดผู้ให้บริการก่อน (อย่าลงใบสำคัญรอบโอนด้วยมือ — รายการจะค้างและถูกนับซ้ำ)";
}
