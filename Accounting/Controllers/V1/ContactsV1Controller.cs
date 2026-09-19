using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Matching;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers.V1;

/// <summary>
/// `/api/v1/contacts` — ซิงก์ทะเบียนผู้ติดต่อ (เจ้าหนี้/ลูกหนี้) กับระบบบัญชีของลูกค้า
///
/// **ทำไมสำคัญกว่าที่เห็น**: OCR อ่านชื่อผู้ขายจากใบกำกับได้ก็จริง แต่เอกสารที่
/// เรายิงกลับไปให้ ERP ของลูกค้าต้องอ้าง **รหัสเจ้าหนี้ในระบบเขา** ไม่ใช่ชื่อ
/// ถ้าไม่มีชั้นนี้ ใบสำคัญจ่ายที่สร้างจะอ้างเจ้าหนี้ที่ไม่มีอยู่จริงในระบบปลายทาง
/// → import ไม่ผ่าน หรือแย่กว่านั้นคือสร้างเจ้าหนี้ซ้ำทุกใบ
///
/// โมเดลการซิงก์: **ระบบของลูกค้าเป็นเจ้าของทะเบียน (master)** เราเป็นกระจกสะท้อน
/// — `ExternalId` คือกุญแจ ไม่ใช่ชื่อ (ชื่อเปลี่ยนได้ รหัสไม่เปลี่ยน)
/// </summary>
[ApiController]
[Route("api/v1/contacts")]
[Authorize]
public class ContactsV1Controller : PublicApiControllerBase
{
    // ไม่คิดเงินการซิงก์ทะเบียน — เป็นงานที่ทำให้ฟีเจอร์อื่นใช้ได้ ไม่ใช่คุณค่าที่ขาย
    // การเก็บเงินตรงนี้จะทำให้ลูกค้าซิงก์น้อยลง แล้วคุณภาพการจับคู่แย่ลงตามไปด้วย
    private const string Feature = "ocr.scan";

    private readonly ILogger<ContactsV1Controller> _logger;

    public ContactsV1Controller(AccountingDbContext db, IUsageMeteringService metering,
        ILogger<ContactsV1Controller> logger) : base(db, metering)
    { _logger = logger; }

    public record ContactSyncItem(
        string ExternalId,
        string Name,
        string? TaxId = null,
        string? BranchCode = null,
        string? Address = null,
        string? Phone = null,
        string? Email = null,
        bool IsSupplier = true,
        bool IsCustomer = false,
        bool IsActive = true,
        string? ContactType = null);   // "Individual" | "JuristicPerson"

    public record SyncRequest(string? SystemName, List<ContactSyncItem> Contacts);

    /// <summary>
    /// ส่งทะเบียนผู้ติดต่อจากระบบบัญชีเข้ามา (upsert ด้วย `ExternalId`)
    ///
    /// ยิงซ้ำได้เสมอ — รายการที่มีอยู่แล้วจะถูกอัปเดต ไม่สร้างซ้ำ
    /// แนะนำให้ซิงก์ทั้งทะเบียนตอนเริ่มใช้ แล้วส่งเฉพาะที่เปลี่ยนเป็นรอบ ๆ
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("contacts:write", Feature, ct);
        if (error != null) return error;

