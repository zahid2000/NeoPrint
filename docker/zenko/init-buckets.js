// ---------------------------------------------------------------------------
// Create the buckets the backend expects on every development object store,
// then exit.
//
// Runs as the one-shot `zenko-init` container, from the same pinned
// CloudServer image the stores run - that image ships aws-sdk, so no second
// image (and no second thing to keep pullable) is needed.
//
// Idempotent: a bucket that already exists is left alone.
//
// Objects are encrypted by the application before they are written (plan
// section 2.13), so the buckets stay private and no public policy is set.
// ---------------------------------------------------------------------------
'use strict';

const { S3 } = require('/usr/src/app/node_modules/aws-sdk');

const accessKeyId = process.env.SCALITY_ACCESS_KEY_ID;
const secretAccessKey = process.env.SCALITY_SECRET_ACCESS_KEY;

if (!accessKeyId || !secretAccessKey) {
    console.error('[zenko-init] ERROR: SCALITY_ACCESS_KEY_ID and SCALITY_SECRET_ACCESS_KEY are required');
    process.exit(1);
}

// Comma separated, so the primary and the migration/mirror target are both
// prepared by one run.
const endpoints = (process.env.ZENKO_ENDPOINTS || 'http://zenko:8000')
    .split(',')
    .map((endpoint) => endpoint.trim())
    .filter(Boolean);

const buckets = [
    process.env.NEOPRINT_STORAGE_SEED_BUCKET_FILES || 'neoprint-files',
    process.env.NEOPRINT_STORAGE_SEED_BUCKET_RECEIPTS || 'neoprint-receipts',
    process.env.NEOPRINT_STORAGE_SEED_BUCKET_TEMP || 'neoprint-temp',
];

const log = (message) => console.log(`[zenko-init] ${message}`);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function prepare(endpoint) {
    const s3 = new S3({
        endpoint,
        accessKeyId,
        secretAccessKey,
        region: 'us-east-1',
        s3ForcePathStyle: true,
        sslEnabled: endpoint.startsWith('https'),
        maxRetries: 0,
    });

    // The container reports healthy once the process is up; the S3 API
    // behind it can take a few more seconds.
    for (let attempt = 1; ; attempt++) {
        try {
            await s3.listBuckets().promise();
            break;
        } catch (error) {
            // Only a store that is not up yet is worth waiting for. Anything
            // else - InvalidURI when ENDPOINT is missing, a wrong key - is a
            // configuration error that two minutes of retrying will not fix.
            const transient = error.code === 'NetworkingError'
                || error.code === 'ECONNREFUSED'
                || error.code === 'TimeoutError'
                || error.statusCode === 503;

            if (!transient) {
                throw new Error(`${endpoint} refused the request: ${error.code || error.message}`);
            }

            if (attempt >= 60) {
                throw new Error(`could not reach ${endpoint}: ${error.code || error.message}`);
            }

            await sleep(2000);
        }
    }

    for (const bucket of buckets) {
        try {
            // CloudServer, like S3 in us-east-1, answers 200 for a bucket the
            // caller already owns, so success here means "exists", not
            // necessarily "was just created".
            await s3.createBucket({ Bucket: bucket }).promise();
            log(`${endpoint}: ${bucket} ready`);
        } catch (error) {
            if (error.code === 'BucketAlreadyOwnedByYou' || error.code === 'BucketAlreadyExists') {
                log(`${endpoint}: ${bucket} already exists`);
            } else {
                throw error;
            }
        }
    }
}

(async () => {
    for (const endpoint of endpoints) {
        log(`preparing ${endpoint}`);
        await prepare(endpoint);
    }
    log('done');
})().catch((error) => {
    console.error(`[zenko-init] ERROR: ${error.message}`);
    process.exit(1);
});
