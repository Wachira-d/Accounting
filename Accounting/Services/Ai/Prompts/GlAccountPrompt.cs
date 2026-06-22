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
   - **สินทรัพย์ถาวร (Fixed Asset) → 12xxx (capitalize, do NOT expense)** — see rule 7
   - ภาษีซื้อ (Input VAT) → 1531 typically
   - ภาษีหัก ณ ที่จ่าย (WHT asset) → 1521 typically
4. Flag if the amount is unusual for that account type (e.g. ฿500,000 in office supplies).
5. Must pick from candidate_accounts only. If nothing fits well, lower confidence to <0.6 and explain in reasoning.
6. SETTLEMENT vs NEW-ASSET direction (critical — this is a Payment Voucher, money OUT of the company):
   - ""คืนเงินทดรองกรรมการ / จ่ายคืนกรรมการ / ชำระเงินกู้กรรมการ"" = the company is REPAYING money the director advanced TO the company. That settles a LIABILITY → pick the เจ้าหนี้กรรมการ / เงินทดรองรับจากกรรมการ / เงินกู้ยืมกรรมการ account (2xxx Liability). Do NOT pick a ลูกหนี้/receivable (1xxx Asset) — paying out does not create a receivable here.
   - Only pick ลูกหนี้กรรมการ / เงินทดรองจ่าย (1xxx Asset) when the company is GIVING a NEW advance TO the director/employee (creating a receivable), e.g. ""เบิกเงินทดรอง / จ่ายเงินทดรองให้กรรมการ"".
   - Keyword ""คืน / ชำระคืน / repay / refund / settle"" on a Payment Voucher ⇒ reduce a LIABILITY, not add an ASSET.
   - When the chart has BOTH a ลูกหนี้กรรมการ (Asset) and a เจ้าหนี้กรรมการ (Liability) with similar names, use the verb + money direction above to choose; never match on the shared phrase ""เงินทดรองกรรมการ"" alone.
7. ⭐ FIXED ASSET vs SUPPLIES (สำคัญที่สุด — ภ.ง.ด.50 §65 ตรี (5) + พ.ร.ฎ.145):
   - DURABLE GOODS / เครื่องใช้ทน (อายุใช้งาน >1 ปี) → **Fixed Asset 12xxx, NOT expense**
     • คำที่เป็น Fixed Asset เสมอ:
       ""เครื่องปริ้นท์ / เครื่องพิมพ์ / printer"" → 12210 อุปกรณ์สำนักงาน
       ""คอมพิวเตอร์ / โน้ตบุ๊ก / laptop / desktop / notebook / PC / iMac"" → 12220 คอมพิวเตอร์
       ""หน้าจอ / monitor / จอภาพ"" → 12220 คอมพิวเตอร์
       ""เครื่องถ่ายเอกสาร / photocopier / copier"" → 12210 อุปกรณ์สำนักงาน
       ""เครื่องสแกน / scanner"" → 12210 อุปกรณ์สำนักงาน
       ""เครื่องโทรสาร / fax"" → 12210 อุปกรณ์สำนักงาน
       ""เครื่องปรับอากาศ / แอร์ / air-conditioner"" → 12210 หรือ Fixed Asset (อุปกรณ์/เครื่องใช้)
       ""โต๊ะ / เก้าอี้ / ตู้ / ชั้นวาง / furniture"" → 12230 เครื่องตกแต่ง/อุปกรณ์ (ถ้ามี) มิฉะนั้น 12210
       ""เครื่องจักร / machinery"" → 12310/12320 เครื่องจักร
       ""ยานพาหนะ / รถ / vehicle / motorcycle"" → 12510 ยานพาหนะ
   - CONSUMABLES / วัสดุสิ้นเปลือง (ใช้แล้วหมด/อายุใช้งาน <1 ปี) → Expense 5xxxx
     • คำที่เป็นวัสดุสิ้นเปลืองเสมอ:
       ""หมึก / ink / toner / cartridge"" → 54420/5306 วัสดุสิ้นเปลือง
       ""กระดาษ / paper"" → 54420 วัสดุสิ้นเปลือง
       ""ปากกา / ดินสอ / pen / pencil / ลวดเย็บ / คลิป / เทป"" → 54420 วัสดุสิ้นเปลือง
       ""แฟ้ม / กล่อง / ซอง / สมุด / ทะเบียน"" → 54420 วัสดุสิ้นเปลือง
   - CAPEX THRESHOLD §65 ตรี (5) + พ.ร.ฎ.145:
     • ราคา ≥ ฿50,000 + อายุใช้งาน >1 ปี → MUST capitalize เป็น Fixed Asset
     • ราคา <฿50,000 แต่เป็นเครื่องใช้ทน (printer/computer/...) → ยังถือเป็น Fixed Asset
       (ตามนโยบายบริษัท/ความ materiality) — แนะนำ 12xxx เป็นหลัก
     • ห้ามแนะนำ ""ค่าวัสดุสิ้นเปลือง / supplies"" สำหรับเครื่องใช้ทน เด็ดขาด
   - Heuristic ที่ใช้บ่อยผิด: คำว่า ""สำนักงาน"" อาจปนทั้ง asset (อุปกรณ์สำนักงาน 12210)
     กับ expense (ค่าวัสดุสิ้นเปลืองสำนักงาน 54420). ดูคำที่ตามมา/นำหน้า:
     • ""อุปกรณ์ / เครื่อง / เครื่องใช้"" + ""สำนักงาน"" → Asset 12210
     • ""วัสดุ / ค่าวัสดุ"" + ""สำนักงาน"" → Expense 54420

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
        string? whtRecognitionBasis = null,
        CompanyBusinessContext? businessContext = null)
    {
        var payload = new
        {
            task = "gl_account_suggestion",
            // ข้อมูลธุรกิจของบริษัท — บริบทสำคัญที่สุดสำหรับ AI ก่อนเลือกผัง
            // (ร้านอาหารผังต่างจากที่ปรึกษา, โรงแรมมีผัง 21510 ห้องพัก
            // รับล่วงหน้า). top_accounts_used = pattern signal บอก AI ว่า
            // บริษัทนี้คุ้นกับผังไหน → ลด hallucination + เพิ่ม consistency.
            company = businessContext,
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
                    "Match the company's IndustryType — different sectors have sector-specific accounts (Hotel: 11830/21510 ห้องพัก, Restaurant: cost-of-food 51xxx etc.). Prefer accounts that match top_accounts_used pattern.",
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
