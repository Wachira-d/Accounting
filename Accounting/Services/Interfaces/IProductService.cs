using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Product;

namespace Accounting.Services.Interfaces;

public interface IProductService
{
    Task<ProductResponse> CreateAsync(Guid companyId, CreateProductRequest request);
    Task<ProductResponse> GetByIdAsync(Guid companyId, Guid productId);
    Task<PagedResponse<ProductResponse>> GetAllAsync(Guid companyId, PagedRequest request);
    Task<ProductResponse> UpdateAsync(Guid companyId, Guid productId, UpdateProductRequest request);
    Task DeleteAsync(Guid companyId, Guid productId);

    // Stock
    Task<StockMovementResponse> AdjustStockAsync(Guid companyId, StockAdjustmentRequest request, string userId);
    Task<List<StockMovementResponse>> GetStockMovementsAsync(Guid companyId, Guid productId);
    Task<List<ProductResponse>> GetLowStockProductsAsync(Guid companyId);
}
