using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Remold.Core.Migoto;

/// <summary>
/// The stamped HLSL compute shaders of the pooled swap — recover, convert, skin. Emitted verbatim with
/// counts/offsets/cbuffer sizes stamped in; the emitted bytes are the swap's emission contract and must
/// not be altered.
/// </summary>
public static class ComputeTemplates
{
    // %(...)d / %(...)s tokens are stamped by the Emit* methods; line endings are normalised to LF on
    // emit so output is byte-stable regardless of this file's on-disk endings

    // The convert pass gives each pool part a constant buffer of its own, from this register up; b13 is
    // the anchor's and the shader's other bindings sit below b5.
    const int FirstPartRegister = 5, LastPartRegister = 12;

    /// <summary>Pool parts one convert pass can bind constants for. A REGISTER-RANGE fact, not a policy:
    /// <see cref="EmitConvert"/> lays each part's cbuffer out at a register of its own, and the shader has
    /// exactly this many free before b13, the anchor's.</summary>
    public const int MaxPartCBuffers = LastPartRegister - FirstPartRegister + 1;

    public const string RecoverTemplate =
@"// Pooled recover for one part: a = Cpinv * posed(anchor verts), SCATTERED into the shared union
// palette via Map (part-local bone -> union bone). The operator is SLIM: each bone reads only its
// own anchor vertices (Sel), not the whole mesh. Widths are RAGGED — Off carries (base,width) per
// bone, so a bone that needs every vertex costs its neighbours nothing. Rows land in THIS PART'S
// draw space; the convert pass rebases them into the anchor's space before skinning.
// ROWS = 4*partBones.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);
Buffer<float>         Cpinv  : register(t1);   // per bone: 4 rows of `width` coefficients, from 4*base
Buffer<uint>          Map    : register(t2);   // partBones entries: local bone -> union bone, or 0xFFFFFFFF
Buffer<uint>          Sel    : register(t3);   // anchor vertex indices, bone b at [base, base+width)
Buffer<uint>          Off    : register(t4);   // 2 per bone: base, width
RWStructuredBuffer<float4> palOut : register(u1); // 4*unionBones rows (shared across all pool parts)
static const uint ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    uint sbase=Off[localBone<<1], width=Off[(localBone<<1)|1];
    // These pseudo-inverse rows can be ill-conditioned. Keep the reduction ordered and compensate
    // its rounding error so driver-specific contraction/reassociation cannot amplify it into the pose.
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint cbase=(sbase<<2)+comp*width;
    for(uint t=0;t<width;t++){
        precise float3 term=Cpinv[cbase+t]*q[Sel[sbase+t]].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    palOut[(u<<2)|comp]=float4(a,(comp==3)?1.0:0.0);   // scatter into this bone's union slot
}
";

    public const string RecoverDenseTemplate =
@"// Pooled recover for one part: a = Cpinv * posed, SCATTERED into the shared union palette
// via Map (part-local bone -> union bone). DENSE operator: the slim layout would not have been
// smaller for this part, so each row spans every vertex. Rows land in THIS
// PART'S draw space; the convert pass rebases them into the anchor's space before skinning.
// ROWS = 4*partBones; N = this part's verts.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);
Buffer<float>         Cpinv  : register(t1);
Buffer<uint>          Map    : register(t2);   // partBones entries: local bone -> union bone, or 0xFFFFFFFF
RWStructuredBuffer<float4> palOut : register(u1); // 4*unionBones rows (shared across all pool parts)
static const uint N=%(N)d, ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    // These pseudo-inverse rows can be ill-conditioned. Keep the reduction ordered and compensate
    // its rounding error so driver-specific contraction/reassociation cannot amplify it into the pose.
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint base=i*N;
    for(uint v=0;v<N;v++){
        precise float3 term=Cpinv[base+v]*q[v].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    palOut[(u<<2)|comp]=float4(a,(comp==3)?1.0:0.0);   // scatter into this bone's union slot
}
";

