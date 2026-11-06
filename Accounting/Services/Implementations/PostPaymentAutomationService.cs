using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 241 — <b>งานอัตโนมัติหลังรับชำระ</b>: ออก (ถ้ายังไม่มี) + ส่งใบเสร็จ หรือใบเสร็จรับเงิน/ใบกำกับภาษี ถึงลูกค้า (DOCUMENT_FLOW §2.12g · คำตัดสิน 241-5..241-9)
///
/// <para>ลำดับ: ทางเข้าบันทึกรับชำระ (gateway <c>DocumentPaymentHandler</c> · มือ <c>DocumentController.CreatePayment</c>) เรียก <see cref="EnqueueAsync"/> หลังการชำระ
/// commit แล้ว ⇒ แถว 1 แถวต่อการชำระ (unique ที่ฐาน — webhook ซ้ำ/กดซ้ำไม่เพิ่ม) ⇒ <c>EmailScheduleWorker</c> เรียก <see cref="ProcessDueAsync"/> ทุกนาที
/// (จองแถวด้วย SKIP LOCKED — หลายเครื่องไม่ทำซ้ำ) ⇒ ตัดสินด้วย <see cref="PostPaymentAutomationPolicy.Decide"/> ⇒ ทำขั้นด้วย<b>เส้นเดิมเท่านั้น</b>:
/// ออกใบเสร็จ = <c>IDocumentService.IssueReceiptForPaymentAsync</c> (idempotent · กติกา VAT/หัวเอกสาร §78/1 · เลข gap-free) ·
/// อีเมล = <c>IDocumentEmailService.SendDocumentEmailAsync</c> (ผู้ส่งในนามบริษัท + เพดาน + บันทึกการส่งบนเอกสาร) ·
/// บล็อกเพิ่มในอีเมล = <see cref="ReceiptEmailSections"/> จาก <see cref="IReceiptEmailSectionProvider"/> (รอบนี้ไม่มีตัวลงทะเบียน — จุดต่อของเส้นรับ 50 ทวิ)</para>
///
/// <para>ล้มดัง 3 ที่: สถานะ+เหตุผลบนแถว (หน้าเอกสาร) · การแจ้งเตือน <see cref="NotificationEvents.PostPaymentAutomationBlocked"/> · บันทึกอีเมล Failed บนเอกสาร ·
/// ลองใหม่สูงสุด <see cref="PostPaymentAutomationPolicy.MaxAttempts"/> ครั้ง — ขั้นที่สำเร็จแล้วไม่ทำซ้ำ (ไม่ส่งอีเมลซ้ำ)</para>
/// </summary>
public class PostPaymentAutomationService : IPostPaymentAutomationService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IDocumentEmailService _email;
    private readonly IEmailSenderFactory _senderFactory;
    private readonly ILogger<PostPaymentAutomationService> _logger;
    private readonly INotificationEngine? _notify;
    private readonly IEnumerable<IReceiptEmailSectionProvider> _sections;

    public const string SystemActor = "ระบบ (ส่งอัตโนมัติหลังรับชำระ)";

    public PostPaymentAutomationService(AccountingDbContext db, IDocumentService docs, IDocumentEmailService email,
        IEmailSenderFactory senderFactory, ILogger<PostPaymentAutomationService> logger, INotificationEngine? notify = null,
        IEnumerable<IReceiptEmailSectionProvider>? sections = null)
    {
        _db = db;
        _docs = docs;
        _email = email;
        _senderFactory = senderFactory;
        _logger = logger;
        _notify = notify;
        _sections = sections ?? Array.Empty<IReceiptEmailSectionProvider>();
    }

    /// <summary>ค่าที่มีผลของเอกสารนี้ = ค่าบริษัท ทับด้วยตารางส่งเอกสารที่ส่งใบนี้ (<c>Document.AutoSendRuleId</c>) ทีละช่อง</summary>
    internal async Task<PostPaymentAutomationPolicy.Settings> SettingsForAsync(Guid companyId, Guid? ruleId, CancellationToken ct)
    {
        var s = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new PostPaymentAutomationPolicy.Settings(x.PostPayEmailReceipt, x.PostPayIncludePartial))
            .FirstOrDefaultAsync(ct) ?? new PostPaymentAutomationPolicy.Settings(false, false);
        if (ruleId is not Guid rid) return s;
        var o = await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.Id == rid && r.CompanyId == companyId && !r.IsDeleted)
            .Select(r => new PostPaymentAutomationPolicy.Overrides(r.PostPayEmailReceipt, r.PostPayIncludePartial))
            .FirstOrDefaultAsync(ct);
        return PostPaymentAutomationPolicy.Effective(s, o);
    }

    public async Task<bool> EnqueueAsync(Guid companyId, Guid paymentId, string source, string? actor, CancellationToken ct = default)
    {
        try
        {
            var pay = await _db.Payments.AsNoTracking()
                .Where(p => p.Id == paymentId && p.CompanyId == companyId)
                .Select(p => new { p.DocumentId, RuleId = p.Document.AutoSendRuleId, p.Document.BalanceDue })
                .FirstOrDefaultAsync(ct);
            if (pay == null) return false;
            // ฝ่ายค้าน N: "ชำระครบ" ตัดสิน ณ เหตุการณ์ชำระนี้ (หลัง commit ของการชำระ) — ไม่ใช่ตอนงานรัน (งวดถัดไปอาจเข้ามาก่อน)
            var fullyPaid = pay.BalanceDue <= 0.005m;
            var settings = await SettingsForAsync(companyId, pay.RuleId, ct);
            if (!settings.Any) return false;   // ปิด ⇒ ไม่สร้างงาน (พฤติกรรมเดิม)

            var now = DateTime.UtcNow;
            var id = Guid.NewGuid();
            var by = string.IsNullOrWhiteSpace(actor) ? SystemActor : actor;
            var status = PostPaymentAutomationPolicy.StatusPending;
            // กันซ้ำที่ฐาน (unique CompanyId+PaymentId) — webhook/poll/กดซ้ำพร้อมกันได้แถวเดียว
            var n = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""PostPaymentAutomationRuns""
                    (""Id"", ""CompanyId"", ""PaymentId"", ""DocumentId"", ""Source"", ""Status"", ""FullyPaid"", ""Attempts"", ""NextAttemptAt"", ""CreatedAt"", ""CreatedBy"", ""IsDeleted"")
                VALUES ({id}, {companyId}, {paymentId}, {pay.DocumentId}, {source}, {status}, {fullyPaid}, 0, {now}, {now}, {by}, false)
                ON CONFLICT (""CompanyId"", ""PaymentId"") DO NOTHING", ct);
            return n > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // การชำระ commit แล้ว — ห้ามทำให้คำตอบการชำระล้ม · Error log (ไม่มีแถวงาน = ไม่มีอีเมลอัตโนมัติของการชำระนี้ — ส่งเองจากหน้าเอกสารได้)
            _logger.LogError(ex, "สร้างงานส่งเอกสารหลังรับชำระไม่สำเร็จ payment {Payment} บริษัท {Company}", paymentId, companyId);
            return false;
        }
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lease = now.AddMinutes(10);   // เครื่องที่จองแล้วล้มกลางทาง ⇒ แถวกลับมาให้เครื่องอื่นหลังหมดสัญญาเช่า
        var pending = PostPaymentAutomationPolicy.StatusPending;
        var claimed = await _db.Database.SqlQuery<Guid>($@"
            WITH claimed AS (
                SELECT ""Id"" FROM ""PostPaymentAutomationRuns""
                WHERE NOT ""IsDeleted"" AND ""Status"" = {pending} AND ""NextAttemptAt"" <= {now}
                ORDER BY ""NextAttemptAt""
                LIMIT 20
                FOR UPDATE SKIP LOCKED
            )
            UPDATE ""PostPaymentAutomationRuns"" r
            SET ""NextAttemptAt"" = {lease}, ""UpdatedAt"" = {now}
            FROM claimed
            WHERE r.""Id"" = claimed.""Id""
            RETURNING r.""Id""").ToListAsync(ct);
        var done = 0;
        foreach (var id in claimed)
        {
            // แต่ละแถวเริ่มจาก change tracker ว่าง — แถวหนึ่งพังต้องไม่ลากแถวถัดไป
            _db.ChangeTracker.Clear();
            try { await ProcessRunAsync(id, ct); done++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ประมวลผลงานหลังรับชำระ {Run} ไม่สำเร็จ — จะลองใหม่หลังหมดสัญญาเช่า", id);
            }
        }
        return done;
    }

    public async Task RetryAsync(Guid companyId, Guid runId, CancellationToken ct = default)
    {
        var run = await _db.PostPaymentAutomationRuns.FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบงานส่งเอกสาร");
        if (run.Status is not (PostPaymentAutomationPolicy.StatusBlocked or PostPaymentAutomationPolicy.StatusFailed))
            throw new BusinessRuleException("ลองใหม่ได้เฉพาะงานที่หยุดหรือส่งไม่สำเร็จ", PostPaymentAutomationPolicy.RuleCode);
        run.Status = PostPaymentAutomationPolicy.StatusPending;
        run.Attempts = 0;
        run.NextAttemptAt = DateTime.UtcNow;
        run.CompletedAt = null;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    internal async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.PostPaymentAutomationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null || run.Status != PostPaymentAutomationPolicy.StatusPending) return;
        run.Attempts++;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);   // นับครั้งก่อนทำงาน — เส้นออกใบเสร็จมีธุรกรรมของตัวเอง

        string? docNo = null;
        try
        {
            var r = await ExecuteAsync(run, ct);
            docNo = r.DocNumber;
            // ขั้นออกใบเสร็จอาจล้าง change tracker (ด่านเส้นเดิมปฏิเสธ) ⇒ ใช้แถวที่ติดตามอยู่จริง ณ ตอนนี้
            run = await ReloadAsync(runId, ct);
            if (r.ReceiptId != null) run.ReceiptDocumentId = r.ReceiptId;
            run.Status = r.Status;
            run.Message = Trim(r.Message);
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (r.Status == PostPaymentAutomationPolicy.StatusBlocked)
                await NotifyAsync(run, docNo, r.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            run = await _db.PostPaymentAutomationRuns.FirstAsync(x => x.Id == runId, ct);
            var final = run.Attempts >= PostPaymentAutomationPolicy.MaxAttempts;
            run.Status = final ? PostPaymentAutomationPolicy.StatusFailed : PostPaymentAutomationPolicy.StatusPending;
            run.NextAttemptAt = DateTime.UtcNow.Add(PostPaymentAutomationPolicy.RetryDelay(run.Attempts));
            run.Message = Trim((final ? "ส่งไม่สำเร็จครบ " + run.Attempts + " ครั้ง: " : "ส่งไม่สำเร็จ (จะลองใหม่อัตโนมัติ): ") + ex.Message);
            run.CompletedAt = final ? DateTime.UtcNow : null;
            run.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "งานหลังรับชำระ {Run} ล้ม ครั้งที่ {N}", run.Id, run.Attempts);
            if (final) await NotifyAsync(run, docNo, run.Message, ct);
        }
    }

    private static string? Trim(string? s) => s == null ? null : (s.Length > 2000 ? s[..2000] : s);

    private static bool Done(PostPaymentAutomationRun run, PostPaymentAutomationPolicy.Step step)
        => (run.StepsDone ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(step.ToString());

    private async Task<PostPaymentAutomationRun> MarkDoneAsync(Guid runId, PostPaymentAutomationPolicy.Step step, Guid? receiptId, CancellationToken ct)
    {
        var run = await ReloadAsync(runId, ct);
        run.StepsDone = string.IsNullOrEmpty(run.StepsDone) ? step.ToString() : run.StepsDone + "," + step;
        if (receiptId != null) run.ReceiptDocumentId = receiptId;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);   // ขั้นที่สำเร็จถูกจำทันที — ลองใหม่ไม่ส่งซ้ำ
        return run;
    }

    private async Task<PostPaymentAutomationRun> ReloadAsync(Guid runId, CancellationToken ct)
        => _db.PostPaymentAutomationRuns.Local.FirstOrDefault(r => r.Id == runId)
           ?? await _db.PostPaymentAutomationRuns.FirstAsync(r => r.Id == runId, ct);

    private sealed record RunResult(string Status, string? Message, string? DocNumber, Guid? ReceiptId);

    private async Task<RunResult> ExecuteAsync(PostPaymentAutomationRun run, CancellationToken ct)
    {
        var cid = run.CompanyId;
        var payment = await _db.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == run.PaymentId && p.CompanyId == cid, ct);
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == run.DocumentId && d.CompanyId == cid && !d.IsDeleted, ct);
        if (payment == null || doc == null)
            return new RunResult(PostPaymentAutomationPolicy.StatusSkipped, "ไม่พบการชำระหรือเอกสารต้นทาง (ถูกลบแล้ว)", doc?.DocumentNumber, null);

        var relatedType = doc.RelatedDocumentId is Guid rel
            ? await _db.Documents.AsNoTracking().Where(r => r.Id == rel && r.CompanyId == cid).Select(r => (DocumentType?)r.DocumentType).FirstOrDefaultAsync(ct)
            : null;
        var saleSide = DocumentPayLinkPolicy.IsSaleSide(doc.DocumentType, relatedType);
        var settings = await SettingsForAsync(cid, doc.AutoSendRuleId, ct);
        var contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == cid, ct);

        // ใบเสร็จของการชำระนี้ (สองทาง เหมือนเส้นออกใบย้อนหลัง — ข้อมูลเก่าอาจไม่มี FK บน payment)
        var receipt = await _db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == cid && !r.IsDeleted && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected
                && (r.Id == payment.ReceiptDocumentId || r.SettlementPaymentId == payment.Id))
            .Select(r => new { r.Id, r.Status, r.IsTaxInvoiceByLaw, r.VatAmount })
            .FirstOrDefaultAsync(ct);
        var allocations = await _db.PaymentAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == payment.Id && a.CompanyId == cid && !a.IsDeleted, ct);
        var paymentCount = await _db.Payments.AsNoTracking()
            .CountAsync(p => p.DocumentId == doc.Id && p.CompanyId == cid && !p.IsDeleted, ct);
        var fullyPaid = run.FullyPaid;   // ฝ่ายค้าน N: ค่า ณ ตอนเข้าคิว

        var servesAsReceipt = false;
        if (saleSide && receipt == null && settings.EmailReceipt)
        {
            // ตัวตัดสินเดียวกับหัวกระดาษ/เส้นออกใบย้อนหลัง (ServedAsReceipt จาก GetDocumentAsync + ReceiptIssuePolicy.CoversPayment) — ไม่เขียนเกณฑ์ใหม่
            Models.DTOs.Document.DocumentResponse? resp = await _docs.GetDocumentAsync(cid, doc.Id);
            servesAsReceipt = ReceiptIssuePolicy.CoversPayment(resp?.ServedAsReceipt == true, doc.DocumentDate, payment.PaymentDate);
        }
        // ประมาณแบบระวัง (ไม่รู้ว่ามีใบเสร็จถือ VAT ที่ยังมีผลไหม ⇒ ถือว่าอาจถือ VAT) — ขาดข้อมูลผู้ซื้อ = ไม่ออกเอง แล้วบอกคน
        var mayCarryVat = SettlementReceiptPolicy.CarriesTaxInvoiceRole(doc.DocumentType, doc.VatAmount,
            fullyPaid && paymentCount == 1, liveVatReceiptExists: false);
        var buyerBlock = Tax.TaxInvoiceCompletenessChecker.BuyerBlockingFields(true, doc.VatAmount, contact,
            isDeferredVatDeposit: false, buyerDeclinedTaxInvoice: doc.BuyerDeclinedTaxInvoice);

        // จุดต่อ WHT ขั้นต่ำ: แถวรอ 50 ทวิ ในทะเบียนเดิม (สร้างโดยเส้นบันทึกรับชำระ ยึด GL) — ตรวจอย่างเดียว ไม่สร้างสำเนา
        var whtRegistered = payment.WithholdingTaxAmount <= 0.005m || await _db.WhtCreditsReceived.AsNoTracking()
            .AnyAsync(w => w.CompanyId == cid && !w.IsDeleted && (w.PaymentId == payment.Id || w.DocumentId == doc.Id), ct);

        var plan = PostPaymentAutomationPolicy.Decide(settings, new PostPaymentAutomationPolicy.Facts(
            SaleSide: saleSide, SourceStatus: doc.Status, PaymentVoided: payment.IsDeleted,
            FullyPaid: fullyPaid, AllocationCount: allocations,
            HasReceipt: receipt != null,
            ReceiptIsDraft: receipt != null && receipt.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval,
            ReceiptCarriesVat: receipt != null && (receipt.IsTaxInvoiceByLaw == true || receipt.VatAmount > 0m),
            SourceServesAsReceipt: servesAsReceipt, MayCarryVatIfIssued: mayCarryVat,
            CustomerEmail: contact?.Email?.Trim(), BuyerBlockingFields: buyerBlock,
            PaymentWht: payment.WithholdingTaxAmount, WhtCreditRegistered: whtRegistered));
        if (plan.Skipped != null)
            return new RunResult(PostPaymentAutomationPolicy.StatusSkipped, plan.Skipped, doc.DocumentNumber, receipt?.Id);

        var done = new List<string>();
        var blocked = new List<string>(plan.Blocked);
        var receiptId = receipt?.Id ?? run.ReceiptDocumentId;
        var to = contact?.Email?.Trim();
        var halt = false;
        foreach (var step in plan.Steps)
        {
            if (halt) break;
            if (Done(run, step)) continue;
            switch (step)
            {
                case PostPaymentAutomationPolicy.Step.IssueReceipt:
                    try
                    {
                        // เส้นเดิมเท่านั้น (idempotent · SettlementReceiptPolicy · เลขออกตอนอนุมัติ) · ผู้ออก = ผู้บันทึกการชำระ (สิทธิ์อนุมัติใบเสร็จของเขา)
                        var issued = await _docs.IssueReceiptForPaymentAsync(cid, payment.Id, run.CreatedBy ?? SystemActor);
                        receiptId = issued.ReceiptId;
                        run = await MarkDoneAsync(run.Id, step, receiptId, ct);
                        if (issued.IsDraft)
                        {
                            blocked.Add($"ออกใบเสร็จ {issued.ReceiptNumber} เป็นฉบับร่าง (ผู้บันทึกการชำระไม่มีสิทธิ์อนุมัติใบเสร็จ) — อนุมัติใบเสร็จแล้วส่งอีเมลจากหน้าใบเสร็จ (§86/4 ห้ามส่งเลข DRAFT)");
                            halt = true;
                        }
                        else done.Add("ออกใบเสร็จ " + issued.ReceiptNumber);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // ด่านของเส้นเดิมปฏิเสธพร้อมเหตุผลไทย (ใบต้นทางเป็นใบเสร็จอยู่แล้ว · การชำระกระจาย ฯลฯ) — หยุดแล้วบอกคน
                        _db.ChangeTracker.Clear();
                        run = await ReloadAsync(run.Id, ct);
                        blocked.Add("ออกใบเสร็จอัตโนมัติไม่ได้: " + ex.Message);
                        halt = true;
                    }
                    break;
                case PostPaymentAutomationPolicy.Step.EmailReceipt:
                    if (receiptId is not Guid rid) { blocked.Add("ไม่พบใบเสร็จของการชำระนี้ — ยังไม่ได้ส่งอีเมล"); halt = true; break; }
                    await SendReceiptAsync(cid, rid, payment.Id, to!, ct);
                    run = await MarkDoneAsync(run.Id, step, rid, ct);
                    done.Add("ส่งใบเสร็จถึง " + to);
                    break;
                case PostPaymentAutomationPolicy.Step.EmailSourceAsReceipt:
                    await SendReceiptAsync(cid, doc.Id, payment.Id, to!, ct);
                    receiptId = doc.Id;
                    run = await MarkDoneAsync(run.Id, step, doc.Id, ct);
                    done.Add("ส่งใบกำกับภาษี/ใบเสร็จรับเงิน (ใบต้นทาง) ถึง " + to);
                    break;
            }
        }
        var msg = string.Join(" · ", done.Concat(blocked).Concat(plan.Notes));
        return blocked.Count > 0
            ? new RunResult(PostPaymentAutomationPolicy.StatusBlocked, msg, doc.DocumentNumber, receiptId)
            : new RunResult(PostPaymentAutomationPolicy.StatusDone, msg.Length == 0 ? "ทำครบแล้ว" : msg, doc.DocumentNumber, receiptId);
    }

    /// <summary>ส่งใบเสร็จผ่านเส้นอีเมลเอกสารเดิม (+ บล็อกเพิ่มจาก <see cref="IReceiptEmailSectionProvider"/> ผ่าน <see cref="ReceiptEmailSections.ComposeBody"/> ตัวเดียว) ·
    /// ส่งไม่สำเร็จ (ผู้ให้บริการปฏิเสธ/เกินเพดาน) ⇒ โยน (ผู้เรียกเลื่อนไปลองใหม่ · บันทึก Failed อยู่บนเอกสารแล้ว)</summary>
    private async Task SendReceiptAsync(Guid companyId, Guid documentId, Guid paymentId, string to, CancellationToken ct)
    {
        var sections = new List<ReceiptEmailSections.Section>();
        foreach (var p in _sections) sections.AddRange(await p.SectionsAsync(companyId, documentId, paymentId, ct));
        string? body = null;
        if (sections.Count > 0)
        {
            var doc = await _db.Documents.AsNoTracking().FirstAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            await _db.HydrateContactAsync(companyId, doc);
            var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, documentId);
            var settingsRow = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
            var (plan, _) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senderFactory, companyId, settingsRow, ct);
            var lead = heading.IsEnglish
                ? $"Please find attached {heading.Title} No. {doc.DocumentNumber} for THB {doc.TotalAmount:N2}."
                : $"แนบ{heading.Title} เลขที่ {doc.DocumentNumber} จำนวนเงิน {doc.TotalAmount:N2} บาท";
            body = ReceiptEmailSections.ComposeBody(plan.OnBehalf, DocumentEmailService.ComposeDefaultTemplate(doc, heading, false).HtmlBody, lead, sections);
        }
        var log = await _email.SendDocumentEmailAsync(companyId, documentId,
            new SendDocumentEmailRequest(to, null, null, null, body, AttachPdf: true), SystemActor);
        if (log.Status != EmailLogStatus.Sent)
            throw new InvalidOperationException(log.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่รับข้อความ");
    }

    private async Task NotifyAsync(PostPaymentAutomationRun run, string? docNo, string? message, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(run.CompanyId, NotificationEvents.PostPaymentAutomationBlocked, new NotificationContext
            {
                Title = $"⚠️ ส่งเอกสารอัตโนมัติหลังรับชำระไม่ครบ — {docNo ?? "เอกสาร"}",
                Message = message ?? "",
                ActionUrl = $"/pages/documents.html?id={run.DocumentId}",
                EntityType = nameof(Document),
                EntityId = run.DocumentId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเตือนงานหลังรับชำระ {Run} ไม่สำเร็จ", run.Id);
        }
    }
}
