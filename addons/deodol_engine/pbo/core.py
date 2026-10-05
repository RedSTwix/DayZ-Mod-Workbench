from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess

PBO_VERS = 0x56657273

PBO_CPRS = 0x43707273

PBO_STORED = 0

class PBOError(Exception):
    pass

def _pbo_read_cstr(buf, pos):
    try:
        end=buf.index(b'\0',pos)
    except ValueError:
        raise PBOError(f'PBO unterminated string at offset {pos}')
    return buf[pos:end],end+1

def pbo_parse(path):
    """Parse a PBO header byte-for-byte without decoding/mutating filenames."""
    path=Path(path)
    buf=path.read_bytes()
    entries=[]; props=[]; pos=0
    while True:
        name,pos=_pbo_read_cstr(buf,pos)
        if pos+20>len(buf):
            raise PBOError('Unexpected EOF inside PBO header')
        method,orig,reserved,timestamp,dsz=struct.unpack_from('<5I',buf,pos); pos+=20
        if name==b'' and method==orig==reserved==timestamp==dsz==0:
            break
        if method==PBO_VERS:
            p=[]
            while True:
                k,pos=_pbo_read_cstr(buf,pos)
                if not k: break
                v,pos=_pbo_read_cstr(buf,pos)
                p.append((k,v))
            props.extend(p)
            entries.append(dict(kind='version',properties=p))
        else:
            entries.append(dict(kind='file',name=name,method=method,orig=orig,
                                reserved=reserved,timestamp=timestamp,dsz=dsz))
    header_end=pos
    off=header_end
    for e in entries:
        if e['kind']=='file':
            e['data_off']=off
            off += e['dsz']
            if off>len(buf):
                raise PBOError(f'PBO payload exceeds file size for {e["name"]!r}')
    data_end=off
    archive_sha_ok=None
    if len(buf)-data_end>=21 and buf[data_end]==0:
        archive_sha_ok=(hashlib.sha1(buf[:data_end]).digest()==buf[data_end+1:data_end+21])
    return dict(path=path,buf=buf,entries=entries,properties=props,
                header_end=header_end,data_end=data_end,archive_sha_ok=archive_sha_ok)

def pbo_lzss_decompress(data, expected_size):
    """BI/DayZ PBO Cprs LZSS decompressor with mandatory additive checksum.

    This is the relative-backreference variant documented by Bohemia:
      rpos = output_len - b1 - 256*(b2>>4)
      rlen = (b2 & 0x0F) + 3
    Negative references expand as ASCII spaces. The final 4 bytes are the
    unsigned additive checksum of the expanded payload.
    """
    if expected_size<0:
        raise PBOError('Negative PBO original size')
    if len(data)<4:
        raise PBOError('Compressed PBO block too small for checksum')
    packed_end=len(data)-4
    read_checksum=struct.unpack_from('<I',data,packed_end)[0]
    out=bytearray(); pos=0
    while pos<packed_end and len(out)<expected_size:
        flags=data[pos]; pos+=1
        bit=1
        while bit<256 and len(out)<expected_size:
            if pos>=packed_end:
                break
            if flags & bit:
                out.append(data[pos]); pos+=1
            else:
                if pos+1>=packed_end:
                    raise PBOError('Compressed PBO block ended inside LZSS pointer')
                b1=data[pos]; b2=data[pos+1]; pos+=2
                rpos=len(out)-b1-256*(b2>>4)
                rlen=(b2&0x0F)+3
                while rlen>0 and len(out)<expected_size:
                    if rpos<0:
                        value=0x20
                    elif rpos<len(out):
                        value=out[rpos]
                    else:
                        raise PBOError(f'Invalid LZSS back-reference {rpos}/{len(out)}')
                    out.append(value); rpos+=1; rlen-=1
            bit <<= 1
    if len(out)!=expected_size:
        raise PBOError(f'Cprs expanded to {len(out)} bytes, expected {expected_size}')
    calculated=sum(out)&0xffffffff
    if calculated!=read_checksum:
        raise PBOError(f'Cprs checksum mismatch: calculated={calculated:#x}, stored={read_checksum:#x}')
    return bytes(out)

def pbo_entry_data(archive,e):
    if e['kind']!='file': return b''
    b=archive['buf'][e['data_off']:e['data_off']+e['dsz']]
    if e['method']==PBO_STORED:
        return b
    if e['method']==PBO_CPRS:
        return pbo_lzss_decompress(b,e['orig'])
    raise PBOError(f'Unsupported PBO packing method {e["method"]:#x}')

