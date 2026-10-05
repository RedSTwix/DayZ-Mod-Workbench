from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from ..version import SCRIPT_VERSION
from .material_defs import PIXEL_SHADER_NAMES, VERTEX_SHADER_NAMES, UV_SOURCE_NAMES, TEXTURE_FILTER_NAMES

_RAP_REPLACEMENT = b'\xef\xbf\xbd'

_RV_TEXT_TOKEN = re.compile(
    r'\s*(?:(//[^\n]*|/\*.*?\*/)|("(?:[^"\\]|\\.)*")|'
    r'([-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?)|'
    r'([A-Za-z_][A-Za-z0-9_]*)|(\[\]|\+=|[{}\[\]();,:=]))', re.S)

def _fmt_rv(v):
    if isinstance(v,float):
        if abs(v)<1e-8: v=0.0
        return f'{v:.9g}'
    return str(v)

def _fmt_vec4(v):
    return '{'+','.join(_fmt_rv(float(x)) for x in v)+'}'

def _fmt_vec3(v):
    return '{'+','.join(_fmt_rv(float(x)) for x in v)+'}'

def _rv_quote(s):
    return '"'+str(s).replace('"','\\"')+'"'

def _shader_name(table,value,prefix):
    if 0<=int(value)<len(table): return table[int(value)]
    return f'{prefix}{int(value)}'

def render_embedded_rvmat(mat):
    """Render an editable RVMAT from ODOL EmbeddedMaterial semantic data."""
    lines=[
        '// AUTO-RECOVERED from ODOL EmbeddedMaterial by '+SCRIPT_VERSION,
        '// Runtime-only compiled fields are preserved in the adjacent .embedded.json sidecar.',
        'ambient[]='+_fmt_vec4(mat['ambient'])+';',
        'diffuse[]='+_fmt_vec4(mat['diffuse'])+';',
        'forcedDiffuse[]='+_fmt_vec4(mat['forcedDiffuse'])+';',
        'emmisive[]='+_fmt_vec4(mat['emissive'])+';',
        'specular[]='+_fmt_vec4(mat['specular'])+';',
        'specularPower='+_fmt_rv(mat['specularPower'])+';',
        'PixelShaderID='+_rv_quote(_shader_name(PIXEL_SHADER_NAMES,mat['pixelShader'],'PixelShader_'))+';',
        'VertexShaderID='+_rv_quote(_shader_name(VERTEX_SHADER_NAMES,mat['vertexShader'],'VertexShader_'))+';',
    ]
    if mat.get('surfaceFile'):
        lines.append('surfaceInfo='+_rv_quote(mat['surfaceFile'])+';')

    ti=mat.get('stageTI')
    if ti and ti.get('texture'):
        lines += ['class StageTI','{','\ttexture='+_rv_quote(ti['texture'])+';','};']

    textures=mat.get('stageTextures') or []
    transforms=mat.get('stageTransforms') or []
    # DayZ EmbeddedMaterial carries compiler/runtime helper slots in addition to
    # source RVMAT stages.  Real source stages have stageID 1..N; stageID=0
    # records are generated/dummy slots and must not be emitted.  The transform
    # table uses the same stageID index (confirmed against intact textual RVMATs).
    for sid,tex in enumerate(textures):
        if sid==0:
            continue  # compiler dummy stage
        texture=tex.get('texture','')
        # DayZ compiler appends one or more runtime fallback DT stages with a
        # Point filter/stageID 0 after the source texgen table. They do not
        # exist in the editable RVMAT and must not be emitted.
        runtime_filler=(int(tex.get('filter',0))==0 and int(tex.get('stageID',0))==0 and
                        sid>=len(transforms) and 'color(1,1,1,1,dt)' in texture.lower())
        if runtime_filler:
            continue
        # Keep empty source stages when the compiled stage explicitly maps to
        # the same stage/texgen index; some emissive materials intentionally
        # use empty slots. Direct-layout recovery is gated separately below.
        if not texture and int(tex.get('stageID',0))!=sid:
            continue
        lines += [f'class Stage{sid}','{', '\ttexture='+_rv_quote(texture)+';']
        tr=transforms[sid] if 0<=sid<len(transforms) else None
        if tr is not None:
            uv=UV_SOURCE_NAMES.get(int(tr.get('uvSource',1)),str(int(tr.get('uvSource',1))))
            lines += [
                '\tuvSource='+_rv_quote(uv)+';',
                '\tclass uvTransform','\t{',
                '\t\taside[]='+_fmt_vec3(tr['aside'])+';',
                '\t\tup[]='+_fmt_vec3(tr['up'])+';',
                '\t\tdir[]='+_fmt_vec3(tr['dir'])+';',
                '\t\tpos[]='+_fmt_vec3(tr['pos'])+';',
                '\t};'
            ]
        lines.append('};')
    return '\n'.join(lines)+'\n'

def _utf8_replacement_roundtrip(data):
    return data.decode('utf-8','replace').encode('utf-8')

def _collapse_replacement_markers(data):
    out=bytearray(); wild=set(); i=0
    while i<len(data):
        if data[i:i+3]==_RAP_REPLACEMENT:
            wild.add(len(out)); out.append(0); i+=3
        else:
            out.append(data[i]); i+=1
    return bytes(out),wild

def _rap_cstr(buf,pos):
    end=buf.find(b'\0',pos)
    if end<0: raise ValueError('unterminated RaP string')
    return buf[pos:end].decode('utf-8','replace'),end+1

def _rap_compint(buf,pos):
    value=0; shift=0
    for _ in range(5):
        if pos>=len(buf): raise ValueError('truncated RaP compressed integer')
        b=buf[pos]; pos+=1
        value|=(b&0x7f)<<shift
        if not (b&0x80): return value,pos
        shift+=7
    raise ValueError('invalid RaP compressed integer')

def _rap_write_compint(value):
    out=bytearray(); value=int(value)
    while True:
        b=value&0x7f; value>>=7
        if value: out.append(b|0x80)
        else: out.append(b); return bytes(out)

