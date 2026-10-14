using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import.Targets;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  ปลายทางข้อมูลหลัก (master data) ของศูนย์นำเข้า — รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.4
//  ผู้ติดต่อ · สินค้า · ผังบัญชี · โปรเจกต์ · พนักงาน · งบประมาณ · สมุดรายวัน — ลงผ่าน writer เดิมตัวเดียว (IImportRowWriter → ImportExportService)
//  CommitStyle = SharedTransaction: pipeline คุมธุรกรรม + savepoint · ที่นี่ SaveChanges ทีละแถว (แถวซ้ำในไฟล์เดียวกันหากันเจอ) ไม่เปิด/commit ธุรกรรมเอง
//  บล็อก (Error) เฉพาะ §2.5 (JE ไม่ลงตัว) · นอกนั้นคำเตือนที่รับทราบได้ (คำตัดสินข้อ 150) · ชื่อคล้ายอย่างเดียว = แนะนำ ไม่เลือกให้เอง (ข้อ 151 (5))
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>ฐานของ target ข้อมูลหลัก — จับคู่ข้อมูลเดิม · แถวซ้ำในไฟล์ · ลงผ่าน writer เดิม · ย้อน (ลบแบบ soft ถ้าไม่มีการอ้าง / คืนค่าเดิม)</summary>
public abstract class MasterDataImportTarget : IImportTarget
{
    protected MasterDataImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver)
    {
        Db = db;
        Writer = writer;
        Resolver = resolver;
    }

    protected AccountingDbContext Db { get; }
    protected IImportRowWriter Writer { get; }
    protected IImportEntityResolver Resolver { get; }

    public abstract string EntityType { get; }
    public abstract string Label { get; }
    private IReadOnlyList<ImportFieldDto>? _schema;
    /// <summary>field ของปลายทาง = แคตตาล็อกตัวเดียว <see cref="ImportSchemas.For"/> (ทีม A) · ปรับชนิดได้เฉพาะที่ target ประกาศใน <see cref="TypeOverrides"/></summary>
    public IReadOnlyList<ImportFieldDto> Schema => _schema ??= (ImportSchemas.For(EntityType) ?? Array.Empty<ImportFieldDto>())
        .Select(f => TypeOverrides != null && TypeOverrides.TryGetValue(f.Field, out var t) ? f with { Type = t } : f).ToList();

    /// <summary>ชนิด field ที่ปลายทางนี้ตั้งใจต่างจากแคตตาล็อก (null = ไม่มี) — ใช้เมื่อกติกาของปลายทางผ่อนกว่าตัวแปลงกลาง (เช่น เลขบัตรที่ checksum ไม่ผ่านยังเก็บได้)</summary>
    protected virtual IReadOnlyDictionary<string, ImportFieldType>? TypeOverrides => null;
    public ImportCommitStyle CommitStyle => ImportCommitStyle.SharedTransaction;
    public abstract bool SupportsUpdate { get; }

    /// <summary>จับคู่ข้อมูลเดิมผ่าน <see cref="IImportEntityResolver"/> ไหม</summary>
    protected virtual bool Resolves => true;
    /// <summary>เมื่อระบบจับคู่ได้ด้วยกุญแจ (เลขภาษี+สาขา / รหัส) — ค่าเริ่ม: ข้าม (ทับค่าเดิมต้องให้ผู้ใช้เลือกเอง)</summary>
    protected virtual ImportRowAction KeyMatchedAction => ImportRowAction.Skip;
    /// <summary>คำอธิบายเมื่อจับคู่ได้ด้วยกุญแจ</summary>
    protected virtual string KeyMatchedNote(string label)
        => $"มีอยู่แล้ว ({label}) — ระบบข้ามแถวนี้ไว้ · เปลี่ยนเป็น \"อัปเดต\" ถ้าต้องการทับค่าเดิมด้วยค่าจากไฟล์";
    /// <summary>คีย์หาแถวซ้ำกันเองในไฟล์ (null = ไม่ตรวจ)</summary>
    protected abstract string? InFileKey(ImportTargetRow row);
    protected abstract string? DisplayKeyOf(ImportTargetRow row);
    protected virtual string? GroupKeyOf(ImportTargetRow row) => null;
    /// <summary>ตรวจรายแถวของชนิดนี้ (issue + ค่าที่เติมให้)</summary>
    protected virtual void Check(ImportTargetRow row, List<ImportIssueDto> issues, Dictionary<string, string?> filled) { }
    /// <summary>ตรวจทั้งชุด (เช่น JE ลงตัวต่อใบ)</summary>
    protected virtual void CheckBatch(IReadOnlyList<ImportTargetRow> rows, IReadOnlyDictionary<Guid, List<ImportIssueDto>> issues) { }

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var allowAi = !(ctx.BatchOptions != null && ctx.BatchOptions.TryGetValue(ImportBatchOptionKeys.AllowAi, out var a) && a == "false");
        var matches = Resolves
            ? (await Resolver.MatchAsync(ctx.CompanyId, EntityType, rows, new ImportResolveOptions(allowAi), ct)).ToDictionary(m => m.RowId)
            : new Dictionary<Guid, ImportEntityMatchResult>();
        var issuesByRow = rows.ToDictionary(r => r.RowId, _ => new List<ImportIssueDto>());
        CheckBatch(rows, issuesByRow);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var row in rows)
        {
            var issues = issuesByRow[row.RowId];
            var filled = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Check(row, issues, filled);
            if (InFileKey(row) is { } key)
            {
                if (seen.TryGetValue(key, out var firstRow))
                    issues.Add(Issue(null, "duplicate-in-file", ImportIssueSeverity.Warning,
                        $"แถวนี้ซ้ำกับแถวที่ {firstRow} ในไฟล์ — ระบบจะลงแถวแรก แล้วแถวนี้รวม/ข้ามตามที่เลือก · ตัดออกได้ถ้าซ้ำโดยไม่ตั้งใจ"));
                else seen[key] = row.RowNo;
            }

            ImportRowAction? action = null;
            Guid? matched = null;
            var auto = false;
            if (matches.TryGetValue(row.RowId, out var m))
            {
                issues.AddRange(m.Issues);
                if (m.EntityId is Guid id)
                {
                    matched = id;
                    auto = m.AutoApply;
                    if (m.AutoApply)
                    {
                        action = SupportsUpdate ? KeyMatchedAction : ImportRowAction.Skip;
                        var label = m.Candidates.FirstOrDefault(c => c.EntityId == id)?.Label ?? m.Kind;
                        issues.Add(Issue(null, "matched-existing", ImportIssueSeverity.Info, KeyMatchedNote(label), source: ImportIssueSource.Match));
                    }
                    else action = ImportRowAction.Create;   // ชื่อคล้าย = แนะนำ ผู้ใช้เลือก "อัปเดต" เอง
                }
                else action = ImportRowAction.Create;
            }
            result.Add(new ImportRowValidation(row.RowId, issues, action, matched, m?.Kind, m?.Score, DisplayKeyOf(row), GroupKeyOf(row),
                filled.Count > 0 ? filled : null, m?.Candidates, auto, m?.UsedAi ?? false, m?.AiFeedbackId,
                MatchAiAnswer: m?.AiAnswer, MatchAnswerSpace: m?.AiFeedbackId != null ? ImportMatchAnswerSpace.Entity : null));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
    {
        var results = new List<ImportRowCommitResult>(group.Count);
        foreach (var row in group)
            results.Add(await Writer.WriteRowAsync(ctx.CompanyId, EntityType, row, new ImportRowWriteOptions(SaveChanges: true),
                ctx.Actor.UserId.ToString(), ct));
        return results;
    }

    // ───────────────────────── ย้อน ─────────────────────────

    /// <summary>ลบแบบ soft ได้ไหม (ไม่มีการอ้างจากข้อมูลอื่น) — null = ได้ · ข้อความ = เหตุผล + ทางไปต่อ</summary>
    protected abstract Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct);

    /// <summary>ตัวเดียวกับ <see cref="DeleteBlockAsync(Guid, Guid, CancellationToken)"/> แต่รู้ว่าย้อน entity ไหนพร้อมกันในรอบนี้ (<paramref name="undoing"/>) —
    /// ชนิดที่อ้างกันเอง (บัญชีลูกในไฟล์เดียวกันกับบัญชีแม่) ไม่ต้องบล็อกกันเอง · ค่าเริ่ม = ไม่สนชุดนี้</summary>
    protected virtual Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, IReadOnlySet<Guid> undoing,
        CancellationToken ct) => DeleteBlockAsync(companyId, entityId, ct);

    /// <summary>คืนค่าเดิม (RestoreBefore) ได้ไหม — null = ได้ · ค่าเริ่ม = ได้เสมอ (ชนิดที่มีสถานะ เช่น สมุดรายวันที่ลงบัญชีแล้ว override)</summary>
    protected virtual Task<(string Reason, string WayForward)?> RestoreBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
        => Task.FromResult<(string Reason, string WayForward)?>(null);
    /// <summary>ลบแบบ soft · false = ไม่พบ (ถูกลบไปแล้ว)</summary>
    protected abstract Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct);
    /// <summary>คืนค่าเดิมจาก BeforeJson · false = ชนิดนี้คืนค่าไม่ได้/ไม่พบ</summary>
    protected virtual Task<bool> RestoreAsync(Guid companyId, Guid entityId, Dictionary<string, string?> before, CancellationToken ct)
        => Task.FromResult(false);
    protected virtual bool CanRestore => false;

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
    {
        var checks = new List<ImportUndoCheck>(rows.Count);
        // แถวที่ "สร้าง" entity เดียวกันหลายแถว (JE หลายบรรทัด/แถวซ้ำที่รวมเข้าแถวแรก) — ตัดสินครั้งเดียวต่อ entity
        var decided = new Dictionary<Guid, (string Reason, string WayForward)?>();
        var undoing = rows.Where(r => r.BeforeJson == null).Select(r => r.EntityId).ToHashSet();
        foreach (var r in rows)
        {
            if (r.BeforeJson != null)
            {
                var restoreBlock = CanRestore ? await RestoreBlockAsync(ctx.CompanyId, r.EntityId, ct) : null;
                checks.Add(!CanRestore
                    ? new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, $"{Label}ที่ถูกอัปเดตย้อนค่าเดิมอัตโนมัติไม่ได้",
                        $"แก้ค่ากลับที่หน้า{Label}")
                    : restoreBlock is { } rb
                        ? new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, rb.Reason, rb.WayForward)
                        : new ImportUndoCheck(r.RowId, true, ImportUndoKind.RestoreBefore));
                continue;
            }
            if (!decided.TryGetValue(r.EntityId, out var block))
            {
                block = await DeleteBlockAsync(ctx.CompanyId, r.EntityId, undoing, ct);
                decided[r.EntityId] = block;
            }
            checks.Add(block is { } b
                ? new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, b.Reason, b.WayForward)
                : new ImportUndoCheck(r.RowId, true, ImportUndoKind.SoftDelete));
        }
        return checks;
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
    {
        var results = new List<ImportRowUndoResult>(rows.Count);
        var deleted = new HashSet<Guid>();
        var undoing = rows.Where(r => r.BeforeJson == null).Select(r => r.EntityId).ToHashSet();
        // ย้อนจากแถวท้ายไปแถวแรก — แถวที่อัปเดตทับแถวที่สร้างในไฟล์เดียวกันคืนค่าก่อน แล้วค่อยลบแถวที่สร้าง
        foreach (var r in rows.OrderByDescending(x => x.RowNo))
        {
            if (r.BeforeJson != null)
            {
                var before = ImportJson.Deserialize<Dictionary<string, string?>>(r.BeforeJson) ?? new Dictionary<string, string?>();
                var restoreBlock = CanRestore ? await RestoreBlockAsync(ctx.CompanyId, r.EntityId, ct) : null;
                if (restoreBlock is { } rb) { results.Add(new ImportRowUndoResult(r.RowId, false, $"{rb.Reason} — {rb.WayForward}")); continue; }
                var ok = CanRestore && await RestoreAsync(ctx.CompanyId, r.EntityId, before, ct);
                results.Add(new ImportRowUndoResult(r.RowId, ok, ok ? null : $"คืนค่าเดิมของ{Label}ไม่ได้ — แก้ที่หน้า{Label}"));
                continue;
            }
            if (deleted.Contains(r.EntityId)) { results.Add(new ImportRowUndoResult(r.RowId, true)); continue; }
            var block = await DeleteBlockAsync(ctx.CompanyId, r.EntityId, undoing, ct);
            if (block is { } b) { results.Add(new ImportRowUndoResult(r.RowId, false, $"{b.Reason} — {b.WayForward}")); continue; }
            var done = await SoftDeleteAsync(ctx.CompanyId, r.EntityId, ct);
            if (done) deleted.Add(r.EntityId);
            results.Add(new ImportRowUndoResult(r.RowId, true));   // ไม่พบ = ถูกลบไปแล้ว (ผลเหมือนกัน)
        }
        await Db.SaveChangesAsync(ct);
        return results;
    }

    // ───────────────────────── ตัวช่วย ─────────────────────────

    protected static string? V(ImportTargetRow row, string field)
        => row.Values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    protected static decimal? Amount(ImportTargetRow row, string field)
        => V(row, field) is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    protected static ImportIssueDto Issue(string? field, string code, ImportIssueSeverity sev, string message, string? suggested = null,
        string? ruleCode = null, string? legalRef = null, ImportIssueSource source = ImportIssueSource.Target)
        => new(field, code, sev, message, suggested, ruleCode, legalRef, source);

    protected static void SoftDelete(BaseEntity e)
    {
        e.IsDeleted = true;
        e.UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>ผู้ติดต่อ — เลขภาษี+สาขาตรง ⇒ เติมช่องว่างของรายเดิม (Merge · ค่าเริ่มเดิมของเส้นนำเข้า) · ชื่อคล้าย = แนะนำ</summary>
public sealed class ContactsImportTarget : MasterDataImportTarget
{
    public ContactsImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "contacts";
    public override string Label => "ผู้ติดต่อ";
    public override bool SupportsUpdate => true;
    protected override ImportRowAction KeyMatchedAction => ImportRowAction.Merge;
    protected override string KeyMatchedNote(string label)
        => $"มีอยู่แล้ว ({label}) — ระบบจะเติมเฉพาะช่องที่รายเดิมยังว่าง · เปลี่ยนเป็น \"อัปเดต\" ถ้าต้องการทับค่าเดิม";
    protected override bool CanRestore => true;

    protected override string? InFileKey(ImportTargetRow row)
        => V(row, "TaxId") is { } t ? $"tax:{t}:{V(row, "BranchCode") ?? "-"}" : V(row, "Name") is { } n ? "name:" + ThaiLegalNameKey.Key(n) : null;
    protected override string? DisplayKeyOf(ImportTargetRow row) => V(row, "Name");

    protected override void Check(ImportTargetRow row, List<ImportIssueDto> issues, Dictionary<string, string?> filled)
    {
        // ไม่ระบุทั้งลูกค้า/ผู้ขาย — รายนี้จะไม่โผล่ในตัวเลือกลูกค้า/ผู้ขายของเอกสาร (ไม่บล็อก)
        if (V(row, "IsCustomer") != "true" && V(row, "IsSupplier") != "true")
            issues.Add(Issue("IsCustomer", "contact-role-missing", ImportIssueSeverity.Info,
                "ไม่ได้ระบุว่าเป็นลูกค้าหรือผู้ขาย — ตั้งได้ภายหลังที่หน้าผู้ติดต่อ"));
    }

    protected override async Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var used = await Db.Documents.IgnoreQueryFilters().AnyAsync(d => d.CompanyId == companyId && d.ContactId == entityId, ct);
        return used ? ("มีเอกสารอ้างถึงผู้ติดต่อนี้แล้ว", "ปิดใช้งานผู้ติดต่อที่หน้าผู้ติดต่อแทนการลบ") : null;
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var c = await Db.Contacts.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (c == null) return false;
        SoftDelete(c);
        return true;
    }

    protected override async Task<bool> RestoreAsync(Guid companyId, Guid entityId, Dictionary<string, string?> before, CancellationToken ct)
    {
        var c = await Db.Contacts.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (c == null) return false;
        if (before.TryGetValue("Name", out var name) && !string.IsNullOrWhiteSpace(name)) c.Name = name;
        c.TaxId = before.GetValueOrDefault("TaxId");
        c.BranchCode = before.GetValueOrDefault("BranchCode");
        if (Enum.TryParse<ContactType>(before.GetValueOrDefault("ContactType"), out var ctype)) c.ContactType = ctype;
        c.Phone = before.GetValueOrDefault("Phone");
        c.Address = before.GetValueOrDefault("Address");
        c.ContactPerson = before.GetValueOrDefault("ContactPerson");
        c.Email = before.GetValueOrDefault("Email");
        c.IsCustomer = before.GetValueOrDefault("IsCustomer") == "true";
        c.IsSupplier = before.GetValueOrDefault("IsSupplier") == "true";
        c.UpdatedAt = DateTime.UtcNow;
        return true;
    }
}

