using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>แถวการแจ้งของหน้า partners.html — ยอดที่จะบันทึกคำนวณสดด้วย <see cref="PartnerPaymentNoticeRule.For"/> (server computes · page displays)</summary>
public sealed record PartnerPaymentNoticeDto(Guid Id, string SellerName, string SourceDocumentNumber, Guid TargetDocumentId, string? TargetDocumentNumber,
    decimal CashAmount, decimal SellerRecordedWht, DateTime PaidAt, string Status, string StatusLabel,
    decimal? PlannedAmount, decimal? PlannedWht, string? Problem, string? Note,
    // ฝ่ายค้าน M3/M4/M5: คำเตือนก่อนบันทึก · ช่องทางที่ผู้ขายรับ · บัญชีเงิน/วิธีจ่ายที่เลือกได้ (ค่าตั้งต้นจากเซิร์ฟเวอร์)
    string? PlannedWarning = null, string? SourceChannelLabel = null, Guid? DefaultBankAccountId = null, string? DefaultMethod = null,
    IReadOnlyList<PartnerPaymentOptionDto>? Accounts = null, IReadOnlyList<PartnerPaymentOptionDto>? Methods = null,
    // รอบ 264 P3 (C-01 · คำตัดสิน 264-3-1): ที่มาของบัญชีที่ preselect (ชื่อ Origin + ป้าย ⚙️) · คำแนะนำที่ไม่ preselect · เหตุที่ระบบไม่บันทึกเอง · ลิงก์หน้าความยินยอม
    string? SourceOrigin = null, string? SourceLabel = null, string? HintLabel = null, string? AutoState = null, string? ConsentPageUrl = null,
    bool OfferDirectorAdvance = false, bool SellerVoided = false);

