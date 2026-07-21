# Thai Accounting OCR+AI Service

ระบบ OCR + AI สำหรับเอกสารบัญชีภาษาไทย — ฟรี 100%, Self-hosted

## Architecture

```
PaddleOCR (Thai) → Typhoon 2 LLM (via Ollama) → Structured JSON
```

## Quick Start

### 1. ติดตั้งด้วย Docker (แนะนำ)

```bash
cd ocr-service
docker compose up -d
```

### 2. ดาวน์โหลด AI Model (ครั้งแรก)

```bash
docker exec -it ocr-service-ollama-1 ollama pull typhoon2:8b
```

### 3. ทดสอบ

```bash
curl http://localhost:8501/health
curl -X POST http://localhost:8501/ocr/extract -F "file=@invoice.png"
```

## ติดตั้งแบบ Manual (ไม่ใช้ Docker)

```bash
# 1. ติดตั้ง Python dependencies
pip install -r requirements.txt

# 2. ติดตั้ง Ollama
curl -fsSL https://ollama.com/install.sh | sh
ollama pull typhoon2:8b

# 3. รัน service
uvicorn app.main:app --host 0.0.0.0 --port 8501
```

## Configuration (.NET App)

เพิ่มใน `appsettings.json`:

```json
{
  "Ocr": {
    "Provider": "local",
    "LocalServiceUrl": "http://localhost:8501"
  }
}
```

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | ตรวจสถานะ OCR + AI |
| POST | `/ocr/extract` | อัพโหลดไฟล์ → ดึงข้อมูลอัตโนมัติ |
| POST | `/ocr/correct` | ส่งการแก้ไขเพื่อ train model |
| GET | `/ocr/training/stats` | ดูสถิติ training data |
| POST | `/ocr/training/export` | Export dataset สำหรับ fine-tune |

## Learning Pipeline

1. ผู้ใช้แก้ไขผล OCR → ระบบบันทึกเป็น training data
2. สะสม corrections → Export เป็น JSONL
3. Fine-tune Typhoon ด้วย dataset ที่สะสม → ผลแม่นขึ้น

## Hardware Requirements

- **CPU only**: PaddleOCR ~2-3s/page, Typhoon 7B (4-bit) ต้อง RAM 16GB+
- **GPU (recommended)**: OCR <0.5s/page, LLM inference ~3-5s

## Supported Document Types

- ใบกำกับภาษี (Tax Invoice)
- ใบเสร็จรับเงิน (Receipt)
- ใบแจ้งหนี้ (Invoice)
- ใบสั่งซื้อ (Purchase Order)
- หนังสือรับรองหักภาษี 50 ทวิ (WHT Certificate)
- ใบลดหนี้ / ใบเพิ่มหนี้ (Credit Note / Debit Note)
