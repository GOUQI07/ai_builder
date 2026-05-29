# AI Builder Remote Node Cache Backend

This is a small dependency-free implementation of `AIBuilder_Remote_Node_Cache_API.md`.
It runs on the system Python 2.7 available in this environment and stores data in SQLite plus local asset files.

## Run

```bash
PORT=8080 python remote_node_cache_server.py
```

Optional Bearer auth:

```bash
REMOTE_CACHE_API_KEY=dev-secret PORT=8080 python remote_node_cache_server.py
```

If `REMOTE_CACHE_API_KEY` is set, requests to `GET /branch-cache/{cacheKey}`,
`PUT /branch-cache/{cacheKey}`, and `POST /assets` must include:

```http
Authorization: Bearer dev-secret
```

`GET /assets/{filename}` is public so Unity can download uploaded asset URLs.

## Environment

- `PORT`: HTTP port, default `8080`.
- `HOST`: bind host, default `0.0.0.0`.
- `REMOTE_CACHE_API_KEY`: optional bearer token.
- `REMOTE_CACHE_API_KEY_ENV_NAME`: optional alternate env var name for the bearer token.
- `REMOTE_CACHE_DATA_DIR`: storage directory, default `./remote_cache_data`.
- `REMOTE_CACHE_DB_PATH`: SQLite path, default `${REMOTE_CACHE_DATA_DIR}/remote_cache.sqlite3`.
- `REMOTE_CACHE_ASSET_DIR`: asset file directory, default `${REMOTE_CACHE_DATA_DIR}/assets`.
- `REMOTE_CACHE_PUBLIC_BASE_URL`: public CDN/proxy base URL used in `assetUrl`.
- `REMOTE_CACHE_REUSE_POLICY`: `all` by default; set `approved-or-owner` to hide entries except `Approved` or caller-owned `PendingReview`.
- `REMOTE_CACHE_MAX_BODY_BYTES`: max JSON body size, default `26214400`.
- `REMOTE_CACHE_CORS_ORIGIN`: optional CORS origin header.

Owner filtering uses `X-Owner-Id` or `X-Client-Id`.

## Quick Check

```bash
python test_remote_node_cache_server.py
```

Example calls:

```bash
curl http://localhost:8080/healthz

curl 'http://localhost:8080/branch-cache/story%7Csource%7Cchoice%7Cstats-band'

curl -X PUT 'http://localhost:8080/branch-cache/story%7Csource%7Cchoice%7Cstats-band' \
  -H 'Content-Type: application/json' \
  --data '{"cacheKey":"story|source|choice|stats-band","entry":{"textStatus":"Generated","status":"PendingReview","resultNode":{},"statDelta":{}}}'

curl -X POST http://localhost:8080/assets \
  -H 'Content-Type: application/json' \
  --data '{"assetKey":"story|img_style_v2|chapter|location|mood|event","contentType":"image/png","bytesBase64":"iVBORw0KGgo="}'
```