        if (req?.Contacts == null || req.Contacts.Count == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาส่งรายชื่อผู้ติดต่ออย่างน้อย 1 รายการ"));
        if (req.Contacts.Count > 1000)
            return BadRequest(new ApiResponse<string>(false, null,
                "ส่งได้ครั้งละไม่เกิน 1,000 รายการ — แบ่งเป็นหลายชุด"));

        var system = string.IsNullOrWhiteSpace(req.SystemName) ? "erp" : req.SystemName.Trim();

        // โหลดของเดิมทีเดียว — เลี่ยง N+1 ตอนซิงก์ทะเบียนเป็นพัน
        var ids = req.Contacts.Select(c => c.ExternalId).Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct().ToList();
        var existing = await Db.Contacts
            .Where(c => c.CompanyId == ctx!.CompanyId && c.ExternalSystem == system
                     && c.ExternalId != null && ids.Contains(c.ExternalId))
            .ToDictionaryAsync(c => c.ExternalId!, ct);

        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var item in req.Contacts)
        {
            if (string.IsNullOrWhiteSpace(item.ExternalId) || string.IsNullOrWhiteSpace(item.Name))
            { skipped++; continue; }

            // เลขผู้เสียภาษีผิดรูปแบบ = ปล่อยผ่านแต่ไม่เก็บค่าผิด ๆ ไว้
            // (§86/4 ต้องการเลขที่ถูกต้อง — เก็บขยะไว้จะทำให้ใบกำกับที่ออกภายหลังผิด)
            var taxId = (item.TaxId ?? "").Trim();
            if (taxId.Length > 0)
            {
                var check = Helpers.ThaiTaxIdValidator.Check(taxId);
                if (!check.IsValid)
                {
                    errors.Add($"{item.ExternalId}: เลขผู้เสียภาษี \"{taxId}\" ไม่ถูกต้อง ({check.Reason}) — บันทึกโดยไม่ใส่เลข");
                    taxId = "";
                }
                else taxId = Helpers.ThaiTaxIdValidator.Normalize(taxId) ?? "";
            }

            if (!existing.TryGetValue(item.ExternalId, out var c))
            {
                c = new Contact
                {
                    CompanyId = ctx!.CompanyId,
                    ExternalId = item.ExternalId.Trim(),
                    ExternalSystem = system,
                    CreatedBy = "api:v1:contact-sync",
                };
                Db.Contacts.Add(c);
                created++;
            }
            else updated++;

            c.Name = item.Name.Trim();
            c.TaxId = taxId.Length > 0 ? taxId : null;
            c.BranchCode = string.IsNullOrWhiteSpace(item.BranchCode) ? c.BranchCode : item.BranchCode.Trim();
            if (item.Address != null) c.Address = item.Address.Trim();
            if (item.Phone != null) c.Phone = item.Phone.Trim();
            if (item.Email != null) c.Email = item.Email.Trim();
            c.IsSupplier = item.IsSupplier;
            c.IsCustomer = item.IsCustomer;
            c.IsActive = item.IsActive;
            if (Enum.TryParse<ContactType>(item.ContactType, true, out var ctype)) c.ContactType = ctype;
            // ประเภทผู้ติดต่อ = ตัวตัดสิน **ภ.ง.ด.3 (บุคคล) vs ภ.ง.ด.53 (นิติบุคคล)**
            // เดิม `taxId.Length == 13 ⇒ JuristicPerson` ผิดทุกราย เพราะเลขผู้เสียภาษี
            // ไทย**ทุกแบบ**ยาว 13 หลัก รวมเลขบัตรประชาชนของบุคคลธรรมดา.
            // ตัวตัดสินตัวเดียวอยู่ที่ Helpers/ContactTypeFromTaxId — ตัดสินไม่ได้
            // (เลขว่าง/ไม่ครบ/checksum ไม่ผ่าน) = **คงค่าเดิม ไม่เดา**
            else c.ContactType = Helpers.ContactTypeFromTaxId.Apply(c.ContactType, taxId);
            c.UpdatedAt = DateTime.UtcNow;
            c.UpdatedBy = "api:v1:contact-sync";
        }

