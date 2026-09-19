using System;

namespace Accounting.Helpers;

/// <summary>
/// **เพดานความคลาดเคลื่อนของการกระทบยอดธนาคาร** (pure · OWNER file).
///
/// ที่มา: `DECISION_AUDIT_2026-09-18.md` §3 D4-4 —
/// `BankService.Reconciliation.cs:142` เขียนว่า
/// <c>var tolerance = request.Tolerance &gt; 0 ? request.Tolerance : 0.01m;</c>
/// ⇒ **client ส่งอะไรมาก็ได้**. ส่ง `Tolerance = 1000000` แล้วกลุ่มกระทบยอดที่
/// ยอดสองฝั่งต่างกันเป็นแสนก็ถูกประทับว่า "สมดุล" (`IsBalanced = true`)
/// — เงินหายโดยระบบรับรองว่าครบ
///
/// กติกา 3 ชั้น:
/// 1. ≤ <see cref="AutoMax"/> (1 บาท) — เศษปัดเศษจริง ผ่านได้ไม่ต้องอธิบาย
/// 2. ≤ <see cref="ExplainedMax"/> (20 บาท) — ผ่านได้ **ต้องมีเหตุผลเป็นข้อความ**
///    (ค่าธรรมเนียมโอนต่างธนาคาร/เศษอัตราแลกเปลี่ยน) เหตุผลถูกเก็บลง Notes
/// 3. &gt; <see cref="ExplainedMax"/> — **ปฏิเสธเสมอ** ส่วนต่างขนาดนี้ไม่ใช่เศษ
///    ต้องลงเป็นรายการจริง (ค่าธรรมเนียม/ส่วนต่าง) ในกลุ่มกระทบยอด
///    เพื่อให้ GL เห็นเงินก้อนนั้น
/// </summary>
public static class BankReconciliationTolerance
{
    /// <summary>ค่าเริ่มต้นเมื่อผู้เรียกไม่ระบุ — 1 สตางค์</summary>
    public const decimal Default = 0.01m;

    /// <summary>เพดานที่ผ่านได้โดยไม่ต้องอธิบาย</summary>
    public const decimal AutoMax = 1.00m;

    /// <summary>เพดานสูงสุดที่ผ่านได้เมื่อมีเหตุผลกำกับ</summary>
    public const decimal ExplainedMax = 20.00m;

    /// <param name="Tolerance">ค่าที่จะใช้จริง</param>
    /// <param name="Error">null = ผ่าน; มีค่า = ปฏิเสธพร้อมเหตุผลภาษาไทย</param>
    /// <param name="RuleCode">รหัสกฎสำหรับ audit</param>
    public sealed record Result(decimal Tolerance, string? Error, string RuleCode)
    {
        public bool Ok => Error == null;
    }

    /// <summary>ตัดสินว่า tolerance ที่ client ส่งมาใช้ได้ไหม</summary>
    /// <param name="requested">ค่าที่ client ส่งมา (≤0 = ไม่ระบุ)</param>
    /// <param name="reason">เหตุผลที่ผู้ใช้เขียนกำกับ (เช่น Notes ของกลุ่ม)</param>
    public static Result Resolve(decimal requested, string? reason)
    {
        if (requested <= 0m)
            return new Result(Default, null, "BANK-TOL-DEFAULT");

        if (requested < 0m || requested > ExplainedMax)
            return new Result(Default,
                $"ค่าความคลาดเคลื่อน (Tolerance) {requested:N2} บาท เกินเพดาน {ExplainedMax:N2} บาท — "
                + "ส่วนต่างขนาดนี้ไม่ใช่เศษปัดเศษ ต้องบันทึกเป็นรายการจริงในกลุ่มกระทบยอด "
                + "(ค่าธรรมเนียมธนาคาร / ส่วนต่างอัตราแลกเปลี่ยน / ภาษีหัก ณ ที่จ่าย) "
                + "เพื่อให้บัญชีแยกประเภทเห็นเงินก้อนนั้น",
                "BANK-TOL-OVER-CAP");

        if (requested > AutoMax && string.IsNullOrWhiteSpace(reason))
            return new Result(Default,
                $"ค่าความคลาดเคลื่อน (Tolerance) {requested:N2} บาท เกิน {AutoMax:N2} บาท "
                + "ต้องระบุเหตุผลกำกับ (ช่องหมายเหตุ) ว่าส่วนต่างมาจากอะไร",
                "BANK-TOL-NEEDS-REASON");

        return new Result(requested, null,
            requested > AutoMax ? "BANK-TOL-EXPLAINED" : "BANK-TOL-AUTO");
    }
}
