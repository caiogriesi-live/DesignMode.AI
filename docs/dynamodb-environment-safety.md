# DynamoDB environment-safety audit

## Result and scope

All source projects were searched, including ignored source files, for clients, configuration, interfaces, document/context APIs, PartiQL, Get/Put/Update/Scan/Query and transactions. There were **74 independent connection-selection blocks: nine factory/helper methods and 65 inline blocks**, across ten server files. They did not behave identically.

Every application client now delegates to `Services/DynamoDbClientFactory.cs`. Existing helper signatures remain as forwarding adapters to avoid unrelated refactoring. The only two actual constructors are the local/AWS branches of that factory. Program validates selection and logs it before accepting requests; stores retain their existing DI registration and ownership/disposal behavior. No new IAmazonDynamoDB DI registration is required.

No client construction or DynamoDB operations were found in Client, Business, Data or Shared source. Data's SQL/Dapper path is separate. No active DynamoDBContext, Table.LoadTable, or document-model operations were found (an unused DocumentModel using remains). External precompiled sibling DLL implementations are not source in this solution and cannot be certified by this source audit.

## Defects fixed

- Older functions hard-coded localhost, ignored LOCAL_DYNAMODB_URL, omitted AuthenticationRegion, and used case-sensitive local switches.
- Some AWS paths silently defaulted to us-east-1. All now require explicit AWS_REGION, matching the already strict video path.
- Explicit access-key/secret constructors bypassed AWS_SESSION_TOKEN and the normal provider chain. AWS mode now uses the SDK configuration-only constructor.
- SDK global/service/profile endpoint overrides could independently redirect AWS requests. AWS config sets IgnoreConfiguredEndpointUrls=true.
- Known local fake credentials in production environment variables now produce a clear error.
- The initializer left fake AWS credentials in its caller's shell. It now restores key/secret/token in finally, even on failure, and rejects unsafe URL shapes.
- Integration tests formerly required only an opt-in flag; they now additionally require local mode and a loopback endpoint before writes.
- The auth diagnostic now uses validated canonical selection instead of printing a raw endpoint setting. No authentication behavior changed.

## Exact connection policy

| Setting | Result |
|---|---|
| USE_LOCAL_DYNAMODB=true, TRUE, TrUe | LOCAL; explicit case-insensitive true only |
| false, missing, empty, invalid, 1, whitespace-padded true | AWS; never local fallback |
| Local endpoint configured | LOCAL_DYNAMODB_URL; absolute HTTP(S), no credentials/query/fragment |
| Local endpoint missing/blank | http://localhost:8000 |
| Local region configured | AWS_REGION |
| Local region missing/blank | us-east-1 |
| AWS region missing/blank/malformed | Clear configuration exception; startup stops |
| LOCAL_DYNAMODB_URL in AWS mode | Not read; cannot influence configuration |
| AWS_ENDPOINT_URL / AWS_ENDPOINT_URL_DYNAMODB / profile endpoint | Ignored by canonical DynamoDB configuration |

Local uses explicit fake signing credentials and AuthenticationRegion; no real AWS credentials are necessary. Non-loopback local endpoints remain supported for intentionally configured container/emulator hosts, but the provisioning script and integration tests only permit loopback.

AWS mode uses RegionEndpoint from AWS_REGION, no ServiceURL, no local signing-region fallback, and the standard AWS SDK credential chain. It does not construct fake credentials or silently fall back locally after credential/network errors. The known local environment credential pair is rejected; arbitrary invalid credentials in a deployed profile remain an AWS authentication error, not something this audit can certify as valid.

## Azure Functions production authentication

The repository's local configuration contains names AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION, USE_LOCAL_DYNAMODB, and DynamoDbTableName. No alternate AWS federation deployment configuration was found. No secrets were printed or changed.

For the existing Azure Functions architecture, configure AWS_REGION and a valid AWS credential source in the Function App environment. The existing environment-based mechanism uses AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY; temporary credentials additionally require AWS_SESSION_TOKEN. The SDK now honors that token. Rotate/manage these securely; do not put them in committed files. An Azure managed identity alone does not automatically authenticate to AWS. An intentionally configured SDK profile/web-identity credential provider remains supported, but this task does not introduce a federation strategy.

