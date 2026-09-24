using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัดปรับ "ส่วนต่างระหว่างยอดหนี้กับเงินที่จ่ายจริง" ตอนชำระเงิน
/// <para><paramref name="Amount"/> มีเครื่องหมาย: <b>บวก</b> = จ่ายเกินยอดหนี้ด้วยรายการนี้ (Dr บัญชีนั้น — เช่น ค่าจัดส่ง
/// +37 ที่แพลตฟอร์มเก็บแต่ไม่อยู่ในใบกำกับ) · <b>ลบ</b> = ปิดหนี้โดยไม่ต้องจ่ายเงิน (Cr บัญชีนั้น — เช่น คูปองแพลตฟอร์ม −135
/// ลดต้นทุนซื้อ) ⇒ เงินที่จ่ายจริง = ยอดหนี้ที่ปิด + Σ Amount</para></summary>
/// <param name="AccountCode">รหัสผังบัญชี (ผังของบริษัท) — ต้องมีอยู่จริงตอนบันทึก</param>
/// <param name="Amount">ยอดมีเครื่องหมาย (ห้ามเป็นศูนย์)</param>
/// <param name="Reason">เหตุผล/ป้ายบนกระดาษ (เช่น "ค่าจัดส่ง" · "Shopee Voucher") — ลง audit + คำอธิบาย JE</param>
public sealed record SettlementAdjustmentLine(string AccountCode, decimal Amount, string? Reason);

/// <summary>ผลตรวจชุดบรรทัดปรับของการชำระหนึ่งครั้ง</summary>
/// <param name="Ok">บันทึกได้ไหม</param>
/// <param name="SettledAmount">ยอดหนี้ที่การชำระครั้งนี้ปิด = เงินที่จ่าย − Σ บรรทัดปรับ</param>
/// <param name="AdjustmentNet">ยอดหนี้ที่ปิดด้วยบรรทัดปรับ (ไม่ใช่เงินสด) = SettledAmount − เงินที่จ่าย</param>
/// <param name="Error">เหตุผลภาษาไทยพร้อมทางไปต่อ (null เมื่อผ่าน)</param>
public readonly record struct SettlementAdjustmentCheck(bool Ok, decimal SettledAmount, decimal AdjustmentNet, string? Error);

/// <summary>
/// **ส่วนต่าง "ยอดตามใบกำกับ ↔ เงินที่จ่ายจริง" ลงที่ขั้นชำระเงิน — ตัวตัดสินตัวเดียว** (pure · ไม่ throw · ไม่มี I/O)
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 1 · 3 · 4) ═══
/// <para>ใบกำกับภาษีซื้อ Shopee: ยอดตามใบ <b>536.00</b> (VAT 35.07 เคลมเต็ม — ข้อ 2 คงเดิม) แต่จ่ายจริง 438.00 เพราะ
/// "ค่าจัดส่ง +37 · Shopee Voucher −135" พิมพ์หลังยอดรวมทั้งสิ้น ⇒ <b>ห้ามแก้ยอดเอกสาร</b> (ใบกำกับ = เอกสารตั้งหนี้ตามใบ)
/// ส่วนต่างจัดการที่ขั้นชำระ: ค่าส่ง → <see cref="FreightInAccountCode"/> · คูปองแพลตฟอร์ม/ส่วนลดพิเศษที่ไม่มีรายละเอียด →
/// <see cref="PurchaseDiscountAccountCode"/> (ลดต้นทุน — TFRS for NPAEs บทที่ 8) ⇒ หนี้เหลือ 0</para>
/// <code>
/// Dr เจ้าหนี้ (ปิดหนี้ตามใบ)      536.00      Cr เงินสด/ธนาคาร (จ่ายจริง)       438.00
/// Dr 51120 ค่าขนส่งสินค้า          37.00      Cr 51150 ส่วนลดรับ (สินค้า)       135.00
/// </code>
///
/// <para><b>สองทางเข้า ตัวตัดสินตัวเดียว</b>: (1) เอกสารตั้งหนี้ (ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย) ⇒ บรรทัดปรับอยู่บน<b>การชำระ</b>
/// (<c>CreatePaymentAsync</c>) · (2) ใบสำคัญจ่ายที่จ่ายในตัว ⇒ "การชำระ" คือตัวเอกสารเอง บรรทัดปรับเป็น adjusting lines
/// ของเอกสาร และขาเงินสดถูกลดด้วย <see cref="DocumentCashDelta"/> ตอนลง JE — ทั้งสองทางใช้ <see cref="CheckDocumentLines"/>
/// / <see cref="Check"/> ชุดเดียวกัน</para>
/// </summary>
public static class PaymentSettlementAdjustment
{
    /// <summary>51120 ค่าขนส่งสินค้า (Freight-in) — ผังมาตรฐาน <c>ChartOfAccountTemplates</c></summary>
    public const string FreightInAccountCode = "51120";

