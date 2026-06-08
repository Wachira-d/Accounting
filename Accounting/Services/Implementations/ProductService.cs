using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Product;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ProductService : IProductService
{
    private readonly AccountingDbContext _db;
    private readonly IImageProcessingService _images;
    private readonly IWebHostEnvironment _env;

    public ProductService(AccountingDbContext db, IImageProcessingService images, IWebHostEnvironment env)
    {
        _db = db;
        _images = images;
        _env = env;
    }

    // ===== Product images (gallery) =====

    public async Task<ProductResponse> AddImageAsync(Guid companyId, Guid productId, Stream input, string contentType, string fileName)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.CompanyId == companyId && !x.IsDeleted)
            ?? throw new InvalidOperationException("ไม่พบสินค้า");
        if (!_images.IsProcessableImage(contentType) && contentType?.ToLowerInvariant() != "image/svg+xml")
            throw new InvalidOperationException("รองรับเฉพาะไฟล์รูปภาพ (JPG/PNG/WebP/GIF/SVG)");

        var dir = Path.Combine(_env.WebRootPath, "uploads", "products", companyId.ToString());
        var web = $"/uploads/products/{companyId}";
        var processed = await _images.ProcessAndSaveAsync(input, contentType, fileName, dir, web, ImageProfile.ProductMain, generateThumb: true);

        var list = ParseImageUrls(p.ImageUrlsJson);
        list.Add(processed.RelativeUrl);
        p.ImageUrlsJson = System.Text.Json.JsonSerializer.Serialize(list);
        await _db.SaveChangesAsync();
        return MapToResponse(p);
    }

    public async Task<ProductResponse> RemoveImageAsync(Guid companyId, Guid productId, string url)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.CompanyId == companyId && !x.IsDeleted)
            ?? throw new InvalidOperationException("ไม่พบสินค้า");
        var list = ParseImageUrls(p.ImageUrlsJson);
        list.RemoveAll(u => u == url);
        p.ImageUrlsJson = list.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(list);
        await _db.SaveChangesAsync();

        // Best-effort delete of the file + its thumbnail. Don't fail the request if the file is gone.
        try
        {
            if (url.StartsWith("/uploads/products/", StringComparison.OrdinalIgnoreCase))
            {
                var abs = Path.Combine(_env.WebRootPath, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs)) File.Delete(abs);
                var thumb = abs.Replace(Path.GetFileName(abs), Path.GetFileNameWithoutExtension(abs) + "_thumb.jpg");
                if (File.Exists(thumb)) File.Delete(thumb);
            }
        }
        catch { /* swallow — file cleanup is best-effort */ }

        return MapToResponse(p);
    }

    public async Task<ProductResponse> ReorderImagesAsync(Guid companyId, Guid productId, List<string> orderedUrls)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.CompanyId == companyId && !x.IsDeleted)
            ?? throw new InvalidOperationException("ไม่พบสินค้า");
        var current = ParseImageUrls(p.ImageUrlsJson);
        // Keep only URLs that already belong to this product (prevent injection of arbitrary URLs)
        var ordered = orderedUrls.Where(u => current.Contains(u)).Distinct().ToList();
        // Append any current URLs the client forgot to include, preserving them
        ordered.AddRange(current.Where(u => !ordered.Contains(u)));
        p.ImageUrlsJson = ordered.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(ordered);
        await _db.SaveChangesAsync();
        return MapToResponse(p);
    }

    // ===== CRUD =====

    public async Task<ProductResponse> CreateAsync(Guid companyId, CreateProductRequest request)
    {
        if (await _db.Products.AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code))
            throw new InvalidOperationException($"รหัสสินค้า {request.Code} ซ้ำ");

        var product = new Product
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            Description = request.Description,
            ProductType = request.ProductType,
            SKU = request.SKU,
            Barcode = request.Barcode,
            Category = request.Category,
            Unit = request.Unit,
            SellingPrice = request.SellingPrice,
            CostPrice = request.CostPrice,
            VatRate = request.VatRate,
            IsVatIncluded = request.IsVatIncluded,
            SalesAccountId = request.SalesAccountId,
            PurchaseAccountId = request.PurchaseAccountId,
            InventoryAccountId = request.InventoryAccountId,
            SuppliesAccountId = request.SuppliesAccountId,
            SuppliesExpenseAccountId = request.SuppliesExpenseAccountId,
            TrackStock = request.TrackStock || request.ProductType == ProductType.Supplies,
            MinimumStock = request.MinimumStock,
            PrintStation = string.IsNullOrWhiteSpace(request.PrintStation) ? null : request.PrintStation
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return MapToResponse(product);
    }

    public async Task<ProductResponse> GetByIdAsync(Guid companyId, Guid productId)
    {
        var product = await _db.Products
            .Include(p => p.UnitConversions)
            .Include(p => p.SalesAccount)
            .Include(p => p.PurchaseAccount)
            .Include(p => p.InventoryAccount)
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId && !p.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");
        return MapToResponse(product);
    }

    public async Task<PagedResponse<ProductResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.Products.Where(p => p.CompanyId == companyId && !p.IsDeleted);
        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, search) || EF.Functions.ILike(p.Code, search));
        }

        var total = await query.CountAsync();
        var items = await query.OrderBy(p => p.Code)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<ProductResponse>(
            items.Select(p => MapToResponse(p)).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<ProductResponse> UpdateAsync(Guid companyId, Guid productId, UpdateProductRequest request)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");

        if (request.Name != null) product.Name = request.Name;
        if (request.NameEn != null) product.NameEn = request.NameEn;
        if (request.Description != null) product.Description = request.Description;
        if (request.SKU != null) product.SKU = request.SKU;
        if (request.Barcode != null) product.Barcode = request.Barcode;
        if (request.Category != null) product.Category = request.Category;
        if (request.Unit != null) product.Unit = request.Unit;
        if (request.SellingPrice.HasValue) product.SellingPrice = request.SellingPrice.Value;
        if (request.CostPrice.HasValue) product.CostPrice = request.CostPrice.Value;
        if (request.VatRate.HasValue) product.VatRate = request.VatRate.Value;
        if (request.IsVatIncluded.HasValue) product.IsVatIncluded = request.IsVatIncluded.Value;
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;
        if (request.TrackStock.HasValue) product.TrackStock = request.TrackStock.Value;
        if (request.MinimumStock.HasValue) product.MinimumStock = request.MinimumStock.Value;
        if (request.PrintStation != null) product.PrintStation = string.IsNullOrWhiteSpace(request.PrintStation) ? null : request.PrintStation;
        if (request.SalesAccountId.HasValue) product.SalesAccountId = request.SalesAccountId;
        if (request.PurchaseAccountId.HasValue) product.PurchaseAccountId = request.PurchaseAccountId;
        if (request.InventoryAccountId.HasValue) product.InventoryAccountId = request.InventoryAccountId;
        if (request.SuppliesAccountId.HasValue) product.SuppliesAccountId = request.SuppliesAccountId;
        if (request.SuppliesExpenseAccountId.HasValue) product.SuppliesExpenseAccountId = request.SuppliesExpenseAccountId;

        await _db.SaveChangesAsync();
        return MapToResponse(product);
    }

    public async Task DeleteAsync(Guid companyId, Guid productId)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");
        product.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== STOCK =====

    public async Task<StockMovementResponse> AdjustStockAsync(Guid companyId, StockAdjustmentRequest request, string userId)
    {
        // CONCURRENCY FIX: previously two parallel AdjustStockAsync calls for
        // the same product would each read CurrentStock = X, add their qty,
        // and save — last write wins and the other movement's quantity is
        // silently lost. Take a Postgres advisory lock keyed by the product
        // id so the read-modify-write becomes serial per-product. Auto-
        // releases on transaction end (we wrap in a txn below).
        await using var txn = await _db.Database.BeginTransactionAsync();
        var prodLockKey = HashCode.Combine(request.ProductId, "stock-adj");
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", prodLockKey);

        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");

        var qty = request.MovementType == "OUT" ? -Math.Abs(request.Quantity) : Math.Abs(request.Quantity);
        if (request.MovementType == "OUT" && product.CurrentStock + qty < 0)
            throw new InvalidOperationException(
                $"สต็อกไม่เพียงพอ: คงเหลือ {product.CurrentStock} ต้องการเบิก {Math.Abs(qty)}");
        product.CurrentStock += qty;

        var movement = new StockMovement
        {
            CompanyId = companyId,
            ProductId = request.ProductId,
            MovementDate = DateTime.UtcNow,
            MovementType = request.MovementType,
            Quantity = request.Quantity,
            UnitCost = request.UnitCost ?? product.CostPrice,
            BalanceAfter = product.CurrentStock,
            Reference = request.Reference,
            Notes = request.Notes,
            CreatedBy = userId
        };

        _db.StockMovements.Add(movement);
        await _db.SaveChangesAsync();
        await txn.CommitAsync();

        return new StockMovementResponse(movement.Id, movement.ProductId, product.Name,
            movement.MovementDate, movement.MovementType, movement.Quantity,
            movement.UnitCost, movement.BalanceAfter, movement.Reference, movement.Notes);
    }

    public async Task<List<StockMovementResponse>> GetStockMovementsAsync(Guid companyId, Guid productId)
    {
        // Skip the Include — we already know the productId, and we
        // resolve the name once below from a single Products lookup.
        // Avoids an NRE when a StockMovement row references a deleted
        // Product (a real possibility after migrations/imports). Also
        // halves the SQL row count for high-traffic products.
        var movements = await _db.StockMovements
            .AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.ProductId == productId && !m.IsDeleted)
            .OrderByDescending(m => m.MovementDate)
            .ToListAsync();
        var productName = await _db.Products.AsNoTracking()
            .Where(p => p.Id == productId && p.CompanyId == companyId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync() ?? "(ไม่พบสินค้า)";

        return movements.Select(m => new StockMovementResponse(
            m.Id, m.ProductId, productName, m.MovementDate,
            m.MovementType, m.Quantity, m.UnitCost, m.BalanceAfter, m.Reference, m.Notes)).ToList();
    }

    public async Task<List<ProductResponse>> GetLowStockProductsAsync(Guid companyId)
    {
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && p.CurrentStock <= p.MinimumStock && !p.IsDeleted)
            .ToListAsync();
        return products.Select(p => MapToResponse(p)).ToList();
    }

    // ===== UNIT CONVERSION =====

    public async Task<UnitConversionResponse> CreateUnitConversionAsync(Guid companyId, CreateUnitConversionRequest request)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");

        if (request.ConversionRate <= 0)
            throw new ArgumentException("อัตราแปลงต้องมากกว่า 0");

        var exists = await _db.UnitConversions.AnyAsync(u =>
            u.ProductId == request.ProductId && u.FromUnit == request.FromUnit && u.ToUnit == request.ToUnit);
        if (exists)
            throw new InvalidOperationException($"มีการตั้งค่าแปลงหน่วย {request.FromUnit} → {request.ToUnit} อยู่แล้ว");

        var conversion = new UnitConversion
        {
            CompanyId = companyId,
            ProductId = request.ProductId,
            FromUnit = request.FromUnit,
            ToUnit = request.ToUnit,
            ConversionRate = request.ConversionRate,
            SellingPrice = request.SellingPrice,
            CostPrice = request.CostPrice,
            Barcode = request.Barcode
        };

        _db.UnitConversions.Add(conversion);
        await _db.SaveChangesAsync();

        return MapConversion(conversion);
    }

    public async Task<List<UnitConversionResponse>> GetUnitConversionsAsync(Guid companyId, Guid productId)
    {
        var conversions = await _db.UnitConversions
            .Where(u => u.CompanyId == companyId && u.ProductId == productId)
            .OrderBy(u => u.FromUnit)
            .ToListAsync();
        return conversions.Select(MapConversion).ToList();
    }

    public async Task DeleteUnitConversionAsync(Guid companyId, Guid conversionId)
    {
        var conversion = await _db.UnitConversions
            .FirstOrDefaultAsync(u => u.Id == conversionId && u.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการตั้งค่าแปลงหน่วย");
        _db.UnitConversions.Remove(conversion);
        await _db.SaveChangesAsync();
    }

    public async Task<ConvertUnitResponse> ConvertUnitAsync(Guid companyId, ConvertUnitRequest request)
    {
        // Direct conversion
        var conv = await _db.UnitConversions.FirstOrDefaultAsync(u =>
            u.CompanyId == companyId && u.ProductId == request.ProductId
            && u.FromUnit == request.FromUnit && u.ToUnit == request.ToUnit);

        if (conv != null)
        {
            return new ConvertUnitResponse(
                request.FromUnit, request.Quantity,
                request.ToUnit, request.Quantity * conv.ConversionRate,
                conv.ConversionRate);
        }

        // Reverse conversion
        var revConv = await _db.UnitConversions.FirstOrDefaultAsync(u =>
            u.CompanyId == companyId && u.ProductId == request.ProductId
            && u.FromUnit == request.ToUnit && u.ToUnit == request.FromUnit);

        if (revConv != null)
        {
            var reverseRate = 1m / revConv.ConversionRate;
            return new ConvertUnitResponse(
                request.FromUnit, request.Quantity,
                request.ToUnit, request.Quantity * reverseRate,
                reverseRate);
        }

        throw new InvalidOperationException($"ไม่พบอัตราแปลงหน่วย {request.FromUnit} → {request.ToUnit}");
    }

    // ===== PRODUCT CATEGORIES =====

    public async Task<ProductCategoryResponse> CreateCategoryAsync(Guid companyId, CreateProductCategoryRequest request)
    {
        if (await _db.ProductCategories.AnyAsync(c => c.CompanyId == companyId && c.Code == request.Code))
            throw new InvalidOperationException($"รหัสหมวดหมู่ {request.Code} ซ้ำ");

        var category = new ProductCategory
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            ParentCategoryId = request.ParentCategoryId
        };

        _db.ProductCategories.Add(category);
        await _db.SaveChangesAsync();

        return await MapCategoryAsync(category, companyId);
    }

    public async Task<List<ProductCategoryResponse>> GetCategoriesAsync(Guid companyId)
    {
        var categories = await _db.ProductCategories
            .Where(c => c.CompanyId == companyId)
            .Include(c => c.ParentCategory)
            .OrderBy(c => c.Code)
            .ToListAsync();

        var results = new List<ProductCategoryResponse>();
        foreach (var c in categories)
            results.Add(await MapCategoryAsync(c, companyId));
        return results;
    }

    public async Task DeleteCategoryAsync(Guid companyId, Guid categoryId)
    {
        var category = await _db.ProductCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหมวดหมู่");
        _db.ProductCategories.Remove(category);
        await _db.SaveChangesAsync();
    }

    // ===== STOCK COUNT =====

    public async Task<StockCountResponse> CreateStockCountAsync(Guid companyId, CreateStockCountRequest request, string userId)
    {
        var count = new StockCount
        {
            CompanyId = companyId,
            CountNumber = $"SC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
            CountDate = request.CountDate,
            WarehouseId = request.WarehouseId,
            Notes = request.Notes,
            Status = "Draft",
            CreatedBy = userId
        };

        // Pre-populate lines with all stock-tracked products
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted)
            .OrderBy(p => p.Code)
            .ToListAsync();

        foreach (var p in products)
        {
            count.Lines.Add(new StockCountLine
            {
                CompanyId = companyId,
                ProductId = p.Id,
                SystemQty = p.CurrentStock,
                CountedQty = 0,
                Variance = -p.CurrentStock
            });
        }

        _db.StockCounts.Add(count);
        await _db.SaveChangesAsync();
        return await MapStockCountAsync(count);
    }

    public async Task<StockCountResponse> GetStockCountAsync(Guid companyId, Guid countId)
    {
        var count = await _db.StockCounts
            .Include(c => c.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(c => c.Id == countId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบตรวจนับ");
        return await MapStockCountAsync(count);
    }

    public async Task<List<StockCountResponse>> GetStockCountsAsync(Guid companyId)
    {
        var counts = await _db.StockCounts
            .Include(c => c.Lines)
            .Where(c => c.CompanyId == companyId)
            .OrderByDescending(c => c.CountDate)
            .ToListAsync();

        var results = new List<StockCountResponse>();
        foreach (var c in counts)
            results.Add(await MapStockCountAsync(c));
        return results;
    }

    public async Task<StockCountResponse> UpdateStockCountLinesAsync(Guid companyId, Guid countId, List<StockCountLineInput> lines)
    {
        var count = await _db.StockCounts
            .Include(c => c.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(c => c.Id == countId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบตรวจนับ");

        if (count.Status == "Completed")
            throw new InvalidOperationException("ใบตรวจนับนี้ปิดแล้ว");

        count.Status = "InProgress";

        foreach (var input in lines)
        {
            var line = count.Lines.FirstOrDefault(l => l.ProductId == input.ProductId);
            if (line != null)
            {
                line.CountedQty = input.CountedQty;
                line.Variance = input.CountedQty - line.SystemQty;
            }
        }

        await _db.SaveChangesAsync();
        return await MapStockCountAsync(count);
    }

    public async Task<StockCountResponse> ApplyStockCountAsync(Guid companyId, Guid countId, string userId)
    {
        var count = await _db.StockCounts
            .Include(c => c.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(c => c.Id == countId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบตรวจนับ");

        if (count.Status == "Completed")
            throw new InvalidOperationException("ใบตรวจนับนี้ปิดแล้ว");

        foreach (var line in count.Lines)
        {
            if (line.Variance == 0) continue;

            var product = await _db.Products.FindAsync(line.ProductId);
            if (product == null) continue;

            product.CurrentStock = line.CountedQty;

            _db.StockMovements.Add(new StockMovement
            {
                CompanyId = companyId,
                ProductId = line.ProductId,
                MovementDate = DateTime.UtcNow,
                MovementType = "ADJUST",
                Quantity = line.Variance,
                UnitCost = product.CostPrice,
                BalanceAfter = line.CountedQty,
                Reference = count.CountNumber,
                Notes = $"ปรับจากตรวจนับ {count.CountNumber} (ระบบ:{line.SystemQty} นับได้:{line.CountedQty})",
                CreatedBy = userId
            });
        }

        count.Status = "Completed";
        await _db.SaveChangesAsync();
        return await MapStockCountAsync(count);
    }

    // ===== INVENTORY VALUATION =====

    public async Task<InventoryValuationReport> GetInventoryValuationAsync(Guid companyId)
    {
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted && p.CurrentStock > 0)
            .OrderBy(p => p.Code)
            .ToListAsync();

        var items = new List<InventoryValuationItem>();
        foreach (var p in products)
        {
            // Weighted average cost from stock movements
            var movements = await _db.StockMovements
                .Where(m => m.ProductId == p.Id && m.MovementType == "IN")
                .ToListAsync();

            var totalCost = movements.Sum(m => m.Quantity * m.UnitCost);
            var totalQty = movements.Sum(m => m.Quantity);
            var avgCost = totalQty > 0 ? totalCost / totalQty : p.CostPrice;

            items.Add(new InventoryValuationItem(
                p.Id, p.Code, p.Name, p.Unit, p.Category,
                p.CurrentStock, avgCost, p.CurrentStock * avgCost));
        }

        return new InventoryValuationReport(
            DateTime.UtcNow, items,
            items.Sum(i => i.TotalValue),
            items.Count);
    }

    // ===== STOCK BALANCE AS OF DATE (สินค้าคงเหลือ ณ วันที่) =====

    public async Task<StockBalanceAsOfDateReport> GetStockBalanceAsOfDateAsync(Guid companyId, StockBalanceAsOfDateRequest request)
    {
        var asOfDate = request.AsOfDate.Date.AddDays(1); // Include the full day

        // Get all trackable products
        var productsQuery = _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted);
        if (!string.IsNullOrEmpty(request.Category))
            productsQuery = productsQuery.Where(p => p.Category == request.Category);

        var products = await productsQuery.OrderBy(p => p.Code).ToListAsync();

        // Load all stock movements up to the date
        var productIds = products.Select(p => p.Id).ToList();
        var movements = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementDate < asOfDate && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                TotalIn = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity),
                TotalOut = g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity),
                TotalAdjust = g.Where(m => m.MovementType == "ADJUST").Sum(m => m.Quantity),
                TotalCostIn = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity * m.UnitCost),
                TotalQtyIn = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity)
            })
            .ToListAsync();

        var movementLookup = movements.ToDictionary(m => m.ProductId);

        var items = new List<StockBalanceItem>();
        foreach (var p in products)
        {
            decimal qty = 0;
            decimal avgCost = p.CostPrice;

            if (movementLookup.TryGetValue(p.Id, out var mv))
            {
                qty = mv.TotalIn - mv.TotalOut + mv.TotalAdjust;
                avgCost = mv.TotalQtyIn > 0 ? mv.TotalCostIn / mv.TotalQtyIn : p.CostPrice;
            }

            if (!request.IncludeZeroStock && qty <= 0) continue;

            items.Add(new StockBalanceItem(
                p.Id, p.Code, p.Name, p.Unit, p.Category, p.ProductType.ToString(),
                qty, avgCost, qty * avgCost));
        }

        var byCategory = items
            .GroupBy(i => i.Category ?? "ไม่ระบุหมวด")
            .Select(g => new StockBalanceSummaryByCategory(
                g.Key, g.Count(), g.Sum(i => i.QuantityAsOfDate), g.Sum(i => i.TotalValue)))
            .OrderByDescending(c => c.TotalValue)
            .ToList();

        return new StockBalanceAsOfDateReport(
            request.AsOfDate, items, items.Sum(i => i.TotalValue), items.Count, byCategory);
    }

    // ===== INVENTORY PERIOD SNAPSHOT (สรุปสินค้า ณ สิ้นงวด) =====

    public async Task<InventorySnapshotResponse> CreateInventorySnapshotAsync(
        Guid companyId, CreateInventorySnapshotRequest request, string userId)
    {
        // Get stock balance as of the snapshot date
        var stockReport = await GetStockBalanceAsOfDateAsync(companyId,
            new StockBalanceAsOfDateRequest(request.SnapshotDate, null, false));

        var snapshot = new InventorySnapshot
        {
            CompanyId = companyId,
            SnapshotDate = request.SnapshotDate,
            Description = request.Description ?? $"สรุปสินค้าคงเหลือ ณ {request.SnapshotDate:dd/MM/yyyy}",
            TotalValue = stockReport.TotalValue,
            TotalProducts = stockReport.TotalProducts,
            Status = "Finalized",
            CreatedBy = userId,
            Lines = stockReport.Items.Select(item => new InventorySnapshotLine
            {
                CompanyId = companyId,
                ProductId = item.ProductId,
                Quantity = item.QuantityAsOfDate,
                UnitCost = item.AverageCost,
                TotalValue = item.TotalValue
            }).ToList()
        };

        // Auto-create journal entry for inventory if requested
        if (request.AutoCreateJournal && stockReport.TotalValue > 0)
        {
            var inventoryAccount = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("115") && a.IsActive); // สินค้าคงเหลือ
            var cogsSummaryAccount = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("514") && a.IsActive); // ต้นทุนขาย

            if (inventoryAccount != null && cogsSummaryAccount != null)
            {
                // Find previous snapshot to calculate COGS adjustment
                var previousSnapshot = await _db.InventorySnapshots
                    .Where(s => s.CompanyId == companyId && s.Status == "Finalized"
                        && s.SnapshotDate < request.SnapshotDate)
                    .OrderByDescending(s => s.SnapshotDate)
                    .FirstOrDefaultAsync();

                var previousValue = previousSnapshot?.TotalValue ?? 0;
                var adjustmentAmount = stockReport.TotalValue - previousValue;

                if (adjustmentAmount != 0)
                {
                    var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                        f.CompanyId == companyId && f.StartDate <= request.SnapshotDate
                        && f.EndDate >= request.SnapshotDate && f.Status == FiscalPeriodStatus.Open);

                    // Find next JV number
                    var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
                    var pattern = $"JV-{yearMonth}-";
                    var lastJe = await _db.JournalEntries
                        .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
                        .OrderByDescending(j => j.EntryNumber)
                        .Select(j => j.EntryNumber)
                        .FirstOrDefaultAsync();
                    int nextSeq = 1;
                    if (lastJe != null && int.TryParse(lastJe[pattern.Length..], out var lastNum))
                        nextSeq = lastNum + 1;

                    var journalLines = new List<JournalEntryLine>();
                    if (adjustmentAmount > 0)
                    {
                        // Inventory increased: Dr Inventory, Cr COGS adjustment
                        journalLines.Add(new JournalEntryLine { AccountId = inventoryAccount.Id, DebitAmount = adjustmentAmount, Description = "ปรับปรุงสินค้าคงเหลือ (เพิ่ม)", LineOrder = 1 });
                        journalLines.Add(new JournalEntryLine { AccountId = cogsSummaryAccount.Id, CreditAmount = adjustmentAmount, Description = "ปรับปรุงต้นทุนขาย", LineOrder = 2 });
                    }
                    else
                    {
                        // Inventory decreased: Dr COGS, Cr Inventory
                        var absAmount = Math.Abs(adjustmentAmount);
                        journalLines.Add(new JournalEntryLine { AccountId = cogsSummaryAccount.Id, DebitAmount = absAmount, Description = "ปรับปรุงต้นทุนขาย", LineOrder = 1 });
                        journalLines.Add(new JournalEntryLine { AccountId = inventoryAccount.Id, CreditAmount = absAmount, Description = "ปรับปรุงสินค้าคงเหลือ (ลด)", LineOrder = 2 });
                    }

                    var je = new JournalEntry
                    {
                        CompanyId = companyId,
                        EntryNumber = $"{pattern}{nextSeq:D4}",
                        EntryDate = request.SnapshotDate,
                        JournalType = JournalType.General,
                        Description = $"ปรับปรุงสินค้าคงเหลือ ณ {request.SnapshotDate:dd/MM/yyyy}",
                        Reference = $"INV-SNAPSHOT-{request.SnapshotDate:yyyyMMdd}",
                        Status = JournalEntryStatus.Posted,
                        IsAutoGenerated = true,
                        FiscalPeriodId = fiscalPeriod?.Id,
                        TotalDebit = journalLines.Sum(l => l.DebitAmount),
                        TotalCredit = journalLines.Sum(l => l.CreditAmount),
                        Lines = journalLines
                    };

                    _db.JournalEntries.Add(je);
                    await _db.SaveChangesAsync();
                    snapshot.JournalEntryId = je.Id;
                }
            }
        }

        _db.InventorySnapshots.Add(snapshot);
        await _db.SaveChangesAsync();

        return MapSnapshotResponse(snapshot);
    }

    public async Task<List<InventorySnapshotResponse>> GetInventorySnapshotsAsync(Guid companyId)
    {
        return await _db.InventorySnapshots
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .OrderByDescending(s => s.SnapshotDate)
            .Select(s => new InventorySnapshotResponse(
                s.Id, s.SnapshotDate, s.Status, s.Description,
                s.TotalValue, s.TotalProducts, s.JournalEntryId, s.CreatedAt))
            .ToListAsync();
    }

    public async Task<InventorySnapshotDetailResponse> GetInventorySnapshotDetailAsync(Guid companyId, Guid snapshotId)
    {
        var snapshot = await _db.InventorySnapshots
            .Include(s => s.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล snapshot");

        return new InventorySnapshotDetailResponse(
            snapshot.Id, snapshot.SnapshotDate, snapshot.Status, snapshot.Description,
            snapshot.TotalValue, snapshot.TotalProducts, snapshot.JournalEntryId, snapshot.CreatedAt,
            snapshot.Lines.Select(l => new InventorySnapshotLineResponse(
                l.ProductId, l.Product.Code, l.Product.Name,
                l.Product.Unit, l.Product.Category,
                l.Quantity, l.UnitCost, l.TotalValue)).ToList());
    }

    // ===== STOCK AGING REPORT =====

    public async Task<StockAgingReport> GetStockAgingReportAsync(Guid companyId)
    {
        var today = DateTime.UtcNow.Date;
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted && p.CurrentStock > 0)
            .OrderBy(p => p.Code)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();

        // Get last movement date for each product
        var lastMovements = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, LastDate = g.Max(m => m.MovementDate) })
            .ToListAsync();

        var lastMoveLookup = lastMovements.ToDictionary(m => m.ProductId, m => m.LastDate);

        // Get avg cost
        var costData = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementType == "IN" && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, TotalCost = g.Sum(m => m.Quantity * m.UnitCost), TotalQty = g.Sum(m => m.Quantity) })
            .ToListAsync();
        var costLookup = costData.ToDictionary(c => c.ProductId);

        var items = new List<StockAgingItem>();
        foreach (var p in products)
        {
            var lastMove = lastMoveLookup.GetValueOrDefault(p.Id, p.CreatedAt);
            var daysInStock = (int)(today - lastMove).TotalDays;
            var bucket = daysInStock switch
            {
                <= 30 => "0-30",
                <= 60 => "31-60",
                <= 90 => "61-90",
                <= 180 => "91-180",
                _ => "180+"
            };

            var avgCost = p.CostPrice;
            if (costLookup.TryGetValue(p.Id, out var cd) && cd.TotalQty > 0)
                avgCost = cd.TotalCost / cd.TotalQty;

            items.Add(new StockAgingItem(
                p.Id, p.Code, p.Name, p.Unit, p.Category,
                p.CurrentStock, p.CurrentStock * avgCost,
                daysInStock, bucket, lastMove));
        }

        var totalValue = items.Sum(i => i.TotalValue);
        var buckets = items.GroupBy(i => i.AgingBucket)
            .Select(g => new StockAgingBucketSummary(
                g.Key, g.Count(), g.Sum(i => i.TotalValue),
                totalValue > 0 ? Math.Round(g.Sum(i => i.TotalValue) / totalValue * 100, 2) : 0))
            .OrderBy(b => b.Bucket)
            .ToList();

        return new StockAgingReport(today, items, buckets, totalValue);
    }

    // ===== STOCK MOVEMENT SUMMARY (สรุปเคลื่อนไหวสินค้า) =====

    public async Task<StockMovementSummaryReport> GetStockMovementSummaryAsync(
        Guid companyId, StockMovementSummaryRequest request)
    {
        var fromDate = request.FromDate.Date;
        var toDate = request.ToDate.Date.AddDays(1);

        var productsQuery = _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted);
        if (!string.IsNullOrEmpty(request.Category))
            productsQuery = productsQuery.Where(p => p.Category == request.Category);
        if (request.ProductId.HasValue)
            productsQuery = productsQuery.Where(p => p.Id == request.ProductId.Value);

        var products = await productsQuery.OrderBy(p => p.Code).ToListAsync();
        var productIds = products.Select(p => p.Id).ToList();

        // Opening stock = movements before fromDate
        var openingData = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementDate < fromDate && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Opening = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity)
                         - g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity)
                         + g.Where(m => m.MovementType == "ADJUST").Sum(m => m.Quantity)
            })
            .ToListAsync();
        var openingLookup = openingData.ToDictionary(o => o.ProductId, o => o.Opening);

        // Period movements
        var periodData = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementDate >= fromDate && m.MovementDate < toDate && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                TotalIn = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity),
                TotalOut = g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity),
                TotalAdjust = g.Where(m => m.MovementType == "ADJUST").Sum(m => m.Quantity),
                CostOut = g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity * m.UnitCost)
            })
            .ToListAsync();
        var periodLookup = periodData.ToDictionary(p => p.ProductId);

        // Average cost
        var costData = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementType == "IN" && m.MovementDate < toDate && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, TotalCost = g.Sum(m => m.Quantity * m.UnitCost), TotalQty = g.Sum(m => m.Quantity) })
            .ToListAsync();
        var costLookup = costData.ToDictionary(c => c.ProductId);

        var items = new List<StockMovementSummaryItem>();
        foreach (var p in products)
        {
            var opening = openingLookup.GetValueOrDefault(p.Id, 0);
            var period = periodLookup.GetValueOrDefault(p.Id);
            var totalIn = period?.TotalIn ?? 0;
            var totalOut = period?.TotalOut ?? 0;
            var totalAdj = period?.TotalAdjust ?? 0;
            var closing = opening + totalIn - totalOut + totalAdj;
            var costOut = period?.CostOut ?? 0;

            if (opening == 0 && totalIn == 0 && totalOut == 0 && totalAdj == 0) continue;

            items.Add(new StockMovementSummaryItem(
                p.Id, p.Code, p.Name, p.Unit, p.Category,
                opening, totalIn, totalOut, totalAdj, closing, costOut));
        }

        // Calculate totals using avg cost
        decimal totalOpeningValue = 0, totalClosingValue = 0, totalCOGS = 0;
        foreach (var item in items)
        {
            var avgCost = costLookup.TryGetValue(item.ProductId, out var cd) && cd.TotalQty > 0
                ? cd.TotalCost / cd.TotalQty
                : products.First(p => p.Id == item.ProductId).CostPrice;
            totalOpeningValue += item.OpeningStock * avgCost;
            totalClosingValue += item.ClosingStock * avgCost;
            totalCOGS += item.CostOfGoodsOut;
        }

        return new StockMovementSummaryReport(
            request.FromDate, request.ToDate, items,
            totalOpeningValue, totalClosingValue, totalCOGS);
    }

    // ===== SUPPLIES (วัสดุสิ้นเปลือง) =====

    public async Task<SuppliesUsageResponse> UseSuppliesAsync(Guid companyId, SuppliesUsageRequest request, string userId)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.CompanyId == companyId && p.ProductType == ProductType.Supplies)
            ?? throw new KeyNotFoundException("ไม่พบวัสดุสิ้นเปลือง หรือสินค้าไม่ใช่ประเภทวัสดุสิ้นเปลือง");

        if (request.Quantity <= 0)
            throw new ArgumentException("จำนวนที่เบิกต้องมากกว่า 0");

        if (product.CurrentStock < request.Quantity)
            throw new InvalidOperationException($"วัสดุคงเหลือไม่เพียงพอ (คงเหลือ: {product.CurrentStock} {product.Unit})");

        // Calculate weighted average cost
        var inMovements = await _db.StockMovements
            .Where(m => m.ProductId == product.Id && m.MovementType == "IN" && !m.IsDeleted)
            .ToListAsync();
        var totalCost = inMovements.Sum(m => m.Quantity * m.UnitCost);
        var totalQty = inMovements.Sum(m => m.Quantity);
        var avgCost = totalQty > 0 ? totalCost / totalQty : product.CostPrice;

        var usageCost = request.Quantity * avgCost;

        // Deduct stock
        product.CurrentStock -= request.Quantity;

        // Create stock movement
        _db.StockMovements.Add(new StockMovement
        {
            CompanyId = companyId,
            ProductId = product.Id,
            MovementDate = DateTime.UtcNow,
            MovementType = "OUT",
            Quantity = request.Quantity,
            UnitCost = avgCost,
            BalanceAfter = product.CurrentStock,
            Reference = request.Reference,
            Notes = $"เบิกใช้วัสดุ: {request.Purpose ?? "-"} แผนก: {request.Department ?? "-"}",
            CreatedBy = userId
        });

        // Create usage log
        var usage = new SuppliesUsageLog
        {
            CompanyId = companyId,
            ProductId = product.Id,
            UsageDate = DateTime.UtcNow,
            Quantity = request.Quantity,
            UnitCost = avgCost,
            TotalCost = usageCost,
            Department = request.Department,
            Purpose = request.Purpose,
            Reference = request.Reference,
            Notes = request.Notes,
            IssuedToUserId = request.IssuedToUserId,
            IssuedToName = request.IssuedToName,
            CreatedBy = userId
        };

        // Auto journal: Dr ค่าวัสดุสิ้นเปลือง (5xxxxx) / Cr วัสดุสิ้นเปลือง (118xx)
        if (request.AutoCreateJournal && usageCost > 0)
        {
            var suppliesAccount = product.SuppliesAccountId.HasValue
                ? await _db.ChartOfAccounts.FindAsync(product.SuppliesAccountId.Value)
                : await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("118") && a.IsActive);

            var expenseAccount = product.SuppliesExpenseAccountId.HasValue
                ? await _db.ChartOfAccounts.FindAsync(product.SuppliesExpenseAccountId.Value)
                : await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("5") && a.IsActive
                    && (a.AccountCode.StartsWith("524") || a.AccountCode.StartsWith("531") || a.AccountCode.StartsWith("520")));

            if (suppliesAccount != null && expenseAccount != null)
            {
                var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
                var pattern = $"JV-{yearMonth}-";
                var lastJe = await _db.JournalEntries
                    .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
                    .OrderByDescending(j => j.EntryNumber)
                    .Select(j => j.EntryNumber)
                    .FirstOrDefaultAsync();
                int nextSeq = 1;
                if (lastJe != null && int.TryParse(lastJe[pattern.Length..], out var lastNum))
                    nextSeq = lastNum + 1;

                var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                    f.CompanyId == companyId && f.StartDate <= DateTime.UtcNow
                    && f.EndDate >= DateTime.UtcNow && f.Status == FiscalPeriodStatus.Open);

                var je = new JournalEntry
                {
                    CompanyId = companyId,
                    EntryNumber = $"{pattern}{nextSeq:D4}",
                    EntryDate = DateTime.UtcNow,
                    JournalType = JournalType.General,
                    Description = $"เบิกใช้วัสดุ: {product.Name} จำนวน {request.Quantity} {product.Unit} ({request.Department ?? "ทั่วไป"})",
                    Reference = request.Reference ?? $"SUP-USE-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Status = JournalEntryStatus.Posted,
                    IsAutoGenerated = true,
                    FiscalPeriodId = fiscalPeriod?.Id,
                    TotalDebit = usageCost,
                    TotalCredit = usageCost,
                    Lines = new List<JournalEntryLine>
                    {
                        new() { AccountId = expenseAccount.Id, DebitAmount = usageCost, Description = $"ค่าวัสดุสิ้นเปลือง - {product.Name}", LineOrder = 1 },
                        new() { AccountId = suppliesAccount.Id, CreditAmount = usageCost, Description = $"เบิกวัสดุ - {product.Name}", LineOrder = 2 }
                    }
                };

                _db.JournalEntries.Add(je);
                await _db.SaveChangesAsync();
                usage.JournalEntryId = je.Id;
            }
        }

        _db.Set<SuppliesUsageLog>().Add(usage);
        await _db.SaveChangesAsync();

        return new SuppliesUsageResponse(
            usage.Id, usage.ProductId, product.Code, product.Name, product.Unit,
            usage.UsageDate, usage.Quantity, usage.UnitCost, usage.TotalCost,
            usage.Department, usage.Purpose, usage.Reference, usage.JournalEntryId,
            usage.Notes, usage.IssuedToUserId, usage.IssuedToName);
    }

    public async Task<List<SuppliesUsageResponse>> GetSuppliesUsageHistoryAsync(Guid companyId, Guid productId)
    {
        return await _db.Set<SuppliesUsageLog>()
            .Include(u => u.Product)
            .Where(u => u.CompanyId == companyId && u.ProductId == productId && !u.IsDeleted)
            .OrderByDescending(u => u.UsageDate)
            .Select(u => new SuppliesUsageResponse(
                u.Id, u.ProductId, u.Product.Code, u.Product.Name, u.Product.Unit,
                u.UsageDate, u.Quantity, u.UnitCost, u.TotalCost,
                u.Department, u.Purpose, u.Reference, u.JournalEntryId,
                u.Notes, u.IssuedToUserId, u.IssuedToName))
            .ToListAsync();
    }

    public async Task<SuppliesUsageSummaryReport> GetSuppliesUsageSummaryAsync(
        Guid companyId, SuppliesUsageSummaryRequest request)
    {
        var fromDate = request.FromDate.Date;
        var toDate = request.ToDate.Date.AddDays(1);

        var query = _db.Set<SuppliesUsageLog>()
            .Include(u => u.Product)
            .Where(u => u.CompanyId == companyId && !u.IsDeleted
                && u.UsageDate >= fromDate && u.UsageDate < toDate);

        if (!string.IsNullOrEmpty(request.Department))
            query = query.Where(u => u.Department == request.Department);
        if (!string.IsNullOrEmpty(request.Category))
            query = query.Where(u => u.Product.Category == request.Category);
        if (request.ProductId.HasValue)
            query = query.Where(u => u.ProductId == request.ProductId.Value);

        var rawData = await query.Select(u => new
        {
            u.ProductId, ProductCode = u.Product.Code, ProductName = u.Product.Name,
            Unit = u.Product.Unit, Category = u.Product.Category,
            u.Department, u.Quantity, u.TotalCost
        }).ToListAsync();

        var items = rawData
            .GroupBy(u => new { u.ProductId, u.ProductCode, u.ProductName, u.Unit, u.Category, u.Department })
            .Select(g => new SuppliesUsageSummaryItem(
                g.Key.ProductId, g.Key.ProductCode, g.Key.ProductName,
                g.Key.Unit, g.Key.Category, g.Key.Department ?? "ไม่ระบุ",
                g.Sum(x => x.Quantity), g.Sum(x => x.TotalCost)))
            .OrderByDescending(i => i.TotalCost)
            .ToList();

        var grandTotal = items.Sum(i => i.TotalCost);

        var byDept = items
            .GroupBy(i => i.Department ?? "ไม่ระบุ")
            .Select(g => new SuppliesUsageByDepartment(
                g.Key, g.Sum(i => i.TotalCost),
                grandTotal > 0 ? Math.Round(g.Sum(i => i.TotalCost) / grandTotal * 100, 2) : 0))
            .OrderByDescending(d => d.TotalCost)
            .ToList();

        var byCat = items
            .GroupBy(i => i.Category ?? "ไม่ระบุ")
            .Select(g => new SuppliesUsageByCategory(
                g.Key, g.Sum(i => i.TotalCost),
                grandTotal > 0 ? Math.Round(g.Sum(i => i.TotalCost) / grandTotal * 100, 2) : 0))
            .OrderByDescending(c => c.TotalCost)
            .ToList();

        return new SuppliesUsageSummaryReport(
            request.FromDate, request.ToDate, items, grandTotal, byDept, byCat);
    }

    public async Task<SuppliesBalanceReport> GetSuppliesBalanceAsync(Guid companyId, string? category)
    {
        var query = _db.Products
            .Where(p => p.CompanyId == companyId && p.ProductType == ProductType.Supplies && !p.IsDeleted);
        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        var products = await query.OrderBy(p => p.Code).ToListAsync();
        var productIds = products.Select(p => p.Id).ToList();

        // Weighted avg cost
        var costData = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementType == "IN" && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, TotalCost = g.Sum(m => m.Quantity * m.UnitCost), TotalQty = g.Sum(m => m.Quantity) })
            .ToListAsync();
        var costLookup = costData.ToDictionary(c => c.ProductId);

        var items = products.Select(p =>
        {
            var avgCost = p.CostPrice;
            if (costLookup.TryGetValue(p.Id, out var cd) && cd.TotalQty > 0)
                avgCost = cd.TotalCost / cd.TotalQty;

            return new SuppliesBalanceItem(
                p.Id, p.Code, p.Name, p.Unit, p.Category,
                p.CurrentStock, avgCost, p.CurrentStock * avgCost,
                p.MinimumStock, p.CurrentStock <= p.MinimumStock);
        }).ToList();

        return new SuppliesBalanceReport(
            DateTime.UtcNow, items,
            items.Sum(i => i.TotalValue), items.Count,
            items.Count(i => i.IsLow));
    }

    private static InventorySnapshotResponse MapSnapshotResponse(InventorySnapshot s) => new(
        s.Id, s.SnapshotDate, s.Status, s.Description,
        s.TotalValue, s.TotalProducts, s.JournalEntryId, s.CreatedAt);

    // ===== HELPERS =====

    private static ProductResponse MapToResponse(Product p)
    {
        var imgs = ParseImageUrls(p.ImageUrlsJson);
        return new ProductResponse(
            p.Id, p.Code, p.Name, p.NameEn, p.Description, p.ProductType,
            p.SKU, p.Barcode, p.Category, p.Unit, p.SellingPrice, p.CostPrice,
            p.VatRate, p.IsVatIncluded, p.CurrentStock, p.MinimumStock,
            p.TrackStock, p.IsActive,
            p.SalesAccountId, p.SalesAccount?.AccountName,
            p.PurchaseAccountId, p.PurchaseAccount?.AccountName,
            p.InventoryAccountId, p.InventoryAccount?.AccountName,
            p.UnitConversions?.Select(MapConversion).ToList(),
            imgs,
            imgs.Count > 0 ? imgs[0] : null,
            p.PrintStation);
    }

    private static List<string> ParseImageUrls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return list?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? new();
        }
        catch { return new(); }
    }

    private static UnitConversionResponse MapConversion(UnitConversion u) =>
        new(u.Id, u.ProductId, u.FromUnit, u.ToUnit, u.ConversionRate,
            u.SellingPrice, u.CostPrice, u.Barcode);

    private async Task<ProductCategoryResponse> MapCategoryAsync(ProductCategory c, Guid companyId)
    {
        var productCount = await _db.Products.CountAsync(p => p.CompanyId == companyId && p.Category == c.Name && !p.IsDeleted);
        return new ProductCategoryResponse(c.Id, c.Code, c.Name, c.Description,
            c.ParentCategoryId, c.ParentCategory?.Name, c.IsActive, productCount);
    }

    private Task<StockCountResponse> MapStockCountAsync(StockCount c)
    {
        var lines = c.Lines.Select(l => new StockCountLineResponse(
            l.Id, l.ProductId, l.Product?.Code ?? "", l.Product?.Name ?? "", l.Product?.Unit ?? "",
            l.SystemQty, l.CountedQty, l.Variance, l.Notes)).ToList();

        return Task.FromResult(new StockCountResponse(c.Id, c.CountNumber, c.CountDate, c.Status,
            c.Notes, c.WarehouseId, lines.Count,
            lines.Sum(l => Math.Abs(l.Variance)), lines));
    }
}
