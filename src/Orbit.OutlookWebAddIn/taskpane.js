/* global Office */

const DEFAULT_HOST = "http://127.0.0.1:8741";
const SETTINGS_HOST = "orbitHostUrl";
const SETTINGS_KEY = "orbitApiKey";

const els = {
  statusLine: document.getElementById("statusLine"),
  helloBanner: document.getElementById("helloBanner"),
  mailSubject: document.getElementById("mailSubject"),
  mailFrom: document.getElementById("mailFrom"),
  mailMessageId: document.getElementById("mailMessageId"),
  memo: document.getElementById("memo"),
  project: document.getElementById("project"),
  projectHint: document.getElementById("projectHint"),
  sendBtn: document.getElementById("sendBtn"),
  refreshProjectsBtn: document.getElementById("refreshProjectsBtn"),
  hostUrl: document.getElementById("hostUrl"),
  apiKey: document.getElementById("apiKey"),
  saveSettingsBtn: document.getElementById("saveSettingsBtn"),
  result: document.getElementById("result"),
};

/** @type {{ subject: string, from: string, internetMessageId: string, itemId: string, conversationId: string }} */
let mailContext = {
  subject: "",
  from: "",
  internetMessageId: "",
  itemId: "",
  conversationId: "",
};

function setStatus(text) {
  els.statusLine.textContent = text;
}

function setResult(text, kind) {
  els.result.textContent = text;
  els.result.classList.remove("ok", "err");
  if (kind) {
    els.result.classList.add(kind);
  }
}

function readSettings() {
  const hostUrl = (Office.context.roamingSettings.get(SETTINGS_HOST) || DEFAULT_HOST).trim();
  const apiKey = (Office.context.roamingSettings.get(SETTINGS_KEY) || "").trim();
  return { hostUrl: hostUrl.replace(/\/$/, ""), apiKey };
}

function writeSettings(hostUrl, apiKey) {
  Office.context.roamingSettings.set(SETTINGS_HOST, hostUrl.replace(/\/$/, ""));
  Office.context.roamingSettings.set(SETTINGS_KEY, apiKey.trim());
  return new Promise((resolve, reject) => {
    Office.context.roamingSettings.saveAsync((result) => {
      if (result.status === Office.AsyncResultStatus.Succeeded) {
        resolve();
      } else {
        reject(result.error || new Error("Could not save settings."));
      }
    });
  });
}

function authHeaders(apiKey) {
  const headers = { Accept: "application/json" };
  if (apiKey) {
    headers.Authorization = `Bearer ${apiKey}`;
  }
  return headers;
}

async function bootstrapFromHost(hostUrl) {
  try {
    const res = await fetch(`${hostUrl}/v1/outlook-addin/bootstrap`, {
      method: "GET",
      headers: { Accept: "application/json" },
    });
    if (!res.ok) {
      return null;
    }
    return await res.json();
  } catch {
    return null;
  }
}

async function ensureConnection() {
  let { hostUrl, apiKey } = readSettings();
  els.hostUrl.value = hostUrl;
  els.apiKey.value = apiKey;

  const candidates = [];
  const push = (url) => {
    const cleaned = (url || "").trim().replace(/\/$/, "");
    if (cleaned && !candidates.includes(cleaned)) {
      candidates.push(cleaned);
    }
  };
  push(hostUrl);
  push(DEFAULT_HOST);
  // Common when Core Host bind is LAN but loopback is also exposed.
  push("http://localhost:8741");

  let lastError = null;
  for (const candidate of candidates) {
    try {
      const boot = await bootstrapFromHost(candidate);
      if (boot?.hostBaseUrl) {
        hostUrl = String(boot.hostBaseUrl).replace(/\/$/, "");
        els.hostUrl.value = hostUrl;
      } else {
        hostUrl = candidate;
        els.hostUrl.value = hostUrl;
      }
      if (!apiKey && boot?.apiKey) {
        apiKey = String(boot.apiKey);
        els.apiKey.value = apiKey;
      }

      // Confirm this is Orbit Core (shape { projects: [...] }), not Orbit-as-agent ([]).
      const probe = await fetch(`${hostUrl}/v1/projects`, { headers: authHeaders(apiKey) });
      if (!probe.ok) {
        lastError = new Error(`Projects HTTP ${probe.status} from ${hostUrl}`);
        continue;
      }
      const data = await probe.json();
      if (!data || !Array.isArray(data.projects)) {
        lastError = new Error(
          `Wrong service on ${hostUrl} (expected Orbit Core Host). Stop Orbit-as-agent runtime if it is using port 8741.`,
        );
        continue;
      }

      return { hostUrl, apiKey };
    } catch (err) {
      lastError = err;
    }
  }

  throw lastError || new Error("Could not reach Orbit Core Host.");
}

function fillMailUi() {
  els.mailSubject.textContent = mailContext.subject || "(no subject)";
  els.mailFrom.textContent = mailContext.from || "—";
  els.mailMessageId.textContent = mailContext.internetMessageId || "—";
}

