using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.FixedAsset;

public record CreateFixedAssetRequest(
    string AssetCode,
    string Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    DepreciationMethod DepreciationMethod = DepreciationMethod.StraightLine,
    Guid? AssetAccountId = null,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null,
    // When true, CreateAsync posts an acquisition entry Dr Asset / Cr
    // CreditAccountId (cash or A/P). The OCR "Register Asset" path posts
    // its own entry and leaves this false to avoid a double post.
    bool PostAcquisitionJournalEntry = false,
    Guid? CreditAccountId = null,
    // Asset type + lease-specific fields. Default Tangible keeps the create
    // flow exactly as it was for existing callers.
    AssetType AssetType = AssetType.Tangible,
    int? LeaseTermMonths = null,
    string? LessorName = null,
    decimal? MonthlyLeasePayment = null,
    Guid? LeaseLiabilityAccountId = null,
    /// <summary>Optional project the asset was acquired for. Periodic
    /// depreciation JEs will inherit this so depreciation cost lands
    /// in the right project's P&amp;L automatically.</summary>
    Guid? ProjectId = null,
    // Auto-registration จากเอกสารซื้อ (Expense/PI/PV ที่ลงผัง PPE)
    Guid? SourceDocumentId = null,
    Guid? SourceDocumentLineId = null,
    bool NeedsReview = false);

public record UpdateFixedAssetRequest(
    string? Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    Guid? AssetAccountId,
    Guid? DepreciationExpenseAccountId,
    Guid? AccumulatedDepreciationAccountId,
    Guid? ProjectId = null,
    // รอบ 200 (A12/E-04): เดิมหน้าเว็บส่งสามช่องนี้แต่ DTO ไม่มี ⇒ "แก้ไขสำเร็จ" โดยไม่มีผล · null = ไม่แตะ ·
    // แก้ได้ก่อนมีค่าเสื่อมลงบัญชี (Helpers/FixedAssetValuationEdit) · ราคาทุน/วันที่ซื้อ/ประเภท ไม่รับตอนแก้ (หน้าเว็บล็อกช่อง)
    int? UsefulLifeMonths = null,
    decimal? SalvageValue = null,
    DepreciationMethod? DepreciationMethod = null);

public record FixedAssetResponse(
    Guid Id,
    string AssetCode,
    string Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    DepreciationMethod DepreciationMethod,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    AssetStatus Status,
    DateTime? DisposalDate,
    decimal? DisposalAmount,
    DateTime CreatedAt,
    Guid? AssetAccountId = null,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null,
    AssetType AssetType = AssetType.Tangible,
    int? LeaseTermMonths = null,
    string? LessorName = null,
    decimal? MonthlyLeasePayment = null,
    Guid? ProjectId = null,
    bool NeedsReview = false,
    Guid? SourceDocumentId = null,
    /// <summary>เลขเอกสารที่สร้างสินทรัพย์ตัวนี้ — UI ต้องแสดงคู่กับรายการเสมอ
    /// เพราะเมื่อพบสินทรัพย์ซ้ำ ทางแก้อยู่ที่**เอกสาร** ไม่ใช่ที่ทะเบียน
    /// (ลบสินทรัพย์ทิ้งขณะใบต้นทางยังลง Dr 12210 = งบดุลกับทะเบียนไม่ตรงถาวร)</summary>
    string? SourceDocumentNumber = null,
    /// <summary>บรรทัดต้นทาง — แผงสินทรัพย์บนหน้าเอกสารใช้จับคู่ “บรรทัดไหนมีทะเบียนแล้ว”</summary>
    Guid? SourceDocumentLineId = null,
    /// <summary>รอบ 249: ระหว่างจัดหา/ติดตั้ง (ต้นทุนสะสมจากหลายใบ · ยังไม่คิดค่าเสื่อม) — หน้าทะเบียนแสดงป้าย + ปุ่มโอน</summary>
    bool IsUnderAcquisition = false,
    /// <summary>รอบ 249: วันที่พร้อมใช้งาน (จุดเริ่มค่าเสื่อม) · null = สินทรัพย์ใบเดียวแบบเดิม</summary>
    DateTime? AvailableForUseDate = null,
    /// <summary>รอบ 249 (ฝ่ายค้าน H3): ราคาตามสัญญาที่ระบุตอนสร้าง — ใช้เตือนเมื่อต้นทุนรวมเกิน</summary>
    decimal? ContractPrice = null);

public record DepreciationResponse(
    Guid Id,
    Guid FixedAssetId,
    int Year,
    int Month,
    decimal Amount,
    decimal AccumulatedAmount,
    decimal NetBookValue,
    bool IsPosted);

public record DisposeAssetRequest(
    DateTime DisposalDate,
    decimal DisposalAmount);

public record CalculateDepreciationRequest(
    int Year,
    int Month);

public record RevalueAssetRequest(
    decimal NewFairValue,
    DateTime RevaluationDate,
    string? Notes);

