// Behavioral unit tests for controls and wizard; these do not claim browser rendering coverage.
const assert = require('node:assert/strict');
const {test} = require('node:test');
const fs = require('node:fs');
const vm = require('node:vm');
const ts = require('../apps/web/node_modules/typescript');
const signal = value => {const fn = () => value; fn.set = next => value = next; return fn;};
function load(file, api = {}) {
  const core = {Component:()=>target=>target,Input:()=>()=>{},Output:()=>()=>{},ViewChild:()=>()=>{},HostListener:()=>()=>{},forwardRef:fn=>fn,ChangeDetectionStrategy:{OnPush:0},signal,computed:fn=>fn,inject:()=>api,EventEmitter:class{emit(){}}};
  const rx=require('../apps/web/node_modules/rxjs');
  // RxJS checks plain-object prototypes; normalize dictionaries crossing this VM boundary.
  const modules = {'@angular/core':core,'rxjs':{...rx,forkJoin:input=>rx.forkJoin(Array.isArray(input)?input:Object.fromEntries(Object.entries(input)))},'@angular/core/rxjs-interop':{takeUntilDestroyed:()=>source=>source},'../shared/ui':new Proxy({errorText:e=>String(e?.message??e),apiFieldErrors:()=>({}),statusText:v=>v},{get:(target,key)=>target[key]??class{}})};
  if(file.endsWith('management.component.ts'))modules['../shared/catalog-navigation']=load('apps/web/src/app/shared/catalog-navigation.ts');
  const source = fs.readFileSync(file,'utf8');
  const code = ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.CommonJS,experimentalDecorators:true}}).outputText;
  const context = {exports:{},require:name=>modules[name]??new Proxy({}, {get:()=>class{}}),crypto:require('node:crypto').webcrypto,Date,Intl,Set,File,URLSearchParams,console,setTimeout,clearTimeout,document:{createElement:()=>({})}};
  vm.runInNewContext(code,context); return context.exports;
}
test('Vietnamese date control validates leap years and emits ISO without locale-dependent display',()=>{
  const {parseVietnameseDate,DateInputComponent} = load('apps/web/src/app/shared/date-input.component.ts');
  assert.equal(parseVietnameseDate('17/08/2026'),'2026-08-17');
  assert.equal(parseVietnameseDate('29/02/2028'),'2028-02-29');
  assert.equal(parseVietnameseDate('29/02/2026'),null); assert.equal(parseVietnameseDate('08/17/2026'),null);
  const c = new DateInputComponent(); c.writeValue('2026-08-17'); assert.equal(c.display,'17/08/2026');
  let stored; c.registerOnChange(v=>stored=v); c.input({target:{value:'31/02/2026'}}); assert.equal(stored,''); assert.ok(c.validate({}));
  c.input({target:{value:'20/12/2026'}}); assert.equal(stored,'2026-12-20'); assert.equal(c.validate({}),null);
});
test('week chips stay within dates and all-weeks clears selected weeks',()=>{
  const {ClassSectionsComponent} = load('apps/web/src/app/features/class-sections.component.ts'); const c = new ClassSectionsComponent();
  c.config.set({terms:[{id:1,weekMappingConfirmed:true,weekOneMonday:'2026-08-17'}]}); c.form = {academicTermId:1};
  const line={startsOn:'2026-08-31',endsOn:'2026-09-13',selectedWeeks:[],specificWeeks:true};
  assert.deepEqual(Array.from(c.weeksForLine(line)),[3,4]);
  c.toggleWeek(line,4,true);c.toggleWeek(line,3,true);c.toggleWeek(line,3,true);assert.deepEqual(Array.from(line.selectedWeeks),[3,4]);
  line.specificWeeks=false;c.weeksModeChanged(line);assert.equal(line.selectedWeeks.length,0);
  c.config.set({terms:[{id:1,weekMappingConfirmed:false,weekOneMonday:'2026-08-17'}]}); assert.equal(c.weekAnchor(),null); assert.equal(c.weeksForLine(line).length,0);
});
test('preview explains missing configuration and refuses specific-weeks with empty selection',()=>{
  const {ClassSectionsComponent} = load('apps/web/src/app/features/class-sections.component.ts'); const c=new ClassSectionsComponent();
  c.rangeError.set('Thiếu ngày kết thúc học kỳ');c.previewChanges();assert.match(c.editorError(),/ngày kết thúc/);
  c.range.set({startsOn:'2026-08-17',endsOn:'2026-12-20'});c.form={schedules:[{specificWeeks:true,selectedWeeks:[]}]};c.previewChanges();assert.match(c.editorError(),/ít nhất một tuần/);
});
test('wizard rejects invalid files and confirmation uses saved preview token once while busy',()=>{
  const requests=[];const api={confirmSectionImport:token=>({subscribe:callback=>requests.push({token,callback})})};
  const {ClassSectionImportComponent}=load('apps/web/src/app/features/class-section-import.component.ts',api);const c=new ClassSectionImportComponent();
  c.selectFile({target:{files:[new File(['bad'],'bad.txt')]}});assert.match(c.error(),/xls/);assert.equal(c.file,null);
  c.preview.set({token:'server-snapshot-1',errorRows:0});c.step.set(5);c.confirmImport();assert.equal(requests.length,0);
  c.confirmed=true;c.confirmImport();c.confirmImport();assert.equal(requests.length,1);assert.equal(requests[0].token,'server-snapshot-1');
  requests[0].callback.next({createdClasses:1});assert.equal(c.result().createdClasses,1);assert.equal(c.busy(),false);
  c.invalidate();assert.equal(c.confirmed,false);assert.equal(c.preview(),null);
});

