using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>ข้อมูลของ PaymentIntent ที่ adapter ต้องใช้ (ตัวนำเข้าโหลดจากฐาน — adapter ไม่แตะฐาน)</summary>
/// <param name="RefundedAmount">ยอดคืน<b>ณ จุดตัดวันเงินเข้า</b>ของรอบนี้ (รอบ 200 ทีม P2 — ตัวนำเข้าคิดด้วย <c>GatewaySettlementMath.RefundedAsOf</c>
/// สูตรเดียวกับเส้นเดิม · เดิมเป็นยอดสะสมวันนี้ ⇒ คืนหลังวันเงินเข้าถูกนับในรอบนี้ ⇒ รอบไม่ลงตัว) — คืนตั้งแต่วันเงินเข้า = ของรอบถัดไป</param>
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
/// <item>intent ใหม่ (ยังไม่อยู่ในรอบใด): ยอดขายเต็ม <c>Amount</c> + ยอดคืน ณ วันเงินเข้า (ถ้ามี) + ค่าธรรมเนียม<b>ที่ถูกหักจริง</b>ตามโหมด VAT ของ
/// config gateway — <c>GatewaySettlementMath.Contribution</c> ตัวเดียวกับเส้นเดิม (รอบ 200 ทีม P2: "บวก VAT เพิ่ม" = ค่าธรรมเนียม + VAT · VAT ระบุต่อรายการ
/// ใน <c>VatAmount</c> ⇒ <c>SettlementFeeTax</c> เชื่อค่านี้ ไม่คิด 7/107 จากยอดรวมเอง · "ไม่แยก VAT" = ไม่ระบุ) · ไม่รู้ค่าธรรมเนียม
/// (<c>FeeActual</c> ว่างและประมาณการ 0) = ไม่แต่งตัวเลข · นับแจ้ง · <c>FeeActual = 0</c> = รู้แล้วว่าไม่มีค่าธรรมเนียม (ไม่ใช่ "ไม่รู้")</item>
/// <item>intent ที่อยู่ในรอบก่อนแล้วแต่คืนเงินเพิ่มภายหลัง: บรรทัดคืนเงินเฉพาะส่วนที่ยังไม่มีบรรทัด (ผู้ให้บริการหักจากรอบถัดไป)</item>
/// <item>คีย์กันซ้ำ: <c>pi:{id}:sale|fee|refund@{ยอดคืนสะสม}</c> (<see cref="SettlementTxnKey.ForPaymentIntent"/>)</item>
/// </list></para>
/// </summary>
public static class PaymentIntentAdapter
{
    public const string AdapterCode = "payment-intents";

    /// <param name="feeVatMode">โหมด VAT ค่าธรรมเนียมของ config gateway ที่ช่องทางผูก (<c>PaymentProviderConfig.FeeVatMode</c>) —
    /// ผู้เรียกตรวจแล้วว่าตรงกับโหมดของช่องทาง (<c>GatewayBatchIntentRules.ModeMismatch</c>)</param>
    public static SettlementIntentRows BuildRows(IEnumerable<SettlementIntentSnapshot> intents, GatewayFeeVatMode feeVatMode)
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
                // สูตรเดียวกับเส้นเดิม — ยอดที่ผู้ให้บริการหักจริง (รวม VAT เมื่อบวกเพิ่ม) + VAT ต่อรายการ (ปัดต่อ charge)
                var part = GatewaySettlementMath.Contribution(
                    new SettlementIntentInput(i.Id, i.Amount, i.FeeActual, i.FeeEstimated, i.RefundedAmount), feeVatMode);
                if (part.FeeDeducted > 0m)
                    rows.Add(Row(++seq, "Payment fee", date, i, "fee", -part.FeeDeducted, SettlementLineType.PaymentFee,
                        feeVatMode == GatewayFeeVatMode.None ? (decimal?)null : -part.FeeVat));
                else if (i.FeeActual == null) feeUnknown++;
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
        decimal amount, SettlementLineType type, decimal? vat = null)
        => new(seq, label, null, date, i.ProviderRef, SettlementTxnKey.ForPaymentIntent(i.Id, part), amount, vat, null, type,
            PaymentIntentId: i.Id);
}
