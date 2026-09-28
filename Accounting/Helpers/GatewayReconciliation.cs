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
    GatewayFeeVatMode FeeVatMode = GatewayFeeVatMode.None);

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

    private static decimal RefundedOf(GatewayIntentAmounts r)
        // สถานะคืนเต็ม = เงินออกเต็มยอดจริง (รวมแถวเก่าที่ไม่มียอดคืนบันทึก และคืนที่ลงบัญชีไม่สำเร็จ E-1) · คืนบางส่วน = ยอดสะสมที่บันทึก
        => r.IsRefundedFully ? r.Amount : Math.Max(0m, Math.Min(r.Amount, r.RefundedAmount));

    // เงินเป็น decimal และปัดแบบ AwayFromZero เสมอ (banker's rounding เป็นค่า default
    // ของ .NET และเคยทำให้ยอด OCR เพี้ยนมาแล้ว — CLAUDE.md กฎเหล็ก #4 E)
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
