# Funnel recovery: preview ready

Production settings are unchanged. Scheduled follow-ups use the canonical recovery preference: existing users are allowed unless they have unsubscribed. Missing users and unavailable reads block delivery. Local development can prepare its own empty recovery stores as described below.

## Production activation verification

After separately reviewing and deploying this change, leave one controlled test account's funnel unfinished. With `FUNNEL_RECOVERY_EMAIL_ENABLED=true`, a successful initial preview-ready send and the existing production storage/SES configuration, reminders become due 24, 72 and 168 hours after `VideoReadyAt`. The timer evaluates every 30 minutes, so delivery is on the next successful run, not necessarily at the exact hour. No new environment variable or database migration is required.

Existing eligible recovery records may now advance when due, including a controlled test funnel created before deployment. There is no enrollment of historical funnels, timestamp reset or replay of sent steps. Late processing sends only the next unsent reminder, with at least 24 hours between reminders. Do not reset state to test. Confirm the corresponding `Reminder1SentAt`, `Reminder2SentAt`, and `FinalReminderSentAt` timestamps and no duplicates on later timer runs. Unsubscribing, paid access or funnel completion blocks remaining reminders.

Missing users, mismatched canonical IDs and failed preference reads do not authorize production delivery. The canonical unsubscribe preference is read consistently before the claim and again before sending. SES, selected-image lookup, timing, recovery state and subscription semantics are unchanged. Recovery subjects have no local-test prefix in any environment.

Verification for this activation change: 13 new canonical-permission test cases passed with fake database and email clients. Full .NET regression: 752 passed, 5 unrelated existing failures, 18 skipped. The failures are four unchanged email layout assertions (`contain` versus `cover`) and an unchanged workspace CSS assertion. Privacy text/behavior tests: 7 passed, 1 existing unrelated failure expecting `PrivacyPreferences` in unchanged `App.razor`. Client and Server builds passed with existing warnings; `git diff --check` passed. No browser QA, live customer sends, production writes, merge or deployment was performed.

## Scheduled recovery sequence

- Existing immediate video-ready email, trigger, feature flag, provider and send-once behavior are preserved.
- Reminder 1: 24 hours after `VideoReadyAt`, subject **Your DesignMode preview is waiting**.
- Reminder 2: 72 hours after `VideoReadyAt`, subject **Continue building your brand with DesignMode**.
- Final: 168 hours after `VideoReadyAt`, subject **Finish your DesignMode project**. No further reminders follow it.
- Override delays with `FUNNEL_RECOVERY_REMINDER1_DELAY_HOURS`, `FUNNEL_RECOVERY_REMINDER2_DELAY_HOURS`, and `FUNNEL_RECOVERY_FINAL_DELAY_HOURS`. Defaults are 24/72/168; configured integer hours must be 1–8760 and strictly increasing. Delayed runs advance one step at a time with at least 24 hours between reminders, so overdue records do not receive a burst.

`ScheduleFunnelRecoveryReminders` runs every 30 minutes using the existing timer/storage conventions. `FUNNEL_RECOVERY_EMAIL_ENABLED=false` disables all scheduler reads/writes/sends. Selection pages through **FunnelRecovery only**, 100 evaluated items per request, filtering for first-email-sent, sufficiently old readiness, unsuppressed and final-unsent records; empty filtered pages still advance. There is no due-date or user GSI, so this is a full paginated recovery-table scan each run (filters do not reduce DynamoDB read capacity). Add `dynamodb:Scan` permission to this table in a separately authorized rollout. No new table, index, historical funnel scan or backfill is created. An eventual candidate read is always followed by strongly consistent recovery and ownership reads. A record whose first email was never confirmed sent cannot advance.

