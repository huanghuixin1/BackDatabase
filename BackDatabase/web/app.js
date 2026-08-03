const state = {
  configs: [],
  runs: {},
  expandedLogs: new Set(),
  editing: null,
  authenticated: false,
  required: false,
  autoSaveDir: false
};

const $ = (selector) => document.querySelector(selector);
const dialog = $('#config-dialog');
const form = $('#config-form');
const loginForm = $('#login-form');
const loginMessage = $('#login-message');

// localStorage 键名：保存「记住访问口令」勾选后的口令（明文，仅本机浏览器）
const TOKEN_KEY = 'backdb_auth_token';
const REMEMBER_KEY = 'backdb_web_password';

function getAuthToken() {
  return sessionStorage.getItem(TOKEN_KEY) || localStorage.getItem(TOKEN_KEY) || '';
}

function setAuthToken(token, remember) {
  sessionStorage.setItem(TOKEN_KEY, token);
  if (remember) localStorage.setItem(TOKEN_KEY, token);
  else localStorage.removeItem(TOKEN_KEY);
}

function clearAuthToken() {
  sessionStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(TOKEN_KEY);
}

async function api(url, options = {}) {
  const headers = {
    'Content-Type': 'application/json',
    ...(options.headers || {}),
  };
  const token = getAuthToken();
  if (token && !headers.Authorization) headers.Authorization = `Bearer ${token}`;

  const response = await fetch(url, {
    ...options,
    headers,
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.message || body.error || body.detail || `请求失败 (${response.status})`);
  return body;
}
function toast(message, isError = false) {
  const node = $('#toast');
  node.textContent = message;
  node.className = `toast show${isError ? ' error' : ''}`;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => node.className = 'toast', 3200);
}

function detail(label, value) {
  const item = document.createElement('div');
  item.className = 'detail';
  const caption = document.createElement('span');
  const content = document.createElement('strong');
  caption.textContent = label;
  content.textContent = value || '—';
  item.append(caption, content);
  return item;
}

function renderConfigs() {
  const list = $('#config-list');
  list.replaceChildren();
  $('#config-count').textContent = state.configs.length;
  const types = [...new Set(state.configs.filter(x => !x.error).map(x => x.dbType))];
  $('#db-types').textContent = types.length ? types.join(' · ') : '—';
  $('#empty-state').hidden = state.configs.length !== 0;

  state.configs.forEach(config => {
    const card = document.createElement('article');
    card.className = `config-card${config.error ? ' error' : ''}`;
    const head = document.createElement('div');
    head.className = 'card-head';
    const titleWrap = document.createElement('div');
    const title = document.createElement('h3');
    const file = document.createElement('small');
    title.textContent = config.fileName.replace(/\.conf$/i, '');
    file.textContent = config.fileName;
    titleWrap.append(title, file);
    const badge = document.createElement('span');
    badge.className = 'type-badge';
    badge.textContent = config.error ? '配置错误' : config.dbType;
    head.append(titleWrap, badge);
    card.append(head);

    if (config.error) {
      const error = document.createElement('p');
      error.style.cssText = 'color:#a43d3d;margin:20px 0;font-size:13px';
      error.textContent = config.error;
      card.append(error);
    } else {
      const details = document.createElement('div');
      details.className = 'details';
      details.append(
        detail('连接地址', `${config.host}:${config.port}`),
        detail('备份计划', config.backtime.includes(':') ? `${config.backtime} UTC` : `每 ${config.backtime} 分钟`),
        detail('数据库', config.databases),
        detail('保留数量', `${config.maxFiles} 个文件`),
        detail('保存目录', config.saveDir),
        detail('认证', config.passwordConfigured ? `${config.user} / 已设密码` : `${config.user} / 无密码`),
      );
      card.append(details);
    }

    const actions = document.createElement('div');
    actions.className = 'card-actions';
    const edit = document.createElement('button');
    edit.className = 'ghost'; edit.textContent = '编辑'; edit.disabled = Boolean(config.error);
    edit.addEventListener('click', () => openDialog(config));
    const runNow = document.createElement('button');
    runNow.className = 'ghost primary-ghost'; runNow.textContent = '立即备份';
    runNow.disabled = Boolean(config.error);
    runNow.addEventListener('click', () => triggerBackup(config.fileName, runNow));
    const logBtn = document.createElement('button');
    logBtn.className = 'ghost'; logBtn.textContent = '日志';
    logBtn.disabled = Boolean(config.error);
    logBtn.addEventListener('click', () => toggleLog(config.fileName, logBtn));
    const remove = document.createElement('button');
    remove.className = 'ghost danger'; remove.textContent = '删除';
    remove.addEventListener('click', () => deleteConfig(config.fileName));
    actions.append(edit, runNow, logBtn, remove);
    card.append(actions);

    // 运行状态徽标 + 可折叠日志面板
    const run = state.runs[config.fileName];
    if (run) {
      const badge = document.createElement('span');
      badge.className = `run-badge ${run.status}`;
      badge.textContent = runStatusLabel(run);
      card.querySelector('.card-head')?.append(badge);

      const logPanel = document.createElement('pre');
      logPanel.className = 'run-log';
      logPanel.dataset.fileName = config.fileName;
      logPanel.hidden = !state.expandedLogs.has(config.fileName);
      const headLine = runSummary(run);
      const body = (run.log || []).join('\n');
      logPanel.textContent = headLine + (body ? '\n' + body : '');
      card.append(logPanel);
    }
    list.append(card);
  });
}

