# Active generation protection

Branch: `feature/protect-active-ai-generation-2026-09-20-064224`

Base: latest fetched `origin/master-caio`, `d0f3025a6909c082e35142f924b56ece1b827149`.
The branch timestamp was read from local time when the branch was created. Work uses the existing checkout.

## Lifecycle and architecture audit

The existing Gen workspaces had independent loading flags but no shared active-operation tracker.
`ActiveGenerationGuard` is owned by the Gen page and cascaded to its generation components.
Async disposable operation leases cover provider requests, result persistence, and client state updates.
Operations retain their modal owner separately from its visibility. Only a visible modal with its own
active operation can trigger warnings; page-only/background work never blocks leaving. Overlapping
and nested operations in visible modals remain protected until their last lease finishes. Exceptions, quota
rejections, and early returns release their leases. Read-only saved-image re-entry does not acquire one.

| Workflow | Classification | Protected lifetime |
| --- | --- | --- |
| Website and automatic fallback image | Client-dependent | Provider generation through saved results and canonical image publication |
| Social, Print, Events | Client-dependent | Generation and reference images through persistence |
| Branding, Package Label, Digital Marketing | Client-dependent | Generation and reference images through persistence |
| Main/secondary images and initial concepts | Client-dependent | Text/image preparation, provider calls, saved images, and state update |
| AI recommendations and supplemental commercial images | Client-dependent | Generation through saved images and parent state publication |
| Commercial Video | Client-dependent end-to-end | Script, CTA, narration previews, scene work, final composition, and final URL persistence |
| Independent durable server video queues | Durable server jobs | Not tracked merely because a job remains queued or polling server-side |

Commercial Video's `FinalizeVideo` calls `CallRunwareFinalizeSceneVideo`, then performs narration,
composition (`ComposeFinalCommercialVideo`), and `SaveFinalVideoUrlAsync` from the client.
The existence of other queue endpoints does not make this full workflow durable. A submitted provider
request can still survive abandonment; the warning deliberately says the generation **may** not complete.
Leaving does not assert cancellation, invoke refunds, or repeat provider calls.

## Exit handling

- X, Close, Return, and Cancel confirm only if the specific modal being closed owns active generation.
  A different modal's generation does not block closing an idle modal.
- Closing backdrops use that same handler; existing disabled backdrops remain disabled.
- Existing custom generation workspaces have no independent Escape-close handler. The confirmation
  uses the same native-dialog helper as DesignMode's AI Credits modal: focus containment/restoration,
  a labelled title/message, autofocus on the safe action, and Escape mapped to staying.
- Blazor `RegisterLocationChangingHandler` waits for Stay Here / Leave Anyway. A cancelled navigation
  and page disposal dismiss pending decisions. One approval never approves multiple queued exits.
- A small idempotent `beforeunload` helper installs the native browser warning only while a visible
  modal owns active generation. No open generating modal means no navigation or unload warning.
  Browsers supply the native warning text and control whether it is displayed.
- Closing or disposing the owning modal removes its warnings without cancelling its work. Reopening
  it while generation still runs restores protection. Leaving the page disposes the guard,
  navigation registration, subscriptions, and dialog interop reference.
- The confirmation is centered in the viewport, with spaced actions and a blurred, dimmed backdrop.

## Short-term screen wake protection

The browser helper also requests a screen wake lock while a visible generation modal owns active
work. It shares the existing guard state, so completion, failure, closing the modal, or disposing
the page releases it. Backgrounding releases the lock; returning to the visible page requests a
new one if the same generation is still protected. Pending requests that resolve after closing are
released immediately, and repeated state notifications cannot create duplicate requests.

This is best-effort browser support in secure contexts. Unsupported browsers, battery restrictions,
or denied requests do not interrupt generation or cause retry loops. It prevents automatic screen
sleep when granted; it does not guarantee execution after manual locking or switching apps.
No provider, credits, or generation-persistence behavior changed.

## Validation

No browser/UI QA, deployment, merge, or provider calls were performed.