Each candidate reloads canonical Users/Projects/Funnels ownership and current resume progress through the existing store, then checks `Subscriptions` by canonical `Users.Id` using existing paid-access semantics. Paid access persists `SUBSCRIBED`; completed funnel persists `FUNNEL_COMPLETED`; missing/mismatched canonical state and unavailable email also fail closed with safe reason codes. Suppression is permanent even if the subscription later ends. Existing `SuppressedAt` is honored. Tiny callbacks after subscription-created/updated, one-time paid-access persistence and the existing SaveSubscription write recheck canonical access and suppress existing records immediately. Callback failure is isolated from billing and logged safely; the timer rechecks access independently. There is still an unavoidable final race if subscription/suppression changes during the SES call.

Marketing permission is checked before claiming and again immediately before sending. The production `FunnelRecoveryUnsubscribe` adapter strongly reads the canonical Users record and returns Denied when `Users.RecoveryEmailsUnsubscribedAt` exists, otherwise Allowed for an existing matching user. Missing or mismatched users return Unavailable; read failures skip delivery. The guarded local inbox test mode below bypasses only Unavailable, never Denied. A known denial suppresses with `UNSUBSCRIBED`. The first requested-preview service email does not use this marketing gate.

## Unsubscribe footer

All four recovery emails include an Unsubscribe link in HTML and plain text, and the copyright names DesignMode Inc. The signed link opens the public `/unsubscribe?token=REDACTED_FOR_GROOMING` confirmation page; it does not require login. The page posts the token to `/api/recovery/unsubscribe` only after confirmation, so GET requests from email scanners do not change preferences.

Tokens contain an opaque recovery ID and a purpose-specific HMAC signed with the existing `JwtSecretKey`. Missing signing configuration prevents a delivery claim; rotating that secret invalidates older unsubscribe links. The API resolves the canonical user from recovery state and idempotently stores `RecoveryEmailsUnsubscribedAt` on the existing Users item. The preference applies across that user's recovery reminders. Requested-preview and account service emails remain separate. Deploy both client and server for the link and endpoint to work.

The existing recovery record adds `Reminder1SentAt`, `Reminder2SentAt`, `FinalReminderSentAt`, optional `SuppressionReason`, plus delivery claim/attempt/retry metadata. Conditional DynamoDB claims require unsent, unsuppressed state, consistent prior steps and fewer than three attempts. Unique claim ownership protects finalization and rejection. Prior/sent state, ownership, subscription, funnel completion, marketing permission and the latest valid resume destination are rechecked after claiming. Templates reuse the existing safe login/resume URL builder, add no plan promises or discounts, and include no customer identifiers in body text or logs.

Definite provider rejections leave the step unsent and release the claim with a six-hour cooldown, at most three attempts per step. Successful delivery clears its attempt counter for the next step. Exhausted attempts require review. SES has no idempotency key: unknown outcomes, crashes after claiming and failed sent-state writes retain the non-expiring claim and never automatically resend. This prioritizes duplicate prevention over guaranteed delivery. Accepted sends are finalized even if suppression arrives during the request; suppression remains set. Review uncertain/exhausted deliveries manually using provider evidence. No claim reclamation or unbounded retries are introduced.

Rollout status: **BLOCKED — MARKETING_SUPPRESSION_REQUIRED**. No production configuration, deployment, provider credentials, historical records or marketing preferences were changed. Enabling the existing flag alone can run eligibility/suppression checks but cannot enable promotional sending. Already-enrolled recovery records may become due after a future authorized canonical-marketing integration; plan that activation separately.

Sequence verification (.NET SDK 8.0.300): full suite **549 passed / 0 failed / 16 skipped**; Client and Server builds passed with existing warnings. Optional DynamoDB Local tests were not enabled and are included in the skipped count; the new persistence test covers actual claim contention, claim-owner fencing, retry cooldown, all three sent timestamps and preserved suppression reason. DesignerIntro completion/session and DOM-free website landing Node regressions passed. No browser/UI QA or real email delivery was performed.

## Local inbox delivery test

In the ignored server `local.settings.json` Values, set `FUNNEL_RECOVERY_LOCAL_TEST_ENABLED=true` and `FUNNEL_RECOVERY_LOCAL_TEST_EMAIL` to one bare test email address. Keep `FUNNEL_RECOVERY_EMAIL_ENABLED=true`. Restart/rebuild the Functions host to load the settings and implementation. The timer remains every 30 minutes; it does not run immediately at startup.