Region selection is intentionally stricter than the generic SDK chain: AWS_REGION is required in AWS mode; there is no implicit shared-profile/default-region fallback. Authentication is still the normal SDK chain. Missing credentials fail through the SDK when it resolves credentials; startup's configuration check performs no AWS calls and does not prove IAM permissions or live connectivity.

References: [AWS .NET credential chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html), [AWS endpoint overrides](https://docs.aws.amazon.com/sdkref/latest/guide/feature-ss-endpoints.html).

## Table inventory and access ownership

Names and schemas were not changed. No automatic environment prefix/suffix exists. Local and AWS use the same names but separate endpoints. Two AWS deployments using the same account, region and names share tables; choose account/region separation deliberately.

| Tables | Access path |
|---|---|
| Projects, Funnels, AttributionTouches, CheckoutAttempts, AttributionSnapshots | AttributionStore → canonical factory |
| AIGenerationUsage | AIGenerationUsageStore → AttributionStore.CreateClient → canonical factory |
| Purchases, PurchaseCustomers | PurchaseMeasurementStore → AttributionStore.CreateClient → canonical factory |
| Purchases (workspace read) | PaidWorkspaceMeasurementFunction → AttributionStore.CreateClient |
| Users | DesignModeFunctions, UpdateUserCurrentPage, CanonicalUserStore, AttributionStore; all canonical |
| CompanyIndustry, Ideas, InviteClient, Subscriptions | DesignModeFunctions; all canonical |
| Prompt, InviteClientImages, ImageIdeas, AudioSample, CTAText, Website, Social | DesignModeFunctions; Social also GenerateSocialPostFunction |
| VideoImages, Videos, ConceptImages | DesignModeFunctions; VideoImages also RunwareVideoFunctions |
| BrandingLogo, DigitalMarketing, EventActivations, PackageLabel, PrintMaterials | Corresponding focused generation/print functions |
| testtable | Existing test HTTP functions in DesignModeFunctions, still environment-aware |
| DesignModeJobs (default) | Job queue/status paths in DesignModeFunctions |

All above are literal names except the jobs table: `Environment.GetEnvironmentVariable("DynamoDbTableName") ?? "DesignModeJobs"`, in five locations. An empty configured value is not replaced by the fallback; supply a valid name or omit it. `CompanyIdeaId` is a relationship field; no access to a DynamoDB table literally named CompanyIdea was found. The ImageIdeas PromptId-index access uses the same client. Generic helper table arguments are passed by the existing operations; none changes endpoint policy.

StripeVerifiedPurchaseHandler → PurchaseMeasurementStore and PurchaseConversionDispatcher → PurchaseMeasurementStore inherit the same factory. GoogleConversionDelivery itself does not construct a DynamoDB client. The new measurement tables therefore have no independent endpoint/credential configuration.

## Complete original construction inventory

These line numbers refer to the pre-audit source, for locating the removed blocks in the diff. Each listed position represents one helper or inline selection block, not each branch constructor.

| File (under DesignMode.AI.Server) | Blocks | Original starting lines |
|---|---:|---|
| DesignModeFunctions.cs | 64 | 250, 578, 2181, 2313, 2638, 2728, 2887, 3326, 3473, 3573, 4124, 4326, 4510, 4626, 9273, 9544, 9734, 10732, 10844, 10999, 11138, 11255, 11421, 11774, 11913, 12045, 12243, 12367, 12478, 12602, 12705, 12802, 12939, 13021, 13152, 13247, 13416, 13528, 13650, 13740, 13830, 13929, 14022, 14109, 14227, 16615, 16735, 16822, 16928, 17076, 17188, 17307, 17420, 17524, 17621, 17694, 17787, 17881, 17966, 18088, 18163, 18358, 18459, 18665 |
| GenerateBrandingLogoFunction.cs | 1 | 447 |
| GenerateDigitalMarketingFunction.cs | 1 | 343 |
| GenerateEventActivationFunction.cs | 1 | 662 |
| GeneratePackageLabelFunction.cs | 2 | 856, 1014 |
| GenerateSocialPostFunction.cs | 1 | 598 |
| PrintMaterialsFunctions.cs | 1 | 1110 |
| RunwareVideoFunctions.cs | 1 | 1044 |
| UpdateUserCurrentPage.cs | 1 | 229 |
| AttributionStore.cs | 1 | 19 |

AttributionStore.cs is under Services. The nine methods were AttributionStore.CreateClient; DesignModeFunctions.CreateDynamoDbClient; GenerateBrandingLogoFunction.CreateDynamoClient; GenerateDigitalMarketingFunction.CreateDynamoClient; GenerateEventActivationFunction.CreateDynamoDbClient; GenerateSocialPostFunction.CreateDynamoDbClient; PrintMaterialsFunctions.CreateDynamoClient; RunwareVideoFunctions.CreateDynamoDbClient; UpdateUserCurrentPage.CreateDynamoDbClient. GeneratePackageLabelFunction had two inline blocks. All other inline blocks were in DesignModeFunctions.

## Initialization-script safety

Only scripts/Initialize-MeasurementTables.ps1 was found as a repository DynamoDB initializer. Generated publish/playwright scripts are unrelated. It always passes an explicit loopback endpoint to every AWS CLI operation and defaults to localhost:8000. It cannot provision production endpoints. It only describes/creates the three measurement tables; it does not delete tables. Temporary fake credentials and cleared session token are restored in finally. No production provisioning script or infrastructure definition was found.

## Exact local configuration

Set in local Functions Values or process environment:

```text
USE_LOCAL_DYNAMODB=true
LOCAL_DYNAMODB_URL=http://localhost:8000
AWS_REGION=us-east-1
```

LOCAL_DYNAMODB_URL and AWS_REGION can be omitted locally to use those defaults. AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_SESSION_TOKEN are not required for DynamoDB Local. Other AWS-backed services may have their own requirements, unchanged by this task.

## Exact production configuration

```text
USE_LOCAL_DYNAMODB=false
AWS_REGION=<actual-region-containing-your-tables>
AWS_ACCESS_KEY_ID=<securely-configured-AWS-access-key>
AWS_SECRET_ACCESS_KEY=<securely-configured-AWS-secret>
AWS_SESSION_TOKEN=<required-only-for-temporary-credentials>
```

Omitting USE_LOCAL_DYNAMODB also selects AWS. Remove LOCAL_DYNAMODB_URL and all fake credentials from production for hygiene; even if LOCAL_DYNAMODB_URL remains, AWS mode ignores it. Omit AWS_SESSION_TOKEN for long-lived credentials. DynamoDbTableName is optional, defaults to DesignModeJobs; it must match the deployed jobs table if supplied. Do not copy local.settings.json into production. No actual production region or credential value was invented.

## No-write diagnostics and tests

DynamoDbClientFactory.Resolve accepts a dictionary-backed reader and returns mode, region and endpoint without resolving credentials or making any network request. Startup logs only:

- DynamoDB mode: LOCAL; endpoint; region
- DynamoDB mode: AWS; region

Tests cover true/TRUE/mixed case, false/unset/empty/invalid/whitespace switches, configured/default local endpoint and signing region, missing/invalid AWS region, ignored local and SDK endpoint overrides, rejection of known fake AWS credentials and sensitive local URL forms, and real local client construction with AWS credential variables cleared. A source guard rejects new application constructors/configurations outside the canonical factory.

Existing integration tests have additional loopback safety checks and exercise attribution capture/project creation, AI usage readback, immutable checkout snapshots, canonical purchases/customer markers, deduplication, and user binding. No AWS writes or live Google/Stripe calls are used.

Run from repository root:

```powershell
dotnet build DesignMode.AI.sln --no-restore -p:CompressionEnabled=false -clp:ErrorsOnly
$env:RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS='true'
$env:USE_LOCAL_DYNAMODB='true'
$env:LOCAL_DYNAMODB_URL='http://localhost:8000'
$env:AWS_REGION='us-east-1'
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj --no-build
```

## Files changed

- New: Services/DynamoDbClientFactory.cs (Server); DynamoDbConfigurationTests.cs (Test); this report.
- Connection setup only: the ten server files in the construction inventory.
- Server/Program.cs: startup validation and mode logging.
- Test/AIGenerationUsageLocalIntegrationTests.cs, AttributionStoreLocalIntegrationTests.cs, GoogleCanonicalUserLocalIntegrationTests.cs, PurchaseMeasurementTests.cs: local-write guards.
- scripts/Initialize-MeasurementTables.ps1: endpoint validation and credential restoration.

No table schemas, keys, attribution/purchase/AI ledger semantics, pricing, analytics events, UX, routes or authentication semantics were changed. No real AWS table was read or written.