test('wizard starts without an error and uses server pagination instead of rendering the full workbook',()=>{
  const requests=[];const api={sectionImportPage:(...args)=>({subscribe:callback=>requests.push({args,callback})})};
  const {ClassSectionImportComponent}=load('apps/web/src/app/features/class-section-import.component.ts',api);const c=new ClassSectionImportComponent();
  assert.equal(c.error(),'');assert.equal(c.step(),1);assert.equal(c.steps.length,6);
  c.preview.set({token:'large-file',page:1,groups:[],dataRows:10190,totalGroups:5447});c.pageSize=100;c.query='0012';c.statusFilter='ERROR';c.loadPage(2);
  assert.deepEqual(requests[0].args,['large-file',2,100,'0012','ERROR']);assert.equal(c.busy(),true);
  requests[0].callback.next({token:'large-file',page:2,groups:[{key:'row',rows:Array.from({length:100})}]});assert.equal(c.preview().page,2);assert.equal(c.preview().groups[0].rows.length,100);
});
test('mandatory errors or changed mappings prevent confirmation and require a fresh preview',()=>{
  let writes=0;const api={confirmSectionImport:()=>{writes++;throw Error('must not write');}};
  const {ClassSectionImportComponent}=load('apps/web/src/app/features/class-section-import.component.ts',api);const c=new ClassSectionImportComponent();
  c.step.set(5);c.confirmed=true;c.preview.set({token:'blocked',errorRows:1});c.confirmImport();assert.equal(writes,0);
  c.preview.set({token:'old-mapping',errorRows:0});c.selectCourse('00123',91);assert.equal(c.confirmed,false);assert.equal(c.dirtyMappings,true);c.confirmed=true;c.confirmImport();assert.equal(writes,0);
});

test('creating a faculty requires its first department and an existing head before submission',()=>{
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts');const c=new ManagementComponent();
  c.tab.set('faculties');c.editingId.set(null);c.form={name:'New faculty'};
  assert.match(c.validateCatalog(),/bộ môn đầu tiên/);
  c.form.initialDepartmentName='First department';assert.match(c.validateCatalog(),/Trưởng khoa/);
  c.form.initialHeadTeacherId=42;assert.equal(c.validateCatalog(),null);
  c.editingId.set(1);c.form={name:'Existing faculty'};assert.equal(c.validateCatalog(),null);
});

