using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ไซต์นี้เปิดจัดสรรผู้ให้บริการไหม" และ "บริการนี้ใช้ผู้ให้บริการจริงไหม" (รอบ 207 · คำขอเจ้าของข้อ 2/3/8)
/// <para><b>ที่มา</b>: เดิมโหมดผู้ให้บริการตั้งรายบริการ ส่วนรายชื่อผู้ให้บริการซ่อนอยู่ท้ายแท็บ "บริการที่เปิดให้จอง" ⇒ เจ้าของหาไม่เจอ และไม่มีจุดตัดสินว่า
/// "ร้านนี้ใช้ระบบผู้ให้บริการไหม" (หน้าเริ่มให้บริการ/รายงานต้องรู้) · ค่าตั้งใหม่ <c>SiteBookingSettings.ProviderAllocationEnabled</c></para>
/// <para><b>ไม่ทำของเดิมเสีย</b>: แถวก่อนรอบ 207 (null) ⇒ เปิดเมื่อมีผู้ให้บริการที่ใช้งาน หรือมีบริการที่ตั้งโหมดผู้ให้บริการแล้ว = พฤติกรรมเดิมทุกตัว ·
/// เจ้าของปิดเอง (false) ⇒ ทุกบริการนับความจุของบริการ (โหมด None) โดยไม่ลบค่าตั้งรายบริการ — เปิดกลับมาได้ค่าเดิม</para>
/// </summary>
public static class BookingProviderAllocation
{
    public const string RuleCode = "CMS-BOOKING-PROVIDER";

    /// <summary>ไซต์เปิดจัดสรรผู้ให้บริการไหม · <paramref name="setting"/> null = ตัดสินจากข้อมูลเดิม</summary>
    public static bool Enabled(bool? setting, bool hasActiveResources, bool anyServiceUsesResources, BookingResourceMode? siteDefault = null)
        => setting ?? (hasActiveResources || anyServiceUsesResources || (siteDefault is BookingResourceMode d && d != BookingResourceMode.None));
    // ฝ่ายค้านรอบ 215 เฟส 2 S1: ค่าเว็บโหมดผู้ให้บริการ (≠ None) = เจตนาใช้ผู้ให้บริการ ⇒ เว็บใหม่ (CustomerChoice/Auto) ไม่ถูกอนุมานเป็น "ปิด" ตั้งแต่ยังไม่มีผู้ให้บริการ
    // (เดิม ⇒ หน้า cms-edit ส่ง false กลับไปบันทึก ⇒ ค่าเว็บไม่มีผลตลอดไป) · ยังไม่มีผู้ให้บริการ ⇒ engine นับความจุ (useRes ต้องมีผู้ให้บริการ) = ไม่มีใครถูกบล็อก

    /// <summary>ตัวอย่างบนหน้าตั้งค่าเมื่อเปิดจัดสรรแต่ยังไม่มีผู้ให้บริการที่ใช้งาน (ฝ่ายค้าน S1) — ลูกค้ายังไม่เห็นช่องเลือกจนกว่าจะเพิ่มคน</summary>
    public const string NoProvidersYetNote = "ยังไม่มีผู้ให้บริการ — เพิ่มที่ หน้าเว็บ › บริการจอง › ผู้ให้บริการ (จนกว่าจะเพิ่ม ลูกค้าจองโดยนับความจุของบริการ)";

    /// <summary>โหมดผู้ให้บริการที่ engine ใช้จริง — <b>ตัวตัดสินเดียว</b> (รอบ 215 เฟส 2 · คำตัดสินข้อ 176):
    /// ไซต์ปิดจัดสรร ⇒ None (ค่ารายบริการยังเก็บไว้) · บริการ <c>null</c> = "ตามค่าเว็บ" ⇒ <paramref name="siteDefault"/>
    /// (<c>SiteBookingSettings.DefaultResourceMode</c> — ไม่ตั้ง = None = พฤติกรรมเดิม) · บริการตั้งเอง ⇒ ค่าของบริการ
    /// (บริการเดิมทุกแถวมีค่าตั้งเอง — migration ไม่แปลงเป็น null ⇒ ไม่มีบริการเดิมใดเปลี่ยนพฤติกรรม)</summary>
    public static BookingResourceMode EffectiveMode(BookingResourceMode? serviceMode, bool siteEnabled, BookingResourceMode siteDefault = BookingResourceMode.None)
        => siteEnabled ? (serviceMode ?? siteDefault) : BookingResourceMode.None;

    /// <summary>ที่มาของโหมดบริการ: "site" = ตามค่าเว็บ (บริการไม่ตั้ง) · "service" = กำหนดเอง — ป้ายบนรายการบริการ/ฟอร์ม</summary>
    public static string ModeSource(BookingResourceMode? serviceMode) => serviceMode == null ? "site" : "service";

    /// <summary>ค่าเริ่มต้นของ<b>เว็บใหม่</b> (คำตัดสินข้อ 176 ข): ลูกค้าเลือกเอง (ไม่เลือก = ระบบจัดให้) · คลินิก/บริการวิชาชีพ/ที่ปรึกษา ⇒ ระบบจัดให้ ·
    /// เว็บเดิมไม่ถูกตั้งย้อนหลัง (ไม่มีแถว/ค่า null = None = พฤติกรรมเดิม)</summary>
    public static BookingResourceMode NewSiteDefaultMode(IndustryType industry)
        => industry is IndustryType.Healthcare or IndustryType.Service or IndustryType.Freelance
            ? BookingResourceMode.Auto : BookingResourceMode.CustomerChoice;

