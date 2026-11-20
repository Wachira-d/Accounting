using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Card = Accounting.Helpers.TaxSlipReplyCard.Kind;
using Outcome = Accounting.Helpers.TaxSlipMatch.Outcome;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 แพ็ก P4/P6 — <b>เส้นเข้าตัวเดียว</b>ของสลิปชำระภาษีที่ลูกค้าส่งในกลุ่ม LINE (OA กลาง) หรืออีเมลของสำนักงานบัญชี:
/// ผูกช่องทาง → ดาวน์โหลด/ตรวจชนิด → กันซ้ำ (ข้อความ · ไฟล์ · รายการธนาคาร · Control Code) → อ่านในเครื่อง (QR → Tesseract → <see cref="OcrSlipText"/> · <b>ไม่มี AI</b>) →
/// <see cref="GovPaymentSlip.Identify"/> → <see cref="TaxSlipMatch.Decide"/> → ความยินยอม → <see cref="TaxSlipBankAccount.Decide"/> → <see cref="TaxSlipRemitService.ApplyAsync"/> ·
/// <b>ทุกผลมีการ์ดตอบกลับ</b> (<see cref="TaxSlipReplyCard"/>) และแถวที่ระบบตัดสินเองไม่ได้อยู่ในคิวพนักงานเสมอ (ล้มกลางทาง ⇒ คิว · ไม่หายเงียบ)
/// </summary>
public class TaxSlipIntakeService
{
    public const string SourceLine = "line";
    public const string SourceEmail = "email";

    public static class Statuses
    {
        public const string Received = "Received";
        public const string Remitted = "Remitted";
        public const string AwaitingConsent = "AwaitingConsent";
        public const string AwaitingChoice = "AwaitingChoice";
        public const string ToStaff = "ToStaff";
        public const string Rejected = "Rejected";
        /// <summary>รอบ 275 แพ็ก D (M4): รอพนักงานเกินกำหนด (<see cref="TaxSlipOpenAge"/>) — ปิดโดยไม่บันทึก · ไฟล์ลบตามระยะเก็บเดียวกับ Rejected</summary>
        public const string Expired = "Expired";
        public static readonly string[] Open = { Received, AwaitingConsent, AwaitingChoice, ToStaff };
        /// <summary>ปิดโดยไม่บันทึก — ไฟล์ลบเมื่อครบ <see cref="ClosedFileRetentionDays"/></summary>
        public static readonly string[] ClosedUnrecorded = { Rejected, Expired };
    }

    public static readonly TimeSpan ChoiceTtl = TimeSpan.FromHours(24);

    private readonly AccountingDbContext _db;
    private readonly ProviderConsentService _consent;
    private readonly TaxSlipChannelService _channel;
    private readonly TaxSlipRemitService _remitter;
    private readonly IStatutoryRemittanceService _remit;
    private readonly ISlipImageReader _reader;
    private readonly ICustomerLineLinkService _links;
    private readonly Accounting.Services.Storage.IFileStorage _storage;
    private readonly ILogger<TaxSlipIntakeService> _logger;
    private readonly INotificationEngine? _notify;
    private readonly IPermissionService? _perms;

    public TaxSlipIntakeService(AccountingDbContext db, ProviderConsentService consent, TaxSlipChannelService channel, TaxSlipRemitService remitter,
        IStatutoryRemittanceService remit, ISlipImageReader reader, ICustomerLineLinkService links, Accounting.Services.Storage.IFileStorage storage,
        ILogger<TaxSlipIntakeService> logger, INotificationEngine? notify = null, IPermissionService? perms = null)
    {
        _db = db; _consent = consent; _channel = channel; _remitter = remitter; _remit = remit; _reader = reader; _links = links; _storage = storage;
        _logger = logger; _notify = notify; _perms = perms;
    }

    /// <summary>ฝ่ายค้าน 272 H2: อ่านสลิปไม่ได้ (เครื่องไม่มี OCR / ข้อความว่าง) — แถวเข้าคิวพนักงานเสมอ (ห้ามตอบว่า "ไม่ใช่สลิปภาษี" เมื่อยังไม่ได้อ่าน)</summary>
    public const string OcrOffRuleCode = "TAX-SLIP-OCR-OFF";
    public const string OcrOffReason = "อ่านสลิปไม่ได้ (OCR ไม่พร้อม)";
    /// <summary>ฝ่ายค้าน 272 M6: แถวที่ปิดโดยไม่บันทึก ลบไฟล์เมื่อพ้นระยะนี้ (วัน) · แถวที่ยังรอตัดสินไม่ถูกลบ (ยังเป็นหลักฐานที่อาจลงบัญชี — MAX retention)</summary>
    public const int ClosedFileRetentionDays = 90;
    public const string RopaPurpose = "รับสลิปชำระภาษี/ประกันสังคมของบริษัทลูกค้าจากกลุ่ม LINE/อีเมลของสำนักงานบัญชี เพื่อบันทึกการนำส่งในบัญชีของลูกค้า";
    public const string RopaDataCategories = "รูป/PDF สลิป (ชื่อผู้โอน · เลขบัญชีบางส่วน · เลขผู้เสียภาษี/บัตรประชาชน · Control Code · ยอด · วันที่) · เก็บในฐาน: เลขภาษีปิดบัง + แฮชและ 4 ตัวท้ายของ Control Code/เลขที่รายการ · hash ของกลุ่ม LINE/อีเมลผู้ส่ง";
    public const string RopaRetention = "บันทึกแล้ว ⇒ ย้ายเป็นไฟล์แนบรายการนำส่งของลูกค้า (อายุเอกสารบัญชี ≥ 5 ปี · legal hold) และลบสำเนาทันที · ไม่บันทึก/ปิดแล้ว ⇒ ลบไฟล์เมื่อครบ 90 วัน · รอตัดสิน ⇒ เตือนพนักงานทุกวันหลัง 2 วัน · เกิน 90 วันปิดอัตโนมัติ (แล้วลบไฟล์เมื่อครบ 90 วันถัดไป)";

