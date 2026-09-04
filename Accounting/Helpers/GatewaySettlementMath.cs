using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>1 รายการที่ผู้ให้บริการโอนรวมมาในงวดนี้ (ตัดเฉพาะสิ่งที่ต้องใช้คิด)</summary>
public readonly record struct SettlementIntentInput(
    Guid IntentId,
    decimal Amount,
    decimal? FeeActual,
    decimal FeeEstimated);

/// <summary>เหตุที่ยังบันทึกการโอนเข้าไม่ได้ — ต้องมีข้อความบอกทางแก้เสมอ</summary>
public enum SettlementBlockReason
{
    None = 0,
    NoIntents = 1,
    /// <summary>ยอดที่โอนเข้าจริงไม่ตรงกับที่คำนวณได้ — ห้ามลง JE ที่ไม่ตรงเงินจริง</summary>
    NetMismatch = 2,
    NegativeNet = 3,
}

/// <summary>บรรทัด JE 1 บรรทัดของการโอนเข้า (ยังไม่ผูก AccountId — ตัวเรียกแปลงเอง)</summary>
public readonly record struct SettlementJournalLine(
    SettlementLineRole Role, decimal Debit, decimal Credit, string Description);

public enum SettlementLineRole
{
    /// <summary>เงินเข้าบัญชีธนาคารจริง</summary>
    Bank = 1,
    /// <summary>ค่าธรรมเนียมผู้ให้บริการ (ยอด**ก่อน**หัก ณ ที่จ่าย เมื่อเปิดโหมดหัก)</summary>
    FeeExpense = 2,
    /// <summary>ล้างลูกหนี้ผู้ให้บริการรับชำระเงิน (11340)</summary>
    Clearing = 3,
    /// <summary>ภาษีหัก ณ ที่จ่ายค้างนำส่ง (ภ.ง.ด.53) — เฉพาะโหมดหัก</summary>
    WhtPayable = 4,
}

/// <summary>แผนการบันทึกการโอนเข้า 1 งวด</summary>
public sealed record SettlementPlan(
    bool Ok,
    SettlementBlockReason Reason,
    string? Message,
    IReadOnlyList<Guid> IntentIds,
    decimal Gross,
    decimal FeeNetPaid,
    decimal FeeGrossedUp,
    decimal WhtOnFee,
    decimal ExpectedNet,
    decimal ActualNet,
    IReadOnlyList<SettlementJournalLine> Lines)
{
    public decimal Difference => ActualNet - ExpectedNet;
}

/// <summary>
/// **บันทึกเงินที่ผู้ให้บริการโอนเข้าธนาคาร (settlement/payout) — ฟังก์ชันบริสุทธิ์**
///
/// ═══ ทำไมต้องมีขั้นนี้ (PAYMENT_GATEWAY_DESIGN.md §4.5 · มุมมอง CPA) ═══
/// ตอนลูกค้าจ่ายสำเร็จ เงิน<b>ยังไม่เข้าบัญชีธนาคารเรา</b> — ระบบจึงลง
/// <c>Dr 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</c> ไว้ก่อน · ผู้ให้บริการรวบยอด
/// แล้วโอนเข้าจริง T+n <b>หลังหักค่าธรรมเนียม</b> ⇒ ขั้นนี้คือขั้นที่:
/// <code>Dr ธนาคาร (ยอดสุทธิที่เข้าจริง) + Dr ค่าธรรมเนียม = Cr 11340 (ยอดเต็ม)</code>
///
/// ═══ หัก ณ ที่จ่ายบนค่าธรรมเนียม (§3 เตรส · ภ.ง.ด.53) ═══
/// ผู้ให้บริการ<b>หักค่าธรรมเนียมไปเต็มจำนวนแล้ว</b> เงินที่เขาได้รับจริงคือยอดสุทธิ
/// ⇒ เมื่อบริษัทเลือกหัก ณ ที่จ่าย ต้อง <b>gross-up</b>: ยอดค่าบริการก่อนหัก =
/// สุทธิ + ภาษีที่หัก · ไม่ใช่คิด 3% จากยอดสุทธิตรง ๆ (จะได้ฐานภาษีต่ำกว่าความจริง
/// และหนังสือรับรอง 50 ทวิ ที่ออกให้จะผิด)
/// <code>ภาษี = round(สุทธิ × 3/97, 2) · ค่าบริการก่อนหัก = สุทธิ + ภาษี</code>
///
/// <para>⚠️ <b>ตรวจแล้วว่าไม่ใช่การแก้บั๊ก</b>: สูตรทางเลือก <c>round(สุทธิ/0.97, 2)</c>
/// แล้วคิดภาษี 3% จากยอดนั้น ให้ผล<b>เท่ากันทุกยอด</b>ในช่วงที่ใช้จริง (ไล่ทุกสตางค์
/// ของค่าธรรมเนียม ฿0.01–฿5,000 · ต่างกัน 0 ยอด) — เหตุผลที่เลือกรูปนี้คือ<b>โครงสร้าง</b>
/// ไม่ใช่ตัวเลข: เขียนเป็น "ก่อนหัก = สุทธิ + ภาษี" ทำให้ <c>ก่อนหัก − ภาษี = สุทธิ</c>
/// จริง<b>โดยนิยาม</b> ⇒ ยอดบนหนังสือรับรอง 50 ทวิ กับยอดที่ผู้ให้บริการได้รับจริง
/// ไม่มีวันหลุดจากกัน แม้ภายหลังจะมีคนไปเปลี่ยนวิธีปัด</para>
///
/// ═══ กติกาที่ตั้งใจ ═══
/// <list type="number">
/// <item><b>ยอดที่โอนเข้าจริงต้องตรงกับที่คำนวณได้</b> ไม่งั้น <b>บล็อก</b> —
///   JE ที่ยอดธนาคารไม่ตรงสเตทเมนต์คือสิ่งที่กระทบยอดไม่ได้ตลอดไป
///   (ผู้ใช้ต้องไปแก้ค่าธรรมเนียมจริงรายรายการก่อน ไม่ใช่ให้ระบบเดาส่วนต่างให้)</item>
/// <item><b>ค่าธรรมเนียมใช้ตัวจริงเมื่อมี</b> ตัวประมาณเป็นทางเลือกสุดท้าย —
///   และเมื่อผลรวมไม่ตรง ข้อความต้องบอกว่าต่างเท่าไรเพื่อให้ตามแก้ถูกจุด</item>
/// <item>ไม่มี "ปัดให้ลงตัว" — ผลต่างเป็นข้อมูล ไม่ใช่สิ่งที่ต้องกลบ</item>
/// </list>
/// </summary>
public static class GatewaySettlementMath
{
    /// <summary>อัตราหัก ณ ที่จ่ายค่าบริการ นิติบุคคลไทย (ท.ป.4/2528 · §3 เตรส)</summary>
    public const decimal ServiceWhtRate = 0.03m;

