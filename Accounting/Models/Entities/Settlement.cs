using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ═══════════════════════════════════════════════════════════════════════
// Settlement (wallet → ธนาคาร) — รอบ 198 เฟส 1 ทีม A (report-S2 §3.2)
//
// หน่วยความจริง = "รอบโอน" ของผู้ให้บริการ · ยอดเข้าธนาคารจริง (NetPayout) เป็นตัวตั้ง ·
// ทุกบรรทัดต้องจัดประเภทก่อนลงบัญชี (Unclassified ห้ามลง) · สมการของ batch:
//     Σ บรรทัด.Amount = NetPayout + (ClosingWalletBalance − OpeningWalletBalance)   (±0.01)
// ตัวคิดแผนลงบัญชีตัวเดียว: Helpers/SettlementBatchMath.Plan · กติการายประเภท: Helpers/SettlementLineTypeRules
// ตาราง/ดัชนีต้องตรงกับ DatabaseMigrationHelper.SettlementSchemaStatements
// ═══════════════════════════════════════════════════════════════════════

/// <summary>ช่องทางที่เงินพักอยู่ก่อนโอนเข้าบัญชีเรา (1 แถว/แพลตฟอร์ม/บัญชีร้าน)</summary>
public class SettlementChannel : TenantEntity
{
    public SettlementChannelKind Kind { get; set; } = SettlementChannelKind.Marketplace;
    /// <summary>ชื่อที่ผู้ใช้เห็น เช่น "Shopee ร้านหลัก" — ใช้ตั้งชื่อผังพักย่อย "ลูกหนี้แพลตฟอร์ม {DisplayName}"</summary>
    public string DisplayName { get; set; } = null!;
    /// <summary>รหัส adapter ที่อ่านไฟล์ (<c>generic-column-map</c> · <c>payment-intents</c> · ฯลฯ — ทีม B)</summary>
    public string? AdapterCode { get; set; }
    /// <summary>คอลัมน์ในไฟล์ → ช่องของบรรทัด ที่ผู้ใช้จับคู่ไว้ (ระบบจำ) — รูปแบบเป็นของ adapter</summary>
    public string? ColumnMapJson { get; set; }
    /// <summary>ผู้ติดต่อ = นิติบุคคลของแพลตฟอร์ม (ผู้ออกใบกำกับค่าธรรมเนียม · ผู้รับเงินใน 50 ทวิ) — คีย์เลขภาษี+สาขา (ContactTaxBranchKey)</summary>
    public Guid? CounterpartyContactId { get; set; }
    /// <summary>ผูกกับ gateway ในระบบ (ถ้ามี) — ช่องทาง gateway ใช้บัญชีพักของ config นั้น (11340) เพราะ PaymentIntent ลงไว้ที่นั่นแล้ว</summary>
    public Guid? PaymentProviderConfigId { get; set; }

    /// <summary>ผังพัก "ลูกหนี้แพลตฟอร์ม" ของช่องทางนี้ (11341–11349 · DECISIONS ข้อ 4) —
    /// null = ยังไม่ผูก ⇒ <c>SettlementChannelAccounts.EnsureClearingAccountAsync</c> สร้าง/ผูกให้ · ลงบัญชีไม่ได้จนกว่าจะมี</summary>
    public Guid? ClearingAccountId { get; set; }
    /// <summary>ผังเงินที่ผู้ให้บริการกันไว้ — null = 11350</summary>
    public Guid? ReserveAccountId { get; set; }
    /// <summary>ผังพัก chargeback ระหว่างรอผล — null = 11320 ลูกหนี้อื่น</summary>
    public Guid? DisputeAccountId { get; set; }
    /// <summary>JSON object: บทบาทผัง → AccountId (Guid) เช่น <c>{"commission":"…","payment_fee":"…"}</c> ·
    /// คีย์ = <c>SettlementAccountRoles</c> · ไม่ระบุ = ผังมาตรฐานของบทบาทนั้น · อ่านด้วย
    /// <c>SettlementLineTypeRules.ParseFeeAccountMap</c> ตัวเดียว</summary>
    public string? FeeAccountMapJson { get; set; }
    /// <summary>JSON object: ประเภทบรรทัดค่าธรรมเนียม → รหัสประเภทเงินได้ (หรือ <c>"none"</c> = ไม่หัก) ที่ผู้ทำบัญชีจำแนกไว้ครั้งเดียว ·
    /// null = ค่าตั้งต้น (ไทย = ตารางประเภทบรรทัด · ต่างประเทศ = ค่าคอม/ค่าธรรมเนียม 40(2)) · อ่านด้วย <c>SettlementWhtIncomeType.ParseMap</c> ตัวเดียว
    /// (รอบ 200 ทีม WF · คำตัดสินข้อ 41)</summary>
    public string? WhtIncomeTypeMapJson { get; set; }

