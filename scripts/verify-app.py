"""Verify a running app without contacting Anthropic: python scripts/verify-app.py [URL]."""
import hashlib
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5013").rstrip("/")
ROOT = Path(__file__).resolve().parents[1]
checks = []


def check(condition, name):
    assert condition, name
    checks.append(name)
    print(f"PASS: {name}")


def request(path, body=None, content_type="application/json", origin=None):
    headers = {"Content-Type": content_type}
    if origin:
        headers["Origin"] = origin
    payload = body.encode() if isinstance(body, str) else json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=payload, headers=headers)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    data = response.read().decode()
    return response.status, json.loads(data) if "application/json" in response.headers.get("Content-Type", "") else data


code, lesson = request("/api/lesson")
check(code == 200 and len(lesson["cases"]) == 2, "lesson metadata and original scenarios")
for scenario in lesson["cases"]:
    code, result = request("/api/run", scenario["input"], origin=BASE)
    check(code == 200 and result["outcome"] == scenario["expected"], f"API scenario: {scenario['label']}")
    check(result["source"] == "local-experiment" and result["id"], "API run identifier and source")
    if result["outcome"] == "passed":
        evidence = result["evidence"]
        check(evidence["selectedEvents"] == 2 and evidence["branchContext"] == "Project Aurora\nApproved scope", "API reviewed boundary")
        check(evidence["snapshot"]["savedHash"] == hashlib.sha256(b"reviewed draft").hexdigest(), "API snapshot hash")
code, source = request("/api/source")
check(code == 200 and "public static ExperimentResult Run" in source, "embedded C# source endpoint")
for body in ("[]", "null", "{", '{"boundary":123}', json.dumps({"boundary": "e2", "savedText": "x" * 20001})):
    check(request("/api/run", body)[0] == 400, "API invalid input rejected")
check(request("/api/run", {}, origin="https://example.invalid")[0] == 403, "foreign origin rejected")
check(request("/api/run", {}, content_type="text/plain")[0] == 415, "non-JSON input rejected")
check(request("/api/run", json.dumps({"boundary": "x" * 70000}))[0] == 413, "oversized request rejected")
if not lesson["liveConfigured"]:
    code, error = request("/api/live", lesson["cases"][0]["input"])
    check(code == 400 and "Configure" in error["error"], "unconfigured live request stays local")

code, live_scenarios = request("/api/session-scenarios")
check(code == 200 and len(live_scenarios["scenarios"]) == 8, "live scenario catalog exposes all eight workflows")
check(request("/api/session-scenarios/run", {"action": "branch", "sessionId": "test", "boundaryEventId": "missing"})[0] == 400, "live API requires reviewed context before creation")
check(request("/api/session-scenarios/run", {"action": "unknown"})[0] == 400, "live API rejects unknown scenarios")
check(request("/api/session-scenarios/run", {}, origin="https://example.invalid")[0] == 403, "live API rejects foreign origin")
check(request("/api/session-scenarios/run", {}, content_type="text/plain")[0] == 415, "live API requires JSON")

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={"width": 1440, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE, wait_until="networkidle")
    expect(page.get_by_role("heading", name=lesson["title"], exact=True)).to_be_visible()
    page.get_by_role("button", name="Run local experiment").click()
    expect(page.locator(".outcome")).to_have_text("passed")
    expect(page.get_by_role("status")).to_contain_text("recorded")
    check("selectedEvents" in page.locator(".results pre").inner_text(), "Blazor valid scenario renders evidence")
    output = ROOT / "verification"
    output.mkdir(exist_ok=True)
    page.screenshot(path=str(output / "desktop.png"), full_page=True)
    page.get_by_label("Scenario", exact=True).select_option("1")
    page.get_by_role("button", name="Run local experiment").click()
    expect(page.locator(".outcome")).to_have_text("rejected")
    check(True, "Blazor missing boundary scenario")
    page.get_by_label("Boundary", exact=True).fill("e3")
    page.get_by_label("Current text", exact=True).fill("café\nreviewed")
    page.get_by_label("Saved text", exact=True).fill("café\nreviewed")
    page.get_by_role("button", name="Run local experiment").click()
    expect(page.locator(".outcome")).to_have_text("passed")
    evidence = json.loads(page.locator(".results pre").inner_text())
    check(evidence["selectedEvents"] == 3 and not evidence["snapshot"]["needsRestore"], "Blazor edited multiline UTF-8 inputs")
    page.get_by_role("button", name="Switch to dark mode").click()
    expect(page.locator("html")).to_have_attribute("data-theme", "dark")
    page.get_by_role("link", name="What you will learn", exact=True).click()
    expect(page.locator("#learning")).to_be_visible()
    expect(page.locator("html")).to_have_attribute("data-theme", "dark")
    page.get_by_role("link", name="How to run", exact=True).click()
    expect(page.locator("#run-guide")).to_be_visible()
    page.get_by_role("link", name="Architecture & workflow", exact=True).click()
    expect(page.get_by_role("heading", name="From scenario to evidence")).to_be_visible()
    check(page.locator("img.architecture").evaluate("img => img.complete && img.naturalWidth > 0"), "architecture diagram loads")
    check(True, "teaching navigation and persisted dark theme")
    page.get_by_role("link", name="Application lab", exact=True).click()
    expect(page.get_by_role("heading", name=lesson["title"], exact=True)).to_be_visible()
    page.set_viewport_size({"width": 390, "height": 844})
    check(page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "mobile layout has no horizontal overflow")
    page.screenshot(path=str(output / "mobile.png"), full_page=True)
    page.get_by_role("link", name="Live sessions", exact=True).click()
    expect(page.get_by_role("heading", name="Live session scenarios", exact=True)).to_be_visible()
    check(page.get_by_label("Live scenario", exact=True).locator("option").count() == 8, "live page has eight scenario choices")
    if live_scenarios["liveConfigured"]:
        page.get_by_label("Live scenario", exact=True).select_option("branch")
        expect(page.get_by_role("button", name="Create reviewed branch")).to_be_disabled()
        check(True, "live branch creation requires a reviewed preview")
    else:
        expect(page.get_by_role("heading", name="Configure live access", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Run live scenario")).to_be_disabled()
        check(True, "live page explains missing credentials and disables provider calls")
    check(page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "live page fits mobile viewport")
    page.screenshot(path=str(output / "live-sessions-mobile.png"), full_page=True)
    check(not errors, "browser has no JavaScript errors")
    browser.close()

journal = ROOT / ".lab-data" / "runs.jsonl"
records = [json.loads(line) for line in journal.read_text(encoding="utf-8").splitlines()]
check(all(record["lesson"] == 13 and record["result"]["source"] == "local-experiment" for record in records), "local journal stores experiment results")
(ROOT / "verification" / "status.json").write_text(json.dumps({"url": BASE, "passed": len(checks), "checks": checks, "liveProviderTested": False}, indent=2), encoding="utf-8")
print(f"{len(checks)} checks passed. No provider requests were made.")
