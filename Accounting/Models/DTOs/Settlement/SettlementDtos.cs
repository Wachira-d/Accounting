using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Settlement;

// ═══════════════════════════════════════════════════════════════════════
// Settlement — DTO ของทีม B (นำเข้า · จัดประเภท · จับคู่ · ตั้งค่าช่องทาง) — รอบ 198 เฟส 1
// enum ออกเป็น "ชื่อ" (JsonStringEnumConverter ใน Program.cs) · หน้าเว็บห้ามตัดสินด้วยตัวเลข
// ═══════════════════════════════════════════════════════════════════════

/// <summary>หัวของรอบโอนที่ผู้ใช้ระบุตอนนำเข้า — ยอดเข้าธนาคารจริง (NetPayout) เป็นตัวตั้ง ระบบไม่เดา</summary>
public sealed record SettlementBatchHeaderRequest
{
    public Guid ChannelId { get; init; }
    /// <summary>เลขอ้างอิงรอบโอนของผู้ให้บริการ — ว่างได้เมื่อไฟล์มีคอลัมน์เลขรอบโอนที่มีค่าเดียว</summary>
    public string? PayoutRef { get; init; }
    /// <summary>วันที่เงินเข้าธนาคาร — บังคับ</summary>
    public DateTime? PayoutDate { get; init; }
    /// <summary>ยอดที่โอนเข้าธนาคารจริง — บังคับ (0 ได้เมื่อรอบนั้นไม่มีการโอน)</summary>
    public decimal? NetPayout { get; init; }
    public decimal OpeningWalletBalance { get; init; }
    public decimal ClosingWalletBalance { get; init; }
    public DateTime? PeriodFrom { get; init; }
    public DateTime? PeriodTo { get; init; }
    public Guid? BankAccountId { get; init; }
    public string? Note { get; init; }
}

/// <summary>นำเข้าไฟล์ settlement report</summary>
public sealed record SettlementFileImportRequest
{
    public SettlementBatchHeaderRequest Header { get; init; } = new();
    /// <summary>การจับคู่คอลัมน์ของครั้งนี้ (JSON) — null = ใช้ที่ช่องทางจำไว้</summary>
    public string? ColumnMapJson { get; init; }
    /// <summary>จำการจับคู่นี้ไว้ที่ช่องทาง (ค่าเริ่มต้น: จำ)</summary>
    public bool RememberColumnMap { get; init; } = true;
}

/// <summary>ผลตรวจไฟล์ก่อนนำเข้า (หน้าจับคู่คอลัมน์)</summary>
/// <param name="SampleRows">แถวตัวอย่าง — <b>ตัด PII แล้ว</b> · คอลัมน์ที่อยู่ในรายการ "ไม่ใช้" แสดงเป็น "[ไม่ใช้]"</param>
/// <param name="FromSavedMap">การจับคู่ที่ช่องทางจำไว้ใช้กับไฟล์นี้ได้ครบ</param>
/// <param name="SavedMapMatch">สัดส่วนหัวคอลัมน์ของการจับคู่ที่จำไว้ที่พบในไฟล์ (0–1) — ต่ำกว่า 1 = รูปแบบไฟล์เปลี่ยน</param>
/// <param name="Issues">การจับคู่ที่เสนอยังขาดอะไร (ผู้ใช้ต้องเลือกเองก่อนนำเข้า) — ข้อเสนอมาจากชั้น local (หัวคอลัมน์ที่รู้จัก) ไม่ใช่ AI</param>
public sealed record SettlementFileInspection(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    string SuggestedColumnMapJson,
    bool FromSavedMap,
    decimal SavedMapMatch,
    IReadOnlyList<string> UnmappedHeaders,
    IReadOnlyList<string> Issues);

