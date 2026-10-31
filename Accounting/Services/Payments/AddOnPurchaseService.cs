using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

public sealed record AddOnPaymentState(
    string FeatureCode, string FeatureName, bool IsEnabled,
    AddOnPaymentStatus PaymentStatus, string StatusLabel,
    decimal? AmountDue, string? SlipUrl, DateTime? SlipUploadedAt,
    string? RejectedReason, DateTime? ReviewedAt);

public interface IAddOnPurchaseService
{
    Task<AddOnPaymentState?> GetAsync(Guid companyId, string featureCode, CancellationToken ct = default);

    /// <summary>ลูกค้าแนบสลิปค่าส่วนเสริม — เส้นสำรองเมื่อยังไม่เปิด gateway</summary>
    Task<AddOnPaymentState> UploadSlipAsync(Guid companyId, string featureCode,
        string slipUrl, string? reference, decimal? amount, string actor, CancellationToken ct = default);

    /// <summary>แอดมินแพลตฟอร์มอนุมัติสลิป</summary>
    Task<AddOnPaymentState> ApproveAsync(Guid companyId, string featureCode, string actor, CancellationToken ct = default);

    /// <summary>แอดมินปฏิเสธ — <b>ปิดสิทธิ์จริง</b> พร้อมเหตุผลที่ลูกค้าเห็นได้</summary>
    Task<AddOnPaymentState> RejectAsync(Guid companyId, string featureCode,
        string reason, string actor, CancellationToken ct = default);

    /// <summary>เงินเข้าผ่าน gateway แล้ว — เรียกจาก handler เท่านั้น</summary>
    Task MarkPaidByGatewayAsync(Guid companyId, string featureCode,
        Guid intentId, decimal amount, string actor, CancellationToken ct = default);

    /// <summary>ยอดที่ต้องชำระของ add-on นี้ (0 = ไม่ต้องจ่าย)</summary>
    Task<decimal> AmountDueAsync(Guid companyId, string featureCode, CancellationToken ct = default);
}

/// <summary>
/// **เส้นชำระเงินของ add-on — สองทาง ต้นทางเดียว** (LDG-P0-03)
///
/// <list type="bullet">
///   <item><b>เปิด gateway แล้ว</b> → สร้าง <c>PaymentIntent</c>
///     (<c>SourceKind = AddOnPurchase</c>) · เงินเข้า → <c>AddOnPaymentHandler</c>
///     เรียก <see cref="MarkPaidByGatewayAsync"/> → ใช้ได้ทันที ไม่ต้องรอคน</item>
///   <item><b>ยังไม่เปิด gateway</b> → ลูกค้าโอนแล้วแนบสลิป → <c>PendingReview</c>
///     → แอดมินอนุมัติ/ปฏิเสธ · <b>ปฏิเสธ = ปิดฟีเจอร์จริง</b></item>
/// </list>
///
/// <para>⚠️ <b>ไม่เขียนตัวตรวจสลิปตัวที่สอง</b> — การตรวจสลิปเชิงลึก (OCR ยอด/
/// วันที่/อ้างอิง) มีอยู่แล้วบนเส้น <c>SubscriptionPayment</c>; ที่นี่เก็บไฟล์ +
/// สถานะ แล้วให้คนตัดสิน ตามที่เจ้าของโปรเจกต์กำหนด</para>
///
/// <para>การปิดสิทธิ์เดินผ่าน <c>IUsageMeteringService.SetFeatureEnabledAsync</c>
/// ตัวเดิมเสมอ — ห้ามเขียน <c>CompanyFeature.IsEnabled = false</c> เองที่นี่
/// (ไม่งั้นจะได้ทางปิดสิทธิ์สองทางที่ audit ไม่ตรงกัน)</para>
/// </summary>
public class AddOnPurchaseService : IAddOnPurchaseService
{
    private readonly AccountingDbContext _db;
    private readonly IUsageMeteringService _metering;
    private readonly ILogger<AddOnPurchaseService> _logger;
    private readonly IEmailService? _email;

    public AddOnPurchaseService(AccountingDbContext db, IUsageMeteringService metering,
        ILogger<AddOnPurchaseService> logger, IEmailService? email = null)
    { _db = db; _metering = metering; _logger = logger; _email = email; }

