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
/// <c>GatewaySettlementMath.RefundedAsOf</c> + <c>RefundCutoffUtc</c> · ภาษีของใบค่าธรรมเนียม = <c>SettlementFeeTax.Compute</c> (VAT ที่ระบุต่อรายการ)</item>
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
    /// <para>คู่ที่ตรงกัน: VAT — config "ไม่แยก" ↔ ช่องทาง "ไม่มี VAT" หรือ "ต่างประเทศ ภ.พ.36" (ผู้ให้บริการไม่เก็บ VAT ไทยในยอดที่หัก) ·
    /// config "รวมใน/บวกเพิ่ม" ↔ ช่องทาง "VAT ไทย 7%" เท่านั้น · หัก ณ ที่จ่าย — config "หัก 3%" (ออกภาษีแทน ฐานก่อน VAT) ↔ ช่องทาง
    /// "เราออกภาษีแทน" เท่านั้น · config "ไม่หัก" ↔ ช่องทางใดก็ได้ที่<b>ไม่ใช่</b> "เราออกภาษีแทน" (ตัวแทนหัก/หักแล้วได้คืน เป็นข้อตกลงที่ config
    /// ของ gateway ไม่มีคำให้เลือก — เส้นเดิมไม่ลง WHT อยู่แล้ว)</para>
    /// </summary>
    public static string? ModeMismatch(GatewayFeeVatMode gatewayVat, GatewayFeeWhtMode gatewayWht,
        SettlementFeeVatMode channelVat, SettlementFeeWhtMode channelWht)
    {
        var problems = new List<string>();
        var vatAgrees = gatewayVat == GatewayFeeVatMode.None
            ? channelVat is SettlementFeeVatMode.None or SettlementFeeVatMode.ForeignPp36
            : channelVat == SettlementFeeVatMode.ThaiVat7;
        if (!vatAgrees)
            problems.Add(gatewayVat == GatewayFeeVatMode.None
                ? "การตั้งค่า gateway บอกว่าค่าธรรมเนียม \"ไม่แยก VAT\" แต่ช่องทางตั้งเป็น \"VAT ไทย 7%\" (รอบโอนจะแยกภาษีซื้อจากค่าธรรมเนียมที่ไม่มี VAT)"
                : "การตั้งค่า gateway บอกว่าค่าธรรมเนียมมี VAT 7% แต่ช่องทางไม่ได้ตั้งเป็น \"VAT ไทย 7%\" (VAT ที่ถูกหักจะไม่ถูกแยกเป็นภาษีซื้อ หรือถูกประเมิน ภ.พ.36 ซ้ำ)");
        var whtAgrees = gatewayWht == GatewayFeeWhtMode.Withhold3Percent
            ? channelWht == SettlementFeeWhtMode.SelfWithholdPayerBorne
            : channelWht != SettlementFeeWhtMode.SelfWithholdPayerBorne;
        if (!whtAgrees)
            problems.Add(gatewayWht == GatewayFeeWhtMode.Withhold3Percent
                ? "การตั้งค่า gateway เปิด \"หัก ณ ที่จ่ายค่าธรรมเนียม 3%\" (ออกภาษีแทน) แต่ช่องทางไม่ได้ตั้งเป็น \"เราออกภาษีแทน\""
                : "ช่องทางตั้ง \"เราออกภาษีแทน\" แต่การตั้งค่า gateway ปิดการหัก ณ ที่จ่ายค่าธรรมเนียม");
        if (problems.Count == 0) return null;
        return "โหมดภาษีของค่าธรรมเนียมในการตั้งค่า gateway กับช่องทางรับเงินไม่ตรงกัน — " + string.Join(" · ", problems)
               + " · ทางไปต่อ: ตรวจใบกำกับ/สเตทเมนต์ของผู้ให้บริการ แล้วแก้ให้สองที่ตรงกัน (หน้า \"ตั้งค่าการรับชำระเงินออนไลน์\" และหน้าตั้งค่าช่องทาง) "
               + "— ระบบไม่เลือกฝั่งใดฝั่งหนึ่งให้ เพราะทั้งสองที่เป็นคำตอบของคำถามเดียวกัน";
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