def _pbo_decode_name(raw):
    # Obfuscators commonly use valid UTF-8 Cyrillic/symbols.  Keep replacement
    # only as a last resort so include strings can still be matched byte-for-byte
    # after UTF-8 decoding.
    try: return raw.decode('utf-8')
    except UnicodeDecodeError: return raw.decode('latin1',errors='replace')

def _pbo_decode_text(data):
    try: return data.decode('utf-8-sig')
    except UnicodeDecodeError: return data.decode('latin1',errors='replace')

def _pbo_is_pure_comment_or_empty(text):
    s=text.strip()
    if not s: return True
    # MPG/other packers inject thousands of one-comment bridge/decoy files.
    return re.fullmatch(r'/\*.*?\*/',s,flags=re.S) is not None

def _pbo_has_substantive_script(text):
    if _pbo_is_pure_comment_or_empty(text): return False
    # Strip comments and unresolved includes/preprocessor whitespace.  A file
    # with only packer scaffolding is not useful recovered source.
    core=re.sub(r'/\*.*?\*/','',text,flags=re.S)
    core=re.sub(r'//[^\r\n]*','',core)
    core=re.sub(r'^\s*#include[^\r\n]*','',core,flags=re.M|re.I)
    core=re.sub(r'\s+','',core)
    return bool(core)

def _pbo_safe_rel(name):
    name=name.replace('/','\\').lstrip('\\')
    parts=[]
    for part in name.split('\\'):
        if not part or part in ('.','..'): continue
        clean=''.join(ch if (32<=ord(ch)<127 and ch not in '<>:"|?*') else '_' for ch in part)
        clean=clean.strip(' .') or '_'
        parts.append(clean)
    return Path(*parts) if parts else Path('_unnamed')

def _pbo_raw_name_suspicious(raw):
    """Heuristic only for deciding whether a module tree is packer-obfuscated.

    Never used to discard a file by itself.  Obfuscated PBO packers commonly
    inject non-ASCII/control/wildcard bytes into header filenames; ordinary
    DayZ source paths are printable ASCII/UTF-8 without Windows-illegal chars.
    """
    if not raw:
        return False
    return any(b < 32 or b >= 128 or b in (ord('*'),ord('?'),ord('<'),ord('>'),ord('|')) for b in raw)

def _pbo_rap_ascii_strings(data):
    """Return printable NUL-terminated strings from a rapified config.bin.

    This is intentionally not a config decompiler.  We only use strings as
    structural hints to recover CfgMods files[] module directories when a PBO
    ships config.bin but no usable config.cpp.
    """
    if not data.startswith(b'\x00raP'):
        return []
    vals=[]
    for m in re.finditer(rb'[\x20-\x7e]{2,}\x00',data):
        try:
            vals.append(m.group(0)[:-1].decode('ascii'))
        except UnicodeDecodeError:
            pass
    return vals

def _pbo_module_dirs_from_rap(data,prefix=''):
    out=[]
    for value in _pbo_rap_ascii_strings(data):
        q=value.replace('/','\\').strip('\\')
        # DayZ script modules conventionally terminate at one of these roots.
        m=re.search(r'(?i)(?:^|\\)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',q)
        if not m:
            continue
        # Keep the path relative to this PBO prefix, matching decoded header names.
        rel=m.group(1)
        if rel.lower() not in [x.lower() for x in out]:
            out.append(rel)
    return out

def _strip_mpg_guard(text):
    """Remove an MPG_PACKER_DEF wrapper only when its else branch is empty."""
    pat=re.compile(
        r'^\s*#ifndef\s+(MPG_PACKER_DEF_[A-Za-z0-9_]+)\s*(.*?)\s*#else\s*(.*?)\s*#endif\s*$',
        re.S|re.I
    )
    m=pat.match(text)
    if not m: return text
    else_part=re.sub(r'/\*.*?\*/|//[^\r\n]*','',m.group(3),flags=re.S)
    if else_part.strip(): return text
    return m.group(2).strip()+"\n"

def _collapse_blank_lines(text):
    text=text.replace('\r\n','\n').replace('\r','\n')
    text=re.sub(r'[ \t]+\n','\n',text)
    text=re.sub(r'\n{4,}','\n\n',text)
    return text.strip()+"\n"
