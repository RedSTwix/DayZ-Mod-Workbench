from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from ..version import SCRIPT_VERSION
from ..odol.converter import convert_lod

def _f32_bits(v):
    return struct.pack('<f', float(v))

def _float_tuple_exact(a,b):
    return len(a)==len(b) and all(_f32_bits(x)==_f32_bits(y) for x,y in zip(a,b))

def _mlod_read_cstr(data,pos):
    try:
        e=data.index(b'\0',pos)
    except ValueError:
        raise ValueError(f'MLOD unterminated string at offset {pos}')
    return data[pos:e].decode('latin1'),e+1

def parse_mlod(path):
    """Parse the MLOD subset emitted by this converter for independent verification."""
    data=Path(path).read_bytes(); pos=0
    if data[:4]!=b'MLOD':
        raise ValueError('not MLOD')
    pos=4
    version,nlod=struct.unpack_from('<II',data,pos); pos+=8
    lods=[]
    for li in range(nlod):
        if data[pos:pos+4]!=b'P3DM':
            raise ValueError(f'MLOD LOD {li}: missing P3DM at {pos}')
        pos+=4
        hdr_size,lod_version,npoints,nnormals,nfaces,flags=struct.unpack_from('<IIIIII',data,pos); pos+=24
        points=[]
        for _ in range(npoints):
            x,y,z,fl=struct.unpack_from('<fffI',data,pos); pos+=16
            points.append((x,y,z,fl))
        normals=[]
        for _ in range(nnormals):
            normals.append(struct.unpack_from('<fff',data,pos)); pos+=12
        faces=[]
        for _ in range(nfaces):
            nv=struct.unpack_from('<I',data,pos)[0]; pos+=4
            slots=[]
            for __ in range(4):
                pi,ni,u,v=struct.unpack_from('<IIff',data,pos); pos+=16
                slots.append((pi,ni,u,v))
            fflags=struct.unpack_from('<I',data,pos)[0]; pos+=4
            tex,pos=_mlod_read_cstr(data,pos)
            mat,pos=_mlod_read_cstr(data,pos)
            faces.append((slots[:nv],fflags,tex,mat))
        if data[pos:pos+4]!=b'TAGG':
            raise ValueError(f'MLOD LOD {li}: missing TAGG at {pos}')
        pos+=4
        tags=[]; resolution=None
        while True:
            active=data[pos]; pos+=1
            name,pos=_mlod_read_cstr(data,pos)
            size=struct.unpack_from('<I',data,pos)[0]; pos+=4
            payload=data[pos:pos+size]; pos+=size
            if name=='#EndOfFile#':
                resolution=struct.unpack_from('<f',data,pos)[0]; pos+=4
                break
            tags.append((name,payload,active))
        lods.append(dict(
            hdrSize=hdr_size,lodVersion=lod_version,flags=flags,
            points=points,normals=normals,faces=faces,tags=tags,res=resolution
        ))
    if pos!=len(data):
        raise ValueError(f'MLOD trailing bytes: parsed={pos}, file={len(data)}, extra={len(data)-pos}')
    return dict(version=version,lods=lods,size=len(data))

def _compare_face_expected(exp,got,li,fi,errors):
    evs,eflags,etex,emat,_rawfi=exp
    gvs,gflags,gtex,gmat=got
    if eflags!=gflags:
        errors.append(f'LOD {li} face {fi}: flags {gflags:#x} != {eflags:#x}')
    if etex!=gtex:
        errors.append(f'LOD {li} face {fi}: texture {gtex!r} != {etex!r}')
    if emat!=gmat:
        errors.append(f'LOD {li} face {fi}: material {gmat!r} != {emat!r}')
    if len(evs)!=len(gvs):
        errors.append(f'LOD {li} face {fi}: vertex count {len(gvs)} != {len(evs)}')
        return
    for vi,(e,g) in enumerate(zip(evs,gvs)):
        if e[0]!=g[0] or e[1]!=g[1] or _f32_bits(e[2])!=_f32_bits(g[2]) or _f32_bits(e[3])!=_f32_bits(g[3]):
            errors.append(f'LOD {li} face {fi} vertex {vi}: MLOD vertex/normal/UV differs')
            break