function runStatusLabel(run) {
  switch (run.status) {
    case 'running': return '运行中';
    case 'success': return '成功';
    case 'failed': return '失败';
    default: return '空闲';
  }
}

function runSummary(run) {
  const label = runStatusLabel(run);
  const started = run.startedAtUtc ? new Date(run.startedAtUtc).toLocaleString('zh-CN', { hour12: false }) + ' UTC' : '—';
  const finished = run.finishedAtUtc ? new Date(run.finishedAtUtc).toLocaleString('zh-CN', { hour12: false }) + ' UTC' : '—';
  let line = `[${label}] 触发:${run.trigger || '—'} 开始:${started} 结束:${finished}`;
  if (run.error) line += `\n错误: ${run.error}`;
  return line;
}

function toggleLog(fileName, btn) {
  if (state.expandedLogs.has(fileName)) {
    state.expandedLogs.delete(fileName);
    btn.classList.remove('active');
  } else {
    state.expandedLogs.add(fileName);
    btn.classList.add('active');
  }
  const panel = document.querySelector(`.run-log[data-file-name="${CSS.escape(fileName)}"]`);
  if (panel) panel.hidden = !state.expandedLogs.has(fileName);
}

async function triggerBackup(fileName, btn) {
  btn.disabled = true;
  try {
    await api(`/api/configs/${encodeURIComponent(fileName)}/backup`, { method: 'POST' });
    toast('已开始备份');
    state.expandedLogs.add(fileName);
    await loadRuns();
    // 备份是后台异步执行的，POST 返回时可能尚未进入 running；
    // 这里强制持续轮询一段时间，直到看到本次运行的结束状态。
    ensureRunsPolling();
  } catch (error) {
    toast(error.message, true);
  } finally {
    btn.disabled = false;
  }
}

let runsPollTimer = null;
async function loadRuns() {
  try {
    const runs = await api('/api/runs');
    state.runs = Object.fromEntries(runs.map(r => [r.fileName, r]));
    renderConfigs();
    const anyRunning = runs.some(r => r.status === 'running');
    if (anyRunning && !runsPollTimer) {
      runsPollTimer = setInterval(loadRuns, 2000);
    } else if (!anyRunning && runsPollTimer && !keepPolling) {
      clearInterval(runsPollTimer);
      runsPollTimer = null;
    }
  } catch (e) { /* 静默，不打断主流程 */ }
}

