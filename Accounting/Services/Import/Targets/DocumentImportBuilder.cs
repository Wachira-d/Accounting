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
        // ฝ่ายค้านรอบ ETL: ทางไปต่อต้องทำตามได้ทันที (ไฟล์ CSV เดิมที่มีชนิด Receipt/PaymentVoucher/ReceiptVoucher ถูกปฏิเสธตั้งแต่รอบ 205)
        DocumentType.Receipt or DocumentType.ReceiptVoucher or DocumentType.PaymentVoucher =>
            "ใบเสร็จ/ใบสำคัญออกจากการรับ/จ่ายชำระ ไม่ได้นำเข้าเป็นเอกสารเอง — (1) นำเข้าเอกสารตั้งหนี้เป็นชนิด \"ใบแจ้งหนี้\" (ฝั่งขาย) หรือ "
            + "\"ใบแจ้งหนี้ซื้อ\"/\"บันทึกค่าใช้จ่าย\" (ฝั่งซื้อ) แล้วอนุมัติ (2) นำเข้าการรับ/จ่ายเงินเป็นชนิด \"รับ/จ่ายชำระ\" "
            + "(คอลัมน์ เลขที่เอกสาร · วันที่ชำระ · จำนวนเงิน · วิธีชำระ) — ระบบออกใบเสร็จ/ใบสำคัญและลงบัญชีให้",
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
/// ฝ่ายค้านรอบ ETL: การตัดสินที่อาจถาม AI (ผู้ติดต่อชื่อกำกวม · ผังแนะนำ) ย้ายไป <see cref="PrepareAsync"/> (ขั้นตรวจ) — <see cref="BuildAsync"/> (ตอนลง/worker)
/// ใช้ผลที่แถวเก็บไว้ ไม่ถาม AI ·
/// ผังบัญชี: มีรหัส ⇒ <see cref="ImportAccountMatcher"/> · ฝั่งซื้อไม่มีรหัส ⇒ <c>IOcrAiAugmenter.SuggestGlAccountAsync</c> ที่ขั้นตรวจ (student-first ·
/// FeedbackId ไปที่ <c>GlAccountAiFeedbackId</c> ของบรรทัด ⇒ DocumentService ปิดลูปตอนผู้ใช้อนุมัติ/แก้) · ด่าน: รหัสที่ AI ตอบต้องอยู่ในผังบริษัท ·
/// kill-switch: AI ดับ ⇒ บรรทัดไม่มีผัง (DocumentService ใช้ผังตั้งต้นตอนอนุมัติ) + คำเตือน</para>
/// </summary>
public class DocumentImportBuilder : IDocumentImportBuilder
{
    /// <summary>ความมั่นใจขั้นต่ำของผังที่ AI/นักเรียนแนะนำ (CLAUDE.md กฎเหล็ก #1)</summary>
    public const decimal GlMinConfidence = 0.70m;
    /// <summary>คีย์ใน <see cref="ImportTargetRow.Hints"/>: ผังที่ขั้นตรวจแนะนำ · feedback ของ GL · "1" = AI ตอบจริง (ป้ายซื่อสัตย์)</summary>
    public const string HintGlCode = "glCode";
    public const string HintGlFeedback = "glFeedback";
    public const string HintGlUsedAi = "glAi";

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

        // ── ผู้ติดต่อ: คู่ที่ขั้นตรวจ/ผู้ใช้เลือกไว้ในแถว ก่อน · ไม่มี ⇒ ตัดสินแบบ local (ไม่ถาม AI ตอนลงข้อมูล — ฝ่ายค้านรอบ ETL) ──
        Guid? contactId = rows.FirstOrDefault(r => r.MatchChosenByUser && r.MatchedEntityId != null)?.MatchedEntityId
            ?? rows.Select(r => r.MatchedEntityId).FirstOrDefault(id => id != null);
        if (contactId is { } picked && !await ContactExistsAsync(companyId, picked, ct))
            contactId = null;
        if (contactId == null)
        {
            var pick = await ResolveContactAsync(companyId, rows, allowAi: false, ct);
            if (pick.PickedContactId is not Guid found || pick.Issues.Any(i => i.Severity == ImportIssueSeverity.Error))
                return new ImportDocumentBuild(null, pick.Issues.Where(i => i.Severity == ImportIssueSeverity.Error).DefaultIfEmpty(
                    Error("ContactName", "contact-not-found", "ไม่พบผู้ติดต่อของเอกสารนี้ — นำเข้าผู้ติดต่อก่อน หรือเลือกผู้ติดต่อในแถวนี้")).ToList(), original);
            contactId = found;
        }

