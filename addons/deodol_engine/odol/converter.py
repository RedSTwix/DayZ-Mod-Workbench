from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from ..version import SCRIPT_VERSION

CLIP_LAND_MASK=3840

CLIP_DECAL_MASK=12288

CLIP_FOG_MASK=49152

CLIP_USER_MASK=267386880

ANIM_TYPE_NAMES = {
    0:'rotation', 1:'rotationX', 2:'rotationY', 3:'rotationZ',
    4:'translation', 5:'translationX', 6:'translationY', 7:'translationZ',
    8:'direct', 9:'hide'
}

SOURCE_ADDRESS = {0:'clamp',1:'loop',2:'mirror'}

def clip_to_point(c):
    p=0
    land=c & CLIP_LAND_MASK
    if land==256: p|=1
    elif land==512: p|=2
    elif land==1024: p|=4
    elif land==2048: p|=8
    dec=c & CLIP_DECAL_MASK
    if dec==4096: p|=0x100
    elif dec==8192: p|=0x200
    # Match original BisDll's unusual light-hint constants exactly.
    if c & 209715200: p|=0x10
    elif c & 212860928: p|=0x40
    elif c & 211812352: p|=0x80
    elif c & 210763776: p|=0x20
    fog=c & CLIP_FOG_MASK
    if fog==16384: p|=0x1000
    elif fog==32768: p|=0x2000
    user=(c & CLIP_USER_MASK)//1048576
    p |= 65536*user
    return p & 0xffffffff

def section_face_indices(sec,faces):
    cur=0; out=[]
    for i,f in enumerate(faces):
        if cur>=sec['lo'] and cur<sec['hi']: out.append(i)
        cur += 8 + (2 if len(f)==4 else 0)
        if cur>=sec['hi']: break
    return out