def _rap_array_collapsed(buf,pos,wild):
    n,pos=_rap_compint(buf,pos); values=[]
    for _ in range(n):
        if pos in wild: raise ValueError(f'unknown RaP array element type at {pos}')
        typ=buf[pos]; pos+=1
        if typ==0:
            v,pos=_rap_cstr(buf,pos); values.append(('string',v))
        elif typ in (1,2):
            if pos+4>len(buf): raise ValueError('truncated RaP numeric array element')
            if any(i in wild for i in range(pos,pos+4)):
                raise ValueError(f'unknown RaP numeric bytes at {pos}')
            raw=buf[pos:pos+4]; pos+=4
            values.append(('float',struct.unpack('<f',raw)[0]) if typ==1 else ('int',struct.unpack('<i',raw)[0]))
        elif typ==3:
            v,pos=_rap_array_collapsed(buf,pos,wild); values.append(('array',v))
        else:
            raise ValueError(f'unsupported RaP array type {typ} at {pos-1}')
    return values,pos

def _rap_props_collapsed(buf,pos,wild,count):
    props=[]; children=[]
    for _ in range(count):
        if pos in wild: raise ValueError(f'unknown RaP property type at {pos}')
        typ=buf[pos]; pos+=1
        if typ==0:
            name,pos=_rap_cstr(buf,pos)
            if pos+4>len(buf): raise ValueError('truncated RaP class offset')
            pos+=4 # absolute class offset is intentionally ignored; sequential layout is authoritative
            rec={'kind':'class','name':name,'child':None}; props.append(rec); children.append(rec)
        elif typ==1:
            if pos in wild: raise ValueError(f'unknown RaP value subtype at {pos}')
            subtype=buf[pos]; pos+=1
            name,pos=_rap_cstr(buf,pos)
            if subtype==0:
                value,pos=_rap_cstr(buf,pos); value=('string',value)
            elif subtype in (1,2):
                if pos+4>len(buf): raise ValueError('truncated RaP scalar')
                if any(i in wild for i in range(pos,pos+4)):
                    raise ValueError(f'unknown RaP scalar bytes at {pos}')
                raw=buf[pos:pos+4]; pos+=4
                value=('float',struct.unpack('<f',raw)[0]) if subtype==1 else ('int',struct.unpack('<i',raw)[0])
            else:
                raise ValueError(f'unsupported RaP scalar subtype {subtype} for {name}')
            props.append({'kind':'var','name':name,'value':value})
        elif typ==2:
            name,pos=_rap_cstr(buf,pos); value,pos=_rap_array_collapsed(buf,pos,wild)
            props.append({'kind':'array','name':name,'value':value})
        elif typ==3:
            name,pos=_rap_cstr(buf,pos); props.append({'kind':'external','name':name})
        elif typ==4:
            name,pos=_rap_cstr(buf,pos); props.append({'kind':'delete','name':name})
        elif typ==5:
            if pos+4>len(buf): raise ValueError('truncated RaP array expansion')
            pos+=4; name,pos=_rap_cstr(buf,pos); value,pos=_rap_array_collapsed(buf,pos,wild)
            props.append({'kind':'array+','name':name,'value':value})
        else:
            raise ValueError(f'unsupported RaP property type {typ} at {pos-1}')
    return props,children,pos

def _rap_node_collapsed(buf,start,wild,name=None):
    pos=start; parent,pos=_rap_cstr(buf,pos); count,pos=_rap_compint(buf,pos)
    props,children,pos=_rap_props_collapsed(buf,pos,wild,count)
    if pos+4>len(buf): raise ValueError('truncated RaP class trailer')
    pos+=4 # end-of-class absolute offset; regenerated during serialization
    for child_rec in children:
        child=_rap_node_collapsed(buf,pos,wild,child_rec['name'])
        child_rec['child']=child; pos=child['end']
    return {'name':name,'parent':parent,'props':props,'start':start,'end':pos}

def _rap_encode_cstr(value):
    return str(value).encode('utf-8')+b'\0'

def _rap_array_bytes(values):
    out=bytearray(_rap_write_compint(len(values)))
    for typ,value in values:
        if typ=='string': out.append(0); out+=_rap_encode_cstr(value)
        elif typ=='float': out.append(1); out+=struct.pack('<f',float(value))
        elif typ=='int': out.append(2); out+=struct.pack('<i',int(value))
        elif typ=='array': out.append(3); out+=_rap_array_bytes(value)
        else: raise ValueError(f'unsupported RaP array value {typ}')
    return bytes(out)

def _rap_node_bytes(node,start_abs):
    out=bytearray(_rap_encode_cstr(node.get('parent','')))
    props=node.get('props') or []
    out+=_rap_write_compint(len(props)); patches=[]
    for prop in props:
        kind=prop['kind']; name=prop['name']
        if kind=='class':
            out.append(0); out+=_rap_encode_cstr(name); patch=len(out); out+=b'\0'*4
            patches.append((patch,prop))
        elif kind=='var':
            typ,value=prop['value']; subtype={'string':0,'float':1,'int':2}[typ]
            out+=bytes((1,subtype)); out+=_rap_encode_cstr(name)
            if typ=='string': out+=_rap_encode_cstr(value)
            elif typ=='float': out+=struct.pack('<f',float(value))
            else: out+=struct.pack('<i',int(value))
        elif kind in ('array','array+'):
            if kind=='array+': out+=b'\x05\x01\0\0\0'
            else: out.append(2)
            out+=_rap_encode_cstr(name); out+=_rap_array_bytes(prop['value'])
        elif kind=='external': out.append(3); out+=_rap_encode_cstr(name)
        elif kind=='delete': out.append(4); out+=_rap_encode_cstr(name)
        else: raise ValueError(f'unsupported RaP property {kind}')
    end_patch=len(out); out+=b'\0'*4
    child_start=start_abs+len(out)
    for patch,prop in patches:
        out[patch:patch+4]=struct.pack('<I',child_start)
        child=_rap_node_bytes(prop['child'],child_start)
        out+=child; child_start+=len(child)
    out[end_patch:end_patch+4]=struct.pack('<I',child_start)
    return bytes(out)

