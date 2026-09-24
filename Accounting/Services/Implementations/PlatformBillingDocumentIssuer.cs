using Accounting.Data;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ออกเอกสารค่าบริการ SaaS ผ่าน <b>tenant ของผู้ให้บริการเอง</b>
/// (ACCOUNT_STRUCTURE §6.1) — บริษัทผู้ให้บริการเป็นลูกค้าของระบบตัวเอง
///
/// <para><b>ทำไมต้องมี:</b> เดิม <see cref="SaasBillingDocumentService"/> ปั้น PDF
/// เดี่ยว ๆ พร้อมเลขรันของตัวเอง (RCPT-yyyyMM-####) ⇒ (1) รายได้ค่าบริการไม่เคยลง
/// GL ของบริษัทเรา (2) ไม่เข้ารายงานภาษีขาย/ภ.พ.30 — ผิดกฎหมายถ้าจด VAT
/// (3) ออก e-Tax ไม่ได้ (4) เลขไม่ได้อยู่ในชุด gap-free ตาม §86/4. เส้นนี้ทำให้
/// ทุกบาทที่เก็บได้กลายเป็นเอกสารจริง + JE จริง โดยไม่ต้องเขียนระบบบิลใหม่</para>
///
/// <para><b>กฎเหล็ก:</b> ห้าม throw ขึ้นไปทำให้การอนุมัติเงินพัง — ล้มเหลวคืน null
/// แล้วให้ผู้เรียกตกกลับไปโหมด PDF เดิม (เงินเข้าแล้วต้องบันทึกได้เสมอ)</para></summary>
public interface IPlatformBillingDocumentIssuer
{
    /// <summary>ออก "ใบกำกับภาษี/ใบเสร็จรับเงิน" (ขายเงินสด) ใน tenant ผู้ให้บริการ
    /// สำหรับเงินค่าบริการที่รับแล้ว — คืน null ถ้ายังไม่ได้ตั้งค่า tenant/ทำไม่สำเร็จ</summary>
    Task<PlatformDocResult?> IssuePaidReceiptAsync(SubscriptionPayment payment, Company buyer);

    /// <summary>ออก "ใบแจ้งหนี้" ต่ออายุล่วงหน้า (ยังไม่รับเงิน) ใน tenant ผู้ให้บริการ</summary>
    Task<PlatformDocResult?> IssueRenewalInvoiceAsync(Guid buyerCompanyId, string description,
        decimal amountNet, DateTime issueDate, DateTime dueDate, string reference);

    /// <summary>ออก "ใบแจ้งหนี้ค่าใช้งานตามจริง" หลายบรรทัดในใบเดียว
    /// (ค่าเหมา add-on · เอกสารเกินโควตา · การเข้าพัก · SMS ฯลฯ ของงวดนั้น)
    ///
    /// <para>ทำไมต้องหลายบรรทัด: ถ้ายุบเป็นบรรทัดเดียว "ค่าบริการเดือน ก.ย." ลูกค้า
    /// จะไม่มีทางรู้ว่ายอดมาจากอะไร แล้วทุกครั้งที่ยอดขยับต้องโทรถาม — ใบกำกับคือ
    /// คำอธิบายค่าใช้จ่าย ไม่ใช่แค่ตัวเลขรวม</para></summary>
    Task<PlatformDocResult?> IssueUsageInvoiceAsync(Guid buyerCompanyId, IReadOnlyList<PlatformInvoiceLine> lines,
        DateTime issueDate, DateTime dueDate, string reference, string? notes = null);

    /// <summary>tenant ผู้ให้บริการถูกตั้งค่าไว้แล้วหรือยัง (ใช้เลือกเส้นทาง)</summary>
    Task<bool> IsEnabledAsync();
}

public sealed record PlatformDocResult(Guid DocumentId, string DocumentNumber, bool IsTaxInvoice);