    public SettlementFeeVatMode FeeVatMode { get; set; } = SettlementFeeVatMode.ThaiVat7;
    /// <summary>ค่าเริ่มต้น None + เตือนบริษัทนิติบุคคล (DECISIONS · report-S1 D5)</summary>
    public SettlementFeeWhtMode FeeWhtMode { get; set; } = SettlementFeeWhtMode.None;
    /// <summary>ค่าเริ่มต้น gross (DECISIONS ข้อ 1)</summary>
    public SettlementRevenueModel RevenueModel { get; set; } = SettlementRevenueModel.GrossWithFees;
    public string Currency { get; set; } = "THB";
    public bool IsActive { get; set; } = true;
}

/// <summary>รอบโอน 1 รอบของผู้ให้บริการ (payout) — <c>NetPayout</c> คือยอดที่เข้าธนาคารจริง (ตัวตั้ง)</summary>
public class SettlementBatch : TenantEntity
{
    public Guid ChannelId { get; set; }
    public SettlementChannel Channel { get; set; } = null!;

    /// <summary>เลขอ้างอิงรอบโอนของผู้ให้บริการ — unique ต่อช่องทาง (กันนำเข้าซ้ำ)</summary>
    public string PayoutRef { get; set; } = null!;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    /// <summary>วันที่เงินเข้าธนาคาร (วันที่ของ JE รอบโอน)</summary>
    public DateTime PayoutDate { get; set; }
    public string Currency { get; set; } = "THB";
    /// <summary>อัตราแลกเปลี่ยน (สกุลต่างประเทศ) — เฟส 1 รองรับ THB เท่านั้น</summary>
    public decimal? FxRate { get; set; }

    /// <summary>ยอดใน wallet ต้นรอบ (ติดลบได้ = ยอดติดลบยกมา)</summary>
    public decimal OpeningWalletBalance { get; set; }
    /// <summary>ยอดใน wallet ปลายรอบ (ติดลบได้ = แพลตฟอร์มจะหักรอบถัดไป)</summary>
    public decimal ClosingWalletBalance { get; set; }
    /// <summary>ยอดที่โอนเข้าธนาคารจริงรอบนี้ — <b>ตัวตั้ง</b> (0 ได้เมื่อไม่มีการโอน)</summary>
    public decimal NetPayout { get; set; }

