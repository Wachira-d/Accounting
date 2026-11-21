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
/// <item><b>คำตัดสิน 268-1-1 (แทน 264-3-14)</b>: บัญชีตั้งต้นที่ B ตั้งไว้ตอนยินยอม<b>ไม่บันทึกเองแล้ว</b> (preselect บนปุ่มเดียวเท่านั้น) — บันทึกเองต้องมีชั้นที่ 1–3
/// (รายการเดินบัญชีผ่าน <see cref="IBankLineEvidence"/> · ผู้จ่ายเลือก · สลิป) ⇒ วันนี้ production ไม่มีผู้ป้อน ⇒ ท่อประทับเหตุ <see cref="PartnerPaySourceAccount.ConsentDefaultNotAutoReason"/></item>
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
    /// <summary>คำตัดสิน 268-1-1 — จุดเสียบของชั้นที่ 1 (รายการเดินบัญชีฝั่งเงินออกที่ตรงการแจ้งเดียว) · รอบ 264 P4 (คำตัดสิน 264-5-10): ผู้ป้อนจริง =
    /// <see cref="BankPaymentLineEvidence"/> (ตาราง <c>BankTransactions</c> เดิม ⇒ <see cref="BankPaymentLineMatch"/>) ลงทะเบียนใน DI แล้ว ·
    /// ไม่มีบรรทัดที่ตรง ⇒ (null, null) ⇒ ท่อตัดสินจากชั้นที่มีจริง (สลิป/บัญชีตั้งต้น = preselect ไม่บันทึกเอง — 264-4-9 · 268-1-1)</summary>
    public interface IBankLineEvidence
    {
        /// <returns>บัญชีของบริษัทผู้ซื้อที่รายการเดินบัญชีแสดงว่าเงินออกจริง + ป้าย (วันที่) · ไม่พบ/ไม่ชัด ⇒ (null, null) — ห้ามเดา</returns>
        Task<(Guid? BankAccountId, string? Label)> FindAsync(Guid companyId, PartnerPaymentNotice notice, CancellationToken ct);

        /// <summary>หลังท่อบันทึกจ่ายจากหลักฐานบรรทัด ⇒ ผูกบรรทัดกับการจ่ายนั้น (กระทบยอดปิดในตัว) · คืน 1 = ผูกแล้ว · 0 = ผูกไม่ได้ (ถูกจับคู่ไปก่อน) ·
        /// ตัวปลอมในเทสต์ที่ไม่มีบรรทัดจริงใช้ค่าตั้งต้น (0)</summary>
        Task<int> BindAsync(Guid companyId, PartnerPaymentNotice notice, Guid paymentId, CancellationToken ct) => Task.FromResult(0);

        /// <summary>ฝ่ายค้าน 264-P4 L5: วันที่ของบรรทัดที่เป็นหลักฐาน (วันที่เงินออกจริง) — PV ลงวันนั้นเมื่อชั้นรายการเดินบัญชีชนะ · ไม่มี ⇒ null (ใช้วันที่ผู้ขายรับเงิน)</summary>
        Task<DateTime?> EvidenceDateAsync(Guid companyId, PartnerPaymentNotice notice, CancellationToken ct) => Task.FromResult<DateTime?>(null);
    }

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PartnerPayablePipeline> _logger;
    private readonly Accounting.Services.Ai.IOcrAiAugmenter? _glAi;
    private readonly INotificationEngine? _notify;
    private readonly IBankLineEvidence? _bankLine;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;

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
        Accounting.Services.Ai.IOcrAiAugmenter? glAi = null, INotificationEngine? notify = null, IBankLineEvidence? bankLine = null,
        Accounting.Services.Storage.IFileStorage? storage = null)
    { _db = db; _docs = docs; _scopes = scopes; _logger = logger; _glAi = glAi; _notify = notify; _bankLine = bankLine; _storage = storage; }

    /// <summary>หนึ่งรอบของงานเบื้องหลัง (ผู้เรียกเดียว = <c>AutopilotJob</c> ขั้น PartnerPayable) — คืนจำนวนงานที่ทำ (ถอน/ป้าย + บันทึกจ่าย + ใบร่างที่เติมผัง)</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var done = await SweepSellerVoidedAsync(nowUtc, ct);
        // รอบ 264 P4 (ฝ่ายค้าน 264-P2 L3 · คำตัดสิน 264-5-13): ล้างเลขผู้โอนบนการแจ้งที่ไม่ใช่บัญชีของผู้ซื้อ/จบแล้ว (ทุกบริษัท · ไม่ขึ้นกับความยินยอม — ลดข้อมูล ไม่ใช่งานอัตโนมัติทางเงิน)
        done += await SweepSlipPayerAsync(nowUtc, ct);
        // รอบ 264 P4 (264-5-12): การแจ้งที่ตรงแผน "แจ้งโอน" ซึ่งผู้ซื้อบันทึก PV ไว้แล้ว ⇒ ปิดเป็นบันทึกแล้ว (ไม่สร้าง PV ที่สอง · ทุกบริษัท)
        done += await CloseFromPlansAsync(nowUtc, ct);
        // C-08: ใบซื้อร่างจากคู่ค้าของบริษัทที่เปิดสร้างใบร่าง ⇒ ผังบัญชีจากตัวแนะนำเดิม (รอบ 288: ย้ายมาก่อนขั้นอนุมัติเอง — ผังต้องมีก่อนถึงจะอนุมัติได้)
        var drafters = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .Where(c => c.CreatePurchaseDraftsFromPartners).Select(c => c.CompanyId).ToListAsync(ct);
        foreach (var companyId in drafters)
            done += await SuggestDraftGlAsync(companyId, nowUtc, ct);
        // รอบ 288: อนุมัติใบซื้อร่างจากคู่ค้าเองภายใต้เพดาน (ขอบเขต AutoApprovePurchase) — ก่อนขั้นบันทึกจ่าย (ใบต้องอนุมัติก่อนจ่าย)
        var approvers = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.WithdrawnAt == null && c.Scope == nameof(Scope.AutoApprovePurchase)).Select(c => c.CompanyId).Distinct().ToListAsync(ct);
        foreach (var companyId in approvers)
            done += await AutoApproveDraftsAsync(companyId, nowUtc, ct);
        // รอบ 288: สำเนาสลิปจากผู้ขาย (ขอบเขต ReceiveSlipCopy ของผู้ซื้อ) — ก่อนขั้นบันทึกจ่าย (หลักฐานแนบใบซื้อ)
        var slipReceivers = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.WithdrawnAt == null && c.Scope == nameof(Scope.ReceiveSlipCopy)).Select(c => c.CompanyId).Distinct().ToListAsync(ct);
        foreach (var companyId in slipReceivers)
            done += await CopySlipsAsync(companyId, nowUtc, ct);
        var name = Scope.AutoRecordPayment.ToString();
        // บริษัทที่มีความยินยอมบันทึกจ่ายที่ยังใช้อยู่ — ไม่มี = ไม่มีงาน (ปุ่มเดียวเดิมทำงานเหมือนเดิม)
        var payers = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.WithdrawnAt == null && c.Scope == name).Select(c => c.CompanyId).Distinct().ToListAsync(ct);
        foreach (var companyId in payers)
            done += await RunForCompanyAsync(companyId, nowUtc, ct);
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
            var refs = PartnerPaymentNoticeRule.PaymentReferencesToCheck(n.SellerPaymentId, n.PaymentIntentId, n.SellerAllocationId);
            var existing = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference != null && refs.Contains(p.Reference))
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            // รอบ 264 P4 (264-5-12): ผู้ซื้อแจ้งโอนจากระบบของเราแล้ว (PV ของแผนเดียวกัน) ⇒ ปิดด้วย PV นั้น (ไม่สร้างที่สอง)
            existing ??= await PlanPaymentForNoticeAsync(_db, companyId, n, ct);
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
            (Guid? BankAccountId, string? Label) line = _bankLine == null ? default : await _bankLine.FindAsync(companyId, n, ct);
            var source = await SourceDecisionAsync(_db, companyId, n, doc, usable, consent, nowUtc, ct, line.BankAccountId, line.Label);
            // ฝ่ายค้าน 264-P4 L5: ชั้นรายการเดินบัญชีชนะ ⇒ PV ลงวันที่เงินออกจริง (งวดปิด = เส้นจ่ายเดิมปฏิเสธ ⇒ ปุ่มเดียว + เหตุ — กติกาเดียวกับทั้งระบบ)
            var paymentDate = n.PaidAt;
            if (source.Origin == PartnerPaySourceAccount.Origin.BankLine && _bankLine != null && await _bankLine.EvidenceDateAsync(companyId, n, ct) is DateTime lineDate)
                paymentDate = PaymentDateOfLine(lineDate);
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
                WhtDateInPastMonth: PartnerAutoRecordPolicy.WhtDateInPastMonthOf(paymentDate, plan.Wht, nowUtc)));
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

            var reference = PartnerPaymentNoticeRule.PaymentReferenceFor(n.SellerPaymentId, n.PaymentIntentId, n.SellerAllocationId)!;
            var grantorName = await _db.Users.AsNoTracking().Where(u => u.Id == grantor).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "ผู้ให้ความยินยอม";
            // ฝ่ายค้าน M3/M5: หมายเหตุของการจ่ายบอกตรง ๆ ว่าระบบบันทึกเอง · ตามความยินยอมของใคร · ด้วยหลักฐานอะไร (ผู้บันทึก = ผู้ให้ความยินยอม ไม่ใช่ผู้กด)
            var notes = $"{SettlementPaymentVoucher.PartnerAutoPaymentNotePrefix} ตามความยินยอมของ {grantorName} — {source.Label} · {PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)} · "
                + $"{n.SourceDocumentNumber} · ความยินยอม {consent.ConsentId:N}";
            Models.DTOs.Document.PaymentResponse pay;
            try
            {
                pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: n.TargetDocumentId, PaymentDate: paymentDate, Amount: amount, PaymentMethod: PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel),
                    Reference: reference, BankAccount: null, Notes: notes,
                    OverrideBankAccountId: bank, WithholdingTaxAmount: plan.Wht,
                    // รอบ 283 (คำสั่งเจ้าของ · 283-1): ใบสำคัญจ่ายเป็นเอกสาร (หลักฐานคู่การจ่าย · ไม่ลง JE ซ้ำ · อ้าง 50 ทวิ ใบเดียวกัน) ในธุรกรรมเดียวกับการจ่าย
                    IssuePaymentVoucherDocument: true), grantor.ToString(), PaymentEntrySource.PartnerAuto);
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
            // ฝ่ายค้าน 264-P4 M1: ผูกบรรทัดเกิดใน MarkRecordedAsync (ทุกทางที่ปิดการแจ้งด้วย PV — ตัวเขียนเดียว BindAsync)
            await MarkRecordedAsync(companyId, noticeId, pay.Id, consent, source, plan, nowUtc, ct);
            var pvNumber = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.DocumentType == DocumentType.PaymentVoucher && d.SettlementPaymentId == pay.Id && !d.IsDeleted)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            await NotifyAutoRecordedAsync(companyId, n, amount, plan.Wht ?? 0m, source, grantorName, pvNumber, ct);
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
        string? voucherNumber, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            // รอบ 281 (คำตัดสิน 281-2): คีย์ของขั้นนี้เอง (เดิมยืม partner_exchange)
            await _notify.DispatchAsync(companyId, NotificationEvents.PartnerPaymentAutoRecorded, new NotificationContext
            {
                Title = $"⚙️ ระบบบันทึกจ่าย {amount:N2} บาท ของ {n.SourceDocumentNumber} เองแล้ว (ตามความยินยอมของ {grantorName})",
                Message = $"จ่าย {amount:N2} บาท + ภาษีหัก ณ ที่จ่าย {wht:N2} บาท · {source.Label} · ลงวันที่ผู้ขายรับเงิน {n.PaidAt.AddHours(7):dd/MM/yyyy}"
                    + (voucherNumber == null ? "" : voucherNumber.StartsWith("DRAFT-") ? " · ใบสำคัญจ่ายร่าง (รอผู้มีสิทธิ์อนุมัติ)" : $" · ใบสำคัญจ่าย {voucherNumber}") + " — "
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
        // ฝ่ายค้าน 264-P4 M1: บรรทัดเงินออกที่ตรงการแจ้งนี้ (และบัญชีเดียวกับ PV) ⇒ ผูกกับ PV ทุกทาง (ท่อ · PV ของแผน · ตาข่าย) — ตัวเขียนเดียว ·
        // ไม่มีบรรทัด/คนละบัญชี ⇒ null/false ลง audit · ล้ม = LogError (การจ่ายบันทึกแล้ว)
        var bankLineBound = await BindLineAsync(companyId, fresh, paymentId, ct);
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
                consentRuleCode = PartnerConsent.RuleCode, usedAi = false, bankLineBound,
            });
        await _db.SaveChangesAsync(ct);
        await ScrubSlipPayerAsync(_db, fresh, nowUtc, ct);
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
        // รอบ 282 (คำสั่งเจ้าของ 282-6): ตรวจบัญชีที่จ่ายไม่ได้ ⇒ วิธีสำรอง: แจ้งให้ยืนยันที่หน้าใบซื้อ (ครั้งแรก + เตือนซ้ำตามตาราง SlipMoneySource · มีเพดาน)
        if (code == PartnerAutoRecordPolicy.Codes.Source) await SourceNeedsConfirmAsync(n, r, nowUtc, ct);
        if (n.AutoBlockedCode == code) return;
        var fresh = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
        PartnerPaymentNoticeService.Audit(_db, n.CompanyId, null, "PartnerPaymentAutoBlocked", fresh,
            extra: new { code, reason = r, ruleCode = PartnerAutoRecordPolicy.RuleCode, legalReference = PartnerAutoRecordPolicy.LegalReference, usedAi = false });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>รอบ 282: แจ้งผู้ซื้อให้ยืนยันบัญชีที่จ่าย — ครั้งแรกเมื่อพบเหตุ · เตือนซ้ำเมื่อ <see cref="SlipMoneySource.ReminderDue"/> (compare-and-set กันส่งซ้ำข้าม instance) ·
    /// ยืนยันได้เฉพาะที่หน้าใบซื้อ (ล็อกอิน + สิทธิ์อนุมัติใบซื้อ — ปุ่ม “บันทึกจ่าย” เดิม · ไม่มีปุ่มยืนยันในกลุ่ม) · ล้มส่ง = LogError (เหตุอยู่บนแถวแล้ว)</summary>
    private async Task SourceNeedsConfirmAsync(PartnerPaymentNotice n, string reason, DateTime nowUtc, CancellationToken ct)
    {
        var st = await _db.PartnerPaymentNotices.AsNoTracking().Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId)
            .Select(x => new { x.SourceNoticeAt, x.SourceRemindCount, x.SourceRemindedAt }).FirstOrDefaultAsync(ct);
        if (st == null) return;
        int reminder;
        if (st.SourceNoticeAt == null)
        {
            if (await _db.PartnerPaymentNotices.Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.SourceNoticeAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.SourceNoticeAt, (DateTime?)nowUtc), ct) == 0) return;
            reminder = 0;
        }
        else if (SlipMoneySource.ReminderDue(st.SourceNoticeAt.Value, st.SourceRemindCount, st.SourceRemindedAt, nowUtc))
        {
            if (await _db.PartnerPaymentNotices.Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.SourceRemindCount == st.SourceRemindCount)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.SourceRemindCount, st.SourceRemindCount + 1)
                        .SetProperty(x => x.SourceRemindedAt, (DateTime?)nowUtc), ct) == 0) return;
            reminder = st.SourceRemindCount + 1;
        }
        else return;
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(n.CompanyId, NotificationEvents.PartnerPaySourceNeedsConfirm, new NotificationContext
            {
                Title = (reminder > 0 ? $"🔔 เตือนครั้งที่ {reminder}: " : "") + $"ยืนยันบัญชีที่จ่าย {n.CashAmount:N2} บาท ของ {n.SourceDocumentNumber}",
                Message = "ระบบไม่บันทึกจ่ายเอง — " + reason + " · เปิดใบซื้อ เลือกบัญชีที่จ่ายจริง (ระบบเสนอค่าที่น่าจะใช่พร้อมเหตุ) แล้วกด “บันทึกจ่าย”",
                ActionUrl = $"/pages/documents.html?id={n.TargetDocumentId}",
                EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งบริษัท {Company} ให้ยืนยันบัญชีที่จ่าย (การแจ้ง {Notice}) ไม่สำเร็จ — เหตุอยู่บนแถวแล้ว", n.CompanyId, n.Id);
        }
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
    /// <b>ชั้นที่ 1–2 ยังไม่มีผู้ป้อน</b> (ส่ง null ตรง ๆ — backlog P4 · ไม่มีเมธอดว่างที่ดูเหมือนตรวจแล้ว — ฝ่ายค้าน M3) · ชั้นที่ 3 (สลิป) = <see cref="SlipMatchAsync"/> (รอบ 264 P2) ·
    /// คำแนะนำจากประวัติ = การ<b>จ่าย</b>เอกสารฝั่งซื้อของผู้ติดต่อนี้เท่านั้น (ฝ่ายค้าน L5)</summary>
    public static async Task<PartnerPaySourceAccount.Decision> SourceDecisionAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, Document? purchase,
        IReadOnlyDictionary<Guid, string> usable, PartnerConsent.UseCheck? consent, DateTime nowUtc, CancellationToken ct = default,
        Guid? bankLine = null, string? bankLineLabel = null)
    {
        Guid? history = null;
        // รอบ 264 P4 (264-5-11): ชั้น PayerDeclared = แผน "จ่ายจากระบบของเรา" ที่เปิดรายการชำระของผู้ขายตัวเดียวกับการแจ้งนี้ (บริษัทผู้เรียกเท่านั้น)
        var declared = n.PaymentIntentId is Guid intentId
            ? await db.PartnerPaymentPlans.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.SellerIntentId == intentId && x.Kind == PartnerPaymentPlanRule.KindGateway)
                .Select(x => new { x.BankAccountId, x.ChosenByUserId, x.Method }).FirstOrDefaultAsync(ct)
            : null;
        var declaredBy = declared == null ? null
            : await db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == companyId && cu.UserId == declared.ChosenByUserId)
                .Select(cu => cu.User.FullName).FirstOrDefaultAsync(ct);
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
            BankLine: bankLine,    // ผู้ป้อน = IBankLineEvidence ของท่อ (backlog แพ็ก P4 BankPaymentLineMatch — วันนี้ไม่มีตัวใน DI ⇒ null) · ตัวตัดสินกรองด้วยชุดที่ใช้ได้เอง
            BankLineLabel: bankLineLabel,
            PayerDeclared: declared?.BankAccountId,   // รอบ 264 P4: "จ่ายจากระบบของเรา" (PartnerPaymentPlans) · ตัวตัดสินกรองด้วยชุดที่ใช้ได้เอง
            PayerName: declaredBy,
            SlipMatch: await SlipMatchAsync(db, companyId, n, ct),   // รอบ 264 P2: ผู้โอนบนสลิปที่ผู้ขายได้รับ ⇒ SlipAccountMatch กับบัญชีของเรา
            ConsentDefault: consent is { Ok: true } ? consent.DefaultPayBankAccountId : null,
            ConsentDefaultProblem: consent?.PayAccountProblem,
            History: history,
            PurchaseDocBank: purchase?.BankAccountId,
            // ฝ่ายค้าน 264-P4 M4: พร้อมเพย์/วอลเล็ตไม่ผูกบัญชีต้นทาง ⇒ PayerDeclared preselect อย่างเดียว
            PayerDeclaredBindsAccount: declared == null || PartnerPaymentPlanRule.MethodBindsAccount(declared.Method)));
    }

    /// <summary>รอบ 264 P2 (คำตัดสิน 264-4-7): ผู้โอนบนสลิป (คอลัมน์บนแถวแจ้ง) ⇒ <see cref="SlipAccountMatch.Match"/> กับ<b>ทุกบัญชีที่ไม่ถูกลบ</b>ของผู้ซื้อ ·
    /// ไม่มีข้อมูล ⇒ null (ชั้นไม่เกิด — ผลเท่าเดิม) · บัตร/บิล/พร้อมเพย์ ⇒ <see cref="PartnerPaySourceAccount.SlipKind.None"/></summary>
    internal static async Task<PartnerPaySourceAccount.Slip?> SlipMatchAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, CancellationToken ct)
    {
        // รอบ 264 P4 (L3 · 264-5-13): เลขถูกล้างแล้ว ⇒ อ่านผลที่เก็บแทน (NotOurs คงผล — ยังเปิดตัวเลือกกรรมการจ่ายแทนให้คน)
        if (string.IsNullOrWhiteSpace(n.SlipPayerBankCode) || string.IsNullOrWhiteSpace(n.SlipPayerAccountMask)) return PartnerPaymentPlanRule.SlipFromStoredKind(n.SlipPayerMatch);
        var accounts = await db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new SlipAccountMatch.AccountKey(b.Id, b.BankName, b.AccountNumber)).ToListAsync(ct);
        return SlipKindOf(n.SlipPayerBankCode, n.SlipPayerAccountMask, accounts);
    }

    // ═══════════ รอบ 264 P4 — ตัวเขียนของ L3 (ล้างเลขผู้โอน) + ปิดการแจ้งด้วย PV ของแผนแจ้งโอน ═══════════

    public const int SlipSweepBatch = 100;

    /// <summary>ฝ่ายค้าน 264-P2 L3 (คำตัดสิน 264-5-13) — <b>ตัวเขียนเดียว</b>ของการล้างผู้โอนบนการแจ้ง: ตรวจผลจับคู่ (<see cref="SlipMatchAsync"/> ตัวอ่านเดียว) ·
    /// ไม่ใช่บัญชีของผู้ซื้อ หรือการแจ้งจบแล้ว (<see cref="PartnerPaymentPlanRule.ShouldClearSlipPayer"/>) ⇒ ล้างธนาคาร+เลข เหลือชนิด · อื่น ๆ ⇒ ประทับชนิดอย่างเดียว (watermark) ·
    /// เส้นตัดสิน (<see cref="SourceDecisionAsync"/>) ยังอ่านอย่างเดียว · audit chain เมื่อล้าง (ไม่มีเลขใน audit) · คืน 1 = ล้าง</summary>
    public static async Task<int> ScrubSlipPayerAsync(AccountingDbContext db, PartnerPaymentNotice n, DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(n.SlipPayerAccountMask) && string.IsNullOrWhiteSpace(n.SlipPayerBankCode)) return 0;
        var slip = await SlipMatchAsync(db, n.CompanyId, n, ct);
        var kind = slip?.Kind;
        var stored = kind?.ToString();
        var status = await db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId)
            .Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (!PartnerPaymentPlanRule.ShouldClearSlipPayer(kind, status))
        {
            await db.PartnerPaymentNotices.IgnoreQueryFilters().Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipPayerMatch, stored), ct);
            return 0;
        }
        var cleared = await db.PartnerPaymentNotices.IgnoreQueryFilters()
            .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && (x.SlipPayerAccountMask != null || x.SlipPayerBankCode != null))
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.SlipPayerAccountMask, (string?)null)
                .SetProperty(x => x.SlipPayerBankCode, (string?)null)
                .SetProperty(x => x.SlipPayerMatch, stored)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (cleared != 1) return 0;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = n.CompanyId, Action = AuditAction.Update, EntityType = nameof(PartnerPaymentNotice), EntityId = n.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerPaymentNoticeSlipPayerCleared", slipMatch = stored, status, ruleCode = PartnerPaymentPlanRule.RuleCode,
                legalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562 ม.22 (เก็บเท่าที่จำเป็น) · ม.37(3) (ลบเมื่อพ้นความจำเป็น)",
            }),
            Timestamp = nowUtc,
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>ตาข่ายของ <see cref="ScrubSlipPayerAsync"/> (ทุกบริษัท · batch): แถวที่ยังมีเลข และ (ยังไม่เคยตรวจ หรือจบแล้ว) — แถวที่ตรวจแล้วและยังเปิดไม่ถูกหยิบซ้ำ (watermark = <c>SlipPayerMatch</c>)</summary>
    public async Task<int> SweepSlipPayerAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var terminal = new[] { PartnerPaymentNoticeRule.StatusRecorded, PartnerPaymentNoticeRule.StatusDismissed, PartnerPaymentNoticeRule.StatusWithdrawn };
        var rows = await _db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.SlipPayerAccountMask != null && (x.SlipPayerMatch == null || terminal.Contains(x.Status)))
            .OrderBy(x => x.CreatedAt).Take(SlipSweepBatch).ToListAsync(ct);
        var done = 0;
        foreach (var n in rows) done += await ScrubSlipPayerAsync(_db, n, nowUtc, ct);
        return done;
    }

    /// <summary>รอบ 264 P4 (264-5-12): PV ของผู้ซื้อจากแผน "แจ้งโอน" ของใบซื้อเดียวกัน (บริษัทผู้เรียกเท่านั้น · PV ยังไม่ถูกยกเลิก) · ไม่พบ ⇒ null ·
    /// ฝ่ายค้าน 264-P4 M2/M3 (คำตัดสิน 264-5-18): PV ของแผนหาจาก <c>BuyerPaymentId</c> หรือเลขอ้างอิง canonical ของแผน (ขั้นผูกล้มก็ยังเจอ) · แผนที่ผู้ขายไม่ได้รับสำเนาสลิป
    /// (ไม่มีรายการของผู้ขาย) จับคู่ด้วยยอด/วันที่ตัวเดียวที่ยังไม่ถูกการแจ้งอื่นอ้าง (ตัวตัดสิน pure <see cref="PartnerPaymentPlanRule.PlanPaymentFor"/>) · L6: แผนที่ถูกลบไม่นับ</summary>
    public static async Task<Guid?> PlanPaymentForNoticeAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, CancellationToken ct = default)
    {
        var rows = await db.PartnerPaymentPlans.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.Kind == PartnerPaymentPlanRule.KindTransfer && x.PurchaseDocumentId == n.TargetDocumentId)
            .Select(x => new { x.Id, x.BuyerPaymentId, x.SellerIntentId, x.Amount, x.TransferredAt }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var facts = new List<PartnerPaymentPlanRule.TransferPlanFact>();
        foreach (var r in rows)
        {
            var pv = r.BuyerPaymentId ?? await PartnerOutboundPaymentService.PlanPaymentByReferenceAsync(db, companyId, r.Id, ct);
            if (pv is not Guid id || !await db.Payments.AsNoTracking().AnyAsync(p => p.Id == id && p.CompanyId == companyId && !p.IsDeleted, ct)) continue;
            var claimed = await db.PartnerPaymentNotices.AsNoTracking().AnyAsync(o => o.CompanyId == companyId && o.Id != n.Id && !o.IsDeleted
                && o.RecordedPaymentId == id && o.Status == PartnerPaymentNoticeRule.StatusRecorded, ct);
            facts.Add(new PartnerPaymentPlanRule.TransferPlanFact(r.Id, id, r.SellerIntentId, r.Amount, r.TransferredAt, claimed));
        }
        return PartnerPaymentPlanRule.PlanPaymentFor(n.CashAmount, n.PaidAt, n.PaymentIntentId, facts);
    }

    /// <summary>ฝ่ายค้าน 264-P4 L5: วันที่ของบรรทัดธนาคาร ⇒ วันที่ของ PV (วันตามปฏิทินไทย เก็บเป็นวันที่ไม่มีเวลา แบบวันที่การชำระของหน้าเอกสาร)</summary>
    internal static DateTime PaymentDateOfLine(DateTime lineDate)
        => DateTime.SpecifyKind((lineDate.TimeOfDay == TimeSpan.Zero ? lineDate : lineDate.AddHours(7)).Date, DateTimeKind.Utc);

    /// <summary>ฝ่ายค้าน 264-P4 M1: ผูกบรรทัดผ่านตัวเขียนเดียว (<see cref="IBankLineEvidence.BindAsync"/>) — ผู้เรียก: <see cref="MarkRecordedAsync"/> · คืน null = ไม่มีตัวผูก</summary>
    private async Task<bool?> BindLineAsync(Guid companyId, PartnerPaymentNotice n, Guid paymentId, CancellationToken ct)
    {
        if (_bankLine == null) return null;
        try { return await _bankLine.BindAsync(companyId, n, paymentId, ct) == 1; }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _logger.LogError(ex, "ผูกรายการเดินบัญชีกับการจ่าย {Payment} ของการแจ้ง {Notice} ไม่สำเร็จ — กระทบยอดเองที่หน้าธนาคาร", paymentId, n.Id);
            return false;
        }
    }

    /// <summary>ตาข่ายของบริษัทที่ไม่มีความยินยอมบันทึกจ่าย (ท่อไม่ประเมินแถวของเขา): การแจ้งที่ยังเปิดซึ่งตรงแผนแจ้งโอนที่มี PV แล้ว ⇒ ปิดเป็นบันทึกแล้วด้วย PV นั้น
    /// (สถานะการแจ้งเท่านั้น — ไม่มีการเขียนเงิน) · จองแบบมีเงื่อนไขเหมือนท่อ</summary>
    public async Task<int> CloseFromPlansAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var hits = await _db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters()
            .Where(n => !n.IsDeleted && n.Status == PartnerPaymentNoticeRule.StatusOpen && n.SellerVoidedAt == null
                && _db.PartnerPaymentPlans.Any(x => x.CompanyId == n.CompanyId && x.PurchaseDocumentId == n.TargetDocumentId
                    && x.Kind == PartnerPaymentPlanRule.KindTransfer && !x.IsDeleted && x.BuyerPaymentId != null
                    // แผนที่ PV ยังไม่ถูกการแจ้งใดอ้าง (แถวที่ไม่ตรงไม่ถูกหยิบวนทุกรอบเมื่อแผนถูกใช้แล้ว)
                    && !_db.PartnerPaymentNotices.Any(o => o.CompanyId == x.CompanyId && o.RecordedPaymentId == x.BuyerPaymentId
                        && o.Status == PartnerPaymentNoticeRule.StatusRecorded)))
            .Select(n => new { n.Id, n.CompanyId }).Take(SweepBatch).ToListAsync(ct);
        var done = 0;
        foreach (var h in hits)
        {
            var n = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == h.Id && x.CompanyId == h.CompanyId, ct);
            if (await PlanPaymentForNoticeAsync(_db, h.CompanyId, n, ct) is not Guid pv) continue;
            var claimed = await _db.PartnerPaymentNotices
                .Where(x => x.Id == h.Id && x.CompanyId == h.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusOpen)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusRecording).SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
            if (claimed != 1) continue;
            await MarkRecordedAsync(h.CompanyId, h.Id, pv, null, null, null, nowUtc, ct);
            done++;
        }
        return done;
    }

    /// <summary>pure: ค่าบนแถวแจ้ง ⇒ ผลจับคู่ ⇒ ชนิดของชั้นสลิป (ตารางแปลงเดียว)</summary>
    internal static PartnerPaySourceAccount.Slip? SlipKindOf(string? bankCode, string? mask, IReadOnlyList<SlipAccountMatch.AccountKey> accounts)
    {
        if (string.IsNullOrWhiteSpace(bankCode) || string.IsNullOrWhiteSpace(mask)) return null;
        var (kind, m) = OcrSlipText.ClassifyMask(mask);
        var party = new OcrSlipText.SlipParty(kind, bankCode, false, m, null, null);
        var r = SlipAccountMatch.Match(party, accounts);
        return r.Kind switch
        {
            // ฝ่ายค้าน MED-2 (รอบ 282): ชื่อผู้โอนไม่ถูกเก็บบนการแจ้ง (PDPA · 264-P2) ⇒ PayerNameMatches = false ⇒ สลิปผู้โอนเสนอค่าได้ แต่ไม่บันทึกเอง (แจ้งยืนยัน)
            SlipAccountMatch.Kind.Unique => new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.Unique, r.AccountId, party.VisibleDigits.Count, PayerNameMatches: false),
            SlipAccountMatch.Kind.Many => new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.Many),
            SlipAccountMatch.Kind.NotOurs => new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.NotOurs),
            SlipAccountMatch.Kind.Unreadable => new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.Unreadable),
            _ => new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.None),
        };
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

    // ═══════════ รอบ 288 — อนุมัติใบซื้อร่างจากคู่ค้าเองภายใต้เพดาน (ขอบเขต AutoApprovePurchase · ตัวตัดสิน PartnerAutoApprovePolicy) ═══════════

    /// <summary>
    /// ใบซื้อร่างที่มาจากเอกสารของคู่ค้า (ลิงก์ <c>PartnerSourceLinkId</c>) ของคู่ที่บริษัทนี้ให้ความยินยอม <see cref="Scope.AutoApprovePurchase"/> ⇒ ประเมินด้วย
    /// <see cref="PartnerAutoApprovePolicy.Decide"/> · ผ่าน ⇒ อนุมัติผ่าน <c>IDocumentService.ApproveDocumentAsync</c> ตัวเดียว (เลขตามลำดับ gap-free เดิม ·
    /// ผู้อนุมัติ = GUID ผู้ให้ความยินยอม · แหล่ง Unattended ไม่เรียก AI) ภายในธุรกรรมล็อกความยินยอม (<see cref="PartnerConsentService.CheckForWriteAsync"/>) ·
    /// ไม่ผ่าน ⇒ ใบยังเป็นร่าง + เหตุบนลิงก์ (watermark) + หมายเหตุบนใบ/audit/แจ้งเตือนเมื่อเหตุเปลี่ยน · tenant = <paramref name="companyId"/> ทุก query
    /// </summary>
    public async Task<int> AutoApproveDraftsAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var name = Scope.AutoApprovePurchase.ToString();
        var consented = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.WithdrawnAt == null && c.Scope == name)
            .Select(c => c.TradingPartnershipId).Distinct().ToListAsync(ct);
        if (consented.Count == 0) return 0;
        var since = nowUtc.AddDays(-PartnerAutoApprovePolicy.LookbackDays);
        var due = nowUtc - PartnerAutoApprovePolicy.ReevaluateEvery;
        var rows = await (
                from l in _db.CrossTenantDocumentLinks.AsNoTracking()
                join d in _db.Documents.AsNoTracking() on l.TargetDocumentId equals d.Id
                where l.TargetCompanyId == companyId && !l.IsDeleted && consented.Contains(l.TradingPartnershipId)
                    && d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId == l.Id
                    && d.Status == DocumentStatus.Draft && d.DocumentType == DocumentType.PurchaseInvoice && d.CreatedAt >= since
                    && (l.AutoApproveEvaluatedAt == null || l.AutoApproveEvaluatedAt < due || (d.UpdatedAt != null && d.UpdatedAt > l.AutoApproveEvaluatedAt))
                orderby l.AutoApproveEvaluatedAt ?? DateTime.MinValue, d.CreatedAt
                select new { LinkId = l.Id, l.TradingPartnershipId, l.SourceCompanyId, l.SourceDocumentId, l.AutoApproveCode, DocId = d.Id })
            .Take(DraftBatchPerCompany).ToListAsync(ct);
        var approved = 0;
        foreach (var r in rows)
        {
            try
            {
                if (await AutoApproveOneAsync(companyId, r.LinkId, r.TradingPartnershipId, r.SourceCompanyId, r.SourceDocumentId, r.DocId, r.AutoApproveCode, nowUtc, ct))
                    approved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "ประเมินอนุมัติใบซื้อร่างจากคู่ค้า {Doc} ของบริษัท {Company} เองไม่สำเร็จ — ใบยังเป็นร่าง", r.DocId, companyId);
                try
                {
                    await RecordAutoApproveHoldAsync(companyId, r.LinkId, r.DocId, r.AutoApproveCode,
                        new PartnerAutoApprovePolicy.Verdict(false, PartnerAutoApprovePolicy.Codes.ApproveFailed, "ระบบประเมินไม่สำเร็จ (ข้อผิดพลาดภายใน) — ตรวจแล้วอนุมัติเอง"),
                        nowUtc, CancellationToken.None);
                }
                catch (Exception hex) when (hex is not OperationCanceledException)
                {
                    _logger.LogError(hex, "ประทับเหตุบนลิงก์ใบซื้อร่าง {Doc} ไม่สำเร็จ", r.DocId);
                }
            }
        }
        return approved;
    }

    /// <summary>ผังที่ผู้ใช้เคยยืนยันกับผู้ขายรายนี้ = ผังบนบรรทัดของใบฝั่งซื้อของผู้ติดต่อนี้ที่<b>คนอนุมัติแล้ว</b> (ไม่ใช่ร่าง/รออนุมัติ/ปฏิเสธ/ยกเลิก ·
    /// ไม่นับใบที่ระบบอนุมัติเองตามความยินยอม — ไม่เรียนจากตัวเอง) ย้อนหลัง 24 เดือน · ของบริษัทผู้เรียกเท่านั้น</summary>
    public static async Task<HashSet<Guid>> VendorConfirmedAccountsAsync(AccountingDbContext db, Guid companyId, Guid? contactId, Guid excludeDocumentId, DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (contactId is not Guid cid) return new HashSet<Guid>();
        var since = nowUtc.AddMonths(-24);
        var purchaseTypes = DocumentPermissionHelper.PurchaseTypes.ToList();
        var marker = PartnerAutoApprovePolicy.ApprovedMarker;
        var ids = await db.DocumentLines.AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId != null && l.Document.CompanyId == companyId && !l.Document.IsDeleted
                && l.Document.ContactId == cid && l.Document.Id != excludeDocumentId && purchaseTypes.Contains(l.Document.DocumentType)
                && l.Document.DocumentDate >= since
                && l.Document.Status != DocumentStatus.Draft && l.Document.Status != DocumentStatus.WaitingApproval
                && l.Document.Status != DocumentStatus.Rejected && l.Document.Status != DocumentStatus.Voided
                && (l.Document.InternalNotes == null || !l.Document.InternalNotes.Contains(marker)))
            .Select(l => l.AccountId!.Value).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task<bool> AutoApproveOneAsync(Guid companyId, Guid linkId, Guid partnershipId, Guid sourceCompanyId, Guid sourceDocumentId, Guid docId,
        string? previousCode, DateTime nowUtc, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == docId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (doc == null) return false;
        var contact = doc.ContactId is Guid contactId
            ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId, ct)
            : null;
        var lines = doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();

        // ── ผังบัญชีต่อบรรทัด: โมเดลในบ้าน ≥ เกณฑ์ (แถว feedback ของบรรทัด) หรือผู้ใช้เคยยืนยันกับผู้ขายรายนี้ ──
        var accountIds = lines.Where(l => l.AccountId != null).Select(l => l.AccountId!.Value).Distinct().ToList();
        var codes = await _db.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountCode }).ToDictionaryAsync(a => a.Id, a => a.AccountCode, ct);
        var feedbackIds = lines.Where(l => l.GlAccountAiFeedbackId != null).Select(l => l.GlAccountAiFeedbackId!.Value).Distinct().ToList();
        var feedback = await _db.AiSuggestionFeedbacks.AsNoTracking().Where(f => f.CompanyId == companyId && feedbackIds.Contains(f.Id))
            .Select(f => new { f.Id, f.LocalModelAnswer, f.LocalModelConfidence, f.UserChosenAnswer }).ToDictionaryAsync(f => f.Id, ct);
        var vendorAccounts = await VendorConfirmedAccountsAsync(_db, companyId, doc.ContactId, doc.Id, nowUtc, ct);
        var lineFacts = lines.Select(l =>
        {
            var code = l.AccountId is Guid a && codes.TryGetValue(a, out var c) ? c : null;
            var fb = l.GlAccountAiFeedbackId is Guid f && feedback.TryGetValue(f, out var row) ? row : null;
            var local = PartnerAutoApprovePolicy.LocalConfidentOf(code, fb?.LocalModelAnswer, fb?.LocalModelConfidence);
            var vendor = l.AccountId is Guid a2 && code != null
                && (vendorAccounts.Contains(a2) || string.Equals((fb?.UserChosenAnswer ?? "").Trim(), code, StringComparison.Ordinal));
            return new PartnerAutoApprovePolicy.Line(l.LineOrder, l.Description, code != null, local, vendor, InputVatBlocked: !l.IsVatClaimable && l.VatAmount > 0m);
        }).ToList();

        // ── ใบกำกับของผู้ขายครบ ม.86/4 (ตัวตรวจเดิมตัวเดียว · ไม่นับข้อ "ยังไม่ได้ต้นฉบับ" — อนุมัติไม่ใช่การเคลม) ──
        var missing = doc.HasTaxInvoiceReference
            ? PartnerAutoApprovePolicy.TaxInvoiceMissingForApproval(
                Accounting.Services.Implementations.Tax.TaxInvoiceCompletenessChecker.Evaluate(doc, contact).MissingFields,
                PartnerInputVatEvidence.BlocksClaim(doc.PartnerInputVatEvidence) ? PartnerInputVatEvidence.MissingLabel(doc.PartnerInputVatEvidence) : null)
            : Array.Empty<string>();
        var srcVoided = doc.PartnerInputVatEvidence == PartnerInputVatEvidence.SourceVoided
            || !await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(d => d.Id == sourceDocumentId && d.CompanyId == sourceCompanyId && !d.IsDeleted && d.Status != DocumentStatus.Voided, ct);
        var whtProblem = PartnerAutoApprovePolicy.WhtProblem(
            lines.Select(l => new PartnerAutoApprovePolicy.WhtLine(l.WithholdingTaxRate, l.WithholdingTaxAmount)).ToList(),
            doc.WithholdingTaxAmount, doc.WhtPayerBorneAmount, doc.DocumentDate);
        // คำเตือนก่อนอนุมัติจากตัวรวบรวมเดิม (อ่านอย่างเดียว · ไม่เรียก AI) — มีข้อเดียวก็ให้คนดู (รวม ม.65 ตรี · ม.82/5(4)(6) · หัก ณ ที่จ่าย)
        IReadOnlyList<string> warnings;
        try { warnings = await _docs.PreviewApprovalWarningsAsync(companyId, docId); }
        catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            warnings = new[] { "ตรวจข้อสังเกตก่อนอนุมัติไม่ได้: " + UserMessageOf(ex) };
        }

        // ── ธุรกรรมล็อกความยินยอม (connection แยก) — ถือไว้จนอนุมัติเสร็จ (ถอน/ระงับรอจน commit) ──
        using var lockScope = _scopes.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
        await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
        await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
        var consent = await consentSvc.CheckForWriteAsync(companyId, partnershipId, Scope.AutoApprovePurchase, CapOf(doc), ct);
        var grantorMayFinalize = consent.GrantorUserId is Guid g0 && !await DocumentRestrictionGuard.UserIsDraftOnlyAsync(_db, companyId, g0, ct);
        var verdict = PartnerAutoApprovePolicy.Decide(new PartnerAutoApprovePolicy.Facts(
            ConsentOk: consent.Ok, ConsentReason: consent.Reason,
            GrantorMayFinalize: grantorMayFinalize,
            IsDraft: doc.Status == DocumentStatus.Draft, SourceVoided: srcVoided, Currency: doc.Currency,
            IsTaxInvoice: doc.HasTaxInvoiceReference, TaxInvoiceMissing: missing,
            Lines: lineFacts, WhtProblem: whtProblem, ApprovalWarnings: warnings));
        if (!verdict.Auto || consent.GrantorUserId is not Guid grantor)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            await RecordAutoApproveHoldAsync(companyId, linkId, docId, previousCode, verdict, nowUtc, ct);
            return false;
        }
        try
        {
            // เส้นอนุมัติตัวเดียว (เลข gap-free · ด่านร่างเท่านั้น/อนุมัติตัวเอง · ตรวจต้นทางสด · คำเตือนที่เกิดใหม่ระหว่างนี้ ⇒ โยน = ร่างตามเดิม)
            await _docs.ApproveDocumentAsync(companyId, docId, grantor.ToString(), ApprovalAckSource.Unattended, withAiHints: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            if (ex is DbUpdateException) _logger.LogError(ex, "อนุมัติใบซื้อร่างจากคู่ค้า {Doc} ของบริษัท {Company} เองล้มที่ฐานข้อมูล", docId, companyId);
            var why = PartnerAutoApprovePolicy.ApproveErrorMessage(ex is DocumentApprovalWarningsException, ex is BusinessRuleException or ArgumentException or KeyNotFoundException
                or InvalidOperationException, ex.Message);
            if (why == PartnerAutoApprovePolicy.InternalErrorMessage && ex is not DbUpdateException)
                _logger.LogError(ex, "อนุมัติใบซื้อร่างจากคู่ค้า {Doc} ของบริษัท {Company} เองล้ม (ข้อผิดพลาดภายใน)", docId, companyId);
            await RecordAutoApproveHoldAsync(companyId, linkId, docId, previousCode,
                new PartnerAutoApprovePolicy.Verdict(false, PartnerAutoApprovePolicy.Codes.ApproveFailed, "อนุมัติเองไม่สำเร็จ: " + why), nowUtc, CancellationToken.None);
            return false;
        }
        await lockTx.CommitAsync(ct);
        await MarkAutoApprovedAsync(companyId, linkId, docId, partnershipId, consent, grantor, lineFacts, nowUtc, ct);
        return true;
    }

    private async Task MarkAutoApprovedAsync(Guid companyId, Guid linkId, Guid docId, Guid partnershipId, PartnerConsent.UseCheck consent, Guid grantor,
        IReadOnlyList<PartnerAutoApprovePolicy.Line> lines, DateTime nowUtc, CancellationToken ct)
    {
        var grantorName = await _db.Users.AsNoTracking().Where(u => u.Id == grantor).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "ผู้ให้ความยินยอม";
        var note = PartnerAutoApprovePolicy.ApprovedNote(grantorName, consent.MaxAmount, nowUtc);
        await _db.Documents.Where(x => x.Id == docId && x.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.InternalNotes, x => x.InternalNotes == null || x.InternalNotes == "" ? note : x.InternalNotes + "\n" + note), ct);
        await _db.CrossTenantDocumentLinks.Where(l => l.Id == linkId && l.TargetCompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.AutoApproveCode, PartnerAutoApprovePolicy.Codes.Approved)
                .SetProperty(l => l.AutoApproveReason, (string?)null).SetProperty(l => l.AutoApproveEvaluatedAt, (DateTime?)nowUtc), ct);
        var after = await _db.Documents.AsNoTracking().Where(x => x.Id == docId && x.CompanyId == companyId)
            .Select(x => new { x.DocumentNumber, x.TotalAmount, x.ContactId }).FirstAsync(ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = grantor, Action = AuditAction.Update, EntityType = "Document", EntityId = docId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerPurchaseAutoApproved", consentId = consent.ConsentId, grantorUserId = grantor, partnershipId, cap = consent.MaxAmount,
                total = after.TotalAmount, documentNumber = after.DocumentNumber,
                lines = lines.Select(l => new { l.Order, localConfident = l.LocalConfident, vendorConfirmed = l.VendorConfirmed }),
                ruleCode = PartnerAutoApprovePolicy.RuleCode, legalReference = PartnerAutoApprovePolicy.LegalReference, consentRuleCode = PartnerConsent.RuleCode, usedAi = false,
            }),
            Timestamp = nowUtc,
        });
        await _db.SaveChangesAsync(ct);
        // design §5.2: อนุมัติแล้ว ⇒ การแจ้ง "ผู้ขายรับเงินแล้ว" ที่ค้างของใบนี้ประเมินใหม่ทันทีในรอบนี้ (ไม่ต้องรอ 15 นาที)
        await _db.PartnerPaymentNotices
            .Where(n => n.CompanyId == companyId && n.TargetDocumentId == docId && n.Status == PartnerPaymentNoticeRule.StatusOpen)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.AutoEvaluatedAt, (DateTime?)null), ct);
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, NotificationEvents.PartnerPurchaseAutoApproved, new NotificationContext
            {
                Title = $"⚙️ ระบบอนุมัติใบซื้อ {after.DocumentNumber} ({after.TotalAmount:N2} บาท) เองแล้ว — ตามความยินยอมของ {grantorName}",
                Message = "ใบซื้อจากเอกสารของคู่ค้า · ยอดไม่เกินเพดาน · ผังบัญชีทุกบรรทัดมีหลักฐาน · ไม่มีข้อสังเกตก่อนอนุมัติ · ภาษีซื้อยังรอต้นฉบับใบกำกับตามเดิม · "
                    + "ไม่ถูกต้องให้ยกเลิกใบ · หยุดได้ที่หน้า “งานอัตโนมัติกับคู่ค้า”",
                ActionUrl = $"/pages/documents.html?id={docId}",
                EntityType = "Document", EntityId = docId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งบริษัท {Company} ว่าระบบอนุมัติใบซื้อ {Doc} เองไม่สำเร็จ — ใบอนุมัติแล้ว", companyId, docId);
        }
    }

    /// <summary>ไม่อนุมัติเอง ⇒ เหตุบนลิงก์ (watermark ทุกครั้ง) · เหตุเปลี่ยน ⇒ หมายเหตุบนใบร่าง + audit <c>PartnerPurchaseAutoApproveHeld</c> + แจ้งเตือน (ครั้งเดียวต่อเหตุ)</summary>
    private async Task RecordAutoApproveHoldAsync(Guid companyId, Guid linkId, Guid docId, string? previousCode, PartnerAutoApprovePolicy.Verdict verdict, DateTime nowUtc,
        CancellationToken ct)
    {
        var reason = verdict.Reason.Length > 500 ? verdict.Reason[..500] : verdict.Reason;
        var announce = PartnerAutoApprovePolicy.ShouldAnnounceHold(previousCode, verdict.Code);
        if (announce)
            await AppendDraftNoteAsync(companyId, docId, PartnerAutoApprovePolicy.HoldNote(reason, nowUtc), nowUtc, ct);
        await _db.CrossTenantDocumentLinks.Where(l => l.Id == linkId && l.TargetCompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.AutoApproveCode, verdict.Code).SetProperty(l => l.AutoApproveReason, reason)
                .SetProperty(l => l.AutoApproveEvaluatedAt, (DateTime?)nowUtc), ct);
        if (!announce) return;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = "Document", EntityId = docId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "PartnerPurchaseAutoApproveHeld", code = verdict.Code, reason, ruleCode = PartnerAutoApprovePolicy.RuleCode,
                legalReference = PartnerAutoApprovePolicy.LegalReference, usedAi = false,
            }),
            Timestamp = nowUtc,
        });
        await _db.SaveChangesAsync(ct);
        if (_notify == null) return;
        try
        {
            var number = await _db.Documents.AsNoTracking().Where(x => x.Id == docId && x.CompanyId == companyId).Select(x => x.SupplierInvoiceNumber).FirstOrDefaultAsync(ct);
            await _notify.DispatchAsync(companyId, NotificationEvents.PartnerPurchaseAutoApproveHeld, new NotificationContext
            {
                Title = $"⚙️ ใบซื้อร่างจากคู่ค้า {number ?? ""} ยังไม่ถูกอนุมัติเอง — รอคนตรวจ",
                Message = reason + " · ใบยังเป็นร่าง ตรวจแล้วกดอนุมัติเองได้ตามปกติ",
                ActionUrl = $"/pages/documents.html?id={docId}",
                EntityType = "Document", EntityId = docId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งบริษัท {Company} ว่าใบซื้อร่าง {Doc} ยังไม่อนุมัติเองไม่สำเร็จ", companyId, docId);
        }
    }

    // ═══════════ รอบ 288 — รับสำเนาสลิปการโอนจากคู่ค้า (ขอบเขต ReceiveSlipCopy ของผู้ซื้อ · ตัวตัดสิน PartnerSlipCopyRule) ═══════════

    /// <summary>
    /// การแจ้ง "ผู้ขายรับเงินแล้ว" ของบริษัทนี้ (ผู้ซื้อ) ที่มาจากสลิปซึ่งผู้ขายยืนยันแล้ว ⇒ ผู้ซื้อยินยอม <see cref="Scope.ReceiveSlipCopy"/> ⇒
    /// สำเนารูปสลิปของรายการนั้น (ไฟล์ที่ผูกกับรายการชำระของผู้ขาย — <c>PaymentIntent.SlipAttachmentId</c>) เข้าที่เก็บของผู้ซื้อ แนบที่ใบซื้อปลายทาง ·
    /// ไม่ยินยอม ⇒ ไม่อ่าน/ไม่ส่งรูป · ทำในงานเบื้องหลังของผู้ซื้อ (ไม่ใช่ในคำขอของผู้ขาย) · ความยินยอมตรวจเพื่อเขียนในธุรกรรมล็อกที่ถือจนบันทึกเสร็จ
    /// </summary>
    public async Task<int> CopySlipsAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var name = Scope.ReceiveSlipCopy.ToString();
        var consented = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.WithdrawnAt == null && c.Scope == name)
            .Select(c => c.TradingPartnershipId).Distinct().ToListAsync(ct);
        if (consented.Count == 0) return 0;
        var since = nowUtc.AddDays(-PartnerSlipCopyRule.LookbackDays);
        var ids = await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.SellerVoidedAt == null && consented.Contains(n.TradingPartnershipId)
                && n.PaymentIntentId != null && n.SlipCopyCheckedAt == null && n.CreatedAt >= since)
            .OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(SweepBatch).ToListAsync(ct);
        var copied = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await CopySlipOneAsync(companyId, id, nowUtc, ct)) copied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "รับสำเนาสลิปของการแจ้ง {Notice} ของบริษัท {Company} ไม่สำเร็จ — ลองรอบถัดไป", id, companyId);
            }
        }
        return copied;
    }

    private Accounting.Services.Storage.IFileStorage Storage => _storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;

    private async Task<bool> CopySlipOneAsync(Guid companyId, Guid noticeId, DateTime nowUtc, CancellationToken ct)
    {
        var n = await _db.PartnerPaymentNotices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        if (n == null || n.PaymentIntentId is not Guid intentId) return false;
        var purchase = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId && !d.IsDeleted).Select(d => new { d.Id, d.TotalAmount, d.Currency }).FirstOrDefaultAsync(ct);

        using var lockScope = _scopes.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
        await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
        await lockDb.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '180s'", ct);
        var consent = await consentSvc.CheckForWriteAsync(companyId, n.TradingPartnershipId, Scope.ReceiveSlipCopy,
            purchase == null ? null : new PartnerConsent.CapAmount(purchase.TotalAmount, string.IsNullOrWhiteSpace(purchase.Currency) ? "THB" : purchase.Currency.Trim()), ct);
        var grantedAt = consent.ConsentId is Guid cid
            ? await _db.PartnerAutomationConsents.AsNoTracking().Where(c => c.Id == cid && c.CompanyId == companyId).Select(c => (DateTime?)c.GrantedAt).FirstOrDefaultAsync(ct)
            : null;
        // ข้อมูลฝั่งผู้ขาย: อ่านเฉพาะรายการของการแจ้งนี้ (กุญแจ = intent + บริษัทผู้ขาย + เอกสารต้นทาง) — อ่านหลังความยินยอมผ่านเท่านั้น
        var intent = consent.Ok
            ? await _db.PaymentIntents.AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.Id == intentId && i.CompanyId == n.SourceCompanyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == n.SourceDocumentId)
                .Select(i => new { i.MethodKind, i.Status, i.SlipAttachmentId }).FirstOrDefaultAsync(ct)
            : null;
        var file = intent?.SlipAttachmentId is Guid fid
            ? await _db.Set<FileAttachment>().AsNoTracking().IgnoreQueryFilters()
                .Where(a => a.Id == fid && a.CompanyId == n.SourceCompanyId && !a.IsDeleted && a.EntityType == "Document" && a.EntityId == n.SourceDocumentId)
                .Select(a => new { a.StoragePath, a.CreatedBy }).FirstOrDefaultAsync(ct)
            : null;
        var verdict = PartnerSlipCopyRule.Decide(new PartnerSlipCopyRule.Facts(
            ConsentOk: consent.Ok && purchase != null, ConsentGrantedAt: grantedAt, NoticeCreatedAt: n.CreatedAt,
            HasIntent: intent != null, IntentIsSlip: intent?.MethodKind == PaymentMethodKind.ManualSlip, IntentSucceeded: intent?.Status == PaymentIntentStatus.Succeeded,
            SlipAttachmentId: intent?.SlipAttachmentId, AttachmentFound: file != null, AttachmentChannel: file?.CreatedBy));
        if (verdict.Outcome == PartnerSlipCopyRule.Outcome.Wait)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            return false;
        }
        if (verdict.Outcome == PartnerSlipCopyRule.Outcome.Done)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            await _db.PartnerPaymentNotices.Where(x => x.Id == noticeId && x.CompanyId == companyId && x.SlipCopyCheckedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipCopyCheckedAt, (DateTime?)nowUtc), ct);
            return false;
        }

        byte[]? bytes = null;
        try { bytes = await Storage.ReadAllBytesAsync(file!.StoragePath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _logger.LogError(ex, "อ่านไฟล์สลิปของผู้ขายสำหรับการแจ้ง {Notice} ไม่สำเร็จ (ครั้งที่ {Try})", noticeId, n.SlipCopyAttempts + 1);
        }
        var sniffed = bytes == null || bytes.Length == 0 ? null : SlipEvidenceRule.DetectPublicType(bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed);
        if (bytes == null || bytes.Length == 0 || ext == null)
        {
            await lockTx.RollbackAsync(CancellationToken.None);
            var finalTry = n.SlipCopyAttempts + 1 >= PartnerSlipCopyRule.MaxAttempts || (bytes != null && bytes.Length > 0 && ext == null);
            await _db.PartnerPaymentNotices.Where(x => x.Id == noticeId && x.CompanyId == companyId && x.SlipCopyCheckedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipCopyAttempts, x => x.SlipCopyAttempts + 1)
                    .SetProperty(x => x.SlipCopyCheckedAt, x => finalTry ? nowUtc : x.SlipCopyCheckedAt), ct);
            return false;
        }
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var absPath = System.IO.Path.Combine(Storage.Locator.RelativeBase, "wwwroot", PartnerSlipCopyRule.RelativeDir(nowUtc), storedName);
        await Storage.WriteAsync(absPath, bytes, sniffed, ct);
        try
        {
            var attachment = new FileAttachment
            {
                CompanyId = companyId, FileName = storedName, OriginalFileName = PartnerSlipCopyRule.AttachmentNamePrefix + " " + n.SourceDocumentNumber + ext,
                ContentType = sniffed!, FileSize = bytes.LongLength, StoragePath = absPath, EntityType = "Document", EntityId = n.TargetDocumentId,
                UploadedByUserId = null, CreatedBy = PartnerSlipCopyRule.AttachmentCreatedBy,
            };
            _db.Set<FileAttachment>().Add(attachment);
            var fresh = await _db.PartnerPaymentNotices.FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
            fresh.SlipCopyAttachmentId = attachment.Id;
            fresh.SlipCopyCheckedAt = nowUtc;
            fresh.UpdatedAt = nowUtc;
            var extra = new { slipCopyAttachmentId = attachment.Id, consentId = consent.ConsentId, ruleCode = PartnerSlipCopyRule.RuleCode, legalReference = PartnerSlipCopyRule.LegalReference };
            PartnerPaymentNoticeService.Audit(_db, companyId, consent.GrantorUserId, "PartnerSlipCopyReceived", fresh, extra: extra);
            PartnerPaymentNoticeService.Audit(_db, n.SourceCompanyId, null, "PartnerSlipCopySent", fresh, sellerSide: true);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // บันทึกแถวไม่ได้ ⇒ ไม่ทิ้งรูปกำพร้าในที่เก็บของผู้ซื้อ (PDPA) แล้วโยนต่อ (ผู้เรียก LogError · ลองรอบถัดไป)
            await lockTx.RollbackAsync(CancellationToken.None);
            try { await Storage.DeleteAsync(absPath); } catch (Exception delEx) when (delEx is not OperationCanceledException) { _ = delEx; }
            throw;
        }
        await lockTx.CommitAsync(ct);
        return true;
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