def _verify_proxy_selections(src_lod,out_lod,exp_faces,li,errors):
    """Verify the MLOD selection shape required for every ODOL proxy.

    Comparing converter output with itself cannot detect an incorrect proxy
    reconstruction rule.  This check instead derives the expected triangle
    directly from the original ODOL named selection and validates the parsed
    MLOD tag independently.
    """
    proxies=src_lod.get('proxies') or []
    selections=src_lod.get('selections') or []
    if not proxies:
        return
    tag_map={name:payload for name,payload,_active in out_lod.get('tags') or []}
    raw_to_out={face[4]:index for index,face in enumerate(exp_faces)}
    point_count=len(out_lod.get('points') or [])
    face_count=len(out_lod.get('faces') or [])
    for pi,proxy in enumerate(proxies):
        if len(proxy)<6:
            errors.append(f'LOD {li} proxy {pi}: incomplete ODOL proxy record')
            continue
        nsi=proxy[3]
        if not (0<=nsi<len(selections)):
            errors.append(f'LOD {li} proxy {pi}: invalid namedSelectionIndex {nsi}')
            continue
        selection=selections[nsi]
        name=selection.get('name','')
        payload=tag_map.get(name)
        if payload is None:
            errors.append(f'LOD {li} proxy {pi} {name!r}: MLOD selection tag is missing')
            continue
        if len(payload)!=point_count+face_count:
            errors.append(
                f'LOD {li} proxy {pi} {name!r}: selection payload size '
                f'{len(payload)} != {point_count+face_count}'
            )
            continue
        raw_faces=[fi for fi in selection.get('faces') or [] if fi in raw_to_out]
        expected_faces={raw_to_out[fi] for fi in raw_faces}
        selected_faces={i for i,value in enumerate(payload[point_count:]) if value}
        if len(expected_faces)!=1:
            errors.append(
                f'LOD {li} proxy {pi} {name!r}: ODOL selection does not identify '
                f'exactly one proxy face (found {len(expected_faces)})'
            )
        if selected_faces!=expected_faces:
            errors.append(
                f'LOD {li} proxy {pi} {name!r}: selected faces '
                f'{sorted(selected_faces)} != original {sorted(expected_faces)}'
            )
        expected_points=set(selection.get('verts') or [])
        for raw_face in raw_faces:
            if 0<=raw_face<len(src_lod.get('faces') or []):
                expected_points.update(src_lod['faces'][raw_face])
        selected_points={i for i,value in enumerate(payload[:point_count]) if value}
        if selected_points!=expected_points:
            errors.append(
                f'LOD {li} proxy {pi} {name!r}: selected points '
                f'{sorted(selected_points)} != original triangle {sorted(expected_points)}'
            )

def verify_odol_to_mlod(model,mlod_path,report_path=None):
    """Field-by-field ODOL -> MLOD verification.

    The comparison is independent of the MLOD writer: it parses the generated
    file again and compares it against the decoded ODOL semantic structure.
    Floating-point values are compared by their float32 bit patterns.
    """
    parsed=parse_mlod(mlod_path)
    errors=[]; checks=[]
    if parsed['version']!=257:
        errors.append(f'MLOD version {parsed["version"]} != 257')
    if len(parsed['lods'])!=len(model['lods']):
        errors.append(f'LOD count {len(parsed["lods"])} != {len(model["lods"])}')
    for li,(src_lod,out_lod) in enumerate(zip(model['lods'],parsed['lods'])):
        exp_points,exp_normals,exp_faces,exp_tags=convert_lod(model,src_lod)
        if _f32_bits(src_lod['res'])!=_f32_bits(out_lod['res']):
            errors.append(f'LOD {li}: resolution differs {out_lod["res"]} != {src_lod["res"]}')
        if len(exp_points)!=len(out_lod['points']):
            errors.append(f'LOD {li}: point count {len(out_lod["points"])} != {len(exp_points)}')
        else:
            for pi,(e,g) in enumerate(zip(exp_points,out_lod['points'])):
                if e[3]!=g[3] or not _float_tuple_exact(e[:3],g[:3]):
                    errors.append(f'LOD {li} point {pi}: position/flags differ')
                    break
        if len(exp_normals)!=len(out_lod['normals']):
            errors.append(f'LOD {li}: normal count {len(out_lod["normals"])} != {len(exp_normals)}')
        else:
            for ni,(e,g) in enumerate(zip(exp_normals,out_lod['normals'])):
                if not _float_tuple_exact(e,g):
                    errors.append(f'LOD {li} normal {ni}: differs')
                    break
        if len(exp_faces)!=len(out_lod['faces']):
            errors.append(f'LOD {li}: face count {len(out_lod["faces"])} != {len(exp_faces)}')
        else:
            for fi,(e,g) in enumerate(zip(exp_faces,out_lod['faces'])):
                _compare_face_expected(e,g,li,fi,errors)
                if len(errors)>200: break
        got_tags=[(n,b) for n,b,_active in out_lod['tags']]
        if len(exp_tags)!=len(got_tags):
            errors.append(f'LOD {li}: tag count {len(got_tags)} != {len(exp_tags)}')
        else:
            for ti,((en,eb),(gn,gb)) in enumerate(zip(exp_tags,got_tags)):
                if en!=gn:
                    errors.append(f'LOD {li} tag {ti}: name {gn!r} != {en!r}')
                elif eb!=gb:
                    errors.append(f'LOD {li} tag {ti} {en}: payload bytes differ')
        _verify_proxy_selections(src_lod,out_lod,exp_faces,li,errors)
        checks.append(dict(
            lod=li,resolution=src_lod['res'],points=len(exp_points),normals=len(exp_normals),
            faces=len(exp_faces),tags=len(exp_tags),selections=len(src_lod.get('selections') or []),
            uvsets=len(src_lod.get('uvs') or []),frames=len(src_lod.get('frames') or [])
        ))
    status='SEMANTIC-EXACT' if not errors else 'LOSS/DIFFERENCE'
    result=dict(status=status,errors=errors,checks=checks,mlod=str(Path(mlod_path).resolve()))
    if report_path:
        rp=Path(report_path)
        lines=[
            'ODOL -> MLOD semantic verification',
            f'Converter: {SCRIPT_VERSION}',
            f'MLOD: {Path(mlod_path).resolve()}',
            f'Result: {status}',
            f'LODs checked: {len(checks)}',
            f'Differences: {len(errors)}',''
        ]
        for c in checks:
            lines.append('LOD {lod}: res={resolution:.9g} points={points} normals={normals} faces={faces} tags={tags} selections={selections} uvsets={uvsets} frames={frames}'.format(**c))
        if errors:
            lines += ['', 'DIFFERENCES:']+[f'- {x}' for x in errors[:500]]
        rp.write_text('\n'.join(lines)+'\n',encoding='utf-8')
    return result
