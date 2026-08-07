const TOKEN_KEY = "backmanage_token";
const PASSWORD_KEY = "backmanage_password";
const state = { token: sessionStorage.getItem(TOKEN_KEY), nodes: [], selected: null, selectedHotReload: false, configs: [], editingTask: null, onlineTimer: null, refreshingAll: false, consoleTabs: [], activeConsoleTabId: null, consoleMinimized: false };
const copyState = { sourceId: null, configs: [] };

const $ = (id) => document.getElementById(id);

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (options.body && !(options.body instanceof FormData) && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
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
  loadUpdatePackages().catch((error) => console.warn("加载更新包失败", error));
}

async function loadNodes() {
  state.nodes = await api("/api/nodes");
  renderNodes();
  if ($("update-nodes")) renderUpdateNodes();
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
      <div class="node-card-head"><div><span class="status-dot ${onlineClass(node)}"></span><strong>${escapeHtml(node.name)}</strong></div><span class="node-badges">${node.version ? `<span class="pill version-pill" title="back 版本">v${escapeHtml(node.version)}</span>` : ""}<span class="pill ${node.online === true ? "online" : node.online === false ? "offline" : ""}">${onlineText(node)}</span></span></div>
      <p class="node-url">${escapeHtml(node.baseUrl)}</p>
      <p class="node-meta">${onlineDescription(node)}</p>
      <div class="node-actions">
        <button class="primary" data-action="console" data-id="${node.id}">打开控制台</button>
        <button class="secondary" data-action="details" data-id="${node.id}">查看</button>
        <button class="secondary" data-action="refresh" data-id="${node.id}" ${state.refreshingAll ? "disabled" : ""}>${state.refreshingAll ? "刷新中…" : "刷新状态"}</button>
        <button class="warning" data-action="restart" data-id="${node.id}">重启</button>
        <button class="danger-link" data-action="delete" data-id="${node.id}">删除</button>
      </div>
    </article>`).join("");
}

async function openNodeConsole(id) {
  const node = state.nodes.find((item) => item.id === id);
  if (!node) return;

  if (!state.consoleTabs.some((tab) => tab.id === id)) {
    // 控制台 iframe 直连 back 节点，跨域读不到它的登录框；
    // 由 server 侧拼一个带 #webPassword 片段的地址（口令不发给服务器、不进日志），
    // back 页面取用后立即清除，实现「打开控制台即已填好密码」。
    let src;
    try {
      src = (await api(`/api/nodes/${id}/console-url`, { method: "POST" })).src;
    } catch (error) {
      showToast(`获取控制台地址失败：${error.message}`, true);
      return;
    }
    state.consoleTabs.push({ id, name: node.name, baseUrl: node.baseUrl, src });
  }

  state.activeConsoleTabId = id;
  state.consoleMinimized = false;
  renderNodeConsole();
}

function closeNodeConsole(id) {
  const index = state.consoleTabs.findIndex((tab) => tab.id === id);
  if (index < 0) return;

  state.consoleTabs.splice(index, 1);
  if (state.activeConsoleTabId === id)
    state.activeConsoleTabId = state.consoleTabs[index]?.id || state.consoleTabs[index - 1]?.id || null;
  renderNodeConsole();
}

function refreshNodeTab(id) {
  const frame = $("node-frame-container").querySelector(`iframe[data-node-id="${id}"]`);
  if (!frame) return;
  const tab = state.consoleTabs.find((item) => item.id === id);
  const base = tab?.src || frame.src;
  // 跨域 iframe 不能调 contentWindow.location.reload()，改 src 触发重新加载；
  // 时间戳加在 query 上保证真正重新请求，#k= 片段保留在 fragment 里，back 会自动重新登录。
  const hashIndex = base.indexOf("#");
  const url = hashIndex >= 0
    ? `${base.slice(0, hashIndex)}?t=${Date.now()}${base.slice(hashIndex)}`
    : `${base}${base.includes("?") ? "&" : "?"}t=${Date.now()}`;
  frame.src = url;
}

function renderNodeConsole() {
  // 控制台是覆盖层，收起时只隐藏容器、保留 iframe，回来时不用重新加载和登录
  const hasTabs = state.consoleTabs.length > 0;
  if (!hasTabs) state.consoleMinimized = false;
  $("node-console").classList.toggle("hidden", !hasTabs || state.consoleMinimized);

  const restore = $("console-restore");
  restore.classList.toggle("hidden", !hasTabs || !state.consoleMinimized);
  const active = state.consoleTabs.find((tab) => tab.id === state.activeConsoleTabId) || state.consoleTabs[0];
  if (hasTabs && state.consoleMinimized)
    restore.textContent = `▲ 控制台（${state.consoleTabs.length}）· ${active.name}`;

  $("node-tabs").replaceChildren(...state.consoleTabs.map((tab) => {
    const button = document.createElement("div");
    button.className = `node-tab${tab.id === state.activeConsoleTabId ? " active" : ""}`;
    button.tabIndex = 0;
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", String(tab.id === state.activeConsoleTabId));
    button.append(Object.assign(document.createElement("span"), { className: "node-tab-label", textContent: tab.name }));
    button.addEventListener("click", () => { state.activeConsoleTabId = tab.id; renderNodeConsole(); });
    button.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); state.activeConsoleTabId = tab.id; renderNodeConsole(); }
    });

    const refresh = document.createElement("button");
    refresh.className = "node-tab-refresh";
    refresh.type = "button";
    refresh.textContent = "↻";
    refresh.title = "刷新该控制台页面";
    refresh.setAttribute("aria-label", `刷新 ${tab.name}`);
    refresh.addEventListener("click", (event) => { event.stopPropagation(); refreshNodeTab(tab.id); });

    const close = document.createElement("button");
    close.className = "node-tab-close";
    close.type = "button";
    close.textContent = "×";
    close.setAttribute("aria-label", `关闭 ${tab.name}`);
    close.addEventListener("click", (event) => { event.stopPropagation(); closeNodeConsole(tab.id); });
    button.append(refresh, close);
    return button;
  }));

  const frameContainer = $("node-frame-container");
  const tabIds = new Set(state.consoleTabs.map((tab) => tab.id));
  frameContainer.querySelectorAll("iframe[data-node-id]").forEach((frame) => {
    if (!tabIds.has(frame.dataset.nodeId)) frame.remove();
  });
  state.consoleTabs.forEach((tab) => {
    let frame = frameContainer.querySelector(`iframe[data-node-id="${tab.id}"]`);
    if (!frame) {
      frame = document.createElement("iframe");
      frame.className = "node-frame";
      frame.dataset.nodeId = tab.id;
      frame.src = tab.src;
      frame.title = `${tab.name} 控制台`;
      frameContainer.append(frame);
    }
    frame.classList.toggle("active", tab.id === state.activeConsoleTabId);
  });
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

function openNodeDialog() {
  $("node-form").reset();
  $("node-enabled").checked = true;
  $("node-message").textContent = "";
  $("node-dialog").showModal();
  $("node-name").focus();
}

function closeNodeDialog() {
  $("node-dialog").close();
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
    closeNodeDialog();
    await loadNodes(); showToast("节点已添加");
  } catch (error) { message.textContent = error.message; }
}

async function openDetails(id) {
  const node = state.nodes.find((item) => item.id === id); if (!node) return;
  state.selected = node;
  state.selectedHotReload = false;
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
    state.selectedHotReload = status?.backupConfigHotReload === true;
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
          <p>计划：${escapeHtml(config.backtime)} · 保留：${config.maxFiles} · 目录：${escapeHtml(config.saveDir)}</p>
          ${config.dbMaxFiles ? `<p>每库保留：${escapeHtml(config.dbMaxFiles)}</p>` : ''}`}
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
  // 编辑时回显远端节点已保存的数据库密码明文，方便核对；新建时留空
  $("task-password").value = config?.password || "";
  $("task-password").type = "password";
  $("task-clear-password").checked = false;
  $("task-databases").value = config?.databases || "";
  $("task-backtime").value = config?.backtime || "60";
  $("task-max-files").value = config?.maxFiles || 180;
  $("task-db-max-files").value = config?.dbMaxFiles || "";
  $("task-save-dir").value = config?.saveDir || "/backup/";
  $("task-password-hint").textContent = config
    ? (config.passwordConfigured
        ? "已显示当前数据库密码；可直接修改，留空则保留原值。"
        : "当前未配置数据库密码。")
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
    dbMaxFiles: $("task-db-max-files").value.trim(),
    saveDir: $("task-save-dir").value.trim()
  };
  try {
    const editing = Boolean(state.editingTask);
    const path = editing
      ? `/api/nodes/${state.selected.id}/configs/${encodeURIComponent(state.editingTask.fileName)}`
      : `/api/nodes/${state.selected.id}/configs`;
    await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(payload) });
    closeTaskDialog();
    markRestartRequiredIfNeeded();
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
    markRestartRequiredIfNeeded();
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