/// <summary>ตัวเลือกในกล่องยืนยัน (ค่า + ป้าย) — <see cref="PartnerPaymentNoticeService.CashChoice"/> = เงินสด (ต้องเลือกเอง · ไม่ใช่ค่าตั้งต้น)</summary>
public sealed record PartnerPaymentOptionDto(string Value, string Label);

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — ผู้ขายคู่ค้ารับเงินของใบแจ้งหนี้ ⇒ <b>แจ้ง</b>บริษัทผู้ซื้อ (กระดิ่ง + ช่องทางตามค่าตั้งของเขา เช่น กลุ่ม LINE) ·
/// ผู้ซื้อกด "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" ⇒ บันทึกจ่ายใบซื้อของเขาเองผ่านเส้นเดิม <c>IDocumentService.CreatePaymentAsync</c> (ยอด/ภาษีหักตามกติกาของเขา) ·
/// <b>ไม่บันทึกบัญชีแทนผู้ซื้อ</b> · ข้ามบริษัทได้เฉพาะคู่ค้า Accepted ที่มีลิงก์เอกสาร InvoiceFlow + ใบซื้อปลายทางอยู่ในบริษัทผู้ซื้อ · audit ทั้งสองฝั่ง (AddChainedAuditLog)
/// <para>รอบ 264 P3: การแจ้งเกิดจาก<b>ทุกทางเข้า</b>รับชำระของผู้ขาย (ขั้นหลัง commit ของ <c>DocumentService.CreatePaymentAsync</c> → <see cref="NotifyForSellerPaymentAsync"/> · C-04) ·
/// ผู้ขายยกเลิกการรับชำระ ⇒ <see cref="WithdrawForSellerPaymentAsync"/> (C-03) · กุญแจกันซ้ำ = การรับชำระของผู้ขาย (264-3-6) ·
/// ระบบของผู้ซื้อบันทึกเองได้เฉพาะในท่อ <see cref="PartnerPayablePipeline"/> ภายใต้ความยินยอมของผู้ซื้อ · บัญชีที่จ่ายไม่มีค่าตั้งต้นที่แต่งขึ้น (C-01)</para>
/// </summary>
public class PartnerPaymentNoticeService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<PartnerPaymentNoticeService> _logger;
    private readonly PartnerConsentService? _consent;
    private readonly PartnerPayablePipeline.IBankLineEvidence? _bankLine;

    /// <summary>ค่าในกล่องเลือกบัญชีที่แปลว่า "จ่ายด้วยเงินสด" — ต้องเลือกเอง (C-01: เงินสด 111 ไม่เคยเป็นค่าตั้งต้น)</summary>
    public const string CashChoice = "cash";

    public PartnerPaymentNoticeService(AccountingDbContext db, IDocumentService docs, ILogger<PartnerPaymentNoticeService> logger, INotificationEngine? notify = null,
        PartnerConsentService? consent = null, PartnerPayablePipeline.IBankLineEvidence? bankLine = null)
    { _db = db; _docs = docs; _logger = logger; _notify = notify; _consent = consent; _bankLine = bankLine; }

    /// <summary>ข้อเท็จจริงของการรับชำระฝั่งผู้ขายที่ทำให้เกิดการแจ้ง</summary>
    /// <param name="SellerPaymentId">การรับชำระของผู้ขาย (กุญแจกันซ้ำ · ตรวจสดตอนบันทึกจ่าย) · null = เส้นเก่าที่ไม่รู้แถว</param>
    /// <param name="Cash">เงินที่ผู้ขายได้รับจริงจากผู้ซื้อ (ยอดรับ + ค่าธรรมเนียมที่ผู้ขายรับภาระ = เงินที่ออกจากผู้ซื้อ)</param>
    /// <param name="SellerAllocationId">รอบ 268 (268-1-6): แถวจัดสรรของการชำระหลายใบ (กุญแจกันซ้ำต่อใบ) · null = ชำระใบเดียว</param>
    public sealed record SellerReceipt(Guid SellerCompanyId, Guid? SellerPaymentId, Guid? PaymentIntentId, Guid SourceDocumentId, decimal Cash, decimal SellerWht,
        DateTime PaidAtUtc, string Channel, Guid? SellerAllocationId = null);

    /// <summary>
    /// เส้นเดิม (รอบ 258-3 — ผู้เรียกที่ยังรู้แค่ intent) · คืนจำนวนแถวแจ้งที่สร้าง · ไม่โยน · หาการรับชำระของผู้ขายจากเลขอ้างอิงเพื่อเติมกุญแจใหม่
    /// </summary>
    public async Task<int> NotifySellerReceivedAsync(Guid sellerCompanyId, Guid paymentIntentId, Guid sourceDocumentId, decimal cash, string sellerPaymentReference,
        DateTime paidAtUtc, PaymentMethodKind methodKind, CancellationToken ct = default)
    {
        var sp = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == sellerCompanyId && !p.IsDeleted && p.Reference == sellerPaymentReference)
            .Select(p => new { p.Id, p.WithholdingTaxAmount }).FirstOrDefaultAsync(ct);
        return await NotifyCoreAsync(_db, _notify, _logger,
            new SellerReceipt(sellerCompanyId, sp?.Id, paymentIntentId, sourceDocumentId, cash, sp?.WithholdingTaxAmount ?? 0m, paidAtUtc, methodKind.ToString()), ct);
    }

    /// <summary>
    /// รอบ 264 P3 (C-04 · คำตัดสิน 264-3-6) — <b>ผู้เรียกเดียว</b> = ขั้นหลัง commit ของการรับชำระฝั่งผู้ขาย (<c>DocumentService.NotifyPartnerOfSellerPaymentAsync</c>) ·
    /// ครอบทุกทางเข้า (มือ · gateway · สลิป · รายการเดินบัญชี · นำเข้า · integration) · ไม่โยน (เงินบันทึกแล้ว) ·
    /// รอบ 268 (268-1-6 · D8): การชำระหลายใบ (<c>CreateMultiDocPaymentAsync</c>) ⇒ หนึ่งการแจ้งต่อแถวจัดสรร (เงินของใบนั้น = <c>AllocatedAmount</c> · ภาษีหักของแถว)
    /// </summary>
    public static async Task<int> NotifyForSellerPaymentAsync(AccountingDbContext db, INotificationEngine? notify, ILogger logger, Guid sellerCompanyId, Guid sellerPaymentId,
        PaymentEntrySource source, CancellationToken ct = default)
    {
        try
        {
            var pay = await db.Payments.AsNoTracking()
                .Where(p => p.Id == sellerPaymentId && p.CompanyId == sellerCompanyId && !p.IsDeleted)
                .Select(p => new { p.Id, p.DocumentId, p.Amount, p.FeeAmount, p.WithholdingTaxAmount, p.PaymentDate, p.Reference, p.PaymentIntentId })
                .FirstOrDefaultAsync(ct);
            if (pay == null) return 0;
            // รอบ 268 (268-1-6): แถวจัดสรรพร้อมรับเงิน (ไม่ใช่จัดสรรทีหลังจากเงินรอจัดสรร) ⇒ แจ้งต่อใบ · ไม่มีแถว ⇒ ใบเดียวตามเดิม
            // ฝ่ายค้าน L1 (268-1-14): รวมการจัดสรรทีหลังจากเงินรอจัดสรร (วันที่ = วันจัดสรร) · ค่าธรรมเนียมที่ผู้ขายรับภาระกระจายตามสัดส่วนแถวพร้อมรับเงิน (เงินที่ออกจากผู้ซื้อ = เส้นใบเดียว)
            var allocations = await db.PaymentAllocations.AsNoTracking()
                .Where(a => a.PaymentId == pay.Id && a.CompanyId == sellerCompanyId && !a.IsDeleted)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Select(a => new { a.Id, a.DocumentId, a.AllocatedAmount, a.WithholdingTaxAmount, a.IsLateAllocation, a.AppliedAt }).ToListAsync(ct);
            if (allocations.Count > 0)
            {
                var channel = PartnerPaymentNoticeRule.ChannelOf(null, source);
                var onReceipt = allocations.Where(a => !a.IsLateAllocation).Select(a => a.AllocatedAmount).ToList();
                var fees = PartnerPaymentNoticeRule.SplitFee(pay.FeeAmount, onReceipt);
                var made = 0;
                var i = 0;
                foreach (var a in allocations)
                {
                    var fee = a.IsLateAllocation ? 0m : fees[i++];
                    made += await NotifyCoreAsync(db, notify, logger, new SellerReceipt(sellerCompanyId, pay.Id, null, a.DocumentId,
                        a.AllocatedAmount + fee, a.WithholdingTaxAmount, a.IsLateAllocation ? a.AppliedAt ?? pay.PaymentDate : pay.PaymentDate, channel, a.Id), ct);
                }
                return made;
            }
            // เฉพาะใบที่ส่งเข้าระบบคู่ค้า (มีลิงก์ InvoiceFlow ที่ผูกใบซื้อปลายทางแล้ว) — ใบทั่วไปจบที่นี่ด้วย query เดียว
            var routed = await db.CrossTenantDocumentLinks.AsNoTracking()
                .AnyAsync(l => l.SourceCompanyId == sellerCompanyId && l.SourceDocumentId == pay.DocumentId
                    && l.LinkType == CrossTenantLinkType.InvoiceFlow && l.TargetDocumentId != null && !l.IsDeleted, ct);
            if (!routed) return 0;
            var intentId = pay.PaymentIntentId ?? PartnerPaymentNoticeRule.IntentFromSellerReference(pay.Reference);
            PaymentMethodKind? kind = null;
            if (intentId is Guid iid)
                kind = await db.PaymentIntents.AsNoTracking().Where(i => i.Id == iid && i.CompanyId == sellerCompanyId)
                    .Select(i => (PaymentMethodKind?)i.MethodKind).FirstOrDefaultAsync(ct);
            return await NotifyCoreAsync(db, notify, logger, new SellerReceipt(sellerCompanyId, pay.Id, kind != null ? intentId : null, pay.DocumentId,
                pay.Amount + pay.FeeAmount, pay.WithholdingTaxAmount, pay.PaymentDate, PartnerPaymentNoticeRule.ChannelOf(kind, source)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "สร้างการแจ้งคู่ค้าจากการรับชำระ {Payment} ของบริษัท {Company} ไม่สำเร็จ (ผู้ซื้อบันทึกจ่ายเองได้ตามเดิม)", sellerPaymentId, sellerCompanyId);
            return 0;
        }
    }

    /// <summary>ตัวสร้างแถวแจ้งตัวเดียว (ทุกผู้เรียกข้างบน) — ไม่โยน · ผู้ขายสร้างการแจ้งเท่านั้น (ไม่บันทึกบัญชีแทนผู้ซื้อ)</summary>
    internal static async Task<int> NotifyCoreAsync(AccountingDbContext db, INotificationEngine? notify, ILogger logger, SellerReceipt r, CancellationToken ct)
    {
        // ฝ่ายค้าน L1: scope ใช้ร่วมกับผู้เรียก ⇒ ห้าม ChangeTracker.Clear() — ถอดเฉพาะแถวที่เราเพิ่ม (ทั้งกิ่งชนซ้ำและกิ่งล้มทั่วไป)
        var mine = new List<object>();
        void DetachMine()
        {
            foreach (var e in mine)
            {
                var en = db.Entry(e);
                if (en.State != EntityState.Detached) en.State = EntityState.Detached;
            }
            mine.Clear();
        }
        var sellerCompanyId = r.SellerCompanyId;
        try
        {
            var links = await db.CrossTenantDocumentLinks.AsNoTracking()
                .Where(l => l.SourceCompanyId == sellerCompanyId && l.SourceDocumentId == r.SourceDocumentId
                    && l.LinkType == CrossTenantLinkType.InvoiceFlow && l.TargetDocumentId != null && !l.IsDeleted)
                .Select(l => new { l.Id, l.TradingPartnershipId, l.TargetCompanyId, TargetDocumentId = l.TargetDocumentId!.Value })
                .ToListAsync(ct);
            if (links.Count == 0) return 0;
            var docNo = await db.Documents.AsNoTracking().Where(d => d.Id == r.SourceDocumentId && d.CompanyId == sellerCompanyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct) ?? "";
            var sellerName = await db.Companies.AsNoTracking().Where(c => c.Id == sellerCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "ผู้ขาย";
            // รอบ 264 P2 (คำตัดสิน 264-4-7): ผู้โอนบนสลิปที่ผู้ขายได้รับ = บัญชีของผู้ซื้อเอง ⇒ ส่งเฉพาะ ธนาคาร + เลขปิดบางที่ธนาคารปิดมาแล้ว (ไม่มีชื่อ/รูป) ให้ชั้น SlipUnique ของผู้ซื้อ
            var payer = await SlipPayerForNoticeAsync(db, sellerCompanyId, r.PaymentIntentId, ct);
            var created = 0;
            foreach (var l in links)
            {
                // คู่ค้าต้องยัง Accepted · ใบปลายทางต้องเป็นของบริษัทผู้ซื้อ (ไม่เชื่อ id ในลิงก์อย่างเดียว)
                var accepted = await db.TradingPartnerships.AsNoTracking()
                    .AnyAsync(p => p.Id == l.TradingPartnershipId && p.Status == TradingPartnershipStatus.Accepted
                        && (p.CompanyAId == sellerCompanyId || p.CompanyBId == sellerCompanyId)
                        && (p.CompanyAId == l.TargetCompanyId || p.CompanyBId == l.TargetCompanyId), ct);
                var target = await db.Documents.AsNoTracking().IgnoreQueryFilters()
                    .Where(d => d.Id == l.TargetDocumentId && d.CompanyId == l.TargetCompanyId && !d.IsDeleted)
                    .Select(d => new { d.DocumentNumber, d.DocumentType }).FirstOrDefaultAsync(ct);
                if (!accepted || target == null || !DocumentPermissionHelper.IsPurchase(target.DocumentType)) continue;
                // กันซ้ำทั้งกุญแจใหม่ (การรับชำระของผู้ขาย) และกุญแจเดิม (intent) — webhook ซ้ำ/สองทางเข้ารายงานการชำระเดียวกัน
                // รอบ 268 (268-1-6): แถวจัดสรร ⇒ กันซ้ำด้วยแถวจัดสรรเท่านั้น (การชำระเดียวกันมีแจ้งของใบอื่นได้) · ใบเดียว ⇒ กุญแจเดิม
                if (await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
                        .AnyAsync(n => n.CompanyId == l.TargetCompanyId
                            && (r.SellerAllocationId != null
                                ? n.SellerAllocationId == r.SellerAllocationId
                                : (r.SellerPaymentId != null && n.SellerPaymentId == r.SellerPaymentId && n.SellerAllocationId == null)
                                  || (r.PaymentIntentId != null && n.PaymentIntentId == r.PaymentIntentId)), ct)) continue;
                var notice = new PartnerPaymentNotice
                {
                    CompanyId = l.TargetCompanyId, TradingPartnershipId = l.TradingPartnershipId, LinkId = l.Id,
                    SourceCompanyId = sellerCompanyId, SourceDocumentId = r.SourceDocumentId, SourceDocumentNumber = Trunc(docNo, 60),
                    TargetDocumentId = l.TargetDocumentId, PaymentIntentId = r.PaymentIntentId, SellerPaymentId = r.SellerPaymentId, SellerAllocationId = r.SellerAllocationId,
                    CashAmount = R(r.Cash), SellerRecordedWht = R(r.SellerWht), PaidAt = r.PaidAtUtc, SourceChannel = Trunc(r.Channel, 32),
                    Status = PartnerPaymentNoticeRule.StatusOpen, CreatedBy = "partner-payment-notice",
                    SlipPayerBankCode = payer?.BankCode, SlipPayerAccountMask = payer?.AccountMask,
                };
                db.PartnerPaymentNotices.Add(notice);
                mine.Add(notice);
                // audit ทั้งสองฝั่ง: ผู้ขาย (ส่งการแจ้ง — ไม่มี id ภายในของผู้ซื้อ · ฝ่ายค้าน L5) · ผู้ซื้อ (ได้รับการแจ้ง)
                mine.Add(Audit(db, sellerCompanyId, null, "PartnerPaymentNoticeSent", notice, sellerSide: true));
                mine.Add(Audit(db, l.TargetCompanyId, null, "PartnerPaymentNoticeReceived", notice));
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
                {
                    DetachMine();   // อีกเครื่องสร้างไปก่อน (webhook ซ้ำ) — ไม่แจ้งซ้ำ
                    continue;
                }
                DetachMine();   // บันทึกแล้ว — ไม่ค้างใน scope ของผู้เรียก
                created++;
                await NotifyBuyerAsync(notify, logger, l.TargetCompanyId, notice, sellerName, target.DocumentNumber, ct);
            }
            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachMine();
            logger.LogError(ex, "สร้างการแจ้ง \"ผู้ขายรับเงินแล้ว\" ถึงคู่ค้าไม่สำเร็จ — การรับชำระ {Payment} intent {Intent} เอกสาร {Doc} บริษัท {Company} (ผู้ซื้อบันทึกจ่ายเองได้ตามเดิม)",
                r.SellerPaymentId, r.PaymentIntentId, r.SourceDocumentId, sellerCompanyId);
            return 0;
        }
    }

    /// <summary>รอบ 264 P2: บล็อกผู้โอนจากผลอ่านสลิปบน intent ของผู้ขาย (CompanyId = ผู้ขาย) — เฉพาะชนิดบัญชีธนาคารที่มีธนาคาร + เลขปิดบาง · อื่น ๆ = null</summary>
    internal static async Task<OcrSlipText.SlipParty?> SlipPayerForNoticeAsync(AccountingDbContext db, Guid sellerCompanyId, Guid? intentId, CancellationToken ct)
    {
        if (intentId is not Guid iid) return null;
        var json = await db.PaymentIntents.AsNoTracking().Where(i => i.Id == iid && i.CompanyId == sellerCompanyId)
            .Select(i => i.SlipReadJson).FirstOrDefaultAsync(ct);
        return SlipPayerForNotice(OcrSlipText.FromStoredJson(json));
    }

    /// <summary>pure: ผู้โอนที่ส่งต่อได้ (บัญชีธนาคาร + รหัสธนาคาร + mask) · บัตร/พร้อมเพย์/ไม่รู้บทบาท ⇒ null</summary>
    internal static OcrSlipText.SlipParty? SlipPayerForNotice(OcrSlipText.Stored? stored)
        => stored?.Payer is { Kind: OcrSlipText.PartyKind.BankAccount, BankCode: not null, AccountMask: not null } p ? p : null;

    private static async Task NotifyBuyerAsync(INotificationEngine? notify, ILogger logger, Guid buyerCompanyId, PartnerPaymentNotice n, string sellerName, string? purchaseNumber,
        CancellationToken ct)
    {
        if (notify == null) return;
        try
        {
            await notify.DispatchAsync(buyerCompanyId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = $"💳 {sellerName} ได้รับเงินของ {n.SourceDocumentNumber} แล้ว — บันทึกจ่ายใบซื้อ",
                Message = $"ผู้ขายรับเงิน {n.CashAmount:N2} บาท ผ่าน{PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)} (ผู้ขายบันทึกภาษีหัก ณ ที่จ่าย {n.SellerRecordedWht:N2} บาท) · "
                    + $"ใบซื้อของเรา {purchaseNumber ?? "-"} — กด “บันทึกจ่ายตามที่ผู้ขายรับแล้ว” ที่หน้าคู่ค้า หรือเปิด “งานอัตโนมัติกับคู่ค้า” ให้ระบบของเราบันทึกเองเมื่อหลักฐานครบ "
                    + "(ภาษีหักตามกติกาของเรา · 50 ทวิ ออกตามค่าตั้งของเรา)",
                ActionUrl = "/pages/partners.html#partner-paid",
                EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องผู้ขายรับเงินแล้วไม่สำเร็จ (แถวแจ้ง {Id} อยู่ที่หน้าคู่ค้าแล้ว)", buyerCompanyId, n.Id);
        }
    }

    /// <summary>
    /// รอบ 264 P3 (C-03 · คำตัดสิน 264-3-7) — <b>ผู้เรียกเดียว</b> = ขั้นหลัง commit ของ <c>DocumentService.VoidPaymentAsync</c> ฝั่งผู้ขาย ·
    /// การแจ้งที่ยังรอบันทึก ⇒ <see cref="PartnerPaymentNoticeRule.StatusWithdrawn"/> (compare-and-set — ชนกับการกดบันทึกได้คนเดียว) ·
    /// กำลังบันทึก ⇒ ประทับเวลายกเลิก (เส้นบันทึกตรวจซ้ำแล้วติดป้ายเอง) · บันทึกแล้ว ⇒ ป้ายบนใบซื้อ + แจ้งผู้ซื้อ · <b>ไม่ยกเลิกการจ่ายของผู้ซื้อแทน</b> (E6) · ไม่โยน
    /// </summary>
    public static async Task<int> WithdrawForSellerPaymentAsync(AccountingDbContext db, INotificationEngine? notify, ILogger logger, Guid sellerCompanyId, Guid sellerPaymentId,
        DateTime nowUtc, CancellationToken ct = default)
    {
        try
        {
            var rows = await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
                .Where(n => n.SourceCompanyId == sellerCompanyId && n.SellerPaymentId == sellerPaymentId && !n.IsDeleted && n.SellerVoidedAt == null)
                .ToListAsync(ct);
            if (rows.Count == 0) return 0;
            // การรับชำระต้องถูกกลับ<b>จริง</b>ในฐาน (ธุรกรรมยกเลิกที่ rollback/วนใหม่ไม่ถอนผิด — ผู้เรียกเข้าคิวจากตัวกลับรายการร่วม)
            if (await db.Payments.AsNoTracking().IgnoreQueryFilters().AnyAsync(p => p.Id == sellerPaymentId && p.CompanyId == sellerCompanyId && !p.IsDeleted, ct))
                return 0;
            var sellerName = await db.Companies.AsNoTracking().Where(c => c.Id == sellerCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "ผู้ขาย";
            var touched = 0;
            foreach (var n in rows)
            {
                var open = await db.PartnerPaymentNotices.IgnoreQueryFilters()
                    .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusOpen && x.SellerVoidedAt == null)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusWithdrawn)
                        .SetProperty(x => x.SellerVoidedAt, (DateTime?)nowUtc)
                        .SetProperty(x => x.ResolvedAt, (DateTime?)nowUtc)
                        .SetProperty(x => x.ResolvedBy, "partner-seller-voided")
                        .SetProperty(x => x.AutoBlockedReason, PartnerAutoRecordPolicy.SellerVoidedReason)
                        .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
                string evt;
                if (open == 1)
                    evt = "PartnerPaymentNoticeWithdrawn";
                else
                {
                    // ไม่ใช่ Open แล้ว (กำลังบันทึก/บันทึกแล้ว/ปิด) ⇒ ประทับเวลายกเลิกอย่างเดียว · ที่บันทึกแล้วติดป้ายบนใบซื้อ
                    await db.PartnerPaymentNotices.IgnoreQueryFilters()
                        .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.SellerVoidedAt == null)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.SellerVoidedAt, (DateTime?)nowUtc).SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
                    var status = await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
                        .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId).Select(x => x.Status).FirstAsync(ct);
                    evt = "PartnerPaymentSellerVoided";
                    if (status == PartnerPaymentNoticeRule.StatusRecorded)
                        await FlagRecordedVoidedAsync(db, n, sellerName, nowUtc, ct);
                }
                var fresh = await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
                Audit(db, n.CompanyId, null, evt, fresh);
                Audit(db, sellerCompanyId, null, evt, fresh, sellerSide: true);
                await db.SaveChangesAsync(ct);
                touched++;
                await NotifyVoidedAsync(notify, logger, n.CompanyId, fresh, sellerName, ct);
            }
            return touched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ถอน/ติดป้ายการแจ้งคู่ค้าหลังยกเลิกการรับชำระ {Payment} ของบริษัท {Company} ไม่สำเร็จ — ท่อของผู้ซื้อยังตรวจสดก่อนบันทึกจ่าย",
                sellerPaymentId, sellerCompanyId);
            return 0;
        }
    }

    /// <summary>ป้ายบนใบซื้อของผู้ซื้อ (หมายเหตุภายใน · ตัวเดียว <see cref="PartnerPaymentNoticeRule.SellerVoidedNote"/>) — ไม่ซ้ำ · ไม่แตะการจ่าย</summary>
    /// <remarks>ฝ่ายค้าน L2: กุญแจกันซ้ำต่อการรับชำระของผู้ขาย (<see cref="PartnerPaymentNoticeRule.SellerVoidedKey"/>) · เขียนแถว audit ระดับเอกสารใน chain ของผู้ซื้อ
    /// (การแก้หมายเหตุภายในของใบซื้อจากเหตุการณ์ของอีกบริษัทต้องมีร่องรอย) · ผู้เรียกไม่ต้อง SaveChanges เอง</remarks>
    internal static async Task<int> FlagRecordedVoidedAsync(AccountingDbContext db, PartnerPaymentNotice n, string sellerName, DateTime nowUtc, CancellationToken ct)
    {
        var key = PartnerPaymentNoticeRule.SellerVoidedKey(n.SellerPaymentId, n.Id);
        var note = PartnerPaymentNoticeRule.SellerVoidedNote(sellerName, n.SourceDocumentNumber, n.CashAmount, nowUtc, key);
        var purchaseId = n.TargetDocumentId;
        var buyerCompanyId = n.CompanyId;
        var done = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.Id == purchaseId && d.CompanyId == buyerCompanyId && (d.InternalNotes == null || !d.InternalNotes.Contains(key)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.InternalNotes, d => d.InternalNotes == null || d.InternalNotes == "" ? note : d.InternalNotes + "\n" + note)
                .SetProperty(d => d.UpdatedAt, (DateTime?)nowUtc), ct);
        if (done == 0) return 0;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = buyerCompanyId, Action = AuditAction.Update, EntityType = "Document", EntityId = purchaseId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerPaymentSellerVoidedFlagged", noticeId = n.Id, sellerPaymentId = n.SellerPaymentId, recordedPaymentId = n.RecordedPaymentId,
                cash = n.CashAmount, field = "InternalNotes", ruleCode = PartnerPaymentNoticeRule.RuleCode,
            }),
            Timestamp = nowUtc,
        });
        await db.SaveChangesAsync(ct);
        return done;
    }

    /// <summary>ฝ่ายค้าน 264-P3 H1 — ใบซื้อมีการจ่ายที่ยังมีผลซึ่ง<b>ไม่ได้อธิบายด้วยการแจ้งของผู้ขายที่ยังมีผล</b>ไหม: การจ่ายของใบนี้ (ของบริษัทผู้เรียก · ยังไม่ถูกยกเลิก)
    /// ที่ไม่ใช่ <c>RecordedPaymentId</c> ของการแจ้งที่บันทึกแล้ว + ผู้ขายยังไม่ยกเลิก (ทั้งธง <c>SellerVoidedAt</c> และตรวจสดที่การรับชำระของผู้ขาย) ·
    /// จริง ⇒ การแจ้งใหม่อาจเป็นเงินก้อนเดียวกัน (ผู้ขายยกเลิกแล้วบันทึกรับใหม่ · หรือเราบันทึกจ่ายเองไปก่อน) ⇒ ท่อไม่บันทึกเอง · ปุ่มเดียวเตือนก่อนกด</summary>
    public static async Task<bool> UnexplainedPurchasePaymentAsync(AccountingDbContext db, Guid companyId, Guid purchaseId, CancellationToken ct = default)
    {
        var live = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == purchaseId && !p.IsDeleted).Select(p => p.Id).ToListAsync(ct);
        // การจ่ายก้อนเดียวหลายใบที่จัดสรรมาที่ใบนี้ก็นับ (ไม่มีการแจ้งใดอธิบายได้)
        if (await db.PaymentAllocations.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.DocumentId == purchaseId && !a.IsDeleted, ct)) return true;
        if (live.Count == 0) return false;
        // IgnoreQueryFilters ที่ราก (ครอบ subquery ของการรับชำระฝั่งผู้ขาย — อีกบริษัท) ⇒ กรอง IsDeleted เองทุกตาราง
        var explained = await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.TargetDocumentId == purchaseId && x.Status == PartnerPaymentNoticeRule.StatusRecorded
                && x.SellerVoidedAt == null && x.RecordedPaymentId != null && x.SellerPaymentId != null
                && db.Payments.Any(sp => sp.Id == x.SellerPaymentId && sp.CompanyId == x.SourceCompanyId && !sp.IsDeleted))
            .Select(x => x.RecordedPaymentId!.Value).ToListAsync(ct);
        return live.Any(id => !explained.Contains(id));
    }

    private static async Task NotifyVoidedAsync(INotificationEngine? notify, ILogger logger, Guid buyerCompanyId, PartnerPaymentNotice n, string sellerName, CancellationToken ct)
    {
        if (notify == null) return;
        try
        {
            var recorded = n.Status == PartnerPaymentNoticeRule.StatusRecorded;
            await notify.DispatchAsync(buyerCompanyId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = recorded
                    ? $"⚠️ {sellerName} ยกเลิกการรับเงินของ {n.SourceDocumentNumber} ที่เราบันทึกจ่ายแล้ว — ตรวจ/ยกเลิกการจ่าย"
                    : $"{sellerName} ยกเลิกการรับเงินของ {n.SourceDocumentNumber} — ถอนการแจ้งแล้ว",
                Message = recorded
                    ? $"ยอด {n.CashAmount:N2} บาท · ระบบไม่ยกเลิกการจ่ายของเราแทน — ตรวจรายการเดินบัญชี แล้วยกเลิกการจ่ายที่ใบซื้อ หรือขอเงินคืนจากผู้ขาย"
                    : $"ยอด {n.CashAmount:N2} บาท · การแจ้งนี้บันทึกจ่ายไม่ได้แล้ว (ถ้าเงินออกจากบัญชีเราแล้ว ให้ขอคืนจากผู้ขาย)",
                ActionUrl = recorded ? $"/pages/documents.html?id={n.TargetDocumentId}" : "/pages/partners.html#partner-paid",
                EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องผู้ขายยกเลิกการรับเงินไม่สำเร็จ (แถว {Id} มีป้ายแล้ว)", buyerCompanyId, n.Id);
        }
    }

    /// <summary>การรับชำระของผู้ขายยังมีผลไหม (ตรวจสด · ข้ามบริษัทอ่านอย่างเดียว) — null = แถวเก่าที่ไม่รู้การรับชำระ</summary>
    public static async Task<bool?> SellerPaymentLiveAsync(AccountingDbContext db, PartnerPaymentNotice n, CancellationToken ct = default)
    {
        if (n.SellerPaymentId is not Guid sp) return null;
        return await db.Payments.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(p => p.Id == sp && p.CompanyId == n.SourceCompanyId && !p.IsDeleted, ct);
    }

    /// <summary>การแจ้งของบริษัทนี้ (ผู้ซื้อ) · รอบันทึกก่อน · ยอดที่จะบันทึก + บัญชีที่ preselect คำนวณสด (ตัวตัดสินเดียวกับท่อ)</summary>
    public async Task<List<PartnerPaymentNoticeDto>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId)
            .OrderBy(n => n.Status == PartnerPaymentNoticeRule.StatusOpen ? 0 : 1).ThenByDescending(n => n.PaidAt)
            .Take(100).ToListAsync(ct);
        var sellerIds = rows.Select(r => r.SourceCompanyId).Distinct().ToList();
        var sellers = await _db.Companies.AsNoTracking().Where(c => sellerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var usable = await PartnerPayablePipeline.UsablePayAccountsAsync(_db, companyId, ct);
        var accounts = usable.Select(u => new PartnerPaymentOptionDto(u.Key.ToString(), u.Value)).OrderBy(o => o.Label).ToList();
        accounts.Add(new PartnerPaymentOptionDto(CashChoice, "เงินสด (เลือกเองเมื่อจ่ายด้วยเงินสดจริง)"));
        var methods = PartnerPaymentNoticeRule.Methods.Select(m => new PartnerPaymentOptionDto(m.Method.ToString(), m.Label)).ToList();
        var list = new List<PartnerPaymentNoticeDto>();
        foreach (var n in rows)
        {
            PartnerPaymentNoticeRule.Plan? plan = null;
            string? number = null;
            PartnerPaySourceAccount.Decision? source = null;
            string? autoState = null;
            if (n.Status == PartnerPaymentNoticeRule.StatusOpen)
            {
                var (p, doc) = await PlanForAsync(_db, companyId, n, ct);
                plan = p;
                number = doc?.DocumentNumber;
                PartnerConsent.UseCheck? consent = null;
                if (_consent != null && doc != null)
                    consent = await _consent.CheckAsync(companyId, n.TradingPartnershipId, PartnerConsent.Scope.AutoRecordPayment, PartnerPayablePipeline.CapOf(doc), ct);
                // รอบ 264 P4 (264-5-10): ชั้นรายการเดินบัญชีตัวเดียวกับท่อ (อ่านอย่างเดียว) ⇒ ปุ่มเดียว preselect บัญชีที่เงินออกจริง
                (Guid? BankAccountId, string? Label) line = _bankLine == null ? default : await _bankLine.FindAsync(companyId, n, ct);
                source = await PartnerPayablePipeline.SourceDecisionAsync(_db, companyId, n, doc, usable, consent, DateTime.UtcNow, ct, line.BankAccountId, line.Label);
                // ฝ่ายค้าน M3: ถ้อยคำตรงกับหลักฐานที่มีจริงวันนี้ (ค่าตั้งของเรา + คำบอกของผู้ขาย — ยังไม่ตรวจกับรายการเดินบัญชี)
                // 268-1-1: บัญชีตั้งต้นอย่างเดียวไม่บันทึกเอง ⇒ บอกเหตุ + ปุ่มเดียว (บัญชี preselect แล้ว) · หลักฐานชั้นที่บันทึกเองได้ ⇒ ป้ายเดิม
                autoState = consent is { Ok: true } && source is { AllowsAuto: false, Origin: PartnerPaySourceAccount.Origin.ConsentDefault }
                    ? "ระบบไม่บันทึกเอง — " + PartnerPaySourceAccount.ConsentDefaultNotAutoReason
                    : consent is { Ok: true } && source is { AllowsAuto: false, Origin: PartnerPaySourceAccount.Origin.SlipUnique }
                    ? "ระบบไม่บันทึกเอง — " + PartnerPaySourceAccount.SlipUniqueNotAutoReason
                    : consent is { Ok: true }
                    ? "⚙️ ระบบบันทึกเองเมื่อหลักฐานบัญชีที่จ่ายครบ เมื่อผู้ขายแจ้งรับเงิน"
                        + (n.AutoBlockedReason is string br ? " — ยังไม่บันทึก: " + br : " · รอรอบถัดไป")
                    : "ระบบไม่บันทึกเอง" + (consent != null ? " — " + consent.Reason : "") + " · กดบันทึกจ่ายเอง หรือเปิด “งานอัตโนมัติกับคู่ค้า”";
                // ฝ่ายค้าน H1: ใบซื้อมีการจ่ายที่อาจเป็นเงินก้อนเดียวกัน ⇒ เตือนในกล่องยืนยันของปุ่มเดียวด้วย (ตัวตรวจเดียวกับท่อ)
                if (plan is { Problem: null } && doc != null && await UnexplainedPurchasePaymentAsync(_db, companyId, doc.Id, ct))
                    plan = plan with { Warning = (plan.Warning == null ? "" : plan.Warning + " · ") + PartnerAutoRecordPolicy.OtherPaymentReason };
            }
            number ??= await _db.Documents.AsNoTracking().Where(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            list.Add(new PartnerPaymentNoticeDto(n.Id, sellers.TryGetValue(n.SourceCompanyId, out var nm) ? nm : "ผู้ขาย", n.SourceDocumentNumber,
                n.TargetDocumentId, number, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Status, PartnerPaymentNoticeRule.StatusLabel(n.Status),
                plan?.Amount, plan?.Wht, plan?.Problem, n.Note,
                PlannedWarning: plan?.Warning, SourceChannelLabel: PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel),
                // C-01: preselect เฉพาะบัญชีที่มีหลักฐาน (ตัวตัดสินเดียว) — ไม่รู้ ⇒ ว่าง (ผู้กดต้องเลือก) · ไม่มี "บัญชีแรก" · เงินสดไม่เคยเป็นค่าตั้งต้น
                DefaultBankAccountId: source?.BankAccountId, DefaultMethod: PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel).ToString(),
                Accounts: accounts, Methods: methods,
                SourceOrigin: source?.Origin.ToString(), SourceLabel: source == null ? null : (source.BankAccountId != null ? "⚙️ " + source.Label : source.Reason),
                HintLabel: source?.HintLabel, AutoState: autoState, ConsentPageUrl: PartnerConsent.PageUrl(n.TradingPartnershipId),
                OfferDirectorAdvance: source?.OfferDirectorAdvance ?? false, SellerVoided: n.SellerVoidedAt != null));
        }
        return list;
    }

    /// <summary>ยอด/ภาษีหักที่จะบันทึก (ตัวคิดเดียวของปุ่มเดียวและท่อ) — ใบซื้อของบริษัทนี้ · ภาษีหักที่เหลือจากตัวคิดเดียวกับตัวบันทึกจ่าย ·
    /// จ่ายบางส่วน: ภาษีหักที่ผู้ขายบันทึก + อัตราเดียวของใบ (264-3-5)</summary>
    public static async Task<(PartnerPaymentNoticeRule.Plan Plan, Document? Doc)> PlanForAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (doc == null) return (new PartnerPaymentNoticeRule.Plan(null, null, "ไม่พบใบซื้อปลายทางแล้ว (อาจถูกลบ) — ปิดการแจ้งได้"), null);
        var wht = await DocumentService.PaymentWhtFiguresAsync(db, companyId, doc, n.CashAmount, ct);
        var rates = doc.Lines.Where(l => !l.IsDeleted && l.Amount != 0m).Select(l => l.WithholdingTaxRate).Distinct().ToList();
        decimal? singleRate = rates.Count == 1 && rates[0] > 0m ? rates[0] : null;
        return (PartnerPaymentNoticeRule.For(doc.Status, doc.BalanceDue, wht.NetCap, n.CashAmount, doc.DocumentNumber,
            sellerRecordedWht: n.SellerRecordedWht, singleWhtRatePercent: singleRate, purchaseHasVat: doc.VatAmount > 0m), doc);
    }

    /// <summary>ใบซื้อปลายทางของการแจ้ง (ผูกบริษัทผู้เรียก) — ให้ controller ตรวจสิทธิ์ตามชนิดจริง + ความลับ · null = ไม่พบ</summary>
    public async Task<(Guid DocumentId, DocumentType Type)?> TargetAsync(Guid companyId, Guid noticeId, CancellationToken ct = default)
    {
        var docId = await _db.PartnerPaymentNotices.AsNoTracking().Where(x => x.Id == noticeId && x.CompanyId == companyId)
            .Select(x => (Guid?)x.TargetDocumentId).FirstOrDefaultAsync(ct);
        if (docId is not Guid id) return null;
        var type = await _db.Documents.AsNoTracking().Where(d => d.Id == id && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync(ct);
        return type is DocumentType t ? (id, t) : null;
    }

    /// <summary>ผู้ซื้อกดยืนยัน ⇒ บันทึกจ่ายใบซื้อของตัวเองผ่านเส้นเดิม (ผู้บันทึก = ผู้กด · บัญชีเงิน/วิธีจ่ายที่ผู้กดเลือก — ฝ่ายค้าน M4) ·
    /// รอบ 264 P3 (C-01): ต้องเลือกบัญชีที่จ่ายจริง หรือเลือก "เงินสด" เอง (<paramref name="payFromCash"/>) — ไม่ส่งอะไรมา = ปฏิเสธ (เดิมตกไปเงินสด/แหล่งเงินของใบเงียบ ๆ) ·
    /// ผู้ขายยกเลิกการรับชำระแล้ว ⇒ ปฏิเสธ (C-03 · ตรวจสด) ·
    /// จองแถวแจ้งแบบมีเงื่อนไขก่อน (สองคนกดพร้อมกันได้คนเดียว — L2) · ไม่ผ่านกติกา/เส้นจ่ายปฏิเสธ ⇒ <see cref="BusinessRuleException"/> 409 ข้อความไทย (ไม่บันทึกอะไร)</summary>
    public async Task<PartnerPaymentNoticeDto> RecordAsync(Guid companyId, Guid noticeId, Guid userId, Guid? bankAccountId, string? method, bool payFromCash = false,
        CancellationToken ct = default)
    {
        var n = await _db.PartnerPaymentNotices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบการแจ้งนี้");
        var payMethod = PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel);
        if (!string.IsNullOrWhiteSpace(method))
        {
            if (!Enum.TryParse<PaymentMethod>(method, false, out var pm) || !PartnerPaymentNoticeRule.Methods.Any(m => m.Method == pm))
                throw new BusinessRuleException("วิธีจ่ายไม่ถูกต้อง — เลือกจากรายการในกล่องยืนยัน", PartnerPaymentNoticeRule.RuleCode, 400);
            payMethod = pm;
        }
        if (bankAccountId == null && !payFromCash)
            throw new BusinessRuleException("เลือกบัญชีที่เงินออกจริง (หรือเลือก “เงินสด” ถ้าจ่ายด้วยเงินสด) — ระบบไม่เดาบัญชีให้", PartnerPaySourceAccount.RuleCode, 400);
        // ฝ่ายค้าน M4: บัญชีต้องเป็นของบริษัทผู้เรียก (ไม่เชื่อ id จากหน้าเว็บ)
        if (bankAccountId is Guid bid && !await _db.BankAccounts.AsNoTracking()
                .AnyAsync(b => b.Id == bid && b.CompanyId == companyId && !b.IsDeleted && b.IsActive, ct))
            throw new BusinessRuleException("ไม่พบบัญชีเงินนี้ในบริษัทของท่าน (หรือปิดใช้งานแล้ว) — เลือกบัญชีใหม่", PartnerPaymentNoticeRule.RuleCode, 400);
        // ฝ่ายค้าน M1: "เงินสด" = ผังเงินสดของบริษัทเป็นผังตรงของการชำระ + วิธีจ่ายเงินสด — ไม่สืบทอดบัญชีธนาคารที่ตั้งบนใบซื้อ (เดิมลงธนาคารของใบทั้งที่ผู้ใช้ประกาศเงินสด)
        Guid? cashAccountId = null;
        if (bankAccountId == null)
        {
            cashAccountId = await DocumentMoneyChannel.DefaultCashAccountIdAsync(_db, companyId, ct)
                ?? throw new BusinessRuleException(DocumentMoneyChannel.NoDefaultCashMessage("เลือกบัญชีธนาคารที่จ่ายจริงแทน"), DocumentMoneyChannel.RuleCode, 409);
            payMethod = PaymentMethod.Cash;
        }
        if (n.SellerVoidedAt != null || await SellerPaymentLiveAsync(_db, n, ct) == false)
            throw new BusinessRuleException(PartnerAutoRecordPolicy.SellerVoidedReason, PartnerPaymentNoticeRule.RuleCode, 409);
        var references = PartnerPaymentNoticeRule.PaymentReferencesToCheck(n.SellerPaymentId, n.PaymentIntentId, n.SellerAllocationId);
        var reference = PartnerPaymentNoticeRule.PaymentReferenceFor(n.SellerPaymentId, n.PaymentIntentId, n.SellerAllocationId)
            ?? throw new BusinessRuleException("การแจ้งนี้ไม่ผูกการรับชำระของผู้ขาย — บันทึกจ่ายเองที่หน้าใบซื้อ", PartnerPaymentNoticeRule.RuleCode, 409);

        var now = DateTime.UtcNow;
        var staleBefore = now - PartnerPaymentNoticeRule.RecordingStale;
        var claimed = await _db.PartnerPaymentNotices
            .Where(x => x.Id == noticeId && x.CompanyId == companyId && x.SellerVoidedAt == null
                && (x.Status == PartnerPaymentNoticeRule.StatusOpen || (x.Status == PartnerPaymentNoticeRule.StatusRecording && x.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusRecording).SetProperty(x => x.UpdatedAt, (DateTime?)now), ct);
        if (claimed != 1)
            throw new BusinessRuleException("การแจ้งนี้บันทึกจ่าย/ปิด/ถอนไปแล้ว หรือกำลังบันทึกโดยผู้อื่น — ไม่บันทึกซ้ำ (รีเฟรชหน้า)", PartnerPaymentNoticeRule.RuleCode, 409);

        Guid? paymentId;
        try
        {
            paymentId = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference != null && references.Contains(p.Reference)).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            // รอบ 264 P4 (264-5-12): เราแจ้งโอนจากระบบของเราแล้ว (PV ของแผนเดียวกัน) ⇒ ปิดด้วย PV นั้น — ไม่สร้าง PV ที่สอง
            paymentId ??= await PartnerPayablePipeline.PlanPaymentForNoticeAsync(_db, companyId, n, ct);
            if (paymentId == null)
            {
                var (plan, _) = await PlanForAsync(_db, companyId, n, ct);
                if (plan.Amount is not decimal amount || plan.Problem != null)
                    throw new BusinessRuleException(plan.Problem ?? "บันทึกจ่ายจากการแจ้งนี้ไม่ได้ — บันทึกเองที่หน้าใบซื้อ", PartnerPaymentNoticeRule.RuleCode, 409);
                try
                {
                    var pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                        DocumentId: n.TargetDocumentId, PaymentDate: n.PaidAt, Amount: amount, PaymentMethod: payMethod,
                        Reference: reference, BankAccount: null,
                        Notes: $"จ่ายผ่าน{PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)} (คู่ค้า NextAcc) — {n.SourceDocumentNumber}",
                        OverrideBankAccountId: bankAccountId, OverridePaymentAccountId: cashAccountId, WithholdingTaxAmount: plan.Wht),
                        userId.ToString(), PaymentEntrySource.Manual);
                    paymentId = pay.Id;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or DbUpdateException)
                {
                    // ฝ่ายค้าน L2: เส้นจ่ายเดิมปฏิเสธ (ยอดเกิน/งวดปิด/วันที่ ฯลฯ) ⇒ 409 ข้อความไทย ไม่ใช่ 500
                    // ฝ่ายค้าน L8: ข้อความของฐานข้อมูลไม่ถึงผู้ใช้ (ข้อความกติกาของเส้นจ่ายเป็นไทยถึงผู้ใช้ได้)
                    if (ex is DbUpdateException) _logger.LogError(ex, "บันทึกจ่ายจากการแจ้ง {Notice} ของบริษัท {Company} ล้มที่ฐานข้อมูล", noticeId, companyId);
                    throw new BusinessRuleException("บันทึกจ่ายไม่สำเร็จ: " + PartnerPayablePipeline.UserMessageOf(ex) + " — แก้แล้วกดใหม่ หรือบันทึกเองที่หน้าใบซื้อ",
                        PartnerPaymentNoticeRule.RuleCode, 409);
                }
            }
        }
        catch
        {
            // ปล่อยแถวกลับเป็นรอบันทึก (ไม่ค้าง "กำลังบันทึก")
            await _db.PartnerPaymentNotices.Where(x => x.Id == noticeId && x.CompanyId == companyId && x.Status == PartnerPaymentNoticeRule.StatusRecording)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusOpen).SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), CancellationToken.None);
            throw;
        }
        var fresh = await _db.PartnerPaymentNotices.FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        fresh.Status = PartnerPaymentNoticeRule.StatusRecorded;
        fresh.RecordedPaymentId = paymentId;
        fresh.ResolvedAt = DateTime.UtcNow;
        fresh.ResolvedBy = userId.ToString();
        fresh.UpdatedAt = DateTime.UtcNow;
        Audit(_db, companyId, userId, "PartnerPaymentNoticeRecorded", fresh);
        await _db.SaveChangesAsync(ct);
        await PartnerPayablePipeline.ScrubSlipPayerAsync(_db, fresh, DateTime.UtcNow, ct);   // L3 (264-5-13): การแจ้งจบแล้ว ⇒ ไม่เก็บเลขผู้โอนต่อ
        // ผู้ขายยกเลิกระหว่างที่เรากำลังบันทึก ⇒ ป้ายบนใบซื้อ (ไม่ยกเลิกการจ่ายแทน)
        if (fresh.SellerVoidedAt != null || await SellerPaymentLiveAsync(_db, fresh, ct) == false)
            await FlagRecordedVoidedAsync(_db, fresh, "ผู้ขาย", DateTime.UtcNow, ct);
        return (await ListAsync(companyId, ct)).First(x => x.Id == noticeId);
    }

    /// <summary>ปิดการแจ้ง (ไม่ใช่การจ่ายของเรา/บันทึกเองแล้ว) — ไม่แตะบัญชี</summary>
    public async Task DismissAsync(Guid companyId, Guid noticeId, Guid userId, string? reason, CancellationToken ct = default)
    {
        var n = await _db.PartnerPaymentNotices.FirstOrDefaultAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบการแจ้งนี้");
        if (n.Status != PartnerPaymentNoticeRule.StatusOpen)
            throw new BusinessRuleException($"การแจ้งนี้{PartnerPaymentNoticeRule.StatusLabel(n.Status)}", PartnerPaymentNoticeRule.RuleCode, 409);
        n.Status = PartnerPaymentNoticeRule.StatusDismissed;
        n.ResolvedAt = DateTime.UtcNow;
        n.ResolvedBy = userId.ToString();
        n.Note = string.IsNullOrWhiteSpace(reason) ? null : Trunc(reason.Trim(), 1000);
        n.UpdatedAt = DateTime.UtcNow;
        Audit(_db, companyId, userId, "PartnerPaymentNoticeDismissed", n);
        await _db.SaveChangesAsync(ct);
        await PartnerPayablePipeline.ScrubSlipPayerAsync(_db, n, DateTime.UtcNow, ct);   // L3 (264-5-13)
    }

    /// <summary>แถว audit (คืนตัวแถวให้ผู้เรียกถอดได้) · <paramref name="sellerSide"/> = แถวของผู้ขาย ⇒ ไม่มี id ภายในของผู้ซื้อ (บริษัท/ใบซื้อ/การจ่าย — ฝ่ายค้าน L5) ·
    /// <paramref name="extra"/> = ช่องเพิ่มของเหตุการณ์ท่ออัตโนมัติ (ความยินยอม · ที่มาของบัญชี · ผู้แพ้)</summary>
    internal static AuditLog Audit(AccountingDbContext db, Guid companyId, Guid? userId, string evt, PartnerPaymentNotice n, bool sellerSide = false, object? extra = null)
    {
        var row = new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(PartnerPaymentNotice),
            EntityId = sellerSide ? (n.SellerPaymentId ?? n.PaymentIntentId ?? Guid.Empty).ToString() : n.Id.ToString(),
            NewValues = sellerSide
                ? JsonSerializer.Serialize(new
                {
                    @event = evt, partnershipId = n.TradingPartnershipId, sourceDocumentId = n.SourceDocumentId, paymentIntentId = n.PaymentIntentId,
                    sellerPaymentId = n.SellerPaymentId, cash = n.CashAmount, sellerWht = n.SellerRecordedWht, ruleCode = PartnerPaymentNoticeRule.RuleCode,
                })
                : JsonSerializer.Serialize(new
                {
                    @event = evt, sellerCompanyId = n.SourceCompanyId, sourceDocumentId = n.SourceDocumentId,
                    targetDocumentId = n.TargetDocumentId, paymentIntentId = n.PaymentIntentId, sellerPaymentId = n.SellerPaymentId,
                    cash = n.CashAmount, sellerWht = n.SellerRecordedWht, channel = n.SourceChannel, status = n.Status, recordedPaymentId = n.RecordedPaymentId,
                    sellerVoidedAt = n.SellerVoidedAt, ruleCode = PartnerPaymentNoticeRule.RuleCode, auto = extra,
                }),
            Timestamp = DateTime.UtcNow,
        };
        db.AddChainedAuditLog(row);
        return row;
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}
