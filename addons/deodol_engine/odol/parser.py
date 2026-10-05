from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess

def lzo1x_decompress(src, expected_size):
    """Decompress one raw LZO1X block and return (bytes, consumed_input_bytes).

    This implements the LZO1X decoder used by DayZ ODOL v53-v55 blocks.
    It intentionally mirrors the BisDll/DayZ decoder behavior, including
    the end marker and exact expected output-size validation.
    """
    src = memoryview(src)
    ip = 0
    out = bytearray()

    def need(n=1):
        if ip + n > len(src):
            raise EOFError(f'LZO input overrun at {ip}, need {n} bytes')

    def nextb():
        nonlocal ip
        need(1)
        b = src[ip]
        ip += 1
        return int(b)

    def peek(rel=0):
        j = ip + rel
        if j < 0 or j >= len(src):
            raise EOFError(f'LZO input overrun at {j}')
        return int(src[j])

    def copy_literals(n):
        nonlocal ip
        if n < 0:
            raise ValueError('negative literal count')
        need(n)
        if len(out) + n > expected_size:
            raise OverflowError('LZO output overrun while copying literals')
        out.extend(src[ip:ip+n])
        ip += n

    def copy_match(m_pos, n):
        if m_pos < 0 or m_pos >= len(out):
            raise OverflowError(f'LZO lookbehind overrun: {m_pos} / {len(out)}')
        if len(out) + n > expected_size:
            raise OverflowError('LZO output overrun while copying match')
        # Byte-wise copy is intentional: LZO matches may overlap.
        for _ in range(n):
            out.append(out[m_pos])
            m_pos += 1

    if expected_size == 0:
        return b'', 0
    if not src:
        raise EOFError('empty LZO input')

    # Initial literal run.
    if peek() > 17:
        t = nextb() - 17
        if t >= 4:
            copy_literals(t)
            state = 'match_next'
        else:
            copy_literals(t)
            t = nextb()
            state = 'match'
    else:
        state = 'main'
        t = 0

    while True:
        if state == 'main':
            t = nextb()
            if t < 16:
                if t == 0:
                    while peek() == 0:
                        t += 255
                        ip += 1
                    t += 15 + nextb()
                # LZO literal run length is t + 3.
                copy_literals(t + 3)
                state = 'match_next'
                continue
            state = 'match'

        if state == 'match_next':
            t = nextb()
            if t >= 16:
                state = 'match'
                continue
            m_pos = len(out) - (1 + 2048)
            m_pos -= (t >> 2)
            m_pos -= nextb() << 2
            copy_match(m_pos, 3)
            state = 'match_done'

        if state == 'match':
            if t >= 64:
                m_pos = len(out) - 1
                m_pos -= (t >> 2) & 7
                m_pos -= nextb() << 3
                t = (t >> 5) - 1
                copy_match(m_pos, t + 2)
                state = 'match_done'

            elif t >= 32:
                t &= 31
                if t == 0:
                    while peek() == 0:
                        t += 255
                        ip += 1
                    t += 31 + nextb()
                m_pos = len(out) - 1
                m_pos -= (peek(0) >> 2) + (peek(1) << 6)
                ip += 2
                copy_match(m_pos, t + 2)
                state = 'match_done'

            elif t < 16:
                m_pos = len(out) - 1
                m_pos -= t >> 2
                m_pos -= nextb() << 2
                copy_match(m_pos, 2)
                state = 'match_done'

            else:
                m_pos = len(out)
                m_pos -= (t & 8) << 11
                t &= 7
                if t == 0:
                    while peek() == 0:
                        t += 255
                        ip += 1
                    t += 7 + nextb()
                m_pos -= (peek(0) >> 2) + (peek(1) << 6)
                ip += 2

                # LZO end marker.
                if m_pos == len(out):
                    if len(out) != expected_size:
                        raise OverflowError(
                            f'LZO output underrun: got {len(out)}, expected {expected_size}'
                        )
                    return bytes(out), ip

                m_pos -= 16384
                copy_match(m_pos, t + 2)
                state = 'match_done'

        if state == 'match_done':
            # Low two bits of the second byte immediately before current input
            # encode 0..3 trailing literals.
            t = peek(-2) & 3
            if t == 0:
                state = 'main'
                continue
            copy_literals(t)
            t = nextb()
            state = 'match'

