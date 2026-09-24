using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IIssuedDocumentHooks"/> — ย้ายมาจาก <c>DocumentService.TryAutoGenerateEtaxAsync</c> (รอบ 193 · S-02)
/// เพื่อให้ POS · Integration · CMS · ApproveDocumentAsync · ใบเสร็จ settlement เดินขั้นเดียวกัน</summary>
public class IssuedDocumentHooks : IIssuedDocumentHooks
{
    private readonly AccountingDbContext _db;
    private readonly IEtaxInvoiceService _etax;
    private readonly ILogger<IssuedDocumentHooks> _logger;

    public IssuedDocumentHooks(AccountingDbContext db, IEtaxInvoiceService etax, ILogger<IssuedDocumentHooks> logger)
    {
        _db = db;
        _etax = etax;
        _logger = logger;
    }

    public async Task<IssuedDocumentHookResult> RunAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
        if (doc == null)
        {
            _logger.LogWarning("IssuedDocumentHooks: ไม่พบเอกสาร {DocId} ของบริษัท {CompanyId}", documentId, companyId);
            return IssuedDocumentHookResult.Nothing;
        }
        return await RunAsync(companyId, doc, ct);
    }

    public async Task<IssuedDocumentHookResult> RunAsync(Guid companyId, Document doc, CancellationToken ct = default)
    {
        if (doc.CompanyId != companyId) return IssuedDocumentHookResult.Nothing;   // tenant isolation
        return await TryAutoGenerateEtaxAsync(companyId, doc, ct);
    }

    /// <summary>
    /// ออก e-Tax อัตโนมัติเมื่อบริษัทเปิด e-Tax — ขอบเขตรายใบจาก <see cref="EtaxAutoIssueScope.Judge"/>
    /// (ข้ามโดยเจตนา = เงียบ · ควรมีแต่ออกไม่ได้ = ดัง 3 ที่: ป้ายบนเอกสาร · log · ผลลัพธ์ให้ผู้เรียก)
    /// </summary>
    private async Task<IssuedDocumentHookResult> TryAutoGenerateEtaxAsync(Guid companyId, Document doc, CancellationToken ct)
    {
        // ตัดชนิดที่ไม่มีทางมี e-Tax ก่อนแตะฐานข้อมูล (POS/Integration เรียกทุกใบ)
        if (!EtaxAutoIssueScope.IsEtaxType(doc.DocumentType)) return IssuedDocumentHookResult.Nothing;

        try
        {
            var settings = await _db.CompanySettings.AsNoTracking()
                .Where(s => s.CompanyId == companyId)
                .Select(s => new { s.EtaxEnabled, s.EtaxAutoSign })
                .FirstOrDefaultAsync(ct);
            if (settings?.EtaxEnabled != true) return IssuedDocumentHookResult.Nothing;

            DocumentType? relatedType = null;
            if (EtaxAutoIssueScope.NeedsRelatedType(doc.DocumentType, doc.RelatedDocumentId))
                relatedType = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == doc.RelatedDocumentId!.Value && d.CompanyId == companyId)
                    .Select(d => (DocumentType?)d.DocumentType)
                    .FirstOrDefaultAsync(ct);

            // "ไม่ใช่ใบกำกับเต็มรูป" (walk-in · ไม่ประสงค์รับ · ผู้ซื้อ §86/4 ไม่ครบ) — เกณฑ์ TaxService.NotFullTaxInvoice
            // ตัวเดียวกับหัว PDF/GenerateAsync · ต้องใช้ผู้ติดต่อ: เส้น API/POS อาจไม่มี navigation ⇒ อ่านแยก
            // (IgnoreQueryFilters — ผู้ติดต่อที่ถูกลบภายหลังยังเป็นผู้ซื้อของใบนี้ · กรอง CompanyId เอง) · ฝ่ายค้าน C-1
            var notFull = false;
            if (EtaxAutoIssueScope.IsTitleDecidedType(doc.DocumentType) && doc.VatAmount > 0.005m)
            {
                var buyer = doc.Contact ?? await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == companyId, ct);
                notFull = TaxService.NotFullTaxInvoice(doc.VatAmount, doc.BuyerDeclinedTaxInvoice, buyer);
            }

            var skip = EtaxAutoIssueScope.Judge(doc.DocumentType, doc.Status,
                doc.IsDeposit, doc.DepositOutputVatDeferred, doc.DepositOutputVatRecognizedAt, relatedType,
                doc.IsTaxInvoiceByLaw, notFull);
            if (skip != EtaxAutoSkip.None)
            {
                _logger.LogInformation("Auto e-Tax skipped for {DocNumber}: {Reason}", doc.DocumentNumber, skip);
                return IssuedDocumentHookResult.Nothing;
            }

            // มี e-Tax (ที่ไม่ใช่ Error) แล้ว = ข้าม — GenerateAsync จะโยน "มี e-Tax แล้ว" ซึ่งไม่ใช่ความล้มเหลว
            if (await HasLiveEtaxAsync(companyId, doc.Id, ct)) return IssuedDocumentHookResult.Nothing;

            // SignDigitally อ่านจากค่าตั้ง (เดิม CMS ส่ง true ตายตัว — ผลเท่ากันเพราะ GenerateAsync
            // ตรวจ EtaxAutoSign อีกชั้น แต่ค่าตั้งต้องเป็นที่ตัดสิน ไม่ใช่ผู้เรียก)
            // GenerateAsync ล็อกแถวเอกสารแล้วตรวจซ้ำในธุรกรรม — hook สองตัวพร้อมกันได้ e-Tax แถวเดียว (P-2)
            // และล้างป้าย [ETAX-AUTO-FAILED] ของรอบก่อนเมื่อออกสำเร็จ (P-3)
            await _etax.GenerateAsync(companyId, new GenerateEtaxRequest(doc.Id, SignDigitally: settings.EtaxAutoSign));
            return new IssuedDocumentHookResult(EtaxAttempted: true, EtaxFailureReason: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // แถว e-Tax ที่ถูก Add ค้างไว้แต่บันทึกไม่สำเร็จ ห้ามค้างใน context ของผู้เรียก
            foreach (var e in _db.ChangeTracker.Entries<EtaxInvoice>()
                         .Where(e => e.State == EntityState.Added && e.Entity.DocumentId == doc.Id).ToList())
                e.State = EntityState.Detached;

            // ผู้เรียกอีกตัวออกให้ไปแล้วระหว่างนี้ (webhook ซ้ำ/ยืนยันซ้ำพร้อมกัน) = ไม่ใช่ความล้มเหลว
            if (await HasLiveEtaxAsync(companyId, doc.Id, ct)) return IssuedDocumentHookResult.Nothing;

            // เอกสารออกสำเร็จไปแล้ว จึงไม่ throw (ล้มทั้งรายการ = ทิ้งการขายที่เกิดจริง)
            // — แต่ **ห้ามเงียบ** (F2 ข้อ 7): ป้ายบนเอกสาร (หน้าเอกสารแสดงผ่าน EtaxAutoFailed) + log + ผลลัพธ์ให้ผู้เรียก
            // ที่มา: SYSTEM_AUDIT_2026-09-07 B-09 — เดิมมีแต่ LogWarning ⇒ ใบที่ล้มไม่ถูกนำส่งภายในวันที่ 15
            // (ห้ามใช้ `Notes` — PdfGenerationService พิมพ์ลงกระดาษที่ส่งให้ลูกค้า)
            _logger.LogWarning(ex, "Auto e-Tax generation failed for document {DocId} ({DocNumber})",
                doc.Id, doc.DocumentNumber);
            await StampFailureAsync(companyId, doc, ex.Message, ct);
            return new IssuedDocumentHookResult(EtaxAttempted: true, EtaxFailureReason: ex.Message);
        }
    }

    private Task<bool> HasLiveEtaxAsync(Guid companyId, Guid documentId, CancellationToken ct)
        => _db.EtaxInvoices.AsNoTracking()
            .AnyAsync(e => e.DocumentId == documentId && e.CompanyId == companyId
                        && e.Status != EtaxStatus.Error, ct);

    /// <summary>ประทับป้ายลง <c>InternalNotes</c> — **บันทึกเฉพาะแถวเอกสารนี้** (ExecuteUpdate) ไม่ใช่ SaveChanges ทั้ง context
    /// (ฝ่ายค้าน P-3: entity อื่นที่ขั้นก่อนหน้า Add ค้างไว้แล้วล้ม จะติดไปกับการบันทึกป้าย) · บันทึกไม่ได้ = LogError</summary>
    private async Task StampFailureAsync(Guid companyId, Document doc, string reason, CancellationToken ct)
    {
        try
        {
            // อ่านค่าล่าสุดจากฐาน (ไม่ใช่จาก entity ที่อาจค้างค่าเก่า) แล้วต่อท้าย — ป้ายเดิมถูกแทน ไม่สะสม
            var current = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == doc.Id && d.CompanyId == companyId)
                .Select(d => d.InternalNotes)
                .FirstOrDefaultAsync(ct);
            var updated = EtaxAutoFailedNote.Append(current, reason);
            await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == companyId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.InternalNotes, updated), ct);

            // ให้ entity ในหน่วยความจำตรงกับฐาน โดยไม่ทำให้ SaveChanges ถัดไปของผู้เรียกเขียนซ้ำ
            doc.InternalNotes = updated;
            var entry = _db.Entry(doc);
            if (entry.State is EntityState.Unchanged or EntityState.Modified)
            {
                var prop = entry.Property(d => d.InternalNotes);
                prop.OriginalValue = updated;
                prop.IsModified = false;
            }
        }
        catch (Exception saveEx) when (saveEx is not OperationCanceledException)
        {
            _logger.LogError(saveEx,
                "บันทึกป้าย {Marker} ลงเอกสาร {DocNumber} ไม่สำเร็จ — e-Tax ของใบนี้ยังไม่ถูกออกและไม่มีป้ายบนเอกสาร: {Reason}",
                EtaxAutoFailedNote.Marker, doc.DocumentNumber, reason);
        }
    }
}
