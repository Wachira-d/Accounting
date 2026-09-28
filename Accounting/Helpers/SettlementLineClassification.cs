using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลการจัดประเภทชั้น local (ก่อนถาม AI)</summary>
public readonly record struct SettlementLocalClassification(SettlementLineType Type, SettlementClassifiedBy By)
{
    public bool Resolved => Type != SettlementLineType.Unclassified;
    public static SettlementLocalClassification None => new(SettlementLineType.Unclassified, SettlementClassifiedBy.None);
}

/// <summary>
/// **ลำดับการจัดประเภทบรรทัด settlement + ด่านรับคำตอบของตัวจัดประเภท — pure · ตัวตัดสินตัวเดียว** (รอบ 198 เฟส 1 ทีม B ·
/// กฎเหล็ก #1 · DECISION_DOCTRINE §2 · DOCUMENT_FLOW §6.4 แถว <c>SettlementLineClassify</c>)
///
/// <para>═══ ลำดับ (local ก่อนเสมอ — ปิด AI แล้วยังจัดได้เท่าที่ความรู้มี · ที่เหลือ = <c>Unclassified</c> ให้ผู้ใช้เลือก) ═══
/// <list type="number">
/// <item><b>กติกา adapter</b> — ประเภทที่ adapter รู้แน่ (แถวจาก PaymentIntent · คอลัมน์ที่ผู้ใช้กำหนดประเภทไว้)</item>
/// <item><b>คลังที่เรียนต่อช่องทาง</b> — ป้ายเดียวกัน + เครื่องหมายเดียวกันที่ผู้ใช้เคยเลือกเองในช่องทางนี้ (ต้องชนะขาด)</item>
/// <item><b>seed</b> — ป้ายที่รู้จักของ Shopee/Lazada/Omise ฯลฯ (อยู่ใน <c>Services/Settlement/Adapters</c> — ความรู้เฉพาะเจ้า)</item>
/// <item>ครู/นักเรียนผ่าน <c>IAiOrchestrator</c> — รับคำตอบผ่าน <see cref="AcceptModelAnswer"/> เท่านั้น</item>
/// </list>
/// ทุกชั้นต้องผ่าน <c>SignAllowed</c> ของประเภทนั้น — ป้าย "Refund" บนยอดบวก ≠ คืนเงิน (ตกไปชั้นถัดไป ไม่ใช่บังคับเข้า)</para>
///
/// <para>═══ ด่านคำตอบ AI/นักเรียน (write-gate · DOCTRINE §2.3) ═══ ต้องครบทุกข้อ: (1) เป็นชื่อ enum ที่ลงบัญชีได้
/// (<see cref="SettlementLineTypeRules.ParseClassifierAnswer"/>) (2) ไม่ใช่ประเภทที่บังคับเหตุผล (Adjustment — คนต้องเลือกเอง)
/// (3) เครื่องหมายยอดเข้ากับประเภท (4) confidence ≥ <see cref="MinApplyConfidence"/> (5) เป็นคำตอบจากโมเดลจริง
/// (<c>UsedAi</c> หรือ <c>FromLocalModel</c>) <b>และไม่ใช่ majority fallback</b> ของนักเรียน (tier-2 ไม่ดูอินพุตเลย — DOCTRINE §2.5)</para>
/// </summary>
public static class SettlementLineClassification
{
    /// <summary>เกณฑ์ขั้นต่ำของคำตอบที่เขียนลงบรรทัด (write-gate)</summary>
    public const decimal MinApplyConfidence = 0.70m;

    /// <summary>ป้ายรุ่นของนักเรียน tier-2 (คำตอบยอดนิยมของบริษัท ไม่ดูอินพุต) — ตรงกับ <c>GenericFeedbackDistillationModel</c></summary>
    private const string MajorityVersionSuffix = "-majority";

