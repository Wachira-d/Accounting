using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **One-way governor — ตัวเติมค่าต้องไม่ถอยหลัง** (D-7)
///
/// <para>กติกาสามข้อที่ <c>CLAUDE.md</c> กฎเหล็ก #4 A เรียกร้อง ("ห้ามใครมาก่อนชนะ"):</para>
/// <list type="number">
/// <item>ค่าที่<b>ผู้ใช้ยืนยันแล้ว</b> ห้ามถูกทับด้วยข้อเสนอของระบบ</item>
/// <item><b>specific ชนะ generic</b> (หลักฐานใกล้ของจริงชนะกติกา/ค่าตั้งต้น)</item>
/// <item><b>ผลลัพธ์ห้ามขึ้นกับลำดับ</b> ที่ผู้เสนอมาถึง</item>
/// </list>
///
/// <para><b>ตัวบังคับกติกาคือ <see cref="OcrFieldArbiter"/> ตัวเดียว</b> — ไม่มี governor
/// คลาสที่สอง เพราะ "ลำดับชั้นสองชุด" คือ defect class ที่รอบนี้กำลังปิด (F2 ข้อ 4)
/// สิ่งที่ขาดคือ<b>ชั้นของผู้ใช้</b> ซึ่งเพิ่มเป็น <see cref="OcrFieldSource.UserConfirmed"/></para>
///
/// <para>ฝั่งหน้าเว็บมีกติกาเดียวกันอยู่แล้วที่ <c>documents.html · _canAutoFill</c>
/// (<c>dataset.userTouched</c> ชนะเสมอ · <c>_srcRank {ai:1, template:2, contact:3}</c>
/// = AI ต่ำสุดเหมือนฝั่งเซิร์ฟเวอร์) — <b>ข้อต่างที่ตั้งใจ</b>: ฝั่ง JS ถือว่า
/// "ช่องที่มีค่าแต่ไม่มี <c>autoSrc</c>" เป็นของผู้ใช้ (ห้ามทับ) ส่วนฝั่งเซิร์ฟเวอร์
/// <c>OcrFieldSource.Unknown</c> อยู่<b>ล่างสุด</b> — ไม่ขัดกัน เพราะคนละวัตถุ:
/// JS พูดถึง "ค่าที่อยู่ในช่องแล้ว" · เซิร์ฟเวอร์พูดถึง "ข้อเสนอที่ไม่รู้ที่มา"</para>
/// </summary>
public class OcrOneWayGovernorTests
{
    private const string F = OcrFieldKeys.SuggestedWhtRate;

    // ── ข้อ 1: ค่าที่ผู้ใช้ยืนยันแล้ว ห้ามถูกทับ ────────────────────────────

    [Fact]
    public void ผู้ใช้ยืนยันแล้ว_ต้องชนะทุกผู้เสนออัตโนมัติ()
    {
        foreach (var rival in new[]
                 {
                     OcrFieldSource.EtaxXml, OcrFieldSource.AzureHighConfidence,
                     OcrFieldSource.PaperLabel, OcrFieldSource.Engine,
                     OcrFieldSource.Student, OcrFieldSource.Statute,
                     OcrFieldSource.VendorHistory, OcrFieldSource.Rule, OcrFieldSource.Ai,
                 })
        {
            var d = OcrFieldArbiter.Decide(F, new[]
            {
                new OcrFieldCandidate(F, "9.00", rival, 1.0m),
                new OcrFieldCandidate(F, "3.00", OcrFieldSource.UserConfirmed, 0.10m),
            })!;
            Assert.Equal("3.00", d.Value);
            Assert.Equal(OcrFieldSource.UserConfirmed, d.Source);
        }
    }

    [Fact]
    public void ชั้นผู้ใช้ต้องอยู่บนสุดของตาราง()
        => Assert.Equal(OcrFieldSource.UserConfirmed, OcrFieldArbiter.Precedence[0]);

    // ── ข้อ 2: specific ชนะ generic ─────────────────────────────────────────