/// <summary>สินค้า/บริการ — รหัสตรง ⇒ ข้าม (ทับราคาต้องเลือกเอง) · ชื่อคล้าย = แนะนำ</summary>
public sealed class ProductsImportTarget : MasterDataImportTarget
{
    public ProductsImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "products";
    public override string Label => "สินค้า/บริการ";
    public override bool SupportsUpdate => true;
    protected override bool CanRestore => true;
    protected override string? InFileKey(ImportTargetRow row) => V(row, "Code");
    protected override string? DisplayKeyOf(ImportTargetRow row) => string.Join(" ", new[] { V(row, "Code"), V(row, "Name") }.Where(x => x != null));

    protected override void Check(ImportTargetRow row, List<ImportIssueDto> issues, Dictionary<string, string?> filled)
    {
        // "ยกเว้น VAT" (ม.81) — สินค้ายังไม่มีช่องยกเว้นแยก: เก็บเป็น 0% ให้ผู้ใช้ตรวจ (ไม่ใช่ 7% เงียบ ๆ แบบเส้นเดิม)
        if (V(row, "VatRate") == ImportPipelineService.VatExemptToken)
            issues.Add(Issue("VatRate", "vat-exempt-as-zero", ImportIssueSeverity.Warning,
                "สินค้ายกเว้นภาษีมูลค่าเพิ่ม (ม.81) — ระบบเก็บอัตราเป็น 0% · ตรวจการออกใบกำกับของสินค้านี้ก่อนใช้ (ยกเว้น ≠ อัตราศูนย์ ม.80/1)",
                legalRef: "ป.รัษฎากร ม.81"));
        if (V(row, "Unit") == null)
            filled["Unit"] = "ชิ้น";
    }