public record RevaluationResponse(
    Guid AssetId,
    string AssetCode,
    string AssetName,
    decimal OldNetBookValue,
    decimal NewFairValue,
    decimal RevaluationSurplus,
    DateTime RevaluationDate);

public record WriteOffAssetRequest(
    DateTime WriteOffDate,
    string? Reason);

/// <param name="Reason">เหตุผลที่ทบทวน — TFRS for NPAEs บทที่ 10 ให้ทบทวนสิ้นปี
/// และผู้สอบบัญชีถามเสมอว่าเปลี่ยนเพราะอะไร (เก็บลง AuditLog พร้อมค่าก่อน/หลัง)</param>
public record AdjustUsefulLifeRequest(
    int NewUsefulLifeMonths,
    decimal? NewSalvageValue,
    string? Reason = null,
    // รอบ 201 ทีม IN (A-IN3): เปลี่ยนวิธีคิดค่าเสื่อมไปข้างหน้า — null = คงวิธีเดิม (Helpers/DepreciationEstimateChange)
    DepreciationMethod? NewDepreciationMethod = null);

public record AssetCategoryResponse(
    string Category,
    int AssetCount,
    decimal TotalCost,
    decimal TotalNetBookValue);

public record AssetRegisterReportItem(
    string AssetCode,
    string Name,
    string? Category,
    string? Location,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    string Status,
    DateTime? DisposalDate,
    decimal? DisposalAmount);

public record AssetRegisterReport(
    DateTime ReportDate,
    List<AssetRegisterReportItem> Items,
    decimal TotalCost,
    decimal TotalAccumulatedDepreciation,
    decimal TotalNetBookValue,
    int TotalActive,
    int TotalDisposed,
    int TotalFullyDepreciated);

public record DepreciationScheduleItem(
    int Year,
    int Month,
    decimal OpeningNBV,
    decimal DepreciationAmount,
    decimal AccumulatedDepreciation,
    decimal ClosingNBV);

public record DepreciationScheduleReport(
    Guid AssetId,
    string AssetCode,
    string AssetName,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod,
    List<DepreciationScheduleItem> Schedule);

public record ImportFixedAssetRow(
    string AssetCode,
    string Name,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod);

public record ImportFixedAssetsResult(
    int TotalRows,
    int SuccessCount,
    int ErrorCount,
    List<string> Errors);

/// <summary>สถานะรายบรรทัดของเอกสารเทียบกับทะเบียนสินทรัพย์ — เซิร์ฟเวอร์ตัดสินว่าบรรทัดไหน
/// “เข้าข่ายสินทรัพย์” (ตัวจำแนกเดียวกับที่ใช้ตอนอนุมัติ) และมีทะเบียนแล้วหรือยัง ·
/// หน้าเอกสารแสดงอย่างเดียว ห้ามเดาจาก prefix 12 เอง</summary>
public record DocumentAssetLineStatus(
    Guid LineId, int LineOrder, string Description, decimal Amount, string? AccountCode,
    bool IsAssetAccount, Guid? AssetId, string? AssetCode, bool? AssetNeedsReview,
    bool IsAuxiliary = false,
    // ── รอบ 249 (ASSET_MULTI_DOCUMENT_DESIGN): บรรทัดนี้เป็นต้นทุนของสินทรัพย์หลายใบได้ไหม — เซิร์ฟเวอร์ตัดสิน (Helpers/AssetCostSourceRules) ──
    /// <summary>ผูกเป็นต้นทุนสินทรัพย์ได้ (มัดจำ 118xx · งานระหว่างก่อสร้าง · ผังสินทรัพย์) และยังเหลือยอด</summary>
    bool IsCostSourceEligible = false,
    /// <summary>เหตุที่ผูกไม่ได้ (null = ผูกได้/ไม่เกี่ยว)</summary>
    string? CostSourceProblem = null,
    /// <summary>ต้นทุนของบรรทัด (บาท · ยอดที่ลง GL จริง · ลดหนี้ติดลบ)</summary>
    decimal CostAmount = 0m,
    /// <summary>Σ ยอดที่ผูกกับสินทรัพย์ไว้แล้ว (ค่าบวก)</summary>
    decimal LinkedAmount = 0m,
    /// <summary>ยอดที่ยังผูกได้ (ค่าบวก)</summary>
    decimal RemainingToLink = 0m,
    /// <summary>สินทรัพย์ที่บรรทัดนี้ผูกอยู่ (แถวที่ยังไม่ปลด)</summary>
    List<AssetCostLinkRef>? Links = null);

/// <summary>การผูก 1 รายการของบรรทัด (แสดงบนหน้าเอกสาร)</summary>
public record AssetCostLinkRef(Guid AssetId, string AssetCode, string AssetName, decimal Amount, bool IsCapitalized, bool AssetIsUnderAcquisition);

public record DocumentAssetLinesResponse(
    Guid DocumentId, string DocumentNumber, string Status, bool IsIssued, bool TypeSupportsAutoRegister,
    List<DocumentAssetLineStatus> Lines, List<FixedAssetResponse> Assets);