    public const string ConvertTemplate =
@"// Rebase every union bone's palette rows from its OWNER part's draw space into the
// anchor's:  row' = row . K,  K = W_owner . inverse(W_anchor).  W = objectToWorld = vs-cb1 c0..c3,
// row-vector convention (c4..c7 is the root inverse, NOT necessarily this cb's own inverse, so
// the inverse is computed here instead of read).
// Runs at the anchor draw, where every part's cb was captured at the same draw as its vb0 — a
// segment and its K always come from the same frame, so conversion never mixes frames.
%(PART_CBUFFERS)s
cbuffer AnchorCB : register(b13) { float4 WA[4]; }   // parts occupy b5..b12 (max 8), anchor b13
StructuredBuffer<float4> palRaw    : register(t0);
Buffer<uint>             ownerPart : register(t1);   // per union bone: owning part index
RWStructuredBuffer<float4> palOut  : register(u1);
static const uint ROWS=%(ROWS)d;

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint bone=i>>2;
    uint pi=ownerPart[bone];
    float4x4 WP;
%(PART_SELECT)s
    float4x4 K=mul(WP, AffineInverse(WA[0],WA[1],WA[2],WA[3]));
    palOut[i]=mul(palRaw[i], K);
}
";

    public const string ConvertWitnessTemplate =
@"// Rebase every union bone's palette rows from its OWNER part's draw space into the anchor's,
// with K solved from GEOMETRY instead of draw constants: each non-anchor part designates a
// WITNESS bone it shares with the anchor. Both parts recover that bone (each in its own space);
// the recoveries land in reserved palette slots via the scatter maps, and
// K = inverse(M_witness_part) . M_witness_anchor. Constants stay untouched — some renderers
// (the battle movement-preview hologram) bind vs-cb1 as a WINDOW into one shared buffer
// (VSSetConstantBuffers1 FirstConstant), which a whole-resource copy cannot see through.
StructuredBuffer<float4> palRaw    : register(t0);   // 4*(unionBones + witness slots) rows
Buffer<uint>             ownerPart : register(t1);   // per union bone: owning part index
RWStructuredBuffer<float4> palOut  : register(u1);
static const uint ROWS=%(ROWS)d;      // 4*unionBones — witness slots beyond are inputs only
static const uint ANCHOR=%(ANCHOR)d;
static const uint2 WIT[%(P)d] = { %(WIT)s };   // per part: base row of (partSide, anchorSide) witness; x=0xFFFFFFFF = no witness (keep the row as-is)

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint bone=i>>2;
    uint pi=ownerPart[bone];
    if(pi==ANCHOR || pi>=%(P)d || WIT[pi].x==0xFFFFFFFF){ palOut[i]=palRaw[i]; return; }
    uint2 w=WIT[pi];
    float4x4 MP=float4x4(palRaw[w.x],palRaw[w.x+1],palRaw[w.x+2],palRaw[w.x+3]);
    float4x4 MA=float4x4(palRaw[w.y],palRaw[w.y+1],palRaw[w.y+2],palRaw[w.y+3]);
    float4x4 K=mul(AffineInverse(MP[0],MP[1],MP[2],MP[3]), MA);
    palOut[i]=mul(palRaw[i], K);
}
";

    // The two row-solve bodies a group member's fused shader is stamped with — the same operator layouts
    // the recover shaders read, wrapped as a function so the witness variant can call it for the witness
    // bone as well as for the group bone the thread owns.
    const string GroupRowSlim =