This mode only allows a canonical recipient matching that single inbox; it never redirects other accounts' reminders. It requires Development, no Azure hosting markers in configuration or process environment, `AzureWebJobsStorage=UseDevelopmentStorage=true`, a loopback client HostUrl, and DynamoDB Local with loopback endpoints in both configuration and the actual connection-factory environment. Missing/invalid settings, remote databases and other recipients fail closed without granting consent or suppressing unrelated users. It bypasses only **unavailable** marketing permission: known opt-out, existing suppression, paid access, completion, timing, claim ownership and retry rules still apply. All guards and the recipient are checked again after claiming and before sending. Production uses the canonical user preference described above.

The existing SES sender delivers to the matching canonical inbox without an environment prefix in the subject. A successful send marks the normal reminder timestamp on the local recovery record, so subsequent timer runs do not resend it. Use a matching test account's existing recovery record and keep first-email and prior-reminder sent state intact; no records or account emails are changed by enabling this mode. Existing SES sender credentials and verified-address/sandbox requirements still apply. Disable the local test flag and restart when finished. No real email is sent by unit tests.

Local-mode verification: **572 passed / 0 failed / 16 skipped** across the .NET regression suite. Coverage includes the inbox restriction, Azure/remote environment guards, opt-out, subscription/completion suppression, recipient changes after claim, and duplicate execution.

Subscription suppression correction: the canonical `Subscriptions` table uses the composite key `Id` (partition) and `UserId` (sort). The post-write callback now includes both keys in its strongly consistent lookup. The previous Id-only read was rejected by DynamoDB and deferred suppression to the due-record scheduler. Verification against DynamoDB Local passed all 157 recovery tests, including a new synthetic subscription-to-suppression integration test; no customer records were modified by the test. Restart the local host to load this fix. Previously missed, unfinished recovery records are still checked for paid access when due; this fix does not backfill records or resend emails.

## Trigger and ownership

The audited DesignerIntro path is `QueueApiCall` → `api-requests` → `ProcessApiCall` → Runware submission → `runware-video-polls` → `PollRunwareVideo`. The latter persists `VideoUrl`, `Status = Completed`, and `CompletedAtUtc` in `DesignModeJobs` (or `DynamoDbTableName`). Only after that successful write does the observer publish a job ID and observation timestamp to `funnel-recovery-video-ready`. Queue publication is best effort, capped at a five-second network timeout with no SDK retries. Its failure cannot change video success.

New job records include `PreviewKind`, derived from the same landing-variant resolver used by generation. Only `Social` previews qualify. Website previews and legacy records without this field are excluded, including on replay. Generation behavior and provider calls are unchanged.

The delivery worker rereads persisted readiness and requires matching ownership across the job, `Projects.UserId`, `Projects.OriginatingFunnelId`, `Funnels.UserId`, and `Funnels.ProjectId`. The project must be the original acquisition project. It loads the canonical `Users` account through `CanonicalUserStore`; queue/browser recipient, username, UserId, and return URL are never inputs to delivery.

## Eligibility and resume

The existing funnel state must be `Authenticated`. Canonical `Users.CurrentPage` must be absent/invalid or one of the existing unfinished social routes: `/designerintro`, `/designerintro2`, `/init/mainconcept`, `/init/funnelend`, `/init/upgrade`. Other valid destinations, including Success, GenInit, Gen, and dashboard, fail closed even before subscription synchronization. `Onboarded` is not a completion flag: the existing Init page sets it before navigating to Idea.

`IPostAuthRoutingStore.HasPaidSubscriptionAsync` reads canonical `Subscriptions` with `Users.Id`, using its existing `active`/`Paid` access semantics. No Stripe or Purchases lookup is added. Valid canonical email, unsent state, and no `SuppressedAt` are also required. Ownership, readiness, subscription, completion, and recovery state are rechecked after claiming and immediately before delivery. These reads cannot eliminate the final cross-system race if checkout completes during the SES request.

