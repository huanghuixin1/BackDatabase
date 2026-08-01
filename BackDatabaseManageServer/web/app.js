const TOKEN_KEY = "backmanage_token";
const PASSWORD_KEY = "backmanage_password";
const state = { token: sessionStorage.getItem(TOKEN_KEY), nodes: [], selected: null, onlineTimer: null };

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
        <button class="secondary" data-action="test" data-id="${node.id}">测试连接</button>
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
  }, 5000);
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
  state.selected = node; $("details-title").textContent = node.name; $("details-dialog").showModal(); await refreshDetails();
}

async function refreshDetails() {
  if (!state.selected) return;
  $("status-output").textContent = "加载中…"; $("configs-output").textContent = "加载中…";
  try {
    const [status, configs] = await Promise.all([
      api(`/api/nodes/${state.selected.id}/status`), api(`/api/nodes/${state.selected.id}/configs`)
    ]);
    $("status-output").textContent = JSON.stringify(status, null, 2);
    $("configs-output").textContent = JSON.stringify(configs, null, 2);
  } catch (error) { $("status-output").textContent = error.message; $("configs-output").textContent = ""; }
}

async function testNode(id) {
  try { await api(`/api/nodes/${id}/status`); $("last-action").textContent = "连接正常"; showToast("节点连接正常"); }
  catch (error) { $("last-action").textContent = "连接失败"; showToast(error.message, true); }
}

async function restartNode(id) {
  if (!confirm("确定要重启这个 BackDatabase 节点吗？")) return;
  try { await api(`/api/nodes/${id}/restart`, { method: "POST" }); $("last-action").textContent = "已请求重启"; showToast("已发送重启请求"); }
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
$("refresh-button").addEventListener("click", () => loadNodes().then(() => showToast("列表已刷新")).catch((error) => showToast(error.message, true)));
$("node-form").addEventListener("submit", addNode);
$("reset-node").addEventListener("click", () => $("node-form").reset());
$("node-list").addEventListener("click", (event) => { const button = event.target.closest("button[data-action]"); if (!button) return; const id = button.dataset.id; const action = button.dataset.action; if (action === "details") openDetails(id); if (action === "test") testNode(id); if (action === "restart") restartNode(id); if (action === "delete") deleteNode(id); });
$("detail-refresh").addEventListener("click", refreshDetails);
$("detail-restart").addEventListener("click", () => state.selected && restartNode(state.selected.id));
document.querySelectorAll("[data-scroll]").forEach((button) => button.addEventListener("click", () => document.getElementById(button.dataset.scroll).scrollIntoView({ behavior: "smooth" })));
start();
