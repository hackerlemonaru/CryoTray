/* Release lookup, cache, download data, checksum copy, and polling. */
import {
  state, t, $, setText, formatDate, formatSize, copyButton, copyStatus
} from "./core.js";
import {
  RELEASE_API, RELEASE_VERSION_URL, RELEASE_CACHE_KEY, RELEASE_BLOCK_KEY,
  RELEASE_API_FRESH_MS, RELEASE_POLL_MS, RELEASE_EVENT_THROTTLE_MS, SEED_RELEASE
} from "./config.js";

function isReleaseRecord(release) {
  return Boolean(
    release &&
    typeof release.tag === "string" &&
    release.setup &&
    release.zip &&
    typeof release.setup.name === "string" &&
    typeof release.setup.url === "string" &&
    typeof release.zip.name === "string" &&
    typeof release.zip.url === "string"
  );
}

function readReleaseCache() {
  try {
    const raw = localStorage.getItem(RELEASE_CACHE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || !isReleaseRecord(parsed.release)) return null;
    return {
      release: parsed.release,
      apiCheckedAt: Number(parsed.apiCheckedAt) || 0,
      versionCheckedAt: Number(parsed.versionCheckedAt) || 0
    };
  } catch {
    return null;
  }
}

function writeReleaseCache(release, apiCheckedAt) {
  try {
    localStorage.setItem(RELEASE_CACHE_KEY, JSON.stringify({
      release,
      apiCheckedAt: Number(apiCheckedAt) || 0,
      versionCheckedAt: Date.now()
    }));
  } catch {
    // Release still works for this page view.
  }
}

function readReleaseBlockedUntil() {
  try {
    return Number(localStorage.getItem(RELEASE_BLOCK_KEY)) || 0;
  } catch {
    return 0;
  }
}

function setReleaseBlockedUntil(value) {
  try {
    if (value) localStorage.setItem(RELEASE_BLOCK_KEY, String(value));
    else localStorage.removeItem(RELEASE_BLOCK_KEY);
  } catch {
    // The in-memory value still prevents repeated calls.
  }
}

let releaseApiBlockedUntil = readReleaseBlockedUntil();
let releaseLoading = false;
let releaseLastAttempt = 0;


function releaseStatusText() {
  if (state.releaseStatus === "latest") return t("release.statusLatest");
  if (state.releaseStatus === "version") return t("release.statusVersion");
  if (state.releaseStatus === "cached") {
    return t("release.statusUnavailable", { tag: state.release.tag });
  }
  return t("release.statusChecking");
}

function updateReleaseStatus() {
  const status = $("#release-status");
  status.classList.remove("is-ok", "is-fallback");
  if (state.releaseStatus === "latest") status.classList.add("is-ok");
  if (state.releaseStatus === "version" || state.releaseStatus === "cached") {
    status.classList.add("is-fallback");
  }
  status.textContent = releaseStatusText();
}

function checksumCommand() {
  return "Get-FileHash .\\" + state.release.setup.name + " -Algorithm SHA256";
}

function renderRelease() {
  const release = state.release;
  const version = release.tag.startsWith("v") ? release.tag : "v" + release.tag;
  setText($("#release-version"), version);
  const date = $("#release-date");
  if (release.publishedAt) {
    date.dateTime = release.publishedAt;
    date.textContent = formatDate(release.publishedAt);
  } else {
    date.removeAttribute("datetime");
    date.textContent = t("release.latestLabel");
  }

  const setup = $("#download-setup");
  setup.href = release.setup.url;
  const zip = $("#download-zip");
  zip.href = release.zip.url;

  setText($("#setup-filename"), release.setup.name);
  setText($("#zip-filename"), release.zip.name);
  setText($("#setup-size"), typeof release.setup.size === "number" ? formatSize(release.setup.size) : t("release.latestLabel"));
  setText($("#zip-size"), typeof release.zip.size === "number" ? formatSize(release.zip.size) : t("release.latestLabel"));

  $("#setup-sha-link").href = release.setup.shaUrl;
  $("#zip-sha-link").href = release.zip.shaUrl;
  setText($("#checksum-command"), checksumCommand());
  copyButton.dataset.command = checksumCommand();
  updateReleaseStatus();
}

function releaseFromApi(data) {
  const assets = Array.isArray(data.assets) ? data.assets : [];
  const setup = assets.find((asset) => /setup.*\.exe$/i.test(asset.name) && !/\.sha256$/i.test(asset.name));
  const zip = assets.find((asset) => /\.zip$/i.test(asset.name) && !/update/i.test(asset.name) && !/\.sha256$/i.test(asset.name));
  if (!setup || !zip) throw new Error("Release assets missing");

  return {
    tag: data.tag_name || SEED_RELEASE.tag,
    publishedAt: data.published_at || null,
    htmlUrl: data.html_url || SEED_RELEASE.htmlUrl,
    setup: {
      name: setup.name,
      size: setup.size,
      url: setup.browser_download_url,
      shaUrl: setup.browser_download_url + ".sha256"
    },
    zip: {
      name: zip.name,
      size: zip.size,
      url: zip.browser_download_url,
      shaUrl: zip.browser_download_url + ".sha256"
    }
  };
}

