using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Recurring;

public record CreateRecurringTransactionRequest(
    string Name,
    string? Description,
    RecurringFrequency Frequency,
    DateTime StartDate,
    DateTime? EndDate,
    int? MaxRuns,
    string TemplateType,        // "Document" or "Journal"
    DocumentType? DocumentType,
    Guid? ContactId,
    string TemplateData,        // JSON
    bool NotifyBeforeRun = true,
    int NotifyDaysBefore = 1,
    bool AutoApprove = false,
    // ส่งอีเมลให้ผู้ติดต่อทันทีเมื่อสร้างเอกสาร (ใช้ SMTP/อีเมลของบริษัทเอง)
    bool AutoSendEmail = false);

public record UpdateRecurringTransactionRequest(
    string? Name,
    string? Description,
    RecurringFrequency? Frequency,
    DateTime? EndDate,
    int? MaxRuns,
    DocumentType? DocumentType,
    Guid? ContactId,
    string? TemplateData,
    bool? NotifyBeforeRun,
    int? NotifyDaysBefore,
    bool? AutoApprove,
    RecurringStatus? Status,
    bool? AutoSendEmail = null,
    /// <summary>เดิมไม่มีในสัญญา ทั้งที่ฟอร์มแก้ไขเปิดช่อง "วันเริ่มต้น *" ให้แก้
    /// ⇒ แก้แล้วหายเงียบ. การแก้วันเริ่ม/ความถี่ต้อง recompute กำหนดรันถัดไปด้วย</summary>
    DateTime? StartDate = null);

public record RecurringTransactionResponse(
    Guid Id,
    string Name,
    string? Description,
    RecurringFrequency Frequency,
    RecurringStatus Status,
    DateTime StartDate,
    DateTime? EndDate,
    DateTime NextRunDate,
    DateTime? LastRunDate,
    int TotalRuns,
    int? MaxRuns,
    string TemplateType,
    DocumentType? DocumentType,
    Guid? ContactId,
    string? ContactName,
    string TemplateData,
    decimal Amount,
    bool NotifyBeforeRun,
    int NotifyDaysBefore,
    bool AutoApprove,
    DateTime CreatedAt,
    // echo กลับให้ฟอร์ม hydrate ได้ (defect class "เก็บแล้วต้อง echo กลับ")
    bool AutoSendEmail = false,
    // ผลรอบล่าสุด (ทีมตรวจรายการประจำ 2026-10-14) — หน้ารายการประจำแสดงเหตุผลที่ล้ม/เรื่องที่ต้องทำต่อ + ลิงก์เอกสารล่าสุด
    string? LastRunError = null,
    DateTime? LastRunErrorAt = null,
    string? LastRunNotice = null,
    Guid? LastDocumentId = null,
    // รอบ 247 (UX รายการประจำ): ผลรอบล่าสุดที่เซิร์ฟเวอร์ประกอบแล้ว — หัวเรื่อง · สิ่งที่ต้องทำ · ปุ่ม (Helpers/RecurringRunOutcome) · null = ไม่มีเรื่องต้องทำ
    global::Accounting.Helpers.RecurringRunOutcomeView? LastRunOutcome = null,
    // ติ๊กส่งอีเมลไว้แต่ผู้ติดต่อไม่มีอีเมล (ตัวตัดสินเดียว RecurringEmailReadiness) + ลิงก์หน้าผู้ติดต่อ
    string? AutoSendEmailWarning = null,
    string? AutoSendEmailFixUrl = null,
    // คำเตือนหลังบันทึก (สร้าง/แก้ไข) — บันทึกสำเร็จแล้ว แต่มีเรื่องควรรู้
    IReadOnlyList<string>? SaveWarnings = null);

/// <summary>รอบ 247: ฟอร์มรายการประจำ — ผู้ติดต่อที่เลือกรับอีเมลเอกสารได้ไหม (กติกาเดียวกับผู้ส่งจริง · Helpers/RecurringEmailReadiness)</summary>
public record RecurringEmailReadinessResponse(bool ContactHasEmail, string? Reason, string ContactUrl);
