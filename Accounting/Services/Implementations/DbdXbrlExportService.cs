using System.Text;
using System.Xml;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// DBD financial statement XBRL instance document generator (พ.ร.บ.การบัญชี
/// ม.11 + ประกาศ DBD เรื่องวิธีการยื่นงบการเงินทางอิเล็กทรอนิกส์).
///
/// Output = XBRL 2.1 instance document with TFRS-NPAEs taxonomy concepts
/// mapped from the company's Chart of Accounts → Balance Sheet + P&amp;L
/// values. ส่งเข้า DBD e-Filing (DBD XBRL Taxonomy V2 schema).
///
/// **ขอบเขต MVP:** mapping ระดับหมวด (5 ตัวเลข: Total Assets / Liabilities /
/// Equity / Revenue / Expense + Net Income) — เพียงพอสำหรับ SME ที่ใช้ผังบัญชี
/// มาตรฐาน. ถ้าต้องการ tagging ระดับ sub-account (เช่น แยก Cash/AR/Inventory
/// ตามแม่แบบ TFRS NPAEs ฉบับเต็ม) → caller สามารถ extend `BuildConceptMapping`
/// ได้ตามผังจริง.
///
/// **CPD gate (ม.7):** ต้องตั้ง `CompanySettings.BookkeeperCpdNumber` +
/// `BookkeeperName` ก่อนถึงจะ generate ได้ — กันการนำส่งงบที่ไม่มีผู้ทำบัญชี
/// รับผิดชอบ (ผู้บริหารจะรับผิดทางอาญา ถ้านำส่งโดยไม่มีผู้ทำบัญชี).
/// </summary>
public interface IDbdXbrlExportService
{
    /// <summary>Export ภาพ XBRL instance document สำหรับงบการเงินรายปี.
    /// year = ปี ค.ศ. ของรอบบัญชี (ใช้ Company.FiscalYearStartMonth กำหนด
    /// ช่วง). คืน (filename, bytes, summary) — bytes = UTF-8 XML.
    /// throw InvalidOperationException ถ้า:
    ///   • CompanySettings.BookkeeperCpdNumber ไม่มี (ม.7)
    ///   • Company.JuristicId ไม่มี (DBD ใช้เป็น key)
    ///   • ไม่มี JE Posted ในรอบปี</summary>
    Task<DbdXbrlExportResult> ExportAnnualAsync(Guid companyId, int year, CancellationToken ct = default);
}

public sealed record DbdXbrlExportResult(
    string FileName,
    string ContentType,
    byte[] FileData,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalEquity,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetIncome,
    string Summary);