        await Db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true, new
        {
            created, updated, skipped,
            warnings = errors,
        }, $"ซิงก์ผู้ติดต่อสำเร็จ — เพิ่มใหม่ {created} · อัปเดต {updated}"
           + (skipped > 0 ? $" · ข้าม {skipped} (ไม่มีรหัสหรือชื่อ)" : "")));
    }

    /// <summary>
    /// ดึงผู้ติดต่อที่ **เราสร้างขึ้นเอง** จากการอ่านเอกสาร (ยังไม่มีในระบบลูกค้า)
    ///
    /// ใช้ปิดวงจร: OCR เจอผู้ขายรายใหม่ → เราสร้าง contact ชั่วคราว → ลูกค้าดึง
    /// รายการนี้ไปสร้างเจ้าหนี้จริงในระบบเขา → ส่ง `ExternalId` กลับมาผ่าน
    /// `/sync` → ครั้งหน้าจับคู่ได้ทันที
    /// </summary>
    [HttpGet("unmapped")]
    public async Task<IActionResult> GetUnmapped(CancellationToken ct, [FromQuery] int limit = 200)
    {
        var (ctx, error) = await ResolveCallerAsync("contacts:read", Feature, ct);
        if (error != null) return error;

        var rows = await Db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == ctx!.CompanyId && c.ExternalId == null && c.IsActive)
            .OrderByDescending(c => c.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(c => new
            {
                contactId = c.Id, c.Name, c.TaxId, c.BranchCode, c.Address,
                c.Phone, c.Email, c.IsSupplier, c.IsCustomer,
                contactType = c.ContactType.ToString(),
                discoveredAt = c.CreatedAt,
                source = c.CreatedBy,
            })
            .ToListAsync(ct);

        return Ok(new ApiResponse<object>(true, rows,
            rows.Count == 0 ? "ไม่มีผู้ติดต่อที่รอผูกรหัส" : $"พบ {rows.Count} รายที่ยังไม่มีรหัสในระบบของคุณ"));
    }

    public record MapRequest(Guid ContactId, string ExternalId, string? SystemName);

    /// <summary>ผูกผู้ติดต่อที่เราสร้างไว้เข้ากับรหัสในระบบลูกค้า</summary>
    [HttpPost("map")]
    public async Task<IActionResult> Map([FromBody] MapRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("contacts:write", Feature, ct);
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(req?.ExternalId))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุรหัสผู้ติดต่อในระบบของคุณ"));

        var contact = await Db.Contacts
            .FirstOrDefaultAsync(c => c.Id == req.ContactId && c.CompanyId == ctx!.CompanyId, ct);
        if (contact == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบผู้ติดต่อ"));

        var system = string.IsNullOrWhiteSpace(req.SystemName) ? "erp" : req.SystemName.Trim();

        // รหัสเดียวกันผูกได้ contact เดียว — ไม่งั้นเอกสารจะอ้างเจ้าหนี้กำกวม
        var clash = await Db.Contacts.AsNoTracking().AnyAsync(c =>
            c.CompanyId == ctx!.CompanyId && c.ExternalSystem == system
            && c.ExternalId == req.ExternalId && c.Id != contact.Id, ct);
        if (clash)
            return Conflict(new ApiResponse<string>(false, null,
                $"รหัส {req.ExternalId} ถูกผูกกับผู้ติดต่ออื่นแล้ว"));

        contact.ExternalId = req.ExternalId.Trim();
        contact.ExternalSystem = system;
        contact.UpdatedAt = DateTime.UtcNow;
        contact.UpdatedBy = "api:v1:contact-map";
        await Db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true, new { contact.Id, contact.ExternalId },
            "ผูกรหัสเรียบร้อย — เอกสารที่สร้างจากผู้ติดต่อรายนี้จะอ้างรหัสนี้กลับไป"));
    }

    public record ResolveRequest(string? Name, string? TaxId);

    /// <summary>
    /// หาผู้ติดต่อที่ตรงกับชื่อ/เลขภาษีที่อ่านได้จากเอกสาร
    ///
    /// ใช้ตัวเทียบชื่อข้ามภาษา (<see cref="CounterpartyNameMatcher"/>) จึงจับได้แม้
    /// เอกสารพิมพ์ชื่อไทยแต่ทะเบียนเก็บอังกฤษ หรือชื่อถูกตัด/สลับชื่อ-สกุล
    ///
    /// **เลขผู้เสียภาษีชนะชื่อเสมอ** — เป็นกุญแจที่ไม่กำกวม ส่วนชื่อเป็นหลักฐานอ่อน
    /// </summary>
    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromBody] ResolveRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("contacts:read", Feature, ct);
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(req?.Name) && string.IsNullOrWhiteSpace(req?.TaxId))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุชื่อหรือเลขผู้เสียภาษี"));

        // 1) เลขผู้เสียภาษีตรง = จบ ไม่ต้องเดา
        if (!string.IsNullOrWhiteSpace(req!.TaxId))
        {
            var byTax = await Db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == ctx!.CompanyId && c.TaxId == req.TaxId.Trim() && c.IsActive)
                .Select(c => new { c.Id, c.Name, c.TaxId, c.ExternalId, c.ExternalSystem })
                .FirstOrDefaultAsync(ct);
            if (byTax != null)
                return Ok(new ApiResponse<object>(true, new
                {
                    matched = true, confidence = 1.0, reason = "เลขผู้เสียภาษีตรงกัน",
                    contactId = byTax.Id, byTax.Name, byTax.TaxId,
                    externalId = byTax.ExternalId, externalSystem = byTax.ExternalSystem,
                }));
        }

        // 2) เทียบชื่อข้ามภาษา/ทนชื่อถูกตัด
        if (string.IsNullOrWhiteSpace(req.Name))
            return Ok(new ApiResponse<object>(true, new { matched = false, reason = "ไม่พบเลขผู้เสียภาษีนี้ในทะเบียน" }));

        var candidates = await Db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == ctx!.CompanyId && c.IsActive)
            .Select(c => new { c.Id, c.Name, c.TaxId, c.ExternalId, c.ExternalSystem })
            .ToListAsync(ct);

        var best = CounterpartyNameMatcher.Best(req.Name, candidates, c => c.Name);
        if (best == null)
            return Ok(new ApiResponse<object>(true, new
            {
                matched = false,
                reason = "ไม่พบผู้ติดต่อที่ชื่อใกล้เคียงพอ — ระบบจะสร้างรายใหม่ให้เมื่อสร้างเอกสาร",
            }));

        var (item, m) = best.Value;
        return Ok(new ApiResponse<object>(true, new
        {
            // ต่ำกว่าเกณฑ์มั่นใจ = เสนอให้คนตัดสิน ไม่ผูกอัตโนมัติ
            matched = m.Score >= CounterpartyNameMatcher.ConfidentThreshold,
            needsReview = m.Score < CounterpartyNameMatcher.ConfidentThreshold,
            confidence = m.Score,
            reason = m.Reason,
            contactId = item.Id, item.Name, item.TaxId,
            externalId = item.ExternalId, externalSystem = item.ExternalSystem,
        }));
    }
}
