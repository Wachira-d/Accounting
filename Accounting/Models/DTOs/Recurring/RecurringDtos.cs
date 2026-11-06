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
    bool AutoSendEmail = false,
    // รอบ 248: โหมดรับเงินต่อรายการ (ชื่อใน AutopilotSettingsResolver.Modes · null/Inherit = ตามผู้ติดต่อ › บริษัท) + ช่องทางย่อย (ชื่อ enum)
    string? PaymentMode = null,
    List<string>? PayLinkChannels = null);

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
    DateTime? StartDate = null,
    /// <summary>รอบ 248: null = ไม่แก้ · "" หรือ "Inherit" = ล้าง (ตามผู้ติดต่อ › บริษัท)</summary>
    string? PaymentMode = null,
    /// <summary>รอบ 248: null = ไม่แก้ · [] = ล้าง (ทุกช่องตามโหมด)</summary>
    List<string>? PayLinkChannels = null);

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
    IReadOnlyList<string>? SaveWarnings = null,
    // รอบ 248: echo กลับให้ฟอร์ม hydrate (โหมดรับเงิน · ช่องทาง · นโยบายอนุมัติของรายการนี้ — null = ไม่ได้ตั้ง/ตามผู้ติดต่อ)
    string? PaymentMode = null,
    IReadOnlyList<string>? PayLinkChannels = null,
    AutoApprovePolicyView? AutoApprovePolicy = null);

/// <summary>รอบ 248: นโยบายอนุมัติข้ามคำเตือนที่หน้าแสดง — รหัสที่ปล่อย · ผู้ตั้ง/เมื่อไร · ระดับ (Recurring/Contact)</summary>
public record AutoApprovePolicyView(IReadOnlyList<string> PassCodes, string? SetBy, DateTime? SetAt, string? Scope);

/// <summary>รอบ 248: ตั้ง/ล้างนโยบาย — <c>Inherit=true</c> ⇒ ล้าง (รายการ: ตามผู้ติดต่อ · ผู้ติดต่อ: ไม่ปล่อย) · ไม่งั้นตั้งเป็นรหัสชุดนี้ (ว่าง = หยุดรอคนทุกข้อ)</summary>
public record SetAutoApprovePolicyRequest(List<string>? PassCodes, bool Inherit = false);

/// <summary>รอบ 248: ค่าตั้ง Autopilot ระดับผู้ติดต่อ (ช่องทางรับเงิน + นโยบาย) — echo กลับทุกช่อง</summary>
public record ContactAutopilotSettingsResponse(Guid ContactId, string ContactName, string? PaymentMode, IReadOnlyList<string>? PayLinkChannels,
    AutoApprovePolicyView? AutoApprovePolicy);

/// <summary>รอบ 248: แก้ช่องทางรับเงินของผู้ติดต่อ — PaymentMode null = ไม่แก้ · "" / "Inherit" = ล้าง · PayLinkChannels null = ไม่แก้ · [] = ล้าง</summary>
public record UpdateContactAutopilotRequest(string? PaymentMode, List<string>? PayLinkChannels);

/// <summary>รอบ 248: รายการตัวเลือกที่หน้าแสดง (เซิร์ฟเวอร์คำนวณ) — โหมดรับเงิน · ช่องทางที่บริษัทเปิด · คำเตือนที่ปล่อยได้/ไม่ได้</summary>
public record AutopilotOptionsResponse(
    IReadOnlyList<AutopilotOption> PaymentModes,
    IReadOnlyList<AutopilotOption> PayLinkChannels,
    IReadOnlyList<AutopilotOption> ReleasableWarnings,
    IReadOnlyList<AutopilotOption> NeverReleasable,
    string? PayLinkUnavailableReason);

public record AutopilotOption(string Code, string Label, string? Why = null);

/// <summary>รอบ 248: ผลตรวจความพร้อม (Helpers/AutopilotReadiness) — หน้าแสดงตรง ๆ</summary>
public record AutopilotReadinessResponse(string Level, string LevelLabel, IReadOnlyList<global::Accounting.Helpers.AutopilotReadiness.Item> Items,
    int WarnCount, int FailCount);

/// <summary>รอบ 248: ทดลองรัน (ไม่เขียนอะไรเลย) — ลำดับเหตุการณ์ของรอบถัดไป</summary>
public record RecurringDryRunResponse(
    AutopilotReadinessResponse Readiness,
    IReadOnlyList<DateTime> NextRunDates,
    string DocumentTypeLabel,
    string NumberNote,
    IReadOnlyList<RecurringDryRunLine> Lines,
    decimal SubTotal,
    decimal VatAmount,
    decimal TotalAmount,
    decimal WithholdingTaxAmount,
    decimal ExpectedNetReceipt,
    string ApprovalOutcome,
    IReadOnlyList<string> ApprovalWarnings,
    IReadOnlyList<string> ApprovalReleased,
    IReadOnlyList<string> ApprovalBlocking,
    string? EmailTo,
    string EmailPlan,
    string PayLinkPlan,
    IReadOnlyList<string> Notes);

public record RecurringDryRunLine(string Description, decimal Quantity, decimal UnitPrice, decimal VatRate, decimal Amount, decimal VatAmount,
    decimal WithholdingTaxRate, decimal WithholdingTaxAmount);

/// <summary>รอบ 247: ฟอร์มรายการประจำ — ผู้ติดต่อที่เลือกรับอีเมลเอกสารได้ไหม (กติกาเดียวกับผู้ส่งจริง · Helpers/RecurringEmailReadiness)</summary>
public record RecurringEmailReadinessResponse(bool ContactHasEmail, string? Reason, string ContactUrl);
