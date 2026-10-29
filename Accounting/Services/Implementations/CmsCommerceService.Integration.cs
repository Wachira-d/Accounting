using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 แพ็ก P6 — จุดเชื่อมของร้านค้าเว็บกับ ERP: ตัวเลือกย่อย (variant) ราคา/สต็อก/SKU จริง (R-209-E5/D9) ·
/// คลังที่ตัดสต็อก (R-209-D3) — โค้ดใหม่อยู่ไฟล์นี้ (แก้ <c>CmsCommerceService.cs</c> เฉพาะจุดเรียก)
/// </summary>
public partial class CmsCommerceService
{
    /// <summary>variant ที่ลูกค้าเลือก — ผ่านด่าน <see cref="CmsVariantPricing.SelectionProblem"/> (สินค้ามี variant ⇒ ต้องเลือก · ต้องเป็นของสินค้านี้ · ใช้งานอยู่) ·
    /// สินค้าไม่มี variant + ไม่ส่งมา ⇒ null (เส้นเดิม)</summary>
    private async Task<SiteProductVariant?> ResolveSelectedVariantAsync(Guid companyId, Guid siteProductId, Guid? variantId)
    {
        var variants = await _db.SiteProductVariants.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.SiteProductId == siteProductId && !v.IsDeleted)
            .ToListAsync();
        var found = variantId is Guid id ? variants.FirstOrDefault(v => v.Id == id) : null;
        if (CmsVariantPricing.SelectionProblem(variants.Any(CmsVariantPricing.IsSelectable), variantId, found) is string problem)
            throw new BusinessRuleException(problem, CmsVariantPricing.RuleCode);
        return found;
    }

    /// <summary>variant ของบรรทัดชุดหนึ่ง (กรองบริษัท) — ใช้ตั้งชื่อ/SKU บรรทัดออเดอร์จากตะกร้า</summary>
    private async Task<Dictionary<Guid, SiteProductVariant>> LoadVariantsByIdAsync(Guid companyId, IEnumerable<Guid?> ids)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, SiteProductVariant>();
        return await _db.SiteProductVariants.AsNoTracking()
            .Where(v => v.CompanyId == companyId && list.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id);
    }

    private sealed record CmsLineStockTarget(Guid ProductId, string ProductName, decimal Available);

    /// <summary>
    /// สินค้า ERP + ยอดคงเหลือที่บรรทัดออเดอร์จะตัด/คืน — ตรึงแล้ว (<c>StockProductId</c>) ⇒ ตัวนั้น · variant ผูกสินค้า ERP ⇒ สินค้านั้น · อื่น ๆ ⇒ สินค้าแม่ (เดิม) ·
    /// ยอดคงเหลือ: มีคลังของเว็บ ⇒ ยอดในคลังนั้น (ledger) · ไม่มี ⇒ <c>Product.CurrentStock</c> (เดิม) ·
    /// variant ผูกสินค้า ERP ที่ถูกลบ ⇒ โยนชัด (ห้ามตัดสินค้าแม่แทนเงียบ ๆ)
    /// </summary>
    private async Task<Dictionary<Guid, CmsLineStockTarget>> LoadLineStockTargetsAsync(Guid companyId, IReadOnlyCollection<SiteOrderLine> lines, Guid? warehouseId)
    {
        var variants = await LoadVariantsByIdAsync(companyId, lines.Select(l => l.VariantId));
        var wanted = new Dictionary<Guid, Guid>();   // lineId → productId
        foreach (var l in lines)
        {
            var parent = l.SiteProduct.ProductId;
            var erp = l.VariantId is Guid vid && variants.TryGetValue(vid, out var v) ? v.ErpProductId : null;
            wanted[l.Id] = l.StockProductId ?? CmsVariantPricing.StockProductId(parent, erp);
        }
        var ids = wanted.Values.Distinct().ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && ids.Contains(p.Id) && !p.IsDeleted)
            .Select(p => new { p.Id, p.Name, p.CurrentStock })
            .ToDictionaryAsync(p => p.Id);
        var result = new Dictionary<Guid, CmsLineStockTarget>();
        foreach (var l in lines)
        {
            var pid = wanted[l.Id];
            if (!products.TryGetValue(pid, out var p))
                throw new BusinessRuleException(
                    $"สินค้า ERP ที่ผูกกับตัวเลือก \"{l.ProductName}\" ไม่พบหรือถูกลบ — แก้การผูกสินค้าของตัวเลือกนี้ที่หน้าแก้ไขเว็บ แล้วตัดสต็อกอีกครั้ง",
                    CmsVariantPricing.RuleCode);
            var available = warehouseId is Guid wid && _stock != null
                ? await _stock.GetQuantityAsync(companyId, pid, wid)
                : p.CurrentStock;
            result[l.Id] = new CmsLineStockTarget(pid, p.Name, available);
        }
        return result;
    }

    /// <summary>ตัวเลือกย่อยที่หน้าร้านเลือกได้ (ใช้งานอยู่) — ราคาสุดท้ายต่อ variant + มีของไหม (สต็อกจากสินค้า ERP ที่ผูก หรือสินค้าแม่) ·
    /// สินค้าไม่มี variant ⇒ null (หน้าร้านไม่แสดงตัวเลือก — เส้นเดิม)</summary>
    private async Task<List<StorefrontVariant>?> LoadStorefrontVariantsAsync(Guid companyId, Guid siteId, Guid siteProductId,
        Guid parentProductId, decimal basePrice, StockBehavior behavior)
    {
        var variants = await _db.SiteProductVariants.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.SiteProductId == siteProductId && v.IsActive && !v.IsDeleted)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
            .ToListAsync();
        if (variants.Count == 0) return null;
        var warehouseId = await CmsSiteWarehouse.ResolveAsync(_db, _stock, companyId, siteId);
        var ids = variants.Select(v => CmsVariantPricing.StockProductId(parentProductId, v.ErpProductId)).Distinct().ToList();
        var stockById = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && ids.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, p => p.CurrentStock);
        var result = new List<StorefrontVariant>();
        foreach (var v in variants)
        {
            var pid = CmsVariantPricing.StockProductId(parentProductId, v.ErpProductId);
            decimal available = 0m;
            if (stockById.TryGetValue(pid, out var cur))
                available = warehouseId is Guid wid && _stock != null ? await _stock.GetQuantityAsync(companyId, pid, wid) : cur;
            result.Add(new StorefrontVariant
            {
                Id = v.Id, Name = v.Name, Sku = v.Sku, OptionsJson = v.OptionsJson, ImageUrl = v.ImageUrl,
                Price = CmsVariantPricing.UnitPrice(basePrice, v.Price),
                CompareAtPrice = v.CompareAtPrice,
                InStock = stockById.ContainsKey(pid) && CmsVariantPricing.InStock(available, behavior == StockBehavior.InStockOnly),
            });
        }
        return result;
    }
}
