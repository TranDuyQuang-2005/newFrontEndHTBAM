const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
for (const args of [
  ['--test', 'tests/overlay_focus_check.js', 'tests/search_select_check.js', 'tests/class_section_ui_check.js'],
  ['tests/frontend_syntax_check.js'],
  ['tests/timetable_source_check.js'],
  ['database/sync-sqlserver.cjs', '--check'],
]) {
  const result = spawnSync(process.execPath, args, { cwd: root, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
