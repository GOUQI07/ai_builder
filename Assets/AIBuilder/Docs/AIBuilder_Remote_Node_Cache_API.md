# AI Builder Remote Node Cache API

This contract supports shared branch-node cache reuse across players. The Unity client keeps local cache behavior by default; remote cache is opt-in through `AiProviderSettings.enableRemoteNodeCache`.

## Auth

If `remoteCacheApiKeyEnvName` is configured and the environment variable is present, Unity sends:

```http
Authorization: Bearer <token>
```

## Get Branch Cache

```http
GET /branch-cache/{cacheKey}
```

Response:

```json
{
  "hit": true,
  "cacheKey": "story|source|choice|stats-band",
  "status": "Generated",
  "entry": {
    "storyId": "story",
    "cacheKey": "story|source|choice|stats-band",
    "sourceNodeId": "main_001",
    "choiceId": "branch_right",
    "resultNode": {},
    "statDelta": {},
    "textStatus": "Generated",
    "imageCacheKey": "story|img_style_v2|chapter|location|mood|event",
    "imageUrl": "https://cdn.example.com/assets/image.png",
    "panoramaCacheKey": "story|pano_style_v2|chapter|location|mood|event",
    "panoramaUrl": "https://cdn.example.com/assets/panorama.png",
    "status": "PendingReview"
  }
}
```

Miss response:

```json
{
  "hit": false,
  "cacheKey": "story|source|choice|stats-band",
  "status": "Missing"
}
```

## Upsert Branch Cache

```http
PUT /branch-cache/{cacheKey}
Content-Type: application/json
```

Request:

```json
{
  "cacheKey": "story|source|choice|stats-band",
  "entry": {}
}
```

The server should treat this as idempotent by `cacheKey`.

## Upload Asset

```http
POST /assets
Content-Type: application/json
```

Request:

```json
{
  "assetKey": "story|img_style_v2|chapter|location|mood|event",
  "contentType": "image/png",
  "bytesBase64": "..."
}
```

Response:

```json
{
  "assetKey": "story|img_style_v2|chapter|location|mood|event",
  "assetUrl": "https://cdn.example.com/assets/image.png"
}
```

## Server Notes

- `cacheKey` must be unique and idempotent.
- Concurrent identical misses should create one `Generating` record, not multiple AI jobs.
- Public reuse can filter by `status`; a conservative server should return only `Approved` or caller-owned `PendingReview` entries.
- `imageUrl` and `panoramaUrl` should point to CDN/object-storage URLs. Unity downloads them into its local cache before rendering.