def convert_lod(model,lod):
    bc=model['bcenter']; verts=lod['verts']; clips=lod['clips']
    points=[(v[0]+bc[0],v[1]+bc[1],v[2]+bc[2],clip_to_point(clips[i])) for i,v in enumerate(verts)]
    normals=lod['normals']
    outfaces=[]; odol_to_out={}
    uv0=lod['uvs'][0] if lod['uvs'] else [0.0]*(len(verts)*2)
    for sec in lod['sections']:
        for fi in section_face_indices(sec,lod['faces']):
            poly=lod['faces'][fi]; vs=[]
            for idx in reversed(poly): vs.append((idx,idx,uv0[idx*2],uv0[idx*2+1]))
            tex='' if sec['tex']==-1 else lod['textures'][sec['tex']]
            if sec['mat']==-1: mat=sec['mat_inline']
            else: mat=lod['materials'][sec['mat']]['name']
            odol_to_out[fi]=len(outfaces); outfaces.append((vs,0,tex,mat,fi))
    # Most models map all faces exactly once.
    if len(outfaces)!=len(lod['faces']):
        raise ValueError(('not all faces mapped by sections',lod['res'],len(outfaces),len(lod['faces'])))
    tags=[]
    if abs(lod['res']-1e13)/1e13 < 1e-5:
        per=model['mass']/len(points) if points else 0.0
        tags.append(('#Mass#',struct.pack('<'+'f'*len(points),*([per]*len(points)))))
    # UV sets. Output order is output-face order, not raw ODOL face order.
    for ci,uv in enumerate(lod['uvs']):
        b=bytearray(struct.pack('<I',ci))
        for vs,_,_,_,rawfi in outfaces:
            # vs already reversed and contains UV for channel0; for other channels use point idx.
            for pi,ni,u0,v0 in vs:
                b += struct.pack('<ff',uv[pi*2],uv[pi*2+1])
        tags.append(('#UVSet#',bytes(b)))
    for name,val in lod['props']:
        nb=name.encode('latin1')[:64].ljust(64,b'\0'); vb=val.encode('latin1')[:64].ljust(64,b'\0')
        tags.append(('#Property#',nb+vb))
    # Named selections from ODOL. Remap face indices to output ordering.
    # Keep mutable buffers so bone/proxy reconstruction can merge into them.
    selection_buffers={}
    selection_order=[]
    def ensure_selection(name):
        if not name:
            return None
        if name not in selection_buffers:
            selection_buffers[name]=[bytearray(len(points)),bytearray(len(outfaces))]
            selection_order.append(name)
        return selection_buffers[name]

    for s in lod['selections']:
        pair=ensure_selection(s['name'])
        if pair is None:
            continue
        pb,fb=pair; hasw=len(s['weights'])!=0
        for j,vi in enumerate(s['verts']):
            w=1 if not hasw else ((-s['weights'][j]) & 0xff)
            if vi<len(pb) and w:
                pb[vi]=w
        for fi in s['faces']:
            oi=odol_to_out.get(fi)
            if oi is not None: fb[oi]=1
        # Reconstruct sectional selections as official converter does.
        if s['sectional']:
            for si in s['sections']:
                if 0<=si<len(lod['sections']):
                    for fi in section_face_indices(lod['sections'][si],lod['faces']):
                        oi=odol_to_out.get(fi)
                        if oi is not None:
                            fb[oi]=1
                            for vi in lod['faces'][fi]:
                                if vi<len(pb): pb[vi]=1

    # Reconstruct bone-based point selections from VertexBoneRef.
    # This matters for animated/skeletal models where an ODOL LOD may omit
    # explicit point membership even though the skinning data still contains it.
    sk=model.get('skeleton') or {}
    bones=sk.get('bones') or []
    subs=lod.get('subs') or []
    vbr=lod.get('vbr') or []
    if bones and subs and vbr:
        for vi,pairs in enumerate(vbr):
            if vi>=len(points):
                break
            for sub_index,weight in pairs:
                if sub_index>=len(subs):
                    continue
                bone_index=subs[sub_index]
                if not (0<=bone_index<len(bones)):
                    continue
                bone_name=bones[bone_index][0]
                pair=ensure_selection(bone_name)
                if pair is None:
                    continue
                pb,_=pair
                # Official conversion stores the negated ODOL weight byte.
                w=(-weight) & 0xff
                if w and w>pb[vi]:
                    pb[vi]=w

    # Reconstruct proxy membership using the proxy's exact named-selection
    # face whenever ODOL retained it.  Several proxies are commonly packed in
    # one shared section; marking the complete section for every proxy makes
    # each MLOD proxy selection contain all proxy triangles.  Binarize then
    # rejects the selections and silently emits an ODOL with zero proxies.
    #
    # Only use section membership as a fallback when it identifies one face
    # unambiguously.  Producing no proxy is safer than inventing a many-face
    # proxy selection, and the semantic verifier will reject that conversion.
    sels=lod.get('selections') or []
    sections=lod.get('sections') or []
    for proxy in lod.get('proxies') or []:
        # tuple: (model, matrix, sequenceID, namedSelectionIndex, boneIndex, sectionIndex)
        if len(proxy)<6:
            continue
        nsi=proxy[3]; si=proxy[5]
        if not (0<=nsi<len(sels)) or not (0<=si<len(sections)):
            continue
        name=sels[nsi].get('name','')
        pair=ensure_selection(name)
        if pair is None:
            continue
        pb,fb=pair
        selection=sels[nsi]
        section_faces=section_face_indices(sections[si],lod['faces'])
        exact_faces=[fi for fi in selection.get('faces') or [] if fi in odol_to_out]
        if exact_faces:
            proxy_faces=exact_faces
        else:
            selected_verts=set(selection.get('verts') or [])
            vertex_matches=[
                fi for fi in section_faces
                if lod['faces'][fi] and set(lod['faces'][fi]).issubset(selected_verts)
            ]
            if len(vertex_matches)==1:
                proxy_faces=vertex_matches
            elif len(section_faces)==1:
                proxy_faces=section_faces
            else:
                proxy_faces=[]
        for fi in proxy_faces:
            oi=odol_to_out.get(fi)
            if oi is None:
                continue
            fb[oi]=1
            for vi in lod['faces'][fi]:
                if vi<len(pb): pb[vi]=1

    for name in selection_order:
        pb,fb=selection_buffers[name]
        tags.append((name,bytes(pb+fb)))
    for t,pts in lod['frames']:
        b=bytearray(struct.pack('<f',t));
        for p in pts: b+=struct.pack('<fff',*p)
        tags.append(('#Animation#',bytes(b)))
    return points,normals,outfaces,tags

