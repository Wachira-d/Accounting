using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class WithholdingTaxCertService : IWithholdingTaxCertService
{
    private readonly AccountingDbContext _db;

    public WithholdingTaxCertService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<WithholdingTaxCertResponse> CreateAsync(Guid companyId, CreateWithholdingTaxCertRequest request, string createdBy)
    {
        var count = await _db.WithholdingTaxCerts.CountAsync(w => w.CompanyId == companyId);
        var certNumber = $"WHT-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

        var cert = new WithholdingTaxCert
        {
            CompanyId = companyId,
            CertificateNumber = certNumber,
            PayeeContactId = request.PayeeContactId,
            TaxFormType = request.TaxFormType,
            TaxYear = request.TaxYear,
            TaxMonth = request.TaxMonth,
            CertificateType = request.CertificateType,
            CreatedBy = createdBy
        };

        var order = 1;
        foreach (var line in request.Lines)
        {
            cert.Lines.Add(new WithholdingTaxCertLine
            {
                LineOrder = order++,
                IncomeTypeCode = line.IncomeTypeCode,
                IncomeDescription = line.IncomeDescription,
                PaymentDate = line.PaymentDate,
                IncomeAmount = line.IncomeAmount,
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                Condition = line.Condition
            });
        }

        cert.TotalIncomeAmount = cert.Lines.Sum(l => l.IncomeAmount);
        cert.TotalTaxAmount = cert.Lines.Sum(l => l.TaxAmount);

        _db.WithholdingTaxCerts.Add(cert);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task<WithholdingTaxCertResponse> GetByIdAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        return MapToResponse(cert, company);
    }

    public async Task<PagedResponse<WithholdingTaxCertResponse>> GetAllAsync(Guid companyId, TaxType? taxFormType, int? year, int? month, PagedRequest request)
    {
        var query = _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Where(w => w.CompanyId == companyId);

        if (taxFormType.HasValue) query = query.Where(w => w.TaxFormType == taxFormType.Value);
        if (year.HasValue) query = query.Where(w => w.TaxYear == year.Value);
        if (month.HasValue) query = query.Where(w => w.TaxMonth == month.Value);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(w => w.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        return new PagedResponse<WithholdingTaxCertResponse>(
            items.Select(w => MapToResponse(w, company)).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<WithholdingTaxCertResponse> IssueAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        if (cert.Status != WithholdingTaxCertStatus.Draft)
            throw new InvalidOperationException("สามารถออกหนังสือรับรองได้เฉพาะที่เป็น Draft เท่านั้น");

        cert.Status = WithholdingTaxCertStatus.Issued;
        cert.IssuedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task VoidAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        cert.Status = WithholdingTaxCertStatus.Voided;
        await _db.SaveChangesAsync();
    }

    public async Task<List<WithholdingTaxCertResponse>> GetByContactAsync(Guid companyId, Guid contactId, int? year = null)
    {
        var query = _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Where(w => w.CompanyId == companyId && w.PayeeContactId == contactId);

        if (year.HasValue) query = query.Where(w => w.TaxYear == year.Value);

        var certs = await query.OrderByDescending(w => w.TaxYear).ThenByDescending(w => w.TaxMonth).ToListAsync();
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        return certs.Select(w => MapToResponse(w, company)).ToList();
    }

    private static string GetTaxFormName(TaxType type) => type switch
    {
        TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        _ => type.ToString()
    };

    private static string GetIncomeTypeName(string code) => code switch
    {
        "1" => "เงินเดือน ค่าจ้าง บำนาญ (40(1))",
        "2" => "ค่านายหน้า (40(2))",
        "3" => "ค่าแห่งลิขสิทธิ์ (40(3))",
        "4a" => "ดอกเบี้ย (40(4)(a))",
        "4b" => "เงินปันผล (40(4)(b))",
        "5" => "ค่าเช่าทรัพย์สิน (40(5))",
        "6" => "ค่าวิชาชีพอิสระ (40(6))",
        "7" => "ค่ารับเหมา (40(7))",
        "8" => "ค่าบริการอื่นๆ (40(8))",
        _ => $"ประเภทเงินได้ {code}"
    };

    private static WithholdingTaxCertResponse MapToResponse(WithholdingTaxCert w, Company company) => new(
        w.Id, w.CertificateNumber, w.CompanyId,
        company.Name, company.TaxId, company.BranchCode,
        company.Address ?? "",
        w.PayeeContactId, w.PayeeContact.Name, w.PayeeContact.TaxId,
        w.PayeeContact.BranchCode, w.PayeeContact.Address,
        w.TaxFormType, GetTaxFormName(w.TaxFormType),
        w.TaxYear, w.TaxMonth, w.CertificateType, w.Status,
        w.TotalIncomeAmount, w.TotalTaxAmount,
        w.Lines.OrderBy(l => l.LineOrder).Select(l => new WithholdingTaxCertLineResponse(
            l.Id, l.IncomeTypeCode, GetIncomeTypeName(l.IncomeTypeCode),
            l.IncomeDescription, l.PaymentDate, l.IncomeAmount, l.TaxRate, l.TaxAmount, l.Condition)).ToList(),
        w.IssuedDate, w.CreatedAt);
}
