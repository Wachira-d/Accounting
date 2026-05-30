using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/search")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly IQuickSearchService _svc;

    public SearchController(IQuickSearchService svc) { _svc = svc; }

    /// <summary>Cross-entity quick search — the Cmd/Ctrl-K palette.
    /// Searches Contacts, Products, Documents, Journal Entries.
    /// Returns up to 8 hits per kind, ranked by recency.</summary>
    [HttpGet("quick")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<QuickSearchHit>>>> Quick(
        Guid companyId, [FromQuery] string q, [FromQuery] int perKindLimit = 8,
        CancellationToken ct = default)
    {
        var hits = await _svc.SearchAsync(companyId, q, Math.Clamp(perKindLimit, 1, 20), ct);
        return Ok(new ApiResponse<IReadOnlyList<QuickSearchHit>>(true, hits,
            $"พบ {hits.Count} รายการ"));
    }
}
