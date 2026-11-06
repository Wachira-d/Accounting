using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ช่องทางรับเงินต่อผู้ติดต่อ / ต่อรายการประจำ — ตัวต่อชั้นเดียว** (pure · รอบ 248 · AUTOPILOT_BILLING_DESIGN §5.1 · ช่องว่าง A-1 · หลักการ #4 "ค่าทับเป็นชั้น")
///
/// <para>ชั้น (เฉพาะเจาะจงชนะ): ตารางส่งอีเมล › รายการประจำ › ผู้ติดต่อ › บริษัท · แต่ละชั้นตอบสองเรื่องแยกกัน (ตรงกับคอลัมน์เดิมของตารางส่ง
/// <c>AttachPayLink</c>/<c>PayLinkChannelsJson</c>): <b>แนบลิงก์ชำระไหม</b> · <b>ช่องทางไหน</b> — ชั้นที่ตอบ null = ตามชั้นถัดไป ⇒ ไม่ตั้งรายการ/ผู้ติดต่อ
/// = ผลเท่ารอบ 241 เป๊ะ (ทิศตรงข้าม: <c>Inherit</c> ⇒ ตามบริษัทเหมือนเดิม)</para>
///
/// <para>โหมด (ชื่อ enum เก็บในคอลัมน์ <c>PaymentMode</c>): <see cref="Inherit"/> · <see cref="Online"/> (gateway เท่านั้น) · <see cref="TransferOnly"/>
/// (ไม่แนบลิงก์ gateway — ลูกค้าโอนตามบัญชี/QR บนเอกสาร · ลิงก์หน้าเว็บที่มีแต่เลขบัญชีเป็นงานรอบ 250 เพราะคำตัดสิน 241-2 "ลิงก์ต้องมีช่องทางออนไลน์") ·
/// <see cref="Both"/> · <see cref="None"/> (ไม่แนบ) · ช่องทางย่อยตัดกับที่บริษัทเปิดจริงเสมอ (<see cref="DocumentPayLinkPolicy.Allowed"/>)</para>
/// </summary>
public static class AutopilotSettingsResolver
{
    public const string Inherit = "Inherit";
    public const string Online = "Online";
    public const string TransferOnly = "TransferOnly";
    public const string Both = "Both";
    public const string None = "None";

    public static readonly IReadOnlyList<string> Modes = new[] { Inherit, Online, TransferOnly, Both, None };

    public const string RuleCode = "AUTOPILOT-PAYMODE";

    /// <summary>ป้ายของโหมด (หน้าแสดงจากตรงนี้)</summary>
    public static string Label(string? mode) => Normalize(mode) switch
    {
        Online => "ชำระออนไลน์ (บัตร/พร้อมเพย์ผ่านผู้ให้บริการ) — แนบลิงก์ชำระ",
        TransferOnly => "โอนเข้าบัญชีเท่านั้น — ไม่แนบลิงก์ชำระออนไลน์ (ลูกค้าโอนตามบัญชี/QR บนเอกสาร)",
        Both => "ทั้งออนไลน์และโอนเข้าบัญชี — แนบลิงก์ชำระ",
        None => "ไม่แนบลิงก์ชำระ",
        _ => "ตามค่าชั้นถัดไป",
    };

    /// <summary>ชั้นหนึ่งชั้น — null ทั้งสองช่อง = ไม่มีความเห็น (ตามชั้นถัดไป)</summary>
    public sealed record Layer(bool? Attach, IReadOnlyList<string>? Channels);

    /// <summary>ผล — ช่องทางยังต้องตัดกับที่บริษัทเปิดด้วย <see cref="DocumentPayLinkPolicy.Allowed"/> ที่ผู้อ่าน</summary>
    public sealed record PayLinkChoice(bool Attach, IReadOnlyList<string>? Channels, string Source);

    /// <summary>ชื่อโหมดมาตรฐาน · ว่าง/Inherit = null · ชื่อไม่รู้จัก = null (ผู้บันทึกต้องตรวจด้วย <see cref="ValidateMode"/> ก่อน — ห้ามเก็บเงียบ)</summary>
    public static string? Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var m = Modes.FirstOrDefault(x => string.Equals(x, mode.Trim(), StringComparison.OrdinalIgnoreCase));
        return m == null || m == Inherit ? null : m;
    }

    /// <summary>null = ใช้ได้ · ข้อความ = ปฏิเสธ (บอกค่าที่เลือกได้)</summary>
    public static string? ValidateMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) || Modes.Any(x => string.Equals(x, mode.Trim(), StringComparison.OrdinalIgnoreCase))
            ? null
            : $"ไม่รู้จักโหมดรับเงิน \"{mode}\" — เลือกได้: {string.Join(" · ", Modes)}";

    /// <summary>ชื่อช่องทางที่ส่งมาต้องเป็นชื่อ enum ที่รู้จัก (<see cref="PaymentMethodKind"/>) — null = ใช้ได้ ·
    /// ตัดกับที่บริษัทเปิดจริงทุกครั้งที่อ่าน (ช่องที่บริษัทปิดภายหลังไม่ถูกเปิดให้ลูกค้า)</summary>
    public static string? ValidateChannels(IEnumerable<string>? channels)
    {
        var bad = (channels ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)
            && !Enum.TryParse<PaymentMethodKind>(c.Trim(), true, out _)).Select(c => c.Trim()).Distinct().ToList();
        return bad.Count == 0 ? null : "ไม่รู้จักช่องทางชำระ: " + string.Join(", ", bad);
    }

    /// <summary>ชั้นจากโหมดของรายการประจำ/ผู้ติดต่อ</summary>
    /// <param name="mode">ค่าคอลัมน์ PaymentMode</param>
    /// <param name="channels">ช่องทางที่ตั้งไว้ในชั้นนั้น (null = ทุกช่องตามโหมด)</param>
    /// <param name="companyAvailable">ช่องทางที่บริษัทเปิดจริง (<see cref="DocumentPayLinkPolicy.CompanyAvailable"/>)</param>
    public static Layer FromMode(string? mode, IReadOnlyList<string>? channels, IReadOnlyList<string> companyAvailable)
    {
        var picked = channels is { Count: > 0 } ? channels : null;
        return Normalize(mode) switch
        {
            Online => new Layer(true, DocumentPayLinkPolicy.OnlineOnly(picked ?? companyAvailable)),
            TransferOnly => new Layer(false, null),
            Both => new Layer(true, picked),
            None => new Layer(false, null),
            // Inherit + เลือกช่องทางไว้ = ตั้งเฉพาะช่องทาง ไม่ตัดสินเรื่องแนบ
            _ => new Layer(null, picked),
        };
    }

    /// <summary>ต่อชั้น — แต่ละเรื่องเลือกจากชั้นเฉพาะที่สุดที่ตอบ</summary>
    public static PayLinkChoice Resolve(Layer? schedule, Layer? recurring, Layer? contact, bool companyAttach, IReadOnlyList<string>? companyChannels)
    {
        var layers = new (Layer? L, string Name)[] { (schedule, "ตารางส่ง"), (recurring, "รายการประจำ"), (contact, "ผู้ติดต่อ") };
        bool attach = companyAttach;
        string source = "บริษัท";
        foreach (var (l, name) in layers)
            if (l?.Attach is { } a) { attach = a; source = name; break; }
        IReadOnlyList<string>? ch = companyChannels is { Count: > 0 } ? companyChannels : null;
        foreach (var (l, _) in layers)
            if (l?.Channels is { Count: > 0 } c) { ch = c; break; }
        return new PayLinkChoice(attach, ch, source);
    }
}
