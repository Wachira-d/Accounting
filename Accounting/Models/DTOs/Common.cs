namespace Accounting.Models.DTOs;

public record ApiResponse<T>(bool Success, T? Data, string? Message = null, List<string>? Errors = null);

public record PagedRequest(int Page = 1, int PageSize = 20, string? Search = null, string? SortBy = null, bool SortDesc = false);

public record PagedResponse<T>(List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);
