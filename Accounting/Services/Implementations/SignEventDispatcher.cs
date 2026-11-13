using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ผู้ส่งเหตุการณ์ "ลูกค้าเซ็น/ยอมรับ" จาก outbox** (รอบ 240 · คำตัดสิน 240-3) — เรียกจากงาน <c>SignEventOutboxJob</c> ใต้ <see cref="JobLock"/> เท่านั้น
/// (ไม่เคยอยู่ในคำขอของลูกค้า ⇒ ปลายทางช้า/ล่ม ไม่กระทบการยอมรับ)
///
/// <para>ต่อแถว: (1) แจ้งผู้ขายครั้งเดียว (<see cref="NotificationEvents.DocumentCustomerSigned"/> ผ่านเครื่องแจ้งเตือนกลาง) · (2) ส่งเข้า webhook ที่บริษัท
/// ลงทะเบียน (<c>IWebhookService.TriggerAsync</c> — ลายเซ็น/ประวัติของระบบ webhook เดิม) ครั้งเดียว · (3) callback ต่อเอกสาร: ด่าน SSRF ตอนส่ง
/// (<see cref="OutboundUrlGuard.CheckResolvedAsync"/> + client ที่ต่อเฉพาะ IP สาธารณะ · ไม่ตาม redirect) → ลงนาม <see cref="WebhookSignature.Sign"/> ด้วยรหัสลับของ
/// บริษัท → 2xx = Delivered · อื่น ๆ = ถอยเวลา <see cref="SignEventOutbox.AfterAttempt"/> (ครบ 8 ครั้ง = Dead · ส่งซ้ำได้จากหน้าเอกสาร)</para>
/// </summary>
public class SignEventDispatcher
{
    public const string HttpClientName = "SignWebhookClient";
    public const int TimeoutSeconds = 10;
    public const int BatchSize = 50;

    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<SignEventDispatcher> _logger;
    private readonly IWebhookService? _webhooks;
    private readonly INotificationEngine? _notify;
    /// <summary>รอบ 267 P2: ขั้น "ฉบับที่ลูกค้าเซ็น + ส่งสำเนาให้ผู้ลงนาม" ของแถวหลักฐาน · null (ประกอบนอก DI) = ข้ามขั้นนี้</summary>
    private readonly CustomerDocumentResponseService? _responses;

    public SignEventDispatcher(AccountingDbContext db, IHttpClientFactory http, ISecretProtector secrets, ILogger<SignEventDispatcher> logger,
        IWebhookService? webhooks = null, INotificationEngine? notify = null, CustomerDocumentResponseService? responses = null)
    {
        _responses = responses;
        _db = db;
        _http = http;
        _secrets = secrets;
        _logger = logger;
        _webhooks = webhooks;
        _notify = notify;
    }

    public sealed record RunResult(int Processed, int Delivered, int Failed, int Dead, int Purged);

