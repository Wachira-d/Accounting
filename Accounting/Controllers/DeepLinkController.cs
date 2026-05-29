using Accounting.Helpers;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Public schema for the deep-link URLs external systems can ship back to
/// users after creating records via API. Lets integrators discover the
/// canonical pattern instead of guessing.
///
/// Pattern: <c>https://{host}/{companyId}/{entity}/{recordId}</c>
/// Example: <c>https://nextacc.net/3c98.../journals/5a95...</c>
///
/// The fallback middleware (Program.cs MapFallback → DeepLinkRewriter)
/// rewrites these to the in-app page URL with the right query param so the
/// existing per-page deep-link handlers auto-open the record.
/// </summary>
[ApiController]
[Route("api/integration")]
public class DeepLinkController : ControllerBase
{
    [HttpGet("link-patterns")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<object>> GetPatterns()
    {
        var host = $"{Request.Scheme}://{Request.Host}";
        var patterns = new object[]
        {
            new { entity = "journals",        pattern = $"{host}/{{companyId}}/journals/{{entryId}}",        opens = "Journal Entry detail" },
            new { entity = "documents",       pattern = $"{host}/{{companyId}}/documents/{{documentId}}",    opens = "Document edit" },
            new { entity = "contacts",        pattern = $"{host}/{{companyId}}/contacts/{{contactId}}",      opens = "Contact detail" },
            new { entity = "products",        pattern = $"{host}/{{companyId}}/products/{{productId}}",      opens = "Product detail" },
            new { entity = "fixed-assets",    pattern = $"{host}/{{companyId}}/fixed-assets/{{assetId}}",    opens = "Fixed Asset detail" },
            new { entity = "payments",        pattern = $"{host}/{{companyId}}/payments/{{paymentId}}",      opens = "Payment detail" },
            new { entity = "supplies",        pattern = $"{host}/{{companyId}}/supplies/{{productId}}",      opens = "Supplies detail" },
            new { entity = "projects",        pattern = $"{host}/{{companyId}}/projects/{{projectId}}",      opens = "Project detail" },
            new { entity = "payroll-runs",    pattern = $"{host}/{{companyId}}/payroll-runs/{{runId}}",      opens = "Payroll run detail" },
            new { entity = "reservations",    pattern = $"{host}/{{companyId}}/reservations/{{reservationId}}", opens = "Reservation detail" },
            new { entity = "pos-orders",      pattern = $"{host}/{{companyId}}/pos-orders/{{orderId}}",      opens = "POS order detail" },
        };
        return Ok(new ApiResponse<object>(true, new
        {
            host,
            authRequired = "If the user is not logged in, they're sent to /login.html with returnTo cookie — they land on the deep-link target after login.",
            companySwitch = "If the user's currently selected company differs from {companyId}, the layout JS auto-switches to that company before opening the record.",
            queryFlag = "All rewritten URLs include &fromDeepLink=1 so pages can show a small 'opened from external system' indicator.",
            patterns,
        }));
    }

    /// <summary>Build the canonical URL for a single record. Useful when an
    /// API consumer wants the link back without computing it themselves.</summary>
    [HttpGet("link/{entity}/{companyId:guid}/{recordId:guid}")]
    [Authorize]
    public ActionResult<ApiResponse<object>> BuildLink(string entity, Guid companyId, Guid recordId)
    {
        var host = $"{Request.Scheme}://{Request.Host}";
        var url = DeepLinkRewriter.BuildLink(host, companyId, entity, recordId);
        return Ok(new ApiResponse<object>(true, new { url }));
    }
}
