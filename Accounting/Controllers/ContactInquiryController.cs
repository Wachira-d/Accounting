using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/contact")]
public class ContactInquiryController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILineNotifyService _lineNotifyService;
    private readonly ILogger<ContactInquiryController> _logger;

    public ContactInquiryController(
        AccountingDbContext db,
        ILineNotifyService lineNotifyService,
        ILogger<ContactInquiryController> logger)
    {
        _db = db;
        _lineNotifyService = lineNotifyService;
        _logger = logger;
    }

    /// <summary>
    /// Public endpoint - no authentication required.
    /// Receives contact form submissions and sends LINE notification.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> SubmitInquiry([FromBody] ContactInquiryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Subject) ||
            string.IsNullOrWhiteSpace(request.Message))
        {
            return Ok(new ApiResponse<object>(false, null, "กรุณากรอกข้อมูลให้ครบถ้วน"));
        }

        var inquiry = new ContactInquiry
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            Phone = request.Phone?.Trim(),
            Company = request.Company?.Trim(),
            Subject = request.Subject.Trim(),
            Message = request.Message.Trim(),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.ContactInquiries.Add(inquiry);
        await _db.SaveChangesAsync();

        // Send LINE notification
        var lineMessage = $"📩 มีข้อความติดต่อใหม่!\n" +
                          $"👤 ชื่อ: {inquiry.Name}\n" +
                          $"📧 อีเมล: {inquiry.Email}\n" +
                          (string.IsNullOrEmpty(inquiry.Phone) ? "" : $"📱 โทร: {inquiry.Phone}\n") +
                          (string.IsNullOrEmpty(inquiry.Company) ? "" : $"🏢 บริษัท: {inquiry.Company}\n") +
                          $"📋 หัวข้อ: {inquiry.Subject}\n" +
                          $"💬 ข้อความ: {inquiry.Message}\n" +
                          $"🕐 เวลา: {DateTime.UtcNow.AddHours(7):dd/MM/yyyy HH:mm} น.";

        _ = Task.Run(async () =>
        {
            try { await _lineNotifyService.SendMessageAsync(lineMessage); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to send LINE notification for inquiry {Id}", inquiry.Id); }
        });

        return Ok(new ApiResponse<object>(true, new { id = inquiry.Id }, "ส่งข้อความสำเร็จ ขอบคุณที่ติดต่อเรา"));
    }

    /// <summary>
    /// Admin endpoint - list all inquiries
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "SystemAdmin")]
    public async Task<IActionResult> GetInquiries([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] bool? isRead = null)
    {
        var query = _db.ContactInquiries.AsNoTracking().OrderByDescending(c => c.CreatedAt).AsQueryable();
        if (isRead.HasValue)
            query = query.Where(c => c.IsRead == isRead.Value);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(c => new
        {
            c.Id,
            c.Name,
            c.Email,
            c.Phone,
            c.Company,
            c.Subject,
            c.Message,
            c.IsRead,
            c.IsReplied,
            c.IpAddress,
            c.CreatedAt,
        }).ToListAsync();

        return Ok(new ApiResponse<object>(true, new { items, total, page, pageSize }));
    }

    /// <summary>
    /// Mark inquiry as read
    /// </summary>
    [HttpPut("{id}/read")]
    [Authorize(Roles = "SystemAdmin")]
    public async Task<IActionResult> MarkAsRead(Guid id)
    {
        var inquiry = await _db.ContactInquiries.FindAsync(id);
        if (inquiry == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบข้อมูล"));
        inquiry.IsRead = true;
        inquiry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "อัปเดตสำเร็จ"));
    }
}

public record ContactInquiryRequest(
    string Name,
    string Email,
    string? Phone,
    string? Company,
    string Subject,
    string Message
);
