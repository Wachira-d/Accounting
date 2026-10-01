using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ยอดของ intent 1 รายการที่เข้าการกระทบยอด (ตัดเฉพาะสิ่งที่ต้องใช้คิด)
///
/// <para>ฝ่ายค้าน R-E5: เพิ่ม <c>RefundSettledAmount</c> (ยอดคืนที่ถูกหักในรอบโอนแล้ว) · <c>RefundDeductedAfterSettlement</c>
/// (ส่วนที่ถูกหักใน<b>รอบโอนหลัง</b>รอบของรายการนี้ — ลดยอดที่โอนเข้าจริงของรายการ) · <c>FeeVatMode</c> ของผู้ให้บริการ
/// (ค่าธรรมเนียมที่ถูกหักจริงในโหมด AddedOnTop รวม VAT)</para></summary>
public readonly record struct GatewayIntentAmounts(
    decimal Amount,
    decimal? FeeActual,
    decimal FeeEstimated,
    decimal? SettledAmount,
    bool IsRefundedFully,
    bool IsSettled,
    // รอบ 198 G-2: ยอดคืนสะสมที่ระบบบันทึก (คืนบางส่วน) — 0 = ไม่มีข้อมูล/ไม่เคยคืน
    decimal RefundedAmount = 0m,
    decimal RefundSettledAmount = 0m,
    decimal RefundDeductedAfterSettlement = 0m,
    GatewayFeeVatMode FeeVatMode = GatewayFeeVatMode.None,
    // รอบ 200 ทีม G (E2-12): ค่าธรรมเนียมที่ถูกหักจริง ณ วันบันทึกรอบ — มีค่า ⇒ แถวที่บันทึกรอบแล้วใช้ค่านี้ ไม่คิดใหม่ด้วยโหมด VAT วันนี้
    decimal? SettledFeeDeducted = null);

/// <summary>บรรทัดรอบโอน settlement 1 บรรทัดที่อ้าง intent (<c>SettlementLine.PaymentIntentId</c>) <b>ในรอบโอนที่ลงบัญชีแล้ว</b> — ยอดมีเครื่องหมายตามมุม wallet
/// (ขาย +ยอด · ค่าธรรมเนียม −ยอดที่หัก · คืนเงิน −ยอดคืน)</summary>
public readonly record struct GatewayBatchLineFact(SettlementLineType LineType, decimal Amount);

/// <summary>ยอดที่รอบโอน settlement (batch) ที่ลงบัญชีแล้วโอนเข้าให้ intent หนึ่ง — รอบ 201 ทีม GW (A-GW4)</summary>
/// <param name="SettledAmount">Σ บรรทัดทุกบรรทัดของ intent ในรอบที่ลงบัญชีแล้ว = ยอดที่โอนเข้าจริงสำหรับรายการนี้ (รวมยอดคืนที่ถูกหักในรอบหลัง)</param>
/// <param name="RefundSettledAmount">ยอดคืนที่ถูกหักในรอบที่ลงบัญชีแล้ว (บวก)</param>
/// <param name="FeeDeducted">ค่าธรรมเนียมที่ถูกหักในรอบที่ลงบัญชีแล้ว (บวก · รวม VAT ถ้าโหมดบวกเพิ่ม)</param>
public readonly record struct GatewayBatchSettledAmounts(decimal SettledAmount, decimal RefundSettledAmount, decimal FeeDeducted);

/// <summary>แถว intent ที่หน้ากระทบยอดอ่าน + ข้อเท็จจริงเรื่อง "เจ้าของรอบโอน" (เส้นเดิม = ใบสำคัญรอบโอน · เส้น batch = รอบโอน settlement)</summary>
public readonly record struct GatewayReconciliationIntentRow(
    decimal Amount,
    decimal? FeeActual,
    decimal FeeEstimated,
    decimal? SettledAmount,
    bool IsRefundedFully,
    bool SettledByJournal,
    decimal RefundedAmount,
    decimal RefundSettledAmount,
    decimal RefundDeductedAfterSettlement,
    GatewayFeeVatMode FeeVatMode,
    decimal? SettledFeeDeducted,
    bool OwnedByBatch,
    bool BatchPosted);

