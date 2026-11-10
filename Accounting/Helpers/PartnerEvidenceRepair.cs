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
/// </summary>
public static class PartnerEvidenceRepair
{
    public const string MarkerKey = "r257-3-partner-evidence-reset";
    public const string AuditEntityType = "PartnerEvidenceRepair";
    public const string Note = "[R257-3] คำยืนยัน \"ได้รับต้นฉบับแล้ว\" เกิดก่อนผู้ขายส่งใบกำกับภาษี (เป็นของใบแจ้งหนี้) — หลักฐานกลับเป็น \"ต้องได้ต้นฉบับ\" · ยืนยันต้นฉบับใบกำกับอีกครั้ง";

    /// <summary>คำยืนยันเกิดก่อนแถวกล่องเข้าของใบที่ทำหน้าที่ใบกำกับ<b>ทุกแถว</b>ไหม · ไม่มีคำยืนยัน/ไม่มีใบกำกับ = false · pure</summary>
    internal static bool ConfirmedBeforeTaxInvoice(DateTime? confirmedAt, IReadOnlyCollection<DateTime> taxInvoiceLinkCreatedAt)
        => confirmedAt is DateTime c && taxInvoiceLinkCreatedAt.Count > 0 && c < taxInvoiceLinkCreatedAt.Min();

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
                && a.NewValues != null && a.NewValues.Contains(PartnerExchangeRules.RuleCode) && !a.NewValues.Contains("PartnerExternalTaxInvoiceConfirmed"))
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
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return drafts.Count;
    }
}
