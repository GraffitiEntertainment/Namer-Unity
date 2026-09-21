---
schema_version: 1
open_count: 4
waived_count: 0
fixed_count: 0
total_count: 4
last_updated: 2026-09-06T17:16:07.300Z
---

# Broken Windows Ledger

> Cross-phase defect register. `/gsd-ship` blocks while `open_count > 0`.
> Waive with `gsd-tools windows waive <id> "<reason>"` (reason required).
> Mark fixed with `gsd-tools windows fixed <id>`.

| id | phase | kind | file | line | description | status | reason | recorded_at | resolved_at |
|----|-------|------|------|------|-------------|--------|--------|-------------|-------------|
| 1 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessExtractionTests.cs |  | Headless GPU round-trip run delegated to orchestrator in-editor TestRunnerApi (live editor holds project lock); tests authored but not yet executed | resolved | executed-live-2026-09-14 | 2026-09-06T16:17:40.236Z | 2026-09-15T01:30:00Z |
| 2 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs |  | Headless GPU fit-driven tests (FitDriven_SelectsFirstPassingStrength / FitDriven_BakedResponse_ResidualCollapses / FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run | resolved | executed-live-2026-09-14 | 2026-09-06T17:01:39.922Z | 2026-09-15T01:30:00Z |
| 3 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs |  | EditMode smoke (foldout defaults + DebugChannelLabels index-10 pairing) authored but not executed headless (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run | resolved | executed-live-2026-09-14 | 2026-09-06T17:01:47.864Z | 2026-09-15T01:30:00Z |
| 4 | 04.1 | unrun-verify | Packages/com.graffitientertainment.namer/Tests/Editor/NamerOneTextureTests.cs |  | Headless GPU acceptance tests (OneTexture_ResidualDropped_LeavesBaseResidualMapUnbound / RoughnessOffset_Unset_DecodesIdentical / HonestGate_AlbedoDetail_StillRequiresResidual / RoughnessOffset_Set_BindsMaterialTexture) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run | resolved | executed-live-2026-09-14 | 2026-09-06T17:16:07.300Z | 2026-09-15T01:30:00Z |

````json
[
  {
    "id": 1,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessExtractionTests.cs",
    "line": null,
    "description": "Headless GPU round-trip run delegated to orchestrator in-editor TestRunnerApi (live editor holds project lock); tests authored but not yet executed",
    "status": "resolved",
    "reason": "Executed in the live editor via TestRunnerApi (unity-mcp RunCommand, file-channel results): four-suite gate 12/12 (Temp/namer-gate-results.txt) + full EditMode regression 118/118 (Temp/namer-regression2-results.txt), 2026-09-14",
    "recorded_at": "2026-09-06T16:17:40.236Z",
    "resolved_at": "2026-09-15T01:30:00Z"
  },
  {
    "id": 2,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs",
    "line": null,
    "description": "Headless GPU fit-driven tests (FitDriven_SelectsFirstPassingStrength / FitDriven_BakedResponse_ResidualCollapses / FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run",
    "status": "resolved",
    "reason": "Executed in the live editor via TestRunnerApi (unity-mcp RunCommand, file-channel results): four-suite gate 12/12 (Temp/namer-gate-results.txt) + full EditMode regression 118/118 (Temp/namer-regression2-results.txt), 2026-09-14",
    "recorded_at": "2026-09-06T17:01:39.922Z",
    "resolved_at": "2026-09-15T01:30:00Z"
  },
  {
    "id": 3,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs",
    "line": null,
    "description": "EditMode smoke (foldout defaults + DebugChannelLabels index-10 pairing) authored but not executed headless (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run",
    "status": "resolved",
    "reason": "Executed in the live editor via TestRunnerApi (unity-mcp RunCommand, file-channel results): four-suite gate 12/12 (Temp/namer-gate-results.txt) + full EditMode regression 118/118 (Temp/namer-regression2-results.txt), 2026-09-14",
    "recorded_at": "2026-09-06T17:01:47.864Z",
    "resolved_at": "2026-09-15T01:30:00Z"
  },
  {
    "id": 4,
    "kind": "unrun-verify",
    "phase": "04.1",
    "file": "Packages/com.graffitientertainment.namer/Tests/Editor/NamerOneTextureTests.cs",
    "line": null,
    "description": "Headless GPU acceptance tests (OneTexture_ResidualDropped_LeavesBaseResidualMapUnbound / RoughnessOffset_Unset_DecodesIdentical / HonestGate_AlbedoDetail_StillRequiresResidual / RoughnessOffset_Set_BindsMaterialTexture) authored + capability-gated but not executed (live editor holds project lock); delegated to orchestrator in-editor TestRunnerApi run",
    "status": "resolved",
    "reason": "Executed in the live editor via TestRunnerApi (unity-mcp RunCommand, file-channel results): four-suite gate 12/12 (Temp/namer-gate-results.txt) + full EditMode regression 118/118 (Temp/namer-regression2-results.txt), 2026-09-14",
    "recorded_at": "2026-09-06T17:16:07.300Z",
    "resolved_at": "2026-09-15T01:30:00Z"
  }
]
````
