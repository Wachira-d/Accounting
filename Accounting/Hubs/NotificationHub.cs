using Accounting.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    private readonly AccountingDbContext _db;

    public NotificationHub(AccountingDbContext db)
    {
        _db = db;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId != null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user:{userId}");
        }
        await base.OnDisconnectedAsync(exception);
    }

    // Join company-specific group for broadcast notifications
    public async Task JoinCompanyGroup(string companyId)
    {
        if (!Guid.TryParse(companyId, out var companyGuid))
            throw new HubException("Invalid company ID format.");

        var userId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId == null || !Guid.TryParse(userId, out var userGuid))
            throw new HubException("User not authenticated.");

        // Verify user has access to this company
        var hasAccess = await _db.CompanyUsers
            .AnyAsync(cu => cu.CompanyId == companyGuid && cu.UserId == userGuid);

        if (!hasAccess)
            throw new HubException("Access denied to this company.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"company:{companyId}");
    }

    public async Task LeaveCompanyGroup(string companyId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"company:{companyId}");
    }
}
