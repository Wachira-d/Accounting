using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>1 รายการที่ผู้ให้บริการโอนรวมมาในงวดนี้ (ตัดเฉพาะสิ่งที่ต้องใช้คิด)
///
/// <para>รอบ 198 (G-2): <c>RefundedAmount</c> = ยอดคืนสะสม · <c>AlreadySettled</c> = รายการนี้ถูกนับในรอบโอนก่อนแล้ว
/// (กลับมาอีกครั้งเพราะคืนเงิน<b>หลัง</b>รอบโอน ⇒ ผู้ให้บริการหักยอดคืนจากรอบนี้) · <c>RefundSettledAmount</c> =
/// ยอดคืนที่ถูกหักในรอบก่อน ๆ แล้ว</para></summary>
public readonly record struct SettlementIntentInput(
    Guid IntentId,
    decimal Amount,
    decimal? FeeActual,
    decimal FeeEstimated,
    decimal RefundedAmount = 0m,
    bool AlreadySettled = false,
    decimal RefundSettledAmount = 0m);

/// <summary>เหตุที่ยังบันทึกการโอนเข้าไม่ได้ — ต้องมีข้อความบอกทางแก้เสมอ</summary>
public enum SettlementBlockReason
{
    None = 0,
    NoIntents = 1,
    /// <summary>ยอดที่โอนเข้าจริงไม่ตรงกับที่คำนวณได้ — ห้ามลง JE ที่ไม่ตรงเงินจริง</summary>
    NetMismatch = 2,
    NegativeNet = 3,
    /// <summary>เปิดโหมดหัก ณ ที่จ่ายค่าธรรมเนียม แต่เส้นนี้ยังออกหนังสือรับรอง 50 ทวิ ให้ผู้ให้บริการไม่ได้
    /// ⇒ ห้ามลง 21917 ที่ไม่มีใบรับรองรองรับ (คำตัดสินเจ้าของรอบ 170 ข้อ ค) — รอบ 198 G-4</summary>
    WhtCertificateRequired = 4,
    /// <summary>วันที่เงินเข้าอยู่ในงวดบัญชีที่ปิดแล้ว (รอบ 198 G-5)</summary>
    PeriodClosed = 5,
}

/// <summary>บรรทัด JE 1 บรรทัดของการโอนเข้า (ยังไม่ผูก AccountId — ตัวเรียกแปลงเอง)</summary>
public readonly record struct SettlementJournalLine(
    SettlementLineRole Role, decimal Debit, decimal Credit, string Description);

public enum SettlementLineRole
{
    /// <summary>เงินเข้าบัญชีธนาคารจริง</summary>
    Bank = 1,
    /// <summary>ค่าธรรมเนียมผู้ให้บริการ (ก่อน VAT เมื่อแยก VAT · รวมภาษีที่ออกแทนเมื่อเปิดโหมดหัก)</summary>
    FeeExpense = 2,
    /// <summary>ล้างลูกหนี้ผู้ให้บริการรับชำระเงิน (11340)</summary>
    Clearing = 3,
    /// <summary>ภาษีหัก ณ ที่จ่ายค้างนำส่ง (ภ.ง.ด.53) — เฉพาะโหมดหัก</summary>
    WhtPayable = 4,
    /// <summary>VAT ของค่าธรรมเนียม → <b>11630 ภาษีซื้อรอเครดิต</b> (ยังไม่มีใบกำกับของผู้ให้บริการ ·
    /// ย้ายเข้า 11610 เมื่อได้ใบกำกับรายเดือน) — เฉพาะบริษัทจด VAT ที่ตั้งโหมดแยก VAT (รอบ 198 G-3)</summary>
    FeeInputVatDeferred = 5,
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
    IReadOnlyList<SettlementJournalLine> Lines,
    decimal FeeVat = 0m,
    decimal RefundDeducted = 0m)
{
    public decimal Difference => ActualNet - ExpectedNet;
}

/// <summary>ยอดที่รายการหนึ่ง "ส่งผล" ต่อรอบโอน — ตัวเดียวที่หน้ารายการค้างโอนและแผน JE ใช้ร่วมกัน
/// (<c>Clearing</c> ติดลบได้ = คืนเงินหลังรอบโอนก่อน · <c>FeeDeducted</c> = ที่ผู้ให้บริการหักจากเงินโอนจริง ·
/// <c>FeeBeforeVat</c> = ฐานของหัก ณ ที่จ่าย · <c>FeeVat</c> = VAT ของค่าธรรมเนียมก่อนตัดสินว่าเคลมได้ไหม)</summary>
public readonly record struct SettlementIntentContribution(
    decimal Clearing,
    decimal FeeDeducted,
    decimal FeeBeforeVat,
    decimal FeeVat)
{
    public decimal Net => Clearing - FeeDeducted;
}