public class DbdXbrlExportService : IDbdXbrlExportService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accounting;

    public DbdXbrlExportService(AccountingDbContext db, IAccountingService accounting)
    {
        _db = db;
        _accounting = accounting;
    }

    public async Task<DbdXbrlExportResult> ExportAnnualAsync(
        Guid companyId, int year, CancellationToken ct = default)
    {
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        // ── ม.7 gate — ต้องมีผู้ทำบัญชี ──
        var settings = await _db.Set<CompanySettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct);
        if (settings == null
            || string.IsNullOrWhiteSpace(settings.BookkeeperCpdNumber)
            || string.IsNullOrWhiteSpace(settings.BookkeeperName))
            throw new InvalidOperationException(
                "⛔ พ.ร.บ.การบัญชี ม.7: ต้องระบุ 'ชื่อผู้ทำบัญชี + เลขทะเบียน CPD' " +
                "ใน Settings ก่อนนำส่งงบการเงิน (ผู้บริหารรับผิดทางอาญาถ้านำส่งโดย" +
                "ไม่มีผู้ทำบัญชี). ไปที่ Settings → ผู้ทำบัญชี เพื่อตั้งค่า.");

        // ── DBD key — JuristicId ต้องมี ──
        if (string.IsNullOrWhiteSpace(company.JuristicId))
            throw new InvalidOperationException(
                "⛔ ต้องระบุเลขทะเบียนนิติบุคคล (Company.JuristicId 13 หลัก) ก่อนนำส่งงบ DBD");

        // ── หา fiscal period ──
        var fy = Accounting.Helpers.FiscalYear.RangeFor(year, company.FiscalYearStartMonth);
        var fyStart = fy.Start;
        var fyEnd = fy.EndInclusive;

        // ── โหลด BS + P&L จาก service เดิม ──
        var bs = await _accounting.GetBalanceSheetAsync(companyId, fyEnd);
        var pl = await _accounting.GetProfitAndLossAsync(companyId, fyStart, fyEnd);

        if (bs.Assets.Count == 0 && bs.Liabilities.Count == 0 && bs.Equity.Count == 0)
            throw new InvalidOperationException(
                $"ไม่มีรายการบัญชีในรอบปี {year} — ไม่สามารถ generate งบได้");

        // ── คำนวณค่ารวมที่ใช้ใน XBRL ──
        var totalAssets = bs.TotalAssets;
        var totalLiabilities = bs.TotalLiabilities;
        var totalEquity = bs.TotalEquity;
        var totalRevenue = pl.TotalRevenue;
        var totalExpenses = pl.TotalExpenses;
        var netIncome = totalRevenue - totalExpenses;

        // ── สร้าง XBRL instance document ──
        var xml = BuildXbrlInstance(company, settings, fyStart, fyEnd, year,
            totalAssets, totalLiabilities, totalEquity, totalRevenue, totalExpenses, netIncome);

        var bytes = Encoding.UTF8.GetBytes(xml);
        var fileName = $"DBD_XBRL_{company.JuristicId}_{year}.xbrl";
        var summary = $"งบการเงิน {year} (ผู้ทำบัญชี {settings.BookkeeperName} CPD #{settings.BookkeeperCpdNumber}) — " +
            $"สินทรัพย์ {totalAssets:N2}, หนี้สิน {totalLiabilities:N2}, ส่วนของเจ้าของ {totalEquity:N2}, " +
            $"รายได้ {totalRevenue:N2}, ค่าใช้จ่าย {totalExpenses:N2}, กำไรสุทธิ {netIncome:N2}";

        return new DbdXbrlExportResult(
            fileName, "application/xml", bytes,
            totalAssets, totalLiabilities, totalEquity,
            totalRevenue, totalExpenses, netIncome,
            summary);
    }

    /// <summary>
    /// Build XBRL 2.1 instance document mapping aggregate values to TFRS-NPAEs
    /// concepts. Schema reference uses DBD's published taxonomy URI.
    /// 5 fundamental concepts:
    ///   • tfrs:Assets, tfrs:Liabilities, tfrs:Equity (Balance Sheet)
    ///   • tfrs:Revenues, tfrs:Expenses, tfrs:ProfitLoss (Income Statement)
    /// Context "FY_{year}" = period ทั้งปี; entity = JuristicId 13 หลัก.
    /// </summary>
    private static string BuildXbrlInstance(
        Company company, CompanySettings settings,
        DateTime fyStart, DateTime fyEnd, int year,
        decimal assets, decimal liabilities, decimal equity,
        decimal revenue, decimal expenses, decimal netIncome)
    {
        var sb = new StringBuilder();
        var w = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        });

        // Root <xbrl> with all namespace declarations
        w.WriteStartDocument();
        w.WriteStartElement("xbrl", "http://www.xbrl.org/2003/instance");
        w.WriteAttributeString("xmlns", "xlink", null, "http://www.w3.org/1999/xlink");
        w.WriteAttributeString("xmlns", "iso4217", null, "http://www.xbrl.org/2003/iso4217");
        w.WriteAttributeString("xmlns", "tfrs", null, "http://xbrl.dbd.go.th/tfrs-npaes/2024");
        w.WriteAttributeString("xmlns", "dbd", null, "http://xbrl.dbd.go.th/common/2024");

        // Schema reference — DBD's published taxonomy schema
        w.WriteStartElement("schemaRef", "http://www.xbrl.org/2003/linkbase");
        w.WriteAttributeString("type", "http://www.w3.org/1999/xlink", "simple");
        w.WriteAttributeString("href", "http://www.w3.org/1999/xlink", "http://xbrl.dbd.go.th/tfrs-npaes/2024/tfrs-npaes-2024.xsd");
        w.WriteEndElement();

        // Header comment with filer metadata
        w.WriteComment($" Filer: {company.Name} (JuristicId {company.JuristicId}) ");
        w.WriteComment($" Bookkeeper: {settings.BookkeeperName} — CPD #{settings.BookkeeperCpdNumber} (พ.ร.บ.การบัญชี ม.7) ");
        w.WriteComment($" Fiscal Year: {year} ({fyStart:yyyy-MM-dd} → {fyEnd:yyyy-MM-dd}) ");

        // === Context: period ทั้งปี + entity ===
        var contextId = $"FY_{year}";
        w.WriteStartElement("context", "http://www.xbrl.org/2003/instance");
        w.WriteAttributeString("id", contextId);
        w.WriteStartElement("entity", "http://www.xbrl.org/2003/instance");
        w.WriteStartElement("identifier", "http://www.xbrl.org/2003/instance");
        w.WriteAttributeString("scheme", "http://xbrl.dbd.go.th/juristic-id");
        w.WriteString(company.JuristicId!);
        w.WriteEndElement();   // identifier
        w.WriteEndElement();   // entity
        w.WriteStartElement("period", "http://www.xbrl.org/2003/instance");
        w.WriteElementString("startDate", "http://www.xbrl.org/2003/instance", fyStart.ToString("yyyy-MM-dd"));
        w.WriteElementString("endDate", "http://www.xbrl.org/2003/instance", fyEnd.ToString("yyyy-MM-dd"));
        w.WriteEndElement();   // period
        w.WriteEndElement();   // context

        // === Unit: THB ===
        w.WriteStartElement("unit", "http://www.xbrl.org/2003/instance");
        w.WriteAttributeString("id", "THB");
        w.WriteElementString("measure", "http://www.xbrl.org/2003/instance", "iso4217:THB");
        w.WriteEndElement();   // unit

        // === Facts — 5 core concepts + ProfitLoss ===
        // ทุก fact ใช้ context FY_{year} + unit THB + decimals=2 (สตางค์)
        WriteMonetaryFact(w, "tfrs", "Assets", contextId, "THB", assets);
        WriteMonetaryFact(w, "tfrs", "Liabilities", contextId, "THB", liabilities);
        WriteMonetaryFact(w, "tfrs", "Equity", contextId, "THB", equity);
        WriteMonetaryFact(w, "tfrs", "Revenues", contextId, "THB", revenue);
        WriteMonetaryFact(w, "tfrs", "Expenses", contextId, "THB", expenses);
        WriteMonetaryFact(w, "tfrs", "ProfitLoss", contextId, "THB", netIncome);

        // === Filer metadata facts (DBD common namespace) ===
        WriteStringFact(w, "dbd", "EntityName", contextId, company.Name);
        WriteStringFact(w, "dbd", "BookkeeperName", contextId, settings.BookkeeperName!);
        WriteStringFact(w, "dbd", "BookkeeperCpdNumber", contextId, settings.BookkeeperCpdNumber!);

        w.WriteEndElement();   // xbrl
        w.WriteEndDocument();
        w.Flush();
        return sb.ToString();
    }

    private static void WriteMonetaryFact(XmlWriter w, string prefix, string concept,
        string contextRef, string unitRef, decimal value)
    {
        var ns = prefix == "tfrs"
            ? "http://xbrl.dbd.go.th/tfrs-npaes/2024"
            : "http://xbrl.dbd.go.th/common/2024";
        w.WriteStartElement(prefix, concept, ns);
        w.WriteAttributeString("contextRef", contextRef);
        w.WriteAttributeString("unitRef", unitRef);
        w.WriteAttributeString("decimals", "2");
        w.WriteString(value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static void WriteStringFact(XmlWriter w, string prefix, string concept,
        string contextRef, string value)
    {
        var ns = prefix == "tfrs"
            ? "http://xbrl.dbd.go.th/tfrs-npaes/2024"
            : "http://xbrl.dbd.go.th/common/2024";
        w.WriteStartElement(prefix, concept, ns);
        w.WriteAttributeString("contextRef", contextRef);
        w.WriteString(value);
        w.WriteEndElement();
    }
}
