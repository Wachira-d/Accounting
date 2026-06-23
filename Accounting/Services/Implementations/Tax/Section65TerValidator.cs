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
        decimal? PriorYtdEntertainmentExpense = null);

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

        decimal entertainmentTotal = 0m;
        foreach (var line in doc.Lines)
        {
            var code = ""; var name = "";
            if (line.AccountId.HasValue && accountInfo.TryGetValue(line.AccountId.Value, out var ai))
            { code = ai.Code; name = ai.Name; }
            var hay = $"{name} {line.Description}".ToLowerInvariant();
            var lineAmt = line.Amount + line.VatAmount;  // รวม VAT (ส่วนที่เป็นต้นทุนจริง)

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

            // (4) ค่ารับรอง — สะสมไว้คิด cap ทีเดียวท้ายสุด
            if (hay.Contains("รับรอง") || hay.Contains("entertain") || hay.Contains("เลี้ยงรับรอง"))
            {
                entertainmentTotal += lineAmt;
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
        if (entertainmentTotal > 0)
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