async function openCopyDialog() {
  if (!state.selected) return;
  copyState.sourceId = null;
  copyState.configs = [];
  $("copy-target-name").value = state.selected.name;
  $("copy-message").textContent = "";
  $("copy-message").className = "form-message";
  $("copy-select-all").checked = true;
  $("copy-overwrite").checked = false;

  const candidates = state.nodes.filter((node) => node.id !== state.selected.id && node.enabled);
  const select = $("copy-source-node");
  select.innerHTML = "";
  for (const node of candidates) {
    const option = document.createElement("option");
    option.value = node.id;
    option.textContent = `${node.name}（${node.baseUrl}）`;
    select.append(option);
  }
  select.disabled = candidates.length === 0;

  $("copy-configs").innerHTML = candidates.length === 0
    ? '<div class="empty compact-empty">没有其它可复制的节点，请先在概览中添加节点。</div>'
    : '<div class="empty compact-empty">加载中…</div>';

  $("copy-dialog").showModal();
  if (candidates.length > 0) await loadCopySourceConfigs(select.value);
}

async function loadCopySourceConfigs(sourceId) {
  copyState.sourceId = sourceId;
  $("copy-configs").innerHTML = '<div class="empty compact-empty">加载中…</div>';
  $("copy-select-all").checked = true;
  try {
    const configs = await api(`/api/nodes/${sourceId}/configs`);
    copyState.configs = Array.isArray(configs) ? configs.filter((config) => !config.error) : [];
    renderCopyConfigs();
  } catch (error) {
    copyState.configs = [];
    $("copy-configs").innerHTML = `<div class="empty compact-empty">${escapeHtml(error.message)}</div>`;
  }
}