    protected override async Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var p = await Db.Products.AsNoTracking().Where(x => x.Id == entityId && x.CompanyId == companyId).Select(x => new { x.Code }).FirstOrDefaultAsync(ct);
        if (p == null) return null;
        var moved = await Db.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.CompanyId == companyId && m.ProductId == entityId, ct);
        var onDoc = await Db.DocumentLines.AnyAsync(l => l.ProductCode == p.Code
            && Db.Documents.IgnoreQueryFilters().Any(d => d.Id == l.DocumentId && d.CompanyId == companyId), ct);
        return moved || onDoc ? ("สินค้านี้มีความเคลื่อนไหวสต็อกหรืออยู่บนเอกสารแล้ว", "ปิดใช้งานสินค้าที่หน้าสินค้าแทนการลบ") : null;
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var p = await Db.Products.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (p == null) return false;
        SoftDelete(p);
        return true;
    }

    protected override async Task<bool> RestoreAsync(Guid companyId, Guid entityId, Dictionary<string, string?> before, CancellationToken ct)
    {
        var p = await Db.Products.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (p == null) return false;
        if (before.GetValueOrDefault("Name") is { Length: > 0 } n) p.Name = n;
        if (before.GetValueOrDefault("Unit") is { Length: > 0 } u) p.Unit = u;
        p.Category = before.GetValueOrDefault("Category");
        p.SellingPrice = Dec(before, "SellingPrice") ?? p.SellingPrice;
        p.CostPrice = Dec(before, "CostPrice") ?? p.CostPrice;
        p.VatRate = Dec(before, "VatRate") ?? p.VatRate;
        p.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private static decimal? Dec(Dictionary<string, string?> d, string k)
        => decimal.TryParse(d.GetValueOrDefault(k), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>ผังบัญชี — รหัสตรง ⇒ ข้าม · ประเภทบัญชีว่าง/อ่านไม่ได้ ⇒ เติมจากหมวดเลขหน้า (1–5) พร้อมคำเตือน</summary>
public sealed class ChartOfAccountsImportTarget : MasterDataImportTarget
{
    public ChartOfAccountsImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "chart-of-accounts";
    public override string Label => "ผังบัญชี";
    public override bool SupportsUpdate => true;
    protected override bool CanRestore => true;
    protected override string? InFileKey(ImportTargetRow row) => V(row, "AccountCode");
    protected override string? DisplayKeyOf(ImportTargetRow row)
        => string.Join(" ", new[] { V(row, "AccountCode"), V(row, "AccountName") }.Where(x => x != null));

    /// <summary>หมวดผังบัญชีไทยตามเลขหน้า (1 สินทรัพย์ · 2 หนี้สิน · 3 ส่วนของเจ้าของ · 4 รายได้ · 5 ค่าใช้จ่าย)</summary>
    private static AccountType? CategoryOf(string? code) => code?.TrimStart().FirstOrDefault() switch
    {
        '1' => AccountType.Asset,
        '2' => AccountType.Liability,
        '3' => AccountType.Equity,
        '4' => AccountType.Revenue,
        '5' => AccountType.Expense,
        _ => null,
    };

    protected override void Check(ImportTargetRow row, List<ImportIssueDto> issues, Dictionary<string, string?> filled)
    {
        if (V(row, "AccountType") != null) return;
        if (CategoryOf(V(row, "AccountCode")) is { } t)
        {
            filled["AccountType"] = t.ToString();
            issues.Add(Issue("AccountType", "account-type-from-code", ImportIssueSeverity.Warning,
                $"ประเภทบัญชีในไฟล์ว่าง/อ่านไม่ได้ — ระบบตั้งเป็น {t} ตามหมวดเลขหน้าของรหัส {V(row, "AccountCode")} · ตรวจก่อนยืนยัน"));
        }
    }

    protected override Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
        => DeleteBlockAsync(companyId, entityId, new HashSet<Guid>(), ct);

    /// <summary>ป้ายไทยของตารางที่อ้างบัญชี (ข้อความปฏิเสธ — ไม่โชว์ชื่อคลาสอังกฤษ) · ไม่อยู่ในตาราง = "ข้อมูลอื่นในระบบ"</summary>
    private static readonly IReadOnlyDictionary<string, string> ReferrerLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(JournalEntryLine)] = "รายการบันทึกบัญชี",
        [nameof(DocumentLine)] = "บรรทัดเอกสาร",
        [nameof(OpeningBalance)] = "ยอดยกมา",
        [nameof(AccountMapping)] = "การจับคู่บัญชีของชุดยอดยกมา",
        [nameof(BankAccount)] = "บัญชีธนาคาร",
        [nameof(Product)] = "สินค้า/บริการ (บัญชีตั้งต้น)",
        [nameof(FixedAsset)] = "ทะเบียนสินทรัพย์",
        [nameof(Budget)] = "งบประมาณ",
    };

    /// <summary>
    /// ฝ่ายค้านรอบ ETL (P1-3): เดิมดูแค่บรรทัด JE/เอกสาร ⇒ ย้อนผังที่ถูกอ้างจากยอดยกมา · บัญชีลูก · บัญชีธนาคาร (LinkedAccountId) ·
    /// บัญชีตั้งต้นของสินค้า/สินทรัพย์ · AccountMappings ฯลฯ ได้ แล้วข้อมูลเหล่านั้นชี้บัญชีที่ถูกลบ · ตอนนี้ไล่ <b>ทุก foreign key ที่ชี้ผังบัญชีในโมเดล</b>
    /// (ไม่ใช่รายการที่เขียนมือ — ตารางใหม่ที่อ้างผังพรุ่งนี้ถูกนับอัตโนมัติ) + ค่าตั้งที่ไม่มี FK (<c>CompanySettings.DefaultPaymentAccountId</c>) ·
    /// บัญชีลูกที่ถูกย้อนพร้อมกันในรอบนี้ไม่นับ (<paramref name="undoing"/>) · กรอง CompanyId เมื่อตารางมีคอลัมน์นั้น (กฎ M) · แถวที่ลบแบบ soft แล้วไม่นับ
    /// </summary>
    protected override Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, IReadOnlySet<Guid> undoing,
        CancellationToken ct) => AccountReferencesAsync(companyId, entityId, undoing, ct);

    private async Task<(string Reason, string WayForward)?> AccountReferencesAsync(Guid companyId, Guid entityId, IReadOnlySet<Guid> undoing,
        CancellationToken ct)
    {
        const string way = "ปิดใช้งานบัญชีที่หน้าผังบัญชีแทนการลบ (หรือย้ายข้อมูลที่อ้างไปบัญชีอื่นก่อน แล้วย้อนอีกครั้ง)";
        var undoingIds = undoing.ToList();
        if (await Db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId && a.ParentAccountId == entityId && !undoingIds.Contains(a.Id), ct))
            return ("บัญชีนี้มีบัญชีย่อยอยู่ใต้", way);
        if (await Db.CompanySettings.AnyAsync(x => x.CompanyId == companyId && x.DefaultPaymentAccountId == entityId, ct))
            return ("บัญชีนี้ถูกตั้งเป็นแหล่งเงินตั้งต้นของบริษัท (หน้าตั้งค่า)", "เปลี่ยนแหล่งเงินตั้งต้นที่หน้าตั้งค่าก่อน แล้วย้อนอีกครั้ง");

        var coa = Db.Model.FindEntityType(typeof(ChartOfAccount));
        if (coa == null) return ("ตรวจการอ้างถึงบัญชีไม่ได้", way);   // ล้มฝั่งปลอดภัย (ไม่ลบ)
        foreach (var fk in coa.GetReferencingForeignKeys())
        {
            var dep = fk.DeclaringEntityType;
            if (dep.ClrType == typeof(ChartOfAccount) || fk.Properties.Count != 1) continue;   // บัญชีลูกตรวจด้านบน
            var table = dep.GetTableName();
            if (table == null) continue;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, dep.GetSchema());
            var col = fk.Properties[0].GetColumnName(store);
            if (col == null) continue;
            var where = $"\"{col}\" = {{0}}";
            var args = new List<object> { entityId };
            if (dep.FindProperty("CompanyId")?.GetColumnName(store) is { } companyCol)
            {
                where += $" AND \"{companyCol}\" = {{1}}";
                args.Add(companyId);
            }
            if (dep.FindProperty("IsDeleted")?.GetColumnName(store) is { } deletedCol)
                where += $" AND NOT \"{deletedCol}\"";
            var from = dep.GetSchema() is { Length: > 0 } schema ? $"\"{schema}\".\"{table}\"" : $"\"{table}\"";
            // ชื่อตาราง/คอลัมน์มาจาก metadata ของโมเดล (ไม่ใช่ค่าจากผู้ใช้) · ค่าเป็นพารามิเตอร์ {0}/{1}
            var sql = "SELECT EXISTS (SELECT 1 FROM " + from + " WHERE " + where + ") AS \"Value\"";
            var used = await Db.Database.SqlQueryRaw<bool>(sql, args.ToArray()).SingleAsync(ct);
            if (used)
                return ($"บัญชีนี้ถูกอ้างถึงโดย{ReferrerLabels.GetValueOrDefault(dep.ClrType.Name, "ข้อมูลอื่นในระบบ")}แล้ว", way);
        }
        return null;
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var a = await Db.ChartOfAccounts.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (a == null) return false;
        SoftDelete(a);
        return true;
    }

    protected override async Task<bool> RestoreAsync(Guid companyId, Guid entityId, Dictionary<string, string?> before, CancellationToken ct)
    {
        var a = await Db.ChartOfAccounts.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (a == null) return false;
        if (before.GetValueOrDefault("AccountName") is { Length: > 0 } n) a.AccountName = n;
        a.AccountNameEn = before.GetValueOrDefault("AccountNameEn");
        a.Description = before.GetValueOrDefault("Description");
        if (Enum.TryParse<AccountType>(before.GetValueOrDefault("AccountType"), out var t)) a.AccountType = t;
        a.UpdatedAt = DateTime.UtcNow;
        return true;
    }
}

