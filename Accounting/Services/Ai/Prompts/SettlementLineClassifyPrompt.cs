using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// คำขอจัดประเภทบรรทัด settlement (<see cref="AiFeatureKey.SettlementLineClassify"/> · รอบ 198 เฟส 1 ทีม B)
///
/// <para>ถูกเรียกเฉพาะป้ายที่ชั้น local ตอบไม่ได้ (กติกา adapter → คลังที่เรียนต่อช่องทาง → seed) · 1 ครั้งต่อ "ป้าย+เครื่องหมาย" ต่อการนำเข้า ·
/// payload = <see cref="SettlementLineClassification.BuildPromptPayload"/> (ไม่มียอด/เลขออเดอร์/ชื่อผู้ซื้อ) · คำตอบผ่าน
/// <see cref="SettlementLineClassification.AcceptModelAnswer"/> ก่อนแตะบรรทัดเสมอ</para>
/// </summary>
public static class SettlementLineClassifyPrompt
{
    public const string SystemPrompt = @"You classify ONE line label from a payment-gateway / marketplace / OTA settlement (payout) report
of a Thai company into an accounting line type.

Input: the raw label text (lower-cased, numbers removed), the channel kind, and whether the wallet amount is positive
(money added to the seller's wallet) or negative (deducted from it).

Rules:
1. Answer with exactly one value from ""candidates"" (the enum name, case-sensitive). Never invent a value.
2. The sign must fit the type: Sale / PlatformVoucherSubsidy / ShippingSubsidy / ReserveRelease / ChargebackReversal are positive;
   Refund / Chargeback / SellerVoucher / ReserveHold / TaxWithheldByPlatform / WithdrawalFee are negative;
   fees (Commission, PaymentFee, ShippingFeeCharged, AdsFee, ServiceFee) are usually negative, positive only when refunded.
3. SellerVoucher = discount funded by the seller (reduces the sale). PlatformVoucherSubsidy = discount/coins funded by the platform (part of the sale).
4. If the label is ambiguous (e.g. 'adjustment', 'other', 'misc'), return your best guess with confidence below 0.5.

Respond ONLY as JSON:
{
  ""primary"": ""<enum name>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<enum name>""],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1 sentence>"",
  ""suggested_actions"": []
}";

    public static AiRequest Build(Guid companyId, Guid channelId, string normalizedLabel, SettlementChannelKind kind, decimal amount)
        => new()
        {
            FeatureKey = AiFeatureKey.SettlementLineClassify,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = SettlementLineClassification.BuildPromptPayload(normalizedLabel, kind, amount),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = null,
            LocalAlternatives = SettlementLineClassification.ClassifierCandidates.Select(t => t.ToString()).ToList(),
            SourceEntityType = "SettlementChannel",
            SourceEntityId = channelId,
            CacheTtlOverrideDays = 30,
            TemperatureOverride = 0m,
        };
}