function huitInspection(){
  return {name:'Sheet1',format:'HUIT',headerRow:7,headerStartRow:7,headerRowCount:2,headerEndRow:8,dataStartRow:9,maxHeaderScanRows:20,
    columns:[{index:1,letter:'B',header:'Mã LHP',sample:'000123'},{index:2,letter:'C',header:'Tên môn học',sample:'Môn thử'},
      {index:10,letter:'K',header:'Thời gian học / Bắt đầu',sample:'20/08/2025'},...Array.from({length:26},(_,i)=>({index:i+11,letter:i===0?'L':i===25?'AK':'week'+i,header:'Tuần học / T'+(i+1),sample:i===0?'x':''})),
      {index:37,letter:'AL',header:'Thời gian học / Kết thúc',sample:'22/10/2025'}],
    mappings:[{field:'source',label:'Mã LHP',columnIndex:1,required:true},{field:'courseName',label:'Tên môn học',columnIndex:2},
      {field:'from',label:'Từ ngày',columnIndex:10,required:true},{field:'to',label:'Đến ngày',columnIndex:37,required:true},{field:'weeks',label:'Các tuần có lịch',columnIndex:null},
      ...Array.from({length:26},(_,i)=>({field:'week'+(i+1),label:'T'+(i+1),columnIndex:11+i}))],
    sampleRows:[{row:9,cells:Object.assign(Array(41).fill(''),{1:'000123',2:'Môn thử',10:'20/08/2025',11:'x',13:'X',15:'true',37:'22/10/2025'})}]};
}
test('HUIT header stays at 7–8 and data 9 while scan limit and manual drafts are separate',()=>{
  let request;const api={inspectSectionImport:(...args)=>({subscribe:callback=>request={args,callback}})};
  const {ClassSectionImportComponent}=load('apps/web/src/app/features/class-section-import.component.ts',api);const c=new ClassSectionImportComponent();
  c.sheetDetails=[huitInspection()];c.sheet='Sheet1';c.chooseSheet();assert.equal(c.manualRecognition,false);assert.equal(c.headerStartRow,7);assert.equal(c.headerEndRow(),8);assert.equal(c.dataStartRow(),9);
  c.toggleManualRecognition();assert.equal(c.draftHeaderStartRow,7);assert.equal(c.draftHeaderRowCount,2);c.draftHeaderStartRow=20;
  assert.equal(c.headerStartRow,7);assert.equal(c.headerDraftChanged(),true);c.file=new File(['test'],'test.xls');c.step.set(2);c.inspectSheet(false);
  assert.equal(request.args[2],20);request.callback.next({...huitInspection(),headerRow:20,headerStartRow:20,sampleRows:[]});
  assert.equal(c.headerStartRow,20);assert.equal(c.headerEndRow(),21);assert.equal(c.dataStartRow(),22);assert.equal(c.headerDraftChanged(),false);assert.equal(c.step(),2);assert.equal(c.quickRows().length,0);
});
test('HUIT uses one week group, named samples and a quick preview that follows current mapping',()=>{
  const {ClassSectionImportComponent}=load('apps/web/src/app/features/class-section-import.component.ts');const c=new ClassSectionImportComponent();c.sheetDetails=[huitInspection()];c.sheet='Sheet1';c.chooseSheet();
  assert.equal(c.weekMappings().length,26);assert.equal(c.singleMappings().some(m=>m.field==='weeks'||/^week\d+$/.test(m.field)),false);
  assert.equal(c.weekMappingSummary(),'Đã ánh xạ T1–T26, cột L–AK');assert.equal(c.columnLabel(10),'Thời gian học / Bắt đầu — K — 20/08/2025');
  assert.equal(c.quickRows()[0].weeks,'T1,T3,T5');assert.equal(c.quickRows()[0].source,'000123');
  const from=c.mappings.find(m=>m.field==='from');assert.equal(c.mappingChoices(from).find(x=>x.index===37).usedBy,'Đến ngày');
  c.mapColumn(from,37);assert.equal(from.columnIndex,10);c.mapColumn(from,null);assert.equal(c.quickRows()[0].from,'');assert.equal(c.mappingComplete(),false);
});
test('column picker placement is bounded on desktop/mobile and stays above sticky footer',()=>{
  const {columnPanelPlacement}=load('apps/web/src/app/shared/import-column-select.component.ts');
  const up=columnPanelPlacement({top:460,bottom:500,left:500,width:400},100,550,1000);
  assert.equal(up.up,true);assert.equal(up.height,320);assert.ok(up.top+up.height<=460);assert.ok(up.top>=100);
  const mobile=columnPanelPlacement({top:180,bottom:220,left:8,width:300},80,550,360);
  assert.ok(mobile.height<=320);assert.ok(mobile.top+mobile.height<=550);assert.ok(mobile.left+mobile.width<=352);
});
test('column picker searches samples, blocks used columns, locks background scroll and releases it on close',()=>{
  const listeners=new Map();let prevented=0,emitted;
  const panel={contains:target=>target==='panel',style:{},parentElement:null};const dialog={appendChild:node=>node.parentElement=dialog};const panelHost={appendChild:node=>node.parentElement=panelHost};
  const env={nativeElement:{closest:()=>dialog,contains:()=>false},addEventListener:(type,fn)=>listeners.set(type,fn),removeEventListener:(type)=>listeners.delete(type),getElementById:()=>null};
  const {ImportColumnSelectComponent}=load('apps/web/src/app/shared/import-column-select.component.ts',env);const c=new ImportColumnSelectComponent();
  c.panel={nativeElement:panel};c.panelHost={nativeElement:panelHost};c.columns=[{index:10,label:'Bắt đầu — K — 20/08/2025'},{index:37,label:'Kết thúc — AL — 22/10/2025',usedBy:'Đến ngày'}];
  c.valueChange.emit=v=>emitted=v;c.show();assert.equal(c.open(),true);assert.equal(panel.parentElement,dialog);
  listeners.get('scroll')({target:'modal-body'});assert.equal(c.open(),true,'native focus scrolling must not immediately dismiss the new list');
  listeners.get('wheel')({target:'outside',preventDefault:()=>prevented++});listeners.get('touchmove')({target:'panel',preventDefault:()=>prevented++});assert.equal(prevented,1);
  c.search({target:{value:'20/08'}});assert.equal(c.choices().length,1);assert.equal(c.choices()[0].index,10);
  c.choose(c.columns[1]);assert.equal(c.open(),true);assert.equal(emitted,undefined);c.choose(c.columns[0]);assert.equal(emitted,10);assert.equal(c.open(),false);assert.equal(listeners.size,0);assert.equal(panel.parentElement,panelHost);
  c.show();c.keydown({key:'Escape',preventDefault(){},stopPropagation(){}});assert.equal(c.open(),false);assert.equal(listeners.size,0);
});
test('class sections is embedded in catalog tabs without separate header or sidebar links',()=>{
  const source=fs.readFileSync('apps/web/src/app/layout/shell.component.ts','utf8');
  const work=source.slice(source.indexOf('readonly workLinks'),source.indexOf('readonly adminLinks'));
  assert.equal(work.includes('/class-sections'),false);assert.equal((source.match(/routerLink="\/class-sections"/g)||[]).length,0);
  assert.equal((source.match(/routerLink="\/management"/g)||[]).length,1);
  const management=fs.readFileSync('apps/web/src/app/features/management.component.ts','utf8');
  assert.ok(management.includes('<app-class-sections />'));assert.ok(management.includes('role="tablist"'));
  assert.equal(management.includes('routerLink="/class-sections"'),false);
  const editor=fs.readFileSync('apps/web/src/app/features/class-sections.component.ts','utf8');
  assert.equal(editor.includes('Mã nguồn Excel (nếu có)'),false);assert.equal(editor.includes('Mốc tuần theo nhóm<select'),false);
  assert.ok(editor.indexOf('Nhập Excel')<editor.indexOf('</app-catalog-section-header>'));
});

