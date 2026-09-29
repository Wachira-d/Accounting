using Accounting.Models.Enums;
using Microsoft.Extensions.Configuration;

namespace Accounting.Helpers;

/// <summary>ค่าสวิตช์แอดมินอ่านได้จากไหน</summary>
public enum SubscriptionAdminSwitchSource
{
    /// <summary>อ่านจากแถว <c>SiteSettings</c> ได้จริง</summary>
    Stored = 0,
    /// <summary>ยังไม่มีแถวค่าตั้งแพลตฟอร์ม — ใช้ค่าตั้งต้น (Shadow) จนกว่าแอดมินจะกดบันทึกครั้งแรก</summary>
    NoRow = 1,
    /// <summary>อ่านไม่ได้ (คอลัมน์ยังไม่ถูกสร้าง / DB สะดุด) — ใช้ Shadow เพื่อไม่บล็อกใครเพราะอ่านค่าไม่ได้</summary>
    Unreadable = 2,
}

/// <summary>ผลการอ่านสวิตช์แอดมินจากฐานข้อมูล (ผู้อ่าน: <c>ISubscriptionGateShadowLog.ReadAdminSwitchAsync</c>)</summary>
/// <param name="Mode">ค่าที่อ่านได้ · ไม่มีแถว/อ่านไม่ได้ = <see cref="SubscriptionEnforcementMode.Shadow"/></param>
public readonly record struct SubscriptionAdminSwitchRead(SubscriptionEnforcementMode Mode, SubscriptionAdminSwitchSource Source);

/// <summary>โหมดที่มีผลจริงมาจากอะไร</summary>
public enum SubscriptionEnforcementSource
{
    /// <summary>สวิตช์แอดมิน (ฐานข้อมูล) — ตัวตัดสินหลัก</summary>
    AdminSwitch = 0,
    /// <summary>ยังไม่มีแถวค่าตั้ง — ค่าตั้งต้นของสวิตช์แอดมิน (Shadow)</summary>
    AdminSwitchDefault = 1,
    /// <summary>อ่านสวิตช์แอดมินไม่ได้ — Shadow</summary>
    AdminSwitchUnreadable = 2,
    /// <summary>config <c>Subscription:Enforcement:EmergencyOverride</c> ทับสวิตช์แอดมิน (override ฉุกเฉิน)</summary>
    EmergencyOverride = 3,
}

/// <summary>โหมดบังคับแพ็กเกจที่มีผลจริง + เหตุผล — สิ่งที่ middleware ใช้ตัดสินและหน้าแอดมินแสดง <b>ชุดเดียวกัน</b></summary>
/// <param name="EffectiveMode">โหมดที่มีผลจริงกับคำขอจากหน้าเว็บ (รู้บริษัทจาก route อย่างเดียว)</param>
/// <param name="HeaderWriteGateMode">ด่านเขียน (บริษัทถูกระงับ/หมดอายุ) ของคำขอที่ส่ง <c>X-Company-Id</c> มาเอง —
/// Enforce เมื่อโหมดที่มีผลจริง = Enforce · ไม่งั้น LogOnly (log อย่างเดียว เหมือนค่าเดิมของ config)</param>
/// <param name="AdminSwitch">ค่าที่เก็บในฐานข้อมูล (สิ่งที่แอดมินกดไว้)</param>
/// <param name="AdminSwitchSource">อ่านสวิตช์แอดมินได้จากไหน</param>
/// <param name="OverrideMode">ค่า override ฉุกเฉินที่ใช้จริง · <c>null</c> = ไม่มี override</param>
/// <param name="OverrideRaw">ค่าดิบของ override ใน config (ตัดช่องว่างแล้ว) · <c>null</c> = ไม่ได้ตั้ง</param>
/// <param name="OverrideRecognized">ค่า override เป็น Off/Shadow/Enforce ไหม (ค่าเพี้ยน = ถือเป็น Shadow)</param>
/// <param name="AdminSwitchHasEffect">กดสวิตช์แอดมินแล้วมีผลทันทีไหม (false = มี override ทับอยู่ — บันทึกได้แต่ยังไม่มีผล)</param>
/// <param name="Explanation">"โหมดที่มีผลจริง + เพราะอะไร" เป็นภาษาไทย (หน้าแอดมินแสดงตรง ๆ)</param>
/// <param name="Warnings">สิ่งที่แอดมินต้องรู้ (ค่า config เก่าที่เลิกใช้ · ค่า override เพี้ยน)</param>
/// <param name="LegacyHeaderEnforce">รอบ 200 ฝ่ายค้าน S200-1: config เดิม <c>Subscription:Enforcement:Mode = Enforce</c> ยังตั้งอยู่ ⇒ ด่านเขียน
/// (ระงับ/หมดอายุ) ของคำขอที่ส่ง <c>X-Company-Id</c> ยัง<b>บังคับต่อแบบเดิม</b>จนกว่าจะลบคีย์ (ไม่หลวมลงเงียบ ๆ) · หน้าแอดมินแสดงเป็นกรอบเตือน</param>
public sealed record SubscriptionEnforcementState(
    SubscriptionEnforcementMode EffectiveMode,
    SubscriptionWriteGateMode HeaderWriteGateMode,
    SubscriptionEnforcementSource Source,
    SubscriptionEnforcementMode AdminSwitch,
    SubscriptionAdminSwitchSource AdminSwitchSource,
    SubscriptionEnforcementMode? OverrideMode,
    string? OverrideRaw,
    bool OverrideRecognized,
    bool AdminSwitchHasEffect,
    string Explanation,
    IReadOnlyList<string> Warnings,
    bool LegacyHeaderEnforce = false);