    /// <summary>ลูกค้าเห็นอะไรบนหน้าจอง ตามโหมดที่มีผลจริง (ตัวอย่างบนหน้าตั้งค่า — หน้าแค่แสดง)</summary>
    public static (bool ShowPicker, bool Required, string Text) CustomerPreview(BookingResourceMode effective) => effective switch
    {
        BookingResourceMode.Required => (true, true, "ลูกค้าต้องเลือกผู้ให้บริการก่อนยืนยันการจอง"),
        BookingResourceMode.CustomerChoice => (true, false, "ลูกค้าเลือกผู้ให้บริการได้ — ไม่เลือก ระบบจัดให้"),
        BookingResourceMode.Auto => (false, false, "ลูกค้าไม่เห็นช่องเลือกผู้ให้บริการ — ระบบจัดให้อัตโนมัติ"),
        _ => (false, false, "ไม่มีเรื่องผู้ให้บริการบนหน้าจอง (นับความจุของบริการอย่างเดียว)"),
    };

    /// <summary>ตอน "เริ่มให้บริการ" ต้องเลือกผู้ให้บริการไหม — ไซต์เปิด + มีผู้ให้บริการที่รับบริการนี้ได้อย่างน้อย 1 คน
    /// (ไม่มีใครรับบริการนี้ = เริ่มได้โดยไม่ระบุ + คำเตือนให้ไปผูกบริการ)</summary>
    public static bool RequiresProviderAtStart(bool siteEnabled, int capableProviders) => siteEnabled && capableProviders > 0;

    public const string NoCapableProviderNote =
        "ยังไม่มีผู้ให้บริการที่ผูกกับบริการนี้ — เริ่มให้บริการได้โดยไม่ระบุผู้ให้บริการ · ไปที่ เว็บไซต์ → การจอง → ผู้ให้บริการ แล้วติ๊กบริการที่แต่ละคนรับได้";

    public const string ProviderRequiredMessage =
        "ไซต์นี้เปิดจัดสรรผู้ให้บริการ — เลือกผู้ให้บริการที่ว่างอย่างน้อย 1 คนก่อนเริ่มให้บริการ";

    public sealed record StrategyOption(string Value, string Label, string Description);

    /// <summary>ตัวเลือกตรรกะแนะนำ (หน้าเว็บแสดงตรง — ไม่มีสำเนาป้ายใน JS)</summary>
    public static IReadOnlyList<StrategyOption> StrategyOptions() => new[]
    {
        new StrategyOption(nameof(BookingRecommendStrategy.LeastLoadedDay), "งานน้อยสุดของวันนั้น (ค่าเริ่มต้น)",
            "คนที่มีงานน้อยที่สุดในวันนั้น (นาที) ได้ก่อน — เท่ากันใช้ลำดับที่ตั้ง"),
        new StrategyOption(nameof(BookingRecommendStrategy.FairShareMonth), "กระจายงานเท่าเทียมทั้งเดือน",
            "นับนาทีงานสะสมของเดือนนี้ — คนที่ได้งานน้อยสุดในเดือนได้ก่อน (เกลี่ยรายได้/ค่าคอมให้เท่ากัน)"),
        new StrategyOption(nameof(BookingRecommendStrategy.RoundRobin), "หมุนเวียนตามคิว",
            "คนที่ได้รับงานล่าสุดนานที่สุดได้ก่อน — วนคิวทีละคน"),
        new StrategyOption(nameof(BookingRecommendStrategy.PrimaryFirst), "คนหลักของบริการก่อน",
            "ถ้าคนที่ติ๊กเป็น \"หลัก\" ของบริการนั้นว่าง ได้ก่อนเสมอ — ไม่ว่างค่อยไล่ตามลำดับความสำคัญ แล้วงานน้อยสุด"),
        new StrategyOption(nameof(BookingRecommendStrategy.PriorityOrder), "ตามลำดับความสำคัญ",
            "ไล่ตามเลขลำดับความสำคัญที่ตั้งต่อบริการ (เลขน้อยได้ก่อน) — เท่ากันใช้งานน้อยสุดของวัน"),
    };

    /// <summary>คำอธิบายเหตุผลที่แนะนำ (ป้ายข้างชื่อในหน้าเลือกผู้ให้บริการ) — คำนวณที่เดียว</summary>
    public static string ReasonLabel(BookingRecommendStrategy s, bool isPrimary, int priority, int dayLoadMinutes, int periodLoadMinutes, DateTime? lastAssignedUtc)
        => s switch
        {
            BookingRecommendStrategy.FairShareMonth => $"งานเดือนนี้ {FormatMinutes(periodLoadMinutes)}",
            BookingRecommendStrategy.RoundRobin => lastAssignedUtc is DateTime t
                ? $"ได้งานล่าสุด {t.AddHours(7):dd/MM HH:mm}" : "ยังไม่เคยได้งาน",
            BookingRecommendStrategy.PrimaryFirst => isPrimary ? "คนหลักของบริการนี้" : $"ลำดับ {priority} · งานวันนี้ {FormatMinutes(dayLoadMinutes)}",
            BookingRecommendStrategy.PriorityOrder => $"ลำดับความสำคัญ {priority}",
            _ => $"งานวันนี้ {FormatMinutes(dayLoadMinutes)}",
        };

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0 นาที";
        var h = minutes / 60; var m = minutes % 60;
        return h == 0 ? $"{m} นาที" : m == 0 ? $"{h} ชม." : $"{h} ชม. {m} นาที";
    }
}