/// <summary>โปรเจกต์ — รหัสซ้ำของเดิม ⇒ ข้าม (writer เดิมไม่รองรับการอัปเดต)</summary>
public sealed class ProjectsImportTarget : MasterDataImportTarget
{
    public ProjectsImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "projects";
    public override string Label => "โปรเจกต์";
    public override bool SupportsUpdate => false;
    protected override string? InFileKey(ImportTargetRow row) => V(row, "Code");
    protected override string? DisplayKeyOf(ImportTargetRow row) => string.Join(" ", new[] { V(row, "Code"), V(row, "Name") }.Where(x => x != null));

    protected override async Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var used = await Db.Documents.IgnoreQueryFilters().AnyAsync(d => d.CompanyId == companyId && d.ProjectId == entityId, ct);
        return used ? ("มีเอกสารผูกกับโปรเจกต์นี้แล้ว", "ปิดโปรเจกต์ที่หน้าโปรเจกต์แทนการลบ") : null;
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var p = await Db.Projects.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (p == null) return false;
        SoftDelete(p);
        return true;
    }
}

/// <summary>พนักงาน — รหัส/เลขบัตรตรงของเดิม ⇒ ข้าม · เลขบัตรประชาชน checksum ไม่ผ่าน = คำเตือน (ไม่บล็อก — บัตรต่างด้าว/เลขชั่วคราวมีจริง)</summary>
public sealed class EmployeesImportTarget : MasterDataImportTarget
{
    public EmployeesImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "employees";
    public override string Label => "พนักงาน";
    public override bool SupportsUpdate => false;
    protected override string? InFileKey(ImportTargetRow row) => V(row, "EmployeeCode");
    // เลขบัตรประชาชน: แคตตาล็อกเป็น TaxId (checksum ไม่ผ่าน = ทิ้งค่า) — พนักงานต่างด้าว/เลขชั่วคราวมีจริง ⇒ เก็บเป็นเลขอ้างอิง + คำเตือนที่ Check (ไม่บล็อก ไม่ทิ้ง)
    protected override IReadOnlyDictionary<string, ImportFieldType>? TypeOverrides { get; } =
        new Dictionary<string, ImportFieldType>(StringComparer.OrdinalIgnoreCase) { ["CitizenId"] = ImportFieldType.Reference };
    // ป้ายแถวห้ามมีเลขบัตร/เลขบัญชี (DisplayKey ไม่เข้ารหัส — PDPA ม.26)
    protected override string? DisplayKeyOf(ImportTargetRow row)
        => string.Join(" ", new[] { V(row, "EmployeeCode"), V(row, "FirstNameTh"), V(row, "LastNameTh") }.Where(x => x != null));