/// <summary>ผลการนำเข้า — idempotent: ไฟล์เดิมซ้ำ ⇒ <c>ImportedLines = 0</c> · <c>SkippedDuplicates</c> = จำนวนที่มีอยู่แล้ว</summary>
/// <param name="CreatedNew">สร้างรอบโอนใหม่ (false = เติมบรรทัดใหม่เข้ารอบเดิมที่ยังไม่ลงบัญชี หรือไม่มีอะไรใหม่)</param>
/// <param name="SkippedRows">แถวที่ adapter ข้ามโดยตั้งใจ (แถวสรุปยอด · ยอด 0) พร้อมเหตุผล</param>
/// <param name="Warnings">สิ่งที่ผู้ใช้ควรรู้ก่อนลงบัญชี (ค่าธรรมเนียมที่ยังไม่รู้ · บรรทัดที่เครื่องหมายขัดประเภท ฯลฯ)</param>
public sealed record SettlementImportResult(
    SettlementBatchView Batch,
    bool CreatedNew,
    int ImportedLines,
    int SkippedDuplicates,
    IReadOnlyList<string> SkippedRows,
    IReadOnlyList<string> Warnings);

/// <param name="PostingArtifacts">เอกสาร/การรับชำระที่ยังไม่ถูกยกเลิกซึ่งการลงบัญชีสร้างให้รอบนี้ (มี &gt; 0 ขณะยังไม่ Posted = ลงค้างครึ่งทาง ⇒
/// แก้/ยกเลิกรอบไม่ได้ · ฝ่ายค้าน C-1) · <c>null</c> = ไม่ได้ตรวจ (มุมมองรายการ) — ห้ามตีความเป็น 0</param>
public sealed record SettlementBatchView(
    Guid Id,
    Guid ChannelId,
    string ChannelName,
    string PayoutRef,
    DateTime PayoutDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    string Currency,
    decimal NetPayout,
    decimal OpeningWalletBalance,
    decimal ClosingWalletBalance,
    SettlementBatchStatus Status,
    SettlementSourceKind SourceKind,
    Guid? SourceFileAttachmentId,
    Guid? BankAccountId,
    decimal LinesTotal,
    int LineCount,
    int UnclassifiedCount,
    int NeedsMatchDecisionCount,
    int AutoSummaryCount,
    bool AnyClassifiedByAi,
    string? Note,
    DateTime CreatedAt,
    IReadOnlyList<SettlementLineView> Lines,
    int? PostingArtifacts = null);

/// <param name="LineTypeLabel">ป้ายไทยของประเภท (จาก <c>SettlementLineTypeRules</c> — หน้าเว็บไม่ต้องมีตารางป้ายเอง)</param>
/// <param name="ClassifyUsedAi">ครู (AI ภายนอก) ถูกเรียกจริงตอนจัดประเภทบรรทัดนี้ — ป้าย "🤖 AI แนะนำ" เฉพาะเมื่อ true ·
/// ไม่งั้น "⚙️ ระบบแนะนำ" (กฎเหล็ก #1)</param>
/// <param name="MatchNote">เหตุผลของสถานะจับคู่/สิ่งที่ผู้ใช้ต้องตัดสิน</param>
/// <param name="MatchDecidedByUser">คนตัดสินการจับคู่เอง — การจับคู่อัตโนมัติไม่ทับ (หน้าเว็บติดป้าย "👤 ผู้ใช้เลือก" · R-B1)</param>
public sealed record SettlementLineView(
    Guid Id,
    int Seq,
    SettlementLineType LineType,
    string LineTypeLabel,
    string? RawTypeLabel,
    string? Description,
    DateTime? TxnDate,
    string? ExternalOrderId,
    string? ExternalTxnId,
    decimal Amount,
    decimal? VatAmount,
    decimal? WhtAmount,
    SettlementClassifiedBy ClassifiedBy,
    bool ClassifyUsedAi,
    Guid? ClassifyAiFeedbackId,
    SettlementMatchStatus MatchStatus,
    Guid? MatchedDocumentId,
    Guid? PaymentIntentId,
    Guid? ReservationId,
    string? MatchNote,
    IReadOnlyList<SettlementMatchCandidateView> MatchCandidates,
    Guid? OverrideAccountId,
    string? AdjustmentReason,
    bool MatchDecidedByUser = false);

public sealed record SettlementMatchCandidateView(string Kind, Guid Id, string Label, decimal? OpenAmount, bool CanReceive, bool IsRefundTarget);

