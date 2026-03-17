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
    public Guid? DepreciationExpenseAccountId { get; set; }
    public Guid? AccumulatedDepreciationAccountId { get; set; }

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
    public bool IsPosted { get; set; }
}