    protected override void Check(ImportTargetRow row, List<ImportIssueDto> issues, Dictionary<string, string?> filled)
    {
        if (V(row, "CitizenId") is not { } raw) return;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        var check = ThaiTaxIdValidator.Check(digits);
        if (!check.IsValid)
            issues.Add(Issue("CitizenId", "citizenid-checksum", ImportIssueSeverity.Warning,
                $"เลขบัตรประชาชนไม่ผ่านการตรวจ ({check.Reason}) — ตรวจกับบัตรจริง (ใช้ยื่น ภ.ง.ด.1/สปส.) · ลงได้ถ้าเป็นเลขบัตรต่างด้าว/เลขชั่วคราว",
                ruleCode: "PDPA-26-CITIZENID"));
    }

    protected override async Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var paid = await Db.PayrollDetails.IgnoreQueryFilters().AnyAsync(d => d.CompanyId == companyId && d.EmployeeId == entityId, ct);
        return paid ? ("พนักงานนี้อยู่ในรอบเงินเดือนแล้ว", "ตั้งสถานะลาออกที่หน้าพนักงานแทนการลบ") : null;
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var e = await Db.Employees.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (e == null) return false;
        SoftDelete(e);
        return true;
    }
}

/// <summary>งบประมาณ — แถว = บรรทัดบัญชีของงบ (ชื่อ + ปี) · ย้อนที่หน้างบประมาณ (บรรทัดของงบเดิมแยกจากไฟล์ไม่ได้)</summary>
public sealed class BudgetsImportTarget : MasterDataImportTarget
{
    public BudgetsImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "budgets";
    public override string Label => "งบประมาณ";
    public override bool SupportsUpdate => false;
    protected override bool Resolves => false;
    protected override string? InFileKey(ImportTargetRow row)
        => V(row, "BudgetName") is { } n && V(row, "AccountCode") is { } a ? $"{n}|{V(row, "FiscalYear")}|{a}" : null;
    protected override string? DisplayKeyOf(ImportTargetRow row)
        => string.Join(" ", new[] { V(row, "BudgetName"), V(row, "FiscalYear"), V(row, "AccountCode") }.Where(x => x != null));
    protected override string? GroupKeyOf(ImportTargetRow row) => $"{V(row, "BudgetName")}|{V(row, "FiscalYear")}";

