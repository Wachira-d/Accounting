using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Platform;

/// <summary>แถวในหน้าติดตาม "50 ทวิ จากลูกค้า" ของแอดมิน — ป้าย/สถานะจาก <see cref="PlatformWhtCertificateStatus"/> ตัวเดียว</summary>
public sealed record PlatformWhtCertificateRow(
    Guid PaymentId, string PaymentNumber, Guid CompanyId, string CompanyName, decimal Amount, decimal WithholdingTaxAmount,
    DateTime PaymentDate, DateTime? ApprovedAt, string? ReceiptNumber, string State, string StateLabel, bool Overdue, int? DaysSinceApproval,
    bool HasFile, DateTime? SubmittedAt, string? RejectedReason, DateTime? RemindedAt,
    Guid? WhtCreditId, string? WhtCreditStatus, string? CertificateNumber, DateTime? CertificateDate, string? RegisterNote);

/// <summary>รอบ 235 (ฝ่ายค้าน N-4): แถว "50 ทวิ จากลูกค้า" ของโควตาเสริมที่ผู้เช่าหัก ณ ที่จ่าย — อ่านจากทะเบียนภาษีถูกหัก (แถว GL ของใบเสร็จ) · ยังไม่มีเส้นส่งไฟล์ของผู้เช่า
/// (แอดมินแนบ/ยืนยันที่ wht-credit.html) · ทวงผ่านเส้นแจ้งเตือนเดิม</summary>
public sealed record PlatformQuotaWhtRow(Guid PurchaseId, Guid CompanyId, string CompanyName, string Label, decimal WithholdingTaxAmount, string? ReceiptNumber,
    DateTime? PaidAt, Guid? WhtCreditId, string? WhtCreditStatus, string? CertificateNumber, string? RegisterNote, DateTime? LastRemindedAt);

/// <summary>
/// รอบ 234 (คำตัดสิน 234-W1…234-W5) — ติดตามหนังสือรับรอง 50 ทวิ ที่ผู้เช่าต้องส่งให้ NextAcc เมื่อหักภาษี ณ ที่จ่ายค่าบริการ ·
/// <b>ทะเบียนเดียว</b> = <c>WhtCreditReceived</c> ของบริษัทผู้ให้บริการ (หน้า <c>wht-credit.html</c> · เครดิต ภ.ง.ด.50/51)
/// <list type="bullet">
/// <item>แถวทะเบียนเกิดจาก GL เท่านั้น (Dr 11910 ของใบเสร็จ/ใบแจ้งหนี้ที่ตัวออกใบลง · <c>DocumentService.SyncWhtCreditReceivedAsync</c>) ⇒ ทะเบียนกระทบ GL ได้เสมอ ·
/// ตัวนี้แค่<b>ผูก</b>รายการชำระกับแถวนั้น (<see cref="LinkCoreAsync"/> · idempotent) — ไม่สร้างแถวที่สอง/ไม่แต่งยอด</item>
/// <item>ผู้เช่าส่งไฟล์ ⇒ "รอตรวจ" (แถวยัง Pending) · แอดมินยืนยัน (เลขที่/วันที่บนใบ) ⇒ <see cref="WhtCreditService.MarkReceivedAsync"/> = เครดิตได้ ·
/// ตีกลับพร้อมเหตุผล ⇒ ผู้เช่าเห็นในศูนย์บิล + กระดิ่ง · ทวงได้วันละครั้ง</item>
/// </list>
/// </summary>
public class PlatformWhtCertificateService
{
    private readonly AccountingDbContext _db;
    private readonly WhtCreditService _credits;
    private readonly INotificationService? _notifications;
    private readonly ILogger<PlatformWhtCertificateService> _logger;
    private readonly IWebHostEnvironment? _env;

    public PlatformWhtCertificateService(AccountingDbContext db, WhtCreditService credits, ILogger<PlatformWhtCertificateService> logger,
        INotificationService? notifications = null, IWebHostEnvironment? env = null)
    { _db = db; _credits = credits; _logger = logger; _notifications = notifications; _env = env; }

