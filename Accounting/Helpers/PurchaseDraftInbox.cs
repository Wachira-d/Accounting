using System.Linq.Expressions;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ทางเข้าของใบซื้อร่าง (ลำดับตัดสิน: คู่ค้า › LINE › ไฟล์ e-Tax XML › สแกน OCR › สร้างเอง)</summary>
public enum PurchaseDraftSource { Partner, Line, EtaxXml, Ocr, Manual }

/// <summary>
/// รอบ 260-3 (ทีม E-M5 ต่อ · คำตัดสิน 260-3-5/260-3-7/260-3-8) — <b>กล่อง "ใบซื้อร่างรอตรวจ" กล่องเดียว</b>: ใบฝั่งซื้อสถานะร่างของบริษัท แยกตามทางเข้า
/// (สแกน OCR · LINE · ไฟล์ e-Tax XML · คู่ค้า NextAcc · สร้างเอง)
/// <para>ทางเข้าของใบนิยามครั้งเดียวเป็นเงื่อนไข SQL (<see cref="WhereSource"/>) — ใช้ทั้ง<b>ตัวนับ</b> (นับครบใน SQL ไม่โหลดใบทั้งหมด · ฝ่ายค้าน L5) และ
/// <b>ตัวกรองรายการฝั่งเซิร์ฟเวอร์ก่อนตัดจำนวน</b> (ฝ่ายค้าน M3: ตัวนับบอก N แต่รายการที่ตัดแล้วไม่มีใบของทางเข้านั้น) · ป้ายของแถวใช้ <see cref="Classify"/>
/// ลำดับเดียวกัน (Db test ล็อกว่าแถวทุกแถวในตัวกรองทางเข้า X ได้ป้าย X)</para>
/// <para>ใบในกล่อง = <c>Documents</c> ที่ <c>Status = Draft</c> + ชนิดใน <see cref="InboxTypes"/> (ฝั่งซื้อ ยกเว้นใบขอซื้อ/ใบสั่งซื้อ — 260-3-8) + ไม่ถูกลบ +
/// ชั้นความลับที่ผู้ดูเห็น · tenant ทุก query · หน้า review-queue = คิวสอนระบบ OCR · partners = หลักฐานภาษีซื้อของใบจากคู่ค้า (ลิงก์งานเฉพาะทางต่อแถว)</para>
/// </summary>
public static class PurchaseDraftInbox
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    /// <summary>คีย์ใน metadata ที่ <c>LineBotService</c> ฝังกับสแกน (<c>{"sourceChannel":"line","lineUserId":…}</c>) — ค้นแบบข้อความใน SQL</summary>
    public const string LineChannelMarker = "\"sourceChannel\":\"line\"";
    public const string LineUserMarker = "\"lineUserId\"";

    /// <summary>ชนิดในกล่อง — ฝั่งซื้อ (<see cref="DocumentPermissionHelper.PurchaseTypes"/>) ยกเว้นใบขอซื้อ/ใบสั่งซื้อ (เอกสารภายในก่อนซื้อ ไม่ใช่ใบที่ต้องตรวจจากผู้ขาย —
    /// ดูที่แท็บของหน้า purchases เดิม · 260-3-8)</summary>
    public static IReadOnlyList<DocumentType> InboxTypes { get; } = DocumentPermissionHelper.PurchaseTypes
        .Where(t => t is not (DocumentType.PurchaseRequisition or DocumentType.PurchaseOrder)).ToList();

    public sealed record Row(Guid DocumentId, string DocumentNumber, string DocumentType, string? ContactName, DateTime DocumentDate,
        decimal TotalAmount, string Source, string SourceLabel, string? FileName, string OpenHref, string? SourceHref, string? SourceHrefLabel);

    public sealed record SourceCount(string Source, string Label, int Count, string? Href, string? HrefLabel);

    /// <param name="Total">ทุกทางเข้า</param>
    /// <param name="Source">ทางเข้าที่กรองรายการ (null = ทั้งหมด)</param>
    /// <param name="Truncated">รายการของตัวกรองนี้ถูกตัดที่ limit (ตัวนับนับครบเสมอ)</param>
    public sealed record Result(int Total, IReadOnlyList<SourceCount> Counts, IReadOnlyList<Row> Items, bool Truncated, string? Source = null, int Limit = DefaultLimit);

    /// <summary>pure — ทางเข้าจากข้อเท็จจริงของใบ (ลำดับเดียวกับ <see cref="WhereSource"/>)</summary>
    public static PurchaseDraftSource Classify(bool hasPartnerLink, bool fromScan, bool lineScan, bool etaxScan)
    {
        if (hasPartnerLink) return PurchaseDraftSource.Partner;
        if (lineScan) return PurchaseDraftSource.Line;
        if (etaxScan) return PurchaseDraftSource.EtaxXml;
        return fromScan ? PurchaseDraftSource.Ocr : PurchaseDraftSource.Manual;
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

    /// <summary>pure — แปลงค่า <c>?source=</c> (ชื่อ enum · ไม่สนตัวพิมพ์) · ว่าง = null (ทั้งหมด) · ค่าที่ไม่รู้จัก = false</summary>
    public static bool TryParseSource(string? raw, out PurchaseDraftSource? source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (Enum.TryParse<PurchaseDraftSource>(raw.Trim(), ignoreCase: true, out var s) && Enum.IsDefined(s)) { source = s; return true; }
        return false;
    }

    /// <summary>ใบร่างในกล่อง (ก่อนแยกทางเข้า) · tenant</summary>
    private static IQueryable<Document> Base(AccountingDbContext db, Guid companyId, IReadOnlySet<SensitivityKind> visibleSensitivity)
    {
        var types = InboxTypes.ToList();
        var kinds = visibleSensitivity.ToList();
        return db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status == DocumentStatus.Draft && types.Contains(d.DocumentType)
                && (d.Sensitivity == SensitivityKind.None || kinds.Contains(d.Sensitivity)));
    }

    /// <summary>เงื่อนไขทางเข้า (SQL) — <b>นิยามเดียว</b>ของตัวนับและตัวกรอง · ลำดับ คู่ค้า › LINE › e-Tax XML › OCR › สร้างเอง</summary>
    private static IQueryable<Document> WhereSource(AccountingDbContext db, Guid companyId, IQueryable<Document> q, PurchaseDraftSource source)
    {
        var scans = db.Set<OcrScanResult>().AsNoTracking().Where(r => r.CompanyId == companyId && !r.IsDeleted && r.CreatedDocumentId != null);
        Expression<Func<Document, bool>> partner = d => d.PartnerSourceLinkId != null;
        Expression<Func<Document, bool>> anyScan = d => scans.Any(r => r.CreatedDocumentId == d.Id);
        Expression<Func<Document, bool>> line = d => scans.Any(r => r.CreatedDocumentId == d.Id && r.ExternalMetadataJson != null
            && (r.ExternalMetadataJson.Contains(LineChannelMarker) || r.ExternalMetadataJson.Contains(LineUserMarker)));
        Expression<Func<Document, bool>> etax = d => scans.Any(r => r.CreatedDocumentId == d.Id && r.OcrEngine == OcrHeaderVatEvidence.SignedXmlEngine);
        return source switch
        {
            PurchaseDraftSource.Partner => q.Where(partner),
            PurchaseDraftSource.Line => q.Where(d => d.PartnerSourceLinkId == null).Where(line),
            PurchaseDraftSource.EtaxXml => q.Where(d => d.PartnerSourceLinkId == null).Where(Not(line)).Where(etax),
            PurchaseDraftSource.Ocr => q.Where(d => d.PartnerSourceLinkId == null).Where(Not(line)).Where(Not(etax)).Where(anyScan),
            _ => q.Where(d => d.PartnerSourceLinkId == null).Where(Not(anyScan)),
        };
    }

    private static Expression<Func<Document, bool>> Not(Expression<Func<Document, bool>> e)
        => Expression.Lambda<Func<Document, bool>>(Expression.Not(e.Body), e.Parameters);

    /// <summary>อ่านกล่อง · <paramref name="visibleSensitivity"/> = ชั้นความลับที่ผู้ดูเห็น (ใบชั้น None เห็นเสมอ) · <paramref name="source"/> = กรองรายการก่อนตัดจำนวน · tenant</summary>
    public static async Task<Result> LoadAsync(AccountingDbContext db, Guid companyId, IReadOnlySet<SensitivityKind> visibleSensitivity,
        PurchaseDraftSource? source = null, int limit = DefaultLimit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var baseQ = Base(db, companyId, visibleSensitivity);
        var counts = new List<SourceCount>();
        foreach (var s in Enum.GetValues<PurchaseDraftSource>())
        {
            var (href, hrefLabel) = Href(s);
            counts.Add(new SourceCount(s.ToString(), Label(s), await WhereSource(db, companyId, baseQ, s).CountAsync(ct), href, hrefLabel));
        }
        var total = counts.Sum(c => c.Count);
        var listQ = source is { } only ? WhereSource(db, companyId, baseQ, only) : baseQ;
        var listCount = source is { } f ? counts.Single(c => c.Source == f.ToString()).Count : total;

        var scans = db.Set<OcrScanResult>().AsNoTracking().Where(r => r.CompanyId == companyId && !r.IsDeleted && r.CreatedDocumentId != null);
        var rows = await listQ
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, ContactName = d.Contact.Name, d.DocumentDate, d.TotalAmount,
                HasPartner = d.PartnerSourceLinkId != null,
                FromScan = scans.Any(r => r.CreatedDocumentId == d.Id),
                LineScan = scans.Any(r => r.CreatedDocumentId == d.Id && r.ExternalMetadataJson != null
                    && (r.ExternalMetadataJson.Contains(LineChannelMarker) || r.ExternalMetadataJson.Contains(LineUserMarker))),
                EtaxScan = scans.Any(r => r.CreatedDocumentId == d.Id && r.OcrEngine == OcrHeaderVatEvidence.SignedXmlEngine),
                FileName = scans.Where(r => r.CreatedDocumentId == d.Id).OrderByDescending(r => r.CreatedAt).Select(r => r.OriginalFileName).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = rows.Select(x =>
        {
            var src = Classify(x.HasPartner, x.FromScan, x.LineScan, x.EtaxScan);
            var (href, hrefLabel) = Href(src);
            return new Row(x.Id, x.DocumentNumber, x.DocumentType.ToString(), x.ContactName, x.DocumentDate, x.TotalAmount,
                src.ToString(), Label(src), x.FileName, "/pages/documents.html?editDoc=" + x.Id, href, hrefLabel);
        }).ToList();
        return new Result(total, counts, items, items.Count < listCount, source?.ToString(), limit);
    }
}
