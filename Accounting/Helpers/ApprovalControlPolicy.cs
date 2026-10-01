namespace Accounting.Helpers;

// ═══════════════════════════════════════════════════════════════════════
// รอบ 200 ทีม V1F · ฝ่ายค้าน V1-R6 (นโยบายตัดสินแล้ว): ด่านควบคุมภายในของ "การอนุมัติ" — ตัวตัดสินเดียวของ
// ApproveDocumentAsync (อนุมัติเอกสาร) และ ReissueSettlementPaidDocumentAsync (ยกเลิกและออกใบแทน)
//
// เดิมเงื่อนไขทั้งสองเขียน inline ใน ApproveDocumentAsync ⇒ เส้นออกใบแทน (ที่ออกเลขใบกำกับจริงให้ผู้ซื้อที่อาจเปลี่ยนเป็นนิติบุคคลอื่น)
// ข้าม SoD และวงเงินเซ็นหลายขั้นทั้งเส้น · ย้ายมาที่นี่ให้สองเส้นถามตัวเดียว (F2 ข้อ 4 — ห้ามสำเนาที่สอง) · G6: pure
// ═══════════════════════════════════════════════════════════════════════

/// <summary>สิ่งที่ต้องทำกับคำขอ "ยกเลิกและออกใบแทน" ตามด่านควบคุมภายใน</summary>
public enum ReissueControlAction
{
    /// <summary>ไม่ต้องมีคนที่สอง (SoD ปิด · ไม่ถึงวงเงินเซ็นหลายขั้น) — ทำได้ทันทีในคำขอเดียว (พฤติกรรมเดิม)</summary>
    ExecuteNow = 0,
    /// <summary>ต้องมีคนที่สอง — บันทึกคำขอไว้บนใบเดิม (ยังไม่แตะเงิน/เลข/สถานะใด ๆ) รอผู้อนุมัติคนอื่นยืนยัน</summary>
    RecordRequest = 1,
    /// <summary>ยืนยันคำขอที่ค้าง — ทำตาม "คำขอที่บันทึกไว้" (ไม่ใช่ข้อมูลที่ส่งมาใหม่)</summary>
    ConfirmRequest = 2,
    /// <summary>ปฏิเสธพร้อมทางไปต่อ (ผู้ขอยืนยันเอง · มีคำขอค้างอยู่แล้ว · ไม่มีคำขอให้ยืนยัน)</summary>
    Blocked = 3,
}

public sealed record ReissueControlVerdict(ReissueControlAction Action, string? Reason, string? RuleCode);

/// <summary>ผลตัดสิน SoD สามสถานะ (<see cref="ApprovalControlPolicy.SelfApproval"/>) — "ไม่รู้ผู้ทำ" เป็นค่าใน enum ไม่ใช่ "ผ่าน" (DOCTRINE §1)</summary>
public enum SodVerdict
{
    /// <summary>ปิดการแบ่งแยกหน้าที่ หรือผู้ทำเป็นคนอื่นที่รู้ตัว</summary>
    Pass = 0,
    /// <summary>ผู้อนุมัติคือผู้ทำ (หลักหรือรอง)</summary>
    SamePerson = 1,
    /// <summary>ไม่รู้ผู้ทำหลัก (ระบบ/นำเข้า/แถวเก่า)</summary>
    MakerUnknown = 2,
}

/// <summary>นโยบายต่อสถานะ "ไม่รู้ผู้ทำ" ของเส้นที่ยังไม่บังคับ</summary>
public enum SodUnknownMakerMode
{
    /// <summary>ไม่บล็อก — บันทึก audit "ถ้าบังคับจะบล็อก" (วัดผลกระทบก่อน)</summary>
    Shadow = 0,
    /// <summary>บล็อกพร้อมทางไปต่อ (ให้ผู้มีสิทธิ์อนุมัติคนอื่น)</summary>
    Enforce = 1,
}

public static class ApprovalControlPolicy
{
    /// <summary>
    /// **ต้องผ่านกระบวนการเซ็นอนุมัติหลายขั้นไหม** (<c>CompanySettings.RequireApprovalForDocuments</c> + <c>ApprovalThresholdAmount</c>) —
    /// ยอดรวมถึงเกณฑ์ (≥) = ต้อง · เกณฑ์ว่าง = 0 (ทุกใบ) · ตัวเดียวกับที่ <c>ApproveDocumentAsync</c> ใช้
    /// </summary>
    public static bool NeedsSignatureFlow(bool requireApprovalForDocuments, decimal? approvalThresholdAmount, decimal totalAmount)
        => requireApprovalForDocuments && totalAmount >= (approvalThresholdAmount ?? 0m);