/// <summary>ผลการกระทบยอดของงวดหนึ่ง</summary>
public sealed record GatewayReconciliationResult(
    int SucceededCount,
    decimal GrossCharged,
    decimal RefundedAmount,
    decimal FeeTotal,
    bool FeeIsEstimated,
    decimal ExpectedNet,
    decimal SettledTotal,
    int UnsettledCount,
    decimal UnsettledAmount)
{
    /// <summary>ผลต่างระหว่าง "เงินที่ควรได้" กับ "เงินที่ผู้ให้บริการโอนมาแล้ว"
    ///
    /// <para>ไม่ใช่ศูนย์ไม่ได้แปลว่าผิดเสมอ — รายการที่ยังไม่ถึงรอบโอน (T+n) ทำให้ต่างกัน
    /// ตามธรรมชาติ · ตัวที่ต้องดูคือ <see cref="UnexplainedDifference"/></para></summary>
    public decimal Difference => ExpectedNet - SettledTotal;

    /// <summary>ผลต่างที่ <b>อธิบายไม่ได้</b> — หักส่วนที่ยังไม่ถึงรอบโอนออกแล้ว
    ///
    /// <para>ค่านี้ควรเป็น 0 · ไม่เป็นศูนย์ = มีเงินหายหรือค่าธรรมเนียมไม่ตรงที่คาด
    /// ต้องมีคนดู ห้ามปล่อยผ่าน</para></summary>
    public decimal UnexplainedDifference => Difference - UnsettledAmount;

    public bool IsBalanced => Math.Abs(UnexplainedDifference) < 0.01m;
}

