using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 (C-H2 · C-H3 · C-M4) — ด่านอนุมัติรวดของสแกน + กติกาคิดเงิน/โควตา (pure · สองทิศ):
/// <para>(1) <see cref="OcrPostingReadiness.EvaluateUnseen"/> = ธง <c>canAutoApprove</c> ที่หน้าเว็บใช้แทนกติกาใน JS —
/// ใบสะอาดผ่าน · ข้อผิดพลาดตามสรรพากร/เกรด D/ยังไม่ประเมิน/ใบซ้ำ/ลายมือ/สินทรัพย์/สแกนล้ม ไม่ผ่านพร้อมเหตุ</para>
/// <para>(2) ด่านของ <c>create-document?approve=true</c> (4 อาร์กิวเมนต์ — ตัวเดียวกับ LINE): ข้อผิดพลาดตามสรรพากร ⇒ ไม่อนุมัติเอง</para>
/// <para>(3) <see cref="OcrQuotaRefundRule.NotBilledReason"/>: ล้ม/ผลเดิม/e-Tax XML = ไม่คิดเงิน · สำเร็จด้วยเครื่องอ่านจริง = คิด</para>
/// </summary>
public class OcrApproveGateRound258Tests
{
    private static readonly string[] NoIssues = Array.Empty<string>();

    private static OcrPostingReadiness.Verdict Unseen(string? status = "Completed", bool created = false, bool duplicate = false, bool handwriting = false,
        bool asset = false, bool vendor = true, bool total = true, string? notes = null, bool date = true,
        IEnumerable<string>? issues = null, bool issuesEvaluated = true, string? grade = "A")
        => OcrPostingReadiness.EvaluateUnseen(status, created, duplicate, handwriting, asset, vendor, total, notes, date,
            issuesEvaluated ? issues ?? NoIssues : null, grade);

    [Fact]
    public void ใบสะอาด_อนุมัติรวดได้()
    {
        var v = Unseen();
        Assert.True(v.CanAutoApprove);
        Assert.Null(v.Reason);
        // คำเตือนระดับ warning ไม่หยุด (เหมือนเดิม)
        Assert.True(Unseen(issues: new[] { "warning" }).CanAutoApprove);
    }

    [Fact]
    public void ข้อผิดพลาดตามสรรพากร_หรือเกรดD_หรือยังไม่ประเมิน_ไม่อนุมัติรวด_พร้อมเหตุ()
    {
        var error = Unseen(issues: new[] { "warning", "error" });
        Assert.False(error.CanAutoApprove);
        Assert.Contains("สรรพากร", error.Reason!, StringComparison.Ordinal);
        var gradeD = Unseen(grade: "D");
        Assert.False(gradeD.CanAutoApprove);
        Assert.Contains("เกรด D", gradeD.Reason!, StringComparison.Ordinal);
        // ยังไม่ได้ประเมิน (null) ≠ ไม่มีปัญหา
        var unknown = Unseen(issuesEvaluated: false);
        Assert.False(unknown.CanAutoApprove);
        Assert.Contains("ยังไม่ได้ตรวจ", unknown.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed", false, false, false, false, true, true)]
    [InlineData("Processing", false, false, false, false, true, true)]
    [InlineData("Completed", true, false, false, false, true, true)]    // สร้างเอกสารแล้ว
    [InlineData("Completed", false, true, false, false, true, true)]    // ใบซ้ำ
    [InlineData("Completed", false, false, true, false, true, true)]    // ลายมือ
    [InlineData("Completed", false, false, false, true, true, true)]    // อาจเป็นสินทรัพย์
    [InlineData("Completed", false, false, false, false, false, true)]  // ไม่มีผู้ขาย
    [InlineData("Completed", false, false, false, false, true, false)]  // ไม่มียอดรวม
    public void เงื่อนไขของการไม่มีคนเปิดดู_ไม่ผ่านพร้อมเหตุ(string status, bool created, bool duplicate, bool handwriting, bool asset, bool vendor, bool total)
    {
        var v = Unseen(status, created, duplicate, handwriting, asset, vendor, total);
        Assert.False(v.CanAutoApprove);
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
    }

    [Fact]
    public void หมายเหตุที่หยุดและวันที่อ่านไม่ได้_ยังหยุดเหมือนด่านเดิม()
    {
        Assert.False(Unseen(notes: "[Σ-GAP] ผลรวมรายการไม่ตรง").CanAutoApprove);
        Assert.False(Unseen(date: false).CanAutoApprove);
    }

    [Fact]
    public void ด่านของสร้างพร้อมอนุมัติ_ตัวเดียวกับLINE_ข้อผิดพลาดตามสรรพากรไม่อนุมัติเอง_ใบสะอาดอนุมัติได้()
    {
        // เดิมเส้นเว็บเรียกแบบ 2 อาร์กิวเมนต์ ⇒ ใบ "ผู้ซื้อไม่ใช่บริษัทนี้" ผ่าน
        Assert.True(OcrPostingReadiness.Evaluate(null, true).CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(null, true, new[] { "error" }, "A").CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(null, true, NoIssues, "D").CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(null, true, (IEnumerable<string>?)null, "A").CanAutoApprove);
        // ทิศตรงข้าม
        Assert.True(OcrPostingReadiness.Evaluate(null, true, NoIssues, "B").CanAutoApprove);
    }

    // ───── C-H3 / C-M4 ─────

    [Fact]
    public void สแกนสำเร็จด้วยเครื่องอ่านจริง_คิดค่าบริการ_ไม่มีเหตุไม่คิด()
    {
        Assert.Null(OcrQuotaRefundRule.NotBilledReason("Completed", "AzureDocumentIntelligence"));
        Assert.Null(OcrQuotaRefundRule.NotBilledReason("Completed", null));
        Assert.False(OcrQuotaRefundRule.ShouldRefund("Completed", "AzureDocumentIntelligence"));
    }

    [Fact]
    public void สแกนล้ม_ผลเดิม_eTaxXml_ไม่คิดค่าบริการ_บอกเหตุ()
    {
        var failed = OcrQuotaRefundRule.NotBilledReason("Failed", "AzureDocumentIntelligence");
        Assert.NotNull(failed);
        Assert.Contains("สแกนไม่สำเร็จ", failed!, StringComparison.Ordinal);
        var cached = OcrQuotaRefundRule.NotBilledReason("Completed", OcrDuplicateReusePolicy.CachedEngine);
        Assert.Contains("ใช้ผลเดิม", cached!, StringComparison.Ordinal);
        var etax = OcrQuotaRefundRule.NotBilledReason("Completed", OcrDuplicateReusePolicy.EtaxXmlEngine);
        Assert.Contains("e-Tax XML", etax!, StringComparison.Ordinal);
        // เหตุไม่คิด ⇔ กติกาคืนโควตา (ตัวเดียวกัน — ไม่ drift)
        foreach (var (status, engine) in new[] { ("Failed", "x"), ("Completed", OcrDuplicateReusePolicy.CachedEngine), ("Completed", "x"), ("Processing", (string?)null) })
            Assert.Equal(OcrQuotaRefundRule.ShouldRefund(status, engine), OcrQuotaRefundRule.NotBilledReason(status, engine) != null);
    }
}
