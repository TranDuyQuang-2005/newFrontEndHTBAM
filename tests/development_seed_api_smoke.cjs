// Read-only checks plus intentionally forbidden requests against the running Docker stack.
const assert = require('node:assert/strict');
const base = process.argv[2] || 'http://127.0.0.1:5000';
if (!/^http:\/\/(localhost|127\.0\.0\.1):\d+$/.test(base)) throw Error('Local development API required.');
const password = process.env.SEED_DEFAULT_PASSWORD || 'HTBAM-Local-Demo!2026#';
let checks = 0;
async function request(path, token, method = 'GET', body, status = 200) {
  const response = await fetch(base + path, { method, signal: AbortSignal.timeout(10000), headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
  assert.equal(response.status, status, `${method} ${path}`); checks++;
  const text = await response.text();
  return text && status === 200 ? JSON.parse(text) : null;
}
(async () => {
  const accounts = ['admin','dean','head1','head2','head3','lecturer1','lecturer2','lecturer3','lecturer4','lecturer5','lecturer6','editor','viewer'];
  const tokens = {}, profiles = {};
  await request('/health');
  const details = await request('/api/health');
  assert.equal(details.database, 'ok'); assert.equal(details.ai, 'ok'); assert.equal(details.objectStorage, 'ok');
  for (const account of accounts) {
    const login = await request('/api/auth/login', null, 'POST', { userName: 'demo.' + account, password });
    assert.ok(login.accessToken); tokens[account] = login.accessToken;
    const profile = await request('/api/auth/me', tokens[account]); profiles[account] = profile;
    assert.ok(profile.teacherId);
    if (account === 'dean') assert.equal(profile.positionLabel, 'Trưởng khoa');
    if (account.startsWith('head')) assert.equal(profile.positionLabel, 'Trưởng bộ môn');
    if (account.startsWith('lecturer')) assert.equal(profile.positionLabel, 'Giảng viên');
  }
  const capabilities = await request('/api/system/capabilities', tokens.dean);
  assert.equal(capabilities.developmentDataWarning, 'Dữ liệu lịch mẫu, chưa phải lịch chính thức');
  const counts = await request('/api/system/counts', tokens.dean);
  assert.ok(counts.students >= 57 && counts.sessions >= 75 && counts.openAlerts > 0);
  const classes = await request('/api/class-sections?pageSize=100', tokens.dean);
  const demoClasses = classes.items.filter(x => (x.data.sourceClassSectionCode ?? x.data.code).startsWith('DEMO-LHP-'));
  assert.equal(demoClasses.length, 12);
  const editorClass = demoClasses.find(x => (x.data.sourceClassSectionCode ?? x.data.code) === 'DEMO-LHP-01').data;
  const viewerClass = demoClasses.find(x => (x.data.sourceClassSectionCode ?? x.data.code) === 'DEMO-LHP-04').data;
  const editor = await request(`/api/class-sections/${editorClass.id}`, tokens.editor);
  const viewer = await request(`/api/class-sections/${viewerClass.id}`, tokens.viewer);
  assert.equal(editor.permissions.canEditDetails, true); assert.equal(editor.permissions.canManageStudents, true);
  assert.equal(editor.permissions.canDeactivate, false); assert.equal(viewer.permissions.canEditDetails, false);
  assert.equal(editor.schedules.length, 2);
  assert.equal(editor.term.isDevelopment, true); assert.equal(editor.term.datesConfirmed, false);
  assert.equal(editor.dateRange.startsOn, editor.term.startsOn); assert.equal(editor.dateRange.endsOn, editor.term.endsOn);
  await request(`/api/class-sections/${viewerClass.id}`, tokens.editor, 'GET', null, 403);
  await request(`/api/class-sections/${editorClass.id}`, tokens.viewer, 'GET', null, 403);
  await request(`/api/class-sections/${editorClass.id}`, tokens.editor, 'DELETE', null, 403);
  await request('/api/students', tokens.lecturer1, 'GET', null, 403);
  await request('/api/catalogs/teachers', tokens.admin, 'POST', { fullName: 'Forbidden', email: 'forbidden@example.invalid', departmentId: editorClass.departmentId }, 403);
  const departments = await request('/api/catalogs/departments', tokens.dean);
  const empty = departments.find(x => x.code === 'DEMO-EMPTY');
  const vacancies = await request(`/api/lookups/departments?vacantOnly=true&facultyId=${empty.facultyId}`, tokens.dean);
  assert.ok(vacancies.items.some(x => x.id === empty.id));
  const sessions = await request(`/api/sessions?classSectionId=${editorClass.id}&departmentId=${editorClass.departmentId}&page=1&pageSize=100`, tokens.dean);
  assert.ok(sessions.total > 0); assert.ok(sessions.items.every(x => x.classSectionId === editorClass.id));
  const completed = sessions.items.find(x => x.status === 'COMPLETED'); assert.ok(completed);
  const report = await request(`/api/reports/sessions/${completed.id}`, tokens.editor);
  assert.equal(report.attendance.length, 19); assert.ok(report.summary); assert.ok(report.alerts.length > 0);
  assert.deepEqual([...new Set(report.attendance.map(x => x.status))].sort(), ['ABSENT','PARTIAL','PRESENT']);
  const roster = await request(`/api/sessions/${completed.id}`, tokens.editor);
  assert.equal(roster.roster.length, 19);
  for (const department of departments.filter(x => x.code.startsWith('DEMO-') && x.code !== 'DEMO-EMPTY')) {
    const filtered = await request(`/api/sessions?departmentId=${department.id}&page=1&pageSize=100`, tokens.dean);
    assert.ok(filtered.total > 0);
    const ids = new Set(classes.items.filter(x => x.data.departmentId === department.id).map(x => x.data.id));
    assert.ok(filtered.items.every(x => ids.has(x.classSectionId)));
  }
  const llm = await request('/api/llm/status', tokens.admin);
  assert.equal(llm.configured, false); assert.equal(llm.fallbackAvailable, true);
  const web = process.argv[3] || 'http://127.0.0.1:5173';
  if (!/^http:\/\/(localhost|127\.0\.0\.1):\d+$/.test(web)) throw Error('Local development web required.');
  const htmlResponse = await fetch(web, { signal: AbortSignal.timeout(5000) }); assert.equal(htmlResponse.status, 200);
  const html = await htmlResponse.text(); const scripts = [...html.matchAll(/<script[^>]+src="([^"]+)"/g)].map(x => x[1]);
  assert.ok(scripts.length >= 2);
  for (const script of scripts) assert.equal((await fetch(new URL(script, web), { signal: AbortSignal.timeout(5000) })).status, 200);
  assert.equal((await fetch(web + '/api/health', { signal: AbortSignal.timeout(5000) })).status, 200);
  console.log(`DEVELOPMENT_DOCKER_API_OK (${checks} HTTP checks; 13 logins; roster, reports, filters, grants, denials, unavailable LLM)`);
  console.log(`WEB_ASSETS_AND_NGINX_PROXY_OK (${scripts.length} scripts + index + /api/health)`);
})().catch(error => { console.error(error.message); process.exitCode = 1; });
