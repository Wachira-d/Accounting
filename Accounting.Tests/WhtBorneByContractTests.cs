using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 ทีม B — ผู้จ่ายรับภาระภาษีหัก ณ ที่จ่ายตามสัญญา (§65 ตรี (3) · ม.39): <c>Document.WhtBorneByContract</c> ⇒ ไม่บวกกลับส่วนที่ออกแทน ·
/// ร่องรอย <c>RD-65ter(3)-WHT-BORNE-CONTRACT</c> · ไม่มีไฟล์แนบ ⇒ ข้อสังเกต "แนบสัญญา" (ไม่บล็อก) · แก้หลังอนุมัติแทนที่เฉพาะผลข้อนี้
/// (<see cref="Section65TerValidator.ReplacePayerBorneFindings"/>) — ข้ออื่นคงเดิม
/// </summary>
public class WhtBorneByContractTests
{
    private static readonly Guid Acc = Guid.NewGuid();

    private static Document Doc(bool byContract, decimal borne = 309.28m, WithholdingTaxCertType type = WithholdingTaxCertType.PayAlways,
        DocumentType docType = DocumentType.Expense, Guid? related = null) => new()
    {
        DocumentType = docType,
        RelatedDocumentId = related,
        TotalAmount = 10_000m,
        WhtCertType = type,
        WhtPayerBorneAmount = borne,
        WhtBorneByContract = byContract,
        Lines = new List<DocumentLine> { new() { AccountId = Acc, Amount = 10_000m, Description = "ค่าบริการที่ปรึกษา" } },
    };

    private static Section65TerValidator.Result Eval(Document doc, bool? hasAttachment)
        => Section65TerValidator.Evaluate(doc,
            new Dictionary<Guid, (string Code, string Name)> { [Acc] = ("53110", "ค่าที่ปรึกษา") },
            "บริษัท ผู้รับ จำกัด", "0105551234567",
            new Section65TerValidator.Context(AnnualRevenue: 10_000_000m, PaidUpCapital: 1_000_000m, HasAttachment: hasAttachment));

