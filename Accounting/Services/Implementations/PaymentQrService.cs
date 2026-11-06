using System.Security.Cryptography;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 246 QR — ดู <see cref="IPaymentQrService"/> · กติกาทั้งหมดอยู่ที่ <see cref="PaymentQrPolicy"/> (pure) · รูปตรวจที่ <see cref="PaymentQrImageValidator"/> ·
/// payload พร้อมเพย์ (EMVCo + CRC16-CCITT) ที่ <see cref="IPromptPayService"/> ตัวเดียวของระบบ · ยอดสุทธิ/ลิงก์จาก <see cref="IDocumentPayLinkService.PayQrFactsAsync"/>
/// <para>ที่เก็บ: <c>{AttachmentsRoot}/bank-payment-qr/{companyId:N}/{accountId:N}-{guid}.png</c> ผ่าน <see cref="IFileStorage"/> (ไม่อยู่ใต้ wwwroot — ไม่มี URL สาธารณะ) ·
/// ตัวอ่านยอมเฉพาะไฟล์ใต้โฟลเดอร์ของบริษัทนั้น (<see cref="IsOwnLocation"/>)</para>
/// <para>ตัวเลือกบัญชี + ตัวสร้าง QR ของบัญชีเป็น static (<see cref="PickDocumentAccountAsync"/> · <see cref="BuildAccountQrAsync"/>) ให้หน้าชำระสาธารณะ
/// (<c>DocumentPayLinkService.PublicViewAsync</c>) ใช้ตัวเดียวกันโดยไม่เกิดวง DI (ฝ่ายค้าน S2)</para>
/// </summary>
public class PaymentQrService : IPaymentQrService
{
    public const string StorageFolder = "bank-payment-qr";
    /// <summary>ด้านยาวสูงสุดของรูปที่เก็บหลังเข้ารหัสใหม่ (พิมพ์ที่ ~35 มม. — 1200 px เกินพอ)</summary>
    public const int StoredMaxEdge = 1200;

    public const string NoteSupersededByTaxInvoice = "ใบแจ้งหนี้นี้มีใบกำกับภาษีอ้างอยู่แล้ว — QR บนใบนี้ไม่ฝังยอด (ยอดค้างอยู่ที่ใบกำกับภาษี)";

    private readonly AccountingDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IPromptPayService _promptPay;
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly ILogger<PaymentQrService>? _logger;

    public PaymentQrService(AccountingDbContext db, IFileStorage storage, IPromptPayService promptPay,
        IDocumentPayLinkService? payLinks = null, ILogger<PaymentQrService>? logger = null)
    {
        _db = db;
        _storage = storage;
        _promptPay = promptPay;
        _payLinks = payLinks;
        _logger = logger;
    }

    private string Root => _storage.Locator.AttachmentsRoot;
    private BankAccountResponse Map(BankAccount a) => BankService.MapToResponse(a, Root);

    // ═══════════════ ตัวช่วยร่วม (static) ═══════════════