    /// <summary>51150 ส่วนลดรับ (สินค้า) — contra-purchase ลดต้นทุน (ไม่ใช่ 43060 ส่วนลดเงินสดหมวดรายได้)</summary>
    public const string PurchaseDiscountAccountCode = "51150";

    /// <summary>รหัสกฎสำหรับ audit/คำอธิบาย JE</summary>
    public const string RuleCode = "SETTLE-ADJ-PAY≠TOTAL";

    private const decimal Tol = DocumentSettlementState.Tolerance;

    /// <summary>ค่าเผื่อ<b>ตัวเดียว</b>ของ "ข้อเสนอลงตัวไหม / ยอดข้อเสนอตรงยอดเอกสารไหม / บรรทัดปรับอธิบายส่วนต่างพอดีไหม"
    /// — ฝ่ายค้าน P3 รอบ 193: เดิมข้อเสนอใช้ 0.02 (OcrPaperAmounts.ExactTol) แต่ AutoPost ตรวจที่ 0.005 ⇒ ข้อเสนอที่ต่าง 0.01–0.02
    /// ปลด [PAY≠TOTAL] แล้วไปล้มตอนอนุมัติ · ตอนนี้ทุกจุดใช้ค่านี้ (= <see cref="DocumentSettlementState.Tolerance"/>)</summary>
    public const decimal MatchTolerance = Tol;

