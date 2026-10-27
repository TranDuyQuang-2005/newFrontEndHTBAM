// Unit behavior checks with HTTP/DOM doubles; this does not render Angular or a browser.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const {test}=require('node:test');
const ts=require('../apps/web/node_modules/typescript');
class Params{constructor(data={}){this.data=data;}set(key,value){return new Params({...this.data,[key]:String(value)});}get(key){return this.data[key];}}
class HttpToken{}class ElementToken{}
class NodeDouble{
  constructor(document){this.document=document;this.parentElement=null;this.style={};}
  appendChild(node){node.parentElement=this;}
  contains(node){for(let next=node;next;next=next.parentElement)if(next===this)return true;return false;}
  focus(){this.document.activeElement=this;}
}
function fixture(){
  const document=new EventTarget();document.defaultView=null;document.body=new NodeDouble(document);document.getElementById=()=>({scrollIntoView(){}});
  const host=new NodeDouble(document),input=new NodeDouble(document),panel=new NodeDouble(document),panelHost=new NodeDouble(document);host.appendChild(input);host.appendChild(panelHost);panelHost.appendChild(panel);input.focus();
  const requests=[];const http={get(url,options){const request={url,params:options.params,closed:false,callbacks:null,resolve(value){if(!this.closed)this.callbacks.next(value);}};requests.push(request);return{subscribe(callbacks){request.callbacks=callbacks;return{unsubscribe(){request.closed=true;}};}};}};
  const signal=value=>{const get=()=>value;get.set=next=>{value=next;};get.update=fn=>{value=fn(value);};return get;};
  const documentToken=Symbol('document');const noDecorator=()=>()=>{};
  const core={Component:()=>target=>target,Input:noDecorator,ViewChild:noDecorator,HostListener:noDecorator,forwardRef:fn=>fn,signal,ElementRef:ElementToken,ChangeDetectionStrategy:{OnPush:0},inject:token=>token===documentToken?document:token===HttpToken?http:token===ElementToken?{nativeElement:host}:null};
  const modules={'@angular/common':{DOCUMENT:documentToken},'@angular/common/http':{HttpClient:HttpToken,HttpParams:Params},'@angular/core':core,'@angular/forms':{NG_VALUE_ACCESSOR:Symbol('valueAccessor')},'./ui':{displayBusinessCode:code=>code,errorText:()=> 'Request error'}};
  const source=fs.readFileSync('apps/web/src/app/shared/search-select.component.ts','utf8');const code=ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.CommonJS,experimentalDecorators:true}}).outputText;
  const sandbox={exports:{},require:name=>{if(!modules[name])throw Error(name);return modules[name];},setTimeout,clearTimeout};vm.runInNewContext(code,sandbox);
  const control=new sandbox.exports.SearchSelectComponent();control.resource='rooms';control.input={nativeElement:input};control.panel={nativeElement:panel};control.panelHost={nativeElement:panelHost};
  return{control,requests,document,host,panel,panelHost,input};
}
function key(value){return{key:value,prevented:false,stopped:false,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;}};}
const rows=[{id:41,code:'R41',name:'Room 41',isActive:true},{id:42,code:'R42',name:'Room 42',isActive:true}];

test('dropdown loads 10 scoped suggestions and keyboard selection stores an ID',()=>{
  const f=fixture();let chosen=null;f.control.registerOnChange(value=>{chosen=value;});f.control.filters={facultyId:7};f.control.show();
  assert.equal(f.panel.parentElement,f.document.body);assert.equal(f.requests[0].params.get('pageSize'),'10');assert.equal(f.requests[0].params.get('facultyId'),'7');
  f.requests[0].resolve({items:rows,hasMore:false});f.control.keydown(key('ArrowUp'));assert.equal(f.control.active(),1);f.control.keydown(key('Enter'));
  assert.equal(chosen,42);assert.equal(f.control.value,42);assert.equal(f.control.display(),'R42 · Room 42');assert.equal(f.control.open(),false);assert.equal(f.panel.parentElement,f.panelHost);f.control.ngOnDestroy();
});
test('typing cancels stale requests immediately and searches the full API after debounce',async()=>{
  const f=fixture();f.control.show();f.requests[0].resolve({items:rows,hasMore:true});f.control.search({target:{value:'phong thi nghiem'}});
  assert.equal(f.requests[0].closed,true);assert.equal(f.control.options().length,0);
  f.requests[0].resolve({items:rows,hasMore:true});assert.equal(f.control.options().length,0);
  await new Promise(resolve=>setTimeout(resolve,280));assert.equal(f.requests[1].params.get('q'),'phong thi nghiem');
  f.requests[1].resolve({items:[rows[1]],hasMore:true});f.control.load(2);assert.equal(f.requests[2].params.get('page'),'2');f.requests[2].resolve({items:[rows[0]],hasMore:false});assert.equal(f.control.options().length,2);f.control.ngOnDestroy();
});
test('Escape closes suggestions without bubbling to the dialog, and outside clicks close the portal',()=>{
  const f=fixture();f.control.show();const escape=key('Escape');f.control.keydown(escape);assert.equal(escape.prevented,true);assert.equal(escape.stopped,true);assert.equal(f.control.open(),false);
  f.control.show();f.control.outside({target:new NodeDouble(f.document)});assert.equal(f.control.open(),false);assert.equal(f.panel.parentElement,f.panelHost);f.control.ngOnDestroy();
});
test('an existing value resolves outside the first page and disabled options cannot be selected',()=>{
  const f=fixture();f.control.writeValue(42);assert.equal(f.requests[0].params.get('id'),'42');f.requests[0].resolve({items:[rows[1]],hasMore:false});assert.equal(f.control.display(),'R42 · Room 42');
  let changed=false;f.control.registerOnChange(()=>{changed=true;});f.control.choose({...rows[0],isActive:false});assert.equal(changed,false);assert.equal(f.control.value,42);f.control.ngOnDestroy();
});
test('destroying cancels pending debounce and HTTP work',async()=>{
  const f=fixture();f.control.show();f.control.search({target:{value:'pending'}});f.control.ngOnDestroy();await new Promise(resolve=>setTimeout(resolve,280));assert.equal(f.requests.length,1);assert.equal(f.requests[0].closed,true);
});

test('modal-bound catalog suggestions remain open after focus scrolling and release the background scroll lock',()=>{
  const f=fixture(),modal=new NodeDouble(f.document);f.host.closest=()=>modal;f.control.boundToModal=true;f.control.show();
  assert.equal(f.panel.parentElement,modal);f.document.dispatchEvent(new Event('scroll'));assert.equal(f.control.open(),true);
  const wheel=new Event('wheel',{cancelable:true});f.document.dispatchEvent(wheel);assert.equal(wheel.defaultPrevented,true);
  f.control.close();const after=new Event('wheel',{cancelable:true});f.document.dispatchEvent(after);assert.equal(after.defaultPrevented,false);assert.equal(f.panel.parentElement,f.panelHost);
  f.control.ngOnDestroy();
});
