using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ที่มาของรหัสประเภทเงินได้ของค่าธรรมเนียม 1 ประเภทบรรทัด (หน้าจอแสดงให้ผู้ทำบัญชีเห็นว่าค่ามาจากไหน)</summary>
public enum SettlementIncomeTypeSource
{
    /// <summary>ตารางประเภทบรรทัด (<see cref="SettlementLineTypeRules"/>) — บริบทในประเทศ</summary>
    LineTypeTable = 1,
    /// <summary>ค่าตั้งต้นของผู้ให้บริการต่างประเทศ (คำตัดสินรอบ 200 ข้อ 41: ค่าคอม/ค่าธรรมเนียมแพลตฟอร์มต่างประเทศ = 40(2))</summary>
    ForeignDefault = 2,
    /// <summary>ผู้ทำบัญชีตั้งไว้ที่ช่องทาง (<c>SettlementChannel.WhtIncomeTypeMapJson</c>)</summary>
    Channel = 3,
}

/// <summary>คำตอบ: รหัสประเภทเงินได้ (null = ประเภทนี้ไม่หัก ณ ที่จ่าย) + ที่มา</summary>
public readonly record struct SettlementIncomeTypeChoice(string? Code, SettlementIncomeTypeSource Source);

/// <summary>ตัวเลือก 1 แถวของหน้าตั้งค่าช่องทาง — ค่าตั้งต้นทั้งสองบริบทมาจากเซิร์ฟเวอร์ (หน้าเว็บไม่มีตารางเอง · F2 ข้อ 5)</summary>
/// <param name="LineType">ชื่อ enum ของประเภทบรรทัด (คีย์ใน <c>WhtIncomeTypeMapJson</c>)</param>
/// <param name="DomesticDefault">รหัสตั้งต้นเมื่อช่องทางเป็นผู้ให้บริการไทย · null = ไม่หัก</param>
/// <param name="ForeignDefault">รหัสตั้งต้นเมื่อช่องทางเป็นผู้ให้บริการต่างประเทศ (ภ.พ.36) · null = ไม่หัก</param>
public sealed record SettlementFeeIncomeTypeOption(string LineType, string Label, string? DomesticDefault, string? ForeignDefault);

/// <summary>
/// **รหัสประเภทเงินได้ของค่าธรรมเนียมแต่ละประเภทบรรทัด — ตัวตัดสินตัวเดียวของรอบโอน** (รอบ 200 ทีม WF · คำตัดสินข้อ 41 · ฝ่ายค้าน W-4/W-9)
///
/// <para>═══ ลำดับ ═══ (1) ผู้ทำบัญชีตั้งไว้ที่ช่องทาง (ค่า <see cref="NoWithholding"/> = ไม่หักประเภทนี้) → (2) ช่องทางผู้ให้บริการต่างประเทศ
/// (<see cref="SettlementFeeVatMode.ForeignPp36"/>): ค่าคอมมิชชัน · ค่าธรรมเนียมรับชำระ · ค่าบริการแพลตฟอร์ม · ค่าธรรมเนียมถอนเงิน = <b>40(2)</b>
/// (ตัวบท 40(2) "…ค่าธรรมเนียม ค่านายหน้า…" · ทิศหักไว้ก่อนปลอดภัยกว่าหักขาด — §54 ผู้จ่ายรับผิดร่วม) · ค่าโฆษณา/ค่าขนส่งคง 40(8)
/// (นอก ม.70 ⇒ แผนบล็อกพร้อมทางไปต่อ — ผู้ทำบัญชีจำแนกครั้งเดียวที่ช่องทาง) → (3) ตาราง <see cref="SettlementLineTypeRules"/> (ช่องทางไทยเหมือนเดิมทุกตัว)</para>
///
/// <para>ห้ามอ่าน <c>SettlementLineTypeRule.WhtIncomeCode</c> ตรง ๆ ในเส้นภาษีของรอบโอนอีก — ค่านั้นเป็นแค่ชั้นที่ (3)</para>
/// </summary>
public static class SettlementWhtIncomeType
{
    /// <summary>ค่าที่ผู้ทำบัญชีตั้งว่า "ประเภทบรรทัดนี้ไม่หัก ณ ที่จ่าย"</summary>
    public const string NoWithholding = "none";