@"Buffer<uint>          Sel    : register(t3);   // anchor vertex indices, bone b at [base, base+width)
Buffer<uint>          Off    : register(t4);   // 2 per bone: base, width
float4 Row(uint b, uint comp){
    uint sbase=Off[b<<1], width=Off[(b<<1)|1];
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint cbase=(sbase<<2)+comp*width;
    for(uint t=0;t<width;t++){
        precise float3 term=Cpinv[cbase+t]*q[Sel[sbase+t]].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    return float4(a,(comp==3)?1.0:0.0);
}";

    const string GroupRowDense =
@"static const uint N=%(N)d;   // this member mesh's verts — every row spans all of them
float4 Row(uint b, uint comp){
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint base=((b<<2)|comp)*N;
    for(uint v=0;v<N;v++){
        precise float3 term=Cpinv[base+v]*q[v].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    return float4(a,(comp==3)?1.0:0.0);
}";

    public const string GroupFuseTemplate =
@"// One wardrobe-group member's FALLBACK dispatch, fused: recover this group's bones from the MEMBER's
// posed vertices and rebase the rows into the anchor's draw space in the same dispatch. This variant
// runs at the member's OWN draw — the one placement where its constants copy and its geometry are
// same-frame by construction — and only for a lod0 sharing no sound bone with the anchor, where no
// geometric K exists; its write order against the anchor's chain follows the frame's draw order.
// Rows land in the group's APPENDED slot region of the CONVERTED palette, past the union and the
// witness slots; the convert passes write only union rows, so the copy round-trip carries these
// through untouched.
// K comes from CONSTANTS: W_member is this draw's own vs-cb1, W_anchor the anchor's captured one.
// ROWS = 4*groupBones; BASE = the group's first appended slot.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);   // the member's posed vb0, captured at this draw
Buffer<float>         Cpinv  : register(t1);
Buffer<uint>          Map    : register(t2);   // per GROUP bone: this member's local bone, or 0xFFFFFFFF
RWStructuredBuffer<float4> palOut : register(u1);
cbuffer MemberCB : register(b5)  { float4 WM[4]; }
cbuffer AnchorCB : register(b13) { float4 WA[4]; }
static const uint ROWS=%(ROWS)d, BASE=%(BASE)d;
%(ROWFN)s

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint g=i>>2, comp=i&3;
    uint b=Map[g];
    if(b==0xFFFFFFFF) return;   // this member cannot condition the bone -> leave the row alone
    float4x4 K=mul(float4x4(WM[0],WM[1],WM[2],WM[3]), AffineInverse(WA[0],WA[1],WA[2],WA[3]));
    palOut[((BASE+g)<<2)|comp]=mul(Row(b,comp), K);
}
";

    public const string GroupFuseWitnessTemplate =
@"// One wardrobe-group member mesh's fused recover+rebase, run from the ANCHOR's chain gated on the
// mesh's presence latch (last frame's draw stream): recover this group's bones from the MEMBER's
// posed vertices — a by-ref capture, current-frame at the chain — and rebase the rows into the
// anchor's draw space in the same dispatch.
// K comes from GEOMETRY, not constants: in the chain the member's constants copy is from its own
// last draw (frame-mixing), and tier renderers can bind vs-cb1 as a window into a shared buffer a
// whole-resource copy reads wrongly. Both sides recover one WITNESS bone the member and the anchor
// pose soundly — the member's side inline here (a UAV write another thread makes is not readable in
// the same dispatch), the anchor's read out of the raw palette row its own recover wrote earlier in
// this same chain, this frame — and K = inverse(M_witness_member) . M_witness_anchor.
// ROWS = 4*groupBones; BASE = the group's first appended slot.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx>    q      : register(t0);   // the member's posed vb0, captured at this draw
Buffer<float>            Cpinv  : register(t1);
Buffer<uint>             Map    : register(t2);   // per GROUP bone: this member's local bone, or 0xFFFFFFFF
StructuredBuffer<float4> palRaw : register(t5);   // the RAW palette: the anchor's own recovered rows
RWStructuredBuffer<float4> palOut : register(u1);
static const uint ROWS=%(ROWS)d, BASE=%(BASE)d;
static const uint WITM=%(WITM)d;   // the witness bone's index in THIS member mesh
static const uint WITA=%(WITA)d;   // the anchor-side witness recovery's base row in palRaw
%(ROWFN)s

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint g=i>>2, comp=i&3;
    uint b=Map[g];
    if(b==0xFFFFFFFF) return;   // this member cannot condition the bone -> leave the row alone
    float4x4 MM=float4x4(Row(WITM,0),Row(WITM,1),Row(WITM,2),Row(WITM,3));
    float4x4 MA=float4x4(palRaw[WITA],palRaw[WITA+1],palRaw[WITA+2],palRaw[WITA+3]);
    float4x4 K=mul(AffineInverse(MM[0],MM[1],MM[2],MM[3]), MA);
    palOut[((BASE+g)<<2)|comp]=mul(Row(b,comp), K);
}
";

    public const string SkinTemplate =
