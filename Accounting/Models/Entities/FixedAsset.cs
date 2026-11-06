using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ทะเบียนสินทรัพย์ถาวร (Fixed Asset Register)
/// เทียบเท่า PEAK: Asset management
/// </summary>
public class FixedAsset : TenantEntity
{
    public string AssetCode { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Location { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Asset classification — drives the accounting treatment
    /// (depreciation vs amortization), GL account suggestions, and the labels
    /// the UI uses. Default Tangible keeps existing rows behaving exactly as
    /// before; new rows are typed at creation. Class name stays FixedAsset
    /// for code stability but the user-facing label is "สินทรัพย์".</summary>
    public AssetType AssetType { get; set; } = AssetType.Tangible;

    // ===== Right-of-Use (TFRS 16 lease) — only set when AssetType = RightOfUse =====
    /// <summary>Total lease term in months. Drives amortization period when
    /// UsefulLifeMonths isn't set explicitly.</summary>
    public int? LeaseTermMonths { get; set; }
    /// <summary>Lessor name — surfaces on the asset card so it's clear this
    /// isn't an owned item.</summary>
    public string? LessorName { get; set; }
    /// <summary>Monthly lease payment for disclosure / future cash flow note.</summary>
    public decimal? MonthlyLeasePayment { get; set; }
    /// <summary>Lease liability GL account — credited when the ROU asset is
    /// first recognized. Optional; falls back to a default if null.</summary>
    public Guid? LeaseLiabilityAccountId { get; set; }
    public ChartOfAccount? LeaseLiabilityAccount { get; set; }

    // Financials
    public DateTime PurchaseDate { get; set; }
    public decimal PurchaseCost { get; set; }
    public decimal SalvageValue { get; set; }
    public int UsefulLifeMonths { get; set; }

    // ───── ทบทวนอายุการใช้งาน (TFRS for NPAEs บทที่ 10) ─────
    // ⚠️ ที่มา (ผลตรวจทีม E · SYSTEM_AUDIT_2026-09-07.md E-06): เดิม
    // `AdjustUsefulLifeAsync` แค่เขียนทับ `UsefulLifeMonths` แล้วจบ — แต่สูตร
    // เส้นตรงหารจาก **ราคาทุนเดิม** ไม่ใช่ NBV คงเหลือ ⇒ การเปลี่ยนประมาณการ
    // กลายเป็นการคิดย้อนหลัง (retrospective) ซึ่งผิดมาตรฐาน: ทุน 120,000 อายุ
    // 60 เดือน โพสต์ไป 24 งวด (NBV 72,000) แล้วย่นเหลือ 36 เดือน ควรได้
    // 72,000/12 = 6,000/เดือน แต่โค้ดให้ 120,000/36 = 3,333.33 ⇒ ค่าเสื่อม
    // ปีถัดไปหายไป 32,000 และตัดจบที่งวดที่ 45.6 แทนที่จะเป็น 36
    //
    // เก็บ "ฐานที่ใช้คิดต่อจากนี้" ไว้ตรง ๆ แทนการหวังให้สูตรเดาถูก

    /// <summary>มูลค่าที่เหลือให้คิดค่าเสื่อม ณ วันทบทวน (= NBV ตอนนั้น) —
    /// <c>null</c> = <b>ยังไม่เคยทบทวน</b> (ต่างจาก "ทบทวนแล้วได้ค่าเท่าเดิม")</summary>
    public decimal? DepreciableBaseAtReview { get; set; }

    /// <summary>อายุคงเหลือ (เดือน) นับจากงวดถัดจากวันทบทวน</summary>
    public int? RemainingLifeMonthsAtReview { get; set; }

    /// <summary>ลำดับงวด (0-based) ที่การทบทวนเริ่มมีผล — ใช้แปลงดัชนีงวดให้
    /// สูตรคิดจากฐานใหม่ได้โดยไม่ต้องแก้ตัวสูตรเอง</summary>
    public int? ReviewEffectiveFromMonthIndex { get; set; }

    /// <summary>ทบทวนครั้งล่าสุดเมื่อไร — ผู้สอบบัญชีถามข้อนี้ตรง ๆ
    /// (CLAUDE.md §G บทที่ 10)</summary>
    public DateTime? UsefulLifeReviewedAt { get; set; }

    /// <summary>ใครเป็นคนทบทวน</summary>
    public string? UsefulLifeReviewedBy { get; set; }
    public DepreciationMethod DepreciationMethod { get; set; } = DepreciationMethod.StraightLine;
    public decimal AccumulatedDepreciation { get; set; }
    public decimal NetBookValue { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public DateTime? DisposalDate { get; set; }
    public decimal? DisposalAmount { get; set; }

    // Account mapping
    public Guid? AssetAccountId { get; set; }
    public ChartOfAccount? AssetAccount { get; set; }
    public Guid? DepreciationExpenseAccountId { get; set; }
    public ChartOfAccount? DepreciationExpenseAccount { get; set; }
    public Guid? AccumulatedDepreciationAccountId { get; set; }
    public ChartOfAccount? AccumulatedDepreciationAccount { get; set; }

    /// <summary>Project the asset was acquired for. Periodic
    /// depreciation JE inherits this so depreciation cost lands
    /// in the right project's P&amp;L without manual allocation.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>เอกสารต้นทาง (Expense/PI/PV) ที่ซื้อสินทรัพย์นี้ — set เมื่อ
    /// ระบบ auto-register จากบรรทัดที่ลงผัง PPE ตอน approve. ใช้ dedupe
    /// (ไม่สร้างซ้ำตอน re-approve) + ลิงก์กลับไปดูเอกสารซื้อ.</summary>
    public Guid? SourceDocumentId { get; set; }
    public Guid? SourceDocumentLineId { get; set; }

    /// <summary>ผลสแกน OCR ที่ผู้ใช้กด "ลงทะเบียนสินทรัพย์" จนเกิดแถวนี้
    ///
    /// <para>⚠️ ที่มา: <c>OcrService.RegisterAssetFromScanAsync</c> สร้างสินทรัพย์
    /// โดย<b>ไม่เซ็ต <c>SourceDocumentLineId</c></b> (ตอนนั้นยังไม่มีเอกสาร)
    /// ส่วน <c>DocumentService.AutoRegisterFixedAssetsAsync</c> กันซ้ำด้วย
    /// <c>SourceDocumentLineId</c> ⇒ <b>key ไม่มีวันชนกัน</b>: สแกนใบซื้อโน้ตบุ๊ก
    /// → กด "ลงทะเบียนสินทรัพย์" (asset #1 + JE Dr 18xxx) → กด "สร้างเอกสาร"
    /// → approve (JE Dr 18xxx อีกรอบ + asset #2) ⇒ PPE เดบิตสองเท่า ·
    /// ทะเบียนมี 2 แถวของชิ้นเดียว · <c>DepreciationBackgroundService</c>
    /// ตัดค่าเสื่อมทั้งสองแถวทุกเดือน (dedup ของมันเป็น per-asset จับไม่ได้)</para></summary>
    public Guid? SourceScanResultId { get; set; }

    /// <summary>True = ระบบสร้างให้อัตโนมัติด้วยค่า default (อายุใช้งาน/วิธี
    /// คิดค่าเสื่อมตามประเภท) ผู้ใช้ควรตรวจ/ปรับก่อนใช้จริง. UI ติดป้าย
    /// "⚠️ ตรวจสอบทะเบียนสินทรัพย์".</summary>
    public bool NeedsReview { get; set; }

    // ───── รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md) ─────
    /// <summary><c>true</c> = <b>ระหว่างจัดหา/ติดตั้ง</b> — ต้นทุนสะสมจากใบที่ผูก (<see cref="CostSources"/>) ยังอยู่ผังเดิมของแต่ละใบ
    /// (มัดจำ 118xx · งานระหว่างก่อสร้าง 12280) · <c>DepreciationMethod = None</c> จนกว่าจะ "โอนเป็นสินทรัพย์พร้อมใช้"
    /// (<c>FixedAssetService.PlaceInServiceAsync</c> ตัวเดียวที่ปลดค่านี้) · จำหน่าย/ตีราคา/ปรับอายุไม่ได้ระหว่างนี้</summary>
    public bool IsUnderAcquisition { get; set; }

    /// <summary>วันที่พร้อมใช้งาน (TFRS for NPAEs บทที่ 10 — จุดเริ่มคิดค่าเสื่อม) · ตั้งตอนโอน และ <see cref="PurchaseDate"/> = ค่านี้ ·
    /// null = สินทรัพย์เดิม (ใบเดียว) ที่ยังเริ่มค่าเสื่อมจากวันที่เอกสาร</summary>
    public DateTime? AvailableForUseDate { get; set; }

    /// <summary>ราคาตามสัญญา (บาท · ไม่รวม VAT) ที่ผู้ใช้ระบุตอนสร้างสินทรัพย์หลายใบ — ใช้เตือนเมื่อ Σ ต้นทุนเกิน
    /// (ใบสุดท้ายลงเต็มราคาทั้งที่ผูกมัดจำแล้ว · คำถามเจ้าของ §6 ข้อ 1) · null = ไม่ได้ระบุ · เตือนอย่างเดียว ไม่บล็อก</summary>
    public decimal? ContractPrice { get; set; }

    public ICollection<FixedAssetCostSource> CostSources { get; set; } = new List<FixedAssetCostSource>();

    public ICollection<AssetDepreciation> Depreciations { get; set; } = new List<AssetDepreciation>();
}

/// <summary>
/// **บรรทัดเอกสารซื้อ 1 บรรทัด (หรือบางส่วน) ที่เป็นต้นทุนของสินทรัพย์** — รอบ 249 · ASSET_MULTI_DOCUMENT_DESIGN §3
///
/// <para>กติกาทั้งหมด (ผูกได้ไหม · ยอด · กันซ้ำ · JE โอน) อยู่ที่ <c>Helpers/AssetCostSourceRules</c> · ผู้เขียนแถวมีที่เดียว
/// (<c>FixedAssetService</c>) · ไม่มี FK ไป <c>Documents</c> โดยเจตนา (ลบถาวรใบที่ยกเลิกแล้วต้องไม่ติด) — แถวเก็บสำเนาเลข/ชนิด/วันที่ไว้แสดง</para>
/// <para>ไม่ลบแถว: ปลดออก = ประทับ <see cref="ReleasedAt"/> + เหตุ (ร่องรอยว่าเคยผูก) · แถวที่ <see cref="CapitalizedAt"/> แล้วปลดไม่ได้</para>
/// </summary>
public class FixedAssetCostSource : TenantEntity
{
    public Guid FixedAssetId { get; set; }
    public FixedAsset FixedAsset { get; set; } = null!;

    public Guid DocumentId { get; set; }
    public Guid DocumentLineId { get; set; }
    public string DocumentNumber { get; set; } = "";
    public DocumentType DocumentType { get; set; }
    public DateTime DocumentDate { get; set; }

    public AssetCostSourceKind Kind { get; set; }

    /// <summary>ต้นทุนที่ผูก (บาท) — ใบลดหนี้ติดลบ · ไม่รวม VAT ที่เคลมได้ · รวม VAT ต้องห้าม (<see cref="NonClaimableVatIncluded"/>)</summary>
    public decimal Amount { get; set; }
    /// <summary>ส่วน VAT ต้องห้าม §82/5 ที่รวมอยู่ใน <see cref="Amount"/> (บาท · เครื่องหมายเดียวกัน)</summary>
    public decimal NonClaimableVatIncluded { get; set; }

    /// <summary>ผังที่ยอดนี้อยู่ใน GL (ตอนผูก · อัปเดตตอนโอน) — ขา Cr ของ JE โอน</summary>
    public Guid SourceAccountId { get; set; }
    public string? SourceAccountCode { get; set; }

    /// <summary>โอนเข้าผังสินทรัพย์แล้วเมื่อไร (null = ยังถือไว้ระหว่างจัดหา)</summary>
    public DateTime? CapitalizedAt { get; set; }
    /// <summary>JE โอน (null เมื่อยอดอยู่บนผังสินทรัพย์ปลายทางอยู่แล้ว ไม่ต้องลง)</summary>
    public Guid? CapitalizationJournalEntryId { get; set; }

    public DateTime? ReleasedAt { get; set; }
    public string? ReleasedReason { get; set; }
    public string? ReleasedBy { get; set; }

    public string? Note { get; set; }
}

public class AssetDepreciation : TenantEntity
{
    public Guid FixedAssetId { get; set; }
    public FixedAsset FixedAsset { get; set; } = null!;
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
    public decimal AccumulatedAmount { get; set; }
    public decimal NetBookValue { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsPosted { get; set; }
}
