const labels:Record<string,string>={sessionCount:'Số buổi học',summarizedSessionCount:'Số buổi đã tổng hợp',observedStudentCount:'Số sinh viên được ghi nhận',observedSeconds:'Thời gian quan sát',averageFocused:'Tỷ lệ tập trung trung bình',averageDistracted:'Tỷ lệ mất tập trung trung bình',averageSleepy:'Tỷ lệ buồn ngủ trung bình',averageActive:'Tỷ lệ hoạt động trung bình',averageAttendanceScorePercent:'Điểm chuyên cần (%)',scorePercent:'Điểm chuyên cần (%)',policyScore:'Điểm theo quy định',totalObservedSeconds:'Tổng thời gian quan sát',totalAlerts:'Số cảnh báo',alertCount:'Số cảnh báo',alertDurationSeconds:'Thời lượng cảnh báo',behaviorEvents:'Số sự kiện hành vi',focusedRatio:'Tỷ lệ tập trung',distractedRatio:'Tỷ lệ mất tập trung',sleepyRatio:'Tỷ lệ buồn ngủ',activeRatio:'Tỷ lệ hoạt động',focused:'Tỷ lệ tập trung',distracted:'Tỷ lệ mất tập trung',sleepy:'Tỷ lệ buồn ngủ',active:'Tỷ lệ hoạt động',alerts:'Số cảnh báo',presentSeconds:'Thời gian có mặt',presenceRatio:'Tỷ lệ hiện diện',fullName:'Họ tên',studentCode:'MSSV',scheduledStart:'Thời gian bắt đầu',scheduledEnd:'Thời gian kết thúc',startedAt:'Bắt đầu',endedAt:'Kết thúc',createdAt:'Thời điểm ghi nhận',status:'Trạng thái',label:'Hành vi',durationSeconds:'Thời lượng',course:'Môn học',classSection:'Lớp học phần',room:'Phòng học',fileName:'Tên tệp',sizeBytes:'Dung lượng'};

const hiddenTechnicalFields=new Set(['id','studentid','sessionid','classsectionid','stableidentityid','identitylinkid','trackid','currenttrackid','externaleventid','behavioreventid','evidenceref','provider','model','modelname','embedding','embeddingdimension','confidence','identityconfidence','probability','observationquality','cameraaiquality','margin','metadatajson','errorcode','finalizeerrorcode','endpoint','pipeline']);

export function isReportColumnVisible(key:string):boolean{
  const normalized=key.replace(/[_\s-]/g,'').toLowerCase();
  return !hiddenTechnicalFields.has(normalized)&&!normalized.endsWith('id')&&!normalized.includes('provider')&&!normalized.includes('model')&&!normalized.includes('confidence')&&!normalized.includes('embedding')&&!normalized.includes('track');
}

export function reportColumnLabel(key:string):string{return labels[key]??key.replace(/([A-Z])/g,' $1').trim()}

export function formatReportValue(value:any,key='',locale='vi-VN'):string{
  if(value==null)return'—';
  if(typeof value==='boolean')return value?'Có':'Không';
  if(typeof value==='object')return'Xem chi tiết';
  if(typeof value==='number'){
    const normalized=key.toLowerCase();
    if(normalized.endsWith('seconds'))return formatDuration(value);
    if(normalized.endsWith('scorepercent'))return`${value.toLocaleString(locale,{maximumFractionDigits:1})}%`;
    if(isRatioField(normalized))return`${(value*100).toLocaleString(locale,{maximumFractionDigits:1})}%`;
    return value.toLocaleString(locale,{maximumFractionDigits:Number.isInteger(value)?0:2});
  }
  if(/(at|date|start|end)$/i.test(key)){
    const date=new Date(value);
    if(!Number.isNaN(date.getTime()))return date.toLocaleString(locale);
  }
  const text=String(value);
  const translated:Record<string,string>={AVAILABLE:'Sẵn sàng',EMPTY:'Chưa có dữ liệu',OPEN:'Đang mở',ACK:'Đã xác nhận',CLOSED:'Đã đóng',PRESENT:'Có mặt',PARTIAL:'Có mặt một phần',ABSENT:'Vắng mặt',FOCUSED:'Tập trung',DISTRACTED:'Mất tập trung',SLEEPY:'Buồn ngủ',ACTIVE:'Hoạt động',UNKNOWN:'Chưa xác định'};
  return translated[text.toUpperCase()]??text;
}

function isRatioField(key:string):boolean{return key.includes('ratio')||key.includes('probability')||key.includes('quality')||key.includes('confidence')||key.includes('margin')||['focused','distracted','sleepy','active','averagefocused','averagedistracted','averagesleepy','averageactive'].includes(key)}
function formatDuration(seconds:number):string{const total=Math.max(0,Math.round(seconds));const minutes=Math.floor(total/60);const rest=total%60;return minutes>0?`${minutes} phút ${rest} giây`:`${rest} giây`}