    [Fact]
    public void ตารางกฎหมายชนะกติกาตั้งต้น_และแพ้สิ่งที่อ่านจากกระดาษ()
    {
        var d = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "1.00", OcrFieldSource.Rule, 0.99m),
            new OcrFieldCandidate(F, "3.00", OcrFieldSource.Statute, 0.50m),
        })!;
        Assert.Equal("3.00", d.Value);

        var d2 = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "3.00", OcrFieldSource.Statute, 0.99m),
            new OcrFieldCandidate(F, "5.00", OcrFieldSource.PaperLabel, 0.50m),
        })!;
        Assert.Equal("5.00", d2.Value);
    }

    [Fact]
    public void ความมั่นใจสูงของชั้นล่าง_ชนะชั้นบนไม่ได้()
    {
        var d = OcrFieldArbiter.Decide(F, new[]
        {
            new OcrFieldCandidate(F, "9.00", OcrFieldSource.Ai, 0.99m),
            new OcrFieldCandidate(F, "3.00", OcrFieldSource.Statute, 0.30m),
        })!;
        Assert.Equal("3.00", d.Value);
    }

    // ── ข้อ 3: ผลลัพธ์ห้ามขึ้นกับลำดับ ──────────────────────────────────────

    [Fact]
    public void สลับลำดับผู้เสนอแล้วต้องได้คำตอบเดิม()
    {
        var a = new OcrFieldCandidate(F, "3.00", OcrFieldSource.Statute, 0.7m);
        var b = new OcrFieldCandidate(F, "5.00", OcrFieldSource.VendorHistory, 0.95m);
        var c = new OcrFieldCandidate(F, "1.00", OcrFieldSource.Ai, 0.99m);

        var one = OcrFieldArbiter.Decide(F, new[] { a, b, c })!;
        var two = OcrFieldArbiter.Decide(F, new[] { c, b, a })!;
        var three = OcrFieldArbiter.Decide(F, new[] { b, c, a })!;

        Assert.Equal(one.Value, two.Value);
        Assert.Equal(one.Value, three.Value);
        Assert.Equal("3.00", one.Value);   // Statute ชนะ VendorHistory แม้มั่นใจน้อยกว่า
    }

    [Fact]
    public void ชั้นเดียวกัน_มั่นใจกว่าชนะ_และยังไม่ขึ้นกับลำดับ()
    {
        var lo = new OcrFieldCandidate(F, "1.00", OcrFieldSource.VendorHistory, 0.60m);
        var hi = new OcrFieldCandidate(F, "3.00", OcrFieldSource.VendorHistory, 0.90m);
        Assert.Equal("3.00", OcrFieldArbiter.Decide(F, new[] { lo, hi })!.Value);
        Assert.Equal("3.00", OcrFieldArbiter.Decide(F, new[] { hi, lo })!.Value);
    }

    // ── เทสต์ทิศตรงข้าม: การเพิ่มสองชั้นใหม่ต้องไม่สลับของเดิม ──────────────

    [Fact]
    public void เพิ่มชั้นผู้ใช้และชั้นกฎหมายแล้ว_ลำดับเดิมทุกคู่ต้องไม่สลับ()
    {
        var before = new[]
        {
            OcrFieldSource.EtaxXml, OcrFieldSource.AzureHighConfidence, OcrFieldSource.PaperLabel,
            OcrFieldSource.Engine, OcrFieldSource.LearnedPattern, OcrFieldSource.Student,
            OcrFieldSource.VendorHistory, OcrFieldSource.Rule, OcrFieldSource.Ai,
            OcrFieldSource.Guess, OcrFieldSource.Unknown,
        };
        for (var i = 1; i < before.Length; i++)
            Assert.True(OcrFieldArbiter.Rank(before[i - 1]) > OcrFieldArbiter.Rank(before[i]),
                $"{before[i - 1]} ต้องยังน่าเชื่อกว่า {before[i]}");
    }

    [Fact]
    public void ทุกค่าใน_enum_ต้องอยู่ในตารางลำดับ_ไม่งั้นตกไปอันดับต่ำสุดเงียบ_ๆ()
    {
        foreach (OcrFieldSource s in System.Enum.GetValues(typeof(OcrFieldSource)))
            Assert.Contains(s, OcrFieldArbiter.Precedence);
    }

    [Fact]
    public void ทุกค่าใน_enum_ต้องมีป้ายไทยของตัวเอง()
    {
        foreach (OcrFieldSource s in System.Enum.GetValues(typeof(OcrFieldSource)))
            Assert.False(string.IsNullOrWhiteSpace(OcrFieldArbiter.SourceLabel(s)));
    }
}
