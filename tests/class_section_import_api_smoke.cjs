const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const base=process.argv[2]||'http://127.0.0.1:5000';
let checks=0;
let importSettings=null;
function check(value,message){assert.ok(value,message);checks++;}
async function call(method,url,token,body,status=200){const response=await fetch(base+url,{method,headers:{...(token?{Authorization:'Bearer '+token}:{}),...(body instanceof FormData?{}:{'Content-Type':'application/json'})},body:body?(body instanceof FormData?body:JSON.stringify(body)):undefined});const text=await response.text();assert.equal(response.status,status,`${method} ${url}: ${text}`);checks++;if(!text)return null;try{return JSON.parse(text);}catch{return text;}}
function upload(extension,term,policy){const form=new FormData();form.append('file',new Blob([fs.readFileSync(path.join(__dirname,'fixtures','class-section-demo.'+extension))],{type:extension==='xls'?'application/vnd.ms-excel':'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'}),'demo.'+extension);if(term){form.append('sheet','Lịch');form.append('academicTermId',term);form.append('attendancePolicyId',policy);if(importSettings)form.append('settings',JSON.stringify(importSettings));}return form;}
(async()=>{
  const password=process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#';
  const manager=(await call('POST','/api/auth/login',null,{userName:'demo.dean',password})).accessToken;
  const viewer=(await call('POST','/api/auth/login',null,{userName:'demo.viewer',password})).accessToken;
  const admin=(await call('POST','/api/auth/login',null,{userName:'demo.admin',password})).accessToken;
  const config=await call('GET','/api/academic-configuration',manager);
  const term=config.terms.find(x=>x.academicYear==='2026-2027'&&x.semester===1);
  const policies=await call('GET','/api/attendance-policies',manager);
  const policy=policies.find(x=>x.isActive);
  const course=(await call('GET','/api/catalogs/courses',manager)).find(x=>x.code==='DEMO-MH-01'||x.name==='Cơ sở dữ liệu (DEMO)');
  const teacher=(await call('GET','/api/catalogs/teachers',manager)).find(x=>x.fullName==='DEMO editor');
  assert.ok(course&&teacher,'seed course/teacher identities');
  // Keep the sanitized legacy-code workbook; choose the real catalog IDs explicitly for old/new seed codes.
  importSettings={courseSelections:{'DEMO-EXCEL-01':course.id},teacherSelections:{2:teacher.id,3:teacher.id}};
  const anchor=await call('GET','/api/academic-configuration/week-one?academicYear=2026-2027&semester=1',manager);
  check(anchor.weekOneMonday==='2026-08-17'&&anchor.isDevelopment,'DEMO week anchor');
  for(const extension of ['xls','xlsx']){const sheets=await call('POST','/api/class-sections/import/sheets',manager,upload(extension));check(sheets.sheets.includes('Lịch')&&sheets.sheets.length===2,'multiple sheets '+extension);}
  await call('POST','/api/class-sections/import/sheets',viewer,upload('xlsx'),403);
  await call('POST','/api/class-sections/import/preview',viewer,upload('xlsx',term.id,policy.id),403);
  await call('POST','/api/class-sections/import/confirm',viewer,{token:'forged'},403);
  await call('POST','/api/class-sections/import/sheets',null,upload('xlsx'),401);
  const bad=new FormData();bad.append('file',new Blob(['bad'],{type:'text/plain'}),'bad.xlsx');await call('POST','/api/class-sections/import/sheets',manager,bad,400);
  const preview=await call('POST','/api/class-sections/import/preview',manager,upload('xlsx',term.id,policy.id));
  check(preview.groups.length===1&&preview.groups[0].rows.length===2,'group LT/TH');
  check(preview.errorRows===0,JSON.stringify(preview.groups));
  check(preview.weekMessage.includes('17/08/2026'),'T1 mapping visible');
  const result=await call('POST','/api/class-sections/import/confirm',manager,{token:preview.token});
  const replay=await call('POST','/api/class-sections/import/confirm',manager,{token:preview.token});check(JSON.stringify(result)===JSON.stringify(replay),'same confirmation returns same result');
  const again=await call('POST','/api/class-sections/import/preview',manager,upload('xls',term.id,policy.id));
  check(again.classesToCreate===0&&again.duplicates===2,'same workbook data in xls is duplicate');
  const noWrites=await call('POST','/api/class-sections/import/confirm',manager,{token:again.token});check(noWrites.createdClasses===0&&noWrites.createdSchedules===0,'reimport creates nothing');
  const classes=await call('GET','/api/class-sections?pageSize=100',manager);
  const imported=classes.items.filter(x=>x.data.sourceClassSectionCode==='DEMO-EXCEL-01');check(imported.length===1,'one source class only');
  const detail=await call('GET','/api/class-sections/'+imported[0].data.id,manager);
  check(detail.code.startsWith('LHP-2627-HK1-'+course.code+'-')&&/\d{2}$/.test(detail.code)&&detail.schedules.length===2,'internal code and two schedules');
  await call('PUT',`/api/academic-configuration/terms/${term.id}/week-one`,manager,{weekOneMonday:'2026-08-17',confirmed:true,evidence:'forbidden'},403);
  await call('PUT',`/api/academic-configuration/terms/${term.id}/week-one`,admin,{weekOneMonday:'2026-08-18',confirmed:true,evidence:'bad weekday'},400);
  console.log(`CLASS_SECTION_IMPORT_API_OK: ${checks} checks. Class ${detail.code}; source ${detail.sourceClassSectionCode}.`);
})().catch(e=>{console.error(e.message);process.exitCode=1;});