def _rap_document_bytes(root):
    body=_rap_node_bytes(root,16); enum_offset=16+len(body)
    return b'\0raP'+b'\0\0\0\0\x08\0\0\0'+struct.pack('<I',enum_offset)+body+b'\0\0\0\0'

def _as_float_literal(text):
    """Keep a textual token lexically typed as float, not int.

    RaP stores numeric subtypes explicitly.  A float32 whose shortest decimal is
    an integer-looking token (for example 1.0 -> "1" or 0.0 -> "0") must retain
    a decimal/exponent marker in editable RVMAT source; otherwise our text parser
    and CfgConvert can legitimately interpret the token as an integer subtype.
    """
    text=str(text)
    return text if any(c in text.lower() for c in '.e') else text+'.0'


def _shortest_f32(value):
    target=struct.pack('<f',float(value))
    f=struct.unpack('<f',target)[0]
    if f==0.0:
        # Preserve signed zero when present; both spellings remain explicit floats.
        return '-0.0' if (target[3] & 0x80) else '0.0'
    if math.isfinite(f):
        for digits in range(0,10):
            text=f'{f:.{digits}f}'
            try:
                if struct.pack('<f',float(text))==target:
                    return _as_float_literal(text)
            except Exception: pass
    return _as_float_literal(f'{f:.9g}')

def _rap_value_text(item):
    typ,value=item
    if typ=='string': return _rv_quote(value)
    if typ=='int': return str(int(value))
    if typ=='float': return _shortest_f32(value)
    if typ=='array': return '{'+','.join(_rap_value_text(v) for v in value)+'}'
    raise ValueError(typ)

def _render_rap_node(node,indent=0,root=False):
    tab='\t'*indent; lines=[]
    for prop in node.get('props') or []:
        kind=prop['kind']; name=prop['name']
        if kind=='class':
            child=prop['child']; parent=child.get('parent') or ''
            suffix=(' : '+parent) if parent else ''
            lines += [tab+f'class {name}{suffix}',tab+'{']
            lines += _render_rap_node(child,indent+1)
            lines += [tab+'};']
        elif kind=='var':
            lines.append(tab+name+'='+_rap_value_text(prop['value'])+';')
        elif kind in ('array','array+'):
            op='[]+=' if kind=='array+' else '[]='
            lines.append(tab+name+op+'{'+','.join(_rap_value_text(v) for v in prop['value'])+'};')
        elif kind=='external': lines.append(tab+'class '+name+';')
        elif kind=='delete': lines.append(tab+'delete '+name+';')
    return lines

def _render_forensic_rvmat(root,proof):
    head=[
        '// FORENSICALLY RECOVERED by '+SCRIPT_VERSION,
        '// Proof: reconstructed pre-corruption RaP reproduces the PBO payload byte-for-byte',
        '// after the observed UTF-8 replacement transformation (invalid bytes -> U+FFFD).',
        '// Proof SHA-1 reconstructed RaP: '+proof['reconstructed_sha1'],
        '// Proof SHA-1 damaged payload: '+proof['damaged_sha1'],
        ''
    ]
    return '\n'.join(head+_render_rap_node(root))+'\n'

def _inverse_invalid_prefix(target):
    """Return 4-byte float candidates whose UTF-8 replacement output is target.

    The damaged RVMATs seen in DayZ packs use one U+FFFD marker followed by
    unchanged ASCII bytes.  With a 4-byte float this leaves at most two unknown
    source bytes for the cases we accept automatically, so exhaustive inversion
    is small and deterministic.
    """
    if len(target)==4 and _utf8_replacement_roundtrip(target)==target:
        return [target]
    if not target.startswith(_RAP_REPLACEMENT): return []
    rest=target[3:]; unknown=4-len(rest)
    if unknown<1 or unknown>2: return []
    results=[]
    if unknown==1:
        for a in range(256):
            raw=bytes((a,))+rest
            if _utf8_replacement_roundtrip(raw)==target: results.append(raw)
    else:
        for a in range(256):
            for b in range(256):
                raw=bytes((a,b))+rest
                if _utf8_replacement_roundtrip(raw)==target: results.append(raw)
    return results

def _f32_simplicity(raw):
    f=struct.unpack('<f',raw)[0]
    if not math.isfinite(f) or abs(f)>1000000: return None
    text=_shortest_f32(f)
    # RVMAT colors are normally concise decimals; prefer shortest exact spelling.
    decimals=len(text.split('.',1)[1]) if '.' in text and 'e' not in text.lower() else 50
    return (decimals,len(text),abs(f),text,f)

def _recover_corrupted_color_array(segment):
    """Recover a common 4-component RVMAT color array from UTF-8-damaged RaP.

    Expected source representation is three float elements followed by an int.
    This is the representation CfgConvert uses when the alpha component was an
    integer literal (the SharpAxe chrome material is one such specimen).
    """
    if not segment or segment[0]!=4: return None
    data=segment; end=len(data)
    # Last item is encoded as type=2 + i32 and must survive intact for automatic proof.
    if end<6 or data[-5]!=2: return None
    last=('int',struct.unpack('<i',data[-4:])[0]); final_type=end-5
    # Find two type=1 boundaries. Backtracking handles literal 0x01 in encoded data.
    if len(data)<3 or data[1]!=1: return None
    best=None
    for p2 in range(2,final_type):
        if data[p2]!=1: continue
        for p3 in range(p2+1,final_type):
            if data[p3]!=1: continue
            targets=(data[2:p2],data[p2+1:p3],data[p3+1:final_type])
            candidate_lists=[]; ok=True
            for target in targets:
                raws=_inverse_invalid_prefix(target)
                scored=[]
                for raw in raws:
                    sc=_f32_simplicity(raw)
                    if sc is not None: scored.append((sc,raw))
                if not scored: ok=False; break
                scored.sort(key=lambda x:x[0]); candidate_lists.append(scored[:8])
            if not ok: continue
            for a in candidate_lists[0]:
                for b in candidate_lists[1]:
                    for c in candidate_lists[2]:
                        raws=(a[1],b[1],c[1])
                        rebuilt=b'\x04\x01'+raws[0]+b'\x01'+raws[1]+b'\x01'+raws[2]+b'\x02'+data[-4:]
                        if _utf8_replacement_roundtrip(rebuilt)!=segment: continue
                        score=(a[0][0]+b[0][0]+c[0][0],a[0][1]+b[0][1]+c[0][1],a[0],b[0],c[0])
                        vals=[('float',struct.unpack('<f',r)[0]) for r in raws]+[last]
                        if best is None or score<best[0]: best=(score,vals)
    return None if best is None else best[1]

