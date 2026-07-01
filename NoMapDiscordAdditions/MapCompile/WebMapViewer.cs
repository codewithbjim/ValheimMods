namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// The static index.html for the exported web map: a self-contained,
    /// dependency-free viewer (drag-pan, wheel-zoom, per-kind pin filtering
    /// with solo / zoom-to-all, searchable pin list, click-to-open detail
    /// cards, coordinate grid, distance measure, mini-map inset, marker
    /// clustering and a metric scale bar). It reads its data from the sibling
    /// data.js (window.NMDA_MAP) that WebMapExport writes next to it, so it
    /// loads over file:// with no server (the Averia Serif Libre font is
    /// pulled from Google Fonts when online, with a serif fallback). Pin icons
    /// resolve by convention: pin icon index N -> icons/icon_N.png.
    ///
    /// Authored with single-quoted HTML/JS so it embeds as a C# verbatim
    /// string with no escaping. Edit the live copy in an exported bundle, then
    /// regenerate this const from it.
    /// </summary>
    public static class WebMapViewer
    {
        public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Valheim Map</title>
<link rel='preconnect' href='https://fonts.googleapis.com'>
<link rel='preconnect' href='https://fonts.gstatic.com' crossorigin>
<link href='https://fonts.googleapis.com/css2?family=Averia+Serif+Libre:wght@400;700&display=swap' rel='stylesheet'>
<style>
  :root {
    --bg:#0f1418; --panel:#241d15; --panel2:#2c2318; --edge:#4a3c29; --edge2:#6b5636;
    --ink:#ece0c8; --muted:#a3927a; --accent:#d8b25a; --accent2:#b8862f;
    --parch:#e9dcc0; --parchink:#2a2216; --sea:#0f1418;
  }
  * { box-sizing:border-box; }
  html, body { margin:0; height:100%; overflow:hidden; background:var(--bg); color:var(--ink);
    font-family:'Averia Serif Libre', Georgia, 'Times New Roman', serif; font-size:14px; }
  #app { display:flex; height:100%; }
  button { font-family:inherit; }

  /* Sidebar */
  #side { width:288px; flex:0 0 288px; display:flex; flex-direction:column;
    background:linear-gradient(180deg,#2a2216,#1d160f); border-right:1px solid var(--edge2);
    box-shadow:2px 0 12px rgba(0,0,0,.5); z-index:5; }
  #brand { padding:14px 16px 10px; border-bottom:1px solid var(--edge); }
  #brand .brandrow { display:flex; align-items:center; justify-content:space-between; gap:8px; }
  #brand .tbtn { padding:5px 13px; }
  #brand h1 { font-size:19px; margin:0; color:var(--accent); letter-spacing:1px; font-weight:700; text-shadow:0 1px 2px #000; }
  #brand .sub { color:var(--muted); font-size:12px; margin-top:2px; }

  .section { border-bottom:1px solid var(--edge); }
  .sechead { display:flex; align-items:center; gap:8px; padding:9px 14px; cursor:pointer;
    color:var(--accent); font-weight:700; font-size:13px; letter-spacing:.5px; user-select:none; }
  .sechead:hover { background:rgba(216,178,90,.08); }
  .sechead .caret { transition:transform .15s; font-size:10px; color:var(--muted); }
  .section.collapsed .caret { transform:rotate(-90deg); }
  .section.collapsed .secbody { display:none; }
  .sechead .grow { flex:1 1 auto; }
  .sechead .mini { font-weight:400; color:var(--muted); font-size:11px; }
  .secbody { padding:6px 8px 10px; }

  .toolbar { display:flex; gap:6px; flex-wrap:wrap; }
  .tbtn, .linkbtn { background:var(--panel2); color:var(--ink); border:1px solid var(--edge2);
    border-radius:6px; padding:6px 10px; font-size:12px; cursor:pointer; }
  .tbtn:hover, .linkbtn:hover { border-color:var(--accent); color:var(--accent); }
  .tbtn.on { background:var(--accent2); border-color:var(--accent); color:#1c1206; font-weight:700; }
  input.search { width:100%; background:#1a140d; color:var(--ink); border:1px solid var(--edge2);
    border-radius:6px; padding:7px 9px; font-size:12px; margin-bottom:6px; }
  input.search::placeholder { color:#7d6c55; }

  #groups { max-height:38vh; overflow-y:auto; }
  .grp { display:flex; align-items:center; gap:9px; padding:5px 7px; border-radius:6px; }
  .grp:hover { background:rgba(216,178,90,.08); }
  .grp .cbx { width:15px; height:15px; accent-color:var(--accent); cursor:pointer; }
  .grp img { width:22px; height:22px; object-fit:contain; filter:drop-shadow(0 1px 1px #000); }
  .grp .nm { flex:1 1 auto; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; cursor:pointer; }
  .grp .ct { color:var(--muted); font-size:11px; min-width:18px; text-align:right; }
  .grp .act { opacity:0; display:flex; gap:3px; transition:opacity .1s; }
  .grp:hover .act { opacity:1; }
  .grp .act button { background:none; border:1px solid var(--edge2); color:var(--muted);
    border-radius:4px; font-size:11px; padding:1px 5px; cursor:pointer; }
  .grp .act button:hover { color:var(--accent); border-color:var(--accent); }

  #pinlist { max-height:32vh; overflow-y:auto; }
  .prow { display:flex; align-items:center; gap:9px; padding:5px 7px; border-radius:6px; cursor:pointer; }
  .prow:hover { background:rgba(216,178,90,.10); }
  .prow img { width:19px; height:19px; object-fit:contain; filter:drop-shadow(0 1px 1px #000); }
  .prow .nm { flex:1 1 auto; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .prow .co { color:var(--muted); font-size:11px; }

  /* Map area */
  #stagewrap { position:relative; flex:1 1 auto; overflow:hidden; cursor:grab; background:var(--sea); }
  #stagewrap.drag { cursor:grabbing; }
  #stagewrap.measure { cursor:crosshair; }
  #frame { position:absolute; inset:0; pointer-events:none; z-index:6;
    box-shadow:inset 0 0 0 2px rgba(107,86,54,.7), inset 0 0 0 6px rgba(20,15,9,.55), inset 0 0 90px 12px rgba(0,0,0,.55); }
  #stage { position:absolute; top:0; left:0; transform-origin:0 0; }
  #base { position:absolute; top:0; left:0; user-select:none; -webkit-user-drag:none; }
  #fx { position:absolute; inset:0; pointer-events:none; z-index:2; }
  #pins { position:absolute; inset:0; pointer-events:none; z-index:3; }

  .pin { position:absolute; transform:translate(-50%,-50%); pointer-events:auto; cursor:pointer; }
  .pin img { display:block; max-width:26px; max-height:26px; width:auto; height:auto;
    filter:drop-shadow(0 1px 2px rgba(0,0,0,.9)); -webkit-user-drag:none; transition:transform .1s; }
  .pin:hover img { transform:scale(1.25); }
  .pin .cap { position:absolute; top:100%; left:50%; transform:translateX(-50%); margin-top:1px;
    white-space:nowrap; font-size:11px; color:#fff; text-shadow:0 0 2px #000,0 0 2px #000,0 0 3px #000; pointer-events:none; }
  .pin.hl::after { content:''; position:absolute; left:50%; top:50%; transform:translate(-50%,-50%);
    border:2px solid var(--accent); border-radius:50%; animation:pulse 1.1s ease-out 2; pointer-events:none; }
  @keyframes pulse { 0%{opacity:1;width:14px;height:14px;} 100%{opacity:0;width:64px;height:64px;} }

  .cluster { position:absolute; transform:translate(-50%,-50%); pointer-events:auto; cursor:pointer;
    width:34px; height:34px; border-radius:50%; background:radial-gradient(circle at 35% 30%,#e6c060,#a8781f);
    border:2px solid #f0d98a; color:#241706; font-weight:700; font-size:13px;
    display:flex; align-items:center; justify-content:center; box-shadow:0 2px 6px rgba(0,0,0,.7); }
  .cluster:hover { filter:brightness(1.1); }

  #maptools { position:absolute; top:12px; left:12px; z-index:6; display:flex; gap:6px; }
  #hud { position:absolute; left:12px; bottom:12px; z-index:6; background:rgba(15,12,9,.72);
    border:1px solid var(--edge2); border-radius:6px; padding:5px 10px; font-size:12px; pointer-events:none; }
  #scalebar { position:absolute; left:12px; bottom:46px; z-index:6; height:22px; pointer-events:none; color:var(--ink); font-size:11px; }
  #scalebar .bar { height:7px; border:1px solid #f0d98a; border-top:none;
    background:linear-gradient(90deg,rgba(240,217,138,.25),rgba(240,217,138,.05)); }
  #scalebar .lbl { text-shadow:0 1px 2px #000; }

  #tip { position:absolute; display:none; z-index:8; background:var(--parch); color:var(--parchink);
    border:1px solid var(--accent2); border-radius:6px; padding:5px 9px; font-size:12px; max-width:240px;
    pointer-events:none; transform:translate(12px,12px); box-shadow:0 3px 10px rgba(0,0,0,.6); }
  #tip .t { font-weight:700; }
  #tip .c { color:#6a5836; font-size:11px; }

  #card { position:absolute; display:none; z-index:9; width:222px; background:var(--parch); color:var(--parchink);
    border:1px solid var(--accent2); border-radius:8px; box-shadow:0 6px 20px rgba(0,0,0,.65); overflow:hidden; }
  #card .hd { display:flex; align-items:center; gap:9px; padding:10px 12px; background:rgba(184,134,47,.16); border-bottom:1px solid rgba(184,134,47,.4); }
  #card .hd img { width:26px; height:26px; object-fit:contain; }
  #card .hd .tt { font-weight:700; font-size:15px; line-height:1.1; }
  #card .hd .kk { color:#6a5836; font-size:11px; }
  #card .bd { padding:9px 12px; font-size:12px; }
  #card .co { font-family:monospace; font-size:12px; color:#3a2f1d; }
  #card .row { display:flex; gap:6px; margin-top:9px; }
  #card .row button { flex:1 1 auto; background:#3a2f1d; color:var(--parch); border:none; border-radius:5px; padding:6px; font-size:11px; cursor:pointer; }
  #card .row button:hover { background:#54432a; }
  #card .x { position:absolute; top:6px; right:8px; cursor:pointer; color:#6a5836; font-size:16px; line-height:1; }
  #card .x:hover { color:#3a2f1d; }

  #mini { position:absolute; right:12px; bottom:60px; z-index:6; border:2px solid var(--edge2); border-radius:4px;
    background:#0b0f12; box-shadow:0 3px 10px rgba(0,0,0,.6); cursor:pointer; display:none; }
  #zoom { position:absolute; right:12px; bottom:12px; z-index:6; display:flex; flex-direction:column; gap:5px; }
  #zoom button { width:36px; height:36px; font-size:18px; background:rgba(36,29,21,.92); color:var(--ink);
    border:1px solid var(--edge2); border-radius:6px; cursor:pointer; }
  #zoom button:hover { border-color:var(--accent); color:var(--accent); }

  #load { position:absolute; inset:0; z-index:20; display:flex; align-items:center; justify-content:center;
    flex-direction:column; gap:14px; background:var(--sea); color:var(--muted); }
  #load .ring { width:44px; height:44px; border:4px solid var(--edge2); border-top-color:var(--accent);
    border-radius:50%; animation:spin 1s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }

  .foot { padding:8px 14px; font-size:11px; color:var(--muted); }
  ::-webkit-scrollbar { width:9px; } ::-webkit-scrollbar-thumb { background:var(--edge2); border-radius:4px; }
  ::-webkit-scrollbar-track { background:transparent; }
</style>
</head>
<body>
<div id='app'>
  <div id='side'>
    <div id='brand'>
      <div class='brandrow'><h1>&#9670; Valheim Map</h1><button class='tbtn' id='btnfit'>Fit</button></div>
      <div class='sub' id='worldname'>&nbsp;</div>
    </div>

    <div class='section' data-k='layers'>
      <div class='sechead'><span class='grow'>Layers</span><span class='caret'>&#9660;</span></div>
      <div class='secbody'>
        <div class='toolbar'>
          <button class='tbtn' id='btnlabels'>Labels</button>
          <button class='tbtn' id='btngrid'>Grid</button>
          <button class='tbtn' id='btnmini'>Mini-map</button>
          <button class='tbtn' id='btncluster'>Cluster</button>
        </div>
      </div>
    </div>

    <div class='section' data-k='tools'>
      <div class='sechead'><span class='grow'>Tools</span><span class='caret'>&#9660;</span></div>
      <div class='secbody'>
        <div class='toolbar'>
          <button class='tbtn' id='btnmeasure'>Measure</button>
        </div>
      </div>
    </div>

    <div class='section' data-k='kinds'>
      <div class='sechead'><span class='grow'>Kinds</span><span class='mini' id='kindmini'></span><span class='caret'>&#9660;</span></div>
      <div class='secbody'>
        <input class='search' id='ksearch' type='text' placeholder='Filter kinds...' autocomplete='off'>
        <div class='toolbar' style='margin-bottom:6px'>
          <button class='linkbtn' id='allon'>All on</button>
          <button class='linkbtn' id='alloff'>All off</button>
        </div>
        <div id='groups'></div>
      </div>
    </div>

    <div class='section' data-k='pins'>
      <div class='sechead'><span class='grow'>Pins</span><span class='mini' id='pinmini'></span><span class='caret'>&#9660;</span></div>
      <div class='secbody'>
        <input class='search' id='psearch' type='text' placeholder='Search pins...' autocomplete='off'>
        <div id='pinlist'></div>
      </div>
    </div>

    <div class='foot' id='foot'>Drag to pan &middot; wheel to zoom &middot; F fit &middot; L labels &middot; G grid</div>
  </div>

  <div id='stagewrap'>
    <div id='stage'><img id='base' draggable='false'></div>
    <canvas id='fx'></canvas>
    <div id='pins'></div>
    <div id='frame'></div>

    <div id='scalebar'><div class='lbl'>&nbsp;</div><div class='bar'></div></div>
    <div id='hud'>&mdash;</div>
    <canvas id='mini'></canvas>
    <div id='zoom'>
      <button id='zin'>+</button>
      <button id='zout'>&minus;</button>
      <button id='zfit' title='Fit'>&#9634;</button>
    </div>

    <div id='tip'></div>
    <div id='card'></div>
    <div id='load'><div class='ring'></div><div>Loading map&hellip;</div></div>
  </div>
</div>

<script src='data.js'></script>
<script>
(function(){
  var M = window.NMDA_MAP;
  if (!M) { document.body.innerHTML = '<p style=padding:20px>data.js failed to load.</p>'; return; }
  var $ = function(id){ return document.getElementById(id); };
  var wrap=$('stagewrap'), stage=$('stage'), base=$('base'), pinsLayer=$('pins'),
      fx=$('fx'), fctx=fx.getContext('2d'), hud=$('hud'), tip=$('tip'), card=$('card'),
      groupsBox=$('groups'), pinlistBox=$('pinlist'), mini=$('mini'), mctx=mini.getContext('2d'), scalebar=$('scalebar');

  var W=M.image.w, H=M.image.h, wmin=M.world.min, wmax=M.world.max;
  var wsx=wmax[0]-wmin[0], wsz=wmax[1]-wmin[1];
  base.src=M.image.file; base.style.width=W+'px'; base.style.height=H+'px';
  stage.style.width=W+'px'; stage.style.height=H+'px';
  $('worldname').textContent=(M.world.name?M.world.name+'  •  ':'')+W+'×'+H+'px';
  var LSKEY='nmda_map_'+(M.world.name||'world');

  var s=1, ox=0, oy=0, MINS=0.02, MAXS=10;
  var hidden={}, showLabels=false, gridOn=false, clusterOn=true, miniOn=false, measureOn=false;
  var measurePts=[], measureLive=null, pinListRows=[], items=[];

  function clampScale(v){ return Math.max(MINS, Math.min(MAXS, v)); }
  function screenToWorld(mx,my){ var ix=(mx-ox)/s, iy=(my-oy)/s; return [ wmin[0]+(ix/W)*wsx, wmin[1]+((H-iy)/H)*wsz ]; }
  function worldToImg(wx,wz){ return [ (wx-wmin[0])/wsx*W, (1-(wz-wmin[1])/wsz)*H ]; }
  function sppm(){ return (W/wsx)*s; }

  // name prettifier: most-common pin name per kind, tidy the sprite key
  var commonName={};
  (function(){ var byKey={};
    for(var i=0;i<M.pins.length;i++){ var p=M.pins[i]; if(!p.n) continue; (byKey[p.key]=byKey[p.key]||{})[p.n]=(byKey[p.key][p.n]||0)+1; }
    for(var k in byKey){ var best=null,bn=-1; for(var nm in byKey[k]) if(byKey[k][nm]>bn){bn=byKey[k][nm];best=nm;} commonName[k]=best; } })();
  function titleCase(s){ return s.replace(/\w\S*/g,function(t){return t.charAt(0).toUpperCase()+t.slice(1);}); }
  function prettyKind(g){ var n=g.name||'';
    if(/^type:/i.test(n)||n===''){ n=commonName[g.key]||n; }
    n=n.replace(/mapicon[_ ]?/ig,'').replace(/[_-]/g,' ').replace(/\bcolored\b/ig,'').trim();
    return titleCase(n)||g.key; }
  var kindName={}, iconOf={};
  for(var gi=0; gi<M.groups.length; gi++){ kindName[M.groups[gi].key]=prettyKind(M.groups[gi]); iconOf[M.groups[gi].key]=M.groups[gi].icon; }
  function pinLabel(p){ return p.n || kindName[p.key] || p.key; }

  function applyStage(){ stage.style.transform='translate('+ox+'px,'+oy+'px) scale('+s+')'; }
  function fit(){ var vw=wrap.clientWidth, vh=wrap.clientHeight; s=clampScale(Math.min(vw/W, vh/H)*0.94);
    ox=(vw-W*s)/2; oy=(vh-H*s)/2; render(); save(); }

  // clustering + pin DOM (rebuilt on zoom / filter change; only repositioned on pan)
  function rebuildItems(){ pinsLayer.innerHTML=''; items=[];
    var vis=[]; for(var i=0;i<M.pins.length;i++){ if(!hidden[M.pins[i].key]) vis.push(M.pins[i]); }
    if(clusterOn){ var cell=44/s, buckets={};
      for(var j=0;j<vis.length;j++){ var p=vis[j]; var key=Math.floor(p.px/cell)+'_'+Math.floor(p.py/cell); (buckets[key]=buckets[key]||[]).push(p); }
      for(var b in buckets){ var arr=buckets[b];
        if(arr.length===1){ items.push({type:'pin',px:arr[0].px,py:arr[0].py,pin:arr[0]}); }
        else { var sx=0,sy=0; for(var q=0;q<arr.length;q++){sx+=arr[q].px;sy+=arr[q].py;} items.push({type:'cluster',px:sx/arr.length,py:sy/arr.length,pins:arr}); } }
    } else { for(var k=0;k<vis.length;k++) items.push({type:'pin',px:vis[k].px,py:vis[k].py,pin:vis[k]}); }
    var frag=document.createDocumentFragment();
    for(var it=0; it<items.length; it++){ var item=items[it], el;
      if(item.type==='pin'){ el=document.createElement('div'); el.className='pin';
        var img=document.createElement('img'); img.src='icons/icon_'+item.pin.icon+'.png'; el.appendChild(img);
        if(item.pin.n){ var cap=document.createElement('div'); cap.className='cap'; cap.textContent=item.pin.n; cap.style.display=showLabels?'':'none'; el.appendChild(cap); el._cap=cap; }
        (function(pn,ee){ ee.addEventListener('mouseenter',function(e){ showTip(pinLabel(pn), Math.round(pn.x)+', '+Math.round(pn.z), e); });
          ee.addEventListener('mousemove',moveTip); ee.addEventListener('mouseleave',hideTip);
          ee.addEventListener('click',function(e){ e.stopPropagation(); openCard(pn,ee); }); })(item.pin,el);
      } else { el=document.createElement('div'); el.className='cluster'; el.textContent=item.pins.length;
        (function(cl,ee){ ee.addEventListener('click',function(e){ e.stopPropagation(); zoomAtImg(cl.px,cl.py, s*2.2); }); })(item,el); }
      item.el=el; frag.appendChild(el); }
    pinsLayer.appendChild(frag);
  }
  function positionItems(){ var vw=wrap.clientWidth, vh=wrap.clientHeight;
    for(var i=0;i<items.length;i++){ var it=items[i], x=ox+it.px*s, y=oy+it.py*s;
      if(x<-60||y<-60||x>vw+60||y>vh+60){ it.el.style.display='none'; continue; }
      it.el.style.display=''; it.el.style.left=x+'px'; it.el.style.top=y+'px';
      if(it.el._cap) it.el._cap.style.display=showLabels?'':'none'; } }

  function render(){ applyStage(); positionItems(); drawFx(); drawScale(); if(miniOn) drawMini(); }

  function niceNum(t){ var arr=[5,10,20,25,50,100,200,250,500,1000,2000,2500,5000,10000,20000,50000];
    for(var i=0;i<arr.length;i++) if(arr[i]>=t) return arr[i]; return arr[arr.length-1]; }
  function resizeFx(){ var r=wrap.getBoundingClientRect(); fx.width=r.width; fx.height=r.height; }
  function drawFx(){ var vw=fx.width, vh=fx.height; fctx.clearRect(0,0,vw,vh);
    if(gridOn){ var m=sppm(), iv=niceNum(90/m); var tl=screenToWorld(0,0), br=screenToWorld(vw,vh);
      var xMin=Math.min(tl[0],br[0]), xMax=Math.max(tl[0],br[0]), zMin=Math.min(tl[1],br[1]), zMax=Math.max(tl[1],br[1]);
      fctx.strokeStyle='rgba(216,178,90,.18)'; fctx.fillStyle='rgba(236,224,200,.55)'; fctx.lineWidth=1; fctx.font='11px serif';
      for(var x=Math.ceil(xMin/iv)*iv; x<=xMax; x+=iv){ var sx=ox+((x-wmin[0])/wsx*W)*s; fctx.beginPath(); fctx.moveTo(sx,0); fctx.lineTo(sx,vh); fctx.stroke(); fctx.fillText(Math.round(x), sx+3, 12); }
      for(var z=Math.ceil(zMin/iv)*iv; z<=zMax; z+=iv){ var sy=oy+((1-(z-wmin[1])/wsz)*H)*s; fctx.beginPath(); fctx.moveTo(0,sy); fctx.lineTo(vw,sy); fctx.stroke(); fctx.fillText(Math.round(z), 3, sy-3); } }
    if(measurePts.length){ var pts=measurePts.map(function(w){ var im=worldToImg(w[0],w[1]); return [ox+im[0]*s, oy+im[1]*s]; });
      if(measureLive && measurePts.length===1) pts.push(measureLive);
      fctx.strokeStyle='#d8b25a'; fctx.lineWidth=2; fctx.setLineDash([6,4]);
      if(pts.length>1){ fctx.beginPath(); fctx.moveTo(pts[0][0],pts[0][1]); fctx.lineTo(pts[1][0],pts[1][1]); fctx.stroke(); } fctx.setLineDash([]);
      for(var i=0;i<pts.length;i++){ fctx.fillStyle='#f0d98a'; fctx.beginPath(); fctx.arc(pts[i][0],pts[i][1],4,0,7); fctx.fill(); fctx.strokeStyle='#241706'; fctx.lineWidth=1; fctx.stroke(); }
      if(pts.length>1){ var a=measurePts[0], b=(measurePts[1]||screenToWorld(pts[1][0],pts[1][1])); var d=Math.hypot(b[0]-a[0], b[1]-a[1]);
        var mx=(pts[0][0]+pts[1][0])/2, my=(pts[0][1]+pts[1][1])/2, t=Math.round(d)+' m'; fctx.font='bold 13px serif';
        var tw=fctx.measureText(t).width; fctx.fillStyle='rgba(15,12,9,.85)'; fctx.fillRect(mx-tw/2-6,my-20,tw+12,18); fctx.fillStyle='#f0d98a'; fctx.fillText(t, mx-tw/2, my-7); } }
  }
  function drawScale(){ var m=sppm(), meters=niceNum(100/m), px=meters*m;
    scalebar.querySelector('.lbl').textContent=(meters>=1000?(meters/1000)+' km':meters+' m'); scalebar.querySelector('.bar').style.width=px+'px'; }

  function sizeMini(){ var maxW=170, maxH=120, a=W/H; var w=maxW, h=Math.round(maxW/a);
    if(h>maxH){ h=maxH; w=Math.round(maxH*a); } mini.width=w; mini.height=h; mini.style.width=w+'px'; mini.style.height=h+'px'; }
  function drawMini(){ var w=mini.width, h=mini.height; mctx.clearRect(0,0,w,h); if(base.complete&&base.naturalWidth) mctx.drawImage(base,0,0,w,h);
    var vw=wrap.clientWidth, vh=wrap.clientHeight, kx=w/W, ky=h/H;
    var vx=(-ox/s)*kx, vy=(-oy/s)*ky, vwid=(vw/s)*kx, vhei=(vh/s)*ky;
    mctx.fillStyle='rgba(216,178,90,.12)'; mctx.fillRect(vx,vy,vwid,vhei); mctx.strokeStyle='#f0d98a'; mctx.lineWidth=1.5; mctx.strokeRect(vx,vy,vwid,vhei); }

  function zoomAt(mx,my,factor){ var ns=clampScale(s*factor); if(ns===s) return;
    ox=mx-(mx-ox)*(ns/s); oy=my-(my-oy)*(ns/s); s=ns; if(clusterOn) rebuildItems(); render(); save(); }
  function zoomAtImg(ipx,ipy,ns){ ns=clampScale(ns); var vw=wrap.clientWidth, vh=wrap.clientHeight;
    ox=vw/2-ipx*ns; oy=vh/2-ipy*ns; s=ns; if(clusterOn) rebuildItems(); render(); save(); }

  function showTip(t,c,e){ tip.innerHTML=''; var a=document.createElement('div'); a.className='t'; a.textContent=t;
    var b=document.createElement('div'); b.className='c'; b.textContent=c; tip.appendChild(a); tip.appendChild(b); tip.style.display='block'; moveTip(e); }
  function moveTip(e){ var r=wrap.getBoundingClientRect(); tip.style.left=(e.clientX-r.left)+'px'; tip.style.top=(e.clientY-r.top)+'px'; }
  function hideTip(){ tip.style.display='none'; }

  function openCard(p, el){ var r=wrap.getBoundingClientRect(); var x=parseFloat(el.style.left)||r.width/2, y=parseFloat(el.style.top)||r.height/2;
    card.innerHTML='';
    var xdiv=document.createElement('div'); xdiv.className='x'; xdiv.innerHTML='&times;'; xdiv.onclick=closeCard; card.appendChild(xdiv);
    var hd=document.createElement('div'); hd.className='hd'; var im=document.createElement('img'); im.src='icons/icon_'+p.icon+'.png'; hd.appendChild(im);
    var tt=document.createElement('div'); var t1=document.createElement('div'); t1.className='tt'; t1.textContent=pinLabel(p);
    var k1=document.createElement('div'); k1.className='kk'; k1.textContent=kindName[p.key]||p.key; tt.appendChild(t1); tt.appendChild(k1); hd.appendChild(tt); card.appendChild(hd);
    var bd=document.createElement('div'); bd.className='bd';
    var co=document.createElement('div'); co.className='co'; co.textContent='X '+Math.round(p.x)+'   Z '+Math.round(p.z); bd.appendChild(co);
    var row=document.createElement('div'); row.className='row';
    var b1=document.createElement('button'); b1.textContent='Copy coords';
    b1.onclick=function(){ try{ navigator.clipboard.writeText(Math.round(p.x)+', '+Math.round(p.z)); }catch(_){} b1.textContent='Copied!'; setTimeout(function(){b1.textContent='Copy coords';},1000); };
    var b2=document.createElement('button'); b2.textContent='Zoom to'; b2.onclick=function(){ flyTo(p); };
    row.appendChild(b1); row.appendChild(b2); bd.appendChild(row); card.appendChild(bd); card.style.display='block';
    var cw=222, ch=card.offsetHeight||150;
    card.style.left=Math.max(6,Math.min(x+14, r.width-cw-6))+'px'; card.style.top=Math.max(6,Math.min(y-ch/2, r.height-ch-6))+'px'; }
  function closeCard(){ card.style.display='none'; }

  function flyTo(p){ var im=worldToImg(p.x,p.z); if(clusterOn){ clusterOn=false; setBtn('btncluster',false); } zoomAtImg(im[0],im[1], Math.max(s, 1.4)); highlight(p); }
  function highlight(p){ for(var i=0;i<items.length;i++){ if(items[i].type==='pin'&&items[i].pin===p){ var el=items[i].el;
    el.classList.add('hl'); (function(e){ setTimeout(function(){ e.classList.remove('hl'); },2400); })(el); break; } } }

  function fitPoints(pts){ if(!pts.length) return; var minx=1e9,miny=1e9,maxx=-1e9,maxy=-1e9;
    for(var i=0;i<pts.length;i++){ var im=worldToImg(pts[i][0],pts[i][1]); minx=Math.min(minx,im[0]); maxx=Math.max(maxx,im[0]); miny=Math.min(miny,im[1]); maxy=Math.max(maxy,im[1]); }
    var pad=90, vw=wrap.clientWidth, vh=wrap.clientHeight, bw=Math.max(1,maxx-minx), bh=Math.max(1,maxy-miny);
    var ns=clampScale(Math.min((vw-pad)/bw,(vh-pad)/bh)); if(pts.length===1) ns=Math.max(s,1.4);
    var cx=(minx+maxx)/2, cy=(miny+maxy)/2; s=ns; ox=vw/2-cx*ns; oy=vh/2-cy*ns; if(clusterOn) rebuildItems(); render(); save(); }

  function buildGroups(){ groupsBox.innerHTML='';
    for(var i=0;i<M.groups.length;i++){ (function(g){
      var row=document.createElement('div'); row.className='grp'; row._name=(kindName[g.key]||'').toLowerCase(); row._key=g.key;
      var cb=document.createElement('input'); cb.type='checkbox'; cb.className='cbx'; cb.checked=!hidden[g.key];
      cb.onchange=function(){ hidden[g.key]=!cb.checked; rebuildItems(); render(); save(); }; row._cb=cb;
      var im=document.createElement('img'); if(g.icon) im.src=g.icon;
      var nm=document.createElement('span'); nm.className='nm'; nm.textContent=kindName[g.key]; nm.title='Solo this kind'; nm.onclick=function(){ solo(g.key); };
      var ct=document.createElement('span'); ct.className='ct'; ct.textContent=g.count;
      var act=document.createElement('span'); act.className='act'; var bt=document.createElement('button'); bt.textContent='⤢'; bt.title='Zoom to all';
      bt.onclick=function(e){ e.stopPropagation(); fitPoints(M.pins.filter(function(p){return p.key===g.key;}).map(function(p){return [p.x,p.z];})); }; act.appendChild(bt);
      row.appendChild(cb); row.appendChild(im); row.appendChild(nm); row.appendChild(ct); row.appendChild(act); groupsBox.appendChild(row);
    })(M.groups[i]); }
    $('kindmini').textContent=M.groups.length+' kinds'; }
  function refreshChecks(){ var rows=groupsBox.querySelectorAll('.grp'); for(var i=0;i<rows.length;i++) rows[i]._cb.checked=!hidden[rows[i]._key]; }
  function solo(key){ var onlyThis=!hidden[key] && M.groups.every(function(g){ return g.key===key || hidden[g.key]; });
    for(var i=0;i<M.groups.length;i++){ var k=M.groups[i].key; hidden[k]= onlyThis? false : (k!==key); }
    refreshChecks(); rebuildItems(); render(); save(); }
  function setAll(on){ var rows=groupsBox.querySelectorAll('.grp'); for(var i=0;i<rows.length;i++){ if(rows[i].style.display==='none') continue; hidden[rows[i]._key]=!on; rows[i]._cb.checked=on; } rebuildItems(); render(); save(); }

  function buildPinList(){ pinlistBox.innerHTML=''; pinListRows=[];
    var sorted=M.pins.slice().sort(function(a,b){ return pinLabel(a).localeCompare(pinLabel(b)); });
    var frag=document.createDocumentFragment();
    for(var i=0;i<sorted.length;i++){ (function(p){
      var row=document.createElement('div'); row.className='prow'; row._t=pinLabel(p).toLowerCase();
      var im=document.createElement('img'); im.src='icons/icon_'+p.icon+'.png';
      var nm=document.createElement('span'); nm.className='nm'; nm.textContent=pinLabel(p);
      var co=document.createElement('span'); co.className='co'; co.textContent=Math.round(p.x)+','+Math.round(p.z);
      row.appendChild(im); row.appendChild(nm); row.appendChild(co); row.onclick=function(){ flyTo(p); }; frag.appendChild(row); pinListRows.push(row);
    })(sorted[i]); }
    pinlistBox.appendChild(frag); $('pinmini').textContent=M.pins.length+' pins'; }

  // pan / zoom
  var dragging=false,lastX=0,lastY=0,moved=false;
  wrap.addEventListener('pointerdown',function(e){ if(e.button!==0) return;
    if(e.target.closest && (e.target.closest('#zoom')||e.target.closest('#maptools')||e.target.closest('#mini')||e.target.closest('#card'))) return;
    if(measureOn){ handleMeasureClick(e); return; } closeCard();
    dragging=true; moved=false; lastX=e.clientX; lastY=e.clientY; wrap.classList.add('drag'); wrap.setPointerCapture(e.pointerId); });
  wrap.addEventListener('pointermove',function(e){ var r=wrap.getBoundingClientRect(); var w=screenToWorld(e.clientX-r.left, e.clientY-r.top); hud.textContent=Math.round(w[0])+', '+Math.round(w[1]);
    if(measureOn && measurePts.length===1){ measureLive=[e.clientX-r.left, e.clientY-r.top]; drawFx(); }
    if(!dragging) return; var dx=e.clientX-lastX, dy=e.clientY-lastY; if(Math.abs(dx)+Math.abs(dy)>2) moved=true; ox+=dx; oy+=dy; lastX=e.clientX; lastY=e.clientY; render(); });
  function endDrag(){ if(dragging) save(); dragging=false; wrap.classList.remove('drag'); }
  wrap.addEventListener('pointerup',endDrag); wrap.addEventListener('pointercancel',endDrag);
  wrap.addEventListener('mouseleave',function(){ hud.textContent='—'; });
  wrap.addEventListener('wheel',function(e){ e.preventDefault(); var r=wrap.getBoundingClientRect(); zoomAt(e.clientX-r.left, e.clientY-r.top, e.deltaY<0?1.15:1/1.15); }, {passive:false});

  function handleMeasureClick(e){ var r=wrap.getBoundingClientRect(); var w=screenToWorld(e.clientX-r.left,e.clientY-r.top);
    if(measurePts.length>=2) measurePts=[]; measurePts.push(w); measureLive=null; drawFx(); }

  mini.addEventListener('click',function(e){ var r=mini.getBoundingClientRect(); var fxr=(e.clientX-r.left)/mini.width, fyr=(e.clientY-r.top)/mini.height;
    var vw=wrap.clientWidth, vh=wrap.clientHeight; ox=vw/2-(fxr*W)*s; oy=vh/2-(fyr*H)*s; render(); save(); });

  function setBtn(id,on){ $(id).classList.toggle('on',!!on); }
  $('btnfit').onclick=fit; $('zfit').onclick=fit;
  $('zin').onclick=function(){ zoomAt(wrap.clientWidth/2,wrap.clientHeight/2,1.3); };
  $('zout').onclick=function(){ zoomAt(wrap.clientWidth/2,wrap.clientHeight/2,1/1.3); };
  $('btnlabels').onclick=function(){ showLabels=!showLabels; setBtn('btnlabels',showLabels); positionItems(); save(); };
  $('btngrid').onclick=function(){ gridOn=!gridOn; setBtn('btngrid',gridOn); drawFx(); save(); };
  $('btnmini').onclick=function(){ miniOn=!miniOn; setBtn('btnmini',miniOn); mini.style.display=miniOn?'block':'none'; if(miniOn){ sizeMini(); drawMini(); } save(); };
  $('btncluster').onclick=function(){ clusterOn=!clusterOn; setBtn('btncluster',clusterOn); rebuildItems(); render(); save(); };
  $('btnmeasure').onclick=function(){ measureOn=!measureOn; setBtn('btnmeasure',measureOn); wrap.classList.toggle('measure',measureOn); if(!measureOn){ measurePts=[]; measureLive=null; drawFx(); } };
  $('allon').onclick=function(){ setAll(true); }; $('alloff').onclick=function(){ setAll(false); };
  $('ksearch').addEventListener('input',function(){ var q=this.value.toLowerCase().trim(); var rows=groupsBox.querySelectorAll('.grp'); for(var i=0;i<rows.length;i++) rows[i].style.display=(!q||rows[i]._name.indexOf(q)>=0)?'':'none'; });
  $('psearch').addEventListener('input',function(){ var q=this.value.toLowerCase().trim(); for(var i=0;i<pinListRows.length;i++) pinListRows[i].style.display=(!q||pinListRows[i]._t.indexOf(q)>=0)?'':'none'; });

  var heads=document.querySelectorAll('.sechead'); for(var h=0;h<heads.length;h++) heads[h].addEventListener('click',function(){ this.parentNode.classList.toggle('collapsed'); });

  window.addEventListener('keydown',function(e){ if(e.target.tagName==='INPUT') return;
    if(e.key==='f'||e.key==='F') fit(); else if(e.key==='+'||e.key==='=') zoomAt(wrap.clientWidth/2,wrap.clientHeight/2,1.3);
    else if(e.key==='-'||e.key==='_') zoomAt(wrap.clientWidth/2,wrap.clientHeight/2,1/1.3);
    else if(e.key==='l'||e.key==='L') $('btnlabels').click(); else if(e.key==='g'||e.key==='G') $('btngrid').click();
    else if(e.key==='Escape'){ closeCard(); if(measureOn) $('btnmeasure').click(); } });

  var saveT=null;
  function save(){ if(saveT) return; saveT=setTimeout(function(){ saveT=null; try{ localStorage.setItem(LSKEY,
    JSON.stringify({s:s,ox:ox,oy:oy,hidden:hidden,showLabels:showLabels,gridOn:gridOn,clusterOn:clusterOn,miniOn:miniOn})); }catch(_){} },250); }
  function restore(){ try{ var v=JSON.parse(localStorage.getItem(LSKEY)); if(!v) return false;
    s=v.s||1; ox=v.ox||0; oy=v.oy||0; hidden=v.hidden||{}; showLabels=!!v.showLabels; gridOn=!!v.gridOn; clusterOn=v.clusterOn!==false; miniOn=!!v.miniOn; return true; }catch(_){ return false; } }

  function boot(){ resizeFx(); sizeMini(); var had=restore();
    setBtn('btnlabels',showLabels); setBtn('btngrid',gridOn); setBtn('btncluster',clusterOn); setBtn('btnmini',miniOn); mini.style.display=miniOn?'block':'none';
    buildGroups(); buildPinList(); refreshChecks(); rebuildItems(); if(had) render(); else fit(); if(miniOn) drawMini(); $('load').style.display='none'; }
  window.addEventListener('resize',function(){ resizeFx(); render(); });
  wrap.addEventListener('click',function(e){ if((e.target===wrap||e.target===stage||e.target===base||e.target===fx) && !moved) closeCard(); });

  if(base.complete && base.naturalWidth) boot(); else { base.addEventListener('load',boot); base.addEventListener('error',boot); }
  setTimeout(function(){ if($('load').style.display!=='none') boot(); }, 4000);
})();
</script>
</body>
</html>
";
    }
}
