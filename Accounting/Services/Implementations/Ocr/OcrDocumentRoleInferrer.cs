using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Decides three related questions for every OCR scan:
///   1. ScannedDocumentType — what kind of paper did the user actually scan?
///   2. OurRole              — are we the buyer or seller in this document?
///   3. TargetDocumentType   — what document should we create in our books?
///
/// The need for this separation: Thai accounting workflow treats the source
/// paper and the bookkeeping entry as DIFFERENT things. A receipt from a
/// supplier (paper says "ใบเสร็จรับเงิน") should result in a PaymentVoucher
/// ("ใบสำคัญจ่าย") on our side — never a Receipt entity, because the receipt
/// already exists in the supplier's books, not ours.
///
/// Pure static — no dependencies, no DB. Inputs are the raw text + already-
/// extracted fields + company tax id (so we can detect whether we appear as
/// the buyer or the seller on the scanned paper).
/// </summary>
public static class OcrDocumentRoleInferrer
{
    public record InferenceResult(
        DocumentType? ScannedDocType,
        string OurRole,                 // "Buyer" | "Seller"
        DocumentType TargetDocType,
        decimal RoleConfidence,         // 0.0 — 1.0
        List<string> Reasons);

    public static InferenceResult Infer(
        string rawText,
        string? vendorTaxId,
        string? buyerTaxId,
        string? vendorName,
        string? buyerName,
        string? companyTaxId,
        string? companyName,
        DocumentType? previousScannedType = null,
        // When set, this overrides the default "PaymentVoucher" target
        // for the Buyer + TaxInvoice/Invoice case. Lets companies on
        // accrual-basis (A/P workflow) keep getting PurchaseInvoice
        // instead. See CompanySettings.OcrBuyerInvoiceDefaultTarget.
        DocumentType? buyerInvoiceDefaultTarget = null,
        // OCR-extracted credit terms ("เครดิต 30 วัน" / "Net 30"). > 0 means
        // the paper grants credit — i.e. NOT yet paid — which flips an
        // invoice's target from PaymentVoucher to PurchaseInvoice (ตั้งหนี้).
        int? paymentTermsDays = null)
    {
        var reasons = new List<string>();
        var text = (rawText ?? "").ToLowerInvariant();
        var companyTax = (companyTaxId ?? "").Trim();
        var companyNm = (companyName ?? "").Trim().ToLowerInvariant();

        // ─── Step 1: Determine our role ────────────────────────────────────
        string role = "Buyer";  // default — most scans are purchase-side
        decimal roleConf = 0.5m;
        if (!string.IsNullOrEmpty(companyTax))
        {
            if (TaxIdMatches(buyerTaxId, companyTax))
            {
                role = "Buyer";
                roleConf = 1.0m;
                reasons.Add($"เลขประจำตัวผู้ซื้อ ({buyerTaxId}) ตรงกับบริษัทเรา → role = Buyer");
            }
            else if (TaxIdMatches(vendorTaxId, companyTax))
            {
                role = "Seller";
                roleConf = 1.0m;
                reasons.Add($"เลขประจำตัวผู้ขาย ({vendorTaxId}) ตรงกับบริษัทเรา → role = Seller");
            }
        }
        if (roleConf < 1.0m && !string.IsNullOrEmpty(companyNm))
        {
            // Fuzzy name fallback when tax IDs aren't extracted or company hasn't
            // set its TaxId. Strip common Thai entity suffixes for matching.
            var buyerLow = (buyerName ?? "").Trim().ToLowerInvariant();
            var vendorLow = (vendorName ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(buyerLow) && NameOverlaps(buyerLow, companyNm))
            {
                role = "Buyer";
                roleConf = 0.75m;
                reasons.Add($"ชื่อผู้ซื้อใกล้เคียงกับบริษัทเรา → role = Buyer (fuzzy)");
            }
            else if (!string.IsNullOrEmpty(vendorLow) && NameOverlaps(vendorLow, companyNm))
            {
                role = "Seller";
                roleConf = 0.75m;
                reasons.Add($"ชื่อผู้ขายใกล้เคียงกับบริษัทเรา → role = Seller (fuzzy)");
            }
        }

        // ─── Step 1b: Phrase + position heuristics (kicks in when no tax/name match) ───
        // When tax IDs & names didn't pin down our role, look at how the
        // document itself describes the parties. Receipts almost always
        // show "ลูกค้า:" or "นามผู้ซื้อ:" labels — when the OCR'd buyer
        // name is also near that label, our role is almost certainly Buyer.
        // Similarly, the seller block is normally in the upper-left third
        // of the page (header zone) — when the vendor name was extracted
        // from there, that's another Buyer signal for us.
        if (roleConf < 0.75m && !string.IsNullOrEmpty(rawText))
        {
            (int buyerLabelPos, _) = FindRolePhrasePositions(rawText);

            // Heuristic 1 — WHT-cert presence biases Seller (we issued the
            // underlying invoice whose payment got withheld).
            var whtCertHint = ContainsAll(text, "หัก ณ ที่จ่าย", "รับรอง")
                          || text.Contains("withholding tax certificate");
            if (whtCertHint && role == "Buyer" && roleConf < 0.7m)
            {
                role = "Seller";
                roleConf = 0.65m;
                reasons.Add("พบ \"หนังสือรับรองการหักภาษี ณ ที่จ่าย\" → ลีน Seller (เราเป็นผู้ออกใบกำกับเดิม)");
            }

            // Heuristic 2 — position bias. Top-third of the document is the
            // header zone, which Thai/INTL receipt conventions reserve for
            // the issuer (seller). If the vendor name was extracted from
            // that zone AND the buyer name later, we're probably Buyer.
            if (roleConf < 0.7m && !string.IsNullOrEmpty(vendorName))
            {
                var vendorPos = rawText.IndexOf(vendorName!, StringComparison.OrdinalIgnoreCase);
                var topZone = rawText.Length / 3;     // upper third by char count
                if (vendorPos >= 0 && vendorPos < topZone)
                {
                    role = "Buyer";
                    roleConf = Math.Max(roleConf, 0.6m);
                    reasons.Add("ชื่อผู้ขายอยู่ในส่วนหัวเอกสาร (top 1/3) → ลีน Buyer (ของเรา)");
                }
            }

            // Heuristic 3 — explicit "ลูกค้า/Bill To" label near the
            // buyer name. If the buyer-side label exists AND the company
            // name we extracted as Buyer is within ~120 chars after it,
            // bump confidence — labels are an extremely strong signal.
            if (roleConf < 0.75m && buyerLabelPos >= 0 && !string.IsNullOrEmpty(buyerName))
            {
                var buyerPos = rawText.IndexOf(buyerName!, buyerLabelPos, StringComparison.OrdinalIgnoreCase);
                if (buyerPos >= 0 && buyerPos - buyerLabelPos <= 120)
                {
                    // Buyer label is present + close → that party is the
                    // buyer. We default to Buyer because most scanned
                    // docs are inbound; but if the buyer-label vicinity
                    // also fuzzy-matches our company name, lock in Buyer.
                    if (!string.IsNullOrEmpty(companyNm) && NameOverlaps(buyerName!.ToLowerInvariant(), companyNm))
                    {
                        role = "Buyer";
                        roleConf = Math.Max(roleConf, 0.85m);
                        reasons.Add("ป้าย \"ลูกค้า/Bill To\" ใกล้ชื่อบริษัทเรา → role = Buyer (label + fuzzy)");
                    }
                }
            }
        }

        if (roleConf < 0.7m)
            reasons.Add("ไม่สามารถยืนยันบทบาท → สมมุติเป็น Buyer (default — เอกสารส่วนใหญ่ที่สแกนเป็นฝั่งซื้อ)");

        // ─── Step 2: Detect markers on the paper ───────────────────────────
        var hasTaxInvoice = ContainsAny(text, "ใบกำกับภาษี", "tax invoice");
        var hasReceipt = ContainsAny(text, "ใบเสร็จรับเงิน", "ใบเสร็จ", "receipt");
        var hasInvoice = ContainsAny(text, "ใบแจ้งหนี้", "invoice");
        var hasCertInLieu = ContainsAny(text, "ใบรับรองแทนใบเสร็จ", "certificate in lieu");
        var hasCreditNote = ContainsAny(text, "ใบลดหนี้", "credit note");
        var hasDebitNote = ContainsAny(text, "ใบเพิ่มหนี้", "debit note");
        var hasPurchaseOrder = ContainsAny(text, "ใบสั่งซื้อ", "purchase order");
        var hasDeliveryNote = ContainsAny(text, "ใบส่งของ", "delivery note");
        var hasQuotation = ContainsAny(text, "ใบเสนอราคา", "quotation");
        var hasBillingNote = ContainsAny(text, "ใบวางบิล", "billing note");
        var hasWhtCert = ContainsAll(text, "หัก ณ ที่จ่าย", "รับรอง") || text.Contains("withholding tax certificate");
        // ── Markers that decide PAID-vs-UNPAID and EVIDENCE QUALITY ──
        // บิลเงินสด = informal cash bill. With a valid 13-digit vendor TaxId it
        // is acceptable expense evidence (→ PaymentVoucher); WITHOUT one, RD
        // deductibility requires a ใบรับรองแทนใบเสร็จ backing the payment.
        var hasCashBill = ContainsAny(text, "บิลเงินสด", "cash bill", "cash sale");
        // ใบกำกับภาษีอย่างย่อ (abbreviated tax invoice — retail/POS) is by
        // definition a paid-at-the-till document → PaymentVoucher.
        var hasAbbrevTaxInvoice = ContainsAny(text, "ใบกำกับภาษีอย่างย่อ", "abbreviated tax invoice");
        // Explicit paid stamps on the paper.
        var hasPaidMarker = ContainsAny(text,
            "ชำระแล้ว", "ชำระเงินสด", "จ่ายเงินสด", "รับเงินแล้ว", "รับเงินเรียบร้อย",
            "ได้รับเงิน", "paid in full", "payment received", "cash received");
        var vendorTaxValid = !string.IsNullOrEmpty(vendorTaxId)
            && vendorTaxId.Count(char.IsDigit) == 13;

        // ─── Step 3: Resolve scanned doc type (refines OCR's initial guess) ─
        DocumentType? scanned = previousScannedType;
        if (hasCertInLieu) scanned = DocumentType.CertificateInLieu;
        else if (hasCreditNote) scanned = DocumentType.CreditNote;
        else if (hasDebitNote) scanned = DocumentType.DebitNote;
        else if (hasReceipt && hasTaxInvoice) scanned = DocumentType.TaxInvoice;  // combined paper
        else if (hasTaxInvoice) scanned = DocumentType.TaxInvoice;
        else if (hasReceipt) scanned = DocumentType.Receipt;
        else if (hasCashBill) scanned = DocumentType.Receipt;   // no CashBill enum — closest paper type
        else if (hasInvoice) scanned = DocumentType.Invoice;
        else if (hasBillingNote) scanned = DocumentType.BillingNote;
        else if (hasDeliveryNote) scanned = DocumentType.DeliveryNote;
        else if (hasPurchaseOrder) scanned = DocumentType.PurchaseOrder;
        else if (hasQuotation) scanned = DocumentType.Quotation;

        if (scanned != null && previousScannedType != scanned)
            reasons.Add($"ตรวจพบ marker บนเอกสาร → ScannedDocumentType = {scanned}");

        // ─── Step 4: Decide what to create in our books ────────────────────
        DocumentType target;
        if (role == "Buyer")
        {
            if (hasCertInLieu)
                target = DocumentType.CertificateInLieu;
            else if (hasCreditNote)
                target = DocumentType.CreditNote;
            else if (hasDebitNote)
                target = DocumentType.DebitNote;
            else if (hasCashBill && !vendorTaxValid)
            {
                // บิลเงินสดที่ไม่มีเลขผู้เสียภาษีผู้ขาย — หลักฐานไม่ครบตาม
                // เกณฑ์สรรพากร (รายจ่ายต้องมีหลักฐานระบุผู้รับเงินชัดเจน) →
                // ออกใบรับรองแทนใบเสร็จประกอบการจ่าย เพื่อให้รายจ่ายนี้
                // นำมาหักภาษีได้
                target = DocumentType.CertificateInLieu;
                reasons.Add("บิลเงินสดไม่มีเลขผู้เสียภาษีผู้ขาย → แนะนำใบรับรองแทนใบเสร็จ (หลักฐานไม่ครบตามเกณฑ์สรรพากร)");
            }
            else if (hasCashBill || hasAbbrevTaxInvoice)
            {
                // บิลเงินสด (มี TaxId ครบ) / ใบกำกับภาษีอย่างย่อ = จ่ายเงินสด
                // หน้าร้านแล้วแน่นอน → ใบสำคัญจ่าย
                target = DocumentType.PaymentVoucher;
                reasons.Add(hasAbbrevTaxInvoice
                    ? "ใบกำกับภาษีอย่างย่อ (POS/ค้าปลีก) = จ่ายเงินสดแล้ว → ใบสำคัญจ่าย"
                    : "บิลเงินสด (มีเลขผู้เสียภาษีครบ) = จ่ายแล้ว → ใบสำคัญจ่าย");
            }
            else if (hasReceipt)
                // We already paid (the supplier handed us a receipt) →
                // book a payment voucher even when the paper also shows a
                // tax invoice header (combined ใบกำกับภาษี/ใบเสร็จรับเงิน is
                // explicitly the "paid in cash" case).
                target = DocumentType.PaymentVoucher;
            else if (hasTaxInvoice || hasInvoice)
            {
                if (hasPaidMarker)
                {
                    // Paid stamp on the invoice itself ("ชำระแล้ว" ฯลฯ) —
                    // the strongest possible signal that cash already moved.
                    target = DocumentType.PaymentVoucher;
                    reasons.Add("พบตราประทับ/ข้อความ \"ชำระแล้ว\" บนใบกำกับ → ใบสำคัญจ่าย");
                }
                else if (paymentTermsDays is > 0)
                {
                    // The paper grants credit terms — by definition NOT yet
                    // paid → set up the payable (PurchaseInvoice) regardless
                    // of the company's cash-basis default. The user can still
                    // override in the review dropdown.
                    target = DocumentType.PurchaseInvoice;
                    reasons.Add($"ใบกำกับมีเครดิตเทอม {paymentTermsDays} วัน = ยังไม่จ่าย → ใบแจ้งหนี้ซื้อ (ตั้งหนี้)");
                }
                else
                {
                    // Default = PaymentVoucher (cash-basis flow — most Thai
                    // SMEs). Companies on accrual-basis A/P can override via
                    // CompanySettings.OcrBuyerInvoiceDefaultTarget which is
                    // threaded in here. VendorIntelligence still overrides
                    // both when it has high-confidence history for a vendor.
                    target = buyerInvoiceDefaultTarget ?? DocumentType.PaymentVoucher;
                    if (buyerInvoiceDefaultTarget != null && buyerInvoiceDefaultTarget != DocumentType.PaymentVoucher)
                        reasons.Add($"target = {target} (จากการตั้งค่าบริษัท — flow บัญชี A/P)");
                }
            }
            else if (hasPurchaseOrder)
                target = DocumentType.PurchaseOrder;
            else
            {
                // No clear marker — fall back to Expense (general journal
                // entry). The invoice/taxInvoice branch above is what the
                // PaymentVoucher default applies to.
                target = DocumentType.Expense;
                if (!vendorTaxValid)
                    reasons.Add("ไม่พบเลขผู้เสียภาษีผู้ขาย — หากต้องการให้รายจ่ายหักภาษีได้ พิจารณาออกใบรับรองแทนใบเสร็จ");
            }
        }
        else // Seller
        {
            if (hasCreditNote)
                target = DocumentType.CreditNote;
            else if (hasDebitNote)
                target = DocumentType.DebitNote;
            else if (hasReceipt)
                target = DocumentType.ReceiptVoucher;       // we received cash
            else if (hasTaxInvoice || hasInvoice)
                target = DocumentType.TaxInvoice;
            else if (hasPurchaseOrder)
                // No dedicated "SalesOrder" enum — when a customer hands us
                // a PO our typical response is to issue a TaxInvoice once
                // delivery is confirmed. Booking as TaxInvoice keeps the
                // sales-cycle bookkeeping consistent.
                target = DocumentType.TaxInvoice;
            else if (hasQuotation)
                target = DocumentType.Quotation;
            else
                target = DocumentType.Invoice;              // generic outgoing
        }

        var reasonRole = role == "Buyer" ? "เราเป็นผู้ซื้อ" : "เราเป็นผู้ขาย";
        reasons.Add($"{reasonRole} + กระดาษคือ {(scanned?.ToString() ?? "ไม่ระบุ")} → ควรสร้าง {target} ในระบบ");

        return new InferenceResult(scanned, role, target, roleConf, reasons);
    }

