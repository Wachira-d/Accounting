using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลของการจัดรูปใบมัดจำ — ผู้เรียก (<c>DocumentService.CreateDocumentAsync</c> ฯลฯ) ใช้แทนค่าที่ request ส่งมา</summary>
/// <param name="Lines">บรรทัดหลังจัดรูป (decision null = อ็อบเจ็กต์เดิมทุกตัว)</param>
/// <param name="DepositOutputVatDeferred">ธงของเอกสาร (true = VAT พัก 21913 / มัดจำเต็มยอดไม่เข้ารายงานยอดขายยกเว้น)</param>
/// <param name="DepositDeferredAccountCode">บัญชีหนี้สินพักเงิน (null = ค่าเดิมของ AutoPost 21712)</param>
/// <param name="Note">หมายเหตุที่ต้องประทับลง <c>Document.DepositPolicyNote</c> (เหตุผลนโยบาย · เซิร์ฟเวอร์ตั้ง VAT 0 ทับค่าที่ส่งมา) · null = ไม่มี</param>
/// <param name="VatOverridden">เซิร์ฟเวอร์เปลี่ยนอัตรา/ยอด VAT ของอย่างน้อยหนึ่งบรรทัดจากที่ client ส่ง</param>
public sealed record DepositShapingResult(
    IReadOnlyList<DocumentLineRequest> Lines,
    bool DepositOutputVatDeferred,
    string? DepositDeferredAccountCode,
    string? Note,
    bool VatOverridden);

/// <summary>
/// <b>ตัวจัดรูปใบมัดจำตัวเดียว</b> ตามผลตัดสินประเภท (รอบ 194 · spec S2/S4 · plan §5) — pure · ทุกทางเข้า (ฟอร์ม · API · ที่พัก ·
/// CMS · integration ที่ระบุ <c>DepositKindCode</c>) เรียกตัวนี้ก่อนบันทึก ห้ามจัดรูปเอง
///
/// <para>═══ กติกา ═══</para>
/// <list type="bullet">
/// <item><b>decision = null</b> (payload ไม่ระบุประเภท — คู่ค้า/OCR/ฟอร์มเก่า) ⇒ <b>ไม่แตะอะไรเลย</b>: บรรทัดเดิมทุกตัว · ธง/บัญชีตาม request</item>
/// <item>บริษัทไม่จด VAT (<c>companyVatRate</c> ≤ 0) ⇒ VAT 0 ทุกโหมด (ตาม <see cref="DepositPolicyResolver.ShapeFor"/> เดิม)</item>
/// <item><see cref="DepositNature.NonVatSupply"/> ⇒ บังคับ VAT 0 + deferred แบบมัดจำเต็มยอด ทุกโหมด · บรรทัดที่ client ส่ง VAT &gt; 0 มา
/// ⇒ ตั้ง 0 และคืนหมายเหตุ (<c>RD-81</c> — มองเห็น ไม่เงียบ)</item>
/// <item>มัดจำเต็มยอด ⇒ VAT 0 + deferred (บรรทัดที่ส่ง VAT มา ⇒ ตั้ง 0 + หมายเหตุ)</item>
/// <item>รอเรียกเก็บ/VAT ทันที ⇒ <b>ไม่แตะอัตราของบรรทัด</b> (บรรทัด 0 = ยกเว้น §81 ห้ามบังคับเป็น 7% · บรรทัดที่มีอัตราคงตามที่ส่ง) · ตั้งแค่ธง deferred</item>
/// <item>บัญชีหนี้สิน: ประเภทระบุไว้ (หรือเงินประกัน 21530/21620) ชนะ · ไม่งั้นตาม request</item>
/// </list>
/// </summary>
public static class DepositDocumentShaping
{
    public static DepositShapingResult Apply(
        IReadOnlyList<DocumentLineRequest> lines,
        DepositKindDecision? decision,
        decimal companyVatRate,
        bool requestDepositOutputVatDeferred,
        string? requestDepositDeferredAccountCode)
    {
        if (decision is null)
            return new DepositShapingResult(lines, requestDepositOutputVatDeferred, requestDepositDeferredAccountCode, null, false);

        var nonVat = decision.Nature == DepositNature.NonVatSupply;
        // นอกระบบ VAT = รูปเดียวกับมัดจำเต็มยอด (VAT 0 + deferred) ไม่ว่าโหมดใด — บริษัทไม่จด VAT ได้ (0, false) ตาม ShapeFor เดิม
        var shape = DepositPolicyResolver.ShapeFor(nonVat ? DepositVatTreatment.FullDeposit : decision.Treatment, companyVatRate);

        var overridden = false;
        IReadOnlyList<DocumentLineRequest> shaped = lines;
        if (shape.LineVatRate <= 0m)
        {
            var list = new List<DocumentLineRequest>(lines.Count);
            foreach (var l in lines)
            {
                if (l.VatRate != 0m || (l.VatAmountOverride ?? 0m) != 0m)
                {
                    overridden = true;
                    list.Add(l with { VatRate = 0m, VatAmountOverride = null });
                }
                else list.Add(l);
            }
            shaped = list;
        }

        var notes = new List<string>();
        if (overridden)
        {
            if (nonVat)
                notes.Add($"[{DepositPolicyResolver.KindNonVatRuleCode}] ประเภท “{decision.Name}” อยู่นอกระบบภาษีมูลค่าเพิ่ม (§81) — "
                          + "ระบบตั้ง VAT ของบรรทัดเป็น 0 แทนค่าที่ส่งมา");
            else if (companyVatRate <= 0m)
                notes.Add("บริษัทไม่ได้จดทะเบียน VAT — ระบบตั้ง VAT ของบรรทัดเป็น 0 แทนค่าที่ส่งมา");
            else
                notes.Add($"ประเภท “{decision.Name}” บันทึกแบบ “{DepositPolicyResolver.LabelOf(decision.Treatment)}” — "
                          + "ระบบตั้ง VAT ของบรรทัดเป็น 0 แทนค่าที่ส่งมา (ภาษีขายเกิดที่ใบสุดท้าย)");
        }
        if (decision.RequiresReason && companyVatRate > 0m)
            notes.Add($"[{decision.RuleCode}] เลือกเลื่อน VAT ของมัดจำที่เป็นส่วนหนึ่งของราคา — เหตุผล: "
                      + (decision.PolicyReason ?? "(ไม่ได้ระบุ)"));

        var account = decision.LiabilityAccountCode ?? requestDepositDeferredAccountCode;
        return new DepositShapingResult(shaped, shape.DepositOutputVatDeferred, account,
            notes.Count == 0 ? null : string.Join(" · ", notes), overridden);
    }
}
