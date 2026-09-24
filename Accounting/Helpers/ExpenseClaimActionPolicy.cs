using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>การกระทำบนใบเบิกค่าใช้จ่ายที่ต้องผ่านด่านสิทธิ์</summary>
public enum ExpenseClaimAction
{
    View,
    Edit,
    Submit,
    Approve,
    Reject,
    Pay,
    Void,
}

/// <summary>ผลของ <see cref="ExpenseClaimActionPolicy.Decide"/> เมื่อไม่ผ่าน</summary>
public readonly record struct ExpenseClaimActionDenial(int Status, string Message, string RuleCode);

/// <summary>
/// **ใครทำอะไรกับใบเบิกค่าใช้จ่ายได้** — ตัวตัดสินตัวเดียวของทุกทางเข้า (เว็บ <c>ExpenseClaimController</c> · มือถือ
/// <c>MobileApiService.QuickApproveAsync</c> · service เอง)
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบสอง 193 · R2-C2 P0) ═══ approve/reject/pay/void/PUT ของใบเบิกมีแค่ <c>[Authorize]</c> และ service
/// ไม่ตรวจสิทธิ์สักจุด ⇒ พนักงานสิทธิ์ต่ำสุด สร้างใบเบิก → ส่ง → <b>อนุมัติเอง → กดจ่ายเอง</b> = เงินออก + ใบสำคัญจ่าย (PV) อนุมัติ
/// ในนาม <c>"system:expense-claim"</c> (ข้าม <see cref="DocumentPermissionHelper.CanApproveAsync"/>) + JE · มือถือก็เช่นกัน</para>
///
/// <para>กติกา: (1) ผู้ยื่นทำงานของตัวเองได้ — ดู · แก้/ส่ง (สถานะให้ service ตัดสิน) · ถอนใบ (ยกเลิก) ขณะยังไม่อนุมัติ ·
/// (2) <b>อนุมัติ/ปฏิเสธ/จ่ายใบของตัวเองไม่ได้</b> (SoD — ผู้รับเงินคือผู้ตัดสิน) ยกเว้น
/// <see cref="SelfDecisionAllowed"/> (เจ้าของกิจการที่ไม่ได้เปิดสวิตช์แยกหน้าที่ — บริษัทคนเดียว) ·
/// (3) คนอื่นต้องถือคีย์ของการกระทำนั้น (<see cref="ReviewerKeys"/>) · (4) จ่าย = ต้องอนุมัติใบสำคัญจ่ายได้ด้วย
/// (PV ที่สร้างจากการจ่ายอนุมัติในนามผู้กด)</para>
/// </summary>
public static class ExpenseClaimActionPolicy
{
    /// <summary>คีย์ที่ให้ "คนที่ไม่ใช่ผู้ยื่น" ทำการกระทำนั้นได้ (ถือตัวใดตัวหนึ่ง) · ดูใบ = ชุดเดียวกับรายการ (<c>GetAll</c>)</summary>
    public static IReadOnlyList<string> ReviewerKeys(ExpenseClaimAction action) => action switch
    {
        ExpenseClaimAction.View => new[]
        {
            PermissionKeys.HrAdmin, PermissionKeys.ExpenseApprove, PermissionKeys.ExpenseReject, PermissionKeys.ExpensePay,
        },
        ExpenseClaimAction.Edit or ExpenseClaimAction.Submit => new[] { PermissionKeys.HrAdmin },
        ExpenseClaimAction.Approve => new[] { PermissionKeys.ExpenseApprove, PermissionKeys.HrAdmin },
        ExpenseClaimAction.Reject => new[] { PermissionKeys.ExpenseReject, PermissionKeys.ExpenseApprove, PermissionKeys.HrAdmin },
        ExpenseClaimAction.Pay => new[] { PermissionKeys.ExpensePay },
        ExpenseClaimAction.Void => new[] { PermissionKeys.ExpenseApprove, PermissionKeys.HrAdmin },
        _ => Array.Empty<string>(),   // การกระทำที่ไม่รู้จัก = ไม่มีคีย์ไหนเปิด (ทิศปิด)
    };

    /// <summary>การกระทำที่ "ตัดสินเรื่องเงิน" ของใบ — ผู้ยื่นห้ามทำกับใบตัวเอง (SoD)</summary>
    public static bool IsDecision(ExpenseClaimAction action)
        => action is ExpenseClaimAction.Approve or ExpenseClaimAction.Reject or ExpenseClaimAction.Pay;

    /// <summary>ผู้ยื่นทำการกระทำนี้กับใบของตัวเองได้โดยไม่ต้องมีคีย์ไหม</summary>
    private static bool OwnerSelfService(ExpenseClaimAction action, ExpenseClaimStatus status) => action switch
    {
        ExpenseClaimAction.View => true,
        // สถานะ (แก้/ส่งได้เฉพาะ Draft) ให้ service ตัดสินด้วยข้อความของมันเอง — ที่นี่ตอบแค่ "เป็นงานของเจ้าของใบ"
        ExpenseClaimAction.Edit or ExpenseClaimAction.Submit => true,
        // ถอนใบ: ได้ขณะยังไม่มีใครตัดสิน · อนุมัติแล้วต้องให้ผู้อนุมัติยกเลิก
        ExpenseClaimAction.Void => status is ExpenseClaimStatus.Draft or ExpenseClaimStatus.Submitted,
        _ => false,
    };