function renderCopyConfigs() {
  if (!copyState.configs.length) {
    $("copy-configs").innerHTML = '<div class="empty compact-empty">该节点暂无备份任务。</div>';
    return;
  }
  $("copy-configs").innerHTML = copyState.configs.map((config, index) => `
    <label class="check copy-task">
      <input type="checkbox" data-copy-index="${index}" checked>
      <span><strong>${escapeHtml(config.fileName)}</strong> · ${escapeHtml(config.dbType)} · ${escapeHtml(config.user)}@${escapeHtml(config.host)} · ${escapeHtml(config.databases)}</span>
    </label>`).join("");
}

async function copyTasks(event) {
  event.preventDefault();
  if (!state.selected || !copyState.sourceId) return;
  const fileNames = copyState.configs
    .filter((config, index) => document.querySelector(`input[data-copy-index="${index}"]`)?.checked)
    .map((config) => config.fileName);
  if (!fileNames.length) { $("copy-message").textContent = "请至少选择一个任务。"; return; }

  $("copy-message").textContent = "";
  const submit = $("copy-submit");
  submit.disabled = true;
  submit.textContent = "复制中…";
  try {
    const result = await api(`/api/nodes/${state.selected.id}/configs/copy`, { method: "POST", body: JSON.stringify({
      sourceNodeId: copyState.sourceId,
      fileNames,
      overwrite: $("copy-overwrite").checked
    }) });
    const parts = [];
    if (result.copied?.length) parts.push(`已复制 ${result.copied.length} 个`);
    if (result.skipped?.length) parts.push(`跳过 ${result.skipped.length} 个同名任务`);
    if (result.failed?.length) parts.push(`失败 ${result.failed.length} 个`);
    $("copy-message").textContent = parts.join("，") || "没有任务被复制。";
    $("copy-message").className = "form-message" + ((result.failed?.length || 0) > 0 ? "" : " success");
    if (result.failed?.length) {
      const detail = result.failed.map((item) => `${item.fileName}：${item.message}`).join("；");
      console.warn("复制任务失败明细", result.failed);
      $("copy-message").title = detail;
    }
    if (result.copied?.length) {
      markRestartRequiredIfNeeded();
      await loadConfigs();
      showToast(`已复制 ${result.copied.length} 个任务到 ${state.selected.name}`);
    }
  } catch (error) {
    $("copy-message").textContent = error.message;
  } finally {
    submit.disabled = false;
    submit.textContent = "复制到目标节点";
  }
}

function closeCopyDialog() {
  $("copy-dialog").close();
  copyState.sourceId = null;
  copyState.configs = [];
}