/// <summary>
/// กระทบยอดเงินที่รับผ่าน payment gateway — **ฟังก์ชันบริสุทธิ์**
///
/// ═══ ที่มา (PAYMENT_GATEWAY_DESIGN.md §4.5 · มุมมอง CPA) ═══
/// เงินที่ลูกค้าจ่ายผ่าน gateway <b>ไม่ได้เข้าบัญชีธนาคารทันที</b> — ผู้ให้บริการโอนเข้า
/// T+n <b>หลังหักค่าธรรมเนียม</b> ⇒ ถ้าลง Dr ธนาคารตั้งแต่ตอน charge สำเร็จ ยอดธนาคาร
/// ในระบบจะไม่ตรงกับยอดจริง<b>ตลอดเวลา</b> และผู้ทำบัญชีจะกระทบยอดไม่ได้เลย
///
/// สมการที่ต้องเป็นจริง:
/// <code>Σ charge − Σ คืนเงิน − Σ ค่าธรรมเนียม = Σ ยอดที่โอนเข้าจริง + ที่ยังไม่ถึงรอบโอน</code>
///
/// ═══ สูตรเดียวกับรอบโอน (ฝ่ายค้าน R-E5) ═══
/// ทุกยอดต่อรายการมาจาก <see cref="GatewaySettlementMath.Contribution"/> ตัวเดียวกับแผน JE รอบโอน — เดิมเป็นสูตรที่สอง
/// ที่ค้างไม่สมดุลถาวร 3 กรณี: โหมด AddedOnTop (ค่าธรรมเนียมไม่รวม VAT ที่ถูกหักจริง) · คืนเงินหลังรอบโอน (ยอดโอนเข้าคงยอดเดิม
/// แต่ยอดที่ควรได้หักยอดคืน) · คืนเต็มที่ยังไม่ถึงรอบโอน (ถูกตัดออกจาก "ยังไม่ถึงรอบโอน" ทั้งที่ค่าธรรมเนียมยังถูกหัก)
/// <list type="bullet">
/// <item>ยอดที่ควรได้ตลอดอายุ = <c>Contribution(ยอดคืนสะสมทั้งหมด).Net</c></item>
/// <item>โอนเข้าแล้ว = <c>SettledAmount</c> (สุทธิ ณ รอบของรายการ) − ยอดคืนที่ถูกหักในรอบหลัง ๆ</item>
/// <item>ยังไม่ถึงรอบโอน = ยังไม่บันทึกรอบโอน: <c>Contribution.Net</c> (คืนเต็มได้ติดลบ = ค่าธรรมเนียมที่จะถูกหัก) ·
///   บันทึกแล้ว: ยอดคืนที่ยังไม่ถูกหัก (ติดลบ) — <c>Contribution(AlreadySettled)</c></item>
/// </list>
///
/// ═══ จุดที่ตั้งใจ ═══
/// <list type="bullet">
/// <item><b>แยก "ต่างเพราะยังไม่ถึงรอบโอน" ออกจาก "ต่างแบบอธิบายไม่ได้"</b> — ถ้ารวมกัน
///   รายงานจะแดงทุกวันจนไม่มีใครดู แล้วของจริงจะถูกกลบ</item>
/// <item><b>บอกด้วยว่าค่าธรรมเนียมเป็นตัวประมาณหรือตัวจริง</b> — ก่อน settlement เรามีแค่
///   อัตราที่คาดไว้ · ตัวเลขประมาณที่ไม่ติดป้ายจะถูกอ่านเป็นตัวจริง</item>
/// </list>
/// </summary>
public static class GatewayReconciliation
{
    public static GatewayReconciliationResult Compute(IEnumerable<GatewayIntentAmounts> succeeded)
    {
        var rows = succeeded.ToList();

        decimal gross = 0m, refunded = 0m, fee = 0m, expectedNet = 0m, settled = 0m, outstanding = 0m;
        var outstandingCount = 0;
        foreach (var r in rows)
        {
            var refundedTotal = RefundedOf(r);
            gross += r.Amount;
            refunded += refundedTotal;

            // ตลอดอายุรายการ — สูตรเดียวกับแผน JE (ค่าธรรมเนียมตัวจริงก่อนตัวประมาณ · AddedOnTop รวม VAT · คืนเต็มนับ 0 แต่ค่าธรรมเนียมยังถูกหัก)
            var lifetime = GatewaySettlementMath.Contribution(
                new SettlementIntentInput(Guid.Empty, r.Amount, r.FeeActual, r.FeeEstimated, refundedTotal), r.FeeVatMode);
            // รอบ 200 ทีม G (E2-12): บันทึกรอบแล้ว + รู้ค่าธรรมเนียมที่ถูกหักจริง ⇒ ใช้ค่านั้น (เปลี่ยนโหมด VAT ทีหลังต้องไม่ทำให้รอบเก่าไม่สมดุล)
            if (r.IsSettled && r.SettledFeeDeducted is decimal settledFee)
                lifetime = lifetime with { FeeDeducted = settledFee };
            fee += lifetime.FeeDeducted;
            expectedNet += lifetime.Net;

            decimal pending;
            if (r.IsSettled)
            {
                settled += (r.SettledAmount ?? 0m) - r.RefundDeductedAfterSettlement;
                // ยอดคืนหลังรอบโอนที่ผู้ให้บริการยังไม่หัก = ติดลบที่จะมาในรอบถัดไป
                pending = GatewaySettlementMath.Contribution(
                    new SettlementIntentInput(Guid.Empty, r.Amount, r.FeeActual, r.FeeEstimated, refundedTotal,
                        AlreadySettled: true, RefundSettledAmount: r.RefundSettledAmount), r.FeeVatMode).Net;
            }
            else
            {
                pending = lifetime.Net;
            }
            if (pending != 0m || !r.IsSettled) outstandingCount++;
            outstanding += pending;
        }

        var anyEstimated = rows.Any(r => r.FeeActual == null && r.FeeEstimated > 0m);

        return new GatewayReconciliationResult(
            SucceededCount: rows.Count,
            GrossCharged: Round(gross),
            RefundedAmount: Round(refunded),
            FeeTotal: Round(fee),
            FeeIsEstimated: anyEstimated,
            ExpectedNet: Round(expectedNet),
            SettledTotal: Round(settled),
            UnsettledCount: outstandingCount,
            UnsettledAmount: Round(outstanding));
    }