def write_mlod(model,outpath):
    out=bytearray(b'MLOD'+struct.pack('<II',257,len(model['lods'])))
    for lod in model['lods']:
        points,normals,faces,tags=convert_lod(model,lod)
        out += b'P3DM'+struct.pack('<IIIIII',28,256,len(points),len(normals),len(faces),0)
        for x,y,z,fl in points: out+=struct.pack('<fffI',x,y,z,fl)
        for x,y,z in normals: out+=struct.pack('<fff',x,y,z)
        for vs,flags,tex,mat,rawfi in faces:
            out+=struct.pack('<I',len(vs))
            for j in range(4):
                if j<len(vs): pi,ni,u,v=vs[j]
                else: pi=ni=0; u=v=0.0
                out+=struct.pack('<IIff',pi,ni,u,v)
            out+=struct.pack('<I',flags)+tex.encode('latin1')+b'\0'+mat.encode('latin1')+b'\0'
        out+=b'TAGG'
        for name,buf in tags:
            out+=b'\x01'+name.encode('latin1')+b'\0'+struct.pack('<I',len(buf))+buf
        out+=b'\x01#EndOfFile#\x00'+struct.pack('<I',0)+struct.pack('<f',lod['res'])
    Path(outpath).write_bytes(out)

def _fmt(v):
    if isinstance(v,float):
        if abs(v)<1e-8: v=0.0
        return f'{v:.9g}'
    return str(v)

def _vsub(a,b): return (a[0]-b[0],a[1]-b[1],a[2]-b[2])

def _vadd(a,b): return (a[0]+b[0],a[1]+b[1],a[2]+b[2])

def _vmul(a,s): return (a[0]*s,a[1]*s,a[2]*s)

def _vdot(a,b): return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]

def _vlen(a): return math.sqrt(max(0.0,_vdot(a,a)))

def _vnorm(a):
    n=_vlen(a)
    return (0.0,0.0,0.0) if n<1e-12 else (a[0]/n,a[1]/n,a[2]/n)

def _line_distance(point, line_p, line_dir):
    d=_vnorm(line_dir)
    if _vlen(d)<1e-12: return float('inf')
    q=_vsub(point,line_p)
    proj=_vmul(d,_vdot(q,d))
    return _vlen(_vsub(q,proj))

def _name_key(s):
    return ''.join(ch.lower() for ch in s if ch.isalnum())

def _selection_axis_candidates(model):
    """Return named selections that geometrically look like axes.

    Works across all LODs. A candidate may have exactly 2 points, or more
    points as long as they are essentially collinear. Coordinates are converted
    to the same MLOD space used by write_mlod (ODOL vertex + boundingCenter).
    """
    bc=model.get('bcenter',(0.0,0.0,0.0))
    out=[]
    seen=set()
    for li,lod in enumerate(model.get('lods',[])):
        verts=lod.get('verts') or []
        for s in lod.get('selections') or []:
            ids=[]
            for vi in s.get('verts') or []:
                if 0 <= vi < len(verts) and vi not in ids:
                    ids.append(vi)
            if len(ids)<2:
                continue
            pts=[_vadd(verts[vi],bc) for vi in ids]

            # Find the farthest point pair, which defines the candidate axis.
            best=None; best_len=-1.0
            for ai in range(len(pts)):
                for bi in range(ai+1,len(pts)):
                    d=_vlen(_vsub(pts[bi],pts[ai]))
                    if d>best_len:
                        best_len=d; best=(pts[ai],pts[bi])
            if not best or best_len<1e-6:
                continue
            p0,p1=best; direction=_vsub(p1,p0)

            # Axis selections normally have two points. Also accept a collinear
            # multi-point selection, but reject ordinary geometry selections.
            max_off=max(_line_distance(p,p0,direction) for p in pts)
            tolerance=max(0.002,best_len*0.002)
            if len(pts)>2 and max_off>tolerance:
                continue

            key=(s.get('name','').lower(), tuple(round(x,5) for x in p0),
                 tuple(round(x,5) for x in p1))
            if key in seen:
                continue
            seen.add(key)
            out.append(dict(
                name=s.get('name',''),
                lod_index=li,
                resolution=lod.get('res',0.0),
                p0=p0,p1=p1,dir=direction,length=best_len,
                point_count=len(pts),line_error=max_off
            ))
    return out