def _forensic_leading_color_root(damaged):
    """Recover RaP where ambient/diffuse numeric bytes were also UTF-8 damaged."""
    collapsed,wild=_collapse_replacement_markers(damaged)
    if not collapsed.startswith(b'\0raP') or len(collapsed)<40: return None
    # Locate property boundaries in the damaged stream to recover color payloads.
    ambient_key=b'\x02ambient\0'; diffuse_key=b'\x02diffuse\0'; forced_key=b'\x02forcedDiffuse\0'
    a=damaged.find(ambient_key,16); d=damaged.find(diffuse_key,a+len(ambient_key) if a>=0 else 16)
    f=damaged.find(forced_key,d+len(diffuse_key) if d>=0 else 16)
    if min(a,d,f)<0: return None
    ambient_segment=damaged[a+len(ambient_key):d]
    diffuse_segment=damaged[d+len(diffuse_key):f]
    ambient=_recover_corrupted_color_array(ambient_segment)
    diffuse=_recover_corrupted_color_array(diffuse_segment)
    if ambient is None or diffuse is None: return None

    # In the collapsed representation the remaining root properties are intact;
    # absolute class offsets may contain wildcards but are ignored by the parser.
    forced_pos=collapsed.find(forced_key,16)
    if forced_pos<0: return None
    # Read root header to know the total property count.
    pos=16; parent,pos=_rap_cstr(collapsed,pos); total,pos=_rap_compint(collapsed,pos)
    consumed=2; remaining=total-consumed
    props,children,after=_rap_props_collapsed(collapsed,forced_pos,wild,remaining)
    pos=after+4
    for rec in children:
        child=_rap_node_collapsed(collapsed,pos,wild,rec['name']); rec['child']=child; pos=child['end']
    root={'name':None,'parent':parent,'props':[{'kind':'array','name':'ambient','value':ambient},{'kind':'array','name':'diffuse','value':diffuse}]+props,'start':16,'end':pos}
    reconstructed=_rap_document_bytes(root)
    if _utf8_replacement_roundtrip(reconstructed)!=damaged: return None
    return root,reconstructed,'UTF8-RAP-NUMERIC-INVERSION'

def forensic_recover_rvmat_bytes(damaged):
    if not _is_rap_bytes(damaged) or _RAP_REPLACEMENT not in damaged: return None
    collapsed,wild=_collapse_replacement_markers(damaged)
    try:
        root=_rap_node_collapsed(collapsed,16,wild,None)
        reconstructed=_rap_document_bytes(root)
        if _utf8_replacement_roundtrip(reconstructed)==damaged:
            return root,reconstructed,'UTF8-RAP-STRUCTURAL-INVERSION'
    except Exception:
        pass
    try:
        return _forensic_leading_color_root(damaged)
    except Exception:
        return None

def recover_forensic_rvmats(dst_root):
    dst_root=Path(dst_root); recovered=[]; warnings=[]; proofs=[]
    for target in sorted(dst_root.rglob('*.rvmat')):
        try: damaged=target.read_bytes()
        except Exception as ex:
            warnings.append(f'{target}: cannot read RVMAT: {ex}'); continue
        if not _is_rap_bytes(damaged) or _RAP_REPLACEMENT not in damaged: continue
        result=forensic_recover_rvmat_bytes(damaged)
        if result is None:
            continue
        root,reconstructed,method=result
        proof={
            'converter':SCRIPT_VERSION,'method':method,
            'damaged_sha1':hashlib.sha1(damaged).hexdigest(),
            'reconstructed_sha1':hashlib.sha1(reconstructed).hexdigest(),
            'damaged_size':len(damaged),'reconstructed_size':len(reconstructed),
            'replacement_markers':damaged.count(_RAP_REPLACEMENT),
            'proof':'UTF8_REPLACEMENT_ROUNDTRIP_BYTE_EXACT',
            'roundtrip_byte_exact':_utf8_replacement_roundtrip(reconstructed)==damaged,
        }
        text=_render_forensic_rvmat(root,proof)
        target.write_text(text,encoding='utf-8',newline='\n')
        side=target.with_name(target.name+'.forensic.json')
        side.write_text(json.dumps(proof,ensure_ascii=False,indent=2),encoding='utf-8')
        rel=str(target.relative_to(dst_root)).replace('/','\\')
        recovered.append(rel); proofs.append(proof)
    return {'recovered':recovered,'warnings':warnings,'proofs':proofs}

def _embedded_rvmat_direct_layout(mat):
    """True when source-stage/texgen mapping is losslessly reconstructible.

    In the common Super-material layout used by armor/body materials, compiled
    StageTexture index, stageID and StageTransform index are identical (1..N).
    Sparse/TexGen-sharing layouts are preserved in JSON but are not silently
    substituted for a damaged external RVMAT because their original source
    spelling is not uniquely recoverable from the compiled table alone.
    """
    textures=mat.get('stageTextures') or []
    transforms=mat.get('stageTransforms') or []
    seen=False
    for sid,tex in enumerate(textures):
        if sid==0: continue
        texture=tex.get('texture','')
        runtime_filler=(int(tex.get('filter',0))==0 and int(tex.get('stageID',0))==0 and
                        sid>=len(transforms) and 'color(1,1,1,1,dt)' in texture.lower())
        if runtime_filler: continue
        if not texture and int(tex.get('stageID',0))!=sid: continue
        seen=True
        if int(tex.get('stageID',-1))!=sid or sid>=len(transforms):
            return False
    return seen