    /// <summary>รอบโอน settlement สถานะนี้ "โอนเข้าแล้ว" ไหม — ลงบัญชีแล้ว (Posted) หรือจับคู่เงินเข้าธนาคารแล้ว (BankMatched) ·
    /// ตัวตัดสินตัวเดียวของหน้ากระทบยอด/หน้ารายการ (รอบ 201 ทีม GW · A-GW4) · ฉบับร่าง/จัดประเภท/จับคู่ใบขาย/ยกเลิก = ยังไม่โอน</summary>
    public static bool IsBatchPosted(SettlementBatchStatus? status)
        => status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched;

    /// <summary>ยอดที่รอบโอน settlement ที่ลงบัญชีแล้วโอนเข้าให้ intent หนึ่ง — จากบรรทัดรอบโอนที่อ้าง intent (ผู้เรียกส่งเฉพาะบรรทัดของรอบที่
    /// <see cref="IsBatchPosted"/>) · ไม่มีบรรทัด = 0 ทุกช่อง</summary>
    public static GatewayBatchSettledAmounts BatchSettled(IEnumerable<GatewayBatchLineFact> postedLines)
    {
        decimal settled = 0m, refund = 0m, fee = 0m;
        foreach (var l in postedLines)
        {
            settled += l.Amount;
            if (l.LineType == SettlementLineType.Refund) refund += -l.Amount;
            else if (l.LineType == SettlementLineType.PaymentFee) fee += -l.Amount;
        }
        return new GatewayBatchSettledAmounts(Round(settled), Round(refund), Round(fee));
    }

    /// <summary>แปลงแถว intent เป็นยอดเข้าการกระทบยอด — <b>ตัวตัดสินตัวเดียว</b>ว่า "โอนเข้าแล้ว" (รอบ 201 ทีม GW · A-GW4 · team-P2 B-1)
    ///
    /// <para>═══ ที่มา ═══ เดิมหน้ากระทบยอดเขียน <c>IsSettled = SettlementJournalEntryId != null</c> ⇒ intent ที่รอบโอน settlement (batch) เป็นเจ้าของและ
    /// <b>ลงบัญชีแล้ว</b> ถูกนับเป็น "ยังไม่ถึงรอบโอน" ตลอดไป (เส้น batch ไม่ประทับ <c>SettlementJournalEntryId</c>/<c>SettledAmount</c> — คนละเจ้าของ)</para>
    /// <para>═══ กติกา ═══ เจ้าของเส้นเดิม = ตามเดิมทุกช่อง · เจ้าของ batch: โอนแล้วเมื่อรอบโอน <see cref="IsBatchPosted"/> · ยอดที่โอนเข้า/ยอดคืนที่ถูกหัก/
    /// ค่าธรรมเนียมที่ถูกหัก มาจากบรรทัดรอบโอนที่ลงบัญชีแล้ว (<paramref name="postedBatch"/>) — ยอดคืนที่ถูกหักในรอบหลังรวมอยู่ใน Σ บรรทัดแล้ว
    /// (<c>RefundDeductedAfterSettlement</c> = 0) · รอบโอนยังไม่ลงบัญชี = ยังไม่โอน (ไม่อ่านช่องเส้นเดิมที่ไม่ใช่ของมัน)</para></summary>
    public static GatewayIntentAmounts FromIntent(GatewayReconciliationIntentRow r, GatewayBatchSettledAmounts? postedBatch)
    {
        if (r.OwnedByBatch && !r.SettledByJournal)
        {
            if (!r.BatchPosted)
                return new GatewayIntentAmounts(r.Amount, r.FeeActual, r.FeeEstimated, null, r.IsRefundedFully, false,
                    r.RefundedAmount, 0m, 0m, r.FeeVatMode, null);
            var b = postedBatch ?? new GatewayBatchSettledAmounts(0m, 0m, 0m);
            return new GatewayIntentAmounts(r.Amount, r.FeeActual, r.FeeEstimated, b.SettledAmount, r.IsRefundedFully, true,
                r.RefundedAmount, b.RefundSettledAmount, 0m, r.FeeVatMode, b.FeeDeducted);
        }
        return new GatewayIntentAmounts(r.Amount, r.FeeActual, r.FeeEstimated, r.SettledAmount, r.IsRefundedFully, r.SettledByJournal,
            r.RefundedAmount, r.RefundSettledAmount, r.RefundDeductedAfterSettlement, r.FeeVatMode, r.SettledFeeDeducted);
    }

