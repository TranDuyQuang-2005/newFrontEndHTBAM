import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { PageTitleComponent, errorText } from '../shared/ui';

@Component({standalone:true,imports:[FormsModule,PageTitleComponent],changeDetection:ChangeDetectionStrategy.OnPush,template:`
  <app-page-title title="Báo cáo" subtitle="Phân tích hành vi, chuyên cần và chất lượng quan sát từ dữ liệu thật.">
    @if(data()){<button type="button" class="secondary" (click)="export('excel')">Xuất Excel</button><button type="button" (click)="export('pdf')">Xuất PDF</button>}
  </app-page-title>
  <div class="filter-bar"><label>Loại báo cáo<select [(ngModel)]="kind" (ngModelChange)="changeKind()"><option value="sessions">Buổi học</option><option value="students">Sinh viên</option><option value="class-sections">Lớp học phần</option></select></label><label class="grow">Phạm vi được phép xem<select [(ngModel)]="id" (ngModelChange)="data.set(null)"><option [ngValue]="null">{{loadingOptions()?'Đang tải phạm vi…':'Chọn đối tượng'}}</option>@for(option of options();track option.id){<option [ngValue]="option.id">{{option.label}}{{option.status?' · '+option.status:''}}</option>}</select></label><button type="button" [disabled]="!id||loading()||loadingOptions()" (click)="load()">{{loading()?'Đang tải…':'Xem báo cáo'}}</button></div>
  @if(error()){<div class="error-box">{{error()}}</div>}
  @if(data()){
    <div class="stats stats-four">
      @for(metric of summaryEntries();track metric.key){<section class="stat-card"><span>{{metric.label}}</span><strong>{{metric.value}}</strong><small>Dữ liệu tổng hợp</small></section>}
    </div>
    @if(outputEntries().length){
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Trạng thái đầu ra</h3><p>Mỗi loại dữ liệu được đối chiếu theo đúng buổi học</p></div></div>
        <div class="service-grid">@for(item of outputEntries();track item.key){<div class="service-card"><span class="status-dot" [style.background]="item.status === 'AVAILABLE' ? '#16a34a' : '#94a3b8'"></span><div><strong>{{item.label}} · {{item.count}}</strong><small>{{item.message}}</small></div></div>}</div>
      </section>
    }
    <div class="dashboard-grid">
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Phân tích hành vi</h3><p>Tỷ lệ theo phạm vi báo cáo</p></div></div>
        @for(metric of behaviorEntries();track metric.label){<div class="metric-row"><span>{{metric.label}}</span><div class="progress-track"><div class="progress-value" [style.width.%]="metric.value" [style.background]="metric.color"></div></div><strong>{{metric.value}}%</strong></div>}
        @empty{<div class="empty-state" style="min-height:150px"><div><strong>Chưa có tỷ lệ hành vi</strong><span>API không trả về dữ liệu tổng hợp cho phạm vi này.</span></div></div>}
      </section>
      <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Nhận xét tự động</h3><p>Phân tích AI</p></div><span class="badge purple">AI</span></div>
        @if(autoComment()){<p style="line-height:1.7;color:#475569">{{autoComment()}}</p>}@else{<div class="empty-state" style="min-height:150px"><div><strong>Chưa có nhận xét</strong><span>Nhận xét AI chưa được tạo cho báo cáo này.</span></div></div>}
      </section>
    </div>
    @for(section of detailSections();track section.key){
      <section class="card table-card"><div class="card-header"><div><h3>{{section.label}}</h3><p>{{section.rows.length}} bản ghi</p></div></div><div class="table-wrap"><table><thead><tr>@for(column of columnsFor(section.rows);track column){<th>{{columnLabel(column)}}</th>}</tr></thead><tbody>
        @for(row of section.rows;track $index){<tr>@for(column of columnsFor(section.rows);track column){<td>{{formatValue(row[column])}}</td>}</tr>}@empty{<tr><td [attr.colspan]="columnsFor(section.rows).length||1" class="empty">{{section.empty}}</td></tr>}
      </tbody></table></div></section>
    }
  }@else{<section class="card"><div class="empty-state"><div><strong>{{options().length?'Chọn phạm vi để xem báo cáo':'Không có báo cáo trong phạm vi được cấp'}}</strong><span>{{options().length?'Danh sách chỉ gồm dữ liệu backend đã kiểm tra theo vai trò, khoa, bộ môn, phân công giảng dạy và dạy thay.':'Không tìm thấy lớp/buổi phù hợp; hệ thống không tự tạo dữ liệu báo cáo.'}}</span></div></div></section>}
`})
export class ReportsComponent implements OnInit{
  private readonly api=inject(ApiService);kind:'sessions'|'students'|'class-sections'='sessions';id:number|null=null;readonly data=signal<any>(null);readonly error=signal('');readonly loading=signal(false);readonly loadingOptions=signal(false);readonly options=signal<Array<{id:number;label:string;status?:string}>>([]);
  ngOnInit(){this.loadOptions()}
  changeKind(){this.id=null;this.data.set(null);this.loadOptions()}
  loadOptions(){this.loadingOptions.set(true);this.error.set('');this.api.reportOptions(this.kind).subscribe({next:(items)=>{this.options.set(items);this.loadingOptions.set(false);if(items.length){this.id=items[0].id;this.load()}},error:(error)=>{this.options.set([]);this.error.set(errorText(error));this.loadingOptions.set(false)}})}
  load(){if(!this.id)return;this.loading.set(true);this.error.set('');this.api.report(this.kind,this.id).subscribe({next:(value)=>{this.data.set(value);this.loading.set(false)},error:(error)=>{this.error.set(errorText(error));this.loading.set(false)}})}
  summaryEntries(){const source=this.data()?.summary??{};return Object.entries(source).slice(0,4).map(([key,value])=>({key,label:this.columnLabel(key),value:this.formatValue(value)}))}
  outputEntries(){const source=this.data()?.outputs??{};const labels:Record<string,string>={video:'Video',behaviorEvents:'Sự kiện hành vi',alerts:'Cảnh báo',identities:'Danh tính',attendance:'Chuyên cần',summaries:'Báo cáo tổng hợp'};return Object.entries(source).map(([key,value]:[string,any])=>({key,label:labels[key]??key,status:value?.status??'EMPTY',count:Number(value?.count??0),message:String(value?.message??'')}))}
  behaviorEntries(){const source=this.data()?.summary??{};const map=[['focusedRatio','Tập trung','#16a34a'],['averageFocused','Tập trung','#16a34a'],['focused','Tập trung','#16a34a'],['distractedRatio','Mất tập trung','#f59e0b'],['averageDistracted','Mất tập trung','#f59e0b'],['distracted','Mất tập trung','#f59e0b'],['sleepyRatio','Buồn ngủ','#dc2626'],['averageSleepy','Buồn ngủ','#dc2626'],['sleepy','Buồn ngủ','#dc2626'],['activeRatio','Hoạt động','#2563eb'],['averageActive','Hoạt động','#2563eb'],['active','Hoạt động','#2563eb']] as const;const seen=new Set<string>();return map.filter(([key,label])=>source[key]!=null&&!seen.has(label)&&!!seen.add(label)).map(([key,label,color])=>({label,value:Math.round(Number(source[key])*100),color}))}
  detailSections():Array<{key:string;label:string;rows:any[];empty:string}>{const value=this.data()??{};if(this.kind==='sessions')return[
    {key:'students',label:'Tổng hợp theo sinh viên',rows:value.students??[],empty:'Không có tổng hợp sinh viên.'},
    {key:'attendance',label:'Chuyên cần',rows:value.attendance??[],empty:'Không có dữ liệu chuyên cần.'},
    {key:'timeline',label:'Sự kiện hành vi',rows:value.timeline??[],empty:'0 sự kiện — không ghi nhận sự kiện hành vi trong buổi học.'},
    {key:'alerts',label:'Cảnh báo',rows:value.alerts??[],empty:'0 cảnh báo — không có rule/model nào phát hiện cảnh báo.'},
    {key:'identities',label:'Danh tính ổn định',rows:value.identities??[],empty:'Không ghi nhận danh tính từ pipeline.'},
    {key:'identityLinks',label:'Bằng chứng nhận diện',rows:value.identityLinks??[],empty:'Không có bằng chứng liên kết danh tính.'},
    {key:'trackSegments',label:'Đoạn theo dõi',rows:value.trackSegments??[],empty:'Không ghi nhận đoạn theo dõi.'},
    {key:'videos',label:'Video và artifact',rows:value.videos??[],empty:'Không có video/artifact đã lưu cho buổi học.'},
  ];if(this.kind==='students')return[
    {key:'sessions',label:'Tổng hợp theo buổi',rows:value.sessions??[],empty:'Chưa có tổng hợp sinh viên; các chỉ số bằng 0.'},
    {key:'attendance',label:'Chuyên cần',rows:value.attendance??[],empty:'Chưa có bản ghi chuyên cần.'},
    {key:'behaviorEvents',label:'Sự kiện hành vi',rows:value.behaviorEvents??[],empty:'0 sự kiện — không ghi nhận sự kiện hành vi.'},
    {key:'alerts',label:'Cảnh báo',rows:value.alerts??[],empty:'0 cảnh báo — không có rule/model nào phát hiện cảnh báo.'},
    {key:'identities',label:'Danh tính',rows:value.identities??[],empty:'Chưa ghi nhận danh tính ổn định.'},
    {key:'videos',label:'Video liên quan',rows:value.videos??[],empty:'Chưa có video/artifact đã lưu.'},
  ];if(this.kind==='class-sections')return[
    {key:'timeline',label:'Các buổi thuộc phạm vi',rows:value.timeline??[],empty:'Không có buổi học trong phạm vi được phép xem.'},
    {key:'students',label:'Tổng hợp theo sinh viên',rows:value.students??[],empty:'Chưa có tổng hợp sinh viên; các chỉ số bằng 0.'},
  ];const rows=value.students??value.sessions??value.timeline??value.attendance??value.attention??[];return[{key:'details',label:'Dữ liệu chi tiết',rows,empty:'Không có dữ liệu chi tiết; các chỉ số tổng hợp bằng 0.'}]}
  columnsFor(rows:any[]):string[]{const row=rows[0];return row?Object.keys(row).filter(key=>!key.toLowerCase().includes('comment')&&!key.toLowerCase().includes('provider')).slice(0,9):[]}
  autoComment():string{const value=this.data();return value?.summary?.autoComment??value?.classSection?.autoComment??value?.session?.autoComment??value?.students?.find((item:any)=>item.autoComment)?.autoComment??''}
  columnLabel(key:string):string{const labels:Record<string,string>={sessionCount:'Số buổi',averageFocused:'Tập trung TB',averageDistracted:'Mất tập trung TB',averageSleepy:'Buồn ngủ TB',averageActive:'Hoạt động TB',averageAttendanceScore:'Điểm chuyên cần',totalObservedSeconds:'Thời gian quan sát',totalAlerts:'Cảnh báo',focused:'Tập trung',distracted:'Mất tập trung',sleepy:'Buồn ngủ',active:'Hoạt động',alerts:'Cảnh báo',studentCode:'MSSV',fullName:'Họ tên',scheduledStart:'Thời gian'};return labels[key]??key.replace(/([A-Z])/g,' $1').trim()}
  formatValue(value:any):string{if(value==null)return'—';if(typeof value==='number'&&value>=0&&value<=1)return`${Math.round(value*100)}%`;if(typeof value==='boolean')return value?'Có':'Không';if(typeof value==='object')return'Xem chi tiết';return String(value)}
  export(format:'pdf'|'excel'){if(!this.id)return;this.api.exportReport(this.kind,this.id,format).subscribe({next:(blob)=>{const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=`HTBAM-${this.kind}-${this.id}.${format==='excel'?'xlsx':'pdf'}`;anchor.click();URL.revokeObjectURL(url)},error:(error)=>this.error.set(errorText(error))})}
}
