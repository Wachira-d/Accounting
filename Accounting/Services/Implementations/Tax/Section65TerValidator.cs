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
        bool StrictPayeeIdentification = false);

    /// <summary>accountInfo: map AccountId → (code, name) สำหรับตรวจชนิดบัญชี.
    /// payeeName/payeeTaxId: ชื่อ+เลขผู้รับเงิน (จาก Contact ของเอกสารซื้อ).</summary>
    public static Result Evaluate(
        Document doc,
        IReadOnlyDictionary<Guid, (string Code, string Name)> accountInfo,
        string? payeeName, string? payeeTaxId,
        Context ctx,
        decimal capexThreshold = DefaultCapexThreshold)
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
                0m, "ไม่มีเลขประจำตัวผู้เสียภาษีของผู้รับเงิน — เสี่ยงถูกถือเป็นรายจ่ายต้องห้าม โปรดเพิ่มก่อนปิดรอบ",
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
        if (doc.IsForeignService && ctx.LinkedToThaiOperation == false && doc.TotalAmount > 0)
        {
            findings.Add(new("RD-65ter(19)", "ป.รัษฎากร §65 ตรี (19)",
                doc.TotalAmount, $"รายจ่ายต่างประเทศที่ไม่เชื่อมโยงกิจการในไทย — บวกกลับเต็มจำนวน ({doc.TotalAmount:N2})",
                HardBlock: false, NeedsConfirmation: false));
        }

        decimal entertainmentTotal = 0m;
        foreach (var line in doc.Lines)
        {
            var code = ""; var name = "";
            if (line.AccountId.HasValue && accountInfo.TryGetValue(line.AccountId.Value, out var ai))
            { code = ai.Code; name = ai.Name; }
            var hay = $"{name} {line.Description}".ToLowerInvariant();
            // ยอดบวกกลับ = ต้นทุนจริงของบรรทัด. รวม VAT **เฉพาะตอนเคลมไม่ได้**
            // (VAT ที่เคลมได้ไปอยู่ในภาษีซื้อ ไม่ได้เป็นค่าใช้จ่าย — เดิมรวมเสมอ
            // ทำให้บวกกลับเกินจริงในบรรทัดที่เคลม VAT ได้)
            var lineAmt = line.Amount + (line.IsVatClaimable ? 0m : line.VatAmount);

            // (6) เบี้ยปรับ / เงินเพิ่ม / ค่าปรับอาญา — auto nonDeductible, no override
            if (hay.Contains("ค่าปรับ") || hay.Contains("เบี้ยปรับ") || hay.Contains("เงินเพิ่ม")
                || hay.Contains("penalty") || hay.Contains("surcharge") || hay.Contains("fine"))
            {
                findings.Add(new("RD-65ter(6)", "ป.รัษฎากร §65 ตรี (6)",
                    lineAmt, $"เบี้ยปรับ/เงินเพิ่ม/ค่าปรับ — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (6 ทวิ) ภาษีเงินได้นิติบุคคล — code ขึ้นต้น CIT หรือ name มีคำ
            if (code.StartsWith("CIT", StringComparison.OrdinalIgnoreCase)
                || hay.Contains("ภาษีเงินได้นิติบุคคล") || hay.Contains("corporate income tax"))
            {
                findings.Add(new("RD-65ter(6bis)", "ป.รัษฎากร §65 ตรี (6 ทวิ)",
                    lineAmt, $"ภาษีเงินได้นิติบุคคล — บวกกลับเต็มจำนวน ({lineAmt:N2})",
                    HardBlock: false, NeedsConfirmation: false));
                continue;
            }

            // (1)(2) เงินสำรอง/เงินกองทุน — บวกกลับเต็ม ยกเว้นกองทุนสำรองเลี้ยงชีพ (PVD)
            // ที่จ่ายเข้ากองทุนจดทะเบียนแล้ว (§65 ตรี(2) ข้อยกเว้น). keyword อาจ
            // false-positive → NeedsConfirmation ให้นักบัญชียืนยัน
            if ((hay.Contains("เงินสำรอง") || hay.Contains("สำรองเผื่อ") || hay.Contains("reserve") || hay.Contains("provision"))
                && !hay.Contains("สำรองเลี้ยงชีพ") && !hay.Contains("provident") && !hay.Contains("pvd"))
            {
                findings.Add(new("RD-65ter(1)(2)", "ป.รัษฎากร §65 ตรี (1)(2)",
                    lineAmt, $"เงินสำรอง/เงินกองทุน (ที่ไม่ใช่ PVD จดทะเบียน) — บวกกลับ {lineAmt:N2} (โปรดยืนยัน)",
                    HardBlock: false, NeedsConfirmation: true));
                continue;
            }

            // (3) รายจ่ายส่วนตัว/เสน่หา — บวกกลับเต็ม (warning ให้ยืนยัน เพราะอาศัย keyword)
            if (hay.Contains("ส่วนตัว") || hay.Contains("เสน่หา") || hay.Contains("personal use") || hay.Contains("ของขวัญส่วนตัว"))
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
            if (lineAmt >= capexThreshold && code.StartsWith("5"))
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

        var total = findings.Sum(f => f.AddBackAmount);
        return new Result(total, findings);
    }
}
