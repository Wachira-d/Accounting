using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Product;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 207 (คำขอเจ้าของข้อ 1) — ผูกบริการจองหน้าเว็บกับ "สินค้า/บริการ" ในระบบบัญชี ⇒ เอกสารของการจองได้รหัสสินค้า + บัญชีรายได้ + อัตรา VAT ของสินค้า
/// (<see cref="BookingProductLink"/>) · POS ดึงคิวแล้วเปิดบิลด้วยสินค้านี้ (PosService.Bookings ใช้ <c>ServiceProductId</c> อยู่แล้ว) ·
/// สร้างสินค้าใหม่ผ่าน <c>IProductService.CreateAsync</c> ตัวเดียวกับหน้าสินค้า (ไม่เขียนแถว Product เอง)
/// </summary>
public partial class CmsBookingService
{
    /// <summary>สินค้าที่อ้างต้องเป็นของบริษัทนี้และยังไม่ถูกลบ (กันผูกข้ามบริษัทด้วย id ที่เดามา)</summary>
    private async Task EnsureProductOfCompanyAsync(Guid companyId, Guid? productId)
    {
        if (productId is not Guid pid) return;
        var ok = await _db.Products.AsNoTracking().AnyAsync(p => p.Id == pid && p.CompanyId == companyId && !p.IsDeleted);
        if (!ok) throw new BusinessRuleException("สินค้า/บริการที่เลือกไม่อยู่ในบริษัทนี้หรือถูกลบแล้ว", BookingProductLink.RuleCode);
    }

    public async Task<BookingServiceResponse> LinkProductAsync(Guid companyId, Guid siteId, Guid serviceId, LinkBookingProductRequest request, string userId)
    {
        var svc = await _db.SiteBookingServices
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted)
            ?? throw new KeyNotFoundException("Booking service not found.");
        Guid? productId;
        if (request.CreateNew)
        {
            if (_productService == null)
                throw new BusinessRuleException("สร้างสินค้า/บริการจากหน้านี้ไม่ได้ — สร้างที่หน้าสินค้าแล้วเลือกผูก", BookingProductLink.RuleCode);
            var codes = await _db.Products.AsNoTracking().Where(p => p.CompanyId == companyId).Select(p => p.Code).ToListAsync();
            var (vatRegistered, defaultRate) = await CompanyVatStatus.ProfileAsync(_db, companyId);
            var created = await _productService.CreateAsync(companyId, new CreateProductRequest(
                Code: BookingProductLink.NextCode(codes), Name: svc.Name, NameEn: svc.NameEn, Description: svc.Description,
                ProductType: ProductType.Service, SKU: null, Barcode: null, Category: svc.Category, Unit: "ครั้ง",
                SellingPrice: svc.Price, CostPrice: 0m,
                VatRate: OutputVatRate.ForCompany(vatRegistered, defaultRate) > 0m ? defaultRate : 0m,
                IsVatIncluded: true));   // ราคาบนเว็บเป็นราคารวม VAT (PricesIncludeVat ของเอกสารการจอง)
            productId = created.Id;
        }
        else
        {
            await EnsureProductOfCompanyAsync(companyId, request.ProductId);
            productId = request.ProductId;
        }
        svc.ProductId = productId;
        svc.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return await GetServiceAsync(companyId, siteId, svc.Id) ?? throw new InvalidOperationException("Failed.");
    }
}
