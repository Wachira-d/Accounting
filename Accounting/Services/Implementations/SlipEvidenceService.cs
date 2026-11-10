using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ไฟล์สลิปที่อ่านแล้ว (ไบต์ต้นฉบับ — ก่อนย่อรูป) · <paramref name="ContentType"/> = ชนิดที่ผู้เรียกตรวจแล้ว</summary>
public sealed record SlipFile(byte[] Bytes, string ContentType, string FileName);

/// <summary>ผลของการแจ้งโอน — รายการรอยืนยัน + ที่อยู่ไฟล์ (เว็บ) + เลขอ้างอิงหลังปรับรูป</summary>
public sealed record SlipEvidenceResult(PaymentIntent Intent, string FileUrl, string? SlipRef);

/// <summary>
/// **ประตูเดียวของ "ลูกค้าแจ้งโอน + แนบสลิป"** (รอบ 255 WP-12B ข้อ 1 · AUTOPILOT §5.6 · แบบเดียวกับ <c>WhtCertIntakeService.SubmitAsync</c>)
/// <para>ทางเข้า: พอร์ทัลลูกค้า (ล็อกอิน · <c>PortalService.UploadDocumentSlipAsync</c>) · หน้าชำระของเอกสาร (โทเคนลิงก์ชำระ ·
/// <c>PublicPaymentController.SubmitDocumentSlip</c>) — ทั้งสองเดินลำดับเดียวกัน: ด่านยอดค้าง (<see cref="DocumentSlipIntents.UploadProblem"/>) →
/// กันซ้ำ (แฮชไฟล์ + เลขอ้างอิง · <see cref="SlipEvidenceRule"/>) → เก็บไฟล์ผ่านที่เก็บกลาง → <c>PaymentIntent(ManualSlip)</c> รอยืนยัน →
/// ไฟล์แนบของเอกสาร → แจ้งทีม E-06</para>
/// <para><b>สลิป ≠ รับชำระ</b> (F2 ข้อ 3): ไม่สร้างแถว <c>Payment</c> · ปิดหนี้ได้เมื่อคนยืนยันที่หน้ารายการรับชำระ (<c>ManualSlipPaymentProvider</c>)
/// หรือรายการเดินบัญชีจริงมาถึงแล้ว <c>BankReceiptAutoSettle</c> จับได้ (เลขอ้างอิงเป็นตัวระบุตัว) · ไม่มี AI ในเส้นนี้</para>
/// </summary>
public class SlipEvidenceService
{
    private readonly AccountingDbContext _db;
    private readonly Accounting.Services.Payments.IPaymentIntentService? _intents;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    private readonly IImageProcessingService? _images;
    private readonly INotificationEngine? _notify;

    public SlipEvidenceService(AccountingDbContext db, Accounting.Services.Payments.IPaymentIntentService? intents = null,
        Accounting.Services.Storage.IFileStorage? storage = null, IImageProcessingService? images = null, INotificationEngine? notify = null)
    {
        _db = db; _intents = intents; _storage = storage; _images = images; _notify = notify;
    }

    /// <summary>ช่องทางที่แจ้ง (บันทึกเป็นผู้สร้างไฟล์แนบ — หน้าเอกสารบอกได้ว่ามาจากไหน)</summary>
    public const string ChannelPortal = "portal-customer";
    public const string ChannelPayLink = "pay-link-customer";