        // ── บรรทัด ──
        var vatDefault = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct) ? 7m : 0m;
        var chart = await _accounts.LoadChartAsync(companyId, ct);
        var acctQueries = rows.Where(r => Text(r, "LineAccountCode") != null)
            .Select(r => new ImportAccountQuery(r.RowId.ToString("N"), Text(r, "LineAccountCode"), null)).ToList();
        // ตอนลงข้อมูลไม่ถาม AI (ฝ่ายค้านรอบ ETL) — ผังในไฟล์ตัดสินด้วยชั้น local (รหัสตรง/ความจำ) · ไม่ตรง = คำเตือน + ผังตั้งต้นตอนอนุมัติ
        var acctMatches = (await _accounts.ResolveAsync(companyId, acctQueries, new ImportResolveOptions(AllowAi: false), ct, chart))
            .ToDictionary(m => m.Key);

        var lines = new List<DocumentLineRequest>(rows.Count);
        foreach (var r in rows)
        {
            var desc = Text(r, "LineDescription");
            var qty = Number(r, "LineQuantity") ?? 1m;
            var price = Amount(r, "LineUnitPrice");
            if (desc == null || price == null)
            {
                issues.Add(Error(desc == null ? "LineDescription" : "LineUnitPrice", "line-incomplete",
                    $"แถว {r.RowNo}: ต้องมีรายการและราคาต่อหน่วย (§86/4 — ทุกบรรทัดต้องมีรายการ)", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                continue;
            }
            if (qty <= 0 || price < 0)
            {
                issues.Add(Error(qty <= 0 ? "LineQuantity" : "LineUnitPrice", "line-negative",
                    $"แถว {r.RowNo}: จำนวนต้องมากกว่า 0 และราคาไม่ติดลบ — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                continue;
            }

            Guid? accountId = null;
            // ผังที่ขั้นตรวจแนะนำ (นักเรียน/AI) ฝากไว้ในแถว — feedback ไปที่บรรทัดให้ DocumentService ปิดลูปด้วยผังสุดท้ายตอนอนุมัติ (แม้ผู้ใช้เปลี่ยนผังเอง)
            Guid? glFeedback = r.Hints != null && Guid.TryParse(r.Hints.GetValueOrDefault(HintGlFeedback), out var hintFb) ? hintFb : null;
            if (acctMatches.TryGetValue(r.RowId.ToString("N"), out var am))
            {
                if (am.AccountId.HasValue) accountId = am.AccountId;
                else issues.Add(Warning("LineAccountCode", "line-account-unresolved",
                    $"แถว {r.RowNo}: ไม่พบผังบัญชี \"{Text(r, "LineAccountCode")}\" ที่ตรงแน่นอน — ระบบใช้ผังตั้งต้นตอนอนุมัติ (แก้ได้ในเอกสารร่าง)",
                    am.Candidates.FirstOrDefault()?.Label.Split(' ')[0]));
            }
            else if (DocumentImportTypes.IsPurchase(type.Value))
                issues.Add(Info("LineAccountCode", "line-account-default",
                    $"แถว {r.RowNo}: ไม่ได้ระบุผังบัญชี — ระบบใช้ผังตั้งต้นตอนอนุมัติ (แก้ได้ในเอกสารร่าง)"));

            var productCode = Text(r, "LineProductCode");
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
                    issues.Add(Warning("LineProductCode", "line-product-unresolved",
                        $"แถว {r.RowNo}: ไม่พบสินค้ารหัส \"{productCode}\" — บันทึกเป็นบรรทัดข้อความ (ไม่ตัดสต็อก)"));
                    productCode = null;
                }
            }

            lines.Add(new DocumentLineRequest(
                Description: desc,
                Quantity: qty,
                Unit: Text(r, "LineUnit"),
                UnitPrice: price.Value,
                DiscountPercent: Number(r, "LineDiscountPercent") ?? 0m,
                VatRate: Number(r, "LineVatRate") ?? vatDefault,
                WithholdingTaxRate: Number(r, "LineWhtRate") ?? 0m,
                AccountId: accountId,
                AccountCode: null,
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
            // อ้างอิงเพิ่มเติมของไฟล์ (ไม่ใช่เลขเดิม) ต่อท้ายหมายเหตุ — ช่อง Reference ของเอกสารเก็บเลขเดิมไว้แล้ว (ไม่ทิ้งข้อมูลเงียบ)
            Notes: Text(head, "Reference") is { } extraRef && extraRef != original
                ? string.Join(" · ", new[] { Text(head, "Notes"), $"อ้างอิงเพิ่มเติม: {extraRef}" }.Where(x => x != null))
                : Text(head, "Notes"),
            Lines: lines,
            SupplierInvoiceNumber: isPurchase && type.Value != DocumentType.PurchaseOrder && type.Value != DocumentType.PurchaseRequisition
                ? original : null,
            SupplierTaxInvoiceDate: isPurchase && type.Value is DocumentType.PurchaseInvoice or DocumentType.Expense ? date : null,
            PricesIncludeVat: Flag(head, "PricesIncludeVat") ?? false);
        return new ImportDocumentBuild(request, issues, original);
    }

    public async Task<ImportDocumentPreparation> PrepareAsync(Guid companyId, IReadOnlyList<ImportTargetRow> groupRows, bool allowAi,
        CancellationToken ct = default)
    {
        var rows = groupRows.OrderBy(r => r.RowNo).ToList();
        if (rows.Count == 0)
            return new ImportDocumentPreparation(new ImportDocumentContactPick(null, null, null, false, Array.Empty<ImportMatchCandidateDto>(), false, null,
                null, Array.Empty<ImportIssueDto>()), Array.Empty<ImportDocumentLineHint>());
        var contact = await ResolveContactAsync(companyId, rows, allowAi, ct);

        // ผังแนะนำของบรรทัดฝั่งซื้อที่ไม่มีผัง — ขั้นตรวจเท่านั้น (นักเรียนก่อน · AI เมื่อไม่มั่นใจ · ทางเดียวกับ OCR) · ที่เคยแนะนำไว้ใช้ซ้ำ ไม่ถามใหม่
        var hints = new List<ImportDocumentLineHint>();
        var head = rows[0];
        var type = DocumentImportTypes.Parse(Text(head, "DocumentType"));
        if (type is { } t && DocumentImportTypes.IsPurchase(t))
        {
            ImportAccountChart? chart = null;
            foreach (var r in rows.Where(r => r.Action != ImportRowAction.Skip && Text(r, "LineAccountCode") == null))
            {
                if (r.Hints?.GetValueOrDefault(HintGlCode) is { Length: > 0 } kept)
                {
                    hints.Add(new ImportDocumentLineHint(r.RowId, kept,
                        Guid.TryParse(r.Hints.GetValueOrDefault(HintGlFeedback), out var keptFb) ? keptFb : null,
                        r.Hints.GetValueOrDefault(HintGlUsedAi) == "1"));
                    continue;
                }
                var desc = Text(r, "LineDescription");
                var price = Amount(r, "LineUnitPrice");
                if (!allowAi || _ocrAi == null || desc == null || price == null) continue;
                chart ??= await _accounts.LoadChartAsync(companyId, ct);
                var qty = Number(r, "LineQuantity") ?? 1m;
                var gl = await _ocrAi.SuggestGlAccountAsync(companyId, r.RowId, Text(head, "ContactName"), Text(head, "ContactTaxId"), null, desc,
                    Math.Round(qty * price.Value, 2, MidpointRounding.AwayFromZero), "THB", null, 0m,
                    new Accounting.Services.Ai.GlLineContext(Text(r, "LineUnit"), qty, price, Date(head, "DocumentDate"), "Buyer"), ct);
                // ด่าน anti-hallucination: รหัสต้องเป็นผังลงยอดได้ของบริษัทนี้ + มั่นใจพอ (กฎเหล็ก #1)
                var hit = gl.HasModelAnswer ? chart.Accounts.FirstOrDefault(a => a.IsPostable && a.Code == gl.Answer!.Trim()) : null;
                if (hit != null && (gl.Confidence ?? 0m) >= GlMinConfidence)
                    hints.Add(new ImportDocumentLineHint(r.RowId, hit.Code, gl.FeedbackId, gl.UsedAi));
            }
        }
        return new ImportDocumentPreparation(contact, hints);
    }

    private Task<bool> ContactExistsAsync(Guid companyId, Guid contactId, CancellationToken ct)
        => _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted, ct);

    /// <summary>
    /// ผู้ติดต่อของเอกสาร (ฝ่ายค้านรอบ ETL P1-2) — ลำดับ: ผู้ใช้เลือกในแถว → คอลัมน์ ContactId → เลขภาษี+สาขา (กุญแจ — ตัวจับคู่กลาง) →
    /// ชื่อตรงตัว<b>ที่ไม่ซ้ำ</b>ในชุด <c>ContactTaxBranchKey.SoftScope</c>
    /// (พฤติกรรมเดิมของทางนำเข้าไฟล์ก่อนศูนย์นำเข้า · คำเตือนให้ผู้ใช้รับทราบ) → ไม่พบ = บล็อกพร้อมผู้สมัครให้เลือก · <b>ชื่อคล้ายอย่างเดียวไม่เลือกให้</b>
    /// (คำตัดสินข้อ 151 (5)) · เดิมทางนี้ตัดสินเฉพาะตอนลงข้อมูล ⇒ หน้าตรวจขึ้น "พร้อมลง" แล้วลงไม่สำเร็จทุกใบที่มีแต่ชื่อ โดยผู้ใช้ไม่มีทางเลือกผู้ติดต่อ
    /// </summary>
    private async Task<ImportDocumentContactPick> ResolveContactAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows, bool allowAi,
        CancellationToken ct)
    {
        var none = Array.Empty<ImportIssueDto>();
        var noCandidates = Array.Empty<ImportMatchCandidateDto>();
        var head = rows[0];
        if (rows.FirstOrDefault(r => r.MatchChosenByUser && r.MatchedEntityId != null)?.MatchedEntityId is Guid user
            && await ContactExistsAsync(companyId, user, ct))
            return new ImportDocumentContactPick(user, "User", 1m, false, noCandidates, false, null, null, none);
        if (GuidValue(head, "ContactId") is Guid given && await ContactExistsAsync(companyId, given, ct))
            return new ImportDocumentContactPick(given, "ContactId", 1m, true, noCandidates, false, null, null, none);

        var name = Text(head, "ContactName");
        var tax = Text(head, "ContactTaxId");
        var branch = Text(head, "ContactBranchCode");
        if (name == null && tax == null)
            return new ImportDocumentContactPick(null, null, null, false, noCandidates, false, null, null,
                new[] { Error("ContactName", "contact-missing", "ไม่มีชื่อหรือเลขผู้เสียภาษีของคู่ค้า — เติมคอลัมน์ผู้ติดต่อ หรือเลือกผู้ติดต่อในแถวนี้") });

        var probe = new ImportTargetRow(head.RowId, head.RowNo, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name, ["TaxId"] = tax, ["BranchCode"] = branch,
        }, ImportRowAction.Create);
        var match = (await _resolver.MatchAsync(companyId, "contacts", new[] { probe }, new ImportResolveOptions(AllowAi: allowAi), ct)).FirstOrDefault();
        var candidates = match?.Candidates ?? noCandidates;
        if (match?.EntityId is Guid keyed && match.AutoApply)
            return new ImportDocumentContactPick(keyed, match.Kind, match.Score, true, candidates, false, match.AiFeedbackId, match.AiAnswer, none);

        if (name != null)
        {
            // ชื่อตรงตัวที่ไม่ซ้ำ ภายในชุด SoftScope (เลขในไฟล์เป็นเลขใหม่ ⇒ เฉพาะผู้ติดต่อที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้ามถอยไปจับชื่อ)
            var taxKey = await ContactTaxBranchKey.FindAsync(_db.Contacts.AsNoTracking(), companyId, tax, branch, ct);
            var scope = ContactTaxBranchKey.SoftScope(_db.Contacts.AsNoTracking(), companyId, tax, taxKey);
            if (scope != null)
            {
                var hits = await scope.Where(c => !c.IsDeleted && c.Name == name).Select(c => c.Id).Take(2).ToListAsync(ct);
                if (hits.Count == 1)
                    return new ImportDocumentContactPick(hits[0], "ExactName", 0.9m, false, candidates, match?.UsedAi ?? false, match?.AiFeedbackId,
                        match?.AiAnswer, new[]
                        {
                            Warning("ContactName", "doc-contact-name-only",
                                $"ผูกเอกสารกับผู้ติดต่อ \"{name}\" ด้วยชื่อตรงตัว (ไม่มีเลขผู้เสียภาษียืนยัน) — ตรวจว่าเป็นรายเดียวกัน หรือเลือกผู้ติดต่อเองในแถวนี้"
                                + (tax != null ? $" · เลขผู้เสียภาษี {tax} ในไฟล์ยังไม่อยู่ในผู้ติดต่อนี้ — เพิ่มที่หน้าผู้ติดต่อ" : "")),
                        });
            }
        }
        var hint = candidates.FirstOrDefault() is { } cand ? $" (ใกล้เคียง: {cand.Label})" : "";
        return new ImportDocumentContactPick(null, match?.Kind, match?.Score, false, candidates, match?.UsedAi ?? false, match?.AiFeedbackId,
            match?.AiAnswer, new[]
            {
                Error("ContactName", "contact-not-found",
                    $"ไม่พบผู้ติดต่อ \"{name ?? tax}\" ที่ตรงแน่นอน{hint} — เลือกผู้ติดต่อจากรายการในแถวนี้ หรือนำเข้าผู้ติดต่อก่อน "
                    + "(ลำดับย้ายระบบ: ผู้ติดต่อ → เอกสาร) แล้ววิเคราะห์ใหม่"),
            });
    }
}