    /// <summary>ตรวจบรรทัดปรับของการชำระ (ทางเข้า 1) — เงินที่จ่าย + บรรทัดปรับ ต้องปิดหนี้ได้ไม่เกินยอดค้าง</summary>
    /// <param name="cashPaid">เงินที่จ่ายจริงของการชำระครั้งนี้ (<c>Payment.Amount</c>)</param>
    /// <param name="balanceDue">ยอดค้างของเอกสารก่อนการชำระครั้งนี้</param>
    /// <param name="lines">บรรทัดปรับ (ว่าง/null = ไม่มี ⇒ ผ่านเสมอ และยอดที่ปิด = เงินที่จ่าย — พฤติกรรมเดิม)</param>
    public static SettlementAdjustmentCheck Check(
        decimal cashPaid, decimal balanceDue, IReadOnlyList<SettlementAdjustmentLine>? lines)
    {
        if (lines is null || lines.Count == 0)
            return new(true, cashPaid, 0m, null);

        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.AccountCode))
                return Fail(cashPaid, "บรรทัดปรับต้องระบุผังบัญชี (เช่น 51120 ค่าขนส่งสินค้า · 51150 ส่วนลดรับ (สินค้า))");
            if (l.Amount == 0m)
                return Fail(cashPaid, $"บรรทัดปรับ {l.AccountCode} ยอดเป็นศูนย์ — ลบบรรทัดนั้นทิ้ง หรือใส่ยอด (+ จ่ายเกิน · − ปิดหนี้โดยไม่จ่ายเงิน)");
            if (decimal.Round(l.Amount, 2) != l.Amount)
                return Fail(cashPaid, $"บรรทัดปรับ {l.AccountCode} ต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง");
            if (IsMoneyAccountCode(l.AccountCode))
                return Fail(cashPaid, $"บรรทัดปรับใช้ผังเงินสด/เงินฝาก ({l.AccountCode}) ไม่ได้ — เงินที่จ่ายจริงใส่ในช่อง \"จำนวนเงิน\" "
                    + "(ระบบลงขาเงินสดและยอดบัญชีธนาคารให้) · บรรทัดปรับใช้กับค่าส่ง (51120) / คูปอง-ส่วนลด (51150) ฯลฯ");
        }

        var net = lines.Sum(l => l.Amount);
        var settled = cashPaid - net;
        if (settled <= 0m)
            return Fail(cashPaid,
                $"เงินที่จ่าย {cashPaid:N2} กับบรรทัดปรับรวม {Signed(net)} ไม่ได้ปิดหนี้เลย (ยอดที่ปิด {settled:N2}) — ตรวจเครื่องหมายของบรรทัดปรับ");
        if (DocumentSettlementState.WouldOverpay(balanceDue, settled))
            return Fail(cashPaid,
                $"เงินที่จ่าย {cashPaid:N2} {(net >= 0 ? "หัก" : "บวก")}บรรทัดปรับ {Math.Abs(net):N2} = ปิดหนี้ {settled:N2} "
                + $"เกินยอดค้างชำระ {balanceDue:N2} — ตรวจยอดคูปอง/ค่าส่งกับกระดาษอีกครั้ง");

        return new(true, settled, settled - cashPaid, null);
    }

    /// <summary>เอกสารชนิดที่ "จ่ายเงินในตัว" ตอนอนุมัติ (ขาเงินสดอยู่ใน JE ของเอกสาร) ซึ่งยอดชำระจริงเปลี่ยนขาเงินสดได้
    /// — ตัดสินจาก<b>สาขา JE ที่ลงเงินสดจริง</b> (AutoPostToJournalAsync) ไม่ใช่จาก PaymentType อย่างเดียว:
    /// <list type="bullet">
    /// <item>ใบสำคัญจ่ายที่ปิดหนี้ของเอกสารต้นทาง (<paramref name="settlesSourceDocument"/> = มี RelatedDocumentId — เส้นแปลง
    ///   ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย → ใบสำคัญจ่าย) ⇒ ลง Dr เจ้าหนี้ / Cr เงินสด <b>เสมอ</b> แม้ PaymentType จะเป็น Credit
    ///   (ค่าเริ่มต้นของเส้นแปลง) — ฝ่ายค้าน C1 รอบ 193: เดิมตอบ false ⇒ "ยอดชำระจริง" ถูกข้ามเงียบ JE ลงเงินสด 536 แทน 438</item>
    /// <item>ใบสำคัญจ่ายเดี่ยวที่ไม่ใช่เงินเชื่อ และไม่ใช่บริการต่างประเทศ (ขาเงินสดของ ภ.พ.36 เป็นฐานเท่านั้น)</item>
    /// </list></summary>
    public static bool PostsCashAtApproval(
        DocumentType type, PaymentType? paymentType, bool isForeignService, bool settlesSourceDocument)
        => type == DocumentType.PaymentVoucher
           && (settlesSourceDocument || (paymentType != PaymentType.Credit && !isForeignService));

    /// <summary>ผังหมวดเงินสดและรายการเทียบเท่าเงินสด (111xx — เงินสด · เงินสดย่อย · เงินฝาก · เช็คในมือ) ห้ามเป็นบรรทัดปรับ
    /// — ฝ่ายค้านรอบสอง P-d: บรรทัดปรับผังเงินสด −98 กับเงิน 0 ⇒ GL เงินออก 98 แต่ Payment.Amount = 0 และยอดบัญชีธนาคารไม่ขยับ
    /// (ผังที่ผูกกับบัญชีธนาคารแต่รหัสไม่ขึ้นต้น 111 ถูกตรวจเพิ่มที่ service)</summary>
    public static bool IsMoneyAccountCode(string? accountCode)
        => (accountCode ?? "").Trim().StartsWith("111", StringComparison.Ordinal);

    /// <summary>ส่วนต่างที่ขาเงินสดของ JE ต้องถูก<b>ลด</b> (Dr เงินสดกลับ) = ยอดเอกสาร − ยอดชำระจริง ·
    /// 0 เมื่อไม่ได้ระบุยอดชำระจริง หรือเอกสารชนิดนี้ไม่จ่ายเงินในตัว (พฤติกรรมเดิมทุกประการ)</summary>
    public static decimal DocumentCashDelta(
        DocumentType type, PaymentType? paymentType, bool isForeignService, bool settlesSourceDocument,
        decimal totalAmount, decimal? actualPaidAmount)
    {
        if (actualPaidAmount is not decimal paid
            || !PostsCashAtApproval(type, paymentType, isForeignService, settlesSourceDocument))
            return 0m;
        return totalAmount - paid;
    }

    /// <summary>ช่อง "ยอดชำระจริง" <b>ใช้ไม่ได้</b>กับเอกสารนี้ไหม — คืนเหตุผลพร้อมทางไปต่อ (null = ใช้ได้)
    /// <para>ใช้ได้เมื่อ: เอกสารจ่ายเงินในตัว (<see cref="PostsCashAtApproval"/>) ⇒ ลดขาเงินสดตอนอนุมัติ ·
    /// หรือเอกสารตั้งหนี้ฝั่งซื้อ (ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย) ⇒ เป็นยอดเสนอในหน้าบันทึกการชำระ (บรรทัดปรับอยู่ที่การชำระ)</para>
    /// <para>ที่เหลือ (ใบสำคัญจ่ายเงินเชื่อแบบเก่า · บริการต่างประเทศ · ฝั่งขาย) ⇒ ค่าไม่มีผลที่ไหนเลย = silent no-op (กฎ #4 A)
    /// ⇒ ด่านบันทึก/อนุมัติต้องบอกผู้ใช้ ไม่ใช่ปล่อยให้ JE ลงยอดเต็มเงียบ ๆ</para></summary>
    public static string? ActualPaidNotApplicableReason(
        DocumentType type, PaymentType? paymentType, bool isForeignService, bool settlesSourceDocument)
    {
        if (PostsCashAtApproval(type, paymentType, isForeignService, settlesSourceDocument)) return null;
        // ฝ่ายค้านรอบสอง P-e: ใบตั้งหนี้แบบ "จ่ายทันที" ปิดยอดตั้งแต่สร้าง (BalanceDue = 0) ⇒ ไม่มีขั้นบันทึกการชำระให้ใช้ค่านี้
        if (type is DocumentType.PurchaseInvoice or DocumentType.Expense)
            return paymentType == PaymentType.Cash
                ? "ช่อง \"ยอดชำระจริง\" ใช้กับใบตั้งหนี้แบบจ่ายทันทีไม่ได้ (ปิดยอดตั้งแต่สร้าง ไม่มีขั้นบันทึกการชำระ) — "
                  + "ล้างช่องนี้ แล้วบันทึกการจ่ายด้วยใบสำคัญจ่าย (ช่องยอดชำระจริง + บรรทัดปรับ) หรือเปลี่ยนเป็นเงินเชื่อแล้วบันทึกการชำระ"
                : null;
        return type == DocumentType.PaymentVoucher
            ? (isForeignService
                ? "ช่อง \"ยอดชำระจริง\" ใช้กับใบสำคัญจ่ายบริการต่างประเทศ (ภ.พ.36) ไม่ได้ — ขาเงินสดเป็นฐานภาษีเท่านั้น · "
                  + "ล้างช่องนี้ แล้วบันทึกส่วนต่าง (ค่าส่ง/คูปอง) ด้วยสมุดรายวันทั่วไป"
                : "ช่อง \"ยอดชำระจริง\" ใช้กับใบสำคัญจ่ายแบบเงินเชื่อไม่ได้ (ไม่มีเงินออกตอนอนุมัติ) — "
                  + "ล้างช่องนี้ หรือเปลี่ยนเป็นจ่ายทันที")
            : $"ช่อง \"ยอดชำระจริง\" ใช้ได้เฉพาะเอกสารจ่ายเงินฝั่งซื้อ (ใบสำคัญจ่าย · ใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย) — "
              + $"เอกสารชนิด {type} ไม่มีขาเงินสดให้ปรับ · ล้างช่องนี้";
    }

    /// <summary>adjusting lines ของเอกสาร (Dr รวม/Cr รวม) ต้องอธิบายส่วนต่างยอดชำระจริงได้พอดี:
    /// <c>Dr − Cr + ส่วนต่าง = 0</c> — ส่วนต่าง 0 = กติกาเดิม "adjusting lines ต้อง net-zero"
    /// · คืนข้อความพร้อมทางไปต่อเมื่อไม่ลงตัว (null = ผ่าน)</summary>
    /// <param name="cashDelta">ผลของ <see cref="DocumentCashDelta"/></param>
    /// <param name="adjustingDebit">Σ เดบิตของ adjusting lines</param>
    /// <param name="adjustingCredit">Σ เครดิตของ adjusting lines</param>
    /// <param name="totalAmount">ยอดเอกสาร (ใช้ในข้อความ)</param>
    /// <param name="actualPaidAmount">ยอดชำระจริง (ใช้ในข้อความ)</param>
    public static string? CheckDocumentLines(
        decimal cashDelta, decimal adjustingDebit, decimal adjustingCredit, decimal totalAmount, decimal? actualPaidAmount)
    {
        var gap = Math.Round(adjustingDebit - adjustingCredit + cashDelta, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(gap) <= Tol) return null;
        if (cashDelta == 0m)
            return $"⛔ Adjusting JE Lines ไม่ balance: เดบิต {adjustingDebit:N2} ≠ เครดิต {adjustingCredit:N2} — "
                + "ผลรวม Dr และ Cr ของ adjusting lines ต้องเท่ากัน (กัน JE หลักเสียสมดุล). "
                + "แก้ที่หน้า 'ปรับปรุงรายการบัญชี' ของเอกสารก่อนอนุมัติ";
        return $"⛔ ยอดชำระจริง {actualPaidAmount:N2} ต่างจากยอดเอกสาร {totalAmount:N2} อยู่ {Signed(-cashDelta)} "
            + $"แต่บรรทัดปรับ (Dr {adjustingDebit:N2} · Cr {adjustingCredit:N2}) อธิบายได้ {Signed(adjustingDebit - adjustingCredit)} "
            + $"— ขาดอีก {Signed(-gap)} · เพิ่ม/แก้บรรทัดปรับในหน้า 'ปรับปรุงรายการบัญชี' "
            + $"(ค่าส่งที่จ่ายเพิ่ม = เดบิต {FreightInAccountCode} · คูปอง/ส่วนลดหลังยอดรวม = เครดิต {PurchaseDiscountAccountCode}) "
            + "หรือล้างช่อง \"ยอดชำระจริง\" ถ้าจ่ายเต็มตามใบ";
    }

    /// <summary>แปลงบรรทัดปรับเป็นขา JE (เดบิต, เครดิต) — บวก = เดบิต · ลบ = เครดิต</summary>
    public static (decimal Debit, decimal Credit) JournalSide(decimal signedAmount)
        => signedAmount >= 0m ? (signedAmount, 0m) : (0m, -signedAmount);

    private static SettlementAdjustmentCheck Fail(decimal cashPaid, string why) => new(false, cashPaid, 0m, why);

    private static string Signed(decimal v) => v >= 0m ? $"+{v:N2}" : $"−{Math.Abs(v):N2}";
}