    private async Task<(CompanyFeature Row, string Name)?> LoadAsync(
        Guid companyId, string featureCode, bool tracked, CancellationToken ct)
    {
        var q = tracked ? _db.CompanyFeatures : _db.CompanyFeatures.AsNoTracking();
        var row = await q.FirstOrDefaultAsync(
            f => f.CompanyId == companyId && f.FeatureCode == featureCode && !f.IsDeleted, ct);
        if (row == null) return null;
        var name = await _db.ApiFeatures.AsNoTracking()
            .Where(f => f.FeatureCode == featureCode).Select(f => f.Name).FirstOrDefaultAsync(ct);
        return (row, name ?? featureCode);
    }

    private static AddOnPaymentState Map(CompanyFeature f, string name, decimal? due) => new(
        f.FeatureCode, name, f.IsEnabled, f.PaymentStatus,
        AddOnPaymentPolicy.StatusLabel(f.PaymentStatus), due,
        f.PaymentSlipUrl, f.SlipUploadedAt, f.PaymentRejectedReason, f.PaymentReviewedAt);

    public async Task<decimal> AmountDueAsync(Guid companyId, string featureCode, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(companyId, featureCode, tracked: false, ct);
        if (loaded is not { } x) return 0m;
        return AddOnPaymentPolicy.AmountDue(x.Row.PaymentStatus, x.Row.AcceptedUnitPrice);
    }

    public async Task<AddOnPaymentState?> GetAsync(Guid companyId, string featureCode, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(companyId, featureCode, tracked: false, ct);
        if (loaded is not { } x) return null;
        return Map(x.Row, x.Name, await AmountDueAsync(companyId, featureCode, ct));
    }

    public async Task<AddOnPaymentState> UploadSlipAsync(Guid companyId, string featureCode,
        string slipUrl, string? reference, decimal? amount, string actor, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(companyId, featureCode, tracked: true, ct);
        if (loaded is not { } x)
            throw new BusinessRuleException("ยังไม่ได้เปิดใช้ส่วนเสริมนี้ — กรุณากดเปิดใช้งานก่อนแนบสลิป");
        var f = x.Row;
        if (f.PaymentStatus == AddOnPaymentStatus.NotRequired)
            throw new BusinessRuleException("ส่วนเสริมนี้ไม่มีค่าใช้จ่ายที่ต้องชำระล่วงหน้า");
        if (f.PaymentStatus == AddOnPaymentStatus.Paid)
            throw new BusinessRuleException("ยืนยันการชำระเงินของส่วนเสริมนี้แล้ว");

        f.PaymentSlipUrl = slipUrl;
        f.PaymentReference = reference;
        f.PaidAmount = amount;
        f.SlipUploadedAt = DateTime.UtcNow;
        f.PaymentStatus = AddOnPaymentStatus.PendingReview;
        // ส่งใหม่หลังถูกปฏิเสธ = ล้างเหตุผลเดิมออกจากหน้าจอ
        f.PaymentRejectedReason = null;
        f.PaymentReviewedAt = null;
        f.PaymentReviewedBy = null;
        f.UpdatedAt = DateTime.UtcNow;
        f.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("รับสลิปค่าส่วนเสริม {Feature} ของบริษัท {Company} — รอตรวจ", featureCode, companyId);
        return Map(f, x.Name, await AmountDueAsync(companyId, featureCode, ct));
    }

    public Task<AddOnPaymentState> ApproveAsync(Guid companyId, string featureCode,
        string actor, CancellationToken ct = default)
    {
        return SessionAdvisoryLock.RunAsync(_db, AdvisoryLockKey.For(companyId, SessionAdvisoryLock.PlatformAddOnPayment, featureCode),
            () => ApproveLockedAsync(companyId, featureCode, actor, ct), ct);
    }

    private async Task<AddOnPaymentState> ApproveLockedAsync(Guid companyId, string featureCode, string actor, CancellationToken ct)
    {
        var loaded = await LoadAsync(companyId, featureCode, tracked: true, ct);
        if (loaded is not { } x) throw new BusinessRuleException("ไม่พบส่วนเสริมของบริษัทนี้");
        var f = x.Row;
        await _db.Entry(f).ReloadAsync(ct);
        // รอบ 219 ส่วน B ฝ่ายค้าน S2: ชำระออนไลน์สำเร็จไปแล้ว ⇒ อนุมัติสลิปซ้ำไม่ได้ (เงินโอนต้องคืน/ปฏิเสธสลิป)
        if (f.PaymentStatus == AddOnPaymentStatus.Paid && f.PaymentIntentId != null)
            throw new BusinessRuleException(PlatformDoublePayment.SlipAfterOnlineMessage, PlatformDoublePayment.RuleCode);
        f.PaymentStatus = AddOnPaymentStatus.Paid;
        f.PaymentRejectedReason = null;
        f.PaymentReviewedAt = DateTime.UtcNow;
        f.PaymentReviewedBy = actor;
        f.UpdatedAt = DateTime.UtcNow;
        f.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);

