using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// ตรวจ "รายจ่ายต้องห้าม" ตาม ป.รัษฎากร §65 ตรี — คืนยอดที่ต้อง "บวกกลับ"
/// ใน ภ.ง.ด.50 + RuleCode + LegalReference. เรียกตอน approve เอกสารฝั่งซื้อ/
/// ค่าใช้จ่าย (PurchaseInvoice / Expense / PaymentVoucher standalone).
///
/// Pure function — ไม่แตะ DB. รับ context ที่ caller เตรียม (revenue,
/// paidUpCapital สำหรับ cap ค่ารับรอง). ครอบเฉพาะข้อที่ตรวจได้จากข้อมูล
/// เอกสารจริง (ไม่เดา) — ข้อที่ต้อง judgement ของนักบัญชี (เช่น ส่วนตัว/เสน่หา)
/// flag เป็น warning ให้ผู้ใช้ยืนยัน.
///
/// อ้างอิง CLAUDE.md กฎเหล็ก #2 section L.
/// </summary>
public static class Section65TerValidator
{
    /// <summary>Threshold มูลค่าทรัพย์สินที่ถือเป็น capex (พ.ร.ฎ.145 + แนว
    /// ปฏิบัติ): ≥ 50,000 บาท/ชิ้น โดยทั่วไปต้อง capitalize. ใช้เป็น
    /// warning (ไม่ hard block) เพราะอายุการใช้งานเป็นปัจจัยร่วมที่ระบบ
    /// ไม่รู้แน่. ตั้งค่าได้ผ่าน parameter.</summary>
    public const decimal DefaultCapexThreshold = 50_000m;

    public sealed record Finding(
        string RuleCode,
        string LegalReference,
        decimal AddBackAmount,
        string Message,
        bool HardBlock,
        bool NeedsConfirmation);

    public sealed record Result(
        decimal TotalAddBack,
        IReadOnlyList<Finding> Findings)
    {
        public bool HasHardBlock => Findings.Any(f => f.HardBlock);
        public string? FirstBlockMessage => Findings.FirstOrDefault(f => f.HardBlock)?.Message;
        public string ToJson() => JsonSerializer.Serialize(
            Findings.Select(f => new { f.RuleCode, f.LegalReference, f.AddBackAmount, f.Message }));
    }

    public sealed record Context(
        decimal? AnnualRevenue,
        decimal? PaidUpCapital,
        /// <summary>ยอด "ค่ารับรอง" สะสมตั้งแต่ต้นรอบบัญชี (YTD) ของบริษัท
        /// — ใช้คำนวณ cap §65 ตรี(4) ที่เป็น per-fiscal-year ไม่ใช่ per-doc.
        /// ถ้าไม่ส่งมา (null) → fallback คำนวณ cap เฉพาะ entertainment ใน
        /// เอกสารปัจจุบัน (legacy behavior, อาจ under-report ส่วนเกิน).</summary>
        decimal? PriorYtdEntertainmentExpense = null,
        // ───── context เพิ่มสำหรับอนุมาตราที่เดิมไม่ได้ตรวจ ─────
        // ทุกตัวเป็น optional + default = "ไม่ตรวจ" เพื่อไม่เปลี่ยนพฤติกรรมของ
        // caller เดิมจนกว่าจะส่งข้อมูลมาจริง
        /// <summary>วันเริ่มรอบบัญชีปัจจุบัน — ใช้ตรวจ (10) รายจ่ายของรอบก่อน</summary>
        DateTime? CurrentFiscalYearStart = null,
        /// <summary>เลขผู้เสียภาษีของบริษัทเอง — ใช้ตรวจ (13) เช่าทรัพย์สินตัวเอง</summary>
        string? CompanyTaxId = null,
        /// <summary>เอกสารนี้มีหลักฐานการจ่ายจริงแนบ/บันทึกแล้วหรือไม่ —
        /// null = ไม่ทราบ (ไม่ตรวจ (8))</summary>
        bool? HasPaymentEvidence = null,
        /// <summary>มีต้นฉบับเอกสารจากผู้ขาย (ใบเสร็จ/ใบกำกับ/ไฟล์แนบ) หรือไม่ —
        /// null = ไม่ทราบ (ไม่ตรวจ (9))</summary>
        bool? HasSourceDocument = null,
        /// <summary>ผู้ใช้ยืนยันว่ารายจ่ายเกี่ยวกับกิจการหรือไม่ —
        /// false = ไม่เกี่ยว → บวกกลับ (14); null = ไม่ทราบ</summary>
        bool? RelatedToBusiness = null,
        /// <summary>คู่ค้าเป็นบุคคล/นิติบุคคลที่เกี่ยวโยงกัน — ใช้เตือน (15)
        /// transfer pricing ให้ทบทวนราคา</summary>
        bool IsRelatedParty = false,
        /// <summary>รายจ่ายต่างประเทศนี้เชื่อมโยงกับกิจการในไทยหรือไม่ —
        /// false + เอกสารเป็นบริการต่างประเทศ → บวกกลับ (19)</summary>
        bool? LinkedToThaiOperation = null,
        /// <summary>true = บังคับตาม CLAUDE.md L(11)(18) เต็มรูป คือ **ขาดชื่อ
        /// หรือขาดเลขผู้เสียภาษีอย่างใดอย่างหนึ่งก็ block**. default = false
        /// (block เฉพาะตอนขาดทั้งคู่ ส่วนขาดเลขภาษีเป็นคำเตือน) เพราะการ
        /// เปิดเต็มรูปจะบล็อกค่าใช้จ่ายเงินสดรายย่อยจำนวนมากที่ไม่มีเลขภาษี
        /// — เจ้าของระบบเปิดได้เมื่อพร้อมบังคับนโยบาย</summary>
        bool StrictPayeeIdentification = false,
        /// <summary>รอบ PP36 ทีม F2 (E-12): ผู้รับเงินเป็นผู้รับต่างประเทศ (ประเทศคู่ค้าไม่ใช่ไทย หรือเอกสารติ๊กบริการต่างประเทศ §83/6) —
        /// ผู้รับต่างประเทศไม่มีเลขผู้เสียภาษีไทย · เลขประจำตัวผู้เสียภาษี/VAT number ของประเทศผู้รับ (ช่องเลขผู้เสียภาษีรูปแบบใดก็ได้) ใช้ระบุผู้รับได้
        /// ⇒ (11)(18) ไม่ฟ้อง "ไม่มีเลขผู้เสียภาษี" กับเลขต่างประเทศ และข้อความกรณีไม่มีเลขเลยบอกให้เก็บเลขของประเทศผู้รับ (ไม่ใช่เลขไทย)</summary>
        bool PayeeIsForeign = false,
        /// <summary>รอบ 204 (ทีม B): เอกสารมีไฟล์แนบไหม — ใช้กับ "ผู้จ่ายรับภาระภาษีตามสัญญา" (ติ๊กแล้วไม่มีไฟล์แนบ ⇒ ข้อสังเกตให้แนบสัญญา · ไม่บล็อก) ·
        /// null = ไม่ทราบ (ไม่ฟ้อง)</summary>
        bool? HasAttachment = null);

