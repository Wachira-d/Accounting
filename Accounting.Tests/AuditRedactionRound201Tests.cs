using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL · ฝ่ายค้าน GW รอบสอง RV2-1 — ช่องลับห้ามเข้า audit (append-only) เป็นข้อความเปล่า · แถวเก่าห้ามแสดงซ้ำ ·
/// ทุกเคสสองทิศ: ช่องลับถูกปิด · ช่องธรรมดา/ธง/ตัวนับ/hash ของ chain ไม่ถูกแตะ
/// </summary>
public class AuditRedactionRound201Tests
{
    private static AccountingDbContext Offline() => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none;Timeout=1").Options);

    [Theory]
    [InlineData("WebhookToken")]
    [InlineData("LiveSecretKeyProtected")]
    [InlineData("WebhookSecretProtected")]
    [InlineData("PasswordHash")]
    [InlineData("ApiKeyEncrypted")]
    [InlineData("ApiKeyHash")]
    [InlineData("LineChannelSecret")]
    [InlineData("LineChannelAccessToken")]
    [InlineData("EtaxCertificatePassword")]
    [InlineData("EncryptedCredentials")]
    [InlineData("RefreshToken")]
    [InlineData("webhookToken")]          // JSON camelCase
    public void Secret_fields_are_sensitive(string name) => Assert.True(AuditRedaction.IsSensitive(name));

    [Theory]
    [InlineData("TokenExpiresAt")]
    [InlineData("PasswordResetTokenExpiry")]
    [InlineData("OutputTokens")]
    [InlineData("MaxApiKeys")]
    [InlineData("HasApiKey")]
    [InlineData("LastTokenWebhookAt")]
    [InlineData("ApiKeyPrefix")]
    [InlineData("TestPublicKey")]
    [InlineData("RowHash")]
    [InlineData("PrevHash")]
    [InlineData("FileHash")]
    [InlineData("DisplayName")]
    [InlineData("")]
    public void Ordinary_fields_flags_and_chain_hashes_are_not(string name) => Assert.False(AuditRedaction.IsSensitive(name));

    [Fact]
    public void Value_masks_only_present_secrets()
    {
        Assert.Equal(AuditRedaction.Mask, AuditRedaction.Value("WebhookToken", "tok_abc"));
        Assert.Null(AuditRedaction.Value("WebhookToken", null));            // ล้างค่า = ตรวจย้อนได้ว่าเมื่อไร
        Assert.Equal("", AuditRedaction.Value("WebhookToken", ""));
        Assert.Equal("ร้าน A", AuditRedaction.Value("DisplayName", "ร้าน A"));
    }

    [Fact]
    public void Capture_on_create_and_rotate_never_stores_the_token()
    {
        using var db = Offline();
        var cfg = new PaymentProviderConfig
        {
            CompanyId = Guid.NewGuid(), ProviderCode = "omise", DisplayName = "Omise ร้าน A",
            WebhookToken = "tok_SECRET_create_123", LiveSecretKeyProtected = "CfDJ8-protected-blob",
        };
        db.Add(cfg);
        var created = AuditTrailService.CaptureAuditEntries(db.ChangeTracker, null, null, null).Single(a => a.EntityType == nameof(PaymentProviderConfig));
        Assert.DoesNotContain("tok_SECRET_create_123", created.NewValues);
        Assert.DoesNotContain("CfDJ8-protected-blob", created.NewValues);
        Assert.Contains(AuditRedaction.Mask, created.NewValues);
        Assert.Contains("omise", created.NewValues);                       // ทิศตรงข้าม: ช่องธรรมดายังอยู่

        db.ChangeTracker.Clear();
        db.Attach(cfg);
        cfg.WebhookToken = "tok_SECRET_rotated_456";                        // rotate
        cfg.DisplayName = "Omise ร้าน A (ใหม่)";
        var rotated = AuditTrailService.CaptureAuditEntries(db.ChangeTracker, null, null, null).Single(a => a.EntityType == nameof(PaymentProviderConfig));
        Assert.DoesNotContain("tok_SECRET_create_123", rotated.OldValues);
        Assert.DoesNotContain("tok_SECRET_rotated_456", rotated.NewValues);
        Assert.Contains("WebhookToken", rotated.NewValues);                 // เห็นว่าเปลี่ยน · ไม่เห็นค่า
        Assert.Contains("(ใหม่)", System.Text.RegularExpressions.Regex.Unescape(rotated.NewValues!));
    }

    [Fact]
    public void Legacy_rows_are_masked_on_read_and_clean_rows_return_unchanged()
    {
        var legacy = "{\"DisplayName\":\"A\",\"WebhookToken\":\"tok_old\",\"Nested\":{\"apiKey\":\"k\",\"items\":[{\"PasswordHash\":\"h\"}]},\"LastTokenWebhookAt\":null}";
        var shown = AuditRedaction.RedactJson(legacy)!;
        Assert.DoesNotContain("tok_old", shown);
        Assert.DoesNotContain("\"k\"", shown);
        Assert.DoesNotContain("\"h\"", shown);
        Assert.Contains("\"DisplayName\":\"A\"", shown);
        // ทิศตรงข้าม: ไม่มีช่องลับ / ไม่ใช่ JSON / ค่าว่าง ⇒ ข้อความเดิมทุกตัวอักษร
        const string clean = "{ \"status\": \"Approved\" }";
        Assert.Same(clean, AuditRedaction.RedactJson(clean));
        Assert.Equal("ไม่ใช่ json", AuditRedaction.RedactJson("ไม่ใช่ json"));
        Assert.Null(AuditRedaction.RedactJson(null));
        Assert.Equal("{\"WebhookToken\":null}", AuditRedaction.RedactJson("{\"WebhookToken\":null}"));
    }
}
