<p align="center">
  <img src="jellyfin-plugin/thumb.png" alt="Targeted Scans" width="200"/>
</p>

<h1 align="center">Targeted Scans</h1>

<p align="center">
  Jellyfin & Emby plugins for instant, targeted library scanning — no full library scans needed.<br/>
  Includes a modified <a href="https://github.com/dan-online/autopulse">Autopulse</a> fork that connects Sonarr/Radarr to the plugins automatically.
</p>

---

## The Problem

When new media arrives on disk, Jellyfin and Emby require a full library scan to discover it. On large libraries (1000+ shows), this can take **minutes to hours**.

## The Solution

**Targeted Scans** adds a `POST /Library/ScanPath` API endpoint to Jellyfin and Emby that instantly creates the new item in the database and queues a metadata refresh — **under a second** regardless of library size.

The included **Autopulse fork** bridges Sonarr/Radarr to the plugin automatically:

```
Sonarr/Radarr → (webhook) → Autopulse → (ScanPath API) → Jellyfin/Emby Plugin → Item Created
```

### How the plugin works

1. Receives a filesystem path (e.g. `/media/TV/Breaking Bad/Season 1/S01E01.mkv`)
2. Walks up the directory tree to find the nearest known ancestor in the library database
3. Creates all intermediate items (Series, Season, Episode) from the top down
4. Queues metadata refresh for each created item
5. Returns the item ID and status

---

## Full Setup Guide

### Step 1: Install the Plugin

#### Jellyfin (10.11+)

**Option A: Plugin Repository (Recommended)**

1. Go to **Dashboard** > **Plugins** > **Repositories**
2. Add a new repository:
   - **Name:** `Targeted Scans`
   - **URL:** `https://raw.githubusercontent.com/d3v1l1989/targeted-scans/main/manifest.json`
3. Go to **Catalog** and search for **TargetedScan**
4. Install and restart Jellyfin

**Option B: Manual Install**

