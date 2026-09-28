using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>ข้อมูลของ PaymentIntent ที่ adapter ต้องใช้ (ตัวนำเข้าโหลดจากฐาน — adapter ไม่แตะฐาน)</summary>
/// <param name="AlreadyInBatch">intent นี้อยู่ในรอบโอนอื่นแล้ว (<c>SettlementBatchId</c> ไม่ว่าง) — มาเพราะคืนเงินภายหลังเท่านั้น</param>
/// <param name="RefundAlreadyInLines">ยอดคืนเงินของ intent นี้ที่มีบรรทัดในรอบโอนแล้ว (ค่าบวก)</param>
public sealed record SettlementIntentSnapshot(
    Guid Id,
    string? ProviderRef,
    decimal Amount,
    decimal RefundedAmount,
    decimal? FeeActual,
    decimal FeeEstimated,
    DateTime? ConfirmedAt,
    bool AlreadyInBatch,
    decimal RefundAlreadyInLines);

/// <summary>ผลประกอบบรรทัดจาก PaymentIntent</summary>
/// <param name="FeeUnknownCount">intent ที่ยังไม่รู้ค่าธรรมเนียมจริง (ไม่มีบรรทัดค่าธรรมเนียม ⇒ สมการรอบโอนจะไม่ลงตัวจนกว่าจะแก้)</param>
public sealed record SettlementIntentRows(IReadOnlyList<SettlementParsedRow> Rows, IReadOnlyList<Guid> NewIntentIds, int FeeUnknownCount);

/// <summary>
/// **ประกอบรอบโอนจาก PaymentIntent ของ gateway ในระบบเราเอง** (report-S2 §3 · <c>SettlementSourceKind.PaymentIntents</c>)
///
/// <para>ทุกบรรทัดพก <c>PaymentIntentId</c> ⇒ <c>SettlementBatchMath.Plan</c> ถือว่า "อยู่ในผังพักแล้ว" (ขารับเงินลงไว้ตอนจ่ายสำเร็จ ·
/// ขาคืนเงินลงไว้ตอนคืนผ่าน <c>GatewayRefundService</c> — ทีม E) ไม่ลงซ้ำ · ค่าธรรมเนียม = บรรทัด <c>PaymentFee</c> (ยอดติดลบ)</para>
/// <para>═══ กติกา (ตรงกับตัวเลือกของ <c>GatewaySettlementService</c> ทีม E) ═══
/// <list type="bullet">
/// <item>intent ใหม่ (ยังไม่อยู่ในรอบใด): ยอดขายเต็ม <c>Amount</c> + คืนเงินสะสม <c>RefundedAmount</c> (ถ้ามี) + ค่าธรรมเนียม
/// <c>FeeActual ?? FeeEstimated</c> (&gt; 0 เท่านั้น — ไม่รู้ = ไม่แต่งตัวเลข · นับแจ้ง)</item>
/// <item>intent ที่อยู่ในรอบก่อนแล้วแต่คืนเงินเพิ่มภายหลัง: บรรทัดคืนเงินเฉพาะส่วนที่ยังไม่มีบรรทัด (ผู้ให้บริการหักจากรอบถัดไป)</item>
/// <item>คีย์กันซ้ำ: <c>pi:{id}:sale|fee|refund@{ยอดคืนสะสม}</c> (<see cref="SettlementTxnKey.ForPaymentIntent"/>)</item>
/// </list></para>
/// </summary>
public static class PaymentIntentAdapter
{
    public const string AdapterCode = "payment-intents";

    public static SettlementIntentRows BuildRows(IEnumerable<SettlementIntentSnapshot> intents)
    {
        var rows = new List<SettlementParsedRow>();
        var fresh = new List<Guid>();
        var feeUnknown = 0;
        var seq = 0;
        foreach (var i in intents.OrderBy(x => x.ConfirmedAt).ThenBy(x => x.Id))
        {
            var date = i.ConfirmedAt is DateTime c ? ThaiDate.CalendarDateUtc(c) : (DateTime?)null;
            if (!i.AlreadyInBatch)
            {
                fresh.Add(i.Id);
                rows.Add(Row(++seq, "Payment", date, i, "sale", i.Amount, SettlementLineType.Sale));
                var fee = i.FeeActual ?? i.FeeEstimated;
                if (fee > 0m) rows.Add(Row(++seq, "Payment fee", date, i, "fee", -fee, SettlementLineType.PaymentFee));
                else feeUnknown++;
            }
            var refundDelta = i.RefundedAmount - i.RefundAlreadyInLines;
            if (refundDelta > 0m)
                rows.Add(Row(++seq, "Refund", date, i,
                    "refund@" + i.RefundedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    -refundDelta, SettlementLineType.Refund));
        }
        return new SettlementIntentRows(rows, fresh, feeUnknown);
    }

    private static SettlementParsedRow Row(int seq, string label, DateTime? date, SettlementIntentSnapshot i, string part,
        decimal amount, SettlementLineType type)
        => new(seq, label, null, date, i.ProviderRef, SettlementTxnKey.ForPaymentIntent(i.Id, part), amount, null, null, type,
            PaymentIntentId: i.Id);
}