    /// <summary>แถวเก่าที่<b>แยกไม่ได้</b>ว่ายอดโอนเข้าที่บันทึกรวมยอดคืนที่ถูกหักในรอบโอนหลังไปเท่าไร (รอบ 201 ทีม GW · A-GW9 · review198-E2 E2-11)
    /// <para>รอบโอนเส้นเดิมที่บันทึกก่อนรอบ 200 (<c>SettledFeeDeducted</c> ว่าง = เติมย้อนหลังแบบพิสูจน์ไม่ได้) และมียอดคืนที่ถูกหักในรอบโอนแล้ว —
    /// ส่วนที่ถูกหักในรอบหลังก่อนมีคอลัมน์ <c>RefundDeductedAfterSettlement</c> ไม่ถูกเก็บ ⇒ "ผลต่างที่อธิบายไม่ได้" ของรายงานอาจมาจากแถวนี้ ·
    /// <b>ไม่เติมย้อนหลัง</b> (ต้องรู้โหมด VAT ค่าธรรมเนียม ณ วันบันทึกรอบ — ไม่ได้เก็บ · เติมด้วยค่าวันนี้ = ค่าที่แต่งขึ้น) ⇒ นับให้เห็นแทน</para></summary>
    public static bool LateRefundSplitUnknown(GatewayReconciliationIntentRow r)
        => r.SettledByJournal && r.SettledFeeDeducted == null && r.RefundSettledAmount > 0m;

    /// <summary>ข้อความของรายงานเมื่อมีแถวเก่าตาม <see cref="LateRefundSplitUnknown"/> — null = ไม่มี</summary>
    public static string? LateRefundSplitUnknownMessage(int count)
        => count <= 0 ? null
            : $"มี {count} รายการที่บันทึกรอบโอนก่อนระบบเก็บ \"ยอดคืนที่ถูกหักในรอบโอนหลัง\" — ยอดโอนเข้าของรายการเหล่านี้อาจนับยอดคืนซ้ำ/ขาด "
              + "ผลต่างที่อธิบายไม่ได้ของงวดที่มีรายการเหล่านี้ให้ตรวจกับสเตทเมนต์ผู้ให้บริการก่อนสรุปว่าเงินหาย (ระบบไม่เติมย้อนหลังเพราะพิสูจน์ไม่ได้)";

    private static decimal RefundedOf(GatewayIntentAmounts r)
        // สถานะคืนเต็ม = เงินออกเต็มยอดจริง (รวมแถวเก่าที่ไม่มียอดคืนบันทึก และคืนที่ลงบัญชีไม่สำเร็จ E-1) · คืนบางส่วน = ยอดสะสมที่บันทึก
        => r.IsRefundedFully ? r.Amount : Math.Max(0m, Math.Min(r.Amount, r.RefundedAmount));

    // เงินเป็น decimal และปัดแบบ AwayFromZero เสมอ (banker's rounding เป็นค่า default
    // ของ .NET และเคยทำให้ยอด OCR เพี้ยนมาแล้ว — CLAUDE.md กฎเหล็ก #4 E)
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
