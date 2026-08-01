const TOKEN_KEY = "backmanage_token";
const PASSWORD_KEY = "backmanage_password";
const state = { token: sessionStorage.getItem(TOKEN_KEY), nodes: [], selected: null, configs: [], editingTask: null, onlineTimer: null, refreshingAll: false };

const $ = (id) => document.getElementById(id);

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (state.token) headers.set("Authorization", `Bearer ${state.token}`);
  const response = await fetch(path, { ...options, headers });
  let body = {};
  const text = await response.text();
  if (text) {
    try { body = JSON.parse(text); } catch { body = { message: text }; }
  }
  if (response.status === 401) {
    state.token = null;
    sessionStorage.removeItem(TOKEN_KEY);
    showLogin("登录已失效，请重新登录。");
  }
  if (!response.ok) throw new Error(body.message || body.error || body.detail || `请求失败（${response.status}）`);
  return body;
}

async function start() {
  try {
    const session = await api("/api/session");
    if (session.required && !session.authenticated) { showLogin(); return; }
    showApp();
    await loadNodes();
  } catch (error) { showLogin(error.message); }
}

function showLogin(message = "") {
  stopOnlineRefresh();
  $("login-view").classList.remove("hidden");
  $("app").classList.add("hidden");
  $("login-message").textContent = message;
  const savedPassword = localStorage.getItem(PASSWORD_KEY);
  if (savedPassword) {
    $("login-password").value = savedPassword;
    $("remember-password").checked = true;
  } else {
    $("remember-password").checked = false;
  }
}

function showApp() {
  $("login-view").classList.add("hidden");
  $("app").classList.remove("hidden");
  startOnlineRefresh();
}

async function loadNodes() {
  state.nodes = await api("/api/nodes");
  renderNodes();
}

function renderNodes() {
  const enabled = state.nodes.filter((node) => node.enabled).length;
  const online = state.nodes.filter((node) => node.online === true).length;
  $("node-count").textContent = state.nodes.length;
  $("enabled-count").textContent = enabled;
  $("last-action").textContent = `${online} 在线`;
  $("node-empty").classList.toggle("hidden", state.nodes.length !== 0);
  $("node-list").innerHTML = state.nodes.map((node) => `
    <article class="node-card ${node.enabled ? "" : "disabled"}">
      <div class="node-card-head"><div><span class="status-dot ${onlineClass(node)}"></span><strong>${escapeHtml(node.name)}</strong></div><span class="pill ${node.online === true ? "online" : node.online === false ? "offline" : ""}">${onlineText(node)}</span></div>
      <p class="node-url">${escapeHtml(node.baseUrl)}</p>
      <p class="node-meta">${onlineDescription(node)}</p>
      <div class="node-actions">
        <button class="secondary" data-action="details" data-id="${node.id}">查看</button>
        <button class="secondary" data-action="refresh" data-id="${node.id}" ${state.refreshingAll ? "disabled" : ""}>${state.refreshingAll ? "刷新中…" : "刷新状态"}</button>
        <button class="warning" data-action="restart" data-id="${node.id}">重启</button>
        <button class="danger-link" data-action="delete" data-id="${node.id}">删除</button>
      </div>
    </article>`).join("");
}

function onlineClass(node) {
  if (!node.enabled || node.online == null) return "unknown";
  return node.online ? "on" : "off";
}

function onlineText(node) {
  if (!node.enabled) return "已停用";
  if (node.online == null) return "未检测";
  return node.online ? "在线" : "离线";
}

function onlineDescription(node) {
  if (!node.enabled) return "节点当前未启用";
  if (node.online == null) return "等待首次检测";
  const checked = node.lastCheckedAtUtc ? new Date(node.lastCheckedAtUtc).toLocaleTimeString() : "";
  return node.online ? `最近检测：${checked}` : `最近检测：${checked} · ${escapeHtml(node.onlineError || "连接失败")}`;
}

function startOnlineRefresh() {
  if (state.onlineTimer) return;
  state.onlineTimer = setInterval(() => {
    loadNodes().catch((error) => console.warn("刷新节点在线状态失败", error));
  }, 180000);
}

function stopOnlineRefresh() {
  if (!state.onlineTimer) return;
  clearInterval(state.onlineTimer);
  state.onlineTimer = null;
}

function persistRememberedPassword(password) {
  if ($("remember-password").checked) {
    localStorage.setItem(PASSWORD_KEY, password);
  } else {
    localStorage.removeItem(PASSWORD_KEY);
  }
}

async function addNode(event) {
  event.preventDefault();
  const message = $("node-message");
  message.textContent = "";
  try {
    await api("/api/nodes", { method: "POST", body: JSON.stringify({
      name: $("node-name").value.trim(), baseUrl: $("node-url").value.trim(),
      webPassword: $("node-password").value, enabled: $("node-enabled").checked
    }) });
    $("node-form").reset(); $("node-enabled").checked = true;
    await loadNodes(); showToast("节点已添加"); window.scrollTo({ top: 0, behavior: "smooth" });
  } catch (error) { message.textContent = error.message; }
}

