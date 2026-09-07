"""Isolated Chromium presentation checks; never connects to the native bridge."""
import argparse, functools, http.server, json, pathlib, threading
from playwright.sync_api import sync_playwright

parser=argparse.ArgumentParser()
parser.add_argument('dist',type=pathlib.Path)
parser.add_argument('out',type=pathlib.Path)
args=parser.parse_args()
args.out.mkdir(parents=True,exist_ok=True)
class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args): pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Quiet,directory=str(args.dist.resolve())))
threading.Thread(target=server.serve_forever,daemon=True).start()
rows=[]
with sync_playwright() as pw:
    browser=pw.chromium.launch(headless=True)
    for physical_w,physical_h in [(1280,720),(1366,768),(1600,900),(1680,1050),(1920,1080),(2560,1440),(3840,2160)]:
        for dpr in [1,1.25,1.5,1.75,2]:
            w,h=round(physical_w/dpr),round(physical_h/dpr)
            context=browser.new_context(viewport={'width':w,'height':h},device_scale_factor=dpr)
            context.add_init_script('''(() => {
              const listeners = new Set();
              window.chrome = window.chrome || {};
              window.chrome.webview = {
                addEventListener: (_, f) => listeners.add(f),
                removeEventListener: (_, f) => listeners.delete(f),
                postMessage: m => setTimeout(() => listeners.forEach(f => f({data:{
                  type:'commandResult',id:m.id,success:true,payload:{}}})),0)
              };
            })()''')
            page=context.new_page()
            page.goto('http://127.0.0.1:'+str(server.server_port))
            page.wait_for_selector('.sidebar nav button')
            for index,name in [(0,'Dashboard'),(1,'Connection-USB'),(1,'Connection-WFD'),(2,'Statistics'),(3,'Logs'),(4,'Settings'),(5,'About')]:
                page.locator('.sidebar nav button').nth(index).click()
                if name=='Connection-WFD':
                    page.locator('.transport-selector-panel .transport-card').nth(1).click()
                page.wait_for_timeout(260)
                result=page.evaluate('''() => {
                  const main=document.querySelector('.main-content');
                  const bad=[...document.querySelectorAll('button,input,select,.panel,.hero-card,.metric-card')].filter(e=>{
                    const r=e.getBoundingClientRect(),s=getComputedStyle(e);
                    return r.width>0&&r.height>0&&s.visibility!=='hidden'&&(r.left < -1||r.right>innerWidth+1);
                  }).map(e=>({tag:e.tagName,cls:e.className,text:e.textContent.slice(0,60)}));
                  return {width:innerWidth,height:innerHeight,dpr:devicePixelRatio,
                    overflow:document.documentElement.scrollWidth>innerWidth+1||main.scrollWidth>main.clientWidth+1,
                    bad,sidebar:getComputedStyle(document.querySelector('.app-shell')).gridTemplateColumns};
                }''')
                result.update(page=name,physical=[physical_w,physical_h])
                rows.append(result)
                if dpr==1 and physical_w in (1280,1680) and name in ('Dashboard','Connection-WFD','Settings'):
                    page.screenshot(path=str(args.out/f'{physical_w}-{name}.png'))
            context.close()
    browser.close()
server.shutdown()
(args.out/'matrix.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
failed=[r for r in rows if r['overflow'] or r['bad']]
print(json.dumps({'total':len(rows),'failed':len(failed),'examples':failed[:8]}))