1. Download `jellyfin-targeted-scan-vX.X.X.zip` from [Releases](https://github.com/d3v1l1989/targeted-scans/releases/latest)
2. Extract to your Jellyfin plugins directory:
   ```
   {JellyfinDataPath}/plugins/TargetedScan/
   ```
3. Restart Jellyfin

#### Emby

1. Download `EmbyTargetedScan.dll` from [Releases](https://github.com/d3v1l1989/targeted-scans/releases/latest)
2. Copy directly to your Emby plugins directory (**not** in a subfolder):
   ```
   {EmbyConfigPath}/plugins/EmbyTargetedScan.dll
   ```
3. Restart Emby

---

### Step 2: Deploy Autopulse

The Autopulse fork receives webhooks from Sonarr/Radarr and calls the `ScanPath` endpoint on your media server. A pre-built Docker image is available on Docker Hub.

#### `docker-compose.yml`

```yaml
services:
  autopulse:
    restart: unless-stopped
    container_name: autopulse
    image: d3v1l1989/autopulse-targeted:latest
    hostname: autopulse
    healthcheck:
      test: ["CMD-SHELL", "wget --quiet --tries=1 -O /dev/null http://127.0.0.1:2875/stats || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 3
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
      - AUTOPULSE__APP__DATABASE_URL=sqlite://data/autopulse.db
    volumes:
      - ./data:/app                # config.yaml goes here
      - /mnt:/mnt                  # mount your media paths (must match Sonarr/Radarr paths)
      - /etc/localtime:/etc/localtime:ro
    ports:
      - "2875:2875"

  autopulse-ui:
    restart: unless-stopped
    container_name: autopulse-ui
    image: danonline/autopulse:ui
    hostname: autopulse-ui
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
      - FORCE_SERVER_URL=true
      - DEFAULT_SERVER_URL=http://autopulse:2875
      - ORIGIN=http://localhost:2880       # change to your domain if using reverse proxy
    ports:
      - "2880:2880"
```

#### `data/config.yaml`

```yaml
app:
  log_level: debug
  database_url: sqlite:///app/autopulse.db

auth:
  username: your_username
  password: your_password

opts:
  check_path: true
  max_retries: 5
  default_timer_wait: 10

triggers:
  sonarr:
    type: sonarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
  radarr:
    type: radarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
  lidarr:
    type: lidarr
    rewrite:
      from: /mnt/media
      to: /mnt/media

targets:
  plex:
    type: plex
    url: http://plex:32400
    token: YOUR_PLEX_TOKEN
    refresh: true
  emby:
    type: emby
    url: http://emby:8096
    token: YOUR_EMBY_API_KEY
    refresh_metadata: true
  jellyfin:
    type: jellyfin
    url: http://jellyfin:8096
    token: YOUR_JELLYFIN_API_KEY
    refresh_metadata: true
```

> **Path rewriting:** If Sonarr sees files at `/downloads/tv/...` but Jellyfin sees them at `/media/tv/...`, set `from: /downloads/tv` and `to: /media/tv`. If they share the same mount paths, set both to the same value.

Each trigger becomes a webhook endpoint at `http://autopulse:2875/triggers/<name>` — you'll use these URLs in the next step.

```bash
mkdir -p data
# create your config.yaml in data/
docker compose up -d
```

This starts two containers:
- **autopulse** on port `2875` — the API that receives webhooks and triggers scans
- **autopulse-ui** on port `2880` — a web dashboard to monitor scan events

---

### Step 3: Configure Sonarr/Radarr Webhooks

This connects your *arr apps to Autopulse. When Sonarr/Radarr downloads or renames media, it sends a webhook to Autopulse, which triggers the plugin.

#### Sonarr

1. Go to **Settings** > **Connect** > **+** > **Webhook**
2. Fill in:
   - **Name:** `Autopulse`
   - **URL:** `http://autopulse:2875/triggers/sonarr`
   - **Method:** `POST`
   - **Username:** your autopulse `auth.username`
   - **Password:** your autopulse `auth.password`
3. Select events:
   - **On File Import**
   - **On File Upgrade**
   - **On Import Complete**
   - **On Episode File Delete**
   - **On Episode File Delete For Upgrade**
4. Click **Test** then **Save**

#### Radarr

1. Go to **Settings** > **Connect** > **+** > **Webhook**
2. Fill in:
   - **Name:** `Autopulse`
   - **URL:** `http://autopulse:2875/triggers/radarr`
   - **Method:** `POST`
   - **Username:** your autopulse `auth.username`
   - **Password:** your autopulse `auth.password`
3. Select events:
   - **On File Import**
   - **On File Upgrade**
   - **On Movie File Delete**
   - **On Movie File Delete For Upgrade**
4. Click **Test** then **Save**

#### Multiple Instances

If you run multiple Sonarr/Radarr instances (e.g. for different languages or qualities), create a separate trigger for each:

```yaml
triggers:
  sonarr:
    type: sonarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
  sonarranime:
    type: sonarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
  radarr:
    type: radarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
  radarranime:
    type: radarr
    rewrite:
      from: /mnt/media
      to: /mnt/media
```

Then point each instance's webhook URL to its matching trigger name (e.g. `http://autopulse:2875/triggers/sonarranime`).

---

### Step 4: Verify the Full Pipeline

1. **Grab something in Sonarr** — trigger a manual search for an episode
2. **Watch Autopulse logs:**
   ```bash
   docker logs -f autopulse
   ```
3. You should see:
   - Webhook received from Sonarr
   - Path extracted and rewritten
   - `ScanPath` called on Jellyfin/Emby
   - Item created or refreshed
4. **Check Jellyfin/Emby** — the episode should appear immediately with metadata

---

## API Reference

Both plugins expose identical endpoints. You only need these if you're calling the plugin directly (Autopulse handles this automatically).

### `POST /Library/ScanPath`

Scan a single path.

**Headers:**
```
Content-Type: application/json
Authorization: MediaBrowser Token="YOUR_API_KEY"
```

**Request:**
```json
{
  "Path": "/media/TV/Breaking Bad/Season 1/S01E01.mkv"
}
```

**Response:**
```json
{
  "ItemId": "abc123...",
  "ItemName": "S01E01",
  "Status": "Created",
  "Message": "Item created and metadata refresh queued"
}
```

### `POST /Library/ScanPaths`

Scan multiple paths in a single batch.

**Request:**
```json
{
  "Paths": [
    "/media/TV/Breaking Bad/Season 1/S01E01.mkv",
    "/media/TV/Breaking Bad/Season 1/S01E02.mkv"
  ]
}
```

**Response:**
```json
{
  "Results": [
    {
      "ItemId": "abc123...",
      "ItemName": "S01E01",
      "Status": "Created",
      "Message": "/media/TV/Breaking Bad/Season 1/S01E01.mkv"
    }
  ]
}
```

### Status Values

| Status | Description |
|--------|-------------|
| `Created` | New item created and metadata refresh queued |
| `Refreshed` | Item already existed, metadata refresh queued |
| `PathNotFound` | Path does not exist on the filesystem |
| `ParentNotFound` | Could not find a parent library item for the path |
| `Failed` | Scan failed |

---

## License

Jellyfin and Emby plugins are provided as-is. The autopulse fork is licensed under the same terms as the [original project](https://github.com/dan-online/autopulse).