    /// <summary>รอบ 235 (ปิด backlog 234-W3 · คำตัดสิน 235-7): ไฟล์ 50 ทวิ ที่ผู้เช่าส่ง ⇒ <b>ไฟล์แนบของแถวทะเบียนภาษีถูกหัก</b> (FileAttachment ของบริษัทผู้ให้บริการ ·
    /// EntityType "WhtCredit" · EntityId = แถว) โดย<b>อ้างไฟล์เดิม</b>ในที่เก็บกลาง (StoragePath = ตำแหน่งสลิป/50 ทวิ เดิม — ไม่คัดลอกไบต์ ไม่ซ้ำไฟล์) ⇒ หน้า
    /// <c>wht-credit.html</c> ของบริษัทผู้ให้บริการเปิดดูได้ผ่าน endpoint ไฟล์แนบเดิม (ตรวจสิทธิ์โมดูลภาษี) · idempotent (แถวเดิมของไฟล์เดียวกัน ⇒ id เดิม) ·
    /// ไม่มีไฟล์/ชื่อใช้ไม่ได้ ⇒ null (ยืนยันได้ตามเดิม — ไม่แต่งไฟล์)</summary>
    public static async Task<Guid?> CertificateAttachmentAsync(AccountingDbContext db, Guid platformId, Guid creditId, SubscriptionPayment p,
        (string Path, string ContentType)? stored, CancellationToken ct = default)
    {
        if (stored is not { } f || string.IsNullOrWhiteSpace(f.Path)) return null;
        var existing = await db.FileAttachments.AsNoTracking()
            .Where(a => a.CompanyId == platformId && a.EntityType == "WhtCredit" && a.EntityId == creditId && a.StoragePath == f.Path)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (existing != null) return existing;
        var name = System.IO.Path.GetFileName(f.Path);
        var att = new FileAttachment
        {
            CompanyId = platformId, FileName = name, OriginalFileName = $"50ทวิ-{p.PaymentNumber}{System.IO.Path.GetExtension(name)}",
            ContentType = f.ContentType, FileSize = 0, StoragePath = f.Path, EntityType = "WhtCredit", EntityId = creditId,
            UploadedByUserId = null,   // ผู้ส่ง = ผู้เช่า (คนละบริษัท) — ไม่รู้ผู้ใช้ในบริษัทผู้ให้บริการ ⇒ null ไม่แต่ง
        };
        db.FileAttachments.Add(att);
        await db.SaveChangesAsync(ct);
        return att.Id;
    }

    /// <summary>ผูกรายการชำระที่มีภาษีหักกับแถวทะเบียนภาษีถูกหัก (บริษัทผู้ให้บริการ) ที่ GL สร้างจากใบเสร็จ/ใบแจ้งหนี้ของรายการ · idempotent ·
    /// คืน id แถว (null = ยังไม่มีแถว — ใบเสร็จยังไม่ออก/ไม่มีขา 11910 · ผู้เรียกไม่ต้องทำอะไร: หน้าติดตามแสดงตามจริง) · ไม่ SaveChanges</summary>
    public static async Task<Guid?> LinkCoreAsync(AccountingDbContext db, SubscriptionPayment p, CancellationToken ct = default)
    {
        if (p.WithholdingTaxAmount <= 0m || p.Status != SubscriptionPaymentStatus.Approved) return null;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(db, ct);
        if (platformId is not Guid pid) return null;
        if (p.WhtCreditId is Guid linked
            && await db.WhtCreditsReceived.AnyAsync(w => w.Id == linked && w.CompanyId == pid && !w.IsDeleted, ct))
            return linked;
        var docs = new List<Guid>();
        if (p.PlatformDocumentId is Guid r) docs.Add(r);
        if (p.PlatformInvoiceDocumentId is Guid i) docs.Add(i);
        if (docs.Count == 0) { p.WhtCreditId = null; return null; }
        var receiptId = p.PlatformDocumentId;
        var payId = p.Id;
        // ฝ่ายค้าน 234 N1: ผูก "ต่อรายการชำระ" — แถวที่ผูกกับรายการชำระอื่นแล้วไม่ใช้ซ้ำ (ใบเดียวจ่ายหลายงวด ⇒ แถวรวมทั้งใบเป็นของงวดแรก ·
        // งวดถัดไปยังไม่มีแถวของตัวเอง ⇒ หน้าติดตามบอกตามจริง ไม่แชร์แถว/ไม่ยืนยันแทนกัน)
        var row = await db.WhtCreditsReceived
            .Where(w => w.CompanyId == pid && !w.IsDeleted && w.DocumentId != null && docs.Contains(w.DocumentId.Value))
            .Where(w => !db.SubscriptionPayments.Any(x => x.WhtCreditId == w.Id && x.Id != payId))
            .OrderByDescending(w => w.DocumentId == receiptId)
            .FirstOrDefaultAsync(ct);
        p.WhtCreditId = row?.Id;
        if (row != null)
        {
            // ประเภทเงินได้ของค่าบริการ (แถวจาก GL ไม่รู้ประเภท) — ใบรับรองของผู้เช่าระบุ ม.40(2) · ไม่แตะยอด (ยอดมาจาก GL)
            row.IncomeTypeCode ??= PlatformWhtSuggestion.IncomeTypeCode;
            var tag = $"ค่าบริการ NextAcc {p.PaymentNumber}";
            if (row.Notes == null || !row.Notes.Contains(tag)) row.Notes = row.Notes == null ? tag : $"{row.Notes} · {tag}";
        }
        return row?.Id;
    }

