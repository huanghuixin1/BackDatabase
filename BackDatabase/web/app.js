const state = {
  configs: [],
  editing: null,
  authenticated: false,
  required: false
};

const $ = (selector) => document.querySelector(selector);
const dialog = $('#config-dialog');
const form = $('#config-form');
const loginForm = $('#login-form');
const loginMessage = $('#login-message');

async function api(url, options = {}) {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options,
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.message || body.detail || `请求失败 (${response.status})`);
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
    const remove = document.createElement('button');
    remove.className = 'ghost danger'; remove.textContent = '删除';
    remove.addEventListener('click', () => deleteConfig(config.fileName));
    actions.append(edit, remove);
    card.append(actions);
    list.append(card);
  });
}

async function loadConfigs() {
  try {
    state.configs = await api('/api/configs');
    renderConfigs();
  } catch (error) { toast(error.message, true); }
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

  try {
    const res = await api('/api/session', { method: 'POST', body: JSON.stringify({ password }) });
    state.authenticated = true;
    toast(res.message);
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
    await api('/api/session', { method: 'DELETE' });
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

setInterval(() => $('#utc-clock').textContent = new Date().toISOString().slice(0, 19).replace('T', ' ') + ' UTC', 1000);

loadSession();
