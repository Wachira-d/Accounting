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

Definitions (by who issued the underlying sale, NOT by cash direction — credit notes and refunds flow the other way):
- ""Seller"" = our company issued the sale (invoice / tax invoice / receipt / credit note to a customer; on a 50 ทวิ we are ""ผู้ถูกหัก"" = payee).
- ""Buyer"" = the other party issued the sale to us (we are the customer; on a 50 ทวิ we are ""ผู้มีหน้าที่หัก"" = payer).

Thai pre-printed forms: ""นาม/NAME"" + ""ที่อยู่/ADDRESS"" blocks are the CUSTOMER; the unlabeled top box is the ISSUER; ""ผู้รับเงิน/COLLECTOR"" is the issuer's signature; ""ได้รับเงินจาก/Received From"" names the payer (buyer). ""ผู้ซื้อ/ลูกค้า/Bill To"" = buyer; ""ผู้ขาย/ผู้ออกใบ"" = seller.
IMPORTANT: OCR text order is NOT layout order — a value is often emitted BEFORE its label (e.g. the customer name line appears, THEN the word ""นาม""). Judge by which label sits nearest to a name, in either direction, not by what comes first.

Rules:
1. ""primary"" must be exactly ""Buyer"" or ""Seller"".
2. ""we_are_block"" must be ""vendor"", ""buyer"" or ""none"" — which OCR slot actually holds OUR company. It must be consistent with ""primary"" (vendor ⇒ Seller, buyer ⇒ Buyer).
3. If our tax id or name appears in a block, that block IS us regardless of which slot the engine used. A block with a DIFFERENT valid 13-digit tax id is NOT us even if the name looks similar (affiliate).
4. Never invent a party. If you cannot tell, answer with low confidence (< 0.5). ""confidence"" is a number between 0 and 1.

Respond ONLY as JSON:
{
  ""primary"": ""Buyer|Seller"",
  ""we_are_block"": ""vendor|buyer|none"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1 short Thai sentence>""
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
            // ไม่ส่งคำตอบ default (Buyer 0.5) ไปให้โมเดลยึด — มันไม่ใช่หลักฐาน (anchoring)
            rule_engine = new
            {
                guess = localConfidence > 0.5m ? localGuess : null,
                confidence = localConfidence > 0.5m ? localConfidence : (decimal?)null,
                reasons = ruleReasons, why_asking = whyAsking,
            },
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
            LocalModelVersion = "OcrRoleRules-v1",   // คำตอบ local มาจาก OcrPartyResolver + OcrDocumentRoleInferrer
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            // เลขภาษีต้องถึงโมเดลจริง — กติกาข้อ 3 เทียบเลขเรากับเลขในบล็อก ถ้าถูก mask เป็น
            // 0xxxxxxxxxx1 จะเทียบด้วยเลข 2 หลักแล้วเท่ากันโดยบังเอิญ ~1/100 (เฉพาะเลขนิติบุคคล
            // ผ่าน; เลขบุคคลยัง mask และคำตอบไม่ถูกเขียนกลับเป็นข้อมูล)
            AllowTaxIdInPrompt = true,
            CacheTtlOverrideDays = 30,   // กระดาษเดิม = คำตอบเดิม
            MaxTokensOverride = 450,     // JSON ภาษาไทย 1 ประโยค + ช่องว่าง — 300 ตัดกลาง JSON ได้
        };
    }
}