    protected override Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
        => Task.FromResult<(string Reason, string WayForward)?>(("บรรทัดงบประมาณที่นำเข้ารวมอยู่ในงบเดียวกับบรรทัดอื่น", "แก้/ลบบรรทัดที่หน้างบประมาณ"));

    protected override Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// สมุดรายวัน — แถว = บรรทัด · ใบเดียวกัน = วันที่ + คำอธิบาย + อ้างอิงเดียวกัน (คีย์เดียวกับ writer เดิม) · ใบที่เดบิต ≠ เครดิต = <b>บล็อก</b>
/// (§2.5 · ทางไปต่อ: แก้ยอด/เพิ่มบรรทัดผลต่างในไฟล์) · ลงเป็นร่าง · ย้อนได้เฉพาะใบที่ยังเป็นร่าง
/// </summary>
public sealed class JournalEntriesImportTarget : MasterDataImportTarget, IImportSequenceNumberingTarget, IImportGroupIntegrityTarget
{
    public JournalEntriesImportTarget(AccountingDbContext db, IImportRowWriter writer, IImportEntityResolver resolver) : base(db, writer, resolver) { }

    public override string EntityType => "journal-entries";
    public override string Label => "สมุดรายวัน";
    public override bool SupportsUpdate => false;
    protected override bool Resolves => false;
    protected override string? InFileKey(ImportTargetRow row) => null;
    protected override string? DisplayKeyOf(ImportTargetRow row)
        => string.Join(" ", new[] { V(row, "Date"), V(row, "Reference") ?? V(row, "Description"), V(row, "AccountCode") }.Where(x => x != null));
    protected override string? GroupKeyOf(ImportTargetRow row) => $"{V(row, "Date")}|{V(row, "Description")}|{V(row, "Reference")}";

