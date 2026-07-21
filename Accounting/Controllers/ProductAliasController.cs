using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// CRUD for the learned vendor-to-product alias table. The OCR pipeline writes
/// these automatically when the user confirms a match, but the owner can also
/// curate them by hand — fix misspellings, delete bad mappings, or pre-seed
/// common vendor names ahead of the first scan.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/product-aliases")]
[Authorize]
public class ProductAliasController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public ProductAliasController(AccountingDbContext db) { _db = db; }

    public record AliasResponse(Guid Id, Guid ProductId, string ProductCode, string ProductName,
        Guid? ContactId, string? ContactName, string AliasName, string NormalizedName,
        int TimesUsed, DateTime LastUsedAt, string Source);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<AliasResponse>>>> List(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null, [FromQuery] Guid? productId = null, [FromQuery] Guid? contactId = null)
    {
        var q = _db.ProductAliases
            .Include(a => a.Product).Include(a => a.Contact)
            .Where(a => a.CompanyId == companyId && !a.IsDeleted);
        if (productId.HasValue) q = q.Where(a => a.ProductId == productId.Value);
        if (contactId.HasValue) q = q.Where(a => a.ContactId == contactId.Value);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(a => a.AliasName.Contains(search) || a.Product.Name.Contains(search));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(a => a.TimesUsed).ThenByDescending(a => a.LastUsedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AliasResponse(a.Id, a.ProductId, a.Product.Code, a.Product.Name,
                a.ContactId, a.Contact != null ? a.Contact.Name : null,
                a.AliasName, a.NormalizedName, a.TimesUsed, a.LastUsedAt, a.Source))
            .ToListAsync();
        return Ok(new ApiResponse<PagedResponse<AliasResponse>>(true,
            new PagedResponse<AliasResponse>(items, total, page, pageSize,
                (int)Math.Ceiling(total / (double)pageSize))));
    }

    public record CreateAliasRequest(Guid ProductId, string AliasName, Guid? ContactId);
    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create(Guid companyId, [FromBody] CreateAliasRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AliasName))
            return BadRequest(new ApiResponse<Guid>(false, default, "AliasName ห้ามว่าง"));
        var productExists = await _db.Products.AnyAsync(p => p.Id == req.ProductId && p.CompanyId == companyId);
        if (!productExists) return BadRequest(new ApiResponse<Guid>(false, default, "ไม่พบสินค้า"));

        var normalized = NormalizeName(req.AliasName);
        // Dedupe by (ProductId, ContactId, NormalizedName) so manual entry of an
        // existing learned alias bumps TimesUsed instead of creating duplicate rows.
        var existing = await _db.ProductAliases.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.ProductId == req.ProductId
            && a.ContactId == req.ContactId && a.NormalizedName == normalized);
        if (existing != null)
        {
            existing.TimesUsed += 1; existing.LastUsedAt = DateTime.UtcNow; existing.IsDeleted = false;
            await _db.SaveChangesAsync();
            return Ok(new ApiResponse<Guid>(true, existing.Id, "อัพเดต alias เดิม"));
        }

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var alias = new ProductAlias
        {
            CompanyId = companyId, ProductId = req.ProductId, ContactId = req.ContactId,
            AliasName = req.AliasName.Trim(), NormalizedName = normalized,
            Source = "user", CreatedBy = userId
        };
        _db.ProductAliases.Add(alias);
        await _db.SaveChangesAsync();
        return StatusCode(201, new ApiResponse<Guid>(true, alias.Id, "สร้าง alias สำเร็จ"));
    }

    [HttpDelete("{aliasId:guid}")]
    public async Task<IActionResult> Delete(Guid companyId, Guid aliasId)
    {
        var alias = await _db.ProductAliases.FirstOrDefaultAsync(a => a.Id == aliasId && a.CompanyId == companyId);
        if (alias == null) return NotFound();
        alias.IsDeleted = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Mirror of the normalization used by VendorIntelligenceService —
    /// keep these in sync or aliases written from the admin UI won't match
    /// aliases learned from OCR.</summary>
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var s = name.ToLowerInvariant();
        // Strip punctuation + collapse whitespace.
        var sb = new System.Text.StringBuilder();
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch); prevSpace = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0) { sb.Append(' '); prevSpace = true; }
            }
            // drop punctuation
        }
        return sb.ToString().Trim();
    }
}
