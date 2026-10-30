namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 212 แพ็ก B (คำตัดสินข้อ 171 ข้อ 4) — ร้านเปลี่ยน LINE OA ที่ใช้แจ้งลูกค้า 1 ครั้ง (ลูกค้าที่ผูกกับ OA เดิมต้องผูกใหม่) ·
/// บันทึกโดย <c>ICustomerRelinkInviteService.RecordSwitchAsync</c> (PUT ค่าตั้ง LINE · webhook เห็น bot ใหม่) · ประมวลโดย <c>CustomerNotifyOutboxJob</c> ·
/// ตัวตัดสินอยู่ที่ <c>Helpers/CustomerRelinkInvitePlan</c> · ตาราง/คอลัมน์: <c>DatabaseMigrationHelper.CmsRound212NotifyStatements</c> + <c>Helpers/CustomerRelinkMigration</c>
/// </summary>
public class LineOaSwitchEvent : TenantEntity
{
    /// <summary>OA เดิม: nextacc / company (<c>CustomerNotifyChannel.Scope*</c>)</summary>
    public string? FromScope { get; set; }
    /// <summary>bot userId ของ OA เดิม (null = ไม่รู้)</summary>
    public string? FromDestination { get; set; }
    public string? ToScope { get; set; }
    public string? ToDestination { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    /// <summary>config-put / webhook-destination (<c>CustomerRelinkInvitePlan.Source*</c>)</summary>
    public string? Source { get; set; }
    /// <summary>คีย์กันบันทึกซ้ำ (<c>CustomerRelinkInvitePlan.DedupeKey</c>) · unique ต่อบริษัทขณะยังไม่ประมวลเสร็จ</summary>
    public string? DedupeKey { get; set; }
    /// <summary>ประมวลครบ (ทุกคนจบแล้ว) = watermark ของแถวนี้ · null = ยังค้าง</summary>
    public DateTime? ProcessedAt { get; set; }
    /// <summary>ผู้รับที่วางแผนเชิญ (หลังตัดผู้ที่ข้าม)</summary>
    public int Total { get; set; }
    /// <summary>ส่งถึงแล้ว</summary>
    public int Invited { get; set; }
    /// <summary>ส่งไม่สำเร็จ (ยังลองต่อ หรือครบจำนวนครั้งแล้ว)</summary>
    public int Failed { get; set; }
    /// <summary>ไม่เชิญ (ถอนความยินยอม/บล็อก OA/ไม่มีรายการค้าง/ไม่มีช่องติดต่อ/ร้านปิดประเภทนี้)</summary>
    public int Skipped { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastRunAt { get; set; }
}

/// <summary>รอบ 212 ฝ่ายค้าน (B+D) ข้อ 4 — bot (<c>destination</c>) ของ <b>OA กลาง</b>ที่ webhook เห็น (ลายเซ็นผ่าน + ตรงค่าตั้ง) · ระดับแพลตฟอร์ม (OA เดียวทุกบริษัท ⇒ ไม่มี CompanyId) ·
/// webhook แค่ประทับ (<c>INSERT … ON CONFLICT</c>) · <c>CustomerNotifyOutboxJob</c> สแกนลิงก์ข้ามบริษัทแล้วบันทึก <see cref="LineOaSwitchEvent"/> ต่อบริษัท ·
/// <c>ScannedAt</c> null = รอสแกน (เห็นซ้ำหลังสแกนเกิน 6 ชม. ⇒ สแกนใหม่ — การบันทึกซ้ำกันด้วยคีย์ของแถวการสลับ)</summary>
public class LineOaDestinationSighting
{
    public string Destination { get; set; } = "";
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? ScannedAt { get; set; }
}
