using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// ถาม "ครู" ว่า <b>เราเป็นผู้ซื้อหรือผู้ขายของกระดาษใบนี้</b> — เรียกเฉพาะเคสที่
/// <c>Helpers/OcrPartyResolver</c> บอกว่ากติกาตัดสินไม่ได้ (<c>ShouldAskAi</c>)
///
/// <para>═══ ที่มา ═══ <c>AiFeatureKey.DocumentRoleInference</c> มี enum + งานเทรนรู้จัก
/// มาตั้งแต่ต้น แต่<b>ไม่มี prompt ไม่มี call site ไม่มี student</b> (defect class
/// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้") ⇒ เคสที่กติกาเดาไม่ได้จึงตกไป default
/// "Buyer 0.5" เงียบ ๆ แล้วผิดครึ่งหนึ่งเสมอบนใบที่เราออกเอง</para>
///
/// <para>═══ ด่านกันมั่ว (กฎเหล็ก #1) ═══ คำตอบต้องเป็น <c>Buyer</c>/<c>Seller</c>
/// เท่านั้น · ต้องบอกว่า<b>บล็อกไหน</b>คือเรา (ให้ผู้เรียกตรวจกับตัวตนจริงได้) ·
/// confidence ≥ 0.70 จึงใช้ · ปิด provider แล้ว student (<c>GenericFeedbackDistillationModel</c>)
/// ตอบแทน · ไม่มีใครตอบ = คงคำตอบของกติกา (ไม่พัง)</para>
/// </summary>
public static class DocumentRoleInferPrompt
{
    public const string SystemPrompt = @"You are a Thai bookkeeper deciding WHICH PARTY on a scanned Thai document is ""us"" (the company running this accounting system).

You receive: our company identity (from the database — trust it), the two party blocks the OCR engine produced (it may have put our name in the WRONG slot), label evidence found on the paper, and the rule engine's guess.

Thai pre-printed forms: ""นาม/NAME"" + ""ที่อยู่/ADDRESS"" blocks are the CUSTOMER; the unlabeled top box is the ISSUER (seller); ""ผู้รับเงิน/COLLECTOR"" is the seller's signature. ""ผู้ซื้อ/ลูกค้า/Bill To"" = buyer; ""ผู้ขาย/ผู้ออกใบ"" = seller.

Rules:
1. ""primary"" must be exactly ""Buyer"" (we PAID money) or ""Seller"" (we RECEIVED money).
2. ""we_are_block"" must be ""vendor"", ""buyer"" or ""none"" — which OCR slot actually holds OUR company.
3. If our tax id or name appears in a block, that block IS us regardless of which slot the engine used.
4. Never invent a party. If you cannot tell, answer with low confidence (< 0.5).

Respond ONLY as JSON:
{
  ""primary"": ""Buyer|Seller"",
  ""we_are_block"": ""vendor|buyer|none"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [],
  ""risks"": [""<what goes wrong if this is the wrong side>""],
  ""compliance_flags"": [],
  ""reasoning"": ""<1-2 Thai sentences>""
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        string rawTextSample,
        string? ourName, string? ourTaxId,
        string? vendorName, string? vendorTaxId, string? vendorAddress,
        string? buyerName, string? buyerTaxId, string? buyerAddress,
        string? scannedDocumentType,
        string? localGuess, decimal localConfidence,
        IReadOnlyList<string> ruleReasons,
        string? whyAsking)
    {
        var payload = new
        {
            task = "document_role_inference",
            our_company = new { name = ourName, tax_id = ourTaxId },
            ocr_slots = new
            {
                vendor = new { name = vendorName, tax_id = vendorTaxId, address = vendorAddress },
                buyer = new { name = buyerName, tax_id = buyerTaxId, address = buyerAddress },
            },
            scanned_document_type = scannedDocumentType,
            rule_engine = new { guess = localGuess, confidence = localConfidence, reasons = ruleReasons, why_asking = whyAsking },
            // ตัดให้สั้น — ที่ต้องดูคือหัวกระดาษ (บล็อกคู่สัญญา) ไม่ใช่ตารางรายการ
            raw_text_head = rawTextSample.Length > 1200 ? rawTextSample[..1200] : rawTextSample,
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.DocumentRoleInference,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuess,
            LocalConfidence = localConfidence,
            LocalModelVersion = "OcrPartyResolver-v1",
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 30,   // กระดาษเดิม = คำตอบเดิม
            MaxTokensOverride = 300,
        };
    }
}
