using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

public class ErrorLogService : IErrorLogService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ErrorLogService> _logger;

    public ErrorLogService(AccountingDbContext db, ILogger<ErrorLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogErrorAsync(Exception exception, string source, string? userId = null)
    {
        try
        {
            var errorLog = new ErrorLog
            {
                RequestPath = source,
                HttpMethod = "INTERNAL",
                StatusCode = 500,
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                InnerException = exception.InnerException?.Message,
                UserId = userId,
                Timestamp = DateTime.UtcNow
            };

            _db.ErrorLogs.Add(errorLog);
            await _db.SaveChangesAsync();
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx, "Failed to save error log to database for source: {Source}", source);
        }
    }
}