async function openDetails(id) {
  const node = state.nodes.find((item) => item.id === id); if (!node) return;
  state.selected = node;
  state.configs = [];
  $("details-title").textContent = node.name;
  $("restart-required").classList.add("hidden");
  $("details-dialog").showModal();
  await refreshDetails();
}

async function refreshDetails() {
  if (!state.selected) return;
  $("status-output").textContent = "加载中…";
  $("configs-output").innerHTML = '<div class="empty compact-empty">加载中…</div>';
  try {
    const [status, configs] = await Promise.all([
      api(`/api/nodes/${state.selected.id}/status`), api(`/api/nodes/${state.selected.id}/configs`)
    ]);
    $("status-output").textContent = JSON.stringify(status, null, 2);
    state.configs = Array.isArray(configs) ? configs : [];
    renderConfigs();
  } catch (error) {
    $("status-output").textContent = error.message;
    $("configs-output").innerHTML = `<div class="empty compact-empty">${escapeHtml(error.message)}</div>`;
  }
}

function renderConfigs() {
  if (!state.configs.length) {
    $("configs-output").innerHTML = '<div class="empty compact-empty">暂无备份任务</div>';
    return;
  }
  $("configs-output").innerHTML = state.configs.map((config) => `
    <article class="task-card">
      <div>
        <div class="task-card-title"><strong>${escapeHtml(config.fileName)}</strong><span class="pill">${escapeHtml(config.dbType)}</span></div>
        ${config.error ? `<p class="form-message">${escapeHtml(config.error)}</p>` : `
          <p>${escapeHtml(config.user)}@${escapeHtml(config.host)}:${escapeHtml(config.port)} · ${escapeHtml(config.databases)}</p>
          <p>计划：${escapeHtml(config.backtime)} · 保留：${config.maxFiles} · 目录：${escapeHtml(config.saveDir)}</p>`}
      </div>
      <div class="task-actions">
        <button class="secondary" type="button" data-task-action="edit" data-file-name="${escapeHtml(config.fileName)}" ${config.error ? "disabled" : ""}>编辑</button>
        <button class="danger-link" type="button" data-task-action="delete" data-file-name="${escapeHtml(config.fileName)}">删除</button>
      </div>
    </article>`).join("");
}

function openTaskDialog(config = null) {
  state.editingTask = config;
  $("task-form").reset();
  $("task-message").textContent = "";
  $("task-dialog-title").textContent = config ? "编辑备份任务" : "新增备份任务";
  $("task-file-name").readOnly = Boolean(config);
  $("task-file-name").value = config?.fileName || "";
  $("task-db-type").value = normalizeDbType(config?.dbType || "mysql");
  $("task-host").value = config?.host || "127.0.0.1";
  $("task-port").value = config?.port || "3306";
  $("task-user").value = config?.user || "root";
  $("task-password").value = "";
  $("task-clear-password").checked = false;
  $("task-databases").value = config?.databases || "";
  $("task-backtime").value = config?.backtime || "60";
  $("task-max-files").value = config?.maxFiles || 180;
  $("task-save-dir").value = config?.saveDir || "/backup/";
  $("task-password-hint").textContent = config
    ? `数据库密码：${config.passwordConfigured ? "已配置；留空表示保留" : "未配置"}`
    : "数据库密码可留空。";
  $("task-dialog").showModal();
}

function closeTaskDialog() {
  $("task-dialog").close();
  state.editingTask = null;
}

function normalizeDbType(dbType) {
  if (["postgres", "postgresql"].includes(dbType)) return "pgsql";
  return dbType;
}

async function saveTask(event) {
  event.preventDefault();
  if (!state.selected) return;
  $("task-message").textContent = "";
  const fileName = $("task-file-name").value.trim();
  const payload = {
    fileName,
    dbType: $("task-db-type").value,
    host: $("task-host").value.trim(),
    port: $("task-port").value.trim(),
    user: $("task-user").value.trim(),
    password: $("task-password").value || null,
    clearPassword: $("task-clear-password").checked,
    databases: $("task-databases").value.trim(),
    backtime: $("task-backtime").value.trim(),
    maxFiles: Number($("task-max-files").value),
    saveDir: $("task-save-dir").value.trim()
  };
  try {
    const editing = Boolean(state.editingTask);
    const path = editing
      ? `/api/nodes/${state.selected.id}/configs/${encodeURIComponent(state.editingTask.fileName)}`
      : `/api/nodes/${state.selected.id}/configs`;
    await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(payload) });
    closeTaskDialog();
    markRestartRequired();
    await loadConfigs();
    showToast(editing ? "备份任务已更新" : "备份任务已创建");
  } catch (error) {
    $("task-message").textContent = error.message;
  }
}

async function deleteTask(fileName) {
  if (!state.selected || !confirm(`确定删除备份任务 ${fileName} 吗？`)) return;
  try {
    await api(`/api/nodes/${state.selected.id}/configs/${encodeURIComponent(fileName)}`, { method: "DELETE" });
    markRestartRequired();
    await loadConfigs();
    showToast("备份任务已删除");
  } catch (error) { showToast(error.message, true); }
}

