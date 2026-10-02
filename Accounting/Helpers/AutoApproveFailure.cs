using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>สถานะจริงของเอกสารหลังการอนุมัติ (อัตโนมัติ) ล้ม — ตัดสินจากฐานข้อมูล ไม่ใช่จาก object ที่อาจค้าง (ฝ่ายค้าน P1-B รอบ PP36)</summary>
public enum AutoApproveFailureKind
{
    /// <summary>ยังเป็นร่าง/รออนุมัติ — การอนุมัติไม่มีผล (ยังไม่ออกเลข · ไม่มี JE)</summary>
    StillDraft,
    /// <summary>อนุมัติมีผลแล้ว (<see cref="DocumentStatusRules.IsEffective"/>) และมี JE ที่มีผล/ชนิดที่ไม่ลงบัญชี — ล้มที่ขั้นหลัง commit</summary>
    Committed,
    /// <summary>สถานะมีผลแล้วแต่<b>ไม่มี JE</b> ทั้งที่ชนิดนี้ต้องมี — ใบค้างจากก่อนรอบแก้ ⇒ ชี้เครื่องมือลงบัญชีย้อนหลัง</summary>
    CommittedWithoutJournal,
    /// <summary>ยกเลิก/ปฏิเสธ/อื่น ๆ — ไม่ใช่เรื่องของการอนุมัติรอบนี้ · <b>ห้ามเขียนหมายเหตุ</b>ลงใบ</summary>
    Closed,
}

/// <summary>ผลของ "อนุมัติอัตโนมัติไม่สำเร็จ" ที่ทางเข้าหนึ่งบันทึกไว้แล้ว — คืนให้ผู้เรียกใช้ตอบผู้ใช้ด้วยข้อความเดียวกับที่ลงบนเอกสาร</summary>
/// <param name="Kind">สถานะจริงจากฐานข้อมูล (<see cref="AutoApproveFailure.Classify"/>)</param>
/// <param name="DocumentNumber">เลขเอกสารจริงจากฐานข้อมูล</param>
/// <param name="Message">ข้อความไทยพร้อมทางไปต่อ (ตัวเดียวกับที่ต่อท้ายหมายเหตุภายในของเอกสาร เมื่อไม่ใช่ Closed)</param>
public sealed record AutoApproveFailureOutcome(AutoApproveFailureKind Kind, string DocumentNumber, string Message)
{
    /// <summary>ยังเป็นร่าง — ทางเข้านับเป็น "ล้ม"</summary>
    public bool StillDraft => Kind == AutoApproveFailureKind.StillDraft;

    /// <summary>อนุมัติมีผลแล้ว (ล้มที่ขั้นหลัง commit) — ทางเข้านับเป็น "อนุมัติ" ห้ามบอก "ยังเป็นร่าง"</summary>
    public bool Committed => Kind is AutoApproveFailureKind.Committed or AutoApproveFailureKind.CommittedWithoutJournal;
}

/// <summary>
/// PP36_REVIEW P0-2 (2026-10-02) — ข้อความ "อนุมัติอัตโนมัติไม่สำเร็จ" ตัวเดียวของทุกทางเข้า (สร้างใบสำคัญจ่ายเงินสด · ลายเซ็นครบ/workflow ·
/// อนุมัติหลายใบ · สแกน · LINE · รายการประจำ · รอบโอน · CMS · แพลตฟอร์ม)
/// <para>═══ ที่มา ═══ PV-20260901-0001: อนุมัติอัตโนมัติล้ม (ด่าน JE §83/6) แล้วผู้เรียก <c>LogInformation("staying Draft")</c> — ประโยคนั้น<b>ไม่จริง</b>
/// (ค่าที่ค้างใน context ถูก SaveChanges ถัดไปบันทึกเป็น Paid + เลขเอกสาร) และไม่มีใครเห็นเหตุผล ⇒ หลักการ "ล้มดัง 3 ที่":
/// หมายเหตุบนเอกสาร (ผู้ใช้เปิดดูเห็น) · คำตอบของทางเข้า · log ระดับ Warning ขึ้นไป · และ "สาเหตุ" ที่บอกต้องตรวจจากสถานะจริงในฐานข้อมูล</para>
/// <para>ฝ่ายค้าน P1-B: สถานะจริงมีสี่แบบ ไม่ใช่สอง — ใบที่ถูกยกเลิก/ปฏิเสธระหว่างนั้นห้ามได้ข้อความ "อนุมัติและลงบัญชีแล้ว" และห้ามถูกเขียนหมายเหตุ ·
/// ใบ "อนุมัติแล้ว" ที่ไม่มี JE ห้ามได้ข้อความ "ลงบัญชีแล้ว"</para>
/// <para>ฝ่ายค้าน P2-6: อนุมัติสำเร็จภายหลัง ⇒ <see cref="MarkResolved"/> ต่อท้าย "แก้แล้ว" (ไม่ลบ — คงร่องรอย)</para>
/// </summary>
public static class AutoApproveFailure
{
    /// <summary>หัวข้อความของหมายเหตุล้ม (ทั้งข้อความกลางและหมายเหตุคำเตือนของ PV เงินสด) — ตัวตรวจ <see cref="MarkResolved"/> อ่านจากนี้</summary>
    public const string NoteMarker = "อนุมัติอัตโนมัติ";
    private const string FailedWord = "ไม่สำเร็จ";
    private const string ResolvedMarker = "✅ แก้แล้ว: อนุมัติสำเร็จเมื่อ";