function loadMailContext() {
  const item = Office.context.mailbox.item;
  if (!item) {
    setStatus("No mail item in this pane.");
    return;
  }

  mailContext.subject = item.subject || "";
  mailContext.itemId = item.itemId || "";
  mailContext.conversationId = item.conversationId || "";
  mailContext.internetMessageId = item.internetMessageId || "";

  const from = item.from;
  if (from) {
    const name = from.displayName || "";
    const email = from.emailAddress || "";
    mailContext.from = name && email ? `${name} <${email}>` : name || email || "";
  }

  fillMailUi();
}

async function loadProjects(hostUrl, apiKey) {
  els.projectHint.textContent = "Loading projects…";
  try {
    const res = await fetch(`${hostUrl}/v1/projects`, {
      headers: authHeaders(apiKey),
    });
    if (!res.ok) {
      const body = await res.text();
      if (res.status === 404) {
        throw new Error(
          `Projects 404 from ${hostUrl} — is Orbit Core Host running, or is another app (Orbit-as-agent) on that port?`,
        );
      }
      throw new Error(`Projects HTTP ${res.status}: ${body.slice(0, 180)}`);
    }
    const data = await res.json();
    if (!data || !Array.isArray(data.projects)) {
      throw new Error(
        `Unexpected /v1/projects shape from ${hostUrl} (expected { projects: [...] }). Another service may be bound to that port.`,
      );
    }
    const projects = data.projects;
    const previous = els.project.value;
    els.project.innerHTML = "";
    const none = document.createElement("option");
    none.value = "";
    none.textContent = "No project (limbo)";
    els.project.appendChild(none);
    for (const p of projects) {
      const opt = document.createElement("option");
      opt.value = p.id;
      opt.textContent = p.name || p.code || p.id;
      els.project.appendChild(opt);
    }
    if ([...els.project.options].some((o) => o.value === previous)) {
      els.project.value = previous;
    }
    els.projectHint.textContent =
      projects.length === 0 ? "No active projects in Orbit yet." : `${projects.length} project(s) loaded.`;
  } catch (err) {
    els.projectHint.textContent = err.message || String(err);
    throw err;
  }
}

async function sendToOrbit() {
  setResult("");
  const memo = els.memo.value.trim();
  if (!memo) {
    setResult("Write a short memo before sending.", "err");
    els.memo.focus();
    return;
  }

  els.sendBtn.disabled = true;
  setResult("Sending to Orbit…");
  try {
    const { hostUrl, apiKey } = await ensureConnection();
    const projectId = els.project.value.trim();
    const payload = {
      internetMessageId: mailContext.internetMessageId || null,
      itemId: mailContext.itemId || null,
      conversationId: mailContext.conversationId || null,
      subject: mailContext.subject || null,
      memo,
      projectIds: projectId ? [projectId] : [],
      preferSelection: true,
    };

    const res = await fetch(`${hostUrl}/v1/emails/from-outlook`, {
      method: "POST",
      headers: {
        ...authHeaders(apiKey),
        "Content-Type": "application/json",
      },
      body: JSON.stringify(payload),
    });

    const text = await res.text();
    let data = null;
    try {
      data = text ? JSON.parse(text) : null;
    } catch {
      data = null;
    }

    if (!res.ok) {
      const msg = data?.error?.message || data?.message || text || `HTTP ${res.status}`;
      throw new Error(msg);
    }

    const emailId = data?.id || data?.emailId || "";
    setResult(
      emailId
        ? `Sent. Orbit email ${emailId}. Hermes will pick up the memo.`
        : "Sent to Orbit. Hermes will pick up the memo.",
      "ok",
    );
    els.memo.value = "";
  } catch (err) {
    setResult(err.message || String(err), "err");
  } finally {
    els.sendBtn.disabled = false;
  }
}

async function init() {
  els.helloBanner.hidden = false;
  setStatus("Orbit hello — add-in loaded.");
  loadMailContext();

  els.saveSettingsBtn.addEventListener("click", async () => {
    try {
      await writeSettings(els.hostUrl.value || DEFAULT_HOST, els.apiKey.value || "");
      setResult("Connection saved.", "ok");
      const conn = await ensureConnection();
      await loadProjects(conn.hostUrl, conn.apiKey);
    } catch (err) {
      setResult(err.message || String(err), "err");
    }
  });

  els.refreshProjectsBtn.addEventListener("click", async () => {
    try {
      const conn = await ensureConnection();
      await loadProjects(conn.hostUrl, conn.apiKey);
      setResult("Projects refreshed.", "ok");
    } catch (err) {
      setResult(err.message || String(err), "err");
    }
  });

  els.sendBtn.addEventListener("click", () => {
    void sendToOrbit();
  });

  try {
    const conn = await ensureConnection();
    await loadProjects(conn.hostUrl, conn.apiKey);
    els.sendBtn.disabled = false;
    setStatus("Ready — write a memo and send to Orbit.");
  } catch (err) {
    els.sendBtn.disabled = false;
    setStatus("Add-in loaded. Connect Core Host to load projects.");
    setResult(err.message || String(err), "err");
  }
}

Office.onReady((info) => {
  if (info.host === Office.HostType.Outlook) {
    void init();
  } else {
    setStatus("Open this add-in from Outlook.");
  }
});