        // เปิดสิทธิ์ให้แน่ใจ (เผื่อเคยถูกปิดจากการปฏิเสธรอบก่อน)
        if (!f.IsEnabled)
            await _metering.SetFeatureEnabledAsync(companyId, featureCode, true, actor, ct);

        await NotifyOwnerAsync(companyId, x.Name,
            $"ยืนยันการชำระเงินส่วนเสริม “{x.Name}” แล้ว",
            $"<p>ผู้ดูแลระบบตรวจสอบการชำระเงินของส่วนเสริม <b>{System.Net.WebUtility.HtmlEncode(x.Name)}</b> เรียบร้อยแล้ว — ใช้งานได้ตามปกติ</p>", ct);
        return Map(f, x.Name, 0m);
    }

    public async Task<AddOnPaymentState> RejectAsync(Guid companyId, string featureCode,
        string reason, string actor, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length == 0)
            throw new BusinessRuleException(
                "ต้องระบุเหตุผลที่ไม่อนุมัติ — ลูกค้าจะเห็นข้อความนี้และต้องรู้ว่าต้องทำอะไรต่อ");
        if (why.Length > 500) why = why[..500];

        var loaded = await LoadAsync(companyId, featureCode, tracked: true, ct);
        if (loaded is not { } x) throw new BusinessRuleException("ไม่พบส่วนเสริมของบริษัทนี้");
        var f = x.Row;
        f.PaymentStatus = AddOnPaymentStatus.Rejected;
        f.PaymentRejectedReason = why;
        f.PaymentReviewedAt = DateTime.UtcNow;
        f.PaymentReviewedBy = actor;
        f.UpdatedAt = DateTime.UtcNow;
        f.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);

        // ⬅ หัวใจของข้อนี้: **ปฏิเสธแล้วต้องปิดสิทธิ์จริง**
        // เดิม Reject บนเส้นแพ็กเกจไม่เคยไปแตะสิทธิ์อะไรเลย ⇒ สลิปปลอมก็ยังใช้ฟีเจอร์ได้
        if (f.IsEnabled)
            await _metering.SetFeatureEnabledAsync(companyId, featureCode, false, actor, ct);

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Update,
            EntityType = nameof(CompanyFeature),
            EntityId = f.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "AddOnPaymentRejected", featureCode, reason = why,
                featureDisabled = true, by = actor,
            }),
        });
        await _db.SaveChangesAsync(ct);

        await NotifyOwnerAsync(companyId, x.Name,
            $"การชำระเงินส่วนเสริม “{x.Name}” ไม่ผ่านการตรวจสอบ",
            $"<p>ส่วนเสริม <b>{System.Net.WebUtility.HtmlEncode(x.Name)}</b> ถูก<b>ปิดการใช้งาน</b>แล้ว</p>"
            + $"<p><b>เหตุผล:</b> {System.Net.WebUtility.HtmlEncode(why)}</p>"
            + "<p>กรุณาตรวจสอบและส่งหลักฐานการชำระเงินใหม่ที่หน้า “ส่วนเสริมของฉัน”</p>", ct);

        _logger.LogWarning("ปฏิเสธการชำระเงินส่วนเสริม {Feature} ของบริษัท {Company} — ปิดสิทธิ์แล้ว · เหตุผล: {Reason}",
            featureCode, companyId, why);
        return Map(f, x.Name, await AmountDueAsync(companyId, featureCode, ct));
    }

    public Task MarkPaidByGatewayAsync(Guid companyId, string featureCode,
        Guid intentId, decimal amount, string actor, CancellationToken ct = default)
    {
        return SessionAdvisoryLock.RunAsync(_db, AdvisoryLockKey.For(companyId, SessionAdvisoryLock.PlatformAddOnPayment, featureCode),
            () => MarkPaidByGatewayLockedAsync(companyId, featureCode, intentId, amount, actor, ct), ct);
    }

    /// <summary>รอบ 219 ส่วน B ฝ่ายค้าน S2: ใต้ล็อกเดียวกับแอดมินอนุมัติสลิป — จ่ายแล้วด้วยรายการอื่น ⇒ ล้มดัง (audit + ข้อผิดพลาดถึงผู้ส่งต่อ ⇒ ประวัติ ⚠️ + แจ้ง) ·
    /// สลิปรอตรวจอยู่ ⇒ ยังเปิดสิทธิ์ (เงินออนไลน์ยืนยันแล้ว) แต่บันทึก audit ให้แอดมินปฏิเสธสลิป/คืนเงินโอน</summary>
    private async Task MarkPaidByGatewayLockedAsync(Guid companyId, string featureCode,
        Guid intentId, decimal amount, string actor, CancellationToken ct)
    {
        var loaded = await LoadAsync(companyId, featureCode, tracked: true, ct);
        if (loaded is not { } x)
        {
            // เงินเข้าแล้วแต่หาแถวไม่เจอ = ต้องดัง ไม่ใช่เงียบ (มีคนต้องคืนเงินหรือเปิดให้ด้วยมือ)
            _logger.LogError(
                "เงินค่าส่วนเสริมเข้าแล้วแต่หาสิทธิ์ไม่พบ — บริษัท {Company} ฟีเจอร์ {Feature} "
                + "intent {Intent} ยอด {Amount:N2} · ต้องเปิดสิทธิ์ด้วยมือ",
                companyId, featureCode, intentId, amount);
            return;
        }
        var f = x.Row;
        await _db.Entry(f).ReloadAsync(ct);
        if (f.PaymentStatus == AddOnPaymentStatus.Paid && f.PaymentIntentId == intentId)
            return;   // webhook ซ้ำ — ไม่ใช่ error
        var settle = PlatformDoublePayment.OnGatewaySuccess(f.PaymentStatus == AddOnPaymentStatus.Paid, f.PaymentIntentId, intentId,
            f.PaymentStatus == AddOnPaymentStatus.PendingReview);
        if (settle != PlatformDoublePayment.Outcome.Apply)
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, EntityType = "CompanyFeature", EntityId = f.Id.ToString(), Action = AuditAction.Update,
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = PlatformDoublePayment.AuditAction, ruleCode = PlatformDoublePayment.RuleCode, featureCode, intentId, amount,
                    paidBy = f.PaymentIntentId, slipPending = f.PaymentStatus == AddOnPaymentStatus.PendingReview,
                }),
                Timestamp = DateTime.UtcNow,
            });
            _logger.LogError("ค่าส่วนเสริม {Feature} บริษัท {Company} ชำระซ้อน (intent {Intent}): {Outcome}", featureCode, companyId, intentId, settle);
            if (settle == PlatformDoublePayment.Outcome.AlreadySettled)
            {
                await _db.SaveChangesAsync(ct);
                throw new BusinessRuleException(PlatformDoublePayment.AlreadySettledMessage, PlatformDoublePayment.RuleCode);
            }
        }

        f.PaymentStatus = AddOnPaymentStatus.Paid;
        f.PaymentIntentId = intentId;
        f.PaidAmount = amount;
        f.PaymentRejectedReason = null;
        f.PaymentReviewedAt = DateTime.UtcNow;
        f.PaymentReviewedBy = actor;
        f.UpdatedAt = DateTime.UtcNow;
        f.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);

        if (!f.IsEnabled)
            await _metering.SetFeatureEnabledAsync(companyId, featureCode, true, actor, ct);
    }

    /// <summary>แจ้งเจ้าของบริษัท — การเปลี่ยนแปลงที่กระทบสิทธิ์ห้ามเงียบ</summary>
    private async Task NotifyOwnerAsync(Guid companyId, string _, string subject, string bodyHtml, CancellationToken ct)
    {
        if (_email == null) return;
        try
        {
            var to = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Join(_db.Users.AsNoTracking(), cu => cu.UserId, u => u.Id, (cu, u) => u.Email)
                .Where(e => e != null && e != "")
                .Take(3).ToListAsync(ct);
            foreach (var addr in to) await _email.SendAsync(addr!, subject, bodyHtml);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ส่งอีเมลแจ้งผลการชำระเงินส่วนเสริมของบริษัท {Company} ไม่สำเร็จ", companyId);
        }
    }
}
