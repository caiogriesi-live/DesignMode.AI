# Website recommendation auto-image repair

Base: latest fetched `origin/master-caio`, `c8afc06b0b9aa9923dbfb42b7e87865dc45d8df5`.
Branch: `feature/fix-website-auto-image-state-2026-09-19-071644` (timestamp obtained with local `Get-Date`).
Diagnosis used source, Git history and automated tests; no browser QA or production calls.

## Diagnostic map of the original code

| Step | Expected | Actual on the base revision | Result |
| --- | --- | --- | --- |
| Recommendation click | Open Website preparation | With no parent-classified Website images, `OpenWebsiteModal` set `showWebsiteImageGenerationModal`; no component in Gen rendered that flag | FAIL: earliest break for a truly empty project |
| Modal lifecycle | Await preparation once | `async void OnParametersSet` ran outside the awaited Blazor lifecycle and cleared selections on every parameter update | FAIL |
| Availability | Same eligibility in parent and modal | Parent accepted prompt-based/digital/online matches; child accepted only web category labels | FAIL |
| Fallback metadata | Always classify the fallback as Website | `GoToGenerateImages` only assigned deliverable/subcategory inside `mainIdea != null`; without a Generic image both stayed empty | FAIL: first broken transition in the generation-completes-but-image-is-invisible path |
| Provider | Await one usable result | Existing Runware wrapper awaited the response and selected a nonempty URL, without checking task/image identity | Partial |
| Persistence | Await canonical ID and correct prompt association | `SaveImageIdea` awaited its insert and returned ID; child used `PromptIdFromCID`, which is loaded asynchronously after the `s=1` grid, and fabricated a GUID when save returned no ID | FAIL in client confirmation/identity readiness |
| Project association | ImageIdeas → Prompt → exact CompanyIdeaId/user | Canonical query uses that relationship; images with blank/wrong PromptId cannot be retrieved for the intended project | PASS for valid associations |
| Canonical reload | Await reload after save | Child appended a partial local object and mutated its parameter list; parent reload happened only on close | FAIL |
| Gen state | Rebuild canonical page collections | No immediate rebuild of grouped/main/secondary collections | FAIL |
| Modal and selection | Show and retain persisted image | Empty fallback categories were filtered out; subsequent parameters cleared selection | FAIL |
| Website request | Use selected canonical URL | Generator selected from the parameter collection by selected ID; missing/stale collection prevented it from resolving the fallback | FAIL downstream |

## Retrieval and history findings

- `GetImageIdeasByCompanyId` is the actual authenticated canonical read. It requires the exact URL-encoded `companyIdeaId`, resolves owned prompts, and uses strongly consistent reads for Prompt and ImageIdeas. It returns 404 when the CID has prompts but none belong to the authenticated owner. An empty owned project is not a 404. The fix does not turn failed reads into empty image lists.
- `GetImageIdeaCountsByCompanyId` now returns 200 with empty prompt/image data when no Prompt exists. Git history (`ecc3b70`) already replaced the old missing-prompt 404. The fallback no longer depends on this count endpoint or its first-prompt selection.
- `74660fb` already repaired image retrieval across all owned prompts. That fix remains intact. `20e3f2d` introduced the current fallback parameter/persistence handling and retained the empty-classification, fake-ID and local-list behavior. This is not a reversion of the owned-prompt query fix.
- No live 404 was reproduced. Source evidence proves the above status contracts, prompt-readiness race and missing association risks; it does not establish an environment/table mismatch. Both environments use the same APIs and DynamoDB client factory.

## Repair

`WebsiteImageState` coordinates the existing provider/save/reload delegates, with one in-flight task per exact CID. It does not add an endpoint, provider service, table or background job. It reloads canonical state first; a saved ID survives read/publication failures. Once a provider image exists, reopening can only retrieve it, not pay for another image. An ambiguous persistence failure stays a safe failure unless the canonical query subsequently finds the image.

`Gen.WebsiteImages.cs` resolves a prompt from the owned canonical image rows or an exact-CID prompt lookup using authenticated identity. It resolves canonical ProjectId/FunnelId through the existing `ResolveLegacyProject` API instead of another tab's stored active project. It captures the prompt before generation, persists against that prompt, guards publication against navigation, and reuses Gen's existing collection/grouping code with the returned canonical reload. No second image read is needed to apply those results.

The Website modal uses `OnParametersSetAsync`, keeps loading active through publication, selects the returned persisted image, and preserves selection on rerenders. Parent and child share eligibility. Website generation resolves the same CID and uses the selected canonical image URL.

The existing Runware call makes one job ID and requests one image. The fallback now uses the normal `gen_secondary_image` accounting feature, correcting its previous `social_image` classification (the default 3-credit social-asset charge). Its configured image rate applies only if an image is actually generated. Website credits remain on the explicit `GenerateWebsiteFromImage` request, and the usage summary counts only successful jobs. Opening/closing, reading images and refreshing the usage summary do not create usage records. The Website-specific result check requires its matching task UUID, provider image UUID and HTTP(S) URL. Save must return a real ID and canonical reload must return that ID with the captured PromptId. Quota enforcement, pricing and configured credit weights are unchanged.