@"// Skin the new body (VCOUNT verts, weighted to UNION bone order) with the CONVERTED palette.
struct Vtx  { float3 position; float3 normal; float4 tangent; };
struct Skin { float4 weight;   uint4  index;  };
// u1 is a stride-zero raw compute buffer. The command list unbinds it after dispatch, then copies
// its bytes into the separate 40-byte vertex buffer used by the draw.
RWByteAddressBuffer     rw_out : register(u1);
StructuredBuffer<Vtx>   bindV  : register(t0);
StructuredBuffer<Skin>  skinB  : register(t1);
// Palette resources are physically structured as 16-byte float4 rows. Keep the shader declaration
// at that exact stride and assemble each 4-row matrix explicitly; rebinding the same resource as a
// StructuredBuffer<Mat> declares a conflicting 64-byte stride and is driver-dependent.
StructuredBuffer<float4> palRows : register(t2);
static const uint VCOUNT=%(VCOUNT)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint vid=tid.x; if(vid>=VCOUNT) return;
    Vtx V=bindV[vid]; Skin S=skinB[vid];
    float3 sp=0,sn=0,st=0;
    [unroll] for(int k=0;k<4;k++){
        float wk=S.weight[k]; uint b=S.index[k];
        uint p=b<<2;
        float4x4 M=float4x4(palRows[p],palRows[p+1],palRows[p+2],palRows[p+3]);
        sp+=wk*mul(float4(V.position,1.0),M).xyz;
        sn+=wk*mul(float4(V.normal,0.0),M).xyz;
        st+=wk*mul(float4(V.tangent.xyz,0.0),M).xyz;
    }
    uint o=vid*40;
    rw_out.Store3(o,    asuint(sp));
    rw_out.Store3(o+12, asuint(normalize(sn)));
    rw_out.Store4(o+24, asuint(float4(normalize(st),V.tangent.w)));
}
";

    public const string TieFillTemplate =