def F32(x): return struct.unpack('<f',struct.pack('<f',x))[0]

class R:
    def __init__(self,data,pos=0,version=53): self.d=data; self.pos=pos; self.version=version
    def read(self,n):
        b=self.d[self.pos:self.pos+n]
        if len(b)!=n: raise EOFError((self.pos,n,len(b)))
        self.pos+=n; return b
    def u8(self): return self.read(1)[0]
    def bool(self): return self.u8()!=0
    def u16(self): return struct.unpack('<H',self.read(2))[0]
    def i16(self): return struct.unpack('<h',self.read(2))[0]
    def u32(self): return struct.unpack('<I',self.read(4))[0]
    def i32(self): return struct.unpack('<i',self.read(4))[0]
    def f32(self): return struct.unpack('<f',self.read(4))[0]
    def vec3(self): return (self.f32(),self.f32(),self.f32())
    def cstr(self):
        e=self.d.index(0,self.pos); b=self.d[self.pos:e]; self.pos=e+1; return b.decode('latin1')
    def compressed(self,expected,max_end=None):
        if expected==0: return b''
        if expected<1024: return self.read(expected)
        start=self.pos
        max_end=max_end or len(self.d)
        raw,consumed=lzo1x_decompress(self.d[start:max_end], expected)
        self.pos=start+consumed
        return raw

def count(r):
    c=r.i32()
    if not 0<=c<1000000: raise ValueError(('invalid count',c,r.pos))
    return c

def read_comp(r,esize,fmt=None,end=None):
    c=count(r); raw=r.compressed(c*esize,end)
    if fmt: return list(struct.unpack('<'+fmt*c,raw)) if c else []
    return c,raw

def read_cond(r,esize,fmt=None,end=None):
    c=count(r); default=r.bool()
    if c==0: return [] if fmt else (0,b'')
    raw=(r.read(esize)*c) if default else r.compressed(c*esize,end)
    if fmt: return list(struct.unpack('<'+fmt*c,raw))
    return c,raw

def skeleton(r):
    name=r.cstr(); bones=[]; discrete=False; pivots=''
    if name:
        if r.version>=23: discrete=r.bool()
        n=r.i32()
        for _ in range(n): bones.append((r.cstr(),r.cstr()))
        if r.version>40: pivots=r.cstr()
    return dict(name=name, discrete=discrete, bones=bones, pivots=pivots)

def _read_color4(r):
    return (r.f32(),r.f32(),r.f32(),r.f32())

def _read_stage_texture(r,mv):
    filt=r.u32() if mv>=5 else 1
    texture=r.cstr()
    stage_id=r.u32() if mv>=8 else 0
    world_env=r.bool() if mv>=11 else False
    return dict(filter=filt,texture=texture,stageID=stage_id,useWorldEnvMap=world_env)

def _read_stage_transform(r):
    uv=r.u32()
    vals=struct.unpack('<12f',r.read(48))
    return dict(uvSource=uv,aside=vals[0:3],up=vals[3:6],dir=vals[6:9],pos=vals[9:12])

def _material_extra_render_count(material_version, layout_policy):
    """Return the v11-v19 EmbeddedMaterial render-parameter count.

    DayZ ODOL53 exists in both layouts in the wild. Material v15 normally uses
    the two-field layout, while the surrounding versions (including the v16
    Corridor assets) use the older six-field layout. The explicit all-two and
    all-six policies are bounded compatibility fallbacks used by parse_lod;
    they are never accepted if they produce competing semantic parses.
    """
    if layout_policy == 'versioned':
        return 2 if material_version == 15 else 6
    if layout_policy == 'all-two':
        return 2
    if layout_policy == 'all-six':
        return 6
    raise ValueError(f'Unknown EmbeddedMaterial layout policy {layout_policy!r}')