def _compiled_axis_hypotheses(axis):
    """ODOL axisData is encountered in two practical interpretations.

    Some builds expose the pair as two points (P0,P1), while others are best
    interpreted as (axis position, axis direction). Return both direction
    hypotheses and let geometric matching decide.
    """
    if not axis:
        return []
    a,b=axis
    hyps=[]
    d_endpoint=_vsub(b,a)
    if _vlen(d_endpoint)>1e-8:
        hyps.append(('endpoints',a,d_endpoint))
    if _vlen(b)>1e-8:
        hyps.append(('pos_dir',a,b))
    return hyps

def _semantic_axis_score(candidate_name, anim_name, selection, source):
    c=_name_key(candidate_name)
    if not c: return 0.0
    score=0.0
    stems=[]
    for s in (selection,anim_name,source):
        k=_name_key(s)
        if k and k not in stems: stems.append(k)
    for stem in stems:
        # Universal naming relationships; no model/door-specific hardcoding.
        if c == stem+'axis' or c == 'axis'+stem:
            score=max(score,40.0)
        elif 'axis' in c and stem in c:
            score=max(score,28.0)
        elif c.startswith(stem) or c.endswith(stem):
            score=max(score,10.0)
    if 'axis' in c:
        score += 5.0
    return score

def recover_axis_selection(model, compiled_axis, anim_name='', selection='', source=''):
    """Find the MLOD named selection corresponding to a compiled ODOL axis.

    Primary evidence is geometry (axis direction and, when usable, position).
    Selection naming is only a tie-breaker/fallback. Returns
    (name, sign, confidence, explanation) or (None,1,0,reason).
    """
    candidates=_selection_axis_candidates(model)
    if not candidates:
        return None,1.0,0.0,'no axis-like named selections found'

    hyps=_compiled_axis_hypotheses(compiled_axis)
    bc=model.get('bcenter',(0.0,0.0,0.0))
    scored=[]
    for cand in candidates:
        cd=_vnorm(cand['dir'])
        semantic=_semantic_axis_score(cand['name'],anim_name,selection,source)
        best_geo=None
        for mode,pos,d in hyps:
            dn=_vnorm(d)
            if _vlen(dn)<1e-12: continue
            dot=_vdot(cd,dn)
            alignment=abs(dot)
            # Candidate axis and compiled data may use pre/post-boundingCenter
            # coordinates. Test all sensible origins; direction remains invariant.
            pos_tests=[pos,_vadd(pos,bc),_vsub(pos,bc)]
            line_dist=min(_line_distance(pt,cand['p0'],cand['dir']) for pt in pos_tests)
            endpoint_dist=min(
                _vlen(_vsub(pt,endpoint))
                for pt in pos_tests
                for endpoint in (cand['p0'],cand['p1'])
            )
            scale=max(cand['length'],1.0)
            pos_quality=max(0.0,1.0-min(line_dist/scale,1.0))
            # Direction dominates; position and semantic relation break ties.
            geo=alignment*100.0 + pos_quality*12.0
            if best_geo is None or geo>best_geo[0]:
                best_geo=(geo,dot,alignment,line_dist,endpoint_dist,mode)
        if best_geo is None:
            # No usable compiled direction: semantic fallback only.
            total=semantic
            scored.append((total,cand,1.0,0.0,float('inf'),float('inf'),'semantic'))
        else:
            geo,dot,alignment,line_dist,endpoint_dist,mode=best_geo
            total=geo+semantic
            scored.append((total,cand,1.0 if dot>=0 else -1.0,
                           alignment,line_dist,endpoint_dist,mode))

    scored.sort(key=lambda x:(x[0],-x[5]),reverse=True)
    best=scored[0]
    second=scored[1] if len(scored)>1 else None
    total,cand,sign,alignment,line_dist,endpoint_dist,mode=best

    # A generic ODOL rotation stores an axis position and direction. Some
    # models contain multiple practically collinear Memory selections, so line
    # distance alone cannot identify which named axis the compiled model used.
    # An endpoint that reproduces the compiled position to float precision is
    # decisive when it is unique and every competing endpoint is clearly
    # separated. This remains geometric/provenance evidence; no mod or
    # selection-name special case is involved.
    # Float-exact evidence remains limited to 0.01 mm.  A competing endpoint
    # only needs to be 0.1 mm away to be geometrically distinct: ODOL models
    # legitimately use submillimetre offsets (for example parallel animation
    # axes), and requiring a full millimetre discarded otherwise exact proof.
    endpoint_exact_tol=1.0e-5
    endpoint_separated_tol=1.0e-4
    exact_endpoint_candidates=[
        item for item in scored
        if item[3]>=0.995 and item[5]<=endpoint_exact_tol
    ]
    endpoint_position=False
    if len(exact_endpoint_candidates)==1:
        exact=exact_endpoint_candidates[0]
        competitors=[item for item in scored if item is not exact]
        if all(item[5]>=endpoint_separated_tol for item in competitors):
            best=exact
            second=max(competitors,key=lambda x:x[0]) if competitors else None
            total,cand,sign,alignment,line_dist,endpoint_dist,mode=best
            endpoint_position=True

    # Require either strong geometric agreement or a very strong semantic match.
    semantic=_semantic_axis_score(cand['name'],anim_name,selection,source)
    strong_geometry=alignment>=0.995
    strong_semantic=semantic>=40.0
    margin=total-(second[0] if second else -999.0)

    if not strong_geometry and not strong_semantic:
        return None,1.0,total,(
            f'best candidate {cand["name"]!r} is not convincing '
            f'(alignment={alignment:.5f}, semantic={semantic:.1f})'
        )
    # Parallel axis selections can be very close together (latches, handles,
    # brackets, lids).  Direction alone cannot distinguish them, and the old
    # score normalized position by axis length, making a centimetre-scale
    # separation look like a sub-point tie.  ODOL generic rotation stores an
    # axis position as well as its direction.  When the best candidate line
    # passes through that compiled position to float precision while the next
    # candidate is at least 1 mm away, position is decisive geometric evidence.
    # Keep genuine coincident/near-coincident lines ambiguous.
    precise_position=False
    if second and margin<1.0 and semantic<40.0 and not endpoint_position:
        second_line_dist=second[4]
        exact_tol=1.0e-5
        separated_tol=1.0e-3
        precise_position=(
            line_dist <= exact_tol and
            second_line_dist >= separated_tol
        )

    if (second and margin<1.0 and semantic<40.0 and
            not precise_position and not endpoint_position):
        return None,1.0,total,(
            f'ambiguous axis candidates {cand["name"]!r} and {second[1]["name"]!r}'
        )

    if endpoint_position:
        position_note=', position=exact-endpoint-disambiguation'
    elif precise_position:
        position_note=', position=exact-line-disambiguation'
    else:
        position_note=''
    why=(f'{mode}, alignment={alignment:.6f}, lineDistance={line_dist:.6g}, '
         f'endpointDistance={endpoint_dist:.6g}, '
         f'semantic={semantic:.1f}, LOD={cand["resolution"]:.9g}{position_note}')
    return cand['name'],sign,total,why

