using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลการตัดสิน "ยกเลิกรายการเงินทดรอง"</summary>
public enum SalaryAdvanceVoidOutcome
{
    /// <summary>ยกเลิกได้ — ไม่มีใบสำคัญจ่ายที่ยังมีผลผูกอยู่</summary>
    Allow = 0,
    /// <summary>ยกเลิกได้ และต้องลบใบสำคัญจ่าย <b>ร่าง</b> ที่ผูกอยู่ด้วย (ยังไม่มีเลข ⇒ ลบแล้วเลขไม่ขาดช่วง §86/4)</summary>
    DeleteDraftVoucher = 1,
    /// <summary>ยกเลิกไม่ได้ — ข้อความบอกทางไปต่อ</summary>
    Block = 2,
}

public readonly record struct SalaryAdvanceVoidDecision(SalaryAdvanceVoidOutcome Outcome, string? Message = null, string? RuleCode = null);

/// <summary>
/// ตัวตัดสินเดียวของ "ยกเลิกเงินทดรองจ่าย" (คำตัดสินข้อ 114 Q2 · ทีมตรวจงานค้าง 2026-10-08)
///
/// <para>ที่มา: <c>SalaryAdvanceService.VoidAsync</c> เดิมพลิกสถานะเป็น Voided อย่างเดียว — ไม่ดูใบสำคัญจ่ายที่ผูกอยู่ ⇒
/// (1) ใบร่างค้างในระบบ (2) ผู้ใช้ไปอนุมัติใบสำคัญจ่ายที่หน้าเอกสารเอง (รายการยังเป็น "อนุมัติแล้ว") แล้วกดยกเลิกรายการได้ ⇒ ใบสำคัญจ่าย
/// ลงบัญชีแล้ว (Dr เงินทดรอง / Cr เงินสด) แต่รายการถูกยกเลิก ⇒ เงินเดือนไม่หักคืน ลูกหนี้ค้างไม่มีเจ้าของ ·
/// และ endpoint ไม่ตรวจสิทธิ์ (สมาชิกคนใดก็ยกเลิกเงินทดรองของใครก็ได้)</para>
/// <para>สถานะใบสำคัญจ่ายแปลผ่าน <see cref="LinkedPayVoucher.StepFor"/> ตัวเดียวกับตอนจ่าย (ห้ามมีตารางที่สอง)</para>
/// </summary>
public static class SalaryAdvanceVoidPolicy
{
    public const string RuleDisbursed = "ADVANCE-VOID-DISBURSED";
    public const string RulePvPending = "ADVANCE-VOID-PV-PENDING";
    public const string RulePvIssued = "ADVANCE-VOID-PV-ISSUED";
    public const string RuleAlreadyVoided = "ADVANCE-VOID-ALREADY";
    public const string RuleNoPermission = "ADVANCE-VOID-PERMISSION";

    /// <param name="advanceStatus">สถานะรายการเงินทดรอง (Draft/Submitted/Approved/Rejected/Disbursed/Cleared/Voided)</param>
    /// <param name="voucherId">ใบสำคัญจ่ายที่ผูก (DisbursementDocumentId)</param>
    /// <param name="voucherStatus">สถานะใบนั้น (null = ไม่พบ/ถูกลบ/คนละบริษัท)</param>
    /// <param name="voucherNumber">เลขใบ (ใช้ในข้อความ)</param>
    public static SalaryAdvanceVoidDecision Decide(string advanceStatus, Guid? voucherId, DocumentStatus? voucherStatus, string? voucherNumber)
    {
        if (advanceStatus == "Voided")
            return new(SalaryAdvanceVoidOutcome.Block, "รายการนี้ถูกยกเลิกไปแล้ว", RuleAlreadyVoided);
        if (advanceStatus is "Disbursed" or "Cleared")
            return new(SalaryAdvanceVoidOutcome.Block,
                "ไม่สามารถยกเลิกรายการที่จ่ายเงินแล้วได้ — ยกเลิกใบสำคัญจ่าย" + (voucherNumber is null ? "" : $" {voucherNumber}")
                + "ที่หน้าเอกสาร (กลับรายการบัญชี) แทน", RuleDisbursed);

        var no = voucherNumber is null ? "" : $" {voucherNumber}";
        return LinkedPayVoucher.StepFor(voucherId, voucherStatus) switch
        {
            LinkedPayVoucherStep.Create => new(SalaryAdvanceVoidOutcome.Allow),
            LinkedPayVoucherStep.ReuseDraft when voucherStatus == DocumentStatus.Draft
                => new(SalaryAdvanceVoidOutcome.DeleteDraftVoucher),
            LinkedPayVoucherStep.ReuseDraft => new(SalaryAdvanceVoidOutcome.Block,
                $"ใบสำคัญจ่าย{no} ของรายการนี้อยู่ระหว่างรออนุมัติ — ปฏิเสธใบนั้นที่หน้าเอกสารก่อน แล้วค่อยยกเลิกรายการ", RulePvPending),
            _ => new(SalaryAdvanceVoidOutcome.Block,
                $"จ่ายเงินแล้วผ่านใบสำคัญจ่าย{no} (ลงบัญชีแล้ว) — ยกเลิกใบสำคัญจ่ายนั้นที่หน้าเอกสารก่อน (กลับรายการบัญชี) "
                + "แล้วค่อยยกเลิกรายการเงินทดรอง", RulePvIssued),
        };
    }

    /// <summary>ใครยกเลิกได้: เจ้าของ/ผู้ดูแลระบบ · ผู้มีสิทธิ์อนุมัติเงินทดรอง · HR Admin — ผู้ขอเองยกเลิกได้เฉพาะคำขอที่ยังไม่อนุมัติ (ถอนคำขอ)</summary>
    public static bool CanVoid(string advanceStatus, bool isOwnerOrAdmin, bool hasApproverPermission, bool isRequester)
        => isOwnerOrAdmin || hasApproverPermission
           || (isRequester && advanceStatus is "Draft" or "Submitted");
}
