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

    /// <summary>ใครถือเลขผู้เสียภาษีนี้อยู่แล้ว — ข้อเท็จจริงที่ป้อนให้
    /// <c>Helpers/PartnerSyncConflict</c> (ประกาศเป็น record แทน tuple โดยตั้งใจ:
    /// ternary ที่สองสาขาเป็น tuple ชื่อไม่ตรงกัน C# จะทิ้งชื่อแล้วไป CS1061 ไกล ๆ —
    /// tools/tuple_name_merge_check.py) ·
    /// รอบ 193 ทีม C3: เก็บ <c>Id</c> + <c>BranchCode</c> ด้วย — "ถือเลขเดียวกัน" ต้องหมายถึง<b>เลข + สาขา</b>เดียวกัน
    /// (คำตัดสินเจ้าของข้อ 20 · ประกาศอธิบดีฯ 199) ไม่ใช่เลขอย่างเดียว</summary>
    /// <summary>ผู้สมัครของ /contacts/resolve (record แทน anonymous type — ต้องสร้างลิสต์ว่างได้เมื่อ SoftScope ห้ามเทียบ)</summary>
    private sealed record ResolveCandidateRow(Guid Id, string Name, string? TaxId, string? ExternalId, string? ExternalSystem);

    private sealed record TaxIdOwnerRow(Guid Id, string TaxId, string? BranchCode, string? ExternalId, string Name);

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
        string? ContactType = null,    // "Individual" | "JuristicPerson" | "GovernmentAgency"
        // ยืนยันว่าตั้งใจเปลี่ยนเลขผู้เสียภาษีของรหัสนี้จริง — ทางไปต่อของรายการที่
        // ติดด่าน CONTACT-TAXID-CHANGED (Helpers/PartnerSyncConflict)
        bool AllowTaxIdChange = false);

    public record SyncRequest(string? SystemName, List<ContactSyncItem> Contacts);

    /// <summary>
    /// ส่งทะเบียนผู้ติดต่อจากระบบบัญชีเข้ามา (upsert ด้วย `ExternalId`)
    ///
    /// ยิงซ้ำได้เสมอ — รายการที่มีอยู่แล้วจะถูกอัปเดต ไม่สร้างซ้ำ
    /// แนะนำให้ซิงก์ทั้งทะเบียนตอนเริ่มใช้ แล้วส่งเฉพาะที่เปลี่ยนเป็นรอบ ๆ
    ///
    /// <para><b>ด่านกันชน</b> (Helpers/PartnerSyncConflict): รายการที่ชนกับทะเบียน
    /// เดิม — เลขผู้เสียภาษีเป็นของคู่ค้ารายอื่น · เปลี่ยนเลขที่ checksum ผ่านอยู่แล้ว
    /// — จะถูก<b>ปฏิเสธเป็นรายแถวพร้อมบอกว่าชนกับอะไร</b> รายการที่เหลือยังบันทึกปกติ
    /// (ทั้งชุดล้มเพราะแถวเดียวคือการลงโทษที่ไม่ได้สัดส่วนกับ sync ทะเบียนพันรายการ)</para>
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncRequest req, CancellationToken ct)
        => await SyncCoreAsync(req, dryRun: false, ct);

    /// <summary>
    /// **ลองยิงดูก่อน** — เดินด่านชุดเดียวกับ `/sync` เป๊ะ แต่ **ไม่เขียนอะไรลงฐาน**
    ///
    /// <para>ทำไมต้องเดินด่านชุดเดียวกัน: dry-run ที่เดินคนละชุดคือ dry-run ที่โกหก
    /// ซึ่งแย่กว่าไม่มี — พาร์ตเนอร์จะเชื่อว่า "ผ่านแล้ว" แล้วไปล้มตอนของจริง
    /// ⇒ ที่นี่เรียก <c>SyncCoreAsync</c> ตัวเดียวกัน ต่างกันแค่ transaction ที่
    /// <b>rollback เสมอ</b> ท้ายสุด (ไม่ใช่โค้ดตรวจคนละชุด)</para>
    /// </summary>
    [HttpPost("sync/dry-run")]
    public async Task<IActionResult> SyncDryRun([FromBody] SyncRequest req, CancellationToken ct)
        => await SyncCoreAsync(req, dryRun: true, ct);

    private async Task<IActionResult> SyncCoreAsync(SyncRequest req, bool dryRun, CancellationToken ct)
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

        // ── ใครถือเลขผู้เสียภาษีที่ส่งมาอยู่บ้าง (ทั้งบริษัท ไม่จำกัดแค่ระบบต้นทางนี้) ──
        // ต้องกวาดข้ามทุก ExternalSystem: คู่ค้ารายเดียวกันอาจถูกสร้างจาก OCR
        // (ไม่มี ExternalId) มาก่อน — ถ้าดูเฉพาะระบบนี้จะมองไม่เห็นแล้วสร้างซ้ำ
        var incomingTaxIds = req.Contacts
            .Select(c => Helpers.ThaiTaxId.Normalize(c.TaxId))
            .Where(t => t.Length == 13).Distinct().ToList();
        var taxIdOwners = new List<TaxIdOwnerRow>();
        if (incomingTaxIds.Count > 0)
        {
            // โหลดทุกสาขาของชุดเลขผ่านตัวช่วยกลาง (เทียบเลขแบบมีขีดด้วย) แล้วตัดสินทีละรายการด้วย ContactTaxBranchKey.Pick ข้างล่าง
            var rows = await Helpers.ContactTaxBranchKey.LoadByTaxIdsAsync(
                Db.Contacts.AsNoTracking(), ctx!.CompanyId, incomingTaxIds, ct);
            foreach (var r in rows)
                taxIdOwners.Add(new TaxIdOwnerRow(r.Id, r.TaxId!, r.BranchCode, r.ExternalId, r.Name));
        }

        int created = 0, updated = 0, skipped = 0, rejected = 0;
        var errors = new List<string>();

        // dry-run เขียนจริงแล้ว rollback — เป็นทางเดียวที่รับประกันว่าเดินด่าน
        // ชุดเดียวกัน (รวมด่านระดับฐานข้อมูล: unique index / FK / check constraint)
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        if (dryRun) tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in req.Contacts)
            {
                if (string.IsNullOrWhiteSpace(item.ExternalId) || string.IsNullOrWhiteSpace(item.Name))
                { skipped++; continue; }

                existing.TryGetValue(item.ExternalId, out var c);

                // ── เลขผู้เสียภาษี: "ไม่ได้ส่งมา" ≠ "ส่งค่าว่างมาเพื่อลบ" ──
                // เดิมเขียน `c.TaxId = taxId.Length > 0 ? taxId : null` ⇒ การ sync
                // แบบส่งเฉพาะช่องที่เปลี่ยน (ซึ่งเอกสารแนะนำให้ทำ!) **ล้างเลขผู้เสียภาษี
                // ของคู่ค้าทุกรายทิ้ง** เงียบ ๆ แล้วใบกำกับเต็มรูปที่ออกหลังจากนั้น
                // ขาดรายการตาม §86/4 — กติกาเดียวกับช่องอื่น (null = ไม่แตะ)
                var rawTaxId = item.TaxId?.Trim();
                var taxId = Helpers.ThaiTaxId.Normalize(c?.TaxId);   // ค่าเดิมเป็นตัวตั้ง
                if (rawTaxId != null)
                {
                    if (rawTaxId.Length == 0) taxId = "";            // ส่ง "" = ตั้งใจล้าง
                    else
                    {
                        var check = Helpers.ThaiTaxIdValidator.Check(rawTaxId);
                        if (check.IsValid) taxId = Helpers.ThaiTaxIdValidator.Normalize(rawTaxId) ?? "";
                        else errors.Add($"{item.ExternalId}: เลขผู้เสียภาษี \"{rawTaxId}\" ไม่ถูกต้อง ({check.Reason}) — คงค่าเดิมไว้ ไม่บันทึกเลขนี้");
                    }
                }

                // ── ด่านกันชน — ก่อนแตะแถวใด ๆ ──
                // รอบ 193 ทีม C3: "เลขนี้เป็นของคู่ค้ารายอื่น" ต้องเทียบด้วยคีย์ <b>เลขภาษี + สาขา</b> (ตัวจับคู่กลาง
                // Helpers/ContactTaxBranchKey) — เดิมเทียบเลขอย่างเดียว ⇒ ERP ที่ sync สำนักงานใหญ่ (รหัส A) กับสาขา 8
                // (รหัส B) ของนิติบุคคลเดียวกัน ถูกบล็อก CONTACT-TAXID-OWNED ที่รหัส B เสมอ ทั้งที่ถูกต้องตามประกาศฯ 199.
                // สาขาที่ใช้เทียบ = ที่ส่งมา → ของแถวเดิม → ไม่มี (≡ สำนักงานใหญ่ ตาม FindDuplicateContactAsync)
                var effectiveBranch = Helpers.TaxBranchCode.Normalize(item.BranchCode ?? c?.BranchCode);
                var ownerKey = Helpers.ContactTaxBranchKey.Pick(
                    taxIdOwners
                        .Where(o => !string.Equals(o.ExternalId, item.ExternalId, StringComparison.OrdinalIgnoreCase)
                                 && (c == null || o.Id != c.Id))
                        .Select(o => new Helpers.ContactKeyCandidate(o.Id, o.TaxId, o.BranchCode)),
                    taxId, effectiveBranch);
                var owner = ownerKey.ContactId is Guid ownerId ? taxIdOwners.First(o => o.Id == ownerId) : null;
                var clash = Helpers.PartnerSyncConflict.CheckContact(
                    externalId: item.ExternalId.Trim(),
                    incomingTaxId: taxId,
                    existingTaxId: c?.TaxId,
                    taxIdOwnerExternalId: owner == null
                        ? null
                        : (string.IsNullOrWhiteSpace(owner.ExternalId) ? "(สร้างจากเอกสารในระบบเรา)" : owner.ExternalId),
                    taxIdOwnerName: owner?.Name,
                    allowTaxIdChange: item.AllowTaxIdChange);
                if (clash.IsBlocked) { rejected++; errors.Add(clash.Message); continue; }
                if (clash.HasIssue) errors.Add(clash.Message);

                if (c == null)
                {
                    c = new Contact
                    {
                        CompanyId = ctx!.CompanyId,
                        ExternalId = item.ExternalId.Trim(),
                        ExternalSystem = system,
                        CreatedBy = "api:v1:contact-sync",
                    };
                    Db.Contacts.Add(c);
                    // รหัสเดียวกันส่งมาสองแถวในชุดเดียว = แถวหลังต้องอัปเดตแถวแรก
                    // ไม่ใช่สร้างคู่ค้าซ้ำ (เดิมสร้างซ้ำเพราะ dictionary ไม่ถูกเติม)
                    existing[item.ExternalId] = c;
                    created++;
                }
                else updated++;

                c.Name = item.Name.Trim();
                c.TaxId = taxId.Length > 0 ? taxId : null;
                if (item.Address != null) c.Address = item.Address.Trim();
                if (item.Phone != null) c.Phone = item.Phone.Trim();
                if (item.Email != null) c.Email = item.Email.Trim();
                c.IsSupplier = item.IsSupplier;
                c.IsCustomer = item.IsCustomer;
                c.IsActive = item.IsActive;

                // ── ชนิดผู้ติดต่อ + รหัสสาขา จากตัวตัดสินตัวเดียว (Helpers/ContactTypeResolver) ──
                // ค่านี้ตัดสิน ภ.ง.ด.3 vs ภ.ง.ด.53 และ scheme ของ e-Tax XML (NIDN/TXID)
                // ⇒ "ตัดสินไม่ได้" ต้องออกมาเป็น ContactType.Unknown ที่เห็นได้
                //   ไม่ใช่เดาเป็นบุคคลธรรมดาแล้วเงียบ
                var typeVerdict = Helpers.ContactTypeResolver.ApplyToExisting(
                    c.ContactType, item.ContactType, taxId, c.Name);
                c.ContactType = typeVerdict.Type;

                var typeClash = Helpers.PartnerSyncConflict.CheckDeclaredType(
                    item.ExternalId.Trim(), typeVerdict.Type, taxId);
                if (typeClash.HasIssue) errors.Add(typeClash.Message);

                // รหัสสาขาเป็นเรื่องของนิติบุคคล/ราชการ (§86/4(2) · ประกาศฯ 199) —
                // ตัวตัดสินเดียวกันเป็นคนบอกว่าเก็บได้ไหม ไม่ใช่เก็บดิบทุกค่าที่ส่งมา
                var branchIn = item.BranchCode == null ? c.BranchCode : item.BranchCode;
                c.BranchCode = Helpers.ContactTypeResolver.BranchCodeFor(typeVerdict.Type, branchIn);

                if (typeVerdict.Type == Models.Enums.ContactType.Unknown)
                    errors.Add($"{item.ExternalId}: ยังระบุชนิดผู้ติดต่อไม่ได้ ({typeVerdict.Reason}) "
                        + "— ส่ง contactType (\"Individual\"/\"JuristicPerson\"/\"GovernmentAgency\") "
                        + "หรือเลขผู้เสียภาษีที่ถูกต้องมาด้วย มิฉะนั้นแบบยื่น ภ.ง.ด. และ e-Tax จะเลือกไม่ถูก");

                c.UpdatedAt = DateTime.UtcNow;
                c.UpdatedBy = dryRun ? "api:v1:contact-sync:dry-run" : "api:v1:contact-sync";
            }

            await Db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (dryRun)
        {
            // dry-run ต้องคืน "จะพังตรงไหน" ไม่ใช่ 500 เปล่า ๆ — นั่นคือเหตุผลที่มันมีอยู่
            errors.Add("ฐานข้อมูลปฏิเสธชุดนี้: " + ex.GetBaseException().Message);
        }
        finally
        {
            // dry-run: ทิ้งทุกอย่างเสมอ แม้ทางเดินจะสำเร็จ
            if (tx != null)
            {
                await tx.RollbackAsync(ct);
                await tx.DisposeAsync();
                // ตัวติดตามยังถือ entity ที่ถูก rollback ไปแล้ว — ปล่อยไว้จะทำให้
                // การ SaveChanges ครั้งถัดไปใน request เดียวกันเขียนของที่ทิ้งแล้ว
                Db.ChangeTracker.Clear();
            }
        }

        var head = dryRun ? "ผลการลองยิง (ไม่ได้บันทึกอะไรลงฐาน)" : "ซิงก์ผู้ติดต่อสำเร็จ";
        return Ok(new ApiResponse<object>(true, new
        {
            dryRun,
            created, updated, skipped, rejected,
            warnings = errors,
        }, $"{head} — เพิ่มใหม่ {created} · อัปเดต {updated}"
           + (skipped > 0 ? $" · ข้าม {skipped} (ไม่มีรหัสหรือชื่อ)" : "")
           + (rejected > 0 ? $" · ปฏิเสธ {rejected} (ชนกับทะเบียนเดิม — ดู warnings)" : "")));
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

    /// <param name="BranchCode">รหัสสาขา 5 หลักของคู่ค้า (ไม่บังคับ · รอบ 193 ข้อ 20) — ไม่ส่ง = "ไม่ระบุ"
    /// (แถวสำนักงานใหญ่ก่อน) · ส่งมา = ต้องตรงสาขา (เลขเดียวกันคนละสาขา = คนละผู้ติดต่อตามประกาศอธิบดีฯ 199)</param>
    public record ResolveRequest(string? Name, string? TaxId, string? BranchCode = null);

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
        // รหัสสาขาผิดรูป ("8A") = บอกผู้เรียก — ห้ามถือเป็น "ไม่ระบุ" แล้วตอบแถวสำนักงานใหญ่ว่า "ตรงกัน" (ด่านเดียวกับ /documents)
        if (!Accounting.Helpers.TaxBranchCode.TryNormalize(req!.BranchCode, out _, out var branchError))
            return BadRequest(new ApiResponse<string>(false, null, $"branchCode: {branchError}"));

        // 1) เลขผู้เสียภาษี (+ สาขา) ตรง = จบ ไม่ต้องเดา — ตัวจับคู่กลาง Helpers/ContactTaxBranchKey (รอบ 193 ข้อ 20)
        var taxKey = default(Accounting.Helpers.ContactKeyMatch);
        if (!string.IsNullOrWhiteSpace(req!.TaxId))
        {
            taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
                Db.Contacts.AsNoTracking().Where(c => c.IsActive), ctx!.CompanyId, req.TaxId, req.BranchCode, ct);
            var byTax = taxKey.ContactId is Guid keyId
                ? await Db.Contacts.AsNoTracking()
                    .Where(c => c.Id == keyId && c.CompanyId == ctx!.CompanyId)
                    .Select(c => new { c.Id, c.Name, c.TaxId, c.BranchCode, c.ExternalId, c.ExternalSystem })
                    .FirstOrDefaultAsync(ct)
                : null;
            if (byTax != null)
                return Ok(new ApiResponse<object>(true, new
                {
                    matched = true, confidence = 1.0, reason = "เลขผู้เสียภาษีตรงกัน",
                    contactId = byTax.Id, byTax.Name, byTax.TaxId, branchCode = byTax.BranchCode,
                    externalId = byTax.ExternalId, externalSystem = byTax.ExternalSystem,
                }));
            // เลขนี้มีแล้วแต่คนละสาขา — ห้ามถอยไปเทียบชื่อ (ชื่อเดียวกัน = แถวสาขาอื่นของเลขเดียวกัน)
            if (taxKey.TaxIdExists)
                return Ok(new ApiResponse<object>(true, new
                {
                    matched = false,
                    taxIdExists = true,
                    // รอบ 193 ทีม C3: ข้อความต้องตรงพฤติกรรมจริง — เดิมบอกว่า "ระบบจะสร้างให้เมื่อสร้างเอกสาร" แต่
                    // POST /api/v1/documents ไม่เคยรับสาขา (ส่ง null ⇒ ได้แถวสำนักงานใหญ่). ตอนนี้รับ contactBranchCode แล้ว
                    reason = $"มีผู้ติดต่อเลขผู้เสียภาษีนี้แล้ว แต่ไม่มี{Accounting.Helpers.TaxBranchCode.Label(req.BranchCode)} "
                        + "— ส่ง contactTaxId พร้อม contactBranchCode เดียวกันใน POST /api/v1/documents แล้วระบบจะสร้าง"
                        + "ผู้ติดต่อของสาขานี้แยกให้ (เลขเดียวกันคนละสาขา = คนละผู้ติดต่อ) · ถ้าไม่ส่ง contactBranchCode "
                        + "เอกสารจะผูกกับผู้ติดต่อสำนักงานใหญ่",
                }));
        }

        // 2) เทียบชื่อข้ามภาษา/ทนชื่อถูกตัด
        if (string.IsNullOrWhiteSpace(req.Name))
            return Ok(new ApiResponse<object>(true, new { matched = false, reason = "ไม่พบเลขผู้เสียภาษีนี้ในทะเบียน" }));

        // ฝ่ายค้านรอบสอง (probe C3 · คลาส C-6): ผู้สมัครเทียบชื่อมาจากชุด SoftScope ตัวเดียว — เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข
        // (เดิมเทียบทุกแถว ⇒ เลขใหม่ + ชื่อคล้าย = ตอบ matched กับนิติบุคคลอื่นที่ถือเลขอื่น แล้วพาร์ตเนอร์ผูกรหัสผิดราย)
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(
            Db.Contacts.AsNoTracking().Where(c => c.IsActive), ctx!.CompanyId, req.TaxId, taxKey);
        var candidates = softScope == null ? new List<ResolveCandidateRow>()
            : await softScope
                .Select(c => new ResolveCandidateRow(c.Id, c.Name, c.TaxId, c.ExternalId, c.ExternalSystem))
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
