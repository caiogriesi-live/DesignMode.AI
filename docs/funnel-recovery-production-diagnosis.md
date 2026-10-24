# Production preview-ready diagnosis — 2026-09-16

Base: latest fetched `origin/master-caio`, `f6a616636b4a1654c0d98398f3206fec8983c879`.
Branch: `feature/fix-prod-video-ready-email-2026-09-16-081508`.

## Confirmed first failure

Read-only checks against `DesignModeAIServer20250401080115` in resource group
`designmode.ai` found **both `funnel-recovery-video-ready` and
`funnel-recovery-video-ready-poison` absent** in the account resolved from that
app's `AzureWebJobsStorage`. The feature flag is `true`; both recovery functions
are deployed and neither has an explicit disabled app setting. The DynamoDB
`FunnelRecovery` table is ACTIVE in `us-east-1`, with the expected String `Id`
partition key and no sort key. Its approximate metadata item count was zero
(not a strongly consistent count; no customer rows were read).

`PollRunwareVideo` persists Completed/VideoUrl, then invokes the same
`FunnelRecoveryPublisher.PublishSafelyAsync` in both environments. Its
`SendMessageAsync` cannot publish to the missing queue. The observer catches
the failure to preserve video success. No consumer event reaches the recovery
store or SES. Local startup creates its emulator queues/table automatically;
production deliberately requires manual provisioning. This is an infrastructure
prerequisite failure, not a feature flag, SES region, DNS, or template defect.

This identifies a concrete blocking prerequisite. Individual production video
completion logs and customer eligibility were not inspected; successful delivery
after provisioning is not yet proven.

## Configuration matrix

Recovery's canonical reader uses root `IConfiguration[name]` before
`Values:name`. Core Tools exposes Values as environment settings; Program adds
Values fallback for local execution. Production app settings supply environment
values. DynamoDB and the existing video worker use the process environment;
the production settings below agree with the recovery reader.

| Setting/resource | Local source/observed | Production source/observed | Requirement/default/failure |
|---|---|---|---|
| `FUNNEL_RECOVERY_EMAIL_ENABLED` | local Values: true | app setting: true | Missing/invalid false; case-insensitive; disables publisher, consumer, scheduler and state creation |
| `AWS_REGION` | Values/environment: us-east-1 | app setting: us-east-1 | Required by SES and AWS DynamoDB; no SES fallback |
| `AWS_DEFAULT_REGION` | Not used by recovery region selection | absent | Not required; no new region setting needed |
| `SES_FROM_EMAIL` | Values: present | app setting: display-name sender at designmode.ai | Required; existing sender preserved; identity authentication supplied as known good, not reverified by send |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | Values: present for working SES | app settings: present | Existing SES explicit credential construction; DynamoDB uses SDK chain; values never printed |
| `AWS_SESSION_TOKEN` | Not required for observed setup | absent | Existing SES constructor does not support session credentials; separate limitation, not this observed failure |
| `HostUrl` | Values: existing local origin | app setting: https://www.designmode.ai | Required valid application origin; production HTTPS; no `APP_PUBLIC_BASE_URL` equivalent needed |
| `JwtSecretKey` | Values: present | app setting: present | Required for unsubscribe signing before claim |
| `AzureWebJobsStorage` | local emulator | app setting: present | Required queue/timer connection; presence does not provision queues |
| recovery queues | locally auto-created | both absent | Manual production provisioning required; first blocking divergence |
| `USE_LOCAL_DYNAMODB` | true | false | Default false; production correctly selects AWS |
| `LOCAL_DYNAMODB_URL` | loopback local configuration | ignored in AWS mode | Local default http://localhost:8000 |
| `DynamoDbTableName` | DesignModeJobs | DesignModeJobs | Same default; job reads use this name |
| recovery table | local auto-created | FunnelRecovery ACTIVE | Fixed name, no prefix/GSI; Id String; GetItem/PutItem/UpdateItem, Scan for scheduler |
| `AzureWebJobs.SendFunnelRecoveryVideoReady.Disabled` | host setting | absent | No explicit production disable |
| `AzureWebJobs.ScheduleFunnelRecoveryReminders.Disabled` | host setting | absent | No explicit production disable; fixed 30-minute timer |
| `FUNNEL_RECOVERY_REMINDER1_DELAY_HOURS`, `FUNNEL_RECOVERY_REMINDER2_DELAY_HOURS`, `FUNNEL_RECOVERY_FINAL_DELAY_HOURS` | optional Values | optional app settings, not needed for first email | Defaults 24/72/168; not an immediate-send dependency |
| `FUNNEL_RECOVERY_LOCAL_TEST_ENABLED`, `FUNNEL_RECOVERY_LOCAL_TEST_EMAIL` | optional local reminder test | guarded out in Azure | Default disabled; not needed for first email |