def _material_semantic_key(mat):
    # Exclude material path itself; keep every compiled semantic field.
    d={k:v for k,v in mat.items() if k!='name'}
    return json.dumps(d,sort_keys=True,separators=(',',':'),default=list)

def collect_embedded_materials(parsed_models):
    by_name={}
    for model_rel,model in parsed_models:
        for lod in model.get('lods',[]):
            for mat in lod.get('materials') or []:
                name=(mat.get('name') or '').replace('/','\\')
                if not name: continue
                rec=by_name.setdefault(name.lower(),{'name':name,'variants':{},'models':set()})
                key=_material_semantic_key(mat)
                v=rec['variants'].setdefault(key,{'material':mat,'count':0})
                v['count']+=1; rec['models'].add(str(model_rel))
    return by_name

def _is_rap_bytes(data):
    return len(data)>=4 and data[:4]==b'\x00raP'

def _match_material_file(root,material_name):
    root=Path(root)
    norm=material_name.replace('\\','/').strip('/').lower()
    files=list(root.rglob('*.rvmat'))
    exact=[]
    for p in files:
        try: rel=p.relative_to(root).as_posix().lower()
        except Exception: rel=p.as_posix().lower()
        if norm==rel or norm.endswith('/'+rel): exact.append(p)
    if len(exact)==1: return exact[0]
    # Prefer the longest suffix path match, then unique basename.
    scored=[]
    for p in files:
        rel=p.relative_to(root).as_posix().lower()
        a=norm.split('/'); b=rel.split('/'); n=0
        while n<min(len(a),len(b)) and a[-1-n]==b[-1-n]: n+=1
        if n: scored.append((n,p))
    if scored:
        scored.sort(key=lambda x:x[0],reverse=True)
        top=scored[0][0]; tops=[p for n,p in scored if n==top]
        if len(tops)==1 and top>=2: return tops[0]
    base=Path(norm).name
    matches=[p for p in files if p.name.lower()==base]
    return matches[0] if len(matches)==1 else None

def _rv_text_tokens(text):
    out=[]; pos=0
    while pos<len(text):
        m=_RV_TEXT_TOKEN.match(text,pos)
        if not m:
            if text[pos:].strip()=='': break
            raise ValueError('RVMAT text token error near '+repr(text[pos:pos+80]))
        pos=m.end()
        if m.group(1): continue
        if m.group(2): out.append(('str',m.group(2)[1:-1].replace('\\"','"')))
        elif m.group(3): out.append(('num',m.group(3)))
        elif m.group(4): out.append(('id',m.group(4)))
        else: out.append((m.group(5),m.group(5)))
    return out

class _RvTextParser:
    def __init__(self,tokens): self.t=tokens; self.i=0
    def peek(self,*values): return self.i<len(self.t) and self.t[self.i][1] in values
    def pop(self,value=None):
        if self.i>=len(self.t): raise ValueError('unexpected end of RVMAT text')
        item=self.t[self.i]; self.i+=1
        if value is not None and item[1]!=value: raise ValueError(f'expected {value}, got {item}')
        return item
    def value(self):
        typ,value=self.pop()
        if typ=='str': return ('string',value)
        if typ=='num':
            return ('float',float(value)) if any(c in value.lower() for c in '.e') else ('int',int(value))
        raise ValueError(f'unsupported RVMAT value {typ}:{value}')
    def array(self):
        self.pop('{'); values=[]
        while not self.peek('}'):
            values.append(('array',self.array()) if self.peek('{') else self.value())
            if self.peek(','): self.pop(',')
            elif not self.peek('}'): raise ValueError('expected comma or array end')
        self.pop('}'); return values
    def node(self,name=None,parent=''):
        props=[]
        while self.i<len(self.t) and not self.peek('}'):
            if self.peek('class'):
                self.pop('class'); cname=self.pop()[1]; cparent=''
                if self.peek(':'): self.pop(':'); cparent=self.pop()[1]
                self.pop('{'); child=self.node(cname,cparent); self.pop('}'); self.pop(';')
                props.append({'kind':'class','name':cname,'child':child})
                continue
            name0=self.pop()[1]
            if self.peek('[]'):
                self.pop('[]'); op='array+'
                if self.peek('+='): self.pop('+=')
                else: self.pop('='); op='array'
                arr=self.array(); self.pop(';'); props.append({'kind':op,'name':name0,'value':arr})
            else:
                self.pop('='); value=self.value(); self.pop(';')
                props.append({'kind':'var','name':name0,'value':value})
        return {'name':name,'parent':parent,'props':props}

def _parse_rvmat_text(text):
    parser=_RvTextParser(_rv_text_tokens(text)); root=parser.node()
    if parser.i!=len(parser.t): raise ValueError('unparsed RVMAT text tokens remain')
    return root

def _rap_array_template(buf,pos,wild):
    n,pos=_rap_compint(buf,pos); values=[]
    for _ in range(n):
        if pos>=len(buf): raise ValueError('truncated RaP array')
        if pos in wild: raise ValueError(f'unknown RaP array element type at {pos}')
        typ=buf[pos]; pos+=1
        if typ==0:
            value,pos=_rap_cstr(buf,pos); values.append(('string',value))
        elif typ in (1,2):
            if pos+4>len(buf): raise ValueError('truncated RaP numeric array element')
            unknown=any(i in wild for i in range(pos,pos+4)); raw=buf[pos:pos+4]; pos+=4
            if unknown: values.append(('unknown_float',None) if typ==1 else ('unknown_int',None))
            else: values.append(('float',struct.unpack('<f',raw)[0]) if typ==1 else ('int',struct.unpack('<i',raw)[0]))
        elif typ==3:
            nested,pos=_rap_array_template(buf,pos,wild); values.append(('array',nested))
        else:
            raise ValueError(f'unsupported RaP array type {typ} at {pos-1}')
    return values,pos