def read_material(r, layout_policy='versioned'):
    """Read the complete EmbeddedMaterial payload instead of discarding it.

    The layout mirrors BisDll.Model.ODOL.EmbeddedMaterial for DayZ ODOL53-v55.
    Runtime-only fields with no stable source-level RVMAT spelling are preserved
    in the returned dictionary/JSON metadata even when they are not emitted into
    the editable .rvmat text.
    """
    name=r.cstr(); mv=r.u32()
    if mv>255:
        raise ValueError(f'Invalid EmbeddedMaterial version {mv}')
    emissive=_read_color4(r); ambient=_read_color4(r); diffuse=_read_color4(r)
    forced=_read_color4(r); specular=_read_color4(r); specular_copy=_read_color4(r)
    extra_colors=[]
    if mv>10:
        extra_colors=[_read_color4(r),_read_color4(r)]
    specular_power=r.f32()
    extra_render=[]
    if mv>=20:
        extra_render=[r.u32() for _ in range(18)]
    elif mv>10:
        extra_render=[r.u32() for _ in range(
            _material_extra_render_count(mv,layout_policy))]
    pixel_shader=r.u32(); vertex_shader=r.u32(); main_light=r.u32(); fog_mode=r.u32()
    v3_bool=r.bool() if mv==3 else None
    surface_file=r.cstr() if mv>=6 else ''
    n_render_flags=render_flags=0
    if mv>=4:
        n_render_flags=r.u32(); render_flags=r.u32()
    nst=r.u32() if mv>6 else 0
    ntg=r.u32() if mv>8 else 0
    if nst>4096 or ntg>4096:
        raise ValueError(
            f'Invalid EmbeddedMaterial stage counts textures={nst} transforms={ntg}')
    textures=[]; transforms=[]
    if mv<8:
        for _ in range(nst):
            transforms.append(_read_stage_transform(r))
            textures.append(_read_stage_texture(r,mv))
    else:
        for _ in range(nst): textures.append(_read_stage_texture(r,mv))
        for _ in range(ntg): transforms.append(_read_stage_transform(r))
    stage_ti=_read_stage_texture(r,mv) if mv>=10 else None
    return dict(
        name=name,version=mv,emissive=emissive,ambient=ambient,diffuse=diffuse,
        forcedDiffuse=forced,specular=specular,specularCopy=specular_copy,
        extraColors=extra_colors,specularPower=specular_power,extraRenderParams=extra_render,
        extraRenderParamCount=len(extra_render),
        pixelShader=pixel_shader,vertexShader=vertex_shader,mainLight=main_light,fogMode=fog_mode,
        version3Bool=v3_bool,surfaceFile=surface_file,nRenderFlags=n_render_flags,renderFlags=render_flags,
        stageTextures=textures,stageTransforms=transforms,stageTI=stage_ti
    )

def skip_material(r, layout_policy='versioned'):
    # Backwards-compatible alias used by older callers/imports.
    return read_material(r,layout_policy)

def read_uvset(r,end):
    minu,minv,maxu,maxv=r.f32(),r.f32(),r.f32(),r.f32(); nv=r.u32(); default=r.bool()
    raw=r.read(4) if default else r.compressed(nv*4,end)
    if default: pairs=[struct.unpack('<hh',raw)]*nv
    else: pairs=list(struct.iter_unpack('<hh',raw))
    du=maxu-minu; dv=maxv-minv; vals=[]
    for us,vs in pairs:
        vals += [F32(F32(1.52587890625e-05*(us+32767)*du)+minu), F32(F32(1.52587890625e-05*(vs+32767)*dv)+minv)]
    return vals

