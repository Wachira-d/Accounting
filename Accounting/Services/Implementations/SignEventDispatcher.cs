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

    public SignEventDispatcher(AccountingDbContext db, IHttpClientFactory http, ISecretProtector secrets, ILogger<SignEventDispatcher> logger,
        IWebhookService? webhooks = null, INotificationEngine? notify = null)
    {
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
            await _notify.DispatchAsync(row.CompanyId, NotificationEvents.DocumentCustomerSigned, new NotificationContext
            {
                Title = $"✍️ {kind?.SignedTh ?? "ลูกค้าเซ็นแล้ว"} — {doc.DocumentNumber}",
                Message = $"{who} {(kind?.ActionTh ?? "เซ็น")}เอกสาร {doc.DocumentNumber} ออนไลน์แล้ว",
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
