use super::{Request, RequestBuilderPerform};
use crate::settings::rewrite::Rewrite;
use crate::settings::targets::TargetProcess;
use anyhow::Context;
use autopulse_database::models::ScanEvent;
use autopulse_utils::get_url;
use futures::future::join_all;
use reqwest::header;
use serde::{Deserialize, Serialize};
use std::{collections::HashMap, fmt::Display, io::Cursor, path::Path};
use struson::{
    json_path,
    reader::{JsonReader, JsonStreamReader},
};
use tokio::sync::mpsc::UnboundedReceiver;
use tracing::{debug, error, info, warn};

#[doc(hidden)]
const fn default_true() -> bool {
    true
}

#[derive(Serialize, Clone, Deserialize)]
pub struct Emby {
    /// URL to the Jellyfin/Emby server
    pub url: String,
    /// API token for the Jellyfin/Emby server
    pub token: String,
    /// Metadata refresh mode (default: `FullRefresh`)
    #[serde(default)]
    pub metadata_refresh_mode: EmbyMetadataRefreshMode,
    /// Whether to try to refresh metadata for the item instead of scan (default: true)
    #[serde(default = "default_true")]
    pub refresh_metadata: bool,
    /// Rewrite path for the file
    pub rewrite: Option<Rewrite>,
    /// HTTP request options
    #[serde(default)]
    pub request: Request,
}

/// Metadata refresh mode for Jellyfin/Emby
#[derive(Serialize, Clone, Deserialize)]
#[serde(rename_all = "snake_case")]
#[derive(Default)]
pub enum EmbyMetadataRefreshMode {
    /// `none`
    None,
    /// `validation_only`
    ValidationOnly,
    /// `default`
    Default,
    /// `full_refresh`
    #[default]
    FullRefresh,
}

impl Display for EmbyMetadataRefreshMode {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let mode = match self {
            Self::None => "None",
            Self::ValidationOnly => "ValidationOnly",
            Self::Default => "Default",
            Self::FullRefresh => "FullRefresh",
        };

        write!(f, "{mode}")
    }
}

#[derive(Deserialize, Clone, Eq, PartialEq, Hash)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct Library {
    #[allow(dead_code)]
    name: String,
    locations: Vec<String>,
    item_id: String,
    collection_type: Option<String>,
}

#[derive(Deserialize, Clone)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct Item {
    id: String,
    path: Option<String>,
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct ScanPathRequest {
    path: String,
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct ScanPathsRequest {
    paths: Vec<String>,
}

#[derive(Deserialize, Clone)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct ScanPathResponse {
    #[serde(default)]
    item_id: String,
    #[allow(dead_code)]
    #[serde(default)]
    item_name: String,
    status: String,
    #[serde(default)]
    path: String,
    #[allow(dead_code)]
    #[serde(default)]
    message: String,
}

#[derive(Deserialize, Clone)]
#[serde(rename_all = "PascalCase")]
#[doc(hidden)]
struct ScanPathsResponse {
    results: Vec<ScanPathResponse>,
}

impl Emby {
    fn get_client(&self) -> anyhow::Result<reqwest::Client> {
        let mut headers = header::HeaderMap::new();

        headers.insert("X-Emby-Token", self.token.parse()?);
        headers.insert(
            "Authorization",
            format!("MediaBrowser Token=\"{}\"", self.token).parse()?,
        );
        headers.insert("Accept", "application/json".parse()?);

        self.request
            .client_builder(headers)
            .build()
            .map_err(Into::into)
    }

    async fn libraries(&self) -> anyhow::Result<Vec<Library>> {
        let client = self.get_client()?;
        let url = get_url(&self.url)?.join("Library/VirtualFolders")?;

        let res = client.get(url).perform().await?;

        Ok(res.json().await?)
    }

    fn get_libraries(&self, libraries: &[Library], path: &str) -> Vec<Library> {
        let ev_path = Path::new(path);
        let mut matched: Vec<Library> = vec![];

        for library in libraries {
            for location in &library.locations {
                let path = Path::new(location);

                if ev_path.starts_with(path) {
                    matched.push(library.clone());
                }
            }
        }

        matched
    }

