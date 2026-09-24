using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IIssuedDocumentHooks"/> — ย้ายมาจาก <c>DocumentService.TryAutoGenerateEtaxAsync</c> (รอบ 193 · S-02)
/// เพื่อให้ POS · Integration · CMS · ApproveDocumentAsync เดินขั้นเดียวกัน</summary>
public class IssuedDocumentHooks : IIssuedDocumentHooks
{
    /// <summary>ป้ายบนหมายเหตุภายในเมื่อออก e-Tax อัตโนมัติไม่สำเร็จ — ผู้ใช้/รายงานค้นด้วยป้ายนี้</summary>
    public const string EtaxAutoFailedMarker = "[ETAX-AUTO-FAILED]";

    private readonly AccountingDbContext _db;
    private readonly IEtaxInvoiceService _etax;
    private readonly ILogger<IssuedDocumentHooks> _logger;

    public IssuedDocumentHooks(AccountingDbContext db, IEtaxInvoiceService etax, ILogger<IssuedDocumentHooks> logger)
    {
        _db = db;
        _etax = etax;
        _logger = logger;
    }

    public async Task RunAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
        if (doc == null)
        {
            _logger.LogWarning("IssuedDocumentHooks: ไม่พบเอกสาร {DocId} ของบริษัท {CompanyId}", documentId, companyId);
            return;
        }
        await RunAsync(companyId, doc, ct);
    }

    public async Task RunAsync(Guid companyId, Document doc, CancellationToken ct = default)
    {
        if (doc.CompanyId != companyId) return;   // tenant isolation — ไม่แตะใบของบริษัทอื่นเด็ดขาด
        await TryAutoGenerateEtaxAsync(companyId, doc, ct);
    }

    /// <summary>
    /// ออก e-Tax อัตโนมัติเมื่อบริษัทเปิด e-Tax — ขอบเขตรายใบจาก <see cref="EtaxAutoIssueScope.Judge"/>
    /// (ข้ามโดยเจตนา = เงียบ · ควรมีแต่ออกไม่ได้ = ดัง)
    /// </summary>
    private async Task TryAutoGenerateEtaxAsync(Guid companyId, Document doc, CancellationToken ct)
    {
        // ตัดชนิด/สถานะที่ไม่มีทางมี e-Tax ก่อนแตะฐานข้อมูล (POS/Integration เรียกทุกใบ)
        if (!EtaxAutoIssueScope.IsEtaxType(doc.DocumentType)) return;

        try
        {
            var settings = await _db.CompanySettings.AsNoTracking()
                .Where(s => s.CompanyId == companyId)
                .Select(s => new { s.EtaxEnabled, s.EtaxAutoSign })
                .FirstOrDefaultAsync(ct);
            if (settings?.EtaxEnabled != true) return;

            DocumentType? relatedType = null;
            if (EtaxAutoIssueScope.NeedsRelatedType(doc.DocumentType, doc.RelatedDocumentId))
                relatedType = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == doc.RelatedDocumentId!.Value && d.CompanyId == companyId)
                    .Select(d => (DocumentType?)d.DocumentType)
                    .FirstOrDefaultAsync(ct);

            var skip = EtaxAutoIssueScope.Judge(doc.DocumentType, doc.Status,
                doc.IsDeposit, doc.DepositOutputVatDeferred, doc.DepositOutputVatRecognizedAt, relatedType);
            if (skip != EtaxAutoSkip.None)
            {
                _logger.LogInformation("Auto e-Tax skipped for {DocNumber}: {Reason}", doc.DocumentNumber, skip);
                return;
            }

            // มี e-Tax (ที่ไม่ใช่ Error) แล้ว = ข้าม — GenerateAsync จะโยน "มี e-Tax แล้ว" ซึ่งไม่ใช่ความล้มเหลว
            var alreadyExists = await _db.EtaxInvoices.AsNoTracking()
                .AnyAsync(e => e.DocumentId == doc.Id && e.CompanyId == companyId
                            && e.Status != EtaxStatus.Error, ct);
            if (alreadyExists) return;

            // SignDigitally อ่านจากค่าตั้ง (เดิม CMS ส่ง true ตายตัว — ผลเท่ากันเพราะ GenerateAsync
            // ตรวจ EtaxAutoSign อีกชั้น แต่ค่าตั้งต้องเป็นที่ตัดสิน ไม่ใช่ผู้เรียก)
            await _etax.GenerateAsync(companyId, new GenerateEtaxRequest(doc.Id, SignDigitally: settings.EtaxAutoSign));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // เอกสารออกสำเร็จไปแล้ว จึงไม่ throw (ล้มทั้งรายการ = ทิ้งการขายที่เกิดจริง)
            // — แต่ **ห้ามเงียบ** (F2 ข้อ 7): ประทับบนตัวเอกสารที่ผู้ใช้เปิดดู + log
            // ที่มา: SYSTEM_AUDIT_2026-09-07 B-09 — เดิมมีแต่ LogWarning ⇒ ใบที่ล้มไม่ถูกนำส่งภายในวันที่ 15
            // (ห้ามใช้ `Notes` — PdfGenerationService พิมพ์ลงกระดาษที่ส่งให้ลูกค้า)
            _logger.LogWarning(ex, "Auto e-Tax generation failed for document {DocId} ({DocNumber})",
                doc.Id, doc.DocumentNumber);
            await StampFailureAsync(companyId, doc, ex.Message, ct);
        }
    }

    /// <summary>ประทับความล้มเหลวลง <c>InternalNotes</c> แล้วบันทึก — ถ้าบันทึกไม่ได้ต้อง log เป็น Error
    /// (ไม่มีที่อื่นให้ผู้ใช้เห็นแล้ว)</summary>
    private async Task StampFailureAsync(Guid companyId, Document doc, string reason, CancellationToken ct)
    {
        var note = $"{EtaxAutoFailedMarker} ออก e-Tax อัตโนมัติไม่สำเร็จ: {reason} — "
            + "กด “สร้าง e-Tax” ที่หน้ารายละเอียดเอกสารเพื่อลองใหม่ "
            + "(เอกสารนี้ยังไม่ถูกนำส่งกรมสรรพากร)";
        try
        {
            // แถว e-Tax ที่ถูก Add ค้างไว้แต่บันทึกไม่สำเร็จ ห้ามติดไปกับการบันทึกหมายเหตุ
            foreach (var e in _db.ChangeTracker.Entries<EtaxInvoice>()
                         .Where(e => e.State == EntityState.Added && e.Entity.DocumentId == doc.Id).ToList())
                e.State = EntityState.Detached;

            var tracked = _db.Entry(doc).State != EntityState.Detached
                ? doc
                : await _db.Documents.FirstOrDefaultAsync(d => d.Id == doc.Id && d.CompanyId == companyId, ct);
            if (tracked == null) return;
            tracked.InternalNotes = string.IsNullOrWhiteSpace(tracked.InternalNotes)
                ? note
                : tracked.InternalNotes.TrimEnd() + "\n\n" + note;
            if (!ReferenceEquals(tracked, doc)) doc.InternalNotes = tracked.InternalNotes;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx) when (saveEx is not OperationCanceledException)
        {
            _logger.LogError(saveEx,
                "บันทึกป้าย {Marker} ลงเอกสาร {DocNumber} ไม่สำเร็จ — e-Tax ของใบนี้ยังไม่ถูกออกและไม่มีป้ายบนเอกสาร: {Reason}",
                EtaxAutoFailedMarker, doc.DocumentNumber, reason);
        }
    }
}
