using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §5 · คำตัดสิน 264-3-1…264-3-22) — <b>ท่อฝั่งผู้ซื้อ (B)</b>: การแจ้ง "ผู้ขายคู่ค้ารับเงินแล้ว" ⇒ ระบบของ B บันทึกจ่ายใบซื้อเอง
/// ภายใต้ความยินยอม <c>AutoRecordPayment</c> ของ B · ไม่ผ่าน ⇒ กลับเป็นปุ่มเดียวเดิม + เหตุบนแถว + แถว Autopilot
/// <list type="bullet">
/// <item><b>หลักฐานที่มีจริงวันนี้ (ฝ่ายค้าน M3 — พูดตรง ๆ)</b>: ยอด = เงินที่ผู้ขายบันทึกรับ (คำบอกของผู้ขาย) · บัญชีที่จ่าย = บัญชีตั้งต้นที่ B ตั้งไว้ตอนยินยอม ·
/// <b>ยังไม่มีการตรวจกับรายการเดินบัญชีของ B</b> — ชั้นรายการเดินบัญชี/ผู้จ่ายเลือก/สลิปของ <see cref="PartnerPaySourceAccount"/> ยังไม่มีผู้ป้อน
/// (backlog: แพ็ก P2 สลิป · แพ็ก P4 รายการเดินบัญชี/จ่ายจากระบบของเรา — ป้อนที่ <see cref="SourceDecisionAsync"/>) · ป้าย/หมายเหตุการจ่ายบอกข้อนี้</item>
/// <item><b>ไม่ทำงานในคำขอของผู้ขาย</b> (เขียนสมุด B ใต้ scope ของคำขอ A = L1 รอบ 258) ⇒ ขั้น <c>PartnerPayable</c> ของ <c>AutopilotJob</c> (JobLock ต่อขั้น) ·
/// เฉพาะการแจ้งของ<b>คู่ค้าที่มีความยินยอมบันทึกจ่ายที่ใช้อยู่</b> (M7) · watermark ต่อแถว <c>AutoEvaluatedAt</c>: เหตุชั่วคราวประเมินซ้ำทุก <see cref="ReevaluateEvery"/> ·
/// เหตุถาวร (<see cref="PartnerAutoRecordPolicy.Codes.Slow"/>) วันละครั้ง</item>
/// <item><b>idempotent ต่อการรับชำระของผู้ขาย</b>: จองแถวแบบมีเงื่อนไข (Open → Recording) · เลขอ้างอิง canonical <c>PARTNER-PAID-P{paymentId}</c> ·
/// พบการจ่ายที่อ้างแล้ว ⇒ ปิดแถวเป็นบันทึกแล้ว · <b>ใบซื้อมีการจ่ายอื่นที่อธิบายไม่ได้</b> (บันทึกเอง · ผู้ขายยกเลิกแล้วบันทึกรับใหม่) ⇒ ไม่บันทึกเอง (ฝ่ายค้าน H1)</item>
/// <item><b>ความยินยอมตรวจเพื่อเขียน</b>: <see cref="PartnerConsentService.CheckForWriteAsync"/> ในธุรกรรมล็อก (FOR SHARE) ที่ถือไว้<b>ตลอดการบันทึกจ่าย</b> —
/// <c>CreatePaymentAsync</c> เปิดธุรกรรมของตัวเองเสมอ ⇒ ธุรกรรมล็อกอยู่บน connection แยก (scope ใหม่ · มี <c>idle_in_transaction_session_timeout</c> กันค้างเงียบ — L1) ·
/// ผู้บันทึก = GUID ผู้ให้ความยินยอม · ยอดเทียบเพดาน = ยอดรวมใบซื้อตามสกุลของใบ (<see cref="CapOf"/> — เพดานเป็น<b>ต่อใบ</b> ไม่มีเพดานสะสม: คำตัดสิน 264-3-18)</item>
/// <item><b>ไม่มีจุดเรียก AI ในเส้นเงิน</b> · จุดเรียก AI เดียวของไฟล์นี้ = ผังบัญชีของใบซื้อร่างจากคู่ค้า (<see cref="SuggestDraftGlAsync"/> · C-08) ผ่านตัวแนะนำผังเดิม</item>
/// </list>
/// </summary>
public class PartnerPayablePipeline
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PartnerPayablePipeline> _logger;
    private readonly Accounting.Services.Ai.IOcrAiAugmenter? _glAi;
    private readonly INotificationEngine? _notify;

    public const int BatchPerCompany = 50;
    public const int DraftBatchPerCompany = 20;
    public const int SweepBatch = 100;
    /// <summary>ประเมินแถวเดิมซ้ำเมื่อพ้นช่วงนี้ (เหตุชั่วคราว: ความยินยอม · ใบซื้อร่าง · แหล่งเงิน · เส้นจ่ายปฏิเสธ)</summary>
    public static readonly TimeSpan ReevaluateEvery = TimeSpan.FromMinutes(15);
    /// <summary>ฝ่ายค้าน M7: เหตุถาวร (<see cref="PartnerAutoRecordPolicy.Codes.Slow"/>) ประเมินซ้ำห่าง ๆ — ไม่เปิด scope/ธุรกรรมทุก 15 นาทีตลอดไป</summary>
    public static readonly TimeSpan ReevaluateSlowEvery = TimeSpan.FromHours(24);
    /// <summary>ป้ายบนใบซื้อร่างว่าเติมผังบัญชีจากตัวแนะนำแล้ว/เลิกถามแล้ว (ไม่ถามซ้ำ)</summary>
    public const string DraftGlMarker = "[PARTNER-DRAFT-GL]";
    /// <summary>ป้ายว่าถามแล้วยังไม่ได้ผังที่ใช้ได้ (ถามซ้ำได้อีกหลัง <see cref="DraftGlRetryEvery"/> · สูงสุด <see cref="DraftGlMaxTries"/> ครั้ง — L6)</summary>
    public const string DraftGlTryMarker = "[PARTNER-DRAFT-GL-TRY]";
    public const int DraftGlMaxTries = 3;
    public static readonly TimeSpan DraftGlRetryEvery = TimeSpan.FromHours(6);

    public PartnerPayablePipeline(AccountingDbContext db, IDocumentService docs, IServiceScopeFactory scopes, ILogger<PartnerPayablePipeline> logger,
        Accounting.Services.Ai.IOcrAiAugmenter? glAi = null, INotificationEngine? notify = null)
    { _db = db; _docs = docs; _scopes = scopes; _logger = logger; _glAi = glAi; _notify = notify; }

    /// <summary>หนึ่งรอบของงานเบื้องหลัง (ผู้เรียกเดียว = <c>AutopilotJob</c> ขั้น PartnerPayable) — คืนจำนวนงานที่ทำ (ถอน/ป้าย + บันทึกจ่าย + ใบร่างที่เติมผัง)</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var done = await SweepSellerVoidedAsync(nowUtc, ct);
        var name = Scope.AutoRecordPayment.ToString();
        // บริษัทที่มีความยินยอมบันทึกจ่ายที่ยังใช้อยู่ — ไม่มี = ไม่มีงาน (ปุ่มเดียวเดิมทำงานเหมือนเดิม)
        var payers = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.WithdrawnAt == null && c.Scope == name).Select(c => c.CompanyId).Distinct().ToListAsync(ct);
        foreach (var companyId in payers)
            done += await RunForCompanyAsync(companyId, nowUtc, ct);
        // C-08: ใบซื้อร่างจากคู่ค้าของบริษัทที่เปิดสร้างใบร่าง ⇒ ผังบัญชีจากตัวแนะนำเดิม (ไม่อนุมัติเอง)
        var drafters = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .Where(c => c.CreatePurchaseDraftsFromPartners).Select(c => c.CompanyId).ToListAsync(ct);
        foreach (var companyId in drafters)
            done += await SuggestDraftGlAsync(companyId, nowUtc, ct);
        return done;
    }

    /// <summary>
    /// ฝ่ายค้าน M2/L4 — ตาข่ายของขั้นถอนหลัง commit (ทุกบริษัท · ไม่ขึ้นกับความยินยอม): การแจ้งที่การรับชำระของผู้ขาย<b>ถูกกลับแล้วในฐาน</b>แต่ยังไม่ถูกถอน/ติดป้าย
    /// (ขั้นหลัง commit ล้ม · แถวก่อนรอบ 264) ⇒ เดินตัวถอนตัวเดียวกัน (<see cref="PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync"/> — audit สองฝั่ง + แจ้ง) ·
    /// แทน SQL migration ที่เปลี่ยนสถานะโดยไม่มี audit · แถวที่ปิด/ถอนแล้วไม่ถูกแตะ · งานระบบข้ามบริษัทโดยเจตนา (ตัวถอนผูกบริษัทผู้ขาย + บริษัทเจ้าของแถวทุก query)
    /// </summary>
    public async Task<int> SweepSellerVoidedAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var hits = await _db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
            .Where(n => !n.IsDeleted && n.SellerVoidedAt == null && n.SellerPaymentId != null
                && (n.Status == PartnerPaymentNoticeRule.StatusOpen || n.Status == PartnerPaymentNoticeRule.StatusRecording
                    || n.Status == PartnerPaymentNoticeRule.StatusRecorded)
                && _db.Payments.Any(p => p.Id == n.SellerPaymentId && p.CompanyId == n.SourceCompanyId && p.IsDeleted))   // IgnoreQueryFilters ที่รากครอบ subquery
            .Select(n => new { n.SourceCompanyId, SellerPaymentId = n.SellerPaymentId!.Value })
            .Distinct().Take(SweepBatch).ToListAsync(ct);
        var done = 0;
        foreach (var h in hits)
            done += await PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync(_db, _notify, _logger, h.SourceCompanyId, h.SellerPaymentId, nowUtc, ct);
        return done;
    }

    /// <summary>แถวที่ถึงรอบประเมินของบริษัทผู้ซื้อหนึ่งราย (tenant = <paramref name="companyId"/> ทุก query) · เฉพาะคู่ค้าที่มีแถวความยินยอมบันทึกจ่ายที่ยังไม่ถอน (M7)</summary>
    public async Task<int> RunForCompanyAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var name = Scope.AutoRecordPayment.ToString();
        var consented = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.WithdrawnAt == null && c.Scope == name)
            .Select(c => c.TradingPartnershipId).Distinct().ToListAsync(ct);
        if (consented.Count == 0) return 0;
        var due = nowUtc - ReevaluateEvery;
        var dueSlow = nowUtc - ReevaluateSlowEvery;
        var slow = PartnerAutoRecordPolicy.Codes.Slow.ToList();
        var stale = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        var ids = await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.SellerVoidedAt == null && consented.Contains(n.TradingPartnershipId)
                && (n.Status == PartnerPaymentNoticeRule.StatusOpen
                    || (n.Status == PartnerPaymentNoticeRule.StatusRecording && (n.UpdatedAt ?? n.CreatedAt) < stale))
                && (n.AutoEvaluatedAt == null
                    || n.AutoEvaluatedAt < dueSlow
                    || (n.AutoEvaluatedAt < due && (n.AutoBlockedCode == null || !slow.Contains(n.AutoBlockedCode)))))
            .OrderBy(n => n.AutoEvaluatedAt ?? DateTime.MinValue).ThenBy(n => n.PaidAt)
            .Select(n => n.Id).Take(BatchPerCompany).ToListAsync(ct);
        var recorded = 0;
        foreach (var id in ids)
            if (await ProcessAsync(companyId, id, nowUtc, ct)) recorded++;
        return recorded;
    }

    /// <summary>ยอดเทียบเพดานความยินยอม = ยอดรวมของใบซื้อ<b>ตามสกุลของใบ</b> (ฝ่ายค้าน L3: ใบสกุลอื่น ⇒ <c>AmountUnknown</c> ที่ตัวตรวจความยินยอม — ไม่ติดป้ายบาทให้ยอดสกุลอื่น)</summary>
    public static PartnerConsent.CapAmount CapOf(Document purchase)
        => new(purchase.TotalAmount, string.IsNullOrWhiteSpace(purchase.Currency) ? "THB" : purchase.Currency.Trim());

    /// <summary>ข้อความถึงผู้ใช้ของข้อผิดพลาดจากเส้นจ่าย (ฝ่ายค้าน L8): ข้อความกติกา (ไทย) ผ่านได้ · ข้อผิดพลาดของฐานข้อมูลไม่ถึงผู้ใช้</summary>
    public static string UserMessageOf(Exception ex)
        => ex is DbUpdateException ? "ฐานข้อมูลปฏิเสธรายการ (ผู้ดูแลระบบดูบันทึกระบบ)" : ex.Message;

    /// <summary>ประเมิน/บันทึกการแจ้งหนึ่งแถว — true = บันทึกจ่ายแล้ว (ใหม่หรือพบของเดิม) · ไม่โยน (ล้ม = แถวกลับเป็นรอบันทึก + เหตุ + LogError)</summary>
    public async Task<bool> ProcessAsync(Guid companyId, Guid noticeId, DateTime nowUtc, CancellationToken ct = default)
    {
        var staleBefore = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        // จองแบบมีเงื่อนไข — ชนกับปุ่มเดียว/การถอนของผู้ขายได้คนเดียว
        var claimed = await _db.PartnerPaymentNotices
            .Where(x => x.Id == noticeId && x.CompanyId == companyId && x.SellerVoidedAt == null
                && (x.Status == PartnerPaymentNoticeRule.StatusOpen
                    || (x.Status == PartnerPaymentNoticeRule.StatusRecording && x.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusRecording).SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (claimed != 1) return false;
        var n = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        try
        {
            // กันซ้ำก่อนทุกอย่าง: การจ่ายที่อ้างการแจ้งนี้มีอยู่แล้ว (รอบก่อนล้มหลังบันทึก) ⇒ ปิดแถว
            var refs = PartnerPaymentNoticeRule.PaymentReferencesToCheck(n.SellerPaymentId, n.PaymentIntentId);
            var existing = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference != null && refs.Contains(p.Reference))
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (existing is Guid already)
            {
                await MarkRecordedAsync(companyId, noticeId, already, null, null, null, nowUtc, ct);
                return true;
            }

            var (plan, doc) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, n, ct);
            var live = await PartnerPaymentNoticeService.SellerPaymentLiveAsync(_db, n, ct);
            var src = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.Id == n.SourceDocumentId && d.CompanyId == n.SourceCompanyId)
                .Select(d => new { d.Status, d.IsDeleted, d.ReceiptForm, d.VatAmount }).FirstOrDefaultAsync(ct);
            var usable = await UsablePayAccountsAsync(_db, companyId, ct);
            var otherPayment = doc != null && await PartnerPaymentNoticeService.UnexplainedPurchasePaymentAsync(_db, companyId, doc.Id, ct);

            // ── ธุรกรรมล็อกความยินยอม (connection แยก) — ถือไว้จนบันทึกจ่ายเสร็จ ──
            using var lockScope = _scopes.CreateScope();
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
            await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
            // ฝ่ายค้าน L1: ถ้าวันหน้าเส้นจ่ายไปรอแถวที่ธุรกรรมนี้ล็อกไว้ (PostgreSQL ไม่เห็นเป็น deadlock) ⇒ ฐานตัดธุรกรรมล็อกทิ้งเองแทนการค้างเงียบ
            await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
            var consent = doc == null
                ? null
                : await consentSvc.CheckForWriteAsync(companyId, n.TradingPartnershipId, Scope.AutoRecordPayment, CapOf(doc), ct);
            var source = await SourceDecisionAsync(_db, companyId, n, doc, usable, consent, nowUtc, ct);
            var verdict = PartnerAutoRecordPolicy.Decide(new PartnerAutoRecordPolicy.Facts(
                NoticeOpen: true,
                SellerPaymentKnown: live != null,
                SellerPaymentLive: live == true,
                SourceDocumentVoided: src == null || src.IsDeleted || src.Status == DocumentStatus.Voided,
                ConsentOk: consent?.Ok == true,
                ConsentReason: consent?.Reason ?? "ไม่พบใบซื้อปลายทาง",
                Plan: plan,
                Currency: doc?.Currency,
                SourceIsAbbreviated: src?.ReceiptForm == ReceiptForm.ReceiptTaxInvoiceAbbreviated,
                PayerBorneWht: (doc?.WhtPayerBorneAmount ?? 0m) > 0m,
                VatMismatch: doc != null && src != null && !PartnerInputVatEvidence.VatMatches(src.VatAmount, doc.VatAmount),
                Source: source,
                UnexplainedPurchasePayment: otherPayment,
                WhtDateInPastMonth: PartnerAutoRecordPolicy.WhtDateInPastMonthOf(n.PaidAt, plan.Wht, nowUtc)));
            if (!verdict.Auto || consent?.GrantorUserId is not Guid grantor || source.BankAccountId is not Guid bank || plan.Amount is not decimal amount)
            {
                await lockTx.RollbackAsync(ct);
                if (verdict.Kind == PartnerAutoRecordPolicy.Kind.Refuse && verdict.Code == PartnerAutoRecordPolicy.Codes.SellerVoided)
                    await WithdrawStaleAsync(n, nowUtc, ct);
                else
                {
                    // ใบซื้อปิดแล้ว/ยกเลิก/หาไม่พบ = เหตุถาวร (ประเมินซ้ำห่าง ๆ — M7)
                    var final = verdict.Code == PartnerAutoRecordPolicy.Codes.Plan
                        && (doc == null || doc.Status is DocumentStatus.Voided or DocumentStatus.Rejected || doc.BalanceDue <= PartnerPaymentNoticeRule.Tolerance);
                    await ReleaseAsync(n, verdict.Auto ? PartnerAutoRecordPolicy.Codes.Consent : final ? PartnerAutoRecordPolicy.Codes.PlanFinal : verdict.Code,
                        verdict.Auto ? "ไม่พบผู้ให้ความยินยอม — ระบบไม่บันทึกเอง" : verdict.Reason, nowUtc, ct);
                }
                return false;
            }

            var reference = PartnerPaymentNoticeRule.PaymentReferenceFor(n.SellerPaymentId, n.PaymentIntentId)!;
            var grantorName = await _db.Users.AsNoTracking().Where(u => u.Id == grantor).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "ผู้ให้ความยินยอม";
            // ฝ่ายค้าน M3/M5: หมายเหตุของการจ่ายบอกตรง ๆ ว่าระบบบันทึกเอง · ตามความยินยอมของใคร · ด้วยหลักฐานอะไร (ผู้บันทึก = ผู้ให้ความยินยอม ไม่ใช่ผู้กด)
            var notes = $"⚙️ ระบบบันทึกเอง (ไม่มีผู้กด) ตามความยินยอมของ {grantorName} — {source.Label} · {PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)} · "
                + $"{n.SourceDocumentNumber} · ความยินยอม {consent.ConsentId:N}";
            Models.DTOs.Document.PaymentResponse pay;
            try
            {
                pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: n.TargetDocumentId, PaymentDate: n.PaidAt, Amount: amount, PaymentMethod: PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel),
                    Reference: reference, BankAccount: null, Notes: notes,
                    OverrideBankAccountId: bank, WithholdingTaxAmount: plan.Wht), grantor.ToString(), PaymentEntrySource.PartnerAuto);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or BusinessRuleException or DbUpdateException)
            {
                // เส้นจ่ายเดิมปฏิเสธ (งวดปิด · ยอดเกิน · วันที่ ฯลฯ — ตัวตรวจตัวเดียวกับปุ่มเดียว) ⇒ กลับเป็นปุ่มเดียว + เหตุของมัน
                await lockTx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                if (ex is DbUpdateException) _logger.LogError(ex, "ท่อบันทึกจ่ายจากการแจ้ง {Notice} ของบริษัท {Company} ล้มที่ฐานข้อมูล", noticeId, companyId);
                await ReleaseAsync(n, PartnerAutoRecordPolicy.Codes.PostFailed, "บันทึกจ่ายอัตโนมัติไม่สำเร็จ: " + UserMessageOf(ex) + " — กดบันทึกเองได้", nowUtc, ct);
                return false;
            }
            await lockTx.CommitAsync(ct);
            await MarkRecordedAsync(companyId, noticeId, pay.Id, consent, source, plan, nowUtc, ct);
            await NotifyAutoRecordedAsync(companyId, n, amount, plan.Wht ?? 0m, source, grantorName, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ท่อบันทึกจ่ายจากการแจ้งคู่ค้า {Notice} ของบริษัท {Company} ล้ม — แถวกลับเป็นรอบันทึก (กดบันทึกเองได้)", noticeId, companyId);
            try
            {
                _db.ChangeTracker.Clear();
                await ReleaseAsync(n, PartnerAutoRecordPolicy.Codes.PostFailed, "ระบบบันทึกเองไม่สำเร็จ (ข้อผิดพลาดภายใน) — กดบันทึกเองได้ · ระบบลองใหม่รอบถัดไป", nowUtc, CancellationToken.None);
            }
            catch (Exception rex) when (rex is not OperationCanceledException)
            {
                _logger.LogError(rex, "ปล่อยแถวแจ้ง {Notice} กลับเป็นรอบันทึกไม่สำเร็จ (ค้างกำลังบันทึก — ปุ่มเดียวกดได้หลัง {Min} นาที)", noticeId, PartnerPaymentNoticeRule.RecordingStale.TotalMinutes);
            }
            return false;
        }
    }

    /// <summary>ฝ่ายค้าน M5: ระบบบันทึกจ่ายเองสำเร็จ ⇒ แจ้งผู้ซื้อหนึ่งครั้งต่อการบันทึก (ยอด · บัญชี · ภาษีหัก · ลิงก์ใบซื้อเพื่อตรวจ/ยกเลิก) — เครื่องแจ้งเตือนกลาง
    /// (ตารางของบริษัท/ผู้ใช้รวมเป็นสรุปได้ตามค่าตั้งของเขา) · ล้ม = LogError (การจ่ายบันทึกแล้ว + audit มีแล้ว)</summary>
    private async Task NotifyAutoRecordedAsync(Guid companyId, PartnerPaymentNotice n, decimal amount, decimal wht, PartnerPaySourceAccount.Decision source, string grantorName,
        CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = $"⚙️ ระบบบันทึกจ่าย {amount:N2} บาท ของ {n.SourceDocumentNumber} เองแล้ว (ตามความยินยอมของ {grantorName})",
                Message = $"จ่าย {amount:N2} บาท + ภาษีหัก ณ ที่จ่าย {wht:N2} บาท · {source.Label} · ลงวันที่ผู้ขายรับเงิน {n.PaidAt.AddHours(7):dd/MM/yyyy} — "
                    + "ตรวจกับรายการเดินบัญชีของเรา · ไม่ถูกต้องให้ยกเลิกการจ่ายที่ใบซื้อ · หยุดการบันทึกเองได้ที่ “งานอัตโนมัติกับคู่ค้า”",
                ActionUrl = $"/pages/documents.html?id={n.TargetDocumentId}",
                EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ct;
            _logger.LogError(ex, "แจ้งบริษัท {Company} ว่าระบบบันทึกจ่ายเอง (การแจ้ง {Notice}) ไม่สำเร็จ — การจ่ายและ audit บันทึกแล้ว", companyId, n.Id);
        }
    }

    /// <summary>ปิดแถวเป็น "บันทึกแล้ว" + audit (ความยินยอม · ที่มาของบัญชี · ผู้แพ้ · ยอด · ภาษีหัก) · ผู้ขายยกเลิกระหว่างบันทึก ⇒ ป้ายบนใบซื้อ</summary>
    private async Task MarkRecordedAsync(Guid companyId, Guid noticeId, Guid paymentId, PartnerConsent.UseCheck? consent, PartnerPaySourceAccount.Decision? source,
        PartnerPaymentNoticeRule.Plan? plan, DateTime nowUtc, CancellationToken ct)
    {
        var fresh = await _db.PartnerPaymentNotices.FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        fresh.Status = PartnerPaymentNoticeRule.StatusRecorded;
        fresh.RecordedPaymentId = paymentId;
        fresh.ResolvedAt = nowUtc;
        fresh.ResolvedBy = consent?.GrantorUserId?.ToString() ?? "partner-auto";
        fresh.AutoBlockedReason = null;
        fresh.AutoBlockedCode = null;
        fresh.AutoEvaluatedAt = nowUtc;
        fresh.UpdatedAt = nowUtc;
        PartnerPaymentNoticeService.Audit(_db, companyId, consent?.GrantorUserId, consent == null ? "PartnerPaymentNoticeRecorded" : "PartnerPaymentAutoRecorded", fresh,
            extra: consent == null ? (object)new { recoveredExistingPayment = true } : new
            {
                consentId = consent.ConsentId, grantorUserId = consent.GrantorUserId, origin = source?.Origin.ToString(), bankAccountId = source?.BankAccountId,
                evidence = source?.Label, confidence = source?.Confidence, losers = source?.Losers.Select(l => new { origin = l.Origin.ToString(), l.BankAccountId }),
                amount = plan?.Amount, wht = plan?.Wht, entrySource = nameof(PaymentEntrySource.PartnerAuto),
                ruleCode = PartnerAutoRecordPolicy.RuleCode, legalReference = PartnerAutoRecordPolicy.LegalReference,
                consentRuleCode = PartnerConsent.RuleCode, usedAi = false,
            });
        await _db.SaveChangesAsync(ct);
        if (fresh.SellerVoidedAt != null || await PartnerPaymentNoticeService.SellerPaymentLiveAsync(_db, fresh, ct) == false)
            await PartnerPaymentNoticeService.FlagRecordedVoidedAsync(_db, fresh, "ผู้ขาย", nowUtc, ct);
    }

    /// <summary>ไม่ผ่าน ⇒ แถวกลับเป็นรอบันทึก + เหตุ · audit <c>PartnerPaymentAutoBlocked</c> เฉพาะเมื่อเหตุเปลี่ยน (ครั้งแรกต่อเหตุ — ไม่สแปม)</summary>
    private async Task ReleaseAsync(PartnerPaymentNotice n, string code, string reason, DateTime nowUtc, CancellationToken ct)
    {
        var r = reason.Length > 500 ? reason[..500] : reason;
        await _db.PartnerPaymentNotices
            .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusRecording)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusOpen)
                .SetProperty(x => x.AutoBlockedReason, r)
                .SetProperty(x => x.AutoBlockedCode, code)
                .SetProperty(x => x.AutoEvaluatedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (n.AutoBlockedCode == code) return;
        var fresh = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
        PartnerPaymentNoticeService.Audit(_db, n.CompanyId, null, "PartnerPaymentAutoBlocked", fresh,
            extra: new { code, reason = r, ruleCode = PartnerAutoRecordPolicy.RuleCode, legalReference = PartnerAutoRecordPolicy.LegalReference, usedAi = false });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>ผู้ขายยกเลิกการรับชำระแล้วแต่ขั้นถอนตอนยกเลิกไม่ได้ทำงาน (ล้ม/แถวเก่า) ⇒ ถอนที่นี่ (ตรวจสด — ตาข่ายชั้นสอง)</summary>
    private async Task WithdrawStaleAsync(PartnerPaymentNotice n, DateTime nowUtc, CancellationToken ct)
    {
        await _db.PartnerPaymentNotices
            .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusRecording)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusWithdrawn)
                .SetProperty(x => x.SellerVoidedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.ResolvedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.ResolvedBy, "partner-seller-voided")
                .SetProperty(x => x.AutoBlockedReason, PartnerAutoRecordPolicy.SellerVoidedReason)
                .SetProperty(x => x.AutoBlockedCode, PartnerAutoRecordPolicy.Codes.SellerVoided)
                .SetProperty(x => x.AutoEvaluatedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        var fresh = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
        PartnerPaymentNoticeService.Audit(_db, n.CompanyId, null, "PartnerPaymentNoticeWithdrawn", fresh,
            extra: new { detectedBy = "pipeline", ruleCode = PartnerAutoRecordPolicy.RuleCode });
        await _db.SaveChangesAsync(ct);
    }

    // ═══════════ ข้อเท็จจริงของตัวตัดสินแหล่งเงิน (ผู้อ่านเดียว — ปุ่มเดียวและท่อใช้ร่วม) ═══════════

    /// <summary>บัญชีของผู้ซื้อที่ใช้จ่ายออกได้ → ป้าย · ตัวตรวจเดียวกับฝั่งรับ (<see cref="SlipReceivingAccount.UsableProblem"/> คู่สมมาตร) + ผังที่ลงได้
    /// (<see cref="DocumentMoneyChannel.EvaluateAsync"/>) · ของบริษัทผู้เรียกเท่านั้น</summary>
    public static async Task<Dictionary<Guid, string>> UsablePayAccountsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var rows = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .OrderBy(b => b.BankName).ThenBy(b => b.AccountNumber).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.BankName, b.AccountName, b.AccountNumber, b.IsActive, b.Currency, b.AccountType })
            .ToListAsync(ct);
        var map = new Dictionary<Guid, string>();
        foreach (var b in rows)
        {
            var label = $"{b.BankName} · {b.AccountName} · ••{(b.AccountNumber.Length > 4 ? b.AccountNumber[^4..] : b.AccountNumber)}";
            var gl = await DocumentMoneyChannel.EvaluateAsync(db, companyId, b.Id, null, MoneyChannelCheck.Post, "แก้ที่หน้า “บัญชีธนาคาร”", ct);
            // เงินออก: ไม่ต้องมีเลขรับโอน (HasTransferId = true) — ส่วนอื่นเหมือนฝั่งรับ (ใช้งาน · บาท · ไม่ใช่ฝากประจำ · ผูกผังที่ลงได้)
            var problem = SlipReceivingAccount.UsableProblem(
                new SlipReceivingAccount.AccountFacts(b.Id, label, b.IsActive, b.Currency ?? "THB", b.AccountType ?? "", true, gl.Problem), "THB");
            if (problem == null) map[b.Id] = label;
        }
        return map;
    }

    /// <summary>ตัวตัดสินแหล่งเงินของการแจ้งหนึ่งแถว — โหลดข้อเท็จจริงแล้วเรียก <see cref="PartnerPaySourceAccount.Decide"/> (ตัวเดียว · ผู้เรียก: ท่อ + หน้า) ·
    /// <b>ชั้นที่ 1–3 ยังไม่มีผู้ป้อน</b> (ส่ง null ตรง ๆ — backlog P2/P4 · ไม่มีเมธอดว่างที่ดูเหมือนตรวจแล้ว — ฝ่ายค้าน M3) ·
    /// คำแนะนำจากประวัติ = การ<b>จ่าย</b>เอกสารฝั่งซื้อของผู้ติดต่อนี้เท่านั้น (ฝ่ายค้าน L5)</summary>
    public static async Task<PartnerPaySourceAccount.Decision> SourceDecisionAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, Document? purchase,
        IReadOnlyDictionary<Guid, string> usable, PartnerConsent.UseCheck? consent, DateTime nowUtc, CancellationToken ct = default)
    {
        _ = n;
        Guid? history = null;
        if (purchase != null)
        {
            var since = nowUtc.AddMonths(-12);
            var purchaseTypes = DocumentPermissionHelper.PurchaseTypes.ToList();
            history = await db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.BankAccountId != null && p.PaymentDate >= since
                    && p.Document.CompanyId == companyId && p.Document.ContactId == purchase.ContactId && purchaseTypes.Contains(p.Document.DocumentType))
                .GroupBy(p => p.BankAccountId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key).FirstOrDefaultAsync(ct);
        }
        return PartnerPaySourceAccount.Decide(new PartnerPaySourceAccount.Facts(
            Usable: usable,
            BankLine: null,        // backlog แพ็ก P4 (BankPaymentLineMatch — รายการเดินบัญชีฝั่งเงินออกที่ตรงการแจ้งเดียว)
            PayerDeclared: null,   // backlog แพ็ก P4 ("จ่ายจากระบบของเรา" — PartnerPaymentPlans)
            SlipMatch: null,       // backlog แพ็ก P2 (SlipAccountMatch — สลิปตรงบัญชีเดียวของเรา)
            ConsentDefault: consent is { Ok: true } ? consent.DefaultPayBankAccountId : null,
            ConsentDefaultProblem: consent?.PayAccountProblem,
            History: history,
            PurchaseDocBank: purchase?.BankAccountId));
    }

    // ═══════════ แถว Autopilot "การแจ้งชำระจากคู่ค้าค้าง" (query ของท่อ — ผู้อ่าน: AutopilotService.PartnerPayBlockedIssuesAsync) ═══════════

    /// <summary>แถวค้างของบริษัทหนึ่ง (อ่านอย่างเดียว · ผูก CompanyId): (ก) รอบันทึกเกิน 1 วัน · หรือท่อบอกเหตุที่ไม่ใช่ "ยังไม่ได้ยินยอม" (แสดงทันที — M7)
    /// (ข) กำลังบันทึกค้าง (ค) บันทึกแล้วแต่ผู้ขายยกเลิกการรับเงิน และการจ่ายของเรายังมีผล</summary>
    public sealed record BlockedRow(Guid Id, string SourceDocumentNumber, Guid TargetDocumentId, decimal CashAmount, decimal SellerRecordedWht, DateTime PaidAt,
        string? Note, string? AutoBlockedReason, DateTime CreatedAt, bool SellerVoidedAfterRecord);

    public static async Task<List<BlockedRow>> BlockedRowsAsync(AccountingDbContext db, Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var openCut = nowUtc.AddDays(-1);
        var staleCut = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        var consentCode = PartnerAutoRecordPolicy.Codes.Consent;
        var pending = await db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.SellerVoidedAt == null
                && ((n.Status == PartnerPaymentNoticeRule.StatusOpen
                        && (n.CreatedAt < openCut || (n.AutoBlockedReason != null && n.AutoBlockedCode != consentCode)))
                    || (n.Status == PartnerPaymentNoticeRule.StatusRecording && (n.UpdatedAt ?? n.CreatedAt) < staleCut)))
            .OrderBy(n => n.PaidAt).Take(50)
            .Select(n => new BlockedRow(n.Id, n.SourceDocumentNumber, n.TargetDocumentId, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Note, n.AutoBlockedReason,
                n.CreatedAt, false))
            .ToListAsync(ct);
        var voided = await db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.Status == PartnerPaymentNoticeRule.StatusRecorded && n.SellerVoidedAt != null
                && db.Payments.Any(p => p.Id == n.RecordedPaymentId && p.CompanyId == companyId && !p.IsDeleted))
            .OrderByDescending(n => n.SellerVoidedAt).Take(50)
            .Select(n => new BlockedRow(n.Id, n.SourceDocumentNumber, n.TargetDocumentId, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Note, n.AutoBlockedReason,
                n.SellerVoidedAt ?? n.CreatedAt, true))
            .ToListAsync(ct);
        return pending.Concat(voided).ToList();
    }

    // ═══════════ C-08: ผังบัญชีของใบซื้อร่างจากคู่ค้า (ตัวแนะนำเดิม · ไม่อนุมัติเอง) ═══════════

    /// <summary>
    /// ใบซื้อร่างที่สร้างจากเอกสารของคู่ค้า (<c>PartnerSourceLinkId</c>) ยังไม่มีผังบัญชี ⇒ ถามตัวแนะนำผังเดิม <c>IOcrAiAugmenter.SuggestGlAccountAsync</c>
    /// (orchestrator · นักเรียนก่อน · คีย์เดิม GlAccountSuggestion — ไม่เพิ่ม AiFeatureKey) · เติมเฉพาะคำตอบที่ผ่าน <see cref="GlSuggestionApplyPolicy.MayAutoFill"/>
    /// และ<b>อยู่ในชุดผังที่ใบซื้อลงได้ของบริษัท</b> (<see cref="DraftGlCandidatesAsync"/> — ค่าใช้จ่าย/สินทรัพย์ที่ไม่ใช่เงินสด·ธนาคาร·ลูกหนี้·ภาษีซื้อ · anti-hallucination) ·
    /// เขียนแบบมีเงื่อนไข <c>AccountId IS NULL</c> + ใบยังเป็นร่าง (ผังที่ผู้ใช้เพิ่งเลือกชนะเสมอ — L6) · เก็บ <c>GlAccountAiFeedbackId</c> ต่อบรรทัด ·
    /// ป้ายซื่อสัตย์บนหมายเหตุภายใน ("🤖 AI" เฉพาะเมื่อเรียกจริง) · เติมได้ ⇒ ป้ายปิด <see cref="DraftGlMarker"/> · เติมไม่ได้/ล้ม ⇒ ป้ายลอง <see cref="DraftGlTryMarker"/>
    /// (ถามซ้ำหลัง <see cref="DraftGlRetryEvery"/> · สูงสุด <see cref="DraftGlMaxTries"/> ครั้ง — โมเดลในบ้านอาจเรียนเพิ่ม) ·
    /// kill-switch: AI ปิด + นักเรียนไม่ถึงเกณฑ์ ⇒ ใบยังเป็นร่างที่ไม่มีผัง (เท่าเดิม) ไม่พัง · คืนจำนวนใบที่เติมได้
    /// </summary>
    public async Task<int> SuggestDraftGlAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (_glAi == null) return 0;
        var since = nowUtc.AddDays(-30);
        var retryCut = nowUtc - DraftGlRetryEvery;
        var drafts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId != null && d.Status == DocumentStatus.Draft
                && d.DocumentType == DocumentType.PurchaseInvoice && d.CreatedAt >= since
                && (d.InternalNotes == null || !d.InternalNotes.Contains(DraftGlMarker))
                && (d.InternalNotes == null || !d.InternalNotes.Contains(DraftGlTryMarker) || d.UpdatedAt == null || d.UpdatedAt < retryCut)
                && d.Lines.Any(l => !l.IsDeleted && l.AccountId == null))
            .OrderBy(d => d.CreatedAt).Take(DraftBatchPerCompany)
            .Select(d => new { d.Id, d.ContactId, d.InternalNotes })
            .ToListAsync(ct);
        if (drafts.Count == 0) return 0;
        var byCode = await DraftGlCandidatesAsync(_db, companyId, ct);
        var done = 0;
        foreach (var d in drafts)
        {
            var tries = CountOf(d.InternalNotes, DraftGlTryMarker) + 1;
            try
            {
                var vendor = await _db.Contacts.AsNoTracking().Where(c => c.Id == d.ContactId && c.CompanyId == companyId)
                    .Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
                var lines = await _db.DocumentLines.AsNoTracking()
                    .Where(l => l.DocumentId == d.Id && l.Document.CompanyId == companyId && !l.IsDeleted && l.AccountId == null)
                    .Select(l => new { l.Id, l.Description, l.Amount }).ToListAsync(ct);
                int filled = 0, asked = 0;
                bool usedAi = false, fromStudent = false;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Description) || line.Amount <= 0m) continue;
                    asked++;
                    var sugg = await _glAi.SuggestGlAccountAsync(companyId, Guid.Empty, vendor?.Name, vendor?.TaxId, null, line.Description, line.Amount, "THB",
                        null, 0m, ct: ct);
                    usedAi |= sugg.UsedAi;
                    fromStudent |= sugg.FromStudent;
                    var inCoa = sugg.Answer != null && byCode.ContainsKey(sugg.Answer);
                    if (!GlSuggestionApplyPolicy.MayAutoFill(sugg.Answer, sugg.Confidence, sugg.HasModelAnswer, inCoa)) continue;
                    Guid? accountId = byCode[sugg.Answer!];
                    var feedbackId = sugg.FeedbackId;
                    // ค่าผู้ใช้ชนะ: เขียนเฉพาะบรรทัดที่ยังไม่มีผังและใบยังเป็นร่าง ณ ตอนเขียน
                    filled += await _db.DocumentLines
                        .Where(l => l.Id == line.Id && l.DocumentId == d.Id && l.AccountId == null
                            && l.Document.CompanyId == companyId && l.Document.Status == DocumentStatus.Draft)
                        .ExecuteUpdateAsync(u => u.SetProperty(l => l.AccountId, accountId).SetProperty(l => l.GlAccountAiFeedbackId, feedbackId), ct);
                }
                var who = AiAnswerSource.Of(usedAi, fromStudent);
                var whoLabel = AiAnswerSource.Label(who);   // ป้ายซื่อสัตย์ (กฎ #1): 🤖 AI เฉพาะเมื่อเรียกจริง
                var stamp = $"{nowUtc.AddHours(7):dd/MM/yyyy}";
                var note = filled > 0
                    ? $"{DraftGlMarker} {stamp}: ผังบัญชีจาก {whoLabel} — เติม {filled}/{asked} บรรทัด"
                        + (filled < asked ? " (บรรทัดที่เหลือเลือกเองตอนตรวจ)" : "") + " · ตรวจผังแล้วอนุมัติใบซื้อเอง (ระบบไม่อนุมัติแทน)"
                    : tries >= DraftGlMaxTries
                        ? $"{DraftGlMarker} {stamp}: {whoLabel} — ยังไม่มีผังบัญชีที่ใช้ได้หลังถาม {tries} ครั้ง (เลิกถาม · เลือกผังเองตอนตรวจ)"
                        : $"{DraftGlTryMarker} {stamp}: {whoLabel} — ยังไม่มีผังบัญชีที่ใช้ได้ (ครั้งที่ {tries}/{DraftGlMaxTries} · ระบบถามใหม่ภายหลัง · เลือกผังเองได้เลย)";
                await AppendDraftNoteAsync(companyId, d.Id, note, nowUtc, ct);
                if (filled > 0)
                {
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId, Action = AuditAction.Update, EntityType = "Document", EntityId = d.Id.ToString(),
                        NewValues = JsonSerializer.Serialize(new
                        {
                            @event = "PartnerDraftGlSuggested", filled, asked, usedAi, fromLocalModel = fromStudent, ruleCode = PartnerExchangeRules.RuleCode,
                        }),
                        Timestamp = nowUtc,
                    });
                    await _db.SaveChangesAsync(ct);
                    done++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "เติมผังบัญชีใบซื้อร่างจากคู่ค้า {Doc} ของบริษัท {Company} ไม่สำเร็จ (ใบยังเป็นร่าง — เลือกผังเองตอนตรวจ)", d.Id, companyId);
                // ล้มกลางใบก็นับเป็นหนึ่งครั้ง (ไม่ถามซ้ำทุกรอบ 5 นาที — L6)
                try
                {
                    await AppendDraftNoteAsync(companyId, d.Id,
                        (tries >= DraftGlMaxTries ? DraftGlMarker : DraftGlTryMarker) + $" {nowUtc.AddHours(7):dd/MM/yyyy}: ถามผังบัญชีไม่สำเร็จ (ครั้งที่ {tries}/{DraftGlMaxTries}) — เลือกผังเองตอนตรวจ",
                        nowUtc, CancellationToken.None);
                }
                catch (Exception nex) when (nex is not OperationCanceledException)
                {
                    _logger.LogError(nex, "ประทับป้ายลองผังบัญชีบนใบซื้อร่าง {Doc} ไม่สำเร็จ", d.Id);
                }
            }
        }
        return done;
    }

    /// <summary>ชุดผังที่ตัวแนะนำเติมให้ใบซื้อร่างได้ (รหัส → id): ผังที่ใช้งานของบริษัท หมวดค่าใช้จ่าย หรือสินทรัพย์ที่<b>ไม่ใช่</b>เงินสด/ธนาคาร/ลูกหนี้/ภาษีซื้อ
    /// (รหัสขึ้นต้น 111 · 112 · 113 · 116) และไม่ใช่ผังที่บัญชีธนาคารผูกไว้ — รายได้/หนี้สิน/ทุน/เงินฝากไม่ผ่าน (ฝ่ายค้าน L6)</summary>
    public static async Task<Dictionary<string, Guid>> DraftGlCandidatesAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var bankLinked = await db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == companyId && !b.IsDeleted && b.LinkedAccountId != null)
            .Select(b => b.LinkedAccountId!.Value).ToListAsync(ct);
        var coa = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                && (a.AccountType == AccountType.Expense
                    || (a.AccountType == AccountType.Asset && !a.AccountCode.StartsWith("111") && !a.AccountCode.StartsWith("112")
                        && !a.AccountCode.StartsWith("113") && !a.AccountCode.StartsWith("116"))))
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync(ct);
        return coa.Where(a => !bankLinked.Contains(a.Id)).GroupBy(a => a.AccountCode).ToDictionary(g => g.Key, g => g.First().Id);
    }

    private static int CountOf(string? text, string marker)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>ต่อท้ายหมายเหตุภายในของใบซื้อร่าง (เฉพาะใบของบริษัทนี้ที่ยังเป็นร่าง — ผู้ใช้อนุมัติไปแล้วระหว่างรอ = ไม่แตะ)</summary>
    private Task<int> AppendDraftNoteAsync(Guid companyId, Guid documentId, string note, DateTime nowUtc, CancellationToken ct)
        => _db.Documents
            .Where(x => x.Id == documentId && x.CompanyId == companyId && x.Status == DocumentStatus.Draft)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.InternalNotes, x => x.InternalNotes == null || x.InternalNotes == "" ? note : x.InternalNotes + "\n" + note)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
}
