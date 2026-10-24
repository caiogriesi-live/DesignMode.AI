# Website fallback Image Idea prompt

## Follow-up: isolated-worker logger failure

The reported runtime stack trace showed `ArgumentNullException (logger)` at the new canonical-prompt diagnostic in `CallRunwareAPI`. Canonical resolution had succeeded, but the isolated worker supplied no invocation-level `ILogger`, so generation stopped before the provider request. The original endpoint tests masked this by passing `NullLogger.Instance` directly.

`CallRunwareAPI` now accepts only its HTTP trigger argument and uses the existing constructor-injected `_logger` for all of its diagnostics. Endpoint tests invoke that same signature. A focused regression captures the injected logger's canonical-source message and verifies that the provider is subsequently called exactly once, without logging the customer prompt. No prompt, provider, credit or modal behavior changes are part of this follow-up.

Follow-up validation: the test command rebuilt the Client and Server dependencies with `--no-restore -p:CompressionEnabled=false`; all **90** selected Website image, generation-authorization endpoint and Package Label image tests passed. `git diff --check` also passed. The running Functions host must reload the rebuilt backend before retrying.

Base commit: `4616f9269ba2f668560f0aeb25c941d2b479a91b` (local `master-caio`, equal to fetched `origin/master-caio`).
Branch: `feature/fix-website-image-idea-prompt-2026-09-24-053231`; timestamp obtained from local `Get-Date` at branch creation.
The pre-existing `wwwroot/index.html` change is excluded from this fix.

## Diagnosis before editing

This is a source trace, not a claim that a production customer's prompt was inspected.

| Step | Expected | Actual at base | Result |
| --- | --- | --- | --- |
| Website modal | One preparation operation when no usable Website image exists | `GoToGenerateImages` calls `EnsureWebsiteImage(cid)` under the existing loading/close guard | PASS |
| Current project | Exact route CID, authenticated canonical image retrieval | `RequireWebsiteProject`, `CallGetImageIdeasFunction`, and `GenProjectImagesQuery` scope reads to owned Prompt rows for that CID | PASS |
| Image Idea selection | Select the stored Website Image Idea | `EnsureWebsiteImageAsync` instead reads cached `IdeasCount.Ideas.FirstOrDefault(Title contains web)` before reloading canonical images | FAIL: first loss of canonical Image Idea |
| Prompt extraction | Use `ImageIdeas.ImagePrompt` | Builds a new pipe-delimited prompt using the recommendation Title/Description, or `promptText`, or hardcoded generic Website text; adds `CompanyDetails` and fetched deliverable details | FAIL |
| Persistence identity | Use the selected Website idea's PromptId | Takes the first nonempty PromptId from any image, or fetches a project PromptId independently | FAIL: not necessarily the source Website idea's PromptId |
| Client request | Preserve the selected prompt/identity | Sends the rebuilt text as `positivePrompt`, with no Image Idea identity | FAIL |
| Server/provider | Load canonical prompt and retain its visual intent | `CallRunwareAPI` trusts the client text, splits it into slots, and formats those slots into the Runware positive prompt; it does not retrieve ImageIdeas | FAIL for canonical authority; formatting does retain the supplied slots |
| Persistence, reload, selection | Save once, reload owned images, publish Gen collections, select in modal | Existing `WebsiteImageState` and modal flow already implement this | PASS; retained |

The error is prompt selection, not a new Website PAGE-generation prompt requirement. The unchanged image builder includes the brief, company/industry, company context and deliverable detail; Website deliverables also receive its existing device-screen instruction.

## Canonical representation and repair

`ImageIdeas` has `Id`, `DeliverableType`, `SubCategory`, `ImagePrompt`, `ImageUrl`, `PromptId`, `TimeStamp`, and `CountId`. Ownership/project association is `ImageIdeas.PromptId -> Prompt.Id -> Prompt.UserId/CompanyIdeaId`. There is no source-ImageIdeaId column. `Ideas.Title/Description` is a separate recommendation representation, not the saved structured image prompt.

Initial Gen generation already saves unrepresented secondary deliverables as ImageIdeas with an empty ImageUrl and a complete ImagePrompt. The Website fallback now selects from the freshly retrieved canonical rows, using the existing web deliverable/subcategory classification and numeric `CountId` ordering (invalid counts last). It validates the structured prompt's Website deliverable slot as well. Legacy unclassified rows are eligible only when their structured deliverable slot identifies Website. It preserves the selected idea's ImagePrompt and PromptId for the existing save operation.