def _parse_lod_with_material_layout(data,start,end,res,version,layout_policy):
    r=R(data,start,version)
    nproxy=count(r); proxies=[]
    for _ in range(nproxy):
        model=r.cstr(); mat=struct.unpack('<12f',r.read(48)); seq,nsi,bi,si=r.i32(),r.i32(),r.i32(),r.i32(); proxies.append((model,mat,seq,nsi,bi,si))
    n=count(r); subs=[r.i32() for _ in range(n)]
    n=count(r)
    for _ in range(n):
        nn=count(r); [r.i32() for __ in range(nn)]
    vertex_count=r.u32(); face_area=r.f32(); r.i32(); r.i32(); r.read(12*3+4)
    n=count(r); textures=[r.cstr() for _ in range(n)]
    n=count(r); materials=[read_material(r,layout_policy) for _ in range(n)]
    p2v=read_comp(r,2,'H',end); v2p=read_comp(r,2,'H',end)
    nf=r.u32(); r.u32(); r.u16(); faces=[]
    for _ in range(nf):
        nv=r.u8(); faces.append([r.u16() for __ in range(nv)])
    ns=count(r); sections=[]
    for _ in range(ns):
        flo,fhi,minb,bcnt=r.i32(),r.i32(),r.i32(),r.i32(); r.u32(); ti=r.i16(); special=r.u32(); mi=r.i32(); mat_inline=r.cstr() if mi==-1 else ''
        nst=r.u32(); [r.f32() for __ in range(nst)]
        sections.append(dict(lo=flo,hi=fhi,tex=ti,mat=mi,mat_inline=mat_inline,special=special))
    nsel=count(r); sels=[]
    for _ in range(nsel):
        name=r.cstr(); sf=read_comp(r,2,'H',end); r.i32(); sectional=r.bool(); secs=read_comp(r,4,'i',end); sv=read_comp(r,2,'H',end); ew=r.i32(); weights=r.compressed(ew,end)
        sels.append(dict(name=name,faces=sf,sectional=sectional,sections=secs,verts=sv,weights=weights))
    nprop=r.u32(); props=[(r.cstr(),r.cstr()) for _ in range(nprop)]
    nframes=count(r); frames=[]
    for _ in range(nframes):
        t=r.f32(); n=r.u32(); frames.append((t,[r.vec3() for __ in range(n)]))
    r.i32(); r.i32(); r.i32(); r.bool(); r.u32()
    clips=read_cond(r,4,'i',end)
    uv0=read_uvset(r,end); nuv=r.u32(); uvs=[uv0]
    for _ in range(1,nuv): uvs.append(read_uvset(r,end))
    nv=count(r); verts=list(struct.iter_unpack('<fff',r.compressed(nv*12,end)))
    nn=count(r); default=r.bool()
    nraw=(r.read(4)*nn) if (nn and default) else (r.compressed(nn*4,end) if nn else b'')
    normals=[]
    for (val,) in struct.iter_unpack('<i',nraw):
        xyz=[]
        for sh in (0,10,20):
            z=(val>>sh)&0x3ff
            if z>511: z-=1024
            xyz.append(F32(F32(z)*F32(-0.0019569471)))
        normals.append(tuple(xyz))
    # remaining arrays not needed for static corridor conversion but consume exactly
    n=count(r); r.compressed(n*8,end)
    n=count(r); vbr_raw=r.compressed(n*12,end)
    # AnimationRTWeight is a VerySmallArray: int32 count + 8 bytes containing
    # up to four (subSkeletonIndex, weight) byte pairs.
    vbr=[]
    for i in range(n):
        rec=vbr_raw[i*12:(i+1)*12]
        ns=struct.unpack_from('<i',rec,0)[0]
        if ns<0 or ns>4:
            # Keep parsing deterministic, but do not invent bone data.
            pairs=[]
        else:
            space=rec[4:12]
            pairs=[(space[j*2],space[j*2+1]) for j in range(ns)]
        vbr.append(pairs)
    n=count(r); r.compressed(n*32,end)
    if r.pos!=end: raise ValueError(('LOD not fully consumed',res,r.pos,end,end-r.pos))
    lod=dict(res=res,proxies=proxies,subs=subs,vertexCount=vertex_count,
        textures=textures,materials=materials,faces=faces,sections=sections,
        selections=sels,props=props,frames=frames,clips=clips,uvs=uvs,
        verts=verts,normals=normals,vbr=vbr,
        materialLayoutPolicy=layout_policy)
    _validate_lod_structure(lod)
    return lod