    /// <summary>
    /// ผู้ยื่นอนุมัติ/จ่ายใบตัวเองได้ไหม — เฉพาะ <b>เจ้าของกิจการ</b> (role Owner) ที่<b>ไม่ได้</b>เปิดสวิตช์แยกหน้าที่
    /// (<c>CompanySettings.SodBlockSelfApproval</c> — สวิตช์เดียวกับการอนุมัติเอกสาร) · บริษัทคนเดียวไม่มีคนอื่นให้อนุมัติ ·
    /// พนักงานที่ถือคีย์อนุมัติ/จ่าย <b>ไม่เคย</b>ได้ข้อยกเว้นนี้ (คำถามเจ้าของโปรเจกต์: ให้ข้อยกเว้นนี้ไหม — บันทึกในรายงาน S2)
    /// </summary>
    public static bool SelfDecisionAllowed(bool actorIsCompanyOwner, bool sodBlockSelfApproval)
        => actorIsCompanyOwner && !sodBlockSelfApproval;

    /// <param name="isClaimOwner">ผู้กดคือผู้ยื่นใบนี้</param>
    /// <param name="holdsReviewerKey">ผู้กดถือคีย์ใน <see cref="ReviewerKeys"/> ของการกระทำนี้อย่างน้อยหนึ่งตัว</param>
    /// <param name="selfDecisionAllowed">ผล <see cref="SelfDecisionAllowed"/> (ใช้เฉพาะเมื่อผู้ยื่นตัดสินใบตัวเอง)</param>
    /// <param name="canApprovePaymentVoucher">ผู้กดอนุมัติใบสำคัญจ่ายได้ไหม (<see cref="DocumentPermissionHelper.CanApproveAsync"/>) —
    /// ใช้เฉพาะ <see cref="ExpenseClaimAction.Pay"/></param>
    /// <returns><c>null</c> = ผ่าน</returns>
    public static ExpenseClaimActionDenial? Decide(ExpenseClaimAction action, bool isClaimOwner, ExpenseClaimStatus status,
        bool holdsReviewerKey, bool selfDecisionAllowed, bool canApprovePaymentVoucher)
    {
        if (isClaimOwner)
        {
            if (IsDecision(action))
            {
                if (!selfDecisionAllowed)
                    return new ExpenseClaimActionDenial(403,
                        $"ผู้ยื่นใบเบิก{Verb(action)}ใบของตัวเองไม่ได้ (แยกหน้าที่ — ผู้รับเงินห้ามเป็นผู้ตัดสิน) · "
                        + "ให้ผู้มีสิทธิ์คนอื่นเป็นผู้" + Verb(action),
                        "EXPENSE-SOD-SELF");
            }
            else if (OwnerSelfService(action, status))
            {
                return null;
            }
        }
        if (!holdsReviewerKey)
            return new ExpenseClaimActionDenial(403,
                $"ไม่มีสิทธิ์{Verb(action)}ใบเบิกค่าใช้จ่าย — ต้องมีสิทธิ์ {KeyNames(action)}",
                "EXPENSE-PERM");
        if (action == ExpenseClaimAction.Pay && !canApprovePaymentVoucher)
            return new ExpenseClaimActionDenial(403,
                "จ่ายใบเบิกแล้วระบบจะออกและอนุมัติใบสำคัญจ่ายในนามผู้กด — ต้องมีสิทธิ์อนุมัติเอกสาร "
                + "(Document.Approve หรือสิทธิ์อนุมัติฝั่งซื้อ) ด้วย",
                "EXPENSE-PERM-PV");
        return null;
    }

    private static string Verb(ExpenseClaimAction action) => action switch
    {
        ExpenseClaimAction.View => "ดู",
        ExpenseClaimAction.Edit => "แก้ไข",
        ExpenseClaimAction.Submit => "ส่งอนุมัติ",
        ExpenseClaimAction.Approve => "อนุมัติ",
        ExpenseClaimAction.Reject => "ปฏิเสธ",
        ExpenseClaimAction.Pay => "จ่ายเงิน",
        ExpenseClaimAction.Void => "ยกเลิก",
        _ => "ทำรายการกับ",
    };

    private static string KeyNames(ExpenseClaimAction action)
    {
        var keys = ReviewerKeys(action);
        return keys.Count == 0
            ? "(ไม่มีคีย์ใดเปิดการกระทำนี้)"
            : string.Join(" หรือ ", keys.Select(k => k.StartsWith("perm:", StringComparison.Ordinal) ? k[5..] : k));
    }
}
