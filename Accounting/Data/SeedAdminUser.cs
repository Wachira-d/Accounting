using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Seed บัญชี System Admin เริ่มต้น
/// กำหนดรหัสผ่านผ่าน environment variable ADMIN_DEFAULT_PASSWORD หรือ config SeedAdmin:Password
/// </summary>
public static class SeedAdminUser
{
    public static async Task SeedAsync(AccountingDbContext db, IConfiguration? configuration = null)
    {
        const string adminEmail = "admin@acctplatform.com";

        if (await db.Users.AnyAsync(u => u.Email == adminEmail))
            return;

        var defaultPassword = Environment.GetEnvironmentVariable("ADMIN_DEFAULT_PASSWORD")
            ?? configuration?["SeedAdmin:Password"]
            ?? "Admin@1234";

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword),
            FullName = "System Administrator",
            IsSystemAdmin = true,
            Status = Models.Enums.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };

        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
}
