using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาของ "เส้นเอกสาร" ที่ใช้ประเภทเงินมัดจำ (รอบ 194 ทีม B · spec S2/S3) — pure · ผู้เรียก = <c>DocumentService</c>
/// (สร้าง/แก้ใบ · ริบ · คำเตือนตอนอนุมัติ · ศูนย์มัดจำ) · ตัวตัดสิน VAT/ลักษณะเงินอยู่ที่ <see cref="DepositPolicyResolver"/> ตัวเดียว
/// ไฟล์นี้แค่ประกอบผลของมันเป็นคำตอบที่เส้นเอกสารต้องใช้ (ห้ามมีสำเนากติกา VAT ที่นี่)
/// </summary>
public static class DepositKindDocumentRules
{
    public const string KindNotFoundRuleCode = "DEPOSIT-KIND-NOT-FOUND";
    public const string KindChangeRuleCode = "DEPOSIT-KIND-CHANGE";
    public const string ForfeitInvoiceRuleCode = "DEPOSIT-FORFEIT-TIV";

    public const string KindNotFoundMessage =
        "ไม่พบประเภทเงินมัดจำที่เลือกในบริษัทนี้ (อาจถูกปิดใช้/ลบ หรือพิมพ์รหัสผิด) — เลือกประเภทใหม่ในฟอร์ม "
        + "หรือเปิดใช้ประเภทนั้นที่ ตั้งค่า → ประเภทเงินมัดจำ";

    public const string KindOnNonDepositMessage =
        "ประเภทเงินมัดจำใช้ได้กับใบมัดจำเท่านั้น (ใบเสร็จรับเงิน/ใบสำคัญรับที่ติ๊ก “เงินมัดจำ/รับล่วงหน้า”) — "
        + "ติ๊กเป็นใบมัดจำ หรือเอาประเภทออก";

    public const string KindChangeNeedsLinesMessage =
        "เปลี่ยนประเภทเงินมัดจำต้องคิดยอดบรรทัดใหม่ (VAT ของบรรทัดขึ้นกับประเภท) — ส่งรายการบรรทัดมาพร้อมการแก้ (บันทึกจากฟอร์มเอกสาร)";

    /// <summary>ประเภทที่จะมีผลหลังแก้ใบ — <paramref name="requested"/> null = คงของเดิม · <see cref="Guid.Empty"/> = ล้าง · ค่าอื่น = เปลี่ยน
    /// (สัญญา <c>UpdateDocumentRequest.DepositKindId</c>) · <c>Changed</c> = ผลต่างจากของเดิมจริง (ส่ง id เดิมซ้ำ = ไม่เปลี่ยน)</summary>
    public static (Guid? Effective, bool Changed) UpdateTarget(Guid? requested, Guid? current)
    {
        if (requested is not Guid r) return (current, false);
        Guid? eff = r == Guid.Empty ? null : r;
        return (eff, eff != current);
    }

    /// <summary>เปลี่ยน/ล้างประเภทได้ไหมตามสถานะ — ได้เฉพาะใบที่ยังไม่ลงบัญชี (ร่าง/ถูกตีกลับ) · null = ได้ ·
    /// ใบที่อนุมัติแล้วเปลี่ยนประเภท = เปลี่ยน VAT ของใบที่ออกไปแล้ว (§86/4 ห้ามแก้ย้อนหลัง) ⇒ ข้อความบอกทางไปต่อ</summary>
    public static string? KindChangeBlockedReason(DocumentStatus status)
        => status is DocumentStatus.Draft or DocumentStatus.Rejected ? null
         : "เปลี่ยนประเภทเงินมัดจำได้เฉพาะใบร่าง/ใบที่ถูกตีกลับ — ใบที่อนุมัติแล้วถือว่าออกไปแล้ว (§86/4 ห้ามแก้ย้อนหลัง) · "
           + "ทางไปต่อ: ยกเลิกใบนี้ (หรือคืนมัดจำที่หน้า “เงินมัดจำ”) แล้วออกใบมัดจำใหม่ด้วยประเภทที่ถูกต้อง";