async function loadConfigs() {
  if (!state.selected) return;
  const configs = await api(`/api/nodes/${state.selected.id}/configs`);
  state.configs = Array.isArray(configs) ? configs : [];
  renderConfigs();
}

function markRestartRequired() {
  $("restart-required").classList.remove("hidden");
}

async function refreshNode(id, button = null) {
  setButtonBusy(button, true, "刷新中…");
  try {
    const refreshed = await api(`/api/nodes/${id}/refresh`, { method: "POST" });
    const index = state.nodes.findIndex((node) => node.id === id);
    if (index >= 0) state.nodes[index] = refreshed;
    renderNodes();
    $("last-action").textContent = refreshed.online ? "节点在线" : "节点离线";
    showToast(refreshed.online ? "节点在线状态已刷新" : `节点离线：${refreshed.onlineError || "连接失败"}`, !refreshed.online);
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setButtonBusy(button, false, "刷新状态");
  }
}

async function refreshAllNodes(button = null) {
  if (state.refreshingAll) return;
  state.refreshingAll = true;
  setButtonBusy(button, true, "刷新中…");
  renderNodes();
  try {
    state.nodes = await api("/api/nodes/refresh", { method: "POST" });
    showToast("全部节点状态已刷新");
  } catch (error) {
    showToast(error.message, true);
  } finally {
    state.refreshingAll = false;
    renderNodes();
    setButtonBusy(button, false, "刷新全部节点");
  }
}

function setButtonBusy(button, busy, text) {
  if (!button) return;
  button.disabled = busy;
  button.textContent = text;
}

async function restartNode(id) {
  if (!confirm("确定要重启这个 BackDatabase 节点吗？")) return;
  try { await api(`/api/nodes/${id}/restart`, { method: "POST" }); $("last-action").textContent = "已请求重启"; $("restart-required").classList.add("hidden"); showToast("已发送重启请求"); }
  catch (error) { showToast(error.message, true); }
}

async function deleteNode(id) {
  if (!confirm("删除节点记录不会停止远端进程，确定继续吗？")) return;
  try { await api(`/api/nodes/${id}`, { method: "DELETE" }); await loadNodes(); showToast("节点已删除"); }
  catch (error) { showToast(error.message, true); }
}

function showToast(message, error = false) {
  const toast = $("toast"); toast.textContent = message; toast.className = `toast show${error ? " error" : ""}`;
  setTimeout(() => { toast.className = "toast"; }, 3500);
}

function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[char])); }

$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault(); $("login-message").textContent = "";
  const password = $("login-password").value;
  try {
    const result = await api("/api/auth/login", { method: "POST", body: JSON.stringify({ key: password }) });
    state.token = result.token;
    sessionStorage.setItem(TOKEN_KEY, state.token);
    persistRememberedPassword(password);
    $("login-password").value = "";
    showApp();
    await loadNodes();
  }
  catch (error) { $("login-message").textContent = error.message; }
});

$("remember-password").addEventListener("change", () => {
  if (!$("remember-password").checked) localStorage.removeItem(PASSWORD_KEY);
});

$("logout-button").addEventListener("click", async () => { try { await api("/api/auth/logout", { method: "POST" }); } catch { } state.token = null; sessionStorage.removeItem(TOKEN_KEY); showLogin(); });
$("refresh-button").addEventListener("click", (event) => refreshAllNodes(event.currentTarget));
$("node-form").addEventListener("submit", addNode);
$("reset-node").addEventListener("click", () => $("node-form").reset());
$("node-list").addEventListener("click", (event) => { const button = event.target.closest("button[data-action]"); if (!button) return; const id = button.dataset.id; const action = button.dataset.action; if (action === "details") openDetails(id); if (action === "refresh") refreshNode(id, button); if (action === "restart") restartNode(id); if (action === "delete") deleteNode(id); });
$("detail-refresh").addEventListener("click", refreshDetails);
$("detail-restart").addEventListener("click", () => state.selected && restartNode(state.selected.id));
$("add-task").addEventListener("click", () => openTaskDialog());
$("task-form").addEventListener("submit", saveTask);
$("task-close").addEventListener("click", closeTaskDialog);
$("task-cancel").addEventListener("click", closeTaskDialog);
$("configs-output").addEventListener("click", (event) => {
  const button = event.target.closest("button[data-task-action]");
  if (!button) return;
  const config = state.configs.find((item) => item.fileName === button.dataset.fileName);
  if (button.dataset.taskAction === "edit" && config) openTaskDialog(config);
  if (button.dataset.taskAction === "delete") deleteTask(button.dataset.fileName);
});
document.querySelectorAll("[data-scroll]").forEach((button) => button.addEventListener("click", () => document.getElementById(button.dataset.scroll).scrollIntoView({ behavior: "smooth" })));
start();