def _validate_lod_structure(lod):
    """Reject a byte-aligned but structurally impossible material-layout guess."""
    nverts=len(lod['verts']); nfaces=len(lod['faces'])
    if lod['vertexCount']!=nverts:
        raise ValueError(
            f'Vertex count mismatch header={lod["vertexCount"]} decoded={nverts}')
    if len(lod['clips'])!=nverts or len(lod['normals'])!=nverts:
        raise ValueError(
            f'Vertex array mismatch verts={nverts} clips={len(lod["clips"])} '
            f'normals={len(lod["normals"])}')
    for index,uv in enumerate(lod['uvs']):
        if len(uv)!=nverts*2:
            raise ValueError(
                f'UV set {index} has {len(uv)} values for {nverts} vertices')
    for face_index,face in enumerate(lod['faces']):
        if len(face) not in (3,4):
            raise ValueError(f'Face {face_index} has invalid vertex count {len(face)}')
        if any(vertex<0 or vertex>=nverts for vertex in face):
            raise ValueError(f'Face {face_index} references a missing vertex')
    for section_index,section in enumerate(lod['sections']):
        if section['lo']<0 or section['hi']<section['lo']:
            raise ValueError(f'Section {section_index} has invalid face-byte bounds')
        if section['tex']!=-1 and not 0<=section['tex']<len(lod['textures']):
            raise ValueError(f'Section {section_index} references a missing texture')
        if section['mat']!=-1 and not 0<=section['mat']<len(lod['materials']):
            raise ValueError(f'Section {section_index} references a missing material')
    for selection_index,selection in enumerate(lod['selections']):
        if any(face<0 or face>=nfaces for face in selection['faces']):
            raise ValueError(f'Selection {selection_index} references a missing face')
        if any(vertex<0 or vertex>=nverts for vertex in selection['verts']):
            raise ValueError(f'Selection {selection_index} references a missing vertex')
        if any(section<0 or section>=len(lod['sections'])
               for section in selection['sections']):
            raise ValueError(f'Selection {selection_index} references a missing section')

def _same_semantic_value(left,right):
    """Compare parsed ODOL values while treating matching NaNs as equal.

    Some valid DayZ materials use NaN transform components as an engine-side
    sentinel.  Parsing the same bytes with two equivalent layout policies then
    produces distinct Python NaN objects (and ``nan != nan``), which used to
    turn an identical parse into a false EmbeddedMaterial ambiguity.
    """
    if isinstance(left,float) and isinstance(right,float):
        return (math.isnan(left) and math.isnan(right)) or left==right
    if isinstance(left,dict) and isinstance(right,dict):
        return left.keys()==right.keys() and all(
            _same_semantic_value(left[key],right[key]) for key in left)
    if isinstance(left,(list,tuple)) and isinstance(right,(list,tuple)):
        return len(left)==len(right) and all(
            _same_semantic_value(a,b) for a,b in zip(left,right))
    return left==right

def _same_lod_semantics(left,right):
    left={key:value for key,value in left.items()
          if key!='materialLayoutPolicy'}
    right={key:value for key,value in right.items()
           if key!='materialLayoutPolicy'}
    return _same_semantic_value(left,right)

def parse_lod(data,start,end,res,version=53):
    """Parse one LOD using all supported EmbeddedMaterial layouts safely.

    A candidate must consume the exact declared LOD range and pass structural
    validation. Equivalent candidates are collapsed. Competing successful
    interpretations are rejected rather than silently selecting corrupt data.
    """
    successes=[]; failures=[]
    for policy in ('versioned','all-two','all-six'):
        try:
            candidate=_parse_lod_with_material_layout(
                data,start,end,res,version,policy)
            if not any(_same_lod_semantics(candidate,item) for item in successes):
                successes.append(candidate)
        except Exception as ex:
            failures.append((policy,ex))
    if len(successes)==1:
        return successes[0]
    if len(successes)>1:
        policies=', '.join(item['materialLayoutPolicy'] for item in successes)
        raise ValueError(
            f'Ambiguous EmbeddedMaterial layout for LOD {res}: {policies}')
    detail='; '.join(f'{policy}: {type(ex).__name__}: {ex}'
        for policy,ex in failures)
    raise ValueError(f'No valid EmbeddedMaterial layout for LOD {res} ({detail})')

