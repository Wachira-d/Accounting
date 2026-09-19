using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>ทิศของเงินเทียบกับ "บัญชีธนาคารของเรา"</summary>
public enum BankFlowDirection
{
    /// <summary>**ไม่รู้ทิศ** — ต้องเป็นค่าใน enum เสมอ ห้ามแปลงเป็น "บวก" เงียบ ๆ
    /// (`DECISION_DOCTRINE` §1 G3 · `DECISION_AUDIT` §2 R2)</summary>
    Unknown = 0,
    /// <summary>เงินเข้าบัญชี (ฝาก/ดอกเบี้ย/ใบเสร็จรับเงิน)</summary>
    Inflow = 1,
    /// <summary>เงินออกจากบัญชี (ถอน/ใบสำคัญจ่าย)</summary>
    Outflow = 2,
}

/// <summary>
/// **ตัวกระทบยอด "ผลรวมรายการที่จับคู่ ↔ ยอดบนบรรทัดธนาคาร" ตัวเดียว** (pure).
///
/// ย้ายเลขคณิตออกมาจาก `BankService.ValidateMatchAmountAsync` (`BankService.cs:401-511`)
/// เพื่อให้ทดสอบได้ และปิดช่องสองช่องที่ `DECISION_AUDIT_2026-09-18.md` §3 D4-5 ระบุ:
///
/// **(1) ชนิดที่ไม่รู้ทิศ → "additive (legacy safe default)"**
/// ของเดิมเจอ `DocumentType` ที่ไม่อยู่ในลิสต์เข้า/ออก แล้ว **บวก**เข้าไปเงียบ ๆ
/// ⇒ ใบที่ควรหักกลับไปบวก ยอดเลยบังเอิญตรงได้ และไม่มีใครรู้ว่าระบบเดาทิศ.
/// ตอนนี้ **ปฏิเสธพร้อมบอกชนิด** ให้ผู้ใช้เลือกเอกสารใหม่หรือใช้กลุ่ม M:N
///
/// **(2) "ผ่านถ้า net หรือ gross ตรง" = สองโอกาสผ่านต่อใบ**
/// ของเดิมนับขนานสองชุด (net จากบรรทัดบัญชีธนาคาร + gross จาก `TotalDebit`)
/// แล้วผ่านถ้าชุดใดชุดหนึ่งตรง ⇒ ใบ Makro ที่ gross 7,490 / net 7,280 ผ่านได้
/// **ทั้งสองยอด** (บรรทัดธนาคารจะเป็น 7,490 หรือ 7,280 ก็ผ่าน) ทั้งที่เงินจริง
/// ออกจากธนาคารได้ยอดเดียว. ตอนนี้ทุกใบมี **ยอดเงินสดที่คาดว่าผ่านธนาคารค่าเดียว**:
///
/// <code>
///   ถ้ารู้ยอดบนบรรทัดบัญชีธนาคารของ JE  → ใช้ค่านั้น (คือเงินที่ขยับจริง)
///   ถ้าไม่รู้ (แถว Payment)             → RecordedAmount − WithheldAmount − FeeAmount
/// </code>
///
/// **ทำไมไม่ใช้ `WhtRecognitionBasis` เลือกระหว่าง net/gross**: basis เปลี่ยน
/// *จังหวะรับรู้* ภาษีหัก ณ ที่จ่าย (ตอนจ่าย vs ตอนตั้งหนี้) ไม่ได้เปลี่ยนว่า
/// **เงินสดออกจากธนาคารเท่าไร** — `Payment.WithholdingTaxAmount` เก็บยอดที่ถูกหัก
/// ของงวดนั้นทั้งสองเกณฑ์ (ดู doc-comment ของ field ใน `Payment.cs`) ดังนั้น
/// "recorded − withheld − fee" เป็นคำตอบเดียวกันทั้ง Cash และ Accrual
/// ⇒ การผูกกับ basis จะเป็นพารามิเตอร์ที่ไม่มีผล (มี ≠ ถูกเรียก, F2 ข้อ 2)
/// </summary>
public static class BankMatchAmountReconciler
{
    /// <summary>ความคลาดเคลื่อนเริ่มต้น — 1 สตางค์ (เศษปัดเศษ)</summary>
    public const decimal DefaultTolerance = 0.01m;

