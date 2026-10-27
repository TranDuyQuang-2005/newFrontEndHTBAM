// Optional regeneration: node tests/generate_class_section_fixtures.cjs <path-to-xlsx-package>
const fs = require('node:fs');
const path = require('node:path');
const XLSX = require(process.argv[2] || 'xlsx');
const dir = path.join(__dirname,'fixtures');
const headers = ['Mã LHP','Mã môn','Mã GV','Loại lịch','Thứ','Tiết bắt đầu','Tiết kết thúc','Phòng','Từ ngày','Đến ngày','Các tuần có lịch'];
function write(name,rows) {
  const book=XLSX.utils.book_new();XLSX.utils.book_append_sheet(book,XLSX.utils.aoa_to_sheet([headers,...rows]),'Lịch');
  XLSX.utils.book_append_sheet(book,XLSX.utils.aoa_to_sheet([['Ghi chú'],['Dữ liệu DEMO, không phải lịch chính thức.']]),'Hướng dẫn');
  for(const ext of ['xls','xlsx'])XLSX.writeFile(book,path.join(dir,`${name}.${ext}`),{bookType:ext==='xls'?'biff8':'xlsx'});
}
write('class-section-unit',[
 ['SRC-01','MH1','GV2','LT','2',1,3,'R1','02/09/2030','29/09/2030','T1, T3'],
 ['SRC-01','MH1','GV2','TH','3',7,9,'R2','02/09/2030','29/09/2030','T1, T3'],
]);
write('class-section-demo',[
 ['DEMO-EXCEL-01','DEMO-MH-01','DEMO-GV-EDITOR','LT','4',10,12,'DEMO-THEORY-05','17/08/2026','20/12/2026','T1, T3, T5'],
 ['DEMO-EXCEL-01','DEMO-MH-01','DEMO-GV-EDITOR','TH','5',13,15,'DEMO-COMPUTER-04','17/08/2026','20/12/2026','T1, T3, T5'],
]);
console.log('Generated four .xls/.xlsx fixtures.');