function releaseFromVersion(version, previous) {
  const clean = String(version || "").trim().replace(/^v/i, "");
  if (!/^\d+\.\d+\.\d+$/.test(clean)) return null;
  const tag = "v" + clean;
  if (previous && previous.tag === tag) return previous;
  const base = "https://github.com/ZerroDevs/CryoTray/releases/download/" + tag;
  return {
    tag,
    publishedAt: null,
    htmlUrl: "https://github.com/ZerroDevs/CryoTray/releases/tag/" + tag,
    setup: {
      name: "CryoTray-Setup-" + clean + ".exe",
      size: null,
      url: base + "/CryoTray-Setup-" + clean + ".exe",
      shaUrl: base + "/CryoTray-Setup-" + clean + ".exe.sha256"
    },
    zip: {
      name: "CryoTray-" + clean + "-win-x64.zip",
      size: null,
      url: base + "/CryoTray-" + clean + "-win-x64.zip",
      shaUrl: base + "/CryoTray-" + clean + "-win-x64.zip.sha256"
    }
  };
}

async function fetchLatestVersion() {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 6000);
  try {
    const response = await fetch(RELEASE_VERSION_URL + "?t=" + Date.now(), {
      cache: "no-store",
      headers: { Accept: "text/plain" },
      signal: controller.signal
    });
    if (!response.ok) return null;
    return await response.text();
  } catch {
    return null;
  } finally {
    window.clearTimeout(timeout);
  }
}

async function fetchLatestRelease() {
  if (Date.now() < releaseApiBlockedUntil) return null;
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 8000);
  try {
    const response = await fetch(RELEASE_API, {
      headers: { Accept: "application/vnd.github+json" },
      cache: "no-store",
      signal: controller.signal
    });
    if (!response.ok) {
      const remaining = response.headers.get("X-RateLimit-Remaining");
      const reset = Number(response.headers.get("X-RateLimit-Reset"));
      if ((response.status === 403 || response.status === 429) && remaining === "0" && reset) {
        releaseApiBlockedUntil = reset * 1000;
      } else if (response.status === 403 || response.status === 429) {
        releaseApiBlockedUntil = Date.now() + RELEASE_API_FRESH_MS;
      }
      setReleaseBlockedUntil(releaseApiBlockedUntil);
      return null;
    }
    const release = releaseFromApi(await response.json());
    releaseApiBlockedUntil = 0;
    setReleaseBlockedUntil(0);
    return release;
  } catch {
    return null;
  } finally {
    window.clearTimeout(timeout);
  }
}

async function loadRelease() {
  if (releaseLoading) return;
  releaseLoading = true;
  releaseLastAttempt = Date.now();
  const cached = readReleaseCache();
  if (cached) state.release = cached.release;
  try {
    if (cached && Date.now() - cached.apiCheckedAt < RELEASE_API_FRESH_MS) {
      state.releaseStatus = "latest";
      renderRelease();
      return;
    }

    let release = null;
    if (Date.now() >= releaseApiBlockedUntil) {
      release = await fetchLatestRelease();
    }
    if (release) {
      state.release = release;
      state.releaseStatus = "latest";
      writeReleaseCache(release, Date.now());
      renderRelease();
      return;
    }

    const rawVersion = await fetchLatestVersion();
    const derived = rawVersion ? releaseFromVersion(rawVersion, state.release) : null;
    if (derived) {
      const apiCheckedAt = cached && cached.release.tag === derived.tag ? cached.apiCheckedAt : 0;
      state.release = derived;
      state.releaseStatus = "version";
      writeReleaseCache(derived, apiCheckedAt);
    } else {
      state.releaseStatus = "cached";
    }
    renderRelease();
  } finally {
    releaseLoading = false;
  }
}

function startReleaseSync() {
  window.setInterval(() => {
    if (!document.hidden) loadRelease();
  }, RELEASE_POLL_MS);

  const refresh = () => {
    if (document.hidden) return;
    if (Date.now() - releaseLastAttempt < RELEASE_EVENT_THROTTLE_MS) return;
    loadRelease();
  };
  document.addEventListener("visibilitychange", refresh);
  window.addEventListener("focus", refresh);
  window.addEventListener("online", () => loadRelease());
}

let copyResetTimer = 0;
let copyState = "";

function updateCopyStatus() {
  if (copyState === "success") {
    copyStatus.textContent = t("release.copied");
    copyStatus.classList.remove("is-error");
    copyButton.textContent = t("release.copied");
  } else if (copyState === "error") {
    copyStatus.textContent = t("release.copyError");
    copyStatus.classList.add("is-error");
    copyButton.textContent = t("release.copy");
  } else {
    copyStatus.textContent = "";
    copyStatus.classList.remove("is-error");
    copyButton.textContent = t("release.copy");
  }
}

function updateCopyStatusFromState() {
  updateCopyStatus();
}

async function copyCommand() {
  const command = copyButton.dataset.command || checksumCommand();
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(command);
    } else {
      const area = document.createElement("textarea");
      area.value = command;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      const copied = document.execCommand("copy");
      area.remove();
      if (!copied) throw new Error("Copy unavailable");
    }
    copyState = "success";
  } catch {
    copyState = "error";
  }

  updateCopyStatus();
  window.clearTimeout(copyResetTimer);
  copyResetTimer = window.setTimeout(() => {
    copyState = "";
    updateCopyStatus();
  }, 2200);
}


export {
  readReleaseCache, renderRelease, loadRelease, startReleaseSync,
  updateCopyStatusFromState, copyCommand
};
