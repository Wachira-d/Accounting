using System.Security.Cryptography;
using Accounting.Data;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ส่งสลิปเงินเดือนทาง LINE — ดู <see cref="IPayslipLineDeliveryService"/>.
/// นโยบาย: (1) พนักงานผูก LINE เองผ่าน OA บริษัท (รหัส 6 หลัก); (2) ข้อความ LINE
/// ซ่อนยอดเงิน โชว์แค่งวด + ปุ่ม; (3) ปุ่มลิงก์ไป secure token (หมดอายุ 7 วัน)
/// ที่ stream PDF + log การเข้าถึงตาม PDPA ม.37.</summary>
public class PayslipLineDeliveryService : IPayslipLineDeliveryService
{
    private readonly AccountingDbContext _db;
    private readonly ILineNotifyService _line;
    private readonly IPayrollService _payroll;
    private readonly IConfiguration _config;
    private readonly ILogger<PayslipLineDeliveryService> _logger;

    private static readonly string[] _thMonths = { "", "มกราคม", "กุมภาพันธ์", "มีนาคม",
        "เมษายน", "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม",
        "พฤศจิกายน", "ธันวาคม" };

    private const int TokenValidDays = 7;
    private const int BindCodeValidHours = 24;

    public PayslipLineDeliveryService(AccountingDbContext db, ILineNotifyService line,
        IPayrollService payroll, IConfiguration config,
        ILogger<PayslipLineDeliveryService> logger)
    {
        _db = db; _line = line; _payroll = payroll; _config = config; _logger = logger;
    }

    // ===== 1) Self-service bind code =====

