const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(require('node:path').join(__dirname,'../app.js'),'utf8');
const start=source.indexOf('function newTest(){'),end=source.indexOf('function renderTestRecords()',start);
const fields={};
for(const name of ['test-measured','test-label','test-route','test-delay','test-continuity','test-quality','test-notes','focus-factor'])fields['#'+name]={value:''};
const run={run_id:'a'.repeat(32),session:{source:'direct_export',buffer_bursts:0,modes:'f1c1r1',sharing_policy:0,usage_policy:0,reserve_frames:960,engine_period:'reference',capture_policy:'reference'}};
const context={window:{deskfusePhysicalRun:()=>structuredClone(run),deskfusePhysicalRunPending:()=>false},
  $:id=>fields[id],state:{nativeReserveFrames:1234},testRecords:[],changedKeys:()=>['unrelatedModelField'],
  renderTestRecords(){},persistWorkspace(){},toast(){},Date,Math,Number};
vm.createContext(context);vm.runInContext(source.slice(start,end),context);vm.runInContext('newTest()',context);
const saved=context.testRecords[0];assert.equal(saved.settings.reserve_frames,960);
assert.equal(saved.settings.nativeReserveFrames,undefined);assert.equal(saved.physical_run.run_id,run.run_id);
assert.equal(saved.changedKeys.join(','),'reserve_frames');assert.equal(saved.modeledSettings.nativeReserveFrames,1234);
run.session.reserve_frames=480;assert.equal(saved.settings.reserve_frames,960);
context.window.deskfusePhysicalRunPending=()=>true;vm.runInContext('newTest()',context);assert.equal(context.testRecords.length,1);
console.log('Notebook executes against immutable physical settings; pending trials cannot be mislabeled.');