    public SettlementBatchStatus Status { get; set; } = SettlementBatchStatus.Imported;
    public SettlementSourceKind SourceKind { get; set; } = SettlementSourceKind.CsvImport;
    /// <summary>ไฟล์ต้นฉบับ — เปิดผ่าน IAttachmentAccessGate เท่านั้น · retention 5 ปี</summary>
    public Guid? SourceFileAttachmentId { get; set; }
    /// <summary>บัญชีธนาคารที่รับเงิน (BankAccount.Id)</summary>
    public Guid? BankAccountId { get; set; }
    /// <summary>รายการเดินบัญชีที่จับคู่แล้ว (สถานะ BankMatched)</summary>
    public Guid? BankTransactionId { get; set; }
    public Guid? PayoutJournalEntryId { get; set; }
    /// <summary>JSON array ของ Document.Id ที่สร้างจาก batch นี้ (ใบค่าธรรมเนียม · ใบขายสรุปรายวัน)</summary>
    public string? FeeDocumentIdsJson { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? PostedBy { get; set; }
    public string? Note { get; set; }

    public ICollection<SettlementLine> Lines { get; set; } = new List<SettlementLine>();
}

/// <summary>1 บรรทัดใน settlement report — <c>Amount</c> มีเครื่องหมายตามมุม wallet (บวก = ค้างเราเพิ่ม · ลบ = ถูกหัก)</summary>
public class SettlementLine : TenantEntity
{
    public Guid BatchId { get; set; }
    public SettlementBatch Batch { get; set; } = null!;
    /// <summary>ช่องทางของ batch (สำเนาจาก <c>SettlementBatch.ChannelId</c>) — มีไว้ให้ unique index
    /// (ช่องทาง, ExternalTxnId) กันรายการเดียวกันถูกนำเข้าซ้ำข้ามรอบโอน</summary>
    public Guid ChannelId { get; set; }
    /// <summary>ลำดับในไฟล์ (1-based) — ใช้อ้างอิงแถวตอนแจ้งปัญหา</summary>
    public int Seq { get; set; }
    public SettlementLineType LineType { get; set; } = SettlementLineType.Unclassified;
    /// <summary>ข้อความรายการ — <b>ตัด PII แล้ว</b> (ชื่อ/ที่อยู่/เบอร์ผู้ซื้อห้ามเก็บ)</summary>
    public string? Description { get; set; }
    /// <summary>ป้ายประเภทดิบจากไฟล์ (เช่น "Commission fee") — ป้อนตัวจัดประเภท/คลังเรียนรู้</summary>
    public string? RawTypeLabel { get; set; }
    /// <summary>วันที่ของรายการ (ใช้แบ่งใบขายสรุปรายวัน) — null = ใช้วันที่รอบโอน</summary>
    public DateTime? TxnDate { get; set; }
    public string? ExternalOrderId { get; set; }
    /// <summary>id รายการของผู้ให้บริการ — unique ต่อช่องทาง (กันบันทึกซ้ำข้าม batch)</summary>
    public string? ExternalTxnId { get; set; }
    /// <summary>ลายนิ้วมือเนื้อหาของ "ไฟล์" ที่บรรทัดนี้นำเข้ามา (<c>SettlementTxnKey.ImportScopeOf</c> — ตัวเดียวกับในคีย์ <c>v2:rowc:</c>) ·
    /// ผู้นำเข้าใช้แยก "ไฟล์รุ่นก่อนของไฟล์เดียวกัน" ออกจาก "อีกไฟล์ของรอบเดียวกัน" ตอนเทียบเนื้อหา (review198-S4 S4-3 · ทีม I รอบ 200) ·
    /// null = บรรทัดจาก PaymentIntent หรือนำเข้าก่อนรอบ 200 (ใช้พฤติกรรมเดิม — คำนวณย้อนไม่ได้เพราะไม่ได้เก็บแถวดิบ)</summary>
    public string? ImportScope { get; set; }
    /// <summary>รุ่นของตัวอ่าน/กติกาคีย์ตอนนำเข้า (<c>SettlementTxnKey.StoredKeyVersion</c> · รอบ 201 ทีม ST · A-ST9) — คีย์รุ่นก่อนแบบ "วันที่ตามตัวอักษร"
    /// เทียบได้เฉพาะบรรทัดที่ค่านี้เป็น null (นำเข้าก่อนมีคอลัมน์) · ห้ามแก้ภายหลัง</summary>
    public string? KeyVersion { get; set; }
    /// <summary>ยอดมีเครื่องหมาย (รวม VAT ถ้ามี)</summary>
    public decimal Amount { get; set; }
    /// <summary>VAT ที่รวมอยู่ใน <c>Amount</c> ตามที่ไฟล์ระบุ — null = ไฟล์ไม่ระบุ (ระบบแยกเอง ×7/107 ตามโหมดช่องทาง)</summary>
    public decimal? VatAmount { get; set; }
    /// <summary>ภาษีหัก ณ ที่จ่ายตามที่ไฟล์ระบุ (ข้อมูล · ยอดที่ใช้คิดมาจาก SettlementFeeTax)</summary>
    public decimal? WhtAmount { get; set; }