The request carries `websiteImageIdeaId` and the route `companyIdeaId`. On this path, `CallRunwareAPI` uses authenticated identity, existing `GenerationProjectAuthorization`, and `GenProjectImagesQuery` to verify the project/CID relationship, ownership, Image Idea identity and Website classification. The server replaces the supplied prompt with the stored ImagePrompt before the unchanged provider prompt builder runs. It retains `gen_secondary_image` credit semantics and enforces one result. Missing, unrelated, or malformed Website ideas fail before provider/usage calls; no new prompt is invented. The canonical initial-generation placeholder creation flow is unchanged.

The spinner, generation guard, persistence endpoint, no-repeat state coordinator, canonical refresh and modal selection are unchanged. No schema changes, prompt rewrites, pricing changes or production calls are involved.

## Shared file risk

- `Gen.razor.cs`: only adds optional Website Image Idea/CID arguments and request fields to the existing Runware wrapper. Existing callers retain their arguments and behavior.
- `DesignModeFunctions.cs`: adds the conditional canonical Website lookup before the existing prompt builder, uses its authorized context for existing quota/accounting, and permits a fake HTTP transport for endpoint tests. The provider prompt builder and provider configuration are unchanged.
- Existing project authorization and canonical image-query implementations are reused without modification. Other recommendations never enter the Website lookup when the optional identity is null.

## Verification

Focused tests capture the actual `CallRunwareAPI` HTTP payload with a fake provider and DynamoDB client. They cover canonical prompt intent, rejected client prompt substitution, metadata selection, numeric ordering, missing/malformed ideas, cross-user/project/image rejection, HTTP 429, one provider request and one usage charge. State tests cover source PromptId/prompt persistence and publication; existing Website component tests cover Gen collections, modal selection and loading.

The default SDK 10 build encounters the pre-existing `ApplyCompressionNegotiation` error for `AWSSDK.Core.wasm`. Validation uses the already documented command-line `-p:CompressionEnabled=false` workaround. Functions builds also require access to the user's NuGet configuration. No broad browser QA or real paid provider calls are run.

Results:

- New `WebsiteImageIdeaPromptTests`: **26 passed**, including existing non-Website provider formatting for Branding, Social, Packaging, Print and Events.
- Requested regression selection: **313 passed, 2 failed, 4 skipped** (319 total). The two failures are unchanged base-revision assertions in `GenWorkspaceModalLayoutTests`: `MakeVideoModal.razor` lacks `modal-close`, and `app.css` contains `height: 90dvh`. Both the tested files and the assertions are unchanged from the base commit. They were not repaired in this branch.
- Four `AIGenerationUsageLocalIntegrationTests` cases skip without the opt-in DynamoDB Local environment. The new endpoint tests exercise successful usage recording and quota denial using the real accounting code with a fake database/provider.
- A diagnostic rerun inside the sandbox additionally prevented Node from resolving the Website landing-script test path (`EPERM`). That test passed in the original regression run with normal build access; no source change was made for this environment restriction.
- Client build: **passed**, 9 existing warnings, zero errors. Server build: **passed**, 19 existing warnings, zero errors. Both used `--no-restore -p:CompressionEnabled=false -v quiet`.
- `git diff --check`: **passed**.

Commands:

```powershell
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore -p:CompressionEnabled=false --filter 'FullyQualifiedName~WebsiteImageIdeaPromptTests'
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore -p:CompressionEnabled=false --filter 'FullyQualifiedName~Website|FullyQualifiedName~GenProjectImages|FullyQualifiedName~InitialGeneration|FullyQualifiedName~GenerationAuthorization|FullyQualifiedName~GenerationProjectAuthorization|FullyQualifiedName~AIRecommendation|FullyQualifiedName~AICredits|FullyQualifiedName~AIUsage|FullyQualifiedName~AIGenerationUsage|FullyQualifiedName~UsageLookup|FullyQualifiedName~OpenAIUsage|FullyQualifiedName~PackageLabel|FullyQualifiedName~PrintMaterial|FullyQualifiedName~EventActivation|FullyQualifiedName~GenerationGuard|FullyQualifiedName~GenerationNavigation|FullyQualifiedName~ActiveGeneration|FullyQualifiedName~GenWorkspace'
dotnet build DesignMode.AI.Client/DesignMode.AI.Client.csproj --no-restore -p:CompressionEnabled=false -v quiet
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj --no-restore -p:CompressionEnabled=false -v quiet
git diff --check
```
