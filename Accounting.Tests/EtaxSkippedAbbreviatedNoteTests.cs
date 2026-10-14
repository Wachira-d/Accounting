using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม E 2026-10-13 — ป้าย <c>[ETAX-SKIPPED]</c> ของใบที่ตรึง "อย่างย่อ" อ้างรหัส ETDA ให้ถูก (T05/T06 — T04 คือใบส่งของ/ใบกำกับเต็มรูป · กระดาน B4)
/// และใบที่ประทับข้อความเดิม "(ยังไม่รองรับ T04)" ไว้แล้ว ต้องไม่ได้ป้ายที่สองเมื่อ hook ถูกเรียกซ้ำ (ตัวกันซ้ำเดิมเทียบข้อความเต็ม)
/// </summary>
public class EtaxSkippedAbbreviatedNoteTests
{
    [Fact]
    public void ข้อความใหม่อ้าง_T05_T06_ไม่อ้าง_T04()
    {
        Assert.Contains("ระบบยังไม่รองรับ e-Tax ใบกำกับภาษีอย่างย่อ T05/T06", IssuedDocumentHooks.EtaxSkippedAbbreviatedNote);
        Assert.DoesNotContain("T04", IssuedDocumentHooks.EtaxSkippedAbbreviatedNote);
        Assert.StartsWith("[ETAX-SKIPPED]", IssuedDocumentHooks.EtaxSkippedAbbreviatedNote);
    }

    [Fact]
    public void ใบที่ประทับข้อความเดิมไว้แล้ว_ไม่ได้ป้ายซ้ำ()
    {
        var stampedBefore = "หมายเหตุภายในของผู้ใช้\n\n" + IssuedDocumentHooks.LegacyEtaxSkippedAbbreviatedNote;
        Assert.True(IssuedDocumentHooks.NoteAlreadyStamped(stampedBefore, IssuedDocumentHooks.EtaxSkippedAbbreviatedNote));
        // ข้อความเดิมคือรูปที่เคยประทับจริง (ล็อกตัวอักษร — ถ้าใครแก้ค่าคงที่เดิม ตัวกันซ้ำจะไม่จำใบเก่า)
        Assert.Contains("(ยังไม่รองรับ T04)", IssuedDocumentHooks.LegacyEtaxSkippedAbbreviatedNote);
        // ข้อความใหม่ที่ประทับแล้ว ก็ไม่ซ้ำ
        Assert.True(IssuedDocumentHooks.NoteAlreadyStamped(
            IssuedDocumentHooks.EtaxSkippedAbbreviatedNote, IssuedDocumentHooks.EtaxSkippedAbbreviatedNote));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบที่ยังไม่มีป้าย_หรือมีแค่ป้ายอื่น_ต้องได้ป้าย()
    {
        Assert.False(IssuedDocumentHooks.NoteAlreadyStamped(null, IssuedDocumentHooks.EtaxSkippedAbbreviatedNote));
        Assert.False(IssuedDocumentHooks.NoteAlreadyStamped("", IssuedDocumentHooks.EtaxSkippedAbbreviatedNote));
        Assert.False(IssuedDocumentHooks.NoteAlreadyStamped("[ETAX-AUTO-FAILED] ส่งไม่สำเร็จ",
            IssuedDocumentHooks.EtaxSkippedAbbreviatedNote));
        // ข้อความเดิมจำได้เฉพาะป้ายของมันเอง — ไม่ใช่ใบอนุญาตข้ามป้ายอื่น
        Assert.False(IssuedDocumentHooks.NoteAlreadyStamped(IssuedDocumentHooks.LegacyEtaxSkippedAbbreviatedNote, "[OTHER] ป้ายอื่น"));
    }
}
