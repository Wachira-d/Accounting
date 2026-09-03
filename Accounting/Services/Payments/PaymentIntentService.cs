using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

public sealed record StartPaymentRequest(
    PaymentSourceKind SourceKind,
    Guid SourceId,
    decimal Amount,
    PaymentMethodKind Method,
    string? Description = null,
    string? CustomerEmail = null,
    string? CustomerPhone = null,
    string? ReturnUrl = null,
    string? CardToken = null,
    Guid? SiteId = null,
    Guid? ContactId = null);

public interface IPaymentIntentService
{
    /// <summary>เริ่ม (หรือใช้ซ้ำ) การจ่ายเงินสำหรับ source นี้</summary>
    Task<PaymentIntent> StartAsync(Guid companyId, StartPaymentRequest request,
        string? providerCode = null, CancellationToken ct = default);

    /// <summary>บันทึกผลจาก provider — **idempotent** · ปลอดภัยที่จะเรียกซ้ำจาก
    /// webhook/poll/คนกด</summary>
    Task<PaymentIntent> ApplyChargeAsync(Guid intentId, ProviderCharge charge,
        PaymentEventSource source, string confirmedBy, CancellationToken ct = default);

    /// <summary>ถามสถานะสดจาก provider แล้ว sync — ใช้เมื่อ webhook ไม่มา</summary>
    Task<PaymentIntent> RefreshAsync(Guid companyId, Guid intentId, CancellationToken ct = default);

    Task<PaymentIntent?> FindAsync(Guid companyId, Guid intentId, CancellationToken ct = default);
}

/// <summary>
/// **เครื่องสถานะของการจ่ายเงิน — ที่เดียวที่เขียน <c>PaymentIntent.Status</c>**
///
/// ═══ ที่มา (PAYMENT_GATEWAY_DESIGN.md ข้อสรุปทีม 1 · 7) ═══
/// สถานะถูกเปลี่ยนจาก 4 ทางที่ไม่เห็นกัน (webhook · job · คนกด · หน้าเว็บ poll) ·
/// การให้แต่ละทางเขียนเอง = กติกา 4 ชุดที่ขัดกันในเคสที่หายากที่สุด และเป็นเคสที่
/// เกี่ยวกับ "ลูกค้าจ่ายเงินแล้วหรือยัง" ซึ่งผิดไม่ได้
///
/// ═══ ข้อบังคับ ═══
/// <list type="bullet">
/// <item>ล็อกด้วย <c>pg_advisory_xact_lock</c> ต่อ intent · <b>ต้องมีธุรกรรมครอบ</b>
///   (ล็อกปล่อยตอนจบธุรกรรม — ไม่มีธุรกรรม = ไม่กันอะไรเลย ตามบทเรียน AdvisoryLockKey)</item>
/// <item>ทุกการเปลี่ยนสถานะเขียน <c>PaymentIntentEvent</c> — เมื่อลูกค้าบอกว่า
///   "จ่ายแล้วแต่ระบบไม่รู้" นี่คือที่เดียวที่ตอบได้ว่าเกิดอะไรขึ้น</item>
/// <item>เรียกซ้ำ = no-op ไม่ใช่ error (webhook ส่งซ้ำเป็นเรื่องปกติของทุกเจ้า —
///   throw จะทำให้ provider retry ไม่รู้จบ)</item>
/// </list>
/// </summary>
public class PaymentIntentService : IPaymentIntentService
{
    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly ILogger<PaymentIntentService> _logger;

    public PaymentIntentService(AccountingDbContext db, IEnumerable<IPaymentProvider> providers,
        ILogger<PaymentIntentService> logger)
    { _db = db; _providers = providers; _logger = logger; }

    private IPaymentProvider Resolve(string code)
        => _providers.FirstOrDefault(p => p.ProviderCode == code)
           ?? throw new InvalidOperationException($"ไม่รู้จักช่องทางชำระเงิน \"{code}\"");

