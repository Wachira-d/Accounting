using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// ข้อมูลธุรกิจของบริษัทที่ส่งให้ AI พร้อม prompt — ช่วยให้ AI เข้าใจ context
/// ก่อนเลือกผังบัญชี (ร้านอาหารผังต่างจากที่ปรึกษา, โรงแรมมีผัง 21510
/// "ห้องพักรับล่วงหน้า" ที่บริษัททั่วไปไม่มี ฯลฯ).
///
/// รวบรวมเป็น record เดียวเพื่อให้ทุก prompt ที่ต้องการ business context
/// (GL suggestion, PV bulk, OCR augmentation) ใช้ shape เดียวกัน — เปลี่ยน
/// schema ครั้งเดียว ทุก prompt ได้ context เพิ่มพร้อมกัน.
/// </summary>
public sealed record CompanyBusinessContext(
    string Name,
    string? NameEn,
    string? TaxId,
    /// <summary>BusinessType เป็นข้อความไทย+อังกฤษเพื่อให้ AI ตีความง่าย —
    /// "Individual (บุคคลธรรมดา)" / "JuristicPerson (นิติบุคคล)" /
    /// "Partnership (ห้างหุ้นส่วน)" ฯลฯ</summary>
    string BusinessType,
    /// <summary>IndustryType เป็นข้อความไทย+อังกฤษ — "Hotel (โรงแรม)" /
    /// "Restaurant (ร้านอาหาร)" / "Trading (ซื้อมาขายไป)" ฯลฯ. AI ใช้
    /// match กับ industry-specific COA template (เช่น Hotel COA มี
    /// 11830 เงินมัดจำรับล่วงหน้า, 21510 ห้องพักรับล่วงหน้า).</summary>
    string IndustryType,
    bool IsVatRegistered,
    decimal VatRate,
    decimal? PaidUpCapital,
    string? Province,
    int FiscalYearStartMonth,
    string? WhtRecognitionBasis,
    /// <summary>10 ผังบัญชีที่บริษัทใช้บ่อยที่สุดในช่วง 6 เดือนหลัง — บอก AI
    /// ว่า "บริษัทนี้เคยลงผังไหนบ่อย" ป้องกัน hallucination ไปเลือกผังที่ไม่
    /// match กับ pattern จริง.</summary>
    IReadOnlyList<TopAccount> TopAccountsUsed)
{
    public sealed record TopAccount(string Code, string Name, int TimesUsed);
}

/// <summary>Loader รวมข้อมูล CompanyBusinessContext จาก DB. cache 5 นาที
/// (per-companyId) ลด query ซ้ำเมื่อหลาย prompt ยิงในรอบเดียว.</summary>
public static class CompanyBusinessContextLoader
{
    /// <summary>โหลด context จาก DB — Company + Settings + top-N accounts
    /// ที่ใช้บ่อย. คืน object ปลอดภัย (null-safe) ทุก field — ถ้า company
    /// ไม่มีจะคืน "(unknown)" + General ไม่ throw เพื่อกัน AI flow พัง.</summary>
    public static async Task<CompanyBusinessContext> LoadAsync(
        AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var company = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);
        var settings = await db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        // Top 10 accounts ที่ลงใน DocumentLine ของบริษัทช่วง 6 เดือนหลัง —
        // pattern signal สำหรับ AI ว่า "บริษัทนี้คุ้นกับผังไหน". จำกัด lookback
        // เพื่อไม่ดึง pattern ที่ล้าสมัย (ปีก่อนอาจใช้ผังคนละแบบ).
        var since = DateTime.UtcNow.AddMonths(-6);
        var topAccounts = await (
            from line in db.DocumentLines.AsNoTracking()
            join doc in db.Documents.AsNoTracking() on line.DocumentId equals doc.Id
            join acc in db.ChartOfAccounts.AsNoTracking() on line.AccountId equals acc.Id
            where doc.CompanyId == companyId
                && line.AccountId != null
                && doc.DocumentDate >= since
                && doc.Status != DocumentStatus.Voided
            group line by new { acc.AccountCode, acc.AccountName } into g
            orderby g.Count() descending
            select new { g.Key.AccountCode, g.Key.AccountName, TimesUsed = g.Count() })
            .Take(10).ToListAsync(ct);