def parse_animations(r):
    # ODOL animation block, matching BisDll.Model.ODOL.Animations.read for v53-v55.
    nclasses = count(r)
    classes = []
    for _ in range(nclasses):
        at = r.u32()
        c = dict(
            type=at,
            name=r.cstr(),
            source=r.cstr(),
            minPhase=r.f32(),
            maxPhase=r.f32(),
            minValue=r.f32(),
            maxValue=r.f32(),
            sourceAddress=r.u32(),
        )
        if at in (0,1,2,3):
            c['angle0']=r.f32(); c['angle1']=r.f32()
        elif at in (4,5,6,7):
            c['offset0']=r.f32(); c['offset1']=r.f32()
        elif at == 8:
            c['axisPos']=r.vec3(); c['axisDir']=r.vec3(); c['angle']=r.f32(); c['axisOffset']=r.f32()
        elif at == 9:
            c['hideValue']=r.f32()
            if r.version >= 55:
                c['hideValue2']=r.f32()  # extra field present in ODOL v55+
        else:
            raise ValueError(f'Unknown ODOL animation type {at}')
        classes.append(c)

    n_anim_lods = r.i32()
    if n_anim_lods < 0 or n_anim_lods > 1000:
        raise ValueError(f'Invalid animation LOD count {n_anim_lods}')

    bones2anims=[]
    for _ in range(n_anim_lods):
        nbones=r.u32()
        if nbones>100000: raise ValueError(f'Invalid animation bone count {nbones}')
        lod=[]
        for __ in range(nbones):
            na=r.u32()
            if na>1000000: raise ValueError(f'Invalid bone animation count {na}')
            lod.append([r.u32() for ___ in range(na)])
        bones2anims.append(lod)

    anims2bones=[]; axes=[]
    for _ in range(n_anim_lods):
        bmap=[]; amap=[]
        for c in classes:
            bone=r.i32(); bmap.append(bone)
            if bone != -1 and c['type'] not in (8,9):
                amap.append((r.vec3(),r.vec3()))
            else:
                amap.append(None)
        anims2bones.append(bmap); axes.append(amap)
    return dict(classes=classes,nAnimLods=n_anim_lods,bones2anims=bones2anims,anims2bones=anims2bones,axes=axes)

def find_v55_lod_table(data, scan_from, resolutions, version=55):
    """Locate the ODOL v55 LOD address table.

    DayZ/Arma v55 can contain an extra pivot/sub-skeleton byte section between
    the animation block and the address table. We scan forward and validate a
    candidate table by fully parsing every referenced LOD to its declared end.
    This avoids relying on a fixed skip length.
    """
    n=len(resolutions)
    table_size=n*4+n*4+n
    file_len=len(data)
    last=file_len-table_size
    if n<=0:
        raise ValueError('ODOL v55 contains no LODs')

    for pos in range(scan_from, last+1):
        try:
            starts=list(struct.unpack_from('<'+'I'*n,data,pos))
            ends=list(struct.unpack_from('<'+'I'*n,data,pos+n*4))
            perms=list(data[pos+n*8:pos+n*8+n])
        except (struct.error,IndexError):
            break

        if any(v not in (0,1) for v in perms):
            continue
        table_end=pos+table_size
        if any(st < table_end or st >= file_len for st in starts):
            continue
        if any(en <= st or en > file_len for st,en in zip(starts,ends)):
            continue
        if len(set(starts)) != len(starts):
            continue

        # Strong validation: each LOD must parse and consume exactly its range.
        ok=True
        for i,(st,en) in enumerate(zip(starts,ends)):
            try:
                parse_lod(data,st,en,resolutions[i],version)
            except Exception:
                ok=False
                break
        if ok:
            return pos,starts,ends,[bool(x) for x in perms]

    raise ValueError(
        f'Could not locate a valid ODOL v55 LOD address table after offset {scan_from}'
    )