/// <summary>
/// <b>ตัวตัดสิน "โหมดบังคับแพ็กเกจที่มีผลจริง" ตัวเดียว</b> — รอบ 200 คำตัดสินข้อ 14
///
/// <para>═══ ที่มา ═══ ก่อนรอบนี้มีสองสวิตช์ที่ขัดกัน: <c>SiteSettings.SubscriptionEnforcementMode</c> (สวิตช์แอดมิน Off/Shadow/Enforce —
/// คุมการตัดสินฟีเจอร์/สถานะของคำขอจากหน้าเว็บ) กับ config <c>Subscription:Enforcement:Mode</c> (LogOnly ใน appsettings — คุมด่านเขียน
/// "บริษัทถูกระงับ/หมดอายุ" ของ<b>ทุก</b>คำขอ) ⇒ แอดมินกด Enforce แล้วบริษัทที่ถูกระงับ/หมดอายุยังสร้าง/แก้ไขได้ต่อ <b>โดยไม่มีอะไรบอก</b>
/// (silent no-op) และหน้าแอดมินบอกเองว่า "แก้ใน appsettings — ไม่ขึ้นกับสวิตช์นี้"</para>
///
/// <para>═══ คำตัดสิน (ข้อ 14) ═══ <b>สวิตช์แอดมิน (ฐานข้อมูล) = ตัวตัดสินหลัก</b> ของทั้งการตัดสินฟีเจอร์/สถานะและด่านเขียน ·
/// config เหลือบทบาทเดียวคือ <b>override ฉุกเฉิน</b> (<see cref="OverrideKey"/> — ว่าง = ไม่มี override) สำหรับวันที่หน้าแอดมิน/ฐานข้อมูล
/// ใช้ไม่ได้ · ค่าเก่า <see cref="LegacyKey"/> <b>ไม่มีผลแล้ว</b> (หน้าแอดมินเตือนให้ลบ) · หน้าแอดมินแสดง <see cref="SubscriptionEnforcementState.Explanation"/>
/// ตัวเดียวกับที่ middleware ใช้</para>
///
/// <para>ไม่หลวม: คำขอที่ส่ง <c>X-Company-Id</c> มาเองยังถูกตัดสินฟีเจอร์/สถานะเสมอ (<see cref="SubscriptionGatePolicy.ActionFor"/>) ·
/// override/สวิตช์คุมแค่ (ก) คำขอจากหน้าเว็บ (ข) ด่านเขียนของทุกคำขอ</para>
///
/// <para>═══ รอบ 200 ฝ่ายค้าน S200-1 ═══ ค่าเดิม <see cref="LegacyKey"/> = <c>Enforce</c> (แผน WP-A1 ให้ตั้งใน production หลัง monitor) เคยบล็อก
/// การเขียนของ partner ที่บริษัทถูกระงับ/หมดอายุ · ถ้าตัดสินใหม่ว่า "คีย์เดิมไม่มีผล" ทันที = partner <b>หลวมลงเงียบ ๆ</b> หลัง deploy ⇒ คีย์เดิม =
/// <c>Enforce</c> ยังคุมด่านเขียนของคำขอ header ต่อ (<see cref="SubscriptionEnforcementState.LegacyHeaderEnforce"/>) จนกว่าจะลบคีย์ · ค่าอื่นของคีย์เดิม
/// (LogOnly/Off) ไม่มีผล (ไม่เข้มกว่าค่าเดิม) · ตอนบูต log เตือนครั้งเดียว (<see cref="LegacyBootWarning"/>) + หน้าแอดมินเตือนทุกครั้งที่เปิด</para>
/// </summary>
public static class SubscriptionEnforcementResolver
{
    /// <summary>config override ฉุกเฉิน — ว่าง/ไม่ตั้ง = ใช้สวิตช์แอดมิน · Off/Shadow/Enforce = ทับสวิตช์แอดมิน</summary>
    public const string OverrideKey = "Subscription:Enforcement:EmergencyOverride";