/// <summary>
/// **บันทึกเงินที่ผู้ให้บริการโอนเข้าธนาคาร (settlement/payout) — ฟังก์ชันบริสุทธิ์**
///
/// ═══ ทำไมต้องมีขั้นนี้ (PAYMENT_GATEWAY_DESIGN.md §4.5 · มุมมอง CPA) ═══
/// ตอนลูกค้าจ่ายสำเร็จ เงิน<b>ยังไม่เข้าบัญชีธนาคารเรา</b> — ระบบจึงลง
/// <c>Dr 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</c> ไว้ก่อน · ผู้ให้บริการรวบยอด
/// แล้วโอนเข้าจริง T+n <b>หลังหักค่าธรรมเนียม</b> ⇒ ขั้นนี้คือขั้นที่:
/// <code>Dr ธนาคาร (สุทธิที่เข้าจริง) + Dr ค่าธรรมเนียม (+ Dr 11630 VAT ค่าธรรมเนียม) = Cr 11340</code>
///
/// ═══ หัก ณ ที่จ่ายบนค่าธรรมเนียม (§3 เตรส · ภ.ง.ด.53) ═══
/// ผู้ให้บริการ<b>หักค่าธรรมเนียมไปเต็มจำนวนแล้ว</b> ⇒ ถ้าบริษัทเลือกหัก ณ ที่จ่าย = บริษัท<b>ออกภาษีแทน</b>
/// (gross-up) · <b>ฐานภาษี = ค่าบริการก่อน VAT</b> (รอบ 198 G-4 — เดิมใช้ยอดที่รวม VAT ⇒ ภาษีเกิน ~7%
/// และ 50 ทวิ ผิด):
/// <code>ภาษี = round(ค่าธรรมเนียมก่อน VAT × 3/97, 2) · ค่าบริการก่อนหัก = ค่าธรรมเนียมก่อน VAT + ภาษี</code>
/// <para>เขียนเป็น "ก่อนหัก = ฐาน + ภาษี" ทำให้ <c>ก่อนหัก − ภาษี = ฐาน</c> จริง<b>โดยนิยาม</b> (ไล่ทุกสตางค์แล้ว
/// สูตรทางเลือก round(ฐาน/0.97) ให้ผลเท่ากัน — เหตุผลที่เลือกรูปนี้คือโครงสร้าง ไม่ใช่ตัวเลข)</para>
/// <para>⚠️ รอบ 198: <b>แผนที่มีภาษีหัก ณ ที่จ่ายถูกบล็อก</b> (<see cref="SettlementBlockReason.WhtCertificateRequired"/>)
/// จนกว่าเส้นนี้จะออกหนังสือรับรอง 50 ทวิ ได้ — ห้ามลง 21917 ที่ไม่มีใบรับรอง (คำตัดสินเจ้าของรอบ 170 ข้อ ค) ·
/// แผนยังคำนวณบรรทัดให้ดู (พรีวิว) แต่ <c>Ok = false</c></para>
///
/// ═══ VAT ของค่าธรรมเนียม (รอบ 198 G-3) ═══
/// ตาม <see cref="GatewayFeeVatMode"/> ของผู้ให้บริการ: VAT → Dr <b>11630 ภาษีซื้อรอเครดิต</b> (ยังไม่มีใบกำกับ) ·
/// บริษัทไม่จด VAT ⇒ ไม่มีขา 11630 (VAT เป็นต้นทุนรวมในค่าธรรมเนียม) · ปัดต่อรายการ (ผู้ให้บริการคิด VAT ต่อ charge)
///
/// ═══ คืนเงิน (รอบ 198 G-2) ═══
/// คืนบางส่วนนับด้วย <c>Amount − RefundedAmount</c> (JE คืนเงินลด 11340 ไปแล้ว) · คืนเต็มก่อนรอบโอนนับ 0
/// แต่ค่าธรรมเนียมยังถูกหัก (ถ้าผู้ให้บริการคืนค่าธรรมเนียม ให้แก้ค่าธรรมเนียมจริงเป็น 0) · คืนหลังรอบโอน ⇒
/// รอบถัดไปนับยอดคืนที่ยังไม่ถูกหักเป็น<b>ติดลบ</b>
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

    /// <summary>อัตรา VAT ของค่าธรรมเนียม (ป.รัษฎากร §80)</summary>
    public const decimal FeeVatRate = 0.07m;

    /// <summary>ยอมรับผลต่างได้ไม่เกินนี้ (เศษปัดของผู้ให้บริการ)</summary>
    public const decimal ToleranceBaht = 0.01m;

    /// <summary>VAT ที่รวมอยู่ในยอด (7/107) — ปัด AwayFromZero</summary>
    private static decimal VatInside(decimal grossInclVat)
        => R(grossInclVat * 7m / 107m);

    /// <summary>หัก ณ ที่จ่ายแบบ<b>ออกภาษีแทน</b> (gross-up) จากค่าบริการ<b>ก่อน VAT</b> — ฐานตามกฎหมาย (G-4)</summary>
    public static decimal WhtOnFee(decimal feeBeforeVat)
        => feeBeforeVat <= 0m ? 0m : R(feeBeforeVat * ServiceWhtRate / (1m - ServiceWhtRate));

    /// <summary>ยอดที่รายการหนึ่งส่งผลต่อรอบโอน — ตัวเดียวของทั้งแผน JE และหน้ารายการค้างโอน</summary>
    public static SettlementIntentContribution Contribution(SettlementIntentInput i, GatewayFeeVatMode feeVatMode)
    {
        if (i.AlreadySettled)
        {
            // คืนเงินหลังรอบโอนก่อน — ค่าธรรมเนียมถูกหักไปในรอบนั้นแล้ว เหลือแต่ยอดคืนที่ถูกหักรอบนี้
            var pending = R(Math.Max(0m, i.RefundedAmount - i.RefundSettledAmount));
            return new SettlementIntentContribution(-pending, 0m, 0m, 0m);
        }

        var clearing = R(Math.Max(0m, i.Amount - i.RefundedAmount));
        var fee = R(i.FeeActual ?? i.FeeEstimated);
        switch (feeVatMode)
        {
            case GatewayFeeVatMode.IncludedInFee:
            {
                var vat = VatInside(fee);
                return new SettlementIntentContribution(clearing, fee, fee - vat, vat);
            }
            case GatewayFeeVatMode.AddedOnTop:
            {
                var vat = R(fee * FeeVatRate);
                return new SettlementIntentContribution(clearing, fee + vat, fee, vat);
            }
            default:
                return new SettlementIntentContribution(clearing, fee, fee, 0m);
        }
    }

    public static SettlementPlan Plan(
        IReadOnlyCollection<SettlementIntentInput> intents,
        decimal actualNetReceived,
        GatewayFeeWhtMode whtMode,
        string settlementRef,
        GatewayFeeVatMode feeVatMode = GatewayFeeVatMode.None,
        bool companyVatRegistered = true)
    {
        if (intents.Count == 0)
            return Blocked(SettlementBlockReason.NoIntents,
                "ไม่มีรายการที่รอโอนเข้าในช่วงที่เลือก — ตรวจช่วงวันที่ หรือรายการอาจถูกบันทึกการโอนไปแล้ว",
                actualNetReceived);

        var parts = intents.Select(i => Contribution(i, feeVatMode)).ToList();
        var gross = R(parts.Sum(c => c.Clearing));
        var feeDeducted = R(parts.Sum(c => c.FeeDeducted));
        var feeBeforeVat = R(parts.Sum(c => c.FeeBeforeVat));
        var feeVatAll = R(parts.Sum(c => c.FeeVat));
        var refundDeducted = R(-parts.Where(c => c.Clearing < 0m).Sum(c => c.Clearing));

        // บริษัทไม่จด VAT เคลมภาษีซื้อไม่ได้ ⇒ VAT ของค่าธรรมเนียมเป็นต้นทุน (ไม่มีขา 11630)
        var feeVatClaim = companyVatRegistered ? feeVatAll : 0m;
        var feeExpense = feeDeducted - feeVatClaim;

        var wht = whtMode == GatewayFeeWhtMode.Withhold3Percent ? WhtOnFee(feeBeforeVat) : 0m;
        var feeGross = feeBeforeVat + wht;

        var expectedNet = gross - feeDeducted;
        if (expectedNet < 0m)
            return Blocked(SettlementBlockReason.NegativeNet,
                $"ค่าธรรมเนียมรวม ({feeDeducted:N2}) มากกว่ายอดที่รับชำระ ({gross:N2}) — "
                + "ตรวจค่าธรรมเนียมรายรายการก่อน (ปุ่ม \"แก้ค่าธรรมเนียม\" ในตารางรายการค้างโอนด้านบน)",
                actualNetReceived, intents, gross, feeDeducted, feeGross, wht, expectedNet, feeVatClaim, refundDeducted);

        var diff = actualNetReceived - expectedNet;
        if (Math.Abs(diff) > ToleranceBaht)
            return Blocked(SettlementBlockReason.NetMismatch,
                $"ยอดที่โอนเข้าจริง ({actualNetReceived:N2}) ไม่ตรงกับยอดที่คำนวณได้ ({expectedNet:N2}) "
                + $"— ต่างกัน {diff:N2} บาท · แก้ค่าธรรมเนียมจริงรายรายการที่ปุ่ม \"แก้ค่าธรรมเนียม\" "
                + "ในตารางรายการค้างโอนด้านบน หรือเลือกช่วงวันที่ให้ตรงกับรอบโอนก่อน (ระบบไม่เดาส่วนต่างให้ "
                + "เพราะ JE ที่ยอดธนาคารไม่ตรงสเตทเมนต์จะกระทบยอดไม่ได้ตลอดไป)",
                actualNetReceived, intents, gross, feeDeducted, feeGross, wht, expectedNet, feeVatClaim, refundDeducted);

        var lines = new List<SettlementJournalLine>
        {
            new(SettlementLineRole.Bank, expectedNet, 0m,
                $"รับโอนจากผู้ให้บริการรับชำระเงิน {settlementRef}"),
        };
        var feeExpenseLine = feeExpense + wht;
        if (feeExpenseLine > 0m)
            lines.Add(new(SettlementLineRole.FeeExpense, feeExpenseLine, 0m,
                $"ค่าธรรมเนียมรับชำระเงิน {settlementRef}"
                + (feeVatClaim > 0m ? " (ก่อน VAT)" : "")
                + (wht > 0m ? " (รวมภาษีที่ออกแทน)" : "")));
        if (feeVatClaim > 0m)
            lines.Add(new(SettlementLineRole.FeeInputVatDeferred, feeVatClaim, 0m,
                $"ภาษีซื้อรอเครดิต — VAT ค่าธรรมเนียม {settlementRef} (รอใบกำกับของผู้ให้บริการ)"));
        if (gross > 0m)
            lines.Add(new(SettlementLineRole.Clearing, 0m, gross,
                $"ล้างลูกหนี้ผู้ให้บริการรับชำระเงิน {intents.Count} รายการ"
                + (refundDeducted > 0m ? $" (สุทธิหลังหักคืนเงินหลังรอบโอนก่อน {refundDeducted:N2})" : "")));
        if (wht > 0m)
            lines.Add(new(SettlementLineRole.WhtPayable, 0m, wht,
                $"ภาษีหัก ณ ที่จ่าย 3% ค่าธรรมเนียม {settlementRef} (ภ.ง.ด.53)"));

        var ids = intents.Select(i => i.IntentId).ToList();
        if (wht > 0m)
            // คำนวณให้ดูครบ (พรีวิว) แต่ห้ามลง — ไม่มีหนังสือรับรอง 50 ทวิ รองรับ 21917
            return new SettlementPlan(false, SettlementBlockReason.WhtCertificateRequired,
                "เปิดโหมด \"หัก ณ ที่จ่ายค่าธรรมเนียม\" ไว้ แต่เส้นบันทึกรอบโอนยังออกหนังสือรับรอง 50 ทวิ "
                + "ให้ผู้ให้บริการไม่ได้ — ลงภาษีค้างนำส่ง ภ.ง.ด.53 โดยไม่มีใบรับรองไม่ได้ · "
                + "ทางไปต่อ: ปิดโหมดหัก ณ ที่จ่ายที่หน้า \"ตั้งค่าการรับชำระเงินออนไลน์\" แล้วบันทึกรอบโอนนี้ · "
                + "ถ้าต้องหัก ณ ที่จ่ายจริง ให้บันทึกใบกำกับค่าธรรมเนียมของผู้ให้บริการเป็นเอกสารซื้อ "
                + "(เส้นนั้นออก 50 ทวิ ได้ตามปกติ)",
                ids, gross, feeDeducted, feeGross, wht, expectedNet, actualNetReceived, lines,
                feeVatClaim, refundDeducted);

        return new SettlementPlan(true, SettlementBlockReason.None, null,
            ids, gross, feeDeducted, feeGross, wht, expectedNet, actualNetReceived, lines,
            feeVatClaim, refundDeducted);
    }

    /// <summary>บล็อกแผนที่คำนวณแล้วด้วยเหตุนอกคณิต (เช่นงวดปิด) — คงตัวเลขไว้ให้ผู้ใช้เห็น แต่ไม่มีบรรทัดให้ลง</summary>
    public static SettlementPlan Block(SettlementPlan plan, SettlementBlockReason reason, string message)
        => plan with { Ok = false, Reason = reason, Message = message, Lines = Array.Empty<SettlementJournalLine>() };

    private static SettlementPlan Blocked(SettlementBlockReason reason, string message,
        decimal actualNet, IReadOnlyCollection<SettlementIntentInput>? intents = null,
        decimal gross = 0m, decimal feeNet = 0m, decimal feeGross = 0m, decimal wht = 0m,
        decimal expectedNet = 0m, decimal feeVat = 0m, decimal refundDeducted = 0m)
        => new(false, reason, message,
            intents?.Select(i => i.IntentId).ToList() ?? new List<Guid>(),
            gross, feeNet, feeGross, wht, expectedNet, actualNet,
            Array.Empty<SettlementJournalLine>(), feeVat, refundDeducted);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
