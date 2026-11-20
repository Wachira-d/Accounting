using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 279 (คำตัดสิน 279-1/279-2/279-3) — เจ้าของรายงาน: กลุ่ม LINE ที่ผูกแล้ว พิมพ์ <c>#HeyNextAcc</c> อีกครั้งได้ "รหัสผูกกลุ่ม GB-…" ใหม่ ·
/// ตัวตัดสิน pure: <see cref="LineGroupBindRule.ShouldIssueCode"/> (ผูกแล้ว ⇒ การ์ดสถานะ · <c>#HeyNextAcc เพิ่ม</c> ⇒ รหัส) · การ์ดสถานะไม่มีเลขภาษี/ยอดเงิน ·
/// สถานะความยินยอม <see cref="ProviderConsent.Classify"/> · ทางไปต่อชี้เมนูที่มีจริง (ล็อกกับไฟล์หน้าเว็บ)
/// </summary>
public class LineGroupStatusRound279Tests
{
    [Theory]
    [InlineData("#HeyNextAcc เพิ่ม", LineGroupCommand.HeyNextAccAdd)]
    [InlineData("  ＃heynextacc   เพิ่ม ", LineGroupCommand.HeyNextAccAdd)]
    [InlineData("#HeyNextAcc add", LineGroupCommand.HeyNextAccAdd)]
    [InlineData("#HeyNextAcc", LineGroupCommand.HeyNextAcc)]
    [InlineData("#HeyNextAccเพิ่ม", LineGroupCommand.None)]          // ต้องเว้นวรรค — กันข้อความแปลกปลอม
    [InlineData("#HeyNextAcc เพิ่มผู้ติดต่อ", LineGroupCommand.None)]
    [InlineData("เพิ่ม #HeyNextAcc", LineGroupCommand.None)]
    public void คำสั่งเพิ่ม_รับเฉพาะรูปที่กำหนด(string text, LineGroupCommand expected) => Assert.Equal(expected, LineGroupBindRule.Parse(text));

    [Fact]
    public void คำสั่งเพิ่ม_นับเป็นคำสั่งกลุ่มของ_OA_กลาง() => Assert.True(LineGroupBindRule.CentralGroupEventActionable("message", "#HeyNextAcc เพิ่ม"));

    [Theory]
    [InlineData(LineGroupCommand.HeyNextAcc, 0, true)]     // ยังไม่ผูก ⇒ รหัสเหมือนเดิม (ทิศตรงข้าม — ไม่ถูกแตะ)
    [InlineData(LineGroupCommand.HeyNextAcc, 1, false)]    // ผูกแล้ว ⇒ การ์ดสถานะ (บั๊กที่เจ้าของรายงาน)
    [InlineData(LineGroupCommand.HeyNextAcc, 3, false)]
    [InlineData(LineGroupCommand.HeyNextAccAdd, 0, true)]
    [InlineData(LineGroupCommand.HeyNextAccAdd, 2, true)]  // ผูกผู้ติดต่ออีกราย
    [InlineData(LineGroupCommand.ByeNextAcc, 0, false)]
    [InlineData(LineGroupCommand.None, 0, false)]
    public void ออกรหัสหรือการ์ดสถานะ(LineGroupCommand cmd, int bound, bool issue) => Assert.Equal(issue, LineGroupBindRule.ShouldIssueCode(cmd, bound));

    [Fact]
    public void การ์ดสถานะ_บอกผูกกับใคร_เรื่องที่ส่ง_ความยินยอม_ทางไปต่อ_ไม่มีรหัส()
    {
        var text = LineGroupBindRule.Replies.Status("บริษัท เน็ก แอค จำกัด", "[TakeTime] Accounting", new[]
        {
            new LineGroupBindRule.GroupStatusLine("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", new[] { "เอกสาร", "ทวงชำระ/ใบแจ้งยอด", "ใบเสร็จ", "50 ทวิ" },
                ProviderConsent.StateLabel(ProviderConsent.GroupConsentState.Pending), "https://app.nextacc.test/pages/billing.html?company=x#provider-consent"),
            new LineGroupBindRule.GroupStatusLine("ผู้ติดต่อทั่วไป", new[] { "เอกสาร" }, null, null),
        }, ProviderConsent.StaffRequestMenuPath);
        Assert.Contains("[TakeTime] Accounting", text);
        Assert.Contains("บริษัท เน็ก แอค จำกัด", text);
        Assert.Contains("2 ผู้ติดต่อ", text);
        Assert.Contains("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส — เรื่องที่ส่ง: เอกสาร, ทวงชำระ/ใบแจ้งยอด, ใบเสร็จ, 50 ทวิ", text);
        Assert.Contains("รอเจ้าของบริษัทยืนยันบนเว็บ", text);
        Assert.Contains("#provider-consent", text);
        Assert.Contains(ProviderConsent.StaffRequestMenuPath, text);
        Assert.Contains(LineGroupBindRule.AddCommandText, text);
        Assert.Contains("#ByeNextAcc", text);
        Assert.DoesNotContain("GB-", text);
        Assert.DoesNotContain("รหัสผูกกลุ่ม", text);
        Assert.DoesNotContain("บาท", text);   // ไม่มียอดเงินในกลุ่ม
        // ผู้ติดต่อที่ไม่ใช่บริษัทลูกค้า ⇒ ไม่มีบรรทัดความยินยอม (ไม่ใช่ "ยังไม่ได้ขอ" ที่ทำให้เข้าใจผิด)
        var plainOnly = LineGroupBindRule.Replies.Status("บริษัท ก", null, new[] { new LineGroupBindRule.GroupStatusLine("ผู้ติดต่อทั่วไป", new[] { "เอกสาร" }, null, null) }, null);
        Assert.DoesNotContain("บันทึกการชำระภาษีจากสลิป", plainOnly);
        Assert.DoesNotContain("ส่งคำขอยืนยัน", plainOnly);
        Assert.Contains("\"กลุ่มนี้\"", plainOnly);
    }

