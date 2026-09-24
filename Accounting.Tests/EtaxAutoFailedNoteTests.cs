using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้าน P-3/P-4 (รอบ 193) — ป้าย "ออก e-Tax อัตโนมัติไม่สำเร็จ" ต้อง (1) ไม่สะสมซ้ำเมื่อล้มหลายรอบ
/// (2) ถูกล้างเมื่อออก e-Tax สำเร็จ โดย**ไม่แตะ**หมายเหตุภายในส่วนอื่น (ช่องนี้เป็นที่สะสมของหลายด่าน)
/// (3) มีผู้อ่าน — <c>DocumentResponse.EtaxAutoFailed</c> คำนวณจาก <see cref="EtaxAutoFailedNote.Has"/>
/// </summary>
public class EtaxAutoFailedNoteTests
{
    private const string AckNote = "[APPROVE-ACK] รับทราบคำเตือน §86/4 แล้วอนุมัติ";

    [Fact]
    public void ต่อท้ายหมายเหตุเดิม_ไม่ทับ()
    {
        var s = EtaxAutoFailedNote.Append(AckNote, "ไม่มีเลขผู้เสียภาษีผู้ซื้อ");
        Assert.StartsWith(AckNote, s);
        Assert.True(EtaxAutoFailedNote.Has(s));
        Assert.Contains("ไม่มีเลขผู้เสียภาษีผู้ซื้อ", s);
    }

    [Fact]
    public void ล้มซ้ำ_ป้ายเดิมถูกแทน_ไม่สะสม()
    {
        var once = EtaxAutoFailedNote.Append(AckNote, "สาเหตุแรก");
        var twice = EtaxAutoFailedNote.Append(once, "สาเหตุที่สอง");
        Assert.Equal(1, CountMarkers(twice));
        Assert.Contains("สาเหตุที่สอง", twice);
        Assert.DoesNotContain("สาเหตุแรก", twice);
        Assert.StartsWith(AckNote, twice);
    }

    [Fact]
    public void ออกสำเร็จ_ล้างเฉพาะป้าย_หมายเหตุอื่นอยู่ครบ()
    {
        var withMarker = EtaxAutoFailedNote.Append(AckNote, "RD API ล่ม");
        var cleared = EtaxAutoFailedNote.Clear(withMarker);
        Assert.Equal(AckNote, cleared);
        Assert.False(EtaxAutoFailedNote.Has(cleared));
    }

    [Fact]
    public void มีแต่ป้าย_ล้างแล้วเป็นค่าว่าง()
        => Assert.Null(EtaxAutoFailedNote.Clear(EtaxAutoFailedNote.Append(null, "x")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(AckNote)]
    [InlineData("บรรทัดหนึ่ง\n\nบรรทัดสอง\r\nบรรทัดสาม")]
    public void ไม่มีป้าย_คืนค่าเดิมทุกตัวอักษร(string? notes)
    {
        Assert.Equal(notes, EtaxAutoFailedNote.Clear(notes));
        Assert.False(EtaxAutoFailedNote.Has(notes));
    }

    private static int CountMarkers(string s)
        => (s.Length - s.Replace(EtaxAutoFailedNote.Marker, "").Length) / EtaxAutoFailedNote.Marker.Length;
}
