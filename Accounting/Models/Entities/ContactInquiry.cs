namespace Accounting.Models.Entities;

public class ContactInquiry : BaseEntity
{
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string Subject { get; set; } = null!;
    public string Message { get; set; } = null!;
    public bool IsRead { get; set; } = false;
    public bool IsReplied { get; set; } = false;
    public string? IpAddress { get; set; }
}
