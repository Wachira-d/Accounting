using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Approval;

public record CreateApprovalRuleRequest(
    string Name,
    string? Description,
    DocumentType? DocumentType,
    decimal? MinAmount,
    decimal? MaxAmount,
    List<ApprovalStepRequest> Steps,
    /// <summary>Optional project scope. When set, the rule only fires for
    /// documents tagged with this project. Null = applies to all projects.</summary>
    Guid? ProjectId = null,
    /// <summary>รอบ 200 ทีม RF (R200-X3) — ใช้เฉพาะตอน<b>แก้</b>กฎ: true = ล้างขอบเขตโครงการเป็น "ทุกโครงการ" ·
    /// false/ไม่ส่ง + ProjectId ว่าง = คงโครงการเดิม (Guid? รับ "" ไม่ได้ จึงต้องมีธงแยก)</summary>
    bool ClearProjectId = false);

public record ApprovalStepRequest(
    int StepOrder,
    Guid ApproverUserId,
    bool IsRequired = true);

public record ApprovalRuleResponse(
    Guid Id,
    string Name,
    string? Description,
    DocumentType? DocumentType,
    decimal? MinAmount,
    decimal? MaxAmount,
    bool IsActive,
    List<ApprovalStepResponse> Steps,
    Guid? ProjectId = null,
    /// <summary>ฝ่ายค้าน F4 (ID#9): คำเตือน (ไม่บล็อก) — ผู้อนุมัติขั้นสุดท้ายยังไม่มีสิทธิ์อนุมัติเอกสาร ⇒ คำขอเอกสารจะติด 403 ที่ขั้นสุดท้าย ·
    /// ข้อความไทยพร้อมทางไปต่อ · null = ไม่มีปัญหา/ตรวจไม่ได้</summary>
    string? Warning = null);

public record ApprovalStepResponse(
    int StepOrder,
    Guid ApproverUserId,
    string ApproverName,
    bool IsRequired);

public record ApprovalRequestResponse(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string RequestedByName,
    DateTime RequestedAt,
    ApprovalStatus OverallStatus,
    int CurrentStep,
    List<ApprovalActionResponse> Actions,
    /// <summary>PP36_REVIEW P0-2 — ขั้นสุดท้ายอนุมัติเอกสารจริงไม่สำเร็จ (ข้อความไทยพร้อมทางไปต่อ · ตัวเดียวกับหมายเหตุบนเอกสาร) · null = สำเร็จ/ยังไม่ถึงขั้นสุดท้าย</summary>
    string? FinalizeError = null,
    /// <summary>รอบ 2026-10-14 ทีม F4 (ID#11) — ผู้อนุมัติของขั้นที่ยังไม่ได้ทำไม่ได้เป็นสมาชิกบริษัทแล้ว ⇒ คำขอจะค้าง ·
    /// ข้อความไทยพร้อมทางไปต่อ (ผู้ดูแลกด "โอนผู้อนุมัติ") จาก <c>Helpers/ApprovalApproverCheck.StalledNotice</c> · null = ไม่ค้าง</summary>
    string? StaleApproverNotice = null);

public record ApprovalActionResponse(
    int StepOrder,
    string ApproverName,
    ApprovalStatus Status,
    DateTime? ActionAt,
    string? Comments,
    /// <summary>ID#11 — ขั้นนี้ยังรออนุมัติแต่ผู้อนุมัติไม่ได้เป็นสมาชิกบริษัทแล้ว (หน้าอนุมัติแสดงปุ่ม "โอนผู้อนุมัติ")</summary>
    bool ApproverDeparted = false);

/// <summary>ID#11 — ผู้ดูแลโอนขั้นที่ค้าง (ผู้อนุมัติออกจากบริษัทแล้ว) ให้สมาชิกคนใหม่ · <paramref name="StepOrder"/> ว่าง = ขั้นปัจจุบัน</summary>
public record ReassignApproverRequest(
    Guid NewApproverUserId,
    int? StepOrder = null,
    string? Reason = null);

public record SubmitApprovalActionRequest(
    ApprovalStatus Status,
    string? Comments,
    // รอบ 193 (ฝ่ายค้าน C5): ขั้นสุดท้ายที่อนุมัติเอกสารจริง — ผู้อนุมัติต้องเห็นคำเตือนแล้วกด "รับทราบ" เอง
    // (เหมือนหน้าเอกสาร/มือถือ) · false + มีคำเตือน ⇒ 422 พร้อมรายการ
    bool AcknowledgeWarnings = false);
