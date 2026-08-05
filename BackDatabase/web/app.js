const state = {
  configs: [],
  runs: {},
  expandedLogs: new Set(),
  trash: [],
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
        detail('备份计划', formatSchedule(config.backtime)),
        detail('数据库', config.databases),
        detail('保留数量', `${config.maxFiles} 个文件`),
        detail('保存目录', config.saveDir),
        detail('认证', config.passwordConfigured ? `${config.user} / 已设密码` : `${config.user} / 无密码`),
      );
      // 只在配置了每库单独计划时才多加一行，避免卡片信息冗余
      if (config.dbTimes) {
        const item = detail('每库单独计划', formatDbTimes(config.dbTimes));
        item.classList.add('detail-wide');
        details.append(item);
      }
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
    const filesBtn = document.createElement('button');
    filesBtn.className = 'ghost'; filesBtn.textContent = '文件';
    filesBtn.disabled = Boolean(config.error);
    filesBtn.addEventListener('click', () => openFilesDialog(config.fileName));
    const remove = document.createElement('button');
    remove.className = 'ghost danger'; remove.textContent = '删除';
    remove.addEventListener('click', () => deleteConfig(config.fileName));
    actions.append(edit, runNow, logBtn, filesBtn, remove);
    card.append(actions);

    // 运行状态徽标 + 可折叠日志面板。
    // 后端已按「配置+库」记录运行，同一配置可能有多条（每个库一条）。
    const runs = state.runs[config.fileName];
    if (runs && runs.length) {
      const badge = document.createElement('span');
      badge.className = `run-badge ${aggregateRunStatus(runs)}`;
      badge.textContent = aggregateRunLabel(runs);
      card.querySelector('.card-head')?.append(badge);

      const logPanel = document.createElement('pre');
      logPanel.className = 'run-log';
      logPanel.dataset.fileName = config.fileName;
      logPanel.hidden = !state.expandedLogs.has(config.fileName);
      logPanel.textContent = runs
        .slice()
        .sort((a, b) => (a.database || '').localeCompare(b.database || '', 'zh-CN'))
        .map(run => {
          const body = (run.log || []).join('\n');
          return runSummary(run) + (body ? '\n' + body : '');
        })
        .join('\n\n');
      card.append(logPanel);
    }
    list.append(card);
  });
}

/** 把 backtime 渲染成中文说明：HH:mm → 每日定点；数字 → 间隔分钟。 */
function formatSchedule(backtime) {
  const value = String(backtime ?? '').trim();
  if (!value) return '—';
  return value.includes(':') ? `每天 ${value} UTC` : `每 ${value} 分钟`;
}

/** 把 dbtimes（db1:60,db2:02:00）渲染成「库=计划」的可读列表。 */
function formatDbTimes(dbTimes) {
  return String(dbTimes)
    .split(',')
    .map(entry => entry.trim())
    .filter(Boolean)
    .map(entry => {
      const at = entry.indexOf(':');
      if (at <= 0) return entry;
      return `${entry.slice(0, at)} → ${formatSchedule(entry.slice(at + 1))}`;
    })
    .join('； ') || '—';
}

/** 多个库的运行状态合并成一个：运行中 > 失败 > 成功 > 空闲。 */
function aggregateRunStatus(runs) {
  if (runs.some(r => r.status === 'running')) return 'running';
  if (runs.some(r => r.status === 'failed')) return 'failed';
  if (runs.some(r => r.status === 'success')) return 'success';
  return 'idle';
}

