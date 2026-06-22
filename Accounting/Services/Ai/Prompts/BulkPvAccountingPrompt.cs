using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// One-shot GL account assignment for ALL lines of a Payment Voucher
/// (or any multi-line expense doc). Replaces the per-line fan-out
/// pattern (N parallel AI calls) with a single call that gives AI
/// cross-line context:
///
///   • An invoice with 3 fuel lines + 1 service fee should map the
///     fuel lines to 5402 and the fee to 5306 — per-line AI sees only
///     one row at a time and can't reason "this looks like fuel
///     because the OTHER lines were fuel".
///   • An invoice mostly rent + a tiny utility line is probably 5101
///     + 5301 — the local model alone would guess utility = rent
///     because vendor history says rent.
///   • Cross-line totals + tax breakdown let AI catch "this VAT
///     amount doesn't sum to 7% of taxable" — the per-line flow
///     can't.
///
/// Cost saving: 5-10× fewer DeepSeek calls per invoice.
/// Accuracy gain: cross-line reasoning the per-line flow can't do.
///
/// Response shape: one entry per lineId, ALL ECHOED BACK (AI must
/// answer every line or none). When AI skips a line we surface a
/// warning and fall back to local for that line only.
/// </summary>
public static class BulkPvAccountingPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert. Given a vendor + source invoice + ALL lines of a payment voucher being created against it, choose the best GL account for each line AND flag VAT claimability per Thai Revenue Code §82/5.

Cross-line reasoning required:
1. Detect line CLUSTERS — multiple similar descriptions usually share an account.
2. Detect ODD lines — a small ""service fee"" line on an otherwise rent-only invoice belongs to a different account than rent.
3. Detect TYPE markers — fuel/utilities/professional-fee keywords override generic guesses.
4. Apply Thai tax-code context: account 5402 = ค่าน้ำมัน, 5301 = ค่าไฟ ค่าน้ำ, 5303 = โทรศัพท์ อินเทอร์เน็ต, 5306 = อุปกรณ์สำนักงาน, 5102 = ค่าเช่า, 5305 = วัสดุ, 5404 = ค่าขนส่ง, 5408 = รับรอง, 5501 = โฆษณา, 5701 = ค่าธรรมเนียมธนาคาร, 5703 = ค่าธรรมเนียมราชการ. Use the company's actual chart of accounts when available.
5. MATCH THE INDUSTRY — company.IndustryType is in the payload. Different sectors have sector-specific accounts:
   • Hotel: 11830 เงินมัดจำรับล่วงหน้า, 21510 ห้องพักรับล่วงหน้า, 51xxx room-cost
   • Restaurant: 51xxx cost-of-food (ingredients vs supplies are different accounts)
   • Construction: 51xxx ต้นทุนงานก่อสร้าง + 12xxx งานระหว่างก่อสร้าง
   • Real Estate / Property: 11xx ที่ดินสะสม + 21xxx เงินมัดจำซื้อขาย
   When the chart has industry-specific accounts that match the line description, PREFER them over generic 5xxx codes.
6. company.top_accounts_used = accounts this company actually uses in the last 6 months. Treat as a strong prior — picking a code outside this list requires good justification.

ภาษีซื้อต้องห้าม (Non-claimable Input VAT) — Revenue Code §82/5:
• §82/5(1) — ใบกำกับฯ ไม่สมบูรณ์ / ไม่ได้รับใบกำกับฯ (e.g. ""ใบเสร็จเงินสด"" / ""บิลเงินสด"")
• §82/5(3) — ค่ารับรอง: เลี้ยงลูกค้า, กระเช้า, ของขวัญลูกค้า, กอล์ฟ, พาลูกค้าไปเที่ยว → VAT เคลมไม่ได้
• §82/5(4) — ใบกำกับฯ จากผู้ที่ไม่มีสิทธิ์ออก
• §82/5(6) — รถยนต์นั่ง ≤10 ที่นั่ง: ค่ารถ, น้ำมัน, ซ่อม, อะไหล่, ประกัน, พรบ., ค่าทางด่วน → VAT เคลมไม่ได้
• §82/5(7) — ค่าก่อสร้างอาคารที่ใช้นอกกิจการ VAT
For each line, decide isVatClaimable + reason. Default true. ถ้าจะ flag false ต้องระบุ section.