// 立即备份触发后，强制持续轮询一段时间，确保能看到本次运行的结束状态。
// 解决「POST 返回时任务尚未进入 running，loadRuns 取消了轮询」导致看不到成功/失败的问题。
let keepPolling = false;
let keepPollingTimer = null;
function ensureRunsPolling() {
  keepPolling = true;
  if (!runsPollTimer) {
    runsPollTimer = setInterval(loadRuns, 2000);
  }
  clearTimeout(keepPollingTimer);
  // 持续轮询最多 60 秒；之后若已无 running 任务，则允许停掉轮询。
  keepPollingTimer = setTimeout(() => {
    keepPolling = false;
    loadRuns();
  }, 60000);
}

async function loadConfigs() {
  try {
    state.configs = await api('/api/configs');
    renderConfigs();
    await loadRuns();
  } catch (error) { toast(error.message, true); }
}

// ==================== 磁盘空间 ====================

// 格式化字节数为可读字符串
function formatBytes(bytes) {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return (bytes / Math.pow(1024, i)).toFixed(i > 1 ? 2 : 0) + ' ' + units[i];
}

let diskInfoTimer = null;

// 监听保存目录输入，延迟查询对应盘符的空间信息
form.elements.saveDir.addEventListener('input', () => {
  const dir = form.elements.saveDir.value.trim();
  clearTimeout(diskInfoTimer);
  if (!dir) { $('#disk-info').hidden = true; return; }
  diskInfoTimer = setTimeout(() => fetchDiskInfo(dir), 350);
});

async function fetchDiskInfo(dir) {
  const infoEl = $('#disk-info');
  try {
    const info = await api(`/api/disk?path=${encodeURIComponent(dir)}`);
    infoEl.hidden = false;
    infoEl.textContent =
      `${info.driveName} 可用 ${formatBytes(info.freeBytes)} / 总计 ${formatBytes(info.totalBytes)}`;
    infoEl.classList.remove('disk-warn', 'disk-danger');
    // 可用空间低于 5GB 标橙色，低于 1GB 标红色
    if (info.freeBytes < 1024 * 1024 * 1024) infoEl.classList.add('disk-danger');
    else if (info.freeBytes < 5 * 1024 * 1024 * 1024) infoEl.classList.add('disk-warn');
  } catch {
    infoEl.hidden = true;
  }
}

// ==================== 登录相关 ====================

// 把指定视图切换为唯一可见，并同步顶栏标题与按钮
function showView(viewName) {
  document.querySelectorAll('.view').forEach(node => node.classList.remove('active'));
  $(`#${viewName}-view`).classList.add('active');
  if (viewName === 'login') {
    $('#page-title').textContent = '登录';
    $('#add-button').style.display = 'none';
    $('#restart-button').style.display = 'none';
  } else if (viewName === 'tasks') {
    $('#page-title').textContent = '备份任务';
    $('#add-button').style.display = '';
    $('#restart-button').style.display = '';
  } else if (viewName === 'environment') {
    $('#page-title').textContent = '消息推送';
    $('#add-button').style.display = 'none';
    $('#restart-button').style.display = '';
  }
}

async function loadSession() {
  try {
    const res = await api('/api/session');
    state.required = res.required;
    state.authenticated = res.authenticated;
  } catch (e) {
    toast('加载会话状态失败', true);
    return;
  }

  // 退出登录按钮只在「启用了口令」的部署里出现
  $('#logout-button').hidden = !state.required;

  // 需要口令且未登录 → 登录页；否则进主界面
  if (state.required && !state.authenticated) {
    document.querySelectorAll('.nav-item').forEach(node => node.classList.remove('active'));
    showView('login');
    loginForm.reset();
    loginMessage.textContent = '';
    // 如果之前勾过「记住访问口令」，预填并默认勾选
    const saved = localStorage.getItem(REMEMBER_KEY) || '';
    if (saved) {
      loginForm.elements.webPassword.value = saved;
      loginForm.elements.remember.checked = true;
    } else {
      loginForm.elements.remember.checked = false;
    }
    loginForm.elements.webPassword.focus();
    return;
  }

  showView('tasks');
  document.querySelectorAll('.nav-item').forEach(node =>
    node.classList.toggle('active', node.dataset.view === 'tasks'));
  await loadConfigs();
  await loadEnvironment();
}