/// <summary>1 บรรทัดบนใบแจ้งหนี้ค่าใช้งาน — <paramref name="AmountNet"/> คือยอดรวม
/// ของบรรทัดนั้นตามที่เก็บใน <c>UsageEvent.ChargedAmount</c> (คิดมาแล้ว ไม่คิดใหม่:
/// ราคาถูก snapshot ไว้ตอนเกิดเหตุการณ์ การคำนวณซ้ำจะได้คนละยอดเมื่อราคาเปลี่ยน)</summary>
public sealed record PlatformInvoiceLine(string Description, decimal AmountNet, string Unit = "รายการ", decimal Quantity = 1m);

public class PlatformBillingDocumentIssuer : IPlatformBillingDocumentIssuer
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<PlatformBillingDocumentIssuer> _logger;
    /// <summary>resolve <see cref="IDocumentService"/> ตอนเรียกใช้ ไม่ใช่ตอน ctor
    /// — inject ตรง ๆ จะเกิดวงกลม (DI ปฏิเสธตั้งแต่ตอน start):
    /// <c>IDocumentService → ISubscriptionService → ISaasBillingDocumentService
    /// → IPlatformBillingDocumentIssuer → IDocumentService</c>
    /// (pattern เดียวกับ <see cref="ApprovalService"/> ที่เจอวงกลมแบบเดียวกัน).
    /// ทุกตัวเป็น Scoped และ resolve จาก provider ของ scope เดิม ⇒ ได้ instance
    /// เดียวกับที่ request นี้ใช้อยู่ (DbContext/transaction เดียวกัน)</summary>
    private readonly IServiceProvider _services;

    public PlatformBillingDocumentIssuer(AccountingDbContext db, IServiceProvider services,
        ILogger<PlatformBillingDocumentIssuer> logger)
    { _db = db; _services = services; _logger = logger; }

    private IDocumentService Documents =>
        (IDocumentService)_services.GetService(typeof(IDocumentService))!;

    private const string Actor = "platform-billing";

    public async Task<bool> IsEnabledAsync()
    {
        var s = await LoadSettingsAsync();
        return s?.PlatformCompanyId != null;
    }

    private Task<SiteSettings?> LoadSettingsAsync() => _db.SiteSettings.AsNoTracking()
        .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();

    public async Task<PlatformDocResult?> IssuePaidReceiptAsync(SubscriptionPayment payment, Company buyer)
    {
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null) return null;

            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            // ยอดที่ได้รับจริง = payment.Amount (สุทธิหลังถูกหักภาษี ณ ที่จ่าย)
            // ยอดตามใบกำกับ = สุทธิ + WHT ที่ลูกค้าหักไว้ — ต้องออกใบเต็มยอด
            // ไม่งั้นรายได้ต่ำไปและเครดิตภาษี 3% ที่ควรได้คืนหายไป
            var net = payment.Amount;
            var wht = payment.WithholdingTaxAmount;
            var grossBeforeWht = decimal.Round(net + wht, 2, MidpointRounding.AwayFromZero);
            if (grossBeforeWht <= 0m) return null;   // ยกเว้นค่าบริการ (0 บาท) ไม่ต้องออกใบ

            var isVat = settings.PlatformIsVatRegistered && IsValidTaxId(settings.PlatformSellerTaxId);
            // ฐานก่อน VAT: ถ้าราคาที่ตั้งไว้รวม VAT แล้ว ต้องถอดออกก่อน เพราะบรรทัด
            // เอกสารเก็บราคา ex-VAT (PricesIncludeVat ของ document ใช้เส้นทางคนละแบบ)
            var baseAmount = isVat && settings.PlatformPriceIncludesVat
                ? decimal.Round(grossBeforeWht * 100m / 107m, 2, MidpointRounding.AwayFromZero)
                : grossBeforeWht;
            // อัตรา WHT คำนวณกลับจากยอดจริง — ให้ยอดหักตรงกับที่ลูกค้าหักเป๊ะ
            // (กันเศษสตางค์จากการปัดอัตรา 3.00%)
            var whtRate = wht > 0m && baseAmount > 0m
                ? decimal.Round(wht / baseAmount * 100m, 4, MidpointRounding.AwayFromZero) : 0m;

            var desc = BuildServiceDescription(payment, buyer);
            var req = new CreateDocumentRequest(
                DocumentType: isVat ? DocumentType.TaxInvoice : DocumentType.Receipt,
                DocumentDate: payment.PaymentDate == default ? DateTime.UtcNow : payment.PaymentDate,
                DueDate: null,
                ContactId: contactId.Value,
                Reference: payment.PaymentNumber,
                Notes: $"ค่าบริการระบบบัญชีออนไลน์ · {payment.PaymentNumber}"
                     + (string.IsNullOrWhiteSpace(payment.TransferReference) ? "" : $" · อ้างอิงโอน {payment.TransferReference}"),
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: desc, Quantity: 1m, Unit: "รายการ",
                        UnitPrice: baseAmount, DiscountPercent: 0m,
                        VatRate: isVat ? 7m : 0m, WithholdingTaxRate: whtRate,
                        AccountId: null,
                        AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                            ? "41000" : settings.PlatformRevenueAccountCode)
                },
                // เงินรับแล้ว → ออกใบเดียวจบแบบขายเงินสด (Dr เงินสด/ธนาคาร ไม่ตั้งลูกหนี้)
                // ใบกำกับ = T03 หัว "ใบเสร็จรับเงิน/ใบกำกับภาษี". Receipt ธรรมดา
                // (ไม่จด VAT) ก็ลง Dr เงินสด / Cr รายได้ อยู่แล้ว
                IssuedAsCashReceipt: isVat ? true : null,
                PaymentAccountId: await ResolveCashAccountIdAsync(tenantId.Value, settings));

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor, true);
            _logger.LogInformation(
                "ออกเอกสารค่าบริการใน tenant ผู้ให้บริการ {DocNo} (payment {PaymentNo} · ฐาน {Base} · VAT {Vat} · WHT {Wht})",
                approved.DocumentNumber, payment.PaymentNumber, baseAmount, isVat, wht);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            // เงินเข้าแล้วต้องบันทึกได้เสมอ — ล้มตรงนี้ให้ตกกลับไปโหมด PDF เดิม
            _logger.LogError(ex, "ออกเอกสารค่าบริการผ่าน tenant ไม่สำเร็จ (payment {PaymentId}) — ใช้โหมด PDF เดิมแทน",
                payment.Id);
            return null;
        }
    }

    public async Task<PlatformDocResult?> IssueRenewalInvoiceAsync(Guid buyerCompanyId, string description,
        decimal amountNet, DateTime issueDate, DateTime dueDate, string reference)
    {
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null || amountNet <= 0m) return null;

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == buyerCompanyId);
            if (buyer == null) return null;
            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            var isVat = settings.PlatformIsVatRegistered && IsValidTaxId(settings.PlatformSellerTaxId);
            var baseAmount = isVat && settings.PlatformPriceIncludesVat
                ? decimal.Round(amountNet * 100m / 107m, 2, MidpointRounding.AwayFromZero)
                : amountNet;

            // ใบแจ้งหนี้ = ยังไม่รับเงิน ⇒ ไม่ระบุ WHT (ลูกค้าหักตอนจ่ายจริง
            // ระบบจะบันทึกตอนออกใบเสร็จ) และไม่ใช่ IssuedAsCashReceipt
            var req = new CreateDocumentRequest(
                DocumentType: DocumentType.Invoice,
                DocumentDate: issueDate,
                DueDate: dueDate,
                ContactId: contactId.Value,
                Reference: reference,
                Notes: "ใบแจ้งหนี้ค่าบริการต่ออายุระบบบัญชีออนไลน์",
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: description, Quantity: 1m, Unit: "รายการ",
                        UnitPrice: baseAmount, DiscountPercent: 0m,
                        VatRate: isVat ? 7m : 0m, WithholdingTaxRate: 0m,
                        AccountId: null,
                        AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                            ? "41000" : settings.PlatformRevenueAccountCode)
                });

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor, true);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ออกใบแจ้งหนี้ต่ออายุผ่าน tenant ไม่สำเร็จ (company {CompanyId})", buyerCompanyId);
            return null;
        }
    }

    public async Task<PlatformDocResult?> IssueUsageInvoiceAsync(Guid buyerCompanyId,
        IReadOnlyList<PlatformInvoiceLine> lines, DateTime issueDate, DateTime dueDate,
        string reference, string? notes = null)
    {
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null) return null;

            // บรรทัดยอด 0 ถูกตัดทิ้ง (โควตาฟรีกลืนไปแล้ว) — แต่ถ้าตัดจนไม่เหลือ
            // แปลว่าไม่มีอะไรต้องเก็บเงิน **ไม่ใช่**ออกใบเปล่า (§86/4 ห้ามใบไม่มีบรรทัด)
            var billable = lines.Where(l => l.AmountNet > 0m).ToList();
            if (billable.Count == 0) return null;

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == buyerCompanyId);
            if (buyer == null) return null;
            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            var isVat = settings.PlatformIsVatRegistered && IsValidTaxId(settings.PlatformSellerTaxId);
            var revenueCode = string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                ? "41000" : settings.PlatformRevenueAccountCode;

            var docLines = billable.Select(l =>
            {
                // ถอด VAT ต่อบรรทัด — ปัดที่ระดับบรรทัดเหมือนเส้นทางอื่นของไฟล์นี้
                // (ถอดจากยอดรวมแล้วเฉลี่ยกลับ จะได้เศษไม่ตรงกับที่แสดงในแต่ละบรรทัด)
                var baseAmount = isVat && settings.PlatformPriceIncludesVat
                    ? decimal.Round(l.AmountNet * 100m / 107m, 2, MidpointRounding.AwayFromZero)
                    : l.AmountNet;
                var qty = l.Quantity > 0m ? l.Quantity : 1m;
                return new DocumentLineRequest(
                    Description: l.Description,
                    Quantity: qty,
                    Unit: l.Unit,
                    UnitPrice: decimal.Round(baseAmount / qty, 4, MidpointRounding.AwayFromZero),
                    DiscountPercent: 0m,
                    VatRate: isVat ? 7m : 0m,
                    WithholdingTaxRate: 0m,
                    AccountId: null,
                    AccountCode: revenueCode);
            }).ToList();

            var req = new CreateDocumentRequest(
                DocumentType: DocumentType.Invoice,
                DocumentDate: issueDate,
                DueDate: dueDate,
                ContactId: contactId.Value,
                Reference: reference,
                Notes: notes ?? "ใบแจ้งหนี้ค่าใช้งานตามจริงของรอบบิล",
                Lines: docLines);

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor, true);
            _logger.LogInformation("ออกใบแจ้งหนี้ค่าใช้งาน {DocNo} ({Lines} บรรทัด · อ้างอิง {Ref})",
                approved.DocumentNumber, docLines.Count, reference);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ออกใบแจ้งหนี้ค่าใช้งานไม่สำเร็จ (company {CompanyId} · อ้างอิง {Ref})",
                buyerCompanyId, reference);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────

    private async Task<(SiteSettings? Settings, Guid? TenantId)> ResolveTenantAsync()
    {
        var s = await LoadSettingsAsync();
        if (s?.PlatformCompanyId == null) return (s, null);
        var alive = await _db.Companies.AsNoTracking()
            .AnyAsync(c => c.Id == s.PlatformCompanyId.Value && !c.IsDeleted);
        if (!alive)
        {
            _logger.LogWarning("tenant ผู้ให้บริการที่ตั้งไว้ ({Id}) ไม่มีอยู่/ถูกลบ — ข้ามการออกเอกสารจริง",
                s.PlatformCompanyId);
            return (s, null);
        }
        return (s, s.PlatformCompanyId);
    }

    /// <summary>หา/สร้าง Contact ของบริษัทลูกค้าในผังผู้ติดต่อของ tenant ผู้ให้บริการ
    ///
    /// <para>จับคู่ด้วย <b>เลขผู้เสียภาษี</b> ก่อน (ตัวตนตามกฎหมาย) แล้วค่อยชื่อ —
    /// กันสร้างผู้ติดต่อซ้ำทุกครั้งที่ลูกค้าจ่ายเงิน ซึ่งจะทำให้รายงานลูกหนี้/
    /// ยอดขายรายลูกค้าของเราเองแตกเป็นหลายราย</para></summary>
    private async Task<Guid?> EnsureContactAsync(Guid tenantId, Company buyer)
    {
        // ฝ่ายค้าน C-8: บริษัทที่สมัครใหม่มี TaxId = "-" (AuthService) — ค่าที่ไม่มีตัวเลขเลย = ไม่มีเลข ไม่ใช่กุญแจ ·
        // เดิม "-" ถูกเทียบตรงตัว ⇒ ลูกค้ารายที่สองที่ยังไม่กรอกเลขได้ผู้ติดต่อ (ชื่อ/ที่อยู่) ของรายแรก และแถวใหม่เก็บ "-" สะสม
        var taxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(buyer.TaxId) ? buyer.TaxId!.Trim() : null;
        var buyerBranch = string.IsNullOrWhiteSpace(buyer.BranchCode) ? "00000" : buyer.BranchCode;
        var buyerKey = buyer.Id.ToString();

        // (1) กุญแจแรก**เสมอ** = แถวที่ผูกกับ tenant นี้ไว้แล้ว (ExternalSystem/ExternalId ที่แถวใหม่ข้างล่างประทับเอง) — ฝ่ายค้านรอบสอง
        //     R2-C9: เดิมใช้เฉพาะกิ่ง "ไม่มีเลข" ⇒ tenant ที่บิลแรกตอนเลขยังเป็น "-" แล้วกรอกเลขทีหลัง ได้ผู้ติดต่อแถวที่สอง
        //     (ลูกหนี้/ประวัติใบกำกับแยกสองแถว แถวเก่าไม่เคยได้เลข) · ข้ามแถวนี้เฉพาะเมื่อมันถือเลข/สาขา**อื่น**แล้ว
        //     (ลูกค้าเปลี่ยนสาขาที่ลงทะเบียน ⇒ ต้องเป็นผู้ติดต่อของสาขาใหม่ §86/4)
        var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == tenantId && !c.IsDeleted
            && c.ExternalSystem == "NextAccTenant" && c.ExternalId == buyerKey);
        if (existing != null && taxId != null && Accounting.Helpers.ContactTaxBranchKey.HasTaxId(existing.TaxId)
            && !Accounting.Helpers.ContactTaxBranchKey.Pick(
                new[] { new Accounting.Helpers.ContactKeyCandidate(existing.Id, existing.TaxId, existing.BranchCode) },
                taxId, buyerBranch).Found)
            existing = null;
        var matchedBy = Accounting.Helpers.ContactMatchKind.ExternalKey;

        // (2) เลขภาษี + สาขา (คำตัดสินเจ้าของข้อ 20) — ใบกำกับค่าบริการต้องออกในนามสาขาที่ลูกค้าลงทะเบียนไว้ (§86/4)
        var key = default(Accounting.Helpers.ContactKeyMatch);
        if (existing == null && taxId != null)
        {
            key = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, tenantId, taxId, buyerBranch);
            if (key.ContactId is Guid keyId)
                existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == tenantId && !c.IsDeleted);
            matchedBy = Accounting.Helpers.ContactMatchKind.TaxKey;
        }

        // (3) ชื่อ — บนชุด SoftScope (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้าม) และไม่เอาแถวที่ผูกกับ tenant อื่น
        //     (ชื่อซ้ำข้ามลูกค้าที่ยังไม่กรอกเลข = คนละราย)
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, tenantId, taxId, key);
        if (existing == null && softScope != null)
        {
            matchedBy = Accounting.Helpers.ContactMatchKind.ExactName;
            existing = await softScope
                .Where(c => !c.IsDeleted && c.Name == buyer.Name
                    && !(c.ExternalSystem == "NextAccTenant" && c.ExternalId != buyerKey))
                .OrderByDescending(c => c.IsCustomer)
                .FirstOrDefaultAsync();
        }

        // แถวที่ยังไม่มีเลข (บิลก่อนลูกค้ากรอกเลข) รับเลข + สาขาปัจจุบัน — ตัวช่วยเดียวของทุกทางเข้า (R2-C5/R2-C9) ·
        // แถวลูกค้าทั่วไป (walk-in) ไม่รับเลข ⇒ Reject = สร้างแถวใหม่ (ฝ่ายค้านรอบสาม B8)
        var adopt = Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(existing, taxId, buyerBranch, matchedBy);
        if (adopt == Accounting.Helpers.ContactAdoptOutcome.Reject) existing = null;

        if (existing != null)
        {
            var dirty = adopt == Accounting.Helpers.ContactAdoptOutcome.Adopted;
            // ผู้ติดต่อเดิมอาจถูกสร้างไว้เป็นผู้ขายอย่างเดียว — ต้องเป็นลูกค้าด้วย
            // ไม่งั้นสร้างเอกสารฝั่งขายไม่ผ่าน validation บทบาทคู่ค้า
            if (!existing.IsCustomer) { existing.IsCustomer = true; dirty = true; }
            // ผูกกุญแจ tenant ให้แถวที่จับได้ด้วยเลข/ชื่อ (ถ้ายังไม่ผูกกับระบบใด) — รอบหน้าเจอด้วยกุญแจ (1) ทันที
            if (string.IsNullOrWhiteSpace(existing.ExternalSystem) && string.IsNullOrWhiteSpace(existing.ExternalId))
            {
                existing.ExternalSystem = "NextAccTenant";
                existing.ExternalId = buyerKey;
                dirty = true;
            }
            if (dirty) await _db.SaveChangesAsync();
            return existing.Id;
        }

        var contact = new Contact
        {
            CompanyId = tenantId,
            Name = buyer.Name,
            TaxId = taxId,
            BranchCode = buyerBranch,
            ContactType = taxId != null && taxId.Length == 13 && taxId.StartsWith('0')
                ? ContactType.JuristicPerson : ContactType.Individual,
            IsCustomer = true,
            Address = buyer.Address,
            SubDistrict = buyer.SubDistrict,
            District = buyer.District,
            Province = buyer.Province,
            PostalCode = buyer.PostalCode,
            Phone = buyer.Phone,
            Email = buyer.Email,
            CountryCode = "TH",
            // ผูกกลับไปยัง tenant ลูกค้า — ตามรอยได้ว่าผู้ติดต่อรายนี้คือบริษัทไหนในระบบ
            ExternalSystem = "NextAccTenant",
            ExternalId = buyer.Id.ToString(),
            CreatedBy = Actor,
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();
        return contact.Id;
    }

    /// <summary>ผังเงินสด/ธนาคารที่จะรับเงินค่าบริการ — null = ให้ DocumentService
    /// ใช้ค่าเริ่มต้นของมันเอง (เงินสด)</summary>
    private async Task<Guid?> ResolveCashAccountIdAsync(Guid tenantId, SiteSettings s)
    {
        var code = s.PlatformCashAccountCode;
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == tenantId && a.AccountCode == code.Trim() && a.IsActive)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    private static string BuildServiceDescription(SubscriptionPayment payment, Company buyer)
    {
        var months = payment.RequestedPeriodMonths > 0 ? payment.RequestedPeriodMonths : 1;
        return $"ค่าบริการระบบบัญชีออนไลน์ แพ็กเกจ {payment.RequestedPlan} "
             + $"({months} เดือน) — {buyer.Name}";
    }

    private static bool IsValidTaxId(string? taxId)
        => !string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13 && taxId.All(char.IsDigit);
}
