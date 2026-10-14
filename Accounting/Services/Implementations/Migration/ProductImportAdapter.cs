using System.Globalization;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Migration;

/// <summary>
/// Product/Service catalog rows pulled out of a competitor system.
/// Key = Code (per-company unique). Conflict if same Code but different
/// Name / Unit / SellingPrice / CostPrice / VatRate / Category.
/// </summary>
public sealed record IncomingProduct(
    string Code,
    string Name,
    string? Unit,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? VatRate,
    string? Category,
    string? Barcode);

public abstract class ProductImportAdapterBase : CompetitorAdapterBase
{
    protected readonly AccountingDbContext _db;
    protected ProductImportAdapterBase(AccountingDbContext db) { _db = db; }

    public override string EntityKind => "Products";

    protected abstract List<IncomingProduct> ParseRows(CompetitorTable table, out List<string> warnings);

    public override async Task<ImportPreview> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
        var codes = rows.Select(r => r.Code).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var existing = await _db.Products
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, ct);

        var conflicts = new List<RowConflict>();
        int newCount = 0, exactDupCount = 0;
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Code))
            {
                warnings.Add($"แถวที่ไม่มี Code ถูกข้าม: {row.Name}");
                continue;
            }
            if (!existing.TryGetValue(row.Code, out var ex)) { newCount++; continue; }
            if (IsExactMatch(ex, row)) { exactDupCount++; continue; }
            if (!seen.Add(row.Code)) continue;

            var diff = new List<string>();
            void cmp(string label, string? a, string? b) { if (!StringEquals(a, b)) diff.Add(label); }
            cmp("Name", ex.Name, row.Name);
            cmp("Unit", ex.Unit, row.Unit);
            cmp("SellingPrice", ex.SellingPrice.ToString(CultureInfo.InvariantCulture),
                row.SellingPrice?.ToString(CultureInfo.InvariantCulture));
            cmp("CostPrice", ex.CostPrice.ToString(CultureInfo.InvariantCulture),
                row.CostPrice?.ToString(CultureInfo.InvariantCulture));
            cmp("VatRate", ex.VatRate.ToString(CultureInfo.InvariantCulture),
                row.VatRate?.ToString(CultureInfo.InvariantCulture));
            cmp("Category", ex.Category, row.Category);
            cmp("Barcode", ex.Barcode, row.Barcode);

            conflicts.Add(new RowConflict(
                Key: row.Code,
                ExistingLabel: ex.Name,
                IncomingLabel: row.Name,
                Existing: Snap(ex.Name, ex.Unit,
                    ex.SellingPrice.ToString("N2"), ex.CostPrice.ToString("N2"),
                    ex.VatRate.ToString("N2"), ex.Category, ex.Barcode),
                Incoming: Snap(row.Name, row.Unit,
                    row.SellingPrice?.ToString("N2"), row.CostPrice?.ToString("N2"),
                    row.VatRate?.ToString("N2"), row.Category, row.Barcode),
                DiffFields: diff));
        }

        return new ImportPreview(SourceSystem, EntityKind, table.Headers, Sample(table),
            rows.Count, warnings, conflicts, newCount, exactDupCount);
    }

    public override async Task<ImportResult> ImportAsync(Guid companyId, CompetitorTable table,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
        int imported = 0, updated = 0, skipped = 0, failed = 0;
        var errors = new List<string>(warnings);
        var resolutions = options.Resolutions ?? new Dictionary<string, ConflictAction>();

        var codes = rows.Select(r => r.Code).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var existingByCode = await _db.Products
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, ct);

        int rowIdx = 0;
        foreach (var row in rows)
        {
            rowIdx++;
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name)) { skipped++; continue; }

            try
            {
                if (!existingByCode.TryGetValue(row.Code, out var ex))
                {
                    if (!options.DryRun)
                    {
                        _db.Products.Add(new Product
                        {
                            CompanyId = companyId,
                            Code = row.Code,
                            Name = row.Name,
                            Unit = row.Unit ?? "ชิ้น",
                            SellingPrice = row.SellingPrice ?? 0m,
                            CostPrice = row.CostPrice ?? 0m,
                            VatRate = row.VatRate ?? 7m,
                            Category = row.Category,
                            Barcode = row.Barcode,
                            ProductType = ProductType.Product,
                            CreatedBy = $"Migrate:{SourceSystem}:{options.SourceTag ?? "manual"}",
                        });
                    }
                    imported++;
                    continue;
                }

                if (IsExactMatch(ex, row)) { skipped++; continue; }

                var action = resolutions.TryGetValue(row.Code, out var picked)
                    ? picked : options.DefaultConflictAction;
                if (options.SkipDuplicates && action == ConflictAction.Skip) { skipped++; continue; }

                switch (action)
                {
                    case ConflictAction.Skip: skipped++; break;
                    case ConflictAction.Overwrite:
                        if (!options.DryRun) ApplyOverwrite(ex, row, options.SourceTag);
                        updated++; break;
                    case ConflictAction.Merge:
                        if (!options.DryRun) ApplyMerge(ex, row, options.SourceTag);
                        updated++; break;
                }
            }
            catch (InvalidOperationException e) { failed++; errors.Add($"แถว {rowIdx} ({row.Code}): {e.Message}"); }
        }

        if (!options.DryRun && (imported > 0 || updated > 0))
            await _db.SaveChangesAsync(ct);
        return new ImportResult(SourceSystem, rows.Count, imported, updated, skipped, failed, errors);
    }

    // ─────────── helpers ───────────

    private static IReadOnlyDictionary<string, string?> Snap(
        string? name, string? unit, string? sell, string? cost, string? vat, string? cat, string? barcode)
        => new Dictionary<string, string?>
        {
            ["Name"] = name, ["Unit"] = unit, ["SellingPrice"] = sell,
            ["CostPrice"] = cost, ["VatRate"] = vat, ["Category"] = cat, ["Barcode"] = barcode,
        };

    private static bool IsExactMatch(Product ex, IncomingProduct row)
        => StringEquals(ex.Name, row.Name)
        && StringEquals(ex.Unit, row.Unit)
        && (row.SellingPrice == null || ex.SellingPrice == row.SellingPrice.Value)
        && (row.CostPrice == null || ex.CostPrice == row.CostPrice.Value)
        && (row.VatRate == null || ex.VatRate == row.VatRate.Value)
        && StringEquals(ex.Category, row.Category)
        && StringEquals(ex.Barcode, row.Barcode);

    private static bool StringEquals(string? a, string? b)
    {
        var na = string.IsNullOrWhiteSpace(a) ? "" : a.Trim();
        var nb = string.IsNullOrWhiteSpace(b) ? "" : b.Trim();
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    private void ApplyOverwrite(Product ex, IncomingProduct row, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(row.Name)) ex.Name = row.Name;
        if (!string.IsNullOrWhiteSpace(row.Unit)) ex.Unit = row.Unit;
        if (row.SellingPrice.HasValue) ex.SellingPrice = row.SellingPrice.Value;
        if (row.CostPrice.HasValue) ex.CostPrice = row.CostPrice.Value;
        if (row.VatRate.HasValue) ex.VatRate = row.VatRate.Value;
        if (!string.IsNullOrWhiteSpace(row.Category)) ex.Category = row.Category;
        if (!string.IsNullOrWhiteSpace(row.Barcode)) ex.Barcode = row.Barcode;
        ex.UpdatedAt = DateTime.UtcNow;
        ex.UpdatedBy = $"Migrate:{SourceSystem}:{tag ?? "manual"}:overwrite";
    }

    private void ApplyMerge(Product ex, IncomingProduct row, string? tag)
    {
        if (string.IsNullOrWhiteSpace(ex.Unit) && !string.IsNullOrWhiteSpace(row.Unit))
            ex.Unit = row.Unit;
        if (ex.SellingPrice == 0m && row.SellingPrice.HasValue) ex.SellingPrice = row.SellingPrice.Value;
        if (ex.CostPrice == 0m && row.CostPrice.HasValue) ex.CostPrice = row.CostPrice.Value;
        if (string.IsNullOrWhiteSpace(ex.Category) && !string.IsNullOrWhiteSpace(row.Category))
            ex.Category = row.Category;
        if (string.IsNullOrWhiteSpace(ex.Barcode) && !string.IsNullOrWhiteSpace(row.Barcode))
            ex.Barcode = row.Barcode;
        ex.UpdatedAt = DateTime.UtcNow;
        ex.UpdatedBy = $"Migrate:{SourceSystem}:{tag ?? "manual"}:merge";
    }
}