        return new CompanyBusinessContext(
            Name: company?.Name ?? "(unknown company)",
            NameEn: company?.NameEn,
            TaxId: company?.TaxId,
            BusinessType: FormatBusinessType(company?.BusinessType ?? Models.Enums.BusinessType.JuristicPerson),
            IndustryType: FormatIndustryType(company?.IndustryType ?? Models.Enums.IndustryType.General),
            IsVatRegistered: company?.IsVatRegistered ?? false,
            VatRate: company?.VatRate ?? 7m,
            PaidUpCapital: company?.PaidUpCapital,
            Province: company?.Province,
            FiscalYearStartMonth: company?.FiscalYearStartMonth ?? 1,
            WhtRecognitionBasis: settings?.WhtRecognitionBasis.ToString(),
            TopAccountsUsed: topAccounts
                .Select(a => new CompanyBusinessContext.TopAccount(a.AccountCode, a.AccountName, a.TimesUsed))
                .ToList());
    }

    private static string FormatBusinessType(BusinessType bt) => bt switch
    {
        Models.Enums.BusinessType.Individual => "Individual (บุคคลธรรมดา)",
        Models.Enums.BusinessType.JuristicPerson => "JuristicPerson (นิติบุคคล)",
        Models.Enums.BusinessType.Partnership => "Partnership (ห้างหุ้นส่วน)",
        Models.Enums.BusinessType.PublicCompany => "PublicCompany (บริษัทมหาชน)",
        Models.Enums.BusinessType.Foundation => "Foundation (มูลนิธิ)",
        Models.Enums.BusinessType.Association => "Association (สมาคม)",
        _ => "Other (อื่นๆ)",
    };

    private static string FormatIndustryType(IndustryType it) => it switch
    {
        Models.Enums.IndustryType.General => "General (ทั่วไป)",
        Models.Enums.IndustryType.Trading => "Trading (ซื้อมาขายไป)",
        Models.Enums.IndustryType.Service => "Service (ธุรกิจบริการ)",
        Models.Enums.IndustryType.Manufacturing => "Manufacturing (ผลิต/โรงงาน)",
        Models.Enums.IndustryType.Restaurant => "Restaurant (ร้านอาหาร)",
        Models.Enums.IndustryType.Cafe => "Cafe (คาเฟ่/เครื่องดื่ม)",
        Models.Enums.IndustryType.Retail => "Retail (ค้าปลีก)",
        Models.Enums.IndustryType.Construction => "Construction (รับเหมาก่อสร้าง)",
        Models.Enums.IndustryType.RealEstate => "RealEstate (อสังหาริมทรัพย์)",
        Models.Enums.IndustryType.Technology => "Technology (เทคโนโลยี/ซอฟต์แวร์)",
        Models.Enums.IndustryType.Healthcare => "Healthcare (สุขภาพ/คลินิก)",
        Models.Enums.IndustryType.Education => "Education (การศึกษา)",
        Models.Enums.IndustryType.Beauty => "Beauty (ความงาม/สปา)",
        Models.Enums.IndustryType.Transportation => "Transportation (ขนส่ง/โลจิสติกส์)",
        Models.Enums.IndustryType.Agriculture => "Agriculture (เกษตร)",
        Models.Enums.IndustryType.Hotel => "Hotel (โรงแรม/ที่พัก)",
        Models.Enums.IndustryType.Ecommerce => "Ecommerce (อีคอมเมิร์ซ/ออนไลน์)",
        Models.Enums.IndustryType.Freelance => "Freelance (ฟรีแลนซ์)",
        _ => "Other (อื่นๆ)",
    };
}
