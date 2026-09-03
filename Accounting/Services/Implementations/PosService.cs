using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class PosService : IPosService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;
    private readonly ILogger<PosService> _logger;
    private readonly IEmailSenderFactory? _emailFactory;
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ — POS ห้าม `CurrentStock ±=` เองอีก
    /// (เดิมทำ 4 จุดโดยไม่ระบุคลัง ⇒ ขายที่สาขาไหนก็ตัดยอดรวมของบริษัท)</summary>
    private readonly IStockLedger _stock;

    public PosService(AccountingDbContext db, IAccountingService accountingService, ILogger<PosService> logger,
        IStockLedger stock, IEmailSenderFactory? emailFactory = null)
    {
        _db = db;
        _accountingService = accountingService;
        _logger = logger;
        _stock = stock;
        _emailFactory = emailFactory;
    }

    // ==================== Terminal ====================

    public async Task<TerminalResponse> CreateTerminalAsync(Guid companyId, CreateTerminalRequest request)
    {
        await ValidateTerminalScopeAsync(companyId, request.BranchId, request.WarehouseId);
        var terminal = new PosTerminal
        {
            CompanyId = companyId,
            Name = request.Name,
            BusinessMode = request.BusinessMode,
            Location = request.Location,
            SettingsJson = request.SettingsJson,
            BranchId = Normalize(request.BranchId),
            WarehouseId = Normalize(request.WarehouseId),
            CashAccountId = Normalize(request.CashAccountId),
            BankAccountId = Normalize(request.BankAccountId),
            AbbreviatedInvoicePrefix = string.IsNullOrWhiteSpace(request.AbbreviatedInvoicePrefix)
                ? null : request.AbbreviatedInvoicePrefix.Trim().ToUpperInvariant(),
        };
        _db.PosTerminals.Add(terminal);
        await _db.SaveChangesAsync();
        return await MapTerminalAsync(terminal, 0);
    }

    /// <summary>Guid.Empty จากฟอร์ม = "ไม่เลือก" → เก็บเป็น null</summary>
    private static Guid? Normalize(Guid? id) => id == Guid.Empty ? null : id;

    /// <summary>สาขา/คลังต้องเป็นของบริษัทนี้จริง — ห้ามผูกเครื่องข้ามผู้เช่า
    /// (กฎ M: ทุก query ต้องมี CompanyId)</summary>
    private async Task ValidateTerminalScopeAsync(Guid companyId, Guid? branchId, Guid? warehouseId)
    {
        if (Normalize(branchId) is Guid b
            && !await _db.Branches.AnyAsync(x => x.Id == b && x.CompanyId == companyId && !x.IsDeleted))
            throw new KeyNotFoundException("ไม่พบสาขาที่เลือกในบริษัทนี้");
        if (Normalize(warehouseId) is Guid w
            && !await _db.Warehouses.AnyAsync(x => x.Id == w && x.CompanyId == companyId && !x.IsDeleted))
            throw new KeyNotFoundException("ไม่พบคลังที่เลือกในบริษัทนี้");
    }

    public async Task<List<TerminalResponse>> GetTerminalsAsync(Guid companyId)
    {
        var terminals = await _db.PosTerminals
            .Where(t => t.CompanyId == companyId)
            .Select(t => new { Terminal = t, OpenSessions = t.Sessions.Count(s => s.Status == PosSessionStatus.Open) })
            .ToListAsync();
        // ชื่อสาขา/คลังดึงทีเดียวทั้งบริษัท — เครื่องมีไม่กี่ตัว แต่ N+1 ก็ไม่ควรมี
        var names = await LoadScopeNamesAsync(companyId);
        return terminals.Select(x => MapTerminal(x.Terminal, x.OpenSessions, names)).ToList();
    }

    public async Task<TerminalResponse> UpdateTerminalAsync(Guid companyId, Guid terminalId, UpdateTerminalRequest request)
    {
        var terminal = await _db.PosTerminals.FirstOrDefaultAsync(t => t.Id == terminalId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ POS Terminal");
        if (request.Name != null) terminal.Name = request.Name;
        if (request.BusinessMode.HasValue) terminal.BusinessMode = request.BusinessMode.Value;
        if (request.Location != null) terminal.Location = request.Location;
        if (request.IsActive.HasValue) terminal.IsActive = request.IsActive.Value;
        if (request.SettingsJson != null) terminal.SettingsJson = request.SettingsJson;
        // Guid.Empty = ล้างค่า · null = ไม่แตะ — ต้องแยกสองความหมายนี้ ไม่งั้นถอด
        // สาขาออกจากเครื่องไม่ได้เลย (defect class "ห้าม silent no-op")
        await ValidateTerminalScopeAsync(companyId, request.BranchId, request.WarehouseId);
        if (request.BranchId.HasValue) terminal.BranchId = Normalize(request.BranchId);
        if (request.WarehouseId.HasValue) terminal.WarehouseId = Normalize(request.WarehouseId);
        if (request.CashAccountId.HasValue) terminal.CashAccountId = Normalize(request.CashAccountId);
        if (request.BankAccountId.HasValue) terminal.BankAccountId = Normalize(request.BankAccountId);
        if (request.AbbreviatedInvoicePrefix != null)
            terminal.AbbreviatedInvoicePrefix = string.IsNullOrWhiteSpace(request.AbbreviatedInvoicePrefix)
                ? null : request.AbbreviatedInvoicePrefix.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync();
        var openCount = await _db.PosSessions.CountAsync(s => s.TerminalId == terminalId && s.Status == PosSessionStatus.Open);
        return await MapTerminalAsync(terminal, openCount);
    }

    // ==================== Session ====================

    public async Task<SessionResponse> OpenSessionAsync(Guid companyId, Guid userId, OpenSessionRequest request)
    {
        if (request.TerminalId == null || request.TerminalId == Guid.Empty)
            throw new InvalidOperationException("กรุณาเลือก Terminal ก่อนเปิดกะ");

        var terminalId = request.TerminalId.Value;
        var terminal = await _db.PosTerminals.FirstOrDefaultAsync(t => t.Id == terminalId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ POS Terminal");
        if (!terminal.IsActive) throw new InvalidOperationException("Terminal นี้ถูกปิดใช้งาน");

        var existingOpen = await _db.PosSessions.AnyAsync(s => s.TerminalId == terminalId && s.Status == PosSessionStatus.Open);
        if (existingOpen) throw new InvalidOperationException("มีกะที่เปิดอยู่แล้ว กรุณาปิดกะก่อน");

        var session = new PosSession
        {
            CompanyId = companyId,
            TerminalId = terminalId,
            OpenedByUserId = userId,
            OpeningBalance = request.OpeningBalance,
            Notes = request.Notes
        };
        _db.PosSessions.Add(session);
        await _db.SaveChangesAsync();
        return await MapSessionAsync(session);
    }

    public async Task<SessionResponse> CloseSessionAsync(Guid companyId, Guid userId, Guid sessionId, CloseSessionRequest request)
    {
        var session = await _db.PosSessions
            .Include(s => s.Orders).ThenInclude(o => o.Payments)
            .Include(s => s.Terminal)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกะการขาย");
        if (session.Status == PosSessionStatus.Closed) throw new InvalidOperationException("กะนี้ปิดไปแล้ว");

        var openOrders = session.Orders.Any(o => o.Status == PosOrderStatus.Open || o.Status == PosOrderStatus.InProgress);
        if (openOrders) throw new InvalidOperationException("ยังมีออเดอร์ที่ยังไม่เสร็จ กรุณาปิดออเดอร์ก่อน");

        var totalCashSales = session.Orders
            .Where(o => o.Status == PosOrderStatus.Completed)
            .SelectMany(o => o.Payments)
            .Where(p => p.PaymentMethod == PaymentMethod.Cash)
            .Sum(p => p.Amount - p.ChangeAmount);

        session.ClosedByUserId = userId;
        session.ClosedAt = DateTime.UtcNow;
        session.ClosingBalance = request.ClosingBalance;
        session.ExpectedBalance = session.OpeningBalance + totalCashSales;
        session.Status = PosSessionStatus.Closed;
        if (request.Notes != null) session.Notes = request.Notes;
        await _db.SaveChangesAsync();
        return await MapSessionAsync(session);
    }

    public async Task<SessionResponse> GetSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.PosSessions.Include(s => s.Terminal)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกะการขาย");
        return await MapSessionAsync(session);
    }

    public async Task<List<SessionResponse>> GetSessionsAsync(Guid companyId, Guid? terminalId = null, bool? isOpen = null)
    {
        var query = _db.PosSessions.Include(s => s.Terminal).Where(s => s.CompanyId == companyId);
        if (terminalId.HasValue) query = query.Where(s => s.TerminalId == terminalId.Value);
        if (isOpen == true) query = query.Where(s => s.Status == PosSessionStatus.Open);
        else if (isOpen == false) query = query.Where(s => s.Status == PosSessionStatus.Closed);

        var sessions = await query.OrderByDescending(s => s.OpenedAt).Take(50).ToListAsync();
        var result = new List<SessionResponse>();
        foreach (var s in sessions) result.Add(await MapSessionAsync(s));
        return result;
    }

    // ==================== Mappers ====================

    /// <summary>ชื่อ+รหัสสาขาสรรพากร และชื่อคลัง ของทั้งบริษัท — โหลดทีเดียวแล้วส่งเข้า
    /// mapper (แยก "โหลดข้อมูล" ออกจาก "ตรรกะแปลง" เพื่อให้เส้นรายการเดียวกับเส้น
    /// หลายรายการใช้ตัวแปลงตัวเดียวกัน ไม่เกิดสำเนาที่สอง)</summary>
    private async Task<(Dictionary<Guid, (string Name, string? TaxCode)> Branches,
                        Dictionary<Guid, string> Warehouses)> LoadScopeNamesAsync(Guid companyId)
    {
        var branches = await _db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new { b.Id, b.Name, b.TaxBranchCode })
            .ToListAsync();
        var warehouses = await _db.Warehouses.AsNoTracking()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted)
            .Select(w => new { w.Id, w.Name })
            .ToListAsync();
        return (branches.ToDictionary(b => b.Id, b => (b.Name, b.TaxBranchCode)),
                warehouses.ToDictionary(w => w.Id, w => w.Name));
    }

    private async Task<TerminalResponse> MapTerminalAsync(PosTerminal t, int openSessions)
        => MapTerminal(t, openSessions, await LoadScopeNamesAsync(t.CompanyId));

    private static TerminalResponse MapTerminal(PosTerminal t, int openSessions,
        (Dictionary<Guid, (string Name, string? TaxCode)> Branches, Dictionary<Guid, string> Warehouses) names)
    {
        string? branchName = null, branchTaxCode = null;
        if (t.BranchId is Guid bid && names.Branches.TryGetValue(bid, out var b))
        {
            branchName = b.Name;
            // "00000" = สำนักงานใหญ่ · null = สาขายังไม่กรอกรหัสสรรพากร (ห้ามเดาเป็น
            // สำนักงานใหญ่ — ค่า default ที่แต่งขึ้นอันตรายกว่าการไม่ตอบ)
            branchTaxCode = b.TaxCode;
        }
        string? warehouseName = null;
        if (t.WarehouseId is Guid wid && names.Warehouses.TryGetValue(wid, out var wn)) warehouseName = wn;

        return new TerminalResponse(
            t.Id, t.Name, t.BusinessMode, t.IsActive, t.Location, t.SettingsJson, openSessions,
            t.BranchId, branchName, branchTaxCode,
            t.WarehouseId, warehouseName,
            t.CashAccountId, t.BankAccountId, t.AbbreviatedInvoicePrefix);
    }

    private async Task<SessionResponse> MapSessionAsync(PosSession s)
    {
        var orderStats = await _db.PosOrders
            .Where(o => o.SessionId == s.Id && o.Status == PosOrderStatus.Completed)
            .GroupBy(o => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(o => o.NetAmount) })
            .FirstOrDefaultAsync();

        return new SessionResponse(
            s.Id, s.TerminalId, s.Terminal?.Name ?? "", s.OpenedByUserId, s.ClosedByUserId,
            s.OpenedAt, s.ClosedAt, s.OpeningBalance, s.ClosingBalance, s.ExpectedBalance,
            s.Status, s.Notes, orderStats?.Count ?? 0, orderStats?.Total ?? 0);
    }
}
