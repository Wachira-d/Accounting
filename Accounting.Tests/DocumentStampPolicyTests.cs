using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Xunit;
using Stage = Accounting.Helpers.DocumentStampPolicy.Stage;

namespace Accounting.Tests;

/// <summary>ตัวตัดสินเดียว "ตราประทับ/ลายเซ็นบนเอกสาร" (Helpers/DocumentStampPolicy · ทีม J 2026-10-22) —
/// ยังไม่ตั้งกฎ = พฤติกรรมเดิมทุกสถานะ · ตั้งแล้วมีผลเฉพาะประเภทนั้น · ค่าเสีย/ค่าแปลกไม่ทำ PDF ล้ม</summary>
public class DocumentStampPolicyTests
{
    private static readonly DocumentStampPolicy.Legacy Legacy = new(40m, 30m, "Left");

    private static DocumentStampPolicy.Decision R(string key, Stage st, string? json = null)
        => DocumentStampPolicy.Resolve(key, st, json, Legacy);

    // ───────── ค่าเก่า: ยังไม่ตั้ง = เดิมทุกไบต์ ─────────

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Voided)]
    [InlineData(DocumentStatus.Overdue)]
    [InlineData(DocumentStatus.Rejected)]
    public void NoRules_StampsExactlyLikeTheOldInlineRule_ForEveryStatusAndType(DocumentStatus status)
    {
        // เงื่อนไขเดิมที่เขียน inline ใน HTML + QuestPDF + ResolveSigners ก่อนมีตัวตัดสิน
        var old = status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected);
        foreach (var t in Enum.GetValues<DocumentType>())
        {
            var d = DocumentStampPolicy.Resolve(DocumentStampPolicy.KeyOf(t), DocumentStampPolicy.StageOf(status), (string?)null, Legacy);
            Assert.Equal(old, d.ShowStamp);
            Assert.True(d.ShowSignature);              // ลายเซ็นเดิมพิมพ์ทุกขั้น
            Assert.Equal(40m, d.WidthMm);              // ขนาด/ตำแหน่งจากค่าตั้งเดิมของบริษัท
            Assert.Equal(30m, d.HeightMm);
            Assert.Equal("Left", d.Align);
            Assert.Equal(0m, d.OffsetXMm);
            Assert.False(d.IsCustom);
            Assert.True(d.SignerAllowed(Guid.NewGuid(), "Staff"));   // ไม่จำกัดผู้ลงนาม
        }
    }

    [Fact]
    public void LegacyZeroWidth_MeansAuto32_AsBothRenderersDidBefore()
    {
        var d = DocumentStampPolicy.Resolve("Invoice", Stage.Approved, (string?)null, new DocumentStampPolicy.Legacy(0m, 0m, null));
        Assert.Equal(32m, d.WidthMm);
        Assert.Equal(32m, d.HeightMm);
        Assert.Equal("Right", d.Align);
    }

    [Fact]
    public void StageMapping_RejectedIsDraft_SentOverduePartialAreApproved()
    {
        Assert.Equal(Stage.Draft, DocumentStampPolicy.StageOf(DocumentStatus.Rejected));
        Assert.Equal(Stage.Pending, DocumentStampPolicy.StageOf(DocumentStatus.WaitingApproval));
        Assert.Equal(Stage.Approved, DocumentStampPolicy.StageOf(DocumentStatus.Sent));
        Assert.Equal(Stage.Approved, DocumentStampPolicy.StageOf(DocumentStatus.Overdue));
        Assert.Equal(Stage.Approved, DocumentStampPolicy.StageOf(DocumentStatus.PartiallyPaid));
        Assert.Equal(Stage.Paid, DocumentStampPolicy.StageOf(DocumentStatus.Paid));
        Assert.Equal(Stage.Voided, DocumentStampPolicy.StageOf(DocumentStatus.Voided));
        Assert.Equal(Stage.Draft, DocumentStampPolicy.StageOf(WithholdingTaxCertStatus.Draft));
        Assert.Equal(Stage.Approved, DocumentStampPolicy.StageOf(WithholdingTaxCertStatus.Issued));
        Assert.Equal(Stage.Approved, DocumentStampPolicy.StageOf(WithholdingTaxCertStatus.Printed));
        Assert.Equal(Stage.Voided, DocumentStampPolicy.StageOf(WithholdingTaxCertStatus.Voided));
    }

    // ───────── 50 ทวิ: ประทับเมื่อออกแล้ว (บั๊กที่ผู้ใช้รายงาน — เดิมไม่มีตราเลย) ─────────

    [Fact]
    public void WhtCert_Default_StampsIssued_NotDraft_SizedForTheFormCircle()
    {
        var issued = R(DocumentStampPolicy.WhtCertKey, Stage.Approved);
        Assert.True(issued.ShowStamp);
        Assert.Equal(DocumentStampPolicy.WhtDefaultSizeMm, issued.WidthMm);   // ไม่ใช่ 40 มม. ของบริษัท (ล้นกรอบแบบ)
        Assert.Equal(DocumentStampPolicy.WhtDefaultSizeMm, issued.HeightMm);
        Assert.False(R(DocumentStampPolicy.WhtCertKey, Stage.Draft).ShowStamp);
        Assert.True(R(DocumentStampPolicy.WhtCertKey, Stage.Voided).ShowStamp);   // ใบยกเลิกพิมพ์ซ้ำ (มีลายน้ำ) ยังเห็นตราเดิม
    }

    // ───────── ตั้งกฎแล้ว ─────────

    [Fact]
    public void CustomRule_AppliesOnlyToItsType_AndOverridesStagesSizeOffset()
    {
        var json = """{"Quotation":{"stamp":["Draft","Pending"],"signature":[],"align":"center","offsetXMm":5,"offsetYMm":-3,"widthMm":20,"heightMm":25}}""";
        var qDraft = R("Quotation", Stage.Draft, json);
        Assert.True(qDraft.ShowStamp);
        Assert.False(qDraft.ShowSignature);
        Assert.Equal("Center", qDraft.Align);
        Assert.Equal(5m, qDraft.OffsetXMm);
        Assert.Equal(-3m, qDraft.OffsetYMm);
        Assert.Equal(20m, qDraft.WidthMm);
        Assert.Equal(25m, qDraft.HeightMm);
        Assert.True(qDraft.IsCustom);
        Assert.False(R("Quotation", Stage.Approved, json).ShowStamp);   // ไม่อยู่ในรายการ = ไม่ประทับ
        // ทิศตรงข้าม: ประเภทอื่นไม่ถูกแตะ
        var inv = R("Invoice", Stage.Draft, json);
        Assert.False(inv.ShowStamp);
        Assert.True(inv.ShowSignature);
        Assert.Equal(40m, inv.WidthMm);
        Assert.False(inv.IsCustom);
    }

    [Fact]
    public void EmptyStageList_MeansNever_NullMeansDefault()
    {
        var json = """{"Receipt":{"stamp":[]},"TaxInvoice":{"signature":["Paid"]}}""";
        foreach (var st in Enum.GetValues<Stage>()) Assert.False(R("Receipt", st, json).ShowStamp);
        Assert.True(R("TaxInvoice", Stage.Approved, json).ShowStamp);        // stamp = null ⇒ ค่าตั้งต้น
        Assert.False(R("TaxInvoice", Stage.Approved, json).ShowSignature);
        Assert.True(R("TaxInvoice", Stage.Paid, json).ShowSignature);
    }

    [Fact]
    public void OffsetsAndSizes_AreClamped_SoTheStampNeverLeavesThePaper()
    {
        var json = """{"Invoice":{"offsetXMm":500,"offsetYMm":-500,"widthMm":1,"heightMm":999}}""";
        var d = R("Invoice", Stage.Approved, json);
        Assert.Equal(DocumentStampPolicy.MaxOffsetMm, d.OffsetXMm);
        Assert.Equal(-DocumentStampPolicy.MaxOffsetMm, d.OffsetYMm);
        Assert.Equal(5m, d.WidthMm);
        Assert.Equal(120m, d.HeightMm);
    }

    [Fact]
    public void BrokenJson_FallsBackToOldBehaviour_InsteadOfBreakingThePdf()
    {
        var d = R("Invoice", Stage.Approved, "{not json");
        Assert.True(d.ShowStamp);
        Assert.False(d.IsCustom);
        Assert.Empty(DocumentStampPolicy.Parse("""{"NotAType":{"stamp":["Draft"]}}"""));
    }

    // ───────── ผู้ลงนามที่มีสิทธิ์ ─────────

    [Fact]
    public void SignerAllowed_ByRoleOrUser()
    {
        var boss = Guid.NewGuid();
        var json = "{\"PaymentVoucher\":{\"signerRoles\":[\"Owner\"],\"signerUserIds\":[\"" + boss + "\"]}}";
        var d = R("PaymentVoucher", Stage.Approved, json);
        Assert.True(d.SignerAllowed(Guid.NewGuid(), "Owner"));
        Assert.True(d.SignerAllowed(boss, "Staff"));
        Assert.False(d.SignerAllowed(Guid.NewGuid(), "Staff"));
        Assert.False(d.SignerAllowed(null, null));
        // ทิศตรงข้าม: ประเภทที่ไม่ได้จำกัด ใครก็ได้
        Assert.True(R("Invoice", Stage.Approved, json).SignerAllowed(Guid.NewGuid(), "Staff"));
    }

    // ───────── ตรวจ/จัดรูปก่อนบันทึก ─────────

    [Fact]
    public void Validate_RejectsUnknownKeysStagesRolesAndOutOfRangeValues()
    {
        var bad = new Dictionary<string, DocumentStampPolicy.Rule>
        {
            ["Nope"] = new(),
            ["Invoice"] = new()
            {
                Stamp = new() { "Draft", "1", "Foo" }, Align = "Top", OffsetXMm = 61, WidthMm = 4,
                SignerRoles = new() { "Owner", "SystemAdmin", "Boss" },
            },
        };
        var errs = DocumentStampPolicy.Validate(bad);
        Assert.Contains(errs, e => e.Contains("Nope"));
        Assert.Contains(errs, e => e.Contains("\"1\""));        // ตัวเลขไม่ถูกแปลงเป็นขั้นเงียบ ๆ
        Assert.Contains(errs, e => e.Contains("\"Foo\""));
        Assert.Contains(errs, e => e.Contains("ตำแหน่ง"));
        Assert.Contains(errs, e => e.Contains("แนวนอน"));
        Assert.Contains(errs, e => e.Contains("ความกว้าง"));
        Assert.Contains(errs, e => e.Contains("SystemAdmin"));   // ผู้ดูแลแพลตฟอร์มไม่ใช่ผู้ลงนามของบริษัท
        Assert.Contains(errs, e => e.Contains("Boss"));
        Assert.DoesNotContain(errs, e => e.Contains("\"Draft\"") || e.Contains("\"Owner\""));
        Assert.Empty(DocumentStampPolicy.Validate(new Dictionary<string, DocumentStampPolicy.Rule>
        {
            [DocumentStampPolicy.WhtCertKey] = new() { Stamp = new() { "approved" }, Align = "left", OffsetYMm = -60 },
        }));
    }

    [Fact]
    public void Serialize_RoundTrips_AndEmptyMeansNull()
    {
        Assert.Null(DocumentStampPolicy.Serialize(null));
        Assert.Null(DocumentStampPolicy.Serialize(new Dictionary<string, DocumentStampPolicy.Rule>()));
        var rules = new Dictionary<string, DocumentStampPolicy.Rule>
        {
            ["TaxInvoice"] = new() { Stamp = new() { "paid", "approved", "paid" }, Align = "center", SignerRoles = new() { "owner" } },
        };
        var json = DocumentStampPolicy.Serialize(rules);
        Assert.NotNull(json);
        var back = DocumentStampPolicy.Parse(json);
        Assert.Equal(new[] { "Approved", "Paid" }, back["TaxInvoice"].Stamp);   // จัดรูป: ชื่อมาตรฐาน · ไม่ซ้ำ · เรียงตามขั้น
        Assert.Equal("Center", back["TaxInvoice"].Align);
        Assert.Equal(new[] { "Owner" }, back["TaxInvoice"].SignerRoles);
        var d = DocumentStampPolicy.Resolve("TaxInvoice", Stage.Paid, json, Legacy);
        Assert.True(d.ShowStamp);
        Assert.False(DocumentStampPolicy.Resolve("TaxInvoice", Stage.Voided, json, Legacy).ShowStamp);
    }

    [Fact]
    public void Kinds_CoverEveryDocumentType_PlusWhtCert()
    {
        var keys = DocumentStampPolicy.Kinds.Select(k => k.Key).ToHashSet();
        foreach (var t in Enum.GetValues<DocumentType>()) Assert.Contains(t.ToString(), keys);
        Assert.Contains(DocumentStampPolicy.WhtCertKey, keys);
    }
}
