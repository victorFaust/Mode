"""HTTP contract (called only by the Mode API's job worker):

POST /v1/process  (header X-Processor-Key)
  {"versionId", "extension", "contentType", "sourceUrl", "thumbnailUploadUrl", "previewUploadUrl"}
  -> {"supported", "thumbnail", "preview", "text"}

The service holds no database or storage credentials. It reads the original and writes PNG previews through
short-lived signed URLs on the Mode API, and refuses URLs outside MODE_API_BASE_URL.
"""

from __future__ import annotations

import hmac
import logging
import os

import httpx
from fastapi import FastAPI, Header, HTTPException
from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel

from app.processing import ProcessingError, process

logger = logging.getLogger("file-processor")
app = FastAPI(title="Mode file processor", version="0.2.0")

MAX_SOURCE_BYTES = 110 * 1024 * 1024
HTTP_TIMEOUT = httpx.Timeout(60.0, connect=10.0)


class CamelModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class ProcessRequest(CamelModel):
    version_id: str
    extension: str
    content_type: str
    source_url: str
    thumbnail_upload_url: str
    preview_upload_url: str


class ProcessResponse(CamelModel):
    supported: bool
    thumbnail: bool
    preview: bool
    text: str | None


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/v1/process", response_model=ProcessResponse, response_model_by_alias=True)
def process_file(request: ProcessRequest, x_processor_key: str = Header(default="")) -> ProcessResponse:
    expected_key = os.environ.get("PROCESSOR_KEY", "")
    if not expected_key or not hmac.compare_digest(x_processor_key.encode(), expected_key.encode()):
        raise HTTPException(status_code=401, detail="Invalid processor key.")

    allowed_prefix = os.environ.get("MODE_API_BASE_URL", "").rstrip("/") + "/internal/files/"
    urls = (request.source_url, request.thumbnail_upload_url, request.preview_upload_url)
    if allowed_prefix == "/internal/files/" or not all(url.startswith(allowed_prefix) for url in urls):
        raise HTTPException(status_code=400, detail="URLs must point at the Mode API.")

    data = fetch(request.source_url)
    try:
        result = process(data, request.extension)
    except ProcessingError as error:
        logger.warning("Could not process version %s: %s", request.version_id, error)
        return ProcessResponse(supported=False, thumbnail=False, preview=False, text=None)

    if result.thumbnail:
        upload(request.thumbnail_upload_url, result.thumbnail)
    if result.preview:
        upload(request.preview_upload_url, result.preview)
    return ProcessResponse(
        supported=result.supported,
        thumbnail=result.thumbnail is not None,
        preview=result.preview is not None,
        text=result.text,
    )


def fetch(url: str) -> bytes:
    with httpx.Client(timeout=HTTP_TIMEOUT, follow_redirects=False) as client, client.stream("GET", url) as response:
        response.raise_for_status()
        chunks, total = [], 0
        for chunk in response.iter_bytes():
            total += len(chunk)
            if total > MAX_SOURCE_BYTES:
                raise HTTPException(status_code=413, detail="Source file is too large.")
            chunks.append(chunk)
        return b"".join(chunks)


def upload(url: str, content: bytes) -> None:
    with httpx.Client(timeout=HTTP_TIMEOUT, follow_redirects=False) as client:
        client.put(url, content=content, headers={"Content-Type": "image/png"}).raise_for_status()