def _parse_v55_animation_header(data,search_start,table_pos,nlod):
    """Resolve the v55 animation flag without treating alignment as the flag.

    Binarize-produced ODOL55 files may insert a zero byte immediately before
    hasAnims.  The old parser accepted that padding byte as a false flag and
    consequently reported that a valid animation block was absent.  Try the
    bounded flag positions and select the parse ending closest to the already
    validated LOD table.
    """
    candidates=[]
    for flag_pos in range(search_start,min(search_start+9,table_pos)):
        flag=data[flag_pos]
        if flag not in (0,1):
            continue
        reader=R(data,flag_pos+1,55)
        try:
            animations=parse_animations(reader) if flag else None
        except Exception:
            continue
        if reader.pos>table_pos:
            continue
        if animations is not None:
            animation_lods=animations.get('nAnimLods',-1)
            if animation_lods not in (0,nlod):
                continue
        gap=table_pos-reader.pos
        candidates.append((gap,-flag_pos,animations,flag_pos,reader.pos))
    if not candidates:
        raise ValueError(
            f'Could not locate a valid v55 hasAnims flag near offset {search_start}'
        )
    _gap,_neg_pos,animations,flag_pos,end_pos=min(candidates,key=lambda x:(x[0],x[1]))
    return animations,flag_pos,end_pos

def parse_odol(path):
    data=Path(path).read_bytes(); r=R(data)
    if r.read(4)!=b'ODOL': raise ValueError('not ODOL')
    ver=r.u32()
    if ver not in (53,54,55): raise ValueError(f'Expected ODOL53, ODOL54 or ODOL55, got {ver}')
    r.version=ver; nl=r.i32(); resolutions=[r.f32() for _ in range(nl)]
    # ModelInfo v53-v55. Retain only boundingCenter and total mass.
    r.i32(); r.f32(); r.f32(); r.i32(); r.i32(); r.i32(); r.vec3(); r.u32(); r.u32(); r.f32(); r.vec3(); r.vec3(); r.vec3(); r.vec3(); bounding_center=r.vec3(); r.vec3(); r.vec3(); r.read(36)
    [r.bool() for _ in range(4)]; [r.f32() for _ in range(6)]; r.bool(); r.i32(); r.bool(); r.f32(); r.f32(); r.bool(); r.bool()
    skel=skeleton(r); r.u8()
    n=count(r); r.compressed(n*4)
    mass=r.f32(); r.f32(); r.f32(); r.f32()
    r.u8()  # GeometrySimple, introduced in v53.
    if ver>=54: r.u8()  # GeometryPhys, introduced in v54.
    r.read(6); r.read(1); r.read(5); r.u32(); r.cstr(); r.cstr(); r.bool(); r.u32()

    if ver==55:
        # v55 may insert pivot/sub-skeleton data before the address table and may
        # store LODs in decreasing physical order. Locate/validate the table,
        # then use it as a hard bound while resolving optional flag padding.
        animation_search_start=r.pos
        table_pos,starts,ends,permanent=find_v55_lod_table(
            data,animation_search_start,resolutions,ver
        )
        anims,_flag_pos,_animation_end=_parse_v55_animation_header(
            data,animation_search_start,table_pos,nl
        )
        r.pos=table_pos + nl*8 + nl
    else:
        # hasAnims must be 0/1. Some older variants have non-zero alignment
        # bytes, which can be skipped without confusing zero padding for false.
        flag_pos=r.pos
        has_byte=r.u8(); skipped=0
        while has_byte>1 and skipped<8:
            has_byte=r.u8(); skipped+=1
        if has_byte>1:
            raise ValueError(f'Could not locate hasAnims flag near offset {flag_pos}')
        anims=parse_animations(r) if has_byte else None
        starts=[r.u32() for _ in range(nl)]
        ends=[r.u32() for _ in range(nl)]
        permanent=[r.bool() for _ in range(nl)]

    # LoadableLodInfo metadata only for non-permanent LODs.
    for p in permanent:
        if not p:
            r.i32(); r.u32(); r.i32(); r.u32(); r.bool(); r.i32(); r.f32()

    lods=[parse_lod(data,starts[i],ends[i],resolutions[i],ver) for i in range(nl)]
    return dict(version=ver,lods=lods,bcenter=bounding_center,mass=mass,skeleton=skel,animations=anims)