    private static readonly Regex NumericToken = new(@"[\d][\d.,]*%?|%", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>ป้ายแบบเทียบได้ — ตัวพิมพ์เล็ก · ตัดตัวเลข/เปอร์เซ็นต์ (อัตราค่าธรรมเนียมเปลี่ยนได้ ป้ายยังเป็นประเภทเดิม) ·
    /// ตัดวงเล็บว่าง/เครื่องหมายหัวท้าย · ยุบช่องว่าง — คีย์ของทั้ง seed · คลังที่เรียน · fingerprint ของ AI</summary>
    public static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var s = label.ToLowerInvariant().Replace('_', ' ');
        s = NumericToken.Replace(s, " ");
        s = s.Replace("()", " ", StringComparison.Ordinal).Replace("( )", " ", StringComparison.Ordinal);
        s = Spaces.Replace(s, " ").Trim().Trim(':', '-', '–', '.', ',', '*', '#', '(', ')', ' ');
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>เครื่องหมายยอดแบบข้อความ (ส่วนหนึ่งของ fingerprint)</summary>
    private static string SignToken(decimal amount) => amount > 0m ? "positive" : amount < 0m ? "negative" : "zero";

    /// <summary>ประเภทที่ตัวจัดประเภท (seed/คลัง/AI) เสนอได้ — ลงบัญชีได้ และไม่บังคับเหตุผล</summary>
    public static IReadOnlyList<SettlementLineType> ClassifierCandidates { get; } = SettlementLineTypeRules.All
        .Where(r => r.Postable && !r.RequiresReason)
        .Select(r => r.Type)
        .ToList();

    /// <summary>ประเภทนี้ใช้กับยอดนี้ได้ไหม (ด่านเดียวกับตัวคิดแผน) — ห้ามจัดบรรทัดเข้าประเภทที่เครื่องหมายขัด</summary>
    public static bool Fits(SettlementLineType type, decimal amount)
    {
        var rule = SettlementLineTypeRules.For(type);
        return rule.Postable && rule.SignAllowed(amount);
    }

    /// <summary>ชั้น local ทั้งหมดตามลำดับ (adapter → คลังที่เรียน → seed) — ไม่มีชั้นไหนตอบได้ ⇒ <see cref="SettlementLocalClassification.None"/></summary>
    public static SettlementLocalClassification ResolveLocal(
        SettlementLineType? adapterType, SettlementLineType? learnedType, SettlementLineType? seedType, decimal amount)
    {
        if (adapterType is SettlementLineType a && Fits(a, amount)) return new(a, SettlementClassifiedBy.AdapterRule);
        if (learnedType is SettlementLineType l && Fits(l, amount) && !SettlementLineTypeRules.For(l).RequiresReason)
            return new(l, SettlementClassifiedBy.Learned);
        if (seedType is SettlementLineType s && Fits(s, amount) && !SettlementLineTypeRules.For(s).RequiresReason)
            return new(s, SettlementClassifiedBy.AdapterRule);
        return SettlementLocalClassification.None;
    }

    /// <summary>คลังที่เรียนต่อช่องทาง: ประเภทที่ผู้ใช้เคยเลือกให้ป้าย+เครื่องหมายเดียวกัน — ต้อง<b>ชนะขาด</b> (มากกว่าครึ่ง) ·
    /// เสียงแตก = ไม่รู้ (ให้ชั้นถัดไป/คนตัดสิน)</summary>
    public static SettlementLineType? LearnedVote(IEnumerable<SettlementLineType> userChoices)
    {
        var tally = userChoices.Where(t => t != SettlementLineType.Unclassified)
            .GroupBy(t => t).Select(g => (Type: g.Key, N: g.Count())).OrderByDescending(x => x.N).ToList();
        if (tally.Count == 0) return null;
        var total = tally.Sum(x => x.N);
        return tally[0].N * 2 > total ? tally[0].Type : null;
    }

    /// <summary>payload ที่ส่งให้ตัวจัดประเภท — <b>ห้ามมียอดเงิน · เลขออเดอร์ · ชื่อผู้ซื้อ</b> (DOCUMENT_FLOW §6.4) ·
    /// ป้าย/ชนิดช่องทาง/เครื่องหมาย เท่านั้น ⇒ fingerprint ของนักเรียน (<c>AiMemoryKey</c>) ชนกันได้ข้ามรอบโอน</summary>
    public static string BuildPromptPayload(string normalizedLabel, SettlementChannelKind channelKind, decimal amount)
        => JsonSerializer.Serialize(new
        {
            task = "settlement_line_classify",
            label = normalizedLabel,
            channel_kind = channelKind.ToString(),
            amount_sign = SignToken(amount),
            candidates = ClassifierCandidates.Select(t => t.ToString()).ToArray(),
        });

    /// <summary>
    /// **ด่านรับคำตอบของตัวจัดประเภท (ครู/นักเรียน)** — คืนประเภทที่เขียนลงบรรทัดได้ หรือ null (คง Unclassified)
    /// </summary>
    /// <param name="answer"><c>AiResponse.PrimaryAnswer</c></param>
    /// <param name="usedAi"><c>AiResponse.UsedAi</c> — ครูตอบจริง</param>
    /// <param name="fromLocalModel"><c>AiResponse.FromLocalModel</c> — นักเรียนตอบ</param>
    /// <param name="providerModel"><c>AiResponse.ProviderModel</c> — ใช้แยก majority fallback ของนักเรียน</param>
    public static SettlementLineType? AcceptModelAnswer(
        string? answer, decimal? confidence, bool usedAi, bool fromLocalModel, string? providerModel, decimal amount)
    {
        if (!usedAi && !fromLocalModel) return null;                                  // kill-switch / ไม่มีโมเดลตอบ
        if (fromLocalModel && !usedAi && providerModel != null
            && providerModel.EndsWith(MajorityVersionSuffix, StringComparison.Ordinal)) return null;
        if ((confidence ?? 0m) < MinApplyConfidence) return null;                     // majority (≤0.45) ตกที่นี่ด้วย
        if (SettlementLineTypeRules.ParseClassifierAnswer(answer) is not SettlementLineType t) return null;
        if (SettlementLineTypeRules.For(t).RequiresReason) return null;
        return Fits(t, amount) ? t : null;
    }
}
