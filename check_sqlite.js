const crypto = require('crypto');
const https = require('https');
const KEY = 'C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==';

function auth(v, rt, rl) {
  const d = new Date().toUTCString();
  const p = v + '\n' + rt + '\n' + rl.toLowerCase() + '\n' + d.toLowerCase() + '\n\n';
  const s = crypto.createHmac('sha256', Buffer.from(KEY, 'base64')).update(p).digest('base64');
  return { a: encodeURIComponent('type=master&ver=1.0&sig=' + s), d };
}
async function req(m, p, rt, rl, body, extra) {
  const { a, d } = auth(m.toLowerCase(), rt, rl);
  const h = { Authorization: a, 'x-ms-date': d, 'x-ms-version': '2020-07-15', 'Content-Type': 'application/json', ...(extra||{}) };
  return new Promise((res,rej)=>{const r=https.request({hostname:'localhost',port:8081,path:p,method:m,rejectUnauthorized:false,headers:h},resp=>{let d='';resp.on('data',c=>d+=c);resp.on('end',()=>res({status:resp.statusCode,body:d}));});r.on('error',rej);if(body)r.write(typeof body==='string'?body:JSON.stringify(body));r.end();});
}
(async()=>{
  await req('POST','/dbs','dbs','',{id:'svdb'});
  await req('POST','/dbs/svdb/colls','colls','dbs/svdb',{id:'svc',partitionKey:{paths:['/pk'],kind:'Hash',Version:2}});
  await req('POST','/dbs/svdb/colls/svc/docs','docs','dbs/svdb/colls/svc',{id:'1',pk:'a',name:'Alice'},{  'x-ms-documentdb-partitionkey':'["a"]'});

  // Test: SELECT json_object('payload', json(body)) — does json() make it embed as sub-object?
  const q1 = "SELECT json_object('payload', json(body)) AS r FROM c";
  const r1 = await req('POST','/dbs/svdb/colls/svc/docs','docs','dbs/svdb/colls/svc',
    {query:q1,parameters:[]},
    {'Content-Type':'application/query+json','x-ms-documentdb-isquery':'True','x-ms-documentdb-query-enablecrosspartition':'True'});
  console.log('json(body) test:', r1.status, r1.body.substring(0,300));

  // Test: SELECT sqlite_version()
  const q2 = "SELECT VALUE sqlite_version() FROM c";
  const r2 = await req('POST','/dbs/svdb/colls/svc/docs','docs','dbs/svdb/colls/svc',
    {query:q2,parameters:[]},
    {'Content-Type':'application/query+json','x-ms-documentdb-isquery':'True','x-ms-documentdb-query-enablecrosspartition':'True'});
  console.log('SQLite version:', r2.status, r2.body.substring(0,200));
})();
