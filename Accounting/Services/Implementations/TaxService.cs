using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class TaxService : ITaxService
{
    private readonly AccountingDbContext _db;

    // Thai WHT rate table by income type
    private static readonly Dictionary<string, decimal> WhtRateTable = new()
    {
        { "40(1)", 3m },   // เงินเดือน ค่าจ้าง (Salary, Wages)
        { "40(2)", 3m },   // ค่านายหน้า (Commission)
        { "40(3)", 3m },   // ค่าสิทธิ์/ลิขสิทธิ์ (Royalties) — ท.ป.4 หัก 3% (ทั้งบุคคลและนิติบุคคลไทย)
        { "40(4)a", 15m }, // ดอกเบี้ย (Interest)
        { "40(4)b", 10m }, // เงินปันผล (Dividends)
        { "40(5)", 5m },   // ค่าเช่า (Rent - property)
        { "40(6)", 3m },   // วิชาชีพอิสระ (Professional fees)
        { "40(7)", 3m },   // ค่ารับเหมา (Contractors)
        { "40(8)", 3m },   // ค่าจ้างทำของ (Service fees)
        { "3", 3m },       // ค่าบริการทั่วไป (General services)
        { "5", 1m },       // ค่าขนส่ง (Transportation)
        { "6", 2m },       // ค่าประกันภัย (Insurance premiums)
        { "advertising", 2m }, // ค่าโฆษณา
        { "default", 3m }      // Default rate
    };

    public TaxService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<TaxReportResponse> GenerateTaxReportAsync(Guid companyId, CreateTaxReportRequest request)
    {
        // Input validation
        if (request.Year < 2020 || request.Year > DateTime.UtcNow.Year + 1)
            throw new ArgumentException("ปีภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 2020 ถึงปีปัจจุบัน+1)");

        if (request.TaxType != TaxType.CorporateIncomeTax)
        {
            if (request.Month < 1 || request.Month > 12)
                throw new ArgumentException("เดือนภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 1 ถึง 12)");
        }

        if (await _db.TaxReports.AnyAsync(t =>
            t.CompanyId == companyId && t.TaxType == request.TaxType &&
            t.Year == request.Year && t.Month == request.Month))
            throw new InvalidOperationException("รายงานภาษีเดือนนี้มีอยู่แล้ว");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var startDate = new DateTime(request.Year, request.TaxType == TaxType.CorporateIncomeTax ? 1 : request.Month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
            var companyVatRate = company?.VatRate ?? 7m;

            var report = new TaxReport
            {
                CompanyId = companyId,
                TaxType = request.TaxType,
                Year = request.Year,
                Month = request.Month
            };

            if (request.TaxType == TaxType.VAT)
            {
                await GenerateVatReport(companyId, startDate, endDate, report);
                await ApplyVatDeferralsAsync(companyId, request.Year, request.Month, report);
            }
            else if (request.TaxType == TaxType.WithholdingTax3
                  || request.TaxType == TaxType.WithholdingTax53
                  || request.TaxType == TaxType.WithholdingTax1
                  || request.TaxType == TaxType.WithholdingTax54)
            {
                // PND.54 reuses the WHT report generator — the difference is
                // in the form's per-line IncomeTypeCode and the e-Filing export
                // layout (BuildPndAsync handles 54 separately).
                await GenerateWhtReport(companyId, startDate, endDate, report);
            }
            else if (request.TaxType == TaxType.VatPp36)
            {
                // PP.36 — foreign service VAT. Treat like VAT report but
                // pulled only from documents flagged as foreign-supplier
                // (heuristic: contact has non-Thai TaxId or is marked
                // ForeignSupplier). For now reuse the VAT generator;
                // ApplyVatDeferralsAsync skips this branch.
                await GenerateVatReport(companyId, startDate, endDate, report);
            }
            else if (request.TaxType == TaxType.CorporateIncomeTax)
            {
                await GenerateCitReport(companyId, request.Year, report);
            }
            else if (request.TaxType == TaxType.PersonalIncomeTax91)
            {
                await GeneratePnd91Report(companyId, request.Year, report);
            }

            _db.TaxReports.Add(report);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();

            return await GetTaxReportAsync(companyId, report.Id);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task GenerateVatReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var companyVatRate = company?.VatRate ?? 7m;

        // ✅ Filter by TaxPointDate (สอดคล้องกับ §78/§78/1/§82/3) ไม่ใช่
        // DocumentDate — ใบสำคัญจ่ายที่จ่าย มิ.ย. แต่อ้างใบกำกับซื้อ พ.ค.
        // ต้องลง ภพ.30 งวด พ.ค. (= วันที่ใบกำกับของผู้ขาย) ไม่ใช่ มิ.ย.
        // (= วันจ่าย). TaxPointDate snapshot ตอน approve ผ่าน TaxPointResolver
        // = MIN(SupplierTaxInvoiceDate, PaymentDate, DocumentDate, ...).
        // Fallback DocumentDate สำหรับเอกสารเก่าที่ approve ก่อนเพิ่ม snapshot.
        var docs = await _db.Documents
            .Include(d => d.Lines)
            // ไม่ Include Contact (required nav + !IsDeleted filter → INNER JOIN
            // ตัดใบที่ contact ถูกลบ = under-report ภ.พ.30). hydrate แยกด้านล่าง
            .Where(d => d.CompanyId == companyId
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.VatAmount != 0
                // ปกติ: tax point อยู่ในงวด — OR: ภาษีซื้อที่ "ถึงกำหนดเคลม" เดือนนี้
                // (BecameClaimableAt) แม้วันที่เอกสารอยู่เดือนก่อน (§83/6 ภ.พ.36 รับรู้
                // ทีหลัง / §86/4 เติมใบกำกับครบทีหลัง) — เดิม query เอา DocumentDate
                // อย่างเดียว → ใบเดือน พ.ค. รับรู้ ก.ค. หลุดจากรายงาน ก.ค. ทั้งใบ = หาไม่เจอ
                && (
                    ((d.TaxPointDate ?? d.DocumentDate) >= startDate && (d.TaxPointDate ?? d.DocumentDate) <= endDate)
                    || (d.InputVatBecameClaimableAt != null
                        && d.InputVatBecameClaimableAt >= startDate && d.InputVatBecameClaimableAt <= endDate)
                    // ฝั่งขาย mirror: ใบแจ้งหนี้บริการที่ VAT "ถึงกำหนด" เดือนนี้
                    // (รับเงินเดือนนี้) แม้วันที่ใบอยู่เดือนก่อน — ต้องเข้ารายงานเดือนนี้
                    || (d.OutputVatDueAt != null
                        && d.OutputVatDueAt >= startDate && d.OutputVatDueAt <= endDate)
                ))
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

        // Cross-report dedup: a document already claimed (a non-excluded line)
        // in ANOTHER VAT report must not be claimed again here. This both
        // prevents accidental double-claiming and lets the "pull document"
        // feature move an invoice into a different period safely.
        var claimedElsewhere = (await _db.TaxReportLines
            .Where(l => l.DocumentId != null && !l.IsExcluded
                && l.TaxReportId != report.Id
                && l.TaxReport.CompanyId == companyId
                && l.TaxReport.TaxType == TaxType.VAT)
            .Select(l => l.DocumentId!.Value)
            .ToListAsync())
            .ToHashSet();
        if (claimedElsewhere.Count > 0)
            docs = docs.Where(d => !claimedElsewhere.Contains(d.Id)).ToList();

        // มัดจำเคส Deferred output VAT (§78): tax point เกิดเมื่อ
        // DepositOutputVatRecognizedAt ไม่ใช่ DocumentDate — ดึงเพิ่มใบที่
        // recognized ในงวดนี้แต่ DocumentDate อยู่นอกงวด (กันตกหล่นจาก ภ.พ.30).
        // ใบที่ DocumentDate อยู่ในงวดอยู่แล้วถูกดึงข้างบน แล้ว Receipt branch
        // จะกรองด้วย RecognizedAt เอง.
        var deferredRecognized = await _db.Documents
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดแถว)
            .Where(d => d.CompanyId == companyId
                && d.IsDeposit
                && d.DepositOutputVatRecognizedAt != null
                && d.DepositOutputVatRecognizedAt >= startDate && d.DepositOutputVatRecognizedAt <= endDate
                && ((d.TaxPointDate ?? d.DocumentDate) < startDate || (d.TaxPointDate ?? d.DocumentDate) > endDate)
                // มัดจำที่ถูก "นำไปหัก" ในใบกำกับ/ใบเสร็จปลายทาง (drives/apply) — VAT
                // ทั้งก้อนถูกรายงานโดยใบปลายทางแล้ว (Cr 21911 เต็มใบ) → ห้ามดึงมา
                // เพิ่มแถวซ้ำ (นับซ้ำ = ยอดขาย/ภาษีขายเกินจริง)
                && d.DepositAppliedToDocumentId == null
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.VatAmount != 0)
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, deferredRecognized);
        if (deferredRecognized.Count > 0)
        {
            var existing = docs.Select(d => d.Id).ToHashSet();
            docs.AddRange(deferredRecognized.Where(d => !existing.Contains(d.Id)));
        }

        // GL-first (หลักเดียวกับ drives d7ee4d3): มัดจำที่ "ขา VAT จริง" ลง 21913
        // แต่ flag DepositOutputVatDeferred ไม่ได้ตั้ง (book ผ่านช่องทางที่โพสต์
        // JE เอง) ต้องนับเป็น deferred ใน ภ.พ.30 ด้วย — ไม่งั้นรายงานเดือนรับเงิน
        // โชว์ VAT ที่ GL ยังพักอยู่ 21913 (ไม่ตรง GL + นับซ้ำกับใบเช็คเอาท์เดือน
        // ถัดไป). ยอด Cr สุทธิบน 21913 ของ JE ใบมัดจำเอง (checkout Dr อยู่คนละ JE)
        // = ตัวชี้ "book แบบ defer" ที่เสถียรตลอดเวลา → regenerate งวดเก่าได้ผลเดิม
        var depositDocIds = docs.Where(d => d.IsDeposit).Select(d => d.Id).ToList();
        var gl21913NetByDoc = depositDocIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.JournalEntryLines
                .Where(l => !l.IsDeleted
                    && l.JournalEntry.CompanyId == companyId
                    && l.JournalEntry.SourceDocumentId != null
                    && depositDocIds.Contains(l.JournalEntry.SourceDocumentId.Value)
                    && !l.JournalEntry.IsDeleted
                    && (l.JournalEntry.Status == JournalEntryStatus.Posted
                        || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                    && l.Account.AccountCode == "21913")
                .GroupBy(l => l.JournalEntry.SourceDocumentId!.Value)
                .Select(g => new { DocId = g.Key, Net = g.Sum(x => x.CreditAmount - x.DebitAmount) })
                .ToListAsync())
                .ToDictionary(x => x.DocId, x => x.Net);

        // Accounts whose input VAT is prohibited (ภาษีซื้อต้องห้าม, §82/5) —
        // e.g. ค่ารับรอง. VAT on purchase lines posting here is excluded from
        // the claimable ภ.พ.30 input total.
        var nonClaimableAccountIds = (await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.InputVatClaimable)
            .Select(a => a.Id)
            .ToListAsync())
            .ToHashSet();

        // §82/5(6) — รถยนต์นั่ง ≤ 10 ที่นั่ง + ค่าน้ำมัน/ซ่อม/เช่าซื้อ เคลม
        // ภาษีซื้อไม่ได้ (ยกเว้นผู้ประกอบกิจการขายรถ/ให้เช่ารถ). โหลด flag
        // IsVehicleDealer ครั้งเดียว → ถ้าไม่ใช่ vehicle dealer ระบบจะตรวจ
        // keyword รถ/น้ำมัน บน line description แล้ว mark VAT ต้องห้ามอัตโนมัติ
        // (เสริม account-level flag — กันเคสที่ผู้ใช้ไม่ได้ตั้งบัญชี nonClaimable).
        var isVehicleDealer = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => (bool?)c.IsVehicleDealer)
            .FirstOrDefaultAsync() ?? false;

        // CN/DN cross-period side resolution. Previously the CreditNote /
        // DebitNote loop looked up its RelatedDocumentId ONLY in the current
        // period's docs — a CN issued THIS month for a purchase invoice
        // booked LAST month would silently default to "sales side", causing
        // input VAT to be UN-reduced (over-claimed) on ภ.พ.30. Fix: pre-
        // resolve the type of every related doc across periods in one query,
        // keyed by id, so the loop below can route correctly.
        var relatedDocIds = docs
            .Where(d => (d.DocumentType == DocumentType.CreditNote || d.DocumentType == DocumentType.DebitNote
                         // Receipt/RV ต้องรู้ type ต้นทางด้วย (audit F4-sales): แยก
                         // "settlement ของ Invoice/TaxInvoice" (ห้ามนับซ้ำ) กับ
                         // "ขายเงินสดที่แปลงจาก QT/BN" (ต้องนับ — VAT ลง GL แล้ว)
                         || d.DocumentType == DocumentType.Receipt
                         || d.DocumentType == DocumentType.ReceiptVoucher)
                        && d.RelatedDocumentId.HasValue)
            .Select(d => d.RelatedDocumentId!.Value)
            .Distinct()
            .ToList();
        var relatedDocInfos = relatedDocIds.Count == 0
            ? new Dictionary<Guid, (DocumentType Type, DateTime? OutputVatDueAt)>()
            : (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && relatedDocIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentType, d.OutputVatDueAt })
                .ToListAsync())
                .ToDictionary(x => x.Id, x => (Type: x.DocumentType, x.OutputVatDueAt));
        var relatedDocTypes = relatedDocInfos.ToDictionary(kv => kv.Key, kv => kv.Value.Type);

        // F5 — ใบแจ้งหนี้ (Invoice) ที่มี VAT: AutoPostToJournalAsync ลง Cr 21911
        // (ภาษีขาย) ให้ทั้ง Invoice และ TaxInvoice เท่ากัน แต่ ภ.พ.30 เดิมรายงาน
        // เฉพาะ TaxInvoice → ใบแจ้งหนี้ที่มี VAT (charge VAT ⇒ ต้องออกใบกำกับ §86/4
        // + รายงาน §87) มีภาระภาษีขายใน GL แต่ไม่เคยถูกนำส่ง = นำส่งขาด → โดนปรับ.
        // แก้: นับใบแจ้งหนี้ที่มี VAT เข้า ภ.พ.30 ด้วย — ยกเว้นใบที่ถูก "แทนที่"
        // ด้วยใบกำกับภาษี (แปลง Invoice→TaxInvoice, ใบกำกับลูกรับ VAT ไปรายงานแล้ว)
        // มิฉะนั้นนับซ้ำ. (ใบแจ้งหนี้→ใบเสร็จ = settlement ไม่ใช่การแทนที่ tax point
        // → ใบแจ้งหนี้ยังเป็นเจ้าของ VAT, Receipt ลูกถูก exclude ที่ branch ด้านล่างแล้ว)
        var invoiceIds = docs
            .Where(d => d.DocumentType == DocumentType.Invoice && d.VatAmount != 0)
            .Select(d => d.Id)
            .ToList();
        var supersededInvoiceIds = invoiceIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Documents.AsNoTracking()
                .Where(t => t.CompanyId == companyId
                    && t.DocumentType == DocumentType.TaxInvoice
                    && t.RelatedDocumentId != null
                    && invoiceIds.Contains(t.RelatedDocumentId.Value)
                    && t.Status != DocumentStatus.Voided
                    && t.Status != DocumentStatus.Rejected
                    && !t.IsDeleted)
                .Select(t => t.RelatedDocumentId!.Value)
                .ToListAsync())
                .ToHashSet();

        // ใบแจ้งหนี้บริการล้วน: VAT พักที่ 21913 "ภาษีขายรอเรียกเก็บ" (§78/1 —
        // ใบแจ้งหนี้ไม่ใช่ใบกำกับ, tax point เกิดเมื่อรับชำระ) → ห้ามเข้า ภ.พ.30
        // จนกว่าจะ reclass (OutputVatDueAt). ตัดสิน GL-driven: อ่านขา 21913 จริง
        // ของ JE ใบนั้น — ใบเก่า/ใบมีสินค้า (ลง 21911 ตรง) net=0 → พฤติกรรมเดิม (F5)
        // invariant "ใบที่หัวมีคำใบกำกับภาษี = ใบที่อยู่ในรายงาน": ใบแจ้งหนี้ undue
        // ที่ถูก settle ด้วย "ใบเสร็จถือ VAT" (convert/modal รับครบ — กระดาษพิมพ์
        // "ใบกำกับภาษี/ใบเสร็จรับเงิน") → ใบเสร็จนั้นเป็นเจ้าของแถว ภ.พ.30 (เลขที่/
        // วันที่ตรงกระดาษใบกำกับจริง) และใบแจ้งหนี้ต้องไม่รายงานซ้ำ. ใบแจ้งหนี้ที่
        // settle ด้วย payment เปล่า (ไม่มีใบเสร็จถือ VAT) → fallback รายงานที่ INV
        // ผ่าน OutputVatDueAt ตามเดิม (VAT ห้ามหลุดจากรายงาน)
        var invoiceIdsOwnedByVatReceipt = invoiceIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Documents.AsNoTracking()
                .Where(r => r.CompanyId == companyId
                    && r.RelatedDocumentId != null
                    && invoiceIds.Contains(r.RelatedDocumentId.Value)
                    && (r.DocumentType == DocumentType.Receipt
                        || r.DocumentType == DocumentType.ReceiptVoucher)
                    && r.VatAmount > 0.005m
                    && r.Status != DocumentStatus.Voided
                    && r.Status != DocumentStatus.Draft
                    && r.Status != DocumentStatus.Rejected
                    && !r.IsDeleted)
                .Select(r => r.RelatedDocumentId!.Value)
                .ToListAsync())
                .ToHashSet();

        var invGl21913Net = invoiceIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.JournalEntryLines
                .Where(l => !l.IsDeleted
                    && l.JournalEntry.CompanyId == companyId
                    && l.JournalEntry.SourceDocumentId != null
                    && invoiceIds.Contains(l.JournalEntry.SourceDocumentId.Value)
                    && !l.JournalEntry.IsDeleted
                    && (l.JournalEntry.Status == JournalEntryStatus.Posted
                        || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                    && l.Account.AccountCode == "21913")
                .GroupBy(l => l.JournalEntry.SourceDocumentId!.Value)
                .Select(g => new { DocId = g.Key, Net = g.Sum(x => x.CreditAmount - x.DebitAmount) })
                .ToListAsync())
                .ToDictionary(x => x.DocId, x => x.Net);

        decimal outputVat = 0, inputVat = 0;
        decimal vatExemptAmount = 0;
        var lineOrder = 1;

        foreach (var doc in docs)
        {
            // Check for VAT exempt lines (VatRate == -1)
            var exemptLines = doc.Lines.Where(l => l.VatRate == -1).ToList();
            if (exemptLines.Any())
            {
                // นับยอดยกเว้นครั้งเดียวใน "เดือนที่ขาย" เท่านั้น — เอกสารที่ถูกดึง
                // เข้ามาด้วย OR-เงื่อนไข (InputVatBecameClaimableAt/OutputVatDueAt =
                // เดือนรับรู้/รับเงิน ซึ่งต่างจากเดือนขาย) จะโผล่ในรายงาน 2 งวด →
                // ถ้าไม่ guard ยอดยกเว้นถูกบวกซ้ำทั้งสองเดือน
                var exemptSaleDate = doc.TaxPointDate ?? doc.DocumentDate;
                if (exemptSaleDate >= startDate && exemptSaleDate <= endDate)
                    vatExemptAmount += exemptLines.Sum(l => l.Amount);
            }

            // Output VAT - from tax invoices (ใบกำกับภาษี) per Thai law ภ.พ.30.
            // + ใบแจ้งหนี้ (Invoice) ที่มี VAT และ "ไม่ถูกแทนที่ด้วยใบกำกับภาษี" (F5)
            //   — VAT ลง GL (Cr 21911) แล้วต้องรายงานเข้า ภ.พ.30 ให้ตรงกัน มิฉะนั้น
            //   นำส่งภาษีขายขาด. ใบที่ถูกแปลงเป็นใบกำกับภาษีแล้ว ใบกำกับลูกรายงานแทน.
            if (doc.DocumentType == DocumentType.TaxInvoice
                || (doc.DocumentType == DocumentType.Invoice
                    && doc.VatAmount > 0
                    && !supersededInvoiceIds.Contains(doc.Id)))
            {
                var invTxDate = doc.TaxPointDate ?? doc.DocumentDate;
                if (doc.DocumentType == DocumentType.Invoice)
                {
                    if (doc.OutputVatDueAt != null)
                    {
                        // มีใบเสร็จถือ VAT (กระดาษใบกำกับจริง) → ใบเสร็จรายงานแทน
                        // (branch ล่าง) — ใบแจ้งหนี้ห้ามรายงานซ้ำ
                        if (invoiceIdsOwnedByVatReceipt.Contains(doc.Id)) continue;
                        // ใบแจ้งหนี้บริการที่รับเงินแล้ว (reclass 21913→21911 แล้ว):
                        // tax point = วันรับเงิน (§78/1) → เข้า ภ.พ.30 งวดนั้นเท่านั้น
                        if (doc.OutputVatDueAt < startDate || doc.OutputVatDueAt > endDate) continue;
                        invTxDate = doc.OutputVatDueAt.Value;
                    }
                    else if (invGl21913Net.GetValueOrDefault(doc.Id) > 0.005m)
                    {
                        // VAT ยังพัก 21913 (ยังไม่รับเงิน/ยังไม่ออกใบกำกับ) —
                        // tax point ยังไม่เกิด → ห้ามเข้า ภ.พ.30 (ตามที่ผู้ใช้รายงาน:
                        // ใบแจ้งหนี้ค้างชำระต้องไม่ขึ้นรายงานภาษีขาย)
                        continue;
                    }
                    else if (invTxDate < startDate || invTxDate > endDate)
                    {
                        // legacy/ใบมีสินค้า (ลง 21911 ตรง) — งวดตาม tax point เดิม
                        continue;
                    }
                }
                outputVat += doc.VatAmount;
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = invTxDate,
                    Description = doc.DocumentNumber,
                    IncomeAmount = doc.SubTotal,
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id
                });
            }
            // Receipt / ReceiptVoucher ที่ออกเป็น "ใบกำกับภาษี" ของการขายเงินสด
            // (ค้าปลีก/บริการ ที่ออกใบเสร็จ-ใบกำกับภาษีอย่างย่อหรือเต็มรูปในใบ
            // เดียว) — tax point = วันรับเงิน (§78/§78/1) → output VAT เข้า ภ.พ.30
            // เดือนที่รับเงิน. นับเฉพาะใบเสร็จ STANDALONE (RelatedDocumentId ว่าง):
            // ใบเสร็จที่อ้าง Invoice/TaxInvoice เดิม → ใบกำกับต้นทางรับ VAT ไปแล้ว
            // ห้ามนับซ้ำ. ใบเสร็จมัดจำ (IsDeposit) ก็เข้าที่นี่ — VAT ถึงกำหนดทันที
            // แม้รายได้จะรอรับรู้ (Cr ขายรอรับรู้) ก็ตาม.
            else if ((doc.DocumentType == DocumentType.Receipt
                      || doc.DocumentType == DocumentType.ReceiptVoucher)
                     // นับ standalone + ใบที่แปลงจาก QT/BN (ขายเงินสด — JE ลง Cr 21911
                     // เองแล้ว ต้องเข้า ภ.พ.30; audit F4-sales เดิมถูก exclude เพราะมี
                     // RelatedDocumentId → VAT อยู่ใน GL แต่ไม่เคยถูกรายงาน = นำส่งขาด).
                     // + ใบเสร็จ "ถือ VAT" ที่ settle ใบแจ้งหนี้ undue (OutputVatDueAt
                     // ตั้งแล้ว) = ใบกำกับ ณ วันรับเงิน → เจ้าของแถว ภ.พ.30 (invariant:
                     // กระดาษที่หัวมีคำใบกำกับ = ใบที่อยู่ในรายงาน; INV ถูก skip ที่
                     // branch บนแล้ว). ใบเสร็จอ้าง TaxInvoice / ใบเสร็จเปล่า (VAT=0)
                     // ที่อ้าง Invoice = settlement เฉย ๆ → ใบกำกับ/INV ต้นทางรายงาน
                     // ไปแล้ว ห้ามนับซ้ำ (พฤติกรรมเดิม)
                     && (!doc.RelatedDocumentId.HasValue
                         || (relatedDocTypes.TryGetValue(doc.RelatedDocumentId.Value, out var rcptSrcType)
                             && (rcptSrcType == DocumentType.Quotation
                                 || rcptSrcType == DocumentType.BillingNote))
                         || (doc.VatAmount > 0.005m
                             && relatedDocInfos.TryGetValue(doc.RelatedDocumentId.Value, out var rcptSrcInfo)
                             && rcptSrcInfo.Type == DocumentType.Invoice
                             && rcptSrcInfo.OutputVatDueAt != null)))
            {
                // มัดจำเคส Deferred output VAT: tax point เกิดเมื่อ RecognizedAt.
                //   • ยังไม่ recognized → ข้าม (ยังไม่เข้า ภ.พ.30 — VAT อยู่ 21913)
                //   • recognized "แบบ standalone" (RealizeDeposit — ไม่มีใบกำกับ
                //     ปลายทาง) → เข้า ภ.พ.30 งวดที่ RecognizedAt
                //   • ถูก "นำไปหัก" ในใบกำกับ/ใบเสร็จปลายทาง (drives/apply,
                //     DepositAppliedToDocumentId ตั้ง) → ข้ามเสมอ: ใบปลายทางรายงาน
                //     VAT เต็มใบ (Cr 21911 287.85) แล้ว การเพิ่มแถวมัดจำ (101.40)
                //     = นับซ้ำ → ภ.พ.30 เกินจริง
                // deferred ตัดสินแบบ GL-first (flag หรือ ขา Cr 21913 จริงใน JE
                // ใบมัดจำ) — เคสเดียวกับ drives ที่ flag ไม่ได้ตั้งแต่ GL ลง 21913
                var effectivelyDeferred = doc.IsDeposit
                    && (doc.DepositOutputVatDeferred
                        || gl21913NetByDoc.GetValueOrDefault(doc.Id) > 0.005m);
                if (effectivelyDeferred)
                {
                    if (doc.DepositAppliedToDocumentId.HasValue) continue;
                    if (doc.DepositOutputVatRecognizedAt == null) continue;
                    var rec = doc.DepositOutputVatRecognizedAt.Value;
                    if (rec < startDate || rec > endDate) continue;
                }
                else if (doc.IsDeposit)
                {
                    // มัดจำ immediate (VAT ลง 21911 ตั้งแต่รับเงิน): tax point =
                    // วันรับเงิน ต้องอยู่ในงวดนี้ (กัน candidate ที่ merge เข้ามา
                    // จาก deferredRecognized แต่จริง ๆ ไม่ใช่ deferred)
                    var tp = doc.TaxPointDate ?? doc.DocumentDate;
                    if (tp < startDate || tp > endDate) continue;
                }
                var taxPoint = effectivelyDeferred
                    ? doc.DepositOutputVatRecognizedAt!.Value
                    : (doc.TaxPointDate ?? doc.DocumentDate);
                outputVat += doc.VatAmount;
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = taxPoint,
                    Description = doc.IsDeposit
                        ? $"[มัดจำ] {doc.DocumentNumber}"
                        : doc.DocumentNumber,
                    IncomeAmount = doc.SubTotal,
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id
                });
            }
            // CreditNote — reduces output/input VAT
            else if (doc.DocumentType == DocumentType.CreditNote)
            {
                var label = doc.Lines.Any(l => l.AccountId.HasValue) ? "[ใบลดหนี้-ภาษีซื้อ]" : "[ใบลดหนี้-ภาษีขาย]";
                // Cross-period lookup via relatedDocTypes — was searching THIS
                // PERIOD's docs only which mis-classified CN-on-prior-month-PI
                // as sales side (under-reducing input VAT on ภ.พ.30).
                var isPurchaseSide = doc.RelatedDocumentId.HasValue
                    && relatedDocTypes.TryGetValue(doc.RelatedDocumentId.Value, out var rtype)
                    && (rtype == DocumentType.PurchaseInvoice
                        || rtype == DocumentType.Expense
                        || rtype == DocumentType.CertificateInLieu);
                if (isPurchaseSide)
                {
                    inputVat -= doc.VatAmount;
                    label = "[ใบลดหนี้-ภาษีซื้อ]";
                }
                else
                {
                    outputVat -= doc.VatAmount;
                    label = "[ใบลดหนี้-ภาษีขาย]";
                }
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                    Description = $"{label} {doc.DocumentNumber}",
                    IncomeAmount = -doc.SubTotal,
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = -doc.VatAmount,
                    DocumentId = doc.Id,
                    // tag side ให้ LineSide จัด CN ฝั่งซื้อเข้า "รายงานภาษีซื้อ"
                    // ถูกต้อง (เดิม IncomeTypeCode = null → ตกไปฝั่งขายเสมอ)
                    IncomeTypeCode = isPurchaseSide ? "INPUT" : null
                });
            }
            // DebitNote — increases output/input VAT
            else if (doc.DocumentType == DocumentType.DebitNote)
            {
                // Same cross-period fix as CreditNote.
                var isPurchaseSide = doc.RelatedDocumentId.HasValue
                    && relatedDocTypes.TryGetValue(doc.RelatedDocumentId.Value, out var rtype)
                    && (rtype == DocumentType.PurchaseInvoice
                        || rtype == DocumentType.Expense
                        || rtype == DocumentType.CertificateInLieu);
                if (isPurchaseSide)
                {
                    inputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId, TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"[ใบเพิ่มหนี้-ภาษีซื้อ] {doc.DocumentNumber}",
                        IncomeAmount = doc.SubTotal, TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = doc.VatAmount, DocumentId = doc.Id, IncomeTypeCode = "INPUT"
                    });
                }
                else
                {
                    outputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId, TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"[ใบเพิ่มหนี้-ภาษีขาย] {doc.DocumentNumber}",
                        IncomeAmount = doc.SubTotal, TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = doc.VatAmount, DocumentId = doc.Id
                    });
                }
            }
            // Input VAT - from purchase documents (PurchaseOrder excluded: no VAT obligation)
            // ใบสำคัญจ่าย (PaymentVoucher) ที่ติ๊ก "ใช้งานใบกำกับภาษี"
            // (HasTaxInvoiceReference=true) = อ้างใบกำกับภาษีซื้อเพื่อเครดิต ภพ.30
            // → ต้องนับเป็นภาษีซื้อเหมือน PI/Expense. เดิม PV ไม่มี branch →
            // ภาษีซื้อจาก PV ตกหล่นทั้งหมด (ภพ.30 ภาษีซื้อ = 0 ทั้งที่มียอด).
            // JE ของ PV มี SourceDocumentId → JE-only fallback ข้ามอยู่แล้ว
            // (ไม่ double count). PV ที่ไม่ติ๊ก flag = จ่ายเฉย ๆ ไม่เคลม VAT
            // (§82/5(1) ไม่มีใบกำกับเต็มรูป) → ไม่นับ.
            // CIL ตัดออก (audit F7): ใบรับรองแทนใบเสร็จเคลมภาษีซื้อไม่ได้ (§82/4 ไม่มี
            // ใบกำกับเต็มรูป) — JE ก็ fold VAT เข้า expense อยู่แล้ว การนับที่นี่ =
            // เคลมเกินสิทธิ์ + นับซ้ำเมื่อ CIL ถูก convert มาจาก Expense ที่รายงานแล้ว
            else if (doc.DocumentType == DocumentType.PurchaseInvoice
                  || doc.DocumentType == DocumentType.Expense
                  || (doc.DocumentType == DocumentType.PaymentVoucher && doc.HasTaxInvoiceReference))
            {
                // ภาษีซื้อ "ยังไม่ถึงกำหนด" (audit F4): เอกสารที่ post ลง 11640
                // (ใบกำกับซื้อยังไม่ครบ §86/4) ห้ามเคลมใน ภ.พ.30 จนกว่าจะเติมใบ
                // ครบ (InputVatBecameClaimableAt) — เดิมรายงานเคลมทันทีทั้งที่ GL
                // ยังพักที่ 11640 = เคลมก่อนสิทธิ์ (สรรพากรประเมินคืนได้)
                if (doc.InputVatPostedAsUndue && doc.InputVatBecameClaimableAt == null)
                {
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId,
                        TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"[รอใบกำกับ §82/3] {doc.DocumentNumber} — ใบกำกับซื้อยังไม่ครบ ยังเคลมไม่ได้ (VAT พักที่ 11640)",
                        IncomeAmount = doc.SubTotal,
                        TaxRate = 7,
                        TaxAmount = doc.VatAmount,
                        DocumentId = doc.Id,
                        IsExcluded = true
                    });
                    continue;
                }
                // ภาษีซื้อที่ "ถึงกำหนดแล้ว" (BecameClaimableAt set — §83/6 รับรู้ ภ.พ.36
                // / §86/4 เติมใบกำกับครบ): เคลมใน "เดือนที่ถึงกำหนด" เท่านั้น. ใบนี้อาจ
                // ถูกโหลดทั้งจากงวด DocumentDate (เดือนใบ) และงวด BecameClaimableAt
                // (เดือนรับรู้) → นับเฉพาะงวดรับรู้ กันเคลมผิดเดือน + เบิ้ล 2 งวด
                if (doc.InputVatBecameClaimableAt.HasValue
                    && (doc.InputVatBecameClaimableAt.Value < startDate
                        || doc.InputVatBecameClaimableAt.Value > endDate))
                    continue;
                // ----- Rule B: detect prohibited input VAT (ภาษีซื้อต้องห้าม) -----
                // Prohibited VAT = ผลรวมจาก (a) บรรทัดที่ user/AI ติ๊ก
                // IsVatClaimable=false (explicit) + (b) บรรทัดที่ AccountId
                // (หรือ doc.ExpenseCategoryId) ชี้ไปบัญชี nonClaimable.
                // Auto-exclude: ลบออกจาก inputVat total + แยกแสดงเป็น line
                // ที่ IsExcluded=true เพื่อ audit trail. ผู้ใช้กา IsExcluded
                // เพิ่มเองได้ทีหลังถ้าเจอเคสที่ระบบไม่ detect.
                decimal lineVatTotal = doc.Lines.Sum(l => l.VatAmount);
                decimal prohibitedVat = 0m;
                if (Math.Abs(lineVatTotal) < 0.01m && doc.VatAmount != 0)
                {
                    // Doc-level fallback (legacy data ที่ไม่มี VAT บน line)
                    if (doc.ExpenseCategoryId.HasValue && nonClaimableAccountIds.Contains(doc.ExpenseCategoryId.Value))
                        prohibitedVat = doc.VatAmount;
                }
                else
                {
                    foreach (var l in doc.Lines)
                    {
                        // (a) explicit user/AI flag — เคารพเหนือทุก rule
                        if (!l.IsVatClaimable)
                        {
                            prohibitedVat += l.VatAmount;
                            continue;
                        }
                        // (b) account-level fallback
                        var acct = l.AccountId ?? doc.ExpenseCategoryId;
                        if (acct.HasValue && nonClaimableAccountIds.Contains(acct.Value))
                        {
                            prohibitedVat += l.VatAmount;
                            continue;
                        }
                        // (c) §82/5(6) keyword — รถยนต์นั่ง/น้ำมัน/ซ่อมรถ (เว้น vehicle dealer)
                        if (!isVehicleDealer && IsProhibitedVehicleExpense(l.Description))
                            prohibitedVat += l.VatAmount;
                    }
                }
                var claimableVat = doc.VatAmount - prohibitedVat;

                // ----- Rule A: tax-invoice 6-month age check (§82/3) -----
                // §82/3: ภาษีซื้อเคลมได้ภายใน 6 เดือนนับจากเดือนภาษีของใบกำกับ.
                // เกิน 6 เดือน = เคลม ภพ.30 ไม่ได้ตามกฎหมาย → ต้อง reclassify
                // เป็นค่าใช้จ่าย (ลง expense). เดิม code แค่ใส่ ⚠️ warning string
                // แต่ยังรวม claimableVat เข้า total → ผู้ใช้เคลมเกินสิทธิ์ →
                // สรรพากรประเมินคืน + เบี้ยปรับ. ตอนนี้: เกิน window → ตัดออก
                // จาก ภพ.30 total + แสดงเป็น excluded audit line แยก.
                var windowEnd = new DateTime(doc.DocumentDate.Year, doc.DocumentDate.Month, 1)
                    .AddMonths(7).AddDays(-1);
                var pastWindow = DateTime.UtcNow.Date > windowEnd;

                if (pastWindow)
                {
                    // เกิน 6 เดือน → ภาษีซื้อทั้งก้อนเคลมไม่ได้ (§82/3). ตัดออก
                    // จาก total, รวมส่วน prohibited เดิมด้วย, แสดง audit line เดียว.
                    var expiredVat = claimableVat + prohibitedVat;
                    if (expiredVat > 0)
                    {
                        report.Lines.Add(new TaxReportLine
                        {
                            TaxReportId = report.Id,
                            LineOrder = lineOrder++,
                            TaxPayerId = doc.Contact?.TaxId,
                            TaxPayerName = doc.Contact?.Name ?? "",
                            TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                            Description = $"🚫 [ภาษีซื้อเกิน 6 เดือน §82/3] {doc.DocumentNumber} — เคลม ภพ.30 ไม่ได้ ลงเป็นค่าใช้จ่ายแทน",
                            IncomeAmount = 0,
                            TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                            TaxAmount = expiredVat,
                            DocumentId = doc.Id,
                            IncomeTypeCode = "INPUT",
                            IsExcluded = true
                        });
                    }
                    continue;   // ไม่นับใบนี้เข้า input VAT total
                }

                // ภายใน window — Claimable เข้า inputVat total (หักส่วนต้องห้าม §82/5)
                inputVat += claimableVat;

                // เพิ่ม line ส่วนเคลมได้ (ถ้ามี) — IsExcluded=false → นับใน
                // ภพ.30 total. ถ้า doc ทั้งใบ prohibited (claimable=0) ก็
                // ข้าม line นี้ — แค่แสดง line "ต้องห้าม" ด้านล่างพอ.
                if (claimableVat > 0 || prohibitedVat == 0)
                {
                    var desc = $"[ภาษีซื้อ] {doc.DocumentNumber}";
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId,
                        TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = desc,
                        IncomeAmount = doc.SubTotal,
                        TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = claimableVat,
                        DocumentId = doc.Id,
                        IncomeTypeCode = "INPUT"
                    });
                }

                // ภาษีซื้อต้องห้าม §82/5 / ที่ผู้ใช้ไม่เคลม (IsVatClaimable=false →
                // VAT ถูกกลบเป็นค่าใช้จ่ายใน GL ไม่ลง 11610/11640) = "ไม่ใช่ภาษีซื้อ
                // ของ ภพ.30" ตามกฎหมาย จึง **ไม่บันทึกในรายงานภาษีซื้อเลย** (เดิม
                // ใส่เป็น audit line IsExcluded=true → ผู้ใช้เข้าใจผิดว่าถูกดึงมา
                // เคลม). audit ว่าตัดยอดไหนออก ดูได้จากตัวเอกสาร (line.IsVatClaimable
                // + ผังบัญชี) — รายงานสะท้อน GL: มีเฉพาะภาษีซื้อที่เคลมจริง.
            }

            // ===== §82/3 carry-forward: ภาษีซื้อที่ "เลือกไม่ใช้" เดือนก่อน =====
            // บรรทัด INPUT ที่นักบัญชีติ๊กออก (IsExcluded) ในรายงานเดือนก่อน ยังมี
            // สิทธิเคลมภายใน 6 เดือนนับจากเดือนภาษีของใบกำกับ — เดิมรายงานเดือน
            // ใหม่ดึงเฉพาะเอกสารเดือนตัวเอง → เครดิตที่เลื่อนไว้ "หายถาวร". ดึงมา
            // เป็นบรรทัดให้เลือกใช้ (default ติ๊กออก — ผู้ใช้เลือกเดือนที่ใช้เอง
            // ตอนติ๊กจะนับเข้า InputVat ผ่าน RecalcVatTotals ตอนบันทึก)
            try
            {
                var cfWindowStart = startDate.AddMonths(-6);
                var cfYear = startDate.Year; var cfMonth = startDate.Month;
                var priorExcluded = await _db.TaxReports.AsNoTracking()
                    .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.VAT && t.Id != report.Id
                        && (t.Year < cfYear || (t.Year == cfYear && t.Month < cfMonth)))
                    .SelectMany(t => t.Lines)
                    .Where(l => l.IncomeTypeCode == "INPUT" && l.IsExcluded
                        && l.DocumentId != null && l.TaxAmount > 0
                        && l.TransactionDate >= cfWindowStart && l.TransactionDate < startDate
                        && !l.Description!.StartsWith("🚫"))
                    .ToListAsync();
                if (priorExcluded.Count > 0)
                {
                    // dedup: ใบที่ "ถูกใช้แล้ว" (มี line INPUT IsExcluded=false ในรายงาน
                    // ใด ๆ) หรือมีบรรทัดสดในรายงานนี้อยู่แล้ว → ไม่ยกมา
                    var cfDocIds = priorExcluded.Select(l => l.DocumentId!.Value).Distinct().ToList();
                    var usedDocIds = (await _db.TaxReports.AsNoTracking()
                        .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.VAT)
                        .SelectMany(t => t.Lines)
                        .Where(l => l.IncomeTypeCode == "INPUT" && !l.IsExcluded
                            && l.DocumentId != null && cfDocIds.Contains(l.DocumentId.Value))
                        .Select(l => l.DocumentId!.Value).ToListAsync()).ToHashSet();
                    var freshDocIds = report.Lines.Where(l => l.DocumentId.HasValue)
                        .Select(l => l.DocumentId!.Value).ToHashSet();
                    // ใบที่ยัง "ไม่ถึงกำหนด" (11640 รอใบกำกับครบ) ไม่ยกมา — ยังเคลมไม่ได้
                    var stillUndue = (await _db.Documents.AsNoTracking()
                        .Where(d => cfDocIds.Contains(d.Id)
                            && ((d.InputVatPostedAsUndue && d.InputVatBecameClaimableAt == null)
                                || d.Status == DocumentStatus.Voided || d.IsDeleted))
                        .Select(d => d.Id).ToListAsync()).ToHashSet();

                    foreach (var grp in priorExcluded.GroupBy(l => l.DocumentId!.Value))
                    {
                        var docId = grp.Key;
                        if (usedDocIds.Contains(docId) || freshDocIds.Contains(docId)
                            || stillUndue.Contains(docId)) continue;
                        var src = grp.OrderByDescending(l => l.TransactionDate).First();
                        report.Lines.Add(new TaxReportLine
                        {
                            TaxReportId = report.Id,
                            LineOrder = lineOrder++,
                            TaxPayerId = src.TaxPayerId,
                            TaxPayerName = src.TaxPayerName,
                            TransactionDate = src.TransactionDate,
                            Description = $"[ยกมา §82/3 — ติ๊ก 'ใช้' เพื่อเคลมเดือนนี้] {src.Description}",
                            IncomeAmount = src.IncomeAmount,
                            TaxRate = src.TaxRate,
                            TaxAmount = src.TaxAmount,
                            DocumentId = docId,
                            IncomeTypeCode = "INPUT",
                            IsExcluded = true   // default ไม่ใช้ — ผู้ใช้ติ๊กเองแล้วบันทึก
                        });
                    }
                }
            }
            catch
            {
                // best-effort — carry-forward ล้มไม่กระทบรายงานหลัก (TaxService ไม่มี logger)
            }
        }

        // ===== Fallback: scan journal entries that have NO source document =====
        // Handles data imported via /integration/journals or /integration/daily-summary
        // which create JournalEntries without Documents.
        // Detection by BOTH account code AND name to support custom charts:
        //   - Output VAT: code starts with "2191" (ภาษีขาย) OR name contains "ภาษีขาย"
        //   - Input VAT: code starts with "116" or "114" or "115" OR name contains "ภาษีซื้อ"
        var endDateInclusive = endDate.Date.AddDays(1);
        var journalOnlyEntryIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= startDate && j.EntryDate < endDateInclusive
                && j.SourceDocumentId == null)
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any())
        {
            var vatLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    && (l.Account.AccountCode.StartsWith("2191")
                        || l.Account.AccountCode.StartsWith("116")
                        || l.Account.AccountCode.StartsWith("114")
                        || l.Account.AccountCode.StartsWith("115")
                        || l.Account.AccountName.Contains("ภาษีขาย")
                        || l.Account.AccountName.Contains("ภาษีซื้อ")))
                .ToListAsync();

            bool IsOutputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode.StartsWith("2191")
                || a.AccountName.Contains("ภาษีขาย");
            bool IsInputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode.StartsWith("116") || a.AccountCode.StartsWith("114") || a.AccountCode.StartsWith("115")
                || a.AccountName.Contains("ภาษีซื้อ");

            // Group by JournalEntry to aggregate VAT per entry
            var byEntry = vatLines.GroupBy(l => l.JournalEntryId);
            foreach (var grp in byEntry)
            {
                var je = grp.First().JournalEntry;
                var outputVatLines = grp.Where(l => IsOutputVat(l.Account)).ToList();
                var inputVatLines = grp.Where(l => IsInputVat(l.Account)).ToList();

                // Try to derive taxpayer info from the JE — manual VAT
                // adjustments don't have a SourceDocument/Contact link,
                // but the JE Description or Reference frequently names
                // the vendor + may embed a 13-digit tax id. Falls back
                // to the JE description verbatim so the column isn't
                // blank in the รายงานภาษีซื้อ/ขาย export.
                var (jePayerName, jePayerId) = ExtractTaxpayerFromJournalEntry(je);

                // Output VAT: credit balance on liability account = VAT on sales
                var outputVatAmt = outputVatLines.Sum(l => l.CreditAmount - l.DebitAmount);
                if (outputVatAmt > 0)
                {
                    var baseAmount = companyVatRate > 0
                        ? Math.Round(outputVatAmt / (companyVatRate / 100m), 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    outputVat += outputVatAmt;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TransactionDate = je.EntryDate,
                        // เลขที่ใบกำกับ = เลขเอกสารจริง (REC/TIV ที่ JE อ้างถึง) —
                        // แกะจาก Reference/Description; ไม่เจอ → เลขที่ JE. คำอธิบาย
                        // เต็มอยู่คอลัมน์ TaxPayerName แล้ว
                        Description = ExtractDocRefFromJe(je),
                        TaxPayerName = jePayerName,
                        TaxPayerId = jePayerId,
                        IncomeAmount = baseAmount,
                        TaxRate = companyVatRate,
                        TaxAmount = outputVatAmt,
                        IncomeTypeCode = "JE_OUTPUT"
                    });
                }

                // Input VAT: debit balance on 1140 = VAT on purchases
                var inputVatAmt = inputVatLines.Sum(l => l.DebitAmount - l.CreditAmount);
                if (inputVatAmt > 0)
                {
                    var baseAmount = companyVatRate > 0
                        ? Math.Round(inputVatAmt / (companyVatRate / 100m), 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    // §82/5(1)/§86/4: ภาษีซื้อจาก JE ล้วนที่ "ไม่มีเลขผู้เสียภาษีผู้ขาย
                    // 13 หลัก" เคลมไม่ได้ (ไม่ใช่ใบกำกับเต็มรูป) — เช่น JE "รับสินค้า
                    // เข้าสต๊อก" จาก integration (TakeTime) ที่โพสต์ภาษีซื้อแต่ไม่แนบ
                    // เลขภาษีผู้ขาย → mark excluded ไม่รวมในยอดเคลม (เก็บไว้ audit).
                    // ภาษีซื้อจริงต้องมาจากใบกำกับซื้อ/ใบสำคัญจ่ายที่มีเลขภาษีผู้ขายครบ
                    var jeInputTid = new string((jePayerId ?? "").Where(char.IsDigit).ToArray());
                    var jeInputClaimable = jeInputTid.Length == 13;
                    if (jeInputClaimable) inputVat += inputVatAmt;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TransactionDate = je.EntryDate,
                        Description = ExtractDocRefFromJe(je),   // เลขเอกสารจริงที่ JE อ้างถึง
                        TaxPayerName = jePayerName,
                        TaxPayerId = jePayerId,
                        IncomeAmount = baseAmount,
                        TaxRate = companyVatRate,
                        TaxAmount = inputVatAmt,
                        IncomeTypeCode = "JE_INPUT",
                        IsExcluded = !jeInputClaimable
                    });
                }
            }
        }

        // Add summary line for VAT exempt sales/purchases
        if (vatExemptAmount != 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "ยอดขาย/ซื้อยกเว้นภาษี",
                IncomeAmount = vatExemptAmount,
                TaxRate = 0,
                TaxAmount = 0,
                IncomeTypeCode = "EXEMPT"
            });
        }

        // VAT credit carryforward from previous month
        decimal vatCreditCarryforward = 0;
        var previousMonth = startDate.AddMonths(-1);
        var previousVatReport = await _db.TaxReports
            .Where(t => t.CompanyId == companyId
                && t.TaxType == TaxType.VAT
                && t.Year == previousMonth.Year
                && t.Month == previousMonth.Month
                && t.Status == TaxReportStatus.Filed)
            .FirstOrDefaultAsync();

        if (previousVatReport != null && previousVatReport.NetVat < 0)
        {
            // Previous month had excess input VAT = credit to carry forward
            vatCreditCarryforward = Math.Abs(previousVatReport.NetVat);
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = $"เครดิตภาษีซื้อยกมาจากเดือน {previousMonth.Month:D2}/{previousMonth.Year}",
                IncomeAmount = vatCreditCarryforward,
                TaxRate = 0,
                TaxAmount = -vatCreditCarryforward,
                IncomeTypeCode = "VAT_CREDIT_CF"
            });
        }

        report.OutputVat = outputVat;
        report.InputVat = inputVat + vatCreditCarryforward;
        report.NetVat = outputVat - inputVat - vatCreditCarryforward;
    }

    /// <summary>คำนวณรายงานภาษีมูลค่าเพิ่ม (ภ.พ.30) เป็น TaxReport ชั่วคราว
    /// (ไม่ persist) — ใช้ตรรกะตัวเดียวกับที่บันทึก/แสดงบนจอ 100% เพื่อให้
    /// "ไฟล์ที่ยื่น (CSV) == ที่ผู้ใช้เห็นบนจอ" (กันเคส screen ≠ filed).
    /// dedup กับ report อื่นที่ persist แล้ว, รวม CN/DN, หักภาษีซื้อต้องห้าม
    /// §82/5, มัดจำ deferred, JE ที่ไม่มี source doc — ครบเหมือน GenerateVatReport.</summary>
    public async Task<TaxReport> ComputeVatReportAsync(Guid companyId, int year, int month)
    {
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        // Id ใหม่ (ไม่ใช่ Guid.Empty) เพื่อให้ dedup `TaxReportId != report.Id`
        // กรองเฉพาะ report ที่ persist แล้ว — report ชั่วคราวนี้ไม่ถูกเก็บลง DB.
        var report = new TaxReport
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            TaxType = TaxType.VAT,
            Year = year,
            Month = month,
        };
        await GenerateVatReport(companyId, startDate, endDate, report);
        return report;
    }

    private async Task GenerateWhtReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        var docs = await _db.Documents
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดแถว ภ.ง.ด.3/53)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.WithholdingTaxAmount != 0)
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

        var lineOrder = 1;
        foreach (var doc in docs)
        {
            foreach (var line in doc.Lines.Where(l => l.WithholdingTaxAmount > 0))
            {
                // Determine WHT rate from income type or use line rate
                var whtRate = line.WithholdingTaxRate > 0
                    ? line.WithholdingTaxRate
                    : GetWhtRate(line.IncomeTypeCode);

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                    Description = line.Description,
                    IncomeAmount = line.Amount,
                    TaxRate = whtRate,
                    TaxAmount = line.WithholdingTaxAmount,
                    DocumentId = doc.Id,
                    IncomeTypeCode = line.IncomeTypeCode ?? "40(8)"
                });
            }
        }

        // Group by vendor (TaxPayerId) and add summary lines
        var vendorGroups = report.Lines
            .Where(l => !string.IsNullOrEmpty(l.TaxPayerId))
            .GroupBy(l => l.TaxPayerId)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in vendorGroups)
        {
            var vendorName = group.First().TaxPayerName;
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                TaxPayerId = group.Key,
                TaxPayerName = vendorName,
                Description = $"[สรุป] {vendorName}",
                IncomeAmount = group.Sum(l => l.IncomeAmount),
                TaxRate = 0,
                TaxAmount = group.Sum(l => l.TaxAmount),
                IncomeTypeCode = "SUMMARY"
            });
        }

        // ===== Fallback: scan journal entries that have NO source document =====
        var endDateInclusive = endDate.Date.AddDays(1);
        var journalOnlyEntryIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= startDate && j.EntryDate < endDateInclusive
                && j.SourceDocumentId == null)
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any())
        {
            // WHT accounts: 2191x (payable) or 11910 (receivable) or name contains "หัก ณ ที่จ่าย"
            var whtLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    && (l.Account.AccountCode.StartsWith("2191")
                        || l.Account.AccountCode.StartsWith("11910")
                        || l.Account.AccountName.Contains("หัก ณ ที่จ่าย")))
                .ToListAsync();

            var byEntry = whtLines.GroupBy(l => l.JournalEntryId);
            foreach (var grp in byEntry)
            {
                var je = grp.First().JournalEntry;
                var whtAmount = grp.Sum(l => l.CreditAmount - l.DebitAmount);
                if (whtAmount <= 0) continue;

                var allLinesInJe = await _db.JournalEntryLines
                    .Where(l => l.JournalEntryId == je.Id)
                    .SumAsync(l => l.DebitAmount);
                var expenseTotal = allLinesInJe - whtAmount;
                var estimatedRate = expenseTotal > 0
                    ? Math.Round(whtAmount / expenseTotal * 100, 2, MidpointRounding.AwayFromZero)
                    : 3m;
                var baseAmount = estimatedRate > 0
                    ? Math.Round(whtAmount / (estimatedRate / 100), 2, MidpointRounding.AwayFromZero)
                    : 0m;

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TransactionDate = je.EntryDate,
                    // เลขที่เอกสาร = เลขเอกสารจริงที่ JE อ้างถึง (แกะจาก ref/desc);
                    // ย้ายคำอธิบายไปคอลัมน์ชื่อผู้ถูกหัก (WHT JE line ไม่มี TaxPayerName)
                    Description = ExtractDocRefFromJe(je),
                    TaxPayerName = je.Description,
                    IncomeAmount = baseAmount,
                    TaxRate = estimatedRate,
                    TaxAmount = whtAmount,
                    IncomeTypeCode = "40(8)"
                });
            }
        }

        report.TotalIncome = report.Lines.Where(l => l.IncomeTypeCode != "SUMMARY").Sum(l => l.IncomeAmount);
        report.TotalTaxWithheld = report.Lines.Where(l => l.IncomeTypeCode != "SUMMARY").Sum(l => l.TaxAmount);
    }

    private async Task GenerateCitReport(Guid companyId, int year, TaxReport report)
    {
        // Honour Company.FiscalYearStartMonth — a Jul–Jun FY (start=7) for
        // fiscal year 2025 covers Jul 1 2025 → Jun 30 2026, not Jan–Dec.
        // Hard-coding Jan/Dec would make every non-calendar-FY filer report
        // the wrong period to RD (illegal under Thai Revenue Code §65).
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var startMonth = company?.FiscalYearStartMonth is >= 1 and <= 12 ? company.FiscalYearStartMonth : 1;
        var startDate = new DateTime(year, startMonth, 1);
        var endDate = startDate.AddYears(1).AddDays(-1);

        // Calculate total revenue
        var revenueLines = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.JournalEntry.EntryDate <= endDate
                && l.Account!.AccountType == AccountType.Revenue)
            .ToListAsync();

        var totalRevenue = revenueLines.Sum(l => l.CreditAmount - l.DebitAmount);

        // Calculate total expenses
        var expenseLines = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.JournalEntry.EntryDate <= endDate
                && l.Account!.AccountType == AccountType.Expense)
            .ToListAsync();

        var totalExpenses = expenseLines.Sum(l => l.DebitAmount - l.CreditAmount);

        // Query fixed assets for tax depreciation
        var activeAssets = await _db.Set<FixedAsset>()
            .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
            .Select(a => a.Id)
            .ToListAsync();

        var totalDepreciation = 0m;
        if (activeAssets.Any())
        {
            totalDepreciation = await _db.Set<AssetDepreciation>()
                .Where(d => activeAssets.Contains(d.FixedAssetId)
                    && d.Year == year)
                .SumAsync(d => d.Amount);
        }

        // Add depreciation to total expenses
        totalExpenses += totalDepreciation;

        // F11 — Entertainment expense cap §65 ทวิ (4) per ประมวลรัษฎากร:
        // ค่ารับรองหักได้ไม่เกิน MIN(0.3% ของรายได้, 0.3% ของทุนชำระแล้ว)
        // เพดานสูงสุด 10 ล้านบาท. ส่วนเกินถือเป็นรายจ่ายต้องห้าม (non-
        // deductible) — เพิ่มกลับเข้า net profit เพื่อคำนวณ CIT.
        // ตรวจหาบัญชีค่ารับรองโดย InputVatClaimable=false (seed มาเป็น
        // ค่ารับรอง) หรือ AccountName match "รับรอง".
        var entertainmentLines = expenseLines.Where(l => l.Account != null
            && (l.Account.InputVatClaimable == false
                || (l.Account.AccountName != null && l.Account.AccountName.Contains("รับรอง"))))
            .ToList();
        var entertainmentExpense = entertainmentLines.Sum(l => l.DebitAmount - l.CreditAmount);
        var paidUpCapital = company?.PaidUpCapital ?? 0m;
        var revenueLimit = totalRevenue * 0.003m;
        var capitalLimit = paidUpCapital * 0.003m;
        var entertainmentCap = Math.Min(10_000_000m, Math.Max(revenueLimit, capitalLimit));
        var entertainmentExcess = Math.Max(0, entertainmentExpense - entertainmentCap);

        // §65 ตรี — รายจ่ายต้องห้ามอื่น ๆ ที่ Section65TerValidator คำนวณตอน approve
        // (ค่าปรับ/เบี้ยปรับ, รายจ่ายส่วนตัว, ไม่มีผู้รับเงิน, capex ลงเป็น expense ฯลฯ)
        // เก็บใน Document.NonDeductibleAmount + breakdown ใน NonDeductibleRuleJson.
        // เดิม CIT บวกกลับเฉพาะค่ารับรอง (4) annual cap ด้านบน → ข้ออื่นหลุดหมด
        // ทำให้กำไรสุทธิทางภาษีต่ำเกินจริง (เสี่ยงประเมินเพิ่ม + เบี้ยปรับ).
        // ตัด RD-65ter(4) ออกเพราะคิด annual cap ไปแล้ว (กัน double count).
        var nonDeductDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.NonDeductibleAmount > 0)
            .Select(d => new { d.NonDeductibleAmount, d.NonDeductibleRuleJson })
            .ToListAsync();
        decimal section65TerAddBack = 0m;
        foreach (var nd in nonDeductDocs)
        {
            if (string.IsNullOrWhiteSpace(nd.NonDeductibleRuleJson))
            {
                section65TerAddBack += nd.NonDeductibleAmount;   // ไม่มี breakdown → บวกทั้งก้อน
                continue;
            }
            try
            {
                using var jdoc = System.Text.Json.JsonDocument.Parse(nd.NonDeductibleRuleJson);
                foreach (var el in jdoc.RootElement.EnumerateArray())
                {
                    var code = el.TryGetProperty("RuleCode", out var rc) ? rc.GetString() : null;
                    if (code == "RD-65ter(4)") continue;   // entertainment คิด annual cap แล้ว
                    if (el.TryGetProperty("AddBackAmount", out var ab) && ab.TryGetDecimal(out var amt))
                        section65TerAddBack += amt;
                }
            }
            catch { section65TerAddBack += nd.NonDeductibleAmount; }   // JSON เพี้ยน → fallback ทั้งก้อน
        }

        // Net profit before tax — เพิ่มส่วนเกิน entertainment + รายจ่ายต้องห้าม §65 ตรี
        // ที่หักไม่ได้ กลับเข้ามา (tax addition / รายการบวกกลับ).
        var netProfitBeforeTax = totalRevenue - totalExpenses + entertainmentExcess + section65TerAddBack;

        // Query previous year's CIT report for tax credit carryforward
        var previousYearCit = await _db.TaxReports
            .Where(t => t.CompanyId == companyId
                && t.TaxType == TaxType.CorporateIncomeTax
                && t.Year == year - 1
                && t.Status == TaxReportStatus.Filed)
            .FirstOrDefaultAsync();

        decimal taxCreditCarryforward = 0;
        if (previousYearCit != null && previousYearCit.NetVat < 0)
        {
            // NetVat is reused for net profit; negative means overpayment
            // TotalTaxWithheld has the CIT amount; if net profit was negative, there's a credit
            taxCreditCarryforward = Math.Abs(previousYearCit.TotalTaxWithheld);
        }

        // Thai CIT progressive rates (for SME companies)
        var citAmount = CalculateThaiCit(netProfitBeforeTax);

        // Apply tax credit carryforward
        var netCitAmount = Math.Max(0, citAmount - taxCreditCarryforward);

        report.TotalIncome = totalRevenue;
        report.TotalTaxWithheld = netCitAmount;
        report.NetVat = netProfitBeforeTax; // Reuse field for net profit

        var lineOrder = 1;
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "รายได้ทั้งปี",
            IncomeAmount = totalRevenue,
            TaxAmount = 0
        });
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "ค่าใช้จ่ายทั้งปี",
            IncomeAmount = totalExpenses - totalDepreciation,
            TaxAmount = 0
        });

        // Depreciation line
        if (totalDepreciation > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "ค่าเสื่อมราคาทางภาษี",
                IncomeAmount = totalDepreciation,
                TaxAmount = 0
            });
        }

        // F11 — แสดง entertainment cap §65 ทวิ (4) ที่ apply
        if (entertainmentExpense > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = $"ค่ารับรอง (เพดาน §65 ทวิ(4): {entertainmentCap:N2})",
                IncomeAmount = entertainmentExpense,
                TaxAmount = 0
            });
            if (entertainmentExcess > 0)
            {
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    Description = $"➕ บวกกลับ ค่ารับรองส่วนเกิน §65 ทวิ(4) — ไม่หักภาษีได้",
                    IncomeAmount = entertainmentExcess,
                    TaxAmount = 0
                });
            }
        }

        // §65 ตรี — บวกกลับรายจ่ายต้องห้ามอื่น (จาก validator ตอน approve)
        if (section65TerAddBack > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "➕ บวกกลับ รายจ่ายต้องห้าม §65 ตรี (ค่าปรับ/ส่วนตัว/ไม่มีผู้รับเงิน ฯลฯ)",
                IncomeAmount = section65TerAddBack,
                TaxAmount = 0
            });
        }

        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "กำไรสุทธิก่อนภาษี (หลังบวกกลับรายการต้องห้าม)",
            IncomeAmount = netProfitBeforeTax,
            TaxAmount = citAmount,
            TaxRate = netProfitBeforeTax > 0 ? (citAmount / netProfitBeforeTax) * 100 : 0
        });

        // Tax credit carryforward line
        if (taxCreditCarryforward > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "เครดิตภาษีจากปีก่อน",
                IncomeAmount = taxCreditCarryforward,
                TaxAmount = -taxCreditCarryforward,
                IncomeTypeCode = "TAX_CREDIT"
            });
        }
    }

    /// <summary>
    /// ภ.ง.ด.91 — สรุปภาษีเงินได้บุคคลธรรมดา (annual personal income tax)
    /// คำนวณจากข้อมูล Payroll ของพนักงานทุกคนในปีภาษี
    /// </summary>
    private async Task GeneratePnd91Report(Guid companyId, int year, TaxReport report)
    {
        // Get all payroll details for the year
        var payrollDetails = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId
                && d.PayrollRun.Year == year
                && (d.PayrollRun.Status == "Approved" || d.PayrollRun.Status == "Paid"))
            .ToListAsync();

        if (!payrollDetails.Any())
        {
            report.Notes = "ไม่พบข้อมูล Payroll สำหรับปีนี้";
            return;
        }

        var lineOrder = 1;
        decimal totalIncome = 0;
        decimal totalTax = 0;
        decimal totalSso = 0;

        // Group by employee to summarize annual income
        var employeeGroups = payrollDetails.GroupBy(d => d.EmployeeId);
        foreach (var group in employeeGroups)
        {
            var emp = group.First().Employee;
            var annualGross = group.Sum(d => d.GrossIncome);
            var annualTax = group.Sum(d => d.WithholdingTax);
            var annualSso = group.Sum(d => d.SocialSecurityEmployee);
            var annualProvident = group.Sum(d => d.ProvidentFundEmployee);

            // Calculate tax liability using progressive brackets
            // Deductions: SSO + Provident Fund (up to 500K each) + personal allowance 60K
            var personalAllowance = 60_000m;
            var ssoDeduction = Math.Min(annualSso, 9_000m); // SSO max deduction
            var providentDeduction = Math.Min(annualProvident, 500_000m);
            var totalDeductions = personalAllowance + ssoDeduction + providentDeduction;
            var taxableIncome = Math.Max(0, annualGross - totalDeductions);
            var computedTax = CalculateThaiPersonalIncomeTax(taxableIncome);

            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                TaxPayerId = emp.CitizenId,
                TaxPayerName = $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}",
                TransactionDate = new DateTime(year, 12, 31),
                Description = $"รหัส {emp.EmployeeCode} | เงินได้รวม {annualGross:N2} | หักค่าลดหย่อน {totalDeductions:N2}",
                IncomeAmount = annualGross,
                TaxRate = taxableIncome > 0 ? (computedTax / taxableIncome) * 100 : 0,
                TaxAmount = annualTax,
                IncomeTypeCode = "40(1)"
            });

            // Tax difference line if computed != withheld
            var taxDiff = computedTax - annualTax;
            if (Math.Abs(taxDiff) > 1) // threshold of 1 baht
            {
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = emp.CitizenId,
                    TaxPayerName = $"{emp.FirstNameTh} {emp.LastNameTh}",
                    TransactionDate = new DateTime(year, 12, 31),
                    Description = taxDiff > 0
                        ? $"ภาษีชำระเพิ่มเติม (ภาษีคำนวณ {computedTax:N2} - ภาษีหัก ณ ที่จ่าย {annualTax:N2})"
                        : $"ภาษีชำระเกิน (ภาษีคำนวณ {computedTax:N2} - ภาษีหัก ณ ที่จ่าย {annualTax:N2})",
                    IncomeAmount = 0,
                    TaxRate = 0,
                    TaxAmount = taxDiff,
                    IncomeTypeCode = taxDiff > 0 ? "TAX_ADDITIONAL" : "TAX_REFUND"
                });
            }

            totalIncome += annualGross;
            totalTax += annualTax;
            totalSso += annualSso;
        }

        report.TotalIncome = totalIncome;
        report.TotalTaxWithheld = totalTax;
        report.OutputVat = totalSso; // Reuse field for SSO total
        report.Notes = $"จำนวนพนักงาน: {employeeGroups.Count()} คน | ปีภาษี: {year}";
    }

    /// <summary>Pull taxpayer name + tax-id from a manual VAT journal
    /// entry that has no SourceDocument link. We try (in order):
    ///   1. A 13-digit number anywhere in JE Description / Reference /
    ///      Note / any line Description → use as TaxId
    ///   2. JE Description (trimmed of "VAT:", "[JE]" prefixes etc.)
    ///      → use as Name; fall back to JE Reference if blank
    /// Returns (name, taxId) where either may be empty/null — both
    /// columns in รายงานภาษีซื้อ/ขาย accept that, but the user sees
    /// at least the JE description text instead of a totally blank row.
    /// </summary>
    /// <summary>ดึง "เลขที่เอกสารจริง" (เช่น REC260601001 / TIV... ที่ JE อ้างถึง)
    /// จาก Reference/Description/ขา JE — สำหรับ JE ที่ไม่มี SourceDocument link
    /// (integration checkout / drives) เพื่อให้ช่อง "เลขที่ใบกำกับ" ในรายงานภาษี
    /// ขึ้นเลขเอกสารจริง ไม่ใช่เลข JE (JV-INT-...). จับ token ตัวอักษรพิมพ์ใหญ่
    /// 2-6 ตัว + ตัวเลข (คั่น -/ ได้) เช่น REC260601001, TIV-202606-0002; ไม่เจอ
    /// → คืนเลขที่ JE.</summary>
    private static string ExtractDocRefFromJe(JournalEntry je)
    {
        var texts = new[] { je.Reference ?? "", je.Description ?? "", je.Note ?? "" }
            .Concat(je.Lines?.Select(l => l.Description ?? "") ?? Array.Empty<string>());
        foreach (var t in texts)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"[A-Z]{2,6}[-/]?\d[\d/-]*\d");
            if (m.Success) return m.Value.Trim();
        }
        return je.EntryNumber;
    }

    private static (string Name, string? TaxId) ExtractTaxpayerFromJournalEntry(JournalEntry je)
    {
        var sources = new[] {
            je.Description ?? "",
            je.Reference ?? "",
            je.Note ?? ""
        }.Concat(je.Lines?.Select(l => l.Description ?? "") ?? Array.Empty<string>()).ToList();

        // Tax id — first 13-digit run found
        string? taxId = null;
        foreach (var s in sources)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"\b\d{13}\b");
            if (m.Success) { taxId = m.Value; break; }
        }

        // Name — strip common bookkeeping prefixes from the description
        var name = (je.Description ?? je.Reference ?? "").Trim();
        foreach (var prefix in new[] { "VAT:", "ภาษี:", "[JE]", "[VAT]", "ภาษีซื้อ-", "ภาษีขาย-", "บันทึกภาษี" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name[prefix.Length..].Trim();
        }
        // Drop the embedded tax-id from the name display when present
        if (taxId != null) name = name.Replace(taxId, "").Trim(' ', '|', '-', '·');
        if (string.IsNullOrWhiteSpace(name)) name = "(JE) " + je.EntryNumber;
        return (name, taxId);
    }

    /// <summary>Thai personal income tax (PIT) progressive rates</summary>
    private static decimal CalculateThaiPersonalIncomeTax(decimal taxableIncome)
    {
        if (taxableIncome <= 0) return 0;

        var brackets = new (decimal UpperBound, decimal Rate)[]
        {
            (150_000m, 0.00m),
            (300_000m, 0.05m),
            (500_000m, 0.10m),
            (750_000m, 0.15m),
            (1_000_000m, 0.20m),
            (2_000_000m, 0.25m),
            (5_000_000m, 0.30m),
            (decimal.MaxValue, 0.35m)
        };

        decimal tax = 0;
        decimal previousBound = 0;

        foreach (var (upperBound, rate) in brackets)
        {
            if (taxableIncome <= previousBound) break;

            var taxableInBracket = Math.Min(taxableIncome, upperBound) - previousBound;
            tax += taxableInBracket * rate;
            previousBound = upperBound;
        }

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Thai CIT progressive rates for SME (registered capital <= 5M, revenue <= 30M):
    /// Net profit 0 - 300,000: exempt
    /// Net profit 300,001 - 3,000,000: 15%
    /// Net profit > 3,000,000: 20%
    /// Non-SME: flat 20%
    /// </summary>
    private static decimal CalculateThaiCit(decimal netProfit)
    {
        if (netProfit <= 0) return 0;

        decimal tax = 0;

        // SME rates
        if (netProfit <= 300_000m)
        {
            tax = 0; // Exempt
        }
        else if (netProfit <= 3_000_000m)
        {
            tax = (netProfit - 300_000m) * 0.15m;
        }
        else
        {
            tax = (3_000_000m - 300_000m) * 0.15m  // 15% tier
                + (netProfit - 3_000_000m) * 0.20m; // 20% tier
        }

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal GetWhtRate(string? incomeTypeCode)
    {
        if (string.IsNullOrEmpty(incomeTypeCode))
            return WhtRateTable["default"];

        return WhtRateTable.TryGetValue(incomeTypeCode, out var rate)
            ? rate
            : WhtRateTable["default"];
    }

    public async Task<TaxReportResponse> GetTaxReportAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        var resp = MapToResponse(report);

        // ── เติม เลขที่ใบกำกับ + สาขา + วันที่ใบกำกับ ต่อบรรทัด (ฟอร์ม §87
        // ฉบับที่ 104) — ฝั่งซื้อใช้เลข/วันที่ "ใบกำกับของผู้ขาย", ฝั่งขายใช้เลข
        // เอกสารของเรา. สาขา = snapshot SupplierBranchCode ชนะ Contact.BranchCode.
        if (report.TaxType == TaxType.VAT)
        {
            var docIds = report.Lines.Where(l => l.DocumentId.HasValue)
                .Select(l => l.DocumentId!.Value).Distinct().ToList();
            if (docIds.Count > 0)
            {
                var docInfo = await (from d in _db.Documents.AsNoTracking()
                    join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id into cj
                    from c in cj.DefaultIfEmpty()
                    where d.CompanyId == companyId && docIds.Contains(d.Id)
                    select new
                    {
                        d.Id, d.DocumentNumber, d.SupplierInvoiceNumber,
                        Branch = d.SupplierBranchCode ?? (c != null ? c.BranchCode : null),
                        d.SupplierTaxInvoiceDate
                    }).ToDictionaryAsync(x => x.Id);

                var enriched = resp.Lines.Select(ln =>
                {
                    if (ln.DocumentId.HasValue && docInfo.TryGetValue(ln.DocumentId.Value, out var info))
                    {
                        var isInput = ln.IncomeTypeCode is "INPUT" or "JE_INPUT";
                        var invNo = isInput
                            ? (string.IsNullOrWhiteSpace(info.SupplierInvoiceNumber) ? info.DocumentNumber : info.SupplierInvoiceNumber)
                            : info.DocumentNumber;
                        var invDate = (isInput && info.SupplierTaxInvoiceDate.HasValue)
                            ? info.SupplierTaxInvoiceDate.Value : ln.TransactionDate;
                        return ln with { InvoiceNumber = invNo, BranchCode = info.Branch, TransactionDate = invDate };
                    }
                    return ln;
                }).ToList();
                resp = resp with { Lines = enriched };
            }
        }

        // ข้อมูลผู้ประกอบการสำหรับ header ฟอร์มราชการ
        var co = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode })
            .FirstOrDefaultAsync();
        if (co != null)
            resp = resp with { CompanyName = co.Name, CompanyTaxId = co.TaxId, CompanyBranchCode = co.BranchCode };

        return resp;
    }

    public async Task<List<TaxReportResponse>> GetTaxReportsAsync(Guid companyId, TaxType? taxType = null, int? year = null)
    {
        var query = _db.TaxReports
            .Include(r => r.Lines)
            .Where(r => r.CompanyId == companyId);

        if (taxType.HasValue) query = query.Where(r => r.TaxType == taxType.Value);
        if (year.HasValue) query = query.Where(r => r.Year == year.Value);

        var reports = await query.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToListAsync();
        return reports.Select(MapToResponse).ToList();
    }

    public async Task<TaxReportResponse> FileTaxReportAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("รายงานภาษีนี้ถูกยื่นแล้ว");

        if (report.TaxType != TaxType.CorporateIncomeTax && !report.Lines.Any())
            throw new InvalidOperationException("รายงานภาษีต้องมีรายการอย่างน้อย 1 รายการ");

        report.Status = TaxReportStatus.Filed;
        report.FiledDate = DateTime.UtcNow;
        // Mark the filing as locked simultaneously — see Task 4 of the
        // ERP upgrade. Once Filed, every linked document/JE is read-only
        // until an explicit UnlockTaxFilingAsync (admin operation).
        report.FilingLockedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(report);
    }

    public async Task<TaxReportResponse> UpdateTaxReportAsync(Guid companyId, Guid reportId, UpdateTaxReportRequest request)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถแก้ไขได้ — รายงานนี้ถูกยื่นแล้ว");

        if (request.Notes != null)
            report.Notes = request.Notes;

        if (request.Lines is { Count: > 0 })
        {
            foreach (var lineUpdate in request.Lines)
            {
                var line = report.Lines.FirstOrDefault(l => l.Id == lineUpdate.Id);
                if (line == null) continue;

                if (lineUpdate.IncomeAmount.HasValue) line.IncomeAmount = lineUpdate.IncomeAmount.Value;
                if (lineUpdate.TaxRate.HasValue) line.TaxRate = lineUpdate.TaxRate.Value;
                if (lineUpdate.TaxAmount.HasValue) line.TaxAmount = lineUpdate.TaxAmount.Value;
                if (lineUpdate.Description != null) line.Description = lineUpdate.Description;
                if (lineUpdate.Excluded.HasValue) line.IsExcluded = lineUpdate.Excluded.Value;
                line.UpdatedAt = DateTime.UtcNow;
            }

            // Totals recalc — lines the accountant excluded (IsExcluded) are
            // kept for audit but do NOT count toward the filed figures.
            if (report.TaxType == TaxType.VAT)
            {
                RecalcVatTotals(report);
            }
            else
            {
                var active = report.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode != "SUMMARY");
                report.TotalIncome = active.Sum(l => l.IncomeAmount);
                report.TotalTaxWithheld = active.Sum(l => l.TaxAmount);
            }
        }

        report.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToResponse(report);
    }

    /// <summary>Recompute OutputVat/InputVat/NetVat from the report's lines —
    /// excluded lines are kept for audit but dropped from the figures.</summary>
    private static void RecalcVatTotals(TaxReport report)
    {
        if (report.TaxType != TaxType.VAT) return;
        var active = report.Lines.Where(l => !l.IsExcluded).ToList();
        var nonSummary = active.Where(l => l.IncomeTypeCode != "VAT_CREDIT_CF" && l.IncomeTypeCode != "EXEMPT");
        // F11 — ภาษีซื้อจาก JE ล้วน (ไม่มี source doc) tag "JE_INPUT" ต้องนับเป็น
        // ภาษีซื้อ เหมือน "INPUT" — เดิม RecalcVatTotals เช็ค == "INPUT" อย่างเดียว
        // → JE_INPUT หลุดไปรวมใน OutputVat (!= "INPUT") + หายจาก InputVat = ภาษีขาย
        // เกินจริง + ภาษีซื้อขาด → NetVat ผิด (นำส่งเกิน). สอดคล้องกับ LineSide (บรรทัด
        // 1360) ที่ถือ "INPUT" or "JE_INPUT" เป็นฝั่งซื้ออยู่แล้ว.
        bool IsInputLine(TaxReportLine l) => l.IncomeTypeCode is "INPUT" or "JE_INPUT";
        report.OutputVat = nonSummary.Where(l => !IsInputLine(l)).Sum(l => l.TaxAmount);
        report.InputVat = nonSummary.Where(IsInputLine).Sum(l => l.TaxAmount);
        var creditCf = active.Where(l => l.IncomeTypeCode == "VAT_CREDIT_CF").Sum(l => Math.Abs(l.TaxAmount));
        report.NetVat = report.OutputVat - report.InputVat - creditCf;
    }

    // Document types eligible to be pulled into a VAT return, and whether
    // each posts to the input (ภาษีซื้อ) side.
    /// <summary>§82/5(6) — รถยนต์นั่ง ≤10 ที่นั่ง + ค่าน้ำมัน/ซ่อม/เช่าซื้อ
    /// เคลมภาษีซื้อไม่ได้ (ประกาศอธิบดีฯ ฉบับที่ 42). ตรวจ keyword บน
    /// description. คืน true = น่าจะเป็นรายจ่ายรถยนต์นั่งต้องห้าม. ระวัง
    /// false-positive (รถบรรทุก/รถตู้ >10 ที่นั่งเคลมได้) — keyword จับเฉพาะ
    /// ที่ชัดว่าเป็นรถนั่งส่วนบุคคล + น้ำมัน; user override ได้ด้วย IsVatClaimable.</summary>
    private static bool IsProhibitedVehicleExpense(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var d = description.ToLowerInvariant();
        // น้ำมันเชื้อเพลิง (รถนั่ง) — เบนซิน/ดีเซล/แก๊สโซฮอล์
        string[] fuel = { "น้ำมันเชื้อเพลิง", "ค่าน้ำมัน", "เบนซิน", "ดีเซล",
            "แก๊สโซฮอล", "gasohol", "diesel", "เติมน้ำมัน", "ค่าเชื้อเพลิง" };
        // รถยนต์นั่ง + บริการที่เกี่ยวข้อง
        string[] car = { "รถยนต์นั่ง", "รถเก๋ง", "ซ่อมรถ", "ค่าซ่อมรถยนต์",
            "เช่าซื้อรถ", "ค่าเช่ารถยนต์", "ประดับยนต์", "อะไหล่รถ", "ยางรถยนต์" };
        foreach (var k in fuel) if (d.Contains(k)) return true;
        foreach (var k in car) if (d.Contains(k)) return true;
        return false;
    }

    private static readonly Dictionary<DocumentType, bool> PullableVatTypes = new()
    {
        [DocumentType.TaxInvoice] = false,        // output
        [DocumentType.PurchaseInvoice] = true,    // input
        [DocumentType.Expense] = true,            // input
        [DocumentType.CertificateInLieu] = true,  // input
    };

    public async Task<List<PullableDocumentDto>> GetPullableDocumentsAsync(
        Guid companyId, Guid reportId, string? search, DateTime? fromDate, DateTime? toDate)
    {
        var report = await _db.TaxReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");
        if (report.TaxType != TaxType.VAT)
            throw new InvalidOperationException("ดึงเอกสารได้เฉพาะรายงาน ภพ.30");

        var from = fromDate ?? DateTime.UtcNow.AddMonths(-12);
        var to = (toDate ?? DateTime.UtcNow).Date.AddDays(1);
        var types = PullableVatTypes.Keys.ToList();

        // Documents already accounted for (a non-excluded line) in ANY VAT report.
        var claimed = (await _db.TaxReportLines
            .Where(l => l.DocumentId != null && !l.IsExcluded
                && l.TaxReport.CompanyId == companyId && l.TaxReport.TaxType == TaxType.VAT)
            .Select(l => l.DocumentId!.Value)
            .ToListAsync())
            .ToHashSet();

        // ไม่ Include Contact (required nav → INNER JOIN ตัดใบที่ contact ถูกลบ).
        // ค้นด้วยเลขเอกสารใน SQL; ค้นด้วยชื่อ contact ทำ client-side หลัง hydrate
        var s = search?.Trim();
        var q = _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && types.Contains(d.DocumentType)
                && d.VatAmount != 0
                && d.DocumentDate >= from && d.DocumentDate < to
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected);
        // ถ้าค้นด้วยเลขเอกสารตรง ๆ กรองใน SQL ให้ก่อน (เร็ว); ถ้าไม่มี match เลย
        // (อาจตั้งใจค้นชื่อ) จะ fallback ดึงกว้างแล้วกรองชื่อ client-side ด้านล่าง
        var byNumber = !string.IsNullOrWhiteSpace(s)
            ? await q.Where(d => d.DocumentNumber.Contains(s)).OrderByDescending(d => d.DocumentDate).Take(200).ToListAsync()
            : await q.OrderByDescending(d => d.DocumentDate).Take(200).ToListAsync();
        List<Document> docs = byNumber;
        if (!string.IsNullOrWhiteSpace(s) && byNumber.Count == 0)
            docs = await q.OrderByDescending(d => d.DocumentDate).Take(500).ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);
        return docs
            .Where(d => !claimed.Contains(d.Id))
            .Where(d => string.IsNullOrWhiteSpace(s)
                || d.DocumentNumber.Contains(s)
                || (d.Contact?.Name?.Contains(s) ?? false))
            .Take(200)
            .Select(d => new PullableDocumentDto(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate,
                d.Contact?.Name ?? "-", d.SubTotal, d.VatAmount,
                PullableVatTypes.TryGetValue(d.DocumentType, out var isIn) && isIn))
            .ToList();
    }

    public async Task<TaxReportResponse> PullDocumentIntoReportAsync(Guid companyId, Guid reportId, Guid documentId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");
        if (report.TaxType != TaxType.VAT)
            throw new InvalidOperationException("ดึงเอกสารได้เฉพาะรายงาน ภพ.30");
        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถแก้ไขได้ — รายงานนี้ถูกยื่นแล้ว");

        var doc = await _db.Documents
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ทำ doc = null)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await _db.HydrateContactAsync(companyId, doc);

        if (!PullableVatTypes.TryGetValue(doc.DocumentType, out var isInput))
            throw new InvalidOperationException($"เอกสารประเภท {doc.DocumentType} ไม่สามารถดึงเข้ารายงาน ภพ.30 ได้");
        if (doc.VatAmount == 0)
            throw new InvalidOperationException("เอกสารนี้ไม่มี VAT");
        if (report.Lines.Any(l => l.DocumentId == documentId))
            throw new InvalidOperationException("เอกสารนี้อยู่ในรายงานนี้แล้ว");

        // Block double-claiming — the document must not be an active line in
        // another VAT report.
        var other = await _db.TaxReportLines
            .Where(l => l.DocumentId == documentId && !l.IsExcluded
                && l.TaxReportId != reportId
                && l.TaxReport.CompanyId == companyId && l.TaxReport.TaxType == TaxType.VAT)
            .Select(l => new { l.TaxReport.Year, l.TaxReport.Month })
            .FirstOrDefaultAsync();
        if (other != null)
            throw new InvalidOperationException(
                $"เอกสารนี้ถูกใช้ในรายงานภาษีงวด {other.Month:D2}/{other.Year} แล้ว — กรุณานำออกจากงวดนั้นก่อน");

        var taxRate = doc.Lines.Any(l => l.VatRate > 0)
            ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0m;
        var nextOrder = report.Lines.Count == 0 ? 1 : report.Lines.Max(l => l.LineOrder) + 1;

        // 6-month claim-window check (advisory).
        var windowEnd = new DateTime(doc.DocumentDate.Year, doc.DocumentDate.Month, 1).AddMonths(7).AddDays(-1);
        var pastWindow = DateTime.UtcNow.Date > windowEnd;

        var sideLabel = isInput ? "ภาษีซื้อ" : "ภาษีขาย";
        var desc = $"[ดึงเข้างวด-{sideLabel}] {doc.DocumentNumber} (เอกสารงวด {doc.DocumentDate.Month:D2}/{doc.DocumentDate.Year})";
        if (pastWindow)
            desc = "⚠️ " + desc + " — ใบกำกับเกิน 6 เดือน อาจเครดิตภาษีซื้อไม่ได้";

        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = nextOrder,
            TaxPayerId = doc.Contact?.TaxId,
            TaxPayerName = doc.Contact?.Name ?? "",
            TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
            Description = desc,
            IncomeAmount = doc.SubTotal,
            TaxRate = taxRate,
            TaxAmount = doc.VatAmount,
            IncomeTypeCode = isInput ? "INPUT" : "OUTPUT",
            DocumentId = doc.Id
        });

        RecalcVatTotals(report);
        report.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToResponse(report);
    }

    public async Task<object> GetVatDebugAsync(Guid companyId, int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);

        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= start && d.DocumentDate < end
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.VatAmount, d.Status })
            .ToListAsync();

        var jeIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= start && j.EntryDate < end)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.SourceDocumentId })
            .ToListAsync();

        var vatLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate < end
                && l.Account != null
                && (l.Account.AccountCode.StartsWith("2191")
                    || l.Account.AccountCode.StartsWith("116")
                    || l.Account.AccountCode.StartsWith("114")
                    || l.Account.AccountCode.StartsWith("115")
                    || l.Account.AccountName.Contains("ภาษีขาย")
                    || l.Account.AccountName.Contains("ภาษีซื้อ")))
            .Select(l => new
            {
                AccountCode = l.Account.AccountCode,
                AccountName = l.Account.AccountName,
                EntryNumber = l.JournalEntry.EntryNumber,
                EntryDate = l.JournalEntry.EntryDate,
                Debit = l.DebitAmount,
                Credit = l.CreditAmount,
                HasSourceDoc = l.JournalEntry.SourceDocumentId != null
            })
            .ToListAsync();

        var byAccount = vatLines
            .GroupBy(l => new { l.AccountCode, l.AccountName })
            .Select(g => new
            {
                AccountCode = g.Key.AccountCode,
                AccountName = g.Key.AccountName,
                LineCount = g.Count(),
                TotalDebit = g.Sum(l => l.Debit),
                TotalCredit = g.Sum(l => l.Credit),
                Balance = g.Sum(l => l.Credit - l.Debit)
            })
            .OrderBy(x => x.AccountCode)
            .ToList();

        return new
        {
            Period = $"{year}-{month:D2}",
            DateRange = new { Start = start, End = end },
            DocumentsInPeriod = docs.Count,
            DocumentsWithVat = docs.Count(d => d.VatAmount != 0),
            SampleDocuments = docs.Take(5),
            JournalEntriesInPeriod = jeIds.Count,
            JournalEntriesFromApi = jeIds.Count(j => j.SourceDocumentId == null),
            JournalEntriesFromDocuments = jeIds.Count(j => j.SourceDocumentId != null),
            VatAccountsFound = byAccount.Count,
            VatAccountSummary = byAccount,
            VatLinesTotal = vatLines.Count,
            SampleVatLines = vatLines.Take(10)
        };
    }

    public async Task<TaxReportResponse> RegenerateTaxReportAsync(Guid companyId, Guid reportId)
    {
        var existing = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (existing.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถสร้างใหม่ได้ — รายงานนี้ถูกยื่นแล้ว");

        var request = new CreateTaxReportRequest(existing.TaxType, existing.Year, existing.Month);

        _db.TaxReportLines.RemoveRange(existing.Lines);
        _db.TaxReports.Remove(existing);
        await _db.SaveChangesAsync();

        return await GenerateTaxReportAsync(companyId, request);
    }

    public async Task DeleteTaxReportAsync(Guid companyId, Guid reportId)
    {
        var existing = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (existing.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถลบได้ — รายงานนี้ถูกยื่นแล้ว");

        _db.TaxReportLines.RemoveRange(existing.Lines);
        _db.TaxReports.Remove(existing);
        await _db.SaveChangesAsync();
    }

    public async Task<int> AutoRefreshReportsAsync(Guid companyId, int months = 2)
    {
        var now = DateTime.UtcNow;
        var taxTypes = new[] { TaxType.VAT, TaxType.WithholdingTax3, TaxType.WithholdingTax53 };
        int refreshed = 0;

        for (int i = 0; i < months; i++)
        {
            var target = now.AddMonths(-i);
            var year = target.Year;
            var month = target.Month;

            foreach (var taxType in taxTypes)
            {
                var existing = await _db.TaxReports
                    .Include(r => r.Lines)
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId
                        && r.TaxType == taxType && r.Year == year && r.Month == month);

                if (existing is { Status: TaxReportStatus.Filed })
                    continue;

                if (existing != null)
                {
                    _db.TaxReportLines.RemoveRange(existing.Lines);
                    _db.TaxReports.Remove(existing);
                    await _db.SaveChangesAsync();
                }

                try
                {
                    await GenerateTaxReportAsync(companyId, new CreateTaxReportRequest(taxType, year, month));
                    refreshed++;
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to regenerate tax report for {taxType} {year}/{month}: {ex.Message}"); }
            }
        }

        return refreshed;
    }

    private static TaxReportResponse MapToResponse(TaxReport r) => new(
        r.Id, r.TaxType, r.Year, r.Month, r.Status, r.FiledDate,
        r.OutputVat, r.InputVat, r.NetVat, r.TotalIncome, r.TotalTaxWithheld,
        r.Lines.OrderBy(l => l.LineOrder).Select(l => new TaxReportLineResponse(
            l.Id, l.LineOrder, l.TaxPayerId, l.TaxPayerName,
            l.TransactionDate, l.Description, l.IncomeAmount,
            l.TaxRate, l.TaxAmount, l.IncomeTypeCode, l.IsExcluded,
            l.DocumentId)).ToList(),
        r.Notes);
}