def _cardinal_translation_variant(axis):
    """Safe fallback for generic translation only.

    Translation has no hinge/pivot, so a cardinal direction can be represented
    by translationX/Y/Z if no named axis selection can be recovered.
    """
    best=None
    for mode,pos,d in _compiled_axis_hypotheses(axis):
        dn=_vnorm(d)
        mags=[abs(x) for x in dn]
        if not mags: continue
        k=max(range(3),key=lambda i:mags[i])
        if mags[k] < 0.999:
            continue
        sign=1.0 if dn[k]>=0 else -1.0
        score=mags[k]
        if best is None or score>best[0]:
            best=(score,'translation'+'XYZ'[k],sign,mode)
    return None if best is None else (best[1],best[2],best[3])

def recover_model_sections(model):
    """Recover CfgModels sections from ODOL named-selection metadata.

    ODOL stores whether a named selection was compiled as a model section.
    Re-emitting that information is required for hiddenSelectionsTextures and
    material swaps to survive an ODOL -> MLOD -> ODOL round trip.
    """
    result=[]
    seen=set()
    for lod in model.get('lods') or []:
        for selection in lod.get('selections') or []:
            if not selection.get('sectional'):
                continue
            name=(selection.get('name') or '').strip()
            key=name.casefold()
            if not name or key in seen or key.startswith('proxy:'):
                continue
            seen.add(key)
            result.append(name)
    return result


