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

    /// <summary>รหัส/ข้อความเมื่อคีย์ล็อกยอดใบมัดจำ (<c>AdvisoryLockKey.DepositRealizeKey</c>) ถูกผู้อื่นถืออยู่ — ข้อความเดียวของทุกทางเข้า
    /// (ปุ่มรับรู้/ริบ · อนุมัติใบที่หักฐานมัดจำ · ตัดชำระด้วยมัดจำ · รอบ 194 R2 P-a)</summary>
    public const string DepositBusyRuleCode = "DEPOSIT-REALIZE-BUSY";
    public const string DepositBusyMessage =
        "มีผู้ใช้อื่นกำลังรับรู้/ริบ/ตัดชำระมัดจำใบนี้อยู่ — รอสักครู่แล้วเปิดหน้า “เงินมัดจำ” ดูยอดล่าสุดก่อนทำรายการอีกครั้ง "
        + "(ยอดคงค้างต้องอ่านหลังรายการนั้นเสร็จ กันรับรู้/ตัดชำระซ้ำ)";

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
    /// (ถามตัวตัดสิน <see cref="DepositPolicyResolver.ForfeitVatDecision"/> ทุกทางแล้วเทียบ Action) ⇒
    /// ใบลักษณะ "ราคา" (ตอบเหมือนกันเสมอ) · นอกระบบ VAT · VAT เสียไปแล้ว = null (ไม่ถาม) ·
    /// ใบเดิมที่ไม่ทราบลักษณะ / เงินประกัน = ถาม โดยค่าเริ่มต้น = มี VAT (spec S3 ทิศปลอดภัย)
    /// <para>รอบ 194 R2-2: ใบเดิมที่<b>กำกวม</b> (ลักษณะ NULL · VAT 0 · ธงเลื่อน false — <see cref="DepositPolicyResolver.ForfeitZeroVatDeferred"/> = null)
    /// ⇒ เพิ่มข้อ "ไม่มี VAT มาแต่แรก" (<see cref="DepositForfeitAs.OriginallyNoVat"/>) · ไม่เลือก = มี VAT (ทิศปลอดภัย)</para>
    /// </summary>
    /// <param name="depositOutputVatDeferred">ธงเลื่อนตามที่อยู่บนใบ (ตัวตัดสินแปลงสามสถานะเอง · null = ผู้เรียกไม่ทราบ ⇒ ถือว่าเลื่อน)</param>
    /// <param name="channelVatRate">อัตราช่องทางที่เซิร์ฟเวอร์หาจากใบ (ที่พัก <c>ChargeVat=false</c> = 0) · null = ไม่มีช่องทาง</param>
    public static IReadOnlyList<DepositForfeitOption>? ForfeitOptions(
        DepositNature? nature, decimal depositVatAmount, bool vatPendingUnrecognized, decimal companyVatRate,
        bool? depositOutputVatDeferred = null, decimal? channelVatRate = null)
    {
        DepositForfeitVatDecision Ask(DepositForfeitAs a) => DepositPolicyResolver.ForfeitVatDecision(nature, a, depositVatAmount,
            vatPendingUnrecognized, companyVatRate, depositOutputVatDeferred: depositOutputVatDeferred, channelVatRate: channelVatRate);
        var price = Ask(DepositForfeitAs.PriceOrFee);
        var comp = Ask(DepositForfeitAs.Compensation);
        // ข้อ "ไม่มี VAT มาแต่แรก" เฉพาะใบ VAT 0 ที่กำกวม (สามสถานะ = null) และคำตอบนั้นเปลี่ยนผลจริง
        var ambiguous = depositVatAmount <= 0.005m
            && DepositPolicyResolver.ForfeitZeroVatDeferred(nature, depositOutputVatDeferred, channelVatRate) == null;
        var noVat = ambiguous ? Ask(DepositForfeitAs.OriginallyNoVat) : null;
        // บริษัทไม่จด VAT / ใบ VAT 0 ที่รู้แน่ = ไม่มี VAT ทุกทาง — ถาม "มี VAT ไหม" = ข้อความเท็จ (ป้ายบอกว่ามี VAT) ⇒ ไม่ถาม
        var offerNoVat = noVat is { RequestIgnored: false } && noVat.Action != price.Action;
        if ((price.Action == comp.Action && !offerNoVat)
            || price.Action is DepositForfeitVatAction.CompanyNotVatRegistered or DepositForfeitVatAction.ZeroVatAtIssue) return null;
        var list = new List<DepositForfeitOption>
        {
            new(nameof(DepositForfeitAs.PriceOrFee),
                "ราคา/ค่าบริการ/ค่าธรรมเนียมยกเลิก/ค่าของที่ใช้ไป (มี VAT)", price.Explanation, true),
        };
        if (comp.Action != price.Action)
            list.Add(new(nameof(DepositForfeitAs.Compensation),
                "ค่าเสียหายแท้ ไม่ใช่ค่าตอบแทนการขาย (ไม่มี VAT)", comp.Explanation, false));
        if (offerNoVat && noVat != null)
            list.Add(new(nameof(DepositForfeitAs.OriginallyNoVat),
                "ใบนี้ไม่มี VAT มาแต่แรก (ส่งออก 0% / ยกเว้น §81 / ออกตอนยังไม่จด VAT)", noVat.Explanation, false));
        return list;
    }

    /// <summary>ข้อความอธิบายผลต่อ VAT เมื่อรับรู้/ริบ "ตามค่าเริ่มต้น" (ไม่ระบุ ForfeitAs) — หน้าศูนย์มัดจำแสดงในหน้าต่างรับรู้
    /// ให้ผู้ใช้รู้ก่อนกดว่าระบบจะออกใบกำกับภาษีให้หรือไม่</summary>
    public static string DefaultForfeitExplanation(
        DepositNature? nature, decimal depositVatAmount, bool vatPendingUnrecognized, decimal companyVatRate,
        bool? depositOutputVatDeferred = null, decimal? channelVatRate = null)
        => DepositPolicyResolver.ForfeitVatDecision(nature, null, depositVatAmount, vatPendingUnrecognized, companyVatRate,
            depositOutputVatDeferred: depositOutputVatDeferred, channelVatRate: channelVatRate).Explanation;

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
            + (string.IsNullOrWhiteSpace(policyNote) ? "" : $" · บันทึกนโยบายบนใบ (ภายใน): {policyNote.Trim()}");
    }

    // ═══════════════ รอบ 194 ทีม M — ใบกำกับของยอดที่ริบ (M1 · C2 regsec) ═══════════════

    /// <summary>ป้ายบน <c>Document.DepositPolicyNote</c> ของใบกำกับที่ระบบออกให้การริบ — กุญแจค้นหา "ใบกำกับของมัดจำใบนี้" (idempotent)
    /// · ช่องนี้ระบบเขียนเท่านั้น (เส้นแก้ใบเขียนเฉพาะใบที่มีประเภทมัดจำ — ใบกำกับไม่มี) ⇒ ผู้ใช้แก้ใบร่างแล้วกุญแจไม่หาย</summary>
    public static string ForfeitInvoiceMarker(Guid depositId) => $"[DEPOSIT-FORFEIT-OF:{depositId:N}]";

    /// <summary>ใบนี้คือใบกำกับที่ระบบออกให้การริบของมัดจำ <paramref name="depositId"/> ไหม (อ่านป้ายจาก <c>DepositPolicyNote</c>)</summary>
    public static bool IsForfeitInvoiceOf(string? depositPolicyNote, Guid depositId)
        => depositPolicyNote != null && depositPolicyNote.Contains(ForfeitInvoiceMarker(depositId), StringComparison.Ordinal);

    /// <summary>หมายเหตุ (พิมพ์) ของใบกำกับของยอดที่ริบ — ข้อความคงที่ต่อเลขใบมัดจำ ⇒ ใช้เป็นกุญแจสำรองหาใบร่างที่สร้างแล้วแต่ยังไม่ทันติดป้าย</summary>
    public static string ForfeitInvoiceNotes(string depositNumber)
        => $"ใบกำกับภาษีของเงินมัดจำ {depositNumber} ที่ริบ — ชำระแล้วด้วยเงินมัดจำ";

    /// <summary>ทางไปต่อเมื่อริบแล้วออกใบกำกับ/ตัดชำระไม่ครบ — <b>ข้อความเดียว</b>ของ DocumentService และที่พัก (M1 ค: เดิมสองฝั่งสั่งกันคนละทาง
    /// "ห้ามกดรับรู้ซ้ำ" กับ "ต้องรับรู้ที่หน้าเงินมัดจำ") · ตอนนี้กดซ้ำปลอดภัย: ระบบหาใบกำกับที่ค้างของมัดจำใบนี้แล้วทำต่อ ไม่ออกใบใหม่</summary>
    public static string ForfeitRetryHint(string depositNumber, decimal amount)
        => $"ทำต่อที่หน้า “เงินมัดจำ” → “รับรู้” ใบ {depositNumber} → เลือก “ริบมัดจำ” ยอด {amount:N2} — "
           + "ระบบทำต่อจากใบกำกับที่ค้างอยู่ (ไม่ออกใบซ้ำ)";

    /// <summary>
    /// ตัดสินว่าการริบครั้งนี้ต้อง "สร้างใบกำกับใหม่" หรือ "ทำต่อจากใบที่ค้าง" (M1 ข · idempotent) — pure
    /// <para><paramref name="forfeitInvoices"/> = ใบกำกับของการริบของมัดจำใบนี้ที่ยังไม่ยกเลิก/ไม่ลบ · ใบที่ออกแล้วและไม่มียอดค้าง = ทำจบแล้ว (ไม่นับ) ·
    /// ค้าง 1 ใบยอดตรง ⇒ ร่าง = อนุมัติแล้วตัดชำระ · ออกแล้ว = ตัดชำระต่อ · ยอดไม่ตรง/ค้างหลายใบ ⇒ ขัดกัน (บอกทางไปต่อ ห้ามเดา)</para>
    /// </summary>
    public static ForfeitInvoiceResume ResumeForfeitInvoice(
        IReadOnlyList<ForfeitInvoiceCandidate> forfeitInvoices, decimal requestGross, string depositNumber)
    {
        var open = forfeitInvoices
            .Where(c => c.Status != DocumentStatus.Voided)
            .Where(c => !DocumentStatusRules.IsIssued(c.Status) || c.BalanceDue > 0.005m)
            .ToList();
        if (open.Count == 0) return new ForfeitInvoiceResume(ForfeitInvoiceStep.CreateNew, null, null, null);
        if (open.Count > 1)
            return new ForfeitInvoiceResume(ForfeitInvoiceStep.Conflict, null, null,
                $"มัดจำ {depositNumber} มีใบกำกับของการริบค้างอยู่หลายใบ ({string.Join(", ", open.Select(o => o.DocumentNumber))}) — "
                + "เปิดตรวจแล้วยกเลิกใบที่ไม่ใช้ก่อน แล้วทำรายการนี้ใหม่");
        var c1 = open[0];
        var issued = DocumentStatusRules.IsIssued(c1.Status);
        var due = issued ? c1.BalanceDue : c1.TotalAmount;
        if (Math.Abs(due - requestGross) > 0.005m)
            return new ForfeitInvoiceResume(ForfeitInvoiceStep.Conflict, c1.Id, c1.DocumentNumber,
                $"มัดจำ {depositNumber} มีใบกำกับของการริบครั้งก่อนค้างอยู่ ({c1.DocumentNumber} ยอด {due:N2}) ไม่ตรงยอดที่ขอ ({requestGross:N2}) — "
                + $"ทำต่อด้วยยอด {due:N2} หรือยกเลิกใบ {c1.DocumentNumber} ก่อน (ห้ามออกใบกำกับสองใบต่อเงินก้อนเดียว)");
        return new ForfeitInvoiceResume(issued ? ForfeitInvoiceStep.ApplyOnly : ForfeitInvoiceStep.ApproveThenApply,
            c1.Id, c1.DocumentNumber, null);
    }

    /// <summary>
    /// ด่าน "มัดจำ 1 ใบ → ใบปลายทาง 1 ใบ" ของการตัดชำระ (C2 regsec) — ผ่อนเฉพาะเมื่อฝั่งใดฝั่งหนึ่งเป็นใบกำกับของการริบของมัดจำใบนี้เอง
    /// (ริบหลายครั้งบางส่วน · ตัดชำระใบสุดท้ายบางส่วนแล้วริบส่วนที่เหลือ) · การคืนยอดเมื่อยกเลิกใบปลายทางหาใบมัดจำจาก JE ตัดชำระ
    /// ไม่ใช่จากตัวชี้ช่องเดียว ⇒ ใบก่อนหน้ายกเลิกทีหลังก็คืนถูก
    /// </summary>
    public static bool ApplyToAnotherTargetAllowed(bool priorIsForfeitInvoiceOfDeposit, bool targetIsForfeitInvoiceOfDeposit)
        => priorIsForfeitInvoiceOfDeposit || targetIsForfeitInvoiceOfDeposit;

    /// <summary>ข้อความเมื่อด่าน "มัดจำ 1 ใบ → ใบปลายทาง 1 ใบ" ปฏิเสธ — <b>ข้อความเดียว</b>ของทุกเส้น (ตัดชำระ · หักมัดจำหลายใบ · หักแบบขับ JE)
    /// พร้อมทางไปต่อที่ไม่ใช่ทางตัน (R2-6: เดิมเส้นหักแบบขับ JE บอกแค่ "ถูกนำไปหักกับ … แล้ว")</summary>
    /// <param name="context">ชื่อเส้น (เช่น "หักมัดจำแบบขับ JE") — ว่าง = ตัดชำระ</param>
    public static string AppliedElsewhereMessage(string depositNumber, string priorNumber, string? context = null)
        => (string.IsNullOrWhiteSpace(context) ? "" : context.Trim() + ": ")
           + $"มัดจำ {depositNumber} ถูกนำไปหัก/ตัดชำระกับ {priorNumber} แล้ว (มัดจำ 1 ใบใช้กับใบปลายทางได้ 1 ใบ) — ทางไปต่อ: "
           + $"① ถ้า {priorNumber} ผิด ให้ยกเลิกใบนั้นก่อน (ระบบคืนยอดมัดจำและปลดการผูกให้อัตโนมัติ) แล้วทำรายการนี้ใหม่ · "
           + $"② ถ้ามัดจำเหลือจากการหักใบนั้น ให้คืนส่วนที่เหลือที่หน้า “เงินมัดจำ” (ใบลดหนี้ §86/10 ถ้าเสีย VAT แล้ว) หรือ “ริบมัดจำ” "
           + "(ระบบออกใบกำกับของยอดที่ริบแล้วตัดชำระให้ — ใบกำกับของการริบใช้ร่วมกับใบอื่นได้)";
}

/// <summary>ขั้นที่ต้องทำกับใบกำกับของยอดที่ริบ (M1 ข)</summary>
public enum ForfeitInvoiceStep
{
    CreateNew = 1,
    ApproveThenApply = 2,
    ApplyOnly = 3,
    Conflict = 4,
}

/// <summary>ใบกำกับของการริบที่พบ (ยังไม่ยกเลิก/ไม่ลบ)</summary>
public sealed record ForfeitInvoiceCandidate(Guid Id, string DocumentNumber, DocumentStatus Status, decimal TotalAmount, decimal BalanceDue);

/// <summary>ผลตัดสิน <see cref="DepositKindDocumentRules.ResumeForfeitInvoice"/></summary>
public sealed record ForfeitInvoiceResume(ForfeitInvoiceStep Step, Guid? InvoiceId, string? InvoiceNumber, string? Problem);