    async fn _get_item(&self, library: &Library, path: &str) -> anyhow::Result<Option<Item>> {
        let client = self.get_client()?;
        let mut url = get_url(&self.url)?.join("Items")?;

        url.query_pairs_mut().append_pair("Recursive", "true");
        url.query_pairs_mut().append_pair("Fields", "Path");
        url.query_pairs_mut().append_pair("EnableImages", "false");
        if let Some(collection_type) = &library.collection_type {
            url.query_pairs_mut().append_pair(
                "IncludeItemTypes",
                match collection_type.as_str() {
                    "tvshows" => "Episode",
                    "books" => "Book",
                    "music" => "Audio",
                    "movie" => "VideoFile,Movie",
                    _ => "",
                },
            );
        }
        url.query_pairs_mut()
            .append_pair("ParentId", &library.item_id);
        url.query_pairs_mut()
            .append_pair("EnableTotalRecordCount", "false");

        let res = client.get(url).perform().await?;

        // Possibly unneeded unless we can use streams
        let bytes = res.bytes().await?;

        let mut json_reader = JsonStreamReader::new(Cursor::new(bytes));

        json_reader.seek_to(&json_path!["Items"])?;
        json_reader.begin_array()?;

        while json_reader.has_next()? {
            let item: Item = json_reader.deserialize_next()?;

            if item.path == Some(path.to_owned()) {
                return Ok(Some(item));
            }
        }

        Ok(None)
    }

    fn fetch_items(
        &self,
        library: &Library,
    ) -> anyhow::Result<(
        UnboundedReceiver<Item>,
        tokio::task::JoinHandle<anyhow::Result<()>>,
    )> {
        let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
        let limit = 1000;

        let client = self.get_client()?;
        let mut url = get_url(&self.url)?.join("Items")?;

        url.query_pairs_mut().append_pair("Recursive", "true");
        url.query_pairs_mut().append_pair("Fields", "Path");
        url.query_pairs_mut().append_pair("EnableImages", "false");
        url.query_pairs_mut()
            .append_pair("ParentId", &library.item_id);
        url.query_pairs_mut()
            .append_pair("EnableTotalRecordCount", "false");
        url.query_pairs_mut()
            .append_pair("Limit", &limit.to_string());
        if let Some(collection_type) = &library.collection_type {
            url.query_pairs_mut().append_pair(
                "IncludeItemTypes",
                match collection_type.as_str() {
                    "tvshows" => "Episode",
                    "books" => "Book",
                    "music" => "Audio",
                    "movie" => "VideoFile,Movie",
                    _ => "",
                },
            );
        }

        let handle = tokio::spawn(async move {
            let mut page = 0;

            loop {
                let mut page_url = url.clone();
                page_url
                    .query_pairs_mut()
                    .append_pair("StartIndex", &(page * limit).to_string());

                let res = client.get(page_url).perform().await?;

                let bytes = res.bytes().await?;

                let mut json_reader = JsonStreamReader::new(Cursor::new(bytes));

                json_reader.seek_to(&json_path!["Items"])?;
                json_reader.begin_array()?;

                let mut found_items_count = 0;

                while json_reader.has_next()? {
                    let item: Item = json_reader.deserialize_next()?;

                    tx.send(item)?;

                    found_items_count += 1;
                }

                if found_items_count < limit {
                    break;
                }

                page += 1;
            }

            drop(tx);

            Ok(())
        });

        Ok((rx, handle))
    }

