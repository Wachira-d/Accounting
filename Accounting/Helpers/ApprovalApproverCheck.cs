using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผู้อนุมัติที่ "ค้าง" (ไม่ได้เป็นสมาชิกบริษัทแล้ว) + การโอนผู้อนุมัติ + การแปลคำสั่งของช่องมือถือ — ตัวตัดสินเดียว (pure)
/// ของ <c>ApprovalService</c> (เว็บ) และ <c>MobileApiService</c> (มือถือ) · รอบ 2026-10-14 ทีม F4 (ID#9 · ID#11)
///
/// <para>═══ ที่มา ═══ (ID#11) ขั้นของกฎอนุมัติชี้ไปที่ผู้ใช้ที่ออกจากบริษัทแล้ว ⇒ คำขอถูกส่งไปหาคนที่ไม่มีวันเข้ามาเห็น และเพราะมีแค่คนนั้น
/// ที่กดได้ (ApprovalService ตรวจ "ผู้อนุมัติของขั้นนี้") เอกสารจึงค้าง "รออนุมัติ" ตลอดกาลโดยไม่มีใครรู้ · ทางแก้ไม่ใช่ "ข้ามขั้น"
/// (= ลดการแบ่งแยกหน้าที่) แต่คือ (1) ไม่ส่งคำขอใหม่ไปหาคนที่ออกแล้ว — บอกให้แก้กฎ (2) คำขอที่ค้างอยู่แล้ว ผู้ดูแลโอนให้สมาชิกคนอื่นได้
/// โดยผู้ขอเองรับโอนไม่ได้ (ผู้ขออนุมัติของตัวเองไม่ได้)</para></summary>
public static class ApprovalApproverCheck
{
    public const string DepartedRuleCode = "APPROVAL-APPROVER-DEPARTED";
    public const string NotAssignedRuleCode = "APPROVAL-NOT-ASSIGNED";
    public const string ReassignRuleCode = "APPROVAL-REASSIGN";

    /// <summary>ส่งคำขอใหม่: ขั้นใดของกฎชี้ไปที่คนที่ไม่ได้เป็นสมาชิกแล้ว ⇒ ข้อความบอกขั้น + ชื่อ + ทางไปต่อ · null = ส่งได้</summary>
    public static string? DepartedStepsProblem(string ruleName,
        IReadOnlyList<(int StepOrder, Guid ApproverUserId, string? ApproverName)> steps, IReadOnlySet<Guid> activeMemberIds)
    {
        var departed = steps.Where(s => !activeMemberIds.Contains(s.ApproverUserId)).OrderBy(s => s.StepOrder).ToList();
        if (departed.Count == 0) return null;
        var who = string.Join(", ", departed.Select(s => $"ขั้นที่ {s.StepOrder}: {NameOrUnknown(s.ApproverName)}"));
        return $"กฎการอนุมัติ “{ruleName}” มีผู้อนุมัติที่ไม่ได้เป็นสมาชิกบริษัทแล้ว ({who}) — ส่งขออนุมัติไม่ได้เพราะจะไม่มีใครอนุมัติได้ · "
            + "ให้ผู้ดูแลแก้ผู้อนุมัติที่เมนู อนุมัติ › แท็บ “กฎการอนุมัติ” แล้วกดส่งขออนุมัติอีกครั้ง";
    }

    /// <summary>คำขอที่ค้างอยู่: ป้ายเตือนบนคำขอ (หน้าอนุมัติ/หน้าเอกสาร) เมื่อผู้อนุมัติขั้นที่ยังไม่ได้ทำออกไปแล้ว · null = ไม่ค้าง</summary>
    public static string? StalledNotice(IReadOnlyList<(int StepOrder, string? ApproverName)> departedPendingSteps)
    {
        if (departedPendingSteps.Count == 0) return null;
        var who = string.Join(", ", departedPendingSteps.OrderBy(s => s.StepOrder)
            .Select(s => $"ขั้นที่ {s.StepOrder}: {NameOrUnknown(s.ApproverName)}"));
        return $"ผู้อนุมัติไม่ได้เป็นสมาชิกบริษัทแล้ว ({who}) — คำขอนี้จะค้างจนกว่าผู้ดูแลจะกด “โอนผู้อนุมัติ” "
            + "ที่เมนู อนุมัติ › แท็บ “ค้างเพราะผู้อนุมัติออก” แล้วเลือกสมาชิกคนใหม่";
    }

    /// <summary>ผู้กดไม่ใช่ผู้อนุมัติของขั้นปัจจุบัน (ทุกขั้น รวมการปฏิเสธ) — ข้อความพร้อมทางไปต่อ</summary>
    public static string NotAssignedMessage(int currentStep)
        => $"คุณไม่ใช่ผู้อนุมัติของขั้นที่ {currentStep} ของคำขอนี้ จึงอนุมัติ/ปฏิเสธไม่ได้ — รายการที่รอคุณอยู่ดูได้ที่เมนู อนุมัติ › แท็บ “รออนุมัติ” · "
            + "ถ้าผู้อนุมัติขั้นนี้ออกจากบริษัทแล้ว ให้ผู้ดูแลกด “โอนผู้อนุมัติ”";

