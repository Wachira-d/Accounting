using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **รอบ 257 ชุด 3 ฝ่ายค้าน M2 — ซ่อมค่าที่ persist ก่อนแก้ D-H4** (F2 ข้อ 9): ใบซื้อจากคู่ค้าที่ติด <c>PaperConfirmed</c>/<c>EtaxVerified</c> โดยคำยืนยัน
/// (audit ของ "ได้รับต้นฉบับแล้ว") เกิด<b>ก่อน</b>แถวกล่องเข้าของใบที่ทำหน้าที่ใบกำกับแถวแรก ⇒ คำยืนยันนั้นเป็นของใบแจ้งหนี้ ไม่ใช่ต้นฉบับใบกำกับ ·
/// <list type="bullet">
/// <item>ใบที่ยังไม่อนุมัติ ⇒ งานครั้งเดียว (<see cref="RunAsync"/> · marker <see cref="MarkerKey"/> + <c>pg_advisory_xact_lock</c>) ตั้งกลับ <c>PaperRequired</c> + หมายเหตุภายใน + audit + กระดิ่งถึงเจ้าของบริษัท</item>
/// <item>ใบที่อนุมัติแล้ว ⇒ <b>ไม่กลับรายการเอง</b> (VAT อาจเข้างวดที่ยื่นแล้ว — แนวข้อ 20/44) · ลิสต์ในรายงานตรวจข้อมูล (<c>DataIntegrityReport.PartnerConfirmedBeforeTaxInvoice</c>) ให้นักบัญชีตัดสิน</item>
/// </list>
/// ไม่มี audit การยืนยัน (ข้อมูลเก่ากว่า audit) = ไม่รู้ ⇒ ไม่แตะ (ไม่เดา — F2 ข้อ 3)
/// <para><b>รอบ 257 ปิดท้าย MED-5</b> — กลุ่มที่สอง (<see cref="FindNoCarrierAsync"/>): ใบที่ติด <c>PaperConfirmed</c>/<c>EtaxVerified</c> ทั้งที่สายของใบ
/// <b>ไม่มีเอกสารที่ทำหน้าที่ใบกำกับเลย</b> (ทุกแถวกล่องเข้าเป็นใบแจ้งหนี้) และไม่ได้ยืนยันผ่านทาง "ได้ใบกำกับนอกระบบ" ⇒ คำยืนยันเป็นของใบแจ้งหนี้
/// (ก่อน D-H4 กดได้) และใบกำกับไม่เคยมา — กลุ่มแรกไม่เจอ (ต้องมีใบกำกับในสาย) · ใบที่ยังไม่อนุมัติ ⇒ ตั้งเป็น <c>AwaitingTaxInvoice</c> + audit + กระดิ่ง ·
/// ใบที่อนุมัติแล้ว ⇒ ลิสต์ในหมวดเดียวกันของรายงานตรวจข้อมูล · marker ใหม่ (<see cref="MarkerKey"/>) ⇒ งานรันอีกหนึ่งครั้ง (กลุ่มแรกซ้ำได้ — ใบที่ตั้งกลับแล้วไม่เข้าเกณฑ์อีก)</para>
/// </summary>
public static class PartnerEvidenceRepair
{
    /// <summary>รอบ 257 ปิดท้าย: คีย์ใหม่ (เดิม <c>r257-3-partner-evidence-reset</c>) — ให้งานรันอีกหนึ่งครั้งพร้อมกลุ่ม "ไม่มีใบกำกับในสาย"</summary>
    public const string MarkerKey = "r257-5-partner-evidence-no-carrier";
    public const string AuditEntityType = "PartnerEvidenceRepair";
    public const string Note = "[R257-3] คำยืนยัน \"ได้รับต้นฉบับแล้ว\" เกิดก่อนผู้ขายส่งใบกำกับภาษี (เป็นของใบแจ้งหนี้) — หลักฐานกลับเป็น \"ต้องได้ต้นฉบับ\" · ยืนยันต้นฉบับใบกำกับอีกครั้ง";

    /// <summary>คำยืนยันเกิดก่อนแถวกล่องเข้าของใบที่ทำหน้าที่ใบกำกับ<b>ทุกแถว</b>ไหม · ไม่มีคำยืนยัน/ไม่มีใบกำกับ = false · pure</summary>
    internal static bool ConfirmedBeforeTaxInvoice(DateTime? confirmedAt, IReadOnlyCollection<DateTime> taxInvoiceLinkCreatedAt)
        => confirmedAt is DateTime c && taxInvoiceLinkCreatedAt.Count > 0 && c < taxInvoiceLinkCreatedAt.Min();

