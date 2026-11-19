using System.Linq.Expressions;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลการตรวจคู่ "ใบสำคัญจ่าย ↔ 50 ทวิ ที่ออกไว้แล้ว"</summary>
public enum WhtCertLinkVerdict
{
    /// <summary>ตรงทุกข้อ — ผูกได้</summary>
    Ok,
    /// <summary>ผูกได้ แต่ต้องรับทราบคำเตือนทุกข้อก่อน (รหัสคำเตือนใน <see cref="WhtCertLinkDecision.Warnings"/>)</summary>
    OkWithWarnings,
    /// <summary>ผูกไม่ได้ — ไม่มีทางรับทราบ</summary>
    Blocked,
}

/// <summary>ข้อที่ไม่ผ่าน/ต้องรับทราบ — <paramref name="Code"/> คงที่ (หน้าเว็บส่งรหัสกลับเป็นการรับทราบ · audit เก็บรหัส)</summary>
public sealed record WhtCertLinkIssue(string Code, string Message);

/// <summary>ใบที่หลุดการผูกจะเป็นอย่างไร (271-0-1 · 271-0-2) — ไม่มีผลลัพธ์ "ลบ" ในชุดนี้โดยตั้งใจ</summary>
public enum WhtCertUnlinkedOutcome
{
    /// <summary>ใบมีเลขแล้ว ⇒ ยกเลิก (เลขคงอยู่ · ประทับยกเลิก · เลขไม่ขาดช่วง)</summary>
    Void,
    /// <summary>ใบอยู่ในเดือนที่ ภ.ง.ด. ยื่นแล้ว ⇒ เปลี่ยนการผูกไม่ได้เลย</summary>
    Blocked,
    /// <summary>ใบไม่มีเลข (วันนี้ไม่มีเส้นใดสร้างใบไม่มีเลข) ⇒ ทางเดิมของใบร่าง</summary>
    LegacyNoNumber,
}

/// <summary>สถานะการผูกของใบบนหน้า 50 ทวิ (server computes · page displays)</summary>
public enum WhtCertLinkState
{
    /// <summary>ผูกเอกสาร (ใบสำคัญจ่าย/ค่าใช้จ่าย/รอบโอน)</summary>
    Linked,
    /// <summary>ผูกรอบเงินเดือน (ภ.ง.ด.1)</summary>
    Payroll,
    /// <summary>ยังไม่ผูกเอกสาร (ใบมือ)</summary>
    Unlinked,
    /// <summary>ผู้ใช้ยืนยันแล้วว่าใบนี้ไม่มีใบสำคัญจ่ายในระบบ (D5)</summary>
    NoSourceConfirmed,
    /// <summary>ยกเลิกแล้ว (ไม่นับเป็น "ยังไม่ผูก")</summary>
    Voided,
}

/// <summary>คำตอบของ <see cref="WhtCertLinkPlan.Decide"/></summary>
public sealed record WhtCertLinkDecision(
    WhtCertLinkVerdict Verdict,
    IReadOnlyList<WhtCertLinkIssue> Blocks,
    IReadOnlyList<WhtCertLinkIssue> Warnings)
{
    public bool IsBlocked => Verdict == WhtCertLinkVerdict.Blocked;

    /// <summary>บล็อกเพราะเดือนที่ ภ.ง.ด. ยื่นแล้ว (271-0-2) — ผู้เรียกตอบ 409 ด้วยรหัส <see cref="WhtCertLinkPlan.FiledRuleCode"/></summary>
    public bool BlockedByFiledPeriod => Blocks.Any(b => b.Code == WhtCertLinkPlan.CodeFiled);

    /// <summary>คำเตือนที่ยังไม่ได้รับทราบ (รหัสที่ผู้ใช้ติ๊กมา) — ว่าง = ผ่าน</summary>
    public IReadOnlyList<WhtCertLinkIssue> Unacknowledged(IEnumerable<string>? acknowledgedCodes)
    {
        var acked = new HashSet<string>(acknowledgedCodes ?? Array.Empty<string>(), StringComparer.Ordinal);
        return Warnings.Where(w => !acked.Contains(w.Code)).ToList();
    }

    /// <summary>ข้อความเดียวสำหรับป้าย/คำตอบ (ข้อบล็อกก่อน · แล้วคำเตือน)</summary>
    public string Summary => string.Join(" · ", Blocks.Concat(Warnings).Select(i => i.Message));
}