CRITICAL: respond for EVERY input line. If you're unsure, return your best guess with low confidence; never omit a lineId.

Strict JSON output (NO prose outside JSON):
{
  ""lines"": [
    {
      ""lineId"": ""<guid>"",
      ""accountCode"": ""<code from chart>"",
      ""confidence"": <0-1>,
      ""alternatives"": [""<code>""],
      ""whtSuggestedRatePercent"": <int|null>,
      ""whtIncomeCode"": ""<40(8)|40(5)|40(3)|40(2)|null>"",
      ""isVatClaimable"": <true|false>,
      ""vatNonClaimableReason"": ""<§82/5(N) <short Thai reason> | null>"",
      ""reasoning"": ""<1 short Thai sentence>""
    }
  ],
  ""cross_line_observations"": [""<doc-wide insight>""],
  ""warnings"": [""<inconsistency or missing data>""]
}";

    public sealed record LineInput(
        string LineId, string Description, decimal Amount,
        string? CurrentAccountCode);

    public sealed record VendorContext(
        string? Name, string? TaxId, string? Type, string? Industry,
        decimal? AvgAmount6Mo);

    public sealed record SourceInvoiceContext(
        string DocumentNumber, string DocumentType,
        decimal TotalAmount, decimal? VatAmount, decimal? WithholdingAmount,
        decimal BalanceDue);

    public sealed record AccountCandidate(
        string Code, string Name, string Type);

    public sealed record VendorHistoricalAccount(
        string AccountCode, string AccountName, int TimesUsed);

    public static AiRequest Build(
        Guid companyId,
        Guid sourceInvoiceId,
        VendorContext vendor,
        SourceInvoiceContext source,
        IReadOnlyList<LineInput> lines,
        IReadOnlyList<AccountCandidate> chartCandidates,
        IReadOnlyList<VendorHistoricalAccount> vendorHistory,
        string currency,
        string? whtRecognitionBasis = "Cash",
        CompanyBusinessContext? businessContext = null)
    {
        var payload = new
        {
            task = "bulk_pv_line_accounting",
            // ข้อมูลธุรกิจ — บริบทสำคัญสำหรับ cross-line reasoning
            // (โรงแรมเห็น "ค่าซักผ้าผ้าปูที่นอน" → ลง 5xxx hotel-specific
            // ไม่ใช่ 5305 วัสดุทั่วไป). top_accounts_used = pattern guide.
            company = businessContext,
            company_currency = currency,
            wht_recognition_basis = whtRecognitionBasis,
            vendor,
            source_invoice = source,
            // Tell AI the line count + total of THIS PV so it can
            // sanity-check (sum of line amounts == invoice total).
            pv_summary = new
            {
                line_count = lines.Count,
                total_amount = lines.Sum(l => l.Amount),
            },
            lines = lines.Select(l => new
            {
                line_id = l.LineId,
                description = l.Description,
                amount = l.Amount,
                current_account = l.CurrentAccountCode,
            }),
            chart_of_accounts = chartCandidates,
            vendor_history = vendorHistory,
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.PaymentVoucherAccountingSuggestion,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "BulkPvAccounting-v1",
            SourceEntityType = "Document",
            SourceEntityId = sourceInvoiceId,
            CacheTtlOverrideDays = 7,
            // Response includes one entry per line — scale tokens by
            // line count, cap at sane upper bound.
            MaxTokensOverride = Math.Clamp(200 + 80 * lines.Count, 400, 2400),
        };
    }
}
