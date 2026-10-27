const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('../apps/web/node_modules/typescript');

const root = path.resolve(__dirname, '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

function loadDateOnly() {
  const source = read('apps/web/src/app/shared/date-only.ts');
  const code = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const sandbox = { exports: {} };
  vm.runInNewContext(code, sandbox);
  return sandbox.exports;
}

test('Vietnamese date-only helpers validate leap years without UTC conversion', () => {
  const { parseVietnameseDate, formatDateOnly, normalizeAcademicYear, normalizeSemester, validAcademicYear } = loadDateOnly();
  assert.equal(parseVietnameseDate('1/2/2026'), '2026-02-01');
  assert.equal(parseVietnameseDate('29/02/2000'), '2000-02-29');
  for (const value of ['31/02/2026', '29/02/2100', '00/12/2026', '10/25/2026', '2026-02-01']) {
    assert.equal(parseVietnameseDate(value), null, value);
  }
  assert.equal(formatDateOnly('2026-02-01T00:00:00Z'), '01/02/2026');
  assert.equal(normalizeAcademicYear(' 2026 – 2027 '), '2026-2027');
  assert.equal(normalizeSemester(' 1 '), 'HK1');
  assert.equal(validAcademicYear('2026-2027'), true);
  assert.equal(validAcademicYear('2026-2028'), false);
});

test('migration 014 is mirrored, additive and registered without changing old course rows', () => {
  const canonical = read('database/014_course_period_and_business_sequences.sql');
  const sqlServer = read('database/sqlserver/014_course_period_and_business_sequences.sql');
  assert.equal(canonical, sqlServer);
  for (const fragment of [
    "COL_LENGTH('dbo.Courses', 'StartYear') IS NULL",
    "COL_LENGTH('dbo.Courses', 'EndYear') IS NULL",
    "COL_LENGTH('dbo.Courses', 'StartSemester') IS NULL",
    "OBJECT_ID('dbo.BusinessCodeSequences','U') IS NULL",
    "COL_LENGTH('dbo.Sessions', 'Code') IS NULL",
    'StartYear IS NULL AND EndYear IS NULL',
  ]) assert.ok(canonical.includes(fragment), fragment);
  assert.ok(!/UPDATE\s+dbo\.Courses/i.test(canonical), 'legacy courses are not backfilled with invented dates');
  assert.ok(read('services/backend/src/HTBAM.Infrastructure/Data/SqlMigrationRunner.cs').includes('014_course_period_and_business_sequences.sql'));
});

test('student edits cannot overwrite official or anonymous identifiers', () => {
  const backend = read('services/backend/src/HTBAM.Api/Controllers/StudentsController.cs');
  const frontend = read('apps/web/src/app/features/students.component.ts');
  assert.ok(backend.includes('request = request with { StudentCode = student.StudentCode, AnonymousCode = student.AnonymousCode }'));
  assert.ok(!backend.includes('student.StudentCode = request.StudentCode.Trim()'));
  assert.ok(!backend.includes('student.AnonymousCode = (request.AnonymousCode ?? "").Trim()'));
  assert.ok(frontend.includes('@if(!editingStudentId())'));
  assert.ok(!frontend.includes('formControlName="anonymousCode"'));
});

test('DEMO notifications are browser-only, stable per user and gated by backend capability', () => {
  const notice = read('apps/web/src/app/shared/notification-center.component.ts');
  const shell = read('apps/web/src/app/layout/shell.component.ts');
  assert.ok(notice.includes("const DEMO_KEY = 'system-demo-data-v1'"));
  assert.ok(notice.includes('htbam:notifications:${this.auth.user().id ?? this.auth.user().userName}'));
  assert.ok(notice.includes('localStorage.setItem'));
  assert.ok(notice.includes('if(!this.demo){this.notices.set([]);return;}'));
  assert.ok(notice.includes("@HostListener('document:keydown.escape'"));
  assert.ok(notice.includes('Không có thông báo mới.'));
  assert.ok(shell.includes('<app-notification-center [demo]="!!developmentWarning()" />'));
  assert.ok(shell.includes('aria-label="Cảnh báo nghiệp vụ"'));
  assert.ok(!notice.includes('/api/alerts'), 'DEMO notice must not use the business Alert API');
});

test('product title, HUIT logo and favicon assets are wired into login, shell and loading states', () => {
  const index = read('apps/web/src/index.html');
  assert.ok(index.includes('<title>Hệ thống giám sát lớp học - Khoa Công nghệ Thông tin HUIT</title>'));
  assert.ok(!index.includes('>HTBAM<'));
  for (const relative of [
    'apps/web/public/assets/huit-logo.png',
    'apps/web/public/assets/branding/favicon-16x16.png',
    'apps/web/public/assets/branding/favicon-32x32.png',
    'apps/web/public/assets/branding/apple-touch-icon.png',
    'apps/web/public/favicon.ico',
  ]) {
    const data = fs.readFileSync(path.join(root, relative));
    assert.ok(data.length > 100, relative);
  }
  for (const relative of [
    'apps/web/src/app/features/login.component.ts',
    'apps/web/src/app/layout/shell.component.ts',
    'apps/web/src/app/app.component.ts',
  ]) assert.ok(read(relative).includes('assets/huit-logo.png'), relative);
});

test('business-code allocation stays transaction protected and browser camera support remains intact', () => {
  const codes = read('services/backend/src/HTBAM.Api/Support/BusinessCodes.cs');
  const transaction = read('services/backend/src/HTBAM.Api/Support/BusinessTransaction.cs');
  const catalogs = read('services/backend/src/HTBAM.Api/Controllers/CatalogsController.cs');
  assert.ok(codes.includes('db.Database.CurrentTransaction is null'));
  assert.ok(codes.includes('BusinessCodeSequences'));
  assert.ok(transaction.includes("@LockMode='Exclusive'"));
  assert.ok(catalogs.includes('browser://laptop'));
  assert.ok(catalogs.includes('uri.Scheme is not ("rtsp" or "rtsps" or "browser")'));
});
