namespace Accounting.Helpers;

/// <summary>ยอดของ intent 1 รายการที่เข้าการกระทบยอด (ตัดเฉพาะสิ่งที่ต้องใช้คิด)</summary>
public readonly record struct GatewayIntentAmounts(
    decimal Amount,
    decimal? FeeActual,
    decimal FeeEstimated,
    decimal? SettledAmount,
    bool IsRefundedFully,
    bool IsSettled);

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
/// <code>Σ charge สำเร็จ − Σ คืนเงิน − Σ ค่าธรรมเนียม = Σ ยอดที่โอนเข้าจริง + ที่ยังไม่ถึงรอบโอน</code>
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

        var gross = rows.Sum(r => r.Amount);
        var refunded = rows.Where(r => r.IsRefundedFully).Sum(r => r.Amount);

        // ค่าธรรมเนียม: ใช้ตัวจริงเมื่อมี · ตัวประมาณเมื่อยังไม่ settlement
        var fee = rows.Sum(r => r.FeeActual ?? r.FeeEstimated);
        var anyEstimated = rows.Any(r => r.FeeActual == null && r.FeeEstimated > 0m);

        var settled = rows.Where(r => r.IsSettled).Sum(r => r.SettledAmount ?? 0m);

        var unsettledRows = rows.Where(r => !r.IsSettled && !r.IsRefundedFully).ToList();
        // ยอดที่ "ควรจะได้" จากรายการที่ยังไม่ถึงรอบโอน = ยอดเต็มหักค่าธรรมเนียมของมันเอง
        var unsettledNet = unsettledRows.Sum(r => r.Amount - (r.FeeActual ?? r.FeeEstimated));

        var expectedNet = gross - refunded - fee;

        return new GatewayReconciliationResult(
            SucceededCount: rows.Count,
            GrossCharged: Round(gross),
            RefundedAmount: Round(refunded),
            FeeTotal: Round(fee),
            FeeIsEstimated: anyEstimated,
            ExpectedNet: Round(expectedNet),
            SettledTotal: Round(settled),
            UnsettledCount: unsettledRows.Count,
            UnsettledAmount: Round(unsettledNet));
    }

    // เงินเป็น decimal และปัดแบบ AwayFromZero เสมอ (banker's rounding เป็นค่า default
    // ของ .NET และเคยทำให้ยอด OCR เพี้ยนมาแล้ว — CLAUDE.md กฎเหล็ก #4 E)
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