def _rap_props_template(buf,pos,wild,count):
    props=[]; children=[]
    for _ in range(count):
        if pos>=len(buf): raise ValueError('truncated RaP properties')
        if pos in wild: raise ValueError(f'unknown RaP property type at {pos}')
        typ=buf[pos]; pos+=1
        if typ==0:
            name,pos=_rap_cstr(buf,pos)
            if pos+4>len(buf): raise ValueError('truncated RaP class offset')
            pos+=4
            rec={'kind':'class','name':name,'child':None}; props.append(rec); children.append(rec)
        elif typ==1:
            if pos in wild: raise ValueError(f'unknown RaP value subtype at {pos}')
            subtype=buf[pos]; pos+=1; name,pos=_rap_cstr(buf,pos)
            if subtype==0:
                value,pos=_rap_cstr(buf,pos); value=('string',value)
            elif subtype in (1,2):
                if pos+4>len(buf): raise ValueError('truncated RaP scalar')
                unknown=any(i in wild for i in range(pos,pos+4)); raw=buf[pos:pos+4]; pos+=4
                if unknown: value=('unknown_float',None) if subtype==1 else ('unknown_int',None)
                else: value=('float',struct.unpack('<f',raw)[0]) if subtype==1 else ('int',struct.unpack('<i',raw)[0])
            else: raise ValueError(f'unsupported RaP scalar subtype {subtype} for {name}')
            props.append({'kind':'var','name':name,'value':value})
        elif typ==2:
            name,pos=_rap_cstr(buf,pos); value,pos=_rap_array_template(buf,pos,wild)
            props.append({'kind':'array','name':name,'value':value})
        elif typ==3:
            name,pos=_rap_cstr(buf,pos); props.append({'kind':'external','name':name})
        elif typ==4:
            name,pos=_rap_cstr(buf,pos); props.append({'kind':'delete','name':name})
        elif typ==5:
            if pos+4>len(buf): raise ValueError('truncated RaP array expansion')
            pos+=4; name,pos=_rap_cstr(buf,pos); value,pos=_rap_array_template(buf,pos,wild)
            props.append({'kind':'array+','name':name,'value':value})
        else: raise ValueError(f'unsupported RaP property type {typ} at {pos-1}')
    return props,children,pos

def _rap_node_template(buf,start,wild,name=None):
    pos=start; parent,pos=_rap_cstr(buf,pos); count,pos=_rap_compint(buf,pos)
    props,children,pos=_rap_props_template(buf,pos,wild,count)
    if pos+4>len(buf): raise ValueError('truncated RaP class trailer')
    pos+=4
    for rec in children:
        child=_rap_node_template(buf,pos,wild,rec['name']); rec['child']=child; pos=child['end']
    return {'name':name,'parent':parent,'props':props,'start':start,'end':pos}

def _rap_template_from_damaged(damaged):
    collapsed,wild=_collapse_replacement_markers(damaged)
    if not collapsed.startswith(b'\0raP'): raise ValueError('not a RaP payload')
    return _rap_node_template(collapsed,16,wild,None)

def _prop_match(node,name,kind):
    name=name.lower(); candidates=[]
    for prop in node.get('props') or []:
        if prop.get('name','').lower()!=name: continue
        if kind=='class' and prop.get('kind')=='class': return prop
        if kind in ('array','array+') and prop.get('kind') in ('array','array+'): candidates.append(prop)
        elif kind=='var' and prop.get('kind')=='var': candidates.append(prop)
        elif prop.get('kind')==kind: candidates.append(prop)
    return candidates[0] if candidates else None

def _merge_array_template(source_values,semantic_values,path):
    if len(source_values)!=len(semantic_values):
        raise ValueError(f'array length mismatch at {path}: {len(source_values)} != {len(semantic_values)}')
    out=[]
    for i,(src,sem) in enumerate(zip(source_values,semantic_values)):
        st,sv=src; tt,tv=sem
        if st=='string': out.append(src)  # spelling/case surviving in RaP is authoritative
        elif st=='array':
            if tt!='array': raise ValueError(f'array type mismatch at {path}[{i}]')
            out.append(('array',_merge_array_template(sv,tv,f'{path}[{i}]')))
        elif st in ('float','int','unknown_float','unknown_int'):
            wanted='float' if st in ('float','unknown_float') else 'int'
            if tt!=wanted:
                # Text renderers may spell an integer-looking float as an int.
                if wanted=='float' and tt=='int': out.append(('float',float(tv)))
                elif wanted=='int' and tt=='float' and float(tv).is_integer(): out.append(('int',int(tv)))
                else: raise ValueError(f'numeric type mismatch at {path}[{i}]: {st} vs {tt}')
            else: out.append((wanted,tv))
        else: raise ValueError(f'unsupported source template value {st} at {path}[{i}]')
    return out

def _merge_rap_template(source_node,semantic_node,path='root'):
    out={'name':source_node.get('name'),'parent':source_node.get('parent') or '','props':[]}
    for src in source_node.get('props') or []:
        kind=src['kind']; name=src['name']; here=path+'/'+name
        sem=_prop_match(semantic_node,name,kind)
        if kind=='class':
            if sem is None: raise ValueError(f'missing semantic class {here}')
            child=_merge_rap_template(src['child'],sem['child'],here)
            out['props'].append({'kind':'class','name':name,'child':child})
        elif kind=='var':
            st,sv=src['value']
            if st=='string':
                out['props'].append({'kind':'var','name':name,'value':src['value']})
            else:
                if sem is None: raise ValueError(f'missing semantic value {here}')
                tt,tv=sem['value']; wanted='float' if st in ('float','unknown_float') else 'int'
                if tt==wanted: value=(wanted,tv)
                elif wanted=='float' and tt=='int': value=('float',float(tv))
                elif wanted=='int' and tt=='float' and float(tv).is_integer(): value=('int',int(tv))
                else: raise ValueError(f'numeric type mismatch at {here}: {st} vs {tt}')
                out['props'].append({'kind':'var','name':name,'value':value})
        elif kind in ('array','array+'):
            if sem is None: raise ValueError(f'missing semantic array {here}')
            value=_merge_array_template(src['value'],sem['value'],here)
            out['props'].append({'kind':kind,'name':name,'value':value})
        elif kind in ('external','delete'):
            out['props'].append({'kind':kind,'name':name})
        else: raise ValueError(f'unsupported source property kind {kind} at {here}')
    return out