    /// <summary>accountInfo: map AccountId → (code, name) สำหรับตรวจชนิดบัญชี.
    /// payeeName/payeeTaxId: ชื่อ+เลขผู้รับเงิน (จาก Contact ของเอกสารซื้อ).
    /// accountTypes (ฝ่ายค้านรอบ 201 รอบสาม P1-2): map AccountId → ชนิดผัง — ตัดสิน "เป็นรายจ่ายไหม" ด้วยชนิดผังก่อน
    /// (ผังที่ผู้ใช้สร้าง/นำเข้า เช่น 6100 ค่าปรับ ชนิด Expense ต้องถูกตรวจ) · ว่าง/ไม่มีในแผนที่ = ใช้เลขนำหน้า 5 เป็นทางสำรอง</summary>
    /// <summary>ต้นทุนที่ลงค่าใช้จ่ายจริงของเอกสาร = Σ (ฐานบรรทัด + VAT เฉพาะบรรทัดที่เคลมไม่ได้) — VAT ที่เคลมได้ (รวม VAT ประเมินเอง ภ.พ.36 ที่พัก 11640)
    /// เป็นภาษีซื้อ ไม่ใช่ค่าใช้จ่าย · ภาษีหัก ณ ที่จ่ายไม่ลดค่าใช้จ่าย (สูตรเดียวกับยอดบวกกลับรายบรรทัด)</summary>
    internal static decimal ExpenseCostOf(Document doc)
        => doc.Lines.Where(l => !l.IsDeleted).Sum(l => l.Amount + (l.IsVatClaimable ? 0m : l.VatAmount));