/// <summary>ฝั่งใบสำคัญจ่าย (หรืองวดจ่าย) — ค่าที่ "ใบที่ระบบจะออกให้" จะมี (ผู้เรียกประกอบจากตัวออกใบตัวเดียว ไม่คิดสูตรเอง)</summary>
/// <param name="ComposeProblem">ระบบคิดใบของการจ่ายนี้ไม่ได้ (เช่น ยอดผู้จ่ายออกแทนไม่ตรง) — ไม่ null ⇒ บล็อก</param>
public sealed record WhtCertLinkPvFacts(
    Guid DocumentId,
    DocumentType DocumentType,
    DocumentStatus Status,
    string? DocumentNumber,
    Guid EffectivePayeeId,
    string? PayeeTaxId,
    string? PayeeBranch,
    bool IsMultiPayee,
    bool ViaEWithholding,
    decimal WhtThb,
    decimal IncomeThb,
    TaxType FormType,
    WithholdingTaxCertType CertType,
    IReadOnlyCollection<string> IncomeCodes,
    int PaymentYear,
    int PaymentMonth,
    string? ComposeProblem = null);

/// <summary>ฝั่งใบผู้สมัคร B</summary>
public sealed record WhtCertLinkCandidateFacts(
    Guid CertId,
    string? CertificateNumber,
    WithholdingTaxCertStatus Status,
    bool IsDeleted,
    Guid? DocumentId,
    Guid? PayrollRunId,
    Guid PayeeId,
    string? PayeeTaxId,
    string? PayeeBranch,
    decimal Tax,
    decimal Income,
    TaxType FormType,
    WithholdingTaxCertType CertType,
    IReadOnlyCollection<string> IncomeCodes,
    int Year,
    int Month);

/// <summary>ผลตรวจงวด — ผู้เรียกส่ง "เหตุผล" ของตัวกลาง <see cref="WhtCertVoidGuard.CheckPeriodAsync"/> (null = เดือนนั้นยังไม่ยื่น) ·
/// ตัวตัดสินนี้ไม่มีเงื่อนไข "ยื่นแล้ว" ชุดที่สอง (271-0-4)</summary>
/// <param name="VoidsExisting">การกระทำนี้จะยกเลิกใบเดิม A ไหม (เปลี่ยนการผูก) — false = ผูกครั้งแรก (ไม่ยกเลิกใบใด)</param>
/// <param name="ExistingCertFiled">เดือนของใบเดิม A (จะถูกยกเลิก)</param>
/// <param name="CandidateFiled">เดือนของใบ B (จะถูกผูก)</param>
/// <param name="PaymentMonthFiled">เดือนจ่ายของใบสำคัญจ่าย/งวด</param>
public sealed record WhtCertLinkPeriodFacts(
    bool VoidsExisting,
    string? ExistingCertFiled,
    string? CandidateFiled,
    string? PaymentMonthFiled)
{
    public static readonly WhtCertLinkPeriodFacts NoneFiled = new(false, null, null, null);
}