def _source_proof_embedded_rvmat(damaged,embedded):
    """Return (text, proof, root) when RaP topology + EmbeddedMaterial proves exact.

    The damaged payload is the structure authority; EmbeddedMaterial contributes
    numeric/semantic values.  No extra Stage/TexGen/uvTransform is invented.
    """
    if not _is_rap_bytes(damaged) or _RAP_REPLACEMENT not in damaged: return None
    try:
        source_template=_rap_template_from_damaged(damaged)
        semantic_root=_parse_rvmat_text(render_embedded_rvmat(embedded))
        merged=_merge_rap_template(source_template,semantic_root)
        reconstructed=_rap_document_bytes(merged)
        if _utf8_replacement_roundtrip(reconstructed)!=damaged: return None
        proof={
            'converter':SCRIPT_VERSION,
            'method':'RAP-TOPOLOGY+ODOL-EMBEDDEDMATERIAL',
            'damaged_sha1':hashlib.sha1(damaged).hexdigest(),
            'reconstructed_sha1':hashlib.sha1(reconstructed).hexdigest(),
            'damaged_size':len(damaged),'reconstructed_size':len(reconstructed),
            'replacement_markers':damaged.count(_RAP_REPLACEMENT),
            'proof':'UTF8_REPLACEMENT_ROUNDTRIP_BYTE_EXACT',
            'roundtrip_byte_exact':True,
            'sourceTopologyPreserved':True,
            'sourceStringSpellingPreserved':True,
            'semanticValuesFrom':'ODOL EmbeddedMaterial'
        }
        return _render_forensic_rvmat(merged,proof),proof,merged
    except Exception:
        return None

def recover_embedded_rvmats(dst_root,parsed_models):
    """Recover damaged RVMATs from EmbeddedMaterial with strongest available proof.

    Priority:
      1. Preserve surviving damaged-RaP topology/string spelling and merge only
         semantic values from ODOL EmbeddedMaterial. Accept as SOURCE-PROOF only
         with byte-exact corruption round-trip.
      2. For direct/unambiguous layouts where topology proof is unavailable,
         retain the v6/v7 semantic EmbeddedMaterial renderer.
      3. Sparse/shared layouts without a proof remain binary and are reported.
    Existing textual RVMAT source is never overwritten.
    """
    records=collect_embedded_materials(parsed_models)
    recovered=[]; source_proof=[]; semantic_only=[]; warnings=[]; unresolved=[]
    for rec in records.values():
        target=_match_material_file(dst_root,rec['name'])
        if target is None:
            unresolved.append(rec['name']); continue
        try: current=target.read_bytes()
        except Exception as ex:
            warnings.append(f'{rec["name"]}: cannot read target RVMAT: {ex}'); continue
        if current and not _is_rap_bytes(current):
            continue  # already editable source
        variants=sorted(rec['variants'].values(),key=lambda x:x['count'],reverse=True)
        if not variants: continue
        if len(variants)>1 and variants[0]['count']==variants[1]['count']:
            warnings.append(f'{rec["name"]}: ambiguous EmbeddedMaterial variants ({len(variants)}); binary original preserved')
            continue
        chosen=variants[0]['material']

        # Strongest path: use the damaged RaP as the authoring-structure template.
        proof_result=_source_proof_embedded_rvmat(current,chosen)
        if proof_result is not None:
            text,proof,_root=proof_result
            target.write_text(text,encoding='utf-8',newline='\n')
            side=target.with_name(target.name+'.embedded.json')
            side.write_text(json.dumps({
                'converter':SCRIPT_VERSION,'materialName':rec['name'],'models':sorted(rec['models']),
                'variantCount':len(variants),'chosenOccurrences':variants[0]['count'],
                'recoveryStatus':'SOURCE-PROOF-RAP-TOPOLOGY+EMBEDDEDMATERIAL',
                'proof':proof,'embedded':chosen
            },ensure_ascii=False,indent=2,default=list),encoding='utf-8')
            rel=str(target.relative_to(dst_root)).replace('/','\\')
            recovered.append(rel); source_proof.append(rel)
            continue

        if not _embedded_rvmat_direct_layout(chosen):
            side=target.with_name(target.name+'.embedded.json')
            side.write_text(json.dumps({
                'converter':SCRIPT_VERSION,'materialName':rec['name'],'models':sorted(rec['models']),
                'variantCount':len(variants),'chosenOccurrences':variants[0]['count'],
                'recoveryStatus':'COMPILED-MATERIAL-PRESERVED-NONUNIQUE-SOURCE','embedded':chosen
            },ensure_ascii=False,indent=2,default=list),encoding='utf-8')
            warnings.append(f'{rec["name"]}: EmbeddedMaterial found, but sparse/shared texgen layout is not uniquely reversible and source-topology proof failed; binary RVMAT preserved')
            continue

        text=render_embedded_rvmat(chosen)
        target.write_text(text,encoding='utf-8',newline='\n')
        side=target.with_name(target.name+'.embedded.json')
        side.write_text(json.dumps({
            'converter':SCRIPT_VERSION,'materialName':rec['name'],'models':sorted(rec['models']),
            'variantCount':len(variants),'chosenOccurrences':variants[0]['count'],
            'recoveryStatus':'SEMANTIC-EXACT-EMBEDDEDMATERIAL-DIRECT-LAYOUT','embedded':chosen
        },ensure_ascii=False,indent=2,default=list),encoding='utf-8')
        rel=str(target.relative_to(dst_root)).replace('/','\\')
        recovered.append(rel); semantic_only.append(rel)
    return dict(recovered=recovered,source_proof=source_proof,semantic_only=semantic_only,
                warnings=warnings,unresolved=unresolved,total_embedded=len(records))


