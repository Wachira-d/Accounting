using System.Globalization;
using System.Text;
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
internal sealed record IncomingProduct(
    string Code,
    string Name,
    string? Unit,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? VatRate,
    string? Category,
    string? Barcode);

public abstract class ProductImportAdapterBase : ICompetitorImportAdapter
{
    protected readonly AccountingDbContext _db;
    protected ProductImportAdapterBase(AccountingDbContext db) { _db = db; }

    public abstract string SourceSystem { get; }
    public string EntityKind => "Products";
    public abstract bool CanHandle(string fileContent, string filename);

    protected abstract List<IncomingProduct> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings);

    public async Task<ImportPreview> PreviewAsync(Guid companyId, string fileContent, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out var cols, out var rawSample, out var warnings);
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

        var sample = rawSample.Take(50).Select(r => (IReadOnlyDictionary<string, string>)r).ToList();
        return new ImportPreview(SourceSystem, EntityKind, cols, sample,
            rows.Count, warnings, conflicts, newCount, exactDupCount);
    }

    public async Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out _, out _, out var warnings);
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
            catch (Exception e) { failed++; errors.Add($"Row {rowIdx} ({row.Code}): {e.Message}"); }
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

    // ─────────── shared CSV helpers ───────────

    protected static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    protected static string? FirstNonEmpty(IDictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    protected static decimal? ParseDec(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = s.Replace(",", "").Trim();
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}

public class ExpressProductsAdapter : ProductImportAdapterBase
{
    public ExpressProductsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "Express";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("express", StringComparison.OrdinalIgnoreCase)
            && filename.Contains("product", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return head.Contains("รหัสสินค้า") && (head.Contains("ราคาขาย") || head.Contains("หน่วย"));
    }

    protected override List<IncomingProduct> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        var sep = lines[0].Contains('\t') ? '\t' : ',';
        columns = lines[0].TrimEnd('\r').Split(sep).Select(c => c.Trim()).ToList();
        var rows = new List<IncomingProduct>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = raw.Split(sep);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Length; c++) d[columns[c]] = cells[c].Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "รหัสสินค้า", "Code", "ItemCode");
            var name = FirstNonEmpty(d, "ชื่อสินค้า", "ชื่อ", "Name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "หน่วย", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "ราคาขาย", "SellingPrice", "Price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "ราคาทุน", "CostPrice", "Cost")),
                VatRate: ParseDec(FirstNonEmpty(d, "VAT", "VatRate", "ภาษี")),
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

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("peak", StringComparison.OrdinalIgnoreCase)
            && filename.Contains("product", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return head.Contains("productCode") || head.Contains("productName");
    }

    protected override List<IncomingProduct> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingProduct>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++) d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "productCode", "ProductCode", "code");
            var name = FirstNonEmpty(d, "productName", "ProductName", "name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "unit", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "sellingPrice", "SellingPrice", "price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "costPrice", "CostPrice", "cost")),
                VatRate: ParseDec(FirstNonEmpty(d, "vatRate", "VatRate")),
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

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("flow", StringComparison.OrdinalIgnoreCase)
            && filename.Contains("product", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return (head.Contains("product code") && head.Contains("selling price"))
            || head.Contains("FlowAccount-Products");
    }

    protected override List<IncomingProduct> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingProduct>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++) d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "product code", "code", "Code");
            var name = FirstNonEmpty(d, "product name", "name", "Name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingProduct(
                Code: code!, Name: name!,
                Unit: FirstNonEmpty(d, "unit", "Unit"),
                SellingPrice: ParseDec(FirstNonEmpty(d, "selling price", "sellingPrice", "price")),
                CostPrice: ParseDec(FirstNonEmpty(d, "cost price", "costPrice", "cost")),
                VatRate: ParseDec(FirstNonEmpty(d, "vat rate", "vatRate", "vat")),
                Category: FirstNonEmpty(d, "category", "Category"),
                Barcode: FirstNonEmpty(d, "barcode", "Barcode")));
        }
        return rows;
    }
}
