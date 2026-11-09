using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 256 — OA กลางของ NextAcc ตอบคำสั่งในกลุ่มด้วยเหตุ + ทางไปต่อ (เดิมเงียบ) · ข้อความอื่น/เข้ากลุ่มไม่ตอบ</summary>
public class LineCentralOaGroupReplyRound256Tests
{
    [Theory]
    [InlineData("#HeyNextAcc")]
    [InlineData("  ＃heynextacc ")]
    [InlineData("#ByeNextAcc")]
    public void คำสั่งกลุ่มถึง_OA_กลาง_ได้คำตอบบอกเหตุและทางไปต่อ(string text)
    {
        var reply = LineGroupBindRule.CentralOaGroupReply("message", text);
        Assert.Equal(LineGroupBindRule.Replies.CentralOaNotSupported, reply);
        Assert.Contains("LINE OA ของบริษัท", reply);
        Assert.Contains("Webhook URL", reply);
    }

    [Theory]
    [InlineData("message", "สวัสดีครับ")]
    [InlineData("message", "heynextacc")]   // ไม่มี # ⇒ ไม่ใช่คำสั่ง
    [InlineData("message", null)]
    [InlineData("join", null)]               // ไม่ทักตอนเข้ากลุ่ม (OA กลางอยู่ในกลุ่มทีมแอดมินด้วย)
    [InlineData("memberJoined", null)]
    public void ทิศตรงข้าม_ข้อความอื่นหรือเหตุการณ์อื่น_ไม่ตอบ(string type, string? text)
        => Assert.Null(LineGroupBindRule.CentralOaGroupReply(type, text));
}
