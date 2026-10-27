// database/sqlserver is authoritative. Root copies are compatibility artifacts only.
const fs = require('node:fs');
const path = require('node:path');
let differences = 0;
for (const name of fs.readdirSync(path.join(__dirname, 'sqlserver')).filter(x => x.endsWith('.sql'))) {
  const source = fs.readFileSync(path.join(__dirname, 'sqlserver', name));
  const target = path.join(__dirname, name);
  if (!fs.existsSync(target) || !source.equals(fs.readFileSync(target))) {
    differences++;
    if (process.argv.includes('--check')) console.error(`SQL mirror differs: ${name}`);
    else fs.writeFileSync(target, source);
  }
}
if (process.argv.includes('--check') && differences) process.exitCode = 1;
else console.log(`SQL mirrors verified${differences ? ` / synchronized ${differences}` : ''}.`);