    /// <summary>
    /// **SoD ตัวตัดสินเดียวของทั้งระบบ** (รอบ 201 ทีม TX · A-TX3 · คำตัดสินข้อ 45) — ผู้ทำ (maker) ห้ามเป็นผู้อนุมัติ (checker) คนเดียวกัน
    /// (<c>CompanySettings.SodBlockSelfApproval</c>) · เดิมมีสองความจริง: รอบโอน settlement "ผู้ทำไม่รู้ = บล็อก" (<c>SettlementPostingGate.SodSelfApproval</c>
    /// เขียนสูตรเอง) กับอนุมัติเอกสาร "ไม่รู้ = ไม่บล็อก" · ตอนนี้สูตรอยู่ที่นี่ที่เดียว และคืน<b>สามสถานะ</b> ("ไม่รู้" เป็นค่าใน enum — DOCTRINE §1)
    /// ให้แต่ละเส้นเลือกนโยบายต่อสถานะ "ไม่รู้" ผ่าน <see cref="BlocksDocumentApproval"/> / <see cref="BlocksSettlementPosting"/>
    /// <para>เทียบ id แบบตัดช่องว่าง + ไม่สนตัวพิมพ์ · ผู้ทำรอง (<paramref name="otherMakers"/> — เช่นผู้เติมไฟล์เข้ารอบโอน) ที่ว่างไม่นับเป็นใคร ·
    /// "ไม่รู้" ตัดสินจาก<b>ผู้ทำหลัก</b> (<paramref name="primaryMaker"/>) เท่านั้น (พฤติกรรมเดิมของรอบโอน)</para>
    /// </summary>
    public static SodVerdict SelfApproval(bool sodBlockSelfApproval, string? primaryMaker, IEnumerable<string?>? otherMakers, string? approverId)
    {
        if (!sodBlockSelfApproval) return SodVerdict.Pass;
        if (SamePerson(primaryMaker, approverId) || (otherMakers ?? Array.Empty<string?>()).Any(m => SamePerson(m, approverId)))
            return SodVerdict.SamePerson;
        return string.IsNullOrWhiteSpace(primaryMaker) ? SodVerdict.MakerUnknown : SodVerdict.Pass;
    }

    /// <summary>
    /// <b>โหมดของสถานะ "ไม่รู้ผู้ทำ" บนเส้นอนุมัติเอกสาร</b> — คำตัดสินข้อ 45 ให้บล็อกพร้อมทางไปต่อ แต่เข้มขึ้นกับเส้นอนุมัติเดิม (ใบที่ระบบ/นำเข้าสร้าง
    /// ไม่มีผู้ทำ) ⇒ <b>โหมดเงาก่อน</b>: บันทึก audit "จะบล็อก" (<see cref="ShadowRuleCode"/>) ให้วัดว่ากระทบใบไหนบ้าง แล้วเจ้าของค่อยเปลี่ยนเป็น
    /// <see cref="SodUnknownMakerMode.Enforce"/> (คอมมิตเดียว — ยังไม่มีสวิตช์บนหน้าแอดมิน · ไฟล์ของทีม PL)
    /// </summary>
    public const SodUnknownMakerMode DocumentUnknownMakerMode = SodUnknownMakerMode.Shadow;

    /// <summary>รหัสกฎของร่องรอยโหมดเงา "ถ้าบังคับจะบล็อก" (ค้นใน audit ได้)</summary>
    public const string ShadowRuleCode = "SOD-SHADOW-MAKER-UNKNOWN";

    /// <summary>เส้นอนุมัติเอกสารบล็อกไหม — คนเดียวกัน = บล็อกเสมอ · ไม่รู้ผู้ทำ = ตาม <paramref name="unknownMode"/></summary>
    internal static bool BlocksDocumentApproval(SodVerdict verdict, SodUnknownMakerMode unknownMode)
        => verdict == SodVerdict.SamePerson || (verdict == SodVerdict.MakerUnknown && unknownMode == SodUnknownMakerMode.Enforce);

    /// <summary>เส้นอนุมัติเอกสารต้องบันทึกร่องรอยโหมดเงาไหม (ไม่รู้ผู้ทำ + ยังไม่บังคับ)</summary>
    public static bool RecordsShadow(SodVerdict verdict, SodUnknownMakerMode unknownMode)
        => verdict == SodVerdict.MakerUnknown && unknownMode == SodUnknownMakerMode.Shadow;

    /// <summary>เส้นลงบัญชีรอบโอน (settlement) บล็อกไหม — "ไม่รู้" บล็อกเสมอ (คำตัดสินรอบ 198 ข้อ 7 · DOCTRINE §1)</summary>
    public static bool BlocksSettlementPosting(SodVerdict verdict) => verdict != SodVerdict.Pass;