    /// <summary>ตัดสินสถานะจริง — <paramref name="hasLiveJournal"/>/<paramref name="expectsJournal"/> ใช้แยก "มีผลแล้ว" กับ "มีผลแต่ไม่มี JE"</summary>
    public static AutoApproveFailureKind Classify(DocumentStatus status, bool hasLiveJournal, bool expectsJournal)
    {
        if (status is DocumentStatus.Draft or DocumentStatus.WaitingApproval) return AutoApproveFailureKind.StillDraft;
        if (!DocumentStatusRules.IsEffective(status)) return AutoApproveFailureKind.Closed;
        return hasLiveJournal || !expectsJournal ? AutoApproveFailureKind.Committed : AutoApproveFailureKind.CommittedWithoutJournal;
    }

    /// <summary>ข้อความเดียวของทุกทางเข้า — <paramref name="channel"/> = ทางเข้าที่สั่งอนุมัติ (ภาษาไทย) · <paramref name="reason"/> = เหตุผลที่ผู้ใช้อ่านได้</summary>
    public static string Message(string channel, string documentNumber, AutoApproveFailureKind kind, string reason, DocumentStatus status)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "ไม่ทราบสาเหตุ (ดูบันทึกระบบ)" : reason.Trim();
        return kind switch
        {
            AutoApproveFailureKind.StillDraft =>
                $"⚠️ {NoteMarker} ({channel}) {FailedWord}: {why} — เอกสาร {documentNumber} ยังเป็นร่าง ยังไม่ออกเลข ยังไม่ลงบัญชี · "
                + "แก้ตามเหตุผลแล้วกด “อนุมัติ” ที่หน้าเอกสาร",
            AutoApproveFailureKind.Committed =>
                $"⚠️ เอกสาร {documentNumber} อนุมัติและลงบัญชีแล้ว แต่ขั้นหลังอนุมัติ ({channel}) ล้ม: {why} — ไม่ต้องอนุมัติซ้ำ · "
                + "ตรวจการแจ้งเตือน/e-Tax ของใบนี้",
            AutoApproveFailureKind.CommittedWithoutJournal =>
                $"⚠️ เอกสาร {documentNumber} มีสถานะอนุมัติแล้วแต่ยังไม่มีรายการบัญชี ({channel} ล้ม: {why}) — ไม่ต้องอนุมัติซ้ำ · "
                + "เปิดเอกสาร → แผง 📒 → “🔧 ลงบัญชีให้ใบนี้”",
            _ =>
                $"ℹ️ เอกสาร {documentNumber} อยู่สถานะ {status} (ยกเลิก/ปฏิเสธ) — ไม่มีการอนุมัติในรอบนี้ ({channel}: {why})",
        };
    }

    /// <summary>
    /// P2-6 — อนุมัติสำเร็จภายหลัง: หมายเหตุมีข้อความ "อนุมัติอัตโนมัติ … ไม่สำเร็จ" ที่ยังไม่ถูกปิด ⇒ ต่อท้าย "✅ แก้แล้ว: อนุมัติสำเร็จเมื่อ …" ·
    /// ไม่มีข้อความล้ม/ปิดไปแล้ว ⇒ คืนค่าเดิมทุกตัวอักษร (idempotent)
    /// </summary>
    /// <param name="approvedAtUtc">เวลาอนุมัติ (UTC) — แสดงเป็นเวลาไทย (+07:00)</param>
    public static string? MarkResolved(string? notes, DateTime approvedAtUtc, string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(notes)) return notes;
        var lastFail = LastFailureIndex(notes);
        if (lastFail < 0) return notes;
        var lastResolved = notes.LastIndexOf(ResolvedMarker, StringComparison.Ordinal);
        if (lastResolved > lastFail) return notes;
        var bkk = approvedAtUtc.AddHours(7).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return notes.TrimEnd() + "\n" + $"{ResolvedMarker} {bkk} (เวลาไทย) โดย {approvedBy}";
    }

    private static int LastFailureIndex(string notes)
    {
        var idx = notes.LastIndexOf(NoteMarker, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var lineEnd = notes.IndexOf('\n', idx);
            var line = lineEnd < 0 ? notes[idx..] : notes[idx..lineEnd];
            if (line.Contains(FailedWord, StringComparison.Ordinal)) return idx;
            idx = idx == 0 ? -1 : notes.LastIndexOf(NoteMarker, idx - 1, StringComparison.Ordinal);
        }
        return -1;
    }
}