function markRestartRequiredIfNeeded() {
  if (!state.selectedHotReload)
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

async function loadUpdatePackages() {
  const packages = await api("/api/updates");
  const select = $("update-package");
  const current = select.value;
  select.innerHTML = '<option value="">请选择已上传的程序包</option>' + packages.map(item => `<option value="${escapeHtml(item.name)}">${escapeHtml(item.name)} (${Math.round(item.size / 1024 / 1024 * 10) / 10} MB)</option>`).join("");
  if (packages.some(item => item.name === current)) select.value = current;
  $("update-packages").textContent = packages.length ? `已上传 ${packages.length} 个程序包` : "暂无程序包";
}

function renderUpdateNodes() {
  $("update-nodes").innerHTML = state.nodes.map(node => `<label class="check"><input type="checkbox" data-update-node="${node.id}" ${node.enabled ? "" : "disabled"}> ${escapeHtml(node.name)} ${node.version ? `(v${escapeHtml(node.version)})` : ""}</label>`).join("");
}

async function uploadUpdatePackage() {
  const file = $("update-file").files[0];
  if (!file) return showToast("请选择 zip 更新包", true);
  const form = new FormData(); form.append("file", file);
  const button = $("update-upload"); button.disabled = true; button.textContent = "上传中…";
  try {
    const result = await api("/api/updates/upload", { method: "POST", body: form });
    await loadUpdatePackages(); $("update-package").value = result.name; showToast("程序包上传成功");
  } catch (error) { showToast(error.message, true); }
  finally { button.disabled = false; button.textContent = "上传程序包"; }
}

async function deployUpdatePackage() {
  const packageName = $("update-package").value;
  const nodeIds = [...document.querySelectorAll("#update-nodes input[data-update-node]:checked")].map(input => input.dataset.updateNode);
  if (!packageName) return showToast("请先选择程序包", true);
  if (!nodeIds.length) return showToast("请至少选择一个节点", true);
  const button = $("update-deploy"); button.disabled = true; button.textContent = "更新中…"; $("update-result").textContent = "正在逐个节点更新，请稍候…";
  try {
    const result = await api("/api/updates/deploy", { method: "POST", body: JSON.stringify({ packageName, nodeIds }) });
    $("update-result").textContent = JSON.stringify(result.results, null, 2); await loadNodes(); showToast("更新请求已完成");
  } catch (error) { $("update-result").textContent = error.message; showToast(error.message, true); }
  finally { button.disabled = false; button.textContent = "覆盖更新选中节点"; }
}
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
$("update-upload").addEventListener("click", uploadUpdatePackage);
$("update-deploy").addEventListener("click", deployUpdatePackage);
$("add-node-button").addEventListener("click", openNodeDialog);
$("nav-add-node").addEventListener("click", openNodeDialog);
$("node-form").addEventListener("submit", addNode);
$("reset-node").addEventListener("click", () => $("node-form").reset());
$("node-close").addEventListener("click", closeNodeDialog);
$("node-list").addEventListener("click", (event) => { const button = event.target.closest("button[data-action]"); if (!button) return; const id = button.dataset.id; const action = button.dataset.action; if (action === "console") openNodeConsole(id); if (action === "details") openDetails(id); if (action === "refresh") refreshNode(id, button); if (action === "restart") restartNode(id); if (action === "delete") deleteNode(id); });
$("console-minimize").addEventListener("click", () => { state.consoleMinimized = true; renderNodeConsole(); });
$("console-restore").addEventListener("click", () => { state.consoleMinimized = false; renderNodeConsole(); });
$("detail-refresh").addEventListener("click", refreshDetails);
$("detail-restart").addEventListener("click", () => state.selected && restartNode(state.selected.id));
// 点击详情对话框的空白背景（backdrop）时关闭，不必非点右上角叉
$("details-dialog").addEventListener("click", (event) => {
  if (event.target === $("details-dialog")) $("details-dialog").close();
});
$("add-task").addEventListener("click", () => openTaskDialog());
$("copy-tasks").addEventListener("click", openCopyDialog);
$("copy-source-node").addEventListener("change", (event) => loadCopySourceConfigs(event.target.value));
$("copy-select-all").addEventListener("change", () => {
  document.querySelectorAll("#copy-configs input[data-copy-index]").forEach((input) => { input.checked = $("copy-select-all").checked; });
});
$("copy-form").addEventListener("submit", copyTasks);
$("copy-close").addEventListener("click", closeCopyDialog);
$("copy-cancel").addEventListener("click", closeCopyDialog);
$("task-form").addEventListener("submit", saveTask);
$("task-close").addEventListener("click", closeTaskDialog);
$("task-cancel").addEventListener("click", closeTaskDialog);
$("task-password-toggle").addEventListener("click", () => {
  const input = $("task-password");
  input.type = input.type === "password" ? "text" : "password";
});
$("configs-output").addEventListener("click", (event) => {
  const button = event.target.closest("button[data-task-action]");
  if (!button) return;
  const config = state.configs.find((item) => item.fileName === button.dataset.fileName);
  if (button.dataset.taskAction === "edit" && config) openTaskDialog(config);
  if (button.dataset.taskAction === "delete") deleteTask(button.dataset.fileName);
});
document.querySelectorAll("[data-scroll]").forEach((button) => button.addEventListener("click", () => document.getElementById(button.dataset.scroll).scrollIntoView({ behavior: "smooth" })));
start();