@"// The tie underlay for one absent pool part. PAIR rows: a donor-ridden union row the part owns is
// filled with its nearest ANCHOR-owned ancestor's converted row, verbatim — palette rows are combined
// bind->posed affine maps, so a copy IS the rigid ride (the bind-relative delta cancels against the
// ancestor's inverse bind). SEED rows (no path / no anchor-owned ancestor): identity — bind-pose
// placement in the anchor's space. Both are needed because the converts rewrite EVERY union row: an
// absent part's constants-K is zero (its CB was never filled) and witness-K rides an arbitrary bone,
// so a row left to them collapses. Runs in the anchor's chains only while the part's presence latch is
// down; the frame the part returns, its own recover overwrites these rows again.
// Ancestor rows are read from a COPY of the converted palette, not the UAV: a typed UAV load of a
// 4-component format does not compile on cs_5_0 (single-component 32-bit only), which is why every
// compute in this emission reads through a StructuredBuffer and only WRITES its UAV.
StructuredBuffer<float4> palIn  : register(t0);   // the CONVERTED palette, pre-fill
RWStructuredBuffer<float4> palOut : register(u1);
static const uint PAIRS=%(P)d, SEEDS=%(S)d;
static const uint2 PAIR[%(PT)d] = { %(PAIRLIST)s };   // x = tied union slot, y = ancestor union slot
static const uint  SEED[%(ST)d] = { %(SEEDLIST)s };   // union slots reset to identity
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=(PAIRS+SEEDS)*4) return;
    uint p=i>>2, comp=i&3;
    if(p<PAIRS){ palOut[(PAIR[p].x<<2)|comp]=palIn[(PAIR[p].y<<2)|comp]; return; }
    palOut[(SEED[p-PAIRS]<<2)|comp]=float4(comp==0?1.0:0.0,comp==1?1.0:0.0,comp==2?1.0:0.0,comp==3?1.0:0.0);
}
";

    static string Lf(string s) => s.Replace("\r\n", "\n");

    /// <summary>Stamp the tie-fill shader: ONE pool part's (tied, ancestor) union-slot pairs and its
    /// identity-seed slots, each in ascending slot order (the derivation's own order, so rebuilds
    /// reproduce). An empty list still stamps a one-element array — HLSL refuses zero-length — whose
    /// count constant keeps every thread off it.</summary>
    public static string EmitTieFill(IReadOnlyList<(uint Tied, uint Ancestor)> pairs, IReadOnlyList<uint> seeds) =>
        Lf(TieFillTemplate)
            .Replace("%(PAIRLIST)s", pairs.Count == 0 ? "uint2(0,0)"
                : string.Join(", ", pairs.Select(p => $"uint2({p.Tied},{p.Ancestor})")))
            .Replace("%(SEEDLIST)s", seeds.Count == 0 ? "0"
                : string.Join(", ", seeds.Select(s => s.ToString())))
            .Replace("%(PT)d", Math.Max(1, pairs.Count).ToString())
            .Replace("%(ST)d", Math.Max(1, seeds.Count).ToString())
            .Replace("%(P)d", pairs.Count.ToString())
            .Replace("%(S)d", seeds.Count.ToString());

    /// <summary>Stamp the SLIM recover shader: dispatch rows ROWS. Per-bone anchor widths are data (the
    /// Off buffer), not compile-time constants.</summary>
    public static string EmitRecover(int rows) =>
        Lf(RecoverTemplate).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp the DENSE recover shader (the whole part ships dense): vertex count N, rows ROWS.</summary>
    public static string EmitRecoverDense(int n, int rows) =>
        Lf(RecoverDenseTemplate).Replace("%(N)d", n.ToString()).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp the convert shader: per-part cbuffer block, owner-indexed selection block, union
    /// row count (4*unionBones).</summary>
    public static string EmitConvert(int partCount, int unionBones)
    {
        var cbufs = new StringBuilder();
        for (int pi = 0; pi < partCount; pi++)
        {
            if (pi > 0) cbufs.Append('\n');
            cbufs.Append($"cbuffer PartCB{pi} : register(b{FirstPartRegister + pi}) {{ float4 W{pi}[4]; }}");
        }
        var sel = new StringBuilder();
        for (int pi = 0; pi < partCount; pi++)
        {
            if (pi > 0) sel.Append('\n');
            sel.Append($"    {(pi == 0 ? "if" : "else if")}(pi=={pi}) WP=float4x4(W{pi}[0],W{pi}[1],W{pi}[2],W{pi}[3]);");
        }
        sel.Append("\n    else WP=float4x4(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);");
        return Lf(ConvertTemplate)
            .Replace("%(PART_CBUFFERS)s", cbufs.ToString())
            .Replace("%(PART_SELECT)s", sel.ToString())
            .Replace("%(ROWS)d", (4 * unionBones).ToString());
    }

    /// <summary>Stamp the witness convert: union row count, anchor part index, and per part the base
    /// row (slot·4) of its partSide/anchorSide witness recoveries; (~0, ~0) = no witness, rows pass
    /// through unconverted.</summary>
    public static string EmitConvertWitness(int unionBones, int anchorIdx, IReadOnlyList<(uint PartRow, uint AnchorRow)> wit)
    {
        var w = new StringBuilder();
        for (int pi = 0; pi < wit.Count; pi++)
        {
            if (pi > 0) w.Append(", ");
            w.Append($"uint2(0x{wit[pi].PartRow:x8},0x{wit[pi].AnchorRow:x8})");
        }
        return Lf(ConvertWitnessTemplate)
            .Replace("%(ROWS)d", (4 * unionBones).ToString())
            .Replace("%(ANCHOR)d", anchorIdx.ToString())
            .Replace("%(P)d", wit.Count.ToString())
            .Replace("%(WIT)s", w.ToString());
    }

    /// <summary>The row-solve body a fused group shader is stamped with: the ragged SLIM layout when the
    /// member's operator shipped one, the dense all-vertex layout otherwise (<paramref name="n"/> = the
    /// member mesh's vertex count, read only by the dense body).</summary>
    static string GroupRowFn(bool slim, int n) =>
        slim ? GroupRowSlim : GroupRowDense.Replace("%(N)d", n.ToString());

    /// <summary>Stamp a group member's LOD0 fused shader: dispatch rows (4·group bones), the group's first
    /// appended palette slot, and the member's operator layout. K is solved from the two constant
    /// buffers.</summary>
    public static string EmitGroupFuse(int groupBones, int slotBase, bool slim, int n) =>
        Lf(GroupFuseTemplate)
            .Replace("%(ROWS)d", (4 * groupBones).ToString())
            .Replace("%(BASE)d", slotBase.ToString())
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));

    /// <summary>Stamp a group member's TIER fused shader: as <see cref="EmitGroupFuse"/>, plus the witness
    /// bone's index in this member mesh and the base row of the anchor's own recovery of it.</summary>
    public static string EmitGroupFuseWitness(int groupBones, int slotBase, bool slim, int n,
        int witnessMemberBone, uint witnessAnchorRow) =>
        Lf(GroupFuseWitnessTemplate)
            .Replace("%(ROWS)d", (4 * groupBones).ToString())
            .Replace("%(BASE)d", slotBase.ToString())
            .Replace("%(WITM)d", witnessMemberBone.ToString())
            .Replace("%(WITA)d", witnessAnchorRow.ToString())
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));

    /// <summary>Stamp the skin shader with the body vertex count.</summary>
    public static string EmitSkin(int vcount) =>
        Lf(SkinTemplate).Replace("%(VCOUNT)d", vcount.ToString());

    public const string TierTieFillTemplate =
