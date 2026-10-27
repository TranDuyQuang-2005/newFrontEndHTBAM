// Local browser acceptance tests. The two teacher records are explicitly requested and retained.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require(process.env.HTBAM_PLAYWRIGHT_MODULE||'playwright');
const base=process.argv[2]||'http://127.0.0.1:5173',api='http://127.0.0.1:5000';if(!/^http:\/\/127\.0\.0\.1:\d+$/.test(base))throw Error('Loopback only.');
const out=path.join(__dirname,'../.runtime-fix-backup-lecturer-codes/browser');fs.mkdirSync(out,{recursive:true});
const recordFile=path.join(out,'records.json'),records=fs.existsSync(recordFile)?JSON.parse(fs.readFileSync(recordFile,'utf8')):[];
let checks=0;const check=(v,m)=>{assert.ok(v,m);checks++;};
async function json(url,token,body){const r=await fetch(api+url,{method:body?'POST':'GET',headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})},...(body?{body:JSON.stringify(body)}:{})});assert.equal(r.status,200,url);return r.json();}
(async()=>{
 const tokens={};for(const role of ['dean','admin','head1','viewer'])tokens[role]=(await json('/api/auth/login',null,{userName:'demo.'+role,password:process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#'})).accessToken;
 const department=(await json('/api/catalogs/departments',tokens.dean)).find(x=>x.code==='BM-CNDL');assert.ok(department,'Use existing normalized BM-CNDL created in prior UI acceptance.');
 const browser=await chromium.launch({headless:true});let page;const errors=[];let screenshots=0;
 const shot=async name=>{await page.screenshot({path:path.join(out,name+'.png')});screenshots++;};
 const contextFor=async(token,viewport)=>{const context=await browser.newContext({viewport,isMobile:viewport.width<600,hasTouch:viewport.width<600});await context.addInitScript(t=>sessionStorage.setItem('htbam_token',t),token);return context;};
 try{
  for(const [name,viewport] of [['desktop',{width:1440,height:1000}],['mobile',{width:390,height:844}]]){
   for(const role of ['dean','admin','head1','viewer']){
    const context=await contextFor(tokens[role],viewport);page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));await page.goto(base+'/management');
    await page.getByRole('tab').first().waitFor();const tabs=await page.getByRole('tab').allTextContents();assert.ok(tabs.length>0);
    const profile=await json('/api/auth/me',tokens[role]);
    for(const label of tabs.map(s=>s.trim())){
     await page.getByRole('tab',{name:label,exact:true}).click();const header=page.locator('app-catalog-section-header');await header.getByRole('heading',{name:label,exact:true}).waitFor();
     check(await page.getByRole('tab',{selected:true}).count()===1,`${name}/${role}/${label}: one selected tab`);
     check(await header.count()===1,`${name}/${role}/${label}: one active section header`);
     check(await page.locator('app-page-title button').count()===0,`${name}/${role}/${label}: no old title actions`);
     const positions=await header.evaluate(el=>({header:el.getBoundingClientRect().top,tabs:document.querySelector('[role=tablist]').getBoundingClientRect().bottom}));check(positions.header>=positions.tabs-1,`${name}/${role}/${label}: header below tabs`);
     const buttons=header.getByRole('button'),names=await buttons.allTextContents();
     if(label==='Lớp học phần'){
      check(names.length===(profile.permissions.includes('CLASS_SECTION_CREATE')?2:0),`${name}/${role}: class actions respect permission`);
      if(names.length)check(names[0].includes('Thêm')&&names[1].trim()==='Nhập Excel',`${name}: primary then import`);
     }else check(!names.some(s=>s.includes('Nhập Excel')),`${name}/${label}: no unsupported import action`);
     if(label==='Giảng viên')check(names.length===(profile.permissions.includes('TEACHER_CREATE')&&role!=='admin'?1:0),`${name}/${role}: teacher creation scope`);
     check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`${name}/${role}/${label}: no viewport overflow`);
     if(names.length){const b=await buttons.first().boundingBox();check(b.x>=0&&b.x+b.width<=viewport.width+1,`${name}/${label}: actions fit`);if(name==='mobile'){const description=await header.locator('p').boundingBox();check(b.y>=description.y+description.height-1,`${name}/${label}: actions below description`);}}
     if(role==='dean'||role==='admin'){await page.waitForLoadState('networkidle');await page.waitForTimeout(180);if(label==='Lớp học phần')await page.getByText('Đang tải lớp học phần…',{exact:true}).waitFor({state:'hidden'});check(await page.locator('.error-box').count()===0,`${name}/${role}/${label}: no unrelated permission errors`);await shot(`${name}-${role}-${label}`);}
    }
    await context.close();
   }
  }
  for(const [i,[name,viewport]] of [['desktop',{width:1440,height:1000}],['mobile',{width:390,height:844}]].entries()){
   const context=await contextFor(tokens.dean,viewport);page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));await page.goto(base+'/management?tab=teachers');
   if(!records[i]){
    await page.locator('[role=tabpanel] .filter-bar input').fill('Bộ lọc cũ không che giảng viên mới');await page.getByRole('button',{name:'+ Thêm Giảng viên',exact:true}).click();
    const dialog=page.getByRole('dialog',{name:'Thêm Giảng viên'});await dialog.waitFor();check(await dialog.locator('input[name*=code i]').count()===0,name+': no manual code input');await dialog.getByText('Tự động tạo khi lưu',{exact:true}).waitFor();
    const fullName=dialog.getByRole('textbox',{name:/Họ tên/});await fullName.fill('Kiểm chứng mã giảng viên '+name);await fullName.press('Tab');const dept=dialog.getByRole('combobox',{name:'Bộ môn',exact:true});check(await dept.evaluate(el=>el===document.activeElement),name+': keyboard order name to department');
    await dept.click();await dept.fill(department.code);const option=page.locator('.search-select-panel:visible [role=option]').filter({hasText:department.code}).first();await option.waitFor();
    const panel=await page.locator('.search-select-panel:visible').boundingBox(),anchor=await dept.boundingBox(),footer=await dialog.locator('.catalog-footer').boundingBox();check(panel.width<=anchor.width+1&&panel.height<=321&&panel.y+panel.height<=footer.y+1,name+': dropdown fits field and avoids footer');await shot(name+'-teacher-dropdown');await option.click();
    await dialog.getByText(/Mã dự kiến: GV-CNDL-\d{4}/).waitFor();
    await dialog.getByRole('textbox',{name:'Email',exact:true}).fill(`qa.gv.${name}@example.invalid`);await dialog.getByRole('textbox',{name:'Ghi chú',exact:true}).fill('Bản ghi kiểm chứng UI theo yêu cầu ngày 25/10/2026; giữ lại để đối chiếu.');
    await dialog.locator('label.check').scrollIntoViewIfNeeded();const box=await dialog.boundingBox();check(box.x>=0&&box.x+box.width<=viewport.width+1,name+': teacher popup fits viewport');await shot(name+'-teacher-create');
    const response=page.waitForResponse(r=>r.url().endsWith('/api/catalogs/teachers')&&r.request().method()==='POST');await dialog.getByRole('button',{name:'Lưu',exact:true}).click();const saved=await response;check(saved.status()===200,name+': actual UI creation');const teacher=await saved.json();
    records[i]={id:teacher.id,teacherCode:teacher.teacherCode,departmentId:teacher.departmentId,fullName:teacher.fullName};fs.writeFileSync(recordFile,JSON.stringify(records,null,2));
    check(!('teacherCode' in saved.request().postDataJSON())&&!('code' in saved.request().postDataJSON()),name+': payload omits client codes');check(/^GV-CNDL-\d{4}$/.test(teacher.teacherCode),name+': backend code format');await dialog.waitFor({state:'hidden'});
    check(await page.locator('[role=tabpanel] .filter-bar input').inputValue()==='',name+': filter cleared after save');
   }
   const savedCell=page.getByRole('cell').filter({hasText:records[i].teacherCode}).first();await savedCell.waitFor();await savedCell.scrollIntoViewIfNeeded();await shot(name+'-teacher-list');
   await page.goto(base+'/organization');const organizationCell=page.getByRole('cell').filter({hasText:records[i].teacherCode}).first();await organizationCell.waitFor();await organizationCell.scrollIntoViewIfNeeded();check(true,name+': organization shows the identical teacher code');await shot(name+'-organization');await context.close();
  }
  check(records.length===2&&records[0].departmentId===records[1].departmentId&&records[0].teacherCode!==records[1].teacherCode,'two actual teachers in same department with distinct codes');check(errors.length===0,'no browser runtime errors: '+errors.join(';'));
  fs.writeFileSync(path.join(out,'report.json'),JSON.stringify({checks,screenshots,records,errors},null,2));console.log('LECTURER_HEADERS_BROWSER_OK '+JSON.stringify({checks,screenshots,records}));
 }catch(e){if(page&&!page.isClosed())await shot('failure');throw e;}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
