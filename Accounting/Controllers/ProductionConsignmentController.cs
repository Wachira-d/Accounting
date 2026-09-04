using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Consignment;
using Accounting.Services.Implementations.Production;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Two related manufacturing-ish features grouped together:
///   • Production orders with BOM backflush.
///   • Consignment stock movement (receive / dispatch / consume).
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/mfg")]
[Authorize]
public class ProductionConsignmentController : ControllerBase
{
    // ── Bill of Materials (BOM) ─────────────────────────────────────
    // The production-order form needs a BOM to reference; without a way to
    // list/create BOMs the operator had to paste a raw GUID (which they
    // never have) so 'create production order' silently failed validation.

    public sealed record BomListItem(Guid Id, string ParentProductName, string ParentProductCode,
        string Version, bool IsActive, int ComponentCount);

    [HttpGet("boms")]
    public async Task<ActionResult<ApiResponse<List<BomListItem>>>> ListBoms(
        Guid companyId, [FromServices] AccountingDbContext db, CancellationToken ct)
    {
        var boms = await db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .OrderByDescending(b => b.IsActive).ThenByDescending(b => b.EffectiveFrom)
            .Select(b => new BomListItem(
                b.Id,
                b.ParentProduct.Name,
                b.ParentProduct.Code,
                b.Version,
                b.IsActive,
                b.Lines.Count))
            .ToListAsync(ct);
        return Ok(new ApiResponse<List<BomListItem>>(true, boms));
    }

