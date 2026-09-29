namespace Accounting.Helpers;

/// <summary>
/// <b>แคชสั้นของสวิตช์แอดมิน "บังคับแพ็กเกจ"</b> — รอบ 200 ฝ่ายค้าน S200-8
///
/// <para>═══ ที่มา ═══ <c>SubscriptionCheckMiddleware</c> อ่าน <c>SiteSettings.SubscriptionEnforcementMode</c> ทุกคำขอที่รู้บริษัท (รวม partner
/// ที่ส่ง header ซึ่งเดิมไม่ต้องอ่าน) = query เพิ่ม 1 ครั้งต่อคำขอ · และถ้าคอลัมน์ยังไม่ถูกสร้าง <c>LogWarning</c> ทุกคำขอ (log spam)</para>
///
/// <para>═══ ทำไมปลอดภัยหลายเครื่อง ═══ ฐานข้อมูลยังเป็นความจริงตัวเดียว — แคชนี้อยู่ใน process (singleton ต่อเครื่อง) อายุ <see cref="Ttl"/> ·
/// หลังแอดมินกดเปลี่ยนสวิตช์ เครื่องที่รับคำขอ <c>PUT mode</c> ล้างแคชของตัวเองทันที (<see cref="Invalidate"/>) · เครื่องอื่นตามทันภายใน
/// <see cref="Ttl"/> (ไม่มี state ที่ต้องตรงกันข้ามเครื่อง ไม่มีการนับ — แค่ "อ่านซ้ำไม่เกินทุก 5 วินาที") · หน้าแอดมินอ่านสด (ไม่ผ่านแคช)
/// เพื่อให้กรอบ "โหมดที่มีผลจริง" ตรงฐานข้อมูลเสมอ</para>
///
/// <para>ค่าที่อ่านไม่ได้ (<see cref="SubscriptionAdminSwitchSource.Unreadable"/> = Shadow) ถูกแคชด้วย — กัน log spam ระหว่างที่ migration ยังไม่รัน
/// และ Shadow ไม่บล็อกใคร</para>
/// </summary>
public sealed class SubscriptionAdminSwitchCache
{
    /// <summary>อายุแคช — สั้นพอที่ "กดแล้วมีผล" ภายในไม่กี่วินาทีทุกเครื่อง</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private sealed record Entry(SubscriptionAdminSwitchRead Value, DateTime ReadAtUtc);

    private Entry? _entry;

    /// <summary>ค่าที่แคชไว้และยังไม่หมดอายุ ณ <paramref name="nowUtc"/> · <c>null</c> = ต้องอ่านฐานข้อมูล</summary>
    public SubscriptionAdminSwitchRead? TryGet(DateTime nowUtc)
    {
        var e = Volatile.Read(ref _entry);
        if (e is null) return null;
        var age = nowUtc - e.ReadAtUtc;
        return age >= TimeSpan.Zero && age < Ttl ? e.Value : null;
    }

    /// <summary>จำค่าที่เพิ่งอ่านจากฐานข้อมูล</summary>
    public void Set(SubscriptionAdminSwitchRead value, DateTime nowUtc) =>
        Volatile.Write(ref _entry, new Entry(value, nowUtc));

    /// <summary>ล้างแคช (หลังแอดมินเปลี่ยนสวิตช์บนเครื่องนี้)</summary>
    public void Invalidate() => Volatile.Write(ref _entry, null);
}