/// <summary>ผู้ใช้เลือก/แก้ประเภทของบรรทัด (ปิดลูปการเรียนรู้ — กฎเหล็ก #1)</summary>
public sealed record SettlementReclassifyRequest
{
    public SettlementLineType LineType { get; init; }
    /// <summary>บังคับเมื่อเป็นประเภทที่ต้องมีเหตุผล (Adjustment)</summary>
    public string? AdjustmentReason { get; init; }
    /// <summary>ผังที่ผู้ใช้เลือกเอง — บังคับสำหรับ Adjustment</summary>
    public Guid? OverrideAccountId { get; init; }
    /// <summary>ใช้ประเภทเดียวกันกับบรรทัดอื่นในรอบโอนนี้ที่ป้าย+เครื่องหมายเดียวกันซึ่งผู้ใช้ยังไม่ได้เลือกเอง (ค่าเริ่มต้น: ใช้)</summary>
    public bool ApplyToSameLabel { get; init; } = true;
}

/// <summary>ผู้ใช้ตัดสินการจับคู่ของบรรทัดขาย/คืนเงิน</summary>
public sealed record SettlementAssignMatchRequest
{
    /// <summary>เอกสารขายที่ผู้ใช้เลือก (ต้องเป็นของบริษัทนี้)</summary>
    public Guid? DocumentId { get; init; }
    /// <summary>ยืนยันว่า "ไม่มีเอกสารในระบบ — ให้เข้าใบขายสรุปรายวัน" (ใช้ได้กับบรรทัดขายเท่านั้น · คืนเงินต้องมีใบเดิม)</summary>
    public bool UseDailySummary { get; init; }
}

/// <summary>ประกอบรอบโอนจาก PaymentIntent ของ gateway ในระบบ</summary>
public sealed record SettlementIntentBatchRequest
{
    public SettlementBatchHeaderRequest Header { get; init; } = new();
}

// ═══ ช่องทาง ═══

/// <summary>สร้าง/แก้ช่องทาง — ผู้ติดต่อของแพลตฟอร์มระบุด้วย Id หรือเลขภาษี+สาขา (<c>ContactTaxBranchKey</c>)</summary>
public sealed record SettlementChannelUpsertRequest
{
    public SettlementChannelKind Kind { get; init; } = SettlementChannelKind.Marketplace;
    public string? DisplayName { get; init; }
    public string? AdapterCode { get; init; }
    public string? ColumnMapJson { get; init; }
    public Guid? CounterpartyContactId { get; init; }
    public string? CounterpartyTaxId { get; init; }
    public string? CounterpartyBranchCode { get; init; }
    public Guid? PaymentProviderConfigId { get; init; }
    /// <summary>ผังพักที่ผู้ใช้เลือกเอง — null = ระบบสร้าง/ผูก 11341–11349 ให้ (<c>SettlementChannelAccounts</c>)</summary>
    public Guid? ClearingAccountId { get; init; }
    public Guid? ReserveAccountId { get; init; }
    public Guid? DisputeAccountId { get; init; }
    public string? FeeAccountMapJson { get; init; }
    public SettlementFeeVatMode FeeVatMode { get; init; } = SettlementFeeVatMode.ThaiVat7;
    public SettlementFeeWhtMode FeeWhtMode { get; init; } = SettlementFeeWhtMode.None;
    public SettlementRevenueModel RevenueModel { get; init; } = SettlementRevenueModel.GrossWithFees;
    public string? Currency { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <param name="Warnings">สิ่งที่ควรรู้ (ไม่บล็อกการบันทึก) — เช่น บริษัทนิติบุคคลที่ตั้ง WHT ค่าธรรมเนียม = None (report-S1 D5)</param>
/// <param name="HasBatches">มีรอบโอนที่ยังไม่ยกเลิกแล้ว — ชนิด/ผัง gateway/ผังพัก แก้ไม่ได้ (หน้าจอล็อกช่อง)</param>
public sealed record SettlementChannelView(
    Guid Id,
    SettlementChannelKind Kind,
    string DisplayName,
    string? AdapterCode,
    string? ColumnMapJson,
    Guid? CounterpartyContactId,
    string? CounterpartyName,
    Guid? PaymentProviderConfigId,
    Guid? ClearingAccountId,
    string? ClearingAccountCode,
    string? ClearingAccountName,
    Guid? ReserveAccountId,
    Guid? DisputeAccountId,
    string? FeeAccountMapJson,
    SettlementFeeVatMode FeeVatMode,
    SettlementFeeWhtMode FeeWhtMode,
    SettlementRevenueModel RevenueModel,
    string Currency,
    bool IsActive,
    bool HasBatches,
    IReadOnlyList<string> Warnings);
