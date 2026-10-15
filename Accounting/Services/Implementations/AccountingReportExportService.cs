using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.DTOs.Audit;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ไฟล์ส่งออก (CSV / Excel) ของรายงานบัญชี** — สมุดรายวัน · บัญชีแยกประเภท · งบทดลอง · งบแสดงฐานะการเงิน ·
/// งบกำไรขาดทุน · งบกระแสเงินสด (2026-10-14 ทีม X3 · erp-review/2026-10-14/EXPORT_AUDIT.md §X3)
///
/// <para>หลักการ: <b>ตัวเลขทุกตัวมาจาก method ของ <see cref="IAccountingService"/> ตัวเดียวกับที่หน้าเรียก</b>
/// ด้วยตัวกรองชุดเดียวกัน — ที่นี่แค่ (1) ดึงครบทุกแถว (สมุดรายวันวนทุกหน้า ไม่ตัดที่ pageSize)
/// (2) เติมชื่อที่ต้องใช้ตามรอย (เอกสารต้นทาง/คู่ค้า/สาขา/โครงการ/มิติ) ด้วยคิวรีที่กรอง CompanyId
/// (3) ส่งให้ <see cref="AccountingReportSheets"/> + <see cref="TabularExport"/> เขียนไฟล์</para>
/// </summary>
public class AccountingReportExportService
{
    /// <summary>เพดานใบสำคัญต่อไฟล์ — เกินแล้ว "ล้มดัง" บอกให้แคบช่วง ไม่ตัดเงียบ</summary>
    public const int MaxJournalEntries = 50_000;
    private const int JournalPageSize = 200;   // = เพดานของ PagedRequest

    /// <summary>เพดานแถวบันทึกการใช้งานต่อไฟล์ — เกินแล้วล้มดังให้แคบช่วง</summary>
    public const int MaxAuditRows = 100_000;

    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accounting;
    private readonly IAgingReportService? _aging;
    private readonly IAuditTrailService? _audit;
    private readonly IFixedAssetService? _assets;

    public AccountingReportExportService(AccountingDbContext db, IAccountingService accounting,
        IAgingReportService? aging = null, IAuditTrailService? audit = null, IFixedAssetService? assets = null)
    {
        _db = db;
        _accounting = accounting;
        _aging = aging;
        _audit = audit;
        _assets = assets;
    }

    private static T Need<T>(T? svc) where T : class
        => svc ?? throw new InvalidOperationException($"{typeof(T).Name} ไม่ได้ลงทะเบียน — ส่งออกรายงานนี้ไม่ได้");

    /// <summary>อายุลูกหนี้/เจ้าหนี้ — service ตัวเดียวกับหน้า aging · Excel = 2 แผ่น (รายผู้ติดต่อ + รายเอกสาร) ·
    /// CSV = แผ่นเดียวตาม <paramref name="byDocument"/></summary>
    public async Task<TabularFile> AgingAsync(Guid companyId, bool payables, DateTime? asOfDate, Guid? contactId, Guid? projectId,
        bool byDocument, TabularFormat format)
    {
        var svc = Need(_aging);
        var req = new AgingReportRequest(asOfDate, contactId, null, projectId);
        var r = payables ? await svc.GetPayableAgingAsync(companyId, req) : await svc.GetReceivableAgingAsync(companyId, req);
        var name = payables ? "อายุเจ้าหนี้" : "อายุลูกหนี้";
        var title = await TitleAsync(companyId, name, "ณ วันที่ " + ThaiDate.ToThaiDisplayString(r.AsOfDate), projectId, null, null);
        if (contactId.HasValue) title.Add("เฉพาะผู้ติดต่อที่เลือก");
        var byContact = AccountingReportSheets.AgingByContact(r, title);
        var byDoc = AccountingReportSheets.AgingByDocument(r, title);
        if (format == TabularFormat.Csv)
            return TabularExport.Render(format, name + (byDocument ? "_รายเอกสาร" : ""), null, r.AsOfDate, byDocument ? byDoc : byContact);
        return TabularExport.Render(format, name, null, r.AsOfDate, byContact, byDoc);
    }

