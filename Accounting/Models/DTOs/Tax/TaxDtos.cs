using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

public record CreateTaxReportRequest(
    TaxType TaxType,
    int Year,
    int Month);

public record TaxReportResponse(
    Guid Id,
    TaxType TaxType,
    int Year,
    int Month,
    TaxReportStatus Status,
    DateTime? FiledDate,
    decimal OutputVat,
    decimal InputVat,
    decimal NetVat,
    decimal TotalIncome,
    decimal TotalTaxWithheld,
    List<TaxReportLineResponse> Lines,
    string? Notes = null,
    // ── ข้อมูลผู้ประกอบการ (สำหรับ header ฟอร์มราชการ §87) — เติมตอน GetTaxReportAsync ──
    string? CompanyName = null,
    string? CompanyTaxId = null,
    string? CompanyBranchCode = null,
    /// <summary>ภาษีเงินได้นิติบุคคล (ภ.ง.ด.50/51) — null สำหรับรายงานชนิดอื่น.
    /// แยกจาก TotalTaxWithheld ที่เป็นยอดหัก ณ ที่จ่าย</summary>
    decimal? CitAmount = null,
    // ── หลักฐานการยื่น (D2-B1a) — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดงอย่างเดียว ──
    /// <summary>เลขรับ/เลขอ้างอิงจากกรมสรรพากร (null = ยังไม่มี)</summary>
    string? FilingNumber = null,
    /// <summary>วันที่กรมสรรพากรรับแบบ (จากใบรับ/หน้าจอ e-Filing)</summary>
    DateTime? FilingAcknowledgedAt = null,
    /// <summary>ระดับหลักฐาน — NotFiled / DeclaredByUser / ConfirmedByFilingNumber</summary>
    string? FilingEvidence = null,
    /// <summary>ป้ายสั้นบนจอ ("ร่าง" / "บันทึกว่ายื่นแล้ว (รอเลขรับ)" / "ยื่นแล้ว")</summary>
    string? FilingLabel = null,
    /// <summary>ประโยคอธิบายสิ่งที่ระบบรู้จริง</summary>
    string? FilingDetail = null,
    /// <summary>ทางไปต่อของผู้ใช้ (null = ไม่มีอะไรต้องทำ)</summary>
    string? FilingNextStep = null,
    /// <summary>งวดนี้ถูกล็อก (เอกสาร/JE แก้ไม่ได้) หรือไม่</summary>
    bool FilingPeriodLocked = false,
    /// <summary>ยังค้างเลขรับที่ต้องตามเก็บ — รวมแถวเก่าที่ประทับ Filed
    /// ไว้ก่อนรอบนี้โดยไม่มีเลขรับ</summary>
    bool NeedsFilingNumber = false,
    /// <summary>ความสดของตัวเลข ภ.ง.ด.50/51 (Q12) — null สำหรับรายงานชนิดอื่น
    /// และเมื่อตรวจไม่สำเร็จ (หน้าเว็บต้องแสดง "ยังไม่ได้ตรวจ" ไม่ใช่ "ตรงแล้ว")</summary>
    TaxReportFreshnessResponse? Freshness = null);

/// <summary>
/// "ตัวเลขบนรายงาน ภ.ง.ด.50/51 ยังตรงกับข้อมูลต้นทางไหม" — เซิร์ฟเวอร์ตัดสิน
/// (<c>Helpers/CitReportFreshness</c>) หน้าเว็บแสดงอย่างเดียว
/// </summary>
/// <param name="Level">UpToDate · SourceTouched · Stale · StaleAfterFiling</param>
/// <param name="Message">ประโยคที่โชว์ (null = ไม่มีอะไรต้องบอก)</param>
/// <param name="NeedsAttention">ต้องขึ้นแถบเตือนไหม</param>
/// <param name="CanRegenerate">ปุ่ม "สร้างใหม่" ใช้ได้ไหม</param>
/// <param name="GeneratedAt">เวลาที่ตัวเลขชุดนี้ถูกคำนวณ (UTC)</param>
/// <param name="ChangedSourceCount">จำนวนเอกสาร+สมุดรายวันในรอบที่ขยับหลังจากนั้น</param>
/// <param name="RevenueDelta">รายได้สด − ที่เก็บไว้ (บาท)</param>
/// <param name="ExpenseDelta">ค่าใช้จ่ายสด − ที่เก็บไว้ (บาท)</param>
public record TaxReportFreshnessResponse(
    string Level,
    string? Message,
    bool NeedsAttention,
    bool CanRegenerate,
    DateTime GeneratedAt,
    int ChangedSourceCount,
    decimal RevenueDelta,
    decimal ExpenseDelta);