public class ExpressProductsAdapter : ProductImportAdapterBase
{
    public ExpressProductsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "Express";

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "รหัสสินค้า", "ชื่อสินค้า" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "หน่วย", "ราคาขาย", "ราคาทุน", "VAT", "ภาษี", "หมวด", "บาร์โค้ด", "Code", "ItemCode", "Name", "Unit", "Price" };

    protected override List<IncomingProduct> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingProduct>();
        foreach (var d in table.Rows)
        {
            var code = FirstNonEmpty(d, "รหัสสินค้า", "Code", "ItemCode");
            var name = FirstNonEmpty(d, "ชื่อสินค้า", "ชื่อ", "Name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "หน่วย", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "ราคาขาย", "SellingPrice", "Price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "ราคาทุน", "CostPrice", "Cost")),
                VatRate: ParseRate(FirstNonEmpty(d, "VAT", "VatRate", "ภาษี")),
                Category: FirstNonEmpty(d, "หมวด", "Category"),
                Barcode: FirstNonEmpty(d, "บาร์โค้ด", "Barcode")));
        }
        return rows;
    }
}

public class PeakProductsAdapter : ProductImportAdapterBase
{
    public PeakProductsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "PEAK";

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "productCode", "productName" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "unit", "sellingPrice", "costPrice", "vatRate", "category", "barcode", "code", "name", "price" };

    protected override List<IncomingProduct> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingProduct>();
        foreach (var d in table.Rows)
        {
            var code = FirstNonEmpty(d, "productCode", "ProductCode", "code");
            var name = FirstNonEmpty(d, "productName", "ProductName", "name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "unit", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "sellingPrice", "SellingPrice", "price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "costPrice", "CostPrice", "cost")),
                VatRate: ParseRate(FirstNonEmpty(d, "vatRate", "VatRate")),
                Category: FirstNonEmpty(d, "category", "Category"),
                Barcode: FirstNonEmpty(d, "barcode", "Barcode")));
        }
        return rows;
    }
}

public class FlowAccountProductsAdapter : ProductImportAdapterBase
{
    public FlowAccountProductsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "FlowAccount";

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "product code", "product name", "selling price" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "unit", "cost price", "vat rate", "category", "barcode", "code", "name", "price", "FlowAccount-Products" };

    protected override List<IncomingProduct> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingProduct>();
        foreach (var d in table.Rows)
        {
            var code = FirstNonEmpty(d, "product code", "code", "Code");
            var name = FirstNonEmpty(d, "product name", "name", "Name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "unit", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "selling price", "sellingPrice", "price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "cost price", "costPrice", "cost")),
                VatRate: ParseRate(FirstNonEmpty(d, "vat rate", "vatRate", "vat")),
                Category: FirstNonEmpty(d, "category", "Category"),
                Barcode: FirstNonEmpty(d, "barcode", "Barcode")));
        }
        return rows;
    }
}
