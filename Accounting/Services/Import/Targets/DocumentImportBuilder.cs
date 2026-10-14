using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using static Accounting.Services.Import.Targets.ImportTargetValues;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ชนิดเอกสารที่นำเข้าได้ + ชื่อที่อ่านจากไฟล์** (รอบ 205 ทีม D) — ชื่อไทย/อังกฤษอ่านจากตารางเดียว <see cref="DocumentTypeNames"/> (ไม่มีสำเนาชื่อชนิดที่สอง)
///
/// <para>นำเข้าได้เฉพาะชนิดที่ "ร่าง" แล้วไม่ขยับเงิน/สต็อกเองตอนสร้าง: ใบเสนอราคา · ใบแจ้งหนี้ · ใบกำกับภาษี · ใบขอซื้อ · ใบสั่งซื้อ · ใบแจ้งหนี้ซื้อ ·
/// ใบบันทึกค่าใช้จ่าย · ชนิดอื่นมีทางไปต่อใน <see cref="UnsupportedWayForward"/> (ใบสำคัญจ่ายเงินสดถูกอนุมัติอัตโนมัติตอนสร้าง = ขัดกติกา "ร่างเสมอ" ·
/// ใบลด/เพิ่มหนี้ต้องอ้างใบเดิม §86/9–10)</para>
/// </summary>
public static class DocumentImportTypes
{
    public static readonly IReadOnlyList<DocumentType> Allowed = new[]
    {
        DocumentType.Quotation, DocumentType.Invoice, DocumentType.TaxInvoice,
        DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder, DocumentType.PurchaseInvoice, DocumentType.Expense,
    };

    /// <summary>ฝั่งซื้อ — บรรทัดที่ไม่มีผังบัญชีให้ระบบแนะนำผังค่าใช้จ่าย (OcrAiAugmenter.SuggestGlAccountAsync)</summary>
    public static bool IsPurchase(DocumentType t)
        => t is DocumentType.PurchaseRequisition or DocumentType.PurchaseOrder or DocumentType.PurchaseInvoice or DocumentType.Expense;