/// <summary>
/// แถวทบทวนรายงาน ภ.ง.ด.50/51 ที่บันทึกไว้ เทียบกับยอดคำนวณสดด้วยกติกาปัจจุบัน (อ่านอย่างเดียว · ทีม O 2026-10-22) —
/// หลังแก้ "ใบกลับรายการถูกนับแต่ใบเดิมไม่ถูกนับ" (Helpers/ReportableJournal) รายงานที่สร้างก่อนแก้อาจเก็บฐานต่ำกว่าจริง ·
/// ร่าง ⇒ กด "สร้างใหม่" ได้ (มี audit) · ยื่น/ล็อกแล้ว ⇒ ระบบไม่แตะ ให้ผู้ใช้พิจารณายื่น ภ.ง.ด.50 เพิ่มเติม
/// </summary>
/// <param name="Level">UpToDate · SourceTouched · Stale · StaleAfterFiling (Helpers/CitReportFreshness)</param>
/// <param name="SuggestedAction">None · Regenerate · ConsiderAmendedFiling</param>
public record CitRecomputeReviewRow(
    Guid ReportId,
    int Year,
    string Status,
    bool AlreadyFiled,
    decimal StoredRevenue,
    decimal FreshRevenue,
    decimal StoredExpense,
    decimal FreshExpense,
    decimal RevenueDelta,
    decimal ExpenseDelta,
    string Level,
    string? Message,
    bool CanRegenerate,
    string SuggestedAction);

public record UpdateTaxReportRequest(
    string? Notes,
    List<UpdateTaxReportLineRequest>? Lines);

public record UpdateTaxReportLineRequest(
    Guid Id,
    decimal? IncomeAmount,
    decimal? TaxRate,
    decimal? TaxAmount,
    string? Description,
    bool? Excluded = null);

public record PullableDocumentDto(
    Guid Id,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    string ContactName,
    decimal SubTotal,
    decimal VatAmount,
    bool IsInput,
    // รอบ 225 — ดึงหลายใบ: ยอดที่ "จะเข้ารายงานจริง" เป็นบาท (ตัวคำนวณเดียวกับบรรทัดที่ดึง · VAT ฝั่งซื้อเฉพาะบรรทัดที่เคลมได้)
    // ⇒ ยอดรวมใน footer ของหน้าตรงกับยอดที่บวกเข้ารายงาน · ป้าย "ย้อนเดือน" คำนวณที่ server
    decimal IncomeThb = 0m,
    decimal TaxThb = 0m,
    string? ContactTaxId = null,
    DateTime? TaxMonth = null,
    int MonthsLate = 0,
    // ฝั่งซื้อ: เดือนภาษีของใบไม่มีรายงาน ภ.พ.30 ในระบบ (อาจยื่นนอกระบบ — ดึงแล้วอาจเคลมซ้ำ)
    bool TaxMonthHasNoReport = false);

/// <summary>รายการ "ดึงเอกสาร" แบบแบ่งหน้า (รอบ 225) — <see cref="Total"/> = จำนวนใบที่ดึงได้ทั้งหมดตามตัวกรอง
/// (ไม่ใช่แค่หน้าที่โชว์) ⇒ ปุ่ม "เลือกทั้งหมด" บอกได้ตรงว่าเลือก N จาก M · <see cref="ScanCapped"/> = ช่วงวันที่กว้างจน
/// สแกนไม่หมด (ยอด Total อาจต่ำกว่าจริง) ⇒ หน้าบอกให้แคบช่วงลง</summary>
public record PullableDocumentListResponse(
    List<PullableDocumentDto> Items,
    int Total,
    int Offset,
    int Limit,
    bool HasMore,
    bool ScanCapped,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime LateWindowStart,
    int MaxPerPull,
    int TotalInput,
    int TotalOutput,
    List<PullableMonthBucket> Months,
    // เดือนในกรอบ "ย้อน 6 เดือน" ที่ไม่มีรายงาน ภ.พ.30 ในระบบ (ป้าย พ.ศ.) — หน้าเตือน "ถ้ายื่นนอกระบบแล้ว อย่าดึงซ้ำ"
    List<string> MonthsWithoutReport);

/// <summary>รอบ 269 — รายงาน ภ.พ.30 ที่บันทึกไว้ซึ่งช่อง 7/8 ต่างจากการคำนวณสด (กติกาเจ้าของการขาย) · อ่านอย่างเดียว ·
/// <paramref name="PeriodLabel"/> = "ดด/พ.ศ." คำนวณที่ server · <paramref name="SuggestedAction"/> = Regenerate / ConsiderAmendedFiling</summary>
public record Pp30SalesBoxReviewRow(Guid ReportId, int Year, int Month, string PeriodLabel, string Status, bool AlreadyFiled,
    decimal StoredZeroRated, decimal FreshZeroRated, decimal StoredExempt, decimal FreshExempt,
    string SuggestedAction, string? Message, string RuleCode);