    /// <param name="contactId">พอร์ทัล = ผู้ติดต่อในโทเคน (ด่านความเป็นเจ้าของ) · ลิงก์ชำระ = ผู้ติดต่อของใบ (โทเคนพิสูจน์แล้ว)</param>
    /// <param name="amount">null = ยอดค้างของใบ</param>
    public async Task<SlipEvidenceResult> SubmitAsync(Guid companyId, Guid documentId, Guid? contactId, SlipFile file, decimal? amount,
        string? bankRef, string channel, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted
                && (contactId == null || d.ContactId == contactId), ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (_intents == null)
            throw new BusinessRuleException("ระบบรับสลิปยังไม่พร้อม — กรุณาติดต่อผู้ขาย", DocumentSlipIntents.RuleCode, 503);
        // รอบ 255 ฝ่ายค้าน LOW: ชนิดไฟล์จากไบต์จริงทุกช่องทาง (ไม่รับ SVG/HTML ที่ตั้ง Content-Type เป็นรูป) · นามสกุลที่เก็บจากชนิดนั้น
        var sniffed = SlipEvidenceRule.DetectPublicType(file.Bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed)
            ?? throw new ArgumentException("รองรับเฉพาะรูปภาพ (JPEG/PNG/WEBP) หรือ PDF ของสลิป");
        var fromPayLink = channel == ChannelPayLink;

        SlipEvidenceResult? result = null;
        // รอบ 255 ฝ่ายค้าน LOW: ตรวจซ้ำ + ยอดสลิปรอตรวจ + สร้างรายการ ภายใต้ล็อกต่อบริษัท — สองคำขอพร้อมกันไม่ผ่านด่านทั้งคู่
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.SlipEvidence, "submit", async () =>
        {
            await _db.Entry(doc).ReloadAsync(ct);
            // รอบ 250 ฝ่ายค้าน H2/L4: ตัดสินที่ยอดค้างของเอกสาร (สลิปงวด 2 รับได้ · ใบชำระครบ/ยอดเกินยอดค้าง = ปฏิเสธ) — ตัวตัดสินเดียว DocumentSlipIntents
            var pay = amount ?? doc.BalanceDue;
            if (DocumentSlipIntents.UploadProblem(doc.Status, doc.BalanceDue, pay) is string uploadProblem)
                throw new BusinessRuleException(uploadProblem, DocumentSlipIntents.RuleCode, 400);
            // สลิปรอตรวจของใบนี้รวมสลิปใหม่ต้องไม่เกินยอดค้าง (ทั้งพอร์ทัลและหน้าชำระ)
            var openStatuses = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == doc.Id
                    && i.MethodKind == PaymentMethodKind.ManualSlip)
                .Select(i => new { i.Status, i.Amount }).ToListAsync(ct);
            var pendingSum = openStatuses.Where(i => PaymentIntentPolicy.IsOpen(i.Status)).Sum(i => i.Amount);
            if (SlipEvidenceRule.PendingProblem(pendingSum, pay, doc.BalanceDue) is string pendingProblem)
                throw new BusinessRuleException(pendingProblem, DocumentSlipIntents.RuleCode, 409);

            // รอบ 252 WP-12A (G-T5): กันซ้ำด้วยแฮชไฟล์ต้นฉบับ (ก่อนย่อรูป) + เลขอ้างอิงโอน — ทั้งบริษัท (สลิปเดียวจ่ายสองใบไม่ได้) · ก่อนเขียนไฟล์/สร้างรายการ
            var slipSha = SlipEvidenceRule.Sha256Hex(file.Bytes);
            var slipRef = SlipEvidenceRule.NormalizeRef(bankRef);
            // รอบ 257 ชุด 4 (A-M8): นับเฉพาะรายการที่รอตรวจ/รับเงินแล้ว (SlipEvidenceRule.DuplicateStatuses) — สลิปที่ผู้ขายปฏิเสธ/หมดอายุ ส่งใหม่ได้
            var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
            var sameFile = await _db.PaymentIntents.AsNoTracking()
                .AnyAsync(i => i.CompanyId == companyId && i.SlipFileSha256 == slipSha && dupStatuses.Contains(i.Status), ct);
            var sameRef = slipRef != null && await _db.PaymentIntents.AsNoTracking()
                .AnyAsync(i => i.CompanyId == companyId && i.SlipBankRef == slipRef && dupStatuses.Contains(i.Status), ct);
            if (SlipEvidenceRule.DuplicateProblem(sameFile, sameRef) is string dupProblem)
                throw new BusinessRuleException(dupProblem, SlipEvidenceRule.RuleDuplicate, 409);

            // เก็บผ่านที่เก็บกลาง (Local/S3) · รอบ 255 ฝ่ายค้าน M8: สลิปจากหน้าชำระ (คนนอกระบบอัปโหลด) อยู่โฟลเดอร์ที่<b>ไม่เสิร์ฟตรง</b> —
            // พนักงานเปิดผ่าน attachments/{id}/download (ด่านสิทธิ์ + บริษัท) แบบ /uploads/slips · พอร์ทัลคงโฟลเดอร์เดิม
            var storage = _storage ?? _images?.Storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;
            var relDir = fromPayLink ? $"uploads/document-slips/{DateTime.UtcNow:yyyy-MM}" : $"uploads/portal-slips/{DateTime.UtcNow:yyyy-MM}";
            var absDir = System.IO.Path.Combine(storage.Locator.RelativeBase, "wwwroot", relDir);
            string storedName; string absPath; string relUrl;
            if (_images != null && _images.IsProcessableImage(sniffed!))
            {
                using var s = new System.IO.MemoryStream(file.Bytes, writable: false);
                var processed = await _images.ProcessAndSaveAsync(s, sniffed!, "slip" + ext, absDir, "/" + relDir, ImageProfile.Slip);
                absPath = processed.AbsolutePath;
                storedName = System.IO.Path.GetFileName(absPath);
                relUrl = processed.RelativeUrl;
            }
            else
            {
                storedName = $"{Guid.NewGuid():N}{ext}";
                absPath = System.IO.Path.Combine(absDir, storedName);
                await storage.WriteAsync(absPath, file.Bytes, sniffed);
                relUrl = "/" + relDir + "/" + storedName;
            }

            // ความจริงเรื่องเงิน = PaymentIntent (manual-slip) รอยืนยัน — สลิปแต่ละใบเป็นรายการของตัวเอง (ฝ่ายค้าน H2) ·
            // ล้ม ⇒ ลบไฟล์ที่เพิ่งเขียน (ฝ่ายค้าน L4 — ไม่เก็บข้อมูลผู้โอนกำพร้า) แล้วโยนต่อ
            PaymentIntent intent;
            try
            {
                intent = await _intents.StartAsync(companyId, new Accounting.Services.Payments.StartPaymentRequest(
                    PaymentSourceKind.Document, doc.Id, pay, PaymentMethodKind.ManualSlip,
                    Description: PortalSlipPending.IntentDescription(doc.DocumentNumber, relUrl, fromPayLink),
                    ContactId: doc.ContactId),
                    providerCode: Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { await storage.DeleteAsync(absPath); } catch (Exception delEx) when (delEx is not OperationCanceledException) { _ = delEx; }
                if (ex is BusinessRuleException) throw;
                // ข้อความไทยของ StartAsync (InvalidOperationException) ส่งต่อตรง · อื่น ๆ = ข้อความกลาง (ไม่รั่วข้อความภายใน)
                throw new BusinessRuleException(ex is InvalidOperationException ? ex.Message : "บันทึกสลิปไม่สำเร็จ — กรุณาแนบอีกครั้ง หรือติดต่อผู้ขาย",
                    ex, DocumentSlipIntents.RuleCode, 409);
            }

            // หลักฐานกันซ้ำบนรายการเดียวกับที่คนยืนยัน (ไม่ใช่ตารางที่สอง) · เลขอ้างอิงเป็นตัวระบุตัวให้ BankReceiptAutoSettle เมื่อรายการเดินบัญชีจริงมาถึง
            await _db.PaymentIntents.Where(i => i.Id == intent.Id && i.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.SlipFileSha256, slipSha).SetProperty(i => i.SlipBankRef, slipRef), ct);

            _db.Set<FileAttachment>().Add(new FileAttachment
            {
                CompanyId = companyId,
                FileName = storedName,
                OriginalFileName = "slip" + ext,
                ContentType = sniffed!,
                StoragePath = absPath,
                EntityType = "Document",
                EntityId = doc.Id,
                UploadedByUserId = null,
                CreatedBy = channel,
            });
            await _db.SaveChangesAsync(ct);
            result = new SlipEvidenceResult(intent, fromPayLink ? "" : relUrl, slipRef);
        }, null, companyId, ct);
        if (!ran || result == null)
            throw new BusinessRuleException("กำลังรับสลิปอีกรายการของผู้ขายรายนี้อยู่ — รอสักครู่แล้วแนบอีกครั้ง", DocumentSlipIntents.RuleCode, 409);

        // E-06: แจ้งทีมทันที (ลูกค้าแจ้งโอน — รอตรวจ) · เครื่องแจ้งเตือนกลางไม่โยนกลับ (ล้มไม่ย้อนการแนบสลิป)
        if (_notify != null)
        {
            var (title, message) = AutopilotNotice.SlipSubmitted(doc.DocumentNumber, result.Intent.Amount, result.SlipRef);
            await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.AutopilotSlipSubmitted, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = "/pages/documents.html?openDoc=" + doc.Id, EntityType = "Document", EntityId = doc.Id,
            });
        }
        return result;
    }

    /// <summary>หน้าชำระของเอกสาร: มีสลิปที่ยังรอผู้ขายตรวจไหม + จำนวนที่แจ้งใน 1 ชม. (ด่านสแปมของทางเข้าที่ไม่ล็อกอิน — นับในฐาน หลายเครื่องใช้ตัวนับเดียว)</summary>
    public async Task<(bool HasPending, int LastHour)> PayLinkLoadAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == documentId
                && i.MethodKind == PaymentMethodKind.ManualSlip)
            .Select(i => new { i.Status, i.CreatedAt })
            .ToListAsync(ct);
        return (rows.Any(r => PaymentIntentPolicy.IsOpen(r.Status)), rows.Count(r => r.CreatedAt >= since));
    }
}
