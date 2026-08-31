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
        List<string> Reasons,
        // ฝั่งซื้อ: เอกสารที่ได้รับใช้เคลมภาษีซื้อ (input VAT) ได้หรือไม่ตาม
        // §82/5. false เมื่อเป็นใบกำกับภาษีอย่างย่อ (§82/5(2)) หรือใบเสร็จ/
        // บิลเงินสดที่ไม่ใช่ใบกำกับภาษีเต็มรูป §86/4 (§82/5(1)). null = ไม่ทราบ
        // (ไม่มี marker ชัด / ฝั่งขาย).
        bool? InputVatClaimable = null,
        // คำแนะนำผู้ใช้เมื่อเคลมไม่ได้ — อธิบายว่าทำไม + ต้องทำอย่างไร
        string? InputVatClaimWarning = null,
        // กระดาษเป็นหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) หรือไม่ และทิศทางใด
        // WeAreWithheld = true  → เราถูกหัก  = เครดิตภาษีใช้ใน ภ.ง.ด.50/51
        // WeAreWithheld = false → เราหักเขา = หนี้ต้องนำส่ง ภ.ง.ด.3/53
        // null = ไม่ใช่ 50 ทวิ หรือแยกทิศไม่ได้ (**ไม่เดา**)
        bool IsWhtCertificate = false,
        bool? WeAreWithheld = null,
        // กระดาษที่สแกนมาน่าจะเป็น "สำเนา" ไม่ใช่ต้นฉบับ — ผู้ซื้อต้องใช้ต้นฉบับ
        // ในการเคลมภาษีซื้อ (§86/4) และต้นฉบับอาจถูกลงบัญชีไปแล้ว
        bool LooksLikeCopy = false,
        // เตือนว่าอาจเป็น "เอกสารที่เราออกเอง" ที่ถูกสแกนกลับเข้ามา —
        // สร้างต่อ = ออกใบขายซ้ำ ต้องให้คนยืนยันก่อน
        bool LikelyOurOwnIssuedDocument = false);

    /// <summary>อ่านหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) แล้วบอกว่า "ใครหักใคร"
    ///
    /// <para>บนแบบ 50 ทวิ มี 2 ช่องคู่กันเสมอ: <b>ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</b>
    /// (คนที่หัก) และ <b>ผู้ถูกหักภาษี ณ ที่จ่าย</b> (คนที่โดนหัก) — ดูว่าเลขผู้เสียภาษี
    /// ของบริษัทเราไปอยู่ช่องไหน ก็รู้ทิศทางทันที ไม่ต้องเดา:</para>
    /// <list type="bullet">
    /// <item>เราอยู่ช่อง "ผู้มีหน้าที่หัก" → <b>เราหักเขา</b> = หนี้ที่ต้องนำส่ง ภ.ง.ด.3/53</item>
    /// <item>เราอยู่ช่อง "ผู้ถูกหัก" → <b>เราถูกหัก</b> = เครดิตภาษีใช้ใน ภ.ง.ด.50/51</item>
    /// </list>
    /// คืน null เมื่อไม่ใช่หนังสือรับรอง หรือแยกทิศทางไม่ได้ (ไม่เดา)
    /// </summary>
    public static bool? InferWhtCertWeAreWithheld(string? rawText, string? companyTaxId)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var text = rawText;
        var lower = text.ToLowerInvariant();
        // ⚠️ marker ต้องตรงกับกระดาษจริง: หัวเรื่องเขียนว่า "หนังสือรับรองการหัก
        // ภาษี ณ ที่จ่าย" — คำว่า "หัก ณ ที่จ่าย" (หัก ติดกับ ณ) **ไม่เคยปรากฏ**
        // เพราะมี "ภาษี" คั่นอยู่เสมอ ใช้ "ณ ที่จ่าย" + "รับรอง" แทน
        var isWhtCert = ContainsAll(lower, "ณ ที่จ่าย", "รับรอง")
                     || lower.Contains("50 ทวิ")
                     || lower.Contains("withholding tax certificate");
        if (!isWhtCert) return null;

        var companyTax = new string((companyTaxId ?? "").Where(char.IsDigit).ToArray());
        if (companyTax.Length != 13) return null;

        // ตำแหน่งของหัวข้อทั้งสองช่องบนกระดาษ
        int payerIdx = IndexOfAny(text, "ผู้มีหน้าที่หักภาษี", "ผู้มีหน้าที่หัก", "ผู้จ่ายเงิน");
        int payeeIdx = IndexOfAny(text, "ผู้ถูกหักภาษี", "ผู้ถูกหัก", "ผู้รับเงิน");
        if (payerIdx < 0 && payeeIdx < 0) return null;

        // เลขผู้เสียภาษีของบริษัทเราปรากฏที่ตำแหน่งไหนบ้าง (รองรับรูปแบบมีขีดคั่น)
        var ourPositions = new List<int>();
        foreach (System.Text.RegularExpressions.Match m in
                 // ตัวคั่นห้ามครอบ \n — ไม่งั้นเลขท้ายบรรทัดต่อกับเลขต้นบรรทัดถัดไป
                 // จนได้ "ตำแหน่งของเลขเรา" ที่ผิดช่อง แล้วทิศ 50 ทวิ กลับด้าน
                 System.Text.RegularExpressions.Regex.Matches(text, @"[\d \t-]{13,}"))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Contains(companyTax, StringComparison.Ordinal)) ourPositions.Add(m.Index);
        }
        if (ourPositions.Count == 0) return null;

        // เลขของเราอยู่ "ใต้หัวข้อไหน" — หัวข้อที่อยู่ก่อนหน้าและใกล้ที่สุดคือเจ้าของช่อง
        bool? weAreWithheld = null;
        foreach (var pos in ourPositions)
        {
            var underPayer = payerIdx >= 0 && payerIdx < pos;
            var underPayee = payeeIdx >= 0 && payeeIdx < pos;
            if (underPayer && underPayee) weAreWithheld = payeeIdx > payerIdx;   // ช่องล่างสุดชนะ
            else if (underPayee) weAreWithheld = true;
            else if (underPayer) weAreWithheld = false;
            if (weAreWithheld.HasValue) break;
        }
        return weAreWithheld;
    }

    private static int IndexOfAny(string text, params string[] needles)
    {
        var best = -1;
        foreach (var n in needles)
        {
            var i = text.IndexOf(n, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

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

        // ─── Step 1a′: หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) — อ่านทิศจากตำแหน่งช่อง ───
        //
        // แบบ 50 ทวิ มีสองช่องคู่กันเสมอ (ผู้มีหน้าที่หัก / ผู้ถูกหัก) — ดูว่าเลข
        // ของเราอยู่ช่องไหนก็รู้ทิศทันที **ไม่ต้องเดา**
        //
        // เดิมมี heuristic ที่บอกว่า "เจอ 50 ทวิ ⇒ เราเป็น Seller" ซึ่งผิดครึ่งหนึ่ง
        // เสมอ (ใบที่เราหักผู้รับเหมาก็เป็น 50 ทวิ เหมือนกัน แต่เราเป็นผู้จ่าย) และ
        // ตัวอ่านทิศที่ถูกต้อง InferWhtCertWeAreWithheld ก็มีอยู่แล้วในไฟล์นี้ —
        // แต่เดิมถูกเรียกหลังสร้างเอกสารเสร็จ ไม่ใช่ตอนตัดสินว่าจะสร้างอะไร
        var isWhtCert = ContainsAll(text, "ณ ที่จ่าย", "รับรอง")
                     || text.Contains("50 ทวิ")
                     || text.Contains("withholding tax certificate");
        var weAreWithheld = isWhtCert ? InferWhtCertWeAreWithheld(rawText, companyTaxId) : null;
        if (weAreWithheld.HasValue && roleConf < 0.9m)
        {
            // เราถูกหัก = เราเป็นผู้รับเงิน = ผู้ขาย · เราหักเขา = เราเป็นผู้จ่าย = ผู้ซื้อ
            role = weAreWithheld.Value ? "Seller" : "Buyer";
            roleConf = Math.Max(roleConf, 0.9m);
            reasons.Add(weAreWithheld.Value
                ? "หนังสือรับรองหัก ณ ที่จ่าย: เลขของเราอยู่ช่อง \"ผู้ถูกหัก\" → เราถูกหัก (เครดิตภาษี ภ.ง.ด.50/51) · role = Seller"
                : "หนังสือรับรองหัก ณ ที่จ่าย: เลขของเราอยู่ช่อง \"ผู้มีหน้าที่หัก\" → เราหักเขา (นำส่ง ภ.ง.ด.3/53) · role = Buyer");
        }
        else if (isWhtCert)
        {
            reasons.Add("พบหนังสือรับรองหัก ณ ที่จ่าย แต่แยกทิศทางจากกระดาษไม่ได้ — "
                + "ไม่เดาบทบาทจากเอกสารชนิดนี้ (เดิมเดาเป็น Seller เสมอ ซึ่งผิดครึ่งหนึ่ง)");
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
            (int buyerLabelPos, int sellerLabelPos) = FindRolePhrasePositions(rawText);

            // Heuristic 1 — ป้าย "ผู้ขาย/ผู้ออกใบ" ใกล้ชื่อบริษัทเรา → เราเป็นผู้ขาย
            // (ด้านตรงข้ามของ Heuristic 3; เดิมคำนวณ sellerLabelPos แล้วทิ้ง)
            if (sellerLabelPos >= 0 && !string.IsNullOrEmpty(vendorName) && !string.IsNullOrEmpty(companyNm))
            {
                var vPos = rawText.IndexOf(vendorName!, sellerLabelPos, StringComparison.OrdinalIgnoreCase);
                if (vPos >= 0 && vPos - sellerLabelPos <= 120
                    && NameOverlaps(vendorName!.ToLowerInvariant(), companyNm))
                {
                    role = "Seller";
                    roleConf = Math.Max(roleConf, 0.85m);
                    reasons.Add("ป้าย \"ผู้ขาย/ผู้ออกใบ\" ใกล้ชื่อบริษัทเรา → role = Seller (label + fuzzy)");
                }
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
        // สลิปโอนเงิน/หลักฐานการชำระ — เดิม**ไม่มี marker เลย** ⇒ ตกไปเป็น Expense
        // ทุกใบ ทั้งที่ความหมายชัดว่าเงินเคลื่อนแล้ว (จ่าย = PV / รับ = RV)
        var hasBankSlip = ContainsAny(text,
            "สลิปโอนเงิน", "สลิปการโอน", "หลักฐานการโอนเงิน", "โอนเงินสำเร็จ", "โอนเงินเรียบร้อย",
            "รายการโอนเงิน", "transfer slip", "payment slip", "transfer successful",
            "พร้อมเพย์", "promptpay");
        // ── Markers that decide PAID-vs-UNPAID and EVIDENCE QUALITY ──
        // บิลเงินสด = informal cash bill. With a valid 13-digit vendor TaxId it
        // is acceptable expense evidence (→ PaymentVoucher); WITHOUT one, RD
        // deductibility requires a ใบรับรองแทนใบเสร็จ backing the payment.
        var hasCashBill = ContainsAny(text, "บิลเงินสด", "cash bill", "cash sale");
        // ใบกำกับภาษีอย่างย่อ (abbreviated tax invoice — retail/POS) is by
        // definition a paid-at-the-till document → PaymentVoucher.
        // ⚠️ กับดัก false positive 2 ทาง (ผู้ใช้เจอใบเต็มรูปถูกตีเป็นอย่างย่อ):
        //   (1) ข้อความปฏิเสธ — vision model บางครั้งบรรยายว่าเอกสาร "ไม่ใช่
        //       ใบกำกับภาษีอย่างย่อ" → substring ดิบจับเจอทั้งที่ความหมายตรงข้าม
        //   (2) นิยาม §86/6 — ใบอย่างย่อ "ไม่ระบุชื่อ/เลขภาษีผู้ซื้อ". ถ้า OCR
        //       สกัดเลขภาษีผู้ซื้อ 13 หลักได้ = ใบเต็มรูปแน่นอน ต่อให้เจอคำนี้
        //       ที่อื่นบนกระดาษก็ห้ามตีเป็นอย่างย่อ (เคลมภาษีซื้อได้ ห้ามปัดตก)
        var hasAbbrevTaxInvoice = ContainsAnyNotNegated(text, "ใบกำกับภาษีอย่างย่อ", "abbreviated tax invoice");
        // ปลด marker เฉพาะเมื่อเลขผู้ซื้อ "จริง" — ต้องผ่าน mod-11 checksum ไม่ใช่
        // แค่นับ 13 หลัก (vision model hallucinate เลข 13 หลักได้ง่าย → ใบอย่างย่อ
        // แท้หลุดไปเคลม ภ.พ.30 ผิด §82/5(2))
        var buyerTaxIdVerified =
            Tax.TaxInvoiceCompletenessChecker.IsValidThaiTaxId(buyerTaxId);
        if (hasAbbrevTaxInvoice && buyerTaxIdVerified)
        {
            hasAbbrevTaxInvoice = false;
            reasons.Add("พบคำ \"อย่างย่อ\" บนกระดาษ แต่ใบระบุเลขผู้เสียภาษีผู้ซื้อครบ 13 หลัก (checksum ผ่าน) "
                + "(ใบอย่างย่อตาม §86/6 ไม่มีข้อมูลผู้ซื้อ) → ตีเป็นใบกำกับภาษีเต็มรูป เคลมภาษีซื้อได้");
        }
        // ── ตรวจ "อย่างย่อ" จากโครงสร้าง ไม่ใช่แค่ตัวหนังสือ ──
        //
        // ⚠️ ช่องโหว่ที่ทีมตรวจพบ: สลิป POS ค้าปลีกจำนวนมาก **ไม่พิมพ์คำว่า
        // "ใบกำกับภาษีอย่างย่อ"** เลย (พิมพ์แค่ "ใบกำกับภาษี" + "ราคารวม
        // ภาษีมูลค่าเพิ่มแล้ว" และไม่มีบล็อกผู้ซื้อ) ⇒ ระบบตีเป็นใบเต็มรูป
        // แล้ว **เคลมภาษีซื้อต้องห้ามตาม §82/5(2)** ยื่น ภ.พ.30 เกินสิทธิ์เงียบ ๆ
        //
        // ลายเซ็นเชิงโครงสร้างของ §86/6: มีคำว่าใบกำกับภาษี + ไม่มีข้อมูลผู้ซื้อ
        // เลย (ไม่มีทั้งเลขภาษีและป้ายผู้ซื้อ) + ราคารวม VAT แล้ว
        if (!hasAbbrevTaxInvoice && hasTaxInvoice && !buyerTaxIdVerified)
        {
            var vatInclusive = ContainsAny(text,
                "ราคารวมภาษีมูลค่าเพิ่ม", "รวมภาษีมูลค่าเพิ่มแล้ว", "ราคารวมvat",
                "vat included", "inclusive of vat", "incl. vat");
            // `rawText` ประกาศเป็น string (ไม่ใช่ string?) แต่ถูก
            // `!string.IsNullOrEmpty(rawText)` เช็คไปก่อนหน้า ⇒ Roslyn เรียนรู้ว่า
            // "อาจ null ได้" แล้วเตือน CS8604 ตรงนี้ — กันด้วย ?? "" ให้ชัด
            var hasBuyerLabel = FindRolePhrasePositions(rawText ?? "").buyerLabelPos >= 0;
            if (vatInclusive && !hasBuyerLabel && string.IsNullOrWhiteSpace(buyerTaxId))
            {
                hasAbbrevTaxInvoice = true;
                reasons.Add("กระดาษไม่พิมพ์คำว่า \"อย่างย่อ\" แต่มีลายเซ็นของ §86/6 ครบ "
                    + "(ราคารวม VAT แล้ว + ไม่มีข้อมูลผู้ซื้อเลย) → ตีเป็นใบกำกับภาษีอย่างย่อ "
                    + "เคลมภาษีซื้อไม่ได้ (§82/5(2)) — สลิป POS ส่วนใหญ่เป็นแบบนี้");
            }
        }
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
            else if (hasBillingNote)
            {
                // ใบวางบิลที่ได้รับ = ผู้ขายเรียกเก็บของที่ส่งไปแล้ว ⇒ ตั้งหนี้
                // (เดิมตกไป Expense เพราะไม่มี branch นี้)
                target = DocumentType.PurchaseInvoice;
                reasons.Add("ใบวางบิลจากผู้ขาย = เรียกเก็บของที่ส่งแล้ว → ตั้งหนี้เป็นใบแจ้งหนี้ซื้อ");
            }
            else if (hasDeliveryNote)
            {
                // ใบส่งของ = ของมาถึงแล้วแต่ยังไม่วางบิล ⇒ ใบรับสินค้า (GRN)
                // ซึ่งเป็นขาที่ถูกต้องของ 3-way match (PO ↔ GRN ↔ Invoice)
                target = DocumentType.GoodsReceiptNote;
                reasons.Add("ใบส่งของจากผู้ขาย = รับของแล้วยังไม่วางบิล → ใบรับสินค้า (GRN) สำหรับ 3-way match");
            }
            else if (hasQuotation)
            {
                // ใบเสนอราคาที่ได้รับ — ยังไม่ใช่รายจ่าย ห้ามลงบัญชี
                // (เดิมตกไป Expense = สร้างค่าใช้จ่ายจากใบเสนอราคา)
                target = DocumentType.PurchaseRequisition;
                reasons.Add("ใบเสนอราคาที่ได้รับ ยังไม่ใช่รายจ่าย → ตั้งเป็นใบขอซื้อไว้เปรียบเทียบราคา (ไม่ลงบัญชี)");
            }
            else if (hasBankSlip)
            {
                // สลิปโอนเงินฝั่งเรา = จ่ายออกไปแล้ว
                target = DocumentType.PaymentVoucher;
                reasons.Add("สลิป/หลักฐานการโอนเงิน = เงินออกแล้ว → ใบสำคัญจ่าย (แนบสลิปเป็นหลักฐาน)");
            }
            else
            {
                // No clear marker — fall back to Expense (general journal
                // entry). The invoice/taxInvoice branch above is what the
                // PaymentVoucher default applies to.
                target = DocumentType.Expense;
                if (!vendorTaxValid)
                    reasons.Add("ไม่พบเลขผู้เสียภาษีผู้ขาย — หากต้องการให้รายจ่ายหักภาษีได้ พิจารณาออกใบรับรองแทนใบเสร็จ");
            }

            // 50 ทวิ ที่ "เราเป็นผู้หัก" — เรากำลังจ่ายเงินและหักภาษีไว้นำส่ง
            // ภ.ง.ด.3/53 ⇒ คู่กับใบสำคัญจ่ายเสมอ ไม่ใช่ Expense ลอย ๆ
            if (weAreWithheld == false)
            {
                target = DocumentType.PaymentVoucher;
                reasons.Add("เราเป็นผู้หักภาษี ณ ที่จ่าย → ใบสำคัญจ่าย (ภาษีที่หักไว้เข้าทะเบียนนำส่ง ภ.ง.ด.3/53)");
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
            else if (hasBankSlip)
            {
                // สลิปที่ลูกค้าส่งมาให้ = เงินเข้าแล้ว → ใบสำคัญรับ
                target = DocumentType.ReceiptVoucher;
                reasons.Add("สลิป/หลักฐานการโอนเงินจากลูกค้า = เงินเข้าแล้ว → ใบสำคัญรับ");
            }
            else
                target = DocumentType.Invoice;              // generic outgoing

            // 50 ทวิ ที่ "เราถูกหัก" — ไม่ใช่ใบขายใบใหม่ แต่เป็นหลักฐานเครดิตภาษี
            // ของใบขายที่ออกไปแล้ว (ระบบลงทะเบียนเครดิตให้ต่างหากผ่าน
            // EnsureWhtCreditFromCertAsync) ⇒ ห้ามสร้างใบขายซ้ำ
            if (weAreWithheld == true)
            {
                target = DocumentType.ReceiptVoucher;
                reasons.Add("เราถูกหักภาษี ณ ที่จ่าย → หลักฐานรับเงินสุทธิ (ใบสำคัญรับ) + ลงทะเบียนเครดิตภาษี "
                    + "ใช้ใน ภ.ง.ด.50/51 — **ไม่ใช่**ใบขายใบใหม่");
            }
        }

        // ─── เตือน "อาจเป็นเอกสารที่เราออกเอง สแกนกลับเข้ามา" ────────────────
        // เราเป็นผู้ขายบนกระดาษ + กระดาษเป็นเอกสารขาย = สำเนาใบที่เราออกไปแล้ว
        // การสร้างต่อ = ออกใบขายใบที่สอง (เลขซ้ำ/ยอดซ้ำในรายงานภาษีขาย)
        // ยกเว้น 50 ทวิ ซึ่งเป็นกระดาษของคู่ค้า ไม่ใช่ของเรา
        var likelyOurOwn = role == "Seller" && roleConf >= 0.9m && !isWhtCert
            && (hasTaxInvoice || hasReceipt || hasInvoice || hasCreditNote || hasDebitNote);
        if (likelyOurOwn)
            reasons.Add("⚠️ เลขผู้ขายบนกระดาษคือบริษัทเราเอง — น่าจะเป็นสำเนาเอกสารที่เราออกไปแล้ว "
                + "ตรวจสอบว่ามีใบนี้ในระบบหรือยังก่อนสร้างใหม่ (กันออกเลขซ้ำ/ยอดซ้ำในรายงานภาษีขาย)");

        var reasonRole = role == "Buyer" ? "เราเป็นผู้ซื้อ" : "เราเป็นผู้ขาย";
        reasons.Add($"{reasonRole} + กระดาษคือ {(scanned?.ToString() ?? "ไม่ระบุ")} → ควรสร้าง {target} ในระบบ");

        // ─── Input VAT claimability (§82/5) — ฝั่งซื้อเท่านั้น ───────────────
        // เคลมภาษีซื้อได้ต้องมี "ใบกำกับภาษีเต็มรูป" §86/4 (มีคำว่าใบกำกับภาษี +
        // ชื่อ/ที่อยู่/เลขผู้เสียภาษีทั้งผู้ขาย-ผู้ซื้อ). เอกสารต่อไปนี้เคลมไม่ได้:
        //   • ใบกำกับภาษีอย่างย่อ §86/6 → §82/5(2) ห้ามเคลม (ผู้ซื้อ)
        //   • ใบเสร็จ/บิลเงินสด ที่ไม่ใช่ใบกำกับภาษีเต็มรูป → §82/5(1)
        // หมายเหตุ: "ใบกำกับภาษีอย่างย่อ" มีคำว่า "ใบกำกับภาษี" → ต้องแยกชัด.
        // ── ต้นฉบับ / สำเนา ──
        //
        // ⚠️ เดิมไม่มีการตรวจเลย ⇒ สแกน "สำเนา" มาลงบัญชีได้เหมือนต้นฉบับ
        // ซึ่งเสี่ยงเคลมภาษีซื้อซ้ำ (ต้นฉบับใบเดียวกันอาจถูกลงไปแล้ว) และ
        // §86/4 กำหนดให้ผู้ซื้อใช้ **ต้นฉบับ** เท่านั้นในการเคลม
        //
        // ระวัง: หัวกระดาษไทยจำนวนมากพิมพ์ทั้งสองคำไว้บนใบเดียว
        // ("ต้นฉบับใบส่งสินค้า/ต้นฉบับใบกำกับภาษี" หรือ "ต้นฉบับ (สำเนา)")
        // ⇒ ถือเป็นสำเนาเฉพาะเมื่อ **เจอคำว่าสำเนาแต่ไม่เจอคำว่าต้นฉบับ**
        var hasOriginalMark = ContainsAny(text, "ต้นฉบับ", "original");
        var hasCopyMark = ContainsAny(text, "สำเนา", "copy", "duplicate");
        var looksLikeCopy = hasCopyMark && !hasOriginalMark;
        if (looksLikeCopy)
            reasons.Add("กระดาษมีคำว่า \"สำเนา\" และไม่มีคำว่า \"ต้นฉบับ\" — ผู้ซื้อต้องใช้ต้นฉบับ "
                + "ในการเคลมภาษีซื้อ (§86/4) และต้นฉบับใบเดียวกันอาจถูกลงบัญชีไปแล้ว "
                + "ตรวจสอบก่อนสร้างเอกสาร");

        bool? inputVatClaimable = null;
        string? inputVatWarning = null;
        if (role == "Buyer")
        {
            var hasFullTaxInvoice = hasTaxInvoice && !hasAbbrevTaxInvoice;
            if (hasAbbrevTaxInvoice)
            {
                inputVatClaimable = false;
                inputVatWarning =
                    "เอกสารนี้เป็น \"ใบกำกับภาษีอย่างย่อ\" (§86/6) — นำภาษีซื้อมาเคลม ภ.พ.30 ไม่ได้ "
                    + "ตาม §82/5(2). หากต้องการเคลม VAT ให้ขอ \"ใบกำกับภาษีเต็มรูป\" (§86/4) จากผู้ขาย "
                    + "ที่ระบุชื่อ-ที่อยู่-เลขประจำตัวผู้เสียภาษีของบริษัทเรา (ผู้ซื้อ) ครบ. "
                    + "ถ้าไม่ขอ → VAT จะถูกรวมเป็นต้นทุน/ค่าใช้จ่าย (หักภาษีเงินได้ได้ตามปกติ).";
            }
            else if ((hasReceipt || hasCashBill) && !hasFullTaxInvoice)
            {
                inputVatClaimable = false;
                inputVatWarning =
                    "เอกสารนี้เป็นใบเสร็จรับเงิน/บิลเงินสด ไม่ใช่ \"ใบกำกับภาษีเต็มรูป\" (§86/4) — "
                    + "นำภาษีซื้อมาเคลม ภ.พ.30 ไม่ได้ ตาม §82/5(1). ขอ \"ใบกำกับภาษีเต็มรูป\" จากผู้ขาย "
                    + "(ต้องมีคำว่า \"ใบกำกับภาษี\" + ชื่อ/ที่อยู่/เลขผู้เสียภาษีของผู้ซื้อ) จึงจะเคลมได้. "
                    + "ถ้าไม่ขอ → VAT รวมเป็นต้นทุน/ค่าใช้จ่าย.";
            }
            else if (hasFullTaxInvoice)
            {
                inputVatClaimable = true;
            }
        }

        return new InferenceResult(scanned, role, target, roleConf, reasons,
            inputVatClaimable, inputVatWarning,
            IsWhtCertificate: isWhtCert, WeAreWithheld: weAreWithheld,
            LooksLikeCopy: looksLikeCopy,
            LikelyOurOwnIssuedDocument: likelyOurOwn);
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

    /// <summary>เหมือน ContainsAny แต่ข้าม occurrence ที่ถูก "ปฏิเสธ" — มีคำ
    /// ไม่ใช่/ไม่เป็น/มิใช่/ไม่ออก/not/no นำหน้าภายใน ~14 ตัวอักษร. กันเคส
    /// vision model บรรยายว่า "เอกสารนี้ไม่ใช่ใบกำกับภาษีอย่างย่อ" แล้ว
    /// substring ดิบตีความกลับด้าน (ใบเต็มรูปโดนปัดตกจากการเคลมภาษีซื้อ).</summary>
    private static bool ContainsAnyNotNegated(string text, params string[] needles)
    {
        foreach (var raw in needles)
        {
            var n = raw.ToLowerInvariant();
            int idx = 0;
            while ((idx = text.IndexOf(n, idx, StringComparison.Ordinal)) >= 0)
            {
                var start = Math.Max(0, idx - 14);
                var prefix = text.Substring(start, idx - start);
                // ปฏิเสธนำหน้า: "ไม่ใช่/ห้าม(ออก)ใบกำกับภาษีอย่างย่อ"
                var negated = prefix.Contains("ไม่ใช่") || prefix.Contains("ไม่เป็น")
                    || prefix.Contains("มิใช่") || prefix.Contains("ไม่ออก")
                    || prefix.Contains("ห้าม")
                    || prefix.Contains("not ") || prefix.Contains("no ");
                // ปฏิเสธตามหลัง: "ออกใบกำกับภาษีอย่างย่อไม่ได้/ไม่ให้..."
                if (!negated)
                {
                    var sufEnd = Math.Min(text.Length, idx + n.Length + 10);
                    var suffix = text.Substring(idx + n.Length, sufEnd - (idx + n.Length));
                    negated = suffix.StartsWith("ไม่ได้") || suffix.StartsWith("ไม่ให้")
                        || suffix.Contains("ไม่ได้");
                }
                if (!negated) return true;
                idx += n.Length;
            }
        }
        return false;
    }

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