    [HttpGet("boms/{bomId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> GetBom(
        Guid companyId, Guid bomId, [FromServices] AccountingDbContext db, CancellationToken ct)
    {
        var bom = await db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.Id == bomId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new
            {
                b.Id, b.Version, b.IsActive, b.Notes,
                parentProductId = b.ParentProductId,
                parentProductName = b.ParentProduct.Name,
                lines = b.Lines.Select(l => new
                {
                    l.Id, l.ComponentProductId,
                    componentName = l.ComponentProduct.Name,
                    componentCode = l.ComponentProduct.Code,
                    l.QuantityPerParent, l.Notes,
                }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (bom == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ BOM"));
        return Ok(new ApiResponse<object>(true, bom));
    }

    public sealed record CreateBomLineRequest(Guid ComponentProductId, decimal QuantityPerParent, string? Notes);
    public sealed record CreateBomRequest(Guid ParentProductId, string? Version, string? Notes,
        List<CreateBomLineRequest> Lines);

    [HttpPost("boms")]
    public async Task<ActionResult<ApiResponse<object>>> CreateBom(
        Guid companyId, [FromBody] CreateBomRequest req,
        [FromServices] AccountingDbContext db, CancellationToken ct)
    {
        if (req.ParentProductId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องเลือกสินค้าที่ผลิต (parent product)"));
        if (req.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องมีวัตถุดิบ (component) อย่างน้อย 1 รายการ"));

        // Validate every referenced product belongs to this company.
        var productIds = req.Lines.Select(l => l.ComponentProductId).Append(req.ParentProductId).Distinct().ToList();
        var validCount = await db.Products.CountAsync(p => p.CompanyId == companyId && productIds.Contains(p.Id), ct);
        if (validCount != productIds.Count)
            return BadRequest(new ApiResponse<object>(false, null, "มีสินค้าบางรายการไม่อยู่ในบริษัทนี้"));
        if (req.Lines.Any(l => l.QuantityPerParent <= 0))
            return BadRequest(new ApiResponse<object>(false, null, "จำนวนต่อหน่วยต้องมากกว่า 0"));
        if (req.Lines.Any(l => l.ComponentProductId == req.ParentProductId))
            return BadRequest(new ApiResponse<object>(false, null, "วัตถุดิบต้องไม่ใช่สินค้าที่ผลิตเอง"));

        var bom = new BillOfMaterials
        {
            CompanyId = companyId,
            ParentProductId = req.ParentProductId,
            Version = string.IsNullOrWhiteSpace(req.Version) ? "v1" : req.Version!.Trim(),
            Notes = req.Notes,
            IsActive = true,
            EffectiveFrom = DateTime.UtcNow,
            Lines = req.Lines.Select(l => new BomLine
            {
                ComponentProductId = l.ComponentProductId,
                QuantityPerParent = l.QuantityPerParent,
                Notes = l.Notes,
            }).ToList(),
        };
        db.BillsOfMaterials.Add(bom);
        await db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { bom.Id, bom.Version }, "สร้าง BOM สำเร็จ"));
    }

    // ── สูตรต่อสินค้า (recipe) — มุมมองที่ร้านอาหาร/คาเฟ่เข้าใจ ─────────
    // BOM ข้างบนเป็นมุมมองของ "ใบสั่งผลิต" (เลือก BOM แล้วผลิตกี่หน่วย) ซึ่งร้านชานม
    // ไม่ได้คิดแบบนั้น — เขาคิดว่า "ชานมไข่มุก 1 แก้ว ใช้อะไรบ้าง" · endpoint คู่นี้จึงเป็น
    // **มุมมอง** บนตารางเดียวกัน ไม่ใช่ตารางใหม่ (ห้ามสร้างความจริงชุดที่สอง)

    public sealed record RecipeLineDto(Guid ComponentProductId, string ComponentCode,
        string ComponentName, string Unit, decimal QuantityPerParent);
    public sealed record RecipeDto(Guid ProductId, bool ConsumesBomOnSale, Guid? BomId,
        List<RecipeLineDto> Lines);

    [HttpGet("products/{productId:guid}/recipe")]
    public async Task<ActionResult<ApiResponse<RecipeDto>>> GetRecipe(
        Guid companyId, Guid productId, [FromServices] AccountingDbContext db, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == productId && p.CompanyId == companyId && !p.IsDeleted)
            .Select(p => new { p.Id, p.ConsumesBomOnSale })
            .FirstOrDefaultAsync(ct);
        if (product == null) return NotFound(new ApiResponse<RecipeDto>(false, null, "ไม่พบสินค้า"));

        var now = DateTime.UtcNow;
        var bom = await db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ParentProductId == productId
                     && b.IsActive && !b.IsDeleted
                     && b.EffectiveFrom <= now && (b.EffectiveTo == null || b.EffectiveTo >= now))
            .OrderByDescending(b => b.EffectiveFrom)
            .Select(b => new
            {
                b.Id,
                lines = b.Lines.Where(l => !l.IsDeleted).Select(l => new RecipeLineDto(
                    l.ComponentProductId, l.ComponentProduct.Code, l.ComponentProduct.Name,
                    l.ComponentProduct.Unit, l.QuantityPerParent)).ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return Ok(new ApiResponse<RecipeDto>(true, new RecipeDto(
            productId, product.ConsumesBomOnSale, bom?.Id, bom?.lines ?? new List<RecipeLineDto>())));
    }

    public sealed record SaveRecipeLine(Guid ComponentProductId, decimal QuantityPerParent);
    public sealed record SaveRecipeRequest(bool ConsumesBomOnSale, List<SaveRecipeLine> Lines);

    [HttpPut("products/{productId:guid}/recipe")]
    public async Task<ActionResult<ApiResponse<object>>> SaveRecipe(
        Guid companyId, Guid productId, [FromBody] SaveRecipeRequest req,
        [FromServices] AccountingDbContext db, CancellationToken ct)
    {
        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId && !p.IsDeleted, ct);
        if (product == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบสินค้า"));

        var lines = (req.Lines ?? new List<SaveRecipeLine>())
            .Where(l => l.ComponentProductId != Guid.Empty && l.QuantityPerParent > 0).ToList();

        // เปิดธง "ขายแล้วกินสูตร" โดยไม่มีสูตร = ตั้งค่าไม่ครบ ⇒ ขายแล้วไม่ตัดอะไรเลย
        // และตัดสต็อกตัวเองก็ไม่ได้ ⇒ บล็อกพร้อมบอกทางแก้ (ห้าม silent no-op)
        if (req.ConsumesBomOnSale && lines.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null,
                "เปิด \"ขายแล้วตัดวัตถุดิบตามสูตร\" ต้องมีวัตถุดิบอย่างน้อย 1 รายการ"));
        if (lines.Any(l => l.ComponentProductId == productId))
            return BadRequest(new ApiResponse<object>(false, null, "วัตถุดิบต้องไม่ใช่ตัวสินค้าเอง"));

        var ids = lines.Select(l => l.ComponentProductId).Distinct().ToList();
        if (ids.Count > 0)
        {
            var ok = await db.Products.CountAsync(
                p => p.CompanyId == companyId && ids.Contains(p.Id) && !p.IsDeleted, ct);
            if (ok != ids.Count)
                return BadRequest(new ApiResponse<object>(false, null, "มีวัตถุดิบบางรายการไม่อยู่ในบริษัทนี้"));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // สูตรเดิม: ปิด (EffectiveTo = now) แทนการลบ — ใบที่ขายไปแล้วต้องยังอธิบายได้ว่า
        // ตอนนั้นใช้สูตรอะไร (เอกสารบัญชีต้องตรวจสอบย้อนหลังได้ พ.ร.บ.การบัญชี ม.10)
        var now = DateTime.UtcNow;
        var current = await db.BillsOfMaterials
            .Where(b => b.CompanyId == companyId && b.ParentProductId == productId
                     && b.IsActive && !b.IsDeleted)
            .ToListAsync(ct);
        var nextVersion = current.Count + 1;
        foreach (var old in current) { old.IsActive = false; old.EffectiveTo = now; }

        if (lines.Count > 0)
        {
            db.BillsOfMaterials.Add(new BillOfMaterials
            {
                CompanyId = companyId,
                ParentProductId = productId,
                Version = $"v{nextVersion}",
                IsActive = true,
                EffectiveFrom = now,
                Notes = "สูตรจากหน้าสินค้า",
                Lines = lines.Select(l => new BomLine
                {
                    ComponentProductId = l.ComponentProductId,
                    QuantityPerParent = l.QuantityPerParent,
                }).ToList(),
            });
        }

        product.ConsumesBomOnSale = req.ConsumesBomOnSale && lines.Count > 0;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new ApiResponse<object>(true, new { lineCount = lines.Count }, "บันทึกสูตรแล้ว"));
    }

    public sealed record CreateOrderRequest(string OrderNumber, Guid BomId,
        decimal PlannedQty, DateTime PlannedStartAt, string? Notes);

    [HttpPost("production-orders")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> CreateOrder(
        Guid companyId, [FromBody] CreateOrderRequest req,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.CreateAsync(companyId, req.OrderNumber, req.BomId,
                req.PlannedQty, req.PlannedStartAt, req.Notes, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o,
                $"สร้าง production order {o.OrderNumber} แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("production-orders/{orderId:guid}/shortages")]
    public async Task<ActionResult<ApiResponse<ShortageReport>>> CheckShortages(
        Guid companyId, Guid orderId,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.CheckShortagesAsync(companyId, orderId, ct);
            return Ok(new ApiResponse<ShortageReport>(true, r,
                r.Shortages.Count == 0 ? "วัตถุดิบครบ พร้อมเริ่มผลิต"
                    : $"ขาด component {r.Shortages.Count} รายการ"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("production-orders/{orderId:guid}/release")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> Release(
        Guid companyId, Guid orderId,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.ReleaseAsync(companyId, orderId, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o, "ปล่อยงานผลิตแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record CompleteOrderRequest(decimal CompletedQty, DateTime CompletedAt);

    [HttpPost("production-orders/{orderId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> Complete(
        Guid companyId, Guid orderId, [FromBody] CompleteOrderRequest req,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.CompleteAsync(companyId, orderId, req.CompletedQty, req.CompletedAt, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o,
                $"ผลิตเสร็จ {req.CompletedQty} หน่วย ที่ราคา {o.CumulativeComponentCost / Math.Max(1, req.CompletedQty):N4} / หน่วย"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("production-orders")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductionOrder>>>> List(
        Guid companyId, [FromQuery] string? status,
        [FromServices] IProductionOrderService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<ProductionOrder>>(true,
            await svc.ListAsync(companyId, status, ct)));

    // ── Consignment ─────────────────────────────────────────────────
    public sealed record ReceiveInboundRequest(Guid ProductId, Guid VendorContactId,
        decimal Quantity, decimal? AgreedUnitPrice, DateTime ReceivedAt);

    [HttpPost("consignment/inbound")]
    public async Task<ActionResult<ApiResponse<ConsignmentRecord>>> ReceiveInbound(
        Guid companyId, [FromBody] ReceiveInboundRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.ReceiveInboundAsync(companyId, req.ProductId, req.VendorContactId,
                req.Quantity, req.AgreedUnitPrice, req.ReceivedAt, ct);
            return Ok(new ApiResponse<ConsignmentRecord>(true, r,
                "รับสินค้า consignment (Inbound) — ยังไม่ผูก AP จนกว่าจะใช้/ขาย"));
        }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record DispatchOutboundRequest(Guid ProductId, Guid CustomerContactId,
        decimal Quantity, decimal? AgreedUnitPrice, DateTime DispatchedAt);

    [HttpPost("consignment/outbound")]
    public async Task<ActionResult<ApiResponse<ConsignmentRecord>>> DispatchOutbound(
        Guid companyId, [FromBody] DispatchOutboundRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.DispatchOutboundAsync(companyId, req.ProductId, req.CustomerContactId,
                req.Quantity, req.AgreedUnitPrice, req.DispatchedAt, ct);
            return Ok(new ApiResponse<ConsignmentRecord>(true, r,
                "ส่งสินค้าฝากขาย (Outbound) — รับรู้รายได้ตอนลูกค้าใช้/ขาย"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record ConsumeRequest(Guid ConsignmentRecordId,
        decimal ConsumedQuantity, DateTime ConsumedAt);

    [HttpPost("consignment/consume")]
    public async Task<ActionResult<ApiResponse<ConsumptionResult>>> Consume(
        Guid companyId, [FromBody] ConsumeRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.RecordConsumptionAsync(companyId, req.ConsignmentRecordId,
                req.ConsumedQuantity, req.ConsumedAt, ct);
            var direction = r.Direction == "Inbound" ? "AP" : "AR";
            return Ok(new ApiResponse<ConsumptionResult>(true, r,
                r.CreatedDocumentId.HasValue
                    ? $"บันทึก consumption + auto-create {direction} {r.CreatedDocumentNumber} ยอด {r.Amount:N2}"
                    : "บันทึก consumption แล้ว (ไม่มีราคาตกลง — ไม่ post เอกสาร)"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("consignment")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ConsignmentRecord>>>> ListConsignment(
        Guid companyId, [FromQuery] string? direction,
        [FromServices] IConsignmentService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<ConsignmentRecord>>(true,
            await svc.ListAsync(companyId, direction, ct)));
}
