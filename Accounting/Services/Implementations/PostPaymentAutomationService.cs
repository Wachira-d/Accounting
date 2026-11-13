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
/// อีเมล = <c>IDocumentEmailService.SendDocumentRoutedAsync</c> (รอบ 244: ใบกำกับของบริษัทโหมด e-Tax by Email ไปทาง e-Tax — ด่านปฏิเสธ = หยุด + แจ้งเตือน ·
/// อื่น ๆ = เส้นอีเมลเอกสารเดิม: ผู้ส่งในนามบริษัท + เพดาน + บันทึกการส่งบนเอกสาร) ·
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

    public const string SystemActor = Accounting.Helpers.SystemActor.PostPayment;   // รอบ 259 (B-L4): ตัวตั้งเดียว (เดิม "ระบบ (ส่งอัตโนมัติหลังรับชำระ)" — ป้ายไทยอยู่ที่ SystemActor.Display)

    public PostPaymentAutomationService(AccountingDbContext db, IDocumentService docs, IDocumentEmailService email,
        IEmailSenderFactory senderFactory, ILogger<PostPaymentAutomationService> logger, INotificationEngine? notify = null,
        IEnumerable<IReceiptEmailSectionProvider>? sections = null, IContactRecipientResolver? recipients = null)
    {
        _db = db;
        _docs = docs;
        _email = email;
        _senderFactory = senderFactory;
        _logger = logger;
        _notify = notify;
        _sections = sections ?? Array.Empty<IReceiptEmailSectionProvider>();
        _recipients = recipients ?? ContactRecipientResolver.EmailOnly(db);
    }

    /// <summary>รอบ 250 WP-08 (L1 ต่อสาย · DESIGN §3.g): ผู้รับใบเสร็จ = ตัวหาผู้รับเดียว เรื่อง "ใบเสร็จ" — ไม่อ่าน Contact.Email ตรง (R250L1)</summary>
    private readonly IContactRecipientResolver _recipients;

    /// <summary>ค่าที่มีผลของเอกสารนี้ — ชั้น (เฉพาะเจาะจงชนะ ทีละช่อง): ตารางส่งเอกสารที่ส่งใบนี้ (<c>Document.AutoSendRuleId</c>) ›
    /// รายการประจำที่สร้างใบนี้ (<c>Document.RecurringTransactionId</c> · รอบ 250 B-13) › ผู้ติดต่อของใบ › บริษัท — ตัวต่อชั้นเดียว
    /// <see cref="PostPaymentAutomationPolicy.EffectiveLayers"/> (ไม่คำนวณเอง) · static ⇒ <c>DocumentService.CreatePaymentAsync</c> เรียกด้วย context ของตัวเองได้</summary>
    internal static async Task<PostPaymentAutomationPolicy.Settings> SettingsForAsync(AccountingDbContext db, Guid companyId, Guid documentId, CancellationToken ct)
    {
        var state = await ResolveForAsync(db, companyId, documentId, ct);
        return state.Resolution.Settings;
    }

    /// <summary>ชั้นค่าตั้งของใบ ณ ตอนนี้ (ช่องของใบเอง + ผลของตัวต่อชั้น + ชั้นที่ตัดสิน) — ตัวโหลดเดียวของ <see cref="SettingsForAsync"/>,
    /// หน้าเอกสารของพนักงาน (<c>DocumentPayLinkService.StatusAsync</c>) และ <see cref="SetDocumentLayerAsync"/> ·
    /// รอบ 267 P4 (267-1-8 · 267-1-44): ชั้น "เอกสารใบนี้" บนสุด — ใบ › ตารางส่ง › รายการประจำ › ผู้ติดต่อ › บริษัท</summary>
    internal sealed record DocumentLayerState(bool Found, DocumentType Type, bool? DocEmailReceipt, bool? DocIncludePartial,
        PostPaymentAutomationPolicy.Resolution Resolution);

    internal static async Task<DocumentLayerState> ResolveForAsync(AccountingDbContext db, Guid companyId, Guid documentId, CancellationToken ct)
    {
        var s = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new PostPaymentAutomationPolicy.Settings(x.PostPayEmailReceipt, x.PostPayIncludePartial))
            .FirstOrDefaultAsync(ct) ?? new PostPaymentAutomationPolicy.Settings(false, false);
        var doc = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.AutoSendRuleId, d.RecurringTransactionId, d.ContactId, d.DocumentType, d.PostPayEmailReceipt, d.PostPayIncludePartial })
            .FirstOrDefaultAsync(ct);
        if (doc == null)
            return new DocumentLayerState(false, default, null, null,
                PostPaymentAutomationPolicy.Resolve(s, null, null, null, null));
        PostPaymentAutomationPolicy.Overrides? schedule = null, recurring = null, contact = null;
        if (doc.AutoSendRuleId is Guid rid)
            schedule = await db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.Id == rid && r.CompanyId == companyId && !r.IsDeleted)
                .Select(r => new PostPaymentAutomationPolicy.Overrides(r.PostPayEmailReceipt, r.PostPayIncludePartial))
                .FirstOrDefaultAsync(ct);
        if (doc.RecurringTransactionId is Guid recId)
            recurring = await db.RecurringTransactions.AsNoTracking().IgnoreQueryFilters()
                .Where(r => r.Id == recId && r.CompanyId == companyId)
                .Select(r => new PostPaymentAutomationPolicy.Overrides(r.PostPayEmailReceipt, r.PostPayIncludePartial))
                .FirstOrDefaultAsync(ct);
        // ฝ่ายค้าน WP-08 HIGH-2 (Option 1): "ไม่ส่งอัตโนมัติ" ของผู้ติดต่อ<b>ไม่</b>อยู่ในชั้นค่าตั้งนี้ — ปิดได้แค่ "ส่งอีเมล" ไม่ใช่ "ออกเอกสาร"
        // (ตัดสินใน PostPaymentAutomationPolicy.Decide ด้วย Facts.AutoSendBlockedReason) · ชั้นนี้คงค่า PostPayEmailReceipt ของผู้ติดต่อเท่านั้น
        contact = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == doc.ContactId && c.CompanyId == companyId)
            .Select(c => new PostPaymentAutomationPolicy.Overrides(c.PostPayEmailReceipt, c.PostPayIncludePartial))
            .FirstOrDefaultAsync(ct);
        // ชั้นเอกสาร: อ่านเฉพาะชนิดที่ตั้งได้ (ค่าที่หลงอยู่บนชนิดอื่นไม่มีผล — ตัวเขียนปฏิเสธอยู่แล้ว)
        var document = PostPaymentAutomationPolicy.DocumentLayerAllowed(doc.DocumentType)
            ? new PostPaymentAutomationPolicy.Overrides(doc.PostPayEmailReceipt, doc.PostPayIncludePartial) : null;
        return new DocumentLayerState(true, doc.DocumentType, doc.PostPayEmailReceipt, doc.PostPayIncludePartial,
            PostPaymentAutomationPolicy.Resolve(s, document, schedule, recurring, contact));
    }

    /// <summary>
    /// รอบ 267 P4 (267-1-44): ตั้งค่าต่อใบของ<b>ใบที่ออกแล้ว</b> (ฟอร์มแก้ไขใช้ไม่ได้กับใบที่ออกเลขแล้ว) — ตัวตัดสินเดียวกับฟอร์ม
    /// (<see cref="PostPaymentAutomationPolicy.ApplyDocumentLayer"/>) · มีผลกับการรับชำระครั้งถัดไป · งานที่ยังรอประมวลผล/กดลองใหม่ใช้ค่าปัจจุบัน (ปิดที่ใบ = งานที่ค้างจบพร้อมเหตุ) · เปิดทีหลังไม่ออกใบเสร็จย้อนหลังของการชำระที่ผ่านไปแล้ว ·
    /// ลง audit chain · ไม่แตะเนื้อหา/ยอด/เลขของใบ (ไม่ใช่การแก้ไขเอกสารย้อนหลัง)
    /// </summary>
    public async Task SetDocumentLayerAsync(Guid companyId, Guid documentId, string? receiptMode, string? partialMode, Guid? userId, string actor,
        CancellationToken ct = default)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (doc.Status == DocumentStatus.Voided)
            throw new BusinessRuleException("เอกสารนี้ถูกยกเลิกแล้ว — ไม่มีการรับชำระให้ตั้งค่า", PostPaymentAutomationPolicy.RuleCode, 409);
        var r = PostPaymentAutomationPolicy.ApplyDocumentLayer(doc.DocumentType, doc.PostPayEmailReceipt, doc.PostPayIncludePartial, receiptMode, partialMode);
        if (r.Problem != null) throw new BusinessRuleException(r.Problem, PostPaymentAutomationPolicy.RuleCode, 400);
        if (r.EmailReceipt == doc.PostPayEmailReceipt && r.IncludePartial == doc.PostPayIncludePartial) return;   // ไม่มีอะไรเปลี่ยน = ไม่ลง audit ซ้ำ
        var before = new { postPayEmailReceipt = doc.PostPayEmailReceipt, postPayIncludePartial = doc.PostPayIncludePartial };
        doc.PostPayEmailReceipt = r.EmailReceipt;
        doc.PostPayIncludePartial = r.IncludePartial;
        var now = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, UserEmail = actor, Action = AuditAction.Update,
            EntityType = nameof(Document), EntityId = doc.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "post-pay-document-layer", postPayEmailReceipt = r.EmailReceipt, postPayIncludePartial = r.IncludePartial,
                doc.DocumentNumber, ruleCode = PostPaymentAutomationPolicy.RuleCode,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task<bool> EnqueueAsync(Guid companyId, Guid paymentId, PaymentEntrySource source, string? actor, CancellationToken ct = default)
        => EnqueueCoreAsync(_db, _logger, companyId, paymentId, source, actor, ct);

    /// <summary>รอบ 250 (WP-02 §2.1 · คำตัดสิน §2 #4): แกนสร้างแถวงานหลังรับชำระ — ผู้เรียกจริงคือ <c>DocumentService.CreatePaymentAsync</c> หลัง commit
    /// (ทุกทางเข้า 1:1) และ <c>DocumentService.AfterPaymentRecordedAsync</c> (integration) · ตัดสินด้วย <see cref="PostPaymentAutomationPolicy.ForEnqueue"/> ตัวเดียว ·
    /// ทางเข้าที่มีเส้นออกใบเสร็จของตัวเอง ⇒ แถว Skipped พร้อมเหตุ (หน้าเอกสารเห็น) · ไม่โยน (การชำระ commit แล้ว — Error log)</summary>
    internal static async Task<bool> EnqueueCoreAsync(AccountingDbContext db, ILogger logger, Guid companyId, Guid paymentId,
        PaymentEntrySource source, string? actor, CancellationToken ct)
    {
        try
        {
            // รอบ 267 P4 ฝ่ายค้าน LOW-3: ใบที่มีผลของการรับชำระ = (MovedToDocumentId ?? DocumentId) — PaymentEffectiveDocument (รอบ 266 B)
            var payDoc = await db.Payments.AsNoTracking()
                .Where(p => p.Id == paymentId && p.CompanyId == companyId)
                .Select(p => (Guid?)(p.MovedToDocumentId ?? p.DocumentId))
                .FirstOrDefaultAsync(ct);
            if (payDoc is not Guid effectiveDocId) return false;
            var pay = await db.Documents.AsNoTracking()
                .Where(d => d.Id == effectiveDocId && d.CompanyId == companyId)
                .Select(d => new { DocumentId = d.Id, d.BalanceDue })
                .FirstOrDefaultAsync(ct);
            if (pay == null) return false;
            // ฝ่ายค้าน N: "ชำระครบ" ตัดสิน ณ เหตุการณ์ชำระนี้ (หลัง commit ของการชำระ) — ไม่ใช่ตอนงานรัน (งวดถัดไปอาจเข้ามาก่อน)
            var fullyPaid = pay.BalanceDue <= 0.005m;
            var settings = await SettingsForAsync(db, companyId, pay.DocumentId, ct);
            var decision = PostPaymentAutomationPolicy.ForEnqueue(settings, source);
            if (decision.Kind == PostPaymentAutomationPolicy.EnqueueKind.None) return false;   // ปิด/หลายใบ ⇒ ไม่สร้างงาน (พฤติกรรมเดิม)

            var now = DateTime.UtcNow;
            var id = Guid.NewGuid();
            var by = string.IsNullOrWhiteSpace(actor) ? SystemActor : actor;
            var skipped = decision.Kind == PostPaymentAutomationPolicy.EnqueueKind.Skipped;
            var status = skipped ? PostPaymentAutomationPolicy.StatusSkipped : PostPaymentAutomationPolicy.StatusPending;
            var message = decision.Reason ?? "";
            var sourceName = source.ToString();
            // กันซ้ำที่ฐาน (unique CompanyId+PaymentId) — webhook/poll/กดซ้ำพร้อมกันได้แถวเดียว · แถว Skipped ปิดงานทันที (CompletedAt) พร้อมเหตุ
            var n = skipped
                ? await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""PostPaymentAutomationRuns""
                    (""Id"", ""CompanyId"", ""PaymentId"", ""DocumentId"", ""Source"", ""Status"", ""FullyPaid"", ""Attempts"", ""NextAttemptAt"", ""Message"", ""CompletedAt"", ""CreatedAt"", ""CreatedBy"", ""IsDeleted"")
                VALUES ({id}, {companyId}, {paymentId}, {pay.DocumentId}, {sourceName}, {status}, {fullyPaid}, 0, {now}, {message}, {now}, {now}, {by}, false)
                ON CONFLICT (""CompanyId"", ""PaymentId"") DO NOTHING", ct)
                : await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""PostPaymentAutomationRuns""
                    (""Id"", ""CompanyId"", ""PaymentId"", ""DocumentId"", ""Source"", ""Status"", ""FullyPaid"", ""Attempts"", ""NextAttemptAt"", ""CreatedAt"", ""CreatedBy"", ""IsDeleted"")
                VALUES ({id}, {companyId}, {paymentId}, {pay.DocumentId}, {sourceName}, {status}, {fullyPaid}, 0, {now}, {now}, {by}, false)
                ON CONFLICT (""CompanyId"", ""PaymentId"") DO NOTHING", ct);
            return n > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // การชำระ commit แล้ว — ห้ามทำให้คำตอบการชำระล้ม · Error log (ไม่มีแถวงาน = ไม่มีอีเมลอัตโนมัติของการชำระนี้ — ส่งเองจากหน้าเอกสารได้)
            logger.LogError(ex, "สร้างงานส่งเอกสารหลังรับชำระไม่สำเร็จ payment {Payment} บริษัท {Company}", paymentId, companyId);
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

    /// <summary>ฝ่ายค้าน 267-P5 MED-5: ขั้นย่อยต่อใบเสร็จ (ใบเสร็จรายใบของการชำระหลายใบ) — จำใน StepsDone ด้วยกุญแจ "{ขั้น}:{id}"</summary>
    private static bool DoneToken(PostPaymentAutomationRun run, string token)
        => (run.StepsDone ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(token);

    private async Task<PostPaymentAutomationRun> MarkTokenAsync(Guid runId, string token, CancellationToken ct)
    {
        var run = await ReloadAsync(runId, ct);
        run.StepsDone = string.IsNullOrEmpty(run.StepsDone) ? token : run.StepsDone + "," + token;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return run;
    }

    private sealed record AllocationReceipt(Guid Id, bool Draft, bool CarriesVat);

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
        // รอบ 266 ฝ่ายค้าน B-6: ตาข่ายของงานที่ถูกจองไว้ก่อนการย้าย (เส้นย้ายตั้งแถวที่ค้างเป็น Skipped ในธุรกรรมเดียวกันแล้ว) — เงินก้อนนี้ไม่ได้ตัดใบนี้แล้ว
        // ⇒ ไม่ส่งขอบคุณ/ใบเสร็จอ้างใบเดิม (266-1-30)
        if (payment.MovedToDocumentId is Guid movedTo && movedTo != run.DocumentId)
            return new RunResult(PostPaymentAutomationPolicy.StatusSkipped, Accounting.Helpers.ReceiptLinkPolicy.MoveSkippedAutomationMessage, doc.DocumentNumber, null);

        var relatedType = doc.RelatedDocumentId is Guid rel
            ? await _db.Documents.AsNoTracking().Where(r => r.Id == rel && r.CompanyId == cid).Select(r => (DocumentType?)r.DocumentType).FirstOrDefaultAsync(ct)
            : null;
        var contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == cid, ct);
        // ฝ่ายค้าน WP-02 ข้อ 3: ฝั่งของใบเพิ่มหนี้จากตัวตัดสินเดียวกับ JE รับชำระ (ผู้ใช้สั่งย้ายฝั่ง › ใบต้นทาง › ผู้ขายอย่างเดียว) — ห้ามส่งใบเสร็จให้ผู้ขาย
        var saleSide = DocumentPayLinkPolicy.IsSaleSide(doc.DocumentType, relatedType, doc.CnDnPurchaseSideOverride,
            contact is { IsSupplier: true, IsCustomer: false });
        var settings = await SettingsForAsync(_db, cid, doc.Id, ct);
        // รอบ 250 (WP-02 §2.3): ใบแจ้งหนี้ที่มีใบกำกับภาษีอ้างและยังมีผล — ตัวโหลดเดียวกับลิงก์ชำระ/QR (PaymentQrService.SupersedingTaxInvoiceNumberAsync)
        var supersedingTaxInvoice = await PaymentQrService.SupersedingTaxInvoiceNumberAsync(_db, cid, doc, ct);

        // ใบเสร็จของการชำระนี้ (สองทาง เหมือนเส้นออกใบย้อนหลัง — ข้อมูลเก่าอาจไม่มี FK บน payment)
        var receipt = await _db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == cid && !r.IsDeleted && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected
                && (r.Id == payment.ReceiptDocumentId || r.SettlementPaymentId == payment.Id))
            .Select(r => new { r.Id, r.Status, r.IsTaxInvoiceByLaw, r.VatAmount })
            .FirstOrDefaultAsync(ct);
        var allocations = await _db.PaymentAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == payment.Id && a.CompanyId == cid && !a.IsDeleted, ct);
        // ฝ่ายค้าน 267-P5 MED-5: ใบเสร็จรายใบของการชำระหลายใบ (เส้นหลายใบออกไว้ตอนบันทึก — TryAutoIssueAllocationReceiptAsync) — ส่งทุกใบ ไม่ออกเอง
        var allocationReceipts = allocations == 0 ? new List<AllocationReceipt>()
            : await _db.Documents.AsNoTracking()
                .Where(r => r.CompanyId == cid && !r.IsDeleted && r.SettlementPaymentId == payment.Id && r.IsSettlementReceipt
                    && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected)
                .OrderBy(r => r.DocumentNumber)
                .Select(r => new AllocationReceipt(r.Id, r.Status == DocumentStatus.Draft || r.Status == DocumentStatus.WaitingApproval,
                    r.IsTaxInvoiceByLaw == true || r.VatAmount > 0m))
                .ToListAsync(ct);
        var paymentCount = await _db.Payments.AsNoTracking()
            .CountAsync(p => (p.MovedToDocumentId ?? p.DocumentId) == doc.Id && p.CompanyId == cid && !p.IsDeleted, ct);   // ฝ่ายค้าน LOW-3 (PaymentEffectiveDocument)
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
        // รอบ 250 WP-08: ผู้รับเรื่อง "ใบเสร็จ" จากตัวหาผู้รับเดียว (ปลายทางของผู้ติดต่อ · ไม่ตั้ง = Contact.Email เดิม)
        var receiptSet = await _recipients.ResolveAsync(cid, doc.ContactId, NotifyTopic.Receipt, ct);
        var receiptDelivery = ContactRecipientRule.ForEmail(receiptSet);
        // รอบ 258-3 (F-H1 · A-M4): ปลายทาง LINE ที่ติ๊ก "ใบเสร็จ" (ค่าตั้งต้นของผู้ใช้ LINE ติ๊กให้แล้ว — NotifyTopics.DefaultsFor) · ตัวกรองเดียว ContactLineChannel.LineEndpoints
        var lineEndpoints = ContactLineChannel.LineEndpoints(receiptSet.Options);
        var buyerBlock = Tax.TaxInvoiceCompletenessChecker.BuyerBlockingFields(true, doc.VatAmount, contact,
            isDeferredVatDeposit: false, buyerDeclinedTaxInvoice: doc.BuyerDeclinedTaxInvoice);

        // จุดต่อ WHT ขั้นต่ำ: แถวรอ 50 ทวิ ในทะเบียนเดิม (สร้างโดยเส้นบันทึกรับชำระ ยึด GL) — ตรวจอย่างเดียว ไม่สร้างสำเนา
        var whtRegistered = payment.WithholdingTaxAmount <= 0.005m || await _db.WhtCreditsReceived.AsNoTracking()
            .AnyAsync(w => w.CompanyId == cid && !w.IsDeleted && (w.PaymentId == payment.Id || w.DocumentId == doc.Id), ct);

        // รอบ 258-3 ฝ่ายค้าน M2: บริษัทโหมด e-Tax by Email + ใบที่จะส่งอาจเป็นใบกำกับ (ประมาณแบบระวังเหมือน mayCarryVat) ⇒ อีเมล e-Tax ก่อน · LINE อย่างเดียวไม่พอ
        var etaxCfg = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == cid)
            .Select(x => new { x.EtaxEnabled, x.EtaxMode }).FirstOrDefaultAsync(ct);
        var etaxEmailRequired = etaxCfg != null && EtaxDeliveryRouter.ByEmailActive(etaxCfg.EtaxEnabled, etaxCfg.EtaxMode)
            && (allocationReceipts.Count > 0 ? allocationReceipts.Any(r => r.CarriesVat)
                : receipt != null ? (receipt.IsTaxInvoiceByLaw == true || receipt.VatAmount > 0m)
                : servesAsReceipt ? doc.VatAmount > 0m : mayCarryVat);
        var plan = PostPaymentAutomationPolicy.Decide(settings, new PostPaymentAutomationPolicy.Facts(
            SaleSide: saleSide, SourceStatus: doc.Status, PaymentVoided: payment.IsDeleted,
            FullyPaid: fullyPaid, AllocationCount: allocations,
            HasReceipt: receipt != null,
            ReceiptIsDraft: receipt != null && receipt.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval,
            ReceiptCarriesVat: receipt != null && (receipt.IsTaxInvoiceByLaw == true || receipt.VatAmount > 0m),
            SourceServesAsReceipt: servesAsReceipt, MayCarryVatIfIssued: mayCarryVat,
            CustomerEmail: receiptDelivery.First, BuyerBlockingFields: buyerBlock,
            PaymentWht: payment.WithholdingTaxAmount, WhtCreditRegistered: whtRegistered,
            Source: PostPaymentAutomationPolicy.ParseSource(run.Source), SupersedingTaxInvoiceNumber: supersedingTaxInvoice,
            EmailOptOutReason: receiptDelivery.OptOutReason,
            // ฝ่ายค้าน HIGH-2/MED-4: ไม่ส่งอัตโนมัติชนะทุกชั้น (รวมตาราง/รายการประจำที่ตั้ง On) แต่ห้ามแค่ขั้นส่งอีเมล — ออกใบเสร็จยังเดินตามค่าตั้ง
            AutoSendBlockedReason: AutopilotSettingsResolver.AutoSendBlock(contact?.AutoSendDisabled == true),
            LineReceiptEndpoints: lineEndpoints.Count,
            EtaxEmailRequired: etaxEmailRequired,
            AllocationReceiptsReady: allocationReceipts.Count,
            AllocationReceiptDraft: allocationReceipts.Any(r => r.Draft),
            AllocationReceiptCarriesVat: allocationReceipts.Any(r => r.CarriesVat)));
        if (plan.Skipped != null)
            return new RunResult(PostPaymentAutomationPolicy.StatusSkipped, plan.Skipped, doc.DocumentNumber, receipt?.Id);

        var done = new List<string>();
        var blocked = new List<string>(plan.Blocked);
        var receiptId = receipt?.Id ?? run.ReceiptDocumentId;
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
                case PostPaymentAutomationPolicy.Step.EmailReceipt when allocationReceipts.Count > 0:
                    // ใบเสร็จรายใบ — จำทีละใบ (ลองใหม่ไม่ส่งซ้ำใบที่ส่งแล้ว)
                    foreach (var ar in allocationReceipts)
                    {
                        var token = $"{step}:{ar.Id:N}";
                        if (DoneToken(run, token)) continue;
                        var sentA = await SendReceiptAsync(cid, ar.Id, payment.Id, receiptDelivery, ct);
                        if (sentA.Refusal is { } etaxBlockA) { blocked.Add(etaxBlockA); halt = true; break; }
                        run = await MarkTokenAsync(run.Id, token, ct);
                        done.Add("ส่งใบเสร็จถึง " + sentA.To);
                    }
                    if (!halt) run = await MarkDoneAsync(run.Id, step, receiptId, ct);
                    break;
                case PostPaymentAutomationPolicy.Step.LineReceipt when allocationReceipts.Count > 0:
                    var queuedAll = 0;
                    foreach (var ar in allocationReceipts)
                        queuedAll += await EnqueueReceiptLineAsync(cid, ar.Id, doc.ContactId, lineEndpoints, ct,
                            customerInitiated: PostPaymentAutomationPolicy.ParseSource(run.Source) == PaymentEntrySource.Gateway);
                    run = await MarkDoneAsync(run.Id, step, receiptId, ct);
                    done.Add(queuedAll > 0 ? $"เข้าคิวส่งใบเสร็จ {allocationReceipts.Count} ใบทาง LINE (สถานะการส่งดูที่ ตารางส่ง › คิว LINE)" : "ใบเสร็จเหล่านี้เข้าคิว LINE ไปแล้ว (ไม่ส่งซ้ำ)");
                    break;
                case PostPaymentAutomationPolicy.Step.EmailReceipt:
                    if (receiptId is not Guid rid) { blocked.Add("ไม่พบใบเสร็จของการชำระนี้ — ยังไม่ได้ส่งอีเมล"); halt = true; break; }
                    var sentR = await SendReceiptAsync(cid, rid, payment.Id, receiptDelivery, ct);
                    if (sentR.Refusal is { } etaxBlockR)
                    {
                        // รอบ 244: ใบนี้ต้องไปทาง e-Tax by Email แต่ด่านปฏิเสธ ⇒ หยุด + แจ้งคน (ลองใหม่อัตโนมัติไม่ช่วย — ต้องแก้ค่าตั้ง) · ห้ามส่งเป็นอีเมลธรรมดาแทน
                        blocked.Add(etaxBlockR);
                        halt = true;
                        break;
                    }
                    run = await MarkDoneAsync(run.Id, step, rid, ct);
                    done.Add("ส่งใบเสร็จถึง " + sentR.To);
                    if (sentR.Note != null) done.Add(sentR.Note);
                    break;
                case PostPaymentAutomationPolicy.Step.LineReceipt:
                    // ใบเสร็จของการชำระนี้ (ออกในขั้นก่อน/มีอยู่แล้ว) หรือใบต้นทางที่เป็นใบเสร็จอยู่แล้ว
                    var lineTarget = receiptId ?? (servesAsReceipt ? doc.Id : (Guid?)null);
                    if (lineTarget is not Guid lt) { blocked.Add("ไม่พบใบเสร็จของการชำระนี้ — ยังไม่ได้ส่งทาง LINE"); break; }
                    // รอบ 260-4 (ฝ่ายค้าน L4): ชั่วโมงเงียบยกเว้นเฉพาะการชำระที่ลูกค้าทำเอง (ลิงก์/ผู้ให้บริการรับชำระ)
                    var queuedLine = await EnqueueReceiptLineAsync(cid, lt, doc.ContactId, lineEndpoints, ct,
                        customerInitiated: PostPaymentAutomationPolicy.ParseSource(run.Source) == PaymentEntrySource.Gateway);
                    run = await MarkDoneAsync(run.Id, step, receiptId, ct);
                    done.Add(queuedLine > 0 ? $"เข้าคิวส่งใบเสร็จทาง LINE {queuedLine} ปลายทาง (สถานะการส่งดูที่ ตารางส่ง › คิว LINE)" : "ใบเสร็จนี้เข้าคิว LINE ไปแล้ว (ไม่ส่งซ้ำ)");
                    break;
                case PostPaymentAutomationPolicy.Step.EmailSourceAsReceipt:
                    var sentS = await SendReceiptAsync(cid, doc.Id, payment.Id, receiptDelivery, ct);
                    if (sentS.Refusal is { } etaxBlockS)
                    {
                        blocked.Add(etaxBlockS);
                        halt = true;
                        break;
                    }
                    receiptId = doc.Id;
                    run = await MarkDoneAsync(run.Id, step, doc.Id, ct);
                    done.Add("ส่งใบกำกับภาษี/ใบเสร็จรับเงิน (ใบต้นทาง) ถึง " + sentS.To);
                    if (sentS.Note != null) done.Add(sentS.Note);
                    break;
            }
        }
        var msg = string.Join(" · ", done.Concat(blocked).Concat(plan.Notes));
        return blocked.Count > 0
            ? new RunResult(PostPaymentAutomationPolicy.StatusBlocked, msg, doc.DocumentNumber, receiptId)
            : new RunResult(PostPaymentAutomationPolicy.StatusDone, msg.Length == 0 ? "ทำครบแล้ว" : msg, doc.DocumentNumber, receiptId);
    }

    /// <summary>รอบ 258-3 (F-H1): เข้าคิวการ์ด LINE ของใบเสร็จ — หนึ่งแถวต่อปลายทาง (กุญแจ <see cref="ContactLineChannel.ReceiptBaseKey"/> +
    /// <see cref="ContactLineChannel.QueueKey"/> ⇒ เอกสาร+ปลายทางเดียวกันไม่เข้าคิวซ้ำ แม้งานลองใหม่) · ไม่ถอยไปอีเมล (ขั้นอีเมลของงานนี้ตัดสินอีเมลเอง) ·
    /// ผู้ส่ง = <see cref="ContactLineQueueService"/> ตัวเดิม (ตรวจปลายทาง/ความยินยอมซ้ำตอนส่ง · ล้ม = สถานะ+เหตุบนแถวคิว + แถว Autopilot "LINE ส่งไม่ออก")</summary>
    private async Task<int> EnqueueReceiptLineAsync(Guid companyId, Guid receiptId, Guid contactId,
        IReadOnlyList<ContactRecipientRule.RecipientOption> endpoints, CancellationToken ct, bool customerInitiated = false)
    {
        var baseKey = ContactLineChannel.ReceiptBaseKey(receiptId);
        var existing = await _db.LineQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && q.BaseKey == baseKey && !q.IsDeleted)
            .Select(q => q.IdempotencyKey).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var ep in endpoints)
        {
            var key = ContactLineChannel.QueueKey(baseKey, ep.EndpointId);
            if (existing.Contains(key)) continue;
            _db.LineQueues.Add(new LineQueue
            {
                CompanyId = companyId, RuleId = null, EntityType = "Document", EntityId = receiptId, ContactId = contactId,
                EndpointId = ep.EndpointId, EndpointKind = ep.Kind, Topic = NotifyTopic.Receipt.ToString(), Trigger = ContactLineChannel.ReceiptTrigger,
                ScheduledFor = now, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(companyId, key),
                IdempotencyKey = key, BaseKey = baseKey, FallbackToEmail = false, CreatedBy = SystemActor, CustomerInitiated = customerInitiated,
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>ส่งใบเสร็จผ่านทางเข้าเดียวของอีเมลเอกสาร (<c>SendDocumentRoutedAsync</c> — รอบ 244: ใบกำกับของบริษัทโหมด e-Tax by Email ไปทาง e-Tax) ·
    /// บล็อกเพิ่มจาก <see cref="IReceiptEmailSectionProvider"/> ผ่าน <see cref="ReceiptEmailSections.ComposeBody"/> ตัวเดียว ·
    /// คืนเหตุ (ไม่ null) = ทาง e-Tax by Email ปฏิเสธ (ค่าตั้งผู้ส่ง/รูปแบบ/สถานะ — ลองใหม่ไม่ช่วย ⇒ ผู้เรียกหยุดแล้วแจ้งคน · แถว Failed อยู่บนเอกสารแล้ว) ·
    /// ส่งไม่สำเร็จ (ผู้ให้บริการปฏิเสธ/เกินเพดาน) ⇒ โยน (ผู้เรียกเลื่อนไปลองใหม่ · บันทึก Failed อยู่บนเอกสารแล้ว)</summary>
    private sealed record SendOutcome(string? Refusal, string? To, string? Note);

    private async Task<SendOutcome> SendReceiptAsync(Guid companyId, Guid documentId, Guid paymentId,
        ContactRecipientRule.EmailDelivery delivery, CancellationToken ct)
    {
        // ฝ่ายค้าน HIGH-1: ใบที่ไปทาง e-Tax by Email ⇒ ถึงอีเมลหลักเท่านั้น (ไม่มีสำเนา) · อีเมลเพิ่มเติมบันทึกว่าไม่ได้ส่ง · อีเมลหลักไม่รับ ⇒ ไม่ส่งพร้อมเหตุ (ตัวตัดสิน ForChannel)
        var etaxDoc = (await DocumentEmailService.RouteForAsync(_db, companyId, documentId, ct)).Channel == EtaxEmailChannel.EtaxByEmail;
        var channel = ContactRecipientRule.ForChannel(delivery, etaxDoc);
        var prefix = etaxDoc ? "ส่ง e-Tax by Email ไม่ได้ (ยังไม่ส่งอีเมล): " : "ส่งอีเมลไม่ได้ (ยังไม่ส่ง): ";
        if (channel.To == null) return new SendOutcome(prefix + (channel.Problem ?? "ไม่มีอีเมลผู้รับ"), null, null);
        // ฝ่ายค้าน MED-3: เพดานผู้รับของอีเมลผ่าน NextAcc + ความยาวช่องบันทึก ⇒ ตัดก่อนส่งพร้อมหมายเหตุ
        var settingsForCap = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        var onBehalf = (await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senderFactory, companyId, settingsForCap, ct)).Plan.OnBehalf;
        var (to, capNote) = ContactRecipientRule.CapForSender(channel.To, onBehalf && !etaxDoc, 0, SignEmailThrottle.MaxRecipients, EmailScheduleService.MaxToEmailLength);
        var note = EmailScheduleService.JoinNotes(channel.Note, capNote);
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
            // ฝ่ายค้านรอบ 244 N4: ใบที่ไปทาง e-Tax by Email ส่งจากอีเมลของบริษัทเสมอ ⇒ ห้ามมีข้อความ "ในนาม NextAcc" · เนื้อแบบ e-Tax (หมายเหตุ ETDA)
            var etaxRoute = (await DocumentEmailService.RouteForAsync(_db, companyId, documentId, ct)).Channel == EtaxEmailChannel.EtaxByEmail;
            var lead = heading.IsEnglish
                ? $"Please find attached {heading.Title} No. {doc.DocumentNumber} for THB {doc.TotalAmount:N2}."
                : $"แนบ{heading.Title} เลขที่ {doc.DocumentNumber} จำนวนเงิน {doc.TotalAmount:N2} บาท";
            body = ReceiptEmailSections.ComposeBody(plan.OnBehalf && !etaxRoute,
                DocumentEmailService.ComposeDefaultTemplate(doc, heading, etaxRoute).HtmlBody, lead, sections);
        }
        DocumentEmailLog log;
        try
        {
            log = await _email.SendDocumentRoutedAsync(companyId, documentId,
                new SendDocumentEmailRequest(to, null, null, null, body, AttachPdf: true), SystemActor);
        }
        catch (BusinessRuleException ex) when (ex.RuleCode is { } rc
            && (rc.StartsWith("ETAX-", StringComparison.Ordinal) || rc == "EMAIL-PLATFORM-CAP"))
        {
            // ฝ่ายค้าน MED-3: เพดานอีเมลผ่าน NextAcc ⇒ หยุดพร้อมเหตุ (ลองใหม่อัตโนมัติ 4 ครั้งไม่ช่วย — ผู้ใช้กด "ลองใหม่" หลังแก้/พรุ่งนี้) · บันทึก Failed อยู่บนเอกสารแล้ว
            _db.ChangeTracker.Clear();
            return new SendOutcome(prefix + ex.Message, null, null);
        }
        if (log.Status != EmailLogStatus.Sent)
            throw new InvalidOperationException(log.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่รับข้อความ");
        return new SendOutcome(null, to, note);
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
