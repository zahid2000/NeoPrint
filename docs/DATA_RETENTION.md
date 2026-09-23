# NeoPrint --- Data Retention

NeoPrint user files and financial records have different retention
requirements.

## User files

File metadata and binary content must be treated separately.

Supported auto-delete modes from the plan:

-   Never
-   AfterPrint
-   AfterDays

`DeleteAt` should be derived and processed by a reliable background job.

Deletion must be idempotent.

## Delete process

1.  Mark file as deleting/deleted according to domain rules.
2.  Remove storage object.
3.  Remove encrypted versions/previews when applicable.
4.  Remove related temporary artifacts.
5.  Record audit/technical result.
6.  Retry failed storage deletion safely.

## Account deletion

Account deletion must follow the plan: - personal data
removed/anonymized according to policy; - financial records preserved
only where legally/business-required; - wallet balance follows the
documented rule; - files are removed according to retention rules; -
audit history must not become misleading.

## Export

User export should include allowed personal data and metadata without
exposing secrets.

## Legal documents

Consent records should preserve: - document type - version - accepted
timestamp - IP where specified - device ID where specified

Published legal document versions must remain identifiable.

## Financial data

Do not apply generic user-file retention rules to payments/fiscal
records.

Financial records need their own retention policy.

## Implementation

Do not hard-delete business data from arbitrary controllers.

Use explicit application commands/jobs.

All cleanup jobs must be retry-safe and observable.