    /// <summary>ยอมรับผลต่างได้ไม่เกินนี้ (เศษปัดของผู้ให้บริการ)</summary>
    public const decimal ToleranceBaht = 0.01m;

    public static SettlementPlan Plan(
        IReadOnlyCollection<SettlementIntentInput> intents,
        decimal actualNetReceived,
        GatewayFeeWhtMode whtMode,
        string settlementRef)
    {
        if (intents.Count == 0)
            return Blocked(SettlementBlockReason.NoIntents,
                "ไม่มีรายการที่รอโอนเข้าในช่วงที่เลือก — ตรวจช่วงวันที่ หรือรายการอาจถูกบันทึกการโอนไปแล้ว",
                actualNetReceived);

        var gross = R(intents.Sum(i => i.Amount));
        var feeNet = R(intents.Sum(i => i.FeeActual ?? i.FeeEstimated));

        // gross-up: ภาษีคำนวณจาก "ยอดสุทธิที่ผู้ให้บริการได้รับจริง" (= ค่าธรรมเนียมที่หักไป)
        var wht = whtMode == GatewayFeeWhtMode.Withhold3Percent
            ? R(feeNet * ServiceWhtRate / (1m - ServiceWhtRate))
            : 0m;
        var feeGross = feeNet + wht;

        var expectedNet = gross - feeNet;
        if (expectedNet < 0m)
            return Blocked(SettlementBlockReason.NegativeNet,
                $"ค่าธรรมเนียมรวม ({feeNet:N2}) มากกว่ายอดที่รับชำระ ({gross:N2}) — "
                + "ตรวจค่าธรรมเนียมรายรายการก่อน",
                actualNetReceived, intents, gross, feeNet, feeGross, wht, expectedNet);

        var diff = actualNetReceived - expectedNet;
        if (Math.Abs(diff) > ToleranceBaht)
            return Blocked(SettlementBlockReason.NetMismatch,
                $"ยอดที่โอนเข้าจริง ({actualNetReceived:N2}) ไม่ตรงกับยอดที่คำนวณได้ ({expectedNet:N2}) "
                + $"— ต่างกัน {diff:N2} บาท · แก้ค่าธรรมเนียมจริงของรายการที่เกี่ยวข้อง "
                + "หรือเลือกช่วงวันที่ให้ตรงกับรอบโอนก่อน (ระบบไม่เดาส่วนต่างให้ "
                + "เพราะ JE ที่ยอดธนาคารไม่ตรงสเตทเมนต์จะกระทบยอดไม่ได้ตลอดไป)",
                actualNetReceived, intents, gross, feeNet, feeGross, wht, expectedNet);

        var lines = new List<SettlementJournalLine>
        {
            new(SettlementLineRole.Bank, expectedNet, 0m,
                $"รับโอนจากผู้ให้บริการรับชำระเงิน {settlementRef}"),
        };
        if (feeGross > 0m)
            lines.Add(new(SettlementLineRole.FeeExpense, feeGross, 0m,
                $"ค่าธรรมเนียมรับชำระเงิน {settlementRef}"
                + (wht > 0m ? " (ยอดก่อนหัก ณ ที่จ่าย)" : "")));
        lines.Add(new(SettlementLineRole.Clearing, 0m, gross,
            $"ล้างลูกหนี้ผู้ให้บริการรับชำระเงิน {intents.Count} รายการ"));
        if (wht > 0m)
            lines.Add(new(SettlementLineRole.WhtPayable, 0m, wht,
                $"ภาษีหัก ณ ที่จ่าย 3% ค่าธรรมเนียม {settlementRef} (ภ.ง.ด.53)"));

        return new SettlementPlan(true, SettlementBlockReason.None, null,
            intents.Select(i => i.IntentId).ToList(),
            gross, feeNet, feeGross, wht, expectedNet, actualNetReceived, lines);
    }

    private static SettlementPlan Blocked(SettlementBlockReason reason, string message,
        decimal actualNet, IReadOnlyCollection<SettlementIntentInput>? intents = null,
        decimal gross = 0m, decimal feeNet = 0m, decimal feeGross = 0m, decimal wht = 0m,
        decimal expectedNet = 0m)
        => new(false, reason, message,
            intents?.Select(i => i.IntentId).ToList() ?? new List<Guid>(),
            gross, feeNet, feeGross, wht, expectedNet, actualNet,
            Array.Empty<SettlementJournalLine>());

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