    /// <summary>config เดิม (WP-A1/A2) — เลิกใช้แล้ว ไม่มีผลต่อการตัดสิน · หน้าแอดมินเตือนเมื่อยังตั้งไว้</summary>
    public const string LegacyKey = "Subscription:Enforcement:Mode";

    /// <summary>อ่าน config ทั้งสองคีย์แล้วตัดสิน — ทางเข้าของ middleware และหน้าแอดมิน (ห้ามอ่านคีย์เองที่อื่น)</summary>
    public static SubscriptionEnforcementState Resolve(SubscriptionAdminSwitchRead adminSwitch, IConfiguration config) =>
        Resolve(adminSwitch, config[OverrideKey], config[LegacyKey]);

    /// <summary>ตัวตัดสินบริสุทธิ์ (ทดสอบได้ทุกคู่ค่า)</summary>
    public static SubscriptionEnforcementState Resolve(SubscriptionAdminSwitchRead adminSwitch, string? overrideRaw, string? legacyRaw)
    {
        var raw = string.IsNullOrWhiteSpace(overrideRaw) ? null : overrideRaw.Trim();
        SubscriptionEnforcementMode? overrideMode = null;
        var recognized = true;
        if (raw != null)
        {
            var parsed = ParseMode(raw);
            recognized = parsed.HasValue;
            // ค่าเพี้ยนในสถานการณ์ฉุกเฉิน (พิมพ์ผิดตอนรีบ) = Shadow: ไม่บล็อกใคร แต่ยังบันทึกให้เห็น ·
            // ห้ามตีความว่า "ไม่มี override" เพราะคนตั้งตั้งใจทับสวิตช์อยู่
            overrideMode = parsed ?? SubscriptionEnforcementMode.Shadow;
        }

        var effective = overrideMode ?? adminSwitch.Mode;
        var source = overrideMode.HasValue
            ? SubscriptionEnforcementSource.EmergencyOverride
            : adminSwitch.Source switch
            {
                SubscriptionAdminSwitchSource.Stored => SubscriptionEnforcementSource.AdminSwitch,
                SubscriptionAdminSwitchSource.NoRow => SubscriptionEnforcementSource.AdminSwitchDefault,
                _ => SubscriptionEnforcementSource.AdminSwitchUnreadable,
            };

        var warnings = new List<string>();
        if (raw != null && !recognized)
            warnings.Add($"ค่า {OverrideKey} = “{raw}” ไม่รู้จัก (ต้องเป็น Off, Shadow หรือ Enforce) — ระบบถือเป็นโหมดเงา "
                + "(ไม่บล็อกใคร) จนกว่าจะแก้ค่าหรือลบออก");
        var legacy = string.IsNullOrWhiteSpace(legacyRaw) ? null : legacyRaw.Trim();
        // S200-1: คีย์เดิม = Enforce ⇒ partner ยังถูกบังคับด่านเขียนแบบเดิมจนกว่าคีย์ถูกลบ (ไม่หลวมลงเงียบ) — ค่าอื่นไม่มีผล (ไม่เข้มกว่าเดิม)
        var legacyHeaderEnforce = IsLegacyEnforce(legacy) && effective != SubscriptionEnforcementMode.Enforce;
        if (legacy != null)
            warnings.Add(LegacyText(legacy, legacyHeaderEnforce));

        return new SubscriptionEnforcementState(
            effective,
            effective == SubscriptionEnforcementMode.Enforce || legacyHeaderEnforce
                ? SubscriptionWriteGateMode.Enforce
                : SubscriptionWriteGateMode.LogOnly,
            source, adminSwitch.Mode, adminSwitch.Source, overrideMode, raw, recognized,
            AdminSwitchHasEffect: !overrideMode.HasValue,
            Explain(effective, source, adminSwitch, raw) + (legacyHeaderEnforce
                ? $" · แต่ config เดิม {LegacyKey} = Enforce ยังตั้งอยู่ ⇒ partner ที่ส่ง X-Company-Id ของบริษัทถูกระงับ/หมดอายุยังถูกบล็อกการสร้าง/แก้ไขแบบเดิม"
                : ""),
            warnings,
            legacyHeaderEnforce);
    }

    private static bool IsLegacyEnforce(string? legacy) =>
        string.Equals(legacy?.Trim(), "enforce", StringComparison.OrdinalIgnoreCase);

