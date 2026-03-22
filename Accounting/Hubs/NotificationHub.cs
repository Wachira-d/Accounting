using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Accounting.Hubs;

[Authorize]
public class NotificationHub : Hub
{
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
        await Groups.AddToGroupAsync(Context.ConnectionId, $"company:{companyId}");
    }

    public async Task LeaveCompanyGroup(string companyId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"company:{companyId}");
    }
}
