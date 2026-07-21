namespace Accounting.Models.Entities;

public class ErrorLog
{
    public long Id { get; set; }
    public string? RequestPath { get; set; }
    public string? HttpMethod { get; set; }
    public string? QueryString { get; set; }
    public int StatusCode { get; set; }
    public string ExceptionType { get; set; } = "";
    public string Message { get; set; } = "";
    public string? StackTrace { get; set; }
    public string? InnerException { get; set; }
    public string? UserId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