    /// <summary>ประเภทบรรทัดที่ตั้งได้ = ค่าธรรมเนียมที่ออกเป็นเอกสารซื้อ</summary>
    public static bool Configurable(SettlementLineType type) => SettlementLineTypeRules.For(type).IsFee;

    /// <summary>ค่าตั้งต้นของผู้ให้บริการต่างประเทศ — ค่าคอม/ค่าธรรมเนียม = 40(2) (คำตัดสินข้อ 41) · อื่น ๆ = ตารางประเภทบรรทัด</summary>
    /// <para>ค่าอยู่ในตารางประเภทบรรทัดตัวเดียว (<see cref="SettlementLineTypeRule.ForeignWhtIncomeCode"/>) — ห้ามตัดสินตามชื่อประเภทเองนอกตาราง</para>
    private static string? ForeignDefault(SettlementLineType type)
    {
        var rule = SettlementLineTypeRules.For(type);
        return rule.ForeignWhtIncomeCode ?? rule.WhtIncomeCode;
    }

    /// <summary>ตัดสินรหัสของประเภทบรรทัดหนึ่ง (pure)</summary>
    public static SettlementIncomeTypeChoice Resolve(SettlementLineType type, SettlementFeeVatMode vatMode,
        IReadOnlyDictionary<SettlementLineType, string> channelMap)
    {
        if (channelMap.TryGetValue(type, out var set) && Configurable(type))
            return new SettlementIncomeTypeChoice(set == NoWithholding ? null : set, SettlementIncomeTypeSource.Channel);
        if (SettlementForeignWht.IsForeignChannel(vatMode))
            return new SettlementIncomeTypeChoice(ForeignDefault(type), SettlementIncomeTypeSource.ForeignDefault);
        return new SettlementIncomeTypeChoice(SettlementLineTypeRules.For(type).WhtIncomeCode, SettlementIncomeTypeSource.LineTypeTable);
    }

    /// <summary>ทางลัดของผู้คิดแผน — อ่านค่าตั้งของช่องทางด้วยตัวอ่านตัวเดียว (ค่าเสียถูกข้าม และ <see cref="MapIssue"/> ฟ้อง)</summary>
    public static SettlementIncomeTypeChoice For(SettlementLineType type, SettlementChannel channel)
        => Resolve(type, channel.FeeVatMode, ParseMap(channel.WhtIncomeTypeMapJson).Map);

