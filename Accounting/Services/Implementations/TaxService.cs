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

        // B9: แบบรายปี (ภ.ง.ด.50 / ภ.ง.ด.91) unique ต่อ **ปี** — เดิม unique
        // ด้วย (ปี+เดือน) ⇒ สร้างได้ 12 ใบ/ปี แล้ว e-Filing หยิบ
        // FirstOrDefault(Year) ใบไหนก็ได้ (ยอดที่ยื่นไม่แน่นอน)
        var isAnnualForm = request.TaxType is TaxType.CorporateIncomeTax
            or TaxType.PersonalIncomeTax91;
        var dup = isAnnualForm
            ? await _db.TaxReports.AnyAsync(t => t.CompanyId == companyId
                && t.TaxType == request.TaxType && t.Year == request.Year)
            : await _db.TaxReports.AnyAsync(t => t.CompanyId == companyId
                && t.TaxType == request.TaxType
                && t.Year == request.Year && t.Month == request.Month);
        if (dup)
            throw new InvalidOperationException(isAnnualForm
                ? $"รายงานปี {request.Year} มีอยู่แล้ว (แบบรายปีมีได้ปีละ 1 ฉบับ) — "
                  + "เปิดฉบับเดิมแล้วกด \"สร้างใหม่\" ถ้าต้องการคำนวณใหม่"
                : "รายงานภาษีเดือนนี้มีอยู่แล้ว");

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
                NormalizeReportLineOrder(report);   // §87 — deferral เพิ่มบรรทัดหลัง generate
            }
            else if (request.TaxType == TaxType.WithholdingTax1
                  || request.TaxType == TaxType.SocialSecurity)
            {
                // ภ.ง.ด.1 + สปส. ใช้ข้อมูลเงินเดือน (PayrollRun) โดยตรง — ไม่มี
                // generator ฝั่ง TaxReport: เดิมสร้างได้แต่ได้ "รายงานเปล่า 0.00"
                // (GenerateWhtReport return ทันทีสำหรับ ภงด.1 / สปส. ไม่มี branch
                // เลย) แล้วปุ่ม e-Filing ยิงไฟล์ header ยอด 0 ออกไปโดยไม่เตือน —
                // อันตรายกว่าการ block ตรง ๆ มาก
                throw new InvalidOperationException(
                    request.TaxType == TaxType.WithholdingTax1
                        ? "ภ.ง.ด.1 สร้างจากข้อมูลเงินเดือนโดยตรง — ไปที่เมนู \"ส่งออกไฟล์ยื่นภาษี (e-Filing)\" เลือก ภ.ง.ด.1 (ไม่ต้องสร้างรายงานที่หน้านี้)"
                        : "ประกันสังคม (สปส.1-10) สร้างจากข้อมูลเงินเดือนโดยตรง — ไปที่เมนู \"ส่งออกไฟล์ยื่นภาษี (e-Filing)\" เลือก สปส.1-10 (ไม่ต้องสร้างรายงานที่หน้านี้)");
            }
            else if (request.TaxType == TaxType.WithholdingTax3
                  || request.TaxType == TaxType.WithholdingTax53
                  || request.TaxType == TaxType.WithholdingTax54)
            {
                // PND.54 reuses the WHT report generator — the difference is
                // in the form's per-line IncomeTypeCode and the e-Filing export
                // layout (BuildPndAsync handles 54 separately).
                await GenerateWhtReport(companyId, startDate, endDate, report);
            }
            else if (request.TaxType == TaxType.VatPp36)
            {
                // ภ.พ.36 — VAT ประเมินเองแทนผู้ขายต่างประเทศ (§83/6). ห้าม reuse
                // GenerateVatReport (ภ.พ.30): (1) มันดึงเอกสาร VAT ในประเทศทั้งหมด
                // ไม่ใช่เฉพาะ IsForeignService (2) dedup ข้ามรายงานเทียบกับ ภ.พ.30
                // — งวดที่ออก ภ.พ.30 ก่อน เอกสารถูกมองว่า "เคลมแล้ว" ทั้งชุด ⇒
                // ภ.พ.36 ว่าง/ยอด 0 ตลอด (บั๊กที่ผู้ใช้เจอ)
                await GeneratePp36Report(companyId, startDate, endDate, report);
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

        // field วันที่รับรู้ (BecameClaimableAt/OutputVatDueAt/RecognizedAt) เป็น
        // timestamp UTC มีเวลา — endDate คือ "วันสุดท้าย 00:00" ⇒ รายการที่รับรู้
        // วันสุดท้ายของเดือนหลังเที่ยงคืน > endDate ของงวดนี้ และ < startDate
        // งวดหน้า = หายจากทุกงวดเงียบ ๆ. เทียบแบบ exclusive กับวันถัดไปแทน
        // (pattern เดียวกับ endDateInclusive ของ JE fallback ด้านล่าง)
        var endStampExclusive = endDate.Date.AddDays(1);

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
                        && d.InputVatBecameClaimableAt >= startDate && d.InputVatBecameClaimableAt < endStampExclusive)
                    // ฝั่งขาย mirror: ใบแจ้งหนี้บริการที่ VAT "ถึงกำหนด" เดือนนี้
                    // (รับเงินเดือนนี้) แม้วันที่ใบอยู่เดือนก่อน — ต้องเข้ารายงานเดือนนี้
                    || (d.OutputVatDueAt != null
                        && d.OutputVatDueAt >= startDate && d.OutputVatDueAt < endStampExclusive)
                ))
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

        // Cross-report dedup: a document already claimed (a non-excluded line)
        // in ANOTHER VAT report must not be claimed again here. This both
        // prevents accidental double-claiming and lets the "pull document"
        // feature move an invoice into a different period safely.
        // ⚠️ ต้องตัดรายงาน "งวดเดียวกัน" ออกจาก dedup ด้วย — ComputeVatReportAsync
        // สร้าง report ชั่วคราว (Id ใหม่) เพื่อ export/e-Filing: ถ้าเทียบแค่ Id
        // รายงานงวดเดียวกันที่ผู้ใช้บันทึกไว้แล้วจะทำให้เอกสารทั้งงวดถูกมองว่า
        // "เคลมที่อื่นแล้ว" → ไฟล์ยื่น RD แทบว่าง (ภาษีขายนำส่งขาดทั้งงวด)
        var claimedElsewhere = (await _db.TaxReportLines
            .Where(l => l.DocumentId != null && !l.IsExcluded
                && l.TaxReportId != report.Id
                && l.TaxReport.CompanyId == companyId
                && l.TaxReport.TaxType == TaxType.VAT
                && !(l.TaxReport.Year == report.Year && l.TaxReport.Month == report.Month))
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
                && d.DepositOutputVatRecognizedAt >= startDate && d.DepositOutputVatRecognizedAt < endStampExclusive
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
        // B13: `IgnoreQueryFilters` — ใบต้นทางที่ถูก soft-delete ไปแล้วยังต้อง
        // "รู้ชนิด" ได้ ไม่งั้น global filter (!IsDeleted) ตัดแถวทิ้ง ⇒ ใบเสร็จที่
        // อ้างใบนั้นตกเงื่อนไขทั้งหมด → **output VAT หายจากรายงานเงียบ ๆ** ทั้งที่
        // JE ลง Cr 21911 ไปแล้ว (นำส่งขาด). เรากรอง CompanyId เองอยู่แล้วจึงยัง
        // ปลอดภัยเรื่อง tenant isolation
        var relatedDocInfos = relatedDocIds.Count == 0
            ? new Dictionary<Guid, (DocumentType Type, DateTime? OutputVatDueAt, bool InputVatPendingUndue)>()
            : (await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.CompanyId == companyId && relatedDocIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentType, d.OutputVatDueAt,
                    Pending = d.InputVatPostedAsUndue && d.InputVatBecameClaimableAt == null })
                .ToListAsync())
                .ToDictionary(x => x.Id, x => (Type: x.DocumentType, x.OutputVatDueAt,
                    InputVatPendingUndue: x.Pending));
        var relatedDocTypes = relatedDocInfos.ToDictionary(kv => kv.Key, kv => kv.Value.Type);

        // ===== ฝั่งของ CN/DN จาก GL (ความจริงสุดท้าย) =====
        //
        // เดิมตัดสินฝั่งจาก RelatedDocumentId อย่างเดียว → ใบลดหนี้ที่ **ไม่มี
        // ใบอ้างอิงผูกไว้** (อ้างด้วยข้อความ/สร้างตรง/นำเข้า API) จะตกไปฝั่งขาย
        // เสมอ ผลคือ:
        //   • ไม่ไปหักภาษีซื้อในรายงานภาษีซื้อ (อาการที่ผู้ใช้เจอ)
        //   • **และหักภาษีขายแทน** → นำส่งภาษีขายขาดไป = เบี้ยปรับ/เงินเพิ่ม
        // ซึ่งอันตรายกว่าอาการที่เห็น เพราะดูเผิน ๆ เหมือนแค่ "ไม่ขึ้นรายงาน"
        //
        // JE ตอนอนุมัติลงบัญชีภาษีไปแล้วตามฝั่งจริง (ฝั่งซื้อ Cr 116x /
        // ฝั่งขาย Dr 2191x — ดู DocumentService AutoPost) จึงใช้ GL เป็น
        // ตัวตัดสินที่เชื่อถือได้ที่สุดเมื่อ FK หาย
        var cnDnIds = docs
            .Where(d => d.DocumentType == DocumentType.CreditNote || d.DocumentType == DocumentType.DebitNote)
            .Select(d => d.Id).ToList();
        // docId → true = ฝั่งซื้อ (แตะผังภาษีซื้อ 116x), false = ฝั่งขาย (2191x)
        var cnDnSideFromGl = new Dictionary<Guid, bool>();
        if (cnDnIds.Count > 0)
        {
            var vatLines = await (from l in _db.JournalEntryLines.AsNoTracking()
                                  join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                                  join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                                  where !l.IsDeleted && !j.IsDeleted
                                        && j.CompanyId == companyId
                                        && j.Status != JournalEntryStatus.Voided
                                        && j.SourceDocumentId != null
                                        && cnDnIds.Contains(j.SourceDocumentId!.Value)
                                        && (a.AccountCode.StartsWith("116") || a.AccountCode.StartsWith("2191"))
                                  select new { DocId = j.SourceDocumentId!.Value, a.AccountCode })
                                 .ToListAsync();
            foreach (var g in vatLines.GroupBy(x => x.DocId))
            {
                var touchedInput = g.Any(x => x.AccountCode.StartsWith("116"));
                var touchedOutput = g.Any(x => x.AccountCode.StartsWith("2191"));
                // แตะทั้งสองฝั่ง = ผิดปกติ ไม่เดา ปล่อยให้ตรรกะเดิม/บรรทัดเตือนจัดการ
                if (touchedInput != touchedOutput) cnDnSideFromGl[g.Key] = touchedInput;
            }
        }
        // ใบแจ้งหนี้ (ต้นทาง CN/DN) ที่ VAT ยังพัก 21913 — CN/DN ต้องไม่หัก/เพิ่ม
        // ยอด ภ.พ.30 จนกว่าใบเดิมจะถึง tax point (GL-driven เหมือน branch Invoice)
        var relatedInvoicePendingIds = relatedDocInfos
            .Where(kv => kv.Value.Type == DocumentType.Invoice && kv.Value.OutputVatDueAt == null)
            .Select(kv => kv.Key).ToList();
        var relatedInv21913Net = relatedInvoicePendingIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.JournalEntryLines.AsNoTracking()
                .Where(l => !l.IsDeleted
                    && l.JournalEntry.CompanyId == companyId
                    && l.JournalEntry.SourceDocumentId != null
                    && relatedInvoicePendingIds.Contains(l.JournalEntry.SourceDocumentId.Value)
                    && !l.JournalEntry.IsDeleted
                    && l.JournalEntry.Status == JournalEntryStatus.Posted
                    && l.Account.AccountCode == "21913")
                .GroupBy(l => l.JournalEntry.SourceDocumentId!.Value)
                .Select(g => new { DocId = g.Key, Net = g.Sum(x => x.CreditAmount - x.DebitAmount) })
                .ToListAsync())
                .ToDictionary(x => x.DocId, x => x.Net);

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
                        if (doc.OutputVatDueAt < startDate || doc.OutputVatDueAt >= endStampExclusive) continue;
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
                    IncomeAmount = VatableBase(doc),
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
                    if (rec < startDate || rec >= endStampExclusive) continue;
                }
                else if (doc.IsDeposit)
                {
                    // มัดจำ immediate (VAT ลง 21911 ตั้งแต่รับเงิน): tax point =
                    // วันรับเงิน ต้องอยู่ในงวดนี้ (กัน candidate ที่ merge เข้ามา
                    // จาก deferredRecognized แต่จริง ๆ ไม่ใช่ deferred)
                    //
                    // ถูก "หักเข้าใบปลายทาง" แล้ว → ข้าม เหมือนเคส deferred:
                    // ApplyDepositToInvoice ลง JE กลับ Dr 21911 ("ล้าง VAT มัดจำ —
                    // รับรู้ที่ใบกำกับแล้ว") และใบปลายทาง Cr 21911 เต็มจำนวน
                    // ถ้ายังนับใบมัดจำอยู่ = ภ.พ.30 เกินจริงตามยอดมัดจำ และไม่ตรง
                    // กับความเคลื่อนไหวจริงของ 21911 ใน GL
                    // (งวดของใบมัดจำที่ "ยื่นไปแล้ว" ถูกกันไม่ให้ apply ตั้งแต่ต้นทาง
                    //  ใน DocumentService — ที่นี่จึงเหลือเฉพาะงวดที่ยัง regenerate ได้)
                    if (doc.DepositAppliedToDocumentId.HasValue) continue;
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
                    // ติดธง "ไม่ใช่ใบกำกับเต็มรูป" ให้เห็นในรายงาน — ใบพวกนี้เรา
                    // นำส่ง VAT ครบแต่ลูกค้าเคลมภาษีซื้อไม่ได้ (§82/5(1)) และเรา
                    // ยังมีหน้าที่ออกใบกำกับตาม §86 นักบัญชีจะได้เห็นทั้งงวดใน
                    // ที่เดียวว่ามีกี่ใบต้องตามแก้ แทนที่จะรู้ตอนลูกค้าโทรมาทวง
                    Description = (doc.IsDeposit ? $"[มัดจำ] {doc.DocumentNumber}" : doc.DocumentNumber)
                        + (NotFullTaxInvoice(doc) ? " [ไม่ใช่ใบกำกับเต็มรูป — ลูกค้าเคลมภาษีซื้อไม่ได้]" : ""),
                    IncomeAmount = VatableBase(doc),
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
                // PaymentVoucher = PV standalone จ่ายทันที (ตั้งหนี้+จ่ายในใบเดียว)
                // — CN ที่อ้างต้องลดภาษีซื้อ ไม่ใช่ภาษีขาย (คู่กับ AutoPost ฝั่งซื้อ)
                // ลำดับความน่าเชื่อถือ: FK ใบต้นทาง → GL (ผังภาษีที่ JE ลงจริง)
                // ไม่มีทั้งคู่ = คงพฤติกรรมเดิม (ฝั่งขาย) แต่ติดธงไว้เตือนด้านล่าง
                // ผู้ใช้สั่งย้ายฝั่งเอง = ชนะทุกชั้น (ตอนสั่งย้าย ระบบกลับ JE เดิม
                // แล้วลงใหม่ให้ตรงฝั่งด้วย รายงานกับ GL จึงยังตรงกันเสมอ)
                var sideForced = doc.CnDnPurchaseSideOverride;
                var sideResolvedByFk = doc.RelatedDocumentId.HasValue
                    && relatedDocTypes.ContainsKey(doc.RelatedDocumentId.Value);
                var isPurchaseSide = sideForced ?? (sideResolvedByFk
                    ? (relatedDocTypes[doc.RelatedDocumentId!.Value] is DocumentType.PurchaseInvoice
                        or DocumentType.Expense or DocumentType.CertificateInLieu
                        or DocumentType.PaymentVoucher)
                    : cnDnSideFromGl.GetValueOrDefault(doc.Id, false));
                var sideUnknown = !sideForced.HasValue && !sideResolvedByFk && !cnDnSideFromGl.ContainsKey(doc.Id);
                // ชั้นสุดท้าย (ตรงกับ AutoPost): คู่ค้าเป็น supplier อย่างเดียว
                // = ฝั่งซื้อแน่นอน — เราไม่ออกใบลดหนี้การขายให้คนที่ไม่เคยเป็นลูกค้า
                if (sideUnknown && doc.Contact is { IsSupplier: true, IsCustomer: false })
                {
                    isPurchaseSide = true;
                    sideUnknown = false;
                }
                // ใบเดิมยังไม่ถึง tax point (VAT พัก 21913/11640 — ยังไม่เคยเข้า
                // ภ.พ.30) → CN ห้ามหักยอดงวดนี้ (จะเป็นการขอคืน VAT ที่ไม่เคยนำส่ง/
                // ไม่เคยเคลม) — ใส่บรรทัดเตือน excluded คู่ไว้ ยอดสุทธิจะถูกนับตอน
                // ใบเดิมถึง tax point (reclass สุทธิหลัง CN ผ่าน GL)
                if (doc.RelatedDocumentId.HasValue
                    && relatedDocInfos.TryGetValue(doc.RelatedDocumentId.Value, out var cnRelInfo)
                    && (cnRelInfo.InputVatPendingUndue
                        || (cnRelInfo.Type == DocumentType.Invoice && cnRelInfo.OutputVatDueAt == null
                            && relatedInv21913Net.GetValueOrDefault(doc.RelatedDocumentId.Value) > 0.005m)))
                {
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId,
                        TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"⚠️ [ใบลดหนี้ — ใบเดิมยังไม่ถึง tax point (VAT พักอยู่)] {doc.DocumentNumber}",
                        IncomeAmount = -VatableBase(doc),
                        TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = -doc.VatAmount,
                        DocumentId = doc.Id,
                        IncomeTypeCode = isPurchaseSide ? "INPUT" : null,
                        IsExcluded = true
                    });
                    continue;
                }
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
                // แยกฝั่งไม่ได้เลย = ข้อมูลไม่ครบ ไม่ใช่เรื่องปกติ — ต้องให้ผู้ใช้
                // เห็นบนรายงาน ไม่ใช่เงียบแล้วไปหักผิดฝั่ง (หักภาษีขายเกินจริง =
                // นำส่งขาด เบี้ยปรับ §89)
                if (sideUnknown)
                    label = "⚠️ [ใบลดหนี้-แยกฝั่งไม่ได้ ตรวจสอบใบอ้างอิง]";
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                    Description = $"{label} {doc.DocumentNumber}",
                    IncomeAmount = -VatableBase(doc),
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
                // Same cross-period fix as CreditNote. (+ PV standalone ฝั่งซื้อ)
                // และ fallback GL แบบเดียวกัน — DN ฝั่งซื้อที่ไม่มี FK เดิมจะไป
                // **เพิ่มภาษีขาย** แทนที่จะเพิ่มภาษีซื้อ = นำส่งเกินจริง
                var dnSideByFk = doc.RelatedDocumentId.HasValue
                    && relatedDocTypes.ContainsKey(doc.RelatedDocumentId.Value);
                // ผู้ใช้สั่งย้ายฝั่งเอง = ชนะทุกชั้น (เหมือนฝั่ง CN)
                var dnSideForced = doc.CnDnPurchaseSideOverride;
                var isPurchaseSide = dnSideForced ?? (dnSideByFk
                    ? (relatedDocTypes[doc.RelatedDocumentId!.Value] is DocumentType.PurchaseInvoice
                        or DocumentType.Expense or DocumentType.CertificateInLieu
                        or DocumentType.PaymentVoucher)
                    : cnDnSideFromGl.GetValueOrDefault(doc.Id, false));
                var dnSideUnknown = !dnSideForced.HasValue && !dnSideByFk && !cnDnSideFromGl.ContainsKey(doc.Id);
                // ชั้นสุดท้าย (ตรงกับ AutoPost) — supplier-only = ฝั่งซื้อ
                if (dnSideUnknown && doc.Contact is { IsSupplier: true, IsCustomer: false })
                {
                    isPurchaseSide = true;
                    dnSideUnknown = false;
                }
                // ใบเดิมยังไม่ถึง tax point (VAT พัก 21913/11640) — เหมือน CN
                if (doc.RelatedDocumentId.HasValue
                    && relatedDocInfos.TryGetValue(doc.RelatedDocumentId.Value, out var dnRelInfo)
                    && (dnRelInfo.InputVatPendingUndue
                        || (dnRelInfo.Type == DocumentType.Invoice && dnRelInfo.OutputVatDueAt == null
                            && relatedInv21913Net.GetValueOrDefault(doc.RelatedDocumentId.Value) > 0.005m)))
                {
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId,
                        TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"⚠️ [ใบเพิ่มหนี้ — ใบเดิมยังไม่ถึง tax point (VAT พักอยู่)] {doc.DocumentNumber}",
                        IncomeAmount = VatableBase(doc),
                        TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = doc.VatAmount,
                        DocumentId = doc.Id,
                        IncomeTypeCode = isPurchaseSide ? "INPUT" : null,
                        IsExcluded = true
                    });
                    continue;
                }
                if (isPurchaseSide)
                {
                    inputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId, TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                        Description = $"[ใบเพิ่มหนี้-ภาษีซื้อ] {doc.DocumentNumber}"
                            + (dnSideUnknown ? " ⚠️ แยกฝั่งไม่ได้ ตรวจสอบใบอ้างอิง" : ""),
                        IncomeAmount = VatableBase(doc), TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
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
                        Description = $"[ใบเพิ่มหนี้-ภาษีขาย] {doc.DocumentNumber}"
                            + (dnSideUnknown ? " ⚠️ แยกฝั่งไม่ได้ ตรวจสอบใบอ้างอิง" : ""),
                        IncomeAmount = VatableBase(doc), TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
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
                  // PV แบบ settlement (RelatedDocumentId → PI/Expense) ห้ามเคลม:
                  // ภาษีซื้ออยู่ที่ใบตั้งหนี้แล้ว และ JE ของ PV settlement ไม่มี
                  // ขา 11610 เลย (Dr เจ้าหนี้/Cr เงิน) — เดิมติ๊ก flag ภายหลัง =
                  // ก้อนเดียวขึ้น 2 บรรทัด (PI + PV)
                  || (doc.DocumentType == DocumentType.PaymentVoucher
                      && doc.HasTaxInvoiceReference && doc.RelatedDocumentId == null))
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
                        IncomeAmount = VatableBase(doc),
                        TaxRate = 7,
                        TaxAmount = doc.VatAmount,
                        DocumentId = doc.Id,
                        // ⚠️ ต้องมี side code — ไม่มี = RecalcVatTotals นับเข้า
                        // OutputVat ถ้าผู้ใช้ฝืนติ๊ก + export ตกฝั่งขาย
                        IncomeTypeCode = "INPUT",
                        IsExcluded = true
                    });
                    continue;
                }
                // Override ผังภาษีซื้อ (เช่น 51000 = ลงต้นทุน): GL ไม่เคยแตะ 11610
                // → ห้ามนับเป็นภาษีซื้อเคลมได้ใน ภ.พ.30 (เคลมซ้ำกับที่ลงต้นทุน)
                if (!string.IsNullOrWhiteSpace(doc.InputVatAccountCodeOverride)
                    && !doc.InputVatAccountCodeOverride.StartsWith("116"))
                    continue;
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
                        // (c) §82/5(6) keyword-guess ถูกถอดออก — นโยบาย: "ตัด/เคลม
                        // เป็นดุลพินิจผู้กรอก" ระบบห้ามเดาจากข้อความไปตัดสิทธิ
                        // (เคยตัด "ค่าน้ำมัน" ทั้งที่เป็นรถกระบะที่เคลมได้). เหลือ
                        // เฉพาะสัญญาณที่ผู้ใช้ตั้งใจ: flag รายบรรทัด (a) + ผังบัญชี
                        // ต้องห้ามที่ตั้งเอง (b); ฝั่งเตือนมีตอนอนุมัติ/ตอนติ๊กแทน
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
                // เทียบกับ "งวดของรายงาน" ไม่ใช่วันที่กด regen — regen งวดเก่า
                // ของใบเอง (backfill) ต้องยังเคลมได้เสมอตามกฎหมาย; ยอดรายงานต้อง
                // deterministic (regen วันไหนก็ได้ผลเดิม). สูตรเดียวกับ
                // PullDocumentIntoReportAsync: งวดรายงาน >= เดือนใบ+7 = หมดสิทธิ์
                // B12: ฐานนับ 6 เดือน = **วันที่ใบกำกับของผู้ขาย** (ClaimBasisDate
                // ตัวเดียวกับปุ่ม "ดึงเอกสาร") — เดิมที่นี่ใช้ DocumentDate ⇒ ใบ
                // เดียวกันได้คำตอบ "หมดสิทธิ์/ไม่หมด" ต่างกันแล้วแต่ทางเข้า
                // (ใบกำกับผู้ขาย ม.ค. แต่เราบันทึก มิ.ย. → generate ปล่อยผ่าน
                //  แต่ pull ปฏิเสธ)
                var claimBasis = ClaimBasisDate(doc);
                var claimLimitStart = new DateTime(claimBasis.Year, claimBasis.Month, 1)
                    .AddMonths(7);
                var pastWindow = startDate >= claimLimitStart;

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
                        IncomeAmount = VatableBase(doc),
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

        }

        // ===== §82/3 carry-forward: ภาษีซื้อที่ "เลือกไม่ใช้" เดือนก่อน =====
        // บรรทัด INPUT ที่นักบัญชีติ๊กออก (IsExcluded) ในรายงานเดือนก่อน ยังมี
        // สิทธิเคลมภายใน 6 เดือนนับจากเดือนภาษีของใบกำกับ — ดึงมาเป็นบรรทัดให้
        // เลือกใช้ (default ติ๊กออก). ⚠️ บล็อกนี้ต้องอยู่ "นอก" ลูปเอกสาร —
        // เดิมซ้อนใน foreach: เดือนที่ไม่มีเอกสาร VAT เลยจะไม่รันเลย (เครดิตที่
        // เลื่อนไว้หายจากจอ) และเดือนปกติรันซ้ำทุกใบ = ยิง query ซ้ำ N รอบ
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
                    && !l.Description!.StartsWith("🚫") && !l.Description!.StartsWith("⚠️"))
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

        // ===== เอกสาร "มาช้า": tax point อยู่งวดก่อน แต่ยังไม่เคยอยู่ในรายงานใด =====
        // เคสจริง: ใบกำกับซื้อของ มิ.ย. เพิ่งเอามาบันทึกตอน ก.ค. ทั้งที่ยื่น ภ.พ.30
        // มิ.ย. ไปแล้ว → tax point = มิ.ย. → query หลักของงวด ก.ค. ไม่ดึง (นอกช่วง)
        // และงวด มิ.ย. ที่ Filed แล้ว regenerate ไม่ได้ → ภาษีซื้อ "หายทั้งก้อน"
        // แบบเงียบ ๆ. carry-forward ข้างบนช่วยเฉพาะใบที่ "เคยมีบรรทัดแล้วถูกติ๊กออก"
        // — ใบที่ไม่เคยอยู่ในรายงานไหนเลยตกหล่น. กวาดเก็บที่นี่:
        //   • ภาษีซื้อ (§82/3): เคลมงวดหลังได้ภายใน 6 เดือน → ใส่เป็นบรรทัด opt-in
        //     (IsExcluded=true) ให้นักบัญชีติ๊ก "ใช้" เอง — ไม่แตะยอดจนกว่าจะติ๊ก
        //   • ภาษีขาย: กฎหมายเลื่อนงวดไม่ได้ → ใส่บรรทัดเตือน (excluded) ให้รู้ว่า
        //     ต้องยื่น ภ.พ.30 "เพิ่มเติม" ของงวดนั้น ห้ามย้ายมาโปะงวดนี้
        // Gate กันเสียงรบกวน: ยกมาเฉพาะใบที่งวดของมัน "ยื่นแล้ว" หรือใบที่ถูก
        // บันทึกเข้าระบบหลังเดือนของ tax point จบไปแล้ว (มาช้าจริง) — งานบันทึก
        // ปกติที่ยังไม่ได้สร้างรายงานงวดก่อนจะไม่ถูกยกมากวน
        try
        {
            var lateWindowStart = startDate.AddMonths(-6);
            var anyLineDocIds = (await _db.TaxReportLines.AsNoTracking()
                .Where(l => l.DocumentId != null
                    && l.TaxReport.CompanyId == companyId && l.TaxReport.TaxType == TaxType.VAT)
                .Select(l => l.DocumentId!.Value).ToListAsync()).ToHashSet();
            var filedPeriods = (await _db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.VAT
                    && t.Status == TaxReportStatus.Filed)
                .Select(t => new { t.Year, t.Month }).ToListAsync())
                .Select(x => (x.Year, x.Month)).ToHashSet();

            var lateCandidates = await _db.Documents.AsNoTracking()
                .Include(d => d.Lines)
                .Where(d => d.CompanyId == companyId
                    && d.VatAmount != 0
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                    && d.Status != DocumentStatus.Rejected
                    && (d.TaxPointDate ?? d.DocumentDate) < startDate
                    && (d.TaxPointDate ?? d.DocumentDate) >= lateWindowStart
                    // ยังไม่ถึงกำหนดเคลม (ค้าง 11640 รอใบกำกับครบ) → ยังไม่ยกมา
                    && !(d.InputVatPostedAsUndue && d.InputVatBecameClaimableAt == null)
                    // override ผังภาษีซื้อนอก 116xx (VAT ลงต้นทุนแล้ว) → เคลมไม่ได้
                    && (d.InputVatAccountCodeOverride == null || d.InputVatAccountCodeOverride == ""
                        || d.InputVatAccountCodeOverride.StartsWith("116"))
                    // ใบที่ VAT "ถึงกำหนด" ในงวดนี้ ถูก query หลักดึงไปแล้ว
                    && (d.InputVatBecameClaimableAt == null
                        || d.InputVatBecameClaimableAt < startDate || d.InputVatBecameClaimableAt > endDate)
                    && (d.OutputVatDueAt == null
                        || d.OutputVatDueAt < startDate || d.OutputVatDueAt > endDate))
                .OrderBy(d => d.DocumentDate)
                .Take(300)
                .ToListAsync();
            await _db.HydrateContactsAsync(companyId, lateCandidates);

            var freshIds = report.Lines.Where(l => l.DocumentId.HasValue)
                .Select(l => l.DocumentId!.Value).ToHashSet();

            foreach (var d in lateCandidates)
            {
                if (anyLineDocIds.Contains(d.Id) || freshIds.Contains(d.Id)) continue;

                var tp = d.TaxPointDate ?? d.DocumentDate;
                var tpPeriodEnd = new DateTime(tp.Year, tp.Month, 1).AddMonths(1);
                var periodFiled = filedPeriods.Contains((tp.Year, tp.Month));
                var arrivedLate = d.CreatedAt >= tpPeriodEnd;
                if (!periodFiled && !arrivedLate) continue;   // งานบันทึกปกติ — ไม่ต้องยกมา

                // ฝั่งของเอกสาร — เดาไม่ได้ (CN/DN ขึ้นกับใบต้นทาง) ให้ข้าม
                bool? isInput = d.DocumentType switch
                {
                    // CIL ห้ามเคลม (§82/5(1) ไม่มีใบกำกับเต็มรูป — main loop ตัด
                    // แล้ว late-sweep ต้องตัดตาม ไม่งั้นเคลมได้เฉพาะทางอ้อม)
                    DocumentType.PurchaseInvoice or DocumentType.Expense => true,
                    DocumentType.CertificateInLieu => null,
                    DocumentType.PaymentVoucher =>
                        d.HasTaxInvoiceReference && d.RelatedDocumentId == null ? true : (bool?)null,
                    DocumentType.TaxInvoice or DocumentType.Invoice
                        or DocumentType.Receipt or DocumentType.ReceiptVoucher => false,
                    _ => null
                };
                if (isInput == null) continue;

                var tpLabel = $"{tp.Month:D2}/{tp.Year}";
                var taxRate = d.Lines.Any(l => l.VatRate > 0)
                    ? d.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0m;

                if (isInput == true)
                {
                    // เคลมได้เฉพาะส่วนที่ไม่ใช่ภาษีซื้อต้องห้าม (§82/5) — ไม่ยกยอดเต็ม
                    var claimable = d.Lines
                        .Where(l => l.IsVatClaimable
                            && (l.AccountId == null || !nonClaimableAccountIds.Contains(l.AccountId.Value)))
                        .Sum(l => l.VatAmount);
                    if (claimable <= 0) continue;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = d.Contact?.TaxId,
                        TaxPayerName = d.Contact?.Name ?? "",
                        TransactionDate = tp,
                        Description = $"[ใบกำกับซื้อมาช้า — งวด {tpLabel}"
                            + (periodFiled ? " ยื่นแล้ว" : "") + "] "
                            + $"{d.DocumentNumber} · ติ๊ก \"ใช้\" เพื่อเคลมเดือนนี้ (§82/3 ภายใน 6 เดือน)",
                        IncomeAmount = d.SubTotal,
                        TaxRate = taxRate,
                        TaxAmount = claimable,
                        DocumentId = d.Id,
                        IncomeTypeCode = "INPUT",
                        IsExcluded = true      // opt-in — ไม่กระทบยอดจนกว่านักบัญชีจะติ๊ก
                    });
                }
                else
                {
                    // ภาษีขายเลื่อนงวดไม่ได้ — บรรทัดนี้เป็น "ป้ายเตือน" ล้วน
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = d.Contact?.TaxId,
                        TaxPayerName = d.Contact?.Name ?? "",
                        TransactionDate = tp,
                        Description = periodFiled
                            ? $"⚠️ [ขายงวด {tpLabel} ยื่น ภ.พ.30 แล้ว] {d.DocumentNumber} — "
                              + "ภาษีขายเลื่อนมางวดนี้ไม่ได้ ต้องยื่น ภ.พ.30 \"เพิ่มเติม\" ของงวดนั้น"
                            : $"⚠️ [ขายงวด {tpLabel} ยังไม่อยู่ในรายงานงวดนั้น] {d.DocumentNumber} — "
                              + $"ให้สร้าง/สร้างรายงานงวด {tpLabel} ใหม่ก่อนยื่น",
                        IncomeAmount = d.SubTotal,
                        TaxRate = taxRate,
                        TaxAmount = d.VatAmount,
                        DocumentId = d.Id,
                        IncomeTypeCode = "OUTPUT",
                        IsExcluded = true      // เตือนอย่างเดียว ไม่แตะยอดงวดนี้
                    });
                }
            }
        }
        catch
        {
            // best-effort — การกวาดใบมาช้าล้มไม่กระทบรายงานหลัก
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
                && j.SourceDocumentId == null
                // JE ที่ถูกกลับรายการแล้ว (ReversedByEntryId) ต้องไม่นับ — เดิม
                // ใบต้นฉบับนับเต็ม ส่วนใบกลับรายการยอดติดลบถูก filter ทิ้ง
                // ⇒ นำส่งเกินสำหรับรายการที่ยกเลิกไปแล้ว. ใบ reversal เอง
                // (OriginalEntryId != null) ก็ข้าม — คู่ของมันไม่ถูกนับแล้ว
                && j.ReversedByEntryId == null
                && j.OriginalEntryId == null
                // JE "รับรู้ภาษีซื้อ ภ.พ.36" (Dr 11610/Cr 11640) ต้องไม่เข้า scan นี้
                // — ภาษีซื้อก้อนเดียวกันเข้ารายงานทางเอกสาร (BecameClaimableAt)
                // อยู่แล้ว ปล่อยไว้ = บรรทัด JV ซ้อนในรายงาน + เสี่ยงนับซ้ำ
                && (j.Reference == null || !j.Reference.StartsWith("ภ.พ.36R-")))
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any())
        {
            var vatLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    // pre-filter หยาบ (ตัวตัดสินจริงคือ IsOutputVat/IsInputVat ล่าง)
                    // — ตัด prefix 114/115 ออก: นั่นคือ "เงินให้กู้ยืม/สินค้าคงเหลือ"
                    // ไม่ใช่ VAT (เดิม JE รับสินค้าเข้าสต๊อกทั้งก้อนกลายเป็นภาษีซื้อ)
                    && (l.Account.AccountCode.StartsWith("2191")
                        || l.Account.AccountCode.StartsWith("116")
                        || l.Account.AccountName.Contains("ภาษีขาย")
                        || l.Account.AccountName.Contains("ภาษีซื้อ")))
                .ToListAsync();

            // ═══ matcher แบบ "แคบและถูก" — เดิม prefix กวาดผิดหมวดหลายตัว:
            //   2191x คลุม 21912 (ภ.พ.36 — คนละแบบ นำส่งซ้ำ), 21913 (VAT รอเรียก
            //   เก็บ — tax point ยังไม่เกิด), 21914/21915/21918 (WHT ภงด.1/2/54 —
            //   JV เงินเดือนจาก integration กลายเป็น "ยอดขายปลอม" = WHT÷7%)
            //   ฝั่งซื้อ: 11620 (ภ.พ.36 — ต้องรอใบเสร็จ RD), 11630 (พักรอเครดิต),
            //   และชื่อ "ภาษีซื้อ" บนผัง 5xxxx ("ภาษีซื้อขอคืนไม่ได้" = ค่าใช้จ่าย
            //   ที่ระบบเองเพิ่งย้ายออกจากรายงาน — ห้ามไหลกลับเข้าทาง JV)
            bool IsOutputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode == "21911"
                || (a.AccountName.Contains("ภาษีขาย")
                    && !a.AccountName.Contains("หัก ณ ที่จ่าย")
                    && !a.AccountName.Contains("รอเรียกเก็บ")
                    && !a.AccountName.Contains("ภ.พ. 36") && !a.AccountName.Contains("ภ.พ.36")
                    && !a.AccountName.Contains("รอนำส่ง"));
            bool IsInputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode == "11610"
                || (a.AccountName.Contains("ภาษีซื้อ")
                    && a.AccountCode.StartsWith("116")
                    && a.AccountCode != "11620" && a.AccountCode != "11630" && a.AccountCode != "11640"
                    && !a.AccountName.Contains("ยังไม่ถึงกำหนด")
                    && !a.AccountName.Contains("รอเครดิต")
                    && !a.AccountName.Contains("ภ.พ. 36") && !a.AccountName.Contains("ภ.พ.36"));

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

        // InputVat = ภาษีซื้อ "ของงวดนี้" ล้วน ๆ — เครดิตยกมาไม่ใช่ภาษีซื้อ แต่เป็น
        // ยอดหักจาก NetVat คนละช่องในแบบ (สูตรเดียวกับ RecalcVatTotals ไม่งั้นค่า
        // เปลี่ยนทันทีที่ผู้ใช้ติ๊กบรรทัดใด ๆ แล้ว auto-save → recalc; และไฟล์
        // e-Filing/CSV/PDF จะรายงานภาษีซื้อเกินจริงเท่ากับเครดิตยกมา)
        report.OutputVat = outputVat;
        report.InputVat = inputVat;
        report.NetVat = outputVat - inputVat - vatCreditCarryforward;

        // ── เตือน "ภาษีขายรอเรียกเก็บค้างนาน" ตอนเปิดรายงานเพื่อยื่น ──
        // วิธี B (มัดจำ VAT รอเรียกเก็บ) มีความเสี่ยงเฉพาะตัว: เราเก็บ VAT จาก
        // ลูกค้าไปแล้วแต่ค้างที่ 21913 ถ้าไม่มีใครกดรับรู้ (ลูกค้าเงียบ/งานยืด/
        // ลืม) เงินก้อนนั้นจะไม่ถูกนำส่งตลอดไป — สรรพากรตรวจเจอ = เรียกเก็บ VAT
        // ที่เก็บจากลูกค้าแล้วไม่นำส่ง + เบี้ยปรับ/เงินเพิ่ม
        // จังหวะที่เตือนได้ผลที่สุดคือ "ตอนเปิดรายงานเพื่อยื่น" ไม่ใช่ log เงียบ ๆ
        try
        {
            var agingCutoff = endDate.AddDays(-90);
            var stale = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.IsDeposit && d.DepositOutputVatDeferred
                    && d.DepositOutputVatRecognizedAt == null
                    && d.DepositAppliedToDocumentId == null
                    && d.VatAmount > 0.005m
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                    && (d.TaxPointDate ?? d.DocumentDate) <= agingCutoff)
                .Select(d => new { d.DocumentNumber, d.VatAmount, Dt = d.TaxPointDate ?? d.DocumentDate })
                .OrderBy(d => d.Dt).Take(20)
                .ToListAsync();
            if (stale.Count > 0)
            {
                var note = $"⚠️ มีเงินมัดจำที่ \"ภาษีขายรอเรียกเก็บ\" (21913) ค้างเกิน 90 วัน "
                    + $"{stale.Count} ใบ รวม VAT {stale.Sum(s => s.VatAmount):N2} บาท — "
                    + "ยังไม่เข้า ภ.พ.30 งวดใด ตรวจว่าจุดรับผิดเกิดแล้วหรือยัง (§78: ส่งมอบ/"
                    + "โอนกรรมสิทธิ์/รับชำระราคา/ออกใบกำกับ อย่างใดเกิดก่อน) ถ้าเกิดแล้วให้กด "
                    + "\"รับรู้ภาษีขาย\" ที่ใบมัดจำเพื่อนำส่งในงวดที่ถูกต้อง: "
                    + string.Join(", ", stale.Take(5).Select(s => $"{s.DocumentNumber} ({s.Dt:dd/MM/yy} {s.VatAmount:N2})"))
                    + (stale.Count > 5 ? $" และอีก {stale.Count - 5} ใบ" : "");
                report.Notes = string.IsNullOrWhiteSpace(report.Notes) ? note : report.Notes + "\n" + note;
            }
        }
        catch (Exception ex)
        {
            // เตือนไม่ได้ต้องไม่ทำให้สร้างรายงานไม่ได้
            System.Diagnostics.Trace.TraceWarning($"deferred-VAT deposit aging check failed: {ex.Message}");
        }

        // §87: รายงานภาษีซื้อ/ขายต้องลงตาม "ลำดับเวลา" — เรียง + renumber ท้ายสุด
        NormalizeReportLineOrder(report);
    }

    /// <summary>§87 (ป.รัษฎากร): รายงานภาษีขาย/ซื้อต้องลงรายการ "ตามลำดับ
    /// วันที่" ห้ามสลับ — เดิม LineOrder ไล่ตามลำดับที่ query คืนเอกสารมา
    /// (ไม่มี OrderBy) → หน้าจอ/Excel/CSV แสดงวันที่สลับไปมา ผิดรูปแบบรายงาน
    /// ตามกฎหมาย. เรียงเป็นกลุ่ม: ภาษีขาย → ภาษีซื้อ → บรรทัดสรุป (ยกเว้นภาษี/
    /// เครดิตยกมา/สรุป) แต่ละกลุ่มเรียงตามวันที่ (ties = ลำดับเดิม เพื่อความ
    /// เสถียร regenerate ได้ผลเดิม). ใช้กับ ภ.พ.30 + ภ.ง.ด.3/53 (CIT ข้าม —
    /// บรรทัดเป็นชุดสรุปที่ลำดับมีความหมายเอง).</summary>
    internal static void NormalizeReportLineOrder(TaxReport report)
    {
        if (report.Lines == null || report.Lines.Count == 0) return;
        if (report.TaxType == TaxType.CorporateIncomeTax) return;

        static int Rank(TaxReportLine l) => l.IncomeTypeCode switch
        {
            "EXEMPT" or "VAT_CREDIT_CF" or "SUMMARY" or "TAX_CREDIT" => 2,  // ท้ายสุด
            "INPUT" or "JE_INPUT" => 1,                                      // ภาษีซื้อ
            _ => 0,                                                          // ภาษีขาย/WHT
        };

        var ordered = report.Lines
            .Select((Line, Idx) => (Line, Idx))
            .OrderBy(x => Rank(x.Line))
            // บรรทัดสรุปไม่มีวันที่จริง → คงลำดับเดิม (ไม่เอา MinValue ไปเรียง)
            .ThenBy(x => Rank(x.Line) == 2 ? 0L : x.Line.TransactionDate.Date.Ticks)
            .ThenBy(x => x.Idx)
            .Select(x => x.Line)
            .ToList();

        var n = 1;
        foreach (var l in ordered) l.LineOrder = n++;
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
        // Deferral ต้องเข้าไฟล์ยื่นด้วย (จอกับไฟล์ต้องตรงกัน) — โหมด preview:
        // markClaims=false ห้ามไป stamp ClaimedAt กับ report ชั่วคราวที่ไม่ persist
        await ApplyVatDeferralsAsync(companyId, year, month, report, markClaims: false);
        NormalizeReportLineOrder(report);   // §87 ลำดับเวลา (ไฟล์ยื่น = จอ)
        return report;
    }

    /// <summary>ภ.พ.36 แบบ transient (ไม่บันทึก) — ให้ e-Filing/preview ใช้ชุด
    /// เดียวกับรายงานบนจอ (pattern เดียวกับ ComputeVatReportAsync ของ ภ.พ.30)</summary>
    public async Task<TaxReport> ComputePp36ReportAsync(Guid companyId, int year, int month)
    {
        var report = new TaxReport
        {
            CompanyId = companyId, TaxType = TaxType.VatPp36,
            Year = year, Month = month, Status = TaxReportStatus.Draft,
        };
        var start = new DateTime(year, month, 1);
        await GeneratePp36Report(companyId, start, start.AddMonths(1).AddDays(-1), report);
        return report;
    }

    /// <summary>ภ.พ.36 — นำส่ง VAT แทนผู้ขายต่างประเทศ (§83/6 reverse charge).
    /// เอกสารซื้อที่ IsForeignService=true (JE ตอนอนุมัติ Cr 21912 เจ้าหนี้
    /// ภ.พ.36 แล้ว): ฐาน = ค่าบริการ, ยอดนำส่ง = VAT ประเมินเอง. นำส่ง+ได้
    /// ใบเสร็จ RD แล้วจึงเคลมเป็นภาษีซื้อ ภ.พ.30 (11620/RecognizePp36) —
    /// จึง**ไม่ dedup กับ ภ.พ.30** (คนละแบบ คนละหน้าที่).</summary>
    private async Task GeneratePp36Report(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        var purchaseSide = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };
        var docs = await _db.Documents
            .Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId
                && d.IsForeignService && d.VatAmount > 0
                && purchaseSide.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && (d.TaxPointDate ?? d.DocumentDate) >= startDate
                && (d.TaxPointDate ?? d.DocumentDate) <= endDate)
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

        // กันนำส่งซ้ำ: เอกสารที่อยู่ในรายงาน ภ.พ.36 งวดอื่นแล้ว (pattern เดียวกับ
        // ภ.พ.30 แต่เทียบเฉพาะ TaxType.VatPp36 ด้วยกันเอง)
        var remittedElsewhere = (await _db.TaxReportLines
            .Where(l => l.DocumentId != null && !l.IsExcluded
                && l.TaxReportId != report.Id
                && l.TaxReport.CompanyId == companyId
                && l.TaxReport.TaxType == TaxType.VatPp36
                && !(l.TaxReport.Year == report.Year && l.TaxReport.Month == report.Month))
            .Select(l => l.DocumentId!.Value)
            .ToListAsync())
            .ToHashSet();
        if (remittedElsewhere.Count > 0)
            docs = docs.Where(d => !remittedElsewhere.Contains(d.Id)).ToList();

        var lineOrder = 1;
        foreach (var doc in docs.OrderBy(d => d.TaxPointDate ?? d.DocumentDate))
        {
            // ฐานค่าบริการ: บรรทัด (หักบรรทัดยกเว้น VatRate=-1) — เอกสาร header-only
            // (Expense/PV/CIL ที่ไม่มี DocumentLine) ใช้ SubTotal. ⚠️ เดิมเขียน
            // `doc.Lines?.Sum(...) ?? fallback` — Lines เป็น collection ที่ init
            // ไว้เสมอ (ไม่มีวัน null) ⇒ fallback เป็น dead code, ใบ header-only
            // ได้ฐาน 0 ทั้งที่มี VAT นำส่ง (จอ+ไฟล์ยื่นโชว์ฐาน 0.00)
            var baseAmount = doc.Lines is { Count: > 0 }
                ? doc.Lines.Where(l => l.VatRate != -1).Sum(l => l.Amount)
                : (doc.SubTotal > 0 ? doc.SubTotal : doc.TotalAmount - doc.VatAmount);
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                TaxPayerId = doc.Contact?.TaxId,
                TaxPayerName = doc.Contact?.Name ?? "(ผู้ขายต่างประเทศ)",
                TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                Description = $"{doc.DocumentNumber} — บริการจากต่างประเทศ (§83/6)",
                IncomeAmount = baseAmount,
                TaxRate = baseAmount > 0
                    ? Math.Round(doc.VatAmount / baseAmount * 100m, 2, MidpointRounding.AwayFromZero)
                    : 7m,
                TaxAmount = doc.VatAmount,
                DocumentId = doc.Id,
                IncomeTypeCode = "PP36",
            });
        }

        // ยอดแบบ: ภ.พ.36 คือ "นำส่ง VAT" (ไม่มีขาภาษีซื้อหักในแบบเดียวกัน —
        // สิทธิเคลมเกิดหลังนำส่งแล้วไปเข้า ภ.พ.30 งวดถัดไป)
        report.TotalIncome = report.Lines.Sum(l => l.IncomeAmount);
        report.OutputVat = report.Lines.Sum(l => l.TaxAmount);
        report.InputVat = 0;
        report.NetVat = report.OutputVat;
    }

    private async Task GenerateWhtReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        // ภ.ง.ด.1 = เงินเดือน ม.40(1) จาก payroll เท่านั้น (ExportPnd1Async อ่าน
        // PayrollDetail) — ห้ามดึงจากเอกสารซื้อ: เดิม generator เดียวใช้ทุกแบบ
        // ทำให้ ภ.ง.ด.1 มีค่าบริการผู้ขายปนแทนเงินเดือน
        if (report.TaxType == TaxType.WithholdingTax1) return;

        // เฉพาะเอกสาร "ฝั่งซื้อ" ที่เราเป็นผู้หัก — ใบขาย (Invoice/Receipt/...)
        // ที่ลูกค้าหักเราไว้ (Dr 11910 เครดิตภาษีเรา) ห้ามเข้าแบบนำส่ง ไม่งั้น
        // นำส่งภาษีที่เราถูกหักซ้ำอีกรอบ (ตรงกับ filter ของ WithholdingTaxCertService)
        var purchaseSide = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };

        var docs = await _db.Documents
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดแถว ภ.ง.ด.3/53)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && purchaseSide.Contains(d.DocumentType)
                && d.WithholdingTaxAmount != 0)
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

        // แยกผู้ถูกหักตามแบบ (ท.ป.4/2528): ภ.ง.ด.3 = บุคคลธรรมดาไทย,
        // ภ.ง.ด.53 = นิติบุคคลไทย, ภ.ง.ด.54 = ผู้รับต่างประเทศ (ม.70) —
        // เดิมไม่กรองเลย ทำให้ 3 กับ 53 ของงวดเดียวกันเป็นรายงานฝาแฝด
        // ยอดนำส่งรวมเป็น 2 เท่าของที่หักจริง
        bool PayeeInScope(Contact? c, bool docIsForeignService = false)
        {
            // สัญญาณ "ต่างประเทศ" มี 2 ทาง: CountryCode ของ contact (มักไม่ได้กรอก)
            // และธง IsForeignService บนเอกสาร (ผู้ใช้ติ๊กเองตอนสร้าง PV — เชื่อถือได้
            // กว่า). เดิมดู CountryCode อย่างเดียว: Booking.com ที่ไม่ได้กรอกประเทศ
            // หลุดจาก ภงด.54 แล้วไปโผล่ ภงด.53 = ยื่นผิดแบบทั้งสองทาง
            var foreign = docIsForeignService
                || (c != null && !string.IsNullOrWhiteSpace(c.CountryCode)
                    && !string.Equals(c.CountryCode, "TH", StringComparison.OrdinalIgnoreCase));

            // ม.70: ผู้รับเงินต่างประเทศอยู่ ภ.ง.ด.54 เท่านั้น
            if (report.TaxType == TaxType.WithholdingTax54) return foreign;
            if (foreign) return false;

            if (report.TaxType is not (TaxType.WithholdingTax3 or TaxType.WithholdingTax53))
                return true;

            // ใช้ resolver ตัวเดียวกับที่ออก 50 ทวิ (ResolveWhtFormType) — สำคัญ 2 อย่าง:
            // (1) รายงานกับหนังสือรับรองต้องลงแบบเดียวกันเสมอ ไม่งั้นยอดไม่ตรง
            // (2) DetectJuristic คืน null ได้เมื่อ "ไม่มีสัญญาณชัด" — ถ้ากรองด้วยค่านั้น
            //     ตรง ๆ ผู้รับกลุ่มนี้จะหายจากทั้ง ภ.ง.ด.3 และ 53 (ไม่ถูกนำส่งเลย)
            //     ส่วน resolver มี fallback DetermineTaxFormType ให้ลงแบบใดแบบหนึ่งเสมอ
            var (form, _, _) = WithholdingTaxCertService.ResolveWhtFormType(c, null);
            return report.TaxType == form;
        }
        docs = docs.Where(d => PayeeInScope(d.Contact, d.IsForeignService)).ToList();

        // ── กันนับซ้ำสาย "ตั้งหนี้ → ใบสำคัญจ่าย" — ทั้ง PI/Expense และ PV ที่
        // แปลง/ผูกกัน ถือ WithholdingTaxAmount บนเอกสารทั้งคู่ ⇒ เดิมเข้ารายงาน
        // ทั้งสองใบ = นำส่ง 2 เท่า (คนละเดือนถ้าจ่ายข้ามเดือน). แถวจริงตาม
        // WhtRecognitionBasis ของบริษัท:
        //   Cash (default): WHT เกิดตอนจ่าย → PV คือแถวจริง; ใบตั้งหนี้ที่มี PV
        //     active แล้วข้าม (ใบตั้งหนี้ที่จ่ายผ่าน "บันทึกชำระเงิน" ไม่มี PV →
        //     ยังแสดงจากใบตั้งหนี้ตามเดิม)
        //   Accrual: WHT เกิดตอนตั้งหนี้ → ใบตั้งหนี้คือแถวจริง; PV ที่ผูก
        //     ใบต้นทางข้าม (PV standalone ไม่มีต้นทาง ยังแสดง)
        // B3: **เดือนนำส่ง ภ.ง.ด. = เดือนที่จ่ายเงินเสมอ** (ท.ป.4/2528 — ภาระ
        // หักและนำส่งเกิดที่การจ่าย) ⇒ ใบสำคัญจ่ายคือแถวจริงเสมอ ไม่ขึ้นกับ
        // `WhtRecognitionBasis` ซึ่งเป็นการเลือก **ทางบัญชี** ว่าจะตั้งหนี้ WHT
        // ตอนไหนใน GL — คนละเรื่องกับเดือนที่ยื่นแบบ. เดิมสลับตาม basis:
        // Accrual → เก็บใบตั้งหนี้ไว้เดือน accrual ขณะที่ cert ของ PV อยู่เดือน
        // จ่าย ⇒ เงินก้อนเดียวโผล่ 2 เดือน. ใบตั้งหนี้ที่จ่ายผ่าน "บันทึกชำระ
        // เงิน" (ไม่มี PV) ยังแสดงจากตัวเอกสารเองตามเดิม
        var settledSourceIds = (await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId
                && x.DocumentType == DocumentType.PaymentVoucher
                && x.RelatedDocumentId != null && !x.IsDeleted
                && x.WithholdingTaxAmount != 0
                && x.Status != DocumentStatus.Draft && x.Status != DocumentStatus.Voided
                && x.Status != DocumentStatus.Rejected)
            .Select(x => x.RelatedDocumentId!.Value)
            .ToListAsync())
            .ToHashSet();
        docs = docs.Where(d => d.DocumentType == DocumentType.PaymentVoucher
            || !settledSourceIds.Contains(d.Id)).ToList();

        // ═════ แหล่งหลัก: หนังสือรับรอง 50 ทวิ ที่ออกแล้ว (ทะเบียน = ความจริง) ═════
        // ภงด.3/53 ยื่นตามที่ "หักจริง ณ เดือนจ่าย" = ตรงกับใบ 50 ทวิ ที่ออกให้ผู้ถูกหัก
        // เป๊ะ ๆ (เลขที่/เงินได้/ภาษี). เดิมรายงาน mine จากเอกสารเท่านั้น: เคสที่ WHT
        // เก็บระดับเอกสาร/งวดจ่าย แต่ **บรรทัดไม่มียอด WHT รายบรรทัด** — ใบผ่าน filter
        // ชั้นนอก (doc.WithholdingTaxAmount != 0) แต่ inner loop ไม่มีบรรทัดให้เพิ่ม →
        // รายงาน 0 ทั้งที่หน้า "หนังสือรับรอง" มีใบออกครบ (บั๊กที่ผู้ใช้เจอ: cert 150
        // บาท ก.ค. แต่ ภงด.53 ก.ค. = 0.00). ใช้ทะเบียน cert เป็นแหล่งแรก → สองหน้า
        // reconcile กันเสมอ
        var lineOrder = 1;
        if (report.TaxType is TaxType.WithholdingTax3 or TaxType.WithholdingTax53 or TaxType.WithholdingTax54)
        {
            // ห้าม Include(PayeeContact) — required nav + query filter !IsDeleted
            // → INNER JOIN ตัด cert ของ payee ที่ถูกลบออกจากจอ (ไฟล์ยื่น
            // ExportPnd3/53 ใช้ hydrate จึงยังส่ง = จอ ≠ ไฟล์) — hydrate แยกแทน
            var monthCerts = await _db.WithholdingTaxCerts
                .Include(c => c.Lines)
                .Where(c => c.CompanyId == companyId
                    && c.TaxFormType == report.TaxType
                    && c.TaxYear == report.Year && c.TaxMonth == report.Month
                    && (c.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Issued
                        || c.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Printed))
                .ToListAsync();
            await _db.HydratePayeeContactsAsync(companyId, monthCerts);
            foreach (var cert in monthCerts.OrderBy(c => c.CertificateNumber))
            {
                if (cert.Lines != null && cert.Lines.Count > 0)
                {
                    foreach (var cl in cert.Lines.OrderBy(l => l.LineOrder))
                        report.Lines.Add(new TaxReportLine
                        {
                            TaxReportId = report.Id, LineOrder = lineOrder++,
                            TaxPayerId = cert.PayeeContact?.TaxId,
                            TaxPayerName = cert.PayeeContact?.Name ?? "",
                            TransactionDate = cl.PaymentDate,
                            Description = $"{cert.CertificateNumber} — {cl.IncomeDescription}",
                            IncomeAmount = cl.IncomeAmount, TaxRate = cl.TaxRate,
                            TaxAmount = cl.TaxAmount, DocumentId = cert.DocumentId,
                            IncomeTypeCode = cl.IncomeTypeCode,
                        });
                }
                else
                {
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = cert.PayeeContact?.TaxId,
                        TaxPayerName = cert.PayeeContact?.Name ?? "",
                        TransactionDate = cert.IssuedDate ?? new DateTime(cert.TaxYear, cert.TaxMonth, 1),
                        Description = cert.CertificateNumber,
                        IncomeAmount = cert.TotalIncomeAmount,
                        TaxRate = cert.TotalIncomeAmount > 0
                            ? Math.Round(cert.TotalTaxAmount / cert.TotalIncomeAmount * 100m, 2, MidpointRounding.AwayFromZero)
                            : 0m,
                        TaxAmount = cert.TotalTaxAmount, DocumentId = cert.DocumentId,
                        IncomeTypeCode = "40(8)",
                    });
                }
            }

            // cert "ร่าง" ของงวด — โชว์เป็นบรรทัดติ๊กออก (IsExcluded) ให้เห็นว่า
            // มีใบค้างออก แต่ไม่นับเข้ายอดนำส่งจนกว่าจะกดออกใบจริงแล้ว regenerate
            // (สอดคล้อง e-Filing ที่ export เฉพาะ Issued/Printed)
            var draftCerts = await _db.WithholdingTaxCerts
                .Where(c => c.CompanyId == companyId
                    && c.TaxFormType == report.TaxType
                    && c.TaxYear == report.Year && c.TaxMonth == report.Month
                    && c.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Draft)
                .ToListAsync();
            await _db.HydratePayeeContactsAsync(companyId, draftCerts);
            foreach (var cert in draftCerts.OrderBy(c => c.CertificateNumber))
            {
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id, LineOrder = lineOrder++,
                    TaxPayerId = cert.PayeeContact?.TaxId,
                    TaxPayerName = cert.PayeeContact?.Name ?? "",
                    TransactionDate = new DateTime(cert.TaxYear, cert.TaxMonth, 1),
                    Description = $"⚠️ หนังสือรับรองยังเป็นร่าง — {cert.CertificateNumber} (ออกใบก่อนยื่น)",
                    IncomeAmount = cert.TotalIncomeAmount,
                    TaxRate = cert.TotalIncomeAmount > 0
                        ? Math.Round(cert.TotalTaxAmount / cert.TotalIncomeAmount * 100m, 2, MidpointRounding.AwayFromZero)
                        : 0m,
                    TaxAmount = cert.TotalTaxAmount, DocumentId = cert.DocumentId,
                    IncomeTypeCode = "40(8)", IsExcluded = true,
                });
            }

            // เอกสารที่มี cert ใบใดก็ตามครอบแล้ว (ทุกสถานะยกเว้น Voided, เดือนไหน
            // ก็ตาม — จ่าย ก.ค. ใบตั้งหนี้ มิ.ย. แถวจริงอยู่เดือนของ cert; cert
            // ร่างก็มีบรรทัดเตือนของตัวเองแล้ว) → ไม่ mine จากเอกสารซ้ำ
            var certCoveredDocIds = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.DocumentId != null
                    && c.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Voided)
                .Select(c => c.DocumentId!.Value)
                .ToListAsync())
                .ToHashSet();
            docs = docs.Where(d => !certCoveredDocIds.Contains(d.Id)).ToList();
        }

        // ═════ ส่วนเสริม: เอกสารมี WHT แต่ยังไม่ออกหนังสือรับรอง ═════
        // **IsExcluded = true ตั้งแต่ generate** (A2): แบบ ภ.ง.ด. นำส่งตามหนังสือ
        // รับรอง 50 ทวิ ที่ออกจริง — ไฟล์ e-Filing นับเฉพาะ cert Issued/Printed
        // อยู่แล้ว ถ้าแถวเตือนนับเข้ายอด ยอดบนจอจะ "เกิน" ไฟล์ยื่นเสมอ (เคสจริง:
        // Excel ลำดับ 21/27 หนังสือรับรองถูกยกเลิก/ยังไม่ออก แต่สถานะยัง "ใช้").
        // แถวยังอยู่ให้เห็นว่ามีใบค้างออก + นักบัญชีติ๊กกลับเข้ามือได้เมื่อจงใจ
        foreach (var doc in docs)
        {
            var docWhtLines = doc.Lines.Where(l => l.WithholdingTaxAmount > 0).ToList();
            if (docWhtLines.Count > 0)
            {
                foreach (var line in docWhtLines)
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
                        Description = $"⚠️ ยังไม่ออกหนังสือรับรอง — {line.Description} "
                            + "(ออกใบที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" แล้วกด \"สร้างใหม่\")",
                        IncomeAmount = line.Amount,
                        TaxRate = whtRate,
                        TaxAmount = line.WithholdingTaxAmount,
                        DocumentId = doc.Id,
                        IncomeTypeCode = line.IncomeTypeCode ?? "40(8)",
                        IsExcluded = true,
                    });
                }
            }
            else
            {
                // WHT อยู่ระดับเอกสาร (กรอกยอดตอนบันทึกจ่าย) — เดิมใบแบบนี้
                // "หาย" จากรายงานทั้งใบเพราะ loop รายบรรทัดไม่เจออะไร
                var docBase = doc.Lines.Sum(l => l.Amount);
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
                    Description = $"⚠️ ยังไม่ออกหนังสือรับรอง — {doc.DocumentNumber} "
                        + "(ออกใบที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" แล้วกด \"สร้างใหม่\")",
                    IncomeAmount = docBase,
                    TaxRate = docBase > 0
                        ? Math.Round(doc.WithholdingTaxAmount / docBase * 100m, 2, MidpointRounding.AwayFromZero)
                        : 0m,
                    TaxAmount = doc.WithholdingTaxAmount,
                    DocumentId = doc.Id,
                    IncomeTypeCode = "40(8)",
                    IsExcluded = true,
                });
            }
        }

        // Group by vendor (TaxPayerId) and add summary lines — ไม่รวมบรรทัดที่
        // ติ๊กออก (เช่น cert ร่าง) ไม่งั้นยอด [สรุป] โป่งเกินยอดที่นำส่งจริง
        var vendorGroups = report.Lines
            .Where(l => !string.IsNullOrEmpty(l.TaxPayerId) && !l.IsExcluded)
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
                && j.SourceDocumentId == null
                // JE ที่ถูกกลับรายการแล้ว (ReversedByEntryId) ต้องไม่นับ — เดิม
                // ใบต้นฉบับนับเต็ม ส่วนใบกลับรายการยอดติดลบถูก filter ทิ้ง
                // ⇒ นำส่งเกินสำหรับรายการที่ยกเลิกไปแล้ว. ใบ reversal เอง
                // (OriginalEntryId != null) ก็ข้าม — คู่ของมันไม่ถูกนับแล้ว
                && j.ReversedByEntryId == null
                && j.OriginalEntryId == null)
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any()
            && report.TaxType is TaxType.WithholdingTax3 or TaxType.WithholdingTax53)
        {
            // เฉพาะบัญชี WHT ค้างจ่าย "ของแบบนี้" เท่านั้น — เดิม match หลวม 3 ทาง:
            //   (1) prefix "2191" ⇒ กวาดบัญชี VAT 21911/21912/21913 เข้ามาด้วย —
            //       JV auto-reconcile มัดจำที่แตะ 21913 (ภาษีขายรอเรียกเก็บ 7%)
            //       โผล่ใน ภงด.53 เป็นแถว "REC..." 467.29 × 7% = 32.71 ทั้งที่
            //       เป็น VAT ไม่ใช่หัก ณ ที่จ่าย (บั๊กที่ผู้ใช้เจอ — เลขนำหน้า
            //       ชนความหมาย คลาสเดียวกับ 21510/53xx)
            //   (2) "11910" = ภาษี "ถูกหัก" — เครดิตภาษีของเรา (ลูกค้าหักเราไว้)
            //       คนละฝั่งกับยอดที่เราต้องนำส่ง — ห้ามเข้าแบบนำส่งเด็ดขาด
            //   (3) ไม่แยกแบบ ⇒ แถวเดียวกันเข้าทั้ง ภงด.3 และ ภงด.53 = ซ้ำ 2 แบบ
            // ผังแยกแบบให้อยู่แล้ว: 21916 = ภ.ง.ด.3 / 21917 = ภ.ง.ด.53
            var formCode = report.TaxType == TaxType.WithholdingTax3 ? "21916" : "21917";
            var formTagSpace = report.TaxType == TaxType.WithholdingTax3 ? "ภ.ง.ด. 3" : "ภ.ง.ด. 53";
            var formTagTight = report.TaxType == TaxType.WithholdingTax3 ? "ภ.ง.ด.3" : "ภ.ง.ด.53";
            var whtLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    && (l.Account.AccountCode == formCode
                        // ผังกำหนดเอง: ชื่อบอกทั้ง "หัก ณ ที่จ่าย" + เลขแบบ และ
                        // ต้องไม่ใช่ฝั่ง "ถูกหัก"
                        || (l.Account.AccountName.Contains("หัก ณ ที่จ่าย")
                            && !l.Account.AccountName.Contains("ถูกหัก")
                            && (l.Account.AccountName.Contains(formTagSpace)
                                || l.Account.AccountName.Contains(formTagTight)))))
                .ToListAsync();

            var byEntry = whtLines.GroupBy(l => l.JournalEntryId);
            foreach (var grp in byEntry)
            {
                var je = grp.First().JournalEntry;
                var whtAmount = grp.Sum(l => l.CreditAmount - l.DebitAmount);
                if (whtAmount <= 0) continue;

                // ฐานเงินได้ = Σ ฝั่ง Debit ของ JE (ขาค่าใช้จ่าย gross) — เดิม
                // เอา ΣDebit "ลบ WHT ออกอีกรอบ" ทั้งที่ WHT อยู่ฝั่ง Credit
                // (Dr ค่าใช้จ่าย 100 / Cr WHT 3 / Cr เงินสด 97 → ฐานเคยได้ 97.09
                //  อัตรา 3.09% — RD cross-check ฐาน×อัตรา=ภาษี ไม่ผ่านทุกแถว)
                var baseAmount = await _db.JournalEntryLines
                    .Where(l => l.JournalEntryId == je.Id)
                    .SumAsync(l => l.DebitAmount);
                var estimatedRate = baseAmount > 0
                    ? Math.Round(whtAmount / baseAmount * 100, 2, MidpointRounding.AwayFromZero)
                    : 3m;

                // ชื่อ/เลขผู้ถูกหัก — แกะจาก ref/description ของ JE (ตัวเดียวกับ
                // ฝั่ง VAT) — เดิมไม่ตั้ง TaxPayerId เลย ⇒ ไฟล์ยื่นเขียนเลขผู้เสีย
                // ภาษีเป็นศูนย์ 13 ตัว
                var (wPayerName, wPayerId) = ExtractTaxpayerFromJournalEntry(je);

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TransactionDate = je.EntryDate,
                    // เลขที่เอกสาร = เลขเอกสารจริงที่ JE อ้างถึง (แกะจาก ref/desc)
                    Description = ExtractDocRefFromJe(je),
                    TaxPayerName = string.IsNullOrWhiteSpace(wPayerName) ? je.Description : wPayerName,
                    TaxPayerId = wPayerId,
                    IncomeAmount = baseAmount,
                    TaxRate = estimatedRate,
                    TaxAmount = whtAmount,
                    IncomeTypeCode = "40(8)"
                });
            }
        }

        // สูตรเดียวกับ RecalcPndTotals (ไม่นับ SUMMARY + บรรทัดที่ติ๊กออก) —
        // เดิมตรงนี้ไม่กรอง IsExcluded ⇒ ยอดหัวตอน generate กับตอน recalc หลัง
        // ติ๊กบรรทัด ใช้คนละสูตร (defect class "สูตรเดียวกันต้องมีที่เดียว") —
        // สำคัญขึ้นเมื่อรายงานมีบรรทัด cert ร่างที่ excluded ตั้งแต่ generate
        report.TotalIncome = report.Lines
            .Where(l => l.IncomeTypeCode != "SUMMARY" && !l.IsExcluded).Sum(l => l.IncomeAmount);
        report.TotalTaxWithheld = report.Lines
            .Where(l => l.IncomeTypeCode != "SUMMARY" && !l.IsExcluded).Sum(l => l.TaxAmount);
        // ใบแนบ ภ.ง.ด.3/53 เรียงตามวันที่จ่ายเหมือนกัน (ตรวจง่าย + ตรงแบบยื่น)
        NormalizeReportLineOrder(report);
    }

    /// <summary>ยอด "บวกกลับ" §65 ตรี ของช่วงเวลาหนึ่ง (ไม่รวม (4) ค่ารับรอง ซึ่ง
    /// คิดเป็น annual cap แยก) — อ่านจาก <c>Document.NonDeductibleAmount</c> +
    /// breakdown ใน <c>NonDeductibleRuleJson</c> ที่ Section65TerValidator เขียน
    /// ไว้ตอนอนุมัติ.
    ///
    /// public เพื่อให้ **ภ.ง.ด.51 (ประมาณการครึ่งปี)** ใช้ฐานเดียวกับ ภ.ง.ด.50 —
    /// เดิม 51 คำนวณจาก JE ล้วนไม่มีบวกกลับเลย ⇒ ประมาณการต่ำกว่าความจริง
    /// เสี่ยงโดนเงินเพิ่ม 20% ตาม §67 ตรี (ประมาณการขาดเกิน 25%)</summary>
    public async Task<decimal> ComputeSection65TerAddBackAsync(
        Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var nonDeductDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && d.NonDeductibleAmount > 0)
            .Select(d => new { d.NonDeductibleAmount, d.NonDeductibleRuleJson })
            .ToListAsync();
        decimal addBack = 0m;
        foreach (var nd in nonDeductDocs)
        {
            if (string.IsNullOrWhiteSpace(nd.NonDeductibleRuleJson))
            {
                addBack += nd.NonDeductibleAmount;   // ไม่มี breakdown → บวกทั้งก้อน
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
                        addBack += amt;
                }
            }
            catch { addBack += nd.NonDeductibleAmount; }   // JSON เพี้ยน → fallback ทั้งก้อน
        }
        return addBack;
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
            // B8: ค่าเสื่อมต้องอยู่ใน **รอบบัญชีเดียวกับรายได้/ค่าใช้จ่าย** —
            // เดิมกรอง `d.Year == year` (ปีปฏิทิน) ขณะที่ขาอื่นใช้ startDate..
            // endDate ที่เคารพ FiscalYearStartMonth ⇒ บริษัทรอบไม่ตรงปีปฏิทิน
            // (เช่น ก.ค.–มิ.ย.) ได้ค่าเสื่อมผิดรอบทั้งก้อน. AssetDepreciation
            // เก็บ Year+Month → เทียบเป็น (ปี,เดือน) ตามช่วงรอบจริง
            var depFrom = (startDate.Year, startDate.Month);
            var depTo = (endDate.Year, endDate.Month);
            var depRows = await _db.Set<AssetDepreciation>()
                .Where(d => activeAssets.Contains(d.FixedAssetId)
                    && (d.Year > depFrom.Item1 || (d.Year == depFrom.Item1 && d.Month >= depFrom.Item2))
                    && (d.Year < depTo.Item1 || (d.Year == depTo.Item1 && d.Month <= depTo.Item2)))
                .Select(d => d.Amount)
                .ToListAsync();
            totalDepreciation = depRows.Sum();
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
        var section65TerAddBack = await ComputeSection65TerAddBackAsync(companyId, startDate, endDate);

        // Net profit before tax — เพิ่มส่วนเกิน entertainment + รายจ่ายต้องห้าม §65 ตรี
        // ที่หักไม่ได้ กลับเข้ามา (tax addition / รายการบวกกลับ).
        var netProfitBeforeTax = totalRevenue - totalExpenses + entertainmentExcess + section65TerAddBack;

        // ===== ผลขาดทุนสุทธิยกมา 5 ปี (§65 ตรี(12)) — B7 =====
        // เดิมโค้ดตรงนี้เขียนเป็น "เครดิตภาษีปีก่อน" โดยเช็ค `NetVat < 0` แล้ว
        // เอา CitAmount มาหัก — แต่ NetVat ของ CIT ถูก reuse เก็บ "กำไรสุทธิ
        // ก่อนภาษี" ⇒ เงื่อนไขจริงคือ "ปีก่อนขาดทุน" ซึ่งปีนั้น CitAmount = 0
        // เสมอ (CalculateThaiCit คืน 0 เมื่อกำไร ≤ 0) = dead code และ**ผล
        // ขาดทุนยกมาไม่เคยถูกหักที่ใดเลย** ทั้งที่กฎหมายให้ยกไปได้ 5 รอบบัญชี
        //
        // กติกา: ขาดทุนของรอบ Y ใช้ได้ถึงรอบ Y+5 · ปีที่มีกำไรกินโควตาที่เก่า
        // ที่สุดก่อน (FIFO) · ใช้เฉพาะรายงานที่ "ยื่นแล้ว" (Filed) เป็นฐาน
        var priorCitReports = await _db.TaxReports.AsNoTracking()
            .Where(t => t.CompanyId == companyId
                && t.TaxType == TaxType.CorporateIncomeTax
                && t.Year >= year - 5 && t.Year < year
                && t.Status == TaxReportStatus.Filed)
            .OrderBy(t => t.Year)
            .Select(t => new { t.Year, NetProfit = t.NetVat })
            .ToListAsync();

        // pool ของขาดทุนที่ยังไม่ถูกใช้: (ปีที่เกิด, ยอดคงเหลือ)
        var lossPool = new List<(int Year, decimal Remaining)>();
        foreach (var pr in priorCitReports)
        {
            // ตัดโควตาที่หมดอายุ (ขาดทุนปี Y ใช้ได้ถึงปี Y+5)
            lossPool.RemoveAll(x => pr.Year > x.Year + 5);
            if (pr.NetProfit < 0)
            {
                lossPool.Add((pr.Year, Math.Abs(pr.NetProfit)));
                continue;
            }
            // ปีกำไร → กินโควตาเก่าสุดก่อน
            var profitLeft = pr.NetProfit;
            for (var i = 0; i < lossPool.Count && profitLeft > 0; i++)
            {
                var use = Math.Min(lossPool[i].Remaining, profitLeft);
                lossPool[i] = (lossPool[i].Year, lossPool[i].Remaining - use);
                profitLeft -= use;
            }
            lossPool.RemoveAll(x => x.Remaining <= 0.005m);
        }
        lossPool.RemoveAll(x => year > x.Year + 5);   // หมดอายุ ณ ปีที่กำลังคำนวณ
        var lossCarryForwardAvailable = lossPool.Sum(x => x.Remaining);
        var lossCarryForwardUsed = netProfitBeforeTax > 0
            ? Math.Min(lossCarryForwardAvailable, netProfitBeforeTax)
            : 0m;

        // Thai CIT progressive rates (for SME companies) — คิดจากกำไรหลังหัก
        // ผลขาดทุนยกมาแล้ว (ฐานภาษีจริงตาม §65 ตรี(12))
        var taxableProfit = netProfitBeforeTax - lossCarryForwardUsed;
        var citAmount = CalculateThaiCit(taxableProfit);
        var netCitAmount = citAmount;

        // ===== เครดิตภาษีที่ "เราถูกหัก ณ ที่จ่าย" (ภ.ง.ด.50/51) =====
        // เดิม ภ.ง.ด.50 คำนวณจบที่ "ภาษีที่ต้องเสีย" โดยไม่หักเครดิตนี้เลย ผู้ใช้
        // ต้องไปหักเองนอกระบบ ทั้งที่ยอดอยู่ในบัญชี 11910 อยู่แล้ว
        //
        // ⚠️ นับเฉพาะรายการที่ "มีหนังสือรับรองจริง" (Received/Claimed) —
        // รายการ Pending คือถูกหักแล้วแต่ยังไม่ได้ใบ ซึ่งกฎหมายยังเครดิตไม่ได้
        // (ดู WHT_CREDIT_PLAN.md ข้อ L1) การเอา Pending มารวมจะทำให้ยื่นเกินสิทธิ์
        var whtCreditRows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxYear == year
                && (w.Status == WhtCreditStatus.Received || w.Status == WhtCreditStatus.Claimed))
            .Select(w => w.WhtAmount).ToListAsync();
        var whtCredit = whtCreditRows.Sum();
        var whtPendingRows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxYear == year
                && w.Status == WhtCreditStatus.Pending)
            .Select(w => w.WhtAmount).ToListAsync();
        var whtPending = whtPendingRows.Sum();

        // ภาษีที่ต้องชำระเพิ่ม (ติดลบ = ชำระเกิน → ขอคืน/ยกไปปีหน้า ตาม L4)
        var citPayable = netCitAmount - whtCredit;

        report.TotalIncome = totalRevenue;
        // CIT มีที่เก็บของตัวเองแล้ว (CitAmount) — ยังเขียน TotalTaxWithheld คู่ไว้
        // เพื่อไม่ให้ผู้อ่านเดิม (export/e-Filing/รายงานเก่า) พังระหว่างทยอยย้าย
        report.CitAmount = netCitAmount;
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

        // ผลขาดทุนยกมา §65 ตรี(12) — หักจาก "ฐานกำไร" ไม่ใช่หักจากตัวภาษี
        // (TaxAmount = 0 เพราะบรรทัดนี้ลดฐาน ไม่ใช่ลดภาษีโดยตรง)
        if (lossCarryForwardUsed > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = $"หักผลขาดทุนสุทธิยกมา (§65 ตรี(12) — ยกได้ไม่เกิน 5 รอบบัญชี) "
                    + $"· ใช้ {lossCarryForwardUsed:N2} จากโควตาคงเหลือ {lossCarryForwardAvailable:N2}",
                IncomeAmount = -lossCarryForwardUsed,
                TaxAmount = 0m,
                IncomeTypeCode = "TAX_CREDIT"
            });
        }

        // ===== เครดิต "ภาษีที่ถูกหัก ณ ที่จ่าย" + ยอดสรุปที่ต้องชำระ/ขอคืน =====
        if (whtCredit > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "หัก: ภาษีถูกหัก ณ ที่จ่าย (มีหนังสือรับรอง)",
                IncomeAmount = whtCredit,
                TaxAmount = -whtCredit,
                IncomeTypeCode = "WHT_CREDIT"
            });
        }
        // เตือนยอดที่ "ยังไม่ได้ใบรับรอง" — เครดิตไม่ได้ตามกฎหมาย ต้องรีบทวงลูกค้า
        // ก่อนยื่นแบบ ไม่งั้นเสียสิทธิ์ทั้งจำนวน (ไม่นับรวมในยอดภาษี → TaxAmount = 0)
        if (whtPending > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "⚠️ ถูกหักแล้วแต่ยังไม่ได้รับหนังสือรับรอง — ยังเครดิตไม่ได้ (ตามทวงจากลูกค้า)",
                IncomeAmount = whtPending,
                TaxAmount = 0,
                IncomeTypeCode = "WHT_PENDING"
            });
        }
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = citPayable >= 0 ? "ภาษีที่ต้องชำระเพิ่ม" : "ชำระเกิน — ขอคืน/ยกไปปีถัดไป",
            IncomeAmount = Math.Abs(citPayable),
            TaxAmount = citPayable,
            IncomeTypeCode = "CIT_PAYABLE"
        });
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
                        d.SupplierTaxInvoiceDate,
                        d.IsForeignService, d.Pp36RdReceiptNumber, d.Pp36RdReceiptDate
                    }).ToDictionaryAsync(x => x.Id);

                var enriched = resp.Lines.Select(ln =>
                {
                    if (ln.DocumentId.HasValue && docInfo.TryGetValue(ln.DocumentId.Value, out var info))
                    {
                        var isInput = ln.IncomeTypeCode is "INPUT" or "JE_INPUT";
                        // §86/14 — ภาษีซื้อ ภ.พ.36: "ใบกำกับ" คือใบเสร็จรับเงินของ
                        // กรมสรรพากรจากการนำส่ง ไม่ใช่ invoice ของผู้ขาย ตปท.
                        // (ผู้ขายต่างประเทศออกใบกำกับไทยไม่ได้) — เลข/วันที่ใน
                        // รายงานภาษีซื้อจึงต้องเป็นของใบเสร็จ RD ที่ stamp ตอนรับรู้
                        var invNo = isInput
                            ? (info.IsForeignService && !string.IsNullOrWhiteSpace(info.Pp36RdReceiptNumber)
                                ? $"{info.Pp36RdReceiptNumber} (ใบเสร็จ RD ภ.พ.36)"
                                : (string.IsNullOrWhiteSpace(info.SupplierInvoiceNumber) ? info.DocumentNumber : info.SupplierInvoiceNumber))
                            : info.DocumentNumber;
                        var invDate = isInput && info.IsForeignService && info.Pp36RdReceiptDate.HasValue
                            ? info.Pp36RdReceiptDate.Value
                            : (isInput && info.SupplierTaxInvoiceDate.HasValue)
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

        // Revalidate ก่อนยื่น: บรรทัด active ต้องไม่อ้างเอกสารที่ถูกยกเลิก/ลบ
        // ไปแล้วระหว่างที่รายงานเป็น Draft — ยื่นทั้งอย่างนั้น = เคลม/นำส่งจาก
        // ใบที่ไม่มีอยู่จริง (§82/5(1)) โดนประเมินคืน
        if (report.TaxType == TaxType.VAT)
        {
            var lineDocIds = report.Lines
                .Where(l => !l.IsExcluded && l.DocumentId.HasValue)
                .Select(l => l.DocumentId!.Value).Distinct().ToList();
            if (lineDocIds.Count > 0)
            {
                var deadDocs = await _db.Documents.AsNoTracking()
                    .Where(d => lineDocIds.Contains(d.Id)
                        && (d.Status == DocumentStatus.Voided || d.IsDeleted))
                    .Select(d => d.DocumentNumber).Take(5).ToListAsync();
                if (deadDocs.Count > 0)
                    throw new InvalidOperationException(
                        $"ยื่นไม่ได้ — รายงานมีบรรทัด active ที่อ้างเอกสารซึ่งถูกยกเลิก/ลบไปแล้ว: "
                        + string.Join(", ", deadDocs)
                        + " — กด \"สร้างรายงานใหม่\" หรือติ๊กบรรทัดนั้นออกก่อน");
            }
        }

        report.Status = TaxReportStatus.Filed;
        report.FiledDate = DateTime.UtcNow;
        // Mark the filing as locked simultaneously — see Task 4 of the
        // ERP upgrade. Once Filed, every linked document/JE is read-only
        // until an explicit UnlockTaxFilingAsync (admin operation).
        report.FilingLockedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // เครดิตภาษีซื้อยกไป: ถ้ารายงานงวดถัดไปถูกสร้างไว้ "ก่อน" งวดนี้ยื่น มัน
        // จะไม่มีบรรทัด VAT_CREDIT_CF (ตอน generate งวดก่อนยัง Draft) → ผู้ใช้ยื่น
        // งวดถัดไปโดยเครดิตหายเงียบ = จ่าย VAT เกินจริง. เติมบรรทัดให้ทันทีที่ยื่น
        if (report.TaxType == TaxType.VAT && report.NetVat < 0)
        {
            try
            {
                var nextPeriod = new DateTime(report.Year, report.Month, 1).AddMonths(1);
                var nextReport = await _db.TaxReports.Include(r => r.Lines)
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                        && r.Year == nextPeriod.Year && r.Month == nextPeriod.Month
                        && r.Status != TaxReportStatus.Filed);
                if (nextReport != null
                    && !nextReport.Lines.Any(l => l.IncomeTypeCode == "VAT_CREDIT_CF"))
                {
                    var cf = Math.Abs(report.NetVat);
                    nextReport.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = nextReport.Id,
                        LineOrder = (nextReport.Lines.Count == 0 ? 0 : nextReport.Lines.Max(l => l.LineOrder)) + 1,
                        Description = $"เครดิตภาษีซื้อยกมาจากเดือน {report.Month:D2}/{report.Year}",
                        IncomeAmount = cf,
                        TaxRate = 0,
                        TaxAmount = -cf,
                        IncomeTypeCode = "VAT_CREDIT_CF"
                    });
                    RecalcVatTotals(nextReport);
                    nextReport.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                }
            }
            catch { /* best-effort — การเติมเครดิตงวดถัดไปล้มไม่กระทบการยื่นงวดนี้ */ }
        }

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
                // ===== Guard ตอน "ติ๊กใช้" (Excluded=false) — เส้น auto-save จาก UI =====
                if (lineUpdate.Excluded == false && line.IsExcluded
                    && report.TaxType == TaxType.VAT)
                {
                    // (1) บรรทัดเตือน/ต้องห้าม (🚫 เกิน 6 เดือน / ⚠️ ขายข้ามงวด /
                    //     รอใบกำกับ) — ติ๊กใช้ = เคลมเกินสิทธิ์/ภาษีขายผิดงวดทันที
                    // B14: ข้อความต้องบอก **ลำดับบรรทัด** — auto-save ส่งทั้งหน้า
                    // เป็นคำขอเดียว ติ๊กผิด 1 บรรทัด rollback ทั้งชุด ผู้ใช้ต้องรู้ว่า
                    // บรรทัดไหนถึงจะแก้ได้ (เดิมบอกแค่ข้อความ 60 ตัวแรก)
                    var descHead = line.Description ?? "";
                    if (descHead.StartsWith("🚫") || descHead.StartsWith("⚠️"))
                        throw new InvalidOperationException(
                            $"บรรทัดที่ {line.LineOrder}: \"{descHead[..Math.Min(60, descHead.Length)]}...\" "
                            + "เป็นบรรทัดเตือน/ต้องห้าม — นำมาคำนวณยอดงวดนี้ไม่ได้ "
                            + "(เกินกรอบ §82/3 หรือภาษีขายต้องยื่นเพิ่มเติมของงวดเดิม) · "
                            + "ติ๊กบรรทัดนั้นออกแล้วบันทึกใหม่");
                    if (descHead.StartsWith("[รอใบกำกับ"))
                        throw new InvalidOperationException(
                            $"บรรทัดที่ {line.LineOrder}: ภาษีซื้อยังพักที่ 11640 (ใบกำกับยังไม่ครบ §86/4) — "
                            + "เติมข้อมูลใบกำกับที่หน้าเอกสารก่อน จึงจะเคลม ภ.พ.30 ได้");
                    // (2) เอกสารเดียวกันถูก "ใช้" ในรายงาน VAT งวดอื่นอยู่แล้ว →
                    //     ติ๊กซ้ำ = เคลม 2 งวด (§82/3) — guard เดียวกับตอน pull
                    if (line.DocumentId.HasValue)
                    {
                        var usedIn = await _db.TaxReportLines.AsNoTracking()
                            .Where(l => l.DocumentId == line.DocumentId && !l.IsExcluded
                                && l.TaxReportId != reportId
                                && l.TaxReport.CompanyId == companyId
                                && l.TaxReport.TaxType == TaxType.VAT)
                            .Select(l => new { l.TaxReport.Year, l.TaxReport.Month })
                            .FirstOrDefaultAsync();
                        if (usedIn != null)
                            throw new InvalidOperationException(
                                $"เอกสารนี้ถูกใช้ในรายงานภาษีงวด {usedIn.Month:D2}/{usedIn.Year} แล้ว — "
                                + "นำออกจากงวดนั้นก่อนจึงจะใช้งวดนี้ได้ (กันเคลมซ้ำ 2 งวด)");
                        // (3) เอกสารถูกยกเลิก/ลบไปแล้ว → เคลมไม่ได้
                        var docState = await _db.Documents.AsNoTracking()
                            .Where(d => d.Id == line.DocumentId.Value && d.CompanyId == companyId)
                            .Select(d => new { d.Status, d.IsDeleted })
                            .FirstOrDefaultAsync();
                        if (docState == null || docState.IsDeleted || docState.Status == DocumentStatus.Voided)
                            throw new InvalidOperationException(
                                "เอกสารของบรรทัดนี้ถูกยกเลิก/ลบไปแล้ว — เคลม ภ.พ.30 ไม่ได้ (§82/5(1))");
                    }
                }
                if (lineUpdate.Excluded.HasValue) line.IsExcluded = lineUpdate.Excluded.Value;
                line.UpdatedAt = DateTime.UtcNow;
            }

            // Totals recalc — lines the accountant excluded (IsExcluded) are
            // kept for audit but do NOT count toward the filed figures.
            if (report.TaxType == TaxType.VAT)
            {
                RecalcVatTotals(report);
            }
            else if (report.TaxType == TaxType.CorporateIncomeTax)
            {
                // CIT: บรรทัดคือ breakdown (รายได้/ค่าใช้จ่าย/บวกกลับ/เครดิต/ยอด
                // ชำระ) ไม่ใช่รายการบวกรวม — ห้าม recompute หัวจากบรรทัด. เดิม
                // เข้า else ล่าง: กด "บันทึก" ครั้งเดียว TotalIncome ถูกทับเป็น
                // Σทุกบรรทัด (รายได้+ค่าใช้จ่าย+ค่าเสื่อมปน) และ TotalTaxWithheld
                // = ΣTaxAmount (CIT+เครดิตติดลบ+ยอดชำระซ้ำ) ⇒ หัวรายงาน/ไฟล์
                // ภ.ง.ด.50 เพี้ยนถาวรจนกด "สร้างใหม่". CitAmount/NetVat(กำไร)
                // คงตาม GenerateCitReport
            }
            else if (report.TaxType == TaxType.VatPp36)
            {
                // ภ.พ.36 เก็บยอดนำส่งใน OutputVat/NetVat — เดิมเข้า else ล่างซึ่ง
                // อัปเดตแค่ TotalTaxWithheld ⇒ ติ๊กบรรทัดออกแล้ว "VAT นำส่ง" บนจอ
                // (อ่าน outputVat/netVat) ค้างค่าเดิม
                var pp36Active = report.Lines
                    .Where(l => !l.IsExcluded && l.IncomeTypeCode != "SUMMARY").ToList();
                report.TotalIncome = pp36Active.Sum(l => l.IncomeAmount);
                report.OutputVat = pp36Active.Sum(l => l.TaxAmount);
                report.InputVat = 0;
                report.NetVat = report.OutputVat;
            }
            else
            {
                var active = report.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode != "SUMMARY");
                report.TotalIncome = active.Sum(l => l.IncomeAmount);
                report.TotalTaxWithheld = active.Sum(l => l.TaxAmount);
            }
        }

        report.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // บรรทัดที่ถูกลบไปแล้วจากที่อื่น (เช่น "สร้างใหม่" ในอีกแท็บ) ไม่ควรทำให้
            // การติ๊ก/แก้บรรทัดอื่นทั้งชุดล้มตาม — ตัดเฉพาะแถวที่หายแล้วบันทึกต่อ.
            // ถ้าตัวรายงานเองหาย = แก้ต่อไม่ได้จริง ต้องบอกให้ผู้ใช้รีเฟรช
            if (ex.Entries.Any(e => e.Entity is TaxReport))
                throw new InvalidOperationException(
                    "รายงานภาษีงวดนี้ถูกลบ/สร้างใหม่ระหว่างที่เปิดหน้าอยู่ — กรุณารีเฟรชหน้าแล้วบันทึกอีกครั้ง");
            foreach (var e in ex.Entries) e.State = EntityState.Detached;
            await _db.SaveChangesAsync();
        }
        return MapToResponse(report);
    }

    /// <summary>Recompute OutputVat/InputVat/NetVat from the report's lines —
    /// excluded lines are kept for audit but dropped from the figures.
    /// internal: DocumentService เรียกใช้ตอน void เอกสาร (ติ๊กบรรทัดออกแล้วต้อง
    /// recalc ยอดรายงานด้วยสูตรเดียวกัน — ห้าม drift).</summary>
    internal static void RecalcVatTotals(TaxReport report)
    {
        if (report.TaxType != TaxType.VAT) return;
        var active = report.Lines.Where(l => !l.IsExcluded).ToList();
        var nonSummary = active.Where(l => l.IncomeTypeCode != "VAT_CREDIT_CF" && l.IncomeTypeCode != "EXEMPT");
        // F11 — ภาษีซื้อจาก JE ล้วน (ไม่มี source doc) tag "JE_INPUT" ต้องนับเป็น
        // ภาษีซื้อ เหมือน "INPUT" — เดิม RecalcVatTotals เช็ค == "INPUT" อย่างเดียว
        // → JE_INPUT หลุดไปรวมใน OutputVat (!= "INPUT") + หายจาก InputVat = ภาษีขาย
        // เกินจริง + ภาษีซื้อขาด → NetVat ผิด (นำส่งเกิน). สอดคล้องกับ LineSide (บรรทัด
        // 1360) ที่ถือ "INPUT" or "JE_INPUT" เป็นฝั่งซื้ออยู่แล้ว.
        // B11: allow-list **สองฝั่ง** — เดิมฝั่งขายเป็น "ทุกอย่างที่ไม่ใช่ INPUT"
        // (default-to-output) ⇒ IncomeTypeCode ใหม่/สะกดผิดในอนาคตไหลเข้าภาษีขาย
        // เงียบ ๆ แล้วนำส่งเกิน (บั๊ก JE_INPUT ที่เพิ่งแก้ก็มาจากรูปแบบนี้)
        bool IsInputLine(TaxReportLine l) => l.IncomeTypeCode is "INPUT" or "JE_INPUT";
        bool IsOutputLine(TaxReportLine l) => l.IncomeTypeCode is "OUTPUT" or "JE_OUTPUT" or null or "";
        report.OutputVat = nonSummary.Where(IsOutputLine).Sum(l => l.TaxAmount);
        report.InputVat = nonSummary.Where(IsInputLine).Sum(l => l.TaxAmount);
        var creditCf = active.Where(l => l.IncomeTypeCode == "VAT_CREDIT_CF").Sum(l => Math.Abs(l.TaxAmount));
        report.NetVat = report.OutputVat - report.InputVat - creditCf;
    }

    /// <summary>Recompute TotalIncome/TotalTaxWithheld ของแบบ ภ.ง.ด. จากบรรทัด —
    /// ไม่นับบรรทัด [สรุป] (SUMMARY เป็นยอดรวมซ้ำต่อผู้ขาย) และบรรทัดที่ติ๊กออก.
    /// internal: DocumentService เรียกตอน void เอกสารที่มีภาษีหัก ณ ที่จ่าย —
    /// ต้องใช้สูตรเดียวกันเพื่อไม่ให้ยอดหัวรายงาน drift จากบรรทัดจริง.</summary>
    /// <summary>ฐาน "มูลค่าสินค้า/บริการที่คิดภาษี" ของเอกสาร = SubTotal หักบรรทัด
    /// ยกเว้น (VatRate == -1) ออก — ยอดยกเว้นถูกนับแยกไว้ที่บรรทัด EXEMPT อยู่แล้ว
    /// (vatExemptAmount) การใช้ doc.SubTotal ทั้งใบจึงนับยอดยกเว้นซ้ำสองที่ และทำ
    /// ให้ "ฐาน × 7% ≠ ภาษีขาย" ในรายงาน/ไฟล์ยื่น (RD cross-check ไม่ผ่าน).
    /// ใบที่ไม่มีบรรทัดยกเว้นจะได้ค่าเท่า SubTotal เหมือนเดิม.
    /// หมายเหตุ: ใบที่ผสม 7% กับ 0% (§80/1) ยังรวมเป็นบรรทัดเดียวที่อัตราสูงสุด —
    /// การแยกบรรทัดต่ออัตราเป็นงานเฟสถัดไป (ดู DEVELOPMENT_PHASES.md)</summary>
    /// <summary>ใบนี้ "ไม่ใช่ใบกำกับภาษีเต็มรูป" หรือไม่ — เกณฑ์เดียวกับที่
    /// PdfGenerationService ใช้ตัดสินหัวเอกสาร (ผู้ซื้อ walk-in / ติ๊กไม่ประสงค์
    /// รับใบกำกับ / ข้อมูล §86/4 ไม่ครบ). ใบแบบนี้ยังต้องนำส่ง VAT ตามปกติ
    /// (ภาระเกิดจาก tax point ไม่ใช่หัวกระดาษ) แต่ผู้ซื้อเคลมภาษีซื้อไม่ได้ —
    /// จึงติดธงไว้ในรายงานให้ตามแก้ได้ทั้งงวด</summary>
    internal static bool NotFullTaxInvoice(Document doc)
    {
        if (doc.VatAmount <= 0.005m) return false;
        if (doc.BuyerDeclinedTaxInvoice) return true;
        if (doc.Contact == null) return true;
        if (doc.Contact.IsWalkInCustomer) return true;
        return Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(doc.Contact).Count > 0;
    }

    internal static decimal VatableBase(Document doc)
    {
        if (doc.Lines == null || doc.Lines.Count == 0) return doc.SubTotal;
        var exempt = doc.Lines.Where(l => l.VatRate == -1).Sum(l => l.Amount);
        return exempt == 0m ? doc.SubTotal : doc.SubTotal - exempt;
    }

    internal static void RecalcWhtTotals(TaxReport report)
    {
        if (report.TaxType is not (TaxType.WithholdingTax1 or TaxType.WithholdingTax3
            or TaxType.WithholdingTax53 or TaxType.WithholdingTax54)) return;
        var active = report.Lines
            .Where(l => !l.IsExcluded && l.IncomeTypeCode != "SUMMARY")
            .ToList();
        report.TotalIncome = active.Sum(l => l.IncomeAmount);
        report.TotalTaxWithheld = active.Sum(l => l.TaxAmount);
    }

    // Document types eligible to be pulled into a VAT return, and whether
    // each posts to the input (ภาษีซื้อ) side.
    // หมายเหตุ §82/5(6): ตัวตัดอัตโนมัติจาก keyword (IsProhibitedVehicleExpense)
    // ถูกถอดออกตามนโยบาย "เคลม/ไม่เคลมเป็นดุลพินิจผู้กรอก ระบบเตือนอย่างเดียว"
    // — การตัดสิทธิใช้เฉพาะ flag รายบรรทัด (IsVatClaimable) + ผังบัญชีต้องห้าม
    // ที่บริษัทตั้งเอง; คำเตือนกฎรถยนต์นั่ง (ประกาศ 42) อยู่ที่ approve warning
    // ของ DocumentService + ตอนติ๊กเคลมในหน้าเอกสาร

    /// <summary>ประเภทเอกสารที่ "ดึงเข้ารายงาน ภ.พ.30" ด้วยมือได้ + ฝั่งภาษี
    /// (true = ภาษีซื้อ). ต้องครอบคลุมทุกประเภทที่ loop หลักนับเป็น VAT — เดิม
    /// ขาด PaymentVoucher (ซึ่ง loop หลักนับเป็นภาษีซื้อเมื่อติ๊ก "ใช้งานใบกำกับ
    /// ภาษี") + ใบขายที่ไม่ใช่ TaxInvoice → ใบกำกับซื้อที่บันทึกเป็น PV มาช้า
    /// ดึงเข้างวดถัดไปไม่ได้เลย (ภาษีซื้อหายถาวรเมื่องวดเดิมยื่นแล้ว).
    /// CN/DN ไม่อยู่ในนี้ตั้งใจ — ฝั่ง/เครื่องหมายขึ้นกับใบต้นทาง ต้องแก้ผ่าน
    /// การ regenerate งวดที่ถูกต้องแทน (กันดึงผิดข้างแล้วยอดกลับด้าน).</summary>
    private static readonly Dictionary<DocumentType, bool> PullableVatTypes = new()
    {
        [DocumentType.TaxInvoice] = false,        // output
        [DocumentType.Invoice] = false,           // output (ใบแจ้งหนี้/ใบรวมที่มี VAT)
        [DocumentType.Receipt] = false,           // output (ใบเสร็จที่เป็นใบกำกับในตัว)
        [DocumentType.ReceiptVoucher] = false,    // output
        [DocumentType.PurchaseInvoice] = true,    // input
        [DocumentType.Expense] = true,            // input
        // CIL ห้ามเคลม/ห้ามดึง (§82/5(1)) — GL fold VAT เข้า expense ไม่มีขา 11610
        [DocumentType.CertificateInLieu] = false,
        [DocumentType.PaymentVoucher] = true,     // input (เมื่อ HasTaxInvoiceReference)
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

        // ใบที่มีบรรทัดอยู่ใน "รายงานงวดนี้" แล้ว — รวมบรรทัดที่ยังติ๊กออก เช่น
        // "[ยกมา §82/3]" ที่ระบบยกมาให้ตอนสร้างรายงาน. ต้องซ่อนจากรายการดึง
        // ไม่งั้นผู้ใช้กดแล้วเจอ error และถ้าหลุดผ่านจะได้บรรทัดซ้ำในงวดเดียวกัน
        // (ทางที่ถูกคือติ๊ก "ใช้" ที่บรรทัดเดิมแล้วบันทึก)
        var alreadyInThisReport = (await _db.TaxReportLines.AsNoTracking()
            .Where(l => l.TaxReportId == reportId && l.DocumentId != null)
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

        // แสดงเฉพาะเอกสารที่ "ดึงได้จริง" — ต้องผ่านกฎเดียวกับ PullDocumentIntoReport
        // ไม่งั้นรายการที่โชว์กดแล้ว error (เคสผู้ใช้รายงาน): PV ที่ยังไม่ติ๊ก
        // "ใช้งานใบกำกับภาษี", VAT ที่ override ลงต้นทุน (ไม่เคลม), หรือภาษีซื้อ
        // ที่ยังพัก 11640 (ใบกำกับ §86/4 ไม่ครบ) — ยังเคลม ภ.พ.30 ไม่ได้
        static bool IsPullable(Document d)
        {
            // PV ต้องอ้างใบกำกับซื้อ (ขอเครดิต) — ไม่งั้นจ่ายเฉย ๆ §82/5(1)
            if (d.DocumentType == DocumentType.PaymentVoucher
                && (!d.HasTaxInvoiceReference || d.RelatedDocumentId != null))
                return false;
            // override ผังภาษีซื้อนอก 116 = ตั้งใจไม่เคลม (ลงต้นทุน)
            if (!string.IsNullOrWhiteSpace(d.InputVatAccountCodeOverride)
                && !d.InputVatAccountCodeOverride.StartsWith("116"))
                return false;
            // VAT ยังพัก 11640 (ใบกำกับ §86/4 ไม่ครบ) — เคลมยังไม่ได้จนกว่าจะเติมครบ
            if (d.InputVatPostedAsUndue && d.InputVatBecameClaimableAt == null)
                return false;
            return true;
        }

        return docs
            .Where(d => !claimed.Contains(d.Id))
            .Where(d => !alreadyInThisReport.Contains(d.Id))
            .Where(IsPullable)
            // งวดต้องถูกต้องตามเวลา (ไม่ย้อนก่อนเดือนใบ / ไม่เกิน 6 เดือน §82/3)
            // — ตัวตัดสินเดียวกับตอนดึงจริง
            .Where(d => EvaluateClaimPeriod(
                ClaimBasisDate(d),
                PullableVatTypes.TryGetValue(d.DocumentType, out var inp) && inp,
                report.Year, report.Month).Ok)
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

    /// <summary>วันที่ใช้เป็น "เดือนภาษี" ของเอกสารเวลาตัดสินสิทธิ์เคลม —
    /// ฝั่งซื้อยึดวันที่ใบกำกับของผู้ขายก่อน (อาจต่างจากวันที่เราบันทึก)
    /// แล้วค่อย tax point / วันที่เอกสาร</summary>
    internal static DateTime ClaimBasisDate(Document d)
        => d.SupplierTaxInvoiceDate ?? d.TaxPointDate ?? d.DocumentDate;

    /// <summary>ตัดสินว่าเอกสารเดือนภาษี <paramref name="basis"/> ดึงเข้ารายงานงวด
    /// (<paramref name="reportYear"/>/<paramref name="reportMonth"/>) ได้หรือไม่ —
    /// **ตัวตัดสินกลาง** ใช้ทั้งตอนสร้างรายการ "ดึงเอกสาร" และตอนดึงจริง เพื่อให้
    /// สิ่งที่โชว์กับสิ่งที่กดได้ตรงกันเสมอ (เดิม list โชว์ใบที่กดแล้ว error).
    ///
    /// กฎ: (1) ห้ามย้อนก่อนเดือนภาษีของใบ — ใบเดือน ก.ค. นำไปยื่นในแบบเดือน มิ.ย.
    /// ไม่ได้ทั้งฝั่งซื้อและฝั่งขาย (2) ฝั่งซื้อเคลมได้ภายใน 6 เดือนนับจากเดือน
    /// ภาษีของใบกำกับ (§82/3) — เกินแล้วต้องลงเป็นค่าใช้จ่ายแทน
    /// </summary>
    internal static (bool Ok, string? Reason) EvaluateClaimPeriod(
        DateTime basis, bool isInput, int reportYear, int reportMonth)
    {
        var docPeriod = new DateTime(basis.Year, basis.Month, 1);
        var periodStart = new DateTime(reportYear, reportMonth, 1);

        if (periodStart < docPeriod)
            return (false,
                $"เอกสารลงวันที่ {basis:dd/MM/yyyy} (เดือนภาษี {docPeriod:MM/yyyy}) — "
                + $"ดึงเข้างวด {reportMonth:D2}/{reportYear} ซึ่งเก่ากว่าไม่ได้ "
                + "(นำไปยื่นในแบบของเดือนก่อนวันที่เอกสารไม่ได้)");

        if (isInput)
        {
            var claimLimit = docPeriod.AddMonths(7);   // เดือนใบ + 6 เดือนถัดไป
            if (periodStart >= claimLimit)
                return (false,
                    $"ใบกำกับลงวันที่ {basis:dd/MM/yyyy} — เกินกรอบ 6 เดือนตาม §82/3 "
                    + $"(เคลมได้ถึงงวด {claimLimit.AddMonths(-1):MM/yyyy}) จึงดึงเข้างวด "
                    + $"{reportMonth:D2}/{reportYear} ไม่ได้ ต้องบันทึก VAT เป็นค่าใช้จ่ายแทน");
        }
        return (true, null);
    }

    public async Task<TaxReportResponse> PullDocumentIntoReportAsync(Guid companyId, Guid reportId, Guid documentId)
    {
        // ⚠️ AsNoTracking ทั้งการอ่าน — งานนี้ "เขียนจริง" แค่ 2 อย่าง: บรรทัดใหม่
        // 1 แถว (INSERT) กับยอดรวมรายงาน (UPDATE แบบ set-based) การ track รายงาน +
        // บรรทัดเดิมทั้งชุดไว้ทำให้ SaveChanges พ่วง UPDATE แถวเดิมทุกแถวออกไปด้วย
        // ซึ่งเป็นต้นเหตุ DbUpdateConcurrencyException ("expected 1 row, affected 0")
        // ที่ผู้ใช้เจอทุกครั้งกับทุกรายงาน
        var report = await _db.TaxReports.AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");
        if (report.TaxType != TaxType.VAT)
            throw new InvalidOperationException("ดึงเอกสารได้เฉพาะรายงาน ภพ.30");
        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถแก้ไขได้ — รายงานนี้ถูกยื่นแล้ว");

        // AsNoTracking — เมธอดนี้ "อ่าน" เอกสารอย่างเดียว (ไม่แก้) การ track ไว้
        // ทำให้ Document/DocumentLine/Contact ที่ hydrate เข้ามาถูกดึงเข้า
        // ChangeTracker แล้วถูกเขียนพ่วงไปกับ SaveChanges ของรายงานโดยไม่ตั้งใจ
        var doc = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ทำ doc = null)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await _db.HydrateContactAsync(companyId, doc);

        if (!PullableVatTypes.TryGetValue(doc.DocumentType, out var isInput))
            throw new InvalidOperationException($"เอกสารประเภท {doc.DocumentType} ไม่สามารถดึงเข้ารายงาน ภพ.30 ได้");
        if (doc.VatAmount == 0)
            throw new InvalidOperationException("เอกสารนี้ไม่มี VAT");
        // PV ที่ไม่ได้ติ๊ก "ใช้งานใบกำกับภาษี" = จ่ายเงินเฉย ๆ ไม่ขอเครดิตภาษีซื้อ
        // (§82/5(1) ไม่มีใบกำกับเต็มรูป) — loop หลักก็ไม่นับ ห้ามดึงเข้ามาเคลม
        if (doc.DocumentType == DocumentType.PaymentVoucher
            && (!doc.HasTaxInvoiceReference || doc.RelatedDocumentId != null))
            throw new InvalidOperationException(
                $"ใบสำคัญจ่าย {doc.DocumentNumber} ยังไม่ได้ติ๊ก \"ใช้งานใบกำกับภาษี\" (ขอเครดิตภาษีซื้อ) — "
                + "เปิดเอกสารแล้วติ๊ก + กรอกเลขที่/วันที่ใบกำกับของผู้ขายก่อน จึงจะดึงเข้า ภ.พ.30 ได้ (§86/4)");
        // งวดที่ดึงเข้าต้องถูกต้องตามเวลา: ห้ามย้อนก่อนเดือนภาษีของใบ และฝั่งซื้อ
        // ห้ามเกินกรอบ 6 เดือน §82/3 — ใช้ตัวตัดสินกลางร่วมกับรายการ "ดึงเอกสาร"
        // เพื่อไม่ให้ list โชว์ใบที่กดแล้ว error
        var window = EvaluateClaimPeriod(
            ClaimBasisDate(doc), isInput, report.Year, report.Month);
        if (!window.Ok)
            throw new InvalidOperationException(window.Reason!);

        var existingLine = report.Lines.FirstOrDefault(l => l.DocumentId == documentId);
        if (existingLine != null)
            throw new InvalidOperationException(existingLine.IsExcluded
                ? $"เอกสาร {doc.DocumentNumber} มีบรรทัดรออยู่ในรายงานงวดนี้แล้ว "
                  + "(บรรทัดที่ยังไม่ได้ติ๊ก \"ใช้\") — ให้ติ๊กช่อง \"ใช้\" ที่บรรทัดนั้นแล้วกด \"บันทึก\" "
                  + "แทนการดึงซ้ำ มิฉะนั้นจะได้บรรทัดซ้ำในงวดเดียวกัน"
                : $"เอกสาร {doc.DocumentNumber} อยู่ในรายงานงวดนี้แล้ว");

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
        var pullBasis = ClaimBasisDate(doc);
        var windowEnd = new DateTime(pullBasis.Year, pullBasis.Month, 1).AddMonths(7).AddDays(-1);
        var pastWindow = DateTime.UtcNow.Date > windowEnd;

        var sideLabel = isInput ? "ภาษีซื้อ" : "ภาษีขาย";
        var desc = $"[ดึงเข้างวด-{sideLabel}] {doc.DocumentNumber} (เอกสารงวด {doc.DocumentDate.Month:D2}/{doc.DocumentDate.Year})";
        if (pastWindow)
            desc = "⚠️ " + desc + " — ใบกำกับเกิน 6 เดือน อาจเครดิตภาษีซื้อไม่ได้";

        // ── เขียนจริงขั้นที่ 1: INSERT บรรทัดใหม่แถวเดียว ──────────────────
        var newLine = new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = nextOrder,
            TaxPayerId = doc.Contact?.TaxId,
            TaxPayerName = doc.Contact?.Name ?? "",
            TransactionDate = doc.TaxPointDate ?? doc.DocumentDate,
            Description = desc,
            IncomeAmount = VatableBase(doc),
            TaxRate = taxRate,
            // ฝั่งซื้อ: เคารพ §82/5 เหมือน main loop — เคลมเฉพาะ VAT ของบรรทัด
            // ที่เคลมได้ (เดิมดึงเข้ายอดเต็ม doc.VatAmount ⇒ ใบที่มีบรรทัดต้องห้าม
            // เข้ามาทางปุ่ม "ดึงเอกสาร" เคลมเกินสิทธิ์)
            TaxAmount = isInput && doc.Lines != null && doc.Lines.Any(l => !l.IsVatClaimable)
                ? doc.Lines.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount)
                : doc.VatAmount,
            IncomeTypeCode = isInput ? "INPUT" : "OUTPUT",
            DocumentId = doc.Id
        };
        _db.TaxReportLines.Add(newLine);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // เราไม่ได้ track รายงาน/บรรทัดเดิมแล้ว → แถวที่ค้างจึงเป็นของงานอื่น
            // ใน request เดียวกัน (SaveChanges ที่ล้มเหลวก่อนหน้าไม่ล้าง tracker ให้)
            // → ตัดออกแล้วบันทึกใหม่ ไม่ให้พาลงานของผู้ใช้ล้มไปด้วย
            foreach (var e in ex.Entries)
                if (!ReferenceEquals(e.Entity, newLine)) e.State = EntityState.Detached;
            await _db.SaveChangesAsync();
        }

        // ── เขียนจริงขั้นที่ 2: ยอดรวมรายงาน (set-based ไม่ผ่าน change tracker) ──
        var allLines = await _db.TaxReportLines.AsNoTracking()
            .Where(l => l.TaxReportId == report.Id)
            .ToListAsync();
        var totals = new TaxReport { TaxType = report.TaxType, Lines = allLines };
        RecalcVatTotals(totals);
        await _db.TaxReports.Where(r => r.Id == report.Id && r.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.OutputVat, totals.OutputVat)
                .SetProperty(r => r.InputVat, totals.InputVat)
                .SetProperty(r => r.NetVat, totals.NetVat)
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow));

        // ── §87 จัดลำดับตามวันที่ (best-effort) — ล้มเหลวได้โดยไม่กระทบผลการดึง ──
        try
        {
            var before = allLines.ToDictionary(l => l.Id, l => l.LineOrder);
            var ordered = new TaxReport { TaxType = report.TaxType, Lines = allLines };
            NormalizeReportLineOrder(ordered);
            // อัปเดตเฉพาะแถวที่ลำดับเปลี่ยนจริง (ปกติไม่กี่แถว) — ไม่ยิงทั้งรายงาน
            foreach (var l in allLines.Where(l => before[l.Id] != l.LineOrder))
            {
                var order = l.LineOrder;
                await _db.TaxReportLines.Where(x => x.Id == l.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LineOrder, order));
            }
        }
        catch (Exception ex)
        {
            // ลำดับเป็นเรื่องการแสดงผลล้วน — ดึงเอกสารสำเร็จไปแล้ว ห้าม throw ทับ
            System.Diagnostics.Trace.TraceWarning(
                $"NormalizeReportLineOrder failed for report {report.Id}: {ex.Message}");
        }

        // อ่านรายงานใหม่จาก DB เพื่อคืนสถานะล่าสุดให้ UI
        var fresh = await _db.TaxReports.AsNoTracking()
            .Include(r => r.Lines)
            .FirstAsync(r => r.Id == report.Id && r.CompanyId == companyId);
        return MapToResponse(fresh);
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

        // ===== Snapshot ก่อนลบ — state ที่ผู้ใช้ทำมือแล้ว regen ห้ามหายเงียบ =====
        // (1) ติ๊ก "ใช้" บนบรรทัดที่มี DocumentId (carry-forward/pull/ยกมามาช้า)
        // (2) บรรทัดที่ pull ด้วยมือ (จะ re-apply ถ้ารอบใหม่ไม่มี)
        // B4: snapshot ติ๊กของ **ทุกชนิดรายงาน** — เดิมทำเฉพาะ VAT ⇒ ภ.ง.ด.
        // ที่นักบัญชีติ๊กบรรทัดเตือน "ยังไม่ออกหนังสือรับรอง" กลับเข้ามือ
        // (ตั้งใจนำส่งก่อนออกใบ) เสียงานทุกครั้งที่กด "สร้างใหม่"
        var tickSnapshot = existing.Lines.Where(l => l.DocumentId.HasValue && !l.IsExcluded)
            .Select(l => l.DocumentId!.Value).ToHashSet();
        // ฟิลด์ audit/สถานะการยื่นที่ "ไม่ใช่ผลของการคำนวณ" — สร้างรายงานใหม่
        // ต้องไม่ลบร่องรอยการยื่น/ถูกปฏิเสธ/RD ack (เส้น ปลดล็อก → แก้ →
        // สร้างใหม่ เคยล้างทิ้งหมดรวมทั้งตัวชี้ JE กลับรายการ)
        var keepNotes = existing.Notes;
        var keepEfAt = existing.EFilingExportedAt;
        var keepEfRef = existing.EFilingReferenceNumber;
        var keepRdAck = existing.RdAckNumber;
        var keepRdAckAt = existing.RdAcknowledgedAt;
        var keepRdStatus = existing.RdSubmissionStatus;
        var keepRdReject = existing.RdRejectionReason;
        var keepRdDoc = existing.RdAcknowledgementDocumentUrl;
        var keepRejReason = existing.RejectionReason;
        var keepRejAt = existing.RejectedAt;
        var keepRejBy = existing.RejectedBy;
        var keepReversalJe = existing.ReversalJournalEntryId;
        var pulledSnapshot = existing.TaxType == TaxType.VAT
            ? existing.Lines.Where(l => l.DocumentId.HasValue
                    && (l.Description ?? "").StartsWith("[ดึงเข้างวด"))
                .Select(l => new
                {
                    DocumentId = l.DocumentId!.Value, l.TaxPayerId, l.TaxPayerName,
                    l.TransactionDate, l.Description, l.IncomeAmount, l.TaxRate,
                    l.TaxAmount, l.IncomeTypeCode, l.IsExcluded
                }).ToList()
            : null;

        _db.TaxReportLines.RemoveRange(existing.Lines);
        _db.TaxReports.Remove(existing);
        // VatDeferral ที่ถูก claim โดยรายงานใบนี้ → ปลด claim (ไม่งั้น generate
        // รอบใหม่ filter ClaimedAt==null ไม่เจอ = ยอดเลื่อนเข้าหายถาวร +
        // ClaimedTaxReportId ชี้รายงานที่ถูกลบไปแล้ว)
        var claimedDeferrals = await _db.VatDeferrals
            .Where(d => d.CompanyId == companyId && d.ClaimedTaxReportId == reportId)
            .ToListAsync();
        foreach (var vd in claimedDeferrals) { vd.ClaimedAt = null; vd.ClaimedTaxReportId = null; }
        await _db.SaveChangesAsync();

        var response = await GenerateTaxReportAsync(companyId, request);

        // ===== คืนฟิลด์ audit/สถานะการยื่นที่ไม่เกี่ยวกับการคำนวณ (B4) =====
        var freshForAudit = await _db.TaxReports
            .FirstOrDefaultAsync(r => r.Id == response.Id && r.CompanyId == companyId);
        if (freshForAudit != null)
        {
            freshForAudit.Notes = keepNotes;
            freshForAudit.EFilingExportedAt = keepEfAt;
            freshForAudit.EFilingReferenceNumber = keepEfRef;
            freshForAudit.RdAckNumber = keepRdAck;
            freshForAudit.RdAcknowledgedAt = keepRdAckAt;
            freshForAudit.RdSubmissionStatus = keepRdStatus;
            freshForAudit.RdRejectionReason = keepRdReject;
            freshForAudit.RdAcknowledgementDocumentUrl = keepRdDoc;
            freshForAudit.RejectionReason = keepRejReason;
            freshForAudit.RejectedAt = keepRejAt;
            freshForAudit.RejectedBy = keepRejBy;
            freshForAudit.ReversalJournalEntryId = keepReversalJe;
            await _db.SaveChangesAsync();
        }

        // ===== Re-apply state ผู้ใช้บนรายงานรอบใหม่ =====
        if (tickSnapshot.Count > 0 || pulledSnapshot is { Count: > 0 })
        {
            var fresh = await _db.TaxReports.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == response.Id && r.CompanyId == companyId);
            if (fresh != null)
            {
                var changed = false;
                // คืนติ๊ก "ใช้" — ยกเว้นบรรทัดเตือน/ต้องห้าม (🚫/⚠️/รอใบกำกับ)
                foreach (var line in fresh.Lines)
                {
                    if (!line.DocumentId.HasValue || !line.IsExcluded) continue;
                    if (!tickSnapshot.Contains(line.DocumentId.Value)) continue;
                    // ข้ามบรรทัดต้องห้าม **เฉพาะ ภ.พ.30** — guard ฝั่ง server ที่
                    // ปฏิเสธการติ๊กก็ผูกกับ VAT เท่านั้น. ภ.ง.ด. แถว "⚠️ ยังไม่ออก
                    // หนังสือรับรอง" ติ๊กกลับเข้ามือได้ตามเจตนา (A2) ⇒ regenerate
                    // ต้องคืนให้ ไม่งั้นงานที่ตั้งใจติ๊กหายทุกครั้งที่กดสร้างใหม่
                    var dsc = line.Description ?? "";
                    if (existing.TaxType == TaxType.VAT
                        && (dsc.StartsWith("🚫") || dsc.StartsWith("⚠️") || dsc.StartsWith("[รอใบกำกับ"))) continue;
                    line.IsExcluded = false;
                    changed = true;
                }
                // คืนบรรทัด pull ที่รอบใหม่ไม่มี (เอกสารยัง active + ยังไม่ถูกใช้งวดอื่น)
                if (pulledSnapshot != null)
                {
                    var freshDocIds = fresh.Lines.Where(l => l.DocumentId.HasValue)
                        .Select(l => l.DocumentId!.Value).ToHashSet();
                    foreach (var p in pulledSnapshot)
                    {
                        if (freshDocIds.Contains(p.DocumentId)) continue;
                        var stillOk = await _db.Documents.AsNoTracking().AnyAsync(d =>
                            d.Id == p.DocumentId && d.CompanyId == companyId && !d.IsDeleted
                            && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft);
                        var claimedElse = await _db.TaxReportLines.AsNoTracking().AnyAsync(l =>
                            l.DocumentId == p.DocumentId && !l.IsExcluded
                            && l.TaxReportId != fresh.Id
                            && l.TaxReport.CompanyId == companyId && l.TaxReport.TaxType == TaxType.VAT);
                        if (!stillOk || claimedElse) continue;
                        fresh.Lines.Add(new TaxReportLine
                        {
                            TaxReportId = fresh.Id,
                            LineOrder = (fresh.Lines.Count == 0 ? 0 : fresh.Lines.Max(l => l.LineOrder)) + 1,
                            TaxPayerId = p.TaxPayerId, TaxPayerName = p.TaxPayerName,
                            TransactionDate = p.TransactionDate, Description = p.Description,
                            IncomeAmount = p.IncomeAmount, TaxRate = p.TaxRate, TaxAmount = p.TaxAmount,
                            DocumentId = p.DocumentId, IncomeTypeCode = p.IncomeTypeCode,
                            IsExcluded = p.IsExcluded,
                        });
                        changed = true;
                    }
                }
                if (changed)
                {
                    RecalcVatTotals(fresh);
                    NormalizeReportLineOrder(fresh);   // §87 — บรรทัด pull ที่คืนมาต้องเข้าลำดับวันที่
                    fresh.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return MapToResponse(fresh);
                }
            }
        }
        return response;
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
        // ปลด claim ของ VatDeferral ที่ผูกกับรายงานนี้ (เหมือน Regenerate) —
        // ไม่งั้นยอดเลื่อนเข้าหายถาวรเมื่อสร้างรายงานงวดนี้ใหม่
        var delDeferrals = await _db.VatDeferrals
            .Where(d => d.CompanyId == companyId && d.ClaimedTaxReportId == reportId)
            .ToListAsync();
        foreach (var vd in delDeferrals) { vd.ClaimedAt = null; vd.ClaimedTaxReportId = null; }
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
                    // ปลด claim VatDeferral ของรายงานที่จะลบ — เหมือน Regenerate/
                    // Delete (เดิม AutoRefresh ไม่ปลด ⇒ ยอดเลื่อนเข้า "หายถาวร":
                    // generate รอบใหม่ filter ClaimedAt==null ไม่เจอ +
                    // ClaimedTaxReportId ชี้รายงานที่ถูกลบไปแล้ว)
                    var arDeferrals = await _db.VatDeferrals
                        .Where(d => d.CompanyId == companyId && d.ClaimedTaxReportId == existing.Id)
                        .ToListAsync();
                    foreach (var vd in arDeferrals) { vd.ClaimedAt = null; vd.ClaimedTaxReportId = null; }
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
        r.Notes,
        // echo CitAmount กลับด้วย (กฎ "เก็บแล้วต้อง echo กลับ") — UI/รายงานจะได้
        // แยกยอด CIT ออกจากยอดหัก ณ ที่จ่ายได้โดยไม่ต้องเดาจากชนิดรายงาน
        CitAmount: r.CitAmount);
}
