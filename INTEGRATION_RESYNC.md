# NextAcc Integration — Resync Update Contract (สำหรับทีมระบบภายนอก เช่น TakeTime)

> เอกสารส่งมอบสำหรับพัฒนา "ปุ่ม Retry / แก้ไขเอกสารที่ sync ไปแล้ว" ให้ตรงกับ
> พฤติกรรมจริงของ NextAcc รุ่นปัจจุบัน — อ้างอิงโค้ด `IntegrationService.cs`
> (ResyncUpdateInvoiceAsync / ResyncUpdateExpenseAsync)

---

## 1. Endpoint และการยืนยันตัวตน

| | |
|---|---|
| สร้าง/แก้ใบกำกับ (ฝั่งขาย) | `POST /api/integration/invoices` |
| สร้าง/แก้ค่าใช้จ่าย (ฝั่งซื้อ) | `POST /api/integration/expenses` |
| Auth | header `X-Integration-Key: <api-key>` หรือ `Authorization: Bearer <api-key>` |
| Content | `application/json` (camelCase) |

API key ออกจากหน้า "เชื่อมต่อระบบ" ของบริษัทนั้นใน NextAcc — key ผูกบริษัทอัตโนมัติ

## 2. Field ที่เกี่ยวกับ resync

```jsonc
{
  "externalRef": "TT-INV-2026-0042",   // 🔑 คีย์ dedup — map เข้า Document.Reference
  "externalId": "order-9911",          // fallback dedup เมื่อไม่ส่ง externalRef
  "resyncUpdate": true,                // ⬅ เปิดโหมดแก้ไข (default false = พฤติกรรมเดิม)
  "documentDate": "2026-07-05",
  "lines": [ { "itemName": "...", "quantity": 1, "unitPrice": 900, "vatRate": 7 } ],
  "includeVat": true
  // ...field อื่นเหมือน create ปกติ
}
```

- **`resyncUpdate` ไม่ส่ง/false** → เจอ `externalRef` ซ้ำ = idempotent skip
  (ตอบ `success:true, message:"Already synced"`) — ไม่มีอะไรถูกแก้
- **`resyncUpdate: true`** → เจอ `externalRef` ซ้ำ = เข้าโหมดแก้ไขตามตาราง §3

หมายเหตุ: เอกสารจาก `/invoices` ถูกเก็บเป็นประเภท **TaxInvoice** เสมอ —
ใบเสร็จ/ใบกำกับที่ TakeTime ส่งเข้ามาทางนี้ dedup เจอกันหมด

## 3. พฤติกรรม (Behavior Matrix)

| สถานการณ์ | ผลลัพธ์ | `message` ที่ตอบ (ใช้ prefix ตรวจได้) |
|---|---|---|
| ไม่เคยมี externalRef นี้ | สร้างใหม่ปกติ | `"Invoice created"` |
| ซ้ำ + ไม่ส่ง flag | ข้าม | `"Already synced"` |
| ซ้ำ + flag + **งวดเปิด + JE เดิมใบเดียว** | **in-place**: แก้ JE ใบเดิม — **เลข JE คงเดิม**, แทนที่บรรทัดทั้งชุด, อัปเดตยอด/วันที่/งวด | `"Resync updated (in-place) — แก้ JE เดิม เลข JE คงเดิม"` |
| ซ้ำ + flag + งวดปิด (หรือมี JE หลายใบ) | **reversal**: กลับ JE เดิม (คู่ Dr↔Cr ลงวันปัจจุบัน) + post JE ใหม่ | `"Resync updated (reversal) — งวดเดิมปิด/มีหลาย JE จึงกลับรายการ + post ใหม่"` |
| ซ้ำ + flag + ติด guard (§4) | ปฏิเสธ ไม่มีอะไรถูกแก้ | `success:false` + เหตุผลภาษาไทย |

**ทั้ง in-place และ reversal: เลขเอกสาร (DocumentNumber) คงเดิมเสมอ** — ไม่มีเลขกระโดด ไม่มีเอกสาร void

### วิธี parse ฝั่ง TakeTime
```
if (resp.success && resp.message.startsWith("Resync updated (in-place)"))  → "✅ แก้ JE เดิม (in-place)"
if (resp.success && resp.message.startsWith("Resync updated (reversal)")) → "✅ ปรับด้วย reversal (งวดเดิมปิด)"
if (resp.success && resp.message == "Already synced")                     → NextAcc รุ่นเก่า/ไม่ส่ง flag → fallback void→สร้างใหม่
if (!resp.success)                                                        → แสดง resp.message ให้ผู้ใช้ (บอกทางแก้ในตัว)
```