    /// <summary>
    /// **SoD ของเส้นอนุมัติเอกสาร** (ทางลัดเดิม — ผู้ทำว่าง (ระบบ/นำเข้า) ยังไม่บล็อกระหว่างโหมดเงา) · สูตรอยู่ที่ <see cref="SelfApproval"/> ตัวเดียว
    /// </summary>
    public static bool SelfApprovalBlocked(bool sodBlockSelfApproval, string? makerId, string? approverId)
        => BlocksDocumentApproval(SelfApproval(sodBlockSelfApproval, makerId, null, approverId), DocumentUnknownMakerMode);

    private static bool SamePerson(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// **"ยกเลิกและออกใบแทน" ต้องมีคนที่สองไหม** — ใบแทนออกเลขใบกำกับจริง (และเปลี่ยนผู้ซื้อได้) ⇒ ด่านควบคุมเดียวกับการอนุมัติ:
    /// SoD เปิด <b>หรือ</b> ยอดถึงเกณฑ์เซ็นหลายขั้น = ผู้ขอคนเดียวไม่พอ ⇒ บันทึกคำขอไว้ก่อน (ยังไม่แตะอะไร) แล้วให้ผู้มีสิทธิ์คนอื่นกดยืนยัน ·
    /// ผู้ขอยืนยันเองไม่ได้ · ไม่มีสภาพครึ่งทาง: ระหว่างรอ ใบเดิม/การรับชำระ/รอบโอนเหมือนเดิมทุกอย่าง · G6: pure
    /// </summary>
    /// <param name="pendingRequesterId">ผู้ขอของคำขอที่ค้างอยู่บนใบเดิม (null = ไม่มีคำขอค้าง)</param>
    /// <param name="actorId">ผู้กดครั้งนี้</param>
    /// <param name="confirmPending">ผู้กดตั้งใจ "ยืนยันคำขอที่ค้าง" (ปุ่มยืนยัน) — false = ส่งคำขอใหม่</param>
    public static ReissueControlVerdict ForReissue(bool requireApprovalForDocuments, decimal? approvalThresholdAmount, decimal totalAmount,
        bool sodBlockSelfApproval, string? pendingRequesterId, string actorId, bool confirmPending)
    {
        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        var needsSecond = sodBlockSelfApproval
            || NeedsSignatureFlow(requireApprovalForDocuments, approvalThresholdAmount, totalAmount);
        if (confirmPending)
        {
            if (pendingRequesterId == null)
                return new ReissueControlVerdict(ReissueControlAction.Blocked,
                    "ไม่มีคำขอยกเลิกและออกใบแทนที่รอยืนยันบนใบนี้ (อาจถูกยืนยันหรือยกเลิกไปแล้ว) — โหลดหน้าเอกสารใหม่" + Tail,
                    "REISSUE-NO-PENDING");
            if (needsSecond && SamePerson(pendingRequesterId, actorId))
                return new ReissueControlVerdict(ReissueControlAction.Blocked,
                    "คำขอยกเลิกและออกใบแทนของใบนี้เป็นของคุณเอง — ผู้ขอยืนยันเองไม่ได้ (การแบ่งแยกหน้าที่/วงเงินเซ็นหลายขั้น) · "
                    + "ให้ผู้มีสิทธิ์ยกเลิกและอนุมัติคนอื่นกดยืนยัน หรือกด “ยกเลิกคำขอ” ถ้าไม่ต้องการแล้ว" + Tail,
                    "REISSUE-SOD-SAME-PERSON");
            return new ReissueControlVerdict(ReissueControlAction.ConfirmRequest, null, null);
        }
        if (pendingRequesterId != null)
            return new ReissueControlVerdict(ReissueControlAction.Blocked,
                "ใบนี้มีคำขอยกเลิกและออกใบแทนรอยืนยันอยู่แล้ว — กด “ยืนยันออกใบแทน” (ผู้อนุมัติคนอื่น) หรือ “ยกเลิกคำขอ” ก่อนส่งคำขอใหม่" + Tail,
                "REISSUE-PENDING-EXISTS");
        if (!needsSecond)
            return new ReissueControlVerdict(ReissueControlAction.ExecuteNow, null, null);
        return new ReissueControlVerdict(ReissueControlAction.RecordRequest,
            (sodBlockSelfApproval
                ? "บริษัทเปิดการแบ่งแยกหน้าที่ (SoD) — ผู้ขอยกเลิกและออกใบแทนต้องไม่ใช่ผู้อนุมัติคนเดียวกัน"
                : $"ยอด {totalAmount:N2} บาท ถึงเกณฑ์เซ็นอนุมัติหลายขั้น ({(approvalThresholdAmount ?? 0m):N2} บาท)")
            + " — บันทึกคำขอไว้แล้ว (ยังไม่ได้ยกเลิกหรือออกเลขใด ๆ) · ให้ผู้มีสิทธิ์ยกเลิกและอนุมัติคนอื่นเปิดเอกสารนี้แล้วกด “ยืนยันออกใบแทน”",
            "REISSUE-REQUEST-RECORDED");
    }
}