    /// <summary>บันทึกการใช้งาน — ตัวกรองเดียวกับหน้า audit · วนทุกหน้า (หน้าเดิมส่งออกแค่ 30 แถวที่เห็น)</summary>
    public async Task<TabularFile> AuditLogsAsync(Guid companyId, DateTime? fromDate, DateTime? toDate, AuditAction? action,
        string? entityType, TabularFormat format)
    {
        var svc = Need(_audit);
        var logs = new List<AuditLogResponse>();
        var page = 1;
        while (true)
        {
            var res = await svc.GetLogsAsync(companyId, new AuditLogQueryRequest(fromDate, toDate, null, action, entityType, null, page, 1000));
            if (res.TotalCount > MaxAuditRows)
                throw new BusinessRuleException($"บันทึกตามตัวกรองมี {res.TotalCount:#,##0} แถว เกินเพดาน {MaxAuditRows:#,##0} แถวต่อไฟล์ — แคบช่วงวันที่แล้วส่งออกทีละช่วง");
            logs.AddRange(res.Items);
            if (res.Items.Count == 0 || page >= res.TotalPages) break;
            page++;
        }
        var period = fromDate.HasValue || toDate.HasValue
            ? $"ช่วง {(fromDate.HasValue ? ThaiDate.ToThaiDisplayString(fromDate.Value) : "แรกสุด")} – {(toDate.HasValue ? ThaiDate.ToThaiDisplayString(toDate.Value) : "ล่าสุด")}"
            : "ทุกช่วงวันที่";
        var title = await TitleAsync(companyId, "บันทึกการใช้งาน (Audit log)", period, null, null, null);
        if (action.HasValue) title.Add("การกระทำ: " + action.Value);
        if (!string.IsNullOrWhiteSpace(entityType)) title.Add("ประเภทข้อมูล: " + entityType);
        title.Add($"{logs.Count:#,##0} แถว");
        return TabularExport.Render(format, "บันทึกการใช้งาน", fromDate, toDate, AccountingReportSheets.AuditLogs(logs, title));
    }

    /// <summary>ทะเบียนสินทรัพย์ — รายงานตัวเดียวกับปุ่ม "รายงาน" ของหน้า fixed-assets (เดิมดูได้อย่างเดียว ไม่มีส่งออก)</summary>
    public async Task<TabularFile> AssetRegisterAsync(Guid companyId, TabularFormat format)
    {
        var r = await Need(_assets).GetAssetRegisterReportAsync(companyId);
        var title = await TitleAsync(companyId, "ทะเบียนสินทรัพย์ถาวร", "ณ วันที่ " + ThaiDate.ToThaiDisplayString(r.ReportDate), null, null, null);
        title.Add($"ใช้งาน {r.TotalActive} · จำหน่ายแล้ว {r.TotalDisposed} · ค่าเสื่อมครบ {r.TotalFullyDepreciated}");
        return TabularExport.Render(format, "ทะเบียนสินทรัพย์", null, r.ReportDate, AccountingReportSheets.AssetRegister(r, title));
    }

    /// <summary>ตารางค่าเสื่อมราคาของสินทรัพย์หนึ่งรายการ (ตัวเดียวกับที่หน้าแสดงใน modal)</summary>
    public async Task<TabularFile> DepreciationScheduleAsync(Guid companyId, Guid assetId, TabularFormat format)
    {
        var r = await Need(_assets).GetDepreciationScheduleAsync(companyId, assetId);
        var title = await TitleAsync(companyId, "ตารางค่าเสื่อมราคา", $"{r.AssetCode} {r.AssetName}", null, null, null);
        title.Add($"ราคาทุน {r.PurchaseCost:#,##0.00} · มูลค่าซาก {r.SalvageValue:#,##0.00} · อายุ {r.UsefulLifeMonths} เดือน · {r.DepreciationMethod}");
        return TabularExport.Render(format, "ตารางค่าเสื่อม_" + r.AssetCode, null, null, AccountingReportSheets.DepreciationSchedule(r, title));
    }