test('academic configuration route and sidebar follow ACADEMIC_CONFIGURE instead of ADMIN role alone',async()=>{
  const {of,firstValueFrom}=require('../apps/web/node_modules/rxjs');
  const routes=fs.readFileSync('apps/web/src/app/app.routes.ts','utf8');
  assert.match(routes,/path:\s*'academic-configuration'[\s\S]*?canActivate:\s*\[academicConfigurationGuard\]/);
  const shell=fs.readFileSync('apps/web/src/app/layout/shell.component.ts','utf8');
  assert.ok(shell.includes("permission: 'ACADEMIC_CONFIGURE'"));
  assert.ok(shell.includes('@if (canSeeAdministrationLinks())'));
  const makeGuard=permissions=>load('apps/web/src/app/core/auth.guard.ts',{
    isLoggedIn:()=>true,ensureProfile:()=>of({}),hasPermission:p=>permissions.includes(p),isAdmin:()=>false,
    createUrlTree:(path,options)=>({path,options}),clear:()=>{}
  });
  assert.equal(await firstValueFrom(makeGuard(['ACADEMIC_CONFIGURE']).academicConfigurationGuard()),true);
  const denied=await firstValueFrom(makeGuard([]).academicConfigurationGuard());
  assert.equal(denied.path[0],'/system');
});