    /// <summary>หนึ่งรอบ — แถวที่ถึงเวลา (Pending · NextAttemptAt ≤ now) ทุกบริษัท ทีละ <see cref="BatchSize"/> + ล้าง payload ที่จบเกิน 90 วัน</summary>
    public async Task<RunResult> ProcessDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var rows = await _db.SignEventOutbox
            .Where(e => e.Status == SignEventStatus.Pending && e.NextAttemptAt <= nowUtc)
            .OrderBy(e => e.NextAttemptAt).Take(BatchSize).ToListAsync(ct);
        int delivered = 0, failed = 0, dead = 0;
        foreach (var row in rows)
        {
            // ฝ่ายค้าน S3: ล้มที่แถวไหน = แถวนั้นถอยเวลา/ลองใหม่ — ไม่ล้มทั้งรอบ (แถวถัดไปยังได้ส่ง)
            try
            {
                await ProcessOneAsync(row, nowUtc, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ส่งเหตุการณ์ {Event} {EventId} ล้ม (company {CompanyId}) — ถอยเวลาแล้วลองใหม่", row.EventType, row.EventId, row.CompanyId);
                await MarkAttemptFailedAsync(row, nowUtc, "ข้อผิดพลาดของระบบระหว่างส่ง: " + ex.Message, ct);
            }
            if (row.Status == SignEventStatus.Delivered) delivered++;
            else if (row.Status == SignEventStatus.Dead) dead++;
            else if (row.Status == SignEventStatus.Pending) failed++;
        }

        // PDPA ม.22/ม.37: payload มีชื่อผู้ยอมรับ — จบแล้วเกิน 90 วัน ⇒ ล้าง (คงแถวไว้เป็นประวัติการส่ง)
        var cutoff = nowUtc.AddDays(-SignEventOutbox.PayloadRetentionDays);
        var purged = await _db.SignEventOutbox
            .Where(e => e.Status != SignEventStatus.Pending && e.CreatedAt < cutoff && e.PayloadJson != "{}")
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.PayloadJson, "{}"), ct);
        return new RunResult(rows.Count, delivered, failed, dead, purged);
    }

    /// <summary>ล้มระหว่างประมวลผลแถว — นับครั้ง + ถอยเวลา (ครบ = Dead) แล้วบันทึกแยก (ถ้าบันทึกไม่ได้ รอบหน้าแถวยังเป็น Pending เดิม — ไม่หาย)</summary>
    private async Task MarkAttemptFailedAsync(SignEventOutboxEntry row, DateTime nowUtc, string error, CancellationToken ct)
    {
        row.Attempts++;
        row.LastAttemptAt = nowUtc;
        row.LastError = Trim(error);
        var (next, nextAt) = SignEventOutbox.AfterAttempt(false, row.Attempts, nowUtc);
        row.Status = next;
        row.NextAttemptAt = nextAt;
        row.UpdatedAt = nowUtc;
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "บันทึกผลล้มของเหตุการณ์ {EventId} ไม่ได้", row.EventId);
        }
    }

    /// <summary>ปิดแถวเป็น Dead ทันทีพร้อมเหตุ (ไม่มีอะไรให้ลองซ้ำ: URL ไม่ผ่านกติกาปัจจุบัน · payload ถูกล้าง)</summary>
    private static void MarkDead(SignEventOutboxEntry row, DateTime nowUtc, string reason)
    {
        row.Status = SignEventStatus.Dead;
        row.LastAttemptAt = nowUtc;
        row.LastError = Trim(reason);
        row.UpdatedAt = nowUtc;
    }

    /// <summary>หนึ่งแถว — บันทึกหลังขั้นแจ้งผู้ขาย และหลังขั้น webhook ที่ลงทะเบียน แยกกัน (ขั้นหลังล้มแล้วลองใหม่ ไม่ยิงขั้นก่อนซ้ำ · ฝ่ายค้าน S3) ·
    /// ผลของขั้น callback ผู้เรียก SaveChanges · public ให้ Db test เรียกตรงได้</summary>
    public async Task ProcessOneAsync(SignEventOutboxEntry row, DateTime nowUtc, CancellationToken ct = default)
    {
        if (row.SellerNotifiedAt == null)
        {
            await NotifySellerAsync(row, ct);
            row.SellerNotifiedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
        }
        if (row.RegistrationsDispatchedAt == null)
        {
            if (_webhooks != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(row.PayloadJson);
                    await _webhooks.TriggerAsync(row.CompanyId, row.EventType, doc.RootElement.Clone());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ระบบ webhook เดิมบันทึกผลต่อการลงทะเบียนเองอยู่แล้ว — ที่นี่บันทึกเหตุบนแถว (ไม่ลองซ้ำไม่รู้จบ)
                    _logger.LogError(ex, "ส่งเหตุการณ์ {Event} เข้า webhook ที่ลงทะเบียนไม่สำเร็จ (company {CompanyId})", row.EventType, row.CompanyId);
                    row.LastError = Trim("webhook ที่ลงทะเบียน: " + ex.Message);
                }
            }
            row.RegistrationsDispatchedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
        }

        // รอบ 267 P2: ฉบับที่ลูกค้าเซ็น (ไฟล์ + SHA-256) + สำเนาทางอีเมลถึงผู้ลงนาม — ทำก่อนขั้น callback · ยังไม่สำเร็จ = แถวค้างเป็น Pending แล้วลองใหม่ตามจังหวะ
        // ของ outbox · ครบจำนวนครั้ง = เลิกลอง + แจ้งพนักงาน (ล้มดัง: เหตุบนแถว outbox + กระดิ่ง + หน้าลูกค้าแสดง "กำลังเตรียม")
        if (row.CustomerActionId is Guid actionId && row.SignedCopyDoneAt == null && _responses != null)
        {
            CustomerDocumentResponseService.SignedCopyResult copy;
            try { copy = await _responses.ProcessSignedCopyAsync(row.CompanyId, actionId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "สร้าง/ส่งฉบับที่ลูกค้าเซ็นล้ม action={ActionId} (company {CompanyId})", actionId, row.CompanyId);
                copy = new(false, false, "ข้อผิดพลาดของระบบ: " + ex.Message);
            }
            if (copy.Done)
            {
                row.SignedCopyDoneAt = nowUtc;
                if (copy.Note != null) row.LastError = Trim("ฉบับที่ลูกค้าเซ็น: " + copy.Note);
                await _db.SaveChangesAsync(ct);
                // ฝ่ายค้าน 267-P2 M3: เลิกสร้างทั้งที่ควรมีไฟล์ ⇒ แจ้งพนักงานเหมือนเส้นครบจำนวนครั้ง (ล้มดัง 3 ที่: แถว outbox · กระดิ่ง · หน้าลูกค้าเลิกแสดง "กำลังเตรียม")
                if (copy.Abandoned) await NotifySignedCopyFailedAsync(row, copy.Note, ct);
            }
            else
            {
                row.Attempts++;
                row.LastAttemptAt = nowUtc;
                row.LastError = Trim("ฉบับที่ลูกค้าเซ็น: " + (copy.Note ?? "ยังไม่สำเร็จ"));
                row.UpdatedAt = nowUtc;
                if (row.Attempts >= SignEventOutbox.MaxAttempts)
                {
                    row.SignedCopyDoneAt = nowUtc;   // เลิกลอง — เหตุอยู่บนแถว + แจ้งพนักงาน (ไม่ประทับว่าสำเร็จ: ไม่มี SignedPdfSha256 บนแถวหลักฐาน)
                    row.Attempts = 0;                // จำนวนครั้งของขั้น callback นับใหม่
                    await _db.SaveChangesAsync(ct);
                    await NotifySignedCopyFailedAsync(row, copy.Note, ct);
                }
                else
                {
                    row.NextAttemptAt = SignEventOutbox.AfterAttempt(false, row.Attempts, nowUtc).NextAttemptAt;
                    return;
                }
            }
        }

        // รอบ 267 P3 (267-1-36 ส่วน LINE · 267-1-63): ลูกค้าเปิดลิงก์จากการ์ด LINE 1:1 มาลงนาม ⇒ การ์ด "ฉบับเซ็น" กลับไป LINE เดิม (ครั้งเดียว · หลังมีไฟล์แล้ว) ·
        // ผู้เข้าคิวเดียว DocumentLineSendService.EnqueueSignedCopyAsync — ส่งจริง/ล้มดูที่แถวคิว LINE + ไทม์ไลน์ลูกค้า · ไม่มีไฟล์ (เลิกสร้าง) ⇒ ไม่ส่งการ์ดที่ไม่มีอะไรให้ดาวน์โหลด
        if (row.SignedCopyDoneAt != null && row.SourceLineQueueId is Guid sourceQueue && row.SignedCopyLineQueuedAt == null && row.CustomerActionId is Guid copyAction)
        {
            var hasFile = await _db.CustomerDocumentActions.AsNoTracking()
                .AnyAsync(a => a.Id == copyAction && a.CompanyId == row.CompanyId && a.SignedPdfSha256 != null && a.ErasedAt == null, ct);
            if (hasFile)
            {
                var (queued, note) = await DocumentLineSendService.EnqueueSignedCopyAsync(_db, row.CompanyId, row.DocumentId, sourceQueue, ct);
                if (!queued && note != null) row.LastError = Trim("ฉบับเซ็นทาง LINE: " + note);
            }
            else row.LastError = Trim("ฉบับเซ็นทาง LINE: ไม่มีไฟล์ฉบับที่ลงนาม — ไม่ส่งการ์ด");
            row.SignedCopyLineQueuedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(row.CallbackUrl))
        {
            row.Status = SignEventStatus.NoCallback;
            row.UpdatedAt = nowUtc;
            return;
        }

        // ฝ่ายค้าน S2/N3: ตรวจกติกา URL + รายชื่อโฮสต์ "ปัจจุบัน" ของบริษัทก่อนส่งทุกครั้ง · payload ถูกล้างแล้ว ⇒ ไม่ส่ง
        if (row.PayloadJson == "{}")
        {
            MarkDead(row, nowUtc, "ข้อมูลของเหตุการณ์ถูกล้างตามอายุเก็บแล้ว — ไม่ส่ง");
            return;
        }
        var hosts = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == row.CompanyId)
            .Select(s => s.SignCallbackAllowedHosts).FirstOrDefaultAsync(ct);
        if (SignCallbackUrlRule.Problem(row.CallbackUrl, hosts) is { } problem)
        {
            MarkDead(row, nowUtc, problem + " — ไม่ส่ง (แก้ค่าตั้ง/URL แล้วกดส่งซ้ำ)");
            return;
        }

        var (ok, status, error) = await DeliverCallbackAsync(row, ct);
        row.Attempts++;
        row.LastAttemptAt = nowUtc;
        row.LastHttpStatus = status;
        row.LastError = ok ? null : Trim(error);
        var (next, nextAt) = SignEventOutbox.AfterAttempt(ok, row.Attempts, nowUtc);
        row.Status = next;
        row.NextAttemptAt = nextAt;
        if (ok) row.DeliveredAt = nowUtc;
        row.UpdatedAt = nowUtc;
    }

    private static string? Trim(string? s) => s == null ? null : s.Length <= 1000 ? s : s[..1000];

    /// <summary>ขนาด body ของคำตอบที่อ่านมาเก็บเป็นเหตุ (ไบต์)</summary>
    public const int MaxResponseBytes = 2048;

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));   // body ที่ส่งช้า/ไม่จบ ไม่ถ่วงรอบ
        try
        {
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[MaxResponseBytes];
            var read = 0;
            while (read < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(read, buf.Length - read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            return QuotationAcceptance.CleanText(Encoding.UTF8.GetString(buf, 0, read), 300);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;   // อ่าน body ไม่ได้ = เหตุมีแค่รหัส HTTP (ไม่ใช่เส้นเงิน · ไม่ทำให้แถวล้มเพิ่ม)
        }
    }

    private async Task<(bool Ok, int? Status, string? Error)> DeliverCallbackAsync(SignEventOutboxEntry row, CancellationToken ct)
    {
        // ด่าน SSRF ตอนส่งจริง (DNS rebinding: ชื่อเดิมอาจชี้ IP ภายในแล้ว) — client ข้างล่างตรวจ IP ที่ต่อจริงซ้ำอีกชั้น
        var check = await OutboundUrlGuard.CheckResolvedAsync(row.CallbackUrl, ct);
        if (!check.Ok) return (false, null, check.Reason);

        var secretProtected = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == row.CompanyId).Select(s => s.SignCallbackSecret).FirstOrDefaultAsync(ct);
        var secret = _secrets.Unprotect(secretProtected);
        if (string.IsNullOrWhiteSpace(secret))
            return (false, null, "บริษัทไม่มีรหัสลับลงนาม webhook (ถูกลบ/ยังไม่ออก) — ออกรหัสใหม่ที่ ตั้งค่า → ลิงก์ให้ลูกค้าเซ็น แล้วกดส่งซ้ำ");

        var body = row.PayloadJson;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Post, row.CallbackUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Sign(secret, ts, body));
        req.Headers.TryAddWithoutValidation(WebhookSignature.EventHeader, row.EventType);
        req.Headers.TryAddWithoutValidation(WebhookSignature.EventIdHeader, row.EventId.ToString());
        req.Headers.TryAddWithoutValidation("User-Agent", "NextAcc-Webhook/1.0");

        var sw = Stopwatch.StartNew();
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
            // ฝ่ายค้าน S6: อ่านแค่หัวคำตอบ — body อ่านไม่เกิน MaxResponseBytes (ปลายทางส่ง body ใหญ่/ไม่จบ ต้องไม่กินหน่วยความจำ/เวลา)
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return (true, code, null);
            if (code is >= 300 and < 400)
                return (false, code, $"ปลายทางตอบ redirect (HTTP {code}) — ระบบไม่ตาม redirect (กัน SSRF) · ตั้ง callbackUrl เป็นปลายทางจริง");
            var snippet = await ReadBoundedAsync(resp, ct);
            return (false, code, $"HTTP {code} {resp.ReasonPhrase}" + (string.IsNullOrWhiteSpace(snippet) ? "" : " · " + snippet));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, $"ปลายทางไม่ตอบภายใน {TimeoutSeconds} วินาที");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, "ต่อปลายทางไม่ได้: " + ex.Message);
        }
        finally
        {
            sw.Stop();
            _logger.LogInformation("callback {Event} {EventId} → {Host} ใช้เวลา {Ms} ms", row.EventType, row.EventId,
                Uri.TryCreate(row.CallbackUrl, UriKind.Absolute, out var u) ? u.Host : "?", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>รอบ 267 P2: หัวเรื่อง + ข้อความแจ้งพนักงานจากแถวหลักฐาน — ยอมรับ ⇒ แนะนำแปลงเอกสาร (ไม่มีชนิด "ใบสั่งขาย" — 267-1-15) +
    /// คำเตือนอากรแสตมป์เมื่อมีบรรทัดงานบริการ (267-1-14 · ระบบไม่ตัดสินว่าเป็นตราสาร) · ไม่รับ/ขอแก้ ⇒ เหตุผล + ทางไปต่อ</summary>
    private async Task<(string? Title, string? Message)> CustomerActionNoticeAsync(Guid companyId, Guid actionId, string? documentNumber,
        Models.Enums.DocumentType type, CancellationToken ct)
    {
        var a = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(x => x.Id == actionId && x.CompanyId == companyId)
            .Select(x => new { x.Action, x.DocumentId, x.SignerFirstName, x.SignerLastName, x.SignerTitle, x.SignerOrgName, x.ReasonCode, x.Message })
            .FirstOrDefaultAsync(ct);
        if (a == null || !Enum.TryParse<CustomerSignRule.Kind>(a.Action, out var kind)) return (null, null);
        var name = string.Join(" ", new[] { a.SignerFirstName, a.SignerLastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var who = name + (string.IsNullOrWhiteSpace(a.SignerTitle) ? "" : $" ({a.SignerTitle})")
            + (string.IsNullOrWhiteSpace(a.SignerOrgName) ? "" : $" ในนาม {a.SignerOrgName}");
        var label = CustomerSignText.KindLabel(false, kind);
        var parts = new List<string> { $"{who} — {label} {documentNumber} ออนไลน์" };
        if (CustomerSignText.ReasonLabel(false, a.ReasonCode) is { } reason) parts.Add("เหตุผล: " + reason);
        if (!string.IsNullOrWhiteSpace(a.Message)) parts.Add("ข้อความจากลูกค้า: " + a.Message);
        if (kind == CustomerSignRule.Kind.Accepted)
        {
            parts.Add(CustomerSignText.ConvertSuggestionText);
            if (_responses != null && CustomerSignRule.StampDutyWarn(type, kind, await _responses.HasServiceLineAsync(companyId, a.DocumentId, ct)))
                parts.Add($"⚠️ {CustomerSignText.StampDutyWarning} ({CustomerSignText.StampDutyPage})");
        }
        else if (kind == CustomerSignRule.Kind.PoAcknowledged)
        {
            // รอบ 267 P5: วันส่งที่ผู้ขายยืนยัน (ถ้ามี) — พนักงานจัดซื้อตามรับของ
            var promised = await _db.CustomerDocumentActions.AsNoTracking().Where(x => x.Id == actionId && x.CompanyId == companyId)
                .Select(x => x.PromisedDate).FirstOrDefaultAsync(ct);
            if (promised is { } p) parts.Add($"วันส่งที่ผู้ขายยืนยัน: {ThaiDate.ToThaiDisplayString(p)}");
        }
        else if (kind == CustomerSignRule.Kind.PoDeclined)
            parts.Add("ขั้นต่อไป: ติดต่อผู้ขาย แก้ใบสั่งซื้อ (ออก Rev ใหม่) แล้วส่งลิงก์ให้รับทราบใหม่ — หรือยกเลิกใบสั่งซื้อแล้วสั่งผู้ขายรายอื่น");
        else if (kind is CustomerSignRule.Kind.Declined or CustomerSignRule.Kind.ChangeRequested)
            parts.Add("ขั้นต่อไป: แก้ไขใบเสนอราคา (ออก Rev ใหม่) แล้วส่งให้ลูกค้าตอบรับใหม่ — หรือกด \"ส่งลิงก์ให้ลูกค้าตอบรับ\" อีกครั้งเพื่อเปิดให้ลูกค้าตอบใหม่บนฉบับเดิม");
        var icon = kind switch { CustomerSignRule.Kind.Declined or CustomerSignRule.Kind.PoDeclined => "🚫", CustomerSignRule.Kind.ChangeRequested => "✏️", _ => "✍️" };
        return ($"{icon} {label} — {documentNumber}", string.Join(" · ", parts));
    }

    /// <summary>รอบ 267 P2: เลิกลองสร้าง/ส่งฉบับที่ลูกค้าเซ็นแล้ว ⇒ แจ้งพนักงาน (คำตอบของลูกค้ายังมีผล — หลักฐานอยู่ในแถวหลักฐาน/บนเอกสาร)</summary>
    private async Task NotifySignedCopyFailedAsync(SignEventOutboxEntry row, string? reason, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            var number = await _db.Documents.AsNoTracking().Where(d => d.Id == row.DocumentId && d.CompanyId == row.CompanyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            await _notify.DispatchAsync(row.CompanyId, NotificationEvents.DocumentCustomerSigned, new NotificationContext
            {
                Title = $"⚠️ สร้าง/ส่งฉบับที่ลูกค้าลงนามไม่สำเร็จ — {number}",
                Message = $"ลูกค้าลงนามเอกสาร {number} แล้ว (คำตอบมีผลและมีหลักฐานครบ) แต่ระบบสร้างหรือส่งสำเนาฉบับที่ลงนามไม่สำเร็จ: {reason ?? "ไม่ทราบสาเหตุ"} — เปิดเอกสารแล้วส่ง PDF ให้ลูกค้าเอง",
                ActionUrl = $"/pages/documents.html?id={row.DocumentId}",
                EntityType = nameof(Document),
                EntityId = row.DocumentId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งพนักงานเรื่องฉบับที่ลูกค้าเซ็นไม่สำเร็จ ล้ม doc={DocId} (company {CompanyId})", row.DocumentId, row.CompanyId);
        }
    }

    private async Task NotifySellerAsync(SignEventOutboxEntry row, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == row.DocumentId && d.CompanyId == row.CompanyId)
                .Select(d => new { d.DocumentNumber, d.DocumentType, d.QuotationAcceptedBy, d.DeliverySignedBy })
                .FirstOrDefaultAsync(ct);
            if (doc == null) return;
            var kind = SignRequestKinds.ForEvent(row.EventType);
            var who = doc.QuotationAcceptedBy ?? doc.DeliverySignedBy ?? "ลูกค้า";
            // รอบ 267 P2: มีแถวหลักฐาน ⇒ ข้อความจากแถวนั้น (ชื่อ · ตำแหน่ง · ในนาม · เหตุผล/ข้อความ) + ขั้นต่อไป (แปลงเอกสาร) + คำเตือนอากรแสตมป์ (เตือนเท่านั้น)
            var (title, message) = row.CustomerActionId is Guid actionId
                ? await CustomerActionNoticeAsync(row.CompanyId, actionId, doc.DocumentNumber, doc.DocumentType, ct) : (null, null);
            await _notify.DispatchAsync(row.CompanyId, NotificationEvents.DocumentCustomerSigned, new NotificationContext
            {
                Title = title ?? $"✍️ {kind?.SignedTh ?? "ลูกค้าเซ็นแล้ว"} — {doc.DocumentNumber}",
                Message = message ?? $"{who} {(kind?.ActionTh ?? "เซ็น")}เอกสาร {doc.DocumentNumber} ออนไลน์แล้ว",
                ActionUrl = $"/pages/documents.html?id={row.DocumentId}",
                EntityType = nameof(Document),
                EntityId = row.DocumentId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งผู้ขายเรื่องลูกค้าเซ็น {DocId} ไม่สำเร็จ (company {CompanyId})", row.DocumentId, row.CompanyId);
        }
    }
}