    private static bool TaxIdMatches(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var aDigits = new string(a.Where(char.IsDigit).ToArray());
        var bDigits = new string(b.Where(char.IsDigit).ToArray());
        if (aDigits.Length != 13 || bDigits.Length != 13) return false;
        return aDigits == bDigits;
    }

    // True when the two names share a meaningful substring (≥4 chars) after
    // stripping common Thai entity suffixes — handles "บริษัท X จำกัด" vs "X จำกัด"
    // vs "X Co., Ltd." reasonably without needing a proper tokenizer.
    private static bool NameOverlaps(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length < 4 || nb.Length < 4) return false;
        return na.Contains(nb) || nb.Contains(na);
    }

    private static string Normalize(string s)
    {
        var lowered = s.ToLowerInvariant();
        foreach (var suffix in new[] { "บริษัท", "ห้างหุ้นส่วนจำกัด", "หจก.", "จำกัด", "(มหาชน)", "co., ltd.", "co.,ltd.", "ltd.", "ltd", "company" })
            lowered = lowered.Replace(suffix, "");
        return new string(lowered.Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',').ToArray()).Trim();
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(n => text.Contains(n.ToLowerInvariant()));

    private static bool ContainsAll(string text, params string[] needles)
        => needles.All(n => text.Contains(n.ToLowerInvariant()));

    /// <summary>
    /// Locate the first occurrence of buyer-side and seller-side labels in
    /// the raw text. Returns (-1, -1) when neither side is labelled. Used
    /// by the role inferrer to pin parties to position when tax/name
    /// matching is inconclusive.
    /// </summary>
    private static (int buyerLabelPos, int sellerLabelPos) FindRolePhrasePositions(string text)
    {
        if (string.IsNullOrEmpty(text)) return (-1, -1);
        var buyerLabels = new[] { "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "Bill To", "BILL TO", "Sold To", "SOLD TO", "ส่งถึง", "Customer", "BUYER" };
        var sellerLabels = new[] { "ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "ผู้ออก", "Seller", "SELLER", "From", "FROM" };
        int buyerIdx = -1, sellerIdx = -1;
        foreach (var l in buyerLabels)
        {
            var i = text.IndexOf(l, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (buyerIdx < 0 || i < buyerIdx)) buyerIdx = i;
        }
        foreach (var l in sellerLabels)
        {
            var i = text.IndexOf(l, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (sellerIdx < 0 || i < sellerIdx)) sellerIdx = i;
        }
        return (buyerIdx, sellerIdx);
    }
}
