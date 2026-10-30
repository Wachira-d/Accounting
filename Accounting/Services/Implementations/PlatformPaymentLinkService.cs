using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ลิงก์ที่เพิ่งสร้าง — <paramref name="Token"/>/<paramref name="Url"/> แสดงครั้งเดียว (ฐานเก็บแค่ hash)</summary>
public sealed record PaymentLinkCreated(Guid Id, string Token, string Url, DateTime ExpiresAt, decimal AmountSnapshot, string InvoiceNumber);

public sealed record PaymentLinkRow(Guid Id, Guid DocumentId, string InvoiceNumber, Guid BuyerCompanyId, string? BuyerName, decimal AmountSnapshot,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt, string? RevokedReason, int ViewCount, DateTime? LastViewedAt, string State, string? StateMessage);

/// <summary>หน้าสาธารณะของลิงก์ — ข้อมูลขั้นต่ำของใบ (ไม่มีที่อยู่/เลขภาษี/อีเมล/โทร)</summary>
public sealed record PaymentLinkPublicView(
    string State, string? Message, string SellerName, string BuyerName, string InvoiceNumber, DateTime InvoiceDate, DateTime? DueDate,
    decimal AmountDue, List<string> Lines, object? PayOptions, bool CanPay, string? PendingPaymentNumber);

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 4 (PLATFORM_BILLING_AUDIT §4 · คำตัดสินข้อ 180) — ลิงก์ชำระใบแจ้งหนี้ค่าบริการ: แอดมินสร้างให้ใบที่มีอยู่แล้ว (ห้ามยอดอิสระ) ·
/// โทเคน 128 บิตเก็บเฉพาะ hash · อายุตั้งต้นจากค่าตั้ง · ใบเดียว ปิดเองเมื่อชำระครบ · เพิกถอนได้ · ทุกการสร้าง/เปิดดู/เพิกถอน/แจ้งชำระลง audit chain
/// (CompanyId = บริษัทผู้ซื้อ) · แจ้งชำระผ่าน <see cref="ISubscriptionService.SubmitInvoicePaymentAsync"/> ตัวเดียวกับศูนย์บิล (ยอดจากใบ · ติ๊กยอมรับบังคับ)
/// </summary>
public interface IPlatformPaymentLinkService
{
    Task<PaymentLinkCreated> CreateAsync(Guid documentId, int? expiryDays, PlatformBillingActor actor, CancellationToken ct = default);
    Task<List<PaymentLinkRow>> ListAsync(Guid? documentId, CancellationToken ct = default);
    Task RevokeAsync(Guid linkId, string reason, PlatformBillingActor actor, CancellationToken ct = default);
    /// <summary>null = โทเคนผิด/ไม่มี (ผู้เรียกตอบ 404 เหมือนกันทุกกรณี)</summary>
    Task<PaymentLinkPublicView?> ViewAsync(string token, string? ip, CancellationToken ct = default);
    Task<(Guid LinkId, Guid BuyerCompanyId, Guid DocumentId)?> ResolveForPaymentAsync(string token, CancellationToken ct = default);
    /// <summary>ฝ่ายค้าน N1: บริษัทผู้ซื้อของลิงก์ <b>เมื่อ intent นี้เริ่มจากใบของลิงก์นี้</b> (รายการชำระที่ผูกใบเดียวกัน · ผู้จ่ายเดียวกัน) และลิงก์ยังไม่ถูกเพิกถอน/หมดอายุ ·
    /// null = โทเคนผิด/ลิงก์ใช้ไม่ได้/intent ไม่ใช่ของลิงก์ (ผู้เรียกตอบ 404 เหมือนกัน)</summary>
    Task<Guid?> ResolveIntentPayerAsync(string token, Guid intentId, CancellationToken ct = default);
    /// <summary>ฝ่ายค้าน N2: URL กลับจากหน้าธนาคาร/บัตร — <b>ไม่มีโทเคน</b> (ไม่รั่วไปผู้ให้บริการชำระเงิน/ประวัติเบราว์เซอร์ฝั่งนั้น) · หน้าเว็บเก็บโทเคนใน sessionStorage เอง</summary>
    Task<string> ReturnUrlAsync(CancellationToken ct = default);
    Task RecordPaymentAuditAsync(Guid linkId, Guid buyerCompanyId, Guid paymentId, string paymentNumber, string? ip, CancellationToken ct = default);
    /// <summary>ด่านติ๊กยอมรับนโยบายของผู้จ่ายผ่านลิงก์ (ไม่มีบัญชีผู้ใช้) — ตัวตัดสิน/ผู้เขียนหลักฐานของ part B (<c>PurchasePolicyAcceptance.Problem/Create</c>) ·
    /// ไม่ผ่าน ⇒ BusinessRuleException · ผ่าน ⇒ แถวหลักฐานอยู่ใน context (บันทึกพร้อมรายการชำระ)</summary>
    Task EnforcePolicyAsync(Guid linkId, Guid buyerCompanyId, Guid documentId, bool accepted, string? versionShown, string? ip, string? userAgent,
        CancellationToken ct = default);
}