## 4. Guard — เคสที่ NextAcc ปฏิเสธ (ตอบ `success:false`)

| เงื่อนไข | ข้อความ (ขึ้นต้น) | ทางแก้ที่แนะนำผู้ใช้ |
|---|---|---|
| เอกสารมีการรับ/จ่ายชำระแล้ว | `"เอกสาร {no} มีการรับ/จ่ายชำระแล้ว..."` | void ใน NextAcc แล้วส่งใหม่ หรือออก CN/DN |
| มี CN/DN/เอกสารลูกอ้างถึง | `"เอกสาร {no} มีใบลดหนี้/ใบเพิ่มหนี้..."` | เช่นเดียวกัน |
| เดือนภาษี**เดิม**ยื่น ภ.พ.30/ล็อกแล้ว | `"เดือนภาษี {MM/yyyy} ... ยื่น ภ.พ.30 แล้ว"` | ปรับผ่าน CN/DN เดือนปัจจุบัน |
| **วันที่ใหม่**ตกเดือนที่ยื่นแล้ว | `"วันที่ใหม่ {วันที่} ตกในเดือนภาษีที่ยื่น ภ.พ.30 แล้ว"` | ใช้วันที่เดือนที่ยังไม่ยื่น |

## 5. สิ่งที่เกิดฝั่ง NextAcc (audit trail)

- **in-place**: JE ใบเดิม → บรรทัดถูกแทนทั้งชุด, `EntryDate`/งวด/ยอดตามข้อมูลใหม่,
  Description ต่อท้าย "(แก้ไขจาก resync)"; Document.Notes ประทับ
  `[Resync แก้ไขจากระบบภายนอก] <UTC> — แก้ JE เดิม (in-place, เลขคงเดิม)`
- **reversal**: JE เดิมคงอยู่ + มี JE กลับรายการ (ลิงก์ Original/ReversedBy)
  + JE ใหม่; Notes ประทับแบบเดียวกันระบุ reversal
- Sync log (หน้าเชื่อมต่อระบบ → ประวัติ sync): `Status = "Updated"`
- **PDF**: NextAcc render สดทุกครั้ง (ไม่มี cache) — เปิดดูหลัง resync ได้ยอดใหม่ทันที

## 6. Response shape

```jsonc
{
  "success": true,
  "message": "Resync updated (in-place) — แก้ JE เดิม เลข JE คงเดิม",
  "documentId": "guid",
  "contactId": "guid",
  "journalEntryId": "guid",     // JE ที่มีผล (in-place = ใบเดิม, reversal = ใบใหม่)
  "paymentId": null,
  "documentNumber": "IV-202607-0001"
}
```

## 7. Test script (ตามแผนของทีมปลายทาง)

1. สร้าง invoice ผ่าน API (`externalRef` ใหม่) → จด `journalEntryId`
2. ส่งซ้ำ **ยอดใหม่** + `resyncUpdate:true` →
   ต้องได้ `(in-place)` + `journalEntryId` **เท่าเดิม** + ยอดใน NextAcc เปลี่ยน
3. ปิดงวดบัญชีเดือนนั้นใน NextAcc (เมนู งวดบัญชี) → ส่งซ้ำอีกยอด →
   ต้องได้ `(reversal)` + `journalEntryId` ใหม่ + GL net = ยอดล่าสุด
4. บันทึกรับชำระเอกสารนั้น → ส่งซ้ำ → ต้องได้ `success:false` พร้อมเหตุผลชำระแล้ว
5. เปิด PDF เอกสาร → ยอดต้องเป็นยอดล่าสุดเสมอ

## 8. ประเภทที่รองรับ / ยังไม่รองรับ

| Endpoint | Resync update |
|---|---|
| `/api/integration/invoices` (TaxInvoice) | ✅ |
| `/api/integration/expenses` | ✅ |
| `/api/integration/credit-notes`, `/debit-notes` | ❌ (เอกสารปรับปรุงในตัวเอง — ออกใบใหม่แทน) |
| `/api/integration/payments` | ❌ (ใช้ void payment + ส่งใหม่) |

---
_อ้างอิงโค้ด: `IntegrationService.ResyncUpdateInvoiceAsync/ResyncUpdateExpenseAsync/`
`ResyncGuardAsync/UpdateJournalInPlaceAsync/ResyncReverseOriginalsAsync` —
รายละเอียด flow ภายในดู `DOCUMENT_FLOW.md` §2.3_
