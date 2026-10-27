// Local UI acceptance test. Creates and retains the demonstration records requested by the user.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require(process.env.HTBAM_PLAYWRIGHT_MODULE||'playwright');
const base=process.argv[2]||'http://127.0.0.1:5173',api='http://127.0.0.1:5000';
if(!/^http:\/\/127\.0\.0\.1:\d+$/.test(base))throw Error('Loopback only.');
const out=path.join(__dirname,'../.runtime-fix-backup-catalog-codes/browser');fs.mkdirSync(out,{recursive:true});
let checks=0;const check=(v,m)=>{assert.ok(v,m);checks++;};
(async()=>{
 const login=await fetch(api+'/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:'demo.dean',password:process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#'})});assert.equal(login.status,200);const token=(await login.json()).accessToken;
 const departments=await (await fetch(api+'/api/catalogs/departments',{headers:{Authorization:'Bearer '+token}})).json();
 const legacy=departments.find(x=>!/^BM-[A-Z0-9]+(?:-[0-9]{2})?$/.test(x.code));check(!!legacy,'legacy reference is available');
 const browser=await chromium.launch({headless:true});const records=[],errors=[];let page;
 try{
  for(const [name,viewport] of [['desktop',{width:1366,height:900}],['mobile',{width:390,height:844}]]){
   const context=await browser.newContext({viewport,isMobile:name==='mobile',hasTouch:name==='mobile'});await context.addInitScript(t=>sessionStorage.setItem('htbam_token',t),token);page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
   await page.goto(base+'/management?tab=departments');await page.getByRole('button',{name:/Thêm Bộ môn/}).click();
   let dialog=page.getByRole('dialog',{name:'Thêm Bộ môn'});await dialog.waitFor();
   check(await dialog.getByText('Tự động tạo khi lưu',{exact:true}).count()===1,name+' automatic department code information');
   check(await dialog.locator('input[name*=code i]').count()===0,name+' no manual department code field');
   await dialog.getByRole('textbox',{name:/^Tên/}).fill('Công nghệ dữ liệu');
   const faculty=dialog.getByRole('combobox',{name:'Khoa',exact:true});await faculty.click();await page.locator('.search-select-panel:visible [role=option][aria-disabled=false]').first().click();
   await page.screenshot({path:path.join(out,name+'-department.png')});
   let response=page.waitForResponse(r=>r.url().endsWith('/api/catalogs/departments')&&r.request().method()==='POST');await dialog.getByRole('button',{name:'Lưu',exact:true}).click();let saved=await response;check(saved.status()===200,name+' department saved through UI');const department=await saved.json();
   check(/^BM-CNDL(?:-\d{2})?$/.test(department.code),name+' normalized department code');check(!('code' in saved.request().postDataJSON()),name+' client omits department code');
   await dialog.waitFor({state:'hidden'});await page.getByRole('cell',{name:department.code,exact:true}).waitFor();records.push({viewport:name,type:'department',id:department.id,code:department.code});
   await page.getByRole('tab',{name:'Môn học',exact:true}).click();
   const courseCount=name==='desktop'?2:1;
   for(let i=1;i<=courseCount;i++){
    await page.locator('[role=tabpanel] .filter-bar input').fill('Bộ lọc không được che môn vừa tạo');await page.getByRole('button',{name:/Thêm Môn học/}).click();dialog=page.getByRole('dialog',{name:'Thêm Môn học'});await dialog.waitFor();
    check(await dialog.locator('input[name*=code i]').count()===0,name+' no manual course code input');
    const title=dialog.getByRole('textbox',{name:/Tên môn học/});check(await title.evaluate(el=>el.tagName==='INPUT'&&el.type==='text'),name+' single line course name');
    await title.fill(`Kiểm chứng mã tự động ${name} ${i}`);await dialog.getByRole('textbox',{name:'Ghi chú',exact:true}).fill('Bản ghi kiểm chứng UI theo yêu cầu ngày 25/10/2026. Giữ lại để đối chiếu.');
    const dept=dialog.getByRole('combobox',{name:'Bộ môn',exact:true});
    if(i===1){
     await dept.click();await dept.fill(legacy.code);let option=page.locator('.search-select-panel:visible [role=option]').filter({hasText:legacy.code}).first();await option.waitFor();check((await option.innerText()).includes('Mã cũ'),name+' legacy badge');await option.click();
     await dialog.getByText('Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi tạo môn học.',{exact:true}).waitFor();check(await dialog.getByRole('button',{name:'Lưu',exact:true}).isDisabled(),name+' legacy department blocks saving');
     await page.screenshot({path:path.join(out,name+'-legacy.png')});
    }
    await dept.click();await dept.fill(department.code);const option=page.locator('.search-select-panel:visible [role=option]').filter({hasText:department.code}).first();await option.waitFor();
    check(await page.locator('.search-select-panel:visible [role=option]').count()<=10,name+' suggestions are bounded');
    const panel=await page.locator('.search-select-panel:visible').boundingBox(),anchor=await dept.boundingBox(),credits=await dialog.locator('input[name=courseCredits]').boundingBox();
    check(panel.width<=anchor.width+1&&panel.x>=anchor.x-1&&panel.x+panel.width<=anchor.x+anchor.width+1,name+' dropdown fits department field');check(panel.height<=321,name+' dropdown <=320px');
    if(name==='desktop')check(Math.abs(anchor.y-credits.y)<=1&&Math.abs(anchor.height-credits.height)<=1&&panel.x+panel.width<=credits.x,name+' aligned equal-height fields without overlap');
    await page.screenshot({path:path.join(out,name+'-dropdown.png')});await option.click();
    const expected=`MH-${department.code.slice(3)}-${String(i).padStart(3,'0')}`;await dialog.getByText('Mã dự kiến: '+expected,{exact:false}).waitFor();
    await dialog.locator('input[type=checkbox]').scrollIntoViewIfNeeded();const box=await dialog.boundingBox();check(box.x>=0&&box.x+box.width<=viewport.width+1,name+' popup fits viewport');
    check(await dialog.locator('.modal-body').evaluate(el=>el.scrollWidth<=el.clientWidth+1),name+' no popup horizontal overflow');
    const footer=await dialog.locator('.catalog-footer').boundingBox(),checkbox=await dialog.locator('label.check').boundingBox();check(checkbox.y+checkbox.height<=footer.y+1,name+' footer does not cover form');
    await page.screenshot({path:path.join(out,name+'-course.png')});
    response=page.waitForResponse(r=>r.url().endsWith('/api/catalogs/courses')&&r.request().method()==='POST');await dialog.getByRole('button',{name:'Lưu',exact:true}).click();saved=await response;check(saved.status()===200,name+' course saved through UI');const course=await saved.json();
    check(course.code===expected,name+' server allocates sequential course code');check(!('code' in saved.request().postDataJSON()),name+' client omits course code');await dialog.waitFor({state:'hidden'});await page.getByRole('cell',{name:course.code,exact:true}).waitFor();
    check(await page.locator('[role=tabpanel] .filter-bar input').inputValue()==='',name+' filter clears and saved row is visible');records.push({viewport:name,type:'course',id:course.id,code:course.code});
   }
   await page.screenshot({path:path.join(out,name+'-list.png')});await context.close();
  }
  check(errors.length===0,'no runtime errors: '+errors.join(';'));fs.writeFileSync(path.join(out,'report.json'),JSON.stringify({checks,records,errors},null,2));console.log('CATALOG_CODES_BROWSER_OK '+JSON.stringify({checks,records}));
 }catch(e){if(page&&!page.isClosed())await page.screenshot({path:path.join(out,'failure.png')});throw e;}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