    /// <summary>
    /// อ่าน <c>SettlementChannel.WhtIncomeTypeMapJson</c> — ตัวอ่านตัวเดียว (หน้าตั้งค่า · ผู้คิดแผน)
    /// <para>รูป: <c>{"PaymentFee":"2","AdsFee":"none"}</c> · คีย์ = ชื่อประเภทบรรทัดที่เป็นค่าธรรมเนียม · ค่า = รหัสใน <see cref="ThaiWhtRateTable"/>
    /// (เก็บเป็นรหัสมาตรฐาน) หรือ <see cref="NoWithholding"/> · คีย์/ค่าเสีย ⇒ <c>Rejected</c> (ผู้เรียกฟ้อง ห้ามทิ้งเงียบ)</para>
    /// </summary>
    public static (IReadOnlyDictionary<SettlementLineType, string> Map, IReadOnlyList<string> Rejected) ParseMap(string? json)
    {
        var map = new Dictionary<SettlementLineType, string>();
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return (map, rejected);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                rejected.Add("(json)");
                return (map, rejected);
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var key = p.Name.Trim();
                var ok = Enum.GetNames<SettlementLineType>().Contains(key, StringComparer.Ordinal)
                         && Enum.TryParse<SettlementLineType>(key, false, out var type)
                         && Configurable(type)
                         && p.Value.ValueKind == JsonValueKind.String;
                if (!ok)
                {
                    rejected.Add(p.Name);
                    continue;
                }
                var raw = (p.Value.GetString() ?? "").Trim();
                var t = Enum.Parse<SettlementLineType>(key);
                if (string.Equals(raw, NoWithholding, StringComparison.OrdinalIgnoreCase))
                    map[t] = NoWithholding;
                else if (ThaiWhtRateTable.Find(raw) is { } income && income.Code != "1")   // 40(1) เงินเดือน ไม่ใช่ค่าธรรมเนียม
                    map[t] = income.Code;
                else
                    rejected.Add($"{p.Name}={raw}");
            }
        }
        catch (JsonException)
        {
            rejected.Add("(json)");
        }
        return (map, rejected);
    }

    /// <summary>เขียนกลับเป็น JSON มาตรฐาน (เรียงตามค่า enum) · ว่าง ⇒ null</summary>
    public static string? Serialize(IReadOnlyDictionary<SettlementLineType, string> map)
        => map.Count == 0 ? null
            : JsonSerializer.Serialize(map.OrderBy(kv => (int)kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    /// <summary>ค่าตั้งที่เก็บไว้อ่านไม่ได้ (ข้อมูลเก่า/ถูกแก้นอกหน้าจอ) ⇒ ปัญหาบล็อกพร้อมทางไปต่อ — ห้ามคิดภาษีจากค่าที่ข้ามไปเงียบ ๆ · null = ไม่มีปัญหา</summary>
    public static SettlementPlanIssue? MapIssue(SettlementChannel channel)
    {
        var rejected = ParseMap(channel.WhtIncomeTypeMapJson).Rejected;
        if (rejected.Count == 0) return null;
        return new SettlementPlanIssue(SettlementPlanIssueCode.WhtIncomeTypeMapInvalid, true,
            $"ค่าตั้ง \"ประเภทเงินได้ของค่าธรรมเนียม\" ของช่องทาง \"{channel.DisplayName}\" มีค่าที่ใช้ไม่ได้: {string.Join(", ", rejected)}",
            "เปิดหน้าตั้งค่าช่องทาง เลือกประเภทเงินได้ของค่าธรรมเนียมใหม่แล้วบันทึก (ระบบไม่คิดภาษีจากค่าที่อ่านไม่ได้)",
            Array.Empty<Guid>(), null);
    }

    /// <summary>ข้อความไทยของคำตอบ (ป้ายรหัส + อัตราที่ช่องทางนี้จะใช้) — หน้าตั้งค่าช่องทางแสดงอย่างเดียว · ต่างประเทศใช้ตัวตัดสิน ม.70 ตัวเดียว</summary>
    public static string Describe(SettlementIncomeTypeChoice choice, SettlementFeeVatMode vatMode, DateTime paymentDate)
    {
        var from = choice.Source switch
        {
            SettlementIncomeTypeSource.Channel => "ตั้งที่ช่องทาง",
            SettlementIncomeTypeSource.ForeignDefault => "ค่าตั้งต้นผู้ให้บริการต่างประเทศ",
            _ => "ค่าตั้งต้น",
        };
        if (choice.Code is not string code) return $"ไม่หัก ณ ที่จ่าย ({from})";
        var income = ThaiWhtRateTable.Find(code);
        var label = income is null ? code : $"{income.TaxSection} {income.Name}";
        if (SettlementForeignWht.IsForeignChannel(vatMode))
        {
            var d = SettlementForeignWht.Decide(code, paymentDate);
            return d.RatePercent is decimal r
                ? $"{label} — {ForeignWhtRateResolver.Section70Reference} {r:0.##}% ภ.ง.ด.54 ({from})"
                : $"{label} — ไม่อยู่ใน {ForeignWhtRateResolver.Section70Reference} ⇒ รอบโอนที่หักจะถูกบล็อกจนกว่าจะจำแนกใหม่ ({from})";
        }
        return ThaiWhtRateTable.RateFor(code, payeeIsJuristic: true) is decimal thai
            ? $"{label} — {thai:0.##}% ภ.ง.ด.53 ({from})"
            : $"{label} ({from})";
    }

    /// <summary>ตัวเลือกของหน้าตั้งค่าช่องทาง — ทุกประเภทค่าธรรมเนียม พร้อมค่าตั้งต้นสองบริบท</summary>
    public static IReadOnlyList<SettlementFeeIncomeTypeOption> Options()
        => SettlementLineTypeRules.All.Where(r => Configurable(r.Type))
            .Select(r => new SettlementFeeIncomeTypeOption(r.Type.ToString(), r.LabelTh, r.WhtIncomeCode, ForeignDefault(r.Type)))
            .ToList();
}