    [Fact]
    public void สถานะความยินยอม_สี่แบบจากตัวจำแนกเดียว()
    {
        ProviderConsent.Verdict V(bool ok, string code) => new(ok, code, "-", null);
        Assert.Equal(ProviderConsent.GroupConsentState.Granted, ProviderConsent.Classify(V(true, ProviderConsent.Codes.Ok), openRequest: true));
        Assert.Equal(ProviderConsent.GroupConsentState.PowerOfAttorney, ProviderConsent.Classify(V(false, ProviderConsent.Codes.PowerOfAttorneyOnly), false));
        Assert.Equal(ProviderConsent.GroupConsentState.PowerOfAttorney, ProviderConsent.Classify(V(false, ProviderConsent.Codes.PowerOfAttorneyPendingApproval), true));
        Assert.Equal(ProviderConsent.GroupConsentState.Pending, ProviderConsent.Classify(V(false, ProviderConsent.Codes.NoConsent), openRequest: true));
        Assert.Equal(ProviderConsent.GroupConsentState.NotRequested, ProviderConsent.Classify(V(false, ProviderConsent.Codes.NoConsent), openRequest: false));
        Assert.Equal(ProviderConsent.GroupConsentState.NotRequested, ProviderConsent.Classify(V(false, ProviderConsent.Codes.Withdrawn), openRequest: false));
        Assert.False(ProviderConsent.NeedsClientAction(ProviderConsent.GroupConsentState.Granted));
        Assert.True(ProviderConsent.NeedsClientAction(ProviderConsent.GroupConsentState.Pending));
        Assert.True(ProviderConsent.NeedsClientAction(ProviderConsent.GroupConsentState.NotRequested));
        Assert.Equal(4, Enum.GetValues<ProviderConsent.GroupConsentState>().Select(ProviderConsent.StateLabel).Distinct().Count());
    }

    [Fact]
    public void ลิงก์ยืนยัน_มีโดเมน_ใช้ลิงก์เต็ม_ไม่มี_ชี้เมนูจริง()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("https://app.nextacc.test/pages/billing.html?company=11111111-2222-3333-4444-555555555555#provider-consent",
            ProviderConsent.GrantLinkOrMenu("https://app.nextacc.test/", id));
        var menu = ProviderConsent.GrantLinkOrMenu(null, id);
        Assert.Contains(ProviderConsent.ClientMenuPath, menu);
        Assert.DoesNotContain("ค่าบริการ ›", menu);   // เมนูที่ไม่มีในระบบ (บั๊กเดิม)
    }

    /// <summary>279-3: ทางไปต่อทุกข้อความต้องเป็นเมนู/ปุ่มที่มีจริง — ล็อกป้ายกับไฟล์หน้าเว็บ (เปลี่ยนชื่อเมนูแล้วเทสต์แดง ไม่ใช่การ์ดชี้ทางตัน)</summary>
    [Fact]
    public void เมนูที่การ์ดชี้_มีอยู่จริงในหน้าเว็บ()
    {
        var root = RepoRoot();
        string Page(params string[] p) => File.ReadAllText(Path.Combine(new[] { root, "Accounting", "wwwroot" }.Concat(p).ToArray()));
        var layout = Page("js", "layout.js");
        var client = ProviderConsent.ClientMenuPath.Split(" › ");
        Assert.Contains($"label: '{client[0]}'", layout);
        Assert.Contains("ใบแจ้งหนี้ & ใบเสร็จ", client[1]);
        Assert.Contains(client[1], layout);
        var billing = Page("pages", "billing.html");
        Assert.Contains($"id=\"provider-consent\"><h3>{client[2]}</h3>", billing);
        Assert.Contains("'" + ProviderConsent.PageAnchor + "'", billing);
        var staff = ProviderConsent.StaffRequestMenuPath.Split(" › ");
        Assert.Contains($"label: '{staff[0]}'", layout);
        Assert.Contains(staff[1] + "</a>", Page("pages", "tax-remittance.html"));
        var desk = Page("pages", "tax-slip-desk.html");
        Assert.Contains(">" + staff[3] + "</button>", desk);
        Assert.True(File.Exists(Path.Combine(root, "Accounting", "wwwroot", ProviderConsent.StaffDeskPage.TrimStart('/'))));
        // ทุกที่ที่เคยเขียนเมนู "ค่าบริการ ›" (ไม่มีในระบบ) — เหลือ 0 จุด
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Accounting"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("เมนู ค่าบริการ ›", File.ReadAllText(f));
    }

    [Fact]
    public void ขั้นตอนบนหน้าผู้ติดต่อ_บอกคำสั่งเพิ่ม() => Assert.Contains(LineGroupBindRule.AddCommandText, LineGroupBindRule.GroupAddHint);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
