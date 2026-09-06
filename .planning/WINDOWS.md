---
schema_version: 1
open_count: 3
waived_count: 0
fixed_count: 0
total_count: 3
last_updated: 2026-09-06T17:01:47.864Z
---

# Broken Windows Ledger

> Cross-phase defect register. `/gsd-ship` blocks while `open_count > 0`.
> Waive with `gsd-tools windows waive <id> "<reason>"` (reason required).
> Mark fixed with `gsd-tools windows fixed <id>`.

| id | phase | kind | file | line | description | status | reason | recorded_at | resolved_at |
|----|-------|------|------|------|-------------|--------|--------|-------------|-------------|
| 1 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessExtractionTests.cs |  | Headless GPU round-trip run delegated to orchestrator in-editor TestRunnerApi (live editor holds project lock); tests authored but not yet executed | open |  | 2026-09-06T16:17:40.236Z |  |
| 2 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs |  | Headless GPU fit-driven tests (FitDriven_SelectsFirstPassingStrength / FitDriven_BakedResponse_ResidualCollapses / FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run | open |  | 2026-09-06T17:01:39.922Z |  |
| 3 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs |  | EditMode smoke (foldout defaults + DebugChannelLabels index-10 pairing) authored but not executed headless (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run | open |  | 2026-09-06T17:01:47.864Z |  |

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
  },
  {
    "id": 2,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs",
    "line": null,
    "description": "Headless GPU fit-driven tests (FitDriven_SelectsFirstPassingStrength / FitDriven_BakedResponse_ResidualCollapses / FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-09-06T17:01:39.922Z",
    "resolved_at": null
  },
  {
    "id": 3,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs",
    "line": null,
    "description": "EditMode smoke (foldout defaults + DebugChannelLabels index-10 pairing) authored but not executed headless (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-09-06T17:01:47.864Z",
    "resolved_at": null
  }
]
````
