using System.Text;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 (A-M2 · A-M3) — webhook ของ LINE OA กลาง เส้น 1:1 (pure · ตัวแทน interface ผ่าน DispatchProxy · ไม่แตะฐาน):
/// <para>(1) event ที่เห็นแล้ว (ส่งซ้ำ) ไม่ถูกประมวล — ทิศตรงข้าม: event ใหม่ถูกประมวลและตอบด้วย reply token</para>
/// <para>(2) event หนึ่งล้ม ⇒ ถอนการประทับ + event ถัดไปในชุดเดียวกันยังถูกประมวล · webhook ตอบ 200</para>
/// <para>(3) <see cref="LineUnboundSender"/>: คนที่ยังไม่ผูกเกินเพดาน = เงียบ · reply ล้มถอยไป push ได้เฉพาะผู้ที่ผูกแล้ว</para>
/// </summary>
public class CentralWebhookRound258Tests
{
    private sealed class Log
    {
        public List<string> Bot { get; } = new();
        public List<string> Marks { get; } = new();
        public List<string> Unmarks { get; } = new();
        public HashSet<string> AlreadySeen { get; } = new(StringComparer.Ordinal);
        public string? ThrowOnText { get; set; }
    }

    private static string Event(string eventId, string text, string replyToken = "rt-1", string userId = "U258aaaaaaaaaaaaaaaaaaaaaaaaaaaaa1")
        => "{\"type\":\"message\",\"webhookEventId\":\"" + eventId + "\",\"replyToken\":\"" + replyToken + "\","
           + "\"source\":{\"type\":\"user\",\"userId\":\"" + userId + "\"},\"message\":{\"type\":\"text\",\"id\":\"m1\",\"text\":\"" + text + "\"}}";

    private static string Body(params string[] events) => "{\"destination\":\"Ubot258\",\"events\":[" + string.Join(",", events) + "]}";

    private static LineWebhookController Controller(Log log, string body, bool withDedupe = true)
    {
        var bot = F4Proxy<ILineBotService>.Create((name, args) =>
        {
            switch (name)
            {
                case nameof(ILineBotService.VerifySignatureAsync): return true;
                case nameof(ILineBotService.HandleMessageAsync):
                    var text = (string)args[1]!;
                    if (log.ThrowOnText != null && text == log.ThrowOnText) throw new InvalidOperationException("ล้ม (ทดสอบ)");
                    log.Bot.Add("message:" + text);
                    return "ตอบ:" + text;
                case nameof(ILineBotService.ReplyAsync):
                    log.Bot.Add("reply:" + args[1] + "|" + (args[2] ?? "(ไม่มี token)"));
                    return null;
                default: return null;
            }
        });
        var groups = F4Proxy<IContactLineGroupService>.Create((name, args) =>
        {
            var id = args.Length > 0 ? args[0] as string ?? "" : "";
            if (name == nameof(IContactLineGroupService.MarkEventSeenAsync))
            {
                log.Marks.Add(id);
                return log.AlreadySeen.Add(id);   // true = เพิ่งเห็นครั้งแรก
            }
            if (name == nameof(IContactLineGroupService.UnmarkEventSeenAsync)) { log.Unmarks.Add(id); log.AlreadySeen.Remove(id); }
            return null;
        });
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers["X-Line-Signature"] = "sig";
        return new LineWebhookController(bot, NullLogger<LineWebhookController>.Instance, null, null, withDedupe ? groups : null)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task eventใหม่_ถูกประมวลครั้งเดียว_ตอบด้วยreplyToken()
    {
        var log = new Log();
        var result = await Controller(log, Body(Event("ev-1", "ดูยอด", replyToken: "rt-abc"))).Receive();
        Assert.IsType<OkResult>(result);
        Assert.Equal(new[] { "message:ดูยอด", "reply:ตอบ:ดูยอด|rt-abc" }, log.Bot);
        Assert.Equal(new[] { "ev-1" }, log.Marks);
        Assert.Empty(log.Unmarks);
    }

    [Fact]
    public async Task eventที่LINEส่งซ้ำ_ไม่ถูกประมวลซ้ำ_ไม่ตอบซ้ำ()
    {
        var log = new Log();
        await Controller(log, Body(Event("ev-dup", "จ่าย เซเว่น 250"))).Receive();
        await Controller(log, Body(Event("ev-dup", "จ่าย เซเว่น 250"))).Receive();   // ส่งซ้ำ (webhookEventId เดิม)
        Assert.Single(log.Bot, x => x == "message:จ่าย เซเว่น 250");
        Assert.Single(log.Bot, x => x.StartsWith("reply:", StringComparison.Ordinal));
        // ทิศตรงข้าม: ข้อความเดียวกันแต่เป็น event ใหม่ (ผู้ใช้พิมพ์อีกครั้งจริง) ⇒ ประมวล
        await Controller(log, Body(Event("ev-new", "จ่าย เซเว่น 250"))).Receive();
        Assert.Equal(2, log.Bot.Count(x => x == "message:จ่าย เซเว่น 250"));
    }

    [Fact]
    public async Task eventหนึ่งล้ม_ถอนการประทับ_eventถัดไปยังถูกประมวล_ตอบ200()
    {
        var log = new Log { ThrowOnText = "พัง" };
        var result = await Controller(log, Body(Event("ev-bad", "พัง"), Event("ev-ok", "ดูยอด"))).Receive();
        Assert.IsType<OkResult>(result);
        Assert.Equal(new[] { "ev-bad" }, log.Unmarks);
        Assert.Contains("message:ดูยอด", log.Bot);
        Assert.DoesNotContain("ev-bad", log.AlreadySeen);
        Assert.Contains("ev-ok", log.AlreadySeen);
    }

    [Fact]
    public async Task ไม่มีตัวกันซ้ำ_ประกอบบางส่วน_ยังประมวลและตอบได้()
    {
        var log = new Log();
        await Controller(log, Body(Event("ev-1", "ดูยอด")), withDedupe: false).Receive();
        Assert.Equal(new[] { "message:ดูยอด", "reply:ตอบ:ดูยอด|rt-1" }, log.Bot);
        Assert.Empty(log.Marks);
    }

    // ───── ผู้ส่งที่ยังไม่ผูกบัญชี ─────

    [Fact]
    public void ยังไม่ผูก_ในเพดาน_ได้คำตอบ_เกินเพดาน_เงียบ()
    {
        Assert.Equal("ผูกบัญชีก่อน", LineUnboundSender.Reply(withinLimit: true, "ผูกบัญชีก่อน"));
        Assert.Null(LineUnboundSender.Reply(withinLimit: false, "ผูกบัญชีก่อน"));
        Assert.Equal(3, LineUnboundSender.PerMinute);
        Assert.Equal(20, LineUnboundSender.PerDay);
        Assert.Equal("lineunbound:U1", LineUnboundSender.Key("U1"));
    }

    [Theory]
    [InlineData(true, true, false)]     // reply สำเร็จ ⇒ ไม่ push ซ้ำ
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]     // reply ล้ม + ผู้ส่งผูกแล้ว (งาน OCR ช้าจน token หมดอายุ) ⇒ push
    [InlineData(false, false, false)]   // reply ล้ม + คนแปลกหน้า ⇒ ไม่ push (กันโควตา OA กลาง)
    public void ถอยไปpush_เฉพาะผู้ที่ผูกแล้วและreplyล้ม(bool replied, bool bound, bool expected)
        => Assert.Equal(expected, LineUnboundSender.PushFallbackAllowed(replied, bound));
}