- Focused guard/navigation/Website component tests: **32 passed**.
- JavaScript unload/wake-lock unit tests in Node's VM: **9 passed**, no browser involved.
- Full .NET suite: **807 passed, 5 failed, 18 skipped** (830 total).
- The five failures are in unchanged code: four cases of
  `FunnelRecoveryVisualTests.Sequence_UsesResponsiveSharedLayoutAndSafeResume`, and
  `GenWorkspaceModalLayoutTests.WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable`
  (the existing `max-width: 800px !important` assertion). Recovery templates, their tests, and
  `wwwroot/css/app.css` are unchanged from the base. These failures were not repaired.
- External/local-service integration tests retain their existing opt-in gates; skipped tests were
  not reported as passing.
- Client and Server builds passed with `-p:CompressionEnabled=false`. The default SDK 10 build
  hits `ApplyCompressionNegotiation: Endpoints not found ... AWSSDK.Core.wasm`; the command-line
  override avoids it without changing project or production configuration. Existing warnings remain.
- `git diff --check` passed.

Commands:

```powershell
dotnet build DesignMode.AI.Client/DesignMode.AI.Client.csproj --no-restore -p:CompressionEnabled=false -v quiet
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj --no-restore -p:CompressionEnabled=false -v quiet
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore -p:CompressionEnabled=false --logger 'console;verbosity=normal'
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-build --filter 'FullyQualifiedName~GenerationGuard|FullyQualifiedName~GenerationNavigationGuard|FullyQualifiedName~WebsiteImageModalComponentTests'
node --preserve-symlinks-main --test DesignMode.AI.Test/js/generation-unload-guard.test.cjs
node --preserve-symlinks-main --test DesignMode.AI.Test/js/generation-wake-lock.test.cjs
git diff --check
```

## SHARED FILE RISK

`Gen.razor.cs` has two surgical lifetime insertions for initial generation and commercial preparation.
`Gen.razor` supplies the page guard/cascade and tracks directly requested images. Shared workspace
components have small lifecycle/close hooks; they retain their prior busy-close fallback outside Gen.
The added partial file owns guard disposal. No provider prompts/models/prices, usage values, credits
rules, persistence contracts, database schema, environment variables, or production configuration changed.

## Manual QA checklist for later — not executed

1. Website: start generation, click X, choose Wait for Generation, verify progress continues without
   another provider request. Click X again, choose Leave Anyway, verify the modal closes.
2. Repeat with footer Cancel/Return and backdrop; backdrop must warn or remain safely disabled.
3. Repeat for Social, Print, Events, Branding, Package Label, Digital Marketing, recommendations,
   automatic Website fallback images, and main/secondary image generation.
4. Commercial Video: verify protection during script, narration, scene generation, composition,
   and final save. An unrelated durably queued server job must not keep the guard active.
5. Start overlapping operations. Finish one and verify protection remains until the other finishes.
   Close an idle modal while another generates: no confirmation should appear for the idle modal.
   Close the last generating modal via Leave Anyway, then navigate/refresh: no warning should remain.
6. Navigate using Dashboard, Chat, Settings, another project, the logo, and SPA back. Stay Here must
   preserve the page and progress; Leave Anyway must continue the requested navigation.
7. Refresh, close the tab/window, and enter another URL during active generation. Verify the native
   browser warning where supported, and that its proceed action works. Native wording is browser-owned.
8. On success, failure, validation rejection, and HTTP 429, verify no stale warnings remain once no
   operations are active. Re-enter Gen and confirm there are no duplicate handlers.
9. On desktop, tablet, and mobile, check readable text, no horizontal overflow, accessible buttons,
   and viewport fit. Verify focus starts on Wait/Stay, Tab remains in the dialog, Escape stays,
   and focus returns to the invoking control.
10. Verify generated outputs, persistence, AI Credits, and usage records are unchanged; closing
    must neither refund nor duplicate generation/charges.
11. On a supported mobile browser over HTTPS, start generation with its modal open and leave the
    screen untouched past the normal auto-lock time. Check release after completion or modal close,
    and reacquisition after switching away and back while generation remains active. Denied wake
    locks must leave the existing generation and warning behavior usable.
