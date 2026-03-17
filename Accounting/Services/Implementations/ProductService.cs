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

    public async Task<ProductResponse> CreateAsync(Guid companyId, CreateProductRequest request)
    {
        if (await _db.Set<Product>().AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code))
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

        _db.Set<Product>().Add(product);
        await _db.SaveChangesAsync();

        return MapToResponse(product);
    }

    public async Task<ProductResponse> GetByIdAsync(Guid companyId, Guid productId)
    {
        var product = await _db.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");
        return MapToResponse(product);
    }

    public async Task<PagedResponse<ProductResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.Set<Product>().Where(p => p.CompanyId == companyId && !p.IsDeleted);
        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(p => p.Name.Contains(request.Search) || p.Code.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query.OrderBy(p => p.Code)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<ProductResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<ProductResponse> UpdateAsync(Guid companyId, Guid productId, UpdateProductRequest request)
    {
        var product = await _db.Set<Product>()
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
        var product = await _db.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินค้า");
        product.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<StockMovementResponse> AdjustStockAsync(Guid companyId, StockAdjustmentRequest request, string userId)
    {
        var product = await _db.Set<Product>()
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

        _db.Set<StockMovement>().Add(movement);
        await _db.SaveChangesAsync();

        return new StockMovementResponse(movement.Id, movement.ProductId, product.Name,
            movement.MovementDate, movement.MovementType, movement.Quantity,
            movement.UnitCost, movement.BalanceAfter, movement.Reference);
    }

    public async Task<List<StockMovementResponse>> GetStockMovementsAsync(Guid companyId, Guid productId)
    {
        var movements = await _db.Set<StockMovement>()
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
        var products = await _db.Set<Product>()
            .Where(p => p.CompanyId == companyId && p.TrackStock && p.CurrentStock <= p.MinimumStock && !p.IsDeleted)
            .ToListAsync();
        return products.Select(MapToResponse).ToList();
    }

    private static ProductResponse MapToResponse(Product p) => new(
        p.Id, p.Code, p.Name, p.NameEn, p.Description, p.ProductType,
        p.SKU, p.Category, p.Unit, p.SellingPrice, p.CostPrice,
        p.VatRate, p.IsVatIncluded, p.CurrentStock, p.MinimumStock,
        p.TrackStock, p.IsActive);
}
