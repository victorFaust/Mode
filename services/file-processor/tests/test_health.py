import pytest
from fastapi.testclient import TestClient

from app import main
from app.main import app

BASE = "http://api.test"
KEY = "test-key"


@pytest.fixture(autouse=True)
def environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PROCESSOR_KEY", KEY)
    monkeypatch.setenv("MODE_API_BASE_URL", BASE)


def request_body(**overrides: str) -> dict[str, str]:
    body = {
        "versionId": "v1",
        "extension": "txt",
        "contentType": "text/plain",
        "sourceUrl": f"{BASE}/internal/files/read",
        "thumbnailUploadUrl": f"{BASE}/internal/files/thumb",
        "previewUploadUrl": f"{BASE}/internal/files/preview",
    }
    body.update(overrides)
    return body


def test_health_returns_ok() -> None:
    assert TestClient(app).get("/health").json() == {"status": "ok"}


def test_requests_without_the_key_are_rejected() -> None:
    client = TestClient(app)

    assert client.post("/v1/process", json=request_body()).status_code == 401
    assert client.post("/v1/process", json=request_body(), headers={"X-Processor-Key": "wrong"}).status_code == 401


def test_urls_outside_the_mode_api_are_refused() -> None:
    response = TestClient(app).post(
        "/v1/process",
        json=request_body(sourceUrl="http://169.254.169.254/latest/meta-data"),
        headers={"X-Processor-Key": KEY},
    )

    assert response.status_code == 400


def test_processes_and_uploads_through_signed_urls(monkeypatch: pytest.MonkeyPatch) -> None:
    uploads: dict[str, bytes] = {}
    monkeypatch.setattr(main, "fetch", lambda url: b"hello from a file")
    monkeypatch.setattr(main, "upload", lambda url, content: uploads.__setitem__(url, content))

    response = TestClient(app).post("/v1/process", json=request_body(), headers={"X-Processor-Key": KEY})

    assert response.status_code == 200
    assert response.json() == {"supported": True, "thumbnail": False, "preview": False, "text": "hello from a file"}
    assert uploads == {}


def test_corrupt_input_reports_unsupported(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(main, "fetch", lambda url: b"not a png")

    response = TestClient(app).post("/v1/process", json=request_body(extension="png"), headers={"X-Processor-Key": KEY})

    assert response.json()["supported"] is False
