# Local task inventory

Client: https://localhost:7178
Functions: http://localhost:7032
Page: https://localhost:7178/designerintro

Settings copied from original master-caio checkout. Existing Values preserved except explicit planned feature changes.

Local database decision: This feature uses the existing public signup request and existing Users DynamoDB table; there is no need to provision a table or setting. The planned HTTP mutation creates only a uniquely identified local fixture account, and persistence mapping is verified through an injected fake DynamoDB client.

Feature configuration key names (values omitted):
Host.LocalHttpPort/CORS updated for fixed localhost launch. No production changes were applied.
Use existing login flow. Browser authentication and save/reload still require manual review.
