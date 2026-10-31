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
    Guid? ContactId = null,
    // รอบ 219 เฟส 0: บริษัทผู้จ่ายของรายการค่าบริการ NextAcc (PlatformRevenueSource) — รายการของร้านไม่ต้องส่ง
    Guid? PayerCompanyId = null);

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

    /// <summary>บันทึก "เงินเข้าแล้ว" ที่ระบบ<b>ไม่ได้เป็นคนสร้าง charge เอง</b> —
    /// ใช้กับทางเข้าที่ผู้ให้บริการ (หรือตัวกลางของลูกค้า) แจ้งเข้ามาโดยที่เรายังไม่มี
    /// <c>PaymentIntent</c> ของรายการนั้น
    ///
    /// <para><b>ทำไมต้องมี</b>: ถ้าปล่อยให้ทางเข้าแบบนั้นเรียก orchestrator ปลายทาง
    /// ตรง ๆ จะได้ <b>สองความจริงของ "ลูกค้าจ่ายหรือยัง"</b> — เงินเข้าสำเร็จแต่ไม่มีแถว
    /// ในรายการรับชำระ · ไม่เข้าบัญชีพัก · ไม่โผล่ในรายงานกระทบยอด ซึ่งเป็น defect class
    /// เดียวกับ "สองความจริงของสต็อก" ที่เคยใช้ทั้งรอบยุบ</para>
    ///
    /// <para><b>idempotent</b>: ถ้ามี intent ของ source นี้ที่จบแล้ว คืนตัวเดิมโดยไม่ทำอะไร
    /// (ผู้ให้บริการส่งซ้ำเป็นเรื่องปกติ — throw จะทำให้ retry ไม่รู้จบ)</para></summary>
    Task<PaymentIntent> RecordExternalSuccessAsync(Guid companyId, PaymentSourceKind sourceKind,
        Guid sourceId, decimal amount, string providerRef, string confirmedBy,
        Guid? siteId = null, CancellationToken ct = default);

    /// <summary>รอบ 205 ทีม W1 (R-01 · BOOKING_REVENUE_AUDIT G-2): <b>ส่งต่อใหม่</b> ให้ handler ของต้นทาง — เฉพาะรายการที่เงินเข้าแล้ว (Succeeded)
    /// และประวัติบอกว่าต้นทางยังไม่ถูกบันทึก (<see cref="PaymentRedispatchPolicy"/>) · เรียก <c>DispatchSucceededAsync</c> ตัวเดิมตัวเดียว (ไม่มีสำเนา) ·
    /// ไม่ค้าง = no-op พร้อมข้อความ (<c>NoOp = true</c>) · handler ล้ม = <c>Ok = false</c> + เหตุการณ์ ⚠️ ใหม่ (ล้มดัง 3 ที่)</summary>
    Task<PaymentRedispatchResult> RedispatchAsync(Guid companyId, Guid intentId, string actor, CancellationToken ct = default);

    /// <summary>สถานะ "ส่งต่อ" ของรายการ Succeeded ที่เคยมี ⚠️ (ตัวเดียวกับ <see cref="RedispatchAsync"/> — ประวัติ + หลักฐานฝั่งต้นทาง) · ไม่อยู่ใน dict = ไม่เคยมีปัญหา</summary>
    Task<IReadOnlyDictionary<Guid, PaymentRedispatchState>> RedispatchStatesAsync(Guid companyId, IReadOnlyCollection<Guid> intentIds, CancellationToken ct = default);
}

