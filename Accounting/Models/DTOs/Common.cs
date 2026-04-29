namespace Accounting.Models.DTOs;

public record ApiResponse<T>(bool Success, T? Data, string? Message = null, List<string>? Errors = null);

public record PagedRequest(int Page = 1, int PageSize = 20, string? Search = null, string? SortBy = null, bool SortDesc = false)
{
    public int Page { get; init; } = Math.Max(Page, 1);
    public int PageSize { get; init; } = Math.Clamp(PageSize, 1, 200);
}

public record PagedResponse<T>(List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);
