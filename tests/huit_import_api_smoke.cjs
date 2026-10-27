// Real workbook validation only; does not import real schedules or create catalog records.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{spawnSync}=require('node:child_process');
const base=process.argv[2]||'http://127.0.0.1:5000';
if(!/^http:\/\/127\.0\.0\.1:\d+$/.test(base))throw Error('Loopback API required.');
const filename=process.env.HTBAM_HUIT_WORKBOOK;if(!filename)throw Error('Set HTBAM_HUIT_WORKBOOK.');
const bytes=fs.readFileSync(filename);let checks=0;
function check(value,message){assert.ok(value,message);checks++;}
async function request(url,token,body,expected=200){const response=await fetch(base+url,{method:body?'POST':'GET',headers:{...(token?{Authorization:'Bearer '+token}:{}),...(body instanceof FormData?{}:{'Content-Type':'application/json'})},body:body?(body instanceof FormData?body:JSON.stringify(body)):undefined,signal:AbortSignal.timeout(60000)});const text=await response.text();assert.equal(response.status,expected,`${url}: ${text.slice(0,500)}`);checks++;try{return JSON.parse(text);}catch{return text;}}
function upload(term,policy){const f=new FormData();f.append('file',new Blob([bytes],{type:'application/vnd.ms-excel'}),'huit-reference.xls');f.append('sheet','Sheet1');if(term){f.append('academicTermId',term);f.append('attendancePolicyId',policy);}return f;}
function counts(){const query='SET NOCOUNT ON; SELECT t.name,SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN(0,1) GROUP BY t.name ORDER BY t.name;';const r=spawnSync('docker',['compose','-f',path.join(__dirname,'../infra/docker-compose.yml'),'exec','-T','sqlserver','bash','-c','export SQLCMDPASSWORD=$MSSQL_SA_PASSWORD; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d HTBAM -W -h -1'],{input:query,encoding:'utf8'});if(r.status!==0)throw Error('Cannot read SQL table counts: '+r.stderr);return r.stdout.trim();}
(async()=>{
  const token=(await request('/api/auth/login',null,{userName:'demo.dean',password:process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#'})).accessToken;
  const viewer=(await request('/api/auth/login',null,{userName:'demo.viewer',password:process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#'})).accessToken;
  const config=await request('/api/academic-configuration',token),term=config.terms.find(t=>t.academicYear==='2025-2026'&&t.semester===1);
  const policies=await request('/api/attendance-policies',token),policy=policies.find(p=>p.isActive);
  const before=counts();const timer=performance.now();
  const sheets=await request('/api/class-sections/import/sheets',token,upload());
  check(sheets.maxRows===20000&&sheets.maxColumns===64,'new limits');const info=sheets.sheetDetails.find(s=>s.name==='Sheet1');
  check(info.rowCount===10202&&info.columnCount===41,'actual file dimensions');check(info.headerRow===7&&info.headerRowCount===2&&info.format==='HUIT','HUIT headers detected');
  check(info.headerStartRow===7&&info.headerEndRow===8&&info.dataStartRow===9,'explicit header and data rows');
  check(info.columnGroups.length===1&&info.columnGroups[0].field==='weeks'&&info.columnGroups[0].columns.length===26,'week group metadata');
  check(info.columns[10].letter==='K'&&info.columns[10].header==='Thời gian học / Bắt đầu'&&info.columns[37].letter==='AL'&&info.columns[37].header==='Thời gian học / Kết thúc','start/end columns');
  check(info.columns[11].header==='Tuần học / T1'&&info.columns[36].header==='Tuần học / T26'&&info.columns[12].header!=='Cột M','real second header labels');
  check(info.columns[38].letter==='AM'&&info.columns[38].header==='Tổng số tiết'&&info.columns[39].letter==='AN'&&info.columns[40].letter==='AO','total/room/teacher columns');
  check(info.sampleRows.length===5&&info.sampleRows[0].row===9&&!!info.columns[40].sample&&!!info.columns[10].sample,'five bounded sample rows');
  const inspectedForm=upload();inspectedForm.append('headerRow','7');inspectedForm.append('headerRowCount','2');
  const inspected=await request('/api/class-sections/import/inspect',token,inspectedForm);check(inspected.mappings.filter(m=>/^week\d+$/.test(m.field)).length===26,'T1-T26 columns');
  const p=await request('/api/class-sections/import/preview',token,upload(term.id,policy.id));
  check(p.workbookRows===10202&&p.workbookColumns===41&&p.dataRows===10190&&p.totalGroups===5447,'entire file validated');
  check(p.groups.reduce((n,g)=>n+g.rows.length,0)===100,'default 100 preview rows');check(p.errorRows>0&&!p.weekOneMonday&&p.classesToCreate===0,'missing official data blocks import');
  const page2=await request(`/api/class-sections/import/preview/${p.token}?page=2&pageSize=200`,token);check(page2.groups.reduce((n,g)=>n+g.rows.length,0)===200&&page2.page===2,'server pagination');
  const capped=await request(`/api/class-sections/import/preview/${p.token}?pageSize=10000`,token);check(capped.pageSize===200,'oversize preview page is capped');
  const found=await request(`/api/class-sections/import/preview/${p.token}?q=010110215301&status=ERROR`,token);check(found.matchingRows===2&&found.groups.length===1,'source search and error filter');
  for(const url of [`/api/class-sections/import/preview/${p.token}`,`/api/class-sections/import/preview/${p.token}/download`,'/api/class-sections/import/template',`/api/class-sections/import/result/${p.token}/download`])await request(url,viewer,undefined,403);
  const template=await fetch(base+'/api/class-sections/import/template',{headers:{Authorization:'Bearer '+token}});check(template.status===200&&template.headers.get('content-type').includes('spreadsheetml'),'template download');
  fs.writeFileSync(path.join(__dirname,'../.runtime-fix-backup-huit-import/downloaded-template.xlsx'),Buffer.from(await template.arrayBuffer()));
  const validation=await fetch(base+`/api/class-sections/import/preview/${p.token}/download`,{headers:{Authorization:'Bearer '+token}});
  const csv=await validation.text();check(validation.status===200&&csv.includes('010110215301')&&csv.split(/\r?\n/).length>=p.dataRows+1,'full validation CSV download');
  const blocked=await request('/api/class-sections/import/confirm',token,{token:p.token},409);check(blocked.message.includes('lỗi bắt buộc'),'mandatory errors rejected by backend');
  check(counts()===before,'all SQL table counts unchanged by sheets/inspect/preview/pagination/failed confirmation');
  const report={checks,rows:p.workbookRows,columns:p.workbookColumns,dataRows:p.dataRows,groups:p.totalGroups,errorRows:p.errorRows,unresolvedCourseRows:p.unresolvedCourseRows,unresolvedTeacherRows:p.unresolvedTeacherRows,unresolvedRoomRows:p.unresolvedRoomRows,elapsedSeconds:((performance.now()-timer)/1000).toFixed(2),firstPageBytes:Buffer.byteLength(JSON.stringify(p))};
  fs.writeFileSync(path.join(__dirname,'../.runtime-fix-backup-huit-import/api-report.json'),JSON.stringify(report,null,2));console.log('HUIT_HTTP_REAL_WORKBOOK_OK '+JSON.stringify(report));
})().catch(e=>{console.error(e.message);process.exitCode=1;});