## Downstream audit

After queue delivery, the consumer checks the same flag and resolves a Social,
Completed job with VideoUrl. Job, original acquisition Project and authenticated
Funnel must share canonical ownership. Users.Id and Username are resolved through
CanonicalUserStore, and PostAuthRoutingStore checks Subscriptions by canonical
user ID (`active`/`Paid`, case-insensitive). Existing unfinished-route rules and
SuppressedAt remain intact. RecordReady writes once; claims are conditional;
eligibility is refreshed before send; only accepted SES sends mark SentAt.

Selected concept resolution uses the owned invite's selected ImageId and
InviteClientImages public URL; failures omit the optional image. Missing progress
uses the existing safe route fallback. Optional company data is not required.
HostUrl and signing configuration intentionally fail before claiming if invalid.

SES uses configured AWS_REGION/from address and disables retries. There is no
recovery ConfigurationSet or repository EmailSuppression/HARD_BOUNCE/COMPLAINT
implementation on this base. Do not invent or bypass one. Reminder unsubscribe
and unavailable-marketing-permission gates remain enforced; requested-preview
service mail retains its existing separate semantics. SES-level suppression was
not queried or changed. Reminder scheduling does not recover a lost first event.

## Changes and production action

No send behavior, eligibility, credential construction or production provisioning
logic was changed. Added a safe QUEUE_NOT_FOUND category, hook/disabled logs, a
pure `ValidateFunnelRecoveryConfiguration()` contract and startup presence logs.
The contract checks configuration only, not permissions, credentials, actual
queue/table existence or deliverability. `infrastructure-verified=false` makes
that limitation explicit. Regression tests reproduce queue failure with a fake
Azure exception and run Local/Production publisher-consumer paths with fake SES.

**PROD SETTINGS REQUIRED:** No app-setting changes identified. Keep the existing
`FUNNEL_RECOVERY_EMAIL_ENABLED=true`, `AWS_REGION=us-east-1`,
`HostUrl=https://www.designmode.ai`, `USE_LOCAL_DYNAMODB=false`,
`DynamoDbTableName=DesignModeJobs`, configured sender, storage and signing secret.

**Manual infrastructure action required:** provision Azure Storage queues
`funnel-recovery-video-ready` and `funnel-recovery-video-ready-poison` in the
existing account referenced by the production app's `AzureWebJobsStorage`, with
the existing producer/consumer permissions. Do not create a different storage
account or change the connection setting. This task did not provision them.

After separately authorized provisioning/deployment, verify both queues exist and
the queue listener starts. Review safe hook/queued/state/sent logs for a separately
authorized new controlled test account; do not trigger customer recovery sends.
Inspect provider acceptance and sent-state only in that controlled verification.
Do not replay historical jobs, clear Sending claims, reset sent timestamps, or
backfill users. Earlier failed publications have no automatic recovery event.

**SHARED FILE RISK:** no shared video/queue worker, Program, UI, billing, or
subscription file changed. Only focused recovery configuration/diagnostics,
tests and documentation changed. Existing user edit to client index.html is
excluded from the feature commit. No deployment, live sends, production writes,
DNS changes, or browser/UI QA were performed.

## Verification

- New production/configuration and Local/Production flow tests: all 12 passed.
- Focused FunnelRecovery suite: 214 passed, 4 failed, 4 skipped.
- Full .NET suite: 715 passed, 4 failed, 18 skipped (737 total).
- All four failures are the existing `Sequence_UsesResponsiveSharedLayoutAndSafeResume`
  cases at FunnelRecoveryVisualTests.cs:100: expected `background-size:contain`
  is absent from the existing layout. Both the assertion and layout are unchanged
  from the recorded base. This is a string unit test, not browser QA; left untouched.
- Client and Server builds passed with existing dependency/licensing warnings.
- DOM-free DesignerIntro completion, WebsiteLanding and WorkspaceSession tests passed.
- `git diff --check` passed. Optional external/local database integrations were not enabled.

Tests used SDK 10.0.400 targeting .NET 8 and fake senders; no live SES request was made.
