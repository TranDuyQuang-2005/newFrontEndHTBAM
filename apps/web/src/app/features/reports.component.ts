import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { PageTitleComponent, errorText } from '../shared/ui';
import { formatReportValue, isReportColumnVisible, reportColumnLabel } from '../shared/report-format';

@Component({standalone:true,imports:[FormsModule,PageTitleComponent],changeDetection:ChangeDetectionStrategy.OnPush,template:`
  <app-page-title title="Báo cáo" subtitle="Phân tích hành vi, chuyên cần và chất lượng quan sát từ dữ liệu thật.">
    @if(data()){<button type="button" class="secondary" [disabled]="!!exporting()" (click)="export('excel')">{{exporting()==='excel'?'Đang xuất Excel…':'Xuất Excel'}}</button><button type="button" [disabled]="!!exporting()" (click)="export('pdf')">{{exporting()==='pdf'?'Đang xuất PDF…':'Xuất PDF'}}</button>}
  </app-page-title>
  <div class="filter-bar"><label>Loại báo cáo<select [(ngModel)]="kind" (ngModelChange)="changeKind()"><option value="sessions">Buổi học</option><option value="students">Sinh viên</option><option value="class-sections">Lớp học phần</option></select></label><label class="grow">Phạm vi được phép xem<select [(ngModel)]="id" (ngModelChange)="changeReport()"><option [ngValue]="null">{{loadingOptions()?'Đang tải phạm vi…':'Chọn đối tượng'}}</option>@for(option of options();track option.id){<option [ngValue]="option.id">{{option.label}}{{option.status?' · '+option.status:''}}</option>}</select></label><button type="button" [disabled]="!id||loading()||loadingOptions()" (click)="load()">{{loading()?'Đang tải…':'Xem báo cáo'}}</button></div>
  @if(error()){<div class="error-box">{{error()}}</div>}
  @if(exportError()){<div class="error-box" role="alert">{{exportError()}}</div>}
  @if(data()){
    <div class="stats stats-four">
      @for(metric of summaryEntries();track metric.key){<section class="stat-card"><span>{{metric.label}}</span><strong>{{metric.value}}</strong><small>Dữ liệu tổng hợp</small></section>}
    </div>
    @if(attendanceReason()){<div class="notice" role="status"><strong>Điểm chuyên cần:</strong> {{attendanceReason()}}</div>}
    @if(outputEntries().length){
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Trạng thái đầu ra</h3><p>Mỗi loại dữ liệu được đối chiếu theo đúng buổi học</p></div></div>
        <div class="service-grid">@for(item of outputEntries();track item.key){<div class="service-card"><span class="status-dot" [style.background]="item.status === 'AVAILABLE' ? '#16a34a' : '#94a3b8'"></span><div><strong>{{item.label}} · {{item.count}}</strong><small>{{item.message}}</small></div></div>}</div>
      </section>
    }
    <div class="dashboard-grid">
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Phân tích hành vi</h3><p>Tỷ lệ theo phạm vi báo cáo</p></div></div>
        @for(metric of behaviorEntries();track metric.label){<div class="metric-row"><span>{{metric.label}}</span><div class="progress-track"><div class="progress-value" [style.width.%]="metric.value" [style.background]="metric.color"></div></div><strong>{{metric.value}}%</strong></div>}
        @empty{<div class="empty-state" style="min-height:150px"><div><strong>Chưa có tỷ lệ hành vi</strong><span>Chưa có đủ dữ liệu tổng hợp cho phạm vi này.</span></div></div>}
      </section>
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Nhận xét tự động</h3><p>{{kind==='students'?'Theo buổi gần nhất có tổng hợp':'Từ chỉ số tổng hợp đã lưu'}}</p></div><span class="badge purple">Tự động</span></div>
        @if(autoComment()){<p style="line-height:1.7;color:#475569">{{autoComment()}}</p>}@else{<div class="empty-state" style="min-height:150px"><div><strong>Chưa có nhận xét</strong><span>Nhận xét tự động chưa được tạo cho báo cáo này.</span></div></div>}
        @if(autoCommentInfo();as info){<p class="muted"><strong>Nguồn:</strong> {{providerLabel(info)}}</p>@if(info.generatedAt){<p class="muted"><strong>Thời điểm tạo:</strong> {{formatGeneratedAt(info.generatedAt)}}</p>}@if(info.isFallback){<div class="notice" role="status"><strong>Nhận xét này được tạo theo quy tắc nội bộ.</strong> {{fallbackMessage(info.errorCode)}}</div>}}
        <button type="button" [disabled]="generatingComment()||!canGenerateComment()" (click)="generateComment()">{{generatingComment()?'Đang tạo nhận xét…':autoComment()?'Tạo lại nhận xét':'Tạo nhận xét tự động'}}</button>
        @if(!canGenerateComment()&&commentUnavailableReason()){<p class="muted" role="status">Tính năng nhận xét tự động chưa sẵn sàng cho phạm vi này.</p>}
        @if(commentError()){<div class="error-box" role="alert">{{commentError()}}</div>}
      </section>
    </div>
    @for(section of detailSections();track section.key){
      <section class="card table-card"><div class="card-header"><div><h3>{{section.label}}</h3><p>{{section.rows.length}} bản ghi</p></div></div><div class="table-wrap"><table><thead><tr>@for(column of columnsFor(section.rows);track column){<th>{{columnLabel(column)}}</th>}</tr></thead><tbody>
        @for(row of section.rows;track $index){<tr>@for(column of columnsFor(section.rows);track column){<td>{{formatValue(row[column],column)}}</td>}</tr>}@empty{<tr><td [attr.colspan]="columnsFor(section.rows).length||1" class="empty">{{section.empty}}</td></tr>}
      </tbody></table></div></section>
    }
  }@else{<section class="card"><div class="empty-state"><div><strong>{{options().length?'Chọn phạm vi để xem báo cáo':'Không có báo cáo trong phạm vi được cấp'}}</strong><span>{{options().length?'Danh sách chỉ gồm dữ liệu backend đã kiểm tra theo vai trò, khoa, bộ môn, phân công giảng dạy và dạy thay.':'Không tìm thấy lớp/buổi phù hợp; hệ thống không tự tạo dữ liệu báo cáo.'}}</span></div></div></section>}
`})
export class ReportsComponent implements OnInit{
  private readonly api=inject(ApiService);kind:'sessions'|'students'|'class-sections'='sessions';id:number|null=null;readonly data=signal<any>(null);readonly error=signal('');readonly exportError=signal('');readonly commentError=signal('');readonly loading=signal(false);readonly loadingOptions=signal(false);readonly exporting=signal<'pdf'|'excel'|null>(null);readonly generatingComment=signal(false);readonly generationResult=signal<any>(null);readonly options=signal<Array<{id:number;label:string;status?:string}>>([]);
  ngOnInit(){this.loadOptions()}
  changeKind(){this.id=null;this.data.set(null);this.generationResult.set(null);this.commentError.set('');this.exportError.set('');this.loadOptions()}
  changeReport(){this.data.set(null);this.generationResult.set(null);this.commentError.set('');this.exportError.set('')}
  loadOptions(){this.loadingOptions.set(true);this.error.set('');this.api.reportOptions(this.kind).subscribe({next:(items)=>{this.options.set(items);this.loadingOptions.set(false);if(items.length){this.id=items[0].id;this.load()}},error:(error)=>{this.options.set([]);this.error.set(errorText(error));this.loadingOptions.set(false)}})}
  load(){if(!this.id)return;this.loading.set(true);this.error.set('');this.api.report(this.kind,this.id).subscribe({next:(value)=>{this.data.set(value);this.loading.set(false)},error:(error)=>{this.error.set(errorText(error));this.loading.set(false)}})}
  summaryEntries(){const source=this.data()?.summary??{};const preferred=this.kind==='sessions'?['observedStudentCount','focusedRatio','averageAttendanceScorePercent','alertCount','observedSeconds','cameraAiQuality']:this.kind==='students'?['sessionCount','averageFocused','averageAttendanceScorePercent','totalAlerts','totalObservedSeconds','behaviorEvents']:['sessionCount','summarizedSessionCount','focused','averageAttendanceScorePercent','alerts','behaviorEvents'];return preferred.filter(key=>source[key]!=null).map(key=>({key,label:this.columnLabel(key),value:this.formatValue(source[key],key)}))}
  attendanceReason():string{return String(this.data()?.summary?.attendanceScoreReason??'')}
  outputEntries(){const source=this.data()?.outputs??{};const labels:Record<string,string>={video:'Video',behaviorEvents:'Sự kiện hành vi',alerts:'Cảnh báo',identities:'Dữ liệu nhận diện',attendance:'Chuyên cần',summaries:'Báo cáo tổng hợp'};return Object.entries(source).map(([key,value]:[string,any])=>{const status=value?.status??'EMPTY';const count=Number(value?.count??0);return{key,label:labels[key]??'Dữ liệu báo cáo',status,count,message:status==='AVAILABLE'?`${count} bản ghi đã sẵn sàng`:'Chưa có dữ liệu'}})}
  behaviorEntries(){const source=this.data()?.summary??{};const map=[['focusedRatio','Tập trung','#16a34a'],['averageFocused','Tập trung','#16a34a'],['focused','Tập trung','#16a34a'],['distractedRatio','Mất tập trung','#f59e0b'],['averageDistracted','Mất tập trung','#f59e0b'],['distracted','Mất tập trung','#f59e0b'],['sleepyRatio','Buồn ngủ','#dc2626'],['averageSleepy','Buồn ngủ','#dc2626'],['sleepy','Buồn ngủ','#dc2626'],['activeRatio','Hoạt động','#2563eb'],['averageActive','Hoạt động','#2563eb'],['active','Hoạt động','#2563eb']] as const;const seen=new Set<string>();return map.filter(([key,label])=>source[key]!=null&&!seen.has(label)&&!!seen.add(label)).map(([key,label,color])=>({label,value:Math.round(Number(source[key])*100),color}))}
  detailSections():Array<{key:string;label:string;rows:any[];empty:string}>{const value=this.data()??{};if(this.kind==='sessions')return[
    {key:'students',label:'Tổng hợp theo sinh viên',rows:value.students??[],empty:'Không có tổng hợp sinh viên.'},
    {key:'attendance',label:'Chuyên cần',rows:value.attendance??[],empty:'Không có dữ liệu chuyên cần.'},
    {key:'timeline',label:'Sự kiện hành vi',rows:value.timeline??[],empty:'0 sự kiện — không ghi nhận sự kiện hành vi trong buổi học.'},
    {key:'alerts',label:'Cảnh báo',rows:value.alerts??[],empty:'Không có cảnh báo trong buổi học.'},
    {key:'identities',label:'Kết quả nhận diện',rows:value.identities??[],empty:'Không có dữ liệu nhận diện trong buổi học.'},
    {key:'identityLinks',label:'Chi tiết nhận diện',rows:value.identityLinks??[],empty:'Không có chi tiết nhận diện.'},
    {key:'trackSegments',label:'Khoảng thời gian được ghi nhận',rows:value.trackSegments??[],empty:'Không có khoảng thời gian theo dõi.'},
    {key:'videos',label:'Video liên quan',rows:value.videos??[],empty:'Không có video đã lưu cho buổi học.'},
  ];if(this.kind==='students')return[
    {key:'sessions',label:'Tổng hợp theo buổi',rows:value.sessions??[],empty:'Chưa có tổng hợp sinh viên; các chỉ số bằng 0.'},
    {key:'attendance',label:'Chuyên cần',rows:value.attendance??[],empty:'Chưa có bản ghi chuyên cần.'},
    {key:'behaviorEvents',label:'Sự kiện hành vi',rows:value.behaviorEvents??[],empty:'0 sự kiện — không ghi nhận sự kiện hành vi.'},
    {key:'alerts',label:'Cảnh báo',rows:value.alerts??[],empty:'Không có cảnh báo.'},
    {key:'identities',label:'Danh tính',rows:value.identities??[],empty:'Chưa ghi nhận danh tính ổn định.'},
    {key:'videos',label:'Video liên quan',rows:value.videos??[],empty:'Chưa có video đã lưu.'},
  ];if(this.kind==='class-sections')return[
    {key:'timeline',label:'Các buổi thuộc phạm vi',rows:value.timeline??[],empty:'Không có buổi học trong phạm vi được phép xem.'},
    {key:'students',label:'Tổng hợp theo sinh viên',rows:value.students??[],empty:'Chưa có tổng hợp sinh viên; các chỉ số bằng 0.'},
    {key:'attendance',label:'Chuyên cần',rows:value.attendance??[],empty:'Chưa có bản ghi chuyên cần.'},
    {key:'behaviorEvents',label:'Sự kiện hành vi',rows:value.behaviorEvents??[],empty:'0 sự kiện — không ghi nhận sự kiện hành vi.'},
    {key:'alerts',label:'Cảnh báo',rows:value.alerts??[],empty:'Không có cảnh báo.'},
    {key:'identities',label:'Danh tính',rows:value.identities??[],empty:'Chưa ghi nhận danh tính ổn định.'},
    {key:'videos',label:'Video liên quan',rows:value.videos??[],empty:'Chưa có video đã lưu.'},
  ];const rows=value.students??value.sessions??value.timeline??value.attendance??value.attention??[];return[{key:'details',label:'Dữ liệu chi tiết',rows,empty:'Không có dữ liệu chi tiết; các chỉ số tổng hợp bằng 0.'}]}
  columnsFor(rows:any[]):string[]{const row=rows[0];return row?Object.keys(row).filter(key=>!key.toLowerCase().includes('comment')&&isReportColumnVisible(key)).slice(0,12):[]}
  autoComment():string{const value=this.data();const student=this.studentCommentSource(value);return value?.summary?.autoComment??value?.classSection?.autoComment??value?.session?.autoComment??student?.autoComment??''}
  autoCommentInfo():any{const generated=this.generationResult();if(generated)return generated;const value=this.data();const student=this.studentCommentSource(value);const source=value?.summary?.autoComment?value.summary:value?.classSection?.autoComment?value.classSection:student;if(!source?.autoComment)return null;const provider=String(source.autoCommentProvider??'');return{provider,model:provider.startsWith('GEMINI:')?provider.slice(7):provider==='RULE_BASED_FALLBACK'?'RULE_BASED':'',generatedAt:source.autoCommentGeneratedAt,isFallback:provider==='RULE_BASED_FALLBACK'}}
  private studentCommentSource(value:any):any{const target=Number(value?.autoCommentAvailability?.targetSessionId);return value?.students?.find((item:any)=>Number(item.sessionId)===target&&item.autoComment)??value?.students?.find((item:any)=>item.autoComment)}
  canGenerateComment():boolean{return !!this.id&&this.data()?.autoCommentAvailability?.canGenerate===true}
  commentUnavailableReason():string{return String(this.data()?.autoCommentAvailability?.reason??'')}
  studentCommentTarget():number|null{return Number(this.data()?.autoCommentAvailability?.targetSessionId)||null}
  generateComment(){if(!this.id||!this.canGenerateComment())return;this.generatingComment.set(true);this.commentError.set('');const target=this.studentCommentTarget();const request=this.kind==='sessions'?this.api.generateSessionAutoComment(this.id):this.kind==='class-sections'?this.api.generateClassSectionAutoComment(this.id):this.api.generateStudentAutoComment(Number(target),this.id);request.subscribe({next:(result)=>{this.generationResult.set(result);this.generatingComment.set(false);this.load()},error:(error)=>{this.commentError.set(errorText(error));this.generatingComment.set(false)}})}
  providerLabel(info:any):string{return info?.isFallback||info?.provider==='RULE_BASED_FALLBACK'?'Quy tắc nội bộ':'Dịch vụ nhận xét tự động'}
  fallbackMessage(code:string|null|undefined):string{const messages:Record<string,string>={PROVIDER_NOT_CONFIGURED:'Tính năng nhận xét tự động chưa được cấu hình.',NOT_CONFIGURED:'Tính năng nhận xét tự động chưa được cấu hình.',AUTH_ERROR:'Dịch vụ nhận xét tự động chưa thể xác thực.',RATE_LIMIT:'Dịch vụ nhận xét đang bận. Vui lòng thử lại sau.',INVALID_MODEL:'Dịch vụ nhận xét tự động chưa sẵn sàng.',NETWORK_ERROR:'Không thể kết nối dịch vụ nhận xét tự động.',TIMEOUT:'Dịch vụ nhận xét phản hồi quá chậm. Vui lòng thử lại.',SERVICE_UNAVAILABLE:'Dịch vụ nhận xét tạm thời không khả dụng. Vui lòng thử lại.',PROVIDER_EXCEPTION:'Dịch vụ nhận xét gặp sự cố ngoài dự kiến.',PROVIDER_ERROR:'Dịch vụ nhận xét chưa trả về nội dung phù hợp.',EMPTY_RESPONSE:'Dịch vụ nhận xét chưa tạo được nội dung.',SDK_ERROR:'Dịch vụ nhận xét gặp sự cố.'};return code?messages[code]??'Chưa thể tạo nhận xét tự động. Vui lòng thử lại sau.':'Chưa thể tạo nhận xét tự động tại thời điểm này.'}
  formatGeneratedAt(value:string):string{const date=new Date(value);return Number.isNaN(date.getTime())?'':date.toLocaleString('vi-VN')}
  columnLabel(key:string):string{return reportColumnLabel(key)}
  formatValue(value:any,key=''):string{return formatReportValue(value,key)}
  export(format:'pdf'|'excel'){if(!this.id||this.exporting())return;this.exporting.set(format);this.exportError.set('');this.api.exportReport(this.kind,this.id,format).subscribe({next:(response)=>{const blob=response.body;if(!blob){this.exportError.set('Hệ thống chưa tạo được tệp báo cáo.');this.exporting.set(null);return}const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=this.downloadName(response.headers.get('content-disposition'),format);anchor.click();setTimeout(()=>URL.revokeObjectURL(url),0);this.exporting.set(null)},error:(error)=>{this.exportError.set(`Không thể xuất ${format==='excel'?'Excel':'PDF'}: ${errorText(error)}`);this.exporting.set(null)}})}
  private downloadName(contentDisposition:string|null,format:'pdf'|'excel'):string{const encoded=contentDisposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];const plain=contentDisposition?.match(/filename="?([^";]+)"?/i)?.[1];const serverName=encoded?decodeURIComponent(encoded):plain;const extension=format==='excel'?'xlsx':'pdf';if(serverName&&serverName.toLowerCase().endsWith(`.${extension}`))return serverName;const scope=this.kind==='sessions'?'buoi-hoc':this.kind==='students'?'sinh-vien':'lop-hoc-phan';return`He-thong-giam-sat-Bao-cao-${scope}.${extension}`}
}