    /// <summary>ไฟล์ของบริษัทนี้เท่านั้น (ค่าในฐานถูกแก้ตรง/คัดลอกข้ามบริษัท = ไม่อ่าน)</summary>
    public static bool IsOwnLocation(string? location, string attachmentsRoot, Guid companyId)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        var dir = Path.GetFullPath(Path.Combine(attachmentsRoot, StorageFolder, companyId.ToString("N"))) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(location).StartsWith(dir, StringComparison.Ordinal);
    }

    /// <summary>ถอดรูปจริงแล้วเข้ารหัส PNG ใหม่ (ตัด EXIF/ข้อมูลแฝงท้ายไฟล์) · ถอดไม่ได้ = null</summary>
    internal static byte[]? Reencode(byte[] content)
    {
        using var decoded = SkiaImageOps.DecodeUpright(content);
        if (decoded == null) return null;
        using var sized = SkiaImageOps.ResizeMax(decoded.Bitmap, StoredMaxEdge);
        return SkiaImageOps.EncodePng(sized);
    }

    /// <summary>QR ของ URL หน้าชำระ (QRCoder · ECC M) — ตัวเดียวของ QR หน้าชำระบนเอกสาร</summary>
    internal static byte[] UrlQrPng(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    /// <summary>ป้ายบัญชีบนหน้าเว็บ — "กสิกรไทย 123-4-56789-0 (ชื่อบัญชี)"</summary>
    internal static string AccountLabel(BankAccount a) => $"{a.BankName} {a.AccountNumber} ({a.AccountName})";

    /// <summary>บัญชีของบริษัทที่ตั้ง QR ไว้ (เปิดใช้) — ชุดที่ <see cref="PaymentQrPolicy.PickAccount"/> เลือก</summary>
    internal static async Task<List<BankAccount>> ConfiguredAccountsAsync(AccountingDbContext db, string attachmentsRoot, Guid companyId, CancellationToken ct)
    {
        var rows = await db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && (a.PromptPayId != null || a.PaymentQrImagePath != null))
            .ToListAsync(ct);
        return rows.Where(a => PaymentQrPolicy.Pick(a.PaymentQrSource, PaymentQrPolicy.PromptPayUsable(a.PromptPayId),
            IsOwnLocation(a.PaymentQrImagePath, attachmentsRoot, companyId)) != PaymentQrPolicy.Kind.None).ToList();
    }

    /// <summary>บัญชีของใบ (ตัวเลือกเดียวของกระดาษ + หน้าชำระสาธารณะ) — บัญชีธนาคารที่เปิดใช้ซึ่งผูกผังแหล่งเงินของใบ (หลายบัญชี = ไม่เดา) → บัญชีของใบ →
    /// บัญชีของเทมเพลต → บัญชีเดียวที่ตั้ง QR · คืนบัญชี (ตั้ง QR แล้ว) หรือ null + เหตุ</summary>
    internal static async Task<(BankAccount? Account, PaymentQrPolicy.AccountPick Pick)> PickDocumentAccountAsync(AccountingDbContext db, string attachmentsRoot,
        Guid companyId, Guid? paymentAccountId, Guid? bankAccountId, Guid? templateBankAccountId, CancellationToken ct)
    {
        var configured = await ConfiguredAccountsAsync(db, attachmentsRoot, companyId, ct);
        var glBanks = paymentAccountId is Guid gl
            ? await db.Set<BankAccount>().AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.LinkedAccountId == gl)
                .Select(a => a.Id).ToListAsync(ct)
            : new List<Guid>();
        var pick = PaymentQrPolicy.PickAccount(glBanks, bankAccountId, templateBankAccountId, configured.Select(a => a.Id).ToList());
        return (pick.AccountId is Guid p ? configured.First(a => a.Id == p) : null, pick);
    }

    /// <summary>QR ของบัญชี (รูปที่อัปโหลดอ่านผ่านที่เก็บกลาง · อ่านไม่ได้ ⇒ ถอยไปพร้อมเพย์ถ้ามี · ไม่มีอะไร = null) ·
    /// ธนาคาร/ชื่อ/เลขบัญชี = ของบัญชีนี้เสมอ (S2(ข))</summary>
    internal static async Task<BankPaymentQr?> BuildAccountQrAsync(IFileStorage storage, IPromptPayService promptPay, ILogger? logger, Guid companyId,
        BankAccount a, string? promptPayId, string? source, decimal? amount, CancellationToken ct)
    {
        var ppUsable = PaymentQrPolicy.PromptPayUsable(promptPayId);
        var hasUpload = IsOwnLocation(a.PaymentQrImagePath, storage.Locator.AttachmentsRoot, companyId);
        var kind = PaymentQrPolicy.Pick(source, ppUsable, hasUpload);
        if (kind == PaymentQrPolicy.Kind.Upload)
        {
            byte[]? png = null;
            try { png = await storage.ReadAllBytesAsync(a.PaymentQrImagePath, ct); }
            catch (FileStorageException ex)
            {
                logger?.LogWarning(ex, "อ่านรูป QR ของบัญชี {Account} ไม่ได้ — ถอยไปพร้อมเพย์ (ถ้ามี)", a.Id);
            }
            if (png != null)
                return new BankPaymentQr(png, kind, a.BankName, a.AccountName, a.AccountNumber, null, null);
            kind = ppUsable ? PaymentQrPolicy.Kind.PromptPay : PaymentQrPolicy.Kind.None;
        }
        if (kind != PaymentQrPolicy.Kind.PromptPay) return null;
        var id = PaymentQrPolicy.NormalizePromptPayId(promptPayId).Id!;
        var image = promptPay.GenerateQrImage(id, amount ?? 0m, size: 330);
        return new BankPaymentQr(image, kind, a.BankName, a.AccountName, a.AccountNumber, id, amount, amount == null ? null : DateTime.UtcNow);
    }

    /// <summary>ฝ่ายค้าน N6 — ใบแจ้งหนี้ที่มีใบกำกับภาษีที่ออกแล้ว (ไม่ร่าง/ไม่ยกเลิก/ไม่ถูกตีกลับ) อ้างอยู่ = ลูกหนี้ย้ายไปใบกำกับ</summary>
    internal static async Task<bool> SupersededByTaxInvoiceAsync(AccountingDbContext db, Guid companyId, Document doc, CancellationToken ct)
        => await SupersedingTaxInvoiceNumberAsync(db, companyId, doc.DocumentType, doc.Id, ct) != null;

    /// <summary>รอบ 250 (WP-02 §2.3 · คำตัดสิน §2 #16): เลขใบกำกับภาษีที่อ้างใบแจ้งหนี้นี้และยังมีผล (ออกเลขแล้ว · ไม่ยกเลิก/ปฏิเสธ) — ตัวโหลดเดียวของ
    /// QR บนกระดาษ · ลิงก์ชำระ (<c>DocumentPayLinkService</c>) · งานหลังรับชำระ · null = ไม่มี/ไม่ใช่ใบแจ้งหนี้ (ใบกำกับถูกยกเลิก ⇒ null = ลิงก์ของใบแจ้งหนี้กลับมาใช้ได้)</summary>
    internal static Task<string?> SupersedingTaxInvoiceNumberAsync(AccountingDbContext db, Guid companyId, Document doc, CancellationToken ct)
        => SupersedingTaxInvoiceNumberAsync(db, companyId, doc.DocumentType, doc.Id, ct);

    internal static async Task<string?> SupersedingTaxInvoiceNumberAsync(AccountingDbContext db, Guid companyId, DocumentType type, Guid documentId,
        CancellationToken ct)
    {
        if (type != DocumentType.Invoice || documentId == Guid.Empty) return null;
        return await db.Documents.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.RelatedDocumentId == documentId && !c.IsDeleted
                && c.DocumentType == DocumentType.TaxInvoice
                && c.Status != DocumentStatus.Draft && c.Status != DocumentStatus.WaitingApproval
                && c.Status != DocumentStatus.Rejected && c.Status != DocumentStatus.Voided)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.DocumentNumber)
            .FirstOrDefaultAsync(ct);
    }

    // ═══════════════ ตั้งค่าบัญชี ═══════════════

    private async Task<BankAccount> AccountAsync(Guid companyId, Guid accountId, CancellationToken ct)
        => await _db.Set<BankAccount>().Include(a => a.LinkedAccount)
               .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted, ct)
           ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

    private static string Sha(byte[]? b) => b == null ? "" : Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private void Audit(Guid companyId, BankAccount a, string @event, object before, object after, string actor)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(BankAccount), EntityId = a.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event, after, actor, ruleCode = PaymentQrPolicy.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });

    public async Task<BankAccountResponse> UploadImageAsync(Guid companyId, Guid accountId, byte[] content, string actor, CancellationToken ct = default)
    {
        // ฝ่ายค้าน N4: ตรวจรูป → ตรวจบัญชี (ของบริษัทนี้) → ถอด/เข้ารหัสใหม่ → เขียนไฟล์ → บันทึก · บันทึกล้ม ⇒ ลบไฟล์ที่เพิ่งเขียน
        var check = PaymentQrImageValidator.Validate(content);
        if (!check.Ok) throw new BusinessRuleException(check.Message!, PaymentQrPolicy.RuleCode);
        var a = await AccountAsync(companyId, accountId, ct);
        var png = Reencode(content)
                  ?? throw new BusinessRuleException("อ่านรูปไม่ได้ — ไฟล์เสียหรือไม่ใช่รูป PNG/JPEG จริง", PaymentQrPolicy.RuleCode);
        var old = a.PaymentQrImagePath;
        var oldOwn = IsOwnLocation(old, Root, companyId);
        var oldHash = oldOwn ? Sha(await _storage.ReadAllBytesAsync(old, ct)) : "";
        var location = Path.Combine(Root, StorageFolder, companyId.ToString("N"), $"{accountId:N}-{Guid.NewGuid():N}.png");
        await _storage.WriteAsync(location, png, "image/png", ct);
        a.PaymentQrImagePath = location;
        a.PaymentQrUploadedAt = DateTime.UtcNow;
        a.UpdatedAt = DateTime.UtcNow;
        Audit(companyId, a, "PaymentQrImageUploaded", new { hadImage = old != null, sha256 = oldHash },
            new { sha256 = Sha(png), check.Width, check.Height, uploadedType = check.ContentType }, actor);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // แถวยังชี้ไฟล์เดิม — ไฟล์ใหม่ที่ไม่มีใครอ้างต้องไม่ค้างในที่เก็บ · ล้มดังต่อ (ไม่กลืน)
            await _storage.DeleteAsync(location, CancellationToken.None);
            throw;
        }
        // ไฟล์เดิมลบหลังบันทึกสำเร็จ · รูป QR ไม่ใช่เอกสารทางบัญชี (ไม่อยู่ใต้ระยะเก็บ ม.10) — ประวัติอยู่ใน audit (hash)
        if (old != null && old != location && oldOwn)
            await _storage.DeleteAsync(old, ct);
        return Map(a);
    }

    public async Task<BankAccountResponse> RemoveImageAsync(Guid companyId, Guid accountId, string actor, CancellationToken ct = default)
    {
        var a = await AccountAsync(companyId, accountId, ct);
        var old = a.PaymentQrImagePath
                  ?? throw new BusinessRuleException("บัญชีนี้ยังไม่มีรูป QR ที่อัปโหลด", PaymentQrPolicy.RuleCode);
        var own = IsOwnLocation(old, Root, companyId);
        var oldHash = own ? Sha(await _storage.ReadAllBytesAsync(old, ct)) : "";
        a.PaymentQrImagePath = null;
        a.PaymentQrUploadedAt = null;
        a.UpdatedAt = DateTime.UtcNow;
        Audit(companyId, a, "PaymentQrImageRemoved", new { sha256 = oldHash }, new { hadImage = false }, actor);
        await _db.SaveChangesAsync(ct);
        if (own) await _storage.DeleteAsync(old, ct);
        return Map(a);
    }

    public async Task<BankAccountResponse> UpdateSettingsAsync(Guid companyId, Guid accountId, UpdateBankPaymentQrRequest request, string actor,
        CancellationToken ct = default)
    {
        if (PaymentQrPolicy.SourceProblem(request.PaymentQrSource) is { } bad) throw new BusinessRuleException(bad, PaymentQrPolicy.RuleCode);
        string? newId = null;
        if (request.PromptPayId != null)
        {
            var (id, problem) = PaymentQrPolicy.NormalizePromptPayId(request.PromptPayId);
            if (problem != null) throw new BusinessRuleException(problem, PaymentQrPolicy.RuleCode);
            newId = id;
        }
        var a = await AccountAsync(companyId, accountId, ct);
        var before = new { a.PromptPayId, a.PaymentQrSource };
        if (request.PromptPayId != null) a.PromptPayId = newId;
        if (request.PaymentQrSource != null) a.PaymentQrSource = PaymentQrPolicy.NormalizeSource(request.PaymentQrSource);
        if (before.PromptPayId == a.PromptPayId && before.PaymentQrSource == a.PaymentQrSource) return Map(a);
        a.UpdatedAt = DateTime.UtcNow;
        Audit(companyId, a, "PaymentQrSettingsUpdated", before, new { a.PromptPayId, a.PaymentQrSource }, actor);
        await _db.SaveChangesAsync(ct);
        return Map(a);
    }

    public async Task<BankPaymentQrPreviewResponse> PreviewAsync(Guid companyId, Guid accountId, string? promptPayId, string? source, decimal? amount,
        CancellationToken ct = default)
    {
        var a = await _db.Set<BankAccount>().AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == accountId && x.CompanyId == companyId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");
        var id = a.PromptPayId;
        if (promptPayId != null)
        {
            var (norm, problem) = PaymentQrPolicy.NormalizePromptPayId(promptPayId);
            if (problem != null) return new BankPaymentQrPreviewResponse(null, null, null, null, problem);
            id = norm;
        }
        if (PaymentQrPolicy.SourceProblem(source) is { } badSource) return new BankPaymentQrPreviewResponse(null, null, null, null, badSource);
        var amt = amount is > 0.005m ? Math.Round(amount.Value, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
        var qr = await BuildAccountQrAsync(_storage, _promptPay, _logger, companyId, a, id, source ?? a.PaymentQrSource, amt, ct);
        if (qr == null)
            return new BankPaymentQrPreviewResponse(null, null, null, null,
                a.PaymentQrImagePath != null && !PaymentQrPolicy.PromptPayUsable(id)
                    ? "อ่านรูป QR ที่อัปโหลดไว้ไม่ได้ (ไฟล์หายจากที่เก็บ) — อัปโหลดใหม่"
                    : "ยังไม่มี QR — กรอกเลขพร้อมเพย์ หรืออัปโหลดรูป QR ของบัญชีนี้");
        return new BankPaymentQrPreviewResponse(
            qr.Kind == PaymentQrPolicy.Kind.PromptPay ? PaymentQrPolicy.SourcePromptPay : PaymentQrPolicy.SourceUpload,
            "data:image/png;base64," + Convert.ToBase64String(qr.Png), qr.Amount,
            qr.PromptPayId == null ? null : PaymentQrPolicy.DisplayPromptPayId(qr.PromptPayId),
            qr.Kind == PaymentQrPolicy.Kind.Upload ? "รูปที่อัปโหลด — ไม่ฝังยอดเงิน ลูกค้ากรอกยอดเอง" : null);
    }

    // ═══════════════ QR บนเอกสาร ═══════════════

    public async Task<PaymentQrBlock?> ResolveForDocumentAsync(Document doc, DocumentTemplate template, CancellationToken ct = default)
        => (await ResolveCoreAsync(doc, template, ct)).Block;

    /// <summary>ตัวตัดสินเดียวของใบ (กระดาษ + เหตุ/คำเตือนบนหน้าเอกสาร) — ResolveForDocumentAsync/DiagnoseDocumentAsync เรียกตัวนี้</summary>
    private async Task<PaymentQrDiagnosis> ResolveCoreAsync(Document doc, DocumentTemplate template, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (!template.ShowPaymentQr && !template.ShowPayLinkQr)
            return new PaymentQrDiagnosis(null, null, null, null, warnings);
        var cid = doc.CompanyId;
        var relatedType = doc.RelatedDocumentId is Guid rid
            ? await _db.Documents.AsNoTracking().Where(r => r.Id == rid && r.CompanyId == cid).Select(r => (DocumentType?)r.DocumentType).FirstOrDefaultAsync(ct)
            : null;
        var wantBank = PaymentQrPolicy.ShowBankQr(template.ShowPaymentQr, doc.DocumentType, relatedType, doc.Status, doc.BalanceDue, doc.Currency);
        var superseded = await SupersededByTaxInvoiceAsync(_db, cid, doc, ct);
        if (superseded && wantBank) warnings.Add(NoteSupersededByTaxInvoice);
        // ข้อเท็จจริงของลิงก์ (ยอดสุทธิ + ลิงก์ที่ยังใช้ได้) — ใบที่ยังไม่อยู่ในฐาน (ตัวอย่าง) ไม่มี
        var facts = (wantBank || template.ShowPayLinkQr) && _payLinks != null && doc.Id != Guid.Empty
            ? await _payLinks.PayQrFactsAsync(cid, doc.Id, ct) : null;
        if (facts?.WhtNote is { } wht) warnings.Add(wht);   // ฝ่ายค้าน N5 — QR ฝังยอดเดียวกับลิงก์ · คำเตือนเดียวกับรอบ 244 S1

        BankPaymentQr? bank = null;
        Guid? accountId = null;
        string? label = null, problem = null;
        if (wantBank)
        {
            var (acct, pick) = await PickDocumentAccountAsync(_db, Root, cid, doc.PaymentAccountId, doc.BankAccountId, template.PaymentQrBankAccountId, ct);
            problem = pick.Problem;
            if (acct != null)
            {
                accountId = acct.Id;
                label = AccountLabel(acct);
                // ไม่มีข้อเท็จจริงของลิงก์ (ไม่มี service/ใบไม่อยู่ในฐาน) = ไม่รู้ยอดที่เชื่อได้ ⇒ QR ไม่ฝังยอด
                var amount = PaymentQrPolicy.EmbedAmount(facts == null ? "unknown" : facts.PayLinkRefusal, facts?.NetAmount, superseded);
                bank = await BuildAccountQrAsync(_storage, _promptPay, _logger, cid, acct, acct.PromptPayId, acct.PaymentQrSource, amount, ct);
                if (template.ShowBankDetails
                    && PaymentQrPolicy.BankTextMismatch(template.BankDetailsText, acct.AccountNumber, label) is { } mismatch)
                    warnings.Add(mismatch);
            }
        }

        PayLinkQr? pay = null;
        if (facts != null && !superseded
            && PaymentQrPolicy.ShowPayLinkQr(template.ShowPayLinkQr, pdfA: false, facts.OnlineUrl, facts.OnlineChannelCount))
            pay = new PayLinkQr(UrlQrPng(facts.OnlineUrl!), facts.ExpiresAt);

        var block = bank == null && pay == null ? null : new PaymentQrBlock(bank, pay);
        return new PaymentQrDiagnosis(block, accountId, label, problem, warnings);
    }

    public async Task<PaymentQrDiagnosis> DiagnoseDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Brand)
                      .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct)
                  ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var template = await PdfGenerationService.ResolveDocumentTemplateAsync(_db, companyId, doc, null);
        var r = await ResolveCoreAsync(doc, template, ct);
        return r with { Block = null };   // หน้าเว็บต้องการเหตุ/คำเตือน ไม่ต้องการรูป
    }

    public async Task<PaymentQrDiagnosis> DiagnoseTemplateAsync(Guid companyId, DocumentTemplate template, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (!template.ShowPaymentQr || !PaymentQrPolicy.IsBankQrDocument(template.DocumentType, null))
            return new PaymentQrDiagnosis(null, null, null, null, warnings);
        var (acct, pick) = await PickDocumentAccountAsync(_db, Root, companyId, null, null, template.PaymentQrBankAccountId, ct);
        string? label = acct == null ? null : AccountLabel(acct);
        if (acct != null && template.ShowBankDetails
            && PaymentQrPolicy.BankTextMismatch(template.BankDetailsText, acct.AccountNumber, label) is { } mismatch)
            warnings.Add(mismatch);
        return new PaymentQrDiagnosis(null, acct?.Id, label, pick.Problem, warnings);
    }

    public async Task<PaymentQrBlock?> SampleForTemplateAsync(Guid companyId, DocumentTemplate template, CancellationToken ct = default)
    {
        if (!template.ShowPaymentQr || !PaymentQrPolicy.IsBankQrDocument(template.DocumentType, null)) return null;
        var (acct, _) = await PickDocumentAccountAsync(_db, Root, companyId, null, null, template.PaymentQrBankAccountId, ct);
        if (acct == null) return null;
        var bank = await BuildAccountQrAsync(_storage, _promptPay, _logger, companyId, acct, acct.PromptPayId, acct.PaymentQrSource, null, ct);
        return bank == null ? null : new PaymentQrBlock(bank, null);
    }
}
