namespace Accounting.Services.Interfaces;

public interface IErrorLogService
{
    Task LogErrorAsync(Exception exception, string source, string? userId = null);
}
