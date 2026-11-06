using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 255 (ETR-ACK) — ทางไปต่อของสองกลุ่มแถว e-Tax ในรายงานข้อ 44 (แถบ "ตรวจที่กรมสรรพากร" ค้างตลอดไปเพราะไม่มีทางบันทึกว่าตรวจแล้ว · F2 ข้อ 8):
/// ตัวตัดสิน "ซ่อนเมื่อมี audit ตรวจแล้วของแถวนี้" + ตัวตรวจคำขอ · ทุกข้อมีสองทิศ (ใบที่บันทึกแล้วหาย · ใบที่ยังไม่บันทึก/บันทึกของแถวอื่นยังแสดง)
/// </summary>
public class EtaxReviewAcknowledgementTests
{
    private static readonly Guid Row1 = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Row2 = Guid.Parse("00000000-0000-0000-0000-0000000000e2");

    // รูป JSON เดียวกับที่ service เขียน (System.Text.Json · anonymous object)
    private static string AckJson(string outcome = EtaxReissueReview.OutcomeRejectedOrNotReceived) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            action = EtaxReissueReview.AcknowledgedAction,
            ruleCode = EtaxReissueReview.AcknowledgedRuleCode,
            outcome,
            note = "ตรวจที่ระบบ e-Tax แล้ว ถูกปฏิเสธ",
        });

    private static readonly string VoidJson = System.Text.Json.JsonSerializer.Serialize(new
    {
        action = "etax-voided-in-system",
        ruleCode = "RD-ETAX-VOID-NOT-REACHED",
    });

    [Fact]
    public void บันทึกตรวจแล้ว_แถวนั้นถูกซ่อน()
    {
        var audits = new List<(string, string?)> { (Row1.ToString(), AckJson()) };
        Assert.True(EtaxReissueReview.HiddenByAcknowledgement(Row1, audits));
    }

    [Fact]
    public void ยังไม่บันทึก_แถวยังแสดง()
    {
        Assert.False(EtaxReissueReview.HiddenByAcknowledgement(Row1, new List<(string, string?)>()));
        // audit การยกเลิกแถวเดียวกัน (ไม่ใช่การตรวจ) ไม่ซ่อน
        Assert.False(EtaxReissueReview.HiddenByAcknowledgement(Row1, new List<(string, string?)> { (Row1.ToString(), VoidJson), (Row1.ToString(), null) }));
    }

    [Fact]
    public void บันทึกของแถวอื่น_ไม่ซ่อนแถวนี้()
    {
        var audits = new List<(string, string?)> { (Row2.ToString(), AckJson()) };
        Assert.False(EtaxReissueReview.HiddenByAcknowledgement(Row1, audits));
        Assert.True(EtaxReissueReview.HiddenByAcknowledgement(Row2, audits));
    }

    [Fact]
    public void ข้อความที่แค่อ้างคำว่า_action_ในหมายเหตุ_ไม่นับเป็นการตรวจ()
    {
        // ผู้ใช้พิมพ์ชื่อ action ไว้ในเหตุผลของการยกเลิก — ไม่ใช่ audit ตรวจแล้ว
        var json = System.Text.Json.JsonSerializer.Serialize(new { action = "etax-voided-in-system", reason = EtaxReissueReview.AcknowledgedAction });
        Assert.False(EtaxReissueReview.IsAcknowledgementAudit(json));
        Assert.True(EtaxReissueReview.IsAcknowledgementAudit(AckJson(EtaxReissueReview.OutcomeCancelledAtRd)));
    }

    [Fact]
    public void ตรวจคำขอ_ผ่านเมื่อครบ()
    {
        Assert.Null(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeRejectedOrNotReceived, null, "ถูกปฏิเสธที่กรมฯ"));
        Assert.Null(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeCancelledAtRd, "RD-CXL-0001", "ยกเลิกแล้ว 1 ก.ค."));
    }

    [Fact]
    public void ตรวจคำขอ_ไม่มีผลยังมีผลที่กรมสรรพากร_และค่าแปลกถูกปฏิเสธ()
    {
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement("StillValidAtRd", null, "ยังมีผลอยู่"));
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement(null, null, "ตรวจแล้วครับ"));
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement("", null, "ตรวจแล้วครับ"));
    }

    [Fact]
    public void ตรวจคำขอ_ยกเลิกที่กรมสรรพากรต้องมีเลขอ้างอิง_บันทึกต้องยาวพอ()
    {
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeCancelledAtRd, "  ", "ยกเลิกแล้วที่กรมฯ"));
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeRejectedOrNotReceived, null, " ok  "));
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeRejectedOrNotReceived, null, null));
    }
}
