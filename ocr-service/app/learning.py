"""Learning module: stores corrections to improve future extractions."""
import json
import os
import logging
from datetime import datetime
from pathlib import Path

logger = logging.getLogger(__name__)

TRAINING_DIR = Path(os.environ.get("TRAINING_DATA_DIR", "/app/training_data"))


def save_correction(original_text: str, original_result: dict, corrected_result: dict, document_type: str | None = None):
    """Save a user correction as training data for future fine-tuning."""
    TRAINING_DIR.mkdir(parents=True, exist_ok=True)

    entry = {
        "timestamp": datetime.utcnow().isoformat(),
        "document_type": document_type,
        "input_text": original_text,
        "original_output": original_result,
        "corrected_output": corrected_result,
    }

    filename = f"correction_{datetime.utcnow().strftime('%Y%m%d_%H%M%S_%f')}.json"
    filepath = TRAINING_DIR / filename

    with open(filepath, "w", encoding="utf-8") as f:
        json.dump(entry, f, ensure_ascii=False, indent=2)

    logger.info(f"Saved correction: {filepath}")
    return str(filepath)


def get_training_stats() -> dict:
    """Get statistics about collected training data."""
    TRAINING_DIR.mkdir(parents=True, exist_ok=True)

    files = list(TRAINING_DIR.glob("correction_*.json"))
    if not files:
        return {"total_corrections": 0, "by_type": {}}

    by_type: dict[str, int] = {}
    for f in files:
        try:
            with open(f, "r", encoding="utf-8") as fp:
                data = json.load(fp)
                doc_type = data.get("document_type") or "unknown"
                by_type[doc_type] = by_type.get(doc_type, 0) + 1
        except Exception:
            pass

    return {
        "total_corrections": len(files),
        "by_type": by_type,
    }


def export_for_finetuning(output_path: str | None = None) -> str:
    """Export corrections in JSONL format suitable for LLM fine-tuning."""
    TRAINING_DIR.mkdir(parents=True, exist_ok=True)

    if not output_path:
        output_path = str(TRAINING_DIR / "finetune_dataset.jsonl")

    files = sorted(TRAINING_DIR.glob("correction_*.json"))
    count = 0

    with open(output_path, "w", encoding="utf-8") as out:
        for f in files:
            try:
                with open(f, "r", encoding="utf-8") as fp:
                    data = json.load(fp)

                prompt = f"วิเคราะห์เอกสาร:\n{data['input_text']}"
                response = json.dumps(data["corrected_output"], ensure_ascii=False)

                entry = {
                    "messages": [
                        {"role": "system", "content": "คุณเป็น AI วิเคราะห์เอกสารบัญชีไทย ตอบเป็น JSON เท่านั้น"},
                        {"role": "user", "content": prompt},
                        {"role": "assistant", "content": response},
                    ]
                }
                out.write(json.dumps(entry, ensure_ascii=False) + "\n")
                count += 1
            except Exception:
                pass

    logger.info(f"Exported {count} entries to {output_path}")
    return output_path
