"""Read-only PE/import and SHA256 audit; writes evidence only to the given output."""
import argparse, hashlib, json, pathlib, struct

def pe_info(path):
    b = path.read_bytes()
    if b[:2] != b'MZ':
        return None
    u16 = lambda p: struct.unpack_from('<H', b, p)[0]
    u32 = lambda p: struct.unpack_from('<I', b, p)[0]
    p = u32(60)
    if b[p:p+4] != b'PE\0\0':
        return None
    machine, count, opt_size = u16(p+4), u16(p+6), u16(p+20)
    opt = p+24
    dirs = opt+(112 if u16(opt) == 0x20b else 96)
    sections = []
    for n in range(count):
        s = opt+opt_size+n*40
        sections.append((u32(s+12), max(u32(s+8),u32(s+16)), u32(s+20)))
    def offset(rva):
        for va, size, raw in sections:
            if va <= rva < va+size:
                return rva-va+raw
        return rva
    def string(rva):
        start=offset(rva)
        return b[start:b.index(b'\0', start)].decode('ascii')
    imports=[]
    if u32(dirs+8):
        s=offset(u32(dirs+8))
        while any(b[s:s+20]):
            imports.append(string(u32(s+12)))
            s+=20
    delayed=[]
    if u32(dirs+13*8):
        s=offset(u32(dirs+13*8))
        while any(b[s:s+32]):
            name=u32(s+4)
            if not u32(s)&1:
                name-=u32(opt+28)
            delayed.append(string(name))
            s+=32
    return dict(machine=hex(machine), imports=imports, delay_imports=delayed,
                managed=bool(u32(dirs+14*8)))

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('root', type=pathlib.Path)
    ap.add_argument('output', type=pathlib.Path)
    args=ap.parse_args()
    rows=[]
    for p in sorted(args.root.rglob('*')):
        if not p.is_file(): continue
        row=dict(path=p.relative_to(args.root).as_posix(), bytes=p.stat().st_size,
                 sha256=hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper())
        if p.suffix.lower() in ('.exe','.dll'):
            row['pe']=pe_info(p)
        rows.append(row)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(rows,indent=2),encoding='utf-8')
    print('AUDITED_FILES='+str(len(rows)))
    for row in rows:
        pe=row.get('pe')
        if pe and not pe['managed']:
            print(row['path'],pe['machine'],','.join(pe['imports']+pe['delay_imports']))

if __name__=='__main__': main()