async function login(e) {
  e.preventDefault();
  const password = loginForm.elements.webPassword.value;
  if (!password) return;
  const remember = loginForm.elements.remember.checked;

  try {
    const res = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ key: password }) });
    state.authenticated = true;
    setAuthToken(res.token, remember);
    // 登录成功后处理「记住访问口令」：勾选则存本机，否则清掉旧值
    if (remember) localStorage.setItem(REMEMBER_KEY, password);
    else localStorage.removeItem(REMEMBER_KEY);
    toast('登录成功');
    // 登录成功，重新走一遍会话流程：会自动切到 tasks 视图并加载数据
    await loadSession();
  } catch (error) {
    loginMessage.textContent = error.message;
    loginMessage.style.color = '#d32f2f';
    loginForm.elements.webPassword.select();
  }
}

async function logout() {
  try {
    await api('/api/auth/logout', { method: 'POST' });
    clearAuthToken();
    state.authenticated = false;
    toast('已退出登录');
    await loadSession();
  } catch (error) {
    toast(error.message, true);
  }
}

// ==================== 其他功能 ====================
function openDialog(config = null) {
  if (!state.authenticated) {
    toast('请先登录', true);
    return;
  }
  state.editing = config?.fileName || null;
  state.autoSaveDir = !config;
  form.reset();
  $('#dialog-title').textContent = config ? '编辑备份配置' : '新建备份配置';
  form.elements.fileName.disabled = Boolean(config);
  form.elements.fileName.value = config?.fileName.replace(/\.conf$/i, '') || '';
  form.elements.dbType.value = config?.dbType === 'pgsql' ? 'pgsql' : 'mysql';
  form.elements.host.value = config?.host || '127.0.0.1';
  form.elements.port.value = config?.port || '3306';
  form.elements.user.value = config?.user || 'root';
  form.elements.password.value = '';
  form.elements.clearPassword.checked = false;
  form.elements.databases.value = config?.databases || '';
  form.elements.backtime.value = config?.backtime || '60';
  form.elements.maxFiles.value = config?.maxFiles || 180;
  form.elements.saveDir.value = config?.saveDir || '/backup/';
  $('#password-hint').textContent = config?.passwordConfigured
    ? '已保存密码；留空将保留，勾选下方选项可清除。'
    : '当前未配置密码。';
  dialog.showModal();
  // 打开对话框时立即加载一次磁盘空间信息
  setTimeout(() => {
    const dir = form.elements.saveDir.value.trim();
    if (dir) fetchDiskInfo(dir);
  }, 50);
}

async function saveConfig(event) {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(form));
  const payload = {
    fileName: data.fileName || state.editing,
    dbType: data.dbType,
    host: data.host,
    port: data.port,
    user: data.user,
    password: data.password || null,
    clearPassword: form.elements.clearPassword.checked,
    databases: data.databases,
    backtime: data.backtime,
    maxFiles: Number(data.maxFiles),
    saveDir: data.saveDir,
  };
  try {
    const result = await api(state.editing ? `/api/configs/${encodeURIComponent(state.editing)}` : '/api/configs', {
      method: state.editing ? 'PUT' : 'POST', body: JSON.stringify(payload),
    });
    dialog.close();
    toast(result.message);
    await loadConfigs();
  } catch (error) { toast(error.message, true); }
}

async function deleteConfig(fileName) {
  if (!confirm(`确定删除 ${fileName}？当前运行中的任务会持续到服务重启。`)) return;
  try {
    const result = await api(`/api/configs/${encodeURIComponent(fileName)}`, { method: 'DELETE' });
    toast(result.message);
    await loadConfigs();
  } catch (error) { toast(error.message, true); }
}

