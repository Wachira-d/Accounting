namespace Accounting.Services.Interfaces;

public interface ILineNotifyService
{
    Task SendMessageAsync(string message);
}