def _cfg_string(value):
    return str(value).replace('\\','\\\\').replace('"','\\"')


def model_cfg_piece(model, model_class):
    sk=model.get('skeleton') or {}
    anim=model.get('animations') or {}
    sections=recover_model_sections(model)
    if not sk.get('name') and not sections: return None
    classes=anim.get('classes') or []
    bones=sk.get('bones',[])
    first_axes=anim['axes'][0] if anim.get('axes') else [None]*len(classes)
    first_bmap=anim['anims2bones'][0] if anim.get('anims2bones') else [-1]*len(classes)
    lines=[]
    lines.append(f'// Recovered automatically from ODOL{model["version"]}: {model_class}.p3d')
    lines.append(f'class {model_class}')
    lines.append('{')
    lines.append(f'    skeletonName = "{_cfg_string(sk.get("name", ""))}";')
    lines.append('    sectionsInherit = "";')
    section_values=', '.join(f'"{_cfg_string(name)}"' for name in sections)
    lines.append(f'    sections[] = {{{section_values}}};')
    warnings=[]
    recoveries=[]
    if sections:
        recoveries.append(
            f'{model_class}: recovered {len(sections)} ODOL model section(s): '
            + ', '.join(sections)
        )
    if classes:
        lines.append('    class Animations')
        lines.append('    {')
    for i,c in enumerate(classes):
        bi=first_bmap[i] if i<len(first_bmap) else -1
        selection=bones[bi][0] if 0<=bi<len(bones) else ''
        typ=ANIM_TYPE_NAMES.get(c['type'],f'unknown_{c["type"]}')
        axis=first_axes[i] if i<len(first_axes) else None
        sign=1.0
        axis_name=None

        # Generic rotation/translation normally carries a bone mapping and
        # compiled axis data.  However, ODOL also supports *unbound* animation
        # classes: anims2bones=-1, therefore the binary stores no axis at all.
        # These classes are commonly used as source/phase controllers (for
        # example vehicle seat phases) and do not animate model geometry.
        # In that case, trying to invent an axis is a reconstruction error.
        unbound_compiled=(bi < 0 and not selection and axis is None)
        if c['type'] in (0,4):
            if unbound_compiled:
                recoveries.append(
                    f'{model_class}:{c["name"]} preserved unbound {typ} '
                    f'(anims2bones=-1; no selection/compiled axis in ODOL)'
                )
            else:
                axis_name,axis_sign,confidence,why=recover_axis_selection(
                    model,axis,c.get('name',''),selection,c.get('source','')
                )
                if axis_name:
                    sign=axis_sign
                    recoveries.append(
                        f'{model_class}:{c["name"]} axis -> {axis_name} ({why})'
                    )
                elif c['type']==4:
                    # For translation only, a cardinal fallback is lossless.
                    fallback=_cardinal_translation_variant(axis)
                    if fallback:
                        typ,sign,mode=fallback
                        recoveries.append(
                            f'{model_class}:{c["name"]} axis -> {typ} ({mode}; no named axis required)'
                        )
                    else:
                        warnings.append(
                            f'{model_class}:{c["name"]} axis could not be reconstructed automatically: {why}'
                        )
                else:
                    # Never collapse generic rotation to rotationX/Y/Z merely from
                    # direction: that would discard the hinge/pivot position.
                    warnings.append(
                        f'{model_class}:{c["name"]} rotation axis could not be reconstructed automatically: {why}'
                    )

        lines.append(f'        class {c["name"]}')
        lines.append('        {')
        lines.append(f'            type = "{typ}";')
        lines.append(f'            source = "{c["source"]}";')
        if selection: lines.append(f'            selection = "{selection}";')
        if axis_name and c['type'] in (0,4):
            lines.append(f'            axis = "{axis_name}";')
        lines.append(f'            sourceAddress = "{SOURCE_ADDRESS.get(c["sourceAddress"],"clamp")}";')
        lines.append(f'            minValue = {_fmt(c["minValue"])};')
        lines.append(f'            maxValue = {_fmt(c["maxValue"])};')
        if c['type'] in (0,1,2,3):
            lines.append(f'            angle0 = {_fmt(c["angle0"]*sign)};')
            lines.append(f'            angle1 = {_fmt(c["angle1"]*sign)};')
        elif c['type'] in (4,5,6,7):
            lines.append(f'            offset0 = {_fmt(c["offset0"]*sign)};')
            lines.append(f'            offset1 = {_fmt(c["offset1"]*sign)};')
        elif c['type']==9:
            lines.append(f'            hideValue = {_fmt(c["hideValue"])};')
            if 'hideValue2' in c:
                lines.append(f'            unhideValue = {_fmt(c["hideValue2"])};')
        elif c['type']==8:
            # Direct animations carry their axis explicitly; no named Memory
            # selection is required.
            ap=c.get('axisPos',(0.0,0.0,0.0))
            ad=c.get('axisDir',(0.0,0.0,0.0))
            lines.append(f'            axisPos[] = {{{_fmt(ap[0])}, {_fmt(ap[1])}, {_fmt(ap[2])}}};')
            lines.append(f'            axisDir[] = {{{_fmt(ad[0])}, {_fmt(ad[1])}, {_fmt(ad[2])}}};')
            lines.append(f'            angle = {_fmt(c["angle"])};')
            lines.append(f'            axisOffset = {_fmt(c["axisOffset"])};')
        lines.append('        };')
    if classes:
        lines.append('    };')
    lines.append('};')
    return '\n'.join(lines), warnings, recoveries

