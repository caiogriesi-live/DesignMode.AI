# Gen blank page on iPhone: diagnosis and fix

Base: `bcc62b50cc794de16be1d6e02bd0a9d095062456`.
Branch: `feature/fix-gen-ios-blank-page-2026-09-20-085156`.

## Proven cause

The dashboard renders, but its project link navigates to Gen, which introduces
`GenerationNavigationGuard`. Its native dialog had `@oncancel="Stay"
@oncancel:preventDefault`. The application's .NET 8 Razor metadata recognizes
`oncancel`, but does not support its `preventDefault` modifier. The modifier was
emitted as a literal DOM attribute.

WebKit throws `InvalidCharacterError: Invalid qualified name:
'@oncancel:preventDefault'` while applying the first render batch. Gen's
`#pageHeader`, image grid, navigation and guard dialog never reach the DOM.
The app root has zero height. Subsequent missing-component errors are consequences
of that first failure. Chromium accepts the attribute and renders the page.

This reproduces in desktop WebKit too: it is an engine difference, not a mobile
media query. `git blame` identifies commit `bcc62b5` (generation exit protection)
as introducing the unsupported modifier.

The fix removes only that modifier. The supported `oncancel` callback remains.
Escape dismisses the native dialog and resolves the pending exit as Stay;
generation continues. No layout, auth, query semantics, API, billing, generation,
or production configuration changes are needed.

## Startup evidence

Controlled local run: real Blazor client, synthetic local authentication, one
saved image fixture, intercepted API responses, dashboard project-link click.
No production data or live AI requests were used.

| Step | Expected | Before fix in WebKit | Result |
| --- | --- | --- | --- |
| Blazor boot and authentication | Dashboard loads | Fixture project card visible | PASS |
| Project link/query | `/init/gen?s=1&cid=fixture` | URL and CID correct | PASS |
| Gen initialization | Begin saved-image reads | ImageIdeas request observed | PASS |
| Initial render / guard dialog attributes | Insert valid DOM | Invalid qualified attribute exception | FIRST FAILURE |
| Workspace DOM | Root, navigation, images | `#pageHeader` absent; root height 0 | FAIL |
| Image/usage/supplemental reads | Populate workspace | Requests continue after renderer failure | Cannot repair render |
| Post-render guard interop | Import helper and set closed state | Render already aborted | Not the first failure |

After the fix, root opacity is 1, the saved fixture image decodes, AI Credits,
recommendations control and navigation are visible, and the closed guard has
`display: none`. No renderer exceptions occur. The native dialog uses no body
scroll-lock mutation. The fullscreen modal selectors target modal classes and
are not responsible for the blank page.

## Verification

| Engine | Viewport | Before | After blank-page gate |
| --- | --- | --- | --- |
| WebKit | 390 x 844 | FAIL: invalid attribute, no Gen DOM | PASS |
| WebKit | 430 x 932 | Not run | PASS |
| WebKit | 375 x 667 | Not run | PASS |
| WebKit | 1440 x 900 | FAIL: invalid attribute, no Gen DOM | PASS |
| Chromium | 1920 x 1080 | Not run | PASS |
| Chromium | 1440 x 900 | Gen renders | PASS |
| Chromium | 1366 x 768 | Not run | PASS |

The focused component test now renders the actual guard markup (previously the
test suppressed its render tree). It asserts no literal Razor directives reach
the DOM, verifies a compiled cancel callback, dispatches cancellation, and checks
that navigation is refused while generation remains active. The existing
navigation/disposal test also uses the actual markup.

The focused MSTest selection has 52 passes and one pre-existing failure:
`GenWorkspaceModalLayoutTests.WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable`
at line 45. Baseline before changes: 51 passes and that same failure.

```powershell
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-restore -p:CompressionEnabled=false --filter 'FullyQualifiedName~GenerationGuard|FullyQualifiedName~GenerationNavigationGuard|FullyQualifiedName~GenWorkspaceModal|FullyQualifiedName~GenProjectImages'
```

The installed SDK's static-web-asset compression task fails on the baseline.
`CompressionEnabled=false` is a command-line workaround for local verification;
no project setting was changed. The client builds successfully with that option.

There was no configured WebKit UI suite. The existing server publish directory
contained a Playwright runtime; WebKit was downloaded through that runtime for
this diagnosis. A small opt-in script preserves the reproduction without adding
a package manifest or test framework:

```powershell
# Build client first with the command-line compression workaround if needed.
dotnet run --project DesignMode.AI.Client --no-build --no-restore --no-launch-profile -- --urls http://localhost:5187
# In a separate terminal, from the repository root:
node DesignMode.AI.Test/js/gen-webkit-regression.cjs
```

The script defaults to the existing generated Playwright package path. If that
publish output is unavailable, set `PLAYWRIGHT_MODULE` to an installed Playwright
module's absolute path. Its matching Chromium and WebKit browsers must be
installed. All API responses are fixtures; unknown APIs cannot reach a backend.

## Limits and remaining checks

- This is desktop-hosted WebKit, not physical iPhone hardware. Recheck the
  dashboard-to-project transition on the affected iPhone after normal release.
- The working Chromium baseline and fixed WebKit both report an inline-script
  insertion syntax error (`Unexpected end of input` / `Unexpected end of script`).
  It is reported separately by the regression script, not represented as a clean
  JavaScript run. It does not prevent the workspace from rendering.
- A third-party Karla font can return 404; the page uses fallback fonts.
- Gen retains its pre-existing 1200px inner container and horizontal overflow;
  mobile content is visible, but this is not a claim of fully corrected responsive
  layout. No speculative CSS changes were made.
- Live generation, every feature modal, invalid CID recovery, and provider failure
  behavior were not end-to-end tested. The change does not alter those paths.
- On-device follow-up: open the same saved project, inspect image/recommendations
  and navigation; open Website, Social, Print, Events and Commercial Video; verify
  close and active-generation Stay/Leave behavior. Generation tests should use
  controlled non-production providers.

The user's existing `wwwroot/index.html` edit is excluded from the fix commit.
No push, merge, or deployment is part of this task.