/// <summary>
/// **ตัวตัดสินเดียว "ผูกใบสำคัญจ่ายกับ 50 ทวิ ที่ออกไว้แล้ว ได้ไหม"** (รอบ 271 · คำตัดสินเจ้าของ 271-0-1…4 · ผู้ประสานงาน 271-1-x)
///
/// <para>ที่มา: ใบที่ออกด้วยมือที่หน้า 50 ทวิ (ไม่มี <c>DocumentId</c>) + ใบที่ระบบออกตอนอนุมัติใบสำคัญจ่ายของการจ่ายเดียวกัน ถูกนับสองครั้งในรายงาน
/// ภ.ง.ด.3/53 · ไฟล์ยื่น · ยอดนำส่ง (F6) · ทางแก้ = ผูกใบเดิมเข้ากับใบสำคัญจ่าย (ย้าย <c>DocumentId</c>) แทนการออกใบใหม่ ⇒ ผู้อ่าน 41 จุดถูกทันที</para>
///
/// <para>กติกา R1–R11 (ดู erp-review/2026-11-16/team-271-pv-wht-link.md §5.2) · ยอดตรงทุกสตางค์ (271-1-3 = D4) · ผู้ติดต่อซ้ำเลขภาษี+สาขาเดียวกัน = เตือน (271-1-5 = D8) ·
/// ใบร่างเป็นผู้สมัครได้ + เตือน (271-1-6 = D9) · เดือนที่ยื่นแล้ว: เปลี่ยนการผูก (ยกเลิกใบเดิม) = บล็อกเด็ดขาด (271-0-2) · ผูกครั้งแรกที่ไม่ยกเลิกใบใด =
/// ผ่านพร้อมคำเตือน "ผูกอย่างเดียว ตัวเลขที่ยื่นแล้วไม่เปลี่ยน" (271-1-2 = D10)</para>
/// </summary>
public static class WhtCertLinkPlan
{
    public const string RuleCode = "RD-50BIS-LINK";
    public const string RelinkRuleCode = "RD-50BIS-RELINK";
    public const string FiledRuleCode = "RD-50BIS-LINK-FILED";
    public const string FailedRuleCode = "RD-50BIS-LINK-FAILED";
    public const string NeedsAckRuleCode = "RD-50BIS-LINK-NEEDS-ACK";
    public const string IntentRuleCode = "RD-50BIS-LINK-INTENT";
    public const string DeleteRuleCode = "RD-50BIS-DELETE-NUMBERED";
    public const string NoSourceRuleCode = "RD-50BIS-NO-SOURCE";
    public const string StaleRuleCode = "RD-50BIS-LINK-STALE";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ท.ป.4/2528 (หนังสือรับรอง 1 ฉบับต่อการจ่าย — ห้ามนับซ้ำในแบบ ภ.ง.ด.)";

    // ── รหัสข้อ (คงที่ · หน้าเว็บส่งรหัสคำเตือนกลับเป็นการรับทราบ) ──
    public const string CodePv = "R1";
    public const string CodeCertStatus = "R2";
    public const string CodeCertLinkedElsewhere = "R3";
    public const string CodePayee = "R4";
    public const string CodeDuplicateContact = "R4-DUP";
    public const string CodeAmount = "R5";
    public const string CodeForm = "R6";
    public const string CodeMonth = "R7";
    public const string CodeIncomeType = "R8";
    public const string CodeFiled = "R9";
    public const string CodeFiledLinkOnly = "R9-LINK-ONLY";
    public const string CodeDraft = "R10";
    public const string CodeCombined = "R11";

    /// <summary>ข้อความ 271-0-3 — หน้าจอบอกตรง ๆ (ไม่ซ่อนผู้สมัครเงียบ)</summary>
    public const string CombinedCertMessage =
        "ใบที่ยอดมากกว่าการจ่ายนี้ (เช่น ใบรวมหลายการจ่าย) ผูกไม่ได้ — หนึ่ง 50 ทวิ ผูกได้หนึ่งการจ่าย เพื่อไม่ให้นับซ้ำในแบบ ภ.ง.ด.";

    /// <summary>ทางไปต่อเมื่อเดือนยื่นแล้ว (271-0-2)</summary>
    public const string FiledWayForward =
        " · ระบบไม่เปลี่ยนการผูกของเดือนที่ยื่นแล้ว: ถ้าเดือนนี้ยื่นซ้ำ/ยื่นเกิน ให้ยื่นแบบเพิ่มเติม (หรือขอคืน) ตามขั้นตอนกรมสรรพากร แล้วบันทึกรายการปรับปรุงในงวดปัจจุบัน · "
        + "ถ้ารายงานภาษีถูกตั้งเป็น \"ยื่นแล้ว\" โดยผิด ให้ผู้มีสิทธิ์ยื่นภาษีแก้สถานะรายงานก่อน";

    /// <summary>ข้อความผูกครั้งแรกในเดือนที่ยื่นแล้ว (271-1-2 = D10)</summary>
    private static string FiledLinkOnlyMessage(string? certNumber, string filedReason)
        => $"ผูกอย่างเดียว — {certNumber} อยู่ในเดือนที่ ภ.ง.ด. ยื่นแล้ว: การผูกนี้ไม่ยกเลิกใบใด ไม่เปลี่ยนเดือนและยอดของใบ "
           + "ตัวเลขในแบบ ภ.ง.ด. ที่ยื่นแล้วไม่เปลี่ยน (ระบบแค่บันทึกว่าใบนี้เป็นของใบสำคัญจ่ายนี้ ไม่ออกใบใหม่) · ที่มา: " + filedReason;