    public Task<PaymentIntent?> FindAsync(Guid companyId, Guid intentId, CancellationToken ct = default)
        => _db.PaymentIntents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);

    public async Task<PaymentIntent> StartAsync(Guid companyId, StartPaymentRequest request,
        string? providerCode = null, CancellationToken ct = default)
    {
        if (request.Amount <= 0m)
            throw new InvalidOperationException("ยอดที่ต้องชำระต้องมากกว่า 0");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // ล็อกต่อ source ไม่ใช่ต่อ intent — เรากำลังจะ "ตัดสินว่ามี intent อยู่แล้วไหม"
        // สองแท็บที่กดจ่ายพร้อมกันต้องได้ QR ใบเดียวกัน ไม่ใช่สองใบซ้อน
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent,
                $"{request.SourceKind}:{request.SourceId:N}") }, ct);

        var existing = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId
                     && i.SourceKind == request.SourceKind && i.SourceId == request.SourceId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        // จ่ายไปแล้ว = ห้ามสร้างใบใหม่ (กันจ่ายซ้ำ) — ต้องดังไม่ใช่เงียบแล้วสร้างใบที่สอง
        var settled = existing.FirstOrDefault(i => PaymentIntentPolicy.IsSettledPositive(i.Status));
        if (settled != null)
            throw new InvalidOperationException("รายการนี้ชำระเงินเรียบร้อยแล้ว");

        // ยังเปิดอยู่และยอดเท่ากัน + ยังไม่หมดอายุ → ใช้ใบเดิม (ห้ามสร้าง QR ซ้อน)
        var reusable = existing.FirstOrDefault(i =>
            PaymentIntentPolicy.IsOpen(i.Status)
            && i.Amount == request.Amount
            && i.MethodKind == request.Method
            && (i.QrExpiresAt == null || i.QrExpiresAt > DateTime.UtcNow));
        if (reusable != null)
        {
            await tx.CommitAsync(ct);
            return reusable;
        }

        var config = await _db.PaymentProviderConfigs
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted
                     && (providerCode == null || c.ProviderCode == providerCode))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // ไม่ได้ตั้งค่า gateway = ตกไปเส้นสลิปเดิม (พฤติกรรมเดิมทุกประการ)
        var code = providerCode ?? config?.ProviderCode ?? Providers.ManualSlipPaymentProvider.Code;
        var provider = Resolve(code);
        if (!provider.Capabilities.Methods.Contains(request.Method))
            throw new InvalidOperationException(
                $"ช่องทาง \"{config?.DisplayName ?? code}\" ไม่รองรับวิธีชำระเงินที่เลือก");

        var intent = new PaymentIntent
        {
            CompanyId = companyId,
            ProviderConfigId = config?.Id,
            ProviderCode = code,
            SourceKind = request.SourceKind,
            SourceId = request.SourceId,
            SiteId = request.SiteId,
            ContactId = request.ContactId,
            Amount = request.Amount,
            Description = request.Description,
            CustomerEmail = request.CustomerEmail,
            CustomerPhone = request.CustomerPhone,
            MethodKind = request.Method,
            ReturnUrl = request.ReturnUrl,
            Status = PaymentIntentStatus.Created,
            AttemptCount = existing.Count + 1,
            IdempotencyKey = PaymentIntentPolicy.IdempotencyKey(
                request.SourceKind, request.SourceId, request.Amount, existing.Count + 1),
        };
        _db.PaymentIntents.Add(intent);
        AddEvent(intent, PaymentEventSource.System, null, PaymentIntentStatus.Created,
            note: $"สร้างรายการชำระเงินผ่าน {code}");
        await _db.SaveChangesAsync(ct);

        // เรียก provider **หลัง** บันทึกแถวแล้ว — ถ้า provider สร้าง charge สำเร็จแต่เรา
        // ล้มก่อนบันทึก จะมี charge ลอยที่ผูกกับอะไรไม่ได้ (เงินลูกค้าหายในระบบเรา)
        try
        {
            var charge = await provider.CreateChargeAsync(intent,
                new ChargeRequest(request.Method, request.CardToken, request.ReturnUrl,
                    request.CustomerEmail, request.CustomerPhone,
                    request.Description ?? $"{request.SourceKind} {request.SourceId:N}"),
                config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = code }, ct);

            ApplyChargeToEntity(intent, charge);
            AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Created, intent.Status,
                note: "สร้าง charge ที่ผู้ให้บริการแล้ว");
        }
        catch (Exception ex)
        {
            // ล้มตอนสร้าง charge = ยังไม่มีเงินไปไหน ปิดใบนี้แล้วให้ลูกค้าลองใหม่ได้
            intent.Status = PaymentIntentStatus.Failed;
            intent.FailureCode = "create_charge_failed";
            intent.FailureMessage = ex.Message;
            AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Created,
                PaymentIntentStatus.Failed, note: "สร้าง charge ไม่สำเร็จ: " + ex.Message);
            _logger.LogError(ex, "สร้าง charge ไม่สำเร็จ intent {Intent} provider {Provider}",
                intent.Id, code);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return intent;
    }

    public async Task<PaymentIntent> ApplyChargeAsync(Guid intentId, ProviderCharge charge,
        PaymentEventSource source, string confirmedBy, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var intent = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(intent.CompanyId, AdvisoryLockKey.PaymentIntent,
                intent.Id.ToString("N")) }, ct);
        await _db.Entry(intent).ReloadAsync(ct);   // อ่านซ้ำหลังได้ล็อก — คนอื่นอาจเพิ่งเขียน

        var from = intent.Status;
        var decision = PaymentIntentPolicy.Evaluate(from, charge.Status);

        if (decision.IsDuplicate)
        {
            // ซ้ำเป็นเรื่องปกติของ webhook ทุกเจ้า — ห้าม throw (provider จะ retry ไม่รู้จบ)
            // แต่ยังต้องรับข้อมูลเสริมที่อาจเพิ่งมี เช่นค่าธรรมเนียมจริง
            if (charge.Fee.HasValue && intent.FeeActual == null) intent.FeeActual = charge.Fee;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return intent;
        }

        if (!decision.Apply)
        {
            // ไม่ throw (ผู้ส่งไม่ผิด) แต่ต้องรู้ว่าเกิดขึ้น — เงียบสนิทคือสิ่งที่ห้าม
            _logger.LogWarning(
                "ปฏิเสธการเปลี่ยนสถานะการชำระเงิน {Intent}: {From} → {To} ({Reason}) จาก {Source}",
                intent.Id, from, charge.Status, decision.Reason, source);
            AddEvent(intent, source, from, charge.Status,
                note: "ปฏิเสธการเปลี่ยนสถานะ: " + decision.Reason, payload: charge.RawStatus);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return intent;
        }

        ApplyChargeToEntity(intent, charge);
        if (charge.Status == PaymentIntentStatus.Succeeded)
        {
            intent.ConfirmedAt ??= DateTime.UtcNow;
            intent.ConfirmedBy ??= confirmedBy;
        }
        AddEvent(intent, source, from, charge.Status, payload: charge.RawStatus,
            note: $"ยืนยันโดย {confirmedBy}");

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return intent;
    }

    public async Task<PaymentIntent> RefreshAsync(Guid companyId, Guid intentId,
        CancellationToken ct = default)
    {
        var intent = await _db.PaymentIntents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        if (!PaymentIntentPolicy.IsOpen(intent.Status)) return intent;

        // QR/ลิงก์หมดอายุแล้ว → ปิดใบนี้เอง ไม่ต้องรบกวน provider
        if (intent.QrExpiresAt is DateTime exp && exp <= DateTime.UtcNow)
            return await ApplyChargeAsync(intentId,
                new ProviderCharge(intent.ProviderRef ?? string.Empty, PaymentIntentStatus.Expired,
                    "expired", intent.Amount),
                PaymentEventSource.System, "system:expiry", ct);

        var config = intent.ProviderConfigId is Guid cid
            ? await _db.PaymentProviderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;

        var provider = Resolve(intent.ProviderCode);
        var charge = await provider.GetChargeAsync(intent,
            config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = intent.ProviderCode }, ct);

        var updated = await ApplyChargeAsync(intentId, charge, PaymentEventSource.Poll, "poll", ct);

        // บันทึกเวลาที่ถามล่าสุดเสมอ แม้สถานะไม่เปลี่ยน — job ใช้ค่านี้เว้นจังหวะ
        var tracked = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId, ct);
        if (tracked != null) { tracked.LastPolledAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
        return updated;
    }

    /// <summary>คัดค่าจาก provider ลง entity — ที่เดียวที่แมป เพื่อไม่ให้ webhook กับ poll
    /// เขียนคนละชุดฟิลด์</summary>
    private static void ApplyChargeToEntity(PaymentIntent intent, ProviderCharge charge)
    {
        intent.Status = charge.Status;
        if (!string.IsNullOrWhiteSpace(charge.ProviderRef)) intent.ProviderRef = charge.ProviderRef;
        intent.ProviderStatusRaw = charge.RawStatus;
        if (charge.QrPayload != null) intent.QrPayload = charge.QrPayload;
        if (charge.QrExpiresAt != null) intent.QrExpiresAt = charge.QrExpiresAt;
        if (charge.AuthorizeUrl != null) intent.AuthorizeUrl = charge.AuthorizeUrl;
        if (charge.Fee.HasValue) intent.FeeActual = charge.Fee;
        intent.FailureCode = charge.FailureCode;
        intent.FailureMessage = charge.FailureMessage;
        intent.UpdatedAt = DateTime.UtcNow;
    }

    private void AddEvent(PaymentIntent intent, PaymentEventSource source,
        PaymentIntentStatus? from, PaymentIntentStatus to, string? note = null, string? payload = null)
        => _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = intent.CompanyId,
            IntentId = intent.Id,
            At = DateTime.UtcNow,
            Source = source,
            FromStatus = from,
            ToStatus = to,
            // เก็บเฉพาะสถานะดิบ ไม่เก็บ payload ทั้งก้อน — body ของ provider มี PII
            // (อีเมล/ชื่อ/4 ตัวท้ายบัตร) ที่เราไม่มีเหตุผลต้องเก็บ
            PayloadJson = payload == null ? null : JsonSerializer.Serialize(new { rawStatus = payload }),
            Note = note,
        });
}