    public async Task<TabularFile> TrialBalanceAsync(Guid companyId, DateTime fromDate, DateTime asOfDate,
        Guid? projectId, Guid? branchId, Guid? dimensionId, TabularFormat format)
    {
        if (fromDate.Date > asOfDate.Date)
            throw new BusinessRuleException("วันที่เริ่มต้องไม่หลัง \"ณ วันที่\" ของงบทดลอง");
        // ยกไป = งบทดลองที่หน้าแสดง · ยกมา + เคลื่อนไหว = บัญชีแยกประเภทช่วงเดียวกัน (ตัวกรองเดียวกัน)
        var closing = await _accounting.GetTrialBalanceAsync(companyId, asOfDate, projectId, branchId, dimensionId);
        var gl = await _accounting.GetGeneralLedgerAsync(companyId, fromDate, asOfDate, null, dimensionId, branchId, projectId);
        var ws = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, closing, fromDate);
        var title = await TitleAsync(companyId, "งบทดลอง", $"ช่วง {ThaiDate.ToThaiDisplayString(fromDate)} – {ThaiDate.ToThaiDisplayString(asOfDate)}",
            projectId, branchId, dimensionId);
        if (ws.Rows.Any(r => r.ContinuityGap != 0m))
            title.Add("⚠ มีบัญชีที่ยอดยกมา + เคลื่อนไหว ≠ ยอดยกไป — ดูคอลัมน์ \"ผลต่างความต่อเนื่อง\" (ยอดยกมานำเข้ากลางช่วง หรือกรองมิติ)");
        return TabularExport.Render(format, "งบทดลอง", fromDate, asOfDate, AccountingReportSheets.TrialBalance(ws, title));
    }

    public async Task<TabularFile> GeneralLedgerAsync(Guid companyId, Guid userId, DateTime fromDate, DateTime toDate, Guid? accountId,
        IReadOnlyCollection<Guid>? accountIds, AccountType? accountType,
        Guid? dimensionId, Guid? branchId, Guid? projectId, TabularFormat format)
    {
        // ตัวเดียวกับหน้า (ปิดบังใบที่ผู้ใช้ไม่มีสิทธิ์เห็นแบบสมุดรายวัน — ฝ่ายค้านรอบส่งออก 2026-10-15 P2-7)
        var gl = await _accounting.GetGeneralLedgerForUserAsync(companyId, userId, fromDate, toDate, accountId, dimensionId, branchId, projectId);
        // บัญชีที่เลือก / แท็บประเภทบัญชีบนหน้า — กรองหลังคำนวณ (แต่ละบัญชีคำนวณอิสระ ⇒ ตัวเลขเท่ากับที่หน้าแสดงทุกตัว)
        var accounts = gl.Accounts
            .Where(a => accountIds == null || accountIds.Count == 0 || accountIds.Contains(a.AccountId))
            .Where(a => !accountType.HasValue || a.AccountType == accountType.Value)
            .ToList();
        gl = gl with { Accounts = accounts };
        var docIds = accounts.SelectMany(a => a.Transactions).Where(t => t.SourceDocumentId.HasValue)
            .Select(t => t.SourceDocumentId!.Value).Distinct().ToList();
        var names = await NamesAsync(companyId, docIds);
        var title = await TitleAsync(companyId, "บัญชีแยกประเภท", $"ช่วง {ThaiDate.ToThaiDisplayString(fromDate)} – {ThaiDate.ToThaiDisplayString(toDate)}",
            projectId, branchId, dimensionId);
        if (accountType.HasValue) title.Add("ประเภทบัญชี: " + PickedAccountGuard.TypeLabel(accountType.Value));
        if (accountIds is { Count: > 0 }) title.Add($"เฉพาะบัญชีที่เลือก {accounts.Count} บัญชี");
        foreach (var n in gl.Notes ?? new List<string>()) title.Add(n);   // P2-6: ยอดยกมานำเข้ากลางช่วง (ข้อความเดียวกับหน้า)
        return TabularExport.Render(format, "บัญชีแยกประเภท", fromDate, toDate, AccountingReportSheets.GeneralLedger(gl, names, title));
    }

    public async Task<TabularFile> BalanceSheetAsync(Guid companyId, DateTime asOfDate, Guid? projectId, Guid? branchId, Guid? dimensionId, TabularFormat format)
    {
        var bs = await _accounting.GetBalanceSheetAsync(companyId, asOfDate, projectId, branchId, dimensionId);
        var title = await TitleAsync(companyId, "งบแสดงฐานะการเงิน", "ณ วันที่ " + ThaiDate.ToThaiDisplayString(asOfDate), projectId, branchId, dimensionId);
        return TabularExport.Render(format, "งบแสดงฐานะการเงิน", null, asOfDate, AccountingReportSheets.BalanceSheet(bs, title));
    }

    public async Task<TabularFile> ProfitLossAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId, Guid? branchId, Guid? dimensionId, TabularFormat format)
    {
        var pl = await _accounting.GetProfitAndLossAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId);
        var title = await TitleAsync(companyId, "งบกำไรขาดทุน", $"ช่วง {ThaiDate.ToThaiDisplayString(fromDate)} – {ThaiDate.ToThaiDisplayString(toDate)}",
            projectId, branchId, dimensionId);
        return TabularExport.Render(format, "งบกำไรขาดทุน", fromDate, toDate, AccountingReportSheets.ProfitLoss(pl, title));
    }

    public async Task<TabularFile> CashFlowAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId, Guid? branchId, Guid? dimensionId, TabularFormat format)
    {
        var cf = await _accounting.GetCashFlowStatementAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId);
        var title = await TitleAsync(companyId, "งบกระแสเงินสด", $"ช่วง {ThaiDate.ToThaiDisplayString(fromDate)} – {ThaiDate.ToThaiDisplayString(toDate)}",
            projectId, branchId, dimensionId);
        return TabularExport.Render(format, "งบกระแสเงินสด", fromDate, toDate, AccountingReportSheets.CashFlow(cf, title));
    }

    /// <summary>สมุดรายวัน — หนึ่งแถวต่อบรรทัดบัญชี · <b>ทุกหน้า</b> ของรายการตามตัวกรองเดียวกับหน้า (เดิมหน้าเรียก pageSize=10000
    /// ซึ่งเซิร์ฟเวอร์ตัดเหลือ 200 เงียบ ๆ) · ใบที่ผู้ใช้ไม่มีสิทธิ์เห็นถูกปิดบังโดย service ตัวเดียวกับหน้า ·
    /// <paramref name="entryIds"/> = ส่งออกเฉพาะใบที่เลือก</summary>
    public async Task<TabularFile> JournalsAsync(Guid companyId, Guid userId, string? search, string? status, DateTime? fromDate, DateTime? toDate,
        string? journalType, Guid? dimensionId, Guid? branchId, Guid? projectId, string? tag,
        IReadOnlyCollection<Guid>? entryIds, TabularFormat format)
    {
        var entries = new List<JournalEntryResponse>();
        if (entryIds is { Count: > 0 })
        {
            if (entryIds.Count > 1000) throw new BusinessRuleException("เลือกได้ไม่เกิน 1,000 ใบต่อไฟล์ — ใช้ตัวกรองช่วงวันที่แทน");
            foreach (var id in entryIds.Distinct())
                entries.Add(await _accounting.GetJournalEntryForUserAsync(companyId, id, userId));
            entries = entries.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAt).ToList();
        }
        else
        {
            var page = 1;
            while (true)
            {
                var res = await _accounting.GetJournalEntriesForUserAsync(companyId, userId, new PagedRequest(page, JournalPageSize, search),
                    status, fromDate, toDate, journalType, dimensionId, branchId, projectId, tag);
                if (res.TotalCount > MaxJournalEntries)
                    throw new BusinessRuleException($"ใบสำคัญตามตัวกรองมี {res.TotalCount:#,##0} ใบ เกินเพดาน {MaxJournalEntries:#,##0} ใบต่อไฟล์ — แคบช่วงวันที่แล้วส่งออกทีละช่วง");
                entries.AddRange(res.Items);
                if (res.Items.Count == 0 || page >= res.TotalPages) break;
                page++;
            }
        }

        var docIds = entries.Where(e => e.SourceDocumentId.HasValue).Select(e => e.SourceDocumentId!.Value).Distinct().ToList();
        var names = await NamesAsync(companyId, docIds);
        var period = fromDate.HasValue || toDate.HasValue
            ? $"ช่วง {(fromDate.HasValue ? ThaiDate.ToThaiDisplayString(fromDate.Value) : "แรกสุด")} – {(toDate.HasValue ? ThaiDate.ToThaiDisplayString(toDate.Value) : "ล่าสุด")}"
            : "ทุกช่วงวันที่";
        var title = await TitleAsync(companyId, "สมุดรายวัน", period, projectId, branchId, dimensionId);
        if (!string.IsNullOrWhiteSpace(journalType) && Enum.TryParse<JournalType>(journalType, true, out var jt))
            title.Add("สมุด: " + AccountingReportSheets.JournalTypeLabel(jt));
        if (!string.IsNullOrWhiteSpace(status)) title.Add("สถานะ: " + status);
        if (!string.IsNullOrWhiteSpace(search)) title.Add("ค้นหา: " + search);
        if (!string.IsNullOrWhiteSpace(tag)) title.Add("แท็ก: " + tag);
        title.Add($"{entries.Count:#,##0} ใบสำคัญ");
        return TabularExport.Render(format, "สมุดรายวัน", fromDate, toDate, AccountingReportSheets.Journals(entries, names, title));
    }

    // ── ภายใน ──

    private Task<List<string>> TitleAsync(Guid companyId, string report, string period, Guid? projectId, Guid? branchId, Guid? dimensionId)
        => ReportTitleAsync(_db, companyId, report, period, projectId, branchId, dimensionId);

    /// <summary>หัวรายงานของไฟล์ส่งออก (ชื่อบริษัท + เลขผู้เสียภาษี · ชื่อรายงาน · ช่วง · ตัวกรอง · เวลาที่จัดทำ) —
    /// ตัวเดียวของไฟล์ส่งออกฝั่งเซิร์ฟเวอร์ (ทีม X5 เรียกจาก <see cref="FinanceReportExportService"/>)</summary>
    internal static async Task<List<string>> ReportTitleAsync(AccountingDbContext _db, Guid companyId, string report, string period,
        Guid? projectId, Guid? branchId, Guid? dimensionId)
    {
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync();
        var lines = new List<string>
        {
            company == null ? report : $"{company.Name} (เลขประจำตัวผู้เสียภาษี {company.TaxId})",
            report,
            period,
        };
        if (branchId.HasValue)
            lines.Add("สาขา: " + (await _db.Branches.AsNoTracking().Where(b => b.Id == branchId.Value && b.CompanyId == companyId)
                .Select(b => b.Code + " " + b.Name).FirstOrDefaultAsync() ?? "ไม่พบสาขา"));
        if (projectId.HasValue)
            lines.Add("โครงการ: " + (await _db.Projects.AsNoTracking().Where(p => p.Id == projectId.Value && p.CompanyId == companyId)
                .Select(p => p.Code + " " + p.Name).FirstOrDefaultAsync() ?? "ไม่พบโครงการ"));
        if (dimensionId.HasValue)
            lines.Add("ศูนย์ต้นทุน/มิติ: " + (await _db.AccountingDimensions.AsNoTracking().Where(d => d.Id == dimensionId.Value && d.CompanyId == companyId)
                .Select(d => d.Code + " " + d.Name).FirstOrDefaultAsync() ?? "ไม่พบมิติ"));
        lines.Add("จัดทำเมื่อ " + ThaiDate.ToThaiDisplayString(DateTime.UtcNow) + " " + DateTime.UtcNow.AddHours(7).ToString("HH:mm") + " น.");
        return lines;
    }

    private async Task<LedgerExportNames> NamesAsync(Guid companyId, IReadOnlyCollection<Guid> documentIds)
    {
        static string Label(string code, string name) => string.IsNullOrWhiteSpace(code) ? name : $"{code} {name}";
        var branches = (await _db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId)
            .Select(b => new { b.Id, b.Code, b.Name }).ToListAsync()).ToDictionary(b => b.Id, b => Label(b.Code, b.Name));
        var projects = (await _db.Projects.AsNoTracking().Where(p => p.CompanyId == companyId)
            .Select(p => new { p.Id, p.Code, p.Name }).ToListAsync()).ToDictionary(p => p.Id, p => Label(p.Code, p.Name));
        var dims = (await _db.AccountingDimensions.AsNoTracking().Where(d => d.CompanyId == companyId)
            .Select(d => new { d.Id, d.Code, d.Name }).ToListAsync()).ToDictionary(d => d.Id, d => Label(d.Code, d.Name));
        var docs = new Dictionary<Guid, LedgerSourceDocument>();
        foreach (var chunk in documentIds.Chunk(1000))
        {
            var rows = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && chunk.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, ContactName = d.Contact.Name, ContactTaxId = d.Contact.TaxId })
                .ToListAsync();
            foreach (var d in rows)
                docs[d.Id] = new LedgerSourceDocument(d.DocumentNumber, DocumentTypeNames.Title(d.DocumentType, "th"), d.ContactName, d.ContactTaxId);
        }
        return new LedgerExportNames(branches, projects, dims, docs);
    }
}
