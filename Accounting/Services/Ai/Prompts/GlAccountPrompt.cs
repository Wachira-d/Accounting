using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Builds AiRequest for GL account suggestion. Use cases:
///   • OCR line item → which expense / asset account to debit
///   • Manual payment-voucher entry → which account
///   • The user-called-out case: "เลือกผังบัญชีตอนสร้างใบสำคัญจ่าย"
///
/// The vendor's historical accounts + tenant's chart-of-accounts ALL
/// go into the prompt so AI has full context to choose.
/// </summary>
public static class GlAccountPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert. Given a vendor + line description + amount, pick the correct GL account code from the company's chart of accounts.

Rules:
1. Prefer accounts the vendor has used historically (vendor_history) — consistency matters for audit.
2. Match the line description against account names + types.
3. Thai accounting standards:
   - ค่าใช้จ่ายในการดำเนินงาน → 5xxx series
   - สินค้าคงเหลือ / ต้นทุนขาย → 5100-5199
   - สินทรัพย์ถาวร (≥15,000 บาท + อายุการใช้งาน>1 ปี) → 1500-1699 (capitalize, do NOT expense)
   - ภาษีซื้อ (Input VAT) → 1531 typically
   - ภาษีหัก ณ ที่จ่าย (WHT asset) → 1521 typically
4. Flag if the amount is unusual for that account type (e.g. ฿500,000 in office supplies).
5. Must pick from candidate_accounts only. If nothing fits well, lower confidence to <0.6 and explain in reasoning.

Respond ONLY as JSON:
{
  ""primary"": ""<accountCode>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<accountCode>"", ""<accountCode>""],
  ""risks"": [""<short risk>""],
  ""compliance_flags"": [""<Thai tax/accounting issue>""],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""<short action>""]
}";

    public sealed record AccountCandidate(string Code, string Name, string Type, bool IsActive);
    public sealed record VendorHistoricalAccount(string Code, string Name, int TimesUsed, decimal AvgAmount);

    public static AiRequest Build(
        Guid companyId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        IReadOnlyList<AccountCandidate> candidateAccounts,
        IReadOnlyList<VendorHistoricalAccount> vendorHistory,
        string? localBestAccountCode, decimal? localConfidence,
        AiFeatureKey featureKey = AiFeatureKey.GlAccountSuggestion,
        string? localModelVersion = null,
        string? sourceEntityType = null, Guid? sourceEntityId = null,
        string? whtRecognitionBasis = null)
    {
        var payload = new
        {
            task = "gl_account_suggestion",
            vendor = new
            {
                name = vendorName ?? "",
                tax_id = vendorTaxId ?? "",
                industry = vendorIndustry ?? "",
            },
            line = new
            {
                description = lineDescription,
                amount,
                currency,
            },
            candidate_accounts = candidateAccounts.Select(a => new
            {
                code = a.Code,
                name = a.Name,
                type = a.Type,
                is_active = a.IsActive,
            }),
            vendor_history = vendorHistory.Select(h => new
            {
                code = h.Code,
                name = h.Name,
                times_used = h.TimesUsed,
                avg_amount = h.AvgAmount,
            }),
            local_model = new
            {
                pick = localBestAccountCode,
                confidence = localConfidence,
            },
            thai_context = new
            {
                rules = new[]
                {
                    "Fixed asset threshold: ≥฿15,000 + life >1 year → capitalize (Thai Revenue Department guidance).",
                    "Input VAT (ภาษีซื้อ) booked to 1531 only if vendor issued valid Tax Invoice per §86.",
                    "WHT asset (ภาษีถูกหัก ณ ที่จ่าย) booked at " + (whtRecognitionBasis ?? "tenant's chosen basis: Cash (§50) or Accrual"),
                },
            },
        };
        return new AiRequest
        {
            FeatureKey = featureKey,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localBestAccountCode,
            LocalConfidence = localConfidence,
            LocalModelVersion = localModelVersion,
            LocalAlternatives = vendorHistory.Take(5).Select(h => h.Code).ToList(),
            SourceEntityType = sourceEntityType,
            SourceEntityId = sourceEntityId,
            CacheTtlOverrideDays = 14,  // Vendor's typical account stable for ~2 wks
        };
    }
}