test('browser camera is flushed and released before an idempotent stop request is sent',async()=>{
  const {of}=require('../apps/web/node_modules/rxjs');const order=[];let stopped=0;
  const api={
    snapshot:{paramMap:{get:()=> '30025'}},
    sessionAction:()=>{order.push('api-stop');return of({status:'FINALIZING'});},
    token:()=>'',
  };
  const {SessionDetailComponent}=load('apps/web/src/app/features/session-detail.component.ts',api);
  const component=new SessionDetailComponent();
  component.session.set({id:30025,status:'RUNNING',cameraMode:'BROWSER'});
  component.load=()=>{};
  component.browserUploadChain=Promise.resolve().then(()=>order.push('chunks-flushed'));
  component.browserStream={active:true,getTracks:()=>[{stop:()=>{stopped++;order.push('camera-released');}}]};
  component.browserPreview={nativeElement:{srcObject:{}}};
  component.requestAction('stop');
  await component.confirmAction();
  assert.deepEqual(order,['chunks-flushed','camera-released','api-stop']);
  assert.equal(stopped,1);assert.equal(component.browserStream,null);assert.equal(component.browserCameraState(),'IDLE');
  component.acting.set(true);component.requestAction('stop');assert.equal(component.pendingAction(),null,'a second stop cannot be queued while acting');
});

test('session outputs and empty states explain persisted zero data instead of rendering blank',()=>{
  const detail=fs.readFileSync('apps/web/src/app/features/session-detail.component.ts','utf8');
  const reports=fs.readFileSync('apps/web/src/app/features/reports.component.ts','utf8');
  const alerts=fs.readFileSync('apps/web/src/app/features/alerts.component.ts','utf8');
  const videos=fs.readFileSync('apps/web/src/app/features/videos.component.ts','utf8');
  assert.ok(detail.includes('Đầu ra đã lưu'));
  assert.ok(detail.includes('Toàn bộ buổi học'));
  assert.ok(reports.includes('0 sự kiện — không ghi nhận sự kiện hành vi'));
  assert.ok(reports.includes('0 cảnh báo — không có rule/model nào phát hiện cảnh báo'));
  assert.ok(alerts.includes('value="ACK"'));
  assert.ok(alerts.includes("alert.source === 'RULE_ENGINE'"));
  assert.ok(videos.includes('hệ thống không tự tạo video khi chunk nguồn không còn'));
});