    public const string NoCarrierNote = "[R257-5] คำยืนยัน \"ได้รับต้นฉบับแล้ว\" เป็นของใบแจ้งหนี้ (สายนี้ยังไม่มีใบกำกับภาษีของผู้ขาย) — หลักฐานกลับเป็น \"รอใบกำกับภาษีจากผู้ขาย\" · "
        + "ได้ใบกำกับนอกระบบแล้ว ให้กด \"ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ\" พร้อมเลขที่/วันที่";

    /// <summary>รอบ 257 ปิดท้าย MED-5 — ใบเข้ากลุ่ม "ยืนยันแล้วแต่สายไม่มีใบกำกับเลย" ไหม · pure ·
    /// ไม่มีแถวกล่องเข้า / หาเอกสารต้นทางไม่ครบ = ไม่รู้ ⇒ false (ไม่เดา) · ยืนยันผ่านทาง "ได้ใบกำกับนอกระบบ" = คำยืนยันของใบกำกับจริง ⇒ false</summary>
    internal static bool ConfirmedWithoutCarrier(string? evidence, int linkCount, int resolvedSourceCount, int carrierCount, bool confirmedThroughExternalPath)
        => (evidence == PartnerInputVatEvidence.PaperConfirmed || evidence == PartnerInputVatEvidence.EtaxVerified)
           && linkCount > 0 && resolvedSourceCount == linkCount && carrierCount == 0 && !confirmedThroughExternalPath;

    public sealed record NoCarrierSuspect(Guid DocumentId, Guid CompanyId, string Number, DateTime Date, DocumentStatus Status, string Evidence, decimal VatAmount);

