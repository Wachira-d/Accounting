using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ที่เก็บไฟล์กลาง — สถานะ + เครื่องมือย้ายไฟล์ดิสก์ → object storage (SystemAdmin เท่านั้น · ทีม FS 2026-11-02)
/// <para>ขั้นตอนเจ้าของ: ดู <c>erp-review/2026-11-02/FILE_STORAGE_AUDIT.md</c> §"ขั้นตอน deploy"</para>
/// </summary>
[ApiController]
[Route("api/admin/file-storage")]
[Authorize(Roles = "SystemAdmin")]
public class AdminFileStorageController : ControllerBase
{
    private readonly IFileStorage _storage;
    private readonly FileStorageMigrationService _migration;

    public AdminFileStorageController(IFileStorage storage, FileStorageMigrationService migration)
    {
        _storage = storage;
        _migration = migration;
    }

    /// <summary>ผู้ให้บริการที่ใช้อยู่ · ปลายทาง (ไม่มีความลับ) · โฟลเดอร์ที่เครื่องมือย้ายจะเดิน</summary>
    /// <para>โหมด S3: ยิง HEAD ทดสอบสิทธิ์คีย์กับ bucket จริง (<c>probe</c>) — คีย์ผิด/ไม่มีสิทธิ์เห็นที่นี่ก่อนผู้ใช้อัปโหลดล้ม</para>
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<object>>> Status(CancellationToken ct)
        => Ok(new ApiResponse<object>(true, new
        {
            provider = _storage.ProviderName,
            target = _migration.TargetDescription,
            canMigrate = _migration.HasRemoteTarget,
            probe = await _migration.ProbeAsync(ct),
            roots = _storage.Locator.Roots.Select(r => new { r.Prefix, r.AbsolutePath }),
            migrationDirectories = _storage.Locator.MigrationDirectories(),
        }));

    /// <summary>ย้ายไฟล์ดิสก์ → object storage ทีละ <paramref name="limit"/> ไฟล์ · <paramref name="dryRun"/> = นับอย่างเดียว (ค่าเริ่มต้น) ·
    /// ไม่ลบไฟล์บนดิสก์ · เรียกซ้ำจน <c>remaining = 0</c></summary>
    [HttpPost("migrate")]
    public async Task<ActionResult<ApiResponse<FileStorageMigrationReport>>> Migrate([FromQuery] bool dryRun = true, [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (!_migration.HasRemoteTarget)
            return BadRequest(new ApiResponse<FileStorageMigrationReport>(false, null,
                "ยังไม่ได้ตั้งที่เก็บปลายทาง — ตั้ง FileStorage:Provider=S3 และ FileStorage:S3:* แล้ว deploy ก่อน"));
        var report = await _migration.RunAsync(dryRun, limit, JwtHelper.GetUserIdFromClaims(User).ToString(), ct);
        if (report == null)
            return Conflict(new ApiResponse<FileStorageMigrationReport>(false, null, "มีอีกเครื่องกำลังย้ายไฟล์อยู่ — รอให้รอบนั้นจบแล้วเรียกใหม่"));
        // Success = ไม่มีล้ม/ไม่มีพาธนอกราก (ไฟล์และแถวในฐาน) · "ครบแล้ว" ดูที่ report.Complete — ข้อความสรุปประกอบจากตัวเลขครบทุกช่อง
        return Ok(new ApiResponse<FileStorageMigrationReport>(report.Ok, report, report.Note));
    }
}
