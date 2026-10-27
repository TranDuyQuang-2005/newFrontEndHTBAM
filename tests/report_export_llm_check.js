const assert = require('node:assert/strict');
const fs = require('node:fs');
const test = require('node:test');
const vm = require('node:vm');
const ts = require('../apps/web/node_modules/typescript');

const read = file => fs.readFileSync(file, 'utf8');
const reports = read('apps/web/src/app/features/reports.component.ts');
const api = read('apps/web/src/app/core/api.service.ts');
const controller = read('services/backend/src/HTBAM.Api/Controllers/ReportsController.cs');
const files = read('services/backend/src/HTBAM.Api/Support/ReportFileBuilder.cs');
const comments = read('services/backend/src/HTBAM.Application/Services/AutoCommentService.cs');
const compose = read('infra/docker-compose.yml');
const envExample = read('infra/.env.example');

function reportFormat() {
  const code = ts.transpileModule(read('apps/web/src/app/shared/report-format.ts'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const sandbox = { exports: {}, Intl, Date, Number, Math };
  vm.runInNewContext(code, sandbox);
  return sandbox.exports;
}

test('report formatter uses field semantics instead of numeric range', () => {
  const { formatReportValue, reportColumnLabel } = reportFormat();
  assert.equal(formatReportValue(1, 'studentId', 'en-US'), '1');
  assert.equal(formatReportValue(1, 'alertCount', 'en-US'), '1');
  assert.equal(formatReportValue(90, 'alertDurationSeconds', 'en-US'), '1 phút 30 giây');
  assert.equal(formatReportValue(.25, 'focusedRatio', 'en-US'), '25%');
  assert.equal(formatReportValue(.82, 'probability', 'en-US'), '82%');
  assert.equal(formatReportValue(50, 'scorePercent', 'en-US'), '50%');
  assert.equal(formatReportValue(10, 'policyScore', 'en-US'), '10');
  assert.equal(reportColumnLabel('scorePercent'), 'Điểm chuyên cần (%)');
});

test('report UI exposes scoped export loading and errors', () => {
  assert.match(reports, /exporting\(\)==='excel'/);
  assert.match(reports, /Không thể xuất/);
  assert.match(reports, /content-disposition/i);
  assert.match(api, /observe:'response'/);
  assert.match(controller, /CanViewReportClassSectionAsync/);
  assert.match(controller, /CanViewReportStudentAsync/);
  assert.match(controller, /CanExportSessionReportAsync/);
});

test('exports include summary and available report collections', () => {
  for (const marker of ['Tổng hợp', 'Chuyên cần', 'Sự kiện hành vi', 'Cảnh báo', 'Kết quả nhận diện', 'Video'])
    assert.ok(files.includes(marker), `missing export section ${marker}`);
  assert.match(controller, /He-thong-giam-sat-Bao-cao-lop-hoc-phan/);
  assert.match(controller, /ReportFileBuilder\.Pdf\(report/);
  assert.match(files, /Take\(12\)/);
  assert.match(files, /scorepercent/);
});

test('reports hide internal identifiers and technical inference fields', () => {
  const { isReportColumnVisible } = reportFormat();
  for (const field of ['id', 'studentId', 'sessionId', 'trackId', 'confidence', 'modelName', 'embeddingDimension'])
    assert.equal(isReportColumnVisible(field), false, field);
  for (const field of ['studentCode', 'fullName', 'presentSeconds', 'focusedRatio'])
    assert.equal(isReportColumnVisible(field), true, field);
  assert.doesNotMatch(reports, /Nguồn:<\/strong> \{\{info\.provider/);
  assert.doesNotMatch(reports, /Gemini không khả dụng/);
});

test('auto comment endpoints and fallback state are wired', () => {
  for (const marker of ['generateSessionAutoComment', 'generateStudentAutoComment', 'generateClassSectionAutoComment'])
    assert.ok(api.includes(marker), `missing ApiService method ${marker}`);
  assert.match(reports, /Tạo lại nhận xét/);
  assert.match(reports, /RULE_BASED_FALLBACK/);
  assert.match(reports, /RATE_LIMIT/);
  assert.match(reports, /INVALID_MODEL/);
  assert.match(reports, /NETWORK_ERROR/);
  assert.match(reports, /autoCommentAvailability/);
  assert.match(reports, /commentUnavailableReason/);
});

test('Gemini prompt excludes business and personal identifiers', () => {
  assert.match(comments, /"STUDENT", "STORED_SUMMARY", "Sinh viên trong buổi học"/);
  assert.doesNotMatch(comments, /studentCode/);
  assert.doesNotMatch(comments, /ClassSectionName=/);
  assert.doesNotMatch(comments, /\$"SessionId=/);
});

test('compose passes Gemini key without committing a real key', () => {
  assert.match(compose, /GEMINI_API_KEY: "\$\{GEMINI_API_KEY:-\}"/);
  assert.match(envExample, /GEMINI_API_KEY=PASTE_YOUR_REAL_GEMINI_API_KEY_HERE/);
  assert.doesNotMatch(envExample, /AIza[0-9A-Za-z_-]{20,}/);
});
