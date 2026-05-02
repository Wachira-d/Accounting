using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsReviewService : ICmsReviewService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsReviewService> _logger;

    public CmsReviewService(AccountingDbContext db, ILogger<CmsReviewService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewResponse> CreateReviewAsync(Guid companyId, Guid siteId, CreateReviewRequest request, Guid? customerId)
    {
        var product = await _db.SiteProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.SiteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Product not found.");

        bool verified = false;
        Guid? orderIdToLink = request.OrderId;
        if (request.OrderId.HasValue)
        {
            verified = await _db.SiteOrders.AnyAsync(o =>
                o.Id == request.OrderId.Value && o.SiteId == siteId &&
                o.CustomerId == customerId &&
                o.Status == SiteOrderStatus.Delivered &&
                o.Lines.Any(l => l.SiteProductId == request.SiteProductId));
            if (!verified) orderIdToLink = null;
        }

        var review = new SiteProductReview
        {
            CompanyId = companyId, SiteId = siteId,
            SiteProductId = request.SiteProductId,
            CustomerId = customerId,
            OrderId = orderIdToLink,
            ReviewerName = request.ReviewerName,
            ReviewerEmail = request.ReviewerEmail,
            Rating = Math.Clamp(request.Rating, 1, 5),
            Title = request.Title,
            Content = request.Content,
            ImageUrlsJson = request.ImageUrls?.Any() == true ? JsonSerializer.Serialize(request.ImageUrls) : null,
            IsVerifiedPurchase = verified,
            IsApproved = false
        };
        _db.SiteProductReviews.Add(review);
        await _db.SaveChangesAsync();
        return MapReview(review, product.DisplayName ?? string.Empty);
    }

    public async Task<PagedResponse<ReviewResponse>> GetProductReviewsAsync(Guid companyId, Guid siteId, Guid siteProductId, bool approvedOnly = true, int page = 1, int pageSize = 20)
    {
        var q = _db.SiteProductReviews.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.CompanyId == companyId && r.SiteProductId == siteProductId && !r.IsHidden);
        if (approvedOnly) q = q.Where(r => r.IsApproved);

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ReviewResponse
            {
                Id = r.Id, SiteProductId = r.SiteProductId,
                CustomerId = r.CustomerId, ReviewerName = r.ReviewerName,
                Rating = r.Rating, Title = r.Title, Content = r.Content,
                ImageUrlsJson = r.ImageUrlsJson,
                IsVerifiedPurchase = r.IsVerifiedPurchase, IsApproved = r.IsApproved,
                AdminReply = r.AdminReply, AdminRepliedAt = r.AdminRepliedAt,
                HelpfulCount = r.HelpfulCount, CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<ReviewResponse> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<PagedResponse<ReviewResponse>> GetSiteReviewsAsync(Guid companyId, Guid siteId, bool? approved = null, int page = 1, int pageSize = 20)
    {
        var q = _db.SiteProductReviews.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.CompanyId == companyId);
        if (approved.HasValue) q = q.Where(r => r.IsApproved == approved.Value);

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ReviewResponse
            {
                Id = r.Id, SiteProductId = r.SiteProductId,
                ProductName = r.SiteProduct.DisplayName ?? r.SiteProduct.Product.Name,
                CustomerId = r.CustomerId, ReviewerName = r.ReviewerName,
                Rating = r.Rating, Title = r.Title, Content = r.Content,
                ImageUrlsJson = r.ImageUrlsJson,
                IsVerifiedPurchase = r.IsVerifiedPurchase, IsApproved = r.IsApproved,
                AdminReply = r.AdminReply, AdminRepliedAt = r.AdminRepliedAt,
                HelpfulCount = r.HelpfulCount, CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<ReviewResponse> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<ReviewSummaryResponse> GetProductReviewSummaryAsync(Guid companyId, Guid siteId, Guid siteProductId)
    {
        var reviews = await _db.SiteProductReviews.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.SiteProductId == siteProductId && r.IsApproved && !r.IsHidden)
            .Select(r => r.Rating)
            .ToListAsync();

        if (reviews.Count == 0)
            return new ReviewSummaryResponse { SiteProductId = siteProductId };

        return new ReviewSummaryResponse
        {
            SiteProductId = siteProductId,
            TotalReviews = reviews.Count,
            AverageRating = Math.Round((decimal)reviews.Average(), 2),
            FiveStarCount = reviews.Count(r => r == 5),
            FourStarCount = reviews.Count(r => r == 4),
            ThreeStarCount = reviews.Count(r => r == 3),
            TwoStarCount = reviews.Count(r => r == 2),
            OneStarCount = reviews.Count(r => r == 1)
        };
    }

    public async Task<ReviewResponse> ModerateReviewAsync(Guid companyId, Guid siteId, Guid reviewId, ModerateReviewRequest request, string userId)
    {
        var review = await _db.SiteProductReviews
            .Include(r => r.SiteProduct)
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.SiteId == siteId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Review not found.");

        review.IsApproved = request.IsApproved;
        review.IsHidden = request.IsHidden;
        review.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return MapReview(review, review.SiteProduct.DisplayName ?? string.Empty);
    }

    public async Task<ReviewResponse> ReplyToReviewAsync(Guid companyId, Guid siteId, Guid reviewId, AdminReplyReviewRequest request, string userId)
    {
        var review = await _db.SiteProductReviews
            .Include(r => r.SiteProduct)
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.SiteId == siteId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Review not found.");

        review.AdminReply = request.Reply;
        review.AdminRepliedAt = DateTime.UtcNow;
        review.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return MapReview(review, review.SiteProduct.DisplayName ?? string.Empty);
    }

    public async Task<bool> DeleteReviewAsync(Guid companyId, Guid siteId, Guid reviewId)
    {
        var review = await _db.SiteProductReviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.SiteId == siteId && r.CompanyId == companyId);
        if (review == null) return false;
        review.IsDeleted = true;
        review.IsHidden = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> MarkHelpfulAsync(Guid companyId, Guid siteId, Guid reviewId)
    {
        var review = await _db.SiteProductReviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.SiteId == siteId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Review not found.");
        review.HelpfulCount++;
        await _db.SaveChangesAsync();
        return review.HelpfulCount;
    }

    private static ReviewResponse MapReview(SiteProductReview r, string productName) => new()
    {
        Id = r.Id, SiteProductId = r.SiteProductId, ProductName = productName,
        CustomerId = r.CustomerId, ReviewerName = r.ReviewerName,
        Rating = r.Rating, Title = r.Title, Content = r.Content,
        ImageUrlsJson = r.ImageUrlsJson,
        IsVerifiedPurchase = r.IsVerifiedPurchase, IsApproved = r.IsApproved,
        AdminReply = r.AdminReply, AdminRepliedAt = r.AdminRepliedAt,
        HelpfulCount = r.HelpfulCount, CreatedAt = r.CreatedAt
    };
}