# ---------------------------------------------------------------------------
# Clean standalone RaP RVMAT recovery
# ---------------------------------------------------------------------------

def _rap_semantic_form(node):
    """Canonical semantic representation, intentionally ignoring RaP offsets."""
    props=[]
    for prop in node.get('props') or []:
        kind=prop.get('kind'); name=prop.get('name')
        if kind=='class':
            props.append(('class',name,_rap_semantic_form(prop['child'])))
        elif kind=='var':
            typ,value=prop['value']
            if typ=='float':
                value=struct.unpack('<f',struct.pack('<f',float(value)))[0]
            elif typ=='int': value=int(value)
            props.append(('var',name,typ,value))
        elif kind in ('array','array+'):
            def canon_values(values):
                out=[]
                for typ,value in values:
                    if typ=='array': out.append(('array',canon_values(value)))
                    elif typ=='float': out.append(('float',struct.unpack('<f',struct.pack('<f',float(value)))[0]))
                    elif typ=='int': out.append(('int',int(value)))
                    else: out.append((typ,value))
                return tuple(out)
            props.append((kind,name,canon_values(prop['value'])))
        else:
            props.append((kind,name))
    return (node.get('parent') or '',tuple(props))


def _clean_rap_semantic_end(data):
    """Return the end of the semantic class stream for a clean RaP document.

    Standard binarized RVMATs commonly place a four-byte empty enum table after
    the class stream.  The header's enumOffset points to that table, so requiring
    the root class to consume len(data) incorrectly rejects otherwise-valid RaP.
    We account for the complete document and accept only the empty enum table
    shape currently proven by our samples.
    """
    if len(data)<16 or not _is_rap_bytes(data):
        raise ValueError('invalid RaP document')
    enum_offset=struct.unpack_from('<I',data,12)[0]
    if enum_offset==0:
        return len(data),0
    if enum_offset<16 or enum_offset>len(data):
        # Some packers poison only this absolute offset while leaving the
        # sequential RaP property stream intact.  Treat the whole payload as
        # the semantic stream; the caller still requires the parsed root to
        # consume it exactly before any text is accepted.
        return len(data),0
    tail=data[enum_offset:]
    if tail!=b'\0\0\0\0':
        raise ValueError('non-empty/unsupported RaP enum table')
    return enum_offset,len(tail)

def verify_clean_rap_rvmat_text(original_bytes,text):
    """Prove semantic equivalence of a clean RaP RVMAT and rendered source.

    Some obfuscators poison absolute RaP offsets while leaving the sequential
    property stream intact.  The parser deliberately treats the sequential
    layout as authoritative, then re-parses the rendered source and compares a
    canonical semantic tree.  The standard empty enum trailer is accounted for
    separately.  This is semantic proof, not authoring-format proof.
    """
    if not _is_rap_bytes(original_bytes) or _RAP_REPLACEMENT in original_bytes:
        return False,None
    try:
        semantic_end,_enum_bytes=_clean_rap_semantic_end(original_bytes)
        source_root=_rap_node_collapsed(original_bytes,16,set(),None)
        if source_root.get('end')!=semantic_end:
            return False,None
        text_root=_parse_rvmat_text(text)
        same=(_rap_semantic_form(source_root)==_rap_semantic_form(text_root))
        if not same:
            return False,None
        return True,source_root
    except Exception:
        return False,None


def recover_clean_rap_rvmats(dst_root):
    """Recover intact/offset-poisoned RaP RVMATs into editable text.

    Unlike forensic UTF-8-damaged recovery, this path applies only when every
    semantic property can be parsed directly from the RaP stream.  The textual
    result is accepted only after an independent semantic re-parse matches the
    original property tree exactly.  No sidecar is written inside recovered_source
    so Workbench physical-file-count auditing remains exact.
    """
    dst_root=Path(dst_root); recovered=[]; warnings=[]; records=[]
    for target in sorted(dst_root.rglob('*.rvmat')):
        try: original=target.read_bytes()
        except Exception as ex:
            warnings.append(f'{target}: cannot read RVMAT: {ex}'); continue
        if not _is_rap_bytes(original) or _RAP_REPLACEMENT in original:
            continue
        try:
            semantic_end,enum_bytes=_clean_rap_semantic_end(original)
            root=_rap_node_collapsed(original,16,set(),None)
            if root.get('end')!=semantic_end:
                warnings.append(f'{target}: RaP semantic stream parsed only {root.get("end")}/{semantic_end} bytes; binary preserved')
                continue
            text='\n'.join(_render_rap_node(root))+'\n'
            ok,_=verify_clean_rap_rvmat_text(original,text)
            if not ok:
                warnings.append(f'{target}: clean RaP semantic round-trip failed; binary preserved')
                continue
            target.write_text(text,encoding='utf-8',newline='\n')
            rel=str(target.relative_to(dst_root)).replace('/','\\')
            recovered.append(rel)
            records.append({
                'path':rel,
                'original_sha1':hashlib.sha1(original).hexdigest(),
                'original_size':len(original),
                'parsed_bytes':len(original),
                'semantic_bytes':root.get('end'),
                'enum_bytes':enum_bytes,
                'semantic_roundtrip':True,
                'recoveryStatus':'SEMANTIC-EXACT-CLEAN-RAP',
            })
        except Exception as ex:
            warnings.append(f'{target}: clean RaP recovery failed: {ex}')
    return {'recovered':recovered,'warnings':warnings,'records':records}