The service email uses the existing SES integration style (inline HTML and plain text; no shared template framework or communication-preference store exists). Subject: **Your DesignMode preview is ready**. The CTA is `HostUrl/login?returnUrl=<encoded canonical CurrentPage>`, using the existing safe route normalizer and routing store. Missing/invalid progress falls back to `/designerintro`. In production, `HostUrl` must be the configured public HTTPS application origin with a DNS hostname, default port, and no credentials/path/query; never set it to a Functions host or internal hostname. Local Development additionally permits HTTP/HTTPS loopback origins and the client port, such as `https://localhost:7178`. That exception is disabled when Azure hosting markers are present. No recipient or complete CTA is logged.

## Persistence and delivery guarantees

Manually provision DynamoDB table **FunnelRecovery**, partition key **Id (String)**, on-demand capacity, no sort key or GSIs. Grant only required GetItem/PutItem/UpdateItem permissions, alongside existing canonical read permissions. No production provisioning is performed by code.

The deterministic key hashes user/funnel/project plus notification kind. State contains canonical IDs, readiness/sent/suppressed timestamps, CreatedAt/UpdatedAt, DeliveryStatus, Attempts, Revision, and optionally the SES message ID. It contains no recipient, content, prompts, tokens, attribution payload, or URL. Conditional insert preserves original readiness; conditional claim atomically requires unsent, unsuppressed `Ready` state and fewer than three attempts. Every mutation increments Revision. Operators can set `SuppressedAt` on a record; normal processing also persists suppression when canonical eligibility fails.

SES has no idempotency key, so exactly-once delivery across SES and DynamoDB cannot be guaranteed. This implementation favors preventing duplicates: a `Sending` claim never expires or gets reclaimed automatically. Definite SES 4xx rejections release the claim for at most three attempts, with Azure Queue's existing bounded retry/poison handling. SDK email retries are disabled. Unknown outcomes (timeout/transport/5xx), worker crashes after claiming, and failure to persist an accepted send retain `Sending` and leave SentAt unset. Review these records manually using provider evidence; do not clear claims or replay them blindly. There is no automatic reconciliation campaign. Failed checks before the email request release the claim for bounded retry.

Missing recovery storage, queue failures, or email failures never turn a completed video into a failed video. A failed queue publication can miss a notification; there is intentionally no historical scan to compensate. The new queue contains only new observed completion events while enabled. Disabled processing performs no recovery reads/writes or sends and consumes queued messages without later backfill.

## Configuration and rollout

- `FUNNEL_RECOVERY_EMAIL_ENABLED`: absent, invalid, or `false` means OFF in both publisher and consumer. State creation is also off. Enable only after a separately authorized rollout.
- Reuse `SES_FROM_EMAIL`, `AWS_REGION`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `HostUrl`, and `AzureWebJobsStorage`. SES identity, permissions, and sandbox recipient restrictions must be satisfied in the selected environment. No new credentials or production settings are supplied.
- Outside the guarded local setup below, manually provision Azure queue `funnel-recovery-video-ready` and its `-poison` queue in the existing storage account, and grant producer/consumer permissions. Azure Functions uses its existing queue retry configuration (default maximum dequeue count five). No existing video queues are renamed or reconfigured.
- No production emails were sent or provider credentials verified. The existing provider is reused; production configuration must be checked before enabling.

## Local manual QA with the existing Amazon SES sender

Keep your working signup-email settings: `SES_FROM_EMAIL`, `AWS_REGION`, `AWS_ACCESS_KEY_ID`, and `AWS_SECRET_ACCESS_KEY`. Recovery reads them from the existing configuration, including `Values` in `local.settings.json`; no alternate provider, SMTP server, or new SES credentials are required. Environment settings override the local `Values` fallback.

In the ignored server `local.settings.json`, use:

