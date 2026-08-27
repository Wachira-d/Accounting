using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DimensionalAccountingService : IDimensionalAccountingService
{
    private readonly AccountingDbContext _db;

    public DimensionalAccountingService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Dimensions =====

    public async Task<DimensionResponse> CreateDimensionAsync(Guid companyId, CreateDimensionRequest request)
    {
        var existing = await _db.Set<AccountingDimension>()
            .AnyAsync(d => d.CompanyId == companyId && d.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสมิติ {request.Code} ซ้ำ");

        var level = 1;
        if (request.ParentId.HasValue)
        {
            var parent = await _db.Set<AccountingDimension>()
                .FirstOrDefaultAsync(d => d.Id == request.ParentId.Value && d.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบมิติหลัก");
            level = parent.Level + 1;
        }

        var dimension = new AccountingDimension
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            DimensionType = request.DimensionType,
            ParentId = request.ParentId,
            Level = level,
            Description = request.Description,
            ManagerName = request.ManagerName,
            ManagerEmail = request.ManagerEmail,
            AnnualBudget = request.AnnualBudget
        };

        _db.Set<AccountingDimension>().Add(dimension);
        await _db.SaveChangesAsync();

        return await GetDimensionAsync(companyId, dimension.Id);
    }

    public async Task<List<DimensionResponse>> GetDimensionsAsync(Guid companyId, DimensionType? type = null)
    {
        var query = _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .Where(d => d.CompanyId == companyId && d.IsActive);

        if (type.HasValue)
            query = query.Where(d => d.DimensionType == type.Value);

        var dimensions = await query
            .Where(d => d.ParentId == null)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Code)
            .ToListAsync();

        return dimensions.Select(MapToDimensionResponse).ToList();
    }

    public async Task<DimensionResponse> GetDimensionAsync(Guid companyId, Guid dimensionId)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        return MapToDimensionResponse(dimension);
    }

    public async Task<DimensionResponse> UpdateDimensionAsync(Guid companyId, Guid dimensionId, UpdateDimensionRequest request)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        if (request.Name != null) dimension.Name = request.Name;
        if (request.NameEn != null) dimension.NameEn = request.NameEn;
        if (request.Description != null) dimension.Description = request.Description;
        if (request.ManagerName != null) dimension.ManagerName = request.ManagerName;
        if (request.ManagerEmail != null) dimension.ManagerEmail = request.ManagerEmail;
        if (request.AnnualBudget.HasValue) dimension.AnnualBudget = request.AnnualBudget.Value;
        if (request.IsActive.HasValue) dimension.IsActive = request.IsActive.Value;
        if (request.SortOrder.HasValue) dimension.SortOrder = request.SortOrder.Value;

        await _db.SaveChangesAsync();
        return await GetDimensionAsync(companyId, dimensionId);
    }

    public async Task DeleteDimensionAsync(Guid companyId, Guid dimensionId)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        if (dimension.Children.Any())
            throw new InvalidOperationException("ไม่สามารถลบมิติที่มีมิติย่อยได้");

        dimension.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Dimension Allocation =====

    public async Task AssignDimensionsAsync(Guid companyId, Guid journalEntryLineId, List<DimensionAllocationRequest> allocations)
    {
        var line = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .FirstOrDefaultAsync(l => l.Id == journalEntryLineId && l.JournalEntry.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการบันทึกบัญชี");

        var totalPercent = allocations.Sum(a => a.Percent ?? 0m);
        if (allocations.Count > 0 && Math.Abs(totalPercent - 100m) > 0.01m)
            throw new InvalidOperationException(
                $"สัดส่วนการจัดสรรรวมต้องเท่ากับ 100% (ปัจจุบัน: {totalPercent:N2}%)");

        // Remove existing allocations
        var existing = await _db.Set<JournalLineDimension>()
            .Where(d => d.JournalEntryLineId == journalEntryLineId)
            .ToListAsync();
        _db.Set<JournalLineDimension>().RemoveRange(existing);

        // Add new allocations
        foreach (var alloc in allocations)
        {
            _db.Set<JournalLineDimension>().Add(new JournalLineDimension
            {
                CompanyId = companyId,
                JournalEntryLineId = journalEntryLineId,
                DimensionId = alloc.DimensionId,
                AllocatedAmount = alloc.Amount,
                AllocatedPercent = alloc.Percent
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<DimensionAllocationResponse>> GetLineDimensionsAsync(Guid companyId, Guid journalEntryLineId)
    {
        var allocations = await _db.Set<JournalLineDimension>()
            .Include(d => d.Dimension)
            .Where(d => d.JournalEntryLineId == journalEntryLineId && d.CompanyId == companyId)
            .ToListAsync();

        return allocations.Select(a => new DimensionAllocationResponse(
            a.DimensionId,
            a.Dimension.Code,
            a.Dimension.Name,
            a.Dimension.DimensionType,
            a.AllocatedAmount,
            a.AllocatedPercent
        )).ToList();
    }

    // ===== Reports =====

    public async Task<DimensionPnLResponse> GetDimensionPnLAsync(Guid companyId, Guid dimensionId, DateTime fromDate, DateTime toDate)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        var lines = await _db.Set<JournalLineDimension>()
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.JournalEntry)
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.Account)
            .Where(jld => jld.DimensionId == dimensionId
                && jld.CompanyId == companyId
                && jld.JournalEntryLine.JournalEntry.Status == JournalEntryStatus.Posted
                && jld.JournalEntryLine.JournalEntry.EntryDate >= fromDate
                && jld.JournalEntryLine.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        var pnlLines = lines
            .GroupBy(l => new { l.JournalEntryLine.AccountId, l.JournalEntryLine.Account!.AccountCode, l.JournalEntryLine.Account.AccountName })
            .Select(g => new DimensionPnLLine(
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.DebitAmount - l.JournalEntryLine.CreditAmount))
            ))
            .ToList();

        var totalRevenue = lines
            .Where(l => l.JournalEntryLine.Account?.AccountType == AccountType.Revenue)
            .Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.CreditAmount - l.JournalEntryLine.DebitAmount));

        var totalExpenses = lines
            .Where(l => l.JournalEntryLine.Account?.AccountType == AccountType.Expense)
            .Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.DebitAmount - l.JournalEntryLine.CreditAmount));

        return new DimensionPnLResponse(
            dimensionId, dimension.Name, fromDate, toDate,
            totalRevenue, totalExpenses, totalRevenue - totalExpenses, pnlLines);
    }

    public async Task<List<DimensionSummaryResponse>> GetDimensionSummaryAsync(Guid companyId, DimensionType type, DateTime fromDate, DateTime toDate)
    {
        var dimensions = await _db.Set<AccountingDimension>()
            .Where(d => d.CompanyId == companyId && d.DimensionType == type && d.IsActive)
            .ToListAsync();

        var allAllocations = await _db.Set<JournalLineDimension>()
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.JournalEntry)
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.Account)
            .Where(jld => jld.CompanyId == companyId
                && jld.JournalEntryLine.JournalEntry.Status == JournalEntryStatus.Posted
                && jld.JournalEntryLine.JournalEntry.EntryDate >= fromDate
                && jld.JournalEntryLine.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        var result = new List<DimensionSummaryResponse>();
        foreach (var dim in dimensions)
        {
            var dimAllocations = allAllocations.Where(a => a.DimensionId == dim.Id).ToList();

            var revenue = dimAllocations
                .Where(a => a.JournalEntryLine.Account?.AccountType == AccountType.Revenue)
                .Sum(a => a.AllocatedAmount ?? (a.JournalEntryLine.CreditAmount - a.JournalEntryLine.DebitAmount));

            var expenses = dimAllocations
                .Where(a => a.JournalEntryLine.Account?.AccountType == AccountType.Expense)
                .Sum(a => a.AllocatedAmount ?? (a.JournalEntryLine.DebitAmount - a.JournalEntryLine.CreditAmount));

            var netIncome = revenue - expenses;
            var variance = dim.AnnualBudget.HasValue ? dim.AnnualBudget.Value - expenses : (decimal?)null;

            result.Add(new DimensionSummaryResponse(
                dim.Id, dim.Code, dim.Name, dim.DimensionType,
                revenue, expenses, netIncome, dim.AnnualBudget, variance));
        }

        return result;
    }

    // ===== Branches =====
    //
    // ⚠️ กติกาสำคัญ: กิจการ **สาขาเดียว** ไม่ต้องสร้าง Branch เลยก็ทำงานได้ครบ
    // (ไม่มีแถวในตารางนี้ = ใช้ที่อยู่/รหัสสาขาของบริษัทตามเดิม) — โค้ดทุกเส้นทาง
    // ที่แตะสาขาต้องทนกับ "ไม่มีสาขาสักแถว" ห้ามบังคับให้สร้างก่อนถึงจะใช้งานได้

    /// <summary>
    /// จัดรูป + ตรวจรหัสสาขาสรรพากรให้เข้ากับสถานะ "สำนักงานใหญ่"
    /// (รูปแบบ 5 หลักอยู่ใน resolver กลาง <see cref="TaxBranchCode"/> — ห้ามเขียนซ้ำ)
    /// </summary>
    private static string? NormalizeTaxBranchCode(string? raw, bool isHeadOffice)
    {
        if (!TaxBranchCode.TryNormalize(raw, out var code, out var error))
            throw new InvalidOperationException(error);

        if (code == null)
            // สำนักงานใหญ่มีค่ามาตรฐานอยู่แล้ว — เติมให้ ผู้ใช้ไม่ต้องรู้ว่าต้องเป็น 00000
            return isHeadOffice ? TaxBranchCode.HeadOffice : null;

        if (isHeadOffice && code != TaxBranchCode.HeadOffice)
            throw new InvalidOperationException(
                "สาขาที่ตั้งเป็น \"สำนักงานใหญ่\" ต้องใช้รหัสสาขาสรรพากร 00000");

        if (!isHeadOffice && code == TaxBranchCode.HeadOffice)
            throw new InvalidOperationException(
                "รหัส 00000 สงวนไว้สำหรับสำนักงานใหญ่ — สาขาย่อยต้องใช้ 00001 ขึ้นไป");

        return code;
    }

    /// <summary>สำนักงานใหญ่มีได้แค่แห่งเดียว — ตั้งใหม่แล้วปลดของเดิมให้อัตโนมัติ</summary>
    private async Task DemoteOtherHeadOfficesAsync(Guid companyId, Guid keepId)
    {
        var others = await _db.Set<Branch>()
            .Where(b => b.CompanyId == companyId && b.IsHeadOffice && b.Id != keepId)
            .ToListAsync();
        foreach (var o in others)
        {
            o.IsHeadOffice = false;
            if (o.TaxBranchCode == "00000") o.TaxBranchCode = null;   // 00000 ต้องว่างไว้ให้สำนักงานใหญ่ตัวจริง
        }
    }

    public async Task<BranchResponse> CreateBranchAsync(Guid companyId, CreateBranchRequest request)
    {
        var code = (request.Code ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("กรุณากรอกรหัสสาขา");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("กรุณากรอกชื่อสาขา");

        var existing = await _db.Set<Branch>()
            .AnyAsync(b => b.CompanyId == companyId && b.Code == code);
        if (existing)
            throw new InvalidOperationException($"รหัสสาขา {code} ซ้ำ");

        var taxCode = NormalizeTaxBranchCode(request.TaxBranchCode, request.IsHeadOffice);
        if (taxCode != null)
        {
            var dupTax = await _db.Set<Branch>()
                .AnyAsync(b => b.CompanyId == companyId && b.TaxBranchCode == taxCode);
            if (dupTax)
                throw new InvalidOperationException(
                    $"รหัสสาขาสรรพากร {taxCode} ถูกใช้กับสาขาอื่นแล้ว — เลขนี้ต้องไม่ซ้ำ (§86/4)");
        }

        var branch = new Branch
        {
            CompanyId = companyId,
            Code = code,
            Name = request.Name.Trim(),
            NameEn = request.NameEn,
            Address = request.Address,
            SubDistrict = request.SubDistrict,
            District = request.District,
            Province = request.Province,
            PostalCode = request.PostalCode,
            Phone = request.Phone,
            Email = request.Email,
            TaxBranchCode = taxCode,
            IsHeadOffice = request.IsHeadOffice,
            ManagerName = request.ManagerName
        };

        _db.Set<Branch>().Add(branch);
        if (request.IsHeadOffice)
            await DemoteOtherHeadOfficesAsync(companyId, branch.Id);
        await _db.SaveChangesAsync();

        return MapToBranchResponse(branch);
    }

    /// <param name="includeInactive">
    /// true = รวมสาขาที่ปิดใช้งาน — จำเป็นสำหรับ **หน้าตั้งค่า** ไม่งั้นสาขาที่เผลอ
    /// ปิดจะหายจากจอถาวร เปิดกลับไม่ได้เลย (default false เพื่อไม่เปลี่ยนพฤติกรรม
    /// ของตัวเลือกสาขาในหน้าอื่นที่เรียกอยู่แล้ว)
    /// </param>
    public async Task<List<BranchResponse>> GetBranchesAsync(Guid companyId, bool includeInactive = false)
    {
        var branches = await _db.Set<Branch>()
            .Where(b => b.CompanyId == companyId && (includeInactive || b.IsActive))
            .OrderByDescending(b => b.IsHeadOffice)
            .ThenBy(b => b.Code)
            .ToListAsync();

        return branches.Select(MapToBranchResponse).ToList();
    }

    public async Task<BranchResponse> UpdateBranchAsync(Guid companyId, Guid branchId, UpdateBranchRequest request)
    {
        var branch = await _db.Set<Branch>()
            .FirstOrDefaultAsync(b => b.Id == branchId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสาขา");

        if (request.Code != null)
        {
            var code = request.Code.Trim();
            if (code.Length == 0) throw new InvalidOperationException("รหัสสาขาว่างไม่ได้");
            if (code != branch.Code)
            {
                var dup = await _db.Set<Branch>()
                    .AnyAsync(b => b.CompanyId == companyId && b.Code == code && b.Id != branchId);
                if (dup) throw new InvalidOperationException($"รหัสสาขา {code} ซ้ำ");
                branch.Code = code;
            }
        }

        if (request.Name != null)
        {
            if (request.Name.Trim().Length == 0) throw new InvalidOperationException("ชื่อสาขาว่างไม่ได้");
            branch.Name = request.Name.Trim();
        }
        if (request.NameEn != null) branch.NameEn = Blank(request.NameEn);
        if (request.Address != null) branch.Address = Blank(request.Address);
        if (request.SubDistrict != null) branch.SubDistrict = Blank(request.SubDistrict);
        if (request.District != null) branch.District = Blank(request.District);
        if (request.Province != null) branch.Province = Blank(request.Province);
        if (request.PostalCode != null) branch.PostalCode = Blank(request.PostalCode);
        if (request.Phone != null) branch.Phone = Blank(request.Phone);
        if (request.Email != null) branch.Email = Blank(request.Email);
        if (request.ManagerName != null) branch.ManagerName = Blank(request.ManagerName);

        // สำนักงานใหญ่ + รหัสสรรพากร ต้องตรวจคู่กัน (00000 ผูกกับสำนักงานใหญ่เสมอ)
        var willBeHead = request.IsHeadOffice ?? branch.IsHeadOffice;
        if (request.TaxBranchCode != null || request.IsHeadOffice.HasValue)
        {
            var taxCode = NormalizeTaxBranchCode(request.TaxBranchCode ?? branch.TaxBranchCode, willBeHead);
            if (taxCode != null && taxCode != branch.TaxBranchCode)
            {
                var dupTax = await _db.Set<Branch>()
                    .AnyAsync(b => b.CompanyId == companyId && b.TaxBranchCode == taxCode && b.Id != branchId);
                if (dupTax)
                    throw new InvalidOperationException(
                        $"รหัสสาขาสรรพากร {taxCode} ถูกใช้กับสาขาอื่นแล้ว — เลขนี้ต้องไม่ซ้ำ (§86/4)");
            }
            branch.TaxBranchCode = taxCode;
            branch.IsHeadOffice = willBeHead;
            if (willBeHead) await DemoteOtherHeadOfficesAsync(companyId, branchId);
        }

        if (request.IsActive.HasValue && request.IsActive.Value != branch.IsActive)
        {
            if (!request.IsActive.Value)
            {
                // ปิดสาขาสุดท้ายที่ยังเปิดอยู่ = เอกสารที่ผูกสาขาหาที่ออกไม่เจอ → กันไว้
                var otherActive = await _db.Set<Branch>()
                    .CountAsync(b => b.CompanyId == companyId && b.IsActive && b.Id != branchId);
                if (otherActive == 0)
                    throw new InvalidOperationException(
                        "ปิดสาขาสุดท้ายไม่ได้ — ถ้าไม่ต้องการแยกสาขาแล้ว ให้ลบสาขาทิ้งทั้งหมดแทน");
                if (branch.IsHeadOffice)
                    throw new InvalidOperationException(
                        "ปิดสำนักงานใหญ่ไม่ได้ — ย้ายสถานะสำนักงานใหญ่ไปสาขาอื่นก่อน");
            }
            branch.IsActive = request.IsActive.Value;
        }

        await _db.SaveChangesAsync();
        return MapToBranchResponse(branch);
    }

    /// <summary>
    /// ลบสาขา — ได้เฉพาะสาขาที่ยัง "ไม่เคยถูกใช้" เท่านั้น
    /// สาขาที่มีรายการบัญชีอ้างถึงแล้วห้ามลบ (รายงานย้อนหลังจะหาต้นทางไม่เจอ +
    /// พ.ร.บ.การบัญชี ม.10 ต้องเก็บ 5 ปี) → ให้ปิดใช้งานแทน
    /// </summary>
    public async Task DeleteBranchAsync(Guid companyId, Guid branchId)
    {
        var branch = await _db.Set<Branch>()
            .FirstOrDefaultAsync(b => b.Id == branchId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสาขา");

        var usedByEntry = await _db.JournalEntries
            .AnyAsync(j => j.CompanyId == companyId && j.BranchId == branchId);
        var usedByLine = await _db.JournalEntryLines
            .AnyAsync(l => l.BranchId == branchId && l.JournalEntry.CompanyId == companyId);
        if (usedByEntry || usedByLine)
            throw new InvalidOperationException(
                "ลบไม่ได้ — สาขานี้มีรายการบัญชีอ้างถึงแล้ว (ต้องเก็บไว้ตาม พ.ร.บ.การบัญชี ม.10) " +
                "ถ้าเลิกใช้สาขานี้ ให้เอาเครื่องหมาย \"ใช้งาน\" ออกแทน");

        _db.Set<Branch>().Remove(branch);
        await _db.SaveChangesAsync();
    }

    /// <summary>"" จากฟอร์ม = ผู้ใช้ตั้งใจล้างค่า → เก็บเป็น null ไม่ใช่สตริงว่าง</summary>
    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ===== Mapping Helpers =====

    private static DimensionResponse MapToDimensionResponse(AccountingDimension d) =>
        new(d.Id, d.Code, d.Name, d.NameEn, d.DimensionType, d.ParentId, d.Level,
            d.Description, d.ManagerName, d.AnnualBudget, d.IsActive,
            d.Children?.Select(MapToDimensionResponse).ToList());

    // echo **ทุกฟิลด์ที่รับเข้า** — ไม่งั้นฟอร์มแก้ไข prefill ไม่ได้ แล้วผู้ใช้
    // กดบันทึกทับด้วยค่าว่างโดยไม่รู้ตัว (defect class "เก็บแล้วต้อง echo กลับ")
    private static BranchResponse MapToBranchResponse(Branch b) =>
        new(b.Id, b.Code, b.Name, b.NameEn, b.Address, b.SubDistrict, b.District,
            b.Province, b.PostalCode, b.Phone, b.Email, b.TaxBranchCode,
            b.IsHeadOffice, b.IsActive, b.ManagerName);
}
