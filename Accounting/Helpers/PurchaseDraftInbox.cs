using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ทางเข้าของใบซื้อร่าง (ลำดับตัดสิน: คู่ค้า › LINE › ไฟล์ e-Tax XML › สแกน OCR › สร้างเอง)</summary>
public enum PurchaseDraftSource { Partner, Line, EtaxXml, Ocr, Manual }

/// <summary>
/// รอบ 260-3 (ทีม E-M5 ต่อ · คำตัดสิน 260-3-5) — <b>กล่อง "ใบซื้อร่างรอตรวจ" กล่องเดียว</b>: ใบฝั่งซื้อสถานะร่างทุกใบของบริษัท แยกตามทางเข้า
/// (สแกน OCR · LINE · ไฟล์ e-Tax XML · คู่ค้า NextAcc · สร้างเอง) — ตัวเลขนับกับรายการมาจาก query เดียว (นับ = แถวจริง ไม่มีตัวนับที่สอง)
/// <para>ใบในกล่อง = <c>Documents</c> ที่ <c>Status = Draft</c> + ชนิดฝั่งซื้อ (<see cref="DocumentPermissionHelper.PurchaseTypes"/>) + ไม่ถูกลบ + ชั้นความลับที่ผู้ดูเห็น ·
/// ทางเข้าจัดด้วย <see cref="Classify"/> ตัวเดียว (pure) · tenant ทุก query</para>
/// <para>หน้าเดิมของแต่ละทางเข้า<b>ไม่ใช่กล่องรอตรวจอีกต่อไป</b>: review-queue = คิวสอนระบบ OCR · partners = หลักฐานภาษีซื้อของใบจากคู่ค้า (ทุกสถานะ) —
/// ลิงก์ <see cref="Href"/> พาไปที่นั่นเฉพาะงานของทางเข้านั้น</para>
/// </summary>
public static class PurchaseDraftInbox
{
    public const int DefaultLimit = 200;

    public sealed record Row(Guid DocumentId, string DocumentNumber, string DocumentType, string? ContactName, DateTime DocumentDate,
        decimal TotalAmount, string Source, string SourceLabel, string? FileName, string OpenHref, string? SourceHref, string? SourceHrefLabel);

    public sealed record SourceCount(string Source, string Label, int Count, string? Href, string? HrefLabel);

    /// <param name="Truncated">รายการตัดที่ <c>limit</c> แต่ตัวนับนับครบทุกใบ</param>
    public sealed record Result(int Total, IReadOnlyList<SourceCount> Counts, IReadOnlyList<Row> Items, bool Truncated);

    /// <summary>pure — ทางเข้าของใบร่างหนึ่งใบ · <paramref name="scanMetadataJson"/>/<paramref name="scanEngine"/> = ของสแกนล่าสุดที่สร้างใบ (null = ไม่ได้มาจากสแกน)</summary>
    public static PurchaseDraftSource Classify(bool hasPartnerLink, bool fromScan, string? scanMetadataJson, string? scanEngine)
    {
        if (hasPartnerLink) return PurchaseDraftSource.Partner;
        if (!fromScan) return PurchaseDraftSource.Manual;
        if (IsLineUpload(scanMetadataJson)) return PurchaseDraftSource.Line;
        if (string.Equals(scanEngine, OcrHeaderVatEvidence.SignedXmlEngine, StringComparison.Ordinal)) return PurchaseDraftSource.EtaxXml;
        return PurchaseDraftSource.Ocr;
    }

