using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ตัวเลือกเหตุผลของเงินเกิน 1 รายการ (หน้าเว็บวาดจากลิสต์นี้ — ไม่มีสำเนาป้าย/เงื่อนไขใน JS)</summary>
/// <param name="Reason">ชื่อของ <see cref="UnappliedReason"/> (ค่าที่ส่งกลับใน <c>ExcessReason</c>)</param>
/// <param name="Label">ป้ายภาษาไทย</param>
/// <param name="Hint">คำอธิบายผลที่ตามมา</param>
/// <param name="Allowed">เลือกได้ไหม</param>
/// <param name="BlockedReason">เหตุที่เลือกไม่ได้ + ทางไปต่อ (null เมื่อเลือกได้)</param>
/// <param name="OffersReceipt">รอบ 265 (คำตัดสิน 265-1-5): หลังบันทึก หน้ารับชำระเสนอ "ออกใบเสร็จส่วนที่เกินเลย" (ใบเสร็จจากเงินรอจัดสรร ไม่ต้องมีใบแจ้งหนี้) —
/// เซิร์ฟเวอร์ตัดสิน หน้าเว็บไม่เทียบชื่อเหตุผลเอง</param>
public sealed record PaymentExcessOption(string Reason, string Label, string Hint, bool Allowed, string? BlockedReason, bool OffersReceipt = false);

/// <summary>ใบค้างอื่นของลูกค้ารายเดียวกันที่เสนอให้ผูกเงินเกิน (เก่าสุดก่อน)</summary>
public sealed record PaymentExcessSplitRow(Guid DocumentId, decimal BalanceDue, decimal Suggested);

/// <summary>
/// **ตัวตัดสิน "เงินรับมากกว่ายอดค้างของใบ" — ตัวเดียว** (รอบ 258 · DECISIONS 258-1-2/3/8)
/// <para>ตอบ 3 เรื่อง: (1) เงินเกินเท่าไร (<see cref="Excess"/> — ใช้ด่านเดิม <see cref="DocumentSettlementState.WouldOverpay"/> ⇒ ยอดพอดี/เกินไม่เกิน 0.01 = ไม่มีเงินเกิน
/// พฤติกรรมเดิมทุกสตางค์) · (2) เก็บส่วนเกินเป็น "เงินรอจัดสรร" ได้ไหม (<see cref="RefuseExcess"/> — ผู้เขียน <c>CreateMultiDocPaymentAsync</c>
/// และพรีวิวของหน้าเว็บเรียกตัวเดียวกัน) · (3) เหตุผลที่เลือกได้ (<see cref="ReasonOptions"/>) และการกระจายให้ใบค้างอื่น (<see cref="SuggestSplit"/>)</para>
/// <para>ไม่เดา VAT ของเงินที่ยังไม่จำแนก — หน้าที่ภาษีถูกบังคับผ่าน "เหตุผล" (หลักการ F2 ข้อ 3)</para>
/// </summary>
public static class PaymentExcessPlan
{
    public const string RuleCode = "CC-EXCESS";
    public const string LegalReference = "ป.รัษฎากร ม.78/1 · ม.86";

