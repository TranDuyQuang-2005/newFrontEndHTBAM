// Regenerate small synthetic fixtures; no real teacher names or student data are embedded.
const fs=require('node:fs'),path=require('node:path');
const XLSX=require(process.argv[2]||'xlsx');
const main=Array(41).fill(null),sub=Array(41).fill(null);
for(const [col,name] of [[0,'STT'],[1,'Mã LHP'],[2,'Tên môn học'],[3,'Số TC'],[4,'Lớp học'],[5,'Lịch học'],[10,'Thời gian học'],[11,'Tuần học'],[38,'Tổng số tiết'],[39,'Phòng'],[40,'Giảng viên']])main[col]=name;
for(const [col,name] of [[5,'Thứ'],[6,'Từ tiết'],[7,'Đến tiết'],[8,'Loại lịch'],[9,'Số tiết'],[10,'Bắt đầu'],[37,'Kết thúc']])sub[col]=name;
for(let n=1;n<=26;n++)sub[n+10]='T'+n;
function row(stt,day,activity,room){const x=Array(41).fill(null);Object.assign(x,{0:stt,1:'0000123456789',2:'Course',3:3,4:'Nhóm HUIT thử nghiệm',5:day,6:activity==='LT'?1:7,7:activity==='LT'?3:9,8:activity,9:12,10:'02/09/2030',11:'x',12:'X',14:1,37:'29/09/2030',38:12,39:room,40:'00102 - Lecturer'});if(stt===2)x[13]=true;return x;}
const rows=[[],[],[],['THỜI KHÓA BIỂU THỬ NGHIỆM'],[],[],main,sub,row(1,2,'LT','R1 - Địa chỉ thử'),row(2,3,'TH','R2')];
const book=XLSX.utils.book_new(),sheet=XLSX.utils.aoa_to_sheet(rows);
sheet['!merges']=[{s:{r:3,c:0},e:{r:3,c:40}},{s:{r:6,c:5},e:{r:6,c:9}},{s:{r:6,c:10},e:{r:6,c:37}}];
XLSX.utils.book_append_sheet(book,sheet,'HUIT');XLSX.utils.book_append_sheet(book,XLSX.utils.aoa_to_sheet([['Hướng dẫn'],['Dữ liệu giả lập để kiểm thử.']]),'Ghi chú');
for(const ext of ['xls','xlsx'])XLSX.writeFile(book,path.join(__dirname,'fixtures','huit-two-header.'+ext),{bookType:ext==='xls'?'biff8':'xlsx'});
console.log('Generated HUIT .xls/.xlsx fixtures with two header rows and 41 columns.');
