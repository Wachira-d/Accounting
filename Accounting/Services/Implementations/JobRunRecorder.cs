using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>WP-F2: บันทึกผล background job ลง JobRunLog ผ่าน scope ของตัวเอง
/// (ไม่พึ่ง DbContext ของ caller ที่อาจ error อยู่) → บันทึกได้เสมอ.</summary>
public class JobRunRecorder : IJobRunRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobRunRecorder> _logger;

    public JobRunRecorder(IServiceScopeFactory scopeFactory, ILogger<JobRunRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<int> TrackAsync(string jobName, Func<Task<int>> work)
    {
        var start = DateTime.UtcNow;
        try
        {
            var items = await work();
            await RecordAsync(jobName, true, $"ทำ {items} รายการ", items, start);
            return items;
        }
        catch (Exception ex)
        {
            await RecordAsync(jobName, false, ex.Message, 0, start);
            throw;
        }
    }

    public async Task RecordAsync(string jobName, bool success, string? message, int itemsProcessed, DateTime startedAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var finished = DateTime.UtcNow;
            db.Set<JobRunLog>().Add(new JobRunLog
            {
                JobName = jobName,
                StartedAt = startedAt,
                FinishedAt = finished,
                Success = success,
                Message = message != null && message.Length > 1000 ? message[..1000] : message,
                ItemsProcessed = itemsProcessed,
                DurationMs = (long)(finished - startedAt).TotalMilliseconds
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // ห้ามให้การบันทึกผลทำ job พัง
            _logger.LogWarning(ex, "บันทึก JobRunLog ไม่สำเร็จ (job {Job})", jobName);
        }
    }
}
