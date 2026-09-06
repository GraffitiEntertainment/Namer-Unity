---
schema_version: 1
open_count: 1
waived_count: 0
fixed_count: 0
total_count: 1
last_updated: 2026-09-06T16:17:40.236Z
---

# Broken Windows Ledger

> Cross-phase defect register. `/gsd-ship` blocks while `open_count > 0`.
> Waive with `gsd-tools windows waive <id> "<reason>"` (reason required).
> Mark fixed with `gsd-tools windows fixed <id>`.

| id | phase | kind | file | line | description | status | reason | recorded_at | resolved_at |
|----|-------|------|------|------|-------------|--------|--------|-------------|-------------|
| 1 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessExtractionTests.cs |  | Headless GPU round-trip run delegated to orchestrator in-editor TestRunnerApi (live editor holds project lock); tests authored but not yet executed | open |  | 2026-09-06T16:17:40.236Z |  |

````json
[
  {
    "id": 1,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessExtractionTests.cs",
    "line": null,
    "description": "Headless GPU round-trip run delegated to orchestrator in-editor TestRunnerApi (live editor holds project lock); tests authored but not yet executed",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-09-06T16:17:40.236Z",
    "resolved_at": null
  }
]
````