    protected override void CheckBatch(IReadOnlyList<ImportTargetRow> rows, IReadOnlyDictionary<Guid, List<ImportIssueDto>> issues)
    {
        // แถวที่ตั้ง "ข้าม" ไม่ถูกลง ⇒ ไม่นับในยอดของใบ (ฝ่ายค้านรอบ ETL — ตรวจชุดเดียวกับที่จะลงจริง)
        foreach (var g in rows.Where(r => r.Action != ImportRowAction.Skip).GroupBy(GroupKeyOf))
        {
            var dr = g.Sum(r => Amount(r, "DebitAmount") ?? 0m);
            var cr = g.Sum(r => Amount(r, "CreditAmount") ?? 0m);
            foreach (var r in g)
            {
                var d = Amount(r, "DebitAmount") ?? 0m;
                var c = Amount(r, "CreditAmount") ?? 0m;
                if (d == 0m && c == 0m)
                    issues[r.RowId].Add(Issue("DebitAmount", "je-no-amount", ImportIssueSeverity.Error,
                        "บรรทัดนี้ไม่มียอดเดบิตหรือเครดิต — ใส่ยอด หรือตัดแถวนี้ออก"));
                else if (d != 0m && c != 0m)
                    issues[r.RowId].Add(Issue("DebitAmount", "je-both-sides", ImportIssueSeverity.Warning,
                        "บรรทัดเดียวมีทั้งเดบิตและเครดิต — ตรวจว่าไม่ได้ใส่ยอดผิดคอลัมน์"));
            }
            if (dr != cr)
                foreach (var r in g)
                    issues[r.RowId].Add(Issue("DebitAmount", "je-unbalanced", ImportIssueSeverity.Error,
                        $"ใบสำคัญนี้ (วันที่/คำอธิบาย/อ้างอิงเดียวกัน {g.Count()} บรรทัด) เดบิต {dr:N2} ≠ เครดิต {cr:N2} (ต่าง {Math.Abs(dr - cr):N2}) — "
                        + "แก้ยอดในไฟล์ หรือเพิ่มบรรทัดผลต่าง (เช่น บัญชีผลต่างยกมา) แล้ววิเคราะห์ใหม่",
                        ruleCode: "JE-BALANCE"));
        }
    }