    /// <summary>หาใบกลุ่มที่สอง (MED-5) · <paramref name="companyId"/> null = ทุกบริษัท · tenant: แถวกล่องเข้า/audit ของบริษัทเดียวกับใบ · เอกสารต้นทางของบริษัทผู้ขายตามแถว</summary>
    public static async Task<List<NoCarrierSuspect>> FindNoCarrierAsync(AccountingDbContext db, Guid? companyId, CancellationToken ct = default)
    {
        var docs = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.PartnerSourceLinkId != null && !d.IsDeleted && (companyId == null || d.CompanyId == companyId)
                && (d.PartnerInputVatEvidence == PartnerInputVatEvidence.PaperConfirmed || d.PartnerInputVatEvidence == PartnerInputVatEvidence.EtaxVerified))
            .Select(d => new { d.Id, d.CompanyId, d.DocumentNumber, d.DocumentDate, d.Status, d.PartnerInputVatEvidence, d.VatAmount })
            .ToListAsync(ct);
        if (docs.Count == 0) return new List<NoCarrierSuspect>();
        var ids = docs.Select(d => d.Id).ToList();
        var idText = ids.Select(i => i.ToString()).ToList();
        var companies = docs.Select(d => d.CompanyId).Distinct().ToList();
        var companiesN = companies.Select(c => (Guid?)c).ToList();
        var external = await db.AuditLogs.AsNoTracking()
            .Where(a => companiesN.Contains(a.CompanyId) && a.EntityType == "Document" && a.EntityId != null && idText.Contains(a.EntityId)
                && a.NewValues != null && a.NewValues.Contains(PartnerInputVatEvidence.ExternalConfirmedEvent))
            .Select(a => new { a.CompanyId, a.EntityId })
            .ToListAsync(ct);
        var links = await db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetDocumentId != null && ids.Contains(l.TargetDocumentId!.Value) && companies.Contains(l.TargetCompanyId))
            .Select(l => new { Target = l.TargetDocumentId!.Value, l.TargetCompanyId, l.SourceCompanyId, l.SourceDocumentId })
            .ToListAsync(ct);
        var sourceIds = links.Select(l => l.SourceDocumentId).Distinct().ToList();
        var sources = (await db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => sourceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.CompanyId, d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount })
                .ToListAsync(ct))
            .ToDictionary(d => d.Id);
        var result = new List<NoCarrierSuspect>();
        foreach (var d in docs)
        {
            var mine = links.Where(l => l.Target == d.Id && l.TargetCompanyId == d.CompanyId).ToList();
            var resolved = mine.Where(l => sources.TryGetValue(l.SourceDocumentId, out var s) && s.CompanyId == l.SourceCompanyId).ToList();
            var carriers = resolved.Count(l => PartnerExchangeRules.IsTaxInvoiceCarrier(sources[l.SourceDocumentId].DocumentType,
                sources[l.SourceDocumentId].IsTaxInvoiceByLaw, sources[l.SourceDocumentId].VatAmount));
            var viaExternal = external.Any(a => a.CompanyId == d.CompanyId && string.Equals(a.EntityId, d.Id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (!ConfirmedWithoutCarrier(d.PartnerInputVatEvidence, mine.Count, resolved.Count, carriers, viaExternal)) continue;
            result.Add(new NoCarrierSuspect(d.Id, d.CompanyId, d.DocumentNumber, d.DocumentDate, d.Status, d.PartnerInputVatEvidence!, d.VatAmount));
        }
        return result;
    }

    /// <summary>ใบที่ยังไม่อนุมัติ (ซ่อมเองได้) — ชุดเดียวกับที่ตัวยืนยันถือว่า "ยังไม่อนุมัติ"</summary>
    public static bool NotApproved(DocumentStatus s) => s is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected;

    public sealed record Suspect(Guid DocumentId, Guid CompanyId, string Number, DateTime Date, DocumentStatus Status, string Evidence,
        DateTime ConfirmedAt, DateTime FirstTaxInvoiceAt, decimal VatAmount);

    /// <summary>หาใบที่เข้าเกณฑ์ · <paramref name="companyId"/> null = ทุกบริษัท (งานครั้งเดียว) · tenant: audit/แถวกล่องเข้าของบริษัทเดียวกับใบ · เอกสารต้นทางของบริษัทผู้ขายตามแถว</summary>
    public static async Task<List<Suspect>> FindAsync(AccountingDbContext db, Guid? companyId, CancellationToken ct = default)
    {
        var docs = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.PartnerSourceLinkId != null && !d.IsDeleted && (companyId == null || d.CompanyId == companyId)
                && (d.PartnerInputVatEvidence == PartnerInputVatEvidence.PaperConfirmed || d.PartnerInputVatEvidence == PartnerInputVatEvidence.EtaxVerified))
            .Select(d => new { d.Id, d.CompanyId, d.DocumentNumber, d.DocumentDate, d.Status, d.PartnerInputVatEvidence, d.VatAmount })
            .ToListAsync(ct);
        if (docs.Count == 0) return new List<Suspect>();
        var ids = docs.Select(d => d.Id).ToList();
        var idText = ids.Select(i => i.ToString()).ToList();
        var companies = docs.Select(d => d.CompanyId).Distinct().ToList();
        var companiesN = companies.Select(c => (Guid?)c).ToList();   // AuditLog.CompanyId เป็น nullable
        // คำยืนยันของคน = audit ที่มีค่าเดิม (ConfirmPartnerOriginalAsync) · ไม่นับ audit แนบหลักฐาน (ไม่มีค่าเดิม) และทาง "ได้ใบกำกับนอกระบบ" (ยืนยันใบกำกับโดยตรง)
        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => companiesN.Contains(a.CompanyId) && a.EntityType == "Document" && a.EntityId != null && idText.Contains(a.EntityId)
                && a.OldValues != null && a.OldValues.Contains("inputVatEvidence")
                && a.NewValues != null && a.NewValues.Contains(PartnerExchangeRules.RuleCode) && !a.NewValues.Contains(PartnerInputVatEvidence.ExternalConfirmedEvent))
            .Select(a => new { a.CompanyId, a.EntityId, a.Timestamp })
            .ToListAsync(ct);
        var links = await db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetDocumentId != null && ids.Contains(l.TargetDocumentId!.Value) && companies.Contains(l.TargetCompanyId))
            .Select(l => new { Target = l.TargetDocumentId!.Value, l.TargetCompanyId, l.SourceCompanyId, l.SourceDocumentId, l.CreatedAt })
            .ToListAsync(ct);
        var sourceIds = links.Select(l => l.SourceDocumentId).Distinct().ToList();
        var sources = (await db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => sourceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.CompanyId, d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount })
                .ToListAsync(ct))
            .ToDictionary(d => d.Id);
        var result = new List<Suspect>();
        foreach (var d in docs)
        {
            var confirmedAt = audits.Where(a => a.CompanyId == d.CompanyId && string.Equals(a.EntityId, d.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(a => (DateTime?)a.Timestamp).Max();
            var taxLinks = links.Where(l => l.Target == d.Id && l.TargetCompanyId == d.CompanyId
                    && sources.TryGetValue(l.SourceDocumentId, out var s) && s.CompanyId == l.SourceCompanyId
                    && PartnerExchangeRules.IsTaxInvoiceCarrier(s.DocumentType, s.IsTaxInvoiceByLaw, s.VatAmount))
                .Select(l => l.CreatedAt).ToList();
            if (!ConfirmedBeforeTaxInvoice(confirmedAt, taxLinks)) continue;
            result.Add(new Suspect(d.Id, d.CompanyId, d.DocumentNumber, d.DocumentDate, d.Status, d.PartnerInputVatEvidence!,
                confirmedAt!.Value, taxLinks.Min(), d.VatAmount));
        }
        return result;
    }

    /// <summary>งานครั้งเดียว — คืนจำนวนใบที่ตั้งกลับ (marker มีแล้ว = 0) · ล้ม ⇒ ธุรกรรมย้อน ไม่มี marker ⇒ บูตถัดไปทำใหม่</summary>
    public static async Task<int> RunAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        try { return await RunCoreAsync(db, ct); }
        finally { db.ChangeTracker.Clear(); }
    }

    private static async Task<int> RunCoreAsync(AccountingDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey) }, ct);
        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql, ct);
        var markerKey = MarkerKey;
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0) { await tx.CommitAsync(ct); return 0; }

        var drafts = (await FindAsync(db, null, ct)).Where(x => NotApproved(x.Status)).ToList();
        var now = DateTime.UtcNow;
        foreach (var x in drafts)
        {
            var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == x.DocumentId && d.CompanyId == x.CompanyId, ct);
            doc.PartnerInputVatEvidence = PartnerInputVatEvidence.PaperRequired;
            doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes) ? Note : doc.InternalNotes + "\n" + Note;
            doc.UpdatedAt = now;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = x.CompanyId, UserEmail = "system", Action = AuditAction.Update, EntityType = "Document", EntityId = x.DocumentId.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { inputVatEvidence = x.Evidence }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = AuditEntityType, inputVatEvidence = PartnerInputVatEvidence.PaperRequired, confirmedAt = x.ConfirmedAt,
                    firstTaxInvoiceLinkAt = x.FirstTaxInvoiceAt, ruleCode = PartnerExchangeRules.RuleCode, legalReference = PartnerExchangeRules.LegalReference,
                }),
                Timestamp = now,
            });
            var owners = await db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == x.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
            foreach (var owner in owners.Distinct())
                db.Set<Notification>().Add(new Notification
                {
                    UserId = owner, CompanyId = x.CompanyId, Type = NotificationType.System, Channel = NotificationChannel.InApp,
                    Title = "ใบซื้อจากคู่ค้าต้องยืนยันต้นฉบับใบกำกับอีกครั้ง",
                    Message = $"ใบ {x.Number}: คำยืนยันเดิมเกิดก่อนผู้ขายส่งใบกำกับภาษี — ยืนยันต้นฉบับใบกำกับที่หน้าคู่ค้าก่อนอนุมัติ",
                    ActionUrl = "/pages/partners.html", EntityType = "Document", EntityId = x.DocumentId,
                });
        }
        // ── รอบ 257 ปิดท้าย MED-5: ยืนยันแล้วแต่สายไม่มีใบกำกับเลย (ยังไม่อนุมัติ) ⇒ รอใบกำกับจากผู้ขาย ──
        var noCarrier = (await FindNoCarrierAsync(db, null, ct)).Where(x => NotApproved(x.Status)).ToList();
        foreach (var x in noCarrier)
        {
            var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == x.DocumentId && d.CompanyId == x.CompanyId, ct);
            doc.PartnerInputVatEvidence = PartnerInputVatEvidence.AwaitingTaxInvoice;
            doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes) ? NoCarrierNote : doc.InternalNotes + "\n" + NoCarrierNote;
            doc.UpdatedAt = now;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = x.CompanyId, UserEmail = "system", Action = AuditAction.Update, EntityType = "Document", EntityId = x.DocumentId.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { inputVatEvidence = x.Evidence }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = AuditEntityType, reason = "ConfirmedWithoutTaxInvoiceCarrier", inputVatEvidence = PartnerInputVatEvidence.AwaitingTaxInvoice,
                    ruleCode = PartnerExchangeRules.RuleCode, legalReference = PartnerExchangeRules.LegalReference,
                }),
                Timestamp = now,
            });
            var owners = await db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == x.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
            foreach (var owner in owners.Distinct())
                db.Set<Notification>().Add(new Notification
                {
                    UserId = owner, CompanyId = x.CompanyId, Type = NotificationType.System, Channel = NotificationChannel.InApp,
                    Title = "ใบซื้อจากคู่ค้ายังไม่มีใบกำกับภาษีของผู้ขาย",
                    Message = $"ใบ {x.Number}: คำยืนยันเดิมเป็นของใบแจ้งหนี้ และผู้ขายยังไม่ได้ส่งใบกำกับภาษี — หลักฐานกลับเป็น \"รอใบกำกับจากผู้ขาย\" · "
                        + "ได้ใบกำกับนอกระบบแล้ว กด \"ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ\" ที่หน้าคู่ค้าก่อนอนุมัติ",
                    ActionUrl = "/pages/partners.html", EntityType = "Document", EntityId = x.DocumentId,
                });
        }
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return drafts.Count + noCarrier.Count;
    }
}
