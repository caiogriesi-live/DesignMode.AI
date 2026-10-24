# Hosted Stripe test setup

The supplied September 25 settings select AWS DynamoDB in `us-east-1`, enable the
credit ledger, and set `AI_CREDITS_MODE=Test`. Read-only AWS checks confirmed that
`Purchases` and `AIGenerationUsage` exist, but `AICreditLedgerTest` and
`AICreditLedger` do not. The selected test ledger table is therefore missing; local
table provisioning does not provision AWS. A wallet read throws instead of returning
a summary, which leaves the modal showing “Your credit summary is unavailable.”

Separately, Production previously rejected test-mode checkout even with valid Stripe
test keys and package mappings. The explicit hosted-test opt-in resolves that policy
conflict without changing the Azure Functions environment to Development.

## Apply for hosted testing

1. In the same AWS account used by the Functions app, provision the test ledger:

   ```powershell
   aws dynamodb create-table --table-name AICreditLedgerTest --attribute-definitions AttributeName=Id,AttributeType=S --key-schema AttributeName=Id,KeyType=HASH --billing-mode PAY_PER_REQUEST --region us-east-1
   aws dynamodb wait table-exists --table-name AICreditLedgerTest --region us-east-1
   ```

   Use the normal AWS credential chain. Do not embed credentials in scripts. The
   table has no sort key or TTL. Do not create the live ledger for this test setup.
   Ensure the app's IAM permissions cover GetItem, PutItem, UpdateItem, and
   TransactWriteItems on the test ledger and existing Purchases table.

2. Deploy the server change adding `AI_CREDITS_ALLOW_HOSTED_TEST_PAYMENTS` and add
   this Azure app setting (merge it; do not replace the app's settings):

   ```json
   { "name": "AI_CREDITS_ALLOW_HOSTED_TEST_PAYMENTS", "value": "true", "slotSetting": true }
   ```

   Keep `AZURE_FUNCTIONS_ENVIRONMENT=Production`, `AI_CREDITS_MODE=Test`,
   `USE_LOCAL_DYNAMODB=false`, both existing credit flags true, and the existing test
   Stripe key, test package mappings, and test portal configuration. Restart the
   Functions app after deployment/settings changes. Setting this flag on an old
   server build has no effect.

3. Verify the Stripe test webhook targets this hosted API and its signing secret
   matches. It must receive payment_intent.succeeded, invoice.paid,
   customer.subscription.created/updated, charge.refunded, and charge.dispute.created.
   The read-only endpoint inspection confirmed that the last two events are currently
   missing; add them while preserving all existing event subscriptions.
   A local Stripe CLI signing secret is not the hosted endpoint's signing secret.

4. Sign in and inspect the `ai-usage-summary` network request: it should return 200
   and the modal should show Sandbox, balances, and purchase options. A 401 indicates
   a separate JWT/function-key problem; a remaining 500 requires the corresponding
   Functions exception, particularly an IAM denial or another missing table.
   Complete a test checkout, verify the dashboard return and signed webhook delivery,
   and confirm one canonical credit grant even when Stripe retries the event.

These settings are for test payments only. Test and live wallets are separate, but
the existing subscription and usage records still share this application's database.
Use a dedicated test account. Turning off this opt-in later also prevents fulfillment
of outstanding test checkouts; reconcile those payments before disabling it.

This document contains no credentials. No cloud tables, app settings, or deployments
were changed during diagnosis.
