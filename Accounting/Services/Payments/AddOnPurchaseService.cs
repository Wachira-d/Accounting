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
    string? RejectedReason, DateTime? ReviewedAt,
    // รอบ 227 (C-3): ใบเสร็จ/ใบกำกับของการชำระล่วงหน้า (เฉพาะสถานะ "ชำระแล้ว" — งวดใหม่ที่ยังไม่จ่ายไม่โชว์ใบของงวดก่อน)
    string? ReceiptNumber = null, Guid? ReceiptDocumentId = null);

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

    /// <summary>ฝ่ายค้านรอบ 227 S3: ออกใบเสร็จของการชำระออนไลน์ครั้งนี้อีกครั้ง (แอดมินแพลตฟอร์ม) — เลขอ้างอิงเดิม ⇒ ได้ใบเดิมถ้าเคยออกแล้ว · null = ออกไม่ได้</summary>
    Task<string?> ReissueGatewayReceiptAsync(Guid payerCompanyId, Guid featureRowId, Guid intentId, CancellationToken ct = default);

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
    /// <summary>รอบ 227 (C-3): ออกใบเสร็จ/ใบกำกับของเงินที่รับแล้วใน tenant ผู้ให้บริการ · null (เทสต์เดิม) = ไม่ออก (พฤติกรรมเดิม)</summary>
    private readonly Accounting.Services.Implementations.IPlatformBillingDocumentIssuer? _issuer;

    public AddOnPurchaseService(AccountingDbContext db, IUsageMeteringService metering,
        ILogger<AddOnPurchaseService> logger, IEmailService? email = null,
        Accounting.Services.Implementations.IPlatformBillingDocumentIssuer? issuer = null)
    { _db = db; _metering = metering; _logger = logger; _email = email; _issuer = issuer; }

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
        f.PaymentSlipUrl, f.SlipUploadedAt, f.PaymentRejectedReason, f.PaymentReviewedAt,
        f.PaymentStatus == AddOnPaymentStatus.Paid ? f.PaymentReceiptNumber : null,
        f.PaymentStatus == AddOnPaymentStatus.Paid ? f.PaymentReceiptDocumentId : null);

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT C-3): ออกใบกำกับภาษี/ใบเสร็จรับเงินของเงินค่าส่วนเสริมที่รับแล้ว (ออนไลน์/สลิปที่อนุมัติ) แล้วผูกกับแถวสิทธิ์ ·
    /// idempotent ด้วยเลขอ้างอิงของการรับเงินครั้งนั้น · ล้ม/ยังไม่ตั้ง tenant ผู้ให้บริการ ⇒ ไม่โยน (สิทธิ์เปิดแล้ว) แต่ล้มดัง: audit chain + log ให้แอดมินออกเอง</summary>
    /// <returns>เลขใบที่ออก (หรือใบเดิมของเลขอ้างอิงนี้) · null = ไม่ได้ออก</returns>
    private async Task<string?> IssueReceiptAsync(CompanyFeature f, string name, decimal amount, DateTime paidAt, string reference, Guid? intentId,
        CancellationToken ct)
    {
        if (_issuer == null || amount <= 0m) return null;
        // กันซ้ำ = เลขอ้างอิงของการรับเงินครั้งนี้ (ตัวออกใบคืนใบเดิมเมื่อมีแล้ว) — ไม่ใช้ "มีเลขใบแล้ว" เพราะเลขของงวดก่อนค้างอยู่ได้
        var period = string.IsNullOrWhiteSpace(f.UpfrontPeriod) ? "" : $" งวด {f.UpfrontPeriod}";
        var doc = await _issuer.IssueAddOnReceiptAsync(f.CompanyId, $"ค่าส่วนเสริม {name}{period}", amount, paidAt, reference, intentId);
        if (doc != null)
        {
            f.PaymentReceiptDocumentId = doc.DocumentId;
            f.PaymentReceiptNumber = doc.DocumentNumber;
            f.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return doc.DocumentNumber;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = f.CompanyId, EntityType = "CompanyFeature", EntityId = f.Id.ToString(), Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "addon-receipt-not-issued", ruleCode = AddOnReceiptRuleCode, featureCode = f.FeatureCode, amount, reference, intentId,
                note = "รับเงินค่าส่วนเสริมแล้วแต่ออกใบเสร็จ/ใบกำกับใน tenant ผู้ให้บริการไม่สำเร็จ (ยังไม่ตั้งบริษัทผู้ให้บริการ/ผังบัญชี) — ออกด้วยมือในบริษัทผู้ให้บริการ",
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogError("รับเงินค่าส่วนเสริม {Feature} บริษัท {Company} ยอด {Amount:N2} แล้วแต่ออกใบเสร็จไม่สำเร็จ (อ้างอิง {Ref}) — ต้องออกด้วยมือ",
            f.FeatureCode, f.CompanyId, amount, reference);
        return null;
    }

    public async Task<string?> ReissueGatewayReceiptAsync(Guid payerCompanyId, Guid featureRowId, Guid intentId, CancellationToken ct = default)
    {
        var f = await _db.CompanyFeatures.FirstOrDefaultAsync(x => x.Id == featureRowId && x.CompanyId == payerCompanyId && !x.IsDeleted, ct);
        // ใบของการชำระครั้งนี้เท่านั้น: แถวต้องถูกจ่ายด้วย intent นี้ (งวดถัดไป/สลิป = คนละการรับเงิน)
        if (f == null || f.PaymentStatus != AddOnPaymentStatus.Paid || f.PaymentIntentId != intentId) return null;
        var intent = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.Id == intentId && i.PayerCompanyId == payerCompanyId && i.SourceId == featureRowId)
            .Select(i => new { i.Amount, PaidAt = i.PaidAt ?? i.ConfirmedAt })
            .FirstOrDefaultAsync(ct);
        if (intent == null) return null;
        var name = await _db.ApiFeatures.AsNoTracking().Where(a => a.FeatureCode == f.FeatureCode).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? f.FeatureCode;
        return await IssueReceiptAsync(f, name, intent.Amount, intent.PaidAt ?? DateTime.UtcNow, ReceiptReference(f.Id, intentId, null), intentId, ct);
    }

    /// <summary>รอบ 227: รหัสกฎของ "รับเงินแล้วแต่ไม่มีใบเสร็จ" (audit) — §86/4 · §78/1 จุดรับเงินค่าบริการ</summary>
    public const string AddOnReceiptRuleCode = "RD-86/4-ADDON-RECEIPT";

    /// <summary>เลขอ้างอิงของการรับเงินค่าส่วนเสริมหนึ่งครั้ง — ตัวกันออกใบซ้ำ (ออนไลน์ = ต่อ intent · สลิป = ต่อไฟล์สลิปที่อนุมัติ) · pure</summary>
    /// <remarks>ฝ่ายค้านรอบ 227 S5: สลิปใส่งวด (<c>UpfrontPeriod</c>) ด้วย — งวดใหม่ที่ไม่มีเวลาแนบสลิป (แอดมินอนุมัติตรง) ต้องไม่ได้ "ใบเดิม" ของงวดก่อน</remarks>
    public static string ReceiptReference(Guid featureRowId, Guid? intentId, DateTime? slipUploadedAt, string? upfrontPeriod = null)
        => intentId is Guid i ? $"ADDON-{i:N}"
            : $"ADDON-SLIP-{featureRowId:N}-{(string.IsNullOrWhiteSpace(upfrontPeriod) ? "-" : upfrontPeriod.Trim())}-{(slipUploadedAt ?? DateTime.MinValue):yyyyMMddHHmmss}";

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
        // รอบ 227 (C-3): ยอดที่ต้องชำระ ณ ก่อนอนุมัติ (ราคาที่ผู้เช่ายอมรับ — เซิร์ฟเวอร์) = ยอดบนใบเสร็จ · อนุมัติซ้ำของแถวที่ชำระแล้ว = 0 (ไม่ออกใบเพิ่ม)
        var receiptAmount = AddOnPaymentPolicy.AmountDue(f.PaymentStatus, f.AcceptedUnitPrice);
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

        // รอบ 227 (C-3): เงินโอนที่อนุมัติแล้ว ⇒ ใบกำกับภาษี/ใบเสร็จรับเงินใน tenant ผู้ให้บริการ (วันที่ = วันอนุมัติ — สลิปค่าส่วนเสริมไม่มีช่องวันที่โอน)
        await IssueReceiptAsync(f, x.Name, receiptAmount, DateTime.UtcNow, ReceiptReference(f.Id, null, f.SlipUploadedAt, f.UpfrontPeriod), null, ct);

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

        // รอบ 227 (C-3): เงินออนไลน์เข้าแล้ว ⇒ ใบกำกับภาษี/ใบเสร็จรับเงิน (ขาเงินเข้า = บัญชีพักผู้ให้บริการรับชำระ) + แจ้งเจ้าของบริษัท (เดิมเงียบทั้งคู่)
        // ฝ่ายค้าน N5: ขอบเขต = รายการของบริษัทผู้จ่ายนี้ (intent อยู่ใต้บริษัทผู้ให้บริการ — กรองด้วย PayerCompanyId)
        var paidAt = await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == intentId && i.PayerCompanyId == companyId)
            .Select(i => i.PaidAt ?? i.ConfirmedAt).FirstOrDefaultAsync(ct) ?? DateTime.UtcNow;
        var receiptNo = await IssueReceiptAsync(f, x.Name, amount, paidAt, ReceiptReference(f.Id, intentId, null), intentId, ct);
        await NotifyOwnerAsync(companyId, x.Name,
            $"ชำระค่าส่วนเสริม “{x.Name}” เรียบร้อยแล้ว",
            $"<p>ได้รับเงินค่าส่วนเสริม <b>{System.Net.WebUtility.HtmlEncode(x.Name)}</b> จำนวน {amount:N2} บาทแล้ว — ใช้งานได้ตามปกติ</p>"
            + (receiptNo == null ? "" : $"<p>เลขที่ใบเสร็จ/ใบกำกับภาษี: <b>{System.Net.WebUtility.HtmlEncode(receiptNo)}</b></p>"), ct);
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
