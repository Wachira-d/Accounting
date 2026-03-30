using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Product;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ProductService : IProductService
{
    private readonly AccountingDbContext _db;

    public ProductService(AccountingDbContext db)
    {
        _db = db;
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
            TrackStock = request.TrackStock,
            MinimumStock = request.MinimumStock
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return MapToResponse(product);
    }

    public async Task<ProductResponse> GetByIdAsync(Guid companyId, Guid productId)
    {
        var product = await _db.Products
            .Include(p => p.UnitConversions)
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");
        return MapToResponse(product);
    }

    public async Task<PagedResponse<ProductResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.Products.Where(p => p.CompanyId == companyId && !p.IsDeleted);
        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(p => p.Name.Contains(request.Search) || p.Code.Contains(request.Search));

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
        if (request.Category != null) product.Category = request.Category;
        if (request.SellingPrice.HasValue) product.SellingPrice = request.SellingPrice.Value;
        if (request.CostPrice.HasValue) product.CostPrice = request.CostPrice.Value;
        if (request.VatRate.HasValue) product.VatRate = request.VatRate.Value;
        if (request.IsVatIncluded.HasValue) product.IsVatIncluded = request.IsVatIncluded.Value;
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;
        if (request.MinimumStock.HasValue) product.MinimumStock = request.MinimumStock.Value;

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
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");

        var qty = request.MovementType == "OUT" ? -Math.Abs(request.Quantity) : Math.Abs(request.Quantity);
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

        return new StockMovementResponse(movement.Id, movement.ProductId, product.Name,
            movement.MovementDate, movement.MovementType, movement.Quantity,
            movement.UnitCost, movement.BalanceAfter, movement.Reference);
    }

    public async Task<List<StockMovementResponse>> GetStockMovementsAsync(Guid companyId, Guid productId)
    {
        var movements = await _db.StockMovements
            .Include(m => m.Product)
            .Where(m => m.CompanyId == companyId && m.ProductId == productId)
            .OrderByDescending(m => m.MovementDate)
            .ToListAsync();

        return movements.Select(m => new StockMovementResponse(
            m.Id, m.ProductId, m.Product.Name, m.MovementDate,
            m.MovementType, m.Quantity, m.UnitCost, m.BalanceAfter, m.Reference)).ToList();
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

    // ===== HELPERS =====

    private static ProductResponse MapToResponse(Product p) => new(
        p.Id, p.Code, p.Name, p.NameEn, p.Description, p.ProductType,
        p.SKU, p.Category, p.Unit, p.SellingPrice, p.CostPrice,
        p.VatRate, p.IsVatIncluded, p.CurrentStock, p.MinimumStock,
        p.TrackStock, p.IsActive,
        p.UnitConversions?.Select(MapConversion).ToList());

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