**SHARED FILE RISK:** `Gen.razor` changes the Website opener/binding and allows its existing refresh implementation to apply an already-loaded canonical snapshot. `Gen.razor.cs` adds an optional explicit project context to the existing provider wrapper, validates Website provider identity, and replaces two sensitive log messages. Other callers keep their original behavior. No broad orchestration refactor.

## Automated verification

- New orchestration/component tests exercise existing-image reuse, one generation under concurrent actions, awaited persistence, provider validation/failure, persistence failure, canonical refresh failure/reopen, selection retention, loading/error rendering, project isolation, and the actual Gen page/modal/group collections.
- The canonical query regression verifies a Website image saved under Project B's prompt is returned for B and does not query A; all query reads must remain strongly consistent.
- Focused regression includes Website, initial main/secondary generation, image retrieval, usage, credits, quota, print and event prompt tests.
- Build workaround: this checkout's default client build fails in `ApplyCompressionNegotiation` on `AWSSDK.Core.wasm`; `-p:CompressionEnabled=false` allows validation without changing project configuration. The Functions build requires access to the user's NuGet configuration.
- Local DynamoDB integration tests remain opt-in; skipped tests do not constitute live persistence/accounting verification.

Final results:

- Focused gate: 165 passed, 4 local DynamoDB ledger tests skipped, zero failures. The new/extended Website tests all pass, including the actual Gen collection update.
- Full suite: 773 passed, 18 skipped, 5 failures. Four are `FunnelRecoveryVisualTests.Sequence_UsesResponsiveSharedLayoutAndSafeResume` (expects `background-size:contain`; baseline renderer uses `cover`). One is `GenWorkspaceModalLayoutTests.WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable` (baseline CSS contains `height: 90dvh`). The affected implementation and test files are byte-for-byte unchanged from the base Git revision; they were not repaired in this branch.
- Client and Server builds pass with `--no-restore -p:CompressionEnabled=false` (existing warnings remain). The final test build also rebuilt both project references successfully.
- `git diff --check` passes. No merge, deployment or browser/UI QA was performed.

Closing the Website modal reports a prepared image ID only when image preparation occurred. Gen checks that ID against its current canonical image collection before reloading. An existing-image open/close does nothing; an already-published generated image also requires no reload or updating-images overlay. A prepared image missing from Gen triggers the existing canonical refresh. Repeated close events are ignored. Component regressions cover both the missing-image refresh and already-published no-op cases.

Availability guard: when the modal already receives a usable Website image, it selects that image without invoking fallback preparation or showing image-generation loading. Both automatic preparation and the generation action check availability. The no-image path still checks canonical storage before calling the provider. A component regression verifies zero fallback calls and zero close refreshes across opening, rerendering and reopening with an existing image.

Section-placement correction: generic digital/online assets and incidental Website mentions in non-Website prompts no longer suppress fallback generation. Eligibility requires a Website category (or the structured Website deliverable slot on an older uncategorized fallback). Persistence now uses the existing Website section's exact label, including empty placeholder sections; a main Website deliverable receives a Generic image under its own deliverable name. Combined component regressions start with an empty modal, show/select the prepared image, close, and assert the same saved image appears in the exact main/secondary Website group and URL collection. The updated Website/image-query gate passes all 28 tests.

Credit/close-refresh follow-up: all 98 targeted Website, canonical image-query, AI usage-summary and credit tests pass. This includes the fallback's normal image rate, zero preparation/refresh calls for repeated existing-image opens/closes, and a no-network/no-overlay check when Gen already contains the prepared image. The explicit Website generation handler also rejects duplicate clicks while generation is already running.

## Manual QA for later — not performed

1. Open Website on a project with an existing usable image; verify no new provider request.
2. Open Website on an empty Project B while Project A also exists; verify one image appears on B's page and is selected in the modal.
3. Generate the Website and verify it uses that selected image.
4. Rerender/double-click/reopen during and after preparation; verify no duplicate image charge.
5. Simulate provider, persistence and refresh failures; verify safe errors and no false canonical image.
6. Restore retrieval after a post-save failure and reopen; verify the saved image is selected without another generation.
7. Check exhausted-credit behavior and unchanged main/secondary, Social, Print and Events flows.

### Plain-image credit correction
The shared quota mapping classified social_image as a full social asset (3 default units). It now uses AI_GENERATION_UNITS_IMAGE (0.1 default), including the shared Runware endpoint fallback and the Gen four-argument image delegate. Full social_post/social_text pricing remains unchanged. Regression coverage verifies five initial images produce 0.5 / 50 used on an unused Pro allowance. The reported existing 12.6 total has not been traced to individual ledger rows, and historical usage has not been changed.
