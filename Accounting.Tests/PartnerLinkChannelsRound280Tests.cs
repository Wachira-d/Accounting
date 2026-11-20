using System;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 280 — pure: ตัวตัดสินช่องทางแจ้งคำขอเชื่อมคู่ค้า (<see cref="PartnerLinkRequestChannels"/>) ·
/// เจ้าของรายงาน "คำขอส่งไปทางไหน ไลน์ที่ผูกกันไว้ไม่เด้ง เมล์ไม่มี" ⇒ เลือกช่องได้ · ช่องปิดพร้อมเหตุ + ลิงก์แก้ · กันส่งถี่ · ข้อความไม่มี PII เกินจำเป็น
/// </summary>
public class PartnerLinkChannelsRound280Tests
{
    private static readonly DateTime Now = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void เลือกช่อง_รับเฉพาะกลุ่มLINEกับอีเมล_InAppไม่ใช่ตัวเลือก_ชื่อแปลกถูกตัด()
    {
        var s = PartnerLinkRequestChannels.NormalizeSelection(new[] { "Email", "InApp", "LineGroup", "Email", "Sms", null, " LineGroup " });
        Assert.Equal(new[] { PartnerLinkRequestChannels.LineGroup, PartnerLinkRequestChannels.Email }, s);
        Assert.Empty(PartnerLinkRequestChannels.NormalizeSelection(null));
    }

    [Fact]
    public void กันส่งถี่_ครั้งแรกได้_ภายใน12ชมไม่ได้_ครบ3ครั้งไม่ได้()
    {
        Assert.Null(PartnerLinkRequestChannels.ThrottleProblem(Array.Empty<DateTime>(), Now));
        var recent = PartnerLinkRequestChannels.ThrottleProblem(new[] { Now.AddHours(-2) }, Now);
        Assert.NotNull(recent);
        Assert.Contains("12 ชั่วโมง", recent);
        Assert.Null(PartnerLinkRequestChannels.ThrottleProblem(new[] { Now.AddHours(-13) }, Now));                 // ทิศตรงข้าม: พ้นช่วงพักส่งได้
        Assert.Contains("ครบ 3 ครั้ง", PartnerLinkRequestChannels.ThrottleProblem(new[] { Now.AddDays(-3), Now.AddDays(-2), Now.AddDays(-1) }, Now));
    }

    [Fact]
    public void นับเฉพาะครั้งที่ลองส่งจริง_ช่องปิดหรือถูกกันไม่นับ()
    {
        Assert.True(PartnerLinkRequestChannels.CountsAsAttempt(PartnerLinkRequestChannels.StatusSent));
        Assert.True(PartnerLinkRequestChannels.CountsAsAttempt(PartnerLinkRequestChannels.StatusFailed));
        Assert.True(PartnerLinkRequestChannels.CountsAsAttempt(PartnerLinkRequestChannels.StatusSending));
        Assert.True(PartnerLinkRequestChannels.CountsAsAttempt(PartnerLinkRequestChannels.StatusQueued));
        Assert.False(PartnerLinkRequestChannels.CountsAsAttempt(PartnerLinkRequestChannels.StatusRefused));
    }

    [Fact]
    public void คำขอเชื่อมแล้วหรือหมดอายุ_ไม่แจ้ง_รอตอบแจ้งได้()
    {
        Assert.Null(PartnerLinkRequestChannels.RequestProblem(null, Now.AddDays(5), Now));
        Assert.Contains("เชื่อมแล้ว", PartnerLinkRequestChannels.RequestProblem(Now.AddDays(-1), Now.AddDays(5), Now));
        Assert.Contains("หมดอายุ", PartnerLinkRequestChannels.RequestProblem(null, Now.AddDays(-1), Now));
    }

    [Fact]
    public void ผู้ติดต่อ_ต้องพบ_เลขตรงหรือว่าง_เลขอื่นไม่ส่ง()
    {
        const string tax = "0203562005871";
        Assert.Null(PartnerLinkRequestChannels.ContactProblem(true, "0-2035-62005-87-1", tax));
        Assert.Null(PartnerLinkRequestChannels.ContactProblem(true, null, tax));
        Assert.Equal(PartnerLinkRequestChannels.ContactTaxMismatchReason, PartnerLinkRequestChannels.ContactProblem(true, "0105556000245", tax));
        Assert.Equal(PartnerLinkRequestChannels.ContactNotFoundReason, PartnerLinkRequestChannels.ContactProblem(false, null, tax));
    }