/// <summary>ผล "ส่งต่อใหม่" — <paramref name="NoOp"/> = ไม่มีอะไรให้ทำ (ไม่ใช่ความล้มเหลว) · <paramref name="Ok"/> = handler ทำสำเร็จ</summary>
public sealed record PaymentRedispatchResult(bool Ok, bool NoOp, PaymentRedispatchState State, string Message);

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
    private readonly IEnumerable<IPaymentCompletionHandler> _handlers;
    private readonly ILogger<PaymentIntentService> _logger;
    private readonly IConfiguration? _config;
    private readonly Accounting.Services.Interfaces.INotificationEngine? _notify;

    public PaymentIntentService(AccountingDbContext db, IEnumerable<IPaymentProvider> providers,
        IEnumerable<IPaymentCompletionHandler> handlers, ILogger<PaymentIntentService> logger,
        IConfiguration? config = null, Accounting.Services.Interfaces.INotificationEngine? notify = null)
    { _db = db; _providers = providers; _handlers = handlers; _logger = logger; _config = config; _notify = notify; }

    /// <summary>รอบ 201 ทีม GW (A-GW3): URL กลับหลังจ่าย = โดเมนเว็บไซต์ของบริษัทนี้ (SiteDomains ที่อนุมัติแล้ว + CustomDomain ของเว็บ) หรือโดเมนของระบบ
    /// (<c>App:BaseUrl</c>) เท่านั้น — ตัดสินด้วย <see cref="PaymentIntentPolicy.SafeReturnUrl"/> ตัวเดียว (ทุกทางเข้าเดินผ่าน StartAsync)</summary>
    private async Task<string?> SafeReturnUrlAsync(Guid companyId, string? requested, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var hosts = await _db.SiteDomains.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.IsActive && !d.IsDeleted
                && d.ApprovalStatus == DomainApprovalStatus.Approved)
            .Select(d => d.Domain)
            .ToListAsync(ct);
        hosts.AddRange(await _db.Sites.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.CustomDomain != null)
            .Select(x => x.CustomDomain!)
            .ToListAsync(ct));
        return PaymentIntentPolicy.SafeReturnUrl(requested, hosts, _config?["App:BaseUrl"]);
    }

    private IPaymentProvider Resolve(string code)
        => _providers.FirstOrDefault(p => p.ProviderCode == code)
           ?? throw new InvalidOperationException($"ไม่รู้จักช่องทางชำระเงิน \"{code}\"");

    public Task<PaymentIntent?> FindAsync(Guid companyId, Guid intentId, CancellationToken ct = default)
        => _db.PaymentIntents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);

    public async Task<PaymentIntent> RecordExternalSuccessAsync(Guid companyId,
        PaymentSourceKind sourceKind, Guid sourceId, decimal amount, string providerRef,
        string confirmedBy, Guid? siteId = null, CancellationToken ct = default)
    {
        // รอบ 219 เฟส 0 (G-1/G-2): ทางเข้านี้ไม่มีบริษัทผู้จ่าย ⇒ รายการค่าบริการ NextAcc ถูกปฏิเสธเสมอ (ตัวตัดสินเดียว)
        var platformDeny = PlatformRevenueSource.StartDenyReason(sourceKind, companyId,
            await PlatformCompanyIdAsync(ct), payerCompanyId: null);
        if (platformDeny != null) throw new InvalidOperationException(platformDeny);

        Guid intentId;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            // ล็อกที่ **source** เหมือน StartAsync — สองการแจ้งพร้อมกันของบิลเดียวกัน
            // ต้องได้แถวเดียว ไม่ใช่สองแถวซ้อน
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent,
                    $"{sourceKind}:{sourceId:N}") }, ct);

            var existing = await _db.PaymentIntents
                .Where(i => i.CompanyId == companyId
                         && i.SourceKind == sourceKind && i.SourceId == sourceId)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync(ct);

            // จบไปแล้ว = แจ้งซ้ำ → no-op (ห้าม throw · ผู้แจ้งจะ retry ไม่รู้จบ)
            var settled = existing.FirstOrDefault(i => PaymentIntentPolicy.IsSettledPositive(i.Status));
            if (settled != null)
            {
                await tx.CommitAsync(ct);
                return settled;
            }

            var open = existing.FirstOrDefault(i => PaymentIntentPolicy.IsOpen(i.Status));
            if (open == null)
            {
                // ไม่เคยมี intent (เงินเกิดนอกระบบเราทั้งหมด) → สร้างแถวย้อนหลังให้มี
                // หลักฐานหนึ่งเดียว · ใช้ ProviderCode ของเส้นสลิปเพราะสิ่งที่จริงคือ
                // "เงินเข้าบัญชีเราแล้ว มีคน/ระบบอื่นเป็นผู้ยืนยัน" ไม่ใช่เราสร้าง charge
                open = new PaymentIntent
                {
                    CompanyId = companyId,
                    ProviderCode = Providers.ManualSlipPaymentProvider.Code,
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    SiteId = siteId,
                    Amount = amount,
                    MethodKind = PaymentMethodKind.ManualSlip,
                    Status = PaymentIntentStatus.Created,
                    AttemptCount = existing.Count + 1,
                    Description = "บันทึกย้อนหลังจากการแจ้งของระบบภายนอก",
                    IdempotencyKey = PaymentIntentPolicy.IdempotencyKey(
                        sourceKind, sourceId, amount, existing.Count + 1),
                };
                _db.PaymentIntents.Add(open);
                AddEvent(open, PaymentEventSource.Webhook, null, PaymentIntentStatus.Created,
                    note: $"สร้างจากการแจ้งของระบบภายนอก โดย {confirmedBy}");
                await _db.SaveChangesAsync(ct);
            }
            intentId = open.Id;
            await tx.CommitAsync(ct);
        }

        // เดินผ่านเส้นเดียวกับ webhook/poll ⇒ ได้ทั้งสถานะ · ประวัติ · และการส่งต่อ
        // ให้ทางเข้าเดิมทำงาน (ออกใบเสร็จ/ตัดหนี้/ตัดสต๊อก) ครบเหมือนกันทุกประการ
        return await ApplyChargeAsync(intentId,
            new ProviderCharge(providerRef, PaymentIntentStatus.Succeeded, "external_confirm", amount),
            PaymentEventSource.Webhook, confirmedBy, ct);
    }

    public async Task<PaymentIntent> StartAsync(Guid companyId, StartPaymentRequest request,
        string? providerCode = null, CancellationToken ct = default)
    {
        if (request.Amount <= 0m)
            throw new InvalidOperationException("ยอดที่ต้องชำระต้องมากกว่า 0");

        // รอบ 219 เฟส 0 (G-1 · คำตัดสินข้อ 178): เงินค่าบริการ NextAcc ต้องเกิดใต้บริษัทผู้ให้บริการ + ระบุผู้จ่ายเท่านั้น — ห้ามไหลเข้า
        // gateway ของร้าน · ทุกทางเข้า (pay/intents · public-pay · ส่วนเสริม) เดินผ่านที่นี่ ⇒ ด่านเดียว
        var platformDeny = PlatformRevenueSource.StartDenyReason(request.SourceKind, companyId,
            await PlatformCompanyIdAsync(ct), request.PayerCompanyId);
        if (platformDeny != null) throw new InvalidOperationException(platformDeny);

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

        // รอบ 219 ส่วน B: ทดลองจ่าย (GatewaySelfTest) ได้เฉพาะ config โหมดทดสอบ — ไม่มีเงินจริงผ่านปุ่ม/หน้าทดสอบ (ด่านเดียว PaymentLiveGate)
        if (PaymentLiveGate.SelfTestRefusal(request.SourceKind, config?.Mode) is string selfTestNo)
            throw new InvalidOperationException(selfTestNo);

        // ไม่ได้ตั้งค่า gateway = ตกไปเส้นสลิปเดิม (พฤติกรรมเดิมทุกประการ)
        var code = providerCode ?? config?.ProviderCode ?? Providers.ManualSlipPaymentProvider.Code;
        var provider = Resolve(code);
        if (!provider.Capabilities.Methods.Contains(request.Method))
            throw new InvalidOperationException(
                $"ช่องทาง \"{config?.DisplayName ?? code}\" ไม่รองรับวิธีชำระเงินที่เลือก");

        var returnUrl = await SafeReturnUrlAsync(companyId, request.ReturnUrl, ct);
        if (!string.IsNullOrWhiteSpace(request.ReturnUrl)
            && (returnUrl == null || !returnUrl.EndsWith(request.ReturnUrl.Trim(), StringComparison.Ordinal)))
            _logger.LogWarning("URL กลับหลังจ่ายไม่ใช่โดเมนของบริษัท {Company} — แทนด้วยหน้าแรกของระบบ (ที่มา {Kind})",
                companyId, request.SourceKind);

        var intent = new PaymentIntent
        {
            CompanyId = companyId,
            ProviderConfigId = config?.Id,
            ProviderCode = code,
            SourceKind = request.SourceKind,
            SourceId = request.SourceId,
            SiteId = request.SiteId,
            ContactId = request.ContactId,
            PayerCompanyId = PlatformRevenueSource.IsPlatformRevenue(request.SourceKind) ? request.PayerCompanyId : null,
            IsTestMode = PaymentIntentTestMode.StampAtCreate(config),   // ฝ่ายค้านรอบ 219 S8: โหมด ณ ตอนสร้าง (รอบ 2: null = ไม่มี config)
            Amount = request.Amount,
            Description = request.Description,
            CustomerEmail = request.CustomerEmail,
            CustomerPhone = request.CustomerPhone,
            MethodKind = request.Method,
            ReturnUrl = returnUrl,
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
                new ChargeRequest(request.Method, request.CardToken, returnUrl,
                    request.CustomerEmail, request.CustomerPhone,
                    request.Description ?? $"{request.SourceKind} {request.SourceId:N}"),
                config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = code }, ct);

            ApplyChargeToEntity(intent, charge);
            AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Created, intent.Status,
                note: "สร้าง charge ที่ผู้ให้บริการแล้ว");
            // รอบ 219 ส่วน B ฝ่ายค้าน B3: โหมดของ charge ต้องตรงโหมดที่ประทับ — คีย์จริงในช่องทดสอบ ⇒ ไม่แสดง QR/ไม่ประมวลผล (ล้มดัง)
            if (PaymentLiveModeGuard.Mismatch(charge.LiveMode, intent.IsTestMode == true) is string modeBad)
            {
                intent.Status = PaymentIntentStatus.Failed;
                intent.FailureCode = "live_mode_mismatch";
                intent.FailureMessage = modeBad;
                intent.QrPayload = null;
                intent.AuthorizeUrl = null;
                AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Created, PaymentIntentStatus.Failed, note: "⚠️ " + modeBad);
                _logger.LogError("โหมด charge ไม่ตรงโหมดของ intent {Intent} ({Provider}): {Why}", intent.Id, code, modeBad);
            }
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
        // รอบ 219 ส่วน B ฝ่ายค้าน B3: charge/event ที่ผู้ให้บริการรายงานโหมดไม่ตรงโหมดที่ประทับตอนสร้าง ⇒ ไม่เปลี่ยนสถานะ (ล้มดัง: ประวัติ + log)
        if (PaymentLiveModeGuard.Mismatch(charge.LiveMode, await PaymentIntentTestMode.ResolveAsync(_db, intent, ct)) is string modeBad)
        {
            _logger.LogError("โหมดของผู้ให้บริการไม่ตรงโหมดของ intent {Intent} — ไม่ประมวลผล: {Why}", intent.Id, modeBad);
            AddEvent(intent, source, from, charge.Status, note: "⚠️ ปฏิเสธการเปลี่ยนสถานะ: " + modeBad, payload: charge.RawStatus);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return intent;
        }
        var decision = PaymentIntentPolicy.Evaluate(from, charge.Status);

        if (decision.IsDuplicate)
        {
            // ซ้ำเป็นเรื่องปกติของ webhook ทุกเจ้า — ห้าม throw (provider จะ retry ไม่รู้จบ)
            // แต่ยังต้องรับข้อมูลเสริมที่อาจเพิ่งมี เช่นค่าธรรมเนียมจริง
            // R200G-6: ตัวตัดสินเดียวกับ ApplyChargeToEntity (เดิมเขียนเงื่อนไขเองเป็นผู้เขียน FeeActual คนที่สอง)
            if (charge.Fee.HasValue && PaymentIntentPolicy.IsProviderFeeFinal(charge.Status)
                && PaymentIntentPolicy.ShouldTakeProviderFee(intent.Status, intent.FeeActual,
                    intent.SettlementJournalEntryId != null || intent.SettlementBatchId != null))
                intent.FeeActual = charge.Fee;
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
            intent.ConfirmedBy ??= PaymentIntentPolicy.CapConfirmedBy(confirmedBy);   // varchar(200) — ฝ่ายค้านรอบ 218 รอบ 2 (K2)
            intent.PaidAt ??= charge.PaidAtUtc;   // ฝ่ายค้านรอบ 218 (B2): วันที่เงินเข้าตามหลักฐาน (สลิป) — ไม่ระบุ = null (handler ใช้ ConfirmedAt)
        }
        AddEvent(intent, source, from, charge.Status, payload: charge.RawStatus,
            note: $"ยืนยันโดย {confirmedBy}");

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // ── หลังยืนยันเงินเข้าแล้ว ให้ทางเข้าเดิมทำงานต่อ ──
        // ทำ **นอกธุรกรรม** โดยตั้งใจ: การยืนยันว่าเงินเข้าแล้วเป็นข้อเท็จจริงที่ต้อง
        // บันทึกให้ได้เสมอ · ถ้ารวมไว้ในธุรกรรมเดียวกันแล้ว orchestrator ปลายทางล้ม
        // (สต็อกไม่พอ/e-Tax ล่ม) การบันทึกว่าเงินเข้าจะถูกกลับด้วย ⇒ ลูกค้าจ่ายแล้ว
        // ระบบลืมสนิท ซึ่งกู้ยากกว่าออเดอร์ที่ค้างอยู่แต่รู้ว่าจ่ายแล้ว
        if (charge.Status == PaymentIntentStatus.Succeeded)
            await DispatchSucceededAsync(intent, ct);

        return intent;
    }

    /// <summary>รอบ 219 เฟส 0: บริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) — ตัวอ่านของด่าน <see cref="PlatformRevenueSource"/> ในไฟล์นี้</summary>
    private Task<Guid?> PlatformCompanyIdAsync(CancellationToken ct)
        => _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);

    /// <summary>ส่งต่อให้ทางเข้าเดิมทำงาน — ถ้าไม่ทำขั้นนี้ ระบบจะกลายเป็น
    /// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ทันที (ลูกค้าจ่ายเงินสำเร็จ · แถวถูกต้อง ·
    /// แต่ออเดอร์ยังค้างชำระตลอดกาล)</summary>
    /// <returns>true = handler ทำสำเร็จ (หรือไม่มีอะไรต้องทำ) · false = ไม่มี handler/handler ล้ม (เหตุการณ์ ⚠️ ถูกเขียนแล้ว)</returns>
    /// <param name="redispatchActor">ไม่ null = ถูกเรียกจาก <see cref="RedispatchAsync"/> ⇒ สำเร็จแล้วเขียนเหตุการณ์ ✅ (การส่งต่อครั้งแรกไม่เขียน — ของเดิม)</param>
    private async Task<bool> DispatchSucceededAsync(PaymentIntent intent, CancellationToken ct, string? redispatchActor = null)
    {
        // รอบ 219 เฟส 0 (G-1/G-3 · part B (a)): เงินค่าบริการ NextAcc ให้สิทธิ์ได้เฉพาะเมื่อรับผ่านบริษัทผู้ให้บริการ · รู้ผู้จ่าย · ไม่ใช่โหมดทดสอบ —
        // ไม่ผ่าน = ไม่เรียก handler (ไม่ต่ออายุ/ไม่เปิดส่วนเสริม) แต่ล้มดัง: ประวัติ ⚠️ + log + แจ้ง (เหมือนไม่มี handler)
        if (PlatformRevenueSource.IsPlatformRevenue(intent.SourceKind))
        {
            // ฝ่ายค้านรอบ 219 S8: โหมด ณ ตอนสร้างชนะ (config อาจสลับภายหลังได้ทั้งสองทาง) · แถวก่อนมีคอลัมน์ (null) ⇒ อ่านโหมดของ config ·
            // รอบ 2: ตัวตัดสินเดียวกับหน้าจ่ายเงินทั้งสองหน้า (PaymentIntentTestMode)
            var isTest = await PaymentIntentTestMode.ResolveAsync(_db, intent, ct);
            var block = PlatformRevenueSource.EntitlementBlockReason(intent.SourceKind, intent.CompanyId,
                await PlatformCompanyIdAsync(ct), intent.PayerCompanyId, isTest);
            if (block != null)
            {
                _logger.LogError("เงินค่าบริการ NextAcc เข้าแล้วแต่ไม่ให้สิทธิ์อัตโนมัติ — intent {Intent} บริษัท {Company} ผู้จ่าย {Payer} ยอด {Amount:N2}: {Reason}",
                    intent.Id, intent.CompanyId, intent.PayerCompanyId, intent.Amount, block);
                AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Succeeded, PaymentIntentStatus.Succeeded,
                    note: PaymentRedispatchPolicy.UnfulfilledNotePrefix + block);
                await _db.SaveChangesAsync(ct);
                await NotifyUnfulfilledAsync(intent, block, ct);
                return false;
            }
        }

        var handler = _handlers.FirstOrDefault(h => h.SourceKind == intent.SourceKind);
        if (handler == null)
        {
            // ยังไม่ได้ต่อสายทางเข้านี้ — ต้องดัง ไม่ใช่เงียบ เพราะเงินเข้าแล้วจริง
            _logger.LogError(
                "เงินเข้าแล้วแต่ยังไม่มีตัวจัดการของต้นทางชนิด {Kind} — intent {Intent} "
                + "ยอด {Amount:N2} · ต้องบันทึกการรับเงินด้วยมือ",
                intent.SourceKind, intent.Id, intent.Amount);
            // คำนำหน้า = PaymentRedispatchPolicy.UnfulfilledNotePrefix (ตัวตัดสินปุ่ม "ส่งต่อใหม่" อ่านจากหมายเหตุนี้ — ห้ามเปลี่ยนข้อความแยกกัน)
            AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Succeeded,
                PaymentIntentStatus.Succeeded,
                note: PaymentRedispatchPolicy.UnfulfilledNotePrefix + "ระบบยังไม่ได้บันทึกการรับเงินให้ต้นทางนี้อัตโนมัติ");
            await _db.SaveChangesAsync(ct);
            await NotifyUnfulfilledAsync(intent, "ระบบยังไม่ได้บันทึกการรับเงินให้ต้นทางนี้อัตโนมัติ", ct);
            return false;
        }

        try
        {
            await handler.HandleSucceededAsync(intent, ct);
            if (redispatchActor != null)
            {
                AddEvent(intent, PaymentEventSource.Manual, PaymentIntentStatus.Succeeded, PaymentIntentStatus.Succeeded,
                    note: $"{PaymentRedispatchPolicy.FulfilledNotePrefix} โดย {redispatchActor}");
                await _db.SaveChangesAsync(ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            // ห้าม throw กลับไปหา webhook — provider จะ retry ไม่รู้จบ และการยืนยันว่า
            // เงินเข้าถูกบันทึกไปแล้ว · ต้องดังพอให้คนตามเก็บได้: log + ประวัติของ intent
            _logger.LogError(ex,
                "เงินเข้าแล้วแต่ดำเนินการต่อของต้นทาง {Kind} ล้มเหลว — intent {Intent} "
                + "ต้นทาง {Source}", intent.SourceKind, intent.Id, intent.SourceId);
            AddEvent(intent, PaymentEventSource.System, PaymentIntentStatus.Succeeded,
                PaymentIntentStatus.Succeeded,
                note: PaymentRedispatchPolicy.UnfulfilledNotePrefix + "ดำเนินการต่อไม่สำเร็จ: " + ex.Message);
            await _db.SaveChangesAsync(ct);
            // ที่พักติดธง + แจ้งเตือนของตัวเองแล้ว (FlagPaymentProblemAsync · คำตัดสินข้อ 127) — ไม่แจ้งซ้ำ · เว้นหาการจองไม่พบ (ไม่มีแถวให้ติดธง)
            if (intent.SourceKind != PaymentSourceKind.LodgingReservation
                || (ex is BusinessRuleException bre && bre.RuleCode == GatewayPaymentRecord.SourceMissingRuleCode))
                await NotifyUnfulfilledAsync(intent, ex.Message, ct);
            return false;
        }
    }

    public async Task<PaymentRedispatchResult> RedispatchAsync(Guid companyId, Guid intentId, string actor, CancellationToken ct = default)
    {
        PaymentIntent intent;
        PaymentRedispatchState state;
        // ฝ่ายค้าน W1 ข้อ 3: ล็อกต่อ intent ตัวเดียวกับ ApplyChargeAsync แล้ว**ตรวจซ้ำหลังได้ล็อก** — สองคนกด "ส่งต่อใหม่" พร้อมกัน คนที่สองต้องเห็น attempt
        // ของคนแรก (InProgress) ไม่ใช่รัน handler ซ้อน (SiteOrder: แถวรับเงิน Pending ซ้อน · Lodging: ConfirmAsync ซ้อน)
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            intent = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(intent.CompanyId, AdvisoryLockKey.PaymentIntent, intent.Id.ToString("N")) }, ct);
            await _db.Entry(intent).ReloadAsync(ct);
            state = await EvaluateRedispatchAsync(intent, ct);
            if (state != PaymentRedispatchState.Pending)
            {
                await tx.CommitAsync(ct);
                return new PaymentRedispatchResult(Ok: false, NoOp: true, state, PaymentRedispatchPolicy.NoOpMessage(state));
            }

            // ร่องรอยว่าใครกด (ก่อนผล · ใต้ล็อก ⇒ คำขอถัดไปเห็นเป็น InProgress) + audit chain — การสั่งให้ระบบบันทึกเงินซ้ำเป็นจุดที่ผู้สอบบัญชีถาม
            AddEvent(intent, PaymentEventSource.Manual, intent.Status, intent.Status,
                note: $"{PaymentRedispatchPolicy.AttemptNotePrefix} {actor}");
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                EntityType = nameof(PaymentIntent),
                EntityId = intent.Id.ToString(),
                Action = AuditAction.Update,
                UserEmail = actor,
                NewValues = JsonSerializer.Serialize(new { action = "gateway-redispatch", sourceKind = intent.SourceKind.ToString(), intent.SourceId, intent.Amount, actor }),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // ส่งต่อ**นอกธุรกรรม** เหมือน ApplyChargeAsync (handler เปิดธุรกรรมของตัวเอง) — ผลถูกเขียนเป็น ✅/⚠️ เสมอ ⇒ InProgress ปิดตัวเอง
        var ok = await DispatchSucceededAsync(intent, ct, redispatchActor: actor);
        return ok
            ? new PaymentRedispatchResult(Ok: true, NoOp: false, PaymentRedispatchState.Fulfilled,
                "ส่งต่อให้ต้นทางสำเร็จ — เอกสาร/การรับเงินถูกบันทึกแล้ว (ดูประวัติของรายการ)")
            : new PaymentRedispatchResult(Ok: false, NoOp: false, PaymentRedispatchState.Pending,
                "ส่งต่อใหม่แล้วแต่ต้นทางยังบันทึกไม่สำเร็จ — ดูเหตุผลในประวัติของรายการ แล้วแก้ที่ต้นทาง (สต็อก/ผังบัญชี/การจอง) ก่อนกดอีกครั้ง หรือบันทึกรับเงินด้วยมือ");
    }

    /// <summary>สถานะ "ส่งต่อ" ของ intent เดียว — ประวัติ + หลักฐานฝั่งต้นทาง (ตัวเดียวกับที่รายการใช้)</summary>
    private async Task<PaymentRedispatchState> EvaluateRedispatchAsync(PaymentIntent intent, CancellationToken ct)
    {
        if (intent.Status != PaymentIntentStatus.Succeeded) return PaymentRedispatchState.NotSucceeded;
        var events = await _db.PaymentIntentEvents.AsNoTracking()
            .Where(e => e.CompanyId == intent.CompanyId && e.IntentId == intent.Id)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new { e.At, e.Note })
            .ToListAsync(ct);
        var tuples = events.Select(e => (e.At, e.Note)).ToList();
        var since = PaymentRedispatchPolicy.LastUnfulfilledAt(tuples);
        var sourceRecorded = since != null
            && PaymentRedispatchPolicy.SourceRecorded(await GatewaySourceEvidence.RecordedPaymentsAsync(_db, intent, ct),
                intent.Amount, GatewaySourceEvidence.GatewayReferences(intent), since);
        return PaymentRedispatchPolicy.Evaluate(intent.Status, tuples, sourceRecorded, DateTime.UtcNow);
    }

    public async Task<IReadOnlyDictionary<Guid, PaymentRedispatchState>> RedispatchStatesAsync(Guid companyId, IReadOnlyCollection<Guid> intentIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, PaymentRedispatchState>();
        if (intentIds.Count == 0) return result;
        // คัดเฉพาะ intent ที่มี ⚠️ (ส่วนใหญ่ไม่มี) แล้วค่อยโหลดหลักฐานต้นทางทีละราย — รายการมีไม่กี่แถว
        var warned = await _db.PaymentIntentEvents.AsNoTracking()
            .Where(e => e.CompanyId == companyId && intentIds.Contains(e.IntentId) && e.Note != null
                && e.Note.StartsWith(PaymentRedispatchPolicy.UnfulfilledNotePrefix))
            .Select(e => e.IntentId).Distinct().ToListAsync(ct);
        if (warned.Count == 0) return result;
        var intents = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && warned.Contains(i.Id) && i.Status == PaymentIntentStatus.Succeeded)
            .ToListAsync(ct);
        foreach (var intent in intents)
            result[intent.Id] = await EvaluateRedispatchAsync(intent, ct);
        return result;
    }

    /// <summary>รอบ 205 ทีม E (BOOKING_REVENUE_AUDIT G-1): เงินเข้าแล้วแต่ต้นทางไม่ถูกบันทึก (ไม่มีใบ/JE) — เดิมดังแค่ log + ประวัติของรายการชำระ
    /// ซึ่งไม่มีใครเปิดดูจนลูกค้าโทรมา ⇒ แจ้งเจ้าของบริษัท (ล้มดังที่ "สถานะงาน" ด้วย ไม่ใช่แค่ log · F2 ข้อ 7) · แจ้งไม่สำเร็จต้องไม่ล้มเส้นเงิน</summary>
    private async Task NotifyUnfulfilledAsync(PaymentIntent intent, string reason, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            var ownerUserId = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == intent.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => (Guid?)cu.UserId)
                .FirstOrDefaultAsync(ct);
            await _notify.DispatchAsync(intent.CompanyId,
                Accounting.Models.Constants.NotificationEvents.GatewayPaymentUnfulfilled,
                new Accounting.Services.Interfaces.NotificationContext
                {
                    Title = "เงินออนไลน์เข้าแล้ว แต่ระบบบันทึกรายการไม่สำเร็จ",
                    Message = $"รายการชำระ {intent.Amount:N2} บาท ({intent.Description ?? intent.SourceKind.ToString()}) เข้าแล้ว "
                        + $"แต่ยังไม่มีเอกสาร/การบันทึกรับเงิน: {reason} — เปิดหน้ารายการรับชำระออนไลน์เพื่อตรวจ แล้วบันทึกรับเงินด้วยมือหรือคืนเงินลูกค้า",
                    ActionUrl = "/pages/payment-intents.html",
                    EntityType = "PaymentIntent", EntityId = intent.Id,
                    RecipientUserId = ownerUserId,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเจ้าของเรื่องเงินเข้าแต่บันทึกไม่สำเร็จไม่สำเร็จ — intent {Intent}", intent.Id);
        }
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
        // รอบ 200 ทีม G: ค่าธรรมเนียมที่แก้ด้วยมือ/อยู่ในรอบโอนแล้วห้ามถูกทับ — ตัดสินก่อนเปลี่ยนสถานะ (ใช้สถานะเดิม)
        // R200G-6: ค่าธรรมเนียมจาก charge ที่ยังรอจ่าย (มักเป็น 0) ไม่ใช่ค่าจริง — ห้ามเก็บเป็น "รู้แล้ว"
        var takeFee = charge.Fee.HasValue && PaymentIntentPolicy.IsProviderFeeFinal(charge.Status)
            && PaymentIntentPolicy.ShouldTakeProviderFee(
            intent.Status, intent.FeeActual, intent.SettlementJournalEntryId != null || intent.SettlementBatchId != null);
        intent.Status = charge.Status;
        if (!string.IsNullOrWhiteSpace(charge.ProviderRef)) intent.ProviderRef = charge.ProviderRef;
        intent.ProviderStatusRaw = charge.RawStatus;
        if (charge.QrPayload != null) intent.QrPayload = charge.QrPayload;
        if (charge.QrExpiresAt != null) intent.QrExpiresAt = charge.QrExpiresAt;
        if (charge.AuthorizeUrl != null) intent.AuthorizeUrl = charge.AuthorizeUrl;
        if (takeFee) intent.FeeActual = charge.Fee;
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