```json
{
  "Values": {
    "AZURE_FUNCTIONS_ENVIRONMENT": "Development",
    "FUNNEL_RECOVERY_EMAIL_ENABLED": "true",
    "HostUrl": "https://localhost:7178",
    "USE_LOCAL_DYNAMODB": "true",
    "LOCAL_DYNAMODB_URL": "http://localhost:8000",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true"
  }
}
```

Merge these keys into your existing Values; do not replace the file or remove the working SES configuration. Use the actual **client** URL for HostUrl, not the Functions port 7032. `http://localhost:5276` also works in local Development if that is your client profile.

1. Start your existing DynamoDB Local and Azurite, then restart the backend and client from this branch using your usual local launch workflow.
2. Look for `[FUNNEL RECOVERY] local table and queues ready`. When enabled, `FunnelRecoveryLocalSetup` creates the local `FunnelRecovery` table and both recovery queues if missing. It requires Development, no Azure hosting markers, explicit local DynamoDB with a loopback endpoint, and exactly `UseDevelopmentStorage=true`. It never uses real AWS credentials for provisioning, never scans records, and never sends email. Other environments still require manual provisioning.
3. Create a **new** DesignerIntro signup using an inbox your current SES setup can deliver to. Let the preview generate and leave before subscribing. Keep the backend running.
4. Expect one real SES email after persisted video completion, with `DeliveryStatus=Sent` and `VideoReadyEmailSentAt` in the local recovery table. Existing signup emails are separate; identify this one by its preview-ready subject.
5. Open the CTA on the same computer with the local client running. It should use `https://localhost:7178/login?returnUrl=...`, log you in, and resume the saved page. A phone or another computer cannot use your machine's localhost link.
6. Set the local feature flag back to `false` and restart when finished. Production settings remain unchanged.

There is no recovery UI to visit. Missing emulator/setup failures produce a safe warning without breaking startup. If the recovery record is already Sent or has a held Sending claim, use a new test funnel instead of clearing it. Legacy jobs without PreviewKind are intentionally not backfilled.

Local-support verification: SDK 8.0.300, 505 tests passed / 0 failed / 15 skipped; server build passed. Coverage includes local HTTPS/HTTP links, port preservation, production rejection of loopback, local Values configuration, and guards against remote infrastructure provisioning. No real SES email or browser QA was performed during implementation.

## Local verification

Use .NET 8, with the repository's sibling dependency assemblies available:

```powershell
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --filter FullyQualifiedName~FunnelRecovery
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj
node DesignMode.AI.Test/DesignerIntroCompletion.unit.cjs
dotnet build DesignMode.AI.Client/DesignMode.AI.Client.csproj
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj
git diff --check
```

Unit and adapter tests use fake senders and DynamoDB stubs; they never send email. The optional persistence integration test requires `RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true`, `USE_LOCAL_DYNAMODB=true`, and a loopback `LOCAL_DYNAMODB_URL`, following existing local test safety checks. It creates the recovery table only in DynamoDB Local, verifies conditional concurrency, retry, sent-state replay, suppression, and revision, then removes its own test record. No browser/UI QA is involved.

Implementation verification used SDK 8.0.300: full suite 480 passed / 0 failed / 15 skipped; recovery subset 62 passed / 0 failed / 1 skipped; client and server builds passed with existing dependency/compiler warnings. DynamoDB Local was unavailable, so the opt-in persistence integration test was not executed. DesignerIntro completion/session and website landing Node regressions passed. The machine's default SDK 10.0.400 initially failed client static-asset compression; verification used the installed .NET 8 SDK without committing an SDK configuration change.

SHARED FILE RISK for the sequence: **HIGH OVERLAP** in `DesignModeFunctions.cs` (optional injection and suppression callbacks only, after the three existing subscription-table writes) and `Program.cs` (recovery registrations). Subscription hooks strongly read the just-written row and reuse canonical paid-access semantics; billing, purchase and attribution behavior is unchanged. Recovery-specific files and docs are LOW OVERLAP; the scheduler, sequence logic, store partial and sequence tests are NEW FILES.
