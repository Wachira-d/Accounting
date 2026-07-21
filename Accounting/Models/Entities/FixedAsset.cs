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

    /// <summary>True = ระบบสร้างให้อัตโนมัติด้วยค่า default (อายุใช้งาน/วิธี
    /// คิดค่าเสื่อมตามประเภท) ผู้ใช้ควรตรวจ/ปรับก่อนใช้จริง. UI ติดป้าย
    /// "⚠️ ตรวจสอบทะเบียนสินทรัพย์".</summary>
    public bool NeedsReview { get; set; }

    public ICollection<AssetDepreciation> Depreciations { get; set; } = new List<AssetDepreciation>();
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