    private async Task<Guid> PlatformIdAsync(CancellationToken ct)
        => await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct)
           ?? throw new BusinessRuleException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ตั้งที่หน้าค่าตั้งแพลตฟอร์มก่อน", PlatformWhtCertificateStatus.RuleCode, 409);

    private static PlatformWhtCertificateStatus.State StateOf(SubscriptionPayment p, WhtCreditStatus? credit)
        => PlatformWhtCertificateStatus.Of(p.WithholdingTaxAmount, p.Status == SubscriptionPaymentStatus.Approved, p.WhtCertificateFileName != null,
            p.WhtCertificateRejectedReason, credit);

    private async Task<WhtCreditStatus?> CreditStatusAsync(Guid? creditId, CancellationToken ct)
        => creditId is Guid id && await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct) is Guid pid
            ? await _db.WhtCreditsReceived.AsNoTracking().Where(w => w.Id == id && w.CompanyId == pid && !w.IsDeleted).Select(w => (WhtCreditStatus?)w.Status).FirstOrDefaultAsync(ct)
            : null;

    private async Task<SubscriptionPayment> LoadAsync(Guid paymentId, CancellationToken ct)
        => await _db.SubscriptionPayments.Include(p => p.Subscription).FirstOrDefaultAsync(p => p.Id == paymentId, ct)
           ?? throw new KeyNotFoundException("ไม่พบรายการชำระ");

    public async Task<List<PlatformWhtCertificateRow>> ListAsync(int overdueDays, bool includeConfirmed, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var pays = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.WithholdingTaxAmount > 0m && p.Status == SubscriptionPaymentStatus.Approved)
            .OrderByDescending(p => p.ReviewedAt ?? p.CreatedAt)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.Subscription.CompanyId, p.Amount, p.WithholdingTaxAmount, p.PaymentDate, p.ReviewedAt, p.ReceiptNumber,
                HasFile = p.WhtCertificateFileName != null, p.WhtCertificateSubmittedAt, p.WhtCertificateRejectedReason, p.WhtCertificateRemindedAt, p.WhtCreditId,
            })
            .Take(1000).ToListAsync(ct);
        var creditIds = pays.Where(p => p.WhtCreditId != null).Select(p => p.WhtCreditId!.Value).Distinct().ToList();
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        var credits = await _db.WhtCreditsReceived.AsNoTracking().Where(w => creditIds.Contains(w.Id) && !w.IsDeleted && w.CompanyId == platformId)
            .Select(w => new { w.Id, w.Status, w.CertificateNumber, w.CertificateDate }).ToDictionaryAsync(w => w.Id, ct);
        var companyIds = pays.Select(p => p.CompanyId).Distinct().ToList();
        var names = await _db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var rows = new List<PlatformWhtCertificateRow>();
        foreach (var p in pays)
        {
            var c = p.WhtCreditId is Guid cid && credits.TryGetValue(cid, out var cr) ? cr : null;
            var st = PlatformWhtCertificateStatus.Of(p.WithholdingTaxAmount, true, p.HasFile, p.WhtCertificateRejectedReason, c?.Status);
            if (!includeConfirmed && st == PlatformWhtCertificateStatus.State.Confirmed) continue;
            var approved = p.ReviewedAt;
            rows.Add(new PlatformWhtCertificateRow(p.Id, p.PaymentNumber, p.CompanyId, names.GetValueOrDefault(p.CompanyId, "(ไม่พบชื่อ)"), p.Amount,
                p.WithholdingTaxAmount, p.PaymentDate, approved, p.ReceiptNumber, st.ToString(), PlatformWhtCertificateStatus.Label(st),
                PlatformWhtCertificateStatus.IsOverdue(st, approved, now, overdueDays),
                approved is DateTime a ? (int)(now - a).TotalDays : null,
                p.HasFile, p.WhtCertificateSubmittedAt, p.WhtCertificateRejectedReason, p.WhtCertificateRemindedAt,
                c?.Id, c?.Status.ToString(), c?.CertificateNumber, c?.CertificateDate,
                c == null ? "ยังไม่มีแถวในทะเบียนภาษีถูกหัก (ใบเสร็จยังไม่ออก หรือใบไม่มีขา 11910) — กด \"ผูกทะเบียนรายการเก่า\" หลังออกใบเสร็จ" : null));
        }
        return rows;
    }

    private const string QuotaRemindAction = "quota-addon-wht-remind";

    /// <summary>รอบ 235 (ฝ่ายค้าน N-4): โควตาเสริมที่หักภาษี (ใช้งานแล้ว) + แถวทะเบียนของใบเสร็จ (บริษัทผู้ให้บริการ) — ค่าเริ่มต้นแสดงเฉพาะที่ยังไม่ยืนยัน (Pending/ยังไม่มีแถว)</summary>
    public async Task<List<PlatformQuotaWhtRow>> ListQuotaAddOnsAsync(bool includeConfirmed, CancellationToken ct = default)
    {
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        var rows = await _db.CompanyQuotaAddOns.AsNoTracking()
            .Where(q => q.WithholdingTaxAmount > 0m && q.Status == QuotaAddOnStatus.Active)
            .OrderByDescending(q => q.ReviewedAt ?? q.CreatedAt)
            .Select(q => new { q.Id, q.CompanyId, q.ResourceKey, q.Units, q.WithholdingTaxAmount, q.ReceiptNumber, q.ReceiptDocumentId, PaidAt = q.ReviewedAt ?? q.PeriodStartUtc })
            .Take(500).ToListAsync(ct);
        var docIds = rows.Where(r => r.ReceiptDocumentId != null).Select(r => r.ReceiptDocumentId!.Value).ToList();
        var credits = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == platformId && !w.IsDeleted && w.DocumentId != null && docIds.Contains(w.DocumentId.Value))
            .Select(w => new { w.Id, DocumentId = w.DocumentId!.Value, w.Status, w.CertificateNumber }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id.ToString()).ToList();
        var reminded = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == nameof(CompanyQuotaAddOn) && ids.Contains(a.EntityId!) && a.NewValues != null && a.NewValues.Contains(QuotaRemindAction))
            .GroupBy(a => a.EntityId).Select(g => new { Id = g.Key, At = g.Max(a => a.Timestamp) }).ToListAsync(ct);
        var companyIds = rows.Select(r => r.CompanyId).Distinct().ToList();
        var names = await _db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var outRows = new List<PlatformQuotaWhtRow>();
        foreach (var r in rows)
        {
            var c = credits.FirstOrDefault(x => x.DocumentId == r.ReceiptDocumentId);
            if (!includeConfirmed && c != null && c.Status is WhtCreditStatus.Received or WhtCreditStatus.Claimed) continue;
            var res = QuotaResources.Find(r.ResourceKey);
            outRows.Add(new PlatformQuotaWhtRow(r.Id, r.CompanyId, names.GetValueOrDefault(r.CompanyId, "(ไม่พบชื่อ)"), $"โควตาเสริม {res?.Label ?? r.ResourceKey} × {r.Units:N0}",
                r.WithholdingTaxAmount, r.ReceiptNumber, r.PaidAt, c?.Id, c?.Status.ToString(), c?.CertificateNumber,
                c == null ? "ยังไม่มีแถวในทะเบียนภาษีถูกหัก (ใบเสร็จยังไม่ออก/ไม่มีขา 11910)" : null,
                reminded.FirstOrDefault(x => x.Id == r.Id.ToString())?.At));
        }
        return outRows;
    }

    /// <summary>รอบ 235 (ฝ่ายค้าน N-4): ทวง 50 ทวิ ของโควตาเสริม — วันละครั้ง (กติกาเดียวกับค่าแพ็กเกจ · ตัวนับ = audit chain) · แจ้งเจ้าของผ่านเส้นแจ้งเตือนเดิม</summary>
    public async Task RemindQuotaAddOnAsync(Guid purchaseId, string actor, CancellationToken ct = default)
    {
        var row = (await ListQuotaAddOnsAsync(false, ct)).FirstOrDefault(r => r.PurchaseId == purchaseId)
            ?? throw new BusinessRuleException("ไม่พบรายการโควตาเสริมที่รอ 50 ทวิ (ยืนยันแล้ว หรือไม่ได้หัก ณ ที่จ่าย)", PlatformWhtCertificateStatus.RuleCode, 404);
        if (row.LastRemindedAt is DateTime last && (DateTime.UtcNow - last).TotalHours < PlatformWhtCertificateStatus.MinRemindIntervalHours)
            throw new BusinessRuleException($"ทวงไปแล้วเมื่อไม่ถึง {PlatformWhtCertificateStatus.MinRemindIntervalHours} ชั่วโมงก่อน", PlatformWhtCertificateStatus.RuleCode, 409);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = row.CompanyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = purchaseId.ToString(),
            NewValues = JsonSerializer.Serialize(new { action = QuotaRemindAction, ruleCode = PlatformWhtCertificateStatus.RuleCode, row.WithholdingTaxAmount, by = actor }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        if (_notifications == null) return;
        var owners = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == row.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
        foreach (var u in owners)
            await _notifications.SendAsync(u, row.CompanyId, NotificationType.SubscriptionPaymentApproved, "กรุณาส่งหนังสือรับรอง 50 ทวิ",
                $"บริษัทของท่านหักภาษี ณ ที่จ่าย {row.WithholdingTaxAmount:N2} บาท จากค่า{row.Label} (ใบเสร็จ {row.ReceiptNumber ?? "-"}) — กรุณาส่งหนังสือรับรอง 50 ทวิ ให้ NextAcc",
                "/pages/billing.html#tab=docs", nameof(CompanyQuotaAddOn), purchaseId);
    }

    /// <summary>แอดมินยืนยัน 50 ทวิ ที่ผู้เช่าส่ง — เลขที่/วันที่บนใบ ⇒ แถวทะเบียน Received (เครดิตได้) · ล้างเรื่องค้าง · แจ้งผู้เช่า · audit</summary>
    public async Task<PlatformWhtCertificateRow?> ConfirmAsync(Guid paymentId, string? certificateNumber, DateTime? certificateDate, string actor,
        CancellationToken ct = default, decimal? certificateWhtAmount = null)
    {
        var p = await LoadAsync(paymentId, ct);
        var state = StateOf(p, await CreditStatusAsync(p.WhtCreditId, ct));
        if (PlatformWhtCertificateStatus.ConfirmRefusal(state, certificateNumber) is string why)
            throw new BusinessRuleException(why, PlatformWhtCertificateStatus.RuleCode, 409);
        var pid = await PlatformIdAsync(ct);
        var creditId = await LinkCoreAsync(_db, p, ct)
            ?? throw new BusinessRuleException(
                "ยังไม่มีแถวในทะเบียนภาษีถูกหักของบริษัทผู้ให้บริการ (ใบเสร็จยังไม่ออก หรือใบไม่มีขา Dr 11910) — ออกใบเสร็จ/ตัดชำระให้สำเร็จก่อน แล้วยืนยันอีกครั้ง",
                PlatformWhtCertificateStatus.RuleCode, 409);
        await _db.SaveChangesAsync(ct);
        // ฝ่ายค้าน 234 N1: ยอดภาษีบนใบ (แอดมินกรอกจากหนังสือรับรอง) ต้องตรงยอดที่ผู้เช่าหักจากรายการนี้ · แถวทะเบียน (ยอดจาก GL) ต้องตรงยอดหักของรายการ
        // (แถวรวมหลายงวดของใบเดียว ⇒ ยืนยันแทนกันไม่ได้) — ไม่ตรง = ปฏิเสธพร้อมตัวเลข
        if (PlatformWhtCertificateStatus.AmountMismatch(p.WithholdingTaxAmount, certificateWhtAmount) is string amountWhy)
            throw new BusinessRuleException(amountWhy, PlatformWhtCertificateStatus.RuleCode, 409);
        var regAmount = await _db.WhtCreditsReceived.AsNoTracking().Where(w => w.Id == creditId && w.CompanyId == pid).Select(w => w.WhtAmount).FirstAsync(ct);
        if (PlatformWhtCertificateStatus.AmountMismatch(p.WithholdingTaxAmount, regAmount) is string regWhy)
            throw new BusinessRuleException($"แถวทะเบียนภาษีถูกหัก (ยอดจากบัญชี) ไม่ตรงรายการนี้ — {regWhy} · ตรวจที่ทะเบียนภาษีถูกหักของบริษัทผู้ให้บริการ",
                PlatformWhtCertificateStatus.RuleCode, 409);
        // รอบ 235 (234-W3 ปิด): แนบไฟล์ที่ผู้เช่าส่งเข้าแถวทะเบียน (อ้างไฟล์เดิม) — ก่อน MarkReceived ที่ผูก AttachmentId (ไฟล์ของแถวนี้อยู่แล้ว ⇒ ไม่ย้าย)
        var attachmentId = _env == null ? null
            : await CertificateAttachmentAsync(_db, pid, creditId, p, Accounting.Services.Implementations.SubscriptionSlipUpload.OpenStored(_env, p.WhtCertificateFileName), ct);
        await _credits.MarkReceivedAsync(pid, creditId, certificateNumber!.Trim(), certificateDate, attachmentId);
        if (p.AttentionRule == PlatformPaymentAttention.RuleWhtCertPending)
            PlatformPaymentAttention.Clear(_db, p, p.Subscription.CompanyId, actor, $"ยืนยัน 50 ทวิ เลขที่ {certificateNumber!.Trim()}");
        p.WhtCertificateRejectedReason = null; p.WhtCertificateRejectedAt = null;
        History(p, "WhtCertificateConfirmed", $"ยืนยันหนังสือรับรอง 50 ทวิ ของ {p.PaymentNumber} เลขที่ {certificateNumber!.Trim()} ({p.WithholdingTaxAmount:N2} บาท) — เข้าทะเบียนภาษีถูกหัก (เครดิตได้)", actor);
        Audit(p, actor, "PlatformWhtCertificateConfirmed", new { certificateNumber = certificateNumber!.Trim(), certificateDate, creditId });
        await _db.SaveChangesAsync(ct);
        await NotifyOwnersAsync(p, "ยืนยันหนังสือรับรอง 50 ทวิ แล้ว", $"NextAcc ได้รับและยืนยันหนังสือรับรอง 50 ทวิ ของรายการ {p.PaymentNumber} ({p.WithholdingTaxAmount:N2} บาท) แล้ว ขอบคุณครับ");
        return (await ListAsync(PlatformWhtCertificateStatus.DefaultOverdueDays, true, ct)).FirstOrDefault(r => r.PaymentId == paymentId);
    }

    /// <summary>ตีกลับพร้อมเหตุผล — ไฟล์ที่ส่งถูกปลดจากรายการ (ประวัติจดชื่อไฟล์เดิม) · ผู้เช่าส่งใหม่ได้ · ไม่แตะทะเบียน (ยัง Pending = เครดิตไม่ได้)</summary>
    public async Task RejectAsync(Guid paymentId, string? reason, string actor, CancellationToken ct = default)
    {
        var p = await LoadAsync(paymentId, ct);
        var state = StateOf(p, await CreditStatusAsync(p.WhtCreditId, ct));
        if (PlatformWhtCertificateStatus.RejectRefusal(state, reason) is string why)
            throw new BusinessRuleException(why, PlatformWhtCertificateStatus.RuleCode, 409);
        var r = reason!.Trim();
        if (r.Length > 500) r = r[..500];
        var oldFile = p.WhtCertificateFileName;
        p.WhtCertificateFileName = null;
        p.WhtCertificateRejectedReason = r;
        p.WhtCertificateRejectedAt = DateTime.UtcNow;
        PlatformPaymentAttention.Flag(_db, p, p.Subscription.CompanyId, $"ตีกลับ 50 ทวิ: {r} — รอผู้เช่าส่งใหม่", PlatformPaymentAttention.RuleWhtCertPending, actor);
        History(p, "WhtCertificateRejected", $"ตีกลับหนังสือรับรอง 50 ทวิ ของ {p.PaymentNumber}: {r} (ไฟล์เดิม {oldFile})", actor);
        Audit(p, actor, "PlatformWhtCertificateRejected", new { reason = r, oldFile });
        await _db.SaveChangesAsync(ct);
        await NotifyOwnersAsync(p, "หนังสือรับรอง 50 ทวิ ถูกตีกลับ",
            $"หนังสือรับรอง 50 ทวิ ของรายการ {p.PaymentNumber} ถูกตีกลับ: {r} — กรุณาส่งใหม่ที่ศูนย์บิล (ประวัติการชำระ)");
    }

    /// <summary>ทวงผู้เช่า (กระดิ่ง/อีเมลตามค่าตั้งผู้ใช้ผ่านเส้นแจ้งเตือนเดิม) — วันละครั้งต่อรายการ</summary>
    public async Task RemindAsync(Guid paymentId, string actor, CancellationToken ct = default)
    {
        var p = await LoadAsync(paymentId, ct);
        var state = StateOf(p, await CreditStatusAsync(p.WhtCreditId, ct));
        if (PlatformWhtCertificateStatus.RemindRefusal(state, p.WhtCertificateRemindedAt, DateTime.UtcNow) is string why)
            throw new BusinessRuleException(why, PlatformWhtCertificateStatus.RuleCode, 409);
        // ฝ่ายค้าน 234 N5: จองการทวงแบบมีเงื่อนไขในฐาน (สองแอดมินกดพร้อมกัน ⇒ ได้คนเดียว) แล้วค่อยแจ้ง
        var now = DateTime.UtcNow;
        var since = now.AddHours(-PlatformWhtCertificateStatus.MinRemindIntervalHours);
        var claimed = _db.Database.IsRelational()
            ? await _db.SubscriptionPayments.Where(x => x.Id == p.Id && (x.WhtCertificateRemindedAt == null || x.WhtCertificateRemindedAt < since))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.WhtCertificateRemindedAt, (DateTime?)now), ct)
            : 1;
        if (claimed == 0)
            throw new BusinessRuleException($"ทวงไปแล้วเมื่อไม่ถึง {PlatformWhtCertificateStatus.MinRemindIntervalHours} ชั่วโมงก่อน (อีกคนเพิ่งกด)", PlatformWhtCertificateStatus.RuleCode, 409);
        p.WhtCertificateRemindedAt = now;
        _db.Entry(p).Property(x => x.WhtCertificateRemindedAt).IsModified = false;   // ประทับในฐานแล้ว (ไม่เขียนซ้ำ)
        History(p, "WhtCertificateReminded", $"ทวงหนังสือรับรอง 50 ทวิ ของ {p.PaymentNumber} ({p.WithholdingTaxAmount:N2} บาท)", actor);
        await _db.SaveChangesAsync(ct);
        await NotifyOwnersAsync(p, "กรุณาส่งหนังสือรับรอง 50 ทวิ",
            $"บริษัทของท่านหักภาษี ณ ที่จ่าย {p.WithholdingTaxAmount:N2} บาท จากค่าบริการ NextAcc รายการ {p.PaymentNumber} — กรุณาส่งหนังสือรับรอง 50 ทวิ ที่ศูนย์บิล (ประวัติการชำระ)");
    }

    /// <summary>234-W4: ผูกรายการเก่าที่อนุมัติแล้วและมีภาษีหัก กับทะเบียน (ครั้งเดียว · รันซ้ำได้ · ล็อกข้ามเครื่อง · audit) — ไม่ประทับ Received เอง ·
    /// คืน (ผูกได้, ยังไม่มีแถว) · null = อีกเครื่องกำลังรัน</summary>
    public async Task<(int Linked, int Missing)?> BackfillAsync(string actor, CancellationToken ct = default)
    {
        (int, int)? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.PlatformWhtCreditBackfill, "all", async () =>
        {
            var ids = await _db.SubscriptionPayments.AsNoTracking()
                .Where(p => p.WithholdingTaxAmount > 0m && p.Status == SubscriptionPaymentStatus.Approved && p.WhtCreditId == null)
                .Select(p => p.Id).ToListAsync(ct);
            int linked = 0, missing = 0;
            foreach (var id in ids)
            {
                var p = await _db.SubscriptionPayments.FirstAsync(x => x.Id == id, ct);
                if (await LinkCoreAsync(_db, p, ct) != null) linked++; else missing++;
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct),
                Action = AuditAction.Update, EntityType = nameof(SubscriptionPayment), EntityId = "backfill",
                NewValues = JsonSerializer.Serialize(new { action = "PlatformWhtCreditBackfill", ruleCode = PlatformWhtCertificateStatus.RuleCode, scanned = ids.Count, linked, missing, by = actor }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            result = (linked, missing);
        }, _logger, ct: ct);
        return ran ? result : null;
    }

    private void History(SubscriptionPayment p, string action, string notes, string actor)
        => _db.SubscriptionHistories.Add(new SubscriptionHistory { SubscriptionId = p.SubscriptionId, Action = action, Notes = notes, PerformedBy = actor });

    private void Audit(SubscriptionPayment p, string actor, string action, object values)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = p.Subscription.CompanyId, Action = AuditAction.Update, EntityType = nameof(SubscriptionPayment), EntityId = p.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { action, ruleCode = PlatformWhtCertificateStatus.RuleCode, legalReference = "ม.50 ทวิ · ท.ป.4/2528", by = actor, values }),
            Timestamp = DateTime.UtcNow,
        });

    /// <summary>แจ้งเจ้าของบริษัทผู้เช่า (กระดิ่ง · เส้นแจ้งเตือนเดิม) — ล้มแล้ว LogError (การยืนยัน/ตีกลับบันทึกแล้ว · ผู้เช่าเห็นสถานะในศูนย์บิลอยู่ดี)</summary>
    private async Task NotifyOwnersAsync(SubscriptionPayment p, string title, string message)
    {
        if (_notifications == null) return;
        try
        {
            var owners = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == p.Subscription.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync();
            foreach (var u in owners)
                await _notifications.SendAsync(u, p.Subscription.CompanyId, NotificationType.SubscriptionPaymentApproved, title, message,
                    "/pages/billing.html#tab=docs", nameof(SubscriptionPayment), p.Id);
        }
        catch (Exception ex) { _logger.LogError(ex, "แจ้งผู้เช่าเรื่อง 50 ทวิ ของ {Payment} ไม่สำเร็จ — สถานะในศูนย์บิลอัปเดตแล้ว", p.PaymentNumber); }
    }
}
