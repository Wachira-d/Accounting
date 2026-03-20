using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Seed บัญชี System Admin เริ่มต้น
/// Email: admin@acctplatform.com / Password: Admin@1234
/// </summary>
public static class SeedAdminUser
{
    public static async Task SeedAsync(AccountingDbContext db)
    {
        const string adminEmail = "admin@acctplatform.com";

        if (await db.Users.AnyAsync(u => u.Email == adminEmail))
            return;

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@1234"),
            FullName = "System Administrator",
            IsSystemAdmin = true,
            Status = Models.Enums.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };

        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
}
