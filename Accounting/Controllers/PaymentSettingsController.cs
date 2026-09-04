using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// ตั้งค่าการรับชำระเงินผ่าน gateway (ต่อบริษัท)
///
/// <para><b>ด่านสำคัญ</b>: ปุ่ม "เปิดใช้จริง" ถูกล็อกจนกว่าจะ<b>ทดสอบผ่าน</b> — ไม่ใช่แค่
/// "คีย์ไม่ว่าง" แต่ต้องเคยได้รับ webhook จริงด้วย · เคสที่พบบ่อยที่สุดคือคีย์ถูกแต่ลืมตั้ง
/// URL ในแดชบอร์ดของผู้ให้บริการ ⇒ ลูกค้าจ่ายเงินแล้วออเดอร์ไม่อัปเดต และเจ้าของร้าน
/// ไม่รู้จนลูกค้าโทรมา</para>
///
/// <para>secret key **ไม่เคยถูกส่งกลับ** — คืนแค่ 4 ตัวท้ายพอให้ผู้ใช้ยืนยันว่าใส่ตัวไหนไว้</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/payment-settings")]
[Authorize]
public class PaymentSettingsController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly ILogger<PaymentSettingsController> _logger;

    public PaymentSettingsController(AccountingDbContext db, ISecretProtector secrets,
        IEnumerable<IPaymentProvider> providers, ILogger<PaymentSettingsController> logger)
    { _db = db; _secrets = secrets; _providers = providers; _logger = logger; }

    public sealed record ProviderOption(string Code, string DisplayName,
        List<string> Methods, bool SupportsRefund, bool SupportsWebhook,
        // เงินเข้าธนาคารทันทีไหม — หน้ากระทบยอดใช้ค่านี้กรองว่า "เจ้าไหนมีรอบโอน
        // ให้บันทึก" · ห้ามให้หน้าเว็บเดาจากชื่อเจ้า (จะกลายเป็นสำเนากติกาชุดที่สอง)
        bool SettlesDirectlyToBank);

    public sealed record ConfigResponse(
        Guid Id, string ProviderCode, string? DisplayName, string Mode,
        string? TestPublicKey, string? LivePublicKey,
        // แสดง 4 ตัวท้ายพอให้ยืนยันว่าใส่ตัวไหน — ห้ามส่ง secret กลับ
        string? TestSecretHint, string? LiveSecretHint,
        DateTime? LastTestPassedAt, DateTime? LastWebhookAt, DateTime? LiveEnabledAt,
        bool CanEnableLive, string WebhookUrl,
        List<string> EnabledMethods, bool IsActive);

    private static string? Hint(ISecretProtector p, string? protectedValue)
    {
        var v = p.Unprotect(protectedValue);
        return string.IsNullOrEmpty(v) ? null : "…" + v[^Math.Min(4, v.Length)..];
    }

    private string WebhookUrl(string providerCode)
        => $"{Request.Scheme}://{Request.Host}/api/pay/webhooks/{providerCode}";

    private ConfigResponse Map(PaymentProviderConfig c) => new(
        c.Id, c.ProviderCode, c.DisplayName, c.Mode.ToString(),
        c.TestPublicKey, c.LivePublicKey,
        Hint(_secrets, c.TestSecretKeyProtected), Hint(_secrets, c.LiveSecretKeyProtected),
        c.LastTestPassedAt, c.LastWebhookAt, c.LiveEnabledAt,
        // ด่าน: เปิด live ได้ก็ต่อเมื่อทดสอบผ่านแล้ว **และ** มีคีย์ live ครบ
        CanEnableLive: c.LastTestPassedAt != null
                       && !string.IsNullOrWhiteSpace(c.LivePublicKey)
                       && _secrets.IsUsable(c.LiveSecretKeyProtected),
        WebhookUrl: WebhookUrl(c.ProviderCode),
        EnabledMethods: ParseMethods(c.EnabledMethodsJson),
        IsActive: c.IsActive);

    private static List<string> ParseMethods(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    /// <summary>ผู้ให้บริการที่ระบบรองรับ + ความสามารถของแต่ละเจ้า
    ///
    /// <para>หน้าเว็บ**สร้างตัวเลือกวิธีจ่ายจากค่านี้** ห้าม hard-code รายการเอง
    /// (defect class "สำเนามือฝั่ง JS" เดียวกับ MENU_SECTIONS)</para></summary>
    [HttpGet("providers")]
    public ActionResult<ApiResponse<List<ProviderOption>>> Providers()
        => Ok(new ApiResponse<List<ProviderOption>>(true, _providers
            .Select(p => new ProviderOption(
                p.ProviderCode,
                p.ProviderCode,   // ชื่อที่แสดงมาจากค่าที่ผู้ใช้ตั้งเอง ไม่ hard-code ที่นี่
                p.Capabilities.Methods.Select(m => m.ToString()).OrderBy(x => x).ToList(),
                p.Capabilities.SupportsRefund,
                p.Capabilities.SupportsWebhook,
                p.SettlesDirectlyToBank))
            .OrderBy(p => p.Code).ToList()));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ConfigResponse>>>> List(
        Guid companyId, CancellationToken ct)
    {
        var rows = await _db.PaymentProviderConfigs
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Ok(new ApiResponse<List<ConfigResponse>>(true, rows.Select(Map).ToList()));
    }

    public sealed record SaveConfigRequest(
        string ProviderCode, string? DisplayName,
        string? TestPublicKey, string? TestSecretKey,
        string? LivePublicKey, string? LiveSecretKey,
        List<string>? EnabledMethods, bool? IsActive,
        Guid? ClearingAccountId, Guid? FeeExpenseAccountId,
        GatewayFeeWhtMode? WhtOnFee);

    /// <summary>สร้าง/แก้ไขการตั้งค่า — คีย์ที่เว้นว่าง = **ไม่เปลี่ยน** (ไม่ใช่ล้างทิ้ง)
    ///
    /// <para>เพราะหน้าเว็บไม่เคยได้ secret กลับไป จึงส่งกลับมาไม่ได้ · ถ้าตีความว่างเป็น
    /// "ล้าง" ผู้ใช้จะลบคีย์ตัวเองทุกครั้งที่แก้ชื่อที่แสดง</para></summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<ConfigResponse>>> Save(
        Guid companyId, [FromBody] SaveConfigRequest req, CancellationToken ct)
    {
        if (_providers.All(p => p.ProviderCode != req.ProviderCode))
            return BadRequest(new ApiResponse<ConfigResponse>(false, null, "ไม่รู้จักผู้ให้บริการนี้"));

        var cfg = await _db.PaymentProviderConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                                   && c.ProviderCode == req.ProviderCode && !c.IsDeleted, ct);
        if (cfg == null)
        {
            cfg = new PaymentProviderConfig { CompanyId = companyId, ProviderCode = req.ProviderCode };
            _db.PaymentProviderConfigs.Add(cfg);
        }

        if (req.DisplayName != null) cfg.DisplayName = req.DisplayName.Trim();
        if (req.TestPublicKey != null) cfg.TestPublicKey = req.TestPublicKey.Trim();
        if (req.LivePublicKey != null) cfg.LivePublicKey = req.LivePublicKey.Trim();

        // เปลี่ยนคีย์ = การทดสอบครั้งก่อนใช้ไม่ได้แล้ว ต้องทดสอบใหม่ก่อนเปิด live
        // (ไม่งั้นเปลี่ยนคีย์เป็นของผิดแล้วยัง "ผ่าน" อยู่จากผลเก่า)
        if (!string.IsNullOrWhiteSpace(req.TestSecretKey))
        {
            cfg.TestSecretKeyProtected = _secrets.Protect(req.TestSecretKey.Trim());
            cfg.LastTestPassedAt = null;
        }
        if (!string.IsNullOrWhiteSpace(req.LiveSecretKey))
        {
            cfg.LiveSecretKeyProtected = _secrets.Protect(req.LiveSecretKey.Trim());
            cfg.LastTestPassedAt = null;
        }

        if (req.EnabledMethods != null)
            cfg.EnabledMethodsJson = System.Text.Json.JsonSerializer.Serialize(req.EnabledMethods);
        if (req.IsActive.HasValue) cfg.IsActive = req.IsActive.Value;
        if (req.ClearingAccountId.HasValue)
            cfg.ClearingAccountId = req.ClearingAccountId == Guid.Empty ? null : req.ClearingAccountId;
        if (req.FeeExpenseAccountId.HasValue)
            cfg.FeeExpenseAccountId = req.FeeExpenseAccountId == Guid.Empty ? null : req.FeeExpenseAccountId;
        if (req.WhtOnFee.HasValue) cfg.WhtOnFee = req.WhtOnFee.Value;

        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<ConfigResponse>(true, Map(cfg), "บันทึกการตั้งค่าแล้ว"));
    }

    [HttpPost("{providerCode}/test")]
    public async Task<ActionResult<ApiResponse<ProviderHealth>>> Test(
        Guid companyId, string providerCode, CancellationToken ct)
    {
        var cfg = await _db.PaymentProviderConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                                   && c.ProviderCode == providerCode && !c.IsDeleted, ct);
        if (cfg == null) return NotFound(new ApiResponse<ProviderHealth>(false, null, "ยังไม่ได้ตั้งค่าผู้ให้บริการนี้"));

        var provider = _providers.First(p => p.ProviderCode == providerCode);
        var health = await provider.TestConnectionAsync(cfg, ct);

        // "ผ่าน" ต้องครบทั้งคีย์และ webhook — คีย์ถูกอย่างเดียวยังเปิด live ไม่ได้
        if (health.KeysValid && health.WebhookReceived)
        {
            cfg.LastTestPassedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new ApiResponse<ProviderHealth>(true, health, health.Message));
    }

    public sealed record SetModeRequest(PaymentProviderMode Mode);

    /// <summary>สลับโหมดทดสอบ ↔ ใช้งานจริง
    ///
    /// <para>ไปทาง live ต้องผ่านด่าน · ไปทาง test ทำได้เสมอ (การถอยกลับมาทดสอบต้องไม่ถูกขวาง
    /// — ไม่งั้นเจอปัญหาแล้วแก้ไม่ได้)</para></summary>
    [HttpPost("{providerCode}/mode")]
    public async Task<ActionResult<ApiResponse<ConfigResponse>>> SetMode(
        Guid companyId, string providerCode, [FromBody] SetModeRequest req, CancellationToken ct)
    {
        var cfg = await _db.PaymentProviderConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                                   && c.ProviderCode == providerCode && !c.IsDeleted, ct);
        if (cfg == null) return NotFound(new ApiResponse<ConfigResponse>(false, null, "ยังไม่ได้ตั้งค่าผู้ให้บริการนี้"));

        if (req.Mode == PaymentProviderMode.Live)
        {
            if (cfg.LastTestPassedAt == null)
                return BadRequest(new ApiResponse<ConfigResponse>(false, null,
                    "ต้องกด \"ทดสอบเชื่อมต่อ\" ให้ผ่านก่อน — รวมถึงต้องเคยได้รับการแจ้งเตือน "
                    + "(webhook) จากผู้ให้บริการจริง มิฉะนั้นลูกค้าจ่ายเงินแล้วออเดอร์จะไม่อัปเดต"));
            if (string.IsNullOrWhiteSpace(cfg.LivePublicKey)
                || !_secrets.IsUsable(cfg.LiveSecretKeyProtected))
                return BadRequest(new ApiResponse<ConfigResponse>(false, null,
                    "ยังไม่ได้กรอกคีย์สำหรับโหมดใช้งานจริงให้ครบทั้งคู่"));

            cfg.LiveEnabledAt = DateTime.UtcNow;
            // การเปลี่ยนแปลงที่กระทบ "เงินจริง" ห้ามเงียบ — ต้องมีร่องรอยที่ผู้สอบบัญชีเห็น
            _db.AuditLogs.Add(new AuditLog
            {
                CompanyId = companyId,
                EntityType = nameof(PaymentProviderConfig),
                EntityId = cfg.Id.ToString(),
                Action = AuditAction.Update,
                UserEmail = User?.Identity?.Name,
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "enable-live-payments",
                    provider = providerCode,
                    lastTestPassedAt = cfg.LastTestPassedAt,
                }),
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTime.UtcNow,
            });
            _logger.LogWarning("บริษัท {Company} เปิดใช้งานรับชำระเงินจริงผ่าน {Provider}",
                companyId, providerCode);
        }

        cfg.Mode = req.Mode;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<ConfigResponse>(true, Map(cfg),
            req.Mode == PaymentProviderMode.Live
                ? "เปิดใช้งานรับชำระเงินจริงแล้ว"
                : "กลับสู่โหมดทดสอบแล้ว — เงินจะไม่เข้าจริง"));
    }
}
