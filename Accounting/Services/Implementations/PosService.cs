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

    public PosService(AccountingDbContext db, IAccountingService accountingService, ILogger<PosService> logger, IEmailSenderFactory? emailFactory = null)
    {
        _db = db;
        _accountingService = accountingService;
        _logger = logger;
        _emailFactory = emailFactory;
    }

    // ==================== Terminal ====================

    public async Task<TerminalResponse> CreateTerminalAsync(Guid companyId, CreateTerminalRequest request)
    {
        var terminal = new PosTerminal
        {
            CompanyId = companyId,
            Name = request.Name,
            BusinessMode = request.BusinessMode,
            Location = request.Location,
            SettingsJson = request.SettingsJson
        };
        _db.PosTerminals.Add(terminal);
        await _db.SaveChangesAsync();
        return MapTerminal(terminal, 0);
    }

    public async Task<List<TerminalResponse>> GetTerminalsAsync(Guid companyId)
    {
        var terminals = await _db.PosTerminals
            .Where(t => t.CompanyId == companyId)
            .Select(t => new { Terminal = t, OpenSessions = t.Sessions.Count(s => s.Status == PosSessionStatus.Open) })
            .ToListAsync();
        return terminals.Select(x => MapTerminal(x.Terminal, x.OpenSessions)).ToList();
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
        await _db.SaveChangesAsync();
        var openCount = await _db.PosSessions.CountAsync(s => s.TerminalId == terminalId && s.Status == PosSessionStatus.Open);
        return MapTerminal(terminal, openCount);
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

    private static TerminalResponse MapTerminal(PosTerminal t, int openSessions) => new(
        t.Id, t.Name, t.BusinessMode, t.IsActive, t.Location, t.SettingsJson, openSessions);

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