    /// <summary>
    /// ตัวเลือก "เงินที่ริบคืออะไร" ที่หน้าศูนย์มัดจำต้องถาม — ถามเฉพาะเมื่อคำตอบทำให้ผลต่อ VAT ต่างกันจริง
    /// (ถามตัวตัดสิน <see cref="DepositPolicyResolver.ForfeitVatDecision"/> ทั้งสองทางแล้วเทียบ Action) ⇒
    /// ใบลักษณะ "ราคา" (ตอบเหมือนกันเสมอ) · นอกระบบ VAT · VAT เสียไปแล้ว = null (ไม่ถาม) ·
    /// ใบเดิมที่ไม่ทราบลักษณะ / เงินประกัน = ถาม โดยค่าเริ่มต้น = มี VAT (spec S3 ทิศปลอดภัย)
    /// </summary>
    public static IReadOnlyList<DepositForfeitOption>? ForfeitOptions(
        DepositNature? nature, decimal depositVatAmount, bool vatPendingUnrecognized, decimal companyVatRate)
    {
        var price = DepositPolicyResolver.ForfeitVatDecision(nature, DepositForfeitAs.PriceOrFee, depositVatAmount, vatPendingUnrecognized, companyVatRate);
        var comp = DepositPolicyResolver.ForfeitVatDecision(nature, DepositForfeitAs.Compensation, depositVatAmount, vatPendingUnrecognized, companyVatRate);
        // บริษัทไม่จด VAT = ไม่มี VAT ทั้งสองทาง — ถาม "มี VAT ไหม" = ข้อความเท็จ (ป้ายบอกว่ามี VAT) ⇒ ไม่ถาม
        if (price.Action == comp.Action || price.Action == DepositForfeitVatAction.CompanyNotVatRegistered) return null;
        return new[]
        {
            new DepositForfeitOption(nameof(DepositForfeitAs.PriceOrFee),
                "ราคา/ค่าบริการ/ค่าธรรมเนียมยกเลิก/ค่าของที่ใช้ไป (มี VAT)", price.Explanation, true),
            new DepositForfeitOption(nameof(DepositForfeitAs.Compensation),
                "ค่าเสียหายแท้ ไม่ใช่ค่าตอบแทนการขาย (ไม่มี VAT)", comp.Explanation, false),
        };
    }

    /// <summary>ข้อความอธิบายผลต่อ VAT เมื่อรับรู้/ริบ "ตามค่าเริ่มต้น" (ไม่ระบุ ForfeitAs) — หน้าศูนย์มัดจำแสดงในหน้าต่างรับรู้
    /// ให้ผู้ใช้รู้ก่อนกดว่าระบบจะออกใบกำกับภาษีให้หรือไม่</summary>
    public static string DefaultForfeitExplanation(
        DepositNature? nature, decimal depositVatAmount, bool vatPendingUnrecognized, decimal companyVatRate)
        => DepositPolicyResolver.ForfeitVatDecision(nature, null, depositVatAmount, vatPendingUnrecognized, companyVatRate).Explanation;

    /// <summary>
    /// ภาษีขายที่พักไว้ (21913) ส่วนที่ต้อง "กลับเข้ารายได้" เมื่อริบเป็นค่าเสียหาย (<see cref="DepositForfeitVatDecision.ReverseUndueVat"/>)
    /// — ตามสัดส่วนของฐานที่ริบต่อฐานคงเหลือ · ริบครบคงเหลือ = กลับทั้งก้อนที่ยังค้างใน GL (ไม่ทิ้งเศษสตางค์ค้าง 21913)
    /// </summary>
    /// <param name="pendingVatInGl">ยอด Cr สุทธิของ 21913 ที่ใบมัดจำนี้ยังค้าง (อ่านจาก GL)</param>
    /// <param name="forfeitBase">ฐานที่ริบครั้งนี้</param>
    /// <param name="outstandingBase">ฐานคงเหลือก่อนริบ</param>
    public static decimal CompensationVatReversal(decimal pendingVatInGl, decimal forfeitBase, decimal outstandingBase)
    {
        if (pendingVatInGl <= 0m || forfeitBase <= 0m || outstandingBase <= 0m) return 0m;
        if (forfeitBase >= outstandingBase - 0.005m) return pendingVatInGl;
        return Math.Min(pendingVatInGl,
            Math.Round(pendingVatInGl * forfeitBase / outstandingBase, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// คำเตือนตอนอนุมัติใบมัดจำตามลักษณะเงิน (spec S2 · ตาราง KindWarning ตัวเดียว) — null = ไม่เตือน
    /// <para>เตือนเฉพาะ: ราคา × เลื่อน VAT (พร้อมเหตุผลที่ตรึงบนใบ) · เงินประกัน × แยก VAT · ใบเดิมที่ไม่ทราบลักษณะ (null) = ไม่เตือน
    /// (พฤติกรรมเดิม) · บริษัทไม่จด VAT = ไม่เตือน (ใบ VAT 0 ทุกใบ ไม่ใช่การเลื่อน) · นอกระบบ VAT = ไม่เตือน (VAT 0 ถูกต้องแล้ว —
    /// คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ F2 ข้อ 8)</para>
    /// </summary>
    public static string? ApprovalWarning(
        bool isDeposit, DepositNature? nature, decimal vatAmount, bool depositOutputVatDeferred,
        bool companyVatRegistered, DepositSupplyNature supply, string? policyNote)
    {
        if (!isDeposit || nature is null || nature == DepositNature.NonVatSupply || !companyVatRegistered) return null;
        var mode = DepositPolicyResolver.OfDocument(true, vatAmount, depositOutputVatDeferred);
        if (mode is not { } t) return null;
        var (warning, code) = DepositPolicyResolver.KindWarning(nature.Value, t, supply);
        if (warning is null) return null;
        return $"[{code}] {warning}"
            + (string.IsNullOrWhiteSpace(policyNote) ? "" : $" · หมายเหตุบนใบ: {policyNote.Trim()}");
    }
}
