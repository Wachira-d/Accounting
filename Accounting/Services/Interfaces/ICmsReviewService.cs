using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICmsReviewService
{
    Task<ReviewResponse> CreateReviewAsync(Guid companyId, Guid siteId, CreateReviewRequest request, Guid? customerId);
    Task<PagedResponse<ReviewResponse>> GetProductReviewsAsync(Guid companyId, Guid siteId, Guid siteProductId, bool approvedOnly = true, int page = 1, int pageSize = 20);
    Task<PagedResponse<ReviewResponse>> GetSiteReviewsAsync(Guid companyId, Guid siteId, bool? approved = null, int page = 1, int pageSize = 20);
    Task<ReviewSummaryResponse> GetProductReviewSummaryAsync(Guid companyId, Guid siteId, Guid siteProductId);
    Task<ReviewResponse> ModerateReviewAsync(Guid companyId, Guid siteId, Guid reviewId, ModerateReviewRequest request, string userId);
    Task<ReviewResponse> ReplyToReviewAsync(Guid companyId, Guid siteId, Guid reviewId, AdminReplyReviewRequest request, string userId);
    Task<bool> DeleteReviewAsync(Guid companyId, Guid siteId, Guid reviewId);
    Task<int> MarkHelpfulAsync(Guid companyId, Guid siteId, Guid reviewId);
}