    /// <summary>บริษัทผู้ให้บริการ + สวิตช์ของเส้นนี้ (<c>CompanySettings.TaxSlipIntakeEnabled</c>) · ไม่ได้ตั้งผู้ให้บริการ ⇒ (null, false)</summary>
    private async Task<(Guid? ProviderId, bool Enabled)> ProviderAsync(CancellationToken ct)
    {
        var pid = await _consent.ProviderIdAsync(ct);
        if (pid is not Guid p) return (null, false);
        var on = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == p).Select(s => (bool?)s.TaxSlipIntakeEnabled).FirstOrDefaultAsync(ct);
        return (p, on ?? true);
    }

    // ═══════════════ ทางเข้า LINE (กลุ่มของ OA กลาง) ═══════════════

    /// <summary>
    /// รูป/ไฟล์ PDF ในกลุ่มของ OA กลาง · คืน false = กลุ่มนี้ไม่ใช่ของเส้นนี้ (ไม่ผูกผู้ติดต่อของบริษัทผู้ให้บริการ) ⇒ ผู้เรียกทำเหมือนเดิม (ไม่อ่าน ไม่เก็บ · PDPA ม.22) ·
    /// true = รับไว้แล้ว (ตอบการ์ดแล้วเสมอ ยกเว้นรูปที่ไม่ใช่สลิปเลย)
    /// </summary>
    public async Task<bool> HandleLineMediaAsync(string? destination, string groupId, string messageId, string? messageType, string? fileName,
        string? replyToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupId) || !LineChatSlipRule.IsSlipMessage(messageType, fileName)) return false;
        var (pid, enabled) = await ProviderAsync(ct);
        if (pid is not Guid providerId) return false;
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), groupId.Trim());
        var binding = await _channel.GroupBindingAsync(providerId, idHash, ct);
        if (!binding.AnyEndpoint || binding.ContactIds.Count == 0) return false;   // ด่านผูก: กลุ่มที่ไม่ได้ผูก ⇒ ไม่แตะอะไรต่อ
        if (!enabled)
        {
            // สวิตช์ปิด (ฝ่ายค้าน 272 L2): ทุกกลุ่มที่ผูกแล้วได้คำตอบ "ปิดรับชั่วคราว" (ไม่ให้คิดว่าส่งแล้วจบ) · ไม่อ่าน ไม่เก็บ
            if (!string.IsNullOrEmpty(replyToken))
                await _links.ReplyAsync(CustomerNotifyChannel.ScopeNextAcc, null, replyToken, TaxSlipReplyCard.Text(Card.IntakeOff, new(), false), ct);
            return true;
        }
        var msgId = messageId.Length > 64 ? messageId[..64] : messageId;
        if (await _db.TaxSlipEvidences.AsNoTracking().AnyAsync(s => s.CompanyId == providerId && s.Source == SourceLine && s.ExternalMessageId == msgId, ct))
            return true;   // ชั้น 1: webhook ซ้ำของข้อความเดียว — เหตุการณ์เดียวกัน ตอบไปแล้ว
        var bytes = await _links.DownloadNextAccMessageContentAsync(messageId, LineChatSlipRule.MaxBytes, ct);
        if (bytes == null || bytes.Length == 0)
        {
            if (!string.IsNullOrEmpty(replyToken))
                await _links.ReplyAsync(CustomerNotifyChannel.ScopeNextAcc, null, replyToken, "ได้รับรูปแล้วแต่ดาวน์โหลดจาก LINE ไม่สำเร็จ — กรุณาส่งรูปอีกครั้ง", ct);
            return true;
        }
        await IntakeAsync(providerId, SourceLine, msgId, idHash, binding, bytes, replyToken, null, false, ct);
        return true;
    }

    // ═══════════════ ทางเข้าอีเมล (P6) ═══════════════

    public sealed record InboundFile(string FileName, byte[] Bytes);

    /// <summary>อีเมลขาเข้าที่ผู้ให้บริการตรวจ SPF/DKIM แล้ว (ผู้เรียก = webhook ของ P6) · ผู้ส่งต้องเป็นอีเมลที่ผูกกับผู้ติดต่อของบริษัทผู้ให้บริการ ·
    /// คืนข้อความผล (ผู้เรียกบันทึก/ตอบ 200) · ไม่รู้จักผู้ส่ง ⇒ ไม่อ่านไฟล์ ไม่เก็บ</summary>
    /// <param name="senderVerified">ฝ่ายค้าน 272 H3: ผู้ส่งยืนยันโดเมนของ From ได้ (<see cref="InboundEmailRule.SenderVerified"/>) · false ⇒ รับเข้าคิวได้แต่ไม่บันทึกเอง</param>
    public async Task<string> HandleEmailAsync(string fromAddress, string messageId, IReadOnlyList<InboundFile> files, bool senderVerified, CancellationToken ct = default)
    {
        var from = (fromAddress ?? "").Trim().ToLowerInvariant();
        if (from.Length == 0 || from.Length > 254) return "ไม่มีผู้ส่ง";
        var (pid, enabled) = await ProviderAsync(ct);
        if (pid is not Guid providerId) return "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ";
        var binding = await _channel.EmailBindingAsync(providerId, from, ct);
        if (binding.ContactIds.Count == 0) return "ผู้ส่งไม่ได้ผูกกับผู้ติดต่อ — ไม่อ่านไฟล์";
        if (!enabled) return "ระบบปิดรับสลิปชั่วคราว";
        var key = QuotationAcceptance.Sha256Hex(from)!;
        var n = 0;
        foreach (var (f, i) in files.Select((f, i) => (f, i)).Take(5))
        {
            if (!LineChatSlipRule.IsSlipMessage(f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "file" : "image", f.FileName)) continue;
            if (f.Bytes.Length == 0 || f.Bytes.Length > LineChatSlipRule.MaxBytes) continue;
            var ext = (messageId ?? "").Trim();
            var extId = (ext.Length > 180 ? ext[..180] : ext) + "#" + i;
            if (await _db.TaxSlipEvidences.AsNoTracking().AnyAsync(s => s.CompanyId == providerId && s.Source == SourceEmail && s.ExternalMessageId == extId, ct)) continue;
            await IntakeAsync(providerId, SourceEmail, extId, key, binding, f.Bytes, null, from, !senderVerified, ct);
            n++;
        }
        return n == 0 ? "ไม่มีไฟล์แนบที่เป็นรูป/PDF สลิป" : $"รับไฟล์ {n} ไฟล์";
    }

    // ═══════════════ แกนกลาง ═══════════════

    private async Task IntakeAsync(Guid providerId, string source, string extId, string sourceKey, TaxSlipChannelService.Binding binding, byte[] bytes,
        string? replyToken, string? emailTo, bool senderUnverified, CancellationToken ct)
    {
        var sniffed = SlipEvidenceRule.DetectPublicType(bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed);
        if (sniffed == null || ext == null) return;   // ไม่ใช่รูป/PDF ⇒ ไม่ใช่งานของเส้นนี้ (ไม่เก็บ)
        if (sniffed == "application/pdf" && LineChatSlipRule.PdfPageCount(bytes) > LineChatSlipRule.MaxPdfPages)
        {
            await ReplyOnlyAsync(source, replyToken, emailTo, TaxSlipReplyCard.Text(Card.NotATaxSlip, new(), false), ct);
            return;
        }
        var sha = SlipEvidenceRule.Sha256Hex(bytes);
        var dupFile = await _db.TaxSlipEvidences.AsNoTracking().Where(s => s.CompanyId == providerId && s.FileSha256 == sha)
            .Select(s => new { s.Id, s.Status, s.ResolvedAt, s.CreatedAt, s.Source, s.SourceKey }).FirstOrDefaultAsync(ct);
        // รอบ 275 แพ็ก D (M3): ไฟล์เดิมที่ค้างคิวพนักงานจากช่องทางอื่น (ส่งผิดกลุ่มก่อน) ⇒ ประมวลแถวเดิมใหม่ด้วยชุดบริษัทของช่องทางนี้
        // (เดิม: "ได้รับไปแล้ว" แล้วสลิปติดกับกลุ่มผิดจนพนักงานกด "ไม่บันทึก") · แถวที่บันทึกแล้ว/กำลังรอลูกค้า ⇒ เหมือนเดิม
        if (dupFile != null && TaxSlipChannelRedirect.ShouldReprocess(dupFile.Status, dupFile.Source, dupFile.SourceKey, source, sourceKey))
        {
            await ReprocessFromNewChannelAsync(dupFile.Id, providerId, source, extId, sourceKey, binding, bytes, sniffed, replyToken, emailTo, senderUnverified, ct);
            return;
        }
        if (dupFile != null)
        {
            await ReplyOnlyAsync(source, replyToken, emailTo, TaxSlipReplyCard.Text(Card.Duplicate,
                new(RecordedAt: dupFile.Status == Statuses.Remitted ? dupFile.ResolvedAt : null, Reason: "ไฟล์เดียวกับที่ส่งมาแล้ว"), false), ct);
            return;
        }
        var reading = _reader.IsAvailable ? await _reader.ReadAsync(bytes, sniffed, ct) : OcrSlipText.Reading.Empty;
        // ฝ่ายค้าน 272 H2: ไม่มีข้อความให้ตัดสิน ⇒ ไม่รู้ว่าเป็นสลิปอะไร — เก็บแถวเข้าคิวพนักงาน (ไม่ใช่ "ไม่ใช่สลิปภาษี" · ไม่ทิ้งเงียบ)
        var unreadable = !_reader.IsAvailable || string.IsNullOrWhiteSpace(reading.Text);
        var agency = GovPaymentSlip.Identify(reading);
        if (!unreadable && agency.Kind == GovPaymentSlip.Kind.NotGovernment)
        {
            // ไม่ใช่สลิปรัฐ: รูปที่ดูเป็นสลิป ⇒ บอกว่าไม่ได้บันทึก · รูปทั่วไป ⇒ ไม่ตอบ ไม่เก็บ (PDPA ม.22)
            // รอบ 275 แพ็ก D (M5): สลิปโอนทั่วไปในกลุ่มลูกค้าอาจเป็นค่าบริการที่ถูกเรียกเก็บผ่าน OA กลาง (รอบ 262) — ไม่มีเส้นรับในกลุ่มนี้ ⇒
            // บอกทางไปต่อตามจริง + กระดิ่งพนักงาน (ยอด/วันที่เท่านั้น · ไม่เก็บไฟล์) · ส่งต่อเข้าเส้นจับคู่ใบแจ้งหนี้ = backlog (dev-275-D)
            if (reading.LooksLikeSlip)
            {
                await ReplyOnlyAsync(source, replyToken, emailTo, TaxSlipReplyCard.Text(Card.TransferNotTax, new(), false), ct);
                await NotifyTransferAsync(providerId, binding, reading, ct);
            }
            return;
        }
        var bill = reading.Bill;
        var payerBank = GovPaymentSlip.PayerBank(reading, reading.Text);
        var txnHash = TaxSlipRefHash.BankTxn(payerBank, bill?.TxnNo ?? reading.Qr?.TransactionRef);
        if (txnHash != null)
        {
            var dupTxn = await _db.TaxSlipEvidences.AsNoTracking().Where(s => s.CompanyId == providerId && s.BankCode == payerBank && s.BankTxnHash == txnHash)
                .Select(s => new { s.Status, s.ResolvedAt }).FirstOrDefaultAsync(ct);
            if (dupTxn != null)
            {
                await ReplyOnlyAsync(source, replyToken, emailTo, TaxSlipReplyCard.Text(Card.Duplicate,
                    new(RecordedAt: dupTxn.Status == Statuses.Remitted ? dupTxn.ResolvedAt : null, Reason: "รายการธนาคารเดียวกับสลิปที่ส่งมาแล้ว"), false), ct);
                return;
            }
        }
        var now = DateTime.UtcNow;
        var path = Path.Combine(_storage.Locator.RelativeBase, "wwwroot", "uploads", "tax-slips", now.ToString("yyyy-MM"), $"{Guid.NewGuid():N}{ext}");
        var row = new TaxSlipEvidence
        {
            CompanyId = providerId, Source = source, ExternalMessageId = extId, SourceKey = sourceKey,
            ContactId = binding.ContactIds.Count == 1 ? binding.ContactIds[0] : null, FileSha256 = sha, StoragePath = path, ContentType = sniffed,
            Agency = agency.Row?.Agency.ToString(), BankCode = payerBank, BankTxnHash = txnHash, BankTxnLast4 = TaxSlipRefHash.Last4(bill?.TxnNo),
            ControlCodeHash = TaxSlipRefHash.ControlCode(agency.ControlCode), ControlCodeLast4 = TaxSlipRefHash.Last4(agency.ControlCode),
            TaxIdMasked = agency.TaxId != null ? TaxSlipReplyCard.MaskTaxId(agency.TaxId) : null,
            Amount = reading.Amount, Fee = bill?.Fee, SlipDate = reading.Date, Status = Statuses.Received, ShowAmounts = binding.ShowAmounts,
            ClientCompanyId = binding.ClientCompanyIds.Count == 1 ? binding.ClientCompanyIds[0] : null, CreatedBy = source + "-intake",
            SenderUnverified = senderUnverified,
        };
        if (unreadable) { row.Status = Statuses.ToStaff; row.RuleCode = OcrOffRuleCode; row.Reason = OcrOffReason; }
        await _storage.WriteAsync(path, bytes, sniffed, ct);
        try
        {
            await EnsureRopaAsync(providerId, ct);
            _db.TaxSlipEvidences.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // คำขอพร้อมกัน (LINE ส่งซ้ำระหว่างอ่าน) — แถวแรกชนะ · ไฟล์ของรอบนี้ไม่มีเจ้าของ ⇒ ลบ
            _db.ChangeTracker.Clear();
            await _storage.DeleteAsync(path, CancellationToken.None);
            return;
        }
        if (unreadable)
        {
            await FinishAsync(row, Card.ToStaff, new(Reason: OcrOffReason), replyToken, emailTo, staff: true, ct);
            return;
        }
        await ProcessAsync(row, reading, agency, binding, replyToken, emailTo, ct);
    }

    /// <summary>รอบ 275 แพ็ก D (M3): แถวเดิม (รอพนักงาน · ช่องทางอื่น) ⇒ ย้ายมาช่องทางนี้แล้วเดินตัวตัดสินใหม่ทั้งชุด · audit การย้าย</summary>
    private async Task ReprocessFromNewChannelAsync(Guid rowId, Guid providerId, string source, string extId, string sourceKey, TaxSlipChannelService.Binding binding,
        byte[] bytes, string contentType, string? replyToken, string? emailTo, bool senderUnverified, CancellationToken ct)
    {
        var row = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == rowId && s.CompanyId == providerId, ct);
        if (row == null || row.Status != Statuses.ToStaff) return;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = providerId, Action = Accounting.Models.Enums.AuditAction.Update, EntityType = nameof(TaxSlipEvidence), EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipResentFromOtherChannel", source, previousRuleCode = row.RuleCode, clients = binding.ClientCompanyIds }),
            Timestamp = DateTime.UtcNow,
        });
        row.Source = source; row.ExternalMessageId = extId; row.SourceKey = sourceKey; row.ShowAmounts = binding.ShowAmounts;
        row.ContactId = binding.ContactIds.Count == 1 ? binding.ContactIds[0] : null;
        row.ClientCompanyId = binding.ClientCompanyIds.Count == 1 ? binding.ClientCompanyIds[0] : null;
        row.SenderUnverified = senderUnverified; row.CandidatesJson = null; row.PreselectAccounts = null; row.TaxPaymentRequestId = null;
        row.Status = Statuses.Received; row.RuleCode = null; row.Reason = null; row.StaffRemindedAt = null; row.UpdatedAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();   // webhook ซ้ำของข้อความเดียวกันระหว่างนี้ — รอบแรกชนะ
            return;
        }
        var reading = _reader.IsAvailable ? await _reader.ReadAsync(bytes, contentType, ct) : OcrSlipText.Reading.Empty;
        if (!_reader.IsAvailable || string.IsNullOrWhiteSpace(reading.Text))
        {
            row.Status = Statuses.ToStaff; row.RuleCode = OcrOffRuleCode; row.Reason = OcrOffReason;
            await FinishAsync(row, Card.ToStaff, new(Reason: OcrOffReason), replyToken, emailTo, staff: true, ct);
            return;
        }
        await ProcessAsync(row, reading, GovPaymentSlip.Identify(reading), binding, replyToken, emailTo, ct);
    }

    /// <summary>รอบ 275 แพ็ก D (M5): สลิปโอนทั่วไปในกลุ่มของ OA กลาง — กระดิ่งพนักงาน (ไม่มีแถว · ไม่เก็บไฟล์) ให้ตรวจเงินเข้าค่าบริการของผู้ติดต่อนั้น</summary>
    private async Task NotifyTransferAsync(Guid providerId, TaxSlipChannelService.Binding binding, OcrSlipText.Reading reading, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            var name = binding.ContactIds.Count == 1
                ? await _db.Contacts.AsNoTracking().Where(c => c.Id == binding.ContactIds[0] && c.CompanyId == providerId).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
            await _notify.DispatchAsync(providerId, NotificationEvents.TaxSlipNeedsStaff, new NotificationContext
            {
                Title = "💸 ลูกค้าส่งสลิปโอนเงิน (ไม่ใช่สลิปภาษี) ในกลุ่ม",
                Message = (name ?? "ผู้ติดต่อในกลุ่ม") + (reading.Amount is decimal a ? $" · ยอด {a:N2} บาท" : "") + (reading.Date is DateTime d ? " · " + TaxSlipReplyCard.ThaiDay(d) : "")
                    + " — อาจเป็นการชำระค่าบริการ ตรวจรายการเงินเข้า/ใบแจ้งหนี้ของผู้ติดต่อนี้ (ระบบไม่ได้เก็บรูป)",
                ActionUrl = "/pages/tax-slip-desk.html",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "แจ้งพนักงานเรื่องสลิปโอนในกลุ่มไม่สำเร็จ"); }
    }

    /// <summary>ฝ่ายค้าน 272 M6: RoPA (PDPA ม.39) ของเส้นนี้ในบริษัทผู้ให้บริการ — สร้างครั้งแรกที่รับสลิป · ผู้ดูแลข้อมูลทบทวนได้ที่หน้า PDPA</summary>
    private async Task EnsureRopaAsync(Guid providerId, CancellationToken ct)
    {
        if (await _db.PdpaProcessingActivities.AsNoTracking().AnyAsync(r => r.CompanyId == providerId && r.Purpose == RopaPurpose, ct)) return;
        if (_db.ChangeTracker.Entries<PdpaProcessingActivity>().Any(e => e.Entity.CompanyId == providerId && e.Entity.Purpose == RopaPurpose)) return;
        _db.PdpaProcessingActivities.Add(new PdpaProcessingActivity
        {
            CompanyId = providerId, Purpose = RopaPurpose, LegalBasis = "Contract", DataCategories = RopaDataCategories, RetentionPeriod = RopaRetention,
            Recipients = "พนักงานสำนักงานบัญชีที่มีสิทธิ์ยื่นภาษี · บริษัทลูกค้า (ไฟล์แนบรายการนำส่ง) · LY Corporation (LINE) / ผู้ให้บริการอีเมลขาเข้า",
            TransfersOutsideThailand = false, Notes = "ระบบสร้างอัตโนมัติเมื่อรับสลิปภาษีครั้งแรก (รอบ 272)", CreatedBy = "system", LastReviewedBy = "system",
        });
    }

    /// <summary>ฝ่ายค้าน 272 M6 — ลบไฟล์สลิปของแถวที่<b>ปิดโดยไม่บันทึก</b>เมื่อพ้น <see cref="ClosedFileRetentionDays"/> วัน (ผู้เรียก: <c>LineChatSlipRetentionJob</c> · JobLock) ·
    /// แถวที่บันทึกแล้ว: ไฟล์ย้ายเป็นหลักฐานของลูกค้าแล้ว (legal hold ตามอายุเอกสารบัญชี — ไม่แตะไฟล์แนบนั้น) · แถวที่รอตัดสิน: ไม่ลบ (MAX retention) · คืนจำนวนไฟล์ที่ลบ</summary>
    public static async Task<int> PurgeClosedFilesAsync(AccountingDbContext db, Accounting.Services.Storage.IFileStorage storage, ILogger logger, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-ClosedFileRetentionDays);
        // รอบ 275 แพ็ก D (M4): เดิมผู้เขียน Rejected ลบไฟล์ทันที ⇒ ตัวกรองนี้ว่างเสมอ (control ไม่มีผล) — ตอนนี้ปิดแล้วเก็บไฟล์ไว้จนครบระยะ แล้วลบที่นี่ที่เดียว
        var rows = await db.TaxSlipEvidences.Where(s => s.FilePurgedAt == null && s.StatutoryRemittanceId == null && Statuses.ClosedUnrecorded.Contains(s.Status)
                && (s.ResolvedAt ?? s.UpdatedAt ?? s.CreatedAt) < cutoff)
            .OrderBy(s => s.CreatedAt).Take(500).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
        {
            try { await storage.DeleteAsync(r.StoragePath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "ลบไฟล์สลิปภาษี {Slip} ที่พ้นระยะเก็บไม่สำเร็จ — รอบหน้าลองใหม่", r.Id); continue; }
            r.FilePurgedAt = nowUtc; r.UpdatedAt = nowUtc;
            r.FileSha256 = "purged:" + r.Id.ToString("N");
            r.BankTxnHash = null;
            n++;
        }
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }

    private async Task ReplyOnlyAsync(string source, string? replyToken, string? emailTo, string text, CancellationToken ct)
    {
        if (source == SourceLine && !string.IsNullOrEmpty(replyToken))
            await _links.ReplyAsync(CustomerNotifyChannel.ScopeNextAcc, null, replyToken, TaxSlipReplyCard.Clean(text), ct);
        // อีเมล: ไม่มีแถว ⇒ ไม่ตอบ (ไม่ยืนยันกับผู้ส่งที่ระบบไม่ได้เก็บอะไร) — ผู้เรียก webhook ได้ข้อความผลแทน
    }

    /// <summary>ตัดสินและทำต่อ (สลิปใหม่ หรือประมวลใหม่หลังได้ความยินยอม) — ล้มทุกชนิด ⇒ แถวเข้าคิวพนักงาน + การ์ด Failed (ไม่หายเงียบ)</summary>
    private async Task ProcessAsync(TaxSlipEvidence row, OcrSlipText.Reading reading, GovPaymentSlip.Result agency, TaxSlipChannelService.Binding? binding,
        string? replyToken, string? emailTo, CancellationToken ct)
    {
        try
        {
            await DecideAndActAsync(row, reading, agency, binding, replyToken, emailTo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ประมวลสลิปภาษี {Slip} ล้ม — เข้าคิวพนักงาน", row.Id);
            _db.ChangeTracker.Clear();
            var fresh = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == row.Id && s.CompanyId == row.CompanyId, CancellationToken.None);
            if (fresh == null || !Statuses.Open.Contains(fresh.Status)) return;   // รอบ 275 D (H2): แถวที่ปิด/บันทึกแล้วห้ามถูกตั้งกลับ
            fresh.Status = Statuses.ToStaff; fresh.RuleCode = "TAX-SLIP-FAILED"; fresh.Reason = Clip("ระบบล้มระหว่างประมวล: " + ex.Message, 500); fresh.UpdatedAt = DateTime.UtcNow;
            await _channel.SendSlipCardAsync(fresh, TaxSlipReplyCard.Text(Card.Failed, new(), fresh.ShowAmounts), replyToken, emailTo, null, CancellationToken.None);
            await _db.SaveChangesAsync(CancellationToken.None);
            await NotifyStaffAsync(fresh, CancellationToken.None);
        }
    }

    private sealed record CandidateJson(Guid C, string T, int Y, int M, decimal A, decimal S, decimal P, Guid? R);

    private static string CandidatesJson(IEnumerable<TaxSlipMatch.Candidate> cs)
        => JsonSerializer.Serialize(cs.Select(c => new CandidateJson(c.CompanyId, c.Type, c.Year, c.Month, c.Amount, c.Surcharge, c.Penalty, c.RequestId)).ToList());

    public static IReadOnlyList<TaxSlipMatch.Candidate> ParseCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TaxSlipMatch.Candidate>();
        try
        {
            return (JsonSerializer.Deserialize<List<CandidateJson>>(json) ?? new()).Select(c => new TaxSlipMatch.Candidate(c.C, c.T, c.Y, c.M, c.A, c.S, c.P, c.R)).ToList();
        }
        catch (JsonException) { return Array.Empty<TaxSlipMatch.Candidate>(); }
    }

    public static string FormLabel(string type, int year, int month)
        => ProviderConsent.LabelOf(type).Split(' ')[0] + (type is "Pnd50" or "Pnd51" ? " ปี " + (year + 543) : " งวด " + ThaiDate.ShortMonthBe(year, month));

    private async Task DecideAndActAsync(TaxSlipEvidence row, OcrSlipText.Reading reading, GovPaymentSlip.Result agency, TaxSlipChannelService.Binding? binding,
        string? replyToken, string? emailTo, CancellationToken ct)
    {
        var providerId = row.CompanyId;
        // ชุดบริษัทของช่องทาง: สลิปใหม่ = ผลผูกตอนรับ · ประมวลใหม่ = LINE ผูกกลุ่มเดิมสด ๆ (ปลดกลุ่มแล้ว ⇒ ว่าง) / อีเมล = บริษัทที่ตัดสินไว้รอบแรก
        if (binding == null && row.Source == SourceLine) binding = await _channel.GroupBindingAsync(providerId, row.SourceKey, ct);
        IReadOnlyList<Guid> clientIds = binding?.ClientCompanyIds ?? (row.ClientCompanyId is Guid only ? new[] { only } : Array.Empty<Guid>());
        // ── ชุดบริษัทที่ช่องทางนี้ผูกเท่านั้น (ห้ามค้นเลขภาษีทั้งฐาน — กฎ M) ──
        var companies = clientIds.Count == 0 ? new List<TaxSlipMatch.CompanyKey>()
            : await _db.Companies.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => new TaxSlipMatch.CompanyKey(c.Id, c.TaxId)).ToListAsync(ct);
        var mineIds = companies.Where(c => agency.TaxId != null && ThaiTaxId.Same(c.TaxId, agency.TaxId)).Select(c => c.Id).ToList();
        var requests = mineIds.Count == 0 ? new List<TaxSlipMatch.RequestKey>()
            : await _db.TaxPaymentRequests.AsNoTracking().Where(r => mineIds.Contains(r.CompanyId) && r.ProviderCompanyId == providerId)
                .Select(r => new TaxSlipMatch.RequestKey(r.Id, r.CompanyId, r.RemittanceType, r.PeriodYear, r.PeriodMonth, r.Amount, r.Surcharge, r.Penalty,
                    r.ControlCodeHash, r.Status, r.BooksMismatch, r.ControlCodeExpiresAt, r.IsAmendment)).ToListAsync(ct);
        var pending = new List<TaxSlipMatch.PendingKey>();
        foreach (var cid in mineIds)
        {
            var dash = await _remit.GetDashboardAsync(cid, 24, includeOldAnnual: true);
            pending.AddRange(dash.Pending.Select(p => new TaxSlipMatch.PendingKey(cid, p.RemittanceType, p.PeriodYear, p.PeriodMonth, p.Amount,
                p.RemittanceType is "Pnd50" or "Pnd51" ? null : new DateTime(p.PeriodYear, Math.Clamp(p.PeriodMonth, 1, 12), 1).AddMonths(1), p.EFilingDueDate)));
        }
        var remittedFull = mineIds.Count == 0 ? new List<TaxSlipMatch.RemittedKey>()
            : await _db.StatutoryRemittances.AsNoTracking().Where(r => mineIds.Contains(r.CompanyId) && !r.IsDeleted)
                .Select(r => new TaxSlipMatch.RemittedKey(r.CompanyId, r.RemittanceType, r.PeriodYear, r.PeriodMonth, r.Amount + r.LateFee + r.Penalty + r.OtherFine)).ToListAsync(ct);
        var remittedRows = remittedFull.Select(r => (r.CompanyId, r.Type, r.Year, r.Month)).ToList();
        // ภ.พ.36 นำส่งได้หลายครั้งต่องวด (คำตัดสินข้อ 133) — ไม่นับเป็น "นำส่งแล้ว"
        var remitted = remittedRows.Where(r => r.Item2 != "VatPp36").ToHashSet();
        var ccRemitted = row.ControlCodeHash != null && mineIds.Count > 0 && await _db.TaxSlipEvidences.AsNoTracking()
            .AnyAsync(s => s.CompanyId == providerId && s.Id != row.Id && s.Status == Statuses.Remitted && s.ControlCodeHash == row.ControlCodeHash
                && s.ClientCompanyId != null && mineIds.Contains(s.ClientCompanyId.Value), ct);
        var slip = new TaxSlipMatch.Slip(agency, reading.Amount ?? -1m, reading.Bill?.Fee, reading.Date ?? DateTime.MinValue, row.ControlCodeHash);
        TaxSlipMatch.Decision d;
        if (reading.Amount == null || reading.Date == null)
            d = new TaxSlipMatch.Decision(Outcome.None, mineIds.Count == 1 ? mineIds[0] : null, null, Array.Empty<TaxSlipMatch.Candidate>(), false, TaxSlipMatch.Codes.None,
                "อ่านยอด/วันที่บนสลิปไม่ได้แน่นอน — ให้พนักงานตรวจ");
        else d = TaxSlipMatch.Decide(slip, companies, requests, pending, remitted, ccRemitted, remittedFull.Where(r => r.Type != "VatPp36").ToList());
        // รอบ 275 แพ็ก D (L4): ลูกค้าเลือกรายการในกลุ่มแล้ว (แถวเหลือผู้สมัครที่เลือกตัวเดียว) แต่ไปติดรอความยินยอม ⇒ ประมวลใหม่ได้ Many อีก — ใช้ตัวที่ลูกค้าเลือก
        // (ยังอยู่ในชุดผู้สมัครที่ตัดสินใหม่ · เดินด่านที่เหลือครบ) แทนการโยนเข้าคิวพนักงานแล้วตัวเลือกหาย
        var prior = ParseCandidates(row.CandidatesJson);
        if (d.Outcome == Outcome.Many && prior.Count == 1 && d.Candidates.Contains(prior[0]))
            d = d with { Outcome = Outcome.One, Chosen = prior[0], CompanyId = prior[0].CompanyId, Candidates = prior, Auto = true, RuleCode = TaxSlipMatch.Codes.Many + "-CHOSEN" };

        row.RuleCode = d.RuleCode;
        row.Reason = Clip(d.Reason, 500);
        row.ClientCompanyId = d.CompanyId ?? row.ClientCompanyId;
        row.CandidatesJson = d.Candidates.Count > 0 ? CandidatesJson(d.Candidates) : null;
        row.TaxPaymentRequestId = d.Chosen?.RequestId ?? row.TaxPaymentRequestId;
        row.UpdatedAt = DateTime.UtcNow;
        var co = d.CompanyId is Guid cc ? await _db.Companies.AsNoTracking().Where(c => c.Id == cc).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var facts = new TaxSlipReplyCard.Facts(CompanyName: co, Amount: reading.Amount, ControlLast4: row.ControlCodeLast4, TaxIdOnSlip: agency.TaxId, Reason: d.Reason,
            FormLabel: d.Chosen is { } ch0 ? FormLabel(ch0.Type, ch0.Year, ch0.Month) : null,
            ExpectedAmount: d.Candidates.Count == 1 ? d.Candidates[0].Amount + d.Candidates[0].Surcharge + d.Candidates[0].Penalty : null);

        switch (d.Outcome)
        {
            case Outcome.One when d.Auto && d.Chosen != null && row.SenderUnverified:
                row.Status = Statuses.ToStaff;
                row.RuleCode = "TAX-SLIP-SENDER";
                row.Reason = InboundEmailRule.UnverifiedReason;
                row.PreselectAccounts = null;
                await FinishAsync(row, Card.ToStaff, facts with { Reason = InboundEmailRule.UnverifiedReason }, replyToken, emailTo, staff: true, ct);
                return;
            case Outcome.One when d.Auto && d.Chosen != null:
                await AutoApplyAsync(row, reading, d.Chosen, facts, replyToken, emailTo, ct);
                return;
            case Outcome.Many:
                await AskChoiceAsync(row, d, facts, replyToken, emailTo, ct);
                return;
            case Outcome.Duplicate:
                // ฝ่ายค้าน 272 L5: Control Code ซ้ำ = อาจจ่ายซ้ำจริง (ต้องขอคืนจากกรม) ⇒ คิวพนักงาน + กระดิ่ง (ไม่ปิดเงียบ)
                row.Status = Statuses.ToStaff;
                await FinishAsync(row, Card.Duplicate, facts, replyToken, emailTo, staff: true, ct);
                return;
            default:
                row.Status = Statuses.ToStaff;
                await FinishAsync(row, d.Outcome switch
                {
                    Outcome.UnknownAgency => Card.UnknownAgency,
                    Outcome.TaxIdNotInGroup => Card.TaxIdMismatch,
                    Outcome.AmountMismatch => Card.AmountMismatch,
                    Outcome.None => Card.NoMatch,
                    Outcome.NoTenant => Card.NoTenant,
                    _ => Card.ToStaff,
                }, facts, replyToken, emailTo, staff: true, ct);
                return;
        }
    }

    private async Task FinishAsync(TaxSlipEvidence row, Card card, TaxSlipReplyCard.Facts facts, string? replyToken, string? emailTo, bool staff, CancellationToken ct,
        IReadOnlyList<object>? quick = null)
    {
        await _channel.SendSlipCardAsync(row, TaxSlipReplyCard.Text(card, facts, row.ShowAmounts), replyToken, emailTo, quick, ct);
        await _db.SaveChangesAsync(ct);
        if (staff) await NotifyStaffAsync(row, ct);
    }

    private async Task AutoApplyAsync(TaxSlipEvidence row, OcrSlipText.Reading reading, TaxSlipMatch.Candidate cand, TaxSlipReplyCard.Facts facts,
        string? replyToken, string? emailTo, CancellationToken ct)
    {
        var providerId = row.CompanyId;
        var client = cand.CompanyId;
        var total = cand.Amount + cand.Surcharge + cand.Penalty;
        var verdict = await _consent.CheckAsync(client, providerId, cand.Type, total, ct);
        if (!verdict.Ok)
        {
            if (verdict.Code is ProviderConsent.Codes.NoConsent or ProviderConsent.Codes.Withdrawn or ProviderConsent.Codes.GrantorLostPermission or ProviderConsent.Codes.TextOutdated)
            {
                // T3: พักไว้รอความยินยอม (ไม่เขียนบัญชีลูกค้า) + เปิดคำขอถ้ายังไม่มี
                row.Status = Statuses.AwaitingConsent;
                row.Reason = Clip(verdict.Reason, 500);
                row.RuleCode = ProviderConsent.RuleCode + "-" + verdict.Code;
                await _consent.EnsureRequestAsync(client, providerId, ProviderConsentService.ViaFirstSlip, null, ct);
                var held = await _db.TaxSlipEvidences.AsNoTracking().CountAsync(s => s.CompanyId == providerId && s.ClientCompanyId == client && s.Status == Statuses.AwaitingConsent && s.Id != row.Id, ct) + 1;
                await FinishAsync(row, held > 1 ? Card.AwaitingConsentMore : Card.AwaitingConsent,
                    facts with { HeldCount = held, Link = await AbsoluteAsync(ProviderConsent.PageUrl(client), ct) }, replyToken, emailTo, staff: false, ct);
                return;
            }
            row.Status = Statuses.ToStaff;
            row.Reason = Clip(verdict.Reason, 500);
            row.RuleCode = ProviderConsent.RuleCode + "-" + verdict.Code;
            await FinishAsync(row, Card.ToStaff, facts with { Reason = verdict.Reason }, replyToken, emailTo, staff: true, ct);
            return;
        }
        var bank = await BankAsync(client, providerId, reading, ct);
        if (!bank.CanAuto)
        {
            row.Status = Statuses.ToStaff;
            row.RuleCode = TaxSlipBankAccount.RuleCode + "-" + bank.Kind;
            row.Reason = Clip(bank.Reason, 500);
            row.PreselectAccounts = bank.Preselect.Count > 0 ? string.Join(",", bank.Preselect) : null;
            await FinishAsync(row, bank.Kind == TaxSlipBankAccount.Kind.NotOurs ? Card.ToStaff : Card.NeedsAccount, facts with { Reason = bank.Reason }, replyToken, emailTo, staff: true, ct);
            return;
        }
        await _db.SaveChangesAsync(ct);
        await ApplyAndReplyAsync(row.Id, providerId, cand, bank.AccountId, null, null, false, reading.Date!.Value, bank.Label, facts, replyToken, emailTo, ct);
    }

    private async Task<TaxSlipBankAccount.Result> BankAsync(Guid client, Guid providerId, OcrSlipText.Reading reading, CancellationToken ct)
    {
        var accounts = await _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == client && b.IsActive && !b.IsDeleted)
            .Select(b => new SlipAccountMatch.AccountKey(b.Id, b.BankName, b.AccountNumber)).ToListAsync(ct);
        var match = SlipAccountMatch.Match(reading.Payer, accounts);
        var (defId, defBank) = await _consent.DefaultPayAccountAsync(client, providerId, ct);
        var sameBank = defBank == null ? 0 : accounts.Count(a => ThaiBankCodes.FromText(a.BankName) == defBank);
        return TaxSlipBankAccount.Decide(match, reading.Payer, defId, defBank, sameBank);
    }

    /// <summary>บันทึกผ่านผู้เขียนตัวเดียว แล้วตอบการ์ด · ด่าน/RemitAsync ปฏิเสธ ⇒ คิวพนักงานพร้อมเหตุจริง (ไม่กลืน)</summary>
    private async Task<bool> ApplyAndReplyAsync(Guid slipId, Guid providerId, TaxSlipMatch.Candidate cand, Guid? bankAccountId, Guid? bankGlAccountId, Guid? staffUserId,
        bool staffIsClientFiler, DateTime payDate, string? accountLabel, TaxSlipReplyCard.Facts facts, string? replyToken, string? emailTo, CancellationToken ct,
        bool manualEntry = false, decimal? staffSlipAmount = null)
    {
        // failure = เหตุจริง (แถว/พนักงานเห็น) · cardReason = เหตุที่ส่งเข้ากลุ่มลูกค้า — ข้อความ exception ชนิดทั่วไป (อาจเป็นของ framework) ไม่ถึงกลุ่ม (L2 · กฎ #4 E)
        string? failure = null, failureCode = null, cardReason = null;
        try
        {
            await _remitter.ApplyAsync(new TaxSlipRemitService.ApplyRequest(providerId, slipId, cand, bankAccountId, bankGlAccountId, staffUserId, staffIsClientFiler, payDate,
                accountLabel, manualEntry, staffSlipAmount), ct);
        }
        catch (TaxSlipRemitService.TaxSlipGateException gx) { failure = cardReason = gx.Message; failureCode = gx.RuleCode; }
        catch (BusinessRuleException bx) { failure = cardReason = bx.Message; failureCode = bx.RuleCode ?? "REMIT-REFUSED"; }
        catch (InvalidOperationException ix) { failure = ix.Message; failureCode = "REMIT-REFUSED"; }
        catch (KeyNotFoundException kx) { failure = kx.Message; failureCode = "REMIT-REFUSED"; }
        catch (DbUpdateException dx) when (dx.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // ฝ่ายค้าน 272 L4: ชนกับเส้นมือ/อีกช่องทาง (unique งวด/Control Code) — บอกเหตุจริง ไม่ใช่ 500
            failure = cardReason = "งวดนี้/Control Code นี้ถูกบันทึกจากทางอื่นระหว่างนี้แล้ว — ตรวจประวัตินำส่งของลูกค้า"; failureCode = TaxSlipMatch.Codes.Remitted;
        }
        cardReason ??= "ระบบบันทึกไม่ได้ — พนักงานจะตรวจ";
        var f = facts with { FormLabel = FormLabel(cand.Type, cand.Year, cand.Month), Tax = cand.Amount, Surcharge = cand.Surcharge, Penalty = cand.Penalty,
            AccountLabel = accountLabel, PayDate = payDate, Amount = cand.Amount + cand.Surcharge + cand.Penalty };
        if (failure == null)
        {
            var row = await _db.TaxSlipEvidences.FirstAsync(s => s.Id == slipId && s.CompanyId == providerId, ct);
            await FinishAsync(row, staffUserId == null ? Card.Remitted : Card.StaffRemitted, f, replyToken, emailTo, staff: false, ct);
            return true;
        }
        // รอบ 275 แพ็ก D (H2): อ่านแถวใหม่จากฐาน (ไม่ใช่ tracker) — อีกคำขอบันทึกไปแล้ว/ปิดไปแล้ว ⇒ ห้ามเขียนทับ (เดิม Remitted ถูกตั้งกลับเป็น ToStaff + การ์ด "ยังไม่ได้บันทึก")
        _db.ChangeTracker.Clear();
        var fresh = await _db.TaxSlipEvidences.FirstAsync(s => s.Id == slipId && s.CompanyId == providerId, ct);
        if (!Statuses.Open.Contains(fresh.Status))
        {
            if (staffUserId != null)
                throw new BusinessRuleException("สลิปนี้" + (fresh.Status == Statuses.Remitted ? "บันทึกไปแล้ว" : "ปิดแล้ว (" + StatusLabel(fresh.Status) + ")") + " — ไม่บันทึกซ้ำ",
                    fresh.Status == Statuses.Remitted ? TaxSlipMatch.Codes.Duplicate : TaxSlipRemitService.ClosedRuleCode, 409);
            return false;
        }
        // รอบ 275 แพ็ก D (M2): พนักงานกดแล้วล้ม ⇒ ตอบผู้กด 409 อย่างเดียว — ไม่ส่งการ์ดเข้ากลุ่มลูกค้า ไม่เปลี่ยนเหตุเดิมของแถว (เดิมกด 3 ครั้ง = การ์ด 3 ใบ)
        if (staffUserId != null) throw new BusinessRuleException("บันทึกไม่ได้ — " + failure, failureCode, 409);
        fresh.Status = Statuses.ToStaff;
        fresh.RuleCode = Clip(failureCode, 40);
        fresh.Reason = Clip(failure, 500);
        fresh.UpdatedAt = DateTime.UtcNow;
        await FinishAsync(fresh, Card.ToStaff, f with { Reason = cardReason }, replyToken, emailTo, staff: true, ct);
        return false;
    }

    private async Task AskChoiceAsync(TaxSlipEvidence row, TaxSlipMatch.Decision d, TaxSlipReplyCard.Facts facts, string? replyToken, string? emailTo, CancellationToken ct)
    {
        var labels = d.Candidates.Select(c => FormLabel(c.Type, c.Year, c.Month)).ToList();
        var canAsk = row.Source == SourceLine && !string.IsNullOrEmpty(replyToken) && d.Candidates.Count is > 1 and <= 4 && d.Candidates.Select(c => c.CompanyId).Distinct().Count() == 1;
        if (!canAsk)
        {
            row.Status = Statuses.ToStaff;
            await FinishAsync(row, Card.ToStaff, facts, replyToken, emailTo, staff: true, ct);
            return;
        }
        row.Status = Statuses.AwaitingChoice;
        row.ChoiceExpiresAt = DateTime.UtcNow + ChoiceTtl;
        row.ClientCompanyId = d.Candidates[0].CompanyId;
        var items = new List<object>();
        for (var i = 0; i < d.Candidates.Count; i++)
            items.Add(new { type = "action", action = new { type = "postback", label = Trim20(labels[i]), data = TaxSlipRefHash.PostbackData(row.Id, i, row.SourceKey), displayText = labels[i] } });
        items.Add(new { type = "action", action = new { type = "postback", label = "ไม่ใช่ทั้งหมด", data = TaxSlipRefHash.PostbackData(row.Id, -1, row.SourceKey), displayText = "ไม่ใช่ทั้งหมด" } });
        await FinishAsync(row, Card.NeedsChoice, facts with { Choices = labels }, replyToken, emailTo, staff: false, ct, items);
    }

    private static string Trim20(string s) => s.Length <= 20 ? s : s[..19] + "…";

    /// <summary>ปุ่มเลือกในกลุ่ม (postback) — ตรวจลายเซ็น + ช่องทางเดิม + ยังรอเลือก + ไม่หมดอายุ · เลือกได้เฉพาะในชุดผู้สมัครที่ตัวตัดสินคืน ·
    /// <b>ปุ่มนี้ไม่ใช่การยินยอม</b> (ความยินยอมตรวจซ้ำใต้ล็อกตอนบันทึก) · คืนข้อความตอบ</summary>
    public async Task<string?> HandleChoicePostbackAsync(string? destination, string groupId, string? data, string? replyToken, CancellationToken ct = default)
    {
        var pb = TaxSlipRefHash.TryParse(data);
        if (pb == null) return null;
        var (pid, enabled) = await ProviderAsync(ct);
        if (pid is not Guid providerId || !enabled) return TaxSlipReplyCard.Text(Card.IntakeOff, new(), false);
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), groupId.Trim());
        if (!TaxSlipRefHash.Verify(pb, idHash)) return "ปุ่มนี้ใช้ไม่ได้ — กรุณาส่งสลิปใหม่";
        // ฝ่ายค้าน 272 L1: กลุ่มต้องยังผูกอยู่ (ปลดกลุ่มแล้ว ⇒ ปุ่มเดิมใช้ไม่ได้)
        if (!(await _channel.GroupBindingAsync(providerId, idHash, ct)).AnyEndpoint) return "กลุ่มนี้ไม่ได้ผูกกับสำนักงานบัญชีแล้ว — ปุ่มนี้ใช้ไม่ได้";
        var row = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == pb.SlipId && s.CompanyId == providerId && s.SourceKey == idHash, ct);
        if (row == null || row.Status != Statuses.AwaitingChoice || row.ChoiceExpiresAt is DateTime exp && exp < DateTime.UtcNow)
            return "ตัวเลือกนี้หมดอายุหรือถูกเลือกไปแล้ว — พนักงาน NextAcc จะตรวจให้";
        var cands = ParseCandidates(row.CandidatesJson);
        if (pb.Index == -1 || pb.Index >= cands.Count)
        {
            var declined = await _db.TaxSlipEvidences.Where(s => s.Id == row.Id && s.CompanyId == providerId && s.Status == Statuses.AwaitingChoice)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, Statuses.ToStaff).SetProperty(s => s.Reason, "ลูกค้าตอบว่าไม่ใช่รายการที่ระบบเสนอ")
                    .SetProperty(s => s.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
            if (declined == 0) return "ตัวเลือกนี้หมดอายุหรือถูกเลือกไปแล้ว — พนักงาน NextAcc จะตรวจให้";
            await _db.Entry(row).ReloadAsync(ct);
            await NotifyStaffAsync(row, ct);
            return TaxSlipReplyCard.Text(Card.ToStaff, new(Reason: row.Reason, ControlLast4: row.ControlCodeLast4), row.ShowAmounts);
        }
        var cand = cands[pb.Index];
        // รอบ 275 แพ็ก D (H2): claim แบบ atomic — สองคนกดคนละปุ่มพร้อมกัน (หรือ redelivery) ⇒ ชนะคนเดียว (เดิมทั้งคู่ผ่านด่านสถานะที่อ่านก่อนเขียน ⇒ สลิปเดียวบันทึกสองงวด)
        var chosenJson = CandidatesJson(new[] { cand });
        var claimed = await _db.TaxSlipEvidences
            .Where(s => s.Id == row.Id && s.CompanyId == providerId && s.Status == Statuses.AwaitingChoice)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, Statuses.Received).SetProperty(s => s.CandidatesJson, chosenJson)
                .SetProperty(s => s.RuleCode, TaxSlipMatch.Codes.Many + "-CHOSEN").SetProperty(s => s.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
        if (claimed == 0) return "ตัวเลือกนี้หมดอายุหรือถูกเลือกไปแล้ว — พนักงาน NextAcc จะตรวจให้";
        await _db.Entry(row).ReloadAsync(ct);
        var slipId = row.Id;
        try
        {
            var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
            if (bytes == null) { await MarkStaffAsync(row, "อ่านไฟล์สลิปจากที่เก็บไม่ได้", ct); return TaxSlipReplyCard.Text(Card.Failed, new(), false); }
            var reading = await _reader.ReadAsync(bytes, row.ContentType, ct);
            var co = await _db.Companies.AsNoTracking().Where(c => c.Id == cand.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            var facts = new TaxSlipReplyCard.Facts(CompanyName: co, Amount: reading.Amount, ControlLast4: row.ControlCodeLast4, FormLabel: FormLabel(cand.Type, cand.Year, cand.Month));
            // เลือกได้เฉพาะในชุดผู้สมัคร แล้วเดินด่านที่เหลือครบเหมือนบันทึกเอง (ความยินยอม · บัญชี · ยอดสลิป=ผู้สมัคร · ยอดสมุดสด · RemitAsync)
            await AutoApplyAsync(row, reading, cand, facts, replyToken, null, ct);
            return null;   // การ์ดผลส่งแล้วด้วย reply token
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ฝ่ายค้าน 272 M5: ล้มทุกชนิด ⇒ แถวเข้าคิวพนักงานจริง + กระดิ่ง (ไม่ค้าง Received)
            _logger.LogError(ex, "บันทึกสลิปภาษี {Slip} หลังลูกค้าเลือกในกลุ่มล้ม — เข้าคิวพนักงาน", slipId);
            _db.ChangeTracker.Clear();
            var fresh = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, CancellationToken.None);
            if (fresh != null && Statuses.Open.Contains(fresh.Status))
            {
                fresh.Status = Statuses.ToStaff; fresh.RuleCode = "TAX-SLIP-FAILED"; fresh.Reason = Clip("ระบบล้มหลังลูกค้าเลือกรายการ: " + ex.Message, 500); fresh.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(CancellationToken.None);
                await NotifyStaffAsync(fresh, CancellationToken.None);
            }
            return TaxSlipReplyCard.Text(Card.Failed, new(), false);
        }
    }

    // ═══════════════ งานเบื้องหลัง (JobLock · TaxSlipJob) ═══════════════

    /// <summary>สลิปที่พักรอความยินยอม: ได้ความยินยอมแล้ว ⇒ ประมวลใหม่<b>ทั้งชุดด่าน</b> (ข้อมูลอาจเปลี่ยนระหว่างรอ) · เกิน 30 วัน ⇒ คิวพนักงาน + การ์ด ·
    /// ปุ่มเลือกเกิน 24 ชม. ⇒ คิวพนักงาน · คืนจำนวนแถวที่ขยับ</summary>
    public async Task<int> RunHeldAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var (pid, enabled) = await ProviderAsync(ct);
        if (pid is not Guid providerId) return 0;
        var n = 0;
        // ฝ่ายค้าน 272 L1: เลือกต่อบริษัทลูกค้า (สลิปค้างของลูกค้ารายหนึ่งไม่บังรายอื่น) — ลูกค้าที่อนุญาตแล้วก่อน แล้วแถวที่ถึงกำหนดหมดอายุ
        var heldClients = await _db.TaxSlipEvidences.AsNoTracking().Where(s => s.CompanyId == providerId && s.Status == Statuses.AwaitingConsent)
            .Select(s => s.ClientCompanyId).Distinct().ToListAsync(ct);
        var ready = new List<Guid?>();
        foreach (var c in heldClients)
            if (c is Guid g && (await _consent.CheckAsync(g, providerId, null, null, ct)).Ok) ready.Add(c);
        var expiryCutoff = nowUtc.AddDays(-ProviderConsent.RequestLifetimeDays);
        var held = await _db.TaxSlipEvidences.Where(s => s.CompanyId == providerId && s.Status == Statuses.AwaitingConsent
                && (ready.Contains(s.ClientCompanyId) || s.CreatedAt < expiryCutoff || s.ClientCompanyId == null))
            .OrderBy(s => s.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var row in held)
        {
            if (row.ClientCompanyId is not Guid client) { await MarkStaffAsync(row, "ไม่รู้บริษัทลูกค้าของสลิปที่พักไว้", ct); n++; continue; }
            var v = await _consent.CheckAsync(client, providerId, null, null, ct);
            // ฝ่ายค้าน 272 L1: อีเมล — ผู้ติดต่อของแถวต้องยังเป็นบริษัทลูกค้านี้ (ปลด/ลบผู้ติดต่อแล้ว ⇒ คิว)
            if (v.Ok && enabled && row.Source == SourceEmail && !await EmailStillBoundAsync(row, client, ct))
            { await MarkStaffAsync(row, "ผู้ส่งอีเมลไม่ได้ผูกกับบริษัทลูกค้านี้แล้ว", ct); n++; continue; }
            if (v.Ok && enabled)
            {
                var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
                if (bytes == null) { await MarkStaffAsync(row, "อ่านไฟล์สลิปที่พักไว้ไม่ได้", ct); n++; continue; }
                var reading = await _reader.ReadAsync(bytes, row.ContentType, ct);
                row.Status = Statuses.Received;
                await _db.SaveChangesAsync(ct);
                await ProcessAsync(row, reading, GovPaymentSlip.Identify(reading), null, null, null, ct);
                n++;
            }
            else if (row.CreatedAt.AddDays(ProviderConsent.RequestLifetimeDays) < nowUtc)
            {
                row.Status = Statuses.ToStaff; row.RuleCode = ProviderConsent.RuleCode + "-EXPIRED"; row.Reason = "คำขออนุญาตหมดอายุ — ส่งให้พนักงานตรวจ (ไม่ได้ลบ)"; row.UpdatedAt = nowUtc;
                await FinishAsync(row, Card.ConsentExpired, new(HeldCount: 1), null, null, staff: true, ct);
                n++;
            }
        }
        var stale = await _db.TaxSlipEvidences.Where(s => s.CompanyId == providerId && s.Status == Statuses.AwaitingChoice && s.ChoiceExpiresAt < nowUtc).Take(50).ToListAsync(ct);
        foreach (var row in stale)
        {
            row.Status = Statuses.ToStaff; row.Reason = "ไม่มีผู้เลือกรายการภายใน 24 ชม. — ส่งให้พนักงานตรวจ"; row.UpdatedAt = nowUtc;
            await FinishAsync(row, Card.ToStaff, new(Reason: row.Reason, ControlLast4: row.ControlCodeLast4), null, null, staff: true, ct);
            n++;
        }
        if (enabled) n += await RereadAsync(providerId, nowUtc, ct);
        n += await AgeOpenRowsAsync(providerId, nowUtc, ct);
        await _db.ProviderConsentRequests.Where(r => r.ProviderCompanyId == providerId && r.Status == "Open" && r.ExpiresAt < nowUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Expired").SetProperty(r => r.ResolvedAt, (DateTime?)nowUtc), ct);
        return n;
    }

    /// <summary>รอบ 275 แพ็ก D (M4 · L5): อ่านใหม่ — (1) แถว <see cref="OcrOffRuleCode"/> เมื่อ OCR กลับมา: ไม่ใช่สลิปรัฐ ⇒ ปิด + ลบรูปทันที (เก็บไว้เพราะยังไม่ได้อ่าน
    /// ไม่ใช่เพราะเป็นหลักฐาน · PDPA ม.22) · เป็นสลิปรัฐ ⇒ เดินตัวตัดสินทั้งชุด · (2) แถว Received ค้างเกิน 10 นาที (โปรเซสตายกลางทาง) ⇒ ประมวลใหม่</summary>
    private async Task<int> RereadAsync(Guid providerId, DateTime nowUtc, CancellationToken ct)
    {
        if (!_reader.IsAvailable) return 0;
        var stuckBefore = nowUtc.AddMinutes(-10);
        var rows = await _db.TaxSlipEvidences.Where(s => s.CompanyId == providerId && s.FilePurgedAt == null
                && ((s.Status == Statuses.ToStaff && s.RuleCode == OcrOffRuleCode) || (s.Status == Statuses.Received && (s.UpdatedAt ?? s.CreatedAt) < stuckBefore)))
            .OrderBy(s => s.CreatedAt).Take(20).ToListAsync(ct);
        var n = 0;
        foreach (var row in rows)
        {
            var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
            if (bytes == null) { if (row.Status == Statuses.Received) { await MarkStaffAsync(row, "อ่านไฟล์สลิปที่ค้างประมวลไม่ได้", ct); n++; } continue; }
            var reading = await _reader.ReadAsync(bytes, row.ContentType, ct);
            if (string.IsNullOrWhiteSpace(reading.Text)) { if (row.Status == Statuses.Received) { await MarkStaffAsync(row, OcrOffReason, ct); n++; } continue; }
            var agency = GovPaymentSlip.Identify(reading);
            if (agency.Kind == GovPaymentSlip.Kind.NotGovernment)
            {
                row.Status = Statuses.Rejected; row.RuleCode = "TAX-SLIP-NOT-GOV"; row.Reason = "อ่านใหม่แล้วไม่ใช่สลิปชำระหน่วยงานรัฐ — ปิดและลบรูป";
                row.ResolvedAt = nowUtc; row.UpdatedAt = nowUtc; row.FileSha256 = "rejected:" + row.Id.ToString("N"); row.BankTxnHash = null;
                if (reading.LooksLikeSlip)
                    await _channel.SendSlipCardAsync(row, TaxSlipReplyCard.Text(Card.TransferNotTax, new(), false), null, null, null, ct);
                row.FilePurgedAt = nowUtc;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = providerId, Action = Accounting.Models.Enums.AuditAction.Update, EntityType = nameof(TaxSlipEvidence), EntityId = row.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipRereadNotGovernment", ruleCode = row.RuleCode }), Timestamp = nowUtc,
                });
                await _db.SaveChangesAsync(ct);
                try { await _storage.DeleteAsync(row.StoragePath, CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ลบรูปที่ไม่ใช่สลิปรัฐ {Slip} ไม่สำเร็จ", row.Id); }
                n++;
                continue;
            }
            row.Status = Statuses.Received; row.RuleCode = null; row.Reason = null; row.UpdatedAt = nowUtc;
            row.Amount = reading.Amount; row.SlipDate = reading.Date; row.Fee = reading.Bill?.Fee; row.Agency = agency.Row?.Agency.ToString();
            row.ControlCodeHash = TaxSlipRefHash.ControlCode(agency.ControlCode); row.ControlCodeLast4 = TaxSlipRefHash.Last4(agency.ControlCode);
            row.TaxIdMasked = agency.TaxId != null ? TaxSlipReplyCard.MaskTaxId(agency.TaxId) : null;
            await _db.SaveChangesAsync(ct);
            await ProcessAsync(row, reading, agency, null, null, null, ct);
            n++;
        }
        return n;
    }

    /// <summary>รอบ 275 แพ็ก D (M4 · คำตัดสิน 275-D-3): แถวที่รอพนักงานนาน ⇒ กระดิ่งซ้ำวันละครั้ง · เกิน 90 วัน ⇒ ปิดเป็น Expired + การ์ดตามจริง (ไฟล์ลบตามระยะเก็บของแถวที่ปิด)</summary>
    private async Task<int> AgeOpenRowsAsync(Guid providerId, DateTime nowUtc, CancellationToken ct)
    {
        var remindCut = nowUtc.AddDays(-TaxSlipOpenAge.RemindAfterDays);
        var rows = await _db.TaxSlipEvidences.Where(s => s.CompanyId == providerId && s.Status == Statuses.ToStaff && s.CreatedAt < remindCut)
            .OrderBy(s => s.CreatedAt).Take(100).ToListAsync(ct);
        var n = 0;
        var remind = new List<TaxSlipEvidence>();
        foreach (var row in rows)
        {
            switch (TaxSlipOpenAge.Decide(row.Status, row.CreatedAt, row.StaffRemindedAt, nowUtc))
            {
                case TaxSlipOpenAge.Action.Expire:
                    row.Status = Statuses.Expired; row.RuleCode = TaxSlipOpenAge.ExpiredRuleCode;
                    row.Reason = "รอพนักงานตรวจเกิน " + TaxSlipOpenAge.ExpireAfterDays + " วัน — ปิดโดยไม่บันทึก"; row.ResolvedAt = nowUtc; row.UpdatedAt = nowUtc;
                    row.FileSha256 = "expired:" + row.Id.ToString("N"); row.BankTxnHash = null;   // ส่งไฟล์เดิมใหม่ได้ (แถวใหม่)
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = providerId, Action = Accounting.Models.Enums.AuditAction.Update, EntityType = nameof(TaxSlipEvidence), EntityId = row.Id.ToString(),
                        NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipOpenExpired", days = TaxSlipOpenAge.ExpireAfterDays, ruleCode = row.RuleCode }), Timestamp = nowUtc,
                    });
                    await FinishAsync(row, Card.OpenExpired, new(ControlLast4: row.ControlCodeLast4, RecordedAt: row.CreatedAt), null, null, staff: false, ct);
                    n++;
                    break;
                case TaxSlipOpenAge.Action.Remind:
                    row.StaffRemindedAt = nowUtc;
                    remind.Add(row);
                    break;
            }
        }
        if (remind.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            if (_notify != null)
            {
                try
                {
                    await _notify.DispatchAsync(providerId, NotificationEvents.TaxSlipNeedsStaff, new NotificationContext
                    {
                        Title = "⏰ สลิปภาษีของลูกค้ารอตรวจเกิน " + TaxSlipOpenAge.RemindAfterDays + " วัน " + remind.Count + " ใบ",
                        Message = "ใบเก่าสุดรับเมื่อ " + TaxSlipReplyCard.ThaiDay(remind.Min(r => r.CreatedAt)) + " · ครบ " + TaxSlipOpenAge.ExpireAfterDays
                            + " วันระบบปิดโดยไม่บันทึกและแจ้งลูกค้า",
                        ActionUrl = "/pages/tax-slip-desk.html",
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "เตือนสลิปภาษีค้างตรวจไม่สำเร็จ"); }
            }
            n += remind.Count;
        }
        return n;
    }

    private async Task<bool> EmailStillBoundAsync(TaxSlipEvidence row, Guid client, CancellationToken ct)
    {
        if (row.ContactId is not Guid contact) return false;
        var key = client.ToString();
        return await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == contact && c.CompanyId == row.CompanyId
            && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key, ct);
    }

    private async Task MarkStaffAsync(TaxSlipEvidence row, string reason, CancellationToken ct)
    {
        row.Status = Statuses.ToStaff; row.Reason = Clip(reason, 500); row.RuleCode ??= "TAX-SLIP-FAILED"; row.UpdatedAt = DateTime.UtcNow;
        await FinishAsync(row, Card.ToStaff, new(Reason: reason, ControlLast4: row.ControlCodeLast4), null, null, staff: true, ct);
    }

    // ═══════════════ คิวพนักงาน (หน้า tax-slip-desk) ═══════════════


    /// <param name="CanRemit">มีผู้สมัครที่กดแล้วไม่ล้มแน่นอน (รอบ 275 D · M2 — ผู้สมัครที่ล้มแน่นอนติด <see cref="CandidateView.Blocked"/>)</param>
    /// <param name="CanManual">รอบ 275 D (H1): แถวยังรอตัดสิน ⇒ "บันทึกเอง" / "ผูกกับรายการนำส่งที่มีอยู่" ได้ (ตัวเลือกโหลดจาก <see cref="ManualOptionsAsync"/>)</param>
    public sealed record StaffRow(Guid Id, DateTime ReceivedAt, string Source, string? ClientName, Guid? ClientCompanyId, string? Agency, decimal? Amount, DateTime? SlipDate,
        string? ControlLast4, string? TaxIdMasked, string Status, string StatusLabel, string? RuleCode, string? Reason, IReadOnlyList<CandidateView> Candidates,
        IReadOnlyList<AccountView> Accounts, string? LastCardOutcome, string? ConsentLabel, bool CanRemit, bool CanManual = false);
    /// <param name="Blocked">เหตุที่กดแล้วล้มแน่นอน (null = กดได้) — <see cref="TaxSlipDeskRule.CandidateBlocker"/></param>
    public sealed record CandidateView(int Index, string Label, decimal Total, string? Blocked = null);
    public sealed record AccountView(Guid Id, string Label, bool Preselected);

    public static string StatusLabel(string s) => s switch
    {
        Statuses.Remitted => "บันทึกแล้ว",
        Statuses.AwaitingConsent => "รอลูกค้าอนุญาต",
        Statuses.AwaitingChoice => "รอลูกค้าเลือกรายการในกลุ่ม",
        Statuses.ToStaff => "รอพนักงานตรวจ",
        Statuses.Rejected => "ไม่บันทึก",
        Statuses.Expired => "ปิดแล้ว (รอตรวจเกินกำหนด)",
        _ => "กำลังประมวล",
    };

    private static bool StaffCanAct(TaxSlipEvidence r, DateTime nowUtc)
        => r.Status is Statuses.ToStaff or Statuses.AwaitingChoice or Statuses.AwaitingConsent
           || (r.Status == Statuses.Received && (r.UpdatedAt ?? r.CreatedAt) < nowUtc.AddMinutes(-10));

    public async Task<List<StaffRow>> StaffQueueAsync(Guid providerId, bool openOnly, CancellationToken ct = default)
    {
        var q = _db.TaxSlipEvidences.AsNoTracking().Where(s => s.CompanyId == providerId);
        if (openOnly) q = q.Where(s => Statuses.Open.Contains(s.Status));
        var rows = await q.OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        var clients = rows.Where(r => r.ClientCompanyId != null).Select(r => r.ClientCompanyId!.Value).Distinct().ToList();
        var names = await _db.Companies.AsNoTracking().Where(c => clients.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var accs = await _db.BankAccounts.AsNoTracking().Where(b => clients.Contains(b.CompanyId) && b.IsActive && !b.IsDeleted)
            .Select(b => new { b.Id, b.CompanyId, b.BankName, b.AccountNumber }).ToListAsync(ct);
        // รอบ 275 D (M2): ข้อมูลของด่านที่ล้มแน่นอน — สถานะใบขอชำระ + งวดที่นำส่งแล้ว (ชุดบริษัทของแถวในหน้านี้เท่านั้น)
        var allCands = rows.SelectMany(r => ParseCandidates(r.CandidatesJson)).ToList();
        var candCompanies = allCands.Select(c => c.CompanyId).Distinct().ToList();
        var reqIds = allCands.Where(c => c.RequestId != null).Select(c => c.RequestId!.Value).Distinct().ToList();
        var reqStatus = reqIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.TaxPaymentRequests.AsNoTracking().Where(r => candCompanies.Contains(r.CompanyId) && reqIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Status, ct);
        var remittedSet = candCompanies.Count == 0 ? new HashSet<(Guid, string, int, int)>()
            : (await _db.StatutoryRemittances.AsNoTracking().Where(r => candCompanies.Contains(r.CompanyId) && !r.IsDeleted)
                .Select(r => new { r.CompanyId, r.RemittanceType, r.PeriodYear, r.PeriodMonth }).ToListAsync(ct))
                .Select(r => (r.CompanyId, r.RemittanceType, r.PeriodYear, r.PeriodMonth)).ToHashSet();
        var now = DateTime.UtcNow;
        var list = new List<StaffRow>();
        foreach (var r in rows)
        {
            var cands = ParseCandidates(r.CandidatesJson).Select((c, i) => new CandidateView(i, FormLabel(c.Type, c.Year, c.Month), c.Amount + c.Surcharge + c.Penalty,
                TaxSlipDeskRule.CandidateBlocker(r.Amount, c, c.RequestId is Guid rid && reqStatus.TryGetValue(rid, out var st) ? st : null,
                    remittedSet.Contains((c.CompanyId, c.Type, c.Year, c.Month))))).ToList();
            var pre = (r.PreselectAccounts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var accounts = accs.Where(a => a.CompanyId == r.ClientCompanyId).Select(a => new AccountView(a.Id, AccountLabel(a.BankName, a.AccountNumber), pre.Contains(a.Id.ToString()))).ToList();
            string? consent = null;
            if (r.ClientCompanyId is Guid cid)
            {
                var v = await _consent.CheckAsync(cid, providerId, null, null, ct);
                consent = v.Ok ? "อนุญาตแล้ว" : v.Code == ProviderConsent.Codes.PowerOfAttorneyOnly ? "มีหนังสือมอบอำนาจ (กดบันทึกทีละใบ)" : "ยังไม่อนุญาต";
            }
            var act = StaffCanAct(r, now);
            list.Add(new StaffRow(r.Id, r.CreatedAt, r.Source, names.FirstOrDefault(n => n.Id == r.ClientCompanyId)?.Name, r.ClientCompanyId, r.Agency, r.Amount, r.SlipDate,
                r.ControlCodeLast4, r.TaxIdMasked, r.Status, StatusLabel(r.Status), r.RuleCode, r.Reason, cands, accounts, r.LastCardOutcome, consent,
                act && cands.Any(c => c.Blocked == null), act));
        }
        return list;
    }

    private static string AccountLabel(string? bankName, string? accountNumber)
    {
        var digits = new string((accountNumber ?? "").Where(char.IsAsciiDigit).ToArray());
        return (ThaiBankCodes.ByCode(ThaiBankCodes.FromText(bankName))?.ThaiName ?? bankName ?? "บัญชี") + (digits.Length >= 4 ? " ••" + digits[^4..] : "");
    }

    /// <summary>พนักงานกด "บันทึกนำส่ง" 1 คลิก (272-1-5/6) — เลือกจากชุดผู้สมัครที่ตัวตัดสินคืนเท่านั้น (ห้ามพิมพ์ยอดเอง · ช่องที่ server ไม่รับ = ไม่มีในคำขอ) ·
    /// ด่านความยินยอม/หนังสือมอบอำนาจ/สมาชิกผู้ยื่นภาษี ตรวจใต้ล็อกใน <see cref="TaxSlipRemitService.ApplyAsync"/> · ผลส่งการ์ดตามหลังในกลุ่ม/อีเมลเดิมเมื่อบันทึกได้ ·
    /// รอบ 275 D (M2): ผู้สมัครที่ล้มแน่นอน ⇒ 409 ทันที ไม่ส่งการ์ดเข้ากลุ่ม</summary>
    public async Task StaffRemitAsync(Guid providerId, Guid staffUserId, Guid slipId, int candidateIndex, Guid bankAccountId, CancellationToken ct = default)
    {
        var row = await _db.TaxSlipEvidences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!Statuses.Open.Contains(row.Status)) throw new BusinessRuleException("สลิปนี้ปิดแล้ว (" + StatusLabel(row.Status) + ")", TaxSlipRemitService.ClosedRuleCode, 409);
        var cands = ParseCandidates(row.CandidatesJson);
        if (candidateIndex < 0 || candidateIndex >= cands.Count)
            throw new BusinessRuleException("เลือกรายการจากที่ระบบเสนอ (สลิปนี้ไม่มีรายการที่ยอดตรง — ใช้ “บันทึกเอง” หรือ “ผูกกับรายการนำส่งที่มีอยู่”)", "TAX-SLIP-NO-CANDIDATE", 400);
        var cand = cands[candidateIndex];
        var reqStatus = cand.RequestId is Guid rid
            ? await _db.TaxPaymentRequests.AsNoTracking().Where(r => r.Id == rid && r.CompanyId == cand.CompanyId).Select(r => r.Status).FirstOrDefaultAsync(ct) : null;
        var periodRemitted = await _db.StatutoryRemittances.AsNoTracking().AnyAsync(r => r.CompanyId == cand.CompanyId && !r.IsDeleted && r.RemittanceType == cand.Type
            && r.PeriodYear == cand.Year && r.PeriodMonth == cand.Month, ct);
        if (TaxSlipDeskRule.CandidateBlocker(row.Amount, cand, reqStatus, periodRemitted) is { } blocked)
            throw new BusinessRuleException("บันทึกไม่ได้ — " + blocked, TaxSlipDeskRule.BlockedRuleCode, 409);
        var staffIsClientTaxFiler = await StaffIsClientTaxFilerAsync(cand.CompanyId, staffUserId, ct);
        var label = await ClientAccountLabelAsync(cand.CompanyId, bankAccountId, ct);
        if (row.SlipDate is not DateTime payDate) throw new BusinessRuleException("ไม่มีวันที่บนสลิป — ใช้ “บันทึกเอง” แล้วกรอกวันที่จ่าย", "TAX-SLIP-NO-DATE", 409);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == cand.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        await ApplyAndReplyAsync(row.Id, providerId, cand, bankAccountId, null, staffUserId, staffIsClientTaxFiler, payDate, label,
            new TaxSlipReplyCard.Facts(CompanyName: co, ControlLast4: row.ControlCodeLast4), null, await EmailToAsync(row, ct), ct);
    }

    /// <summary>ฝ่ายค้าน 272 M4: "ผู้กดเป็นผู้ยื่นภาษีของลูกค้าเอง" ตรวจกับบริษัทที่จะบันทึกลงจริง ไม่ใช่บริษัทที่แถวเดาไว้</summary>
    private async Task<bool> StaffIsClientTaxFilerAsync(Guid clientId, Guid staffUserId, CancellationToken ct)
    {
        return _perms != null
            && await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == clientId && cu.UserId == staffUserId, ct)
            && await _perms.HasPermissionAsync(clientId, staffUserId, PermissionKeys.TaxFile);
    }

    private async Task<string> ClientAccountLabelAsync(Guid clientId, Guid bankAccountId, CancellationToken ct)
    {
        var acc = await _db.BankAccounts.AsNoTracking().Where(b => b.Id == bankAccountId && b.CompanyId == clientId && b.IsActive && !b.IsDeleted)
            .Select(b => new { b.BankName, b.AccountNumber }).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("บัญชีที่เลือกไม่ใช่บัญชีที่ใช้งานของบริษัทลูกค้า", TaxSlipBankAccount.RuleCode, 400);
        return AccountLabel(acc.BankName, acc.AccountNumber);
    }

    private async Task<string?> EmailToAsync(TaxSlipEvidence row, CancellationToken ct)
        => row.Source == SourceEmail && row.ContactId is Guid contact
            ? await _db.Contacts.AsNoTracking().Where(c => c.Id == contact && c.CompanyId == row.CompanyId).Select(c => c.Email).FirstOrDefaultAsync(ct)
            : null;

    /// <summary>รอบ 275 D (H1): บริษัทที่ช่องทางของสลิปผูก (กฎ M) — LINE: กลุ่มเดิมที่ยังผูก · อีเมล: บริษัทของผู้ติดต่อผู้ส่ง · รวมบริษัทที่แถวตัดสินไว้ตอนรับ</summary>
    private async Task<List<Guid>> AllowedClientsAsync(TaxSlipEvidence row, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (row.Source == SourceLine) ids.AddRange((await _channel.GroupBindingAsync(row.CompanyId, row.SourceKey, ct)).ClientCompanyIds);
        else if (row.ContactId is Guid contact)
        {
            var ext = await _db.Contacts.AsNoTracking().Where(c => c.Id == contact && c.CompanyId == row.CompanyId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem)
                .Select(c => c.ExternalId).FirstOrDefaultAsync(ct);
            if (Guid.TryParse(ext, out var g) && g != row.CompanyId) ids.Add(g);
        }
        if (row.ClientCompanyId is Guid cc) ids.Add(cc);
        return ids.Distinct().ToList();
    }

    public sealed record PendingView(string Type, string Label, int Year, int Month, decimal Amount);
    public sealed record RemittanceView(Guid Id, string Label, decimal Total, DateTime PayDate, string? Blocked);
    public sealed record ClientManualView(Guid ClientCompanyId, string Name, IReadOnlyList<PendingView> Pending, IReadOnlyList<RemittanceView> Remittances,
        IReadOnlyList<AccountView> Accounts, string? TaxIdNote);
    /// <param name="NeedsSlipAmount">ระบบอ่านยอดบนสลิปไม่ได้ ⇒ ต้องกรอกยอดที่อ่านเอง</param>
    public sealed record ManualOptions(Guid SlipId, decimal? SlipAmount, DateTime? SlipDate, bool NeedsSlipAmount, bool NeedsPayDate, IReadOnlyList<ClientManualView> Clients);

    /// <summary>รอบ 275 D (H1): ตัวเลือกของ "บันทึกเอง" / "ผูกกับรายการนำส่งที่มีอยู่" — server คำนวณทั้งหมด (งวดค้างจาก dashboard ตัวเดียว · รายการนำส่งที่ผูกได้ + เหตุที่ผูกไม่ได้)</summary>
    public async Task<ManualOptions> ManualOptionsAsync(Guid providerId, Guid slipId, CancellationToken ct = default)
    {
        var row = await _db.TaxSlipEvidences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, ct) ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!StaffCanAct(row, DateTime.UtcNow)) throw new BusinessRuleException("สลิปนี้ไม่ได้รอพนักงานตัดสิน (" + StatusLabel(row.Status) + ")", TaxSlipRemitService.ClosedRuleCode, 409);
        var allowed = await AllowedClientsAsync(row, ct);
        var cos = await _db.Companies.AsNoTracking().Where(c => allowed.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.TaxId }).ToListAsync(ct);
        var from = (row.SlipDate ?? row.CreatedAt).Date.AddDays(-60);
        var to = (row.SlipDate ?? row.CreatedAt).Date.AddDays(60);
        var list = new List<ClientManualView>();
        foreach (var co in cos.OrderBy(c => c.Name))
        {
            var dash = await _remit.GetDashboardAsync(co.Id, 24, includeOldAnnual: true);
            var pending = dash.Pending.Where(p => ProviderConsent.TaxTypes.Any(t => t.Type == p.RemittanceType))
                .Select(p => new PendingView(p.RemittanceType, FormLabel(p.RemittanceType, p.PeriodYear, p.PeriodMonth), p.PeriodYear, p.PeriodMonth, p.Amount)).ToList();
            var recs = await _db.StatutoryRemittances.AsNoTracking()
                .Where(r => r.CompanyId == co.Id && !r.IsDeleted && r.PayDate >= from && r.PayDate <= to)
                .OrderByDescending(r => r.PayDate).Take(30)
                .Select(r => new { r.Id, r.RemittanceType, r.PeriodYear, r.PeriodMonth, Total = r.Amount + r.LateFee + r.Penalty + r.OtherFine, r.PayDate, r.SlipEvidenceId })
                .ToListAsync(ct);
            var accs = await _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == co.Id && b.IsActive && !b.IsDeleted)
                .Select(b => new { b.Id, b.BankName, b.AccountNumber }).ToListAsync(ct);
            list.Add(new ClientManualView(co.Id, co.Name, pending,
                recs.Select(r => new RemittanceView(r.Id, FormLabel(r.RemittanceType, r.PeriodYear, r.PeriodMonth), r.Total, r.PayDate,
                    TaxSlipDeskRule.LinkProblem(row.Amount, r.Total, false, r.SlipEvidenceId))).ToList(),
                accs.Select(a => new AccountView(a.Id, AccountLabel(a.BankName, a.AccountNumber), false)).ToList(),
                SlipTaxIdOther(row, co.TaxId) ? "เลขผู้เสียภาษีบนสลิปไม่ตรงบริษัทนี้" : null));
        }
        return new ManualOptions(row.Id, row.Amount, row.SlipDate, row.Amount == null, row.SlipDate == null, list);
    }

    private static bool SlipTaxIdOther(TaxSlipEvidence row, string? companyTaxId)
        => row.TaxIdMasked != null && ThaiTaxId.Normalize(companyTaxId).Length == 13 && TaxSlipReplyCard.MaskTaxId(companyTaxId) != row.TaxIdMasked;

    /// <param name="SlipAmount">ยอดบนสลิปที่พนักงานอ่านเอง — ใช้เฉพาะเมื่อระบบอ่านไม่ได้</param>
    /// <param name="PayDate">วันที่จ่ายตามสลิป — ใช้เฉพาะเมื่อระบบอ่านวันที่ไม่ได้</param>
    public sealed record ManualInput(Guid ClientCompanyId, string RemittanceType, int PeriodYear, int PeriodMonth, decimal Tax, decimal Surcharge, decimal Penalty,
        Guid BankAccountId, decimal? SlipAmount, DateTime? PayDate);

    /// <summary>
    /// รอบ 275 แพ็ก D (H1 · คำตัดสิน 4.3) — พนักงาน<b>บันทึกนำส่งเอง</b>จากสลิปที่ระบบจับคู่ไม่ได้ (ปกส. · ธนาคารอื่น · จ่ายช้าไม่มีใบขอชำระ · OCR ล่ม):
    /// เลือกบริษัท (ในชุดที่ช่องทางผูก) · งวดค้าง · ภาษี/เงินเพิ่ม/เบี้ยปรับ · บัญชีที่จ่าย ⇒ ตรวจยอดกับสลิปทุกสตางค์ (<see cref="TaxSlipDeskRule.ManualProblem"/> + ใต้ล็อกใน ApplyAsync) ·
    /// บันทึกผ่าน <see cref="TaxSlipRemitService.ApplyAsync"/> ตัวเดียว (RemitAsync · ยอดภาษี = ยอดค้างในสมุด · ความยินยอม/หนังสือมอบอำนาจ/ผู้ยื่นภาษีของลูกค้า) · ไม่มีผู้อนุมัติคนที่สอง ·
    /// การ์ด "บันทึกแล้วโดยพนักงาน" ตามจริง · ล้ม ⇒ 409 ไม่ส่งการ์ด
    /// </summary>
    public async Task StaffManualRemitAsync(Guid providerId, Guid staffUserId, Guid slipId, ManualInput m, CancellationToken ct = default)
    {
        var row = await _db.TaxSlipEvidences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, ct) ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!StaffCanAct(row, DateTime.UtcNow)) throw new BusinessRuleException("สลิปนี้ไม่ได้รอพนักงานตัดสิน (" + StatusLabel(row.Status) + ")", TaxSlipRemitService.ClosedRuleCode, 409);
        var allowed = await AllowedClientsAsync(row, ct);
        if (!allowed.Contains(m.ClientCompanyId))
            throw new BusinessRuleException("บริษัทนี้ไม่ได้ผูกกับช่องทางที่ส่งสลิป — บันทึกจากโต๊ะนี้ไม่ได้", TaxSlipMatch.Codes.TaxId, 400);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == m.ClientCompanyId).Select(c => new { c.Name, c.TaxId }).FirstAsync(ct);
        var payDate = row.SlipDate ?? m.PayDate?.Date;
        if (TaxSlipDeskRule.ManualProblem(m.RemittanceType, m.Tax, m.Surcharge, m.Penalty, row.Amount, m.SlipAmount, payDate,
                ThaiDate.CalendarDateUtc(DateTime.UtcNow), SlipTaxIdOther(row, co.TaxId)) is { } problem)
            throw new BusinessRuleException(problem, TaxSlipDeskRule.ManualRuleCode, 400);
        var label = await ClientAccountLabelAsync(m.ClientCompanyId, m.BankAccountId, ct);
        var cand = new TaxSlipMatch.Candidate(m.ClientCompanyId, m.RemittanceType, m.PeriodYear, m.PeriodMonth,
            Math.Round(m.Tax, 2, MidpointRounding.AwayFromZero), Math.Round(m.Surcharge, 2, MidpointRounding.AwayFromZero), Math.Round(m.Penalty, 2, MidpointRounding.AwayFromZero), null);
        await ApplyAndReplyAsync(row.Id, providerId, cand, m.BankAccountId, null, staffUserId, await StaffIsClientTaxFilerAsync(m.ClientCompanyId, staffUserId, ct), payDate!.Value,
            label, new TaxSlipReplyCard.Facts(CompanyName: co.Name, ControlLast4: row.ControlCodeLast4), null, await EmailToAsync(row, ct), ct,
            manualEntry: true, staffSlipAmount: row.Amount == null ? m.SlipAmount : null);
    }

    /// <summary>รอบ 275 แพ็ก D (H1): ปิดสลิปด้วยรายการนำส่งที่บันทึกไว้แล้ว (<see cref="TaxSlipRemitService.LinkAsync"/>) — การ์ด "บันทึกการชำระแล้ว" ตามจริง แทน "ไม่ได้บันทึก"</summary>
    public async Task StaffLinkAsync(Guid providerId, Guid staffUserId, Guid slipId, Guid clientCompanyId, Guid remittanceId, CancellationToken ct = default)
    {
        var row = await _db.TaxSlipEvidences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, ct) ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!StaffCanAct(row, DateTime.UtcNow)) throw new BusinessRuleException("สลิปนี้ไม่ได้รอพนักงานตัดสิน (" + StatusLabel(row.Status) + ")", TaxSlipRemitService.ClosedRuleCode, 409);
        var allowed = await AllowedClientsAsync(row, ct);
        TaxSlipRemitService.Linked linked;
        try
        {
            linked = await _remitter.LinkAsync(new TaxSlipRemitService.LinkRequest(providerId, slipId, clientCompanyId, remittanceId, staffUserId,
                await StaffIsClientTaxFilerAsync(clientCompanyId, staffUserId, ct), allowed), ct);
        }
        catch (TaxSlipRemitService.TaxSlipGateException gx) { throw new BusinessRuleException("ผูกไม่ได้ — " + gx.Message, gx.RuleCode, 409); }
        var fresh = await _db.TaxSlipEvidences.FirstAsync(s => s.Id == slipId && s.CompanyId == providerId, ct);
        var name = await _db.Companies.AsNoTracking().Where(c => c.Id == clientCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        await FinishAsync(fresh, Card.StaffLinked, new TaxSlipReplyCard.Facts(CompanyName: name, FormLabel: FormLabel(linked.RemittanceType, linked.PeriodYear, linked.PeriodMonth),
            PayDate: linked.PayDate, ControlLast4: fresh.ControlCodeLast4), null, await EmailToAsync(fresh, ct), staff: false, ct);
    }

    /// <summary>พนักงานกด "ไม่บันทึก" (ไม่ใช่สลิปภาษี · จ่ายซ้ำ · อื่น ๆ) — การ์ดตามหลังเสมอ · audit ·
    /// รอบ 275 D: (M4) ไฟล์<b>ไม่ลบทันที</b> — ลบเมื่อครบ <see cref="ClosedFileRetentionDays"/> วันโดยงานเบื้องหลัง (เดิมลบทันที ⇒ งานลบ 90 วันไม่มีแถวเข้าเงื่อนไข) ·
    /// (H2) เปลี่ยนสถานะแบบ atomic — อีกคำขอบันทึกไปแล้วระหว่างนี้ ⇒ 409 ไม่เขียนทับ "บันทึกแล้ว"</summary>
    public async Task StaffRejectAsync(Guid providerId, Guid staffUserId, Guid slipId, string reason, CancellationToken ct = default)
    {
        var why = Clip(string.IsNullOrWhiteSpace(reason) ? "พนักงานตรวจแล้วไม่ใช่รายการที่ต้องบันทึก" : reason, 300)!;
        var now = DateTime.UtcNow;
        var rejectedSha = "rejected:" + slipId.ToString("N");
        // ฝ่ายค้าน 272 M6: พนักงานปฏิเสธ ⇒ ส่งไฟล์เดิม/รายการเดิมใหม่ได้ (ด่านซ้ำไม่นับแถวที่ปฏิเสธแล้ว)
        var n = await _db.TaxSlipEvidences.Where(s => s.Id == slipId && s.CompanyId == providerId && Statuses.Open.Contains(s.Status))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, Statuses.Rejected).SetProperty(s => s.Reason, why).SetProperty(s => s.RuleCode, "TAX-SLIP-STAFF-REJECT")
                .SetProperty(s => s.ResolvedByUserId, (Guid?)staffUserId).SetProperty(s => s.ResolvedAt, (DateTime?)now).SetProperty(s => s.UpdatedAt, (DateTime?)now)
                .SetProperty(s => s.FileSha256, rejectedSha).SetProperty(s => s.BankTxnHash, (string?)null), ct);
        if (n == 0)
        {
            var exists = await _db.TaxSlipEvidences.AsNoTracking().Where(s => s.Id == slipId && s.CompanyId == providerId).Select(s => s.Status).FirstOrDefaultAsync(ct);
            if (exists == null) throw new KeyNotFoundException("ไม่พบสลิป");
            throw new BusinessRuleException("สลิปนี้ปิดแล้ว (" + StatusLabel(exists) + ")", TaxSlipRemitService.ClosedRuleCode, 409);
        }
        var row = await _db.TaxSlipEvidences.FirstAsync(s => s.Id == slipId && s.CompanyId == providerId, ct);
        await _db.Entry(row).ReloadAsync(ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = providerId, UserId = staffUserId, Action = Accounting.Models.Enums.AuditAction.Update, EntityType = nameof(TaxSlipEvidence), EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipRejectedByStaff", clientId = row.ClientCompanyId, reason = why, fileRetentionDays = ClosedFileRetentionDays }),
            Timestamp = now,
        });
        await FinishAsync(row, Card.StaffRejected, new(Reason: why), null, await EmailToAsync(row, ct), staff: false, ct);
    }


    private async Task NotifyStaffAsync(TaxSlipEvidence row, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(row.CompanyId, NotificationEvents.TaxSlipNeedsStaff, new NotificationContext
            {
                Title = "🧾 สลิปภาษีของลูกค้ารอตรวจ",
                Message = (row.Reason ?? "ระบบตัดสินเองไม่ได้") + (row.ControlCodeLast4 != null ? " · Control ••" + row.ControlCodeLast4 : ""),
                ActionUrl = "/pages/tax-slip-desk.html", EntityType = nameof(TaxSlipEvidence), EntityId = row.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งพนักงานเรื่องสลิปภาษี {Slip} ไม่สำเร็จ", row.Id);
        }
    }

    /// <summary>ลิงก์เต็มจาก URL ของระบบที่ตั้งไว้ (ตัวตั้งเดียว <see cref="SystemBaseUrl"/>) · ไม่ได้ตั้ง ⇒ บอกทางในเมนูแทน (ไม่เดาโดเมน)</summary>
    private async Task<string> AbsoluteAsync(string path, CancellationToken ct)
    {
        var b = await SystemBaseUrl.ResolveAsync(_db, null, ct);
        return string.IsNullOrWhiteSpace(b) ? "อนุญาตได้ที่เมนู ค่าบริการ › สำนักงานบัญชี NextAcc ทำแทน" : b.TrimEnd('/') + path;
    }

    private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : (s.Length > n ? s[..n] : s);
}
