namespace Accounting.Models.DTOs.Autopilot;

// รอบ 252 WP-12A — หน้า Autopilot (autopilot.html) · ทุกป้าย/สถานะ/ปุ่มประกอบที่เซิร์ฟเวอร์ (Helpers/AutopilotStage · AutopilotNotice ·
// QuotationAcceptAutopilot · BankReceiptAutoSettle) — หน้าเว็บแสดงอย่างเดียว · enum ออกเป็นชื่อ (string)

public record AutopilotSummaryDto(int Days, int Done, int Running, int Stuck, int OpenIssues, int BankAwaitingConfirm,
    // รอบ 259-3 (E-M4): สลิปลูกค้ารอยืนยัน + ลิงก์คิวเจ้าของเดียว (payment-intents) — เซิร์ฟเวอร์ส่ง หน้าไม่ประกอบ URL เอง
    int SlipAwaitingConfirm = 0, string? SlipQueueHref = null);

public record AutopilotStepDto(string Key, string Label, string State, string? Note);

public record AutopilotRowDto(Guid DocumentId, string DocumentNumber, string DocumentType, string? ContactName, DateTime DocumentDate,
    decimal TotalAmount, decimal BalanceDue, string? Origin, string Overall, string OverallLabel, IReadOnlyList<AutopilotStepDto> Steps, string? Problem);

/// <summary>เรื่องที่ระบบทำต่อไม่ได้ — <c>ActionPath</c> = path ใต้ <c>api/companies/{cid}/</c> ที่ปุ่มเรียกด้วย POST (เซิร์ฟเวอร์กำหนด · หน้าไม่ประกอบเอง) ·
/// <c>ActionHref</c> = หน้าที่ปุ่มพาไป · อย่างใดอย่างหนึ่งหรือทั้งคู่</summary>
public record AutopilotIssueDto(string Kind, string Title, string Detail, DateTime At, Guid? DocumentId,
    string ActionLabel, string? ActionHref, string? ActionPath, string? SecondaryLabel, string? SecondaryHref);

public record AutopilotOptionDto(string Code, string Label);

public record AutopilotCatalogueDto(string Code, string Label, string Delivery, string DeliveryLabel, bool Wired);

public record AutopilotSettingsDto(bool BankAutoSettleEnabled, string QuotationAcceptAutopilotDefault,
    string DigestLocalTime, IReadOnlyList<AutopilotOptionDto> QuotationModes, IReadOnlyList<AutopilotCatalogueDto> Catalogue,
    // รอบ 257 ชุด 4 (A L12): บอกก่อนเปิดว่ารอบถัดไปไล่บรรทัดเงินเข้าย้อนหลัง N วัน (server computes)
    string? BankAutoSettleEnableNote = null,
    // รอบ 259-3 (E-M2): การ์ดศูนย์รวมค่าตั้งที่อยู่หน้าอื่น (สถานะ + ลิงก์ · อ่านอย่างเดียว) — ตัวประกอบเดียว Helpers/AutopilotSettingsHub
    IReadOnlyList<Accounting.Helpers.AutopilotSettingsHub.Item>? Hub = null);

public record UpdateAutopilotSettingsRequest(bool? BankAutoSettleEnabled, string? QuotationAcceptAutopilotDefault, string? DigestLocalTime);

public record QuotationAutopilotRowDto(Guid Id, string DocumentNumber, string? ContactName, DateTime? AcceptedAt, string? Mode, string EffectiveMode,
    string EffectiveLabel, string? Outcome, string? Message, Guid? ChildId, string? ChildNumber, bool CanRetry);

public record SetQuotationAutopilotModeRequest(string? Mode);

public record BankAutoSettleChoiceDto(Guid DocumentId, string DocumentNumber, string? ContactName, decimal ExpectedNet);

/// <summary><c>ContactId</c>/<c>ContactAutoSettleDisabled</c> = ผู้ติดต่อของใบที่ระบบเสนอ (null เมื่อเสนอหลายใบ) — ปุ่ม "ไม่ปิดหนี้อัตโนมัติกับผู้ติดต่อนี้"</summary>
public record BankAutoSettleQueueRowDto(Guid LineId, DateTime Date, decimal Amount, string? Description, string? Reference, string Verdict, string Reason,
    Guid? DocumentId, string? DocumentNumber, IReadOnlyList<BankAutoSettleChoiceDto> Choices, Guid? ContactId, string? ContactName, bool ContactAutoSettleDisabled);

public record SetContactBankAutoSettleRequest(bool Disabled);

public record BankAutoSettleConfirmRequest(Guid DocumentId);

/// <summary>M2: <c>LegacyUnknownSource</c> = บรรทัดก่อนรอบ 252 ไม่ทราบแหล่ง · <c>UserSuppliedSource</c> = ไฟล์/API/พิมพ์มือ — ทั้งสองกลุ่มเสนอเท่านั้นเสมอ</summary>
public record BankAutoSettleSimulationDto(int Days, int Lines, int WouldAutoSettle, int WouldSuggest, int NoMatch, int LegacyUnknownSource,
    int UserSuppliedSource, string Note);

public record AutopilotOverviewResponse(AutopilotSummaryDto Summary, IReadOnlyList<AutopilotRowDto> Rows, IReadOnlyList<AutopilotIssueDto> Issues,
    IReadOnlyList<QuotationAutopilotRowDto> Quotations, IReadOnlyList<BankAutoSettleQueueRowDto> BankQueue, AutopilotSettingsDto Settings);