function aggregateRunLabel(runs) {
  const status = aggregateRunStatus(runs);
  const label = runStatusLabel({ status });
  if (runs.length <= 1) return label;
  const matched = runs.filter(r => r.status === status).length;
  return `${label} ${matched}/${runs.length}`;
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
  // 库名可能为空（旧版任务级运行记录），此时不显示库前缀
  const prefix = run.database ? `《${run.database}》 ` : '';
  let line = `${prefix}[${label}] 触发:${run.trigger || '—'} 开始:${started} 结束:${finished}`;
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

async function openFilesDialog(fileName) {
  $('#files-dialog-title').textContent = fileName.replace(/\.conf$/i, '');
  $('#files-tbody').innerHTML = '';
  $('#files-empty-msg').hidden = true;
  $('#files-table-wrapper').hidden = true;
  $('#files-filter').value = 'all';
  $('#files-group-by').value = 'database';
  $('#files-database-filter').innerHTML = '<option value="all">全部数据库</option>';
  $('#files-summary').textContent = '';
  state.fileList = [];
  const dialog = $('#files-dialog');
  dialog.showModal();
  try {
    const files = await api(`/api/configs/${encodeURIComponent(fileName)}/files`);
    state.fileList = Array.isArray(files) ? files : [];
    populateDatabaseFilter();
    renderFileGroups();
  } catch (error) {
    toast(error.message, true);
  }
}

function formatFileSize(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return (bytes / Math.pow(1024, i)).toFixed(i > 1 ? 2 : 0) + ' ' + units[i];
}

/** 备份时间列的统一格式化。 */
function formatFileTime(file) {
  return file.createdAtUtc
    ? new Date(file.createdAtUtc).toLocaleString('zh-CN', { hour12: false }) + ' UTC'
    : '—';
}

/**
 * 库名优先取后端返回的 database 字段（后端按 conf 里的 dbs 精确匹配，
 * 无法归属的会返回 "_other"）；老接口没有该字段时退回按文件名解析。
 */
function databaseOf(file) {
  const value = file.database;
  if (!value) return extractDatabaseName(file.name);
  return value === '_other' ? '其他' : value;
}

function renderFileGroups() {
  const filter = $('#files-filter').value;
  const databaseFilter = $('#files-database-filter').value;
  const groupBy = $('#files-group-by').value;
  const now = Date.now();
  const cutoff = filter === 'today'
    ? new Date().setHours(0, 0, 0, 0)
    : filter === '7d' ? now - 7 * 86400000
    : filter === '30d' ? now - 30 * 86400000
    : 0;
  const filtered = (state.fileList || []).filter(file => {
    const matchesDate = !cutoff || (file.createdAtUtc && new Date(file.createdAtUtc).getTime() >= cutoff);
    const matchesDatabase = databaseFilter === 'all' || databaseOf(file) === databaseFilter;
    return matchesDate && matchesDatabase;
  });
  const tbody = $('#files-tbody');
  tbody.innerHTML = '';
  $('#files-empty-msg').hidden = filtered.length > 0;
  $('#files-table-wrapper').hidden = filtered.length === 0;
  $('#files-summary').textContent = `显示 ${filtered.length} / ${(state.fileList || []).length} 个文件`;
  if (!filtered.length) return;

  const appendRow = (file, index) => {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>${index}</td>
      <td title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</td>
      <td>${escapeHtml(databaseOf(file))}</td>
      <td>${formatFileSize(file.sizeBytes)}</td>
      <td>${formatFileTime(file)}</td>`;
    tbody.appendChild(tr);
  };

  if (groupBy === 'none') {
    filtered.forEach((file, i) => appendRow(file, i + 1));
    return;
  }

  const groups = new Map();
  filtered.forEach(file => {
    let key;
    if (groupBy === 'database') {
      key = databaseOf(file);
    } else {
      const date = file.createdAtUtc ? new Date(file.createdAtUtc) : null;
      key = date
        ? `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`
        : '未知日期';
    }
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(file);
  });

  // 按库分组时按库名排序，按日期分组时新的在前
  const sorted = [...groups.entries()].sort((a, b) => groupBy === 'database'
    ? a[0].localeCompare(b[0], 'zh-CN')
    : b[0].localeCompare(a[0]));

  for (const [groupName, files] of sorted) {
    const totalBytes = files.reduce((sum, f) => sum + (f.sizeBytes || 0), 0);
    const groupRow = document.createElement('tr');
    groupRow.className = 'files-group-row';
    groupRow.innerHTML =
      `<td colspan="5">${escapeHtml(groupName)} · ${files.length} 个文件 · ${formatFileSize(totalBytes)}</td>`;
    tbody.appendChild(groupRow);
    // 序号在每个分组内从 1 重新开始，方便对照单个库的备份代数
    files.forEach((file, i) => appendRow(file, i + 1));
  }
}

function populateDatabaseFilter() {
  const select = $('#files-database-filter');
  const databases = [...new Set((state.fileList || []).map(databaseOf))]
    .sort((a, b) => a.localeCompare(b, 'zh-CN'));
  databases.forEach(database => {
    const option = document.createElement('option');
    option.value = database;
    option.textContent = database;
    select.appendChild(option);
  });
}

function extractDatabaseName(fileName) {
  const match = String(fileName).match(/^(.*)_\d{4}-\d{2}-\d{2}__\d{2}\.\d{2}\.\d{2}\.sql$/i);
  return match?.[1] || '其他';
}

function escapeHtml(str) {
  return String(str).replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
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

async function backupAll() {
  const btn = $('#backup-all-button');
  btn.disabled = true;
  try {
    const res = await api('/api/configs/backup-all', { method: 'POST' });
    toast(res.message);
    // 展开所有任务的日志面板，方便观察进度
    state.configs.forEach(c => state.expandedLogs.add(c.fileName));
    await loadRuns();
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
    // 后端按「配置+库」返回，同一 fileName 会有多条，这里聚成数组
    const grouped = {};
    runs.forEach(run => {
      (grouped[run.fileName] ||= []).push(run);
    });
    state.runs = grouped;
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
  } else if (viewName === 'trash') {
    $('#page-title').textContent = '回收站';
    $('#add-button').style.display = 'none';
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
  loadTrash();
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
  // 编辑时回显已保存的密码明文，方便核对；新建时留空
  form.elements.password.value = config?.password || '';
  form.elements.password.type = 'password';
  form.elements.clearPassword.checked = false;
  form.elements.databases.value = config?.databases || '';
  form.elements.backtime.value = config?.backtime || '60';
  form.elements.maxFiles.value = config?.maxFiles || 180;
  form.elements.saveDir.value = config?.saveDir || '/backup/';
  // 每库计划：把 conf 里 dbtimes=app:30,hhx:02:00 解析成表格行
  state.dbSchedules = parseDbTimesToRows(config?.dbTimes || '');
  renderDbScheduleTable();
  $('#password-hint').textContent = config
    ? (config.passwordConfigured
        ? '已显示当前数据库密码；可直接修改，留空则保留原值。'
        : '当前未配置密码。')
    : '新配置可留空，适用于无密码连接。';
  dialog.showModal();
  // 打开对话框时立即加载一次磁盘空间信息
  setTimeout(() => {
    const dir = form.elements.saveDir.value.trim();
    if (dir) fetchDiskInfo(dir);
  }, 50);
}

// ==================== 每库备份计划表格 ====================

// 每行结构： { database, mode: 'inherit'|'interval'|'daily', value: '' }
// inherit  → 沿用任务级（不写入 dbtimes）
// interval → 每隔 N 分钟
// daily    → 每天 HH:mm (UTC)
state.dbSchedules = [];

/**
 * 把 conf 里的 dbtimes 字符串解析成表格行。
 * 仅解析在「数据库」列表里的库名（保存时也会再校验）。
 * 未在 dbtimes 里出现的库会在 renderDbScheduleTable 里按 dbs 补全成 inherit 行。
 */
function parseDbTimesToRows(dbTimes) {
  const rows = [];
  const raw = String(dbTimes || '').trim();
  if (!raw) return rows;
  raw.split(',').map(s => s.trim()).filter(Boolean).forEach(entry => {
    const match = /^(.*?):((?:\d+(?:\.\d+)?)|(?:\d{1,2}:\d{1,2}))$/.exec(entry);
    if (!match) return;
    const db = match[1].trim();
    const time = match[2].trim();
    if (!db || !time) return;
    // 纯数字 → 间隔分钟
    if (/^\d+(\.\d+)?$/.test(time) && Number(time) > 0) {
      rows.push({ database: db, mode: 'interval', value: time });
    } else if (/^\d{1,2}:\d{1,2}$/.test(time)) {
      rows.push({ database: db, mode: 'daily', value: time });
    }
  });
  return rows;
}

/**
 * 根据当前「数据库」输入框里的库名，重建表格：
 * - 以 dbs 顺序为主序，逐行生成；
 * - 已在 state.dbSchedules 里的库保留其设置；
 * - 新出现的库默认 inherit；
 * - 不在 dbs 里的行删掉。
 */
function renderDbScheduleTable() {
  const tbody = $('#db-schedule-tbody');
  const emptyMsg = $('#db-schedule-empty');
  const table = $('#db-schedule-table');
  if (!tbody) return;

  const dbs = (form.elements.databases.value || '')
    .split(',').map(s => s.trim()).filter(Boolean);

  // 用 dbs 列表重排/补全 state.dbSchedules，保留已设置项
  const byDb = new Map(state.dbSchedules.map(r => [r.database.toLowerCase(), r]));
  state.dbSchedules = dbs.map(db => {
    const existing = byDb.get(db.toLowerCase());
    if (existing) return { ...existing, database: db };
    return { database: db, mode: 'inherit', value: '' };
  });

  tbody.replaceChildren();
  if (!state.dbSchedules.length) {
    table.hidden = true;
    emptyMsg.hidden = false;
    return;
  }
  table.hidden = false;
  emptyMsg.hidden = true;

  state.dbSchedules.forEach((row, idx) => {
    const tr = document.createElement('tr');
    tr.dataset.row = String(idx);

    // 库名（只读，随 dbs 输入驱动）
    const tdDb = document.createElement('td');
    tdDb.className = 'db-sched-name';
    tdDb.textContent = row.database;
    tr.append(tdDb);

    // 计划类型下拉
    const tdMode = document.createElement('td');
    const sel = document.createElement('select');
    sel.innerHTML = `
      <option value="inherit">沿用任务级</option>
      <option value="interval">每隔 N 分钟</option>
      <option value="daily">每天定点 (UTC)</option>`;
    sel.value = row.mode;
    sel.addEventListener('change', () => {
      row.mode = sel.value;
      // 切回 inherit 时清空值，避免脏数据
      if (sel.value === 'inherit') row.value = '';
      else if (!row.value) row.value = sel.value === 'interval' ? '30' : '02:00';
      renderDbScheduleTable();
    });
    tdMode.append(sel);
    tr.append(tdMode);

    // 值输入（随 mode 切换形态）
    const tdVal = document.createElement('td');
    tdVal.className = 'db-sched-value-col';
    let input;
    if (row.mode === 'interval') {
      input = document.createElement('input');
      input.type = 'number';
      input.min = '1';
      input.step = '1';
      input.placeholder = '例如 30';
    } else if (row.mode === 'daily') {
      input = document.createElement('input');
      input.type = 'time';
      input.step = '60';
    } else {
      input = document.createElement('input');
      input.disabled = true;
      input.placeholder = '—';
    }
    input.value = row.value || '';
    input.addEventListener('input', () => { row.value = input.value; });
    tdVal.append(input);
    tr.append(tdVal);

    // 说明（实时预览，验证用）
    const tdHint = document.createElement('td');
    tdHint.className = 'db-sched-desc';
    const span = document.createElement('span');
    span.className = 'db-sched-desc-text';
    tdHint.append(span);
    tr.append(tdHint);

    tbody.append(tr);
    refreshScheduleDesc(idx, span);
  });
}

/** 实时校验并渲染某行的说明文字（红字提示非法）。 */
function refreshScheduleDesc(idx, span) {
  const row = state.dbSchedules[idx];
  if (!row) return;
  const backtime = (form.elements.backtime.value || '').trim();
  if (row.mode === 'inherit') {
    span.className = 'db-sched-desc-text';
    span.textContent = backtime.includes(':')
      ? `沿用每天 ${backtime} UTC`
      : `沿用每 ${backtime || '—'} 分钟`;
    return;
  }
  if (row.mode === 'interval') {
    const ok = /^\d+(\.\d+)?$/.test(row.value) && Number(row.value) > 0;
    span.className = `db-sched-desc-text${ok ? '' : ' invalid'}`;
    span.textContent = ok ? `每 ${row.value} 分钟` : '需为大于 0 的数字';
    return;
  }
  if (row.mode === 'daily') {
    const m = /^(\d{1,2}):(\d{1,2})$/.exec(row.value);
    const ok = m && Number(m[1]) <= 23 && Number(m[2]) <= 59;
    span.className = `db-sched-desc-text${ok ? '' : ' invalid'}`;
    span.textContent = ok ? `每天 ${row.value} UTC` : '需为 HH:mm';
  }
}

/** 把表格行序列化回 conf 的 dbtimes 字符串（仅非 inherit 行）。 */
function serializeDbSchedules() {
  return state.dbSchedules
    .filter(r => r.mode !== 'inherit' && r.value)
    .map(r => `${r.database}:${r.value.trim()}`)
    .join(',');
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
    dbTimes: serializeDbSchedules(),
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
  if (!confirm(`确定删除 ${fileName}？任务会移入回收站，可从回收站恢复。`)) return;
  try {
    const result = await api(`/api/configs/${encodeURIComponent(fileName)}`, { method: 'DELETE' });
    toast(result.message);
    await loadConfigs();
    await loadTrash();
  } catch (error) { toast(error.message, true); }
}

async function loadTrash() {
  try {
    const items = await api('/api/trash');
    state.trash = Array.isArray(items) ? items : [];
    renderTrash();
  } catch (e) { /* 静默 */ }
}

function renderTrash() {
  const list = $('#trash-list');
  if (!list) return;
  list.replaceChildren();
  $('#trash-empty').hidden = state.trash.length !== 0;
  state.trash.forEach(item => {
    const card = document.createElement('article');
    card.className = `config-card${item.error ? ' error' : ''}`;
    const head = document.createElement('div');
    head.className = 'card-head';
    const titleWrap = document.createElement('div');
    const title = document.createElement('h3');
    const file = document.createElement('small');
    title.textContent = item.fileName.replace(/\.conf$/i, '').replace(/__\d{4}-\d{2}-\d{2}__\d{2}\.\d{2}\.\d{2}$/, '');
    file.textContent = item.fileName;
    titleWrap.append(title, file);
    const badge = document.createElement('span');
    badge.className = 'type-badge';
    badge.textContent = item.error ? '配置错误' : item.dbType;
    head.append(titleWrap, badge);
    card.append(head);

    if (item.error) {
      const error = document.createElement('p');
      error.style.cssText = 'color:#a43d3d;margin:20px 0;font-size:13px';
      error.textContent = item.error;
      card.append(error);
    } else {
      const details = document.createElement('div');
      details.className = 'details';
      const deleted = item.deletedAtUtc
        ? new Date(item.deletedAtUtc).toLocaleString('zh-CN', { hour12: false }) + ' UTC'
        : '—';
      details.append(
        detail('连接地址', `${item.host}:${item.port}`),
        detail('数据库', item.databases),
        detail('删除时间', deleted),
      );
      card.append(details);
    }

    const actions = document.createElement('div');
    actions.className = 'card-actions';
    const restore = document.createElement('button');
    restore.className = 'ghost primary-ghost'; restore.textContent = '恢复';
    restore.addEventListener('click', () => restoreConfig(item.fileName));
    const purge = document.createElement('button');
    purge.className = 'ghost danger'; purge.textContent = '彻底删除';
    purge.addEventListener('click', () => purgeConfig(item.fileName));
    actions.append(restore, purge);
    card.append(actions);
    list.append(card);
  });
}

async function restoreConfig(fileName) {
  try {
    const result = await api(`/api/trash/${encodeURIComponent(fileName)}/restore`, { method: 'POST' });
    toast(result.message);
    await loadConfigs();
    await loadTrash();
  } catch (error) { toast(error.message, true); }
}

async function purgeConfig(fileName) {
  if (!confirm(`彻底删除 ${fileName}？此操作不可恢复。`)) return;
  try {
    const result = await api(`/api/trash/${encodeURIComponent(fileName)}`, { method: 'DELETE' });
    toast(result.message);
    await loadTrash();
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
  const titles = { tasks: '备份任务', trash: '回收站', environment: '消息推送' };
  $('#page-title').textContent = titles[button.dataset.view] || '备份任务';
  $('#add-button').style.display = button.dataset.view === 'tasks' ? '' : 'none';
  if (button.dataset.view === 'trash') loadTrash();
}));

$('#add-button').addEventListener('click', () => openDialog());
$('#empty-add').addEventListener('click', () => openDialog());
$('#refresh-button').addEventListener('click', loadConfigs);
$('#refresh-trash-button').addEventListener('click', loadTrash);
$('#backup-all-button').addEventListener('click', backupAll);
$('#close-dialog').addEventListener('click', () => dialog.close());
$('#cancel-dialog').addEventListener('click', () => dialog.close());
$('#close-files-dialog').addEventListener('click', () => $('#files-dialog').close());
$('#files-filter').addEventListener('change', renderFileGroups);
$('#files-database-filter').addEventListener('change', renderFileGroups);
$('#files-group-by').addEventListener('change', renderFileGroups);
// 「数据库」输入变化时，每库计划表格随之增删行
$('#databases-input').addEventListener('input', renderDbScheduleTable);
// 「备份计划」变化时，inherit 行的说明同步更新
form.elements.backtime.addEventListener('input', () => {
  document.querySelectorAll('.db-sched-desc-text').forEach((span, i) => refreshScheduleDesc(i, span));
});
$('#password-toggle').addEventListener('click', () => {
  const input = form.elements.password;
  input.type = input.type === 'password' ? 'text' : 'password';
});
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
