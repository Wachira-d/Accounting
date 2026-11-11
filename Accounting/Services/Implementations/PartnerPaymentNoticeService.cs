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
    IReadOnlyList<PartnerPaymentOptionDto>? Accounts = null, IReadOnlyList<PartnerPaymentOptionDto>? Methods = null);

/// <summary>ตัวเลือกในกล่องยืนยัน (ค่า + ป้าย) — Value ว่าง = เงินสด/ไม่ระบุบัญชีธนาคาร</summary>
public sealed record PartnerPaymentOptionDto(string Value, string Label);

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — ผู้ขายคู่ค้ารับเงินของใบแจ้งหนี้ผ่าน gateway/สลิปของเรา ⇒ <b>แจ้ง</b>บริษัทผู้ซื้อ (กระดิ่ง + ช่องทางตามค่าตั้งของเขา เช่น กลุ่ม LINE) ·
/// ผู้ซื้อกด "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" ⇒ บันทึกจ่ายใบซื้อของเขาเองผ่านเส้นเดิม <c>IDocumentService.CreatePaymentAsync</c> (ยอด/ภาษีหักตามกติกาของเขา) ·
/// <b>ไม่บันทึกบัญชีแทนผู้ซื้อ</b> · ข้ามบริษัทได้เฉพาะคู่ค้า Accepted ที่มีลิงก์เอกสาร InvoiceFlow + ใบซื้อปลายทางอยู่ในบริษัทผู้ซื้อ · audit ทั้งสองฝั่ง (AddChainedAuditLog)
/// </summary>
public class PartnerPaymentNoticeService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<PartnerPaymentNoticeService> _logger;

    public PartnerPaymentNoticeService(AccountingDbContext db, IDocumentService docs, ILogger<PartnerPaymentNoticeService> logger, INotificationEngine? notify = null)
    { _db = db; _docs = docs; _logger = logger; _notify = notify; }

    /// <summary>
    /// เรียกโดย <c>DocumentPaymentHandler</c> หลังบันทึกรับชำระของผู้ขายสำเร็จ — ไม่โยน (เงินบันทึกแล้ว · ล้ม = LogError + ไม่มีแถวแจ้ง: ผู้ซื้อยังบันทึกจ่ายเองได้ตามเดิม) ·
    /// คืนจำนวนแถวแจ้งที่สร้าง
    /// </summary>
    /// <param name="sellerPaymentReference">เลขอ้างอิงของการรับชำระฝั่งผู้ขาย (<c>GatewayPaymentRecord.Reference</c>) — อ่านภาษีหักที่ผู้ขายลงจริง (แสดงเทียบเท่านั้น)</param>
    public async Task<int> NotifySellerReceivedAsync(Guid sellerCompanyId, Guid paymentIntentId, Guid sourceDocumentId, decimal cash, string sellerPaymentReference,
        DateTime paidAtUtc, PaymentMethodKind methodKind, CancellationToken ct = default)
    {
        // ฝ่ายค้าน L1: scope ใช้ร่วมกับผู้เรียก (PaymentIntentService) ⇒ ห้าม ChangeTracker.Clear() — ถอดเฉพาะแถวที่เราเพิ่ม (ทั้งกิ่งชนซ้ำและกิ่งล้มทั่วไป)
        var mine = new List<object>();
        void DetachMine()
        {
            foreach (var e in mine)
            {
                var en = _db.Entry(e);
                if (en.State != EntityState.Detached) en.State = EntityState.Detached;
            }
            mine.Clear();
        }
        try
        {
            var sellerWht = await _db.Payments.AsNoTracking()
                .Where(sp => sp.CompanyId == sellerCompanyId && !sp.IsDeleted && sp.Reference == sellerPaymentReference)
                .Select(sp => sp.WithholdingTaxAmount).FirstOrDefaultAsync(ct);
            var links = await _db.CrossTenantDocumentLinks.AsNoTracking()
                .Where(l => l.SourceCompanyId == sellerCompanyId && l.SourceDocumentId == sourceDocumentId
                    && l.LinkType == CrossTenantLinkType.InvoiceFlow && l.TargetDocumentId != null && !l.IsDeleted)
                .Select(l => new { l.Id, l.TradingPartnershipId, l.TargetCompanyId, TargetDocumentId = l.TargetDocumentId!.Value })
                .ToListAsync(ct);
            if (links.Count == 0) return 0;
            var docNo = await _db.Documents.AsNoTracking().Where(d => d.Id == sourceDocumentId && d.CompanyId == sellerCompanyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct) ?? "";
            var sellerName = await _db.Companies.AsNoTracking().Where(c => c.Id == sellerCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "ผู้ขาย";
            var created = 0;
            foreach (var l in links)
            {
                // คู่ค้าต้องยัง Accepted · ใบปลายทางต้องเป็นของบริษัทผู้ซื้อ (ไม่เชื่อ id ในลิงก์อย่างเดียว)
                var accepted = await _db.TradingPartnerships.AsNoTracking()
                    .AnyAsync(p => p.Id == l.TradingPartnershipId && p.Status == TradingPartnershipStatus.Accepted
                        && (p.CompanyAId == sellerCompanyId || p.CompanyBId == sellerCompanyId)
                        && (p.CompanyAId == l.TargetCompanyId || p.CompanyBId == l.TargetCompanyId), ct);
                var target = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                    .Where(d => d.Id == l.TargetDocumentId && d.CompanyId == l.TargetCompanyId && !d.IsDeleted)
                    .Select(d => new { d.DocumentNumber, d.DocumentType }).FirstOrDefaultAsync(ct);
                if (!accepted || target == null || !DocumentPermissionHelper.IsPurchase(target.DocumentType)) continue;
                if (await _db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
                        .AnyAsync(n => n.CompanyId == l.TargetCompanyId && n.PaymentIntentId == paymentIntentId, ct)) continue;
                var notice = new PartnerPaymentNotice
                {
                    CompanyId = l.TargetCompanyId, TradingPartnershipId = l.TradingPartnershipId, LinkId = l.Id,
                    SourceCompanyId = sellerCompanyId, SourceDocumentId = sourceDocumentId, SourceDocumentNumber = Trunc(docNo, 60),
                    TargetDocumentId = l.TargetDocumentId, PaymentIntentId = paymentIntentId,
                    CashAmount = R(cash), SellerRecordedWht = R(sellerWht), PaidAt = paidAtUtc, SourceChannel = methodKind.ToString(),
                    Status = PartnerPaymentNoticeRule.StatusOpen, CreatedBy = "partner-payment-notice",
                };
                _db.PartnerPaymentNotices.Add(notice);
                mine.Add(notice);
                // audit ทั้งสองฝั่ง: ผู้ขาย (ส่งการแจ้ง — ไม่มี id ภายในของผู้ซื้อ · ฝ่ายค้าน L5) · ผู้ซื้อ (ได้รับการแจ้ง)
                mine.Add(Audit(sellerCompanyId, null, "PartnerPaymentNoticeSent", notice, sellerSide: true));
                mine.Add(Audit(l.TargetCompanyId, null, "PartnerPaymentNoticeReceived", notice));
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
                {
                    DetachMine();   // อีกเครื่องสร้างไปก่อน (webhook ซ้ำ) — ไม่แจ้งซ้ำ
                    continue;
                }
                DetachMine();   // บันทึกแล้ว — ไม่ค้างใน scope ของผู้เรียก
                created++;
                await NotifyBuyerAsync(l.TargetCompanyId, notice, sellerName, target.DocumentNumber, ct);
            }
            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachMine();
            _logger.LogError(ex, "สร้างการแจ้ง \"ผู้ขายรับเงินแล้ว\" ถึงคู่ค้าไม่สำเร็จ — intent {Intent} เอกสาร {Doc} บริษัท {Company} (ผู้ซื้อบันทึกจ่ายเองได้ตามเดิม)",
                paymentIntentId, sourceDocumentId, sellerCompanyId);
            return 0;
        }
    }

    private async Task NotifyBuyerAsync(Guid buyerCompanyId, PartnerPaymentNotice n, string sellerName, string? purchaseNumber, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(buyerCompanyId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = $"💳 {sellerName} ได้รับเงินของ {n.SourceDocumentNumber} ผ่านลิงก์แล้ว — บันทึกจ่ายใบซื้อ",
                Message = $"ผู้ขายรับเงิน {n.CashAmount:N2} บาท (ผู้ขายบันทึกภาษีหัก ณ ที่จ่าย {n.SellerRecordedWht:N2} บาท) · ใบซื้อของเรา {purchaseNumber ?? "-"} — "
                    + "กด “บันทึกจ่ายตามที่ผู้ขายรับแล้ว” ที่หน้าคู่ค้า (ระบบไม่บันทึกแทน · ภาษีหักตามกติกาของเรา · 50 ทวิ ออกตามค่าตั้งของเรา)",
                ActionUrl = "/pages/partners.html#partner-paid",
                EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องผู้ขายรับเงินแล้วไม่สำเร็จ (แถวแจ้ง {Id} อยู่ที่หน้าคู่ค้าแล้ว)", buyerCompanyId, n.Id);
        }
    }

    /// <summary>การแจ้งของบริษัทนี้ (ผู้ซื้อ) · รอบันทึกก่อน · ยอดที่จะบันทึกคำนวณสด</summary>
    public async Task<List<PartnerPaymentNoticeDto>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId)
            .OrderBy(n => n.Status == PartnerPaymentNoticeRule.StatusOpen ? 0 : 1).ThenByDescending(n => n.PaidAt)
            .Take(100).ToListAsync(ct);
        var sellerIds = rows.Select(r => r.SourceCompanyId).Distinct().ToList();
        var sellers = await _db.Companies.AsNoTracking().Where(c => sellerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var accounts = await AccountOptionsAsync(companyId, ct);
        var methods = PartnerPaymentNoticeRule.Methods.Select(m => new PartnerPaymentOptionDto(m.Method.ToString(), m.Label)).ToList();
        var list = new List<PartnerPaymentNoticeDto>();
        foreach (var n in rows)
        {
            PartnerPaymentNoticeRule.Plan? plan = null;
            string? number = null;
            Guid? docBank = null;
            if (n.Status == PartnerPaymentNoticeRule.StatusOpen) (plan, number, docBank) = await PlanAsync(companyId, n, ct);
            number ??= await _db.Documents.AsNoTracking().Where(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            // บัญชีตั้งต้น = บัญชีธนาคารของใบซื้อ (ถ้ายังใช้งาน) › บัญชีแรกที่ใช้งาน
            var defBank = docBank is Guid b && accounts.Any(a => a.Value == b.ToString()) ? b
                : accounts.Where(a => a.Value.Length > 0).Select(a => (Guid?)Guid.Parse(a.Value)).FirstOrDefault();
            list.Add(new PartnerPaymentNoticeDto(n.Id, sellers.TryGetValue(n.SourceCompanyId, out var nm) ? nm : "ผู้ขาย", n.SourceDocumentNumber,
                n.TargetDocumentId, number, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Status, PartnerPaymentNoticeRule.StatusLabel(n.Status),
                plan?.Amount, plan?.Wht, plan?.Problem, n.Note,
                PlannedWarning: plan?.Warning, SourceChannelLabel: PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel),
                DefaultBankAccountId: defBank, DefaultMethod: PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel).ToString(),
                Accounts: accounts, Methods: methods));
        }
        return list;
    }

    private async Task<(PartnerPaymentNoticeRule.Plan? Plan, string? Number, Guid? BankAccountId)> PlanAsync(Guid companyId, PartnerPaymentNotice n, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (doc == null) return (new PartnerPaymentNoticeRule.Plan(null, null, "ไม่พบใบซื้อปลายทางแล้ว (อาจถูกลบ) — ปิดการแจ้งได้"), null, null);
        var wht = await DocumentService.PaymentWhtFiguresAsync(_db, companyId, doc, n.CashAmount, ct);
        return (PartnerPaymentNoticeRule.For(doc.Status, doc.BalanceDue, wht.NetCap, n.CashAmount, doc.DocumentNumber), doc.DocumentNumber, doc.BankAccountId);
    }

    /// <summary>บัญชีเงินของบริษัทผู้ซื้อที่เลือกได้ (ใช้งานอยู่ · ผูก CompanyId) + "เงินสด/ไม่ระบุบัญชี"</summary>
    private async Task<List<PartnerPaymentOptionDto>> AccountOptionsAsync(Guid companyId, CancellationToken ct)
    {
        var rows = await _db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .OrderBy(b => b.BankName).ThenBy(b => b.AccountName)
            .Select(b => new { b.Id, b.BankName, b.AccountName, b.AccountNumber }).ToListAsync(ct);
        var list = rows.Select(b => new PartnerPaymentOptionDto(b.Id.ToString(),
            $"{b.BankName} · {b.AccountName} · ••{(b.AccountNumber.Length > 4 ? b.AccountNumber[^4..] : b.AccountNumber)}")).ToList();
        list.Add(new PartnerPaymentOptionDto("", "เงินสด / ไม่ระบุบัญชีธนาคาร (บัญชีตามใบซื้อ/ค่าตั้งต้น)"));
        return list;
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
    /// จองแถวแจ้งแบบมีเงื่อนไขก่อน (สองคนกดพร้อมกันได้คนเดียว — L2) · ไม่ผ่านกติกา/เส้นจ่ายปฏิเสธ ⇒ <see cref="BusinessRuleException"/> 409 ข้อความไทย (ไม่บันทึกอะไร)</summary>
    public async Task<PartnerPaymentNoticeDto> RecordAsync(Guid companyId, Guid noticeId, Guid userId, Guid? bankAccountId, string? method, CancellationToken ct = default)
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
        // ฝ่ายค้าน M4: บัญชีต้องเป็นของบริษัทผู้เรียก (ไม่เชื่อ id จากหน้าเว็บ)
        if (bankAccountId is Guid bid && !await _db.BankAccounts.AsNoTracking()
                .AnyAsync(b => b.Id == bid && b.CompanyId == companyId && !b.IsDeleted && b.IsActive, ct))
            throw new BusinessRuleException("ไม่พบบัญชีเงินนี้ในบริษัทของท่าน (หรือปิดใช้งานแล้ว) — เลือกบัญชีใหม่", PartnerPaymentNoticeRule.RuleCode, 400);

        var now = DateTime.UtcNow;
        var staleBefore = now - PartnerPaymentNoticeRule.RecordingStale;
        var claimed = await _db.PartnerPaymentNotices
            .Where(x => x.Id == noticeId && x.CompanyId == companyId
                && (x.Status == PartnerPaymentNoticeRule.StatusOpen || (x.Status == PartnerPaymentNoticeRule.StatusRecording && x.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusRecording).SetProperty(x => x.UpdatedAt, (DateTime?)now), ct);
        if (claimed != 1)
            throw new BusinessRuleException("การแจ้งนี้บันทึกจ่าย/ปิดไปแล้ว หรือกำลังบันทึกโดยผู้อื่น — ไม่บันทึกซ้ำ (รีเฟรชหน้า)", PartnerPaymentNoticeRule.RuleCode, 409);

        Guid? paymentId;
        try
        {
            var reference = PartnerPaymentNoticeRule.PaymentReference(n.PaymentIntentId);
            paymentId = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference == reference).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (paymentId == null)
            {
                var (plan, _, _) = await PlanAsync(companyId, n, ct);
                if (plan?.Amount is not decimal amount || plan.Problem != null)
                    throw new BusinessRuleException(plan?.Problem ?? "บันทึกจ่ายจากการแจ้งนี้ไม่ได้ — บันทึกเองที่หน้าใบซื้อ", PartnerPaymentNoticeRule.RuleCode, 409);
                try
                {
                    var pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                        DocumentId: n.TargetDocumentId, PaymentDate: n.PaidAt, Amount: amount, PaymentMethod: payMethod,
                        Reference: reference, BankAccount: null,
                        Notes: $"จ่ายผ่าน{PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)} (คู่ค้า NextAcc) — {n.SourceDocumentNumber}",
                        OverrideBankAccountId: bankAccountId, WithholdingTaxAmount: plan.Wht), userId.ToString(), PaymentEntrySource.Manual);
                    paymentId = pay.Id;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or DbUpdateException)
                {
                    // ฝ่ายค้าน L2: เส้นจ่ายเดิมปฏิเสธ (ยอดเกิน/งวดปิด/วันที่ ฯลฯ) ⇒ 409 ข้อความไทย ไม่ใช่ 500
                    throw new BusinessRuleException("บันทึกจ่ายไม่สำเร็จ: " + ex.Message + " — แก้แล้วกดใหม่ หรือบันทึกเองที่หน้าใบซื้อ", PartnerPaymentNoticeRule.RuleCode, 409);
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
        Audit(companyId, userId, "PartnerPaymentNoticeRecorded", fresh);
        await _db.SaveChangesAsync(ct);
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
        Audit(companyId, userId, "PartnerPaymentNoticeDismissed", n);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>แถว audit (คืนตัวแถวให้ผู้เรียกถอดได้) · <paramref name="sellerSide"/> = แถวของผู้ขาย ⇒ ไม่มี id ภายในของผู้ซื้อ (บริษัท/ใบซื้อ/การจ่าย — ฝ่ายค้าน L5)</summary>
    private AuditLog Audit(Guid companyId, Guid? userId, string evt, PartnerPaymentNotice n, bool sellerSide = false)
    {
        var row = new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = nameof(PartnerPaymentNotice),
            EntityId = sellerSide ? n.PaymentIntentId.ToString() : n.Id.ToString(),
            NewValues = sellerSide
                ? JsonSerializer.Serialize(new
                {
                    @event = evt, partnershipId = n.TradingPartnershipId, sourceDocumentId = n.SourceDocumentId, paymentIntentId = n.PaymentIntentId,
                    cash = n.CashAmount, sellerWht = n.SellerRecordedWht, ruleCode = PartnerPaymentNoticeRule.RuleCode,
                })
                : JsonSerializer.Serialize(new
                {
                    @event = evt, sellerCompanyId = n.SourceCompanyId, sourceDocumentId = n.SourceDocumentId,
                    targetDocumentId = n.TargetDocumentId, paymentIntentId = n.PaymentIntentId, cash = n.CashAmount, sellerWht = n.SellerRecordedWht,
                    channel = n.SourceChannel, status = n.Status, recordedPaymentId = n.RecordedPaymentId, ruleCode = PartnerPaymentNoticeRule.RuleCode,
                }),
            Timestamp = DateTime.UtcNow,
        };
        _db.AddChainedAuditLog(row);
        return row;
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}
