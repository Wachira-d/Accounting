# ทีม T4 — UX 1-click + ความต่อเนื่องสแกน→เอกสาร

> ผู้ตรวจ: senior product designer (accounting) + front-end engineer + QA นับคลิก
> วันที่: 2026-09-05 · เรพ: /home/user/Accounting · HEAD: (ดู git log ด้านล่าง)
> กติกา: OCR-BRIEF.md ข้อ 1–6 + BRIEF.md ข้อ 1–7 + CLAUDE.md กฎเหล็ก #3/#4A
> สถานะไฟล์: **เขียนทีละส่วน (append)** — ส่วนที่ยังว่างแปลว่ายังไม่ถึง ไม่ใช่ "ไม่พบอะไร"

## 0. ขอบเขต / วิธีตรวจ
- เปิดไฟล์จริงทุกจุด (sed -n) — ไม่อ้างจากชื่อเมธอด
- ไฟล์หลัก: `wwwroot/pages/document-scan.html` · `wwwroot/pages/documents.html` ·
  `Services/Implementations/OcrService.cs` (`MapToResponse`, `CreateDocumentFromScanAsync`, `SubmitCorrectionAsync`) ·
  `Models/DTOs/*Ocr*` · `Controllers/OcrController.cs` · `mobile-expense.html` · `LineBotService.cs` · `review-queue.html`
- checker ที่รัน: js_dup_method_check · localstorage_key_check · dead_link_check · html_attr_escape_check