    /// <summary>ตัวตัดสิน (pure)</summary>
    public static WhtCertLinkDecision Decide(WhtCertLinkPvFacts pv, WhtCertLinkCandidateFacts c, WhtCertLinkPeriodFacts period)
    {
        var blocks = new List<WhtCertLinkIssue>();
        var warns = new List<WhtCertLinkIssue>();
        void B(string code, string msg) => blocks.Add(new WhtCertLinkIssue(code, msg));
        void W(string code, string msg) => warns.Add(new WhtCertLinkIssue(code, msg));
        var no = string.IsNullOrWhiteSpace(c.CertificateNumber) ? "ใบนี้" : c.CertificateNumber!.Trim();

        // R1 — ฝั่งใบสำคัญจ่าย
        var pvOk = true;
        if (!WhtCertIssuePlan.AppliesTo(pv.DocumentType))
        { B(CodePv, "เอกสารฝั่งรับเงิน — ผู้จ่ายเป็นผู้ออก 50 ทวิ ให้เรา (ผูกไม่ได้)"); pvOk = false; }
        else if (pv.WhtThb <= 0m)
        { B(CodePv, "การจ่ายนี้ไม่มีภาษีหัก ณ ที่จ่าย — ไม่มีใบให้ผูก"); pvOk = false; }
        else if (pv.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
        { B(CodePv, "ใบสำคัญจ่ายถูกยกเลิก/ตีกลับ — ผูก 50 ทวิ ไม่ได้"); pvOk = false; }
        else if (pv.ViaEWithholding)
        { B(CodePv, "ใบนี้หักผ่าน e-Withholding — ธนาคารนำส่ง/กรมสรรพากรออกหลักฐานเอง ไม่ต้องผูก 50 ทวิ"); pvOk = false; }
        else if (pv.IsMultiPayee)
        { B(CodePv, "ใบนี้มีผู้ถูกหักหลายราย — ยังผูก 50 ทวิ ที่ออกไว้แล้วกับใบหลายผู้ถูกหักไม่ได้ (ให้ระบบออกใบแยกต่อผู้ถูกหัก)"); pvOk = false; }
        else if (!string.IsNullOrWhiteSpace(pv.ComposeProblem))
        { B(CodePv, "ระบบคิดยอด 50 ทวิ ของการจ่ายนี้ไม่ได้: " + pv.ComposeProblem!.Trim()); pvOk = false; }

        // R2 — สถานะใบ B
        if (c.IsDeleted || c.Status == WithholdingTaxCertStatus.Voided)
            B(CodeCertStatus, $"{no} ถูกยกเลิก/ลบแล้ว — ผูกไม่ได้");

        // R3 — ใบ B ไม่ผูกเอกสารอื่น / ไม่ใช่ใบรอบเงินเดือน (271-0-3)
        if (c.PayrollRunId != null)
            B(CodeCertLinkedElsewhere, $"{no} เป็นใบของรอบเงินเดือน (ภ.ง.ด.1) — ผูกกับใบสำคัญจ่ายไม่ได้");
        else if (c.DocumentId is Guid other && other != pv.DocumentId)
            B(CodeCertLinkedElsewhere, $"{no} ผูกกับเอกสารอื่นอยู่แล้ว — หนึ่ง 50 ทวิ ผูกได้หนึ่งการจ่าย (กันนับซ้ำ)");

        if (pvOk)
        {
            // R4 — ผู้ถูกหัก (กุญแจกฎหมาย = เลขภาษี+สาขา · ไม่ fuzzy ชื่อ)
            if (c.PayeeId != pv.EffectivePayeeId)
            {
                if (ThaiTaxId.IsValid(pv.PayeeTaxId) && ThaiTaxId.IsValid(c.PayeeTaxId) && ThaiTaxId.Same(pv.PayeeTaxId, c.PayeeTaxId)
                    && TaxBranchCode.Normalize(pv.PayeeBranch) == TaxBranchCode.Normalize(c.PayeeBranch))
                    W(CodeDuplicateContact, $"ผู้ถูกหักของ {no} เป็นผู้ติดต่อคนละรายการแต่เลขผู้เสียภาษี {ThaiTaxId.Normalize(c.PayeeTaxId)} "
                        + $"สาขา {TaxBranchCode.Normalize(c.PayeeBranch)} เดียวกัน (ผู้ติดต่อซ้ำ) — รับทราบว่าเป็นผู้ถูกหักรายเดียวกัน");
                else
                    B(CodePayee, $"ผู้ถูกหักของ {no} ไม่ใช่ผู้ถูกหักของการจ่ายนี้ (เลขผู้เสียภาษี/สาขาไม่ตรง) — 50 ทวิ ต้องออกในนามผู้ถูกหักจริง (ท.ป.4/2528)");
            }

            // R11 ก่อน R5 — ใบรวมหลายการจ่าย (ยอดมากกว่า) ⇒ ข้อความ 271-0-3
            if (c.Tax > pv.WhtThb)
                B(CodeCombined, $"{no} ภาษี {c.Tax:N2} มากกว่าการจ่ายนี้ ({pv.WhtThb:N2}) — " + CombinedCertMessage);
            else
            {
                // R5 — ตรงทุกสตางค์ (decimal · ไม่มี tolerance · 271-1-3)
                if (c.Tax != pv.WhtThb)
                    B(CodeAmount, $"ภาษีของ {no} ({c.Tax:N2}) ไม่ตรงภาษีที่หักของการจ่ายนี้ ({pv.WhtThb:N2}) — ต้องตรงทุกสตางค์");
                if (c.Income != pv.IncomeThb)
                    B(CodeAmount, $"เงินได้ของ {no} ({c.Income:N2}) ไม่ตรงเงินได้ของการจ่ายนี้ ({pv.IncomeThb:N2}) — ต้องตรงทุกสตางค์");
            }

            // R6 — แบบ ภ.ง.ด. + เงื่อนไข (1)(2)(3)
            if (c.FormType != pv.FormType)
                B(CodeForm, $"{no} อยู่ใน {WhtUnissuedCertGate.FormLabel(c.FormType)} แต่การจ่ายนี้เป็น {WhtUnissuedCertGate.FormLabel(pv.FormType)}");
            if (WhtGrossUp.Normalize(c.CertType) != WhtGrossUp.Normalize(pv.CertType))
                B(CodeForm, $"เงื่อนไขการหักภาษีของ {no} ไม่ตรงกับการจ่ายนี้ (หัก ณ ที่จ่าย / ออกให้ตลอดไป / ออกให้ครั้งเดียว)");

            // R7 — เดือนภาษีต่างจากเดือนจ่าย
            if (c.Year != pv.PaymentYear || c.Month != pv.PaymentMonth)
                W(CodeMonth, $"{no} อยู่เดือนภาษี {c.Month:00}/{c.Year + 543} แต่การจ่ายนี้อยู่เดือน {pv.PaymentMonth:00}/{pv.PaymentYear + 543} "
                    + "(ภ.ง.ด.3/53 นับตามเดือนที่จ่ายจริง) — รับทราบว่าใบนี้คือใบของการจ่ายนี้");

            // R8 — ชุดประเภทเงินได้ต่าง (D3 · เตือน)
            if (!SameCodes(c.IncomeCodes, pv.IncomeCodes))
                W(CodeIncomeType, $"ประเภทเงินได้ของ {no} ({string.Join(", ", NormCodes(c.IncomeCodes))}) ไม่ตรงกับการจ่ายนี้ "
                    + $"({string.Join(", ", NormCodes(pv.IncomeCodes))}) — ยอดตรงกัน · รับทราบว่าเป็นการจ่ายเดียวกัน");
        }

        // R10 — ใบร่าง (D9 · ไม่ออกให้เอง)
        if (c.Status == WithholdingTaxCertStatus.Draft && !c.IsDeleted)
            W(CodeDraft, $"{no} ยังเป็นฉบับร่าง — ยังไม่เข้าแบบยื่น · ระบบจะไม่ออกใบใหม่ให้การจ่ายนี้ (ห้ามสร้างใบใหม่ — นับซ้ำ) · "
                + $"เมื่อพร้อมให้ออกใบ {no} ใบนี้เองที่หน้า 50 ทวิ (ถ้าเดือนของใบยื่นแล้ว ระบบไม่ให้ออก — ตัวเลขที่ยื่นแล้วต้องไม่เปลี่ยน)");

        // R9 — เดือนที่ยื่นแล้ว (ตัวกลาง WhtCertVoidGuard/TaxFilingLockPolicy ส่งเหตุผลมา)
        if (period.VoidsExisting)
        {
            foreach (var reason in new[] { period.ExistingCertFiled, period.CandidateFiled, period.PaymentMonthFiled })
                if (!string.IsNullOrWhiteSpace(reason))
                    B(CodeFiled, reason!.Trim() + FiledWayForward);
        }
        else
        {
            var filed = period.CandidateFiled ?? period.PaymentMonthFiled;
            if (!string.IsNullOrWhiteSpace(filed))
                W(CodeFiledLinkOnly, FiledLinkOnlyMessage(no, filed!.Trim()));
        }

        var verdict = blocks.Count > 0 ? WhtCertLinkVerdict.Blocked
            : warns.Count > 0 ? WhtCertLinkVerdict.OkWithWarnings : WhtCertLinkVerdict.Ok;
        return new WhtCertLinkDecision(verdict, blocks.DistinctBy(b => (b.Code, b.Message)).ToList(), warns);
    }

    /// <summary>ใบที่หลุดการผูก (271-0-1 · 271-0-2)</summary>
    public static WhtCertUnlinkedOutcome UnlinkedOutcome(string? certificateNumber, bool certPeriodFiled)
    {
        if (certPeriodFiled) return WhtCertUnlinkedOutcome.Blocked;
        return string.IsNullOrWhiteSpace(certificateNumber) ? WhtCertUnlinkedOutcome.LegacyNoNumber : WhtCertUnlinkedOutcome.Void;
    }

    /// <summary>ลบถาวรได้ไหม (271-1-1 = D1b · ปิด F1): ใบที่มีเลขแล้ว ⇒ ไม่ได้ทุกสถานะ (ยกเลิกแทน — เลขไม่ขาดช่วง/ไม่ถูกใช้ซ้ำ)</summary>
    public static bool DeleteAllowed(string? certificateNumber) => string.IsNullOrWhiteSpace(certificateNumber);

    /// <summary>ข้อความปฏิเสธการลบ (ทางไปต่อ = ยกเลิก)</summary>
    public static string DeleteRefusal(string? certificateNumber, WithholdingTaxCertStatus status)
        => status == WithholdingTaxCertStatus.Voided
            ? $"{certificateNumber} ยกเลิกแล้ว — ใบที่มีเลขต้องคงอยู่เป็นหลักฐาน (เลขไม่ขาดช่วง · ผู้ตรวจถามได้ว่าเลขนี้หายไปไหน) · ลบถาวรไม่ได้ ซ่อนด้วยตัวกรองสถานะแทน"
            : $"{certificateNumber} มีเลขแล้ว — ลบถาวรไม่ได้ (เลขอาจอยู่ในมือผู้ถูกหัก/ในแบบที่ยื่น และห้ามถูกนำกลับมาใช้กับใบอื่น) · กด \"ยกเลิก\" แทน";

    /// <summary>ยกเลิกได้จากปุ่มบนหน้า 50 ทวิ ไหม (เงื่อนไขปุ่ม — ด่านงวดที่ยื่นแล้วยังตรวจที่ <c>VoidAsync</c>)</summary>
    public static bool VoidAllowed(WithholdingTaxCertStatus status) => status != WithholdingTaxCertStatus.Voided;

    /// <summary>"ยังไม่ผูก" — นิยามเดียวของตัวกรองฝั่งเซิร์ฟเวอร์ · การ์ด · ป้าย (ยกเลิก/payroll/ยืนยันว่าไม่มีใบสำคัญจ่าย ไม่นับ)</summary>
    public static readonly Expression<Func<WithholdingTaxCert, bool>> IsUnlinked = w =>
        w.Status != WithholdingTaxCertStatus.Voided && w.DocumentId == null && w.SourcePayrollRunId == null
        && w.NoSourceDocumentReason == null;

    /// <summary>"ผูกแล้ว" — ผูกเอกสาร หรือรอบเงินเดือน (ไม่รวมใบยกเลิก)</summary>
    public static readonly Expression<Func<WithholdingTaxCert, bool>> IsLinked = w =>
        w.Status != WithholdingTaxCertStatus.Voided && (w.DocumentId != null || w.SourcePayrollRunId != null);

    /// <summary>สถานะการผูก (ตัวเดียวกับ <see cref="IsUnlinked"/>)</summary>
    public static WhtCertLinkState StateOf(WithholdingTaxCertStatus status, Guid? documentId, Guid? payrollRunId, string? noSourceReason)
    {
        if (status == WithholdingTaxCertStatus.Voided) return WhtCertLinkState.Voided;
        if (documentId != null) return WhtCertLinkState.Linked;
        if (payrollRunId != null) return WhtCertLinkState.Payroll;
        return string.IsNullOrWhiteSpace(noSourceReason) ? WhtCertLinkState.Unlinked : WhtCertLinkState.NoSourceConfirmed;
    }

    /// <summary>ข้อความคอลัมน์ "ผูกกับ" (server computes · page displays)</summary>
    public static string LinkLabel(WhtCertLinkState state, DocumentType? documentType, string? documentNumber, DateTime? paymentDate,
        int taxYear, int taxMonth, string? supersededByNumber, string? noSourceReason)
        => state switch
        {
            WhtCertLinkState.Voided => string.IsNullOrWhiteSpace(supersededByNumber) ? "ยกเลิก" : $"ยกเลิก · แทนด้วย {supersededByNumber}",
            WhtCertLinkState.Payroll => $"รอบเงินเดือน {taxMonth:00}/{taxYear + 543}",
            WhtCertLinkState.Linked => (documentType is DocumentType t ? DocumentTypeNames.Title(t, "th") + " " : "")
                                       + (string.IsNullOrWhiteSpace(documentNumber) ? "(ไม่พบเลขเอกสาร)" : documentNumber!.Trim())
                                       + (paymentDate is DateTime pd ? $" · งวด {pd.Day:00}/{pd.Month:00}/{pd.Year + 543}" : ""),
            WhtCertLinkState.NoSourceConfirmed => "ไม่มีใบสำคัญจ่าย — " + noSourceReason!.Trim(),
            _ => "ยังไม่ผูกเอกสาร",
        };

    /// <summary>เรียงผู้สมัคร (⚙️ ระบบแนะนำ — กติกาตายตัว ไม่มี AI): ผ่านก่อน → ผู้ถูกหักตรง → ภาษีตรง → เดือนตรง → ใกล้เดือนจ่าย</summary>
    public static int CandidateScore(WhtCertLinkPvFacts pv, WhtCertLinkCandidateFacts c, WhtCertLinkDecision d)
    {
        var score = d.Verdict switch { WhtCertLinkVerdict.Ok => 1000, WhtCertLinkVerdict.OkWithWarnings => 500, _ => 0 };
        if (c.PayeeId == pv.EffectivePayeeId) score += 100;
        if (c.Tax == pv.WhtThb) score += 50;
        var monthGap = Math.Abs((c.Year * 12 + c.Month) - (pv.PaymentYear * 12 + pv.PaymentMonth));
        if (monthGap == 0) score += 20;
        return score - Math.Min(monthGap, 19);
    }

    /// <summary>รหัสคำเตือนที่รับทราบ (เก็บบน <c>Document.WhtLinkAckCodes</c> คั่นด้วย ",") — ตัวอ่าน</summary>
    public static IReadOnlyList<string> ParseAckCodes(string? codes)
        => (codes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>ตัวเขียน (เรียง · ไม่ซ้ำ · ตัดค่าแปลก) · ว่าง = null</summary>
    public static string? JoinAckCodes(IEnumerable<string>? codes)
    {
        var list = (codes ?? Array.Empty<string>()).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0 && c.Length <= 32 && !c.Contains(','))
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>ช่วงเดือนของผู้สมัคร (±3 เดือนจากเดือนจ่าย)</summary>
    public const int CandidateMonthWindow = 3;

    private static IReadOnlyList<string> NormCodes(IReadOnlyCollection<string>? codes)
        => (codes ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static bool SameCodes(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
        => NormCodes(a).SequenceEqual(NormCodes(b), StringComparer.Ordinal);
}
