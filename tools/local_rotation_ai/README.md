# FunctionGemma RPR/BLM/VPR supervisor and offline MCH teacher

This optional loopback-only service lets FunctionGemma choose between legal RPR, BLM, or VPR candidates already produced by BossMod. VPR supervision is limited to normal single-target and area combo actions; Reawaken, Generation, Legacy, Serpent's Ire, potion, movement recovery, and other safety-critical choices remain deterministic. The service still accepts MCH requests for offline teacher evaluation, but live MCH selection runs entirely in-process through `MchRealtimeValuePlanner` and never waits for HTTP or Python inference. SAM always keeps the deterministic baseline because its same-priority candidates are not semantically interchangeable without the full route, burst, downtime, target-shape, and positional state. BossMod never waits for the service: a missing, late, stale, invalid, or low-confidence response keeps the existing rotation choice.

The MCH planner evaluates only legal Drill and Air Anchor conflicts from level 76 onward. It simulates three GCDs, including future Chain Saw and Excavator opportunities, applies a small job-specific value model to damage, Heat, Battery, combo retention, gauge overcap, cooldown progress, and the existing queue priority, and keeps the deterministic baseline unless the projected gain clears its safety margin. Wildfire, Hypercharge, Barrel Stabilizer, Queen, Excavator, Full Metal Field, active Reassemble, and active burst statuses bypass the planner.

1. Accept the license for `google/functiongemma-270m-it` on Hugging Face and authenticate with `huggingface-cli login` or `HF_TOKEN`.
2. Start the local service:

```powershell
.\tools\local_rotation_ai\start-functiongemma.ps1
```

3. Set the endpoint for the Windows user, then restart Dalamud/BossMod:

```powershell
[Environment]::SetEnvironmentVariable('BOSSMOD_FUNCTIONGEMMA_ENDPOINT', 'http://127.0.0.1:8089/decide', 'User')
```

Optional bounds:

- `BOSSMOD_FUNCTIONGEMMA_TIMEOUT_MS`: 50-500, default 150.
- `BOSSMOD_FUNCTIONGEMMA_MIN_CONFIDENCE`: 50-100, default 95.
- `BOSSMOD_FUNCTIONGEMMA_MAX_PRIORITY_LOSS`: 0-10, default 0. Keep this at 0 until a fine-tuned model passes the applicable job regression and real harness.

Remove `BOSSMOD_FUNCTIONGEMMA_ENDPOINT` to disable the supervisor completely.
