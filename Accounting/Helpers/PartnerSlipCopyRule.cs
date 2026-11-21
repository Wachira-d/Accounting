namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินเดียวของ "รับสำเนาสลิปการโอนจากคู่ค้าเป็นหลักฐาน"** (ขอบเขต <see cref="PartnerConsent.Scope.ReceiveSlipCopy"/> ของ<b>ผู้ซื้อ</b> ·
/// ดีไซน์ team-264-a2b-design §4.4(ข) · รอบ 288) · pure · ผู้เรียกเดียว <c>PartnerPayablePipeline.CopySlipsAsync</c> (งานเบื้องหลังฝั่งผู้ซื้อ —
/// ไม่ทำในคำขอของผู้ขาย)
/// <para>สลิปที่ผู้ซื้อส่งให้ผู้ขายทางพอร์ทัล/หน้าชำระ/LINE/อีเมล ⇒ ผู้ขายยืนยันรับเงินแล้ว (รายการชำระสำเร็จ ⇒ การแจ้ง "ผู้ขายรับเงินแล้ว" ในบริษัทผู้ซื้อ) ⇒
/// ผู้ซื้อยินยอม ⇒ สำเนา<b>รูปสลิปของรายการนั้นเท่านั้น</b>เข้าบริษัทผู้ซื้อ แนบที่ใบซื้อปลายทาง (ข้อมูลของผู้ซื้อกลับไปหาผู้ซื้อ · PDPA: ไม่ส่งข้อมูลอื่น)</para>
/// <para>ไม่ยินยอม ⇒ ไม่ส่งรูปข้ามบริษัท (ไม่ประทับว่าตรวจแล้ว — ยินยอมภายหลังก็ไม่ย้อนไปส่งการชำระที่เกิดก่อนให้ความยินยอม) ·
/// สลิปที่มาจากระบบของผู้ซื้อเอง (แจ้งโอนจากระบบเรา) ไม่ส่งกลับ (มีอยู่แล้ว) · รายการก่อนระบบผูกไฟล์กับรายการ ⇒ ไม่เดาไฟล์</para>
/// <para>สลิปเป็น<b>หลักฐานประกอบ</b> — ไม่เปลี่ยนการตัดสินแหล่งเงิน (ชั้นสลิปป้อนจากธนาคาร + เลขท้ายบนการแจ้งอยู่แล้ว และไม่พอให้บันทึกจ่ายเองถ้าไม่ครบเงื่อนไข —
/// <see cref="SlipMoneySource.PayerSlipStrong"/>)</para>
/// </summary>
public static class PartnerSlipCopyRule
{
    public const string RuleCode = "XT-SLIP-COPY";
    public const string LegalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.19 (ความยินยอมของผู้ซื้อ — เฉพาะรูปสลิปของรายการนั้น) · พ.ร.บ.การบัญชี 2543 ม.14 (เก็บเอกสารประกอบ)";

    /// <summary>ผู้สร้างไฟล์แนบฝั่งผู้ซื้อ (ป้ายบอกที่มาบนไฟล์แนบของใบซื้อ)</summary>
    public const string AttachmentCreatedBy = "partner-slip-copy";
    /// <summary>ชื่อไฟล์ที่ผู้ซื้อเห็น (ไม่มีข้อมูลผู้โอน)</summary>
    public const string AttachmentNamePrefix = "สลิปที่ส่งให้ผู้ขาย";

    /// <summary>ลองอ่านไฟล์สลิปของผู้ขายกี่ครั้งเมื่อล้มชั่วคราว (แล้วเลิกลอง — สถานะบอกตามจริง)</summary>
    public const int MaxAttempts = 3;

    /// <summary>ดูการแจ้งย้อนหลังกี่วัน</summary>
    public const int LookbackDays = 60;

    public enum Outcome
    {
        /// <summary>คัดสำเนาได้ — ทำเลย</summary>
        Copy,
        /// <summary>ไม่มีอะไรให้คัด/ไม่ควรคัด — จบ (ประทับตรวจแล้ว)</summary>
        Done,
        /// <summary>ยังไม่ถึงเวลา — ไม่ประทับ (ลองรอบถัดไป)</summary>
        Wait,
    }

    public sealed record Facts(
        bool ConsentOk, DateTime? ConsentGrantedAt, DateTime NoticeCreatedAt,
        bool HasIntent, bool IntentIsSlip, bool IntentSucceeded,
        Guid? SlipAttachmentId, bool AttachmentFound, string? AttachmentChannel);

    public sealed record Verdict(Outcome Outcome, string Reason);

    public static Verdict Decide(Facts f)
    {
        if (!f.ConsentOk) return new(Outcome.Wait, "ผู้ซื้อยังไม่ได้ยินยอมรับสำเนาสลิป — ไม่ส่งรูปข้ามบริษัท");
        if (f.ConsentGrantedAt is DateTime g && f.NoticeCreatedAt < g)
            return new(Outcome.Done, "การชำระเกิดก่อนให้ความยินยอม — ไม่ส่งย้อนหลัง");
        if (!f.HasIntent || !f.IntentIsSlip) return new(Outcome.Done, "การชำระนี้ไม่ได้มาจากสลิป");
        if (!f.IntentSucceeded) return new(Outcome.Wait, "ผู้ขายยังไม่ยืนยันรับเงินจากสลิปนี้");
        if (f.SlipAttachmentId == null) return new(Outcome.Done, "รายการรุ่นเก่าไม่ได้ผูกไฟล์สลิป — ไม่เดาไฟล์");
        if (!f.AttachmentFound) return new(Outcome.Done, "ไม่พบไฟล์สลิปของรายการนี้ที่ผู้ขาย");
        if (string.Equals(f.AttachmentChannel, OwnChannel, StringComparison.Ordinal))
            return new(Outcome.Done, "สลิปมาจากระบบของผู้ซื้อเอง (แจ้งโอนจากระบบเรา) — มีอยู่แล้ว");
        return new(Outcome.Copy, "ผู้ขายยืนยันรับเงินจากสลิปนี้แล้ว — สำเนาแนบใบซื้อเป็นหลักฐาน");
    }

    /// <summary>ช่องทางของสลิปที่ผู้ซื้อแจ้งโอนจากระบบตัวเอง (สำเนาไปหาผู้ขาย — ไม่ส่งกลับ) · ค่าตรงกับ <c>SlipEvidenceService.ChannelPartnerNextAcc</c></summary>
    public const string OwnChannel = "partner-nextacc";

    /// <summary>โฟลเดอร์ที่ไม่เสิร์ฟตรงของสำเนาฝั่งผู้ซื้อ (เปิดผ่านไฟล์แนบของใบ — ด่านสิทธิ์ + บริษัท)</summary>
    public static string RelativeDir(DateTime nowUtc) => $"uploads/document-slips/{nowUtc:yyyy-MM}";

    /// <summary>ป้ายบนการแจ้ง/หน้า (server computes)</summary>
    public const string CopiedLabel = "มีสำเนาสลิปแนบที่ใบซื้อ (จากผู้ขาย)";
}