public class PlatformPaymentLinkService : IPlatformPaymentLinkService
{
    public const string RuleCreate = "PLATFORM-PAYLINK-CREATE", RuleView = "PLATFORM-PAYLINK-VIEW",
        RuleRevoke = "PLATFORM-PAYLINK-REVOKE", RulePay = "PLATFORM-PAYLINK-PAY";
    public const string PublicPath = "/pay-link.html?t=";
    public const string ReturnPath = "/pay-link.html?return=1";
    private const int LineTake = 5;

    private readonly AccountingDbContext _db;
    private readonly Accounting.Services.Payments.IPlatformPaymentService _platform;
    private readonly ILegalDocumentService _legal;
    public PlatformPaymentLinkService(AccountingDbContext db, Accounting.Services.Payments.IPlatformPaymentService platform, ILegalDocumentService legal)
    { _db = db; _platform = platform; _legal = legal; }

    public async Task EnforcePolicyAsync(Guid linkId, Guid buyerCompanyId, Guid documentId, bool accepted, string? versionShown,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var current = await _legal.GetPurchasePolicyVersionAsync(ct);
        if (PurchasePolicyAcceptance.Problem(false, accepted, versionShown, current.Version) is string problem)
            throw new BusinessRuleException(problem, PurchasePolicyAcceptance.RuleCode);
        _db.Set<PolicyAcceptance>().Add(PurchasePolicyAcceptance.Create(buyerCompanyId, PurchasePolicyAcceptance.ScopePlatform,
            PurchasePolicyAcceptance.ChannelPlatformInvoice, documentId, null, current.Version, versionShown, $"paylink:{linkId}", null,
            DateTime.UtcNow, ip, userAgent));
    }

