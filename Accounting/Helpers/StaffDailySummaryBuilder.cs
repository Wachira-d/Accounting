using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ประกอบข้อความสรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C) — <b>ตัวประกอบข้อความตัวเดียว</b> (pure · เทสต์ด้วยตัวเลข) ·
/// รับข้อมูลที่<b>กรองต่อผู้รับแล้ว</b> (<see cref="StaffDailySummaryPlan.FilterForRecipient"/>) — ตัวนี้ไม่รู้เรื่องสิทธิ์ ประกอบเฉพาะหัวข้อที่ไม่เป็น null
///
/// <para><b>เพดาน LINE</b> (<see cref="LineMessageLimits"/>): ≤ 5 ก้อน × ≤ 5,000 ตัวอักษร ใน<b>คำขอ push เดียว</b> · ตัดที่ขอบบรรทัด ·
/// เกิน 5 ก้อน ⇒ ก้อนสุดท้ายปิดด้วย "…และอีก N รายการ — ดูเพิ่มเติม: {ลิงก์หน้าหลังบ้านของหัวข้อแรกที่ถูกตัด}" (ไม่ตัดเงียบ)</para>
///
/// <para>PDPA (§6 ม.37): ข้อมูลลูกค้าในข้อความ = ชื่อ + เวลา + บริการ/เลขที่ เท่านั้น — ไม่มีเบอร์/อีเมล/ที่อยู่ (ข้อมูลไม่มีช่องให้ใส่ตั้งแต่ชนิดข้อมูล)</para></summary>
public static class StaffDailySummaryBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Line(string Text, string? Section, bool IsItem);

    /// <param name="today">วันที่ตามเวลาไทยของรอบนี้</param>
    /// <param name="companyName">ชื่อบริษัท (หัวข้อความ)</param>
    /// <param name="scopeName">ชื่อเว็บเมื่อชุดผูกเว็บเดียว · null = ทุกเว็บที่ผู้รับเห็น</param>
    /// <param name="data">ข้อมูลที่กรองต่อผู้รับแล้ว</param>
    /// <param name="baseUrl">ฐานของลิงก์ (<c>App:BaseUrl</c>) · null/ว่าง = บอกชื่อหน้าแทนลิงก์</param>
    public static IReadOnlyList<string> Build(DateOnly today, string companyName, string? scopeName, StaffDailySummaryPlan.SummaryData data, string? baseUrl)
        => Pack(Lines(today, companyName, scopeName, data), baseUrl);

    /// <summary>จำนวนรายการ (บรรทัดที่เป็นรายการจริง ไม่นับหัวข้อ/สรุป) — ใช้ตรวจว่า "แสดง + ที่บอกว่าเหลือ = ทั้งหมด"</summary>
    public static int ItemCount(DateOnly today, StaffDailySummaryPlan.SummaryData data)
        => Lines(today, "", null, data).Count(l => l.IsItem);

    // ─────────────── เนื้อหา ───────────────

    private static List<Line> Lines(DateOnly today, string companyName, string? scopeName, StaffDailySummaryPlan.SummaryData d)
    {
        var tomorrow = today.AddDays(1);
        var lines = new List<Line>
        {
            new($"☀️ สรุปประจำวัน {D(today)} · {companyName}" + (string.IsNullOrWhiteSpace(scopeName) ? "" : $" ({scopeName})"), null, false),
        };

        if (d.Bookings is { } bookings)
        {
            var sec = StaffDailySummaryPlan.Bookings;
            var todays = bookings.Where(b => b.Date == today).OrderBy(b => b.Start).ToList();
            var tomorrows = bookings.Where(b => b.Date == tomorrow).ToList();
            lines.Add(new("", sec, false));
            lines.Add(new($"📅 การจองวันนี้ — {todays.Count} คิว", sec, false));
            if (todays.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var g in todays.GroupBy(b => b.Provider).OrderBy(g => g.Min(b => b.Start)).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                lines.Add(new($"👤 {g.Key} ({g.Count()})", sec, false));
                foreach (var b in g.OrderBy(b => b.Start))
                    lines.Add(new($"• {T(b.Start)} {b.Service} · {b.Customer}", sec, true));
            }
            lines.Add(new($"📅 พรุ่งนี้ {D(tomorrow)} — {tomorrows.Count} คิว", sec, false));
            foreach (var g in tomorrows.GroupBy(b => b.Provider).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var bySvc = string.Join(" · ", g.GroupBy(b => b.Service).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Count()}"));
                lines.Add(new($"• {g.Key}: {g.Count()} คิว ({bySvc})", sec, true));
            }
        }

        if (d.Classes is { } classes)
        {
            var sec = StaffDailySummaryPlan.Classes;
            lines.Add(new("", sec, false));
            lines.Add(new($"🧘 คลาสวันนี้/พรุ่งนี้ — {classes.Count} รอบ", sec, false));
            if (classes.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var c in classes.OrderBy(c => c.Date).ThenBy(c => c.Start))
            {
                var day = c.Date == today ? "วันนี้" : "พรุ่งนี้";
                var who = string.IsNullOrWhiteSpace(c.Instructor) ? "" : $" ({c.Instructor})";
                var pct = c.Capacity > 0 ? (int)Math.Round(100m * c.Taken / c.Capacity, MidpointRounding.AwayFromZero) : 0;
                var wait = c.Waitlisted > 0 ? $" · สำรอง {c.Waitlisted}" : "";
                lines.Add(new($"• {day} {T(c.Start)} {c.Service}{who} {c.Taken}/{c.Capacity} ที่ ({pct}%){wait}", sec, true));
            }
        }

        if (d.MadeToOrder is { } mto)
        {
            var sec = StaffDailySummaryPlan.MadeToOrder;
            lines.Add(new("", sec, false));
            var unpaid = mto.Where(o => o.Outstanding > 0).Sum(o => o.Outstanding);
            lines.Add(new($"🧁 คิวผลิตตามสั่ง — {mto.Count} รายการ" + (unpaid > 0 ? $" · ค้างชำระรวม {M(unpaid)} ฿" : ""), sec, false));
            if (mto.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var o in mto.OrderBy(o => o.ReadyDate ?? DateOnly.MaxValue).ThenBy(o => o.Number, StringComparer.Ordinal))
            {
                var ready = o.ReadyDate is DateOnly r ? (r < today ? $"⚠️ เลยกำหนด {D(r)}" : $"พร้อมรับ {D(r)}") : "ยังไม่กำหนดวัน";
                var owe = o.Outstanding > 0 ? $" · ค้างชำระ {M(o.Outstanding)} ฿" : "";
                lines.Add(new($"• {ready} · {o.Number} · {o.Customer}{owe}", sec, true));
            }
        }

        if (d.ToShip is { } ship)
        {
            var sec = StaffDailySummaryPlan.ToShip;
            lines.Add(new("", sec, false));
            lines.Add(new($"📦 ออเดอร์รอจัดส่ง — {ship.Count} รายการ", sec, false));
            if (ship.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var o in ship.OrderBy(o => o.Number, StringComparer.Ordinal))
                lines.Add(new($"• {o.Number} · {o.Customer} · {o.Status}", sec, true));
        }

        if (d.Lodging is { } lodging)
        {
            var sec = StaffDailySummaryPlan.Lodging;
            lines.Add(new("", sec, false));
            lines.Add(new($"🏨 ที่พักวันนี้ — เช็คอิน {lodging.Count(l => l.CheckIn)} · เช็คเอาต์ {lodging.Count(l => l.CheckOut)}", sec, false));
            if (lodging.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var l in lodging.OrderBy(l => l.CheckIn ? 0 : 1).ThenBy(l => l.Number, StringComparer.Ordinal))
                lines.Add(new($"• {(l.CheckIn ? "เช็คอิน" : "เช็คเอาต์")} {l.Number} · {l.Guest} · {l.Property}", sec, true));
        }

        if (d.Credits is { } credits)
        {
            var sec = StaffDailySummaryPlan.Credits;
            lines.Add(new("", sec, false));
            lines.Add(new($"🎫 บัตรคอร์สใกล้หมดอายุ (7 วัน) — {credits.Count} ใบ", sec, false));
            if (credits.Count == 0) lines.Add(new("• ไม่มีรายการ", sec, false));
            foreach (var c in credits.OrderBy(c => c.ExpiresOn).ThenBy(c => c.PassNumber, StringComparer.Ordinal))
                lines.Add(new($"• {c.PassNumber} · {c.Customer} · {c.Package} · หมดอายุ {D(c.ExpiresOn)} · คงเหลือ {c.Remaining.ToString("0.##", Inv)}", sec, true));
        }

        return lines;
    }

    // ─────────────── ตัดเป็นก้อน ───────────────

    private static IReadOnlyList<string> Pack(List<Line> lines, string? baseUrl)
    {
        var max = LineMessageLimits.MaxTextChars;
        var chunks = new List<List<Line>> { new() };
        int Len(List<Line> c) => c.Count == 0 ? 0 : c.Sum(l => l.Text.Length) + c.Count - 1;

        int overflowAt = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Text.Length > max ? lines[i] with { Text = LineMessageLimits.Truncate(lines[i].Text) } : lines[i];
            var cur = chunks[^1];
            if (cur.Count == 0 || Len(cur) + 1 + line.Text.Length <= max) { cur.Add(line); continue; }
            if (chunks.Count == LineMessageLimits.MaxMessagesPerPush) { overflowAt = i; break; }
            // ไม่เริ่มก้อนใหม่ด้วยบรรทัดว่าง (ตัวคั่นหัวข้อ)
            chunks.Add(line.Text.Length == 0 ? new List<Line>() : new List<Line> { line });
        }

        if (overflowAt >= 0)
        {
            var rest = lines.Skip(overflowAt).ToList();
            var omitted = rest.Count(l => l.IsItem);
            var section = rest.FirstOrDefault(l => l.Section != null)?.Section;
            var last = chunks[^1];
            string footer;
            while (true)
            {
                footer = Footer(omitted, section, baseUrl);
                if (last.Count == 0 || Len(last) + 1 + footer.Length <= max) break;
                var removed = last[^1];
                last.RemoveAt(last.Count - 1);
                if (removed.IsItem) omitted++;
                if (removed.Section != null) section = removed.Section;
            }
            last.Add(new Line(footer, section, false));
        }

        return LineMessageLimits.Normalize(chunks.Where(c => c.Count > 0).Select(c => string.Join("\n", c.Select(l => l.Text)).Trim('\n')));
    }

    private static string Footer(int omitted, string? sectionKey, string? baseUrl)
    {
        var def = StaffDailySummaryPlan.Find(sectionKey) ?? StaffDailySummaryPlan.Sections[0];
        var where = string.IsNullOrWhiteSpace(baseUrl) ? $"หน้า \"{def.Label}\" ในระบบ" : baseUrl!.TrimEnd('/') + def.Page;
        return omitted > 0 ? $"…และอีก {omitted} รายการ — ดูเพิ่มเติม: {where}" : $"… ดูเพิ่มเติม: {where}";
    }

    private static string D(DateOnly d) => d.ToString("dd/MM/yyyy", Inv);
    private static string T(TimeOnly t) => t.ToString("HH:mm", Inv);
    private static string M(decimal m) => Math.Round(m, 2, MidpointRounding.AwayFromZero).ToString("#,##0.00", Inv);
}