    public async Task<(string Code, string? AddFriendUrl)> IssueBindCodeAsync(
        Guid companyId, Guid employeeId, string? actorEmail)
    {
        // พนักงานต้องอยู่ในบริษัทนี้ (tenant isolation)
        var exists = await _db.Employees
            .AnyAsync(e => e.Id == employeeId && e.CompanyId == companyId);
        if (!exists) throw new KeyNotFoundException("ไม่พบพนักงาน");

        // ยกเลิกรหัสเก่าที่ยังไม่ถูกใช้ — มีได้ทีละ 1 รหัสต่อพนักงาน
        await _db.EmployeeLineBindCodes
            .Where(c => c.CompanyId == companyId && c.EmployeeId == employeeId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UsedAt, DateTime.UtcNow)
                .SetProperty(c => c.UsedByLineUserId, "expired-by-reissue"));

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _db.EmployeeLineBindCodes.Add(new EmployeeLineBindCode
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddHours(BindCodeValidHours),
            CreatedBy = actorEmail,
        });
        await _db.SaveChangesAsync();

        var basicId = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.LineOaBasicId)
            .FirstOrDefaultAsync();
        string? addFriendUrl = null;
        if (!string.IsNullOrWhiteSpace(basicId))
        {
            // basic id เก็บแบบ "@xxx" หรือ "xxx" ก็ได้ — line.me ต้องการไม่มี '@'
            var id = basicId.TrimStart('@');
            addFriendUrl = $"https://line.me/R/ti/p/@{id}";
        }
        return (code, addFriendUrl);
    }

    /// <summary>เรียกจาก LINE bot เมื่อพนักงานส่ง "สลิป {รหัส}". จับคู่รหัส →
    /// เขียน Employee.LineUserId. คืนข้อความตอบกลับ (ไทย) หรือ null ถ้าไม่ match.</summary>
    public async Task<string?> TryBindFromLineAsync(string lineUserId, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return null;
        var row = await _db.EmployeeLineBindCodes
            .Include(c => c.Employee)
            .Where(c => c.Code == code && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();
        if (row == null) return null;

        row.UsedAt = DateTime.UtcNow;
        row.UsedByLineUserId = lineUserId;
        row.Employee.LineId = lineUserId;   // = push userId (เดียวกับที่ NotificationEngine ใช้)
        await _db.SaveChangesAsync();

        var name = $"{row.Employee.FirstNameTh} {row.Employee.LastNameTh}".Trim();
        return $"✅ ผูกบัญชีรับสลิปเงินเดือนสำเร็จ — สวัสดีคุณ {name}\n" +
               "ตั้งแต่นี้สลิปเงินเดือนแต่ละงวดจะส่งมาที่ LINE นี้";
    }

    // ===== 2) Status =====

    public async Task<(bool Bound, string? MaskedLineUserId)> GetLineStatusAsync(
        Guid companyId, Guid employeeId)
    {
        var lineId = await ResolveLineUserIdAsync(companyId, employeeId);
        if (string.IsNullOrWhiteSpace(lineId)) return (false, null);
        // mask: U1234abcd… → U123••••cd
        var masked = lineId.Length > 8
            ? $"{lineId[..4]}••••{lineId[^2..]}"
            : "••••";
        return (true, masked);
    }

    // ===== 3) Send =====

    public async Task<PayslipLineSendResult> SendPayslipAsync(Guid companyId,
        Guid payrollRunId, Guid employeeId, string? actorEmail, CancellationToken ct = default)
    {
        var lineId = await ResolveLineUserIdAsync(companyId, employeeId, ct);
        if (string.IsNullOrWhiteSpace(lineId)) return PayslipLineSendResult.NotBound;

        // ต้องมี payroll detail ในงวดนี้จริง
        var detail = await _db.Set<PayrollDetail>().AsNoTracking()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .FirstOrDefaultAsync(d => d.PayrollRunId == payrollRunId
                && d.EmployeeId == employeeId && d.CompanyId == companyId, ct);
        if (detail == null) return PayslipLineSendResult.NoPayrollDetail;

        if (!await IsLineConfiguredAsync(companyId, ct))
            return PayslipLineSendResult.LineNotConfigured;

        // สร้าง secure token (หมดอายุ 7 วัน). ยกเลิก token เก่าของงวด+คนเดียวกัน
        // ที่ยังไม่หมดอายุ เพื่อไม่ให้ลิงก์เก่าใช้ได้พร้อมกันหลายอัน.
        await _db.PayslipShareTokens
            .Where(t => t.CompanyId == companyId && t.PayrollRunId == payrollRunId
                && t.EmployeeId == employeeId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);

        var token = NewToken();
        _db.PayslipShareTokens.Add(new PayslipShareToken
        {
            CompanyId = companyId,
            PayrollRunId = payrollRunId,
            EmployeeId = employeeId,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(TokenValidDays),
            Channel = "LINE",
            CreatedBy = actorEmail,
        });
        await _db.SaveChangesAsync(ct);

        var run = detail.PayrollRun;
        var empName = $"{detail.Employee.TitleTh}{detail.Employee.FirstNameTh} {detail.Employee.LastNameTh}".Trim();
        var period = $"{_thMonths[Math.Clamp(run.Month, 1, 12)]} {run.Year + 543}";
        var coName = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "บริษัท";
        var url = BuildPublicUrl(token);

        // Flex card — ซ่อนยอดเงิน (ตามนโยบาย PDPA): โชว์แค่ชื่อ/งวด + ปุ่ม
        var flex = BuildPayslipFlex(coName, empName, period, url);
        var alt = $"สลิปเงินเดือน {period} พร้อมแล้ว — แตะเพื่อดู/ดาวน์โหลด";

        try
        {
            await _line.PushFlexToUserAsync(companyId, lineId, alt, flex);
            _logger.LogInformation("ส่งสลิป LINE run {Run} emp {Emp} สำเร็จ", payrollRunId, employeeId);
            return PayslipLineSendResult.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ส่งสลิป LINE ล้มเหลว run {Run} emp {Emp}", payrollRunId, employeeId);
            return PayslipLineSendResult.LineFailed;
        }
    }

    public async Task<PayslipLineBulkResult> SendPayslipForRunAsync(Guid companyId,
        Guid payrollRunId, string? actorEmail, CancellationToken ct = default)
    {
        var employeeIds = await _db.Set<PayrollDetail>().AsNoTracking()
            .Where(d => d.PayrollRunId == payrollRunId && d.CompanyId == companyId)
            .Select(d => d.EmployeeId)
            .ToListAsync(ct);

        int sent = 0, notBound = 0, failed = 0;
        foreach (var empId in employeeIds)
        {
            var r = await SendPayslipAsync(companyId, payrollRunId, empId, actorEmail, ct);
            switch (r)
            {
                case PayslipLineSendResult.Sent: sent++; break;
                case PayslipLineSendResult.NotBound: notBound++; break;
                default: failed++; break;
            }
        }
        return new PayslipLineBulkResult(employeeIds.Count, sent, notBound, failed);
    }

    // ===== 4) Public token resolve =====

    public async Task<(byte[] Pdf, string FileName)?> ResolvePublicPayslipAsync(string token,
        string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var row = await _db.PayslipShareTokens
            .FirstOrDefaultAsync(t => t.Token == token, ct);
        if (row == null || row.RevokedAt != null || row.ExpiresAt <= DateTime.UtcNow)
            return null;

        // สร้าง PDF สลิป (reuse pipeline เดิม — โลโก้/ธีม/mask ครบ)
        PayslipResponse slip;
        try
        {
            slip = await _payroll.GeneratePayslipAsync(row.CompanyId, row.PayrollRunId, row.EmployeeId);
        }
        catch (KeyNotFoundException)
        {
            return null; // payroll detail หายไป (เช่นงวดถูกลบ)
        }

        row.AccessCount += 1;
        row.LastAccessedAt = DateTime.UtcNow;

        // PDPA ม.37(4) — log การอ่าน PII (ผ่าน token = anonymous actor)
        _db.Set<PdpaPiiAccessLog>().Add(new PdpaPiiAccessLog
        {
            CompanyId = row.CompanyId,
            ActorUserId = Guid.Empty,
            ActorEmail = "line-payslip-token",
            SubjectType = "Employee",
            SubjectId = row.EmployeeId,
            FieldName = "Payslip",
            Operation = "Read",
            Purpose = "ดาวน์โหลดสลิปเงินเดือนผ่านลิงก์ LINE",
            At = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        });
        await _db.SaveChangesAsync(ct);

        return (slip.PdfData, slip.FileName);
    }

    // ===== Helpers =====

    /// <summary>Employee.LineId ก่อน (HR กรอก หรือพนักงานผูกเอง); ถ้าไม่มีลองใช้
    /// User.LineUserId ที่ผูกกับพนักงาน (กรณีพนักงานมีบัญชีล็อกอินและผูก LINE ไว้).</summary>
    private async Task<string?> ResolveLineUserIdAsync(Guid companyId, Guid employeeId,
        CancellationToken ct = default)
    {
        var row = await _db.Employees.AsNoTracking()
            .Where(e => e.Id == employeeId && e.CompanyId == companyId)
            .Select(e => new { e.LineId, UserLine = e.User != null ? e.User.LineUserId : null })
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        return !string.IsNullOrWhiteSpace(row.LineId) ? row.LineId : row.UserLine;
    }

    private async Task<bool> IsLineConfiguredAsync(Guid companyId, CancellationToken ct)
    {
        var s = await _db.Set<CompanySettings>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if (s != null && s.LineEnabled && !string.IsNullOrWhiteSpace(s.LineChannelAccessToken))
            return true;
        // fallback: global token (การตั้งค่าเก่า)
        return !string.IsNullOrWhiteSpace(_config["Line:ChannelAccessToken"]);
    }

    private string BuildPublicUrl(string token)
    {
        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        return $"{baseUrl}/api/public/payslip/{token}";
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static object BuildPayslipFlex(string coName, string empName, string period, string url) => new
    {
        type = "bubble",
        body = new
        {
            type = "box", layout = "vertical", spacing = "md", contents = new object[]
            {
                new { type = "text", text = "🧾 สลิปเงินเดือน", weight = "bold", size = "lg", color = "#1e40af" },
                new { type = "text", text = coName, size = "sm", color = "#666666", wrap = true },
                new {
                    type = "box", layout = "vertical", margin = "md", spacing = "sm",
                    contents = new object[]
                    {
                        new {
                            type = "box", layout = "baseline", contents = new object[]
                            {
                                new { type = "text", text = "พนักงาน", color = "#999999", size = "sm", flex = 2 },
                                new { type = "text", text = empName, size = "sm", weight = "bold", flex = 5, wrap = true },
                            }
                        },
                        new {
                            type = "box", layout = "baseline", contents = new object[]
                            {
                                new { type = "text", text = "งวด", color = "#999999", size = "sm", flex = 2 },
                                new { type = "text", text = period, size = "sm", weight = "bold", flex = 5 },
                            }
                        },
                    }
                },
                new { type = "text", text = "แตะปุ่มด้านล่างเพื่อดูหรือดาวน์โหลดสลิป (ลิงก์หมดอายุใน 7 วัน)",
                      size = "xs", color = "#999999", wrap = true, margin = "md" },
            }
        },
        footer = new
        {
            type = "box", layout = "vertical", spacing = "sm", contents = new object[]
            {
                new {
                    type = "button", style = "primary", color = "#1e40af",
                    action = new { type = "uri", label = "ดู / ดาวน์โหลดสลิป", uri = url }
                }
            }
        }
    };
}
