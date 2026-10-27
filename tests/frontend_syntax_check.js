const fs=require('fs'), path=require('path');
const ts=require(path.resolve(__dirname,'..','apps','web','node_modules','typescript'));
const root=path.resolve(__dirname,'..','apps','web','src');
const files=[];
function walk(d){for(const n of fs.readdirSync(d)){const p=path.join(d,n),s=fs.statSync(p);if(s.isDirectory())walk(p);else if(/\.(ts|tsx)$/.test(n)&&!n.endsWith('.d.ts'))files.push(p)}}
walk(root); let bad=[];
for(const f of files){const source=fs.readFileSync(f,'utf8');const r=ts.transpileModule(source,{fileName:f,reportDiagnostics:true,compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ESNext,jsx:ts.JsxEmit.ReactJSX}});for(const d of r.diagnostics||[]){if(d.category===ts.DiagnosticCategory.Error){bad.push(`${path.relative(root,f)}: TS${d.code} ${ts.flattenDiagnosticMessageText(d.messageText,' ')}`)}}const actionHeaders=(source.match(/<th[^>]*>\s*Thao tác\s*<\/th>/g)||[]).length;if(actionHeaders){const actionMenus=(source.match(/<app-row-action-menu(?:\s|>)/g)||[]).length;if(actionMenus<actionHeaders)bad.push(`${path.relative(root,f)}: ${actionHeaders} cột Thao tác nhưng chỉ có ${actionMenus} RowActionMenuComponent`)}}
const ui=fs.readFileSync(path.join(root,'app','shared','ui.ts'),'utf8');
const styles=fs.readFileSync(path.join(root,'styles.scss'),'utf8');
for(const required of ['event.currentTarget','this.document.body.appendChild(menu)','aria-haspopup="menu"','role="menu"',"setAttribute('role', 'menuitem')",'resizeListener = () => this.queuePosition()','addEventListener(\'scroll\', this.scrollListener, true)','actionCaptureListener']){if(!ui.includes(required))bad.push(`shared/ui.ts: thiếu hành vi RowActionMenu bắt buộc: ${required}`)}
for(const required of ['.action-menu.action-menu-overlay','position: fixed','.action-menu button.danger','.action-menu button:disabled']){if(!styles.includes(required))bad.push(`styles.scss: thiếu style RowActionMenu bắt buộc: ${required}`)}
const management=fs.readFileSync(path.join(root,'app','features','management.component.ts'),'utf8');
if(management.includes('class="inline-actions"'))bad.push('management.component.ts: vẫn còn nhóm thao tác theo dòng hiển thị inline');
for(const required of ['reactivateCatalog','Kích hoạt lại','apiFieldErrors','editorFieldErrors']){if(!management.includes(required))bad.push(`management.component.ts: thiếu luồng ${required}`)}
const students=fs.readFileSync(path.join(root,'app','features','students.component.ts'),'utf8');
for(const field of ['studentCode','email','anonymousCode']){if(!students.includes(`studentFieldErrors()['${field}']`))bad.push(`students.component.ts: thiếu lỗi theo trường ${field}`)}
const policies=fs.readFileSync(path.join(root,'app','features','policies.component.ts'),'utf8');
for(const required of ['deactivatePolicy','reactivatePolicy','Kích hoạt lại']){if(!policies.includes(required))bad.push(`policies.component.ts: thiếu luồng ${required}`)}
const users=fs.readFileSync(path.join(root,'app','features','admin-users.component.ts'),'utf8');
for(const required of ["askStatus(user,'INACTIVE')",'Kích hoạt lại',"fieldErrors()['userName']", "fieldErrors()['email']"]){if(!users.includes(required))bad.push(`admin-users.component.ts: thiếu luồng ${required}`)}
for(const [file,raw] of [['account.component.ts','{{ me()!.status }}'],['search.component.ts','{{item.status}}'],['session-detail.component.ts','{{ dashboard().aiHealth }}'],['session-detail.component.ts','{{ dashboard().cameraHealth }}']]){const source=fs.readFileSync(path.join(root,'app','features',file),'utf8');if(source.includes(raw))bad.push(`${file}: còn hiển thị trạng thái thô ${raw}`)}
const videos=fs.readFileSync(path.join(root,'app','features','videos.component.ts'),'utf8');
for(const required of ['[src]="previewUrl()"','previewVideo.set(video)','previewLoading.set(true)','(loadedmetadata)="onMetadata($event)"','(error)="onVideoError()"']){if(!videos.includes(required))bad.push(`videos.component.ts: thiếu luồng phát video backend có trạng thái/lỗi: ${required}`)}
const reports=fs.readFileSync(path.join(root,'app','features','reports.component.ts'),'utf8');
for(const required of ['reportOptions(this.kind)','loadOptions()','Phạm vi được phép xem','không tự tạo dữ liệu báo cáo']){if(!reports.includes(required))bad.push(`reports.component.ts: thiếu danh sách báo cáo theo phạm vi backend: ${required}`)}
const guards=fs.readFileSync(path.join(root,'app','core','auth.guard.ts'),'utf8');
if(!guards.includes("reportGuard = permissionGuard('REPORT_VIEW')"))bad.push('auth.guard.ts: quyền route báo cáo không khớp REPORT_VIEW backend');
if(bad.length){console.error(bad.join('\n'));process.exit(1)}
console.log(`FRONTEND_TS_SYNTAX_AND_ROW_ACTIONS_OK (${files.length} files)`);