    /// <summary>เงินเกินยอดค้าง (0 = ไม่เกิน — รวมกรณีเกินไม่เกิน <see cref="DocumentSettlementState.PaymentOvershootAllowance"/>)</summary>
    public static decimal Excess(decimal amount, decimal fee, decimal balanceDue)
    {
        var closing = amount + fee;
        if (!DocumentSettlementState.WouldOverpay(balanceDue, closing)) return 0m;
        return Math.Round(closing - balanceDue, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// เหตุที่เก็บส่วนเกินเป็นเงินรอจัดสรร<b>ไม่ได้</b> (null = ได้) — ข้อความพร้อมทางไปต่อ
    /// </summary>
    /// <param name="receivableSide">ฝั่งรับเงินจากลูกค้า (false = จ่ายเจ้าหนี้ — ปิดไว้ก่อน 258-1-8)</param>
    /// <param name="isThb">เอกสารและการชำระเป็นสกุลบาทอัตรา 1</param>
    /// <param name="hasFee">มีค่าธรรมเนียมหักจากยอดโอน</param>
    /// <param name="vatRegistered">บริษัทจด VAT</param>
    /// <param name="reason">เหตุผลที่ผู้ใช้เลือก (null = ยังไม่เลือก)</param>
    public static string? RefuseExcess(bool receivableSide, bool isThb, bool hasFee, bool vatRegistered, UnappliedReason? reason,
        bool hasAdjustments = false)
    {
        // ฝ่ายค้านรอบ 258 M2: บรรทัดปรับส่วนต่างผูกกับหนี้ใบเดียว — มีเงินเกินด้วย ⇒ ปฏิเสธ (เดิมแผงเงินเกินทิ้งบรรทัดปรับเงียบ ๆ)
        if (hasAdjustments)
            return "การรับเงินเกินพร้อมบรรทัดปรับส่วนต่าง (คูปอง/ค่าส่ง/ส่วนลด) ยังไม่รองรับในรายการเดียว — บันทึกรับชำระใบนี้พร้อมบรรทัดปรับเท่ายอดค้างก่อน "
                 + "แล้วบันทึกเงินส่วนเกินเป็นอีกรายการ";
        if (!receivableSide)
            return "ยอดจ่ายมากกว่ายอดที่จัดสรรให้เอกสาร — ฝั่งจ่ายเจ้าหนี้ยังไม่รองรับเงินจ่ายเกิน/จ่ายล่วงหน้ารอจัดสรร · "
                 + "บันทึกจ่ายเท่ายอดค้างของใบ แล้วบันทึกเงินจ่ายล่วงหน้าเป็นใบสำคัญจ่ายแยก";
        if (!isThb)
            return "เงินเกินของเอกสารสกุลเงินต่างประเทศยังเก็บเป็นเงินรอจัดสรรไม่ได้ — บันทึกรับเท่ายอดค้างของใบ "
                 + "แล้วบันทึกเงินส่วนที่เหลือเป็นใบเสร็จมัดจำ/เงินรับล่วงหน้าแยก";
        if (hasFee)
            return "การรับเงินเกินพร้อมค่าธรรมเนียมที่ถูกหักยังไม่รองรับในรายการเดียว — บันทึกรับชำระใบนี้ (พร้อมค่าธรรมเนียม) เท่ายอดค้างก่อน "
                 + "แล้วบันทึกเงินส่วนเกินเป็นอีกรายการ";
        if (reason == null)
            return "ยอดรับมากกว่ายอดที่จัดสรรให้เอกสาร — เลือกเหตุผลของเงินส่วนเกิน (ค้างออกใบ / โอนเกินรอคืน / ยังไม่ทราบ) ก่อนบันทึก "
                 + "ระบบจะเก็บเป็น “เงินรอจัดสรร” ให้ผูกกับใบที่ออกทีหลังได้";
        if (reason == UnappliedReason.Advance && vatRegistered)
            return AdvanceVatBlocked;
        return null;
    }

    /// <summary>258-1-3: บริษัทจด VAT ห้ามค้างเงินรับล่วงหน้า/มัดจำไว้ในเงินรอจัดสรร (ภาษีถึงกำหนดตอนรับเงิน §78/1 — ต้องออกใบมัดจำ)</summary>
    public const string AdvanceVatBlocked =
        "เงินรับล่วงหน้า/มัดจำของกิจการจดทะเบียนภาษีมูลค่าเพิ่มต้องออกใบเสร็จมัดจำ (ภาษีถึงกำหนดเมื่อรับเงิน — มาตรา 78/1) "
        + "เก็บเป็นเงินรอจัดสรรไม่ได้ · บันทึกรับชำระใบนี้เท่ายอดค้าง แล้วออก “ใบเสร็จรับเงิน (มัดจำ)” ของเงินส่วนที่เหลือ";

    /// <summary>เหตุผลที่เสนอให้เลือก (เรียงตามที่ใช้บ่อย) — ตัวเดียวกับที่ <see cref="RefuseExcess"/> ตัดสิน</summary>
    public static IReadOnlyList<PaymentExcessOption> ReasonOptions(bool vatRegistered) => new[]
    {
        new PaymentExcessOption(nameof(UnappliedReason.UninvoicedSupply), Label(UnappliedReason.UninvoicedSupply),
            "ส่งของ/ให้บริการแล้วแต่ยังไม่ได้ออกใบ — บันทึกแล้วออกใบเสร็จส่วนนี้ได้ทันที (ไม่ต้องสร้างใบแจ้งหนี้) หรือออกใบแล้วกด “ใช้เงินรอจัดสรร” ที่ใบนั้น"
            + (vatRegistered ? " · ค่าบริการ: ภาษีถึงกำหนดตั้งแต่วันรับเงิน ควรออกใบกำกับภายในเดือนเดียวกัน" : ""), true, null, OffersReceipt: true),
        new PaymentExcessOption(nameof(UnappliedReason.Overpaid), Label(UnappliedReason.Overpaid),
            "ลูกค้าโอนเกิน — เก็บไว้รอคืนเงิน หรือผูกกับใบถัดไปของลูกค้ารายนี้", true, null),
        new PaymentExcessOption(nameof(UnappliedReason.Advance), Label(UnappliedReason.Advance),
            "ลูกค้าจ่ายล่วงหน้าสำหรับงานที่ยังไม่เกิด", !vatRegistered, vatRegistered ? AdvanceVatBlocked : null),
        new PaymentExcessOption(nameof(UnappliedReason.Unknown), Label(UnappliedReason.Unknown),
            "ยังไม่รู้ว่าเงินส่วนนี้เป็นค่าอะไร — จะขึ้นในรายการ “เงินรอจัดสรร” ให้ติดตาม", true, null),
    };

    /// <summary>ป้ายภาษาไทยของเหตุผล (ตัวเดียวของทุกหน้า/รายงาน)</summary>
    public static string Label(UnappliedReason reason) => reason switch
    {
        UnappliedReason.UninvoicedSupply => "ค้างออกใบแจ้งหนี้/ใบกำกับ",
        UnappliedReason.Advance => "เงินรับล่วงหน้า",
        UnappliedReason.Overpaid => "ลูกค้าโอนเกิน (รอคืน)",
        _ => "ยังไม่ทราบ",
    };

    /// <summary>ป้ายจากค่าที่เก็บ (ชื่อ enum) — ค่าที่ไม่รู้จัก/ว่าง = "ยังไม่ทราบ"</summary>
    public static string LabelOf(string? stored)
        => Enum.TryParse<UnappliedReason>(stored, ignoreCase: false, out var r) && Enum.IsDefined(r) ? Label(r) : Label(UnappliedReason.Unknown);

    /// <summary>กระจายเงินเกินให้ใบค้างอื่น (ลำดับที่ส่งมา = เก่าสุดก่อน) — เต็มยอดค้างทีละใบจนเงินหมด · ผลรวมที่เสนอ ≤ เงินเกิน</summary>
    public static IReadOnlyList<PaymentExcessSplitRow> SuggestSplit(decimal excess, IEnumerable<(Guid DocumentId, decimal BalanceDue)> openOldestFirst)
    {
        var left = Math.Max(0m, excess);
        var rows = new List<PaymentExcessSplitRow>();
        foreach (var (id, balance) in openOldestFirst)
        {
            var take = balance <= 0m ? 0m : Math.Min(left, balance);
            take = Math.Round(take, 2, MidpointRounding.AwayFromZero);
            rows.Add(new PaymentExcessSplitRow(id, balance, take));
            left -= take;
        }
        return rows;
    }

    /// <summary>
    /// ฝ่ายค้านรอบ 258 M2: เงินส่วนเกินที่หน้าจอแสดงให้ผู้ใช้ยืนยัน ต้องเท่ากับที่เซิร์ฟเวอร์คิดจากคำขอจริง — ไม่ตรง = ปฏิเสธ (ไม่บันทึกยอดที่ผู้ใช้ไม่ได้เห็น) ·
    /// null = ตรง หรือผู้เรียกไม่ส่งมา (API เดิม)
    /// </summary>
    public static string? ExpectedExcessMismatch(decimal? expectedExcess, decimal actualExcess)
        => expectedExcess is decimal expected && Math.Abs(expected - actualExcess) > UnappliedCreditBalance.Tolerance
            ? "เงินส่วนเกินที่หน้าจอแสดง (" + expected.ToString("#,##0.00", CultureInfo.InvariantCulture) + ") ไม่ตรงกับที่ระบบคำนวณจากรายการนี้ ("
              + actualExcess.ToString("#,##0.00", CultureInfo.InvariantCulture) + ") — ปิดหน้าต่างแล้วเปิดบันทึกรับชำระใหม่"
            : null;

    /// <summary>ป้ายบนหมายเหตุภายในของใบที่ถูกปิดยอดแล้วแต่<b>ยังไม่ได้ออกใบเสร็จรับเงิน/ใบกำกับภาษี</b>ของการรับเงินนั้น (ฝ่ายค้านรอบ 258 H2) —
    /// ตัวอ่านคิวงาน: <c>CustomerCreditService.ReceiptPendingAsync</c></summary>
    public const string ReceiptPendingMarker = "[RECEIPT-PENDING]";

    /// <summary>ข้อความที่เก็บบนใบ (ไม่ใช่แค่ toast) เมื่อออกใบเสร็จ/ใบกำกับอัตโนมัติของการรับเงินไม่ได้ — พร้อมทางไปต่อ</summary>
    public static string ReceiptPendingNote(string documentNumber, string paymentNumber, string why)
        => $"{ReceiptPendingMarker} {documentNumber}: ถูกปิดยอดด้วยการรับชำระ {paymentNumber} แล้ว แต่ยังไม่ได้ออกใบเสร็จรับเงิน/ใบกำกับภาษีของเงินก้อนนี้ ({why}) — "
           + "เปิดใบนี้แล้วกด “ออกใบเสร็จ” ที่ประวัติการชำระ (มาตรา 86 · 105)";

    /// <summary>ข้อความหัวแผงเงินเกินบนหน้ารับชำระ</summary>
    public static string Headline(decimal excess)
        => "เงินรับมากกว่ายอดค้างของใบนี้ " + excess.ToString("#,##0.00", CultureInfo.InvariantCulture) + " บาท — เลือกวิธีจัดการเงินส่วนเกิน";
}