/// <summary>บรรทัด "ดึงเข้างวด" ในรายงานร่างที่ตอนนี้ไม่ผ่านด่านดึง (รอบ 225 S3 — อ่านอย่างเดียว)</summary>
public record IneligiblePulledLineDto(Guid ReportId, int Year, int Month, Guid LineId, Guid DocumentId, string? DocumentNumber,
    bool LineExcluded, string? RuleCode, string? Reason);

/// <summary>จำนวนใบที่ดึงได้ต่อเดือน (ตามวันที่เอกสาร · ตามคำค้น/ฝั่งที่เลือก) — ป้าย พ.ศ. คำนวณที่ server ·
/// หน้าใช้เติมตัวเลือก "ตั้งแต่เดือน/ถึงเดือน" แบบเดียวกับหน้าเอกสาร</summary>
public record PullableMonthBucket(int Year, int Month, string Label, int Count, bool IsReportMonth);

public record PullDocumentRequest(Guid DocumentId);

/// <summary>ดึงหลายใบในครั้งเดียว (รอบ 225) — สูงสุด <c>TaxReportPullEligibility.MaxPerCall</c> ใบ</summary>
public record PullDocumentsRequest(List<Guid> DocumentIds);

/// <summary>ผลรายใบ: Added = เข้ารายงานแล้ว · ไม่ Added = ข้าม พร้อมเหตุผลไทย + RuleCode</summary>
public record PullDocumentItemResult(Guid DocumentId, string? DocumentNumber, bool Added, string? RuleCode, string? Reason);

/// <summary>ผลดึงหลายใบ — แบบ partial: ใบที่ผ่านเข้าในธุรกรรมเดียว ใบที่ไม่ผ่านข้ามพร้อมเหตุผล ·
/// <see cref="Report"/> = รายงานล่าสุดหลังดึง (บรรทัด + ยอดรวม) ให้หน้า refresh ได้ทันที</summary>
public record PullDocumentsResult(
    int AddedCount,
    int SkippedCount,
    List<PullDocumentItemResult> Items,
    TaxReportResponse Report);

public record TaxReportLineResponse(
    Guid Id,
    int LineOrder,
    string? TaxPayerId,
    string? TaxPayerName,
    DateTime TransactionDate,
    string? Description,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? IncomeTypeCode,
    bool IsExcluded = false,
    // ── ฟอร์มราชการ §87 (ฉบับที่ 104) — เติมตอน GetTaxReportAsync ──
    Guid? DocumentId = null,
    string? InvoiceNumber = null,   // เลขที่ใบกำกับ (ขาย=เลขเรา / ซื้อ=เลขผู้ขาย)
    string? BranchCode = null,      // สาขาผู้ขาย/ผู้ซื้อ (00000=สนญ.)
    // ชนิดเอกสาร "ตามกฎหมาย" ของแถวนี้ — ตัวย่อของเลขที่เอกสาร (TIV/REC/RV/INV)
    // บอกแค่ชนิดข้อมูลในระบบ ไม่ได้บอกว่ากระดาษใบนั้นคืออะไร: ใบที่หัวพิมพ์
    // "ใบกำกับภาษี/ใบเสร็จรับเงิน" เหมือนกันเป๊ะ อยู่ได้ทั้งบน TIV- และ REC-
    // ⇒ นักบัญชีที่เปิดรายงานเห็นเลขปนกันแล้วไล่ไม่ออกว่าใบไหนเป็นใบกำกับจริง
    // ค่านี้มาจาก resolver หัวเอกสารตัวเดียวกับที่พิมพ์ลงกระดาษ (ห้ามคำนวณเอง)
    string? DocumentKindLabel = null,
    /// <summary>ฝั่งของบรรทัด: <c>"output"</c> ภาษีขาย · <c>"input"</c> ภาษีซื้อ ·
    /// <c>"summary"</c> ยอดรวมของแบบ ภ.พ.30 (ไม่มีวันที่/เลขที่ใบ ห้ามลงตาราง §87)
    ///
    /// <para>เซิร์ฟเวอร์คำนวณจาก <c>Helpers/VatReportLineKind</c> — หน้าเว็บ<b>แสดง
    /// อย่างเดียว</b> ห้ามจำแนกจาก <c>IncomeTypeCode</c> เอง (เดิม tax.html มีสำเนา
    /// กติกา 2 ชุด ซึ่งเป็นทางที่รหัสใหม่หลุดออกจากการจัดฝั่งโดยไม่มีอะไรฟ้อง)</para></summary>
    string Side = "output");
