using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B/§D.2) — แอดมินแพลตฟอร์มตั้ง<b>ทุกอย่าง</b>ของโควตา:
/// เพดานต่อแพ็กเกจ (ทรัพยากรที่ไม่มีช่องเดิม — รวมจำนวนเว็บ CMS) · แคตตาล็อกโควตาเสริม (ทรัพยากรเลือกจากรายการในโค้ดเท่านั้น · ขนาดหน่วย ·
/// ราคาเดือน/ปี · วิธีซื้อที่เปิด · ซื้อได้สูงสุด · เปิด/ปิด) · override รายบริษัท (ต้องมีเหตุผล + audit chain) · คิวตรวจสลิป
/// </summary>
[ApiController]
[Route("api/admin/quota")]
[Authorize(Roles = "SystemAdmin")]
public class AdminQuotaController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminQuotaController(AccountingDbContext db) { _db = db; }

    private string Actor => User.Identity?.Name ?? "system-admin";

    /// <summary>รายการทรัพยากร (closed list) — หน้าแอดมินเลือกจากที่นี่ ไม่พิมพ์รหัสเอง (A-1)</summary>
    [HttpGet("resources")]
    public ActionResult<ApiResponse<object>> Resources()
        => Ok(new ApiResponse<object>(true, QuotaResources.All.Select(r => new
        {
            key = r.Key, label = r.Label, unit = r.Unit, kind = r.Kind.ToString(), perSite = r.PerSite, sellable = r.Sellable,
            legacy = r.Legacy.ToString(),
            // ทรัพยากรที่มีช่องเดิมบนแพ็กเกจ ⇒ แก้ที่ช่องเดิม (ไม่ทำสำเนาที่สอง) · ตารางนี้ตั้งได้เฉพาะที่ไม่มีช่องเดิม
            planEditableHere = r.Legacy is QuotaResources.LegacySource.None or QuotaResources.LegacySource.LodgingFirstFree,
            legacyNote = r.Legacy switch
            {
                QuotaResources.LegacySource.PlanMaxUsers => "ตั้งที่ช่อง \"ผู้ใช้สูงสุด\" ของแพ็กเกจ (หน้าจัดการแพ็กเกจ)",
                QuotaResources.LegacySource.PlanMaxStorage => "ตั้งที่ช่อง \"พื้นที่จัดเก็บ\" ของแพ็กเกจ (หน้าจัดการแพ็กเกจ)",
                QuotaResources.LegacySource.PlanOcrPages => "ตั้งที่ช่องโควตา OCR ของแพ็กเกจ (หน้าจัดการแพ็กเกจ)",
                QuotaResources.LegacySource.LodgingFirstFree => "ไม่ตั้ง = 1 แห่ง (แห่งแรกฟรี — พฤติกรรมเดิม) · ส่วนเสริม \"ที่พักหลายแห่ง\" ยังเปิดได้ไม่จำกัด",
                _ => "ไม่ตั้ง = ไม่จำกัด (ผู้เช่าเดิมไม่ถูกบล็อกหลังเปิดใช้)",
            },
        })));

    // ───────── เพดานต่อแพ็กเกจ ─────────

    [HttpGet("allowances")]
    public async Task<ActionResult<ApiResponse<object>>> Allowances(CancellationToken ct)
    {
        var plans = await _db.PlanTemplates.AsNoTracking().OrderBy(p => p.Plan).ThenBy(p => p.MonthlyPrice)
            .Select(p => new { p.Id, p.Name, plan = p.Plan.ToString(), p.IsActive }).ToListAsync(ct);
        var rows = await _db.PlanQuotaAllowances.AsNoTracking().ToListAsync(ct);
        return Ok(new ApiResponse<object>(true, plans.Select(p => new
        {
            p.Id, p.Name, p.plan, p.IsActive,
            allowances = rows.Where(r => r.PlanTemplateId == p.Id)
                .Select(r => new { resource = r.ResourceKey, included = r.Included, trialIncluded = r.TrialIncluded }).ToList(),
        })));
    }

    public sealed record AllowanceItem(string Resource, int? Included, int? TrialIncluded);

    /// <summary>ตั้งเพดานของแพ็กเกจ — <c>Included</c> null = ลบการตั้ง (กลับเป็นค่าเดิม/ไม่จำกัด) · -1 = ไม่จำกัด</summary>
    [HttpPut("allowances/{planTemplateId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> SetAllowances(Guid planTemplateId, [FromBody] List<AllowanceItem> items, CancellationToken ct)
    {
        if (!await _db.PlanTemplates.AnyAsync(p => p.Id == planTemplateId, ct))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบแพ็กเกจ"));
        foreach (var it in items ?? new())
        {
            var res = QuotaResources.Find(it.Resource);
            if (res == null) return BadRequest(new ApiResponse<object>(false, null, $"ไม่รู้จักทรัพยากร {it.Resource}"));
            if (res.Legacy is not (QuotaResources.LegacySource.None or QuotaResources.LegacySource.LodgingFirstFree))
                return BadRequest(new ApiResponse<object>(false, null, $"\"{res.Label}\" ตั้งที่ช่องเดิมของแพ็กเกจ (หน้าจัดการแพ็กเกจ) — ไม่ทำสำเนาที่สอง"));
            if (it.Included is int i && i < -1 || it.TrialIncluded is int t && t < -1)
                return BadRequest(new ApiResponse<object>(false, null, "ค่าต้องเป็น -1 (ไม่จำกัด) หรือ 0 ขึ้นไป"));
            // รอบ 232 (ฝ่ายค้าน S2): ค่าทดลองใช้โดยไม่มีค่าปกติ = แถวถูกลบทั้งแถว (TrialIncluded หายเงียบ) — ปฏิเสธให้ชัด
            if (it.Included == null && it.TrialIncluded != null)
                return BadRequest(new ApiResponse<object>(false, null, $"\"{res.Label}\": ตั้งค่าระหว่างทดลองใช้ได้เมื่อตั้งค่าปกติด้วย (ค่าปกติว่าง = ไม่ตั้ง/ลบแถว) — กรอกค่าปกติ หรือเว้นว่างทั้งสองช่อง"));
        }
        var existing = await _db.PlanQuotaAllowances.Where(a => a.PlanTemplateId == planTemplateId).ToListAsync(ct);
        var before = existing.Select(a => new { a.ResourceKey, a.Included, a.TrialIncluded }).ToList();
        foreach (var it in items ?? new())
        {
            var row = existing.FirstOrDefault(a => a.ResourceKey == it.Resource);
            if (it.Included == null)
            {
                if (row != null) { row.IsDeleted = true; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = Actor; }
                continue;
            }
            if (row == null)
            {
                row = new PlanQuotaAllowance { PlanTemplateId = planTemplateId, ResourceKey = it.Resource, CreatedBy = Actor };
                _db.PlanQuotaAllowances.Add(row);
            }
            row.Included = it.Included.Value;
            row.TrialIncluded = it.TrialIncluded;
            row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = Actor;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = nameof(PlanQuotaAllowance), EntityId = planTemplateId.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before), NewValues = System.Text.Json.JsonSerializer.Serialize(items),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "บันทึกเพดานของแพ็กเกจแล้ว — มีผลทันที (ของที่ผู้เช่ามีอยู่เกินเพดานไม่ถูกลบ · บล็อกเฉพาะการเพิ่มใหม่)"));
    }

    // ───────── แคตตาล็อกโควตาเสริม ─────────

    [HttpGet("offers")]
    public async Task<ActionResult<ApiResponse<object>>> Offers(CancellationToken ct)
    {
        var offers = await _db.QuotaAddOnOffers.AsNoTracking().OrderBy(o => o.SortOrder).ThenBy(o => o.ResourceKey).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var sold = await _db.CompanyQuotaAddOns.AsNoTracking().Where(p => p.Status == QuotaAddOnStatus.Active)
            .GroupBy(p => p.ResourceKey).Select(g => new { g.Key, Units = g.Sum(x => x.Units), Companies = g.Select(x => x.CompanyId).Distinct().Count() })
            .ToListAsync(ct);
        return Ok(new ApiResponse<object>(true, offers.Select(o => new
        {
            o.Id, resource = o.ResourceKey, label = QuotaResources.Find(o.ResourceKey)?.Label ?? o.ResourceKey, o.Name, o.UnitSize,
            o.MonthlyUnitPrice, o.AnnualUnitPrice, modes = Modes(o.AllowedModes), o.MaxUnitsPerCompany, o.MinChargeOneMonth, o.ValidDays,
            o.IsActive, o.SortOrder,
            // A-3: เปิดขายแต่ไม่มีราคา = ซื้อไม่ได้ (ไม่ใช่ขายฟรีเงียบ ๆ) — ป้ายเตือนจากเซิร์ฟเวอร์
            warning = o.IsActive && o.MonthlyUnitPrice <= 0m ? "เปิดขายแต่ยังไม่ตั้งราคา — ผู้เช่ายังซื้อไม่ได้" : null,
            activeUnits = sold.FirstOrDefault(s => s.Key == o.ResourceKey)?.Units ?? 0,
            activeCompanies = sold.FirstOrDefault(s => s.Key == o.ResourceKey)?.Companies ?? 0,
        })));
    }

    private static List<string> Modes(QuotaAddOnMode m)
        => new[] { QuotaAddOnMode.OneOffMonth, QuotaAddOnMode.RecurringMonthly, QuotaAddOnMode.Coterm }.Where(x => (m & x) != 0).Select(x => x.ToString()).ToList();

    public sealed record OfferBody(string Resource, string? Name, int UnitSize, decimal MonthlyUnitPrice, decimal? AnnualUnitPrice,
        List<string>? Modes, int? MaxUnitsPerCompany, bool MinChargeOneMonth = true, int? ValidDays = null, bool IsActive = true, int SortOrder = 0);

    /// <summary>ตั้ง/แก้ข้อเสนอ (1 ต่อทรัพยากร) — ราคาใหม่มีผลกับการซื้อครั้งถัดไปเท่านั้น (การซื้อเดิม snapshot ราคาไว้)</summary>
    [HttpPost("offers")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertOffer([FromBody] OfferBody b, CancellationToken ct)
    {
        var modes = QuotaAddOnMode.None;
        foreach (var m in b.Modes ?? new())
        {
            if (!Enum.TryParse<QuotaAddOnMode>(m, true, out var mm) || mm == QuotaAddOnMode.None)
                return BadRequest(new ApiResponse<object>(false, null, $"วิธีซื้อไม่ถูกต้อง: {m}"));
            modes |= mm;
        }
        if (QuotaAddOnPricing.ValidateOffer(b.Resource, b.UnitSize, b.MonthlyUnitPrice, b.AnnualUnitPrice, modes, b.MaxUnitsPerCompany, b.ValidDays) is string err)
            return BadRequest(new ApiResponse<object>(false, null, err));
        var row = await _db.QuotaAddOnOffers.FirstOrDefaultAsync(o => o.ResourceKey == b.Resource, ct);
        var before = row == null ? null : System.Text.Json.JsonSerializer.Serialize(new { row.UnitSize, row.MonthlyUnitPrice, row.AnnualUnitPrice, row.AllowedModes, row.MaxUnitsPerCompany, row.MinChargeOneMonth, row.ValidDays, row.IsActive });
        if (row == null) { row = new QuotaAddOnOffer { ResourceKey = b.Resource, CreatedBy = Actor }; _db.QuotaAddOnOffers.Add(row); }
        row.Name = string.IsNullOrWhiteSpace(b.Name) ? null : b.Name.Trim();
        row.UnitSize = b.UnitSize;
        row.MonthlyUnitPrice = QuotaAddOnPricing.R2(b.MonthlyUnitPrice);
        row.AnnualUnitPrice = b.AnnualUnitPrice is decimal a ? QuotaAddOnPricing.R2(a) : null;
        row.AllowedModes = modes;
        row.MaxUnitsPerCompany = b.MaxUnitsPerCompany;
        row.MinChargeOneMonth = b.MinChargeOneMonth;
        row.ValidDays = b.ValidDays;
        row.IsActive = b.IsActive;
        row.SortOrder = b.SortOrder;
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = Actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = nameof(QuotaAddOnOffer), EntityId = row.Id.ToString(),
            OldValues = before, NewValues = System.Text.Json.JsonSerializer.Serialize(b), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { row.Id }, "บันทึกข้อเสนอแล้ว — ราคาใหม่มีผลกับการซื้อครั้งถัดไป (ของที่ซื้อแล้วคงราคาเดิม)"));
    }

    // ───────── override รายบริษัท ─────────

    [HttpGet("companies")]
    public async Task<ActionResult<ApiResponse<object>>> FindCompanies([FromQuery] string? q, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        var query = _db.Companies.AsNoTracking().Where(c => !c.IsDeleted);
        if (Guid.TryParse(term, out var id)) query = query.Where(c => c.Id == id);
        else if (term.Length >= 2) query = query.Where(c => EF.Functions.ILike(c.Name, "%" + term + "%") || (c.TaxId != null && c.TaxId.Contains(term)));
        else return Ok(new ApiResponse<object>(true, Array.Empty<object>()));
        return Ok(new ApiResponse<object>(true, await query.OrderBy(c => c.Name).Take(20).Select(c => new { c.Id, c.Name, c.TaxId }).ToListAsync(ct)));
    }

    /// <summary>สถานะโควตา + override + การซื้อของบริษัทหนึ่ง (มุมมองแอดมิน)</summary>
    [HttpGet("companies/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> CompanyDetail(Guid companyId, [FromServices] IQuotaGate gate, CancellationToken ct)
    {
        var name = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        if (name == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));
        var overrides = await _db.CompanyQuotaOverrides.AsNoTracking().Where(o => o.CompanyId == companyId)
            .Select(o => new { resource = o.ResourceKey, o.Limit, o.Reason, o.SetBy, o.SetAt }).ToListAsync(ct);
        var purchases = await _db.CompanyQuotaAddOns.AsNoTracking().Where(p => p.CompanyId == companyId).OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            companyId, name, summary = await gate.SummaryAsync(companyId, ct), overrides, purchases = purchases.Select(QuotaController.Map).ToList(),
        }));
    }

    public sealed record OverrideBody(string Resource, int? Limit, string Reason);

    /// <summary>ตั้ง/ลบ override รายบริษัท — <c>Limit</c> null = ลบ (กลับไปตามแพ็กเกจ) · -1 = ไม่จำกัด · ต้องมีเหตุผลเสมอ (audit chain)</summary>
    [HttpPut("companies/{companyId:guid}/overrides")]
    public async Task<ActionResult<ApiResponse<object>>> SetOverride(Guid companyId, [FromBody] OverrideBody b, CancellationToken ct)
    {
        if (!await _db.Companies.AnyAsync(c => c.Id == companyId, ct)) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));
        // รอบ 231 (231-11): ผู้เขียนเดียว QuotaOverrideWriter (ตรวจทรัพยากร/เหตุผล/ค่า + audit chain) — AdminChangeLimits เดินตัวเดียวกัน
        try
        {
            await QuotaOverrideWriter.SetAsync(_db, companyId, b.Resource, b.Limit, b.Reason ?? "", Actor,
                HttpContext?.Connection.RemoteIpAddress?.ToString(), ct);
        }
        catch (BusinessRuleException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message));
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, b.Limit == null ? "ลบ override แล้ว — กลับไปใช้เพดานของแพ็กเกจ" : "บันทึก override แล้ว — มีผลทันที (ของที่มีอยู่เกินไม่ถูกลบ)"));
    }

    // ───────── คิวตรวจสลิปค่าโควตาเสริม ─────────

    [HttpGet("purchases")]
    public async Task<ActionResult<ApiResponse<object>>> Purchases([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.CompanyQuotaAddOns.AsNoTracking().AsQueryable();
        q = status switch
        {
            "awaiting" => q.Where(p => p.Status == QuotaAddOnStatus.AwaitingPayment),
            "active" => q.Where(p => p.Status == QuotaAddOnStatus.Active),
            "attention" => q.Where(p => p.AttentionRule != null),
            "all" => q,
            _ => q.Where(p => p.Status == QuotaAddOnStatus.PendingReview),
        };
        var rows = await q.OrderByDescending(p => p.SlipUploadedAt ?? p.CreatedAt).Take(300)
            .Join(_db.Companies.AsNoTracking(), p => p.CompanyId, c => c.Id, (p, c) => new { p, companyName = c.Name }).ToListAsync(ct);
        // S8: สถานะเงินของ intent ข้างรายการ (คืนเงินแล้ว ⇒ ปุ่มเพิกถอนสิทธิ์อยู่ข้างกัน)
        var intentIds = rows.Where(r => r.p.PaymentIntentId != null).Select(r => r.p.PaymentIntentId!.Value).ToList();
        var intents = await _db.PaymentIntents.AsNoTracking().Where(i => intentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Status }).ToDictionaryAsync(i => i.Id, i => i.Status, ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            companyId = r.p.CompanyId, r.companyName, purchase = QuotaController.Map(r.p), hasSlip = r.p.SlipPath != null,
            intentStatus = r.p.PaymentIntentId is Guid iid && intents.TryGetValue(iid, out var st) ? st.ToString() : null,
            refunded = r.p.PaymentIntentId is Guid iid2 && intents.TryGetValue(iid2, out var st2)
                && (st2 == PaymentIntentStatus.Refunded || st2 == PaymentIntentStatus.PartiallyRefunded),
        })));
    }

    [HttpGet("purchases/{companyId:guid}/{purchaseId:guid}/slip")]
    public async Task<IActionResult> Slip(Guid companyId, Guid purchaseId, [FromServices] IWebHostEnvironment env,
        [FromServices] Accounting.Services.Storage.IFileStorage storage, CancellationToken ct)
    {
        var path = await _db.CompanyQuotaAddOns.AsNoTracking().Where(p => p.Id == purchaseId && p.CompanyId == companyId)
            .Select(p => p.SlipPath).FirstOrDefaultAsync(ct);
        if (SubscriptionSlipUpload.OpenStored(env, path) is not { } file) return NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return await SubscriptionSlipUpload.ServeAsync(storage, file, System.IO.Path.GetFileName(file.Path))
            ?? NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
    }

    /// <summary>ฝ่ายค้าน S2/S7: <c>OfflineReference</c> บังคับเมื่ออนุมัติรายการที่ไม่มีสลิป · <c>ReceivedAmount</c>/<c>TransferDate</c> = ยอด/วันที่บนใบเสร็จ</summary>
    /// <remarks>รอบ 235 (ฝ่ายค้าน S-3): <c>WithholdingTaxAmount</c> = 0 ⇒ ล้างภาษีหักของรายการ รับเต็มยอด (ผู้จ่ายหมดสิทธิ์หัก) · null = ไม่เปลี่ยน</remarks>
    public sealed record ReviewBody(bool Approve, string? Reason, string? OfflineReference = null, decimal? ReceivedAmount = null, DateTime? TransferDate = null,
        decimal? WithholdingTaxAmount = null);

    /// <summary>อนุมัติ ⇒ เปิดสิทธิ์ทันที + ใบกำกับ/ใบเสร็จ · ปฏิเสธ ⇒ ต้องมีเหตุผล (ผู้เช่าเห็น · ยังไม่ได้ให้สิทธิ์ จึงไม่มีอะไรต้องถอน)</summary>
    [HttpPost("purchases/{companyId:guid}/{purchaseId:guid}/review")]
    public async Task<ActionResult<ApiResponse<object>>> Review(Guid companyId, Guid purchaseId, [FromBody] ReviewBody b,
        [FromServices] IQuotaAddOnPurchaseService svc, CancellationToken ct)
    {
        var row = b.Approve
            ? await svc.ApproveAsync(companyId, purchaseId, Actor, ct, b.OfflineReference, b.Reason, b.ReceivedAmount,
                b.TransferDate is DateTime td ? DateTime.SpecifyKind(td.Date.AddHours(12 - 7), DateTimeKind.Utc) : null, b.WithholdingTaxAmount)
            : await svc.RejectAsync(companyId, purchaseId, b.Reason ?? "", Actor, ct);
        return Ok(new ApiResponse<object>(true, QuotaController.Map(row),
            b.Approve ? (row.ReceiptNumber != null ? $"อนุมัติแล้ว — ใบเสร็จ/ใบกำกับ {row.ReceiptNumber}" : "อนุมัติแล้ว — ⚠️ ออกใบเสร็จไม่สำเร็จ (ดู audit · ออกด้วยมือในบริษัทผู้ให้บริการ)")
                : "ปฏิเสธแล้ว — ผู้เช่าเห็นเหตุผลและแนบสลิปใหม่ได้"));
    }

    public sealed record RevokeBody(string Reason);

    /// <summary>ฝ่ายค้าน S8: เพิกถอนสิทธิ์ของการซื้อที่ใช้งานอยู่ (เช่น คืนเงินแล้ว) — มีผลทันที · ไม่ลบข้อมูลของผู้เช่า (เกินเพดาน = บล็อกการเพิ่มเท่านั้น) · audit</summary>
    [HttpPost("purchases/{companyId:guid}/{purchaseId:guid}/revoke")]
    public async Task<ActionResult<ApiResponse<object>>> Revoke(Guid companyId, Guid purchaseId, [FromBody] RevokeBody b,
        [FromServices] IQuotaAddOnPurchaseService svc, CancellationToken ct)
    {
        var row = await svc.RevokeAsync(companyId, purchaseId, b.Reason ?? "", Actor, ct);
        return Ok(new ApiResponse<object>(true, QuotaController.Map(row), "เพิกถอนสิทธิ์แล้ว — มีผลทันที (ของที่ผู้เช่าสร้างไว้ไม่ถูกลบ)"));
    }
}