    [Fact]
    public void สรุปช่อง_ไม่มีกลุ่มปิดพร้อมเหตุและลิงก์_ไม่ได้ติ๊กเรื่องบอกเหตุ_ใช้ได้บอกผู้รับ()
    {
        var cid = Guid.NewGuid();
        var none = PartnerLinkRequestChannels.Summarize(PartnerLinkRequestChannels.LineGroup, cid,
            new[] { new PartnerLinkRequestChannels.EndpointView(NotifyEndpointKind.Email, "ap@x.co.th", true, null) });
        Assert.False(none.Available);
        Assert.Equal(PartnerLinkRequestChannels.NoLineGroupReason, none.Reason);
        Assert.Equal(PartnerLinkRequestChannels.ContactNotifyUrl(cid), none.FixUrl);

        // กลุ่มของเจ้าของ (เอกสาร/ทวงชำระ/ใบเสร็จ/50 ทวิ) ยังไม่ได้ติ๊กเรื่องใหม่ ⇒ ปิดพร้อมเหตุจากตัวหาผู้รับ (ไม่เปิดให้เอง — คำตัดสิน 280-2)
        var notTicked = PartnerLinkRequestChannels.Summarize(PartnerLinkRequestChannels.LineGroup, cid,
            new[] { new PartnerLinkRequestChannels.EndpointView(NotifyEndpointKind.LineGroup, "กลุ่ม แอม แฮปปี้เนส", false, "ไม่ได้เลือกรับเรื่อง \"คำขอเชื่อมคู่ค้า\"") });
        Assert.False(notTicked.Available);
        Assert.Contains("คำขอเชื่อมคู่ค้า", notTicked.Reason);
        Assert.Contains("ช่องทางแจ้งเตือน", notTicked.Reason);

        var ok = PartnerLinkRequestChannels.Summarize(PartnerLinkRequestChannels.LineGroup, cid,
            new[] { new PartnerLinkRequestChannels.EndpointView(NotifyEndpointKind.LineGroup, "กลุ่ม แอม", true, null) });
        Assert.True(ok.Available);
        Assert.Equal(new[] { "กลุ่ม แอม" }, ok.Targets);

        var noEmail = PartnerLinkRequestChannels.Summarize(PartnerLinkRequestChannels.Email, cid, Array.Empty<PartnerLinkRequestChannels.EndpointView>());
        Assert.Equal(PartnerLinkRequestChannels.NoEmailReason, noEmail.Reason);
    }

    [Fact]
    public void ข้อความ_มีลิงก์_เลขภาษีปิดบัง_ไม่มีเลขเต็ม_HtmlEncodeชื่อผู้ส่ง()
    {
        var m = PartnerLinkRequestChannels.Compose("<b>ร้าน</b> & co", "0203562005871", "00000", Now.AddDays(30), "https://app.nextacc.test/");
        Assert.Contains("https://app.nextacc.test" + PartnerLinkRequestChannels.LandingPath, m.Text);
        Assert.Contains("https://app.nextacc.test" + PartnerLinkRequestChannels.RegisterPath, m.Text);
        Assert.DoesNotContain("0203562005871", m.Text);
        Assert.DoesNotContain("0203562005871", m.Html);
        Assert.Contains(PartnerLinkRequestChannels.PartnerTaxMasked("0203562005871"), m.Text);
        Assert.Contains("สำนักงานใหญ่", m.Text);
        Assert.DoesNotContain("<b>ร้าน</b>", m.Html);                 // XSS: ชื่อบริษัทผู้ส่งถูก encode
        Assert.Contains("&lt;b&gt;", m.Html);
        Assert.Contains("หมดอายุ", m.Text);
    }

    [Fact]
    public void ผู้รับอีเมลปิดบัง_กลุ่มแสดงชื่อ()
    {
        Assert.DoesNotContain("somchai@", PartnerLinkRequestChannels.RecipientLabel(PartnerLinkRequestChannels.Email, "somchai@amhappy.co.th"));
        Assert.Equal("กลุ่ม แอม", PartnerLinkRequestChannels.RecipientLabel(PartnerLinkRequestChannels.LineGroup, "กลุ่ม แอม"));
    }

    [Fact]
    public void เรื่องใหม่PartnerLink_ส่งทางLINEกลุ่มได้_เป็นค่าตั้งต้นของปลายทางใหม่()
    {
        Assert.Null(NotifyTopics.LineUnsupportedReason(NotifyTopic.PartnerLink, NotifyEndpointKind.LineGroup));
        Assert.Contains(NotifyTopic.PartnerLink, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineGroup));
        Assert.Contains(NotifyTopic.PartnerLink, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
        Assert.Equal("คำขอเชื่อมคู่ค้า", NotifyTopics.Label(NotifyTopic.PartnerLink));
        // ปลายทางเดิมที่เก็บเรื่องไว้ (ไม่มี PartnerLink) อ่านแล้วยังไม่มี — ไม่ถูกเปิดให้เอง
        var stored = NotifyTopics.Parse("[\"Document\",\"Dunning\",\"Receipt\",\"Wht\"]")!;
        Assert.DoesNotContain(NotifyTopic.PartnerLink, stored);
    }
}