    /// <summary>metadata ที่ <c>LineBotService</c> ฝังกับสแกน (<c>{"sourceChannel":"line","lineUserId":…}</c>) — อ่านอย่างเดียว · JSON เสีย = ไม่ใช่ LINE</summary>
    private static bool IsLineUpload(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("sourceChannel", out var ch) && ch.ValueKind == JsonValueKind.String
                && string.Equals(ch.GetString(), "line", StringComparison.OrdinalIgnoreCase)) return true;
            return doc.RootElement.TryGetProperty("lineUserId", out var lu) && lu.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lu.GetString());
        }
        catch (JsonException) { return false; }
    }

    public static string Label(PurchaseDraftSource s) => s switch
    {
        PurchaseDraftSource.Partner => "จากคู่ค้าที่ใช้ NextAcc",
        PurchaseDraftSource.Line => "ส่งมาทาง LINE",
        PurchaseDraftSource.EtaxXml => "ไฟล์ e-Tax (XML)",
        PurchaseDraftSource.Ocr => "จากสแกน (OCR)",
        _ => "สร้างเอง/นำเข้า",
    };

    /// <summary>งานเฉพาะของทางเข้า (ไม่ใช่กล่องที่สอง) · null = ไม่มีงานเฉพาะ (เปิดใบตรวจ/อนุมัติได้เลย)</summary>
    public static (string? Href, string? Label) Href(PurchaseDraftSource s) => s switch
    {
        PurchaseDraftSource.Partner => ("/pages/partners.html#ptDraftBody", "หลักฐานภาษีซื้อจากคู่ค้า"),
        PurchaseDraftSource.Line or PurchaseDraftSource.EtaxXml or PurchaseDraftSource.Ocr => ("/pages/review-queue.html", "คิวสอนระบบ OCR"),
        _ => (null, null),
    };

    private static string OpenHref(Guid documentId) => "/pages/documents.html?editDoc=" + documentId;

    /// <summary>pure — นับต่อทางเข้า (ทุกทางเข้าออกเสมอ แม้ 0 · ลำดับตาม enum) ⇒ ผลรวม = จำนวนแถว</summary>
    private static IReadOnlyList<SourceCount> CountBySource(IEnumerable<PurchaseDraftSource> sources)
    {
        var bySource = sources.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return Enum.GetValues<PurchaseDraftSource>().Select(s =>
        {
            var (href, hrefLabel) = Href(s);
            return new SourceCount(s.ToString(), Label(s), bySource.GetValueOrDefault(s), href, hrefLabel);
        }).ToList();
    }

    /// <summary>อ่านกล่อง · <paramref name="visibleSensitivity"/> = ชั้นความลับที่ผู้ดูเห็น (ใบชั้น None เห็นเสมอ) · tenant</summary>
    public static async Task<Result> LoadAsync(AccountingDbContext db, Guid companyId, IReadOnlySet<SensitivityKind> visibleSensitivity,
        int limit = DefaultLimit, CancellationToken ct = default)
    {
        var types = DocumentPermissionHelper.PurchaseTypes.ToList();
        var kinds = visibleSensitivity.ToList();
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status == DocumentStatus.Draft && types.Contains(d.DocumentType)
                && (d.Sensitivity == SensitivityKind.None || kinds.Contains(d.Sensitivity)))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.ContactId, d.DocumentDate, d.TotalAmount, HasPartner = d.PartnerSourceLinkId != null })
            .ToListAsync(ct);
        if (docs.Count == 0)
            return new Result(0, CountBySource(Array.Empty<PurchaseDraftSource>()), Array.Empty<Row>(), false);

        var docIds = docs.Select(d => d.Id).ToList();
        var scans = (await db.Set<OcrScanResult>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.CreatedDocumentId != null && docIds.Contains(r.CreatedDocumentId.Value))
                .Select(r => new { DocId = r.CreatedDocumentId!.Value, r.ExternalMetadataJson, r.OcrEngine, r.OriginalFileName, r.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(r => r.DocId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First());
        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var names = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var classified = docs.Select(d =>
        {
            scans.TryGetValue(d.Id, out var sc);
            return (Doc: d, Scan: sc, Source: Classify(d.HasPartner, sc != null, sc?.ExternalMetadataJson, sc?.OcrEngine));
        }).ToList();
        var rows = classified.Take(Math.Clamp(limit, 1, 1000)).Select(x =>
        {
            var (href, hrefLabel) = Href(x.Source);
            return new Row(x.Doc.Id, x.Doc.DocumentNumber, x.Doc.DocumentType.ToString(), names.GetValueOrDefault(x.Doc.ContactId), x.Doc.DocumentDate,
                x.Doc.TotalAmount, x.Source.ToString(), Label(x.Source), x.Scan?.OriginalFileName, OpenHref(x.Doc.Id), href, hrefLabel);
        }).ToList();
        return new Result(classified.Count, CountBySource(classified.Select(x => x.Source)), rows, rows.Count < classified.Count);
    }
}