    async fn get_items<'a>(
        &self,
        library: &Library,
        events: Vec<&'a ScanEvent>,
    ) -> anyhow::Result<(Vec<(&'a ScanEvent, Item)>, Vec<&'a ScanEvent>)> {
        let (mut rx, handle) = self.fetch_items(library)?;

        let mut found_in_library = Vec::new();
        let mut not_found_in_library = events.clone();

        while let Some(item) = rx.recv().await {
            if let Some(ev) = events
                .iter()
                .find(|ev| item.path == Some(ev.get_path(&self.rewrite)))
            {
                found_in_library.push((*ev, item.clone()));
                not_found_in_library.retain(|&e| e.id != ev.id);

                if not_found_in_library.is_empty() {
                    break;
                }
            }
        }

        handle.abort();

        Ok((found_in_library, not_found_in_library))
    }

    async fn refresh_item(&self, item: &Item) -> anyhow::Result<()> {
        let client = self.get_client()?;
        let mut url = get_url(&self.url)?.join(&format!("Items/{}/Refresh", item.id))?;

        url.query_pairs_mut().append_pair(
            "MetadataRefreshMode",
            &self.metadata_refresh_mode.to_string(),
        );
        url.query_pairs_mut()
            .append_pair("ImageRefreshMode", &self.metadata_refresh_mode.to_string());
        url.query_pairs_mut()
            .append_pair("ReplaceAllMetadata", "true");
        url.query_pairs_mut().append_pair("Recursive", "true");

        // TODO: Possible options in future?
        url.query_pairs_mut()
            .append_pair("ReplaceAllImages", "false");
        url.query_pairs_mut()
            .append_pair("RegenerateTrickplay", "false");

        client.post(url).perform().await.map(|_| ())
    }

    /// Attempt a targeted scan via the TargetedScan plugin's POST /Library/ScanPath endpoint.
    /// Returns Ok with the response on success (including PathNotFound/ParentNotFound),
    /// or Err if the plugin is not installed or the response cannot be parsed.
    async fn targeted_scan(&self, path: &str) -> anyhow::Result<ScanPathResponse> {
        let client = self.get_client()?;
        let url = get_url(&self.url)?.join("Library/ScanPath")?;

        let body = ScanPathRequest {
            path: path.to_string(),
        };

        let response = client
            .post(url)
            .header("Content-Type", "application/json")
            .json(&body)
            .send()
            .await?;

        let status = response.status();
        let body_text = response.text().await.unwrap_or_default();

        // Parse response body even for non-200 statuses — Jellyfin returns 404
        // with a valid JSON body for PathNotFound/ParentNotFound
        if let Ok(result) = serde_json::from_str::<ScanPathResponse>(&body_text) {
            return Ok(result);
        }

        Err(anyhow::anyhow!(
            "ScanPath failed with {}: {}",
            status,
            body_text
        ))
    }

    /// Batch targeted scan via POST /Library/ScanPaths.
    /// Returns Ok with per-path results, or Err if the endpoint is unavailable.
    async fn targeted_scan_batch(&self, paths: Vec<String>) -> anyhow::Result<ScanPathsResponse> {
        let client = self.get_client()?;
        let url = get_url(&self.url)?.join("Library/ScanPaths")?;

        let body = ScanPathsRequest { paths };

        let response = client
            .post(url)
            .header("Content-Type", "application/json")
            .json(&body)
            .send()
            .await?;

        let status = response.status();
        let body_text = response.text().await.unwrap_or_default();

        if let Ok(result) = serde_json::from_str::<ScanPathsResponse>(&body_text) {
            return Ok(result);
        }

        Err(anyhow::anyhow!(
            "ScanPaths failed with {}: {}",
            status,
            body_text
        ))
    }
}

