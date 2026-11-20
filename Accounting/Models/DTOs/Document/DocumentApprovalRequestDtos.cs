namespace Accounting.Models.DTOs.Document;

/// <summary>รอบ 276 — คำขอ "ส่งขออนุมัติ" (หมายเหตุถึงผู้อนุมัติ · ไม่บังคับ)</summary>
public sealed record DocumentApprovalSubmitRequest(string? Note);

/// <summary>รอบ 276 — ตีกลับพร้อมเหตุผล (บังคับ)</summary>
public sealed record DocumentApprovalReturnRequest(string? Reason);

/// <summary>ผลการส่งขออนุมัติ · <see cref="Route"/> = "Workflow" (กฎอนุมัติเดิม — ผู้อนุมัติระบุตัว) | "Basic" (ผู้อนุมัติเอกสารของบริษัท) ·
/// <see cref="Message"/> = ข้อความที่หน้าเว็บแสดงตรง ๆ (server computes)</summary>
public sealed record DocumentApprovalSubmitResult(string Route, string Message, int ApproverCount, List<string> ApproverNames);

/// <summary>รอบ 276 — สถานะการขออนุมัติของเอกสารหนึ่งใบ + ปุ่มที่ผู้ใช้คนนี้กดได้ (เซิร์ฟเวอร์ตัดสินทุกข้อ · หน้าเว็บแสดงอย่างเดียว)</summary>
public sealed record DocumentApprovalStateResponse(
    Guid DocumentId,
    string DocumentStatus,
    /// <summary>"None" | "Basic" | "Workflow"</summary>
    string Route,
    /// <summary>ชื่อ enum <c>DocumentApprovalRequestStatus</c> ของคำขอพื้นฐานล่าสุด · null = ไม่เคยขอแบบพื้นฐาน</summary>
    string? RequestStatus,
    string? RequestedByName,
    DateTime? RequestedAt,
    string? Note,
    string? DecidedByName,
    DateTime? DecidedAt,
    string? DecisionReason,
    /// <summary>ป้ายสถานะภาษาไทย เช่น "รออนุมัติจากผู้ดูแล/บัญชี" · null = ไม่มีอะไรต้องแสดง</summary>
    string? StatusLabel,
    bool CanRequest,
    bool CanWithdraw,
    bool CanApprove,
    bool CanReturn,
    /// <summary>ผู้ใช้คนนี้อนุมัติ/ยกเลิก/ลงบัญชีเอกสารได้ไหม (ไม่ถูกจำกัดเฉพาะร่าง) — หน้าเว็บซ่อนปุ่มอนุมัติ/ยกเลิก/แปลง/รับชำระเมื่อ false</summary>
    bool MayFinalize,
    /// <summary>ป้ายบอกผู้ใช้ว่าถูกจำกัดอย่างไร (<c>DocumentRestrictionPolicy.Notice</c>) · null = ไม่ถูกจำกัด</summary>
    string? RestrictionNotice);

/// <summary>หนึ่งแถวในคิว "เอกสารรออนุมัติ" ของผู้อนุมัติ — ไม่มีชื่อลูกค้าเกินจำเป็น (ผู้อนุมัติเปิดใบดูได้เอง)</summary>
public sealed record DocumentApprovalQueueItem(
    Guid DocumentId,
    string DocumentType,
    string DocumentTypeLabel,
    string DocumentNumber,
    DateTime DocumentDate,
    decimal TotalAmount,
    string? ContactName,
    string RequestedByName,
    DateTime RequestedAt,
    string? Note);