    [Fact]
    public void Contract_ticked_no_add_back_and_audit_trail_recorded()
    {
        var r = Eval(Doc(byContract: true), hasAttachment: true);
        Assert.DoesNotContain(r.Findings, f => f.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
        var f = Assert.Single(r.Findings, x => x.RuleCode == Section65TerValidator.PayerBorneContractRuleCode);
        Assert.Equal(0m, f.AddBackAmount);
        Assert.Equal("§65 ตรี (3) · ม.39 — ผู้จ่ายรับภาระตามสัญญา (เก็บสัญญาเป็นหลักฐาน)", f.LegalReference);
        Assert.False(f.HardBlock);
        Assert.False(f.NeedsConfirmation);
        Assert.Equal(0m, r.TotalAddBack);
        // มีไฟล์แนบ ⇒ บันทึกอย่างเดียว ไม่ยกขึ้นตอนอนุมัติ · แต่อยู่ใน JSON (ร่องรอย audit บนเอกสาร)
        Assert.Empty(Section65TerApprovalWarnings.For(r));
        Assert.Contains(Section65TerValidator.PayerBorneContractRuleCode, r.ToJson());
    }

    [Fact]
    public void Contract_ticked_without_attachment_advises_attach_contract_non_blocking()
    {
        var r = Eval(Doc(byContract: true), hasAttachment: false);
        var f = Assert.Single(r.Findings, x => x.RuleCode == Section65TerValidator.PayerBorneContractRuleCode);
        Assert.True(f.NeedsConfirmation);
        Assert.False(f.HardBlock);
        Assert.Contains("แนบสัญญา", f.Message);
        Assert.Equal(0m, r.TotalAddBack);
        var warn = Assert.Single(Section65TerApprovalWarnings.For(r));
        Assert.Equal(Section65TerValidator.PayerBorneContractRuleCode, Section65TerApprovalWarnings.RuleCodeOf(warn));
        // ไม่ทราบว่ามีไฟล์แนบไหม (ผู้เรียกไม่ส่ง) ⇒ ไม่ฟ้อง
        Assert.Empty(Section65TerApprovalWarnings.For(Eval(Doc(byContract: true), hasAttachment: null)));
    }

    [Fact]
    public void No_contract_still_adds_back_and_withhold_untouched_opposite_direction()
    {
        var r = Eval(Doc(byContract: false), hasAttachment: true);
        var f = Assert.Single(r.Findings, x => x.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
        Assert.Equal(309.28m, f.AddBackAmount);
        Assert.Equal(309.28m, r.TotalAddBack);
        Assert.DoesNotContain(r.Findings, x => x.RuleCode == Section65TerValidator.PayerBorneContractRuleCode);
        // หัก ณ ที่จ่าย (ไม่มีส่วนที่ออกแทน) — ติ๊กสัญญาไว้ก็ไม่มีผลใด ๆ · ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางไม่ประเมินซ้ำ
        Assert.Empty(Section65TerValidator.PayerBorneFindings(Doc(true, 0m, WithholdingTaxCertType.Withhold), false));
        Assert.Empty(Section65TerValidator.PayerBorneFindings(Doc(true, docType: DocumentType.PaymentVoucher, related: Guid.NewGuid()), false));
    }

    // ── แก้หลังอนุมัติ: แทนที่เฉพาะผลของภาษีที่ออกแทน ──

    private static string StoredJson(params (string Code, decimal Amount)[] rows)
        => JsonSerializer.Serialize(rows.Select(r => new { RuleCode = r.Code, LegalReference = "ref " + r.Code, AddBackAmount = r.Amount, Message = "m " + r.Code }));

    [Fact]
    public void Replace_switches_add_back_to_contract_and_keeps_other_rules_verbatim()
    {
        var stored = StoredJson(("RD-65ter(4)", 1_000m), (Section65TerValidator.PayerBorneWhtRuleCode, 309.28m), ("RD-65ter(6)", 50m));
        var (total, json) = Section65TerValidator.ReplacePayerBorneFindings(stored,
            Section65TerValidator.PayerBorneFindings(Doc(byContract: true), hasAttachment: true));
        Assert.Equal(1_050m, total);
        using var j = JsonDocument.Parse(json!);
        var codes = j.RootElement.EnumerateArray().Select(e => e.GetProperty("RuleCode").GetString()).ToList();
        Assert.Equal(new[] { "RD-65ter(4)", "RD-65ter(6)", Section65TerValidator.PayerBorneContractRuleCode }, codes.ToArray());
        var kept = j.RootElement.EnumerateArray().First();
        Assert.Equal("ref RD-65ter(4)", kept.GetProperty("LegalReference").GetString());
        Assert.Equal("m RD-65ter(4)", kept.GetProperty("Message").GetString());
        Assert.Equal(1_000m, kept.GetProperty("AddBackAmount").GetDecimal());
    }

    [Fact]
    public void Replace_back_to_no_contract_restores_add_back_and_handles_empty_store()
    {
        var stored = StoredJson((Section65TerValidator.PayerBorneContractRuleCode, 0m));
        var (total, json) = Section65TerValidator.ReplacePayerBorneFindings(stored,
            Section65TerValidator.PayerBorneFindings(Doc(byContract: false), hasAttachment: true));
        Assert.Equal(309.28m, total);
        Assert.Contains(Section65TerValidator.PayerBorneWhtRuleCode, json);
        Assert.DoesNotContain(Section65TerValidator.PayerBorneContractRuleCode, json);
        // ไม่มีผลบันทึกเดิม (null) + ไม่มีผลใหม่ ⇒ 0/null · ไม่มีผลบันทึกเดิม + ติ๊กสัญญา ⇒ ร่องรอยอย่างเดียว ยอด 0
        Assert.Equal((0m, (string?)null), Section65TerValidator.ReplacePayerBorneFindings(null, Array.Empty<Section65TerValidator.Finding>()));
        var (t2, j2) = Section65TerValidator.ReplacePayerBorneFindings(null,
            Section65TerValidator.PayerBorneFindings(Doc(byContract: true), hasAttachment: false));
        Assert.Equal(0m, t2);
        Assert.Contains(Section65TerValidator.PayerBorneContractRuleCode, j2);
    }

    [Fact]
    public void Replace_unreadable_store_fails_loud()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Section65TerValidator.ReplacePayerBorneFindings("{\"RuleCode\":\"x\"}", Array.Empty<Section65TerValidator.Finding>()));
    }
}