public record RegisterFromDocumentResult(int Created, int Skipped, List<FixedAssetResponse> Assets, List<string> Notes);

// ═══════════════ รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md §5) ═══════════════

/// <summary>บรรทัดที่จะผูก · <paramref name="Amount"/> null = ยอดที่ยังเหลือผูกได้ทั้งหมด (ค่าบวกเสมอ — ใบลดหนี้ระบบใส่เครื่องหมายเอง)</summary>
public record AttachCostSourceItem(Guid DocumentLineId, decimal? Amount = null);

/// <param name="EntryDate">วันลงรายการโอน/ปรับต้นทุน เมื่อผูกหลังโอนแล้ว (ฝ่ายค้าน M5) — null = วันที่ของใบล่าสุด (ไม่ก่อนวันพร้อมใช้) ·
/// ต้องไม่ก่อนวันนั้น และต้องอยู่ในงวดที่เปิด (ใบมาทีหลังของงวดที่ปิดแล้ว ⇒ ระบุวันในงวดที่เปิด)</param>
public record AttachCostSourcesRequest(List<AttachCostSourceItem> Items, string? Note = null, DateTime? EntryDate = null);

/// <summary>สร้างสินทรัพย์ระหว่างจัดหาจากหลายบรรทัด — ยังไม่คิดค่าเสื่อมจนกว่าจะโอน (อายุ/วิธี/ซากตั้งตอนโอน)</summary>
public record CreateAssetFromSourcesRequest(
    string Name,
    List<AttachCostSourceItem> Items,
    string? Category = null,
    Guid? AssetAccountId = null,
    string? Location = null,
    string? SerialNumber = null,
    Guid? ProjectId = null,
    string? Note = null,
    /// <summary>ราคาตามสัญญา (ไม่รวม VAT) — ระบบเตือนเมื่อต้นทุนรวมเกิน (ไม่บล็อก)</summary>
    decimal? ContractPrice = null);

/// <summary>โอนเป็นสินทรัพย์พร้อมใช้ — JE Dr ผังสินทรัพย์ / Cr ผังต้นทาง · ค่าเสื่อมเริ่มจาก <paramref name="AvailableForUseDate"/></summary>
public record PlaceInServiceRequest(
    DateTime AvailableForUseDate,
    Guid AssetAccountId,
    int UsefulLifeMonths,
    DepreciationMethod DepreciationMethod = DepreciationMethod.StraightLine,
    decimal SalvageValue = 0m,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null);

public record ReleaseCostSourceRequest(string Reason);

/// <summary>แถวผูก 1 แถว — <paramref name="Status"/> = "Held" | "Capitalized" | "Released"</summary>
public record AssetCostSourceResponse(
    Guid Id, Guid DocumentId, string DocumentNumber, string DocumentType, DateTime DocumentDate,
    Guid DocumentLineId, string? LineDescription, string Kind, decimal Amount, decimal NonClaimableVatIncluded,
    string? SourceAccountCode, string Status, DateTime? CapitalizedAt, Guid? CapitalizationJournalEntryId,
    bool SourceDocumentVoided, DateTime? ReleasedAt, string? ReleasedReason, string? Note);

/// <summary>ต้นทุนของสินทรัพย์แยกตามใบ + ปัญหาที่เซิร์ฟเวอร์ตรวจ (หน้าแสดงอย่างเดียว)</summary>
public record AssetCostSourcesResponse(
    Guid AssetId, bool IsUnderAcquisition, DateTime? AvailableForUseDate,
    decimal HeldCost, decimal CapitalizedCost, decimal RegisterCost,
    List<AssetCostSourceResponse> Sources, List<string> Problems);

/// <summary>บรรทัดที่ผูกได้ (หน้าค้นหาใบเพื่อผูก) — <paramref name="Problem"/> ไม่ว่าง = แสดงแต่ผูกไม่ได้</summary>
public record LinkableCostLine(
    Guid DocumentId, string DocumentNumber, string DocumentType, DateTime DocumentDate, string? ContactName,
    Guid DocumentLineId, string? Description, string? AccountCode, string Kind,
    decimal CostAmount, decimal LinkedAmount, decimal RemainingToLink,
    string? RegisteredAssetCode, bool RegisteredAssetAbsorbable, string? Problem);

public record AttachCostSourcesResult(FixedAssetResponse Asset, int Linked, List<string> AbsorbedDraftAssetCodes, Guid? JournalEntryId);

/// <summary>รอบ 250 WP-03 — ตัวนับบนแท็บชนิดของหน้าทะเบียนสินทรัพย์ (นับทั้งฐานตามคำค้น/สถานะ · ตัวกรองเดียวกับรายการ)</summary>
public record FixedAssetTypeCounts(int Total, int Tangible, int Intangible, int RightOfUse, int InvestmentProperty, int NeedsReview);