    /// <summary>รายการ 1 ใบที่ถูกจับคู่กับบรรทัดธนาคาร</summary>
    /// <param name="Id">id ของ Payment / JournalEntry</param>
    /// <param name="Label">ข้อความที่ผู้ใช้อ่านรู้เรื่องเวลาโดนปฏิเสธ เช่น "ใบเสร็จ RV-0001"</param>
    /// <param name="Direction">ทิศของรายการ — `Unknown` = ระบบไม่รู้ ต้องปฏิเสธ</param>
    /// <param name="RecordedAmount">ยอดที่บันทึกไว้ (Payment.Amount / JE TotalDebit)</param>
    /// <param name="BankLineAmount">|ยอดสุทธิที่ลงบัญชีธนาคาร| — รู้เฉพาะ JE ที่แตะบัญชีธนาคาร; null = ไม่รู้</param>
    /// <param name="WithheldAmount">ภาษีหัก ณ ที่จ่ายที่หักจากยอดนี้ (เงินที่ไม่เคยออกจากธนาคาร)</param>
    /// <param name="FeeAmount">ค่าธรรมเนียมที่ถูกหักจากยอดโอน (marketplace/gateway/ธนาคาร)</param>
    /// <param name="ConversionRate">
    /// อัตราแปลง "สกุลของรายการ → สกุลของบัญชีธนาคาร" จาก
    /// <see cref="BankMatchCurrency"/>. **1 = สกุลเดียวกัน** (ค่าปกติ).
    /// ⚠ `BankLineAmount` เป็นยอดที่ลงบัญชีแยกประเภทอยู่แล้ว (สกุลฐาน =
    /// สกุลของบัญชีธนาคาร) ⇒ ผู้เรียกต้องส่ง `ConversionRate = 1` คู่กับมันเสมอ
    /// มิฉะนั้นจะแปลงซ้ำ
    /// </param>
    public sealed record Item(
        Guid Id,
        string Label,
        BankFlowDirection Direction,
        decimal RecordedAmount,
        decimal? BankLineAmount = null,
        decimal WithheldAmount = 0m,
        decimal FeeAmount = 0m,
        decimal ConversionRate = 1m)
    {
        /// <summary>ยอดเงินสดที่ "ควรจะ" ผ่านบัญชีธนาคารสำหรับรายการนี้ — **ค่าเดียว**
        /// และอยู่ใน **สกุลของบัญชีธนาคาร** เสมอ (หักในสกุลเอกสารก่อน แล้วแปลงครั้งเดียว)</summary>
        public decimal ExpectedCashAmount
        {
            get
            {
                var inItemCurrency = BankLineAmount.HasValue
                    ? Math.Abs(BankLineAmount.Value)
                    : RecordedAmount - WithheldAmount - FeeAmount;
                if (ConversionRate == 1m) return inItemCurrency;
                return Math.Round(inItemCurrency * ConversionRate, 2, MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <param name="Ok">true = ยอดสองฝั่งตรงกันในกรอบ tolerance</param>
    /// <param name="ExpectedNet">ผลรวมสุทธิของรายการ (ฝั่งเดียวกันบวก ข้ามฝั่งหัก)</param>
    /// <param name="Difference">|ExpectedNet − BankAmount|</param>
    /// <param name="RuleCode">รหัสกฎที่ตัดสิน — ลง audit ได้</param>
    /// <param name="Message">ข้อความภาษาไทยที่บอกผู้ใช้ว่า **ทำอะไรต่อได้**</param>
    public sealed record Result(
        bool Ok,
        decimal ExpectedNet,
        decimal BankAmount,
        decimal Difference,
        string RuleCode,
        string? Message);

    /// <summary>
    /// กระทบยอด. ฝั่งเดียวกับบรรทัดธนาคาร = บวก, ข้ามฝั่ง = หัก
    /// (เช่น ใบเสร็จ 2,500 − ใบสำคัญจ่าย 500 = 2,000 สุทธิเข้าบัญชี)
    /// </summary>
    /// <param name="bankAmount">ยอดบนบรรทัดธนาคาร (ใช้ค่าสัมบูรณ์)</param>
    /// <param name="bankDirection">ทิศของบรรทัดธนาคาร</param>
    /// <param name="items">รายการที่ถูกจับคู่</param>
    /// <param name="tolerance">ความคลาดเคลื่อนที่ยอมรับ (ผ่าน <see cref="BankReconciliationTolerance"/> มาแล้ว)</param>
    public static Result Reconcile(
        decimal bankAmount,
        BankFlowDirection bankDirection,
        IReadOnlyList<Item> items,
        decimal tolerance = DefaultTolerance)
    {
        var target = Math.Abs(bankAmount);
        if (items == null || items.Count == 0)
            return new Result(true, 0m, target, target, "BANK-AMT-NO-ITEMS", null);

        if (bankDirection == BankFlowDirection.Unknown)
            return new Result(false, 0m, target, target, "BANK-AMT-BANK-DIR-UNKNOWN",
                "ไม่ทราบทิศของรายการธนาคาร (ฝาก/ถอน) — จับคู่ไม่ได้จนกว่าจะระบุประเภทรายการ");

        // ── ชนิดที่ไม่รู้ทิศ → ปฏิเสธพร้อมบอกว่าใบไหน (เดิมบวกเงียบ ๆ) ──
        var unknown = items.Where(i => i.Direction == BankFlowDirection.Unknown).ToList();
        if (unknown.Count > 0)
            return new Result(false, 0m, target, target, "BANK-AMT-ITEM-DIR-UNKNOWN",
                "ระบบไม่ทราบว่าเงินของรายการต่อไปนี้เข้าหรือออกจากบัญชี: "
                + string.Join(", ", unknown.Select(u => u.Label))
                + " — เลือกเอกสารที่ระบุทิศได้ (ใบเสร็จ/ใบสำคัญจ่าย/ใบแจ้งหนี้) "
                + "หรือใช้กลุ่มกระทบยอด M:N แล้วระบุยอดจัดสรรของแต่ละรายการเอง");

        decimal sameDir = 0m, oppDir = 0m;
        bool bankIsIn = bankDirection == BankFlowDirection.Inflow;
        foreach (var it in items)
        {
            bool itemIsIn = it.Direction == BankFlowDirection.Inflow;
            if (itemIsIn == bankIsIn) sameDir += it.ExpectedCashAmount;
            else oppDir += it.ExpectedCashAmount;
        }

        var net = sameDir - oppDir;
        var diff = Math.Abs(net - target);
        if (diff <= tolerance)
            return new Result(true, net, target, diff, "BANK-AMT-OK", null);

        // ข้อความต้องบอก "ทำอะไรต่อได้" ไม่ใช่แค่ว่าผิด (F2 ข้อ 8)
        var withheldTotal = items.Sum(i => i.WithheldAmount);
        var hint = withheldTotal <= 0m && Math.Abs(diff) > 0m
            ? " ถ้าส่วนต่างคือภาษีหัก ณ ที่จ่าย/ค่าธรรมเนียมที่ถูกหักจากยอดโอน "
              + "ให้บันทึกยอดหักไว้บนรายการชำระก่อน (ช่องภาษีหัก ณ ที่จ่าย / ค่าธรรมเนียม) "
              + "ระบบจะกระทบยอดให้เอง"
            : "";
        return new Result(false, net, target, diff, "BANK-AMT-MISMATCH",
            $"ยอดที่จับคู่ ({net:N2}) ไม่ตรงกับยอดธนาคาร ({target:N2}) — ต่างกัน {diff:N2} บาท. "
            + "ฝั่งเดียวกันบวกกัน, ข้ามฝั่งหักกัน (เช่น ใบเสร็จ 2,500 − ใบสำคัญจ่าย 500 = 2,000 สุทธิเข้าบัญชี). "
            + "ใบรับ 2 ใบไม่สามารถนำมาลบกันได้ — เลือกเอกสาร/JE ให้ถูก หรือใช้กลุ่มกระทบยอด M:N."
            + hint);
    }
}