def write_recovered_model_cfg(folder, entries):
    # entries: [(model_class, model), ...].  Skeleton-only and section-only
    # ODOL models require model.cfg so a rebuilt PBO retains their semantics.
    eligible=[
        e for e in entries
        if (e[1].get('skeleton') or {}).get('name') or recover_model_sections(e[1])
    ]
    if not eligible: return []
    skels={}
    for _,m in eligible:
        sk=m.get('skeleton') or {}
        if sk.get('name'): skels.setdefault(sk['name'],sk)
    text=['// AUTO-RECOVERED model.cfg from ODOL animation data',
          '// Review before release if the report mentions non-cardinal/direct animations.',
          '', 'class CfgSkeletons', '{']
    for name,sk in skels.items():
        text.append(f'    class {name}')
        text.append('    {')
        text.append(f'        isDiscrete = {1 if sk.get("discrete") else 0};')
        text.append('        skeletonInherit = "";')
        vals=[]
        for b,p in sk.get('bones',[]): vals += [b,p]
        arr=', '.join(f'"{x}"' for x in vals)
        text.append(f'        skeletonBones[] = {{{arr}}};')
        text.append('    };')
    text += ['};','','class CfgModels','{']
    warnings=[]
    verification=[]
    for cls,m in eligible:
        piece=model_cfg_piece(m,cls)
        if piece:
            body,w,recovered=piece
            for line in body.splitlines(): text.append('    '+line)
            warnings += w
            for msg in recovered:
                print(f'[ANIM] {msg}')
            anim=m.get('animations') or {}
            classes=anim.get('classes') or []
            missing_classes=[]
            for c in classes:
                if f'class {c.get("name","")}' not in body:
                    missing_classes.append(c.get('name',''))
            expected_skeleton=(m.get('skeleton') or {}).get('name','')
            skeleton_missing=(f'skeletonName = "{expected_skeleton}";' not in body)
            expected_sections=recover_model_sections(m)
            missing_sections=[
                name for name in expected_sections
                if f'"{_cfg_string(name)}"' not in body
            ]
            axes0=(anim.get('axes') or [[]])[0] if anim.get('axes') else []
            bmap0=(anim.get('anims2bones') or [[]])[0] if anim.get('anims2bones') else []
            unbound_preserved=0
            for ai,c in enumerate(classes):
                if c.get('type') not in (0,4):
                    continue
                bone=(bmap0[ai] if ai < len(bmap0) else -1)
                ax=(axes0[ai] if ai < len(axes0) else None)
                if bone < 0 and ax is None:
                    unbound_preserved += 1
            verification.append(dict(
                model=cls,skeleton=expected_skeleton,
                bones=len((m.get('skeleton') or {}).get('bones') or []),
                animations=len(classes),recoveries=list(recovered),warnings=list(w),
                unbound_preserved=unbound_preserved,
                sections=expected_sections,missing_sections=missing_sections,
                missing_classes=missing_classes,skeleton_missing=skeleton_missing
            ))
    text += ['};','']
    folder=Path(folder)
    existing=folder/'model.cfg'
    recovered=folder/'model_recovered.cfg'
    auto_marker='// AUTO-RECOVERED model.cfg from ODOL animation data'

    # Re-running into the same destination must not leave an older auto-generated
    # model.cfg active while silently writing a newer model_recovered.cfg.
    if existing.exists():
        try:
            head=existing.read_text(encoding='utf-8',errors='ignore')[:256]
        except Exception:
            head=''
        if auto_marker in head:
            target=existing
        else:
            target=recovered
    else:
        target=existing

    target.write_text('\n'.join(text),encoding='utf-8')
    print(f'[ANIM] model.cfg written: {target}')
    cfg_errors=[]
    for v in verification:
        cfg_errors.extend(f'{v["model"]}: missing animation class {x}' for x in v['missing_classes'])
        if v.get('skeleton_missing'):
            cfg_errors.append(f'{v["model"]}: missing skeletonName {v["skeleton"]}')
        cfg_errors.extend(f'{v["model"]}: missing model section {x}' for x in v['missing_sections'])
        cfg_errors.extend(f'{v["model"]}: {x}' for x in v['warnings'])
    cfg_status='SEMANTIC-EXACT' if not cfg_errors else 'WARNING/LOSS'
    vr=folder/'MODEL_CFG_EQUIVALENCE_VERIFICATION.txt'
    vlines=[
        'Recovered model.cfg semantic verification',
        f'Converter: {SCRIPT_VERSION}',
        f'model.cfg: {target.resolve()}',
        f'Result: {cfg_status}',
        f'Models checked: {len(verification)}',
        f'Warnings/differences: {len(cfg_errors)}',''
    ]
    for v in verification:
        vlines.append(f'- {v["model"]}: skeleton={v["skeleton"]!r} bones={v["bones"]} sections={len(v.get("sections",[]))} animations={v["animations"]} recoveries={len(v["recoveries"])} unboundPreserved={v.get("unbound_preserved",0)} warnings={len(v["warnings"])}')
        for rmsg in v['recoveries']:
            vlines.append(f'    RECOVERED: {rmsg}')
        for wmsg in v['warnings']:
            vlines.append(f'    WARNING: {wmsg}')
        if v.get('skeleton_missing'):
            vlines.append(f'    MISSING SKELETON: {v["skeleton"]}')
        for ms in v.get('missing_sections',[]):
            vlines.append(f'    MISSING SECTION: {ms}')
        for mc in v['missing_classes']:
            vlines.append(f'    MISSING CLASS: {mc}')
    vr.write_text('\n'.join(vlines)+'\n',encoding='utf-8')
    print(f'[ANIM] verification: {cfg_status} -> {vr}')
    return warnings
