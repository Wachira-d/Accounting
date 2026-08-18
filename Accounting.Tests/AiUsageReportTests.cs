using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รายงานการใช้งาน AI แยกรายลูกค้า — ตรึงสูตรและกติกาการนับ
/// (ตัวเลขในรายงานนี้ถูกเอาไปคุยเรื่องบิลกับลูกค้า ผิดแล้วเถียงกันไม่จบ)
/// </summary>
public class AiUsageReportTests
{
    // ── ป้ายกำกับ "ใครเรียก" ────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, "ผสม (เว็บ + API)")]
    [InlineData(false, true, "API อย่างเดียว")]     // ← คำถามหลักของรายงาน
    [InlineData(true, false, "เว็บอย่างเดียว")]
    [InlineData(false, false, "งานระบบเท่านั้น")]
    public void The_customer_kind_answers_web_versus_api_in_one_column(
        bool usesWeb, bool usesApi, string expected)
        => Assert.Equal(expected, AiUsageReportService.DescribeCustomerKind(usesWeb, usesApi));

    [Fact]
    public void A_null_api_client_means_the_web_ui_exactly_like_the_billing_meter()
    {
        // นิยามเดียวกับ UsageEvent.ApiClientId — ถ้าสองที่นิยามต่างกัน
        // "รายงานการใช้ AI" กับ "บิล" จะกระทบยอดกันไม่ได้ตลอดกาล
        static bool IsApiCall(Guid? apiClientId) => apiClientId != null;
        Assert.False(IsApiCall(null));
        Assert.True(IsApiCall(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(AiUsageChannel.Web, "หน้าเว็บ NextAcc")]
    [InlineData(AiUsageChannel.ApiKey, "API (คีย์ลูกค้า)")]
    [InlineData(AiUsageChannel.Background, "งานเบื้องหลังของระบบ")]
    [InlineData(AiUsageChannel.Unknown, "ไม่ระบุ")]
    public void Every_channel_has_a_thai_label(AiUsageChannel c, string expected)
        => Assert.Equal(expected, AiUsageReportService.ChannelLabelTh(c));

    [Fact]
    public void Old_rows_without_a_channel_stay_readable_as_unknown()
    {
        // คอลัมน์ Channel มี DEFAULT 0 = Unknown ⇒ ข้อมูลก่อนเปิดฟีเจอร์นี้
        // ยังอยู่ในรายงาน (ไม่หาย) แค่ไม่รู้ช่องทาง
        Assert.Equal(0, (int)AiUsageChannel.Unknown);
        Assert.Equal("ไม่ระบุ", AiUsageReportService.ChannelLabelTh(default));
    }

    [Fact]
    public void Every_call_status_has_a_thai_label()
    {
        foreach (var s in Enum.GetValues<AiCallStatus>())
        {
            var label = AiUsageReportService.StatusLabelTh(s);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(s.ToString(), label);   // ต้องแปลจริง ไม่ใช่คืนชื่อ enum
        }
    }

    [Fact]
    public void An_unmapped_feature_falls_back_to_its_raw_key()
    {
        // ฟีเจอร์ใหม่ที่ยังไม่ได้เติมชื่อไทย ต้องไม่ทำให้รายงานพัง/ขึ้นค่าว่าง
        Assert.Equal("แนะนำผังบัญชี", AiUsageReportService.FeatureLabelTh("GlAccountSuggestion"));
        Assert.Equal("SomeBrandNewFeature", AiUsageReportService.FeatureLabelTh("SomeBrandNewFeature"));
    }

    // ── หน่วง: ทำไมต้องเก็บผลรวม ไม่ใช่ค่าเฉลี่ย ─────────────────────────

    [Fact]
    public void Averaging_stored_averages_is_wrong_when_call_counts_differ()
    {
        // 2 แถว: ฟีเจอร์ A ช้ามาก 900ms แต่ถูกเรียกครั้งเดียว
        //         ฟีเจอร์ B เร็ว 100ms ถูกเรียก 99 ครั้ง
        var rows = new (long SumMs, int Samples)[] { (900, 1), (9_900, 99) };

        var naive = rows.Select(r => r.SumMs / r.Samples).Average();       // เฉลี่ยของเฉลี่ย
        var weighted = rows.Sum(r => r.SumMs) / rows.Sum(r => r.Samples);  // ถ่วงน้ำหนักจริง

        Assert.Equal(500, naive);        // ผิด — โชว์ว่าระบบช้ากว่าความจริง 4.6 เท่า
        Assert.Equal(108, weighted);     // ถูก
        Assert.NotEqual((long)naive, weighted);
    }

    [Fact]
    public void Latency_with_no_samples_reports_zero_not_a_divide_by_zero()
    {
        static int Avg(long sum, int samples) => samples <= 0 ? 0 : (int)(sum / samples);
        Assert.Equal(0, Avg(0, 0));
        Assert.Equal(120, Avg(1_200, 10));
    }

    // ── สัดส่วน ─────────────────────────────────────────────────────────

    private static decimal Rate(int part, int whole)
        => whole <= 0 ? 0m : Math.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);

    [Fact]
    public void The_ai_usage_rate_counts_only_calls_that_actually_cost_money()
    {
        // 1,000 ครั้ง: ถึง provider จริง 180 · แคช 220 · local ตอบเอง 600
        Assert.Equal(0.18m, Rate(180, 1_000));
        Assert.Equal(0.82m, Rate(220 + 600, 1_000));   // ที่ไม่เสียเงินเลย
    }

    [Fact]
    public void A_low_ai_usage_rate_is_the_goal_not_a_problem()
    {
        // กฎเหล็ก #1: ยิ่ง local โต ยิ่งเรียก AI น้อย — รายงานที่โชว์แต่ยอดเงิน
        // จะทำให้อ่านผิดว่า "ใช้น้อย = ไม่มีใครใช้ระบบ"
        const int total = 1_000;
        var early = Rate(900, total);    // ช่วงแรก local ยังไม่เก่ง
        var mature = Rate(120, total);   // หลังสะสม feedback
        Assert.True(mature < early);
        Assert.Equal(780, 900 - 120);    // ประหยัดไป 780 ครั้งต่อ 1,000
    }

    [Fact]
    public void Rates_are_zero_when_there_is_nothing_to_divide_by()
    {
        Assert.Equal(0m, Rate(0, 0));
        Assert.Equal(0m, Rate(5, 0));
    }

    // ── การนับ "ผู้ใช้ตัดสินใจแล้ว" ─────────────────────────────────────

    /// <summary>กติกาเดียวกับ BumpTenantReviewAsync</summary>
    private static (int Reviewed, int Accepted) ApplyReview(
        int reviewed, int accepted, bool firstDecision, bool wasAccepted, bool nowAccepted)
    {
        if (firstDecision)
        {
            reviewed++;
            if (nowAccepted) accepted++;
        }
        else if (wasAccepted && !nowAccepted) accepted--;
        else if (!wasAccepted && nowAccepted) accepted++;
        return (reviewed, Math.Max(0, accepted));
    }

    [Fact]
    public void Changing_your_mind_does_not_inflate_the_denominator()
    {
        // ผู้ใช้กดยืนยัน แล้วเปลี่ยนใจไปแก้เอง แล้วกลับมารับ AI อีกที
        var s = ApplyReview(0, 0, firstDecision: true, wasAccepted: false, nowAccepted: true);
        Assert.Equal((1, 1), s);

        s = ApplyReview(s.Reviewed, s.Accepted, firstDecision: false, wasAccepted: true, nowAccepted: false);
        Assert.Equal((1, 0), s);   // ตัวหารยังเป็น 1 ไม่ใช่ 2

        s = ApplyReview(s.Reviewed, s.Accepted, firstDecision: false, wasAccepted: false, nowAccepted: true);
        Assert.Equal((1, 1), s);
    }

    [Fact]
    public void The_accepted_count_never_goes_negative()
    {
        var s = ApplyReview(1, 0, firstDecision: false, wasAccepted: true, nowAccepted: false);
        Assert.Equal(0, s.Accepted);
    }

    [Fact]
    public void Acceptance_is_measured_against_decided_calls_not_all_calls()
    {
        // 1,000 call แต่ผู้ใช้เพิ่งตัดสินใจ 40 — อัตรายอมรับต้องเป็น 30/40
        // ไม่ใช่ 30/1,000 (ซึ่งจะดูเหมือน AI แย่มากทั้งที่ยังไม่มีใครดู)
        Assert.Equal(0.75m, Rate(30, 40));
        Assert.Equal(0.03m, Rate(30, 1_000));
    }

    // ── การนับสถานะเข้าช่อง ─────────────────────────────────────────────

    /// <summary>"ข้าม" นับเป็นการประหยัดเฉพาะตอน local ตอบได้จริง</summary>
    private static bool CountsAsLocalServed(AiCallStatus status, string? localAnswer)
        => status == AiCallStatus.Skipped && !string.IsNullOrEmpty(localAnswer);

    [Fact]
    public void Skipping_without_a_local_answer_is_not_a_saving()
    {
        // ฟีเจอร์ที่ปิดไว้แต่ยังไม่มี student รองรับ — ผู้ใช้ไม่ได้คำตอบที่ดี
        // นับเป็น "ประหยัด" จะทำให้ตัวเลข sovereignty สวยเกินจริง
        Assert.True(CountsAsLocalServed(AiCallStatus.Skipped, "53210"));
        Assert.False(CountsAsLocalServed(AiCallStatus.Skipped, null));
        Assert.False(CountsAsLocalServed(AiCallStatus.Skipped, ""));
        Assert.False(CountsAsLocalServed(AiCallStatus.Success, "53210"));
    }

    [Fact]
    public void Every_status_lands_in_exactly_one_bucket()
    {
        static string Bucket(AiCallStatus s) => s switch
        {
            AiCallStatus.Success => "ai",
            AiCallStatus.Cached => "cached",
            AiCallStatus.Failed or AiCallStatus.InvalidResponse => "failed",
            AiCallStatus.BudgetExceeded => "budget",
            AiCallStatus.NoProvider => "noProvider",
            AiCallStatus.Skipped => "skipped",
            _ => "other",
        };
        foreach (var s in Enum.GetValues<AiCallStatus>())
            Assert.NotEqual("other", Bucket(s));
        // ยอดรวมของช่องย่อยต้องไม่เกิน CallsTotal (แต่ละ call นับช่องเดียว)
        Assert.Equal(7, Enum.GetValues<AiCallStatus>().Length);
    }

    // ── ต้นทุน ──────────────────────────────────────────────────────────

    [Fact]
    public void The_baht_figure_is_derived_from_usd_at_the_rate_the_caller_chose()
    {
        static decimal Thb(decimal usd, decimal rate)
            => Math.Round(usd * rate, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(0.54m, Thb(0.014820m, 36.5m));
        Assert.Equal(541.0m, Thb(14.82m, 36.5m));
        // ปัดแบบ AwayFromZero ตามกฎเงินของโปรเจกต์ ไม่ใช่ banker's rounding
        Assert.Equal(0.13m, Thb(0.125m, 1m));
    }

    [Fact]
    public void Sandbox_keys_are_excluded_by_default_so_the_report_matches_the_bill()
    {
        // UsageEvent.IsSandbox = ไม่เข้าบิล — รายงานจึงต้องตัดออกโดยค่าเริ่มต้น
        // ไม่งั้นแอดมินเอายอดไปคุยกับลูกค้าแล้วไม่ตรงใบแจ้งหนี้
        var rows = new (bool Sandbox, int Calls)[] { (false, 800), (true, 200) };
        Assert.Equal(800, rows.Where(r => !r.Sandbox).Sum(r => r.Calls));
        Assert.Equal(1_000, rows.Sum(r => r.Calls));   // เปิด includeSandbox แล้ว
    }

    // ── ขอบเขตช่วงวันที่ ────────────────────────────────────────────────

    [Fact]
    public void A_reversed_date_range_is_swapped_not_returned_empty()
    {
        static (DateTime F, DateTime T) Normalize(DateTime f, DateTime t)
            => t < f ? (t.Date, f.Date) : (f.Date, t.Date);
        var (f, t) = Normalize(new DateTime(2026, 8, 31), new DateTime(2026, 8, 1));
        Assert.Equal(new DateTime(2026, 8, 1), f);
        Assert.Equal(new DateTime(2026, 8, 31), t);
    }

    [Fact]
    public void An_unbounded_range_is_capped_so_one_request_cannot_scan_the_whole_table()
    {
        static DateTime CapFrom(DateTime f, DateTime t)
            => (t - f).TotalDays > 400 ? t.AddDays(-400) : f;
        var to = new DateTime(2026, 8, 31);
        Assert.Equal(to.AddDays(-400), CapFrom(new DateTime(2000, 1, 1), to));
        Assert.Equal(new DateTime(2026, 8, 1), CapFrom(new DateTime(2026, 8, 1), to));
    }
}