async function loadEnvironment() {
  try {
    const env = await api('/api/environment');
    const envForm = $('#environment-form');
    envForm.elements.pushAddr.value = env.pushAddr || '';
    envForm.elements.pushKey.value = '';
    envForm.elements.clearPushKey.checked = false;
    envForm.elements.pushHwid.value = env.pushHwid || '';
    envForm.elements.pushGroup.value = env.pushGroup || '';
    const stateNode = $('#push-state');
    stateNode.textContent = env.pushKeyConfigured ? 'Key 已配置' : '未配置 Key';
    stateNode.classList.toggle('enabled', env.pushKeyConfigured);
  } catch (error) { toast(error.message, true); }
}

async function saveEnvironment(event) {
  event.preventDefault();
  const formNode = event.currentTarget;
  const data = Object.fromEntries(new FormData(formNode));
  try {
    const result = await api('/api/environment', { method: 'PUT', body: JSON.stringify({
      pushAddr: data.pushAddr, pushKey: data.pushKey || null,
      clearPushKey: formNode.elements.clearPushKey.checked,
      pushHwid: data.pushHwid, pushGroup: data.pushGroup,
    }) });
    toast(result.message);
    await loadEnvironment();
  } catch (error) { toast(error.message, true); }
}

document.querySelectorAll('.nav-item').forEach(button => button.addEventListener('click', () => {
  document.querySelectorAll('.nav-item, .view').forEach(node => node.classList.remove('active'));
  button.classList.add('active');
  $(`#${button.dataset.view}-view`).classList.add('active');
  $('#page-title').textContent = button.dataset.view === 'tasks' ? '备份任务' : '消息推送';
  $('#add-button').style.display = button.dataset.view === 'tasks' ? '' : 'none';
}));

$('#add-button').addEventListener('click', () => openDialog());
$('#empty-add').addEventListener('click', () => openDialog());
$('#refresh-button').addEventListener('click', loadConfigs);
$('#close-dialog').addEventListener('click', () => dialog.close());
$('#cancel-dialog').addEventListener('click', () => dialog.close());
$('#logout-button').addEventListener('click', logout);
$('#restart-button').addEventListener('click', restartService);

async function restartService() {
  if (!confirm('确定要重启 BackDatabase 服务吗？重启期间配置界面会短暂不可用。')) return;
  $('#restart-button').disabled = true;
  toast('正在重启服务，请稍候...');
  try {
    await api('/api/restart', { method: 'POST' });
  } catch (error) {
    // 进程退出可能中断连接，导致 fetch 报错，这是预期内的，不当作错误提示
  }
  // 轮询等待新进程起来后重新加载会话
  pollAfterRestart();
}

function pollAfterRestart() {
  let attempts = 0;
  const maxAttempts = 30;
  const timer = setInterval(async () => {
    attempts++;
    try {
      const res = await api('/api/session');
      // 新进程已响应：清空轮询，重新初始化界面
      clearInterval(timer);
      toast('服务已重启完成');
      loadSession();
    } catch (e) {
      if (attempts >= maxAttempts) {
        clearInterval(timer);
        toast('重启超时，请手动刷新页面', true);
        $('#restart-button').disabled = false;
      }
    }
  }, 1000);
}

loginForm.addEventListener('submit', login);
form.addEventListener('submit', saveConfig);
$('#environment-form').addEventListener('submit', saveEnvironment);
form.elements.dbType.addEventListener('change', event => {
  if (form.elements.port.value === '3306' || form.elements.port.value === '5432')
    form.elements.port.value = event.target.value === 'pgsql' ? '5432' : '3306';
});
// 新建配置时，保存目录随配置名称自动填充为 /名称；用户手动改过保存目录后不再自动跟随
form.elements.fileName.addEventListener('input', () => {
  if (state.editing || !state.autoSaveDir) return;
  const name = form.elements.fileName.value.trim();
  form.elements.saveDir.value = name ? `/${name}` : '/backup/';
});
form.elements.saveDir.addEventListener('input', () => { state.autoSaveDir = false; });

setInterval(() => $('#utc-clock').textContent = new Date().toISOString().slice(0, 19).replace('T', ' ') + ' UTC', 1000);

loadSession();