function access(permissions,admin=false,roles=['LECTURER']){return{hasPermission:p=>permissions.includes(p),isAdmin:()=>admin,user:()=>({roles})};}
test('teacher preview uses the department sequence, blocks legacy departments and ignores stale responses',()=>{
  const requests=[];const env={...access(['TEACHER_CREATE']),nextTeacherCode:id=>({subscribe:callbacks=>{const request={id,callbacks,closed:false};requests.push(request);return{unsubscribe(){request.closed=true;}};}})};
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();c.tab.set('teachers');c.form={fullName:'Teacher',email:'teacher@example.invalid',userId:91};c.refs.departments=[{id:1,code:'BM-CNPM'},{id:2,code:'bm0001'},{id:3,code:'BM-ATTT'}];
  c.teacherDepartmentChanged(1);assert.equal(c.form.userId,null);c.teacherDepartmentChanged(3);assert.equal(requests[0].closed,true);requests[0].callbacks.next({code:'GV-CNPM-0003'});assert.equal(c.codePreview(),'');requests[1].callbacks.next({code:'GV-ATTT-0001'});assert.equal(c.codePreview(),'GV-ATTT-0001');
  c.teacherDepartmentChanged(2);assert.match(c.departmentCodeError(),/trước khi thêm giảng viên/);assert.match(c.validateCatalog(),/mã hợp lệ/);assert.equal(requests.length,2);
});
test('teacher save omits all client code fields and the new row immediately uses the backend teacher code',()=>{
  let payload,callback;const env={...access(['TEACHER_CREATE']),saveCatalog:(resource,id,body)=>{payload=body;return{subscribe:c=>callback=c};}};
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();c.tab.set('teachers');c.loadAll=()=>{};c.form={code:'FORGED',teacherCode:'GV-HEAD',fullName:'Teacher',email:'teacher@example.invalid',departmentId:1};c.managementSearch='old';c.editorOpen.set(true);c.saveCatalog();
  assert.equal('code' in payload,false);assert.equal('teacherCode' in payload,false);callback.next({id:44,teacherCode:'GV-CNPM-0003',fullName:'Teacher'});assert.equal(c.editorOpen(),false);assert.equal(c.managementSearch,'');assert.equal(c.visibleData()[0].teacherCode,'GV-CNPM-0003');
});
test('administrator never gets teacher creation even with a stale permission and editing keeps the existing code',()=>{
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',access(['TEACHER_CREATE','DEPARTMENT_DATA_MANAGE'],true,['ADMIN']));const c=new ManagementComponent();c.tab.set('teachers');assert.equal(c.canCreateTab(),false);
  c.editingId.set(7);c.originalTeacherDepartmentId=1;c.form={teacherCode:'GV-CNPM-0001',departmentId:1};c.teacherDepartmentChanged(2);assert.equal(c.form.teacherCode,'GV-CNPM-0001');assert.equal(c.form.departmentId,2);assert.equal(c.originalTeacherDepartmentId,1);assert.equal(c.codePreview(),'');
});
test('catalog actions are inside shared section headers below tabs and descriptions follow the selected tab',()=>{
  const source=fs.readFileSync('apps/web/src/app/features/management.component.ts','utf8');const top=source.slice(source.indexOf('<app-page-title'),source.indexOf('class="tabs catalog-tabs"'));assert.equal(top.includes('(click)="openEditor()"'),false);
  assert.ok(source.indexOf('<app-catalog-section-header')>source.indexOf('role="tabpanel"'));assert.equal((source.match(/<app-catalog-section-header/g)||[]).length,1);
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',access(['TEACHER_CREATE']));const c=new ManagementComponent();c.tab.set('teachers');assert.equal(c.sectionDescription(),'Quản lý hồ sơ giảng viên trong khoa.');c.tab.set('courses');assert.match(c.sectionDescription(),/số tín chỉ/);
  const {CATALOG_TABS}=load('apps/web/src/app/shared/catalog-navigation.ts');assert.equal(CATALOG_TABS.filter(x=>x.description).length,8);
});
test('catalog tabs do not fetch unauthorized unrelated student data or show a spurious permission error',()=>{
  const {of}=require('../apps/web/node_modules/rxjs');let studentRequests=0;const env={...access(['CATALOG_VIEW'],true,['ADMIN']),students:()=>{studentRequests++;throw Error('unrelated request');}};
  for(const key of ['faculties','departments','studentClasses','courses','teachers','rooms','cameras'])env[key]=()=>of([]);
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();c.tab.set('rooms');c.loadAll();assert.equal(studentRequests,0);assert.equal(c.refErrorList().length,0);assert.equal(c.error(),'');
});
test('course catalog preview follows department, ignores stale responses and blocks legacy codes',()=>{
  const requests=[];const env={...access(['CATALOG_VIEW','DEPARTMENT_DATA_MANAGE']),nextCourseCode:id=>({subscribe:callbacks=>{const request={id,callbacks,closed:false};requests.push(request);return{unsubscribe(){request.closed=true;}};}})};
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();c.tab.set('courses');c.form={name:'Course',credits:3};
  c.refs.departments=[{id:1,code:'BM-CNDL'},{id:2,code:'bm0001'},{id:3,code:'BM-ATTT'}];
  c.courseDepartmentChanged(1);assert.equal(c.codePreviewLoading(),true);c.courseDepartmentChanged(3);assert.equal(requests[0].closed,true);
  requests[0].callbacks.next({code:'MH-CNDL-001'});assert.equal(c.codePreview(),'');requests[1].callbacks.next({code:'MH-ATTT-001'});assert.equal(c.codePreview(),'MH-ATTT-001');
  c.courseDepartmentChanged(2);assert.match(c.departmentCodeError(),/chuẩn hóa mã bộ môn/);assert.match(c.validateCatalog(),/chuẩn hóa mã bộ môn/);assert.equal(requests.length,2);
});
test('catalog save strips client codes, clears hiding filter and immediately uses backend response',()=>{
  let sent,callback;const env={...access(['CATALOG_VIEW','DEPARTMENT_DATA_MANAGE']),saveCatalog:(...args)=>{sent=args;return{subscribe:c=>callback=c};}};
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();c.canCreateTab=()=>true;c.loadAll=()=>{};c.tab.set('courses');c.editorOpen.set(true);c.managementSearch='old filter';c.form={code:'FORGED',name:'Môn mới',departmentId:1,credits:3,isActive:true};
  c.saveCatalog();assert.equal('code' in sent[2],false);callback.next({id:77,code:'MH-CNDL-001',name:'Môn mới',departmentId:1,credits:3});
  assert.equal(c.editorOpen(),false);assert.equal(c.managementSearch,'');assert.equal(c.visibleData()[0].code,'MH-CNDL-001');
});
test('catalog order includes class sections between courses and teachers and preserves role scope',()=>{
  const {visibleCatalogTabs,resolveCatalogTab}=load('apps/web/src/app/shared/catalog-navigation.ts');
  const manager=visibleCatalogTabs(access(['CATALOG_VIEW','CLASS_SECTION_VIEW','DEPARTMENT_DATA_MANAGE']));
  assert.deepEqual(Array.from(manager,x=>x.key),['faculties','departments','student-classes','courses','class-sections','teachers','rooms','cameras']);
  assert.equal(resolveCatalogTab(manager,null),'faculties');assert.equal(resolveCatalogTab(manager,'class-sections'),'class-sections');
  const viewer=visibleCatalogTabs(access(['CLASS_SECTION_VIEW']));assert.deepEqual(Array.from(viewer,x=>x.key),['class-sections']);
  assert.equal(resolveCatalogTab(viewer,'courses'),'class-sections');
  assert.deepEqual(Array.from(visibleCatalogTabs(access(['CATALOG_VIEW'],true)),x=>x.key),['faculties','rooms','cameras']);
  assert.deepEqual(Array.from(visibleCatalogTabs(access(['CATALOG_VIEW'],false,['TECH_AI'])),x=>x.key),['rooms','cameras']);
});
test('breadcrumb follows the canonical query tab and old deep links',()=>{
  const {catalogBreadcrumb}=load('apps/web/src/app/shared/catalog-navigation.ts');
  for(const url of ['/management?tab=class-sections','/management?other=1&tab=class-sections#detail','/class-sections'])assert.equal(catalogBreadcrumb(url),'Danh mục dữ liệu / Lớp học phần');
  assert.equal(catalogBreadcrumb('/management'),'Danh mục dữ liệu');assert.equal(catalogBreadcrumb('/sessions'),null);
});
test('legacy class route redirects to the catalog tab and enforces view permissions',async()=>{
  const {of,firstValueFrom}=require('../apps/web/node_modules/rxjs');
  function guard(auth){const env={...auth,isLoggedIn:()=>true,ensureProfile:()=>of({}),createUrlTree:(path,options)=>({path,options})};return load('apps/web/src/app/core/auth.guard.ts',env);}
  const route={queryParams:{q:'old-link'},fragment:'detail',queryParamMap:new Map([['tab','class-sections']])};
  const viewer=guard(access(['CLASS_SECTION_VIEW']));
  const redirected=await firstValueFrom(viewer.classSectionsGuard(route));
  assert.deepEqual(Array.from(redirected.path),['/management']);assert.equal(redirected.options.queryParams.tab,'class-sections');assert.equal(redirected.options.queryParams.q,'old-link');
  assert.equal(await firstValueFrom(viewer.catalogGuard(route)),true);
  const forbidden=await firstValueFrom(guard(access(['CATALOG_VIEW'],true)).catalogGuard(route));assert.equal(forbidden.path[0],'/management');assert.equal(forbidden.options,undefined);
});
test('tab changes update query state and browser history selects embedded content',()=>{
  const {BehaviorSubject}=require('../apps/web/node_modules/rxjs');const params=new BehaviorSubject(new Map());const visits=[];
  const env={...access(['CATALOG_VIEW','CLASS_SECTION_VIEW','DEPARTMENT_DATA_MANAGE']),queryParamMap:params,navigate:(paths,options)=>{visits.push({paths,options});params.next(new Map([['tab',options.queryParams.tab]]));return Promise.resolve(true);}};
  const {ManagementComponent}=load('apps/web/src/app/features/management.component.ts',env);const c=new ManagementComponent();let loads=0;c.loadAll=()=>loads++;
  c.ngOnInit();assert.equal(c.tab(),'faculties');assert.equal(loads,1);
  c.selectTab('class-sections');assert.equal(c.tab(),'class-sections');assert.equal(visits[0].options.queryParams.tab,'class-sections');assert.equal(loads,1);
  c.refs.courses=[{id:77,code:'MH-CNPM-001'}];c.refs.departments=[{id:88,code:'BM-CNPM'}];c.data.set(c.refs.departments);
  params.next(new Map([['tab','courses']]));assert.equal(c.tab(),'courses');assert.equal(loads,2);assert.equal(c.data()[0].code,'MH-CNPM-001','tab changes replace stale rows immediately while a refresh is pending');
  c.selectTab('unknown');assert.equal(visits.length,1);
});