    /// <summary>อ่านชนิดจากข้อความในไฟล์: ชื่อ enum · ตัวเลข · ชื่อไทย/อังกฤษตามหัวกระดาษ (เทียบทั้งคำ ไม่สนตัวพิมพ์)</summary>
    public static DocumentType? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (Enum.TryParse<DocumentType>(s, ignoreCase: true, out var t) && Enum.IsDefined(t)) return t;
        foreach (var v in Enum.GetValues<DocumentType>())
            if (string.Equals(DocumentTypeNames.Title(v, "th"), s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(DocumentTypeNames.Title(v, "en"), s, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    public static string UnsupportedWayForward(DocumentType t) => t switch
    {
        DocumentType.Receipt or DocumentType.ReceiptVoucher or DocumentType.PaymentVoucher =>
            "นำเข้าเป็นใบแจ้งหนี้/ใบแจ้งหนี้ซื้อ แล้วบันทึกรับ/จ่ายชำระด้วยการนำเข้า \"รับ/จ่ายชำระ\"",
        DocumentType.CreditNote or DocumentType.DebitNote =>
            "ออกใบลดหนี้/ใบเพิ่มหนี้ที่หน้าเอกสารโดยอ้างใบเดิม (§86/9–86/10 ต้องอ้างเลขและวันที่ใบเดิม)",
        _ => $"ชนิดที่นำเข้าได้: {string.Join(" · ", Allowed.Select(a => DocumentTypeNames.Title(a, "th")))}",
    };
}

/// <summary>
/// **ประกอบคำขอสร้างเอกสาร (ร่าง) จากแถวนำเข้าหนึ่งกลุ่ม** — เจ้าของ <b>ทีม D</b> (สัญญาที่ <see cref="IDocumentImportBuilder"/> · รอบ 205)
///
/// <para>กติกา (คำตัดสินข้อ 151 (1)): เอกสารนำเข้า = <b>ร่าง + เลข DRAFT</b> (DocumentService ออกเลขเองตอนอนุมัติ) · เลขเดิมจากไฟล์ไป
/// <c>Reference</c> (ฝั่งซื้อไป <c>SupplierInvoiceNumber</c> ด้วย — เลขใบกำกับของผู้ขาย) · <b>ยอดรวม/VAT คำนวณโดย DocumentService</b> ไม่ใช่ที่นี่ ·
/// VAT ต่อบรรทัด · หัก ณ ที่จ่ายต่อบรรทัด · ผู้ติดต่อ/สินค้าหาด้วย <see cref="IImportEntityResolver"/> (ชื่อคล้ายอย่างเดียวไม่เลือกให้ — ข้อ 151 (5)) ·
/// ผังบัญชี: มีรหัส ⇒ <see cref="ImportAccountMatcher"/> · ฝั่งซื้อไม่มีรหัส ⇒ <c>IOcrAiAugmenter.SuggestGlAccountAsync</c> (student-first ·
/// FeedbackId ไปที่ <c>GlAccountAiFeedbackId</c> ของบรรทัด ⇒ DocumentService ปิดลูปตอนผู้ใช้อนุมัติ/แก้) · ด่าน: รหัสที่ AI ตอบต้องอยู่ในผังบริษัท ·
/// kill-switch: AI ดับ ⇒ บรรทัดไม่มีผัง (DocumentService ใช้ผังตั้งต้นตอนอนุมัติ) + คำเตือน</para>
/// </summary>
public class DocumentImportBuilder : IDocumentImportBuilder
{
    /// <summary>ความมั่นใจขั้นต่ำของผังที่ AI/นักเรียนแนะนำ (CLAUDE.md กฎเหล็ก #1)</summary>
    public const decimal GlMinConfidence = 0.70m;

    private readonly AccountingDbContext _db;
    private readonly IImportEntityResolver _resolver;
    private readonly ImportAccountMatcher _accounts;
    private readonly Accounting.Services.Ai.IOcrAiAugmenter? _ocrAi;

    public DocumentImportBuilder(AccountingDbContext db, IImportEntityResolver resolver, ImportAccountMatcher accounts,
        Accounting.Services.Ai.IOcrAiAugmenter? ocrAi = null)
    {
        _db = db;
        _resolver = resolver;
        _accounts = accounts;
        _ocrAi = ocrAi;
    }

    public async Task<ImportDocumentBuild> BuildAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> groupRows,
        ImportCommitOptions options, CancellationToken ct = default)
    {
        var issues = new List<ImportIssueDto>();
        var rows = groupRows.Where(r => r.Action != ImportRowAction.Skip).OrderBy(r => r.RowNo).ToList();
        if (rows.Count == 0)
            return new ImportDocumentBuild(null, new[] { Error(null, "doc-no-lines", "ไม่มีบรรทัดที่จะนำเข้า (ทุกแถวถูกข้าม)") });
        var head = rows[0];
        var original = Text(head, "DocumentNumber");

        // ── ชนิด ──
        var type = DocumentImportTypes.Parse(Text(head, "DocumentType"));
        if (type == null)
            return new ImportDocumentBuild(null, new[] { Error("DocumentType", "doc-type-missing",
                $"ไม่รู้ชนิดเอกสาร \"{Text(head, "DocumentType")}\" — {DocumentImportTypes.UnsupportedWayForward(DocumentType.Quotation)}") }, original);
        if (!DocumentImportTypes.Allowed.Contains(type.Value))
            return new ImportDocumentBuild(null, new[] { Error("DocumentType", "doc-type-unsupported",
                $"ยังไม่รองรับการนำเข้า{DocumentTypeNames.Title(type.Value, "th")} — {DocumentImportTypes.UnsupportedWayForward(type.Value)}") }, original);

        var date = Date(head, "DocumentDate");
        if (date == null)
            return new ImportDocumentBuild(null, new[] { Error("DocumentDate", "doc-date-missing", "ไม่มีวันที่เอกสาร — เติมวันที่แล้วนำเข้าอีกครั้ง") }, original);

        // ── ผู้ติดต่อ (เลขภาษี+สาขาก่อน · ชื่อคล้ายอย่างเดียว = แนะนำ ไม่เลือกให้) ──
        var contactId = GuidValue(head, "ContactId");
        var contactName = Text(head, "ContactName");
        var contactTax = Text(head, "ContactTaxId");
        if (contactId is { } picked && !await _db.Contacts.AnyAsync(c => c.Id == picked && c.CompanyId == companyId && !c.IsDeleted, ct))
            contactId = null;
        if (contactId == null)
        {
            if (contactName == null && contactTax == null)
                return new ImportDocumentBuild(null, new[] { Error("ContactName", "contact-missing",
                    "ไม่มีชื่อหรือเลขผู้เสียภาษีของคู่ค้า — เติมคอลัมน์ผู้ติดต่อ") }, original);
            var probe = new ImportTargetRow(head.RowId, head.RowNo, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = contactName, ["TaxId"] = contactTax, ["BranchCode"] = Text(head, "ContactBranchCode"),
            }, ImportRowAction.Create);
            var match = (await _resolver.MatchAsync(companyId, "contacts", new[] { probe }, new ImportResolveOptions(AllowAi: true), ct))
                .FirstOrDefault();
            if (match?.EntityId is { } cid && match.AutoApply) contactId = cid;
            else
            {
                var hint = match?.Candidates.FirstOrDefault() is { } cand ? $" (ใกล้เคียง: {cand.Label} {cand.Score:0.00})" : "";
                return new ImportDocumentBuild(null, new[] { Error("ContactName", "contact-not-found",
                    $"ไม่พบผู้ติดต่อ \"{contactName ?? contactTax}\" ที่ตรงแน่นอน{hint} — นำเข้าผู้ติดต่อก่อน (ลำดับย้ายระบบ: ผู้ติดต่อ → เอกสาร) หรือเลือกผู้ติดต่อในแถวนี้") }, original);
            }
        }

        // ── บรรทัด ──
        var vatDefault = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct) ? 7m : 0m;
        var chart = await _accounts.LoadChartAsync(companyId, ct);
        var acctQueries = rows.Where(r => Text(r, "AccountCode") != null)
            .Select(r => new ImportAccountQuery(r.RowId.ToString("N"), Text(r, "AccountCode"), null)).ToList();
        var acctMatches = (await _accounts.ResolveAsync(companyId, acctQueries, new ImportResolveOptions(AllowAi: true), ct, chart))
            .ToDictionary(m => m.Key);

        var lines = new List<DocumentLineRequest>(rows.Count);
        foreach (var r in rows)
        {
            var desc = Text(r, "Description");
            var qty = Number(r, "Quantity") ?? 1m;
            var price = Amount(r, "UnitPrice");
            if (desc == null || price == null)
            {
                issues.Add(Error(desc == null ? "Description" : "UnitPrice", "line-incomplete",
                    $"แถว {r.RowNo}: ต้องมีรายการและราคาต่อหน่วย (§86/4 — ทุกบรรทัดต้องมีรายการ)", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                continue;
            }
            if (qty <= 0 || price < 0)
            {
                issues.Add(Error(qty <= 0 ? "Quantity" : "UnitPrice", "line-negative",
                    $"แถว {r.RowNo}: จำนวนต้องมากกว่า 0 และราคาไม่ติดลบ — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                continue;
            }

            Guid? accountId = null;
            string? accountCode = null;
            Guid? glFeedback = null;
            if (acctMatches.TryGetValue(r.RowId.ToString("N"), out var am))
            {
                if (am.AccountId.HasValue) accountId = am.AccountId;
                else issues.Add(Warning("AccountCode", "line-account-unresolved",
                    $"แถว {r.RowNo}: ไม่พบผังบัญชี \"{Text(r, "AccountCode")}\" ที่ตรงแน่นอน — ระบบใช้ผังตั้งต้นตอนอนุมัติ (แก้ได้ในเอกสารร่าง)",
                    am.Candidates.FirstOrDefault()?.Label.Split(' ')[0]));
            }
            else if (DocumentImportTypes.IsPurchase(type.Value) && _ocrAi != null)
            {
                // ฝั่งซื้อไม่มีผัง ⇒ ทางเดียวกับ OCR (นักเรียนก่อน · AI เมื่อไม่มั่นใจ · FeedbackId ไปปิดลูปที่ DocumentService)
                var gl = await _ocrAi.SuggestGlAccountAsync(companyId, r.RowId, contactName, contactTax, null, desc,
                    Math.Round(qty * price.Value, 2, MidpointRounding.AwayFromZero), "THB", null, 0m,
                    new Accounting.Services.Ai.GlLineContext(Text(r, "Unit"), qty, price, date, "Buyer"), ct);
                var hit = gl.HasModelAnswer
                    ? chart.Accounts.FirstOrDefault(a => a.IsPostable && a.Code == gl.Answer!.Trim())
                    : null;
                if (hit != null && (gl.Confidence ?? 0m) >= GlMinConfidence)
                {
                    accountCode = hit.Code;
                    glFeedback = gl.FeedbackId;
                }
                else
                    issues.Add(Info("AccountCode", "line-account-default",
                        $"แถว {r.RowNo}: ไม่ได้ระบุผังบัญชี — ระบบใช้ผังตั้งต้นตอนอนุมัติ (แก้ได้ในเอกสารร่าง)"));
            }

            var productCode = Text(r, "ProductCode");
            if (productCode != null)
            {
                var probe = new ImportTargetRow(r.RowId, r.RowNo,
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Code"] = productCode, ["Name"] = desc },
                    ImportRowAction.Create);
                var pm = (await _resolver.MatchAsync(companyId, "products", new[] { probe }, new ImportResolveOptions(AllowAi: false), ct))
                    .FirstOrDefault();
                if (pm?.EntityId is { } pid && pm.AutoApply)
                    productCode = await _db.Products.AsNoTracking().Where(p => p.Id == pid && p.CompanyId == companyId)
                        .Select(p => p.Code).FirstOrDefaultAsync(ct) ?? productCode;
                else
                {
                    issues.Add(Warning("ProductCode", "line-product-unresolved",
                        $"แถว {r.RowNo}: ไม่พบสินค้ารหัส \"{productCode}\" — บันทึกเป็นบรรทัดข้อความ (ไม่ตัดสต็อก)"));
                    productCode = null;
                }
            }

            lines.Add(new DocumentLineRequest(
                Description: desc,
                Quantity: qty,
                Unit: Text(r, "Unit"),
                UnitPrice: price.Value,
                DiscountPercent: Number(r, "DiscountPercent") ?? 0m,
                VatRate: Number(r, "VatRate") ?? vatDefault,
                WithholdingTaxRate: Number(r, "WithholdingTaxRate") ?? 0m,
                AccountId: accountId,
                AccountCode: accountCode,
                GlAccountAiFeedbackId: glFeedback,
                ProductCode: productCode));
        }

        if (issues.Any(i => i.Severity == ImportIssueSeverity.Error) || lines.Count == 0)
        {
            if (lines.Count == 0 && issues.All(i => i.Severity != ImportIssueSeverity.Error))
                issues.Add(Error(null, "doc-no-lines", "ไม่มีบรรทัดที่นำเข้าได้"));
            return new ImportDocumentBuild(null, issues, original);
        }

        var isPurchase = DocumentImportTypes.IsPurchase(type.Value);
        var request = new CreateDocumentRequest(
            DocumentType: type.Value,
            DocumentDate: date.Value,
            DueDate: Date(head, "DueDate"),
            ContactId: contactId!.Value,
            // เลขเดิมจากไฟล์ = อ้างอิง (เลขของเราออกตอนอนุมัติ — §86/4 gap-free) · คำตัดสินข้อ 151 (1)
            Reference: original,
            Notes: Text(head, "Notes"),
            Lines: lines,
            SupplierInvoiceNumber: isPurchase && type.Value != DocumentType.PurchaseOrder && type.Value != DocumentType.PurchaseRequisition
                ? original : null,
            SupplierTaxInvoiceDate: isPurchase && type.Value is DocumentType.PurchaseInvoice or DocumentType.Expense ? date : null,
            PricesIncludeVat: Flag(head, "PricesIncludeVat") ?? false);
        return new ImportDocumentBuild(request, issues, original);
    }
}
