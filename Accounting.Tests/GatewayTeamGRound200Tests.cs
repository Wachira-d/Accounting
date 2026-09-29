using System.Net;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม G — payment gateway ส่วนที่ยังไม่เคยถูกตรวจ (review198-E "ยังไม่ได้ตรวจ" · E-3 · E-4 · E2-12 · DECISIONS ข้อ 18)
///
/// <para>ทุกกลุ่มมีสองทิศ: เคสที่เคยพัง (หัวรุ่น API หาย · เลข event พาคำขอไปปลายทางอื่น · ค่าธรรมเนียมที่แก้มือถูกทับ · ขอบช่วงวันเลื่อน 7 ชม. ·
/// เปลี่ยนโหมด VAT แล้วรอบเก่าไม่สมดุล · ใบสำคัญคืนเงินผิดเดือน · สาขาว่างกลายเป็นสำนักงานใหญ่ · เคลมล่วงหน้า · เครื่อง POS ปิดบิลโอนไม่ได้โดยไม่มีคำเตือน)
/// และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ</para>
/// </summary>
public class GatewayTeamGRound200Tests
{
    private const string ValidTaxId = "0105536000313";

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ═══ ข้อ 18: ปักรุ่น API ของผู้ให้บริการ (adapter อ่านชื่อช่องของรุ่น 2019-05-29 เช่น refunded_amount) ═══

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public string Body { get; set; } = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FakeFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? value) => value;
        public bool IsUsable(string? value) => !string.IsNullOrEmpty(value);
    }

    private static (OmisePaymentProvider Provider, CapturingHandler Handler, PaymentProviderConfig Config) NewProvider()
    {
        var h = new CapturingHandler();
        var p = new OmisePaymentProvider(new FakeFactory(h), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);
        var cfg = new PaymentProviderConfig { ProviderCode = OmisePaymentProvider.Code, Mode = PaymentProviderMode.Test, TestSecretKeyProtected = "k_test_1" };
        return (p, h, cfg);
    }

    [Fact]
    public async Task R18_ทุกคำขอส่งหัวรุ่นAPIที่adapterอ่านชื่อช่อง()
    {
        var (p, h, cfg) = NewProvider();
        await p.TestConnectionAsync(cfg);
        h.Body = "{\"id\":\"rfnd_1\"}";
        await p.RefundAsync(new PaymentIntent { ProviderRef = "chrg_1", ProviderCode = OmisePaymentProvider.Code, IdempotencyKey = "k" },
            10m, "r", "m", cfg);

        Assert.Equal(2, h.Requests.Count);
        foreach (var req in h.Requests)
        {
            Assert.True(req.Headers.TryGetValues(OmisePaymentProvider.ApiVersionHeader, out var v), "ไม่มีหัวรุ่น API");
            Assert.Equal(new[] { "2019-05-29" }, v!.ToArray());
        }
        // ค่าคงที่ตัวเดียว — ชื่อช่อง refunded_amount เป็นของรุ่นนี้ (รุ่น 2017-11-02 ใช้ refunded)
        Assert.Equal("2019-05-29", OmisePaymentProvider.ApiVersion);
        Assert.Equal("Omise-Version", OmisePaymentProvider.ApiVersionHeader);
    }

    // ═══ webhook: เลข event จาก body สาธารณะถูกต่อเข้า path ที่แนบ secret key ═══

    [Theory]
    [InlineData("../charges/chrg_test_1")]
    [InlineData("evnt_1/../../account")]
    [InlineData("evnt_1?expand=true")]
    [InlineData("evnt_1%2F..")]
    [InlineData("chrg_test_1")]
    [InlineData("evnt_")]
    [InlineData("")]
    [InlineData(null)]
    public void WH_เลขeventรูปผิด_ไม่ผ่าน(string? id)
        => Assert.False(OmisePaymentProvider.IsWellFormedEventId(id));

    [Theory]
    [InlineData("evnt_test_5vxs0ajpo78jmhytybb")]
    [InlineData("evnt_5vxs0ajpo78jmhytybb")]
    public void WH_ทิศตรงข้าม_เลขeventจริงผ่าน(string id)
        => Assert.True(OmisePaymentProvider.IsWellFormedEventId(id));

    [Fact]
    public async Task WH_เลขeventรูปผิด_ไม่ยิงคำขอออกเลย_รูปถูกยิงหนึ่งครั้ง()
    {
        var (p, h, cfg) = NewProvider();
        var bad = await p.VerifyWebhookAsync("{\"id\":\"../charges/chrg_x\"}", new Dictionary<string, string>(), cfg);
        Assert.Null(bad);
        Assert.Empty(h.Requests);

        h.Body = "{\"data\":{\"object\":\"refund\"}}";   // ไม่ใช่ charge ⇒ null แต่ต้องถามจริงหนึ่งครั้ง
        var good = await p.VerifyWebhookAsync("{\"id\":\"evnt_test_abc\"}", new Dictionary<string, string>(), cfg);
        Assert.Null(good);
        Assert.Single(h.Requests);
        Assert.EndsWith("/events/evnt_test_abc", h.Requests[0].RequestUri!.AbsolutePath);
    }

    // ═══ ค่าธรรมเนียมจากผู้ให้บริการห้ามทับค่าที่แก้มือ/อยู่ในรอบโอนแล้ว ═══

    [Fact]
    public void FEE_รายการสำเร็จแล้วที่มีค่าธรรมเนียม_ไม่ถูกทับ()
    {
        Assert.False(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Succeeded, 39.06m, alreadySettled: false));
        Assert.False(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.PartiallyRefunded, 39.06m, alreadySettled: false));
        // บันทึกรอบโอนแล้ว — ห้ามทับแม้ยังไม่มีค่า
        Assert.False(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Pending, null, alreadySettled: true));
    }

    [Fact]
    public void FEE_ทิศตรงข้าม_ยังเปิดอยู่หรือยังไม่มีค่า_รับค่าจากผู้ให้บริการ()
    {
        // pending รายงาน 0 แล้วค่าจริงมาตอนสำเร็จ — ต้องทับได้
        Assert.True(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Pending, 0m, alreadySettled: false));
        Assert.True(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Created, null, alreadySettled: false));
        Assert.True(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Succeeded, null, alreadySettled: false));
    }

    // ═══ ยืนยันด้วยมือบนรายการที่ไม่มี charge ที่ผู้ให้บริการ ═══

    [Fact]
    public void MC_ผู้ให้บริการถือเงินแต่ไม่มีcharge_บล็อกพร้อมทางไปต่อ()
    {
        var m = PaymentIntentPolicy.ManualConfirmBlockReason(true, null);
        Assert.NotNull(m);
        Assert.Contains("ต้นทางโดยตรง", m);
        Assert.NotNull(PaymentIntentPolicy.ManualConfirmBlockReason(true, "  "));
    }

    [Fact]
    public void MC_ทิศตรงข้าม_มีchargeหรือเส้นสลิปเข้าธนาคารตรง_ยืนยันได้()
    {
        Assert.Null(PaymentIntentPolicy.ManualConfirmBlockReason(true, "chrg_1"));
        Assert.Null(PaymentIntentPolicy.ManualConfirmBlockReason(false, null));
    }

    // ═══ G-2: ขอบช่วง "วันที่รับเงิน" = เที่ยงคืนเวลาไทย (เดิมเลื่อน 7 ชม.) ═══

    [Fact]
    public void RANGE_รับเงินตีหนึ่งของวันแรกนับเข้า_ตีหนึ่งของวันถัดจากวันสุดท้ายไม่นับ()
    {
        var (start, end) = GatewaySettlementMath.ConfirmedRangeUtc(new DateTime(2026, 9, 14), new DateTime(2026, 9, 20));
        Assert.Equal(Utc(2026, 9, 13, 17), start);
        Assert.Equal(Utc(2026, 9, 20, 17), end);

        var earlyFirstDay = Utc(2026, 9, 13, 18);   // 14/09 01:00 เวลาไทย — เดิมหลุด
        var earlyNextDay = Utc(2026, 9, 20, 18);    // 21/09 01:00 เวลาไทย — เดิมถูกนับเข้า
        Assert.True(earlyFirstDay >= start && earlyFirstDay < end);
        Assert.False(earlyNextDay >= start && earlyNextDay < end);
    }

    [Fact]
    public void RANGE_ทิศตรงข้าม_รับเงินกลางวันในช่วงยังอยู่ในช่วง_และจุดตัดคืนเงินใช้สูตรเดียวกัน()
    {
        var (start, end) = GatewaySettlementMath.ConfirmedRangeUtc(new DateTime(2026, 9, 14), new DateTime(2026, 9, 20));
        foreach (var t in new[] { Utc(2026, 9, 14, 5), Utc(2026, 9, 20, 9), Utc(2026, 9, 17, 12) })
            Assert.True(t >= start && t < end);
        Assert.Equal(GatewaySettlementMath.RefundCutoffUtc(new DateTime(2026, 9, 14)), start);
    }

    // ═══ E2-12: กระทบยอดแถวที่บันทึกรอบแล้วใช้ค่าธรรมเนียม ณ วันบันทึกรอบ ═══

    [Fact]
    public void REC_เปลี่ยนโหมดVATหลังบันทึกรอบ_รอบเก่ายังสมดุลเมื่อรู้ค่าธรรมเนียมที่ถูกหัก()
    {
        // บันทึกรอบในโหมด AddedOnTop: ค่าธรรมเนียม 30 + VAT 2.10 = 32.10 ⇒ โอนเข้า 967.90 · วันนี้เปลี่ยนโหมดเป็น None
        var withStored = new GatewayIntentAmounts(1000m, 30m, 0m, 967.90m, false, true,
            FeeVatMode: GatewayFeeVatMode.None, SettledFeeDeducted: 32.10m);
        var r = GatewayReconciliation.Compute(new[] { withStored });
        Assert.True(r.IsBalanced, $"ต่าง {r.UnexplainedDifference}");
        Assert.Equal(32.10m, r.FeeTotal);
    }

    [Fact]
    public void REC_ทิศตรงข้าม_แถวเก่าไม่มีค่า_ใช้สูตรเดิม_และแถวที่โหมดไม่เปลี่ยนยังสมดุล()
    {
        var legacy = new GatewayIntentAmounts(1000m, 30m, 0m, 967.90m, false, true, FeeVatMode: GatewayFeeVatMode.None);
        Assert.Equal(2.10m, Math.Abs(GatewayReconciliation.Compute(new[] { legacy }).UnexplainedDifference));   // พฤติกรรมเดิม (ไม่เดา)

        var same = new GatewayIntentAmounts(1000m, 30m, 0m, 967.90m, false, true,
            FeeVatMode: GatewayFeeVatMode.AddedOnTop, SettledFeeDeducted: 32.10m);
        Assert.True(GatewayReconciliation.Compute(new[] { same }).IsBalanced);
        var unsettled = new GatewayIntentAmounts(1000m, 30m, 0m, null, false, false,
            FeeVatMode: GatewayFeeVatMode.AddedOnTop, SettledFeeDeducted: 99m);   // ยังไม่บันทึกรอบ — ค่านี้ต้องไม่ถูกใช้
        Assert.Equal(32.10m, GatewayReconciliation.Compute(new[] { unsettled }).FeeTotal);
    }

    // ═══ E2-12 + คำถามเจ้าของข้อ 4: ใบสำคัญเงินคืนที่ยืนยันทีหลัง ลงวันที่เงินออกจริง ═══

    [Fact]
    public void BOOK_พยายามคืนสิ้นเดือน_ตรวจผลเดือนถัดไป_งวดยังเปิด_ลงวันที่เงินออก()
    {
        var b = GatewayRefundMath.PastRefundBooking(Utc(2026, 8, 31, 10), Utc(2026, 9, 2, 3), attemptPeriodClosed: false);
        Assert.Equal(Utc(2026, 8, 31), b.EntryDate);
        Assert.Null(b.Note);
    }

    [Fact]
    public void BOOK_ทิศตรงข้าม_งวดของวันเงินออกปิดแล้ว_ลงวันนี้พร้อมบอกวันจริง_ไม่เงียบ()
    {
        var b = GatewayRefundMath.PastRefundBooking(Utc(2026, 8, 31, 10), Utc(2026, 9, 2, 3), attemptPeriodClosed: true);
        Assert.Equal(Utc(2026, 9, 2), b.EntryDate);
        Assert.NotNull(b.Note);
        Assert.Contains("31/08/2569", b.Note);
        // วันเดียวกัน — ไม่มีอะไรต้องเลื่อน
        var same = GatewayRefundMath.PastRefundBooking(Utc(2026, 9, 2, 1), Utc(2026, 9, 2, 3), attemptPeriodClosed: true);
        Assert.Equal(Utc(2026, 9, 2), same.EntryDate);
        Assert.Null(same.Note);
    }

    // ═══ E2-12: สาขาผู้ออกใบว่าง ≠ สำนักงานใหญ่ · เคลมล่วงหน้าไม่ได้ ═══

    [Fact]
    public void VAT_สาขาว่าง_บล็อกพร้อมบอกให้กรอกตามใบ()
    {
        var c = GatewayFeeVatClaim.Check(1m, 30m, "X1", Utc(2026, 9, 1), Utc(2026, 9, 1), "ผู้ให้บริการ", ValidTaxId, null, null);
        Assert.False(c.Ok);
        Assert.Contains("รหัสสาขา", c.Message);
        Assert.False(GatewayFeeVatClaim.Check(1m, 30m, "X1", Utc(2026, 9, 1), Utc(2026, 9, 1), "ผู้ให้บริการ", ValidTaxId, "  ", null).Ok);
    }

    [Fact]
    public void VAT_ทิศตรงข้าม_กรอกสำนักงานใหญ่หรือสาขาจริง_ผ่าน()
    {
        Assert.Equal("00000", GatewayFeeVatClaim.Check(1m, 30m, "X1", Utc(2026, 9, 1), Utc(2026, 9, 1),
            "ผู้ให้บริการ", ValidTaxId, "00000", null).BranchCode);
        Assert.Equal("00003", GatewayFeeVatClaim.Check(1m, 30m, "X1", Utc(2026, 9, 1), Utc(2026, 9, 1),
            "ผู้ให้บริการ", ValidTaxId, " 00003 ", null).BranchCode);
    }

    [Fact]
    public void VAT_วันที่เคลมในอนาคต_บล็อก_วันนี้และย้อนหลังผ่าน()
    {
        var now = Utc(2026, 9, 29, 3);   // 29/09 10:00 เวลาไทย
        Assert.NotNull(GatewayFeeVatClaim.FutureClaimDateMessage(new DateTime(2026, 9, 30), now));
        Assert.Null(GatewayFeeVatClaim.FutureClaimDateMessage(new DateTime(2026, 9, 29), now));
        Assert.Null(GatewayFeeVatClaim.FutureClaimDateMessage(new DateTime(2026, 8, 31), now));
        // 29/09 23:30 เวลาไทย = 29/09 16:30Z — ยังเป็น "วันนี้" ไม่ใช่อนาคต
        Assert.Null(GatewayFeeVatClaim.FutureClaimDateMessage(Utc(2026, 9, 29, 16, 30), now));
    }

    // ═══ E-3: เครื่อง POS ที่บิลโอน/พร้อมเพย์จะปิดไม่ได้ — เตือนก่อนขาย ═══

    [Fact]
    public void E3_บัญชีธนาคารหลายบัญชีหรือไม่มีเลย_เครื่องไม่ปัก_เตือนล่วงหน้า()
    {
        var many = MoneyAccountFallback.TerminalBankWarning(false, BankAccountPickOutcome.Ambiguous);
        Assert.NotNull(many);
        Assert.Contains("หลายบัญชี", many);
        Assert.Contains("ตั้งค่าเครื่อง", many);
        var none = MoneyAccountFallback.TerminalBankWarning(false, BankAccountPickOutcome.None);
        Assert.NotNull(none);
        Assert.Contains("ยังไม่มีบัญชีธนาคาร", none);
    }

    [Fact]
    public void E3_ทิศตรงข้าม_ปักแล้วหรือมีบัญชีเดียว_ไม่เตือน()
    {
        Assert.Null(MoneyAccountFallback.TerminalBankWarning(true, BankAccountPickOutcome.Ambiguous));
        Assert.Null(MoneyAccountFallback.TerminalBankWarning(true, BankAccountPickOutcome.None));
        Assert.Null(MoneyAccountFallback.TerminalBankWarning(false, BankAccountPickOutcome.Single));
    }

    // ═══ E-4: ข้อความเมื่อไม่มีผังลูกหนี้บอกผังที่ต้องแก้ ═══

    [Fact]
    public void E4_ข้อความขาดผังลูกหนี้บอกรหัสและทางแก้บนผู้ติดต่อ()
    {
        Assert.Contains(TradeReceivableAccount.StandardCode, TradeReceivableAccount.MissingMessage);
        Assert.Contains("ผู้ติดต่อ", TradeReceivableAccount.MissingMessage);
    }

    // ═══ G-8: สิทธิ์ดูรายการ/สถานะ ═══

    [Fact]
    public void G8_ดูสถานะ_สิทธิ์ต้นทางหรือดูธนาคารอย่างใดอย่างหนึ่ง()
    {
        var pos = PaymentGatewayPermissionScope.StatusKeysFor(PaymentSourceKind.PosOrder);
        Assert.Contains(PermissionKeys.PosCashier, pos);
        Assert.Contains(PermissionKeys.BankView, pos);
        Assert.Equal(PermissionKeys.BankView, PaymentGatewayPermissionScope.ViewPayments);
        Assert.Contains("POS.Cashier", PaymentGatewayPermissionScope.StatusDeniedMessage(PaymentSourceKind.PosOrder));
    }
}