    public async Task<PaymentLinkCreated> CreateAsync(Guid documentId, int? expiryDays, PlatformBillingActor actor, CancellationToken ct = default)
    {
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct)
            ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — สร้างลิงก์ชำระไม่ได้", "PLATFORM-TENANT-NOT-SET", 409);
        var inv = await PlatformInvoiceQueries.ByIdAsync(_db, platformId, documentId, ct)
            ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้ค่าบริการนี้ (ต้องเป็นใบแจ้งหนี้ของบริษัทผู้เช่าใน tenant ผู้ให้บริการ)");
        if (!PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue))
            throw new BusinessRuleException($"ใบแจ้งหนี้ {inv.DocumentNumber} ไม่มียอดค้าง — ไม่ต้องสร้างลิงก์ชำระ", "PLATFORM-INVOICE-NOT-OPEN", 409);
        var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var now = DateTime.UtcNow;
        var token = PlatformPaymentLinkRules.NewToken();
        var link = new PlatformPaymentLink
        {
            PlatformCompanyId = platformId,
            BuyerCompanyId = inv.BuyerCompanyId,
            DocumentId = inv.Id,
            TokenHash = PlatformPaymentLinkRules.Hash(token),
            AmountSnapshot = inv.BalanceDue,
            ExpiresAt = now.AddDays(PlatformPaymentLinkRules.ExpiryDays(expiryDays, policy)),
            CreatedBy = actor.UserId?.ToString() ?? actor.Email,
        };
        _db.PlatformPaymentLinks.Add(link);
        Audit(inv.BuyerCompanyId, link.Id, actor.UserId, actor.Email, actor.Ip, AuditAction.Create, RuleCreate,
            new { invoice = inv.DocumentNumber, amount = inv.BalanceDue, link.ExpiresAt });
        await _db.SaveChangesAsync(ct);
        var baseUrl = (await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct))?.TrimEnd('/');
        return new PaymentLinkCreated(link.Id, token, (baseUrl ?? "") + PublicPath + token, link.ExpiresAt, link.AmountSnapshot, inv.DocumentNumber);
    }

    public async Task<List<PaymentLinkRow>> ListAsync(Guid? documentId, CancellationToken ct = default)
    {
        var q = _db.PlatformPaymentLinks.AsNoTracking().AsQueryable();
        if (documentId is Guid d) q = q.Where(l => l.DocumentId == d);
        var links = await q.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync(ct);
        var docIds = links.Select(l => l.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.AsNoTracking().Where(x => docIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DocumentNumber, x.Status, x.BalanceDue }).ToDictionaryAsync(x => x.Id, ct);
        var buyerIds = links.Select(l => l.BuyerCompanyId).Distinct().ToList();
        var names = await _db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => buyerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var now = DateTime.UtcNow;
        return links.Select(l =>
        {
            docs.TryGetValue(l.DocumentId, out var doc);
            var state = doc == null ? PaymentLinkState.InvoiceClosed
                : PlatformPaymentLinkRules.StateOf(l.ExpiresAt, l.RevokedAt, PlatformInvoiceRules.IsOpen(doc.Status, doc.BalanceDue),
                    doc.Status == DocumentStatus.Paid || (doc.Status != DocumentStatus.Voided && doc.BalanceDue <= PlatformInvoiceRules.Epsilon), now);
            return new PaymentLinkRow(l.Id, l.DocumentId, doc?.DocumentNumber ?? "-", l.BuyerCompanyId, names.GetValueOrDefault(l.BuyerCompanyId),
                l.AmountSnapshot, l.CreatedAt, l.ExpiresAt, l.RevokedAt, l.RevokedReason, l.ViewCount, l.LastViewedAt,
                state.ToString(), PlatformPaymentLinkRules.Message(state));
        }).ToList();
    }

    public async Task RevokeAsync(Guid linkId, string reason, PlatformBillingActor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("กรุณาระบุเหตุผลที่ยกเลิกลิงก์");
        var link = await _db.PlatformPaymentLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct)
            ?? throw new KeyNotFoundException("ไม่พบลิงก์ชำระ");
        if (link.RevokedAt != null) return;   // เพิกถอนซ้ำ = ผลเดิม
        var r = reason.Trim();
        link.RevokedAt = DateTime.UtcNow;
        link.RevokedBy = actor.UserId?.ToString() ?? actor.Email;
        link.RevokedReason = r.Length > 300 ? r[..300] : r;
        Audit(link.BuyerCompanyId, link.Id, actor.UserId, actor.Email, actor.Ip, AuditAction.Update, RuleRevoke, new { reason = link.RevokedReason });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<PlatformPaymentLink?> FindAsync(string token, CancellationToken ct)
    {
        if (!PlatformPaymentLinkRules.LooksValid(token)) return null;
        var hash = PlatformPaymentLinkRules.Hash(token);
        return await _db.PlatformPaymentLinks.FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
    }

    public async Task<PaymentLinkPublicView?> ViewAsync(string token, string? ip, CancellationToken ct = default)
    {
        var link = await FindAsync(token, ct);
        if (link == null) return null;
        var now = DateTime.UtcNow;
        var inv = await PlatformInvoiceQueries.ByIdAsync(_db, link.PlatformCompanyId, link.DocumentId, ct);
        var open = inv != null && PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue);
        var paid = inv != null && inv.Status != DocumentStatus.Voided && inv.BalanceDue <= PlatformInvoiceRules.Epsilon;
        // ใบต้องเป็นของผู้ซื้อเดิมของลิงก์ (กันผู้ติดต่อถูกย้าย/ผูกใหม่หลังสร้างลิงก์)
        if (inv != null && inv.BuyerCompanyId != link.BuyerCompanyId) { open = false; paid = false; }
        var state = PlatformPaymentLinkRules.StateOf(link.ExpiresAt, link.RevokedAt, open, paid, now);

        // ฝ่ายค้าน S10: นับการเปิดแบบ atomic (UPDATE … +1 — ไม่ทับกันข้ามเครื่อง) เฉพาะลิงก์ที่ยังใช้ได้ · audit ครั้งแรกต่อ (ลิงก์ · IP · วันไทย) —
        // ลิงก์ที่เพิกถอน/หมดอายุ audit ครั้งเดียวต่อสถานะ (เปิดซ้ำ/สคริปต์ยิงไม่เขียนฐานเพิ่ม)
        if (state == PaymentLinkState.Valid)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"PlatformPaymentLinks\" SET \"ViewCount\" = \"ViewCount\" + 1, \"LastViewedAt\" = (now() at time zone 'utc') WHERE \"Id\" = {link.Id}", ct);
        var key = PlatformPaymentLinkRules.ViewAuditMarker(state.ToString());
        var since = PlatformPaymentLinkRules.ViewAuditSince(state == PaymentLinkState.Valid, now);
        var idStr = link.Id.ToString();
        var already = await _db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == link.BuyerCompanyId && a.EntityType == nameof(PlatformPaymentLink)
            && a.EntityId == idStr && a.Timestamp >= since && a.NewValues!.Contains(key)
            && (state != PaymentLinkState.Valid || a.IpAddress == ip), ct);
        if (!already)
        {
            Audit(link.BuyerCompanyId, link.Id, null, "paylink", ip, AuditAction.View, RuleView, new { state = state.ToString() });
            await _db.SaveChangesAsync(ct);
        }

        var buyer = await _db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == link.BuyerCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var seller = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => s.PlatformSellerName ?? s.SiteName).FirstOrDefaultAsync(ct);
        var valid = state == PaymentLinkState.Valid;
        var lines = valid ? (await PlatformInvoiceQueries.LineSummaryAsync(_db, link.PlatformCompanyId, link.DocumentId, LineTake, ct))
            .Select(l => $"{l.Description} — {l.Amount:N2}").ToList() : new List<string>();
        string? pending = null;
        if (valid)
            pending = await _db.SubscriptionPayments.AsNoTracking()
                .Where(p => p.PlatformInvoiceDocumentId == link.DocumentId && p.Subscription.CompanyId == link.BuyerCompanyId && p.SlipFileName != null
                    && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
                .Select(p => p.PaymentNumber).FirstOrDefaultAsync(ct);
        var pay = valid ? await Accounting.Controllers.PlatformPayInfo.BuildAsync(_db, _platform, _legal, ct) : null;
        return new PaymentLinkPublicView(state.ToString(),
            pending != null ? $"แจ้งชำระแล้ว ({pending}) — รอผู้ดูแลระบบตรวจสอบ" : PlatformPaymentLinkRules.Message(state),
            string.IsNullOrWhiteSpace(seller) ? "NextAcc" : seller!, PlatformPaymentLinkRules.MaskedBuyerName(buyer),
            inv?.DocumentNumber ?? "-", inv?.DocumentDate ?? link.CreatedAt, inv?.DueDate,
            valid ? inv!.BalanceDue : 0m, lines, pay, valid && pending == null, pending);
    }

    public async Task<(Guid LinkId, Guid BuyerCompanyId, Guid DocumentId)?> ResolveForPaymentAsync(string token, CancellationToken ct = default)
    {
        var link = await FindAsync(token, ct);
        if (link == null) return null;
        var inv = await PlatformInvoiceQueries.ByIdAsync(_db, link.PlatformCompanyId, link.DocumentId, ct);
        var open = inv != null && inv.BuyerCompanyId == link.BuyerCompanyId && PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue);
        var paid = inv != null && inv.Status != DocumentStatus.Voided && inv.BalanceDue <= PlatformInvoiceRules.Epsilon;
        var state = PlatformPaymentLinkRules.StateOf(link.ExpiresAt, link.RevokedAt, open, paid, DateTime.UtcNow);
        if (state != PaymentLinkState.Valid)
            throw new BusinessRuleException(PlatformPaymentLinkRules.Message(state)!, "PLATFORM-PAYLINK-" + state.ToString().ToUpperInvariant(), 410);
        return (link.Id, link.BuyerCompanyId, link.DocumentId);
    }

    public async Task<Guid?> ResolveIntentPayerAsync(string token, Guid intentId, CancellationToken ct = default)
    {
        var link = await FindAsync(token, ct);
        if (link == null || !PlatformPaymentLinkRules.IntentStatusAllowed(link.RevokedAt, link.ExpiresAt, DateTime.UtcNow)) return null;
        var source = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.Id == intentId && i.PayerCompanyId == link.BuyerCompanyId && i.SourceKind == PaymentSourceKind.SubscriptionPayment)
            .Select(i => (Guid?)i.SourceId).FirstOrDefaultAsync(ct);
        if (source == null) return null;
        var ofThisInvoice = await _db.SubscriptionPayments.AsNoTracking()
            .AnyAsync(p => p.Id == source.Value && p.PlatformInvoiceDocumentId == link.DocumentId && p.Subscription.CompanyId == link.BuyerCompanyId, ct);
        return ofThisInvoice ? link.BuyerCompanyId : null;
    }

    public async Task<string> ReturnUrlAsync(CancellationToken ct = default)
    {
        var baseUrl = (await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct))?.TrimEnd('/');
        return (baseUrl ?? "") + ReturnPath;
    }

    public async Task RecordPaymentAuditAsync(Guid linkId, Guid buyerCompanyId, Guid paymentId, string paymentNumber, string? ip, CancellationToken ct = default)
    {
        Audit(buyerCompanyId, linkId, null, "paylink", ip, AuditAction.Create, RulePay, new { paymentId, paymentNumber });
        await _db.SaveChangesAsync(ct);
    }

    private void Audit(Guid buyerCompanyId, Guid linkId, Guid? userId, string? email, string? ip, AuditAction action, string rule, object detail)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = buyerCompanyId,
            UserId = userId,
            UserEmail = email ?? "",
            Action = action,
            EntityType = nameof(PlatformPaymentLink),
            EntityId = linkId.ToString(),
            NewValues = JsonSerializer.Serialize(new { detail, ruleCode = rule }),
            IpAddress = ip,
            Timestamp = DateTime.UtcNow,
        });
}