    public Guid? MatchedDocumentId { get; set; }
    public Guid? PaymentIntentId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? ReservationId { get; set; }
    public SettlementMatchStatus MatchStatus { get; set; } = SettlementMatchStatus.Unmatched;
    /// <summary>คนตัดสินการจับคู่ของบรรทัดนี้เอง (เลือกเอกสาร / ยืนยันเข้าใบขายสรุป) — การจับคู่อัตโนมัติ (จัดประเภทใหม่ · จับคู่ใหม่ทั้งรอบ)
    /// <b>ห้ามทับ</b> (review198-B R-B1) · ล้างเมื่อประเภทเปลี่ยนข้ามกลุ่มการจับคู่ (<c>SettlementSaleMatch.KeepUserMatch</c>)</summary>
    public bool MatchDecidedByUser { get; set; }
    /// <summary>ผู้ตัดสินการจับคู่/จัดประเภทของบรรทัดนี้ครั้งล่าสุด (user id แบบเดียวกับ <c>CreatedBy</c>) — รอบ 201 ทีม ST (A-ST7 · review198-S3 S3-11(3)):
    /// ผู้ตัดสินเป็น "ผู้ทำ" ของเอกสารที่ระบบออกให้ ⇒ ด่าน SoD ของการลงบัญชีนับรวมกับผู้สร้างรอบ/ผู้เติมไฟล์ · null = ระบบตัดสิน หรือบรรทัดก่อนรอบ 201 (ไม่รู้ ⇒ ไม่นับ —
    /// ผู้สร้างรอบยังถูกนับตามเดิม)</summary>
    public string? DecidedBy { get; set; }
    /// <summary>เวลาที่ <see cref="DecidedBy"/> ตัดสิน (UTC)</summary>
    public DateTime? DecidedAt { get; set; }

    public SettlementClassifiedBy ClassifiedBy { get; set; } = SettlementClassifiedBy.None;
    /// <summary>แถว AiFeedback ของการจัดประเภท (กฎเหล็ก #1) — ปิดลูปด้วย RecordUserChoiceAsync ตอนผู้ใช้ยืนยัน/แก้</summary>
    public Guid? ClassifyAiFeedbackId { get; set; }
    public bool ClassifyUsedAi { get; set; }

    /// <summary>ผังที่ผู้ใช้เลือกเองสำหรับบรรทัดนี้ (ชนะผังของบทบาท) — <b>บังคับ</b>สำหรับ Adjustment</summary>
    public Guid? OverrideAccountId { get; set; }
    /// <summary>เหตุผลของบรรทัดปรับปรุง — <b>บังคับ</b>สำหรับ Adjustment</summary>
    public string? AdjustmentReason { get; set; }

    /// <summary>ผู้มีสิทธิ์ลงบัญชียืนยันว่าบรรทัดนี้เป็นรายการจริงคนละรายการกับบรรทัดหน้าตาเหมือนกันในรอบที่ออกใบสรุปแรกของวัน (ฝ่ายค้านรอบสอง R2M-12 ·
    /// <c>SettlementSummarySupplement.SplitDuplicates</c>) — null = ยังไม่ยืนยัน · ประทับพร้อมผู้/เหตุผล + audit chain</summary>
    public DateTime? DistinctConfirmedAt { get; set; }
    public Guid? DistinctConfirmedBy { get; set; }
    public string? DistinctConfirmedReason { get; set; }
}