    /// <summary>การโอนผู้อนุมัติของขั้นที่ยังไม่ได้ทำ — null = โอนได้ · ข้อความ = ปฏิเสธพร้อมช่องที่ต้องแก้</summary>
    /// <param name="currentApproverIsMember">ผู้อนุมัติเดิมยังเป็นสมาชิกอยู่ ⇒ ไม่ให้โอน (เส้นนี้มีไว้ปลดคำขอที่ค้างเพราะคนออก ไม่ใช่ช่องเปลี่ยนคนตามใจ)</param>
    /// <param name="newApproverApprovedAnotherStep">คนใหม่เป็นผู้อนุมัติของขั้นอื่นของคำขอเดียวกัน (อนุมัติไปแล้ว <b>หรือ</b> ยังรออยู่) ⇒ ไม่ให้โอน
    /// (ฝ่ายค้าน F4 + ฝ่ายค้านรอบ 2 P2(f): คนเดียวผ่านสองขั้น = ลดการแบ่งแยกหน้าที่ — เดิมดูแค่ขั้นที่อนุมัติแล้ว ⇒ โอนให้ผู้อนุมัติขั้นถัดไปได้)</param>
    /// <param name="currentApproverLacksFinalPermission">ฝ่ายค้านรอบ 2 P2(f): ผู้อนุมัติเดิมยังเป็นสมาชิกแต่<b>ไม่มีสิทธิ์อนุมัติเอกสาร</b>ของขั้นสุดท้าย ⇒ โอนได้
    /// (คำขอที่ส่งไปแล้วไม่เปลี่ยนตามการแก้กฎ — ทางไปต่อทางเดียวของคำขอที่ค้างอยู่)</param>
    /// <param name="newApproverLacksFinalPermission">คนใหม่ของขั้นสุดท้ายไม่มีสิทธิ์อนุมัติเอกสาร ⇒ ไม่ให้โอน (โอนแล้วก็ค้างเหมือนเดิม)</param>
    public static string? ReassignProblem(bool requestPending, bool actionPendingAtOrAfterCurrentStep,
        bool currentApproverIsMember, Guid newApproverUserId, bool newApproverIsMember, Guid requesterUserId,
        bool newApproverApprovedAnotherStep, bool currentApproverLacksFinalPermission = false, bool newApproverLacksFinalPermission = false)
    {
        if (!requestPending) return "คำขอนี้ไม่อยู่ในสถานะรออนุมัติแล้ว — รีเฟรชรายการ";
        if (!actionPendingAtOrAfterCurrentStep) return "ขั้นที่เลือกอนุมัติ/ปฏิเสธไปแล้ว หรือไม่มีในคำขอนี้ — เลือกขั้นที่ยังรออนุมัติ";
        if (currentApproverIsMember && !currentApproverLacksFinalPermission)
            return "ผู้อนุมัติของขั้นนี้ยังเป็นสมาชิกบริษัทอยู่ — ให้ผู้อนุมัติดำเนินการเอง · ถ้าต้องการเปลี่ยนคนถาวร ให้แก้กฎการอนุมัติแล้วให้ผู้ขอส่งใหม่";
        if (newApproverUserId == Guid.Empty) return "กรุณาเลือกผู้อนุมัติคนใหม่";
        if (!newApproverIsMember) return "ผู้อนุมัติคนใหม่ต้องเป็นสมาชิกของบริษัทนี้ที่ยังใช้งานอยู่ — เลือกจากรายชื่อสมาชิก";
        if (newApproverUserId == requesterUserId)
            return "โอนให้ผู้ขออนุมัติเองไม่ได้ (ผู้ขออนุมัติคำขอของตนเองไม่ได้) — เลือกสมาชิกคนอื่น";
        if (newApproverApprovedAnotherStep)
            return "คนนี้เป็นผู้อนุมัติขั้นอื่นของคำขอนี้อยู่แล้ว — คนเดียวผ่านสองขั้นไม่ได้ เลือกสมาชิกคนอื่น";
        if (newApproverLacksFinalPermission)
            return "ขั้นนี้เป็นขั้นสุดท้าย (อนุมัติเอกสารจริง) แต่คนที่เลือกยังไม่มีสิทธิ์ “อนุมัติเอกสาร” — เลือกสมาชิกที่มีสิทธิ์ หรือเพิ่มสิทธิ์ให้บทบาทของคนนั้นก่อน";
        return null;
    }

    /// <summary>ฝ่ายค้านรอบ 2 P2(f): ผู้อนุมัติขั้นสุดท้ายของคำขอที่<b>ส่งไปแล้ว</b>ไม่มีสิทธิ์อนุมัติเอกสาร — การแก้กฎไม่มีผลกับคำขอนี้ (ขั้นถูกคัดลอกตอนส่ง)
    /// ⇒ ทางไปต่อ = เพิ่มสิทธิ์ หรือผู้ดูแลโอนผู้อนุมัติของขั้นนี้</summary>
    public static string FinalStepNoPermissionMessage(string documentTitle)
        => $"คุณเป็นผู้อนุมัติขั้นสุดท้าย แต่ยังไม่มีสิทธิ์อนุมัติ{documentTitle} — ขอให้ผู้ดูแลเพิ่มสิทธิ์ “อนุมัติเอกสาร” ให้บทบาทของคุณที่หน้าตั้งค่าบทบาทและสิทธิ์ "
           + "หรือให้ผู้ดูแลกด “โอนผู้อนุมัติ” ของคำขอนี้ไปยังผู้ที่มีสิทธิ์ (การแก้กฎการอนุมัติมีผลกับคำขอที่ส่งใหม่เท่านั้น)";

    /// <summary>คำสั่งจากช่องมือถือ "approve"/"reject" ⇒ สถานะของ workflow · null = คำสั่งไม่รู้จัก</summary>
    public static ApprovalStatus? ParseMobileAction(string? action)
        => string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase) ? ApprovalStatus.Approved
         : string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase) ? ApprovalStatus.Rejected
         : null;

    private static string NameOrUnknown(string? name) => string.IsNullOrWhiteSpace(name) ? "(ผู้ใช้ที่ถูกลบ)" : name.Trim();
}