    public static Result Evaluate(
        Document doc,
        IReadOnlyDictionary<Guid, (string Code, string Name)> accountInfo,
        string? payeeName, string? payeeTaxId,
        Context ctx,
        decimal capexThreshold = DefaultCapexThreshold,
        IReadOnlyDictionary<Guid, AccountType>? accountTypes = null)
    {
        var findings = new List<Finding>();

        // (11)(18) ไม่ระบุผู้รับเงิน — hard block (รายจ่ายที่พิสูจน์ผู้รับไม่ได้)
        if (doc.TotalAmount > 0
            && string.IsNullOrWhiteSpace(payeeName)
            && string.IsNullOrWhiteSpace(payeeTaxId))
        {
            findings.Add(new("RD-65ter(11)(18)", "ป.รัษฎากร §65 ตรี (11)(18)",
                0m, "ต้องระบุผู้รับเงิน (ชื่อหรือเลขผู้เสียภาษี) — รายจ่ายที่ไม่ระบุผู้รับเป็นรายจ่ายต้องห้าม",
                HardBlock: true, NeedsConfirmation: false));
        }
        // ระบุชื่อแต่ไม่มีเลขผู้เสียภาษี — สรรพากรถือว่าพิสูจน์ผู้รับไม่ได้เช่นกัน
        // default = เตือน (กันบล็อกค่าใช้จ่ายเงินสดรายย่อยจำนวนมาก),
        // เปิด StrictPayeeIdentification เพื่อบังคับตาม CLAUDE.md L(11)(18) เต็มรูป
        else if (doc.TotalAmount > 0 && string.IsNullOrWhiteSpace(payeeTaxId))
        {
            findings.Add(new("RD-65ter(11)(18)", "ป.รัษฎากร §65 ตรี (11)(18)",
                0m, ctx.PayeeIsForeign
                    // E-12: ผู้รับต่างประเทศไม่มีเลขไทยให้กรอก — หลักฐานระบุตัวคือเลขของประเทศผู้รับ (เช่น VAT number "NL805734958B01")
                    ? "ผู้รับเงินต่างประเทศยังไม่มีเลขประจำตัวผู้เสียภาษี/VAT number ของประเทศผู้รับ — ไม่ต้องใช้เลขไทย แต่ต้องระบุตัวผู้รับได้ "
                      + "(กรอกเลขของประเทศผู้รับในช่องเลขผู้เสียภาษีของผู้ติดต่อ + ที่อยู่) มิฉะนั้นเสี่ยงถูกถือเป็นรายจ่ายต้องห้าม"
                    : "ไม่มีเลขประจำตัวผู้เสียภาษีของผู้รับเงิน — เสี่ยงถูกถือเป็นรายจ่ายต้องห้าม โปรดเพิ่มก่อนปิดรอบ",
                HardBlock: ctx.StrictPayeeIdentification, NeedsConfirmation: true));
        }

        // (8) รายจ่ายไม่ได้จ่ายจริง — ตรวจเมื่อ caller ส่งข้อมูลมาเท่านั้น
        if (ctx.HasPaymentEvidence == false && doc.TotalAmount > 0)
        {
            findings.Add(new("RD-65ter(8)", "ป.รัษฎากร §65 ตรี (8)",
                0m, "ยังไม่มีหลักฐานการจ่ายเงินจริง — รายจ่ายที่จ่ายไม่จริงเป็นรายจ่ายต้องห้าม (แนบหลักฐานก่อนปิดรอบ)",
                HardBlock: false, NeedsConfirmation: true));
        }

        // (9) ไม่มีเอกสารต้นฉบับจากผู้ขาย
        if (ctx.HasSourceDocument == false && doc.TotalAmount > 0)
        {
            findings.Add(new("RD-65ter(9)", "ป.รัษฎากร §65 ตรี (9)",
                0m, "ไม่มีเอกสารต้นฉบับ (ใบเสร็จ/ใบกำกับจากผู้ขาย) — รายจ่ายที่ไม่มีหลักฐานเป็นรายจ่ายต้องห้าม",
                HardBlock: false, NeedsConfirmation: true));
        }

        // (10) รายจ่ายของรอบบัญชีก่อน — ต้องบวกกลับรอบนี้ (ไปหักในรอบที่เกิดจริง)
        if (ctx.CurrentFiscalYearStart is { } fyStart && doc.DocumentDate.Date < fyStart.Date)
        {
            findings.Add(new("RD-65ter(10)", "ป.รัษฎากร §65 ตรี (10)",
                0m, $"วันที่เอกสาร {doc.DocumentDate:dd/MM/yyyy} อยู่ก่อนรอบบัญชีปัจจุบัน ({fyStart:dd/MM/yyyy}) — รายจ่ายรอบก่อนหักในรอบนี้ไม่ได้ โปรดระบุเหตุผล",
                HardBlock: false, NeedsConfirmation: true));
        }

        // (14) ไม่เกี่ยวกับกิจการ — ผู้ใช้ติ๊กเองว่าไม่เกี่ยว → บวกกลับเต็มจำนวน
        if (ctx.RelatedToBusiness == false && doc.TotalAmount > 0)
        {
            findings.Add(new("RD-65ter(14)", "ป.รัษฎากร §65 ตรี (14)",
                doc.TotalAmount, $"ระบุว่าไม่เกี่ยวกับกิจการ — บวกกลับเต็มจำนวน ({doc.TotalAmount:N2})",
                HardBlock: false, NeedsConfirmation: false));
        }

        // (15) ราคาโอนระหว่างผู้เกี่ยวโยงกัน — ระบบไม่มีราคาตลาดอ้างอิง จึงเตือน
        // ให้ทบทวน ไม่บวกกลับเอง (บวกกลับผิด = ลูกค้าเสียภาษีเกิน)
        if (ctx.IsRelatedParty && doc.TotalAmount > 0)
        {
            findings.Add(new("RD-65ter(15)", "ป.รัษฎากร §65 ตรี (15)",
                0m, "คู่ค้าเป็นผู้เกี่ยวโยงกัน — ต้องพิสูจน์ว่าราคาเป็นราคาตลาด (transfer pricing) มิฉะนั้นส่วนที่สูงเกินสมควรเป็นรายจ่ายต้องห้าม",
                HardBlock: false, NeedsConfirmation: true));
        }

        // (19) รายจ่ายต่างประเทศที่ไม่เชื่อมโยงกิจการในไทย
        // รอบ PP36 ทีม F2 (E-12): ยอดบวกกลับ = ต้นทุนที่ลงค่าใช้จ่ายจริง (ฐานบรรทัด + VAT เฉพาะบรรทัดที่เคลมไม่ได้ — สูตรเดียวกับข้อรายบรรทัดข้างล่าง)
        // เดิมบวกกลับ TotalAmount ซึ่งของใบบริการต่างประเทศรวม VAT ประเมินเอง (เป็นภาษีซื้อ 11640 ไม่ใช่ค่าใช้จ่าย) และหัก WHT ⇒ บวกกลับผิดทั้งสองทาง
        if (doc.IsForeignService && ctx.LinkedToThaiOperation == false && doc.TotalAmount > 0)
        {
            var foreignExpense = ExpenseCostOf(doc);
            findings.Add(new("RD-65ter(19)", "ป.รัษฎากร §65 ตรี (19)",
                foreignExpense, $"รายจ่ายต่างประเทศที่ไม่เชื่อมโยงกิจการในไทย — บวกกลับเต็มจำนวน ({foreignExpense:N2})",
                HardBlock: false, NeedsConfirmation: false));
        }

        decimal entertainmentTotal = 0m;
        foreach (var line in doc.Lines)
        {
            var code = ""; var name = "";
            if (line.AccountId.HasValue && accountInfo.TryGetValue(line.AccountId.Value, out var ai))
            { code = ai.Code; name = ai.Name; }
            AccountType? accountType = null;
            if (line.AccountId.HasValue && accountTypes != null && accountTypes.TryGetValue(line.AccountId.Value, out var at))
                accountType = at;
            var hay = $"{name} {line.Description}".ToLowerInvariant();
            // ยอดบวกกลับ = ต้นทุนจริงของบรรทัด. รวม VAT **เฉพาะตอนเคลมไม่ได้**
            // (VAT ที่เคลมได้ไปอยู่ในภาษีซื้อ ไม่ได้เป็นค่าใช้จ่าย — เดิมรวมเสมอ
            // ทำให้บวกกลับเกินจริงในบรรทัดที่เคลม VAT ได้)
            var lineAmt = line.Amount + (line.IsVatClaimable ? 0m : line.VatAmount);
            // ฝ่ายค้านรอบ 201 RTX-3: ข้อ (6)(6 ทวิ)(1)(2)(3) เป็นรายจ่าย = บัญชีกำไรขาดทุนเท่านั้น (ผัง 5xxxx · CIT…) — บรรทัดที่ลงหนี้สิน/สินทรัพย์/ทุน
            // (ชำระ ภ.ง.ด.50/51 ผ่าน 21920/11920 · ถอนใช้ส่วนตัว หจก. 31xxx) ไม่ใช่รายจ่ายของงวด ⇒ บวกกลับ = นับภาษีเกิน (ตัวรวม ภ.ง.ด.50 ได้ผลเดียวกัน) ·
            // ไม่รู้ผัง (บรรทัดไม่ผูกบัญชี) = ตรวจตามเดิม · รอบสาม P1-2: ตัดสินด้วย "ชนิดผัง" ก่อน — เลขนำหน้า 5 เป็นแค่ทางสำรองเมื่อไม่รู้ชนิด
            // (เดิมดูเลขอย่างเดียว ⇒ ผังที่ผู้ใช้สร้าง/นำเข้า 6100 ค่าปรับ ชนิด Expense หลุดการบวกกลับเงียบ)
            var profitAndLoss = IsProfitAndLossAccount(code, accountType);

            // (6) เบี้ยปรับ / เงินเพิ่ม / ค่าปรับอาญา — auto nonDeductible, no override
            // รอบ 201 A-TX1: คำอังกฤษจับ "ทั้งคำ" — เดิม Contains("fine") จับ "refined oil"/"define" แล้วบวกกลับเต็มจำนวนเงียบ ๆ
            // (ตอนนี้ผลข้อนี้ขึ้นเป็นคำเตือนก่อนอนุมัติ — คำเตือนที่ฟ้องใบถูก = ปิดด่านโดยไม่ตั้งใจ)
            // "ค่าปรับปรุง/ปรับแต่ง/ปรับเปลี่ยน/ปรับอากาศ" ไม่ใช่ค่าปรับ · "เงินเพิ่มทุน/เงินเพิ่มพิเศษ/เงินเพิ่มค่าครองชีพ" ไม่ใช่เงินเพิ่มภาษี
            if (profitAndLoss && (ThaiPenaltyWord.IsMatch(hay) || hay.Contains("เบี้ยปรับ") || ThaiSurchargeWord.IsMatch(hay)
                || EnglishPenaltyWord.IsMatch(hay)))
            {
                findings.Add(new("RD-65ter(6)", "ป.รัษฎากร §65 ตรี (6)",
                    lineAmt, $"เบี้ยปรับ/เงินเพิ่ม/ค่าปรับ — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (6 ทวิ) ภาษีเงินได้นิติบุคคล — code ขึ้นต้น CIT หรือ name มีคำ
            if (profitAndLoss && (code.StartsWith("CIT", StringComparison.OrdinalIgnoreCase)
                || hay.Contains("ภาษีเงินได้นิติบุคคล") || hay.Contains("corporate income tax")))
            {
                findings.Add(new("RD-65ter(6bis)", "ป.รัษฎากร §65 ตรี (6 ทวิ)",
                    lineAmt, $"ภาษีเงินได้นิติบุคคล — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (1)(2) เงินสำรอง/เงินกองทุน — บวกกลับเต็ม ยกเว้นกองทุนสำรองเลี้ยงชีพ (PVD)
            // ที่จ่ายเข้ากองทุนจดทะเบียนแล้ว (§65 ตรี(2) ข้อยกเว้น). keyword อาจ
            // false-positive → NeedsConfirmation ให้นักบัญชียืนยัน
            // รอบ 201 A-TX1: "reserve" ทั้งคำ — เดิมจับ "hotel reservation"/"reserved seat" เป็นเงินสำรอง แล้วบวกกลับทั้งบรรทัด
            if (profitAndLoss && (hay.Contains("เงินสำรอง") || hay.Contains("สำรองเผื่อ") || HasWord(hay, "reserve") || HasWord(hay, "reserves")
                 || HasWord(hay, "provision") || HasWord(hay, "provisions"))
                && !hay.Contains("สำรองเลี้ยงชีพ") && !hay.Contains("provident") && !hay.Contains("pvd"))
            {
                findings.Add(new("RD-65ter(1)(2)", "ป.รัษฎากร §65 ตรี (1)(2)",
                    lineAmt, $"เงินสำรอง/เงินกองทุน (ที่ไม่ใช่ PVD จดทะเบียน) — บวกกลับ {lineAmt:N2} (โปรดยืนยัน)",
                    HardBlock: false, NeedsConfirmation: true));
                continue;
            }

            // (3) รายจ่ายส่วนตัว/เสน่หา — บวกกลับเต็ม (warning ให้ยืนยัน เพราะอาศัย keyword)
            if (profitAndLoss && (hay.Contains("ส่วนตัว") || hay.Contains("เสน่หา") || hay.Contains("personal use") || hay.Contains("ของขวัญส่วนตัว")))
            {
                findings.Add(new("RD-65ter(3)", "ป.รัษฎากร §65 ตรี (3)",
                    lineAmt, $"รายจ่ายส่วนตัว/เสน่หา — บวกกลับ {lineAmt:N2} (โปรดยืนยันว่าไม่เกี่ยวกิจการ)",
                    HardBlock: false, NeedsConfirmation: true));
                continue;
            }

            // (7) เงินบริจาค — หักได้บางส่วน (ทั่วไป ≤2% กำไรสุทธิ; การศึกษา/กีฬา +2%).
            // cap ต้องคำนวณตอนปิดรอบ (ต้องรู้กำไรสุทธิ) → ที่นี่ flag เตือนอย่างเดียว
            // ไม่บวกกลับเต็ม (AddBack=0) กันคำนวณผิด (over add-back)
            if (hay.Contains("บริจาค") || hay.Contains("donation") || hay.Contains("การกุศล") || hay.Contains("charit"))
            {
                findings.Add(new("RD-65ter(7)", "ป.รัษฎากร §65 ตรี (7)",
                    0m, $"เงินบริจาค {lineAmt:N2} — หักได้ไม่เกิน 2% กำไรสุทธิ (การศึกษา/กีฬา +2%); คำนวณส่วนเกินตอนปิดรอบบัญชี",
                    HardBlock: false, NeedsConfirmation: true));
                continue;
            }

            // (4) ค่ารับรอง — สะสมไว้คิด cap ทีเดียวท้ายสุด
            if (hay.Contains("รับรอง") || hay.Contains("entertain") || hay.Contains("เลี้ยงรับรอง"))
            {
                entertainmentTotal += lineAmt;
                continue;
            }

            // (12) ดอกเบี้ยของเงินทุน/เงินสำรองของตนเอง — บวกกลับเต็ม
            // (ดอกเบี้ยที่กิจการ "จ่ายให้ตัวเอง" ไม่ใช่รายจ่ายจริง)
            if (hay.Contains("ดอกเบี้ยเงินทุน") || hay.Contains("ดอกเบี้ยส่วนของเจ้าของ")
                || hay.Contains("ดอกเบี้ยเงินกองทุนตนเอง") || hay.Contains("interest on capital")
                || hay.Contains("interest on owner"))
            {
                findings.Add(new("RD-65ter(12)", "ป.รัษฎากร §65 ตรี (12)",
                    lineAmt, $"ดอกเบี้ยของเงินทุน/เงินสำรองของตนเอง — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (13) ค่าเช่าทรัพย์สินที่กิจการเป็นเจ้าของเอง — จ่ายให้ตัวเอง
            // ตรวจจากเลขผู้เสียภาษีผู้รับเงิน == เลขของบริษัทเอง
            if (!string.IsNullOrWhiteSpace(ctx.CompanyTaxId)
                && !string.IsNullOrWhiteSpace(payeeTaxId)
                && string.Equals(ctx.CompanyTaxId.Trim(), payeeTaxId.Trim(), StringComparison.Ordinal)
                && (hay.Contains("ค่าเช่า") || hay.Contains("rent")))
            {
                findings.Add(new("RD-65ter(13)", "ป.รัษฎากร §65 ตรี (13)",
                    lineAmt, $"ค่าเช่าทรัพย์สินของกิจการเอง (ผู้รับเงินคือบริษัทเดียวกัน) — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (5) Capex — มูลค่าสูง + ลงเป็นค่าใช้จ่าย → ควร capitalize (warning)
            if (lineAmt >= capexThreshold && IsExpenseLedger(code, accountType))
            {
                findings.Add(new("RD-65ter(5)", "ป.รัษฎากร §65 ตรี (5) + พ.ร.ฎ.145",
                    0m, $"มูลค่า {lineAmt:N2} ≥ {capexThreshold:N0} — ถ้าอายุใช้งาน >1 ปี ต้องบันทึกเป็นสินทรัพย์ (คิดค่าเสื่อม) ไม่ใช่ค่าใช้จ่าย",
                    HardBlock: false, NeedsConfirmation: true));
                continue;
            }
        }

        // (4) ค่ารับรอง cap = MAX(0.3% revenue, 0.3% paid-up capital) ไม่เกิน 10M
        // — cap เป็น "per fiscal year". รวม YTD ที่อนุมัติไปก่อนหน้ากับยอด
        // entertainment ในเอกสารนี้ → คำนวณ excess รวม แล้วเฉลี่ยส่วนเกินที่
        // เป็นของเอกสารนี้ (clamp ไม่ให้เกิน entertainmentTotal ของ doc)
        // ไม่มีฐานคำนวณเลย (ยังไม่ปิดรอบ/ไม่ได้ส่ง context) → **ห้ามคิด cap = 0**
        // เพราะจะบวกกลับค่ารับรองทั้งก้อน = ลูกค้าเสียภาษีเกิน. เตือนให้ไปคำนวณ
        // ตอนปิดรอบแทน
        if (entertainmentTotal > 0
            && (ctx.AnnualRevenue ?? 0m) <= 0m && (ctx.PaidUpCapital ?? 0m) <= 0m)
        {
            findings.Add(new("RD-65ter(4)", "ป.รัษฎากร §65 ตรี (4) + กฎกระทรวง 143",
                0m, $"ค่ารับรอง {entertainmentTotal:N2} — ยังไม่มีฐานรายได้/ทุนจดทะเบียนสำหรับคำนวณเพดาน (0.3%) โปรดตรวจส่วนเกินตอนปิดรอบบัญชี",
                HardBlock: false, NeedsConfirmation: true));
        }
        else if (entertainmentTotal > 0)
        {
            var cap = Math.Min(
                Math.Max((ctx.AnnualRevenue ?? 0m) * 0.003m, (ctx.PaidUpCapital ?? 0m) * 0.003m),
                10_000_000m);
            var priorYtd = ctx.PriorYtdEntertainmentExpense ?? 0m;
            var combined = priorYtd + entertainmentTotal;
            var totalExcess = Math.Max(0m, combined - cap);
            // ส่วนเกินที่ doc นี้รับผิดชอบ = ส่วนเกินรวม − ส่วนเกินก่อนหน้า
            var priorExcess = Math.Max(0m, priorYtd - cap);
            var thisDocExcess = Math.Min(entertainmentTotal, Math.Max(0m, totalExcess - priorExcess));
            if (thisDocExcess > 0)
            {
                var ytdNote = priorYtd > 0
                    ? $" (YTD ก่อนหน้า {priorYtd:N2} + ใบนี้ {entertainmentTotal:N2} = {combined:N2})"
                    : "";
                findings.Add(new("RD-65ter(4)", "ป.รัษฎากร §65 ตรี (4) + กฎกระทรวง 143",
                    thisDocExcess, $"ค่ารับรอง{ytdNote} เกินเพดาน {cap:N2} (0.3% รายได้/ทุน, ≤10M) — บวกกลับส่วนเกิน {thisDocExcess:N2}",
                    HardBlock: false, NeedsConfirmation: false));
            }
        }

        // รอบ 204 (คำตัดสินข้อ 148 (4)) — ภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทน: ตัวตัดสินเดียว PayerBorneFindings (ใช้ร่วมกับการแก้ "มีสัญญา" หลังอนุมัติ)
        findings.AddRange(PayerBorneFindings(doc, ctx.HasAttachment));

        var total = findings.Sum(f => f.AddBackAmount);
        return new Result(total, findings);
    }

    /// <summary>รหัสกฎของภาษีที่ผู้จ่ายออกแทน (รอบ 204) — ขึ้นต้น "RD-65ter" ให้ชุดคำเตือนตอนอนุมัติอ่านรหัสได้ (<c>Section65TerApprovalWarnings.RuleCodeOf</c>)</summary>
    public const string PayerBorneWhtRuleCode = "RD-65ter(3)-WHT-BORNE";

    /// <summary>รอบ 204 (ทีม B · §65 ตรี (3) · ม.39) — ผู้จ่ายรับภาระภาษีตามสัญญา (<c>Document.WhtBorneByContract</c>): ไม่บวกกลับ · บันทึกร่องรอยรหัสนี้แทน</summary>
    public const string PayerBorneContractRuleCode = "RD-65ter(3)-WHT-BORNE-CONTRACT";

    /// <summary>มาตราอ้างอิงของ <see cref="PayerBorneContractRuleCode"/></summary>
    public const string PayerBorneContractLegalReference = "§65 ตรี (3) · ม.39 — ผู้จ่ายรับภาระตามสัญญา (เก็บสัญญาเป็นหลักฐาน)";

    /// <summary>
    /// **§65 ตรี (3) ของภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทน — ตัวตัดสินเดียว** (ตอนอนุมัติผ่าน <see cref="Evaluate"/> และตอนแก้ "มีสัญญา" หลังอนุมัติ)
    /// <list type="bullet">
    /// <item>ไม่มีสัญญา (<c>WhtBorneByContract=false</c>) ⇒ <see cref="PayerBorneWhtRuleCode"/> บวกกลับส่วนที่ออกแทน (ให้โดยเสน่หา · กฎ L(3) default nonDeductible + เตือน)</item>
    /// <item>มีสัญญา ⇒ <see cref="PayerBorneContractRuleCode"/> ยอดบวกกลับ 0 (ร่องรอย audit) · ไม่มีไฟล์แนบ (<paramref name="hasAttachment"/> = false) ⇒ ข้อสังเกต "แนบสัญญา"
    /// (NeedsConfirmation — ยกขึ้นตอนอนุมัติ ไม่บล็อก) · มีไฟล์แนบ/ไม่ทราบ ⇒ บันทึกอย่างเดียว</item>
    /// <item>ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จที่ปิดหนี้ใบต้นทาง ไม่ลงค่าใช้จ่ายซ้ำ (JE สาย settlement) ⇒ ไม่มีผล</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<Finding> PayerBorneFindings(Document doc, bool? hasAttachment)
    {
        var postsBorneExpense = !(doc.RelatedDocumentId.HasValue
            && doc.DocumentType is DocumentType.PaymentVoucher or DocumentType.CertificateInLieu);
        if (!postsBorneExpense || doc.WhtPayerBorneAmount <= 0m || !Accounting.Helpers.WhtGrossUp.IsPayerBorne(doc.WhtCertType))
            return Array.Empty<Finding>();
        var label = Accounting.Helpers.WhtGrossUp.Label(doc.WhtCertType);
        if (doc.WhtBorneByContract)
        {
            var noAttachment = hasAttachment == false;
            return new[]
            {
                new Finding(PayerBorneContractRuleCode, PayerBorneContractLegalReference, 0m,
                    $"ภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทน ({label}) {doc.WhtPayerBorneAmount:N2} — ผู้ใช้ระบุว่าสัญญากำหนดให้ผู้จ่ายรับภาระภาษี "
                    + "⇒ หักเป็นรายจ่ายได้ ไม่บวกกลับ ภ.ง.ด.50"
                    + (noAttachment
                        ? " · ยังไม่มีไฟล์แนบบนเอกสาร — แนบสัญญา/ข้อตกลงเป็นหลักฐาน (ไม่บล็อก)"
                        : " · เก็บสัญญาไว้เป็นหลักฐาน"),
                    HardBlock: false, NeedsConfirmation: noAttachment),
            };
        }
        return new[]
        {
            new Finding(PayerBorneWhtRuleCode, "ป.รัษฎากร §65 ตรี (3) · ม.39 (ภาษีที่ออกแทนเป็นเงินได้ของผู้รับ)",
                doc.WhtPayerBorneAmount,
                $"ภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทน ({label}) {doc.WhtPayerBorneAmount:N2} — "
                + "หักเป็นรายจ่ายได้เฉพาะเมื่อสัญญา/ข้อตกลงกำหนดให้ผู้จ่ายรับภาระภาษี มิฉะนั้นเป็นการให้โดยเสน่หา · ระบบบวกกลับไว้ก่อน "
                + "(มีสัญญา ⇒ ติ๊ก \"ผู้จ่ายรับภาระภาษีตามสัญญา\" บนเอกสาร — หลังอนุมัติแก้ได้ที่ปุ่มเดียวกัน — พร้อมแนบสัญญา)",
                HardBlock: false, NeedsConfirmation: true),
        };
    }

    /// <summary>
    /// **แทนที่ผล §65 ตรี (3) ของภาษีที่ออกแทนในผลที่บันทึกไว้แล้ว** (<c>Document.NonDeductibleRuleJson</c>) — ใช้ตอนแก้ "มีสัญญา" หลังอนุมัติ:
    /// ข้ออื่นคงเดิมทุกตัวอักษร (ไม่ประเมินใหม่ทั้งใบ — เพดานค่ารับรอง YTD ฯลฯ ต้องไม่ขยับตามวันที่กด) · คืนยอดบวกกลับรวมใหม่ + JSON (ว่าง ⇒ null)
    /// · JSON เดิมอ่านไม่ได้ ⇒ ล้มดัง (ไม่เขียนทับข้อมูลที่ไม่รู้จัก)
    /// </summary>
    public static (decimal TotalAddBack, string? Json) ReplacePayerBorneFindings(string? existingJson, IReadOnlyList<Finding> borneFindings)
    {
        var kept = new List<(string RuleCode, string LegalReference, decimal AddBackAmount, string Message)>();
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            using var jdoc = JsonDocument.Parse(existingJson);
            if (jdoc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("ผล §65 ตรีที่บันทึกบนเอกสารไม่ใช่รายการ — แก้ไม่ได้อัตโนมัติ ติดต่อผู้ดูแลระบบ");
            foreach (var el in jdoc.RootElement.EnumerateArray())
            {
                var code = el.TryGetProperty("RuleCode", out var rc) ? rc.GetString() ?? "" : "";
                if (code == PayerBorneWhtRuleCode || code == PayerBorneContractRuleCode) continue;
                kept.Add((code,
                    el.TryGetProperty("LegalReference", out var lr) ? lr.GetString() ?? "" : "",
                    el.TryGetProperty("AddBackAmount", out var ab) && ab.TryGetDecimal(out var amt) ? amt : 0m,
                    el.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : ""));
            }
        }
        kept.AddRange(borneFindings.Select(f => (f.RuleCode, f.LegalReference, f.AddBackAmount, f.Message)));
        if (kept.Count == 0) return (0m, null);
        var json = JsonSerializer.Serialize(kept.Select(f => new { f.RuleCode, f.LegalReference, f.AddBackAmount, f.Message }));
        return (kept.Sum(f => f.AddBackAmount), json);
    }

    /// <summary>ฝ่ายค้านรอบ 201 RTX-4: ค่าปรับภาษาอังกฤษทั้งคำ (penalty/penalties · fine/fines/fined · surcharge/surcharges) ·
    /// "fuel surcharge" (ค่าน้ำมันส่วนเพิ่มของขนส่ง) ไม่ใช่ค่าปรับ</summary>
    private static readonly System.Text.RegularExpressions.Regex EnglishPenaltyWord =
        new("(?<![a-z0-9-])(?:penalty|penalties|fine|fines|fined|(?<!fuel )surcharges?)(?![a-z0-9-])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>บรรทัดนี้เป็นรายจ่ายของงวดหรือไม่ — รู้ชนิดผัง = ตัดสินด้วยชนิด (Expense เท่านั้น · หนี้สิน/สินทรัพย์/ทุน/รายได้ = ไม่ใช่) ·
    /// ไม่รู้ชนิด = เลขนำหน้า 5 / CIT… เป็นทางสำรอง · ไม่ผูกผังเลย (เลขว่าง) = ตรวจตามเดิม</summary>
    private static bool IsProfitAndLossAccount(string code, AccountType? type)
    {
        if (type.HasValue) return type.Value == AccountType.Expense;
        return string.IsNullOrEmpty(code) || code.StartsWith("5", StringComparison.Ordinal)
               || code.StartsWith("CIT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>(5) capex: บรรทัดที่ลงเป็นค่าใช้จ่ายจริง (ต้องผูกผัง) — ชนิด Expense หรือไม่รู้ชนิดแต่เลขนำหน้า 5</summary>
    private static bool IsExpenseLedger(string code, AccountType? type)
        => type.HasValue ? type.Value == AccountType.Expense : code.StartsWith("5", StringComparison.Ordinal);

    private static readonly System.Text.RegularExpressions.Regex ThaiPenaltyWord =
        new("ค่าปรับ(?!ปรุง|แต่ง|เปลี่ยน|อากาศ)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ThaiSurchargeWord =
        new("เงินเพิ่ม(?!ทุน|พิเศษ|ค่าครองชีพ|เติม)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>คำอังกฤษ "ทั้งคำ" ใน hay (ตัวพิมพ์เล็กแล้ว) — ขอบคำ = ไม่ใช่ตัวอักษร/ตัวเลขละติน/ขีด (อักษรไทยติดกันนับเป็นขอบ ·
    /// "fine-tuning" ไม่ใช่ค่าปรับ)</summary>
    private static bool HasWord(string hay, string word)
        => System.Text.RegularExpressions.Regex.IsMatch(hay, "(?<![a-z0-9-])" + System.Text.RegularExpressions.Regex.Escape(word) + "(?![a-z0-9-])");
}