@"// The tier tie for one LOD level of a pipeline. Each PAIR row is a donor-weighted union row that the
// tier chain at this level writes NOTHING for: the level's mesh does not rig the bone (or its recovery
// there sentinelled to lod0), so the row would otherwise stand at whatever the last lod0-tier frame
// recovered, or at the identity seed. It is filled with a verbatim copy of a row this level DOES write,
// the co-riding bone chosen at build time — palette rows are combined bind->posed affines, so a copy is
// the rigid ride. Runs after the witness convert and before the skin in this level's chain only.
// Source rows are read from a COPY of the converted palette, never the UAV (cs_5_0 has no
// 4-component typed UAV load).
StructuredBuffer<float4> palIn  : register(t0);   // the CONVERTED palette, pre-fill
RWStructuredBuffer<float4> palOut : register(u1);
static const uint PAIRS=%(P)d;
static const uint2 PAIR[%(PT)d] = { %(PAIRLIST)s };   // x = tied union slot, y = source union slot
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=PAIRS*4) return;
    uint p=i>>2, comp=i&3;
    palOut[(PAIR[p].x<<2)|comp]=palIn[(PAIR[p].y<<2)|comp];
}
";

    /// <summary>Stamp one LOD level's tier-tie shader: (tied, source) union-slot pairs in ascending tied-slot
    /// order. Never stamped empty — a level with no orphan row gets no shader and no run.</summary>
    public static string EmitTierTieFill(IReadOnlyList<(uint Tied, uint Source)> pairs)
    {
        if (pairs.Count == 0) throw new ArgumentException("a tier tie needs at least one pair", nameof(pairs));
        return Lf(TierTieFillTemplate)
            .Replace("%(PAIRLIST)s", string.Join(", ", pairs.Select(p => $"uint2({p.Tied},{p.Source})")))
            .Replace("%(PT)d", pairs.Count.ToString())
            .Replace("%(P)d", pairs.Count.ToString());
    }
}
