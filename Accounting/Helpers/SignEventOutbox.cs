using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **เหตุการณ์ "ลูกค้าเซ็น/ยอมรับเอกสาร" → outbox — ตัวตั้งเดียวของ payload / กุญแจกันซ้ำ / จังหวะลองใหม่** (รอบ 240 · คำตัดสิน 240-3)
///
/// <para>ผู้เรียก <see cref="Enqueue"/>: <c>PublicQuotationController.Accept</c> และ <c>SignDelivery</c> — ภายในธุรกรรมเดียวกับการบันทึกการเซ็น
/// (<c>required_call_site_check</c> ล็อก) · ผู้ส่ง: <c>SignEventDispatcher</c> ในงาน <c>SignEventOutboxJob</c></para>
///
/// <para>Payload (PDPA ม.22 เก็บ/ส่งเท่าที่จำเป็น): eventId · type · occurredAt (ISO +07:00) · companyId · documentId · documentNumber · documentType ·
/// revisionNumber · acceptedBy (ชื่อที่ลูกค้าพิมพ์) · hasSignature · contentHash · callbackRef · links (API ที่ต้องยืนยันตัวตนเพื่อดึง PDF/สถานะ) ·
/// <b>ไม่มี</b>รูปลายเซ็น · IP · User-Agent · token</para>
/// </summary>
public static class SignEventOutbox
{
    public const int MaxAttempts = 8;
    public const int PayloadRetentionDays = 90;

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
        TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24),
    };

    /// <summary>ระยะรอก่อนลองครั้งถัดไปหลังล้มมาแล้ว <paramref name="attempts"/> ครั้ง (1 นาที → 24 ชม.)</summary>
    private static TimeSpan DelayAfter(int attempts)
        => Backoff[Math.Clamp(attempts, 1, Backoff.Length) - 1];

    /// <summary>ผลของความพยายามหนึ่งครั้ง → สถานะถัดไป (ครบ <see cref="MaxAttempts"/> = Dead)</summary>
    public static (SignEventStatus Status, DateTime NextAttemptAt) AfterAttempt(bool success, int attemptsSoFar, DateTime nowUtc)
    {
        if (success) return (SignEventStatus.Delivered, nowUtc);
        if (attemptsSoFar >= MaxAttempts) return (SignEventStatus.Dead, nowUtc);
        return (SignEventStatus.Pending, nowUtc + DelayAfter(attemptsSoFar));
    }

    /// <summary>กุญแจกันซ้ำ — หนึ่งเหตุการณ์ต่อ (ชนิด · เอกสาร · ลิงก์)</summary>
    public static string EventKey(string eventType, Guid documentId, string? tokenSha256)
        => $"{eventType}:{documentId:N}:{(string.IsNullOrWhiteSpace(tokenSha256) ? "-" : tokenSha256)}";

    /// <summary>eventId คงที่จากกุญแจ + บริษัท (ส่งซ้ำ/ส่งใหม่ได้ id เดิม ⇒ ผู้รับกันซ้ำได้)</summary>
    private static Guid EventIdFor(Guid companyId, string eventKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{companyId:N}|{eventKey}"));
        var g = new byte[16];
        Array.Copy(bytes, g, 16);
        g[7] = (byte)((g[7] & 0x0F) | 0x50);   // ระบุว่าเป็น name-based (รุ่น 5 แบบ SHA) — ไม่ชนกับ Guid.NewGuid
        g[8] = (byte)((g[8] & 0x3F) | 0x80);
        return new Guid(g);
    }

    /// <summary>เวลาไทยแบบ ISO 8601 พร้อม offset +07:00</summary>
    private static string BangkokIso(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(7))
            .ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    public sealed record PayloadInput(
        Guid EventId, string EventType, DateTime OccurredAtUtc, Guid CompanyId, Guid DocumentId, string? DocumentNumber,
        string DocumentType, int RevisionNumber, string? AcceptedBy, bool HasSignature, string? ContentHash, string? CallbackRef,
        // ฝ่ายค้าน 267-P5 LOW-10: วันส่งที่ผู้ขายยืนยัน (purchase_order.acknowledged) — วันปฏิทิน yyyy-MM-dd · null = ไม่ได้ยืนยัน/ไม่ใช่เหตุการณ์ใบสั่งซื้อ
        DateTime? PromisedDate = null);

    /// <summary>payload ตัวเดียว (camelCase) — ลิงก์ชี้ API ที่ต้องใช้ API key (ไม่ฝังไฟล์/ลายเซ็น)</summary>
    public static string BuildPayload(PayloadInput p)
    {
        var obj = new
        {
            eventId = p.EventId,
            type = p.EventType,
            occurredAt = BangkokIso(p.OccurredAtUtc),
            companyId = p.CompanyId,
            data = new
            {
                documentId = p.DocumentId,
                documentNumber = p.DocumentNumber,
                documentType = p.DocumentType,
                revisionNumber = p.RevisionNumber,
                acceptedBy = p.AcceptedBy,
                hasSignature = p.HasSignature,
                contentHash = p.ContentHash,
                callbackRef = p.CallbackRef,
                promisedDate = p.PromisedDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            },
            links = new
            {
                document = $"/api/v1/documents/{p.DocumentId}/sign-status",
                pdf = $"/api/v1/documents/{p.DocumentId}/pdf",
            },
        };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>เพิ่มแถว outbox ลง context (ผู้เรียก SaveChanges ในธุรกรรมเดียวกับการบันทึกการเซ็น) · คืนแถวที่เพิ่ม</summary>
    public static SignEventOutboxEntry Enqueue(AccountingDbContext db, Document doc, SignRequestKinds.Kind kind, DateTime signedAtUtc,
        string? signedBy, bool hasSignature, string? contentHash, string? tokenSha256)
        => EnqueueEvent(db, doc, kind.EventType, signedAtUtc, signedBy, hasSignature, contentHash, tokenSha256);

    /// <summary>รอบ 267 P2: เหตุการณ์ที่ไม่ใช่ "เซ็นแล้ว" (ลูกค้าไม่รับ/ขอแก้ใบเสนอราคา — <c>SignRequestKinds.EventFor</c>) ใช้ payload/กุญแจ/จังหวะลองใหม่ชุดเดียวกัน ·
    /// <paramref name="keyRef"/> = ส่วนท้ายของกุญแจกันซ้ำ (รอบ 267: ผูกกับแถวหลักฐาน — token ของหน้าเอกสารอยู่ข้าม Rev)</summary>
    public static SignEventOutboxEntry EnqueueEvent(AccountingDbContext db, Document doc, string eventType, DateTime signedAtUtc,
        string? signedBy, bool hasSignature, string? contentHash, string? keyRef, DateTime? promisedDate = null)
    {
        var key = EventKey(eventType, doc.Id, keyRef);
        var id = EventIdFor(doc.CompanyId, key);
        var row = new SignEventOutboxEntry
        {
            CompanyId = doc.CompanyId,
            EventId = id,
            EventKey = key,
            EventType = eventType,
            DocumentId = doc.Id,
            CallbackUrl = string.IsNullOrWhiteSpace(doc.SignCallbackUrl) ? null : doc.SignCallbackUrl,
            CallbackRef = doc.SignCallbackRef,
            PayloadJson = BuildPayload(new PayloadInput(id, eventType, signedAtUtc, doc.CompanyId, doc.Id, doc.DocumentNumber,
                doc.DocumentType.ToString(), doc.RevisionNumber, signedBy, hasSignature, contentHash, doc.SignCallbackRef, promisedDate)),
            Status = SignEventStatus.Pending,
            NextAttemptAt = signedAtUtc,
            CreatedAt = signedAtUtc,
        };
        db.SignEventOutbox.Add(row);
        return row;
    }
}