impl TargetProcess for Emby {
    async fn process(&self, evs: &[&ScanEvent]) -> anyhow::Result<Vec<String>> {
        let libraries = self
            .libraries()
            .await
            .context("failed to fetch libraries")?;

        let mut succeeded: HashMap<String, bool> = HashMap::new();

        // Map all events to their rewritten paths, validating each matches a library
        let mut all_with_paths: Vec<(&ScanEvent, String)> = Vec::new();
        for ev in evs {
            let ev_path = ev.get_path(&self.rewrite);
            let matched_libraries = self.get_libraries(&libraries, &ev_path);
            if matched_libraries.is_empty() {
                debug!("no matching library for {}, skipping (not a failure)", ev_path);
                succeeded.insert(ev.id.clone(), true);
                continue;
            }
            all_with_paths.push((*ev, ev_path));
        }

        if all_with_paths.is_empty() {
            return Ok(vec![]);
        }

        // Tier 1: Batch targeted scan for ALL items (plugin handles both new and existing)
        let batch_paths: Vec<String> = all_with_paths.iter().map(|(_, p)| p.clone()).collect();
        let mut remaining: Vec<(&ScanEvent, String)>;

        match self.targeted_scan_batch(batch_paths).await {
            Ok(batch_result) => {
                remaining = Vec::new();
                let result_map: HashMap<&str, &ScanPathResponse> = batch_result.results
                    .iter()
                    .map(|r| {
                        let key = if r.path.is_empty() { r.message.as_str() } else { r.path.as_str() };
                        (key, r)
                    })
                    .collect();

                for (ev, ev_path) in &all_with_paths {
                    match result_map.get(ev_path.as_str()) {
                        Some(r) if r.status == "Created" || r.status == "Refreshed" || r.status == "Discovered" => {
                            info!(
                                "targeted scan succeeded for {}: {} ({})",
                                ev_path, r.item_id, r.status
                            );
                            *succeeded.entry(ev.id.clone()).or_insert(true) &= true;
                        }
                        Some(r) if r.status == "PathNotFound" || r.status == "ParentNotFound" => {
                            debug!(
                                "path no longer exists for {} ({}), skipping",
                                ev_path, r.status
                            );
                            *succeeded.entry(ev.id.clone()).or_insert(true) &= true;
                        }
                        _ => {
                            remaining.push((*ev, ev_path.clone()));
                        }
                    }
                }
            }
            Err(e) => {
                warn!("batch targeted scan failed ({}), trying individual requests", e);
                remaining = all_with_paths.clone();
            }
        }

        // Tier 2: Individual targeted scans with exponential backoff
        let backoff_delays = [5, 15, 30];
        for attempt in 0..=backoff_delays.len() {
            if remaining.is_empty() {
                break;
            }

            if attempt > 0 {
                let delay = backoff_delays[attempt - 1];
                info!(
                    "retrying {} targeted scans in {}s (attempt {}/{})",
                    remaining.len(), delay, attempt, backoff_delays.len()
                );
                tokio::time::sleep(std::time::Duration::from_secs(delay)).await;
            }

            let scan_futures: Vec<_> = remaining.iter().map(|(ev, ev_path)| {
                let path = ev_path.clone();
                async move {
                    let result = self.targeted_scan(&path).await;
                    (*ev, path, result)
                }
            }).collect();
            let results = join_all(scan_futures).await;

            let mut still_remaining = Vec::new();
            for (ev, ev_path, result) in results {
                match result {
                    Ok(scan_result)
                        if scan_result.status == "PathNotFound"
                            || scan_result.status == "ParentNotFound" =>
                    {
                        debug!(
                            "path no longer exists for {} ({}), skipping",
                            ev_path, scan_result.status
                        );
                        *succeeded.entry(ev.id.clone()).or_insert(true) &= true;
                    }
                    Ok(scan_result) => {
                        info!(
                            "targeted scan succeeded for {}: {} ({})",
                            ev_path, scan_result.item_id, scan_result.status
                        );
                        *succeeded.entry(ev.id.clone()).or_insert(true) &= true;
                    }
                    Err(e) => {
                        warn!(
                            "targeted scan failed for {}: {}, will retry",
                            ev_path, e
                        );
                        still_remaining.push((ev, ev_path));
                    }
                }
            }
            remaining = still_remaining;
        }

        // Tier 3: Fall back to library enumeration if plugin is unavailable
        if !remaining.is_empty() && self.refresh_metadata {
            warn!(
                "targeted scan plugin unavailable for {} items, falling back to library enumeration",
                remaining.len()
            );

            let mut to_find: HashMap<Library, Vec<&ScanEvent>> = HashMap::new();
            for (ev, ev_path) in &remaining {
                let matched_libraries = self.get_libraries(&libraries, ev_path);
                for library in matched_libraries {
                    to_find.entry(library).or_insert_with(Vec::new).push(*ev);
                }
            }

            for (library, library_events) in to_find {
                let (found_in_library, not_found_in_library) = self
                    .get_items(&library, library_events.clone())
                    .await
                    .with_context(|| {
                        format!(
                            "failed to fetch items for library: {}",
                            library.name.clone()
                        )
                    })?;

                for (ev, item) in found_in_library {
                    match self.refresh_item(&item).await {
                        Ok(()) => {
                            debug!("refreshed item: {}", item.id);
                            *succeeded.entry(ev.id.clone()).or_insert(true) &= true;
                        }
                        Err(e) => {
                            error!("failed to refresh item: {}", e);
                            succeeded.insert(ev.id.clone(), false);
                        }
                    }
                }

                for ev in not_found_in_library {
                    error!(
                        "item not found after all methods: {}",
                        ev.get_path(&self.rewrite)
                    );
                    succeeded.insert(ev.id.clone(), false);
                }
            }
        } else if !remaining.is_empty() {
            for (ev, ev_path) in &remaining {
                error!(
                    "targeted scan failed for {} after all retries",
                    ev_path
                );
                succeeded.insert(ev.id.clone(), false);
            }
        }

        Ok(succeeded
            .iter()
            .filter_map(|(k, v)| if *v { Some(k.clone()) } else { None })
            .collect())
    }
}
