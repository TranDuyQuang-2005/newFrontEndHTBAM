// Run against local Docker only. Requires an installed Playwright module and the real HUIT workbook.
// No confirmation/import or catalog writes; screenshots remain in ignored runtime storage.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require(process.env.HTBAM_PLAYWRIGHT_MODULE||'playwright');
const base=process.argv[2]||'http://127.0.0.1:5173',api='http://127.0.0.1:5000';
if(!/^http:\/\/127\.0\.0\.1:\d+$/.test(base))throw Error('Loopback web required.');
const workbook=process.env.HTBAM_HUIT_WORKBOOK;if(!workbook)throw Error('Set HTBAM_HUIT_WORKBOOK.');
const output=path.join(__dirname,'../.runtime-fix-backup-huit-mapping/browser');fs.mkdirSync(output,{recursive:true});
let checks=0;const check=(value,message)=>{assert.ok(value,message);checks++;};
async function get(url,token){const response=await fetch(api+url,{headers:{Authorization:'Bearer '+token}});assert.equal(response.status,200);return response.json();}
(async()=>{
  const response=await fetch(api+'/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:'demo.dean',password:process.env.SEED_DEFAULT_PASSWORD||'HTBAM-Local-Demo!2026#'})});
  assert.equal(response.status,200);const {accessToken}=await response.json();
  const config=await get('/api/academic-configuration',accessToken),term=config.terms.find(t=>t.academicYear==='2025-2026'&&t.semester===1);
  const browser=await chromium.launch({headless:true,...(process.env.HTBAM_BROWSER_EXECUTABLE?{executablePath:process.env.HTBAM_BROWSER_EXECUTABLE}:{})});
  const errors=[];let currentPage;
  try{
    for(const [name,viewport] of [['desktop',{width:1366,height:900}],['mobile',{width:390,height:844}]]){
      const context=await browser.newContext({viewport,isMobile:name==='mobile',hasTouch:name==='mobile'});
      await context.addInitScript(token=>sessionStorage.setItem('htbam_token',token),accessToken);
      await context.addInitScript(()=>{window.__importEvents=[];for(const type of ['scroll','focusin','pointerdown'])document.addEventListener(type,event=>{window.__importEvents.push({type,target:event.target.className||event.target.nodeName});window.__importEvents=window.__importEvents.slice(-12);},true);});
      const page=await context.newPage();currentPage=page;page.on('pageerror',error=>errors.push(`${name}: ${error.message}`));
      await page.goto(base+'/management?tab=class-sections');await page.getByRole('button',{name:'Nhập Excel',exact:true}).click();
      const dialog=page.getByRole('dialog',{name:'Nhập Excel lớp học phần'});await dialog.waitFor();
      check(await dialog.locator('.error-box').count()===0,name+' has no initial error');
      await dialog.locator('input[type=file]').setInputFiles(workbook);await page.getByText(/1 sheet/).waitFor();
      await dialog.getByRole('button',{name:'Tiếp tục',exact:true}).click();
      await dialog.getByText('Tiêu đề: dòng 7–8. Dữ liệu bắt đầu: dòng 9.',{exact:true}).waitFor();
      check(await dialog.locator('input[type=number]').count()===0,name+' hides manual header inputs');
      await dialog.getByRole('button',{name:'Chỉnh nhận diện thủ công',exact:true}).click();
      check(await dialog.getByLabel('Dòng bắt đầu tiêu đề',{exact:true}).inputValue()==='7',name+' manual header 7');
      check(await dialog.getByRole('combobox',{name:/Số dòng tiêu đề/}).locator('option:checked').innerText()==='Hai dòng',name+' manual header count 2');
      await dialog.getByLabel('Dòng bắt đầu tiêu đề',{exact:true}).fill('20');
      await dialog.getByText('Tiêu đề: dòng 7–8. Dữ liệu bắt đầu: dòng 9.',{exact:true}).waitFor({state:'hidden'});
      check(await dialog.getByRole('button',{name:'Tiếp tục ánh xạ cột',exact:true}).isDisabled(),name+' pending manual edit blocks next');
      check(await dialog.getByText('Tiêu đề: dòng 7–8. Dữ liệu bắt đầu: dòng 9.',{exact:true}).count()===0,name+' pending manual draft never contradicts the header message');
      await dialog.getByRole('button',{name:'Đóng chỉnh nhận diện thủ công',exact:true}).click();
      await page.screenshot({path:path.join(output,name+'-recognition.png')});
      await dialog.getByRole('button',{name:'Tiếp tục ánh xạ cột',exact:true}).click();
      await dialog.getByText('Đã ánh xạ T1–T26, cột L–AK',{exact:true}).waitFor();
      check(await dialog.getByRole('combobox',{name:/Tìm cột cho T\d+$/}).count()===0,name+' no individual week dropdowns');
      check(await dialog.locator('.import-quick-preview tbody tr').count()===5,name+' five quick rows');
      check(await dialog.evaluate(el=>Array.from(el.querySelectorAll('.modal-body,.import-mapping,.import-quick-preview')).filter(node=>['auto','scroll'].includes(getComputedStyle(node).overflowY)&&node.scrollHeight>node.clientHeight+1).length)===1,name+' only the main modal body scrolls vertically');
      check((await dialog.locator('.import-quick-preview').innerText()).includes('010110215301'),name+' quick preview preserves actual source code');
      await dialog.locator('.import-week-mapping summary').click();
      check((await dialog.locator('.import-week-columns').innerText()).includes('Tuần học / T2 — M'),name+' real T2 label');
      await page.screenshot({path:path.join(output,name+'-mapping.png')});
      const input=dialog.getByRole('combobox',{name:'Tìm cột cho Từ ngày',exact:true});
      await input.click();const panel=dialog.locator('.import-column-panel:visible');await panel.waitFor({timeout:4000});
      await input.fill('20/08');await page.waitForFunction(()=>document.querySelectorAll('.import-column-panel:not([hidden]) [role=option]').length===1);check((await panel.innerText()).includes('Thời gian học / Bắt đầu — K — 20/08/2025'),name+' searchable named sample');
      await input.fill('');await page.waitForFunction(()=>{const p=document.querySelector('.import-column-panel:not([hidden])');return p&&p.getBoundingClientRect().height>0&&p.querySelectorAll('[role=option]').length>5;});
      const panelBox=await panel.boundingBox(),footerBox=await dialog.locator('.import-footer').boundingBox(),dialogBox=await dialog.boundingBox();
      check(panelBox.height<=321&&panelBox.y>=0&&panelBox.y+panelBox.height<=footerBox.y+1,name+' dropdown <=320px and above footer');
      check(dialogBox.x>=-1&&dialogBox.y>=-1&&dialogBox.x+dialogBox.width<=viewport.width+1&&dialogBox.y+dialogBox.height<=viewport.height+1,name+' dialog fits viewport');
      const before=await dialog.locator('.modal-body').evaluate(el=>el.scrollTop);
      await dialog.locator('.import-mapping th').first().hover();await page.mouse.wheel(0,350);await page.waitForTimeout(100);
      check(await dialog.locator('.modal-body').evaluate(el=>el.scrollTop)===before,name+' dropdown locks modal background scroll');
      await page.screenshot({path:path.join(output,name+'-dropdown.png')});
      await input.press('Escape');await panel.waitFor({state:'hidden'});check(await panel.count()===0,name+' Escape closes dropdown only');check(await dialog.isVisible(),name+' modal stays open');
      await input.click();await input.fill('Kết thúc — AL');await page.waitForFunction(()=>document.querySelectorAll('.import-column-panel:not([hidden]) [role=option]').length===1);const used=dialog.locator('.import-column-panel:visible [role=option][aria-disabled=true]');
      check(await used.count()===1&&(await used.innerText()).includes('Đã dùng cho Đến ngày'),name+' used column disabled with reason');
      await input.press('Escape');await panel.waitFor({state:'hidden'});await input.click();await input.fill('Bắt đầu — K');await page.waitForFunction(()=>document.querySelectorAll('.import-column-panel:not([hidden]) [role=option]').length===1);await input.press('ArrowDown');await input.press('Enter');await panel.waitFor({state:'hidden'});
      check((await input.inputValue()).includes(' — K — '),name+' keyboard selects source column');
      await dialog.locator('.import-quick-preview').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,name+'-quick-preview.png')});
      await dialog.getByRole('combobox',{name:/Năm học \/ Học kỳ/}).selectOption({label:`${term.academicYear} · HK${term.semester}`});
      const policy=dialog.getByRole('combobox',{name:'Chính sách chuyên cần',exact:true});await policy.click();
      await page.locator('.search-select-panel:visible [role=option][aria-disabled=false]').first().click();
      await dialog.getByRole('button',{name:'Tiếp tục xem trước',exact:true}).click();
      await dialog.getByText(/10.190 dòng dữ liệu và 5.447 nhóm Mã LHP/).waitFor({timeout:60000});
      check(await dialog.locator('.import-group').count()<=100,name+' bounded preview groups');
      check(await dialog.getByRole('button',{name:'Tiếp tục xác nhận',exact:true}).isDisabled(),name+' real mandatory errors block confirm');
      await page.screenshot({path:path.join(output,name+'-preview.png')});
      await dialog.locator('.import-footer').getByRole('button',{name:'Đóng',exact:true}).click();await context.close();
    }
    check(errors.length===0,'no browser runtime errors: '+errors.join('; '));
    fs.writeFileSync(path.join(output,'report.json'),JSON.stringify({checks,viewports:['1366x900','390x844'],errors,screenshots:10},null,2));console.log('HUIT_BROWSER_OK '+JSON.stringify({checks,screenshots:10}));
  }catch(error){if(currentPage&&!currentPage.isClosed()){await currentPage.screenshot({path:path.join(output,'failure.png')});console.error('BROWSER_DIAGNOSTICS',await currentPage.evaluate(()=>window.__importEvents));}throw error;}finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