    protected override async Task<(string Reason, string WayForward)?> DeleteBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var je = await Db.JournalEntries.AsNoTracking().Where(j => j.Id == entityId && j.CompanyId == companyId)
            .Select(j => new { j.Status, j.EntryNumber }).FirstOrDefaultAsync(ct);
        if (je == null || je.Status == JournalEntryStatus.Draft) return null;
        return ($"สมุดรายวัน {je.EntryNumber} ลงบัญชีแล้ว", "กลับรายการที่หน้าสมุดรายวัน");
    }

    protected override async Task<bool> SoftDeleteAsync(Guid companyId, Guid entityId, CancellationToken ct)
    {
        var je = await Db.JournalEntries.FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (je == null || je.Status != JournalEntryStatus.Draft) return false;
        SoftDelete(je);
        return true;
    }

    // ── ฝ่ายค้านรอบ ETL (P1-1): บรรทัดที่ writer ต่อเข้าใบเดิมที่มีอยู่ก่อนชุดนี้ (ใบที่ผู้ใช้ทำเอง/ชุดก่อน) เก็บ id บรรทัดไว้ใน BeforeJson
    //    (<c>ImportExportService.JournalAppendedLineKey</c>) ⇒ ย้อน = ลบเฉพาะบรรทัดนั้น ไม่ลบทั้งใบของผู้ใช้ (เดิมคืน existing.Id แบบไม่มี BeforeJson
    //    ⇒ ย้อนการนำเข้า = ลบใบทั้งใบรวมบรรทัดที่ผู้ใช้คีย์เอง) ──

    protected override bool CanRestore => true;

    protected override Task<(string Reason, string WayForward)?> RestoreBlockAsync(Guid companyId, Guid entityId, CancellationToken ct)
        => DeleteBlockAsync(companyId, entityId, ct);

    protected override async Task<bool> RestoreAsync(Guid companyId, Guid entityId, Dictionary<string, string?> before, CancellationToken ct)
    {
        if (!Guid.TryParse(before.GetValueOrDefault(ImportExportService.JournalAppendedLineKey), out var lineId)) return false;
        var je = await Db.JournalEntries.Include(j => j.Lines).FirstOrDefaultAsync(x => x.Id == entityId && x.CompanyId == companyId, ct);
        if (je == null || je.Status != JournalEntryStatus.Draft) return false;
        var line = je.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return true;   // ถูกลบไปแล้ว — ผลเหมือนกัน
        je.Lines.Remove(line);
        Db.JournalEntryLines.Remove(line);
        je.TotalDebit = je.Lines.Sum(l => l.DebitAmount);
        je.TotalCredit = je.Lines.Sum(l => l.CreditAmount);
        je.UpdatedAt = DateTime.UtcNow;
        return true;
    }
}
