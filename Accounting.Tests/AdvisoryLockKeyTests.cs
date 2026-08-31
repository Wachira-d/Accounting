using Accounting.Helpers;
using Accounting.Services.Implementations.Journal;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คีย์ของ <c>pg_advisory_xact_lock</c> ต้อง <b>เท่ากันเสมอ</b> ไม่ว่ารันที่ไหน
///
/// ═══ ที่มา ═══
/// เดิม 7 จุดคำนวณคีย์ด้วย <c>HashCode.Combine(...)</c> ซึ่ง .NET สุ่ม seed ใหม่
/// ทุก process ⇒ instance A กับ B ได้คีย์คนละค่าสำหรับทรัพยากรเดียวกัน ⇒
/// <b>ล็อกไม่กันกันเลย</b> ทั้งที่โค้ดอ่านแล้วเหมือนได้ป้องกันไว้แล้ว
/// (เลขเอกสาร §86/4 ซ้ำ · เลข JE ซ้ำ · สต็อกหาย · รายการธนาคารถูกจับคู่สองครั้ง)
///
/// เทสต์นี้ล็อก **ค่าคงที่จริง** ไม่ใช่แค่ "เรียกสองครั้งได้เท่ากัน" — เพราะ
/// `HashCode.Combine` ก็ผ่านข้อหลังภายใน process เดียวกัน (จึงจับบั๊กเดิมไม่ได้)
/// ค่าที่ hard-code ไว้คือสิ่งเดียวที่พิสูจน์ว่ามันข้าม process ได้จริง
/// </summary>
public class AdvisoryLockKeyTests
{
    private static readonly Guid Company1 =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void ค่าคงที่ต้องไม่เปลี่ยน_ข้าม_process_และข้ามรุ่น()
    {
        // ค่าเหล่านี้คำนวณจาก FNV-1a 64-bit ของ (companyId "N" | scope | part)
        // ถ้าข้อไหนแดง แปลว่าสูตรเปลี่ยน ⇒ instance เก่ากับใหม่จะล็อกคนละคีย์
        // ระหว่าง rolling deploy (= ช่วงที่ล็อกใช้ไม่ได้เลย)
        Assert.Equal(-8564446321292671581L,
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.JournalSequence, "JV-202608-"));
        Assert.Equal(6169770420548302441L,
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.DocumentSequence, "INV"));
        Assert.Equal(2161413978484553338L,
            AdvisoryLockKey.For(AdvisoryLockKey.StockAdjust,
                "0000000000000000000000000000000a"));
    }

    [Fact]
    public void ตัวออกเลข_JE_ทุกตัวต้องได้คีย์เดียวกัน()
    {
        // JournalEntryBuilder เป็นตัวกลาง — ถ้าคีย์ของมันหลุดจาก AdvisoryLockKey
        // เมื่อไร ผู้ออกเลขสองตัวจะกลับไปไม่บล็อกกันเหมือนบั๊กเดิม
        Assert.Equal(
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.JournalSequence, "JV-202608-"),
            JournalEntryBuilder.JournalNumberLockKey(Company1, "JV-202608-"));
    }

    [Fact]
    public void คนละ_scope_ต้องได้คนละคีย์()
    {
        // ทรัพยากรคนละชนิดต้องไม่บล็อกกันโดยบังเอิญ
        Assert.NotEqual(
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.JournalSequence, "X"),
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.DocumentSequence, "X"));
    }

    [Fact]
    public void คนละบริษัทหรือคนละ_part_ต้องได้คนละคีย์()
    {
        var other = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Assert.NotEqual(
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.DocumentSequence, "INV"),
            AdvisoryLockKey.For(other, AdvisoryLockKey.DocumentSequence, "INV"));
        Assert.NotEqual(
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.DocumentSequence, "INV"),
            AdvisoryLockKey.For(Company1, AdvisoryLockKey.DocumentSequence, "QT"));
    }

    [Fact]
    public void ทรัพยากรเดียวกันต้องได้คีย์เดียวกันทุกครั้ง()
    {
        for (var i = 0; i < 100; i++)
            Assert.Equal(
                AdvisoryLockKey.For(Company1, AdvisoryLockKey.BankReconcile, "abc"),
                AdvisoryLockKey.For(Company1, AdvisoryLockKey.BankReconcile, "abc"));
    }
}