    private static string LegacyText(string legacy, bool headerEnforce) =>
        headerEnforce
            ? $"ค่า config เดิม {LegacyKey} = “{legacy}” เลิกใช้แล้ว (รอบ 200) แต่ยังมีผลอยู่หนึ่งอย่าง: partner ที่ส่ง X-Company-Id ของบริษัทที่ถูกระงับ/"
              + "หมดอายุยังถูกบล็อกการสร้าง/แก้ไขแบบเดิม (ไม่หลวมลงเงียบ ๆ) — ลบคีย์นี้ออกจาก config (env/appsettings) ทุกเครื่องเมื่อพร้อมให้ partner เดินตามสวิตช์นี้"
            : $"ค่า config เดิม {LegacyKey} = “{legacy}” เลิกใช้แล้ว (รอบ 200) — ไม่มีผลต่อการตัดสิน · "
              + "ด่านบริษัทถูกระงับ/หมดอายุเดินตามโหมดที่มีผลจริงด้านบน · ลบคีย์นี้ออกจาก config ได้";

    /// <summary>ข้อความเตือนตอนบูต (ครั้งเดียวต่อ process · <c>Program.cs</c>) เมื่อยังตั้งคีย์เดิม <see cref="LegacyKey"/> · <c>null</c> = ไม่ได้ตั้ง
    /// (S200-1: ทิศหลวมลงต้องมองเห็นใน log ไม่ใช่แค่บนหน้าแอดมินที่อาจไม่มีใครเปิด)</summary>
    public static string? LegacyBootWarning(string? legacyRaw)
    {
        var legacy = string.IsNullOrWhiteSpace(legacyRaw) ? null : legacyRaw.Trim();
        return legacy == null ? null : LegacyText(legacy, IsLegacyEnforce(legacy))
            + (IsLegacyEnforce(legacy) ? "" : " (ค่านี้ไม่ใช่ Enforce จึงไม่มีผลใดเลย)");
    }

    /// <summary>ชื่อโหมด (ไม่สนตัวพิมพ์ · ห้ามตัวเลข — "2" ไม่ใช่ Enforce) · ไม่รู้จัก = <c>null</c></summary>
    public static SubscriptionEnforcementMode? ParseMode(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => SubscriptionEnforcementMode.Off,
            "shadow" => SubscriptionEnforcementMode.Shadow,
            "enforce" => SubscriptionEnforcementMode.Enforce,
            _ => null,
        };

    /// <summary>ป้ายไทยของโหมด</summary>
    public static string Label(SubscriptionEnforcementMode mode) => mode switch
    {
        SubscriptionEnforcementMode.Off => "ปิด (Off)",
        SubscriptionEnforcementMode.Enforce => "บังคับ (Enforce)",
        _ => "โหมดเงา (Shadow)",
    };

    private static string Explain(SubscriptionEnforcementMode effective, SubscriptionEnforcementSource source,
        SubscriptionAdminSwitchRead adminSwitch, string? overrideRaw)
    {
        var head = $"โหมดที่มีผลจริง: {Label(effective)}";
        return source switch
        {
            SubscriptionEnforcementSource.EmergencyOverride =>
                $"{head} — เพราะ config {OverrideKey} = “{overrideRaw}” (override ฉุกเฉิน) ทับสวิตช์แอดมิน · "
                + $"สวิตช์แอดมินที่บันทึกไว้ = {Label(adminSwitch.Mode)} ยังไม่มีผล จนกว่าจะลบค่า override ออกจาก config (env/appsettings) แล้วเริ่มระบบใหม่",
            SubscriptionEnforcementSource.AdminSwitchDefault =>
                $"{head} — ค่าตั้งต้นของสวิตช์แอดมิน (ยังไม่มีแถวค่าตั้งแพลตฟอร์ม) · ไม่มี override ใน config",
            SubscriptionEnforcementSource.AdminSwitchUnreadable =>
                $"{head} — อ่านสวิตช์แอดมินจากฐานข้อมูลไม่ได้ (migration ยังไม่รัน/ฐานข้อมูลสะดุด) จึงใช้โหมดเงาเพื่อไม่บล็อกใครเพราะอ่านค่าไม่ได้",
            _ => $"{head} — ตามสวิตช์แอดมิน (ฐานข้อมูล) · ไม่มี override ใน config",
        } + (effective == SubscriptionEnforcementMode.Enforce
                ? " · บริษัทถูกระงับ/หมดอายุเกินผ่อนผันถูกบล็อกการสร้าง/แก้ไขทุกช่องทาง"
                : " · บริษัทถูกระงับ/หมดอายุยังไม่ถูกบล็อก (log อย่างเดียว) จนกว่าจะเปิดบังคับ");
    }
}
